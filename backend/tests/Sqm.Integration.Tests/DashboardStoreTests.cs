using Npgsql;
using Sqm.Application.Dashboards;
using Sqm.Application.Identity;

namespace Sqm.Integration.Tests;

/// <summary>
/// Dashboards against a real database: who a dashboard is shared with, at which level, and what a
/// stale save does.
/// </summary>
/// <remarks>
/// The rules under test are SQL - the strongest grant through a user or any role held now, the
/// revision guard, the unique name per owner, the cascades - so a fake store would only repeat what it
/// was written to say. Skips until operational migration 016 and its grants are applied.
/// </remarks>
public sealed class DashboardStoreTests : IClassFixture<IdentityFixture>, IAsyncLifetime
{
    private const string InitialPassword = "Vx9-quiet-owl-lantern";
    private const string EmptyDefinition = """{"schemaVersion":1,"defaults":{},"refreshMinutes":0,"widgets":[]}""";
    private const string OneWidget = """{"schemaVersion":1,"defaults":{},"refreshMinutes":0,"widgets":[{"id":"w_one000001"}]}""";

    private readonly IdentityFixture _fixture;
    private readonly List<long> _users = [];
    private readonly List<long> _roles = [];
    private string? _missingSchema;

    public DashboardStoreTests(IdentityFixture fixture) => _fixture = fixture;

