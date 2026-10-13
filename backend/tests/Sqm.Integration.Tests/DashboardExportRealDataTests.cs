using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.Dashboards;
using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>
/// Every widget type exported from the real stores, through the host's own wiring: each loads or says
/// why not, none fails, and a card's export carries the same figures as the card's own endpoint.
/// </summary>
/// <remarks>
/// Skips, saying so, without the analytics and operational databases. In the ClickHouse collection,
/// so it never runs beside the other real-data tests: run in parallel with them, a filtered KPI read
/// (90 s on the raw table, see docs/architecture/17-dashboard-builder.md) starved their connections.
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class DashboardExportRealDataTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly AuthenticatedUser Everyone = new(
        1, "user1", "Person 1", false, Permissions.All);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<AuditEntry> _audited = [];

    public DashboardExportRealDataTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, _) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(
                    new ResolvedSession(Guid.NewGuid(), Everyone, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))),
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
        }));

    private static void Write(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static async Task<string?> UnavailableAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            await http.GetStringAsync(new Uri("http://localhost:18123/ping"), TestContext.Current.CancellationToken);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("localhost", 15432, TestContext.Current.CancellationToken);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or TaskCanceledException)
        {
            return $"no analytics or operational database on localhost ({ex.GetType().Name})";
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(string path, string json)
    {
        using var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("Cookie", "sqm_session=x; sqm_csrf=t");
        request.Headers.Add("X-CSRF-Token", "t");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private async Task<JsonElement> GetAsync(string path)
    {
        using var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        var clock = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", "sqm_session=x; sqm_csrf=t");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Write($"GET {path}: {clock.ElapsedMilliseconds} ms");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>One widget per type with its defaults, in definitions of at most twelve.</summary>
    private static IEnumerable<string> Definitions()
    {
        var contract = WidgetContract.Load();
        var types = contract.Types.Values
            .Where(t => t.Type is not ("tools.explorerAggregate" or "tools.quickLookup"))
            .OrderBy(t => t.Type, StringComparer.Ordinal)
            .ToList();

        foreach (var (batch, b) in types.Chunk(12).Select((batch, b) => (batch, b)))
        {
            var y = 0;
            var widgets = new JsonArray();
            foreach (var (spec, i) in batch.Select((s, i) => (s, i)))
            {
                // Required fields stated with their defaults: the validator refuses a required field left out.
                var config = new JsonObject();
                foreach (var (name, field) in spec.Config.Where(f => f.Value.Required))
                {
                    config[name] = JsonNode.Parse(field.Default.GetRawText());
                }

                if (spec.Type == "tools.modelNetworkAge")
                {
                    config["tac"] = "35332211";
                }

                widgets.Add(new JsonObject
                {
                    ["id"] = $"w_real{b}{i:D2}",
                    ["type"] = spec.Type,
                    ["configVersion"] = spec.ConfigVersion,
                    ["title"] = spec.Type,
                    ["subtitle"] = null,
                    ["config"] = config,
                    ["overrides"] = new JsonObject(),
                    ["layout"] = new JsonObject { ["x"] = 0, ["y"] = y, ["w"] = 12, ["h"] = spec.Size.MinH },
                });
                y += spec.Size.MinH;
            }

            yield return new JsonObject
            {
                ["schemaVersion"] = 1,
                ["defaults"] = new JsonObject { ["countBy"] = "bindings" },
                ["refreshMinutes"] = 0,
                ["widgets"] = widgets,
            }.ToJsonString();
        }
    }

    [Fact]
    public async Task Every_type_exports_from_the_real_stores_or_says_why_not_and_none_fails()
    {
        if (await UnavailableAsync() is { } reason)
        {
            Assert.Skip(reason);
        }

        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in Definitions())
        {
            var clock = Stopwatch.StartNew();
            var (status, body) = await PostAsync("/api/v1/dashboards/export/snapshot",
                $$"""{"dashboardId":null,"name":"Real","definition":{{definition}},"lens":{"range":"last30"},"widgetIds":null,"purpose":"print"}""");
            Assert.True(status == HttpStatusCode.OK, $"{status}: {body}");
            Write($"batch prepared in {clock.ElapsedMilliseconds} ms");

            foreach (var w in JsonDocument.Parse(body).RootElement.GetProperty("widgets").EnumerateArray())
            {
                var type = w.GetProperty("type").GetString()!;
                var s = w.GetProperty("status").GetString()!;
                statuses[type] = s;
                Write($"{type,-26} {s,-11} {(w.GetProperty("reason").ValueKind == JsonValueKind.String ? w.GetProperty("reason").GetString() : "")}");
            }
        }

        Assert.All(statuses, kv => Assert.Contains(kv.Value, new[] { "ok", "notReady", "interactive" }));
        Assert.Equal("interactive", statuses["risk.list"]);
        Assert.Equal("ok", statuses["metric.kpi"]);
        Assert.Equal("ok", statuses["change.daily"]);
    }

    [Fact]
    public async Task A_cards_export_carries_the_same_figures_as_the_cards_own_endpoint()
    {
        if (await UnavailableAsync() is { } reason)
        {
            Assert.Skip(reason);
        }

        const string definition = """
            {"schemaVersion":1,"defaults":{},"refreshMinutes":0,"widgets":[
              {"id":"w_kpi0000001","type":"metric.kpi","configVersion":1,"title":null,"subtitle":null,
               "config":{"metric":"activeBindings"},"overrides":{},"layout":{"x":0,"y":0,"w":2,"h":3}},
              {"id":"w_day0000001","type":"change.daily","configVersion":1,"title":"Daily","subtitle":null,
               "config":{},"overrides":{},"layout":{"x":0,"y":3,"w":12,"h":10}}]}
            """;

        // Unfiltered: the marts. A filtered KPI reads the raw table for over a minute - measured, recorded,
        // and a separate piece of work; that the lens reaches each widget is proven with a fake loader.
        var live = await GetAsync("/api/v1/dashboard/kpi?includeUnknownDevice=true");
        var days = await GetAsync("/api/v1/dashboard/daily-changes");

        var clock = Stopwatch.StartNew();
        var (status, body) = await PostAsync("/api/v1/dashboards/export/snapshot",
            $$"""{"dashboardId":null,"name":"Same","definition":{{definition}},"lens":{"range":"last30"},"widgetIds":null,"purpose":"png"}""");
        Write($"POST snapshot: {clock.ElapsedMilliseconds} ms");
        Assert.Equal(HttpStatusCode.OK, status);
        var widgets = JsonDocument.Parse(body).RootElement.GetProperty("widgets").EnumerateArray().ToDictionary(w => w.GetProperty("id").GetString()!);

        var exported = widgets["w_kpi0000001"].GetProperty("data").GetProperty("kpi");
        Assert.Equal(live.GetProperty("activeBindings").GetInt64(), exported.GetProperty("activeBindings").GetInt64());
        Assert.Equal(live.GetProperty("deliveryDate").GetString(), exported.GetProperty("deliveryDate").GetString());
        Write($"Active bindings: {exported.GetProperty("activeBindings").GetInt64():N0}, live and exported");

        Assert.Equal(days.GetArrayLength(), widgets["w_day0000001"].GetProperty("data").GetProperty("changes").GetArrayLength());

        // The CSV holds the days the card draws: the last 30 days of data, as delivered.
        var (csvStatus, csv) = await PostAsync("/api/v1/dashboards/export/csv",
            $$"""{"dashboardId":null,"name":"Same","definition":{{definition}},"lens":{"range":"last30"},"widgetIds":["w_day0000001"]}""");
        Assert.Equal(HttpStatusCode.OK, csvStatus);
        var through = DateOnly.Parse(days[days.GetArrayLength() - 1].GetProperty("date").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var delivered = days.EnumerateArray()
            .Select(d => DateOnly.Parse(d.GetProperty("date").GetString()!, System.Globalization.CultureInfo.InvariantCulture))
            .Count(d => d > through.AddDays(-30) && d <= through);
        var lines = csv.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(delivered + 1, lines.Length);
        Write($"{delivered} delivered days in the last 30 to {through:yyyy-MM-dd}; header: {lines[0]}");
    }
}
