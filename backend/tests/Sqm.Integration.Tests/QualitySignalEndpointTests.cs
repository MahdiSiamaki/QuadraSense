using System.Collections.Frozen;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.Identity;
using Sqm.Application.Quality;

namespace Sqm.Integration.Tests;

/// <summary>
/// The data-quality categories need import.view, list every category in the catalogue even when a
/// run has none of it, and give each a share of its own base - never of the wrong population.
/// </summary>
/// <remarks>The real host with the reader stubbed; the categories are proven against ClickHouse in RiskSnapshotTests.</remarks>
public sealed class QualitySignalEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private string[] _held = [Permissions.ImportView];
    private QualitySnapshot? _snapshot = new(
        1791176608112,
        new DateOnly(2026, 10, 1),
        new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero),
        [
            new QualityCategoryCount("all", 200, 180, 190, 150, 0),
            new QualityCategoryCount("active", 100, 95, 98, 80, 0),
            new QualityCategoryCount("history_all", 50, 48, 49, 45, 0),
            new QualityCategoryCount("missing_imei", 20, 20, 20, 1, 0),
            new QualityCategoryCount("untouched_since_dump", 40, 40, 40, 35, 0),
            new QualityCategoryCount("period_1_day", 10, 10, 10, 10, 12),
        ]);

    public QualitySignalEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, _) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(new ResolvedSession(
                    Guid.NewGuid(),
                    new AuthenticatedUser(7, "viewer", "Viewer", false, _held.ToFrozenSet(StringComparer.Ordinal)),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))),
                "TouchAsync" => Task.CompletedTask,
                "DeleteExpiredAsync" => Task.FromResult(0),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IAuditLog>((_, _) => Task.CompletedTask));
            services.AddSingleton(TestStubs.Create<IQualityReader>((method, _) => method.Name switch
            {
                "GetLatestAsync" => Task.FromResult(_snapshot),
                _ => throw new NotSupportedException(method.Name),
            }));
        }));

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string path)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", "sqm_session=viewer; sqm_csrf=t");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, text.StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private static JsonElement Category(JsonElement body, string code) =>
        body.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("code").GetString() == code);

    [Fact]
    public async Task Each_category_is_a_share_of_its_own_base()
    {
        var (status, body) = await GetAsync("/api/v1/quality/signals");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("available").GetBoolean());
        Assert.Equal("2026-10-01", body.GetProperty("asOf").GetString());

        // Missing IMEI of all bindings, untouched of the active ones, a period length of the history.
        Assert.Equal(0.1, Category(body, "missing_imei").GetProperty("share").GetDouble(), 9);
        Assert.Equal(0.4, Category(body, "untouched_since_dump").GetProperty("share").GetDouble(), 9);
        var period = Category(body, "period_1_day");
        Assert.Equal((0.2, 12), (period.GetProperty("share").GetDouble(), period.GetProperty("periods").GetInt64()));

        Assert.Equal(
            ["all", "active", "history_all"],
            body.GetProperty("bases").EnumerateArray().Select(b => b.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task Every_category_in_the_catalogue_is_listed_even_at_zero()
    {
        var (_, body) = await GetAsync("/api/v1/quality/signals");

        Assert.Equal(
            QualityCategories.All.Select(c => c.Code),
            body.GetProperty("categories").EnumerateArray().Select(c => c.GetProperty("code").GetString()));
        Assert.Equal(0, Category(body, "orphan_remove").GetProperty("bindings").GetInt64());
    }

    [Fact]
    public async Task Before_the_first_run_it_says_why_rather_than_showing_zeros()
    {
        _snapshot = null;

        var (status, body) = await GetAsync("/api/v1/quality/signals");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("available").GetBoolean());
        Assert.Contains("No measures run", body.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, body.GetProperty("categories").GetArrayLength());
    }

    [Fact]
    public async Task Without_import_view_it_is_refused()
    {
        _held = [Permissions.DeviceView];

        var (status, _) = await GetAsync("/api/v1/quality/signals");

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }
}
