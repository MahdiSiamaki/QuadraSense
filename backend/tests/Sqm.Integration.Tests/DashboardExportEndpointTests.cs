using System.Collections.Frozen;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.Dashboards;
using Sqm.Application.Identity;
using Sqm.Contracts.Dashboards;

namespace Sqm.Integration.Tests;

/// <summary>
/// Dashboard exports: who may export, what a widget the exporter may not see becomes, what the audit
/// records, and how CSV carries its scope.
/// </summary>
/// <remarks>
/// The real host with the store in memory and the data loader faked: under test are the export's
/// decisions. Each type's real data is read in <see cref="DashboardExportRealDataTests"/>.
/// </remarks>
public sealed class DashboardExportEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly Dictionary<string, AuthenticatedUser> People = new(StringComparer.Ordinal)
    {
        ["exporter"] = Person(1, Permissions.DashboardView, Permissions.DashboardCreate, Permissions.DataExport, Permissions.ImportView),
        ["noExport"] = Person(2, Permissions.DashboardView, Permissions.DashboardCreate, Permissions.ImportView),
        ["noImports"] = Person(3, Permissions.DashboardView, Permissions.DataExport),
        ["stranger"] = Person(4, Permissions.DashboardView, Permissions.DataExport),
    };

    /// <summary>A KPI anyone may see, a top list, and a freshness card that needs import.view.</summary>
    private const string Definition = """
        {"schemaVersion":1,"defaults":{"countBy":"bindings"},"refreshMinutes":0,"widgets":[
          {"id":"w_kpi0000001","type":"metric.kpi","configVersion":1,"title":"Active","subtitle":null,
           "config":{"metric":"activeBindings"},"overrides":{},"layout":{"x":0,"y":0,"w":2,"h":3}},
          {"id":"w_top0000001","type":"device.top","configVersion":1,"title":"Top vendors","subtitle":null,
           "config":{"dimension":"vendorCanonical"},"overrides":{},"layout":{"x":2,"y":0,"w":6,"h":9}},
          {"id":"w_ops0000001","type":"ops.freshness","configVersion":1,"title":"Freshness","subtitle":null,
           "config":{},"overrides":{},"layout":{"x":0,"y":9,"w":12,"h":4}}]}
        """;

    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<AuditEntry> _audited = [];
    private readonly FakeLoader _loader = new();

    public DashboardExportEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, args) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(People.TryGetValue((string)args[0]!, out var person)
                    ? new ResolvedSession(Guid.NewGuid(), person, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))
                    : null),
                "TouchAsync" => Task.CompletedTask,
                "DeleteExpiredAsync" => Task.FromResult(0),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IAuditLog>((_, args) =>
            {
                lock (_audited)
                {
                    _audited.Add((AuditEntry)args[0]!);
                }

                return Task.CompletedTask;
            }));
            services.AddSingleton<IDashboardStore>(new DashboardEndpointTests.InMemoryDashboards());
            services.AddScoped<IWidgetDataLoader>(_ => _loader);
        }));

    private static AuthenticatedUser Person(long id, params string[] permissions) =>
        new(id, $"user{id}", $"Person {id}", false, permissions.ToFrozenSet(StringComparer.Ordinal));

    private async Task<HttpResponseMessage> SendAsync(string who, HttpMethod method, string path, string? json = null)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(method, path)
        {
            Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Cookie", $"sqm_session={who}; sqm_csrf=t");
        request.Headers.Add("X-CSRF-Token", "t");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();

    private static string Snapshot(string? ids = null, long? dashboardId = null) =>
        $$"""{"dashboardId":{{(dashboardId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")}},"name":"Ops","definition":{{Definition}},"lens":{"vendor":"Samsung","range":"last30"},"widgetIds":{{ids ?? "null"}},"purpose":"png"}""";

    private static string Csv(string ids) =>
        $$"""{"dashboardId":null,"name":"Ops board","definition":{{Definition}},"lens":{"vendor":"Samsung"},"widgetIds":{{ids}}}""";

    [Fact]
    public async Task Without_data_export_nobody_exports()
    {
        using var snapshot = await SendAsync("noExport", HttpMethod.Post, "/api/v1/dashboards/export/snapshot", Snapshot());
        using var csv = await SendAsync("noExport", HttpMethod.Post, "/api/v1/dashboards/export/csv", Csv("""["w_kpi0000001"]"""));

        Assert.Equal(HttpStatusCode.Forbidden, snapshot.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, csv.StatusCode);
        Assert.Empty(_loader.Loaded);
    }

    [Fact]
    public async Task A_widget_the_exporter_may_not_see_is_forbidden_and_never_loaded()
    {
        using var response = await SendAsync("noImports", HttpMethod.Post, "/api/v1/dashboards/export/snapshot", Snapshot());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var widgets = (await JsonAsync(response)).GetProperty("widgets").EnumerateArray().ToDictionary(w => w.GetProperty("id").GetString()!);
        Assert.Equal("ok", widgets["w_kpi0000001"].GetProperty("status").GetString());
        Assert.Equal("forbidden", widgets["w_ops0000001"].GetProperty("status").GetString());
        Assert.Contains("import.view", widgets["w_ops0000001"].GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, widgets["w_ops0000001"].GetProperty("data").ValueKind);
        Assert.DoesNotContain("ops.freshness", _loader.Loaded);
    }

    [Fact]
    public async Task Each_widget_gets_only_the_lens_fields_its_type_supports()
    {
        using var response = await SendAsync("exporter", HttpMethod.Post, "/api/v1/dashboards/export/snapshot", Snapshot());
        var widgets = (await JsonAsync(response)).GetProperty("widgets").EnumerateArray().ToDictionary(w => w.GetProperty("id").GetString()!);

        Assert.Equal("Samsung", widgets["w_top0000001"].GetProperty("effective").GetProperty("vendor").GetString());
        // Freshness has no filters and no range: the vendor never reaches it.
        Assert.Equal(JsonValueKind.Null, widgets["w_ops0000001"].GetProperty("effective").GetProperty("vendor").ValueKind);
    }

    [Fact]
    public async Task An_export_is_audited_by_counts_never_by_content()
    {
        using var response = await SendAsync("noImports", HttpMethod.Post, "/api/v1/dashboards/export/snapshot", Snapshot());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = Assert.Single(_audited, a => a.Action == "data.export");
        Assert.Equal("dashboard", entry.TargetType);
        var detail = JsonSerializer.Serialize(entry.Detail);
        Assert.Contains("\"included\":2", detail, StringComparison.Ordinal);
        Assert.Contains("\"forbidden\":1", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Samsung", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("12345", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_saved_dashboard_the_exporter_cannot_open_answers_as_missing()
    {
        using var created = await SendAsync("exporter", HttpMethod.Post, "/api/v1/dashboards",
            $$"""{"name":"Private","description":"","definition":{{Definition}},"sourceTemplate":null}""");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetInt64();

        using var response = await SendAsync("stranger", HttpMethod.Post, "/api/v1/dashboards/export/snapshot", Snapshot(dashboardId: id));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_loader.Loaded);
    }

    [Fact]
    public async Task A_widget_that_fails_to_load_is_an_error_and_the_rest_still_export()
    {
        _loader.FailOn = "device.top";
        using var response = await SendAsync("exporter", HttpMethod.Post, "/api/v1/dashboards/export/snapshot", Snapshot());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var widgets = (await JsonAsync(response)).GetProperty("widgets").EnumerateArray().ToDictionary(w => w.GetProperty("id").GetString()!);
        Assert.Equal("error", widgets["w_top0000001"].GetProperty("status").GetString());
        Assert.Equal("The server could not prepare this widget.", widgets["w_top0000001"].GetProperty("reason").GetString());
        Assert.Equal("ok", widgets["w_kpi0000001"].GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_widget_not_on_the_dashboard_is_refused()
    {
        using var response = await SendAsync("exporter", HttpMethod.Post, "/api/v1/dashboards/export/snapshot", Snapshot("""["w_nothere0001"]"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task One_widget_is_a_CSV_whose_name_and_header_say_its_scope_and_whose_formulas_are_neutralised()
    {
        using var response = await SendAsync("exporter", HttpMethod.Post, "/api/v1/dashboards/export/csv", Csv("""["w_top0000001"]"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("topN", Assert.Single(response.Headers.GetValues("X-Export-Scope")));
        var name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;
        Assert.StartsWith("ops-board-top-vendors-topN-", name, StringComparison.Ordinal);

        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // Neutralised, then quoted as RFC 4180 asks: the quotes inside are doubled.
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Several_widgets_are_a_ZIP_with_an_about_file_naming_what_was_left_out()
    {
        using var response = await SendAsync("noImports", HttpMethod.Post, "/api/v1/dashboards/export/csv",
            Csv("""["w_kpi0000001","w_top0000001","w_ops0000001"]"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("topN,visible", Assert.Single(response.Headers.GetValues("X-Export-Scope")));

        await using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["about.txt", "active-visible.csv", "top-vendors-topN.csv"], names);

        using var about = new StreamReader(zip.GetEntry("about.txt")!.Open());
        var text = await about.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Freshness (ops.freshness)", text, StringComparison.Ordinal);
        Assert.Contains("Not included (forbidden)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_CSV_of_one_forbidden_widget_is_refused_rather_than_sent_empty()
    {
        using var response = await SendAsync("noImports", HttpMethod.Post, "/api/v1/dashboards/export/csv", Csv("""["w_ops0000001"]"""));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // Refused, and audited as refused - with the missing permission, never the lens.
        var entry = Assert.Single(_audited, a => a.Action == "data.export");
        Assert.Equal(AuditOutcome.Denied, entry.Outcome);
        var detail = JsonSerializer.Serialize(entry.Detail);
        Assert.Contains("import.view", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Samsung", detail, StringComparison.Ordinal);
    }

    /// <summary>Answers every type with a fixed table; the KPI and top list carry the scopes a real loader gives them.</summary>
    private sealed class FakeLoader : IWidgetDataLoader
    {
        public List<string> Loaded { get; } = [];

        public string? FailOn { get; set; }

        public Task<DateOnly?> DataThroughAsync(CancellationToken ct) => Task.FromResult<DateOnly?>(new DateOnly(2026, 9, 30));

        public Task<WidgetDataResult> LoadAsync(
            ValidatedWidget widget, WidgetTypeSpec spec, DashboardLens effective, WidgetLoadContext context, CancellationToken ct)
        {
            lock (Loaded)
            {
                Loaded.Add(widget.Type);
            }

            if (widget.Type == FailOn)
            {
                throw new InvalidOperationException("boom");
            }

            var table = widget.Type == "device.top"
                ? new WidgetTable(["Vendor", "Bindings"], [["=HYPERLINK(\"x\")", 12345L], ["Apple", 678L]], "topN", "The top 10.")
                : new WidgetTable(["Figure", "Value"], [["Active bindings", 12345L]], "visible", "The figures the card shows.");
            return Task.FromResult(new WidgetDataResult("ok", null, new Dictionary<string, object?> { ["rows"] = 12345 }, "2026-09-30", [], table));
        }
    }
}
