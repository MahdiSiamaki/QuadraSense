using Npgsql;
using Sqm.Application.Explorer;
using Sqm.Application.Identity;
using Sqm.Contracts.Explorer;

namespace Sqm.Integration.Tests;

/// <summary>
/// My Queries against a real database: private to the owner, names unique per owner, and a query
/// that reads back exactly as it was saved.
/// </summary>
/// <remarks>
/// Privacy here is a <c>WHERE owner_user_id</c> in every statement and uniqueness is an index on
/// <c>lower(btrim(name))</c>; both are SQL, so a fake store would only repeat what it was written
/// to do. Skips until migration 013 has created the <c>explorer</c> schema.
/// </remarks>
public sealed class ExplorerSavedQueryStoreTests : IClassFixture<IdentityFixture>, IAsyncLifetime
{
    private const string InitialPassword = "Vx9-quiet-owl-lantern";

    private readonly IdentityFixture _fixture;
    private readonly List<long> _users = [];
    private string? _missingSchema;

    public ExplorerSavedQueryStoreTests(IdentityFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(IdentityFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.tables WHERE table_schema = 'explorer' AND table_name = 'saved_query'",
            connection);

        if (await command.ExecuteScalarAsync() is null)
        {
            _missingSchema = "explorer.saved_query does not exist; apply operational migration 013 first";
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Their saved queries go with them: the foreign key cascades.
        foreach (var id in _users)
        {
            await _fixture.DeleteUserAsync(id);
        }
    }

    private bool Skip(out string reason)
    {
        reason = _fixture.UnavailableReason ?? _missingSchema ?? string.Empty;
        return !_fixture.IsAvailable || _missingSchema is not null;
    }

    private async Task<long> UserAsync(string hint)
    {
        var id = await _fixture.Users.CreateAsync(
            new NewUser(IdentityFixture.UniqueUsername(hint), $"Test {hint}", null, null, null, ["viewer"], MustChangePassword: false),
            InitialPassword, "tests", IdentityFixture.Context, TestContext.Current.CancellationToken);

        _users.Add(id);
        return id;
    }

    private static readonly ExplorerQueryRequest Query = new(
        ExplorerDataset.Events,
        Where: new ExplorerFilter(ExplorerLogic.And,
        [
            new(Field: "date", Operator: ExplorerOperator.Between, Values: ["2026-09-01", "2026-09-28"]),
            new(Not: true, Field: "brand", Operator: ExplorerOperator.In, Values: ["Samsung", "Apple"]),
        ]),
        Columns: ["date", "change", "msisdn"],
        GroupBy: null,
        Measures: [new("sims", ExplorerAggregate.CountDistinct, "imsi", ActiveOnly: true)],
        Sort: [new("date", Descending: true)],
        PageSize: 200);

    [Fact]
    public async Task A_saved_query_reads_back_exactly_as_it_was_saved()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var ct = TestContext.Current.CancellationToken;
        var owner = await UserAsync("saved");

        var (outcome, saved) = await _fixture.SavedQueries.CreateAsync(owner, "  September churn  ", "notes", Query, ct);
        var read = await _fixture.SavedQueries.GetAsync(owner, saved!.Id, ct);

        Assert.Equal(SavedQueryOutcome.Saved, outcome);
        Assert.Equal("September churn", read!.Name);

        // Records compare lists by reference, so compare the JSON the API would send.
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(Query),
            System.Text.Json.JsonSerializer.Serialize(read.Query));
    }

    [Fact]
    public async Task Nobody_reads_changes_or_deletes_another_persons_query()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var ct = TestContext.Current.CancellationToken;
        var owner = await UserAsync("owner");
        var other = await UserAsync("other");

        var (_, saved) = await _fixture.SavedQueries.CreateAsync(owner, "Mine", "", Query, ct);

        Assert.Empty(await _fixture.SavedQueries.ListAsync(other, ct));
        Assert.Null(await _fixture.SavedQueries.GetAsync(other, saved!.Id, ct));
        Assert.Equal(SavedQueryOutcome.NotFound,
            (await _fixture.SavedQueries.UpdateAsync(other, saved.Id, "Taken over", "", Query, ct)).Outcome);
        Assert.False(await _fixture.SavedQueries.DeleteAsync(other, saved.Id, ct));

        Assert.Equal("Mine", (await _fixture.SavedQueries.GetAsync(owner, saved.Id, ct))!.Name);
    }

    [Fact]
    public async Task A_name_is_unique_per_owner_ignoring_case_and_spaces_but_not_across_owners()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var ct = TestContext.Current.CancellationToken;
        var owner = await UserAsync("names");
        var other = await UserAsync("names2");

        await _fixture.SavedQueries.CreateAsync(owner, "Dual SIM check", "", Query, ct);
        var (_, second) = await _fixture.SavedQueries.CreateAsync(owner, "Another", "", Query, ct);

        Assert.Equal(SavedQueryOutcome.NameTaken,
            (await _fixture.SavedQueries.CreateAsync(owner, " dual sim CHECK ", "", Query, ct)).Outcome);
        Assert.Equal(SavedQueryOutcome.NameTaken,
            (await _fixture.SavedQueries.UpdateAsync(owner, second!.Id, "DUAL SIM CHECK", "", Query, ct)).Outcome);
        Assert.Equal(SavedQueryOutcome.Saved,
            (await _fixture.SavedQueries.CreateAsync(other, "Dual SIM check", "", Query, ct)).Outcome);
    }

    [Fact]
    public async Task The_list_is_most_recently_changed_first()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var ct = TestContext.Current.CancellationToken;
        var owner = await UserAsync("order");

        var (_, first) = await _fixture.SavedQueries.CreateAsync(owner, "First", "", Query, ct);
        await _fixture.SavedQueries.CreateAsync(owner, "Second", "", Query, ct);
        await _fixture.SavedQueries.UpdateAsync(owner, first!.Id, "First, edited", "", Query, ct);

        Assert.Equal(["First, edited", "Second"], (await _fixture.SavedQueries.ListAsync(owner, ct)).Select(q => q.Name));
    }
}
