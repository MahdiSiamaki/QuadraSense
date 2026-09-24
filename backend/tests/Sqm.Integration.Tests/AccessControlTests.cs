using Npgsql;
using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>
/// The authorisation rules, against a real database.
/// </summary>
/// <remarks>
/// These are the tests that would have to fail before someone sees data they should not. They run
/// against PostgreSQL because the rule itself is SQL: a mock would confirm what it was told.
/// </remarks>
public sealed class AccessControlTests : IClassFixture<IdentityFixture>, IAsyncLifetime
{
    /// <summary>
    /// Passwords for test accounts, sharing nothing with their names.
    /// </summary>
    /// <remarks>
    /// The obvious choice - "TestPassword!Long12345" - is rejected, because these accounts are
    /// called "Test &lt;something&gt;" and the policy refuses a password containing the user's
    /// name. That is the rule working; it cost one confusing test run to notice, which is a
    /// reasonable price for a rule that stops the single most common weak password.
    /// </remarks>
    private const string InitialPassword = "Vx9-quiet-owl-lantern";

    /// <summary>What a password is changed TO in these tests.</summary>
    private const string ChangedPassword = "Zq4-amber-river-stone";

    private readonly IdentityFixture _fixture;
    private readonly List<long> _users = [];
    private readonly List<long> _roles = [];

    public AccessControlTests(IdentityFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _users)
        {
            await _fixture.DeleteUserAsync(id);
        }

