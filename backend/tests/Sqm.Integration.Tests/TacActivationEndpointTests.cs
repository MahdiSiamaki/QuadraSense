using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Api.Endpoints;
using Sqm.Application.DataImport;
using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>
/// Activating a GSMA version - or rolling back to one, which is the same route - owes the dashboard a
/// rebuild, because the marts resolved TACs against the version that was active when they were built.
/// </summary>
/// <remarks>
/// The real host with the repository and the analytics switch stubbed: what is under test is the
/// order of what the endpoint asks for, not the stores, which ImportQueueTests prove against PostgreSQL.
/// </remarks>
public sealed class TacActivationEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<string> _calls = [];
    private readonly List<(string Source, string Reason)> _owed = [];
    private bool _switchFails;

    public TacActivationEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, _) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(new ResolvedSession(
                    Guid.NewGuid(),
                    new AuthenticatedUser(1, "admin", "Administrator", false,
                        new[] { Permissions.ImportView, Permissions.TacActivate }.ToFrozenSet(StringComparer.Ordinal)),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))),
                "TouchAsync" => Task.CompletedTask,
                "DeleteExpiredAsync" => Task.FromResult(0),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IAuditLog>((_, _) => Task.CompletedTask));
            services.AddSingleton(TestStubs.Create<IImportJobRepository>((method, args) =>
            {
                _calls.Add(method.Name);
                return method.Name switch
                {
                    "ActivateTacVersionAsync" => Task.FromResult<TacActivationResult?>(
                        new TacActivationResult(3, 1595, "v2026.10.04", 2, "v2026.09.16")),
                    "MarkDashboardOwedAsync" => Record((string)args[0]!, (string)args[2]!),
                    "RevertTacActivationAsync" or "WriteAuditAsync" => Task.CompletedTask,
                    _ => throw new NotSupportedException(method.Name),
                };
            }));
            services.AddSingleton(TestStubs.Create<ITacVersionStore>((method, _) =>
            {
                _calls.Add(method.Name);
                return method.Name == "ActivateAsync" && _switchFails
                    ? Task.FromException(new InvalidOperationException("analytics store unavailable"))
                    : Task.CompletedTask;
            }));
        }));

    private Task Record(string source, string reason)
    {
        _owed.Add((source, reason));
        return Task.CompletedTask;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> ActivateAsync()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tac-versions/3/activate")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("Cookie", "sqm_session=admin; sqm_csrf=t");
        request.Headers.Add("X-CSRF-Token", "t");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, text.StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    [Fact]
    public async Task An_activation_owes_the_dashboard_a_rebuild_once_both_stores_have_switched()
    {
        var (status, body) = await ActivateAsync();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal([(TacEndpoints.DashboardSource, "TAC version v2026.10.04 activated")], _owed);

        // Owed only after the analytics switch: a debt for a switch that then failed would rebuild
        // the dashboard against the version that is still active, for nothing.
        Assert.True(_calls.IndexOf("ActivateAsync") < _calls.IndexOf("MarkDashboardOwedAsync"));
        Assert.Contains("rebuilt", body.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_switch_changes_nothing_and_owes_nothing()
    {
        _switchFails = true;

        var (status, _) = await ActivateAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("RevertTacActivationAsync", _calls);
        Assert.Empty(_owed);
    }
}