    private Sqm.Infrastructure.Identity.PostgresDashboardStore Store => _fixture.Dashboards;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(IdentityFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT has_table_privilege('dashboard.dashboard', 'INSERT')", connection);

        try
        {
            if (await command.ExecuteScalarAsync() is not true)
            {
                _missingSchema = "sqm_app cannot write dashboard.dashboard; run grants/016_dashboards.sql";
            }
        }
        catch (PostgresException)
        {
            _missingSchema = "dashboard.dashboard does not exist; apply operational migration 016 and grants/016_dashboards.sql first";
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Dashboards, grants, favourites and defaults go with their owners and roles: the keys cascade.
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
        reason = _fixture.UnavailableReason ?? _missingSchema ?? string.Empty;
        return !_fixture.IsAvailable || _missingSchema is not null;
    }

    private async Task<long> UserAsync(string hint, params string[] roles)
    {
        var id = await _fixture.Users.CreateAsync(
            new NewUser(IdentityFixture.UniqueUsername(hint), $"Test {hint}", $"{hint}@example.test", null, null,
                roles.Length == 0 ? ["viewer"] : roles, MustChangePassword: false),
            InitialPassword, "tests", IdentityFixture.Context, Ct);

        _users.Add(id);
        return id;
    }

    private async Task<long> RoleAsync(string hint)
    {
        var id = await _fixture.Roles.CreateRoleAsync(
            $"test_{hint}_{Guid.NewGuid():N}"[..30], $"Test role {hint}", "dashboard tests", [Permissions.DashboardView],
            "tests", IdentityFixture.Context, Ct);

        _roles.Add(id);
        return id;
    }

    private async Task<DashboardRecord> DashboardAsync(long owner, string name, string definition = EmptyDefinition)
    {
        var (outcome, created) = await Store.CreateAsync(new NewDashboard(owner, name, "", definition, 1, null), Ct);
        Assert.Equal(DashboardWriteOutcome.Saved, outcome);
        return created!;
    }

    [Fact]
    public async Task A_new_dashboard_reads_back_as_its_owner_sees_it()
    {
        if (Skip(out var reason)) Assert.Skip(reason);

        var owner = await UserAsync("owner");
        var created = await DashboardAsync(owner, "  Operations  ", OneWidget);

        Assert.Equal("Operations", created.Name);
        Assert.Equal(1, created.Revision);
        Assert.Equal(1, created.WidgetCount);
        Assert.Null(created.GrantLevel);
        Assert.Equal(owner, created.OwnerUserId);

        var owned = await Store.ListOwnedAsync(owner, Ct);
        Assert.Equal([created.Id], owned.Select(d => d.Id));
    }

    [Fact]
    public async Task A_name_is_unique_per_owner_whatever_its_case()
    {
        if (Skip(out var reason)) Assert.Skip(reason);

        var owner = await UserAsync("names");
        var other = await UserAsync("names2");
        await DashboardAsync(owner, "Operations");

        var (outcome, _) = await Store.CreateAsync(new NewDashboard(owner, " OPERATIONS ", "", EmptyDefinition, 1, null), Ct);
        Assert.Equal(DashboardWriteOutcome.NameTaken, outcome);
        Assert.True(await Store.NameExistsAsync(owner, "operations", Ct));

        // Somebody else's "Operations" is theirs to name.
        await DashboardAsync(other, "Operations");
    }

    [Fact]
    public async Task A_save_from_a_stale_revision_is_a_conflict_not_an_overwrite()
    {
        if (Skip(out var reason)) Assert.Skip(reason);

        var owner = await UserAsync("revision");
        var created = await DashboardAsync(owner, "Rev");

        var (first, saved, _) = await Store.UpdateAsync(created.Id, owner, "Rev", "first", OneWidget, 1, expectedRevision: 1, Ct);
        Assert.Equal(DashboardWriteOutcome.Saved, first);
        Assert.Equal(2, saved!.Revision);
        Assert.Equal("first", saved.Description);

        // A second tab that loaded revision 1 tries to save.
        var (second, stale, conflict) = await Store.UpdateAsync(created.Id, owner, "Rev", "second", EmptyDefinition, 1, expectedRevision: 1, Ct);
        Assert.Equal(DashboardWriteOutcome.Conflict, second);
        Assert.Null(stale);
        Assert.Equal(2, conflict!.CurrentRevision);

        var current = await Store.GetAsync(created.Id, owner, Ct);
        Assert.Equal("first", current!.Description);

        var (missing, _, _) = await Store.UpdateAsync(long.MaxValue, owner, "x", "", EmptyDefinition, 1, 1, Ct);
        Assert.Equal(DashboardWriteOutcome.NotFound, missing);
    }

    [Fact]
    public async Task A_grant_through_a_user_or_a_role_shares_it_at_the_strongest_level()
    {
        if (Skip(out var reason)) Assert.Skip(reason);

        var owner = await UserAsync("sharer");
        var reader = await UserAsync("reader");
        var role = await RoleAsync("readers");
        await _fixture.Users.SetRolesAsync(reader, ["viewer", (await _fixture.Roles.GetRoleAsync(role, Ct))!.Code], "tests", IdentityFixture.Context, Ct);
        var dashboard = await DashboardAsync(owner, "Shared");

        Assert.Empty(await Store.ListSharedWithAsync(reader, Ct));
        Assert.Null((await Store.GetAsync(dashboard.Id, reader, Ct))!.GrantLevel);

        var (viaRole, _) = await Store.AddGrantAsync(dashboard.Id, "role", role, GrantLevels.Editor, owner, 100, Ct);
        Assert.Equal(GrantWriteOutcome.Saved, viaRole);
        var (direct, grant) = await Store.AddGrantAsync(dashboard.Id, "user", reader, GrantLevels.Viewer, owner, 100, Ct);
        Assert.Equal(GrantWriteOutcome.Saved, direct);
        Assert.Equal("user", grant!.SubjectType);

        // Editor through the role beats viewer directly.
        Assert.Equal(GrantLevels.Editor, (await Store.GetAsync(dashboard.Id, reader, Ct))!.GrantLevel);
        Assert.Equal([dashboard.Id], (await Store.ListSharedWithAsync(reader, Ct)).Select(d => d.Id));

        // Taking the role away takes its grant with it, on the next read.
        await _fixture.Users.SetRolesAsync(reader, ["viewer"], "tests", IdentityFixture.Context, Ct);
        Assert.Equal(GrantLevels.Viewer, (await Store.GetAsync(dashboard.Id, reader, Ct))!.GrantLevel);

        await Store.RemoveGrantAsync(dashboard.Id, grant.Id, Ct);
        Assert.Null((await Store.GetAsync(dashboard.Id, reader, Ct))!.GrantLevel);
        Assert.Empty(await Store.ListSharedWithAsync(reader, Ct));
    }

    [Fact]
    public async Task A_grant_is_refused_to_the_owner_twice_to_an_inactive_user_and_past_the_limit()
    {
        if (Skip(out var reason)) Assert.Skip(reason);

        var owner = await UserAsync("limits");
        var a = await UserAsync("limita");
        var b = await UserAsync("limitb");
        var gone = await UserAsync("gone");
        await _fixture.Users.SetActiveAsync(gone, false, "left", "tests", IdentityFixture.Context, Ct);
        var dashboard = await DashboardAsync(owner, "Limits");

        Assert.Equal(GrantWriteOutcome.SubjectIsOwner, (await Store.AddGrantAsync(dashboard.Id, "user", owner, GrantLevels.Viewer, owner, 100, Ct)).Outcome);
        Assert.Equal(GrantWriteOutcome.SubjectNotFound, (await Store.AddGrantAsync(dashboard.Id, "user", gone, GrantLevels.Viewer, owner, 100, Ct)).Outcome);
        Assert.Equal(GrantWriteOutcome.Saved, (await Store.AddGrantAsync(dashboard.Id, "user", a, GrantLevels.Viewer, owner, 1, Ct)).Outcome);
        Assert.Equal(GrantWriteOutcome.Duplicate, (await Store.AddGrantAsync(dashboard.Id, "user", a, GrantLevels.Editor, owner, 100, Ct)).Outcome);
        Assert.Equal(GrantWriteOutcome.LimitReached, (await Store.AddGrantAsync(dashboard.Id, "user", b, GrantLevels.Viewer, owner, 1, Ct)).Outcome);
        Assert.Equal(GrantWriteOutcome.NotFound, (await Store.AddGrantAsync(long.MaxValue, "user", b, GrantLevels.Viewer, owner, 100, Ct)).Outcome);
    }

    [Fact]
    public async Task A_grant_level_changes_only_within_its_own_dashboard()
    {
        if (Skip(out var reason)) Assert.Skip(reason);

        var owner = await UserAsync("level");
        var reader = await UserAsync("levelreader");
        var mine = await DashboardAsync(owner, "Mine");
        var other = await DashboardAsync(owner, "Other");
        var (_, grant) = await Store.AddGrantAsync(mine.Id, "user", reader, GrantLevels.Viewer, owner, 100, Ct);

        Assert.Equal(GrantWriteOutcome.NotFound, (await Store.UpdateGrantAsync(other.Id, grant!.Id, GrantLevels.Editor, Ct)).Outcome);
        Assert.Null(await Store.RemoveGrantAsync(other.Id, grant.Id, Ct));

        var (changed, updated) = await Store.UpdateGrantAsync(mine.Id, grant.Id, GrantLevels.Editor, Ct);
        Assert.Equal(GrantWriteOutcome.Saved, changed);
        Assert.Equal(GrantLevels.Editor, updated!.Level);
    }

    [Fact]
    public async Task Favourites_and_the_default_are_per_person()
    {
        if (Skip(out var reason)) Assert.Skip(reason);

        var owner = await UserAsync("prefs");
        var reader = await UserAsync("prefsreader");
        var dashboard = await DashboardAsync(owner, "Prefs");

        await Store.SetFavoriteAsync(owner, dashboard.Id, true, Ct);
        await Store.SetFavoriteAsync(owner, dashboard.Id, true, Ct);
        await Store.SetDefaultAsync(owner, dashboard.Id, null, Ct);

        var asOwner = await Store.GetAsync(dashboard.Id, owner, Ct);
        Assert.True(asOwner!.IsFavorite);
        Assert.True(asOwner.IsDefault);
        Assert.False((await Store.GetAsync(dashboard.Id, reader, Ct))!.IsFavorite);

        await Store.SetDefaultAsync(owner, null, "security-risk", Ct);
        Assert.Equal(new DashboardDefaultPreference(null, "security-risk"), await Store.GetDefaultAsync(owner, Ct));

        await Store.SetDefaultAsync(owner, null, null, Ct);
        Assert.Equal(new DashboardDefaultPreference(null, null), await Store.GetDefaultAsync(owner, Ct));

        await Store.SetFavoriteAsync(owner, dashboard.Id, false, Ct);
        Assert.False((await Store.GetAsync(dashboard.Id, owner, Ct))!.IsFavorite);
    }

    [Fact]
    public async Task Deleting_a_dashboard_takes_its_grants_and_favourites_and_clears_a_default()
    {
        if (Skip(out var reason)) Assert.Skip(reason);

        var owner = await UserAsync("deleter");
        var reader = await UserAsync("deletereader");
        var dashboard = await DashboardAsync(owner, "Doomed");
        await Store.AddGrantAsync(dashboard.Id, "user", reader, GrantLevels.Viewer, owner, 100, Ct);
        await Store.SetFavoriteAsync(reader, dashboard.Id, true, Ct);
        await Store.SetDefaultAsync(reader, dashboard.Id, null, Ct);

        Assert.True(await Store.DeleteAsync(dashboard.Id, Ct));
        Assert.False(await Store.DeleteAsync(dashboard.Id, Ct));

        Assert.Null(await Store.GetAsync(dashboard.Id, owner, Ct));
        Assert.Empty(await Store.ListSharedWithAsync(reader, Ct));
        Assert.Empty(await Store.ListGrantsAsync(dashboard.Id, Ct));
        Assert.Equal(new DashboardDefaultPreference(null, null), await Store.GetDefaultAsync(reader, Ct));
    }

    [Fact]
    public async Task Others_lists_what_is_neither_owned_nor_shared()
    {
        if (Skip(out var reason)) Assert.Skip(reason);

        var admin = await UserAsync("admin");
        var owner = await UserAsync("others");
        var shared = await DashboardAsync(owner, "Shared with admin");
        var notShared = await DashboardAsync(owner, "Not shared");
        await Store.AddGrantAsync(shared.Id, "user", admin, GrantLevels.Viewer, owner, 100, Ct);

        var others = (await Store.ListOthersAsync(admin, Ct)).Select(d => d.Id).ToList();
        Assert.Contains(notShared.Id, others);
        Assert.DoesNotContain(shared.Id, others);
    }

    [Fact]
    public async Task The_share_directory_returns_names_never_email_and_never_inactive_people()
    {
        if (Skip(out var reason)) Assert.Skip(reason);

        var hint = $"dir{Guid.NewGuid():N}"[..12];
        var active = await UserAsync(hint);
        var inactive = await UserAsync($"{hint}x");
        await _fixture.Users.SetActiveAsync(inactive, false, "left", "tests", IdentityFixture.Context, Ct);

        var (users, roles) = await Store.SearchShareDirectoryAsync($"Test {hint}", 20, 2, Ct);
        Assert.Contains(users, u => u.Id == active);
        Assert.DoesNotContain(users, u => u.Id == inactive);
        Assert.All(users, u => Assert.DoesNotContain("@", u.Detail ?? string.Empty, StringComparison.Ordinal));

        // A typed wildcard is a character, not a wildcard.
        var (wild, _) = await Store.SearchShareDirectoryAsync("%_%", 20, 2, Ct);
        Assert.DoesNotContain(wild, u => u.Id == active);

        // One character searches roles only.
        var (none, someRoles) = await Store.SearchShareDirectoryAsync("v", 20, 2, Ct);
        Assert.Empty(none);
        Assert.Contains(someRoles, r => r.Name == "Viewer");
        Assert.NotNull(roles);
    }
}
