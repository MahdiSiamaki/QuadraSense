using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>Ordinary bad input is a 4xx with a reason, never a 500.</summary>
/// <remarks>
/// Each of these used to escape as an exception: an undefined enum number reached the audit
/// query's mapping, a repeated permission hit a primary key only the foreign key was translated
/// for, and a missing password reached the hasher.
/// </remarks>
public sealed class MalformedInputTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly FrozenSet<string> Held = new[]
    {
        Permissions.AuditView, Permissions.UserView, Permissions.UserManage,
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly List<AuditQuery> _queries = [];
    private readonly WebApplicationFactory<Program> _factory;

    public MalformedInputTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton(TestStubs.Create<ISessionStore>((method, _) => method.Name switch
            {
                "ResolveAsync" => Task.FromResult<ResolvedSession?>(new ResolvedSession(
                    Guid.NewGuid(), new AuthenticatedUser(2, "admin2", "Admin", false, Held),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))),
                "TouchAsync" => Task.CompletedTask,
                "DeleteExpiredAsync" => Task.FromResult(0),
                _ => throw new NotSupportedException(method.Name),
            }));
            services.AddSingleton(TestStubs.Create<IAuditLog>((method, args) =>
            {
                if (method.Name == "QueryAsync")
                {
                    _queries.Add((AuditQuery)args[0]!);
                    return Task.FromResult(new AuditPage([], 0, 1, 50));
                }

                return Task.CompletedTask;
            }));
            services.AddSingleton(TestStubs.Create<IUserDirectory>((method, _) =>
                throw new NotSupportedException($"refused input must not reach the directory ({method.Name})")));
        }));

    [Fact]
    public async Task An_undefined_audit_category_is_ignored_rather_than_a_500()
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/v1/audit?category=999&outcome=77");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Assert.Single(_queries).Category);
    }

    [Fact]
    public async Task A_permission_listed_twice_is_a_validation_error()
    {
        using var response = await SendAsync(HttpMethod.Put, "/api/v1/users/42/permissions", new
        {
            overrides = new[]
            {
                new { permissionCode = Permissions.AuditView, effect = "grant", reason = "a" },
                new { permissionCode = Permissions.AuditView, effect = "deny", reason = "b" },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_password_change_without_the_current_password_is_a_validation_error()
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/auth/me/password",
            new { newPassword = "a-long-new-password-1" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add("Cookie", "sqm_session=admin2; sqm_csrf=t");
        request.Headers.Add("X-CSRF-Token", "t");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
