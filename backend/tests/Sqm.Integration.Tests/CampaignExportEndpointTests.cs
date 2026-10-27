using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sqm.Application.Campaigns;
using Sqm.Application.Identity;
using Sqm.Application.Planning;
using Sqm.Contracts.Planning;

namespace Sqm.Integration.Tests;

/// <summary>
/// The controlled export follows the product owner's decision of 2026-10-21: its own permission with
/// lookup.subscriber, a recorded reason, a second person above the threshold, and a download that only
/// the requester gets, only once approved and not expired, read from the run it was counted on.
/// </summary>
/// <remarks>The real host with the store and the reader stubbed; the store's SQL is proven against PostgreSQL elsewhere.</remarks>
public sealed class CampaignExportEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const ulong Number = 9121234567;
    private static readonly PlanningRunInfo Run = new(1790000000000, 246, "2026-10-10", "2026-10-27T05:00:00Z", true);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<AuditEntry> _audited = [];
    private readonly Dictionary<long, CampaignExportRecord> _requests = [];
    private long _userId = 7;
    private string[] _held = [Permissions.CampaignExport, Permissions.LookupSubscriber];
    private long _threshold;
    private long? _count = 1_250;

    public CampaignExportEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, _) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(new ResolvedSession(
                    Guid.NewGuid(),
                    new AuthenticatedUser(_userId, $"user{_userId}", "User", false, _held.ToFrozenSet(StringComparer.Ordinal)),
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
            services.AddSingleton(TestStubs.Create<IOptionsMonitor<CampaignExportOptions>>((method, _) => method.Name switch
            {
                "get_CurrentValue" => new CampaignExportOptions { ApprovalThreshold = _threshold, ValidHours = 72 },
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IPlanningReader>((method, args) => method.Name switch
            {
                "GetStatusAsync" => Task.FromResult(new PlanningStatusResponse(true, [Run], null, null, null)),
                "CountNumbersAsync" => Task.FromResult(_count),
                "StreamNumbersAsync" => Stream((Func<PlanningNumberRead, ValueTask>)args[5]!),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<ICampaignExportStore>((method, args) => method.Name switch
            {
                "CreateAsync" => Task.FromResult(Create(args)),
                "GetAsync" => Task.FromResult(_requests.GetValueOrDefault((long)args[0]!)),
                "ListAsync" => Task.FromResult<IReadOnlyList<CampaignExportRecord>>([.. _requests.Values]),
                "DecideAsync" => Task.FromResult(Decide((long)args[0]!, (long)args[1]!, (bool)args[2]!)),
                "RecordDownloadAsync" => Task.CompletedTask,
                _ => throw new NotSupportedException(method.Name),
            }));
        }));

    private static async Task<long> Stream(Func<PlanningNumberRead, ValueTask> onRow)
    {
        await onRow(new PlanningNumberRead(Number, 0, 1, 1, ["Nokia 105"]));
        return 1;
    }

    private CampaignExportRecord Create(object?[] args)
    {
        var needsApproval = (bool)args[4]!;
        var id = _requests.Count + 1;
        var record = new CampaignExportRecord(
            id, (CampaignListDefinition)args[0]!, (long)args[1]!, (string)args[2]!, (long)args[3]!, $"user{args[3]}",
            DateTimeOffset.UtcNow, needsApproval, needsApproval ? "pending" : "approved",
            null, null, null, null, needsApproval ? null : (DateTimeOffset?)args[5], 0, null);
        _requests[id] = record;
        return record;
    }

    private (CampaignDecisionOutcome, CampaignExportRecord?) Decide(long id, long decider, bool approve)
    {
        if (!_requests.TryGetValue(id, out var r))
        {
            return (CampaignDecisionOutcome.NotFound, null);
        }

        if (r.RequestedBy == decider)
        {
            return (CampaignDecisionOutcome.OwnRequest, r);
        }

        var decided = r with
        {
            Status = approve ? "approved" : "rejected", DecidedBy = decider, DecidedByName = $"user{decider}",
            DecidedAt = DateTimeOffset.UtcNow, ExpiresAt = approve ? DateTimeOffset.UtcNow.AddHours(72) : null,
        };
        _requests[id] = decided;
        return (CampaignDecisionOutcome.Decided, decided);
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(string path, object? body = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        request.Headers.Add("Cookie", "sqm_session=user; sqm_csrf=t");
        request.Headers.Add("X-CSRF-Token", "t");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static readonly object Ask = new
    {
        view = "sunset", @class = "exposed", vendor = "Nokia", purpose = "2G/3G migration campaign, wave 1",
    };

    [Fact]
    public async Task Without_campaign_export_nothing_is_counted_and_the_refusal_is_audited()
    {
        _held = [Permissions.LookupSubscriber, Permissions.DataExport];

        var (status, _) = await SendAsync("/api/v1/campaign-exports", Ask);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Empty(_requests);
        Assert.Equal(AuditOutcome.Denied, Assert.Single(_audited).Outcome);
    }

    [Fact]
    public async Task A_reason_is_required()
    {
        var (status, body) = await SendAsync("/api/v1/campaign-exports", new { view = "sunset", @class = "exposed", purpose = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("purpose", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Above_the_threshold_it_waits_and_its_requester_cannot_download_or_approve_it()
    {
        _threshold = 1_000;
        _held = [.. _held, Permissions.CampaignApprove];

        var (created, body) = await SendAsync("/api/v1/campaign-exports", Ask);
        Assert.Equal(HttpStatusCode.Created, created);
        Assert.Equal("pending", JsonDocument.Parse(body).RootElement.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync("/api/v1/campaign-exports/1/download")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync("/api/v1/campaign-exports/1/approve", new { note = "fine" })).Status);
        Assert.Equal("pending", _requests[1].Status);
    }

    [Fact]
    public async Task A_second_person_approves_and_only_the_requester_downloads_the_list_which_the_audit_never_names()
    {
        _threshold = 1_000;
        await SendAsync("/api/v1/campaign-exports", Ask);

        // Someone else, an approver, approves - and cannot take the list.
        _userId = 8;
        _held = [Permissions.CampaignApprove, Permissions.LookupSubscriber, Permissions.CampaignExport];
        Assert.Equal(HttpStatusCode.OK, (await SendAsync("/api/v1/campaign-exports/1/approve", new { note = "agreed" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync("/api/v1/campaign-exports/1/download")).Status);

        // The requester takes it, masked without reveal.
        _userId = 7;
        _held = [Permissions.CampaignExport, Permissions.LookupSubscriber];
        var (status, csv) = await SendAsync("/api/v1/campaign-exports/1/download");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.StartsWith("Number,Class", csv.TrimStart('﻿'), StringComparison.Ordinal);
        Assert.DoesNotContain(Number.ToString(System.Globalization.CultureInfo.InvariantCulture), csv, StringComparison.Ordinal);
        Assert.Contains("Nokia 105", csv, StringComparison.Ordinal);

        var audit = JsonSerializer.Serialize(_audited);
        Assert.Contains("campaign.export.download", audit, StringComparison.Ordinal);
        Assert.Contains("2G/3G migration campaign, wave 1", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(Number.ToString(System.Globalization.CultureInfo.InvariantCulture), audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task At_or_below_the_threshold_it_is_approved_on_request()
    {
        _threshold = 5_000;

        var (_, body) = await SendAsync("/api/v1/campaign-exports", Ask);

        Assert.Equal("approved", JsonDocument.Parse(body).RootElement.GetProperty("status").GetString());
        _held = [.. _held, Permissions.IdentifierReveal];
        var (status, csv) = await SendAsync("/api/v1/campaign-exports/1/download");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("9121234567", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_list_whose_run_is_gone_is_refused_rather_than_read_from_another()
    {
        _threshold = 5_000;
        await SendAsync("/api/v1/campaign-exports", Ask);

        _count = null;
        var (status, _) = await SendAsync("/api/v1/campaign-exports/1/download");

        Assert.Equal(HttpStatusCode.Gone, status);
    }
}
