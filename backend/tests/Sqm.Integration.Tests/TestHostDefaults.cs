[assembly: AssemblyFixture(typeof(Sqm.Integration.Tests.TestHostDefaults))]

namespace Sqm.Integration.Tests;

/// <summary>
/// Settings every API host this assembly starts must have, applied before the first one starts.
/// </summary>
/// <remarks>
/// <para>
/// <b>An API started by a test runs no import worker.</b> In Development the worker runs inside
/// the API (<c>Import:RunWorkerInProcess</c>), and <c>WebApplicationFactory</c> starts the API in
/// Development, against the real database. So every test that booted the API also started a worker
/// polling the real queue, and a lease-recovery service rewriting it.
/// </para>
/// <para>
/// Found as flakiness: <c>ImportQueueTests</c> passed 13 of 13 on its own and failed one or two
/// different tests on every full run, because a test host's worker claimed the jobs the queue tests
/// had just enqueued. It went unnoticed while two tests booted the API and surfaced when nine did.
/// The flakiness was the harmless half. The same worker would claim a real import queued at the
/// time, and abandon it when the test host shut down seconds later.
/// </para>
/// <para>
/// An environment variable rather than a setting on each factory, because a rule that every new
/// test class has to remember is the rule the ninth one forgets. <see cref="TestHostWorkerTests"/>
/// fails if it stops being applied.
/// </para>
/// </remarks>
public sealed class TestHostDefaults
{
    /// <summary>Applies the settings. Runs once, before any test in the assembly.</summary>
    public TestHostDefaults() =>
        Environment.SetEnvironmentVariable("Import__RunWorkerInProcess", "false");
}
