using Microsoft.Extensions.Options;
using Npgsql;
using Sqm.Application.Campaigns;
using Sqm.Application.Identity;
using Sqm.Infrastructure.DataImport;
using Sqm.Infrastructure.Identity;

namespace Sqm.Integration.Tests;

/// <summary>
/// Campaign list requests against the real database: a second person decides, once; the table itself
/// refuses a requester's own decision; a list under the threshold is approved on request.
/// </summary>
/// <remarks>
/// The four-eyes rule is SQL - a conditional UPDATE and a CHECK - so a fake store would only repeat
/// what it was told. Connects as sqm_app, which has no DELETE on the table: cleanup removes the test
/// users, and their requests go with them by the foreign key. Skips until migration 021 is applied.
/// </remarks>
public sealed class CampaignExportStoreTests : IClassFixture<IdentityFixture>, IAsyncLifetime
{
    private const string InitialPassword = "Vx9-quiet-owl-lantern";

    private static readonly CampaignListDefinition Definition = new(
        "sunset", "exposed", null, "Nokia", null, null, 1790000000000, 246, "2026-10-10");

    private readonly IdentityFixture _fixture;
    private readonly List<long> _users = [];
    private string? _missingSchema;

    public CampaignExportStoreTests(IdentityFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(IdentityFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.tables WHERE table_schema = 'campaign' AND table_name = 'export_request'", connection);
        if (await command.ExecuteScalarAsync() is null)
        {
            _missingSchema = "campaign.export_request does not exist; apply operational migration 021 and its grants first";
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Requesters first: their requests cascade away, and a request still naming its decider would
        // otherwise hold the decider's row.
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

    private static PostgresCampaignExportStore Store()
    {
        var postgres = Options.Create(new PostgresOptions { ConnectionString = IdentityFixture.ConnectionString });
        return new PostgresCampaignExportStore(new IdentityDataSource(postgres), postgres);
    }

    private async Task<long> UserAsync(string hint)
    {
        var id = await _fixture.Users.CreateAsync(
            new NewUser(IdentityFixture.UniqueUsername(hint), $"Test {hint}", null, null, null, ["viewer"], MustChangePassword: false),
            InitialPassword, "tests", IdentityFixture.Context, TestContext.Current.CancellationToken);
        _users.Add(id);
        return id;
    }

    [Fact]
    public async Task A_second_person_decides_once_and_the_requester_never()
    {
        Assert.SkipWhen(Skip(out var reason), reason);
        var ct = TestContext.Current.CancellationToken;
        var requester = await UserAsync("req");
        var approver = await UserAsync("appr");
        var store = Store();

        var request = await store.CreateAsync(Definition, 1_250, "2G/3G migration, wave 1", requester, needsApproval: true, null, ct);
        Assert.Equal(("pending", null), (request.Status, request.ExpiresAt));
        Assert.Equal(Definition, request.Definition);

        var own = await store.DecideAsync(request.Id, requester, approve: true, "mine", DateTimeOffset.UtcNow.AddHours(72), ct);
        Assert.Equal(CampaignDecisionOutcome.OwnRequest, own.Outcome);

        var decided = await store.DecideAsync(request.Id, approver, approve: true, "agreed", DateTimeOffset.UtcNow.AddHours(72), ct);
        Assert.Equal(CampaignDecisionOutcome.Decided, decided.Outcome);
        Assert.Equal(("approved", approver), (decided.Request!.Status, decided.Request.DecidedBy));
        Assert.NotNull(decided.Request.ExpiresAt);

        var again = await store.DecideAsync(request.Id, approver, approve: false, "changed my mind", null, ct);
        Assert.Equal(CampaignDecisionOutcome.NotPending, again.Outcome);
        Assert.Equal("approved", (await store.GetAsync(request.Id, ct))!.Status);
    }

    [Fact]
    public async Task The_table_refuses_a_requesters_own_decision_even_past_the_application()
    {
        Assert.SkipWhen(Skip(out var reason), reason);
        var ct = TestContext.Current.CancellationToken;
        var requester = await UserAsync("self");
        var request = await Store().CreateAsync(Definition, 1_250, "2G/3G migration, wave 1", requester, needsApproval: true, null, ct);

        await using var connection = new NpgsqlConnection(IdentityFixture.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "UPDATE campaign.export_request SET status = 'approved', decided_by = requested_by, decided_at = now() WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", request.Id);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct));
        Assert.Equal("23514", refused.SqlState);
    }

    [Fact]
    public async Task Under_the_threshold_it_is_approved_on_request_and_can_be_withdrawn()
    {
        Assert.SkipWhen(Skip(out var reason), reason);
        var ct = TestContext.Current.CancellationToken;
        var requester = await UserAsync("small");
        var store = Store();

        var request = await store.CreateAsync(Definition, 40, "A small pilot of forty", requester, needsApproval: false,
            DateTimeOffset.UtcNow.AddHours(72), ct);
        Assert.Equal("approved", request.Status);
        Assert.NotNull(request.ExpiresAt);

        await store.RecordDownloadAsync(request.Id, ct);
        Assert.True(await store.WithdrawAsync(request.Id, requester, ct));
        var withdrawn = (await store.GetAsync(request.Id, ct))!;
        Assert.Equal(("withdrawn", 1), (withdrawn.Status, withdrawn.Downloads));
    }
}