        foreach (var id in _roles)
        {
            await _fixture.DeleteRoleAsync(id);
        }
    }

    private bool Skip(out string reason)
    {
        reason = _fixture.UnavailableReason ?? string.Empty;
        return !_fixture.IsAvailable;
    }

    private async Task<long> CreateAsync(string hint, params string[] roles)
    {
        var id = await _fixture.Users.CreateAsync(
            new NewUser(IdentityFixture.UniqueUsername(hint), $"Test {hint}",
                null, null, null, roles, MustChangePassword: false),
            InitialPassword, "tests", IdentityFixture.Context, TestContext.Current.CancellationToken);

        _users.Add(id);
        return id;
    }

    // =======================================================================

    [Fact]
    public async Task Every_permission_in_code_exists_in_the_catalogue_and_the_reverse()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var catalogue = await _fixture.Roles.GetPermissionCatalogueAsync(
            TestContext.Current.CancellationToken);

        var inDatabase = catalogue.Select(p => p.Code).ToHashSet(StringComparer.Ordinal);

        // Both directions. A permission in the database with no constant cannot be required by
        // any endpoint, so granting it does nothing and an access review is reading a fiction. A
        // constant with no row cannot be granted to anyone, so the endpoint it protects is
        // unreachable. Both failures are silent in production.
        var missingFromDatabase = Permissions.All.Except(inDatabase, StringComparer.Ordinal).ToList();
        var missingFromCode = inDatabase.Except(Permissions.All, StringComparer.Ordinal).ToList();

        Assert.Empty(missingFromDatabase);
        Assert.Empty(missingFromCode);
    }

    [Fact]
    public async Task Seeded_roles_carry_the_permissions_the_security_model_says_they_do()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var roles = await _fixture.Roles.GetRolesAsync(TestContext.Current.CancellationToken);

        var viewer = roles.Single(r => r.Code == "viewer");
        var analyst = roles.Single(r => r.Code == "analyst");
        var operatorRole = roles.Single(r => r.Code == "data_operator");
        var administrator = roles.Single(r => r.Code == "administrator");

        // Viewer must not be able to resolve an individual. This is the line the whole role split
        // exists to draw: aggregate analytics needs no ability to identify a person.
        Assert.DoesNotContain(Permissions.LookupSubscriber, viewer.PermissionCodes);
        Assert.DoesNotContain(Permissions.DataExport, viewer.PermissionCodes);
        Assert.Contains(Permissions.DashboardView, viewer.PermissionCodes);

        Assert.Contains(Permissions.LookupSubscriber, analyst.PermissionCodes);
        Assert.DoesNotContain(Permissions.ImportUploadSqm, analyst.PermissionCodes);

        // Operator imports, Admin activates - decision D4 of the import platform.
        Assert.Contains(Permissions.ImportUploadTac, operatorRole.PermissionCodes);
        Assert.DoesNotContain(Permissions.TacActivate, operatorRole.PermissionCodes);
        Assert.DoesNotContain(Permissions.ImportDelete, operatorRole.PermissionCodes);
        Assert.DoesNotContain(Permissions.UserManage, operatorRole.PermissionCodes);

        Assert.Equal(Permissions.All.Count, administrator.PermissionCodes.Count);
    }

    [Fact]
    public async Task A_direct_deny_beats_a_role_that_grants_the_same_permission()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var id = await CreateAsync("deny", "analyst");
        var ct = TestContext.Current.CancellationToken;

        var before = await _fixture.Users.GetEffectivePermissionsAsync(id, ct);
        Assert.True(before.Single(p => p.Code == Permissions.LookupSubscriber).IsGranted);

        await _fixture.Users.SetPermissionOverridesAsync(
            id,
            [new PermissionOverride(Permissions.LookupSubscriber, PermissionEffect.Deny,
                "under review")],
            "tests", IdentityFixture.Context, ct);

        var after = await _fixture.Users.GetEffectivePermissionsAsync(id, ct);
        var lookup = after.Single(p => p.Code == Permissions.LookupSubscriber);

        Assert.False(lookup.IsGranted);
        Assert.True(lookup.DeniedDirectly);

        // The provenance has to survive the deny, or the user detail page cannot explain what
        // happened - "she has the Analyst role but cannot look anyone up" is the question the
        // screen exists to answer.
        Assert.Contains("Analyst", lookup.GrantedByRoles);
        Assert.Equal("under review", lookup.OverrideReason);

        // Everything else the role grants is untouched.
        Assert.True(after.Single(p => p.Code == Permissions.DashboardView).IsGranted);
    }

    [Fact]
    public async Task A_direct_grant_adds_a_permission_no_role_carries()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var id = await CreateAsync("grant", "viewer");
        var ct = TestContext.Current.CancellationToken;

        await _fixture.Users.SetPermissionOverridesAsync(
            id,
            [new PermissionOverride(Permissions.DataExport, PermissionEffect.Grant, "one-off")],
            "tests", IdentityFixture.Context, ct);

        var permissions = await _fixture.Users.GetEffectivePermissionsAsync(id, ct);
        var export = permissions.Single(p => p.Code == Permissions.DataExport);

        Assert.True(export.IsGranted);
        Assert.True(export.GrantedDirectly);
        Assert.Empty(export.GrantedByRoles);
    }

    [Fact]
    public async Task Two_roles_union_their_permissions()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var id = await CreateAsync("union", "viewer", "data_operator");
        var ct = TestContext.Current.CancellationToken;

        var permissions = await _fixture.Users.GetEffectivePermissionsAsync(id, ct);
        var dashboard = permissions.Single(p => p.Code == Permissions.DashboardView);

        Assert.True(dashboard.IsGranted);
        Assert.Equal(2, dashboard.GrantedByRoles.Count);
        Assert.True(permissions.Single(p => p.Code == Permissions.ImportReprocess).IsGranted);
        Assert.False(permissions.Single(p => p.Code == Permissions.UserManage).IsGranted);
    }

    [Fact]
    public async Task The_session_resolves_exactly_the_permissions_the_user_page_shows()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var id = await CreateAsync("resolve", "data_operator");
        var ct = TestContext.Current.CancellationToken;

        await _fixture.Users.SetPermissionOverridesAsync(
            id,
            [
                new PermissionOverride(Permissions.AuditView, PermissionEffect.Grant, null),
                new PermissionOverride(Permissions.LookupSubscriber, PermissionEffect.Deny, null),
            ],
            "tests", IdentityFixture.Context, ct);

        var session = await _fixture.Sessions.CreateAsync(id, "127.0.0.1", "tests", ct);
        var resolved = await _fixture.Sessions.ResolveAsync(session.Token, ct);

        Assert.NotNull(resolved);

        var fromPage = (await _fixture.Users.GetEffectivePermissionsAsync(id, ct))
            .Where(p => p.IsGranted).Select(p => p.Code).OrderBy(c => c, StringComparer.Ordinal);

        var fromSession = resolved.User.Permissions.OrderBy(c => c, StringComparer.Ordinal);

        // The rule is written twice - once in SQL for the request pipeline, once in C# for the
        // detail page. They must agree for a user holding a role, a direct grant and a direct
        // deny at the same time, or the screen is describing a different system from the one
        // enforcing access.
        Assert.Equal(fromPage, fromSession);
        Assert.True(resolved.User.Can(Permissions.AuditView));
        Assert.False(resolved.User.Can(Permissions.LookupSubscriber));
    }

    [Fact]
    public async Task Deactivating_a_user_kills_their_live_sessions_immediately()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var id = await CreateAsync("deactivate", "viewer");
        var ct = TestContext.Current.CancellationToken;

        var session = await _fixture.Sessions.CreateAsync(id, "127.0.0.1", "tests", ct);
        Assert.NotNull(await _fixture.Sessions.ResolveAsync(session.Token, ct));

        await _fixture.Users.SetActiveAsync(
            id, false, "left the company", "tests", IdentityFixture.Context, ct);

        // This is the property that decided sessions over tokens. With a signed token the same
        // cookie would keep working until it expired.
        Assert.Null(await _fixture.Sessions.ResolveAsync(session.Token, ct));
    }

    [Fact]
    public async Task A_revoked_session_stops_working_and_an_unknown_token_never_worked()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var id = await CreateAsync("revoke", "viewer");
        var ct = TestContext.Current.CancellationToken;

        var session = await _fixture.Sessions.CreateAsync(id, "127.0.0.1", "tests", ct);
        Assert.NotNull(await _fixture.Sessions.ResolveAsync(session.Token, ct));

        await _fixture.Sessions.RevokeAsync(session.SessionId, "tests", "test", ct);

        Assert.Null(await _fixture.Sessions.ResolveAsync(session.Token, ct));
        Assert.Null(await _fixture.Sessions.ResolveAsync("not-a-real-token", ct));
    }

    [Fact]
    public async Task Changing_a_password_ends_every_other_session_but_not_this_one()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var id = await CreateAsync("password", "viewer");
        var ct = TestContext.Current.CancellationToken;

        var mine = await _fixture.Sessions.CreateAsync(id, "127.0.0.1", "this device", ct);
        var theirs = await _fixture.Sessions.CreateAsync(id, "10.0.0.9", "other device", ct);

        var changed = await _fixture.Users.ChangeOwnPasswordAsync(
            id, InitialPassword, ChangedPassword, mine.SessionId,
            IdentityFixture.Context, ct);

        Assert.True(changed);

        // The attacker's session is gone; the user is not signed out of the tab they just used.
        Assert.Null(await _fixture.Sessions.ResolveAsync(theirs.Token, ct));
        Assert.NotNull(await _fixture.Sessions.ResolveAsync(mine.Token, ct));
    }

    [Fact]
    public async Task The_wrong_current_password_does_not_change_anything()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var id = await CreateAsync("wrongcurrent", "viewer");
        var ct = TestContext.Current.CancellationToken;

        var changed = await _fixture.Users.ChangeOwnPasswordAsync(
            id, "not-the-password", ChangedPassword, Guid.NewGuid(),
            IdentityFixture.Context, ct);

        Assert.False(changed);

        var outcome = await _fixture.Authenticator.AuthenticateAsync(
            (await _fixture.Users.GetAsync(id, ct))!.Username, InitialPassword,
            IdentityFixture.Context, ct);

        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account_and_the_right_one_then_says_so()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var id = await CreateAsync("lockout", "viewer");
        var ct = TestContext.Current.CancellationToken;
        var username = (await _fixture.Users.GetAsync(id, ct))!.Username;

        for (var i = 0; i < _fixture.Options.Lockout.MaxFailedAttempts; i++)
        {
            var failed = await _fixture.Authenticator.AuthenticateAsync(
                username, "wrong", IdentityFixture.Context, ct);

            // Never AccountLocked for a wrong password: that would tell an attacker the username
            // exists. Only a caller who knows the password is told about the lock.
            Assert.Equal(LoginStatus.InvalidCredentials, failed.Status);
        }

        var locked = await _fixture.Authenticator.AuthenticateAsync(
            username, InitialPassword, IdentityFixture.Context, ct);

        Assert.Equal(LoginStatus.AccountLocked, locked.Status);
        Assert.NotNull(locked.LockedUntil);

        await _fixture.Users.UnlockAsync(id, "tests", IdentityFixture.Context, ct);

        var after = await _fixture.Authenticator.AuthenticateAsync(
            username, InitialPassword, IdentityFixture.Context, ct);

        Assert.True(after.Succeeded);
    }

    [Fact]
    public async Task A_deactivated_account_is_refused_even_with_the_right_password()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var id = await CreateAsync("disabled", "viewer");
        var ct = TestContext.Current.CancellationToken;
        var username = (await _fixture.Users.GetAsync(id, ct))!.Username;

        await _fixture.Users.SetActiveAsync(
            id, false, "test", "tests", IdentityFixture.Context, ct);

        var outcome = await _fixture.Authenticator.AuthenticateAsync(
            username, InitialPassword, IdentityFixture.Context, ct);

        Assert.Equal(LoginStatus.AccountDisabled, outcome.Status);

        // ...and a wrong password on the same disabled account says only "invalid", so the
        // account's existence is not disclosed to someone who does not know it.
        var blind = await _fixture.Authenticator.AuthenticateAsync(
            username, "wrong", IdentityFixture.Context, ct);

        Assert.Equal(LoginStatus.InvalidCredentials, blind.Status);
    }

    [Fact]
    public async Task The_system_refuses_to_leave_itself_with_no_administrator()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var ct = TestContext.Current.CancellationToken;

        // The guard is a property of the whole database, so this test asserts on the real state
        // rather than constructing an isolated one: there IS an administrator, and the last one
        // cannot be removed. Taking the real administrator's role away to prove it would be a
        // test that breaks the development environment when it fails halfway.
        Assert.True(await _fixture.Users.AdministrationIsReachableAsync(ct));

        var administrators = await _fixture.Users.SearchAsync(
            new UserQuery(RoleCode: "administrator", IsActive: true, PageSize: 100), ct);

        Assert.NotEmpty(administrators.Items);

        if (administrators.Total > 1)
        {
            Assert.Skip("more than one administrator exists, so removing one proves nothing");
        }

        var last = administrators.Items[0];

        await Assert.ThrowsAsync<LastAdministratorException>(() =>
            _fixture.Users.SetRolesAsync(
                last.Id, ["viewer"], "tests", IdentityFixture.Context, ct));

        await Assert.ThrowsAsync<LastAdministratorException>(() =>
            _fixture.Users.SetActiveAsync(
                last.Id, false, "test", "tests", IdentityFixture.Context, ct));

        // And the transaction really did roll back - not merely reported a failure.
        var reread = await _fixture.Users.GetAsync(last.Id, ct);
        Assert.NotNull(reread);
        Assert.True(reread.IsActive);
        Assert.Contains(reread.Roles, r => r.Code == "administrator");
    }

    [Fact]
    public async Task A_system_role_cannot_be_deleted()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var ct = TestContext.Current.CancellationToken;
        var roles = await _fixture.Roles.GetRolesAsync(ct);
        var viewer = roles.Single(r => r.Code == "viewer");

        var refused = await Assert.ThrowsAsync<RoleChangeRefusedException>(() =>
            _fixture.Roles.DeleteRoleAsync(viewer.Id, "tests", IdentityFixture.Context, ct));

        Assert.Contains("built-in", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_role_with_members_cannot_be_deleted()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var ct = TestContext.Current.CancellationToken;

        var roleId = await _fixture.Roles.CreateRoleAsync(
            $"test_role_{Guid.NewGuid():N}"[..20], "Test Role", "created by a test",
            [Permissions.DashboardView], "tests", IdentityFixture.Context, ct);
        _roles.Add(roleId);

        var role = await _fixture.Roles.GetRoleAsync(roleId, ct);
        Assert.NotNull(role);

        var userId = await CreateAsync("member", role.Code);

        await Assert.ThrowsAsync<RoleChangeRefusedException>(() =>
            _fixture.Roles.DeleteRoleAsync(roleId, "tests", IdentityFixture.Context, ct));

        await _fixture.Users.SetRolesAsync(userId, [], "tests", IdentityFixture.Context, ct);

        Assert.True(await _fixture.Roles.DeleteRoleAsync(
            roleId, "tests", IdentityFixture.Context, ct));

        _roles.Remove(roleId);
    }

    [Fact]
    public async Task A_duplicate_username_is_refused_by_the_index_not_by_a_prior_check()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var ct = TestContext.Current.CancellationToken;
        var id = await CreateAsync("dupe", "viewer");
        var username = (await _fixture.Users.GetAsync(id, ct))!.Username;

        // Different case. If uniqueness were a plain column constraint this would succeed and
        // produce two accounts a human reads as one.
        await Assert.ThrowsAsync<DuplicateUsernameException>(() =>
            _fixture.Users.CreateAsync(
                new NewUser(username.ToUpperInvariant(), "Clash", null, null, null, ["viewer"]),
                InitialPassword, "tests", IdentityFixture.Context, ct));
    }

    [Fact]
    public async Task A_short_password_is_refused_and_one_containing_the_name_is_too()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<PasswordPolicyException>(() =>
            _fixture.Users.CreateAsync(
                new NewUser(IdentityFixture.UniqueUsername("short"), "Short", null, null, null, []),
                "short", "tests", IdentityFixture.Context, ct));

        await Assert.ThrowsAsync<PasswordPolicyException>(() =>
            _fixture.Users.CreateAsync(
                new NewUser("mahmoud.rezaei", "Mahmoud Rezaei", null, null, null, []),
                "mahmoud.rezaei.2026", "tests", IdentityFixture.Context, ct));
    }

    [Fact]
    public async Task The_audit_log_records_who_changed_what_and_what_was_taken_away()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var ct = TestContext.Current.CancellationToken;
        var id = await CreateAsync("audited", "analyst");

        await _fixture.Users.SetRolesAsync(id, ["viewer"], "auditor", IdentityFixture.Context, ct);

        var page = await _fixture.Audit.QueryAsync(
            new AuditQuery(Action: "user.roles.set", PageSize: 20), ct);

        var entry = page.Items.FirstOrDefault(e => e.TargetId == id.ToString(
            System.Globalization.CultureInfo.InvariantCulture));

        Assert.NotNull(entry);
        Assert.Equal("auditor", entry.ActorName);
        Assert.NotNull(entry.Detail);

        // Before AND after. "Roles are now Viewer" does not tell a reviewer that Analyst - and
        // with it subscriber lookup - was removed, which is the half that matters.
        Assert.Contains("analyst", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("removed", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_number_past_int_range_is_an_empty_page_not_a_negative_offset()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var ct = TestContext.Current.CancellationToken;

        // (page - 1) * pageSize wrapped negative in 32 bits, and PostgreSQL refuses a negative
        // OFFSET: a 500 from any list with a page parameter.
        var audit = await _fixture.Audit.QueryAsync(new AuditQuery(Page: int.MaxValue, PageSize: 100), ct);
        var users = await _fixture.Users.SearchAsync(
            new UserQuery(null, null, null, int.MaxValue, 100, UserSort.DisplayName), ct);

        Assert.Empty(audit.Items);
        Assert.Empty(users.Items);
    }

    [Fact]
    public async Task Activating_an_active_account_succeeds_rather_than_not_found()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var ct = TestContext.Current.CancellationToken;
        var id = await CreateAsync("already-active", "viewer");

        Assert.True(await _fixture.Users.SetActiveAsync(id, true, null, "tests", IdentityFixture.Context, ct));
        Assert.False(await _fixture.Users.SetActiveAsync(long.MaxValue, true, null, "tests", IdentityFixture.Context, ct));
    }

    [Fact]
    public async Task An_administrative_change_is_linked_to_the_account_that_made_it()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var ct = TestContext.Current.CancellationToken;
        var admin = await _fixture.Users.GetByUsernameAsync("admin", ct);
        Assert.NotNull(admin); // the bootstrap administrator every database starts with

        var id = await CreateAsync("linked", "analyst");
        await _fixture.Users.SetRolesAsync(id, ["viewer"], admin.Username, IdentityFixture.Context, ct);

        var page = await _fixture.Audit.QueryAsync(
            new AuditQuery(Action: "user.roles.set", PageSize: 20), ct);
        var entry = page.Items.First(e => e.TargetId == id.ToString(
            System.Globalization.CultureInfo.InvariantCulture));

        // By name only, the entry had no actor id: the log showed no link to the account, and a
        // filter by actor id left out every user and role change.
        Assert.Equal(admin.Id, entry.ActorUserId);
    }

    [Fact]
    public async Task The_application_role_cannot_rewrite_or_delete_audit_history()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var ct = TestContext.Current.CancellationToken;

        await _fixture.Audit.WriteAsync(new AuditEntry(
            ActorName: "tests", Action: "system.selftest",
            Category: AuditCategory.System, Outcome: AuditOutcome.Success), ct);

        await using var connection = new NpgsqlConnection(IdentityFixture.ConnectionString);
        await connection.OpenAsync(ct);

        // This is the test that makes security-model section 8 a fact rather than a claim. It
        // connects as the application role, which is the role a compromised API process would
        // have, and asserts that PostgreSQL itself refuses.
        foreach (var statement in new[]
        {
            "UPDATE audit.event SET action = 'tampered' WHERE action = 'system.selftest'",
            "DELETE FROM audit.event WHERE action = 'system.selftest'",
            "UPDATE imports.import_audit SET actor = 'tampered'",
        })
        {
            await using var command = new NpgsqlCommand(statement, connection);

            var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                command.ExecuteNonQueryAsync(ct));

            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        }
    }
}
