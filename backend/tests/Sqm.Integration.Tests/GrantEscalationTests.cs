using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>
/// A manager cannot grant what they do not hold, and cannot change their own access.
/// </summary>
/// <remarks>
/// <para>
/// Before this, <c>user.manage</c> or <c>role.manage</c> alone was a path to <c>system.admin</c>:
/// by role, by direct grant, by lifting a deny, or by adding it to a role the manager was in.
/// Each of those routes is exercised here against the real host and its real endpoints, as a
/// manager holding only the four administration permissions.
/// </para>
/// <para>
/// No database: the session store signs the manager in and the two directories are stubs that
/// record writes. A refused change must never reach the directory - asserted, not assumed.
/// </para>
/// </remarks>
public sealed class GrantEscalationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const long ManagerId = 7;
    private const long TargetId = 42;

    private static readonly FrozenSet<string> ManagerPermissions = new[]
    {
        Permissions.UserView, Permissions.UserManage, Permissions.RoleView, Permissions.RoleManage,
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly RoleDetail AdminRole = Role(1, "administrator", [.. Permissions.All]);
    private static readonly RoleDetail HelpdeskRole = Role(2, "helpdesk", [Permissions.UserView]);

    private readonly Stubs _stubs = new();
    private readonly WebApplicationFactory<Program> _factory;

    public GrantEscalationTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton<ISessionStore>(_stubs.Sessions);
            services.AddSingleton<IUserDirectory>(_stubs.Users);
            services.AddSingleton<IRoleDirectory>(_stubs.Roles);
        }));

    [Fact]
    public async Task Giving_another_user_a_role_with_permissions_the_manager_lacks_is_refused()
    {
        var response = await SendAsync(HttpMethod.Put, $"/api/v1/users/{TargetId}/roles",
            new { roleCodes = new[] { "administrator" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(Permissions.SystemAdmin, await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(_stubs.Writes);
    }

    [Fact]
    public async Task Giving_another_user_a_role_within_the_managers_own_permissions_goes_through()
    {
        var response = await SendAsync(HttpMethod.Put, $"/api/v1/users/{TargetId}/roles",
            new { roleCodes = new[] { "helpdesk" } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["SetRolesAsync"], _stubs.Writes);
    }

    [Fact]
    public async Task Changing_ones_own_roles_is_refused_even_within_ones_own_permissions()
    {
        var response = await SendAsync(HttpMethod.Put, $"/api/v1/users/{ManagerId}/roles",
            new { roleCodes = new[] { "helpdesk" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_stubs.Writes);
    }

    [Fact]
    public async Task A_direct_grant_the_manager_lacks_is_refused()
    {
        var response = await SendAsync(HttpMethod.Put, $"/api/v1/users/{TargetId}/permissions",
            new { overrides = new[] { new { permissionCode = Permissions.SystemAdmin, effect = "grant", reason = "x" } } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_stubs.Writes);
    }

    [Fact]
    public async Task Lifting_a_deny_the_manager_could_not_grant_is_refused()
    {
        // The target is denied audit.view directly; an empty override list lifts that deny.
        _stubs.TargetDeny = Permissions.AuditView;

        var response = await SendAsync(HttpMethod.Put, $"/api/v1/users/{TargetId}/permissions",
            new { overrides = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_stubs.Writes);
    }

    [Fact]
    public async Task Adding_a_deny_needs_no_more_than_the_manage_permission()
    {
        var response = await SendAsync(HttpMethod.Put, $"/api/v1/users/{TargetId}/permissions",
            new { overrides = new[] { new { permissionCode = Permissions.AuditView, effect = "deny", reason = "x" } } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["SetPermissionOverridesAsync"], _stubs.Writes);
    }

    [Fact]
    public async Task Adding_a_permission_the_manager_lacks_to_a_role_is_refused()
    {
        var response = await SendAsync(HttpMethod.Put, "/api/v1/roles/2/permissions",
            new { permissionCodes = new[] { Permissions.UserView, Permissions.SystemAdmin } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_stubs.Writes);
    }

    [Fact]
    public async Task Creating_a_role_with_a_permission_the_manager_lacks_is_refused()
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/roles",
            new { code = "sneaky", displayName = "Sneaky", permissionCodes = new[] { Permissions.SystemAdmin } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_stubs.Writes);
    }

    [Fact]
    public async Task Creating_a_user_with_a_role_the_manager_could_not_grant_is_refused()
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/users",
            new { username = "mallory", displayName = "Mallory", password = "a-long-password-1", roleCodes = new[] { "administrator" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_stubs.Writes);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object body)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };

        // Development host: no __Host- prefix. The CSRF double-submit needs cookie and header.
        request.Headers.Add("Cookie", "sqm_session=manager; sqm_csrf=t");
        request.Headers.Add("X-CSRF-Token", "t");

        return await client.SendAsync(request, Ct);
    }

    private static RoleDetail Role(long id, string code, IReadOnlyList<string> permissions) =>
        new(id, code, code, string.Empty, false, 0, permissions,
            DateTimeOffset.UnixEpoch, null, DateTimeOffset.UnixEpoch, null);

    /// <summary>The session store and directories, answering only what these tests expect.</summary>
    private sealed class Stubs
    {
        public Stubs()
        {
            Sessions = TestStubs.Create<ISessionStore>(Handle);
            Users = TestStubs.Create<IUserDirectory>(Handle);
            Roles = TestStubs.Create<IRoleDirectory>(Handle);
        }

        public ISessionStore Sessions { get; }

        public IUserDirectory Users { get; }

        public IRoleDirectory Roles { get; }

        public List<string> Writes { get; } = [];

        public string? TargetDeny { get; set; }

        private object? Handle(MethodInfo method, object?[] args) => method.Name switch
        {
            "ResolveAsync" => Task.FromResult<ResolvedSession?>(new ResolvedSession(
                Guid.NewGuid(),
                new AuthenticatedUser(ManagerId, "manager", "Manager", false, ManagerPermissions),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1))),
            "TouchAsync" => Task.CompletedTask,
            "DeleteExpiredAsync" => Task.FromResult(0),
            "GetRolesAsync" => Task.FromResult<IReadOnlyList<RoleDetail>>([AdminRole, HelpdeskRole]),
            "GetRoleAsync" => Task.FromResult<RoleDetail?>((long)args[0]! == 2 ? HelpdeskRole : AdminRole),
            "GetAsync" => Task.FromResult<UserDetail?>(Target((long)args[0]!)),
            "SetRolesAsync" or "SetPermissionOverridesAsync" or "SetRolePermissionsAsync" =>
                Record(method.Name, Task.FromResult(true)),
            "CreateAsync" or "CreateRoleAsync" => Record(method.Name, Task.FromResult(1L)),
            // The audit writer for denied requests.
            "WriteAsync" => Task.CompletedTask,
            _ => throw new NotSupportedException($"Unexpected call: {method.Name}"),
        };

        private object Record(string name, object result)
        {
            Writes.Add(name);
            return result;
        }

        private UserDetail Target(long id) => new(
            id, "target", "Target", null, null, null, "local", true, false, null, null, null, 0,
            null, null, null, null, null, DateTimeOffset.UnixEpoch, null, DateTimeOffset.UnixEpoch,
            null, [], TargetDeny is null ? [] :
            [
                new PermissionResolution(TargetDeny, "x", "x", "x", false, false, [], false, true, "x"),
            ], 0);
    }
}
