using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Infrastructure.ClickHouse;

namespace Sqm.Ingestion.Tests;

/// <summary>The worker on its own can read what ClickHouse sends it.</summary>
/// <remarks>
/// ClickHouse compresses every HTTP response. The read paths the pipeline shares with the API ask
/// the factory for the API's client name, which the standalone worker never registered - so it got
/// the factory's default handler, which does not decompress, and the first TAC upload to the
/// containerised worker failed on every attempt (2026-10-22). Development never saw it: there the
/// worker runs inside the API and borrows the API's client.
/// </remarks>
public sealed class StandaloneQueryClientTests
{
    [Fact]
    public void The_standalone_worker_decompresses_what_ClickHouse_compresses()
    {
        var services = new ServiceCollection();
        services.AddStandaloneQueryClient();
        using var provider = services.BuildServiceProvider();

        using var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(ClickHouseAnalyticsStore.HttpClientName);

        var primary = Assert.IsType<SocketsHttpHandler>(Primary(handler));
        Assert.Equal(DecompressionMethods.All, primary.AutomaticDecompression);
    }

    [Fact]
    public void The_query_client_waits_as_long_as_the_API_s()
    {
        var services = new ServiceCollection();
        services.AddStandaloneQueryClient();
        using var provider = services.BuildServiceProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ClickHouseAnalyticsStore.HttpClientName);

        // 180 s, as Sqm.Api's Program.cs: above ClickHouse:QueryTimeoutSeconds, so a slow query
        // ends as the server's timeout rather than the client walking away from it.
        Assert.Equal(TimeSpan.FromSeconds(180), client.Timeout);
    }

    private static HttpMessageHandler Primary(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler { InnerHandler: { } inner })
        {
            handler = inner;
        }

        return handler;
    }
}
