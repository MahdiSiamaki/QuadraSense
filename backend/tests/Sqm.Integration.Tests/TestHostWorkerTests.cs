using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sqm.Ingestion;

namespace Sqm.Integration.Tests;

/// <summary>
/// The guard for <see cref="TestHostDefaults"/>: an API started by a test must not touch the
/// import queue on its own.
/// </summary>
/// <remarks>
/// Deliberately does not take the assembly fixture as a constructor argument. The property being
/// checked is that it applies to every test host, including the ones that never ask for it.
/// </remarks>
public sealed class TestHostWorkerTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void An_API_started_by_a_test_runs_no_import_worker_and_no_lease_recovery()
    {
        var hosted = factory.Services.GetServices<IHostedService>().ToList();

        Assert.DoesNotContain(hosted, s => s is ImportWorker);
        Assert.DoesNotContain(hosted, s => s is LeaseRecoveryService);
    }
}
