using System.Security.Cryptography;
using Npgsql;
using Sqm.Application.Abstractions;
using Sqm.Contracts.Devices;

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

    private static Task<long> StageAsync(string modelKey, byte[] bytes, int score = 80) =>
        StageAsync(modelKey, bytes, "TestBrand", score, bindings: 0, warnings: "[]", evidence: "{}", sourceType: "encyclopedic");

    /// <summary>A candidate as the package importer stages one: bindings, warnings and evidence included.</summary>
    private static async Task<long> StageAsync(
        string modelKey, byte[] bytes, string brand, int score, long bindings, string warnings, string evidence,
        string sourceType)
    {
        var id = await ScalarAsync("""
            INSERT INTO catalog.device_image_candidate
                (model_key, brand, marketing_name, status, content_type, bytes, sha256,
                 source_type, source_domain, source_url,
                 original_width, original_height, original_bytes, quality_score, score_breakdown,
                 bindings, warnings, evidence)
            VALUES (@key, @brand, 'Model One', 'needs_review', 'image/webp', @bytes, @sha,
                    @sourceType, 'upload.wikimedia.org', 'https://upload.wikimedia.org/x.webp',
                    1200, 1200, 40000, @score,
                    '[{"term":"exactModelMatch","points":30,"detail":"matched"}]'::jsonb,
                    @bindings, CAST(@warnings AS jsonb), CAST(@evidence AS jsonb))
            RETURNING id
            """,
            ("key", modelKey), ("bytes", bytes), ("sha", SHA256.HashData(bytes)), ("score", score),
            ("brand", brand), ("bindings", bindings), ("warnings", warnings), ("evidence", evidence),
            ("sourceType", sourceType));

        return Convert.ToInt64(id, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A brand nothing else uses, so a filtered page holds this test's rows and nothing else.</summary>
    private static string Brand() => "TestBrand-" + Guid.NewGuid().ToString("N")[..8];

    private static DeviceImageCandidateQuery Query(
        string brand, bool? onNetwork = null, string warnings = "any", string sort = "bindings", string? sourceType = null) =>
        new("needs_review", brand, sourceType, onNetwork, warnings, sort, 60, 0);

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
        var brand = Brand();
        await StageAsync(key, new byte[] { 1, 1, 1, 1, 1 }, brand, 80, 0, "[]", "{}", "encyclopedic");

        // Filtered by its own brand: the real queue holds a couple of thousand package candidates,
        // ordered by bindings, and a test row with none would never be on the first page.
        var page = await _fixture.ImageCandidates.ListAsync(Query(brand), TestContext.Current.CancellationToken);
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
    public async Task Each_filter_and_both_orders_return_exactly_the_matching_candidates()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var brand = Brand();
        const string Info = """[{"code":"touches_edges","severity":"info","detail":"x"}]""";
        const string High = """[{"code":"partial_coverage","severity":"high","detail":"x"}]""";
        const string Both = """[{"code":"touches_edges","severity":"info","detail":"x"},{"code":"different_variant","severity":"high","detail":"y"}]""";

        // bindings, score, warnings, source: a is the most carried model, b is not on the network.
        var a = await StageAsync(Key(), [1, 0, 1], brand, 50, 500, "[]", "{}", "manufacturer");
        var b = await StageAsync(Key(), [1, 0, 2], brand, 90, 0, Info, "{}", "retailer");
        var c = await StageAsync(Key(), [1, 0, 3], brand, 70, 100, High, "{}", "manufacturer");
        var d = await StageAsync(Key(), [1, 0, 4], brand, 60, 50, Both, "{}", "museum_archive");

        async Task<long[]> Ids(DeviceImageCandidateQuery query) =>
            [.. (await _fixture.ImageCandidates.ListAsync(query, TestContext.Current.CancellationToken)).Items.Select(i => i.Id)];

        Assert.Equal([a, c, d, b], await Ids(Query(brand)));
        Assert.Equal([b, c, d, a], await Ids(Query(brand, sort: "score")));
        Assert.Equal([a, c, d], await Ids(Query(brand, onNetwork: true)));
        Assert.Equal([b], await Ids(Query(brand, onNetwork: false)));
        Assert.Equal([c, d, b], await Ids(Query(brand, warnings: "with")));
        Assert.Equal([a], await Ids(Query(brand, warnings: "without")));
        Assert.Equal([c, d], await Ids(Query(brand, warnings: "high")));
        Assert.Equal([a, c], await Ids(Query(brand, sourceType: "manufacturer")));

        // The brand matches without regard to case, and the total is the filtered total.
        var upper = await _fixture.ImageCandidates.ListAsync(Query(brand.ToUpperInvariant()), TestContext.Current.CancellationToken);
        Assert.Equal(4, upper.Total);

        var facets = await _fixture.ImageCandidates.GetFacetsAsync("needs_review", TestContext.Current.CancellationToken);
        Assert.Contains(facets.Brands, f => f.Value == brand && f.Count == 4);
    }

    [Fact]
    public async Task Warnings_and_evidence_come_back_as_staged_and_a_bad_element_costs_only_itself()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var brand = Brand();
        await StageAsync(Key(), [2, 0, 1], brand, 80, 1_645_449,
            """[{"code":"partial_coverage","severity":"high","detail":"1 of 200 TACs"},{"severity":"high","detail":"no code"},{"code":"aspect","severity":"alarming","detail":"z"}]""",
            """{"package":"TAC_Catalog_Images_20260916","productName":"Galaxy A14 5G","matchMethods":["official_model_code"],"mappedTacs":1,"modelTacs":"200","modelBindings":1645449}""",
            "manufacturer");

        var row = Assert.Single((await _fixture.ImageCandidates.ListAsync(Query(brand), TestContext.Current.CancellationToken)).Items);

        Assert.Equal(1_645_449, row.Bindings);

        // A warning without a code is dropped; an unknown severity is shown as info, never as high.
        Assert.Equal(
            [("partial_coverage", "high", "1 of 200 TACs"), ("aspect", "info", "z")],
            row.Warnings.Select(w => (w.Code, w.Severity, w.Detail)));

        Assert.NotNull(row.Evidence);
        Assert.Equal(("Galaxy A14 5G", "TAC_Catalog_Images_20260916", (long?)1), (row.Evidence!.ProductName, row.Evidence.Package, row.Evidence.MappedTacs));
        Assert.Equal(["official_model_code"], row.Evidence.MatchMethods!);

        // A TAC count written as a string is not read as a number: that member is null, the rest stand.
        Assert.Null(row.Evidence.ModelTacs);
        Assert.Equal(1_645_449L, row.Evidence.ModelBindings);
    }

    [Fact]
    public async Task A_batch_reports_the_model_of_each_candidate_so_a_clash_can_be_refused()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var shared = Key();
        var first = await StageAsync(shared, [3, 0, 1]);
        var second = await StageAsync(shared, [3, 0, 2]);
        var other = await StageAsync(Key(), [3, 0, 3]);

        var models = await _fixture.ImageCandidates.GetModelsAsync([first, second, other, -1], TestContext.Current.CancellationToken);

        Assert.Equal([first, second, other], models.Select(m => m.Id));
        Assert.Equal(2, models.Count(m => m.ModelKey == shared));
    }

    [Fact]
    public async Task Replacing_warnings_follow_the_live_image_not_what_was_stored()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var brand = Brand();
        var key = Key();

        // Staged when the model had a verified image - a snapshot that is now wrong: there is none.
        var stale = await StageAsync(key, [5, 0, 1], brand, 80, 10,
            """[{"code":"replaces_verified","severity":"high","detail":"old"},{"code":"touches_edges","severity":"info","detail":"x"}]""",
            "{}", "manufacturer");
        var sibling = await StageAsync(key, [5, 0, 2], brand, 70, 10, "[]", "{}", "manufacturer");

        async Task<DeviceImageCandidate> Row(long id) =>
            (await _fixture.ImageCandidates.ListAsync(Query(brand), TestContext.Current.CancellationToken)).Items.Single(i => i.Id == id);

        Assert.Equal(["touches_edges"], (await Row(stale)).Warnings.Select(w => w.Code));
        Assert.Empty(await HighIds(brand));

        // A reviewer approves the sibling: the first candidate now replaces a verified image.
        Assert.True(await _fixture.ImageCandidates.ApproveAsync(sibling, 1, TestContext.Current.CancellationToken));

        var now = await Row(stale);
        Assert.Equal(("replaces_verified", "high"), (now.Warnings[0].Code, now.Warnings[0].Severity));
        Assert.Equal([stale], await HighIds(brand));
    }

    private async Task<long[]> HighIds(string brand) =>
        [.. (await _fixture.ImageCandidates.ListAsync(Query(brand, warnings: "high"), TestContext.Current.CancellationToken)).Items.Select(i => i.Id)];

    [Fact]
    public async Task A_brand_stored_with_spaces_is_listed_and_found_trimmed()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var brand = Brand();
        await StageAsync(Key(), [6, 0, 1], brand + "  ", 80, 0, "[]", "{}", "manufacturer");
        await StageAsync(Key(), [6, 0, 2], brand.ToUpperInvariant(), 80, 0, "[]", "{}", "manufacturer");

        var facets = await _fixture.ImageCandidates.GetFacetsAsync("needs_review", TestContext.Current.CancellationToken);
        var entry = Assert.Single(facets.Brands, f => string.Equals(f.Value, brand, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, entry.Count);
        Assert.Equal(entry.Value, entry.Value.Trim());

        // What the facet offers, sent back, finds both.
        Assert.Equal(2, (await _fixture.ImageCandidates.ListAsync(Query(entry.Value), TestContext.Current.CancellationToken)).Total);
    }

    [Fact]
    public async Task A_page_past_the_end_still_reports_how_many_match()
    {
        if (Skip(out var reason))
        {
            Assert.Skip(reason);
        }

        var brand = Brand();
        await StageAsync(Key(), [7, 0, 1], brand, 80, 0, "[]", "{}", "manufacturer");
        await StageAsync(Key(), [7, 0, 2], brand, 80, 0, "[]", "{}", "manufacturer");

        // After a reviewer approves the whole last page: nothing on it, but the queue is not empty.
        var past = await _fixture.ImageCandidates.ListAsync(
            new DeviceImageCandidateQuery("needs_review", brand, null, null, "any", "bindings", 1, 5),
            TestContext.Current.CancellationToken);

        Assert.Empty(past.Items);
        Assert.Equal(2, past.Total);
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
