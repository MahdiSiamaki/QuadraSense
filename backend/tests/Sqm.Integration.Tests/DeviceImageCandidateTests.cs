using System.Security.Cryptography;
using Npgsql;

namespace Sqm.Integration.Tests;

/// <summary>
/// The safety properties of the image review queue, against a real database.
/// </summary>
/// <remarks>
/// <para>
/// Every guarantee here is a property of PostgreSQL rather than of the application: an approval is
/// a transaction, a deduplication is a unique index, and a rejection is a row that has to survive.
/// A mocked repository would assert our own expectations back at us.
/// </para>
/// <para>
/// The one that matters most is that a live image does not move because a script ran. Automatic
/// sourcing put shop-shelf photographs into this catalogue once, and the fix was not a better
/// search - it was the rule that nothing reaches the live table without a person. These are the
/// tests that would fail if that rule were quietly removed.
/// </para>
/// </remarks>
public sealed class DeviceImageCandidateTests : IClassFixture<IdentityFixture>, IAsyncLifetime
{
    private readonly IdentityFixture _fixture;
    private readonly List<string> _models = [];

    public DeviceImageCandidateTests(IdentityFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        foreach (var key in _models)
        {
            await ExecuteAsync(
                "DELETE FROM catalog.device_image_candidate WHERE model_key = @key;"
                + "DELETE FROM catalog.device_model_image WHERE model_key = @key",
                ("key", key));
        }
    }

    private bool Skip(out string reason)
    {
        reason = _fixture.UnavailableReason ?? string.Empty;
        return !_fixture.IsAvailable;
    }

    /// <summary>A model nothing else uses, so tests cannot tread on each other or on real data.</summary>
    private string Key()
    {
        var key = $"testbrand|model {Guid.NewGuid():N}";
        _models.Add(key);
        return key;
    }

    private static async Task ExecuteAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var connection = new NpgsqlConnection(IdentityFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var connection = new NpgsqlConnection(IdentityFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return await command.ExecuteScalarAsync();
    }

    private static async Task<long> StageAsync(string modelKey, byte[] bytes, int score = 80)
    {
        var id = await ScalarAsync("""
            INSERT INTO catalog.device_image_candidate
                (model_key, brand, marketing_name, status, content_type, bytes, sha256,
                 source_type, source_domain, source_url,
                 original_width, original_height, original_bytes, quality_score, score_breakdown)
            VALUES (@key, 'TestBrand', 'Model One', 'needs_review', 'image/webp', @bytes, @sha,
                    'encyclopedic', 'upload.wikimedia.org', 'https://upload.wikimedia.org/x.webp',
                    1200, 1200, 40000, @score,
                    '[{"term":"exactModelMatch","points":30,"detail":"matched"}]'::jsonb)
            RETURNING id
            """,
            ("key", modelKey), ("bytes", bytes), ("sha", SHA256.HashData(bytes)), ("score", score));

        return Convert.ToInt64(id, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Approving_a_candidate_makes_it_live_and_marks_it_verified()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var key = Key();
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var id = await StageAsync(key, bytes);

        Assert.True(await _fixture.ImageCandidates.ApproveAsync(id, 1, TestContext.Current.CancellationToken));

        var status = await ScalarAsync(
            "SELECT status FROM catalog.device_model_image WHERE model_key = @key", ("key", key));
        Assert.Equal("verified", status);

        var live = await _fixture.LiveImages.GetAsync(key, TestContext.Current.CancellationToken);
        Assert.NotNull(live);
        Assert.Equal(bytes, live!.Bytes);

        // Decided once. A second click cannot re-run the promotion.
        Assert.False(await _fixture.ImageCandidates.ApproveAsync(id, 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Staging_another_candidate_does_not_touch_the_live_image()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var key = Key();
        var original = new byte[] { 9, 9, 9 };
        await _fixture.ImageCandidates.ApproveAsync(await StageAsync(key, original), 1, TestContext.Current.CancellationToken);

        // Exactly what a sourcing run does: propose against a model that already has an image.
        await StageAsync(key, new byte[] { 7, 7, 7 }, score: 99);

        var live = await _fixture.LiveImages.GetAsync(key, TestContext.Current.CancellationToken);
        Assert.NotNull(live);
        Assert.Equal(original, live!.Bytes);

        var status = await ScalarAsync(
            "SELECT status FROM catalog.device_model_image WHERE model_key = @key", ("key", key));
        Assert.Equal("verified", status);
    }

    [Fact]
    public async Task A_rejected_candidate_is_kept_and_cannot_be_approved_afterwards()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var key = Key();
        var id = await StageAsync(key, new byte[] { 4, 4, 4 });

        Assert.True(await _fixture.ImageCandidates.RejectAsync(id, 1, "shop shelf", TestContext.Current.CancellationToken));

        // Kept rather than deleted: (model_key, sha256) is what the sourcing tool checks before
        // proposing, so deleting a rejection would guarantee it came straight back on the next run.
        var stored = await ScalarAsync(
            "SELECT status || '/' || coalesce(rejection_reason, '') "
            + "FROM catalog.device_image_candidate WHERE id = @id", ("id", id));
        Assert.Equal("rejected/shop shelf", stored);

        Assert.False(await _fixture.ImageCandidates.ApproveAsync(id, 1, TestContext.Current.CancellationToken));
        Assert.Null(await _fixture.LiveImages.GetAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_same_image_cannot_be_staged_twice_for_one_model()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var key = Key();
        var bytes = new byte[] { 5, 5, 5, 5 };
        await StageAsync(key, bytes);

        // The unique index is the deduplication. Re-running the sourcing tool is idempotent
        // because of the database, not because the tool remembers what it did.
        await Assert.ThrowsAnyAsync<PostgresException>(() => StageAsync(key, bytes));
    }

    [Fact]
    public async Task The_queue_carries_the_evidence_a_reviewer_needs()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var key = Key();
        await StageAsync(key, new byte[] { 1, 1, 1, 1, 1 });

        var page = await _fixture.ImageCandidates.ListAsync("needs_review", 60, 0, TestContext.Current.CancellationToken);
        var row = page.Items.FirstOrDefault(i => i.ModelKey == key);

        Assert.NotNull(row);

        // The reviewer's first question: am I filling a gap, or overruling somebody?
        Assert.Equal("missing", row!.CurrentStatus);
        Assert.Equal("upload.wikimedia.org", row.SourceDomain);

        // The reasoning travels with the candidate rather than being recomputed against whatever
        // the rules have since become.
        Assert.NotEmpty(row.ScoreBreakdown);
        Assert.Equal("exactModelMatch", row.ScoreBreakdown[0].Term);
        Assert.Equal(30, row.ScoreBreakdown[0].Points);
    }

    [Fact]
    public async Task An_unknown_candidate_cannot_be_approved_rejected_or_fetched()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        Assert.False(await _fixture.ImageCandidates.ApproveAsync(-1, 1, TestContext.Current.CancellationToken));
        Assert.False(await _fixture.ImageCandidates.RejectAsync(-1, 1, null, TestContext.Current.CancellationToken));
        Assert.Null(await _fixture.ImageCandidates.GetImageAsync(-1, TestContext.Current.CancellationToken));
    }
}
