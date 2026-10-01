using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.Abstractions;
using Sqm.Application.Explorer;
using Sqm.Application.Identity;
using Sqm.Contracts.Explorer;

namespace Sqm.Integration.Tests;

/// <summary>
/// The Explorer's routes enforce both layers of permission, mask identifiers without reveal, and
/// audit every query and refusal without the values it looked for.
/// </summary>
/// <remarks>
/// The real host, with the engine stubbed: what is under test is the API's decisions, not the SQL -
/// <see cref="ExplorerEngineTests"/> covers that against ClickHouse.
/// </remarks>
public sealed class ExplorerEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Msisdn = "9121234567";
    private const string Imsi = "432110123456789";
    private const string Imei = "35085748123456";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<AuditEntry> _audited = [];
    private readonly List<CheckedExplorerQuery> _ran = [];
    private string[] _held = [Permissions.ExplorerQuery, Permissions.LookupSubscriber, Permissions.LookupImsi, Permissions.LookupImei];
    private Func<CheckedExplorerQuery, ExplorerRows>? _engine;
    private readonly SavedQueries _saved = new();

    private static readonly ExplorerPlanInfo Plan = new("Current state, read by number", "KeyRead", 400_000, "Light", 100_000_000, []);

    public ExplorerEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, _) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(new ResolvedSession(
                    Guid.NewGuid(),
                    new AuthenticatedUser(7, "analyst", "Analyst", false, _held.ToFrozenSet(StringComparer.Ordinal)),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))),
                "TouchAsync" => Task.CompletedTask,
                "DeleteExpiredAsync" => Task.FromResult(0),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IAuditLog>((_, args) =>
            {
                _audited.Add((AuditEntry)args[0]!);
                return Task.CompletedTask;
            }));
            services.AddSingleton(TestStubs.Create<IDeviceAnalyticsStore>((method, _) => method.Name switch
            {
                "GetModelIdentityAsync" => Task.FromResult<DeviceModelIdentity?>(new("Samsung", "Samsung Korea", "Galaxy A32")),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IExplorerEngine>((method, args) =>
            {
                if (method.Name == "DataThroughAsync")
                {
                    return Task.FromResult<DateOnly?>(new DateOnly(2026, 9, 28));
                }

                var query = (CheckedExplorerQuery)args[0]!;
                _ran.Add(query);
                return method.Name switch
                {
                    "PlanAsync" => Task.FromResult(Plan),
                    "RunAsync" => Task.FromResult((_engine ?? Rows)(query)),
                    _ => throw new NotSupportedException(method.Name),
                };
            }));
            services.AddSingleton<IExplorerSavedQueryStore>(_saved);
            services.AddSingleton(TestStubs.Create<Sqm.Application.Timeline.ITimelineStore>((method, _) => method.Name switch
            {
                "GetReadinessAsync" => Task.FromResult(_history),
                "GetSeenAsync" => Task.FromResult<Sqm.Application.Timeline.EntitySeen?>(
                    new(new DateOnly(2025, 12, 27), true, new DateOnly(2026, 9, 26))),
                _ => throw new NotSupportedException(method.Name),
            }));
        }));

    private Sqm.Application.Timeline.HistoryReadiness _history = Sqm.Application.Timeline.HistoryReadiness.NotDeployed;

    /// <summary>
    /// An owner-scoped store in memory. What the database itself guarantees is proven against
    /// PostgreSQL in <see cref="ExplorerSavedQueryStoreTests"/>; here the question is only what the
    /// API does with each answer.
    /// </summary>
    private sealed class SavedQueries : IExplorerSavedQueryStore
    {
        private readonly List<(long Owner, SavedExplorerQuery Query)> _all = [];
        private long _next = 100;

        public long Seed(long owner, string name, ExplorerQueryRequest query)
        {
            var saved = new SavedExplorerQuery(_next++, name, "", query, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            _all.Add((owner, saved));
            return saved.Id;
        }

        public IEnumerable<SavedExplorerQuery> Of(long owner) => _all.Where(e => e.Owner == owner).Select(e => e.Query);

        public Task<IReadOnlyList<SavedExplorerQuery>> ListAsync(long ownerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SavedExplorerQuery>>([.. Of(ownerId)]);

        public Task<SavedExplorerQuery?> GetAsync(long ownerId, long id, CancellationToken ct) =>
            Task.FromResult(Of(ownerId).FirstOrDefault(q => q.Id == id));

        public Task<(SavedQueryOutcome Outcome, SavedExplorerQuery? Query)> CreateAsync(
            long ownerId, string name, string description, ExplorerQueryRequest query, CancellationToken ct)
        {
            if (Of(ownerId).Any(q => string.Equals(q.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult<(SavedQueryOutcome, SavedExplorerQuery?)>((SavedQueryOutcome.NameTaken, null));
            }

            var id = Seed(ownerId, name.Trim(), query);
            return Task.FromResult<(SavedQueryOutcome, SavedExplorerQuery?)>((SavedQueryOutcome.Saved, Of(ownerId).Single(q => q.Id == id)));
        }

        public Task<(SavedQueryOutcome Outcome, SavedExplorerQuery? Query)> UpdateAsync(
            long ownerId, long id, string name, string description, ExplorerQueryRequest query, CancellationToken ct)
        {
            var index = _all.FindIndex(e => e.Owner == ownerId && e.Query.Id == id);
            if (index < 0)
            {
                return Task.FromResult<(SavedQueryOutcome, SavedExplorerQuery?)>((SavedQueryOutcome.NotFound, null));
            }

            var updated = _all[index].Query with { Name = name.Trim(), Description = description, Query = query };
            _all[index] = (ownerId, updated);
            return Task.FromResult<(SavedQueryOutcome, SavedExplorerQuery?)>((SavedQueryOutcome.Saved, updated));
        }

        public Task<bool> DeleteAsync(long ownerId, long id, CancellationToken ct) =>
            Task.FromResult(_all.RemoveAll(e => e.Owner == ownerId && e.Query.Id == id) > 0);
    }

    /// <summary>One row with a plausible value for every column.</summary>
    private static ExplorerRows Rows(CheckedExplorerQuery query) => new(Plan,
        [[.. query.Columns.Select(c => (object?)(c.Type switch
        {
            ExplorerFieldType.Msisdn => Msisdn,
            ExplorerFieldType.Imsi => Imsi,
            ExplorerFieldType.Imei => Imei,
            ExplorerFieldType.Boolean => true,
            ExplorerFieldType.Date => "2026-09-26",
            ExplorerFieldType.Number => 3L,
            _ => "Galaxy A32",
        }))]], 1, 12, 400_000);

    private static object Query(params string[] columns) => new
    {
        dataset = "Bindings",
        where = new { field = "tac", @operator = "Equals", values = new[] { "35085748" } },
        columns,
    };

    private Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path, object body) =>
        SendAsync(HttpMethod.Post, path, body);

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, object? body = null)
    {
        var (status, text, _) = await SendRawAsync(method, path, body);
        return (status, string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<(HttpStatusCode Status, string Text, System.Net.Http.Headers.HttpContentHeaders Headers)> SendRawAsync(
        HttpMethod method, string path, object? body = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("Cookie", "sqm_session=analyst; sqm_csrf=t");
        request.Headers.Add("X-CSRF-Token", "t");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, text, response.Content.Headers);
    }

    [Fact]
    public async Task Without_explorer_query_the_builder_is_closed()
    {
        _held = [Permissions.LookupSubscriber, Permissions.LookupImsi, Permissions.LookupImei, Permissions.IdentifierReveal];

        var (status, _) = await PostAsync("/api/v1/explorer/query", Query("model"));

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Empty(_ran);
    }

    [Fact]
    public async Task A_query_for_identifiers_the_caller_may_not_see_is_refused_whole_and_says_why()
    {
        _held = [Permissions.ExplorerQuery, Permissions.LookupImei];

        var (status, body) = await PostAsync("/api/v1/explorer/query", Query("imei", "msisdn"));

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal([Permissions.LookupSubscriber], body.GetProperty("missing").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(_ran);

        var entry = Assert.Single(_audited);
        Assert.Equal((Permissions.ExplorerQuery, AuditOutcome.Denied), (entry.Action, entry.Outcome));
    }

    /// <summary>With explorer.query alone a person can query models and TACs - and nobody by name.</summary>
    [Fact]
    public async Task Models_and_TACs_need_no_lookup_permission()
    {
        _held = [Permissions.ExplorerQuery];

        var (status, body) = await PostAsync("/api/v1/explorer/query", Query("tac", "model", "active"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Galaxy A32", body.GetProperty("rows")[0][1].GetString());
    }

    [Fact]
    public async Task Without_reveal_every_identifier_comes_back_masked()
    {
        var (status, body) = await PostAsync("/api/v1/explorer/query", Query("msisdn", "imsi", "imei", "model"));
        var raw = body.GetRawText();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain(Msisdn, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Imsi, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Imei, raw, StringComparison.Ordinal);
        Assert.Equal([true, true, true, false], body.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("masked").GetBoolean()));
    }

    [Fact]
    public async Task With_reveal_identifiers_are_complete()
    {
        _held = [.. _held, Permissions.IdentifierReveal];

        var (_, body) = await PostAsync("/api/v1/explorer/query", Query("msisdn", "imsi", "imei"));

        Assert.Equal([Msisdn, Imsi, Imei], body.GetProperty("rows")[0].EnumerateArray().Select(v => v.GetString()));
    }

    /// <summary>The audit says what the query was - dataset, fields, cost - never what it looked for.</summary>
    [Fact]
    public async Task Every_query_is_audited_without_its_values()
    {
        _held = [.. _held, Permissions.IdentifierReveal];

        await PostAsync("/api/v1/explorer/query", new
        {
            dataset = "Bindings",
            where = new { field = "msisdn", @operator = "Equals", values = new[] { Msisdn } },
            columns = new[] { "imsi" },
        });

        var entry = Assert.Single(_audited);
        var detail = JsonSerializer.Serialize(entry);

        Assert.Equal((Permissions.ExplorerQuery, AuditOutcome.Success, 7L), (entry.Action, entry.Outcome, entry.ActorUserId));
        Assert.Equal("msisdn", entry.Detail!["fields"]);
        Assert.Equal(1, entry.Detail["returned"]);
        Assert.DoesNotContain(Msisdn, detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Imsi, detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_query_over_the_budget_is_answered_with_its_plan_and_audited()
    {
        _engine = _ => throw new ExplorerRefusedException(
            Plan with { Verdict = "Refused", EstimatedRows = 737_000_000, Notes = ["To bring it within the limit: ..."] },
            "This query would read about 737,000,000 rows; the limit is 100,000,000.");

        var (status, body) = await PostAsync("/api/v1/explorer/query", Query("model"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("Refused", body.GetProperty("plan").GetProperty("verdict").GetString());
        Assert.Equal(AuditOutcome.Failure, Assert.Single(_audited).Outcome);
    }

    [Fact]
    public async Task When_every_slot_is_taken_the_answer_is_busy()
    {
        _engine = _ => throw new ExplorerBusyException();

        var (status, _) = await PostAsync("/api/v1/explorer/query", Query("model"));

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
    }

    [Fact]
    public async Task A_malformed_query_says_where_before_anything_runs()
    {
        var (status, body) = await PostAsync("/api/v1/explorer/query", new
        {
            dataset = "Bindings",
            where = new { field = "tac", @operator = "Equals", values = new[] { "123" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("where.values[0]", out _));
        Assert.Empty(_ran);
    }

    [Fact]
    public async Task An_entity_summary_follows_its_kinds_lookup_permission()
    {
        _held = [Permissions.ExplorerQuery, Permissions.LookupSubscriber];
        _engine = q => new ExplorerRows(Plan, [[3L, 2L, 1L, 1L, 2L, 1L, 3L, 2L, "2026-09-26"]], 1, 5, 400_000);

        var (refused, _) = await PostAsync("/api/v1/explorer/entity", new { identifier = Imsi });
        var (status, body) = await PostAsync("/api/v1/explorer/entity", new { identifier = "0912 123 4567" });

        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("msisdn", 2L, 3L, "2026-09-26"), (
            body.GetProperty("kind").GetString(),
            body.GetProperty("sims").GetInt64(),
            body.GetProperty("handsets").GetInt64(),
            body.GetProperty("lastChange").GetString()));
    }

    /// <summary>First seen comes from the binding history, and only once it is complete.</summary>
    [Fact]
    public async Task An_entity_summary_says_when_it_was_first_seen_once_the_history_is_complete()
    {
        _engine = q => new ExplorerRows(Plan, [[3L, 2L, 1L, 1L, 2L, 1L, 3L, 2L, "2026-09-26"]], 1, 5, 400_000);

        var (_, before) = await PostAsync("/api/v1/explorer/entity", new { identifier = Msisdn });
        _history = Sqm.Application.Timeline.HistoryReadiness.Ready;
        var (_, after) = await PostAsync("/api/v1/explorer/entity", new { identifier = Msisdn });

        Assert.Equal(JsonValueKind.Null, before.GetProperty("firstSeen").ValueKind);
        Assert.Equal(("2025-12-27", true), (after.GetProperty("firstSeen").GetString(), after.GetProperty("firstSeenIsDumpWindow").GetBoolean()));
    }

    [Fact]
    public async Task The_catalogue_says_what_day_the_data_runs_to()
    {
        var (status, body) = await SendAsync(HttpMethod.Get, "/api/v1/explorer/catalogue");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("2026-09-28", body.GetProperty("dataThrough").GetString());

        // What the builder needs to offer a list rather than free text, and to show its defaults.
        var events = body.GetProperty("datasets").EnumerateArray().Single(d => d.GetProperty("dataset").GetString() == "Events");
        var change = events.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "change");
        Assert.Equal(["add", "remove"], change.GetProperty("values").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("date", events.GetProperty("defaultColumns")[0].GetString());
    }

    // ------------------------------------------------------------------ export

    [Fact]
    public async Task Exporting_needs_data_export_on_top_of_explorer_query()
    {
        var (status, _) = await PostAsync("/api/v1/explorer/export", Query("model"));

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Empty(_ran);
    }

    /// <summary>
    /// One run of every reachable row, not the page the grid happens to show - whatever page and
    /// size the request carried.
    /// </summary>
    [Fact]
    public async Task An_export_is_one_run_of_every_reachable_row()
    {
        _held = [.. _held, Permissions.DataExport];

        var (status, _, headers) = await SendRawAsync(HttpMethod.Post, "/api/v1/explorer/export", new
        {
            dataset = "Bindings",
            where = new { field = "tac", @operator = "Equals", values = new[] { "35085748" } },
            columns = new[] { "model" },
            page = 7,
            pageSize = 25,
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("text/csv", headers.ContentType?.MediaType);
        Assert.StartsWith("explorer-bindings-", headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName, StringComparison.Ordinal);

        var ran = Assert.Single(_ran);
        Assert.Equal((1, 10_000, 0), (ran.Page, ran.PageSize, ran.Offset));
    }

    [Fact]
    public async Task An_export_masks_like_the_grid_and_is_audited_as_an_export_without_values()
    {
        _held = [.. _held, Permissions.DataExport];

        var (status, csv, _) = await SendRawAsync(HttpMethod.Post, "/api/v1/explorer/export", new
        {
            dataset = "Bindings",
            where = new { field = "msisdn", @operator = "Equals", values = new[] { Msisdn } },
            columns = new[] { "msisdn", "imsi", "imei", "model" },
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain(Msisdn, csv, StringComparison.Ordinal);
        Assert.DoesNotContain(Imsi, csv, StringComparison.Ordinal);
        Assert.DoesNotContain(Imei, csv, StringComparison.Ordinal);
        Assert.Contains("Galaxy A32", csv, StringComparison.Ordinal);

        var entry = Assert.Single(_audited);
        Assert.Equal((Permissions.DataExport, AuditOutcome.Success), (entry.Action, entry.Outcome));
        Assert.Equal((1, true), (entry.Detail!["rows"], entry.Detail["masked"]));
        Assert.DoesNotContain(Msisdn, JsonSerializer.Serialize(entry), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_export_over_the_budget_is_refused_and_audited()
    {
        _held = [.. _held, Permissions.DataExport];
        _engine = _ => throw new ExplorerRefusedException(Plan with { Verdict = "Refused" }, "Too much.");

        var (status, body) = await PostAsync("/api/v1/explorer/export", Query("model"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("Refused", body.GetProperty("plan").GetProperty("verdict").GetString());
        Assert.Equal((Permissions.DataExport, AuditOutcome.Failure), (Assert.Single(_audited).Action, _audited[0].Outcome));
    }

    [Fact]
    public void A_csv_cell_cannot_become_a_formula()
    {
        var csv = System.Text.Encoding.UTF8.GetString(Sqm.Api.Infrastructure.CsvExport.Write(
            ["Model", "Count"],
            [
                ["=HYPERLINK(\"http://x\")", 1L],
                ["+1", -2L],
                ["-cmd", null],
                ["@SUM(A1)", true],
                ["Redmi Note 8, \"Pro\"", 3L],
            ]));

        Assert.Equal(
            "﻿Model,Count\r\n"
            + "\"'=HYPERLINK(\"\"http://x\"\")\",1\r\n"
            + "'+1,'-2\r\n"
            + "'-cmd,\r\n"
            + "'@SUM(A1),true\r\n"
            + "\"Redmi Note 8, \"\"Pro\"\"\",3\r\n",
            csv);
    }

    // ------------------------------------------------------------------ My Queries

    private static object Saved(string name, object? query = null) => new
    {
        name,
        description = "notes",
        query = query ?? Query("model"),
    };

    [Fact]
    public async Task A_query_is_saved_listed_renamed_and_deleted_by_its_owner()
    {
        var (created, body) = await PostAsync("/api/v1/explorer/saved", Saved("Galaxy A32 SIMs"));
        var id = body.GetProperty("id").GetInt64();

        var (_, list) = await SendAsync(HttpMethod.Get, "/api/v1/explorer/saved");
        var (renamed, _) = await SendAsync(HttpMethod.Put, $"/api/v1/explorer/saved/{id}", Saved("A32 SIMs"));
        var (deleted, _) = await SendAsync(HttpMethod.Delete, $"/api/v1/explorer/saved/{id}");

        Assert.Equal(HttpStatusCode.Created, created);
        Assert.Equal("Galaxy A32 SIMs", Assert.Single(list.EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal("35085748", list[0].GetProperty("query").GetProperty("where").GetProperty("values")[0].GetString());
        Assert.Equal(HttpStatusCode.OK, renamed);
        Assert.Equal(HttpStatusCode.NoContent, deleted);
        Assert.Empty(_saved.Of(7));
    }

    [Fact]
    public async Task Another_persons_query_is_not_listed_and_cannot_be_changed_or_deleted()
    {
        var theirs = _saved.Seed(8, "Theirs", new ExplorerQueryRequest(ExplorerDataset.Bindings));

        var (_, list) = await SendAsync(HttpMethod.Get, "/api/v1/explorer/saved");
        var (changed, _) = await SendAsync(HttpMethod.Put, $"/api/v1/explorer/saved/{theirs}", Saved("Mine now"));
        var (deleted, _) = await SendAsync(HttpMethod.Delete, $"/api/v1/explorer/saved/{theirs}");

        Assert.Empty(list.EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, changed);
        Assert.Equal(HttpStatusCode.NotFound, deleted);
        Assert.Equal("Theirs", Assert.Single(_saved.Of(8)).Name);
    }

    [Fact]
    public async Task A_second_query_by_the_same_name_is_a_conflict()
    {
        await PostAsync("/api/v1/explorer/saved", Saved("Weekly check"));
        var (status, _) = await PostAsync("/api/v1/explorer/saved", Saved("weekly CHECK"));

        Assert.Equal(HttpStatusCode.Conflict, status);
    }

    /// <summary>A query is saved only if the person saving it could run it.</summary>
    [Fact]
    public async Task A_query_the_caller_could_not_run_is_not_saved()
    {
        _held = [Permissions.ExplorerQuery, Permissions.LookupImei];

        var (forbidden, _) = await PostAsync("/api/v1/explorer/saved", Saved("Numbers", Query("msisdn")));
        var (invalid, body) = await PostAsync("/api/v1/explorer/saved", Saved("Broken", new
        {
            dataset = "Bindings",
            where = new { field = "tac", @operator = "Equals", values = new[] { "123" } },
        }));

        Assert.Equal(HttpStatusCode.Forbidden, forbidden);
        Assert.Equal(HttpStatusCode.BadRequest, invalid);
        Assert.True(body.GetProperty("errors").TryGetProperty("where.values[0]", out _));
        Assert.Empty(_saved.Of(7));
    }

    [Fact]
    public async Task Saving_is_audited_by_id_and_name_never_by_the_values_in_the_query()
    {
        await PostAsync("/api/v1/explorer/saved", Saved("One number", new
        {
            dataset = "Bindings",
            where = new { field = "msisdn", @operator = "Equals", values = new[] { Msisdn } },
            columns = new[] { "imsi" },
        }));

        var entry = Assert.Single(_audited);
        Assert.Equal(($"{Permissions.ExplorerQuery}.save", "One number"), (entry.Action, entry.TargetName));
        Assert.DoesNotContain(Msisdn, JsonSerializer.Serialize(entry), StringComparison.Ordinal);
    }
}
