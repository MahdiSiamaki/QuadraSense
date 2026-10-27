using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.Identity;
using Sqm.Application.Planning;
using Sqm.Contracts.Planning;

namespace Sqm.Integration.Tests;

/// <summary>
/// The planning routes follow the identifier rules: planning.view for the figures, lookup.subscriber
/// for a page of numbers, masking without reveal, an audit that never holds a number - and no figures
/// at all before a run is published.
/// </summary>
/// <remarks>The real host with the reader stubbed; the reader's SQL is proven against ClickHouse elsewhere.</remarks>
public sealed class PlanningEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const ulong Number = 9121234567;
    private static readonly PlanningRunInfo Run = new(1790000000000, 246, "2026-10-10", "2026-10-27T05:00:00Z", true);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<AuditEntry> _audited = [];
    private string[] _held = [Permissions.PlanningView, Permissions.LookupSubscriber];
    private bool _published = true;

    public PlanningEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, _) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(new ResolvedSession(
                    Guid.NewGuid(),
                    new AuthenticatedUser(7, "planner", "Planner", false, _held.ToFrozenSet(StringComparer.Ordinal)),
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
            services.AddSingleton(TestStubs.Create<IPlanningReader>((method, _) => method.Name switch
            {
                "GetStatusAsync" => Task.FromResult(new PlanningStatusResponse(true, [Run], null, null, null)),
                "GetSunsetAsync" => Task.FromResult<SunsetResponse?>(_published
                    ? new SunsetResponse(Run, false, 10, [], [], 0, 0, 0, [], [], [])
                    : null),
                "ListNumbersAsync" => Task.FromResult<(PlanningRunInfo, IReadOnlyList<PlanningNumberRead>, long)?>(
                    (Run, [new PlanningNumberRead(Number, 0, 1, 1, ["Nokia 105"])], 1)),
                _ => throw new NotSupportedException(method.Name),
            }));
        }));

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add("Cookie", "sqm_session=planner; sqm_csrf=t");
        request.Headers.Add("X-CSRF-Token", "t");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, text.StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    [Fact]
    public async Task Without_planning_view_nothing_is_shown()
    {
        _held = [Permissions.DashboardView, Permissions.LookupSubscriber];

        var (status, _) = await SendAsync(HttpMethod.Get, "/api/v1/planning/status");

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task A_page_of_numbers_needs_lookup_subscriber_and_the_refusal_is_audited_without_a_number()
    {
        _held = [Permissions.PlanningView];

        var (status, _) = await SendAsync(HttpMethod.Post, "/api/v1/planning/numbers", new { view = "sunset", @class = "exposed" });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        var entry = Assert.Single(_audited);
        Assert.Equal(("planning.numbers", AuditOutcome.Denied), (entry.Action, entry.Outcome));
        Assert.Equal("exposed", entry.Detail!["class"]);
    }

    [Fact]
    public async Task Numbers_are_masked_without_reveal_and_the_audit_counts_them_without_naming_them()
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/v1/planning/numbers", new { view = "sunset", @class = "exposed", vendor = "Nokia" });

        Assert.Equal(HttpStatusCode.OK, status);
        var shown = body.GetProperty("rows")[0].GetProperty("msisdn").GetString();
        Assert.NotEqual(Number.ToString(System.Globalization.CultureInfo.InvariantCulture), shown);
        Assert.True(body.GetProperty("masked").GetBoolean());

        var entry = Assert.Single(_audited);
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
        Assert.Equal(1, entry.Detail!["returned"]);
        Assert.DoesNotContain(
            Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(entry), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_reveal_the_number_is_whole()
    {
        _held = [.. _held, Permissions.IdentifierReveal];

        var (_, body) = await SendAsync(HttpMethod.Post, "/api/v1/planning/numbers", new { view = "sunset", @class = "exposed" });

        Assert.Equal("9121234567", body.GetProperty("rows")[0].GetProperty("msisdn").GetString());
        Assert.False(body.GetProperty("masked").GetBoolean());
    }

    [Fact]
    public async Task Before_a_run_is_published_the_view_says_so_rather_than_answering_zero()
    {
        _published = false;

        var (status, body) = await SendAsync(HttpMethod.Get, "/api/v1/planning/sunset");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("No planning run yet", body.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("/api/v1/planning/breakdown?view=sunset&class=exposed&by=colour")]
    [InlineData("/api/v1/planning/breakdown?view=bands&class=exposed&by=vendor")]
    public async Task A_breakdown_by_anything_but_its_four_properties_is_refused(string path)
    {
        var (status, _) = await SendAsync(HttpMethod.Get, path);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }
}
