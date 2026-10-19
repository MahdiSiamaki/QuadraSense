using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sqm.Contracts.Lookup;
using Sqm.Infrastructure.ClickHouse;

namespace Sqm.Integration.Tests;

/// <summary>
/// The dual-SIM pair is found from whichever end the search starts: the handset, its other radio,
/// or the number they share.
/// </summary>
/// <remarks>
/// <para>
/// The case is the one that found the twin-TAC scheme (docs/architecture/14-relationship-explorer.md):
/// Galaxy A51 IMEIs 35446411748626 and 35446511748626, one serial in TACs a hundred apart, both
/// carrying 9132108209 and 9931845457. Until 2026-10-19 a search by either IMEI showed no pair: the
/// pair search ran only when the graph had other handsets, and a handset centre never does.
/// </para>
/// <para>
/// Real data, as the API's user. Skips, saying so, without the database, or if the feed no longer
/// holds the shared numbers on both - the data moves, and this does not invent it.
/// </para>
/// </remarks>
[Collection("clickhouse-scratch")]
public sealed class RelationshipPairRealDataTests : IAsyncLifetime
{
    private const string Endpoint = "http://localhost:18123/";
    private const string Handset = "35446411748626";
    private const string OtherRadio = "35446511748626";
    private const string SharedNumber = "9132108209";

    private readonly HttpClient _reader = new() { Timeout = TimeSpan.FromMinutes(1) };
    private string? _unavailable;

    public async ValueTask InitializeAsync()
    {
        _reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("sqm_app:sqm_dev")));

        try
        {
            using var response = await _reader.PostAsync(Endpoint, new StringContent($"""
                SELECT uniqExact(imei) FROM sqm.binding_by_imei FINAL
                WHERE imei IN ('{Handset}', '{OtherRadio}') AND msisdn = {SharedNumber}
                """, Encoding.UTF8, "text/plain"), TestContext.Current.CancellationToken);
            var body = (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Trim();
            if (!response.IsSuccessStatusCode || body != "2")
            {
                _unavailable = $"the feed no longer holds {SharedNumber} on both radios ({body})";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _unavailable = $"no analytics store at {Endpoint}: {ex.Message}";
        }
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });
    }

    private static Task<RelationshipGraph> ExploreAsync(RelatedKind kind, string identifier) =>
        new ClickHouseAnalyticsStore(
            Options.Create(new ClickHouseOptions
            {
                ConnectionString = "Host=localhost;Port=18123;Database=sqm;Username=sqm_app;Password=sqm_dev",
            }),
            new Factory(),
            NullLogger<ClickHouseAnalyticsStore>.Instance)
        .GetRelationshipsAsync(kind, identifier, new RelationshipSections(true, true, true), TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(Handset, OtherRadio)]
    [InlineData(OtherRadio, Handset)]
    public async Task A_handset_shows_its_other_radio(string centre, string partner)
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }

        var graph = await ExploreAsync(RelatedKind.Imei, centre);

        var pair = Assert.Single(graph.Paired);
        Assert.Equal(partner, pair.Imei);
        Assert.Equal("Galaxy A51", pair.MarketingName);
        Assert.InRange(pair.SharedSubscribers, 1, 2);
    }

    [Fact]
    public async Task The_number_they_share_shows_the_pair_as_before()
    {
        if (_unavailable is not null)
        {
            Assert.Skip(_unavailable);
        }

        var graph = await ExploreAsync(RelatedKind.Msisdn, SharedNumber);

        Assert.Contains(graph.Paired, p => p.Imei is Handset or OtherRadio);
    }
}
