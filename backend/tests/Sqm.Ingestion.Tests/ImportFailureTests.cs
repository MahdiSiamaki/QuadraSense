using Sqm.Ingestion;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion.Tests;

/// <summary>
/// Tests for how a failure is classified and how much of it survives to the operator.
/// </summary>
/// <remarks>
/// The length rule earned these tests in production. The TAC structural scan's rejection runs to
/// roughly 900 characters and ends with the sentence that says what to do about it; every failure
/// summary was clipped at 500, so the stored message stopped at "...it is the key manufacturer,
/// model an" and the remedy was the one part nobody could read.
/// </remarks>
public class ImportFailureTests
{
    [Fact]
    public void A_rejected_file_is_not_retried()
    {
        var (retryable, _) = ImportFailure.Classify(
            new ImportRejectedException("The header is wrong."));

        Assert.False(retryable);
    }

    [Fact]
    public void An_unrecognised_failure_is_retried()
    {
        // Wrong in this direction costs two attempts; wrong in the other permanently fails a
        // day's data and waits for a human to notice.
        var (retryable, _) = ImportFailure.Classify(new InvalidOperationException("odd"));

        Assert.True(retryable);
    }

    [Fact]
    public void An_authored_rejection_survives_past_the_incidental_limit()
    {
        var message = TacScanRules.Evaluate(new TacFileScan(
            482_047, 26, 27, 270_885, 211_135, 211_135, 0, 113_551, [],
            ["35308690", "35308700"])).Rejection!;

        Assert.True(message.Length > 500,
            $"the real rejection should be longer than the old limit, was {message.Length}");

        var (_, summary) = ImportFailure.Classify(new ImportRejectedException(message));

        Assert.Equal(message, summary);
        Assert.DoesNotContain("[...]", summary, StringComparison.Ordinal);

        // The part that was being lost.
        Assert.Contains("downloading it again", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_remedy_leads_so_a_clip_can_never_remove_it()
    {
        var message = TacScanRules.Evaluate(new TacFileScan(
            482_047, 26, 27, 270_885, 211_135, 211_135, 0, 113_551, [],
            ["35308690"])).Rejection!;

        Assert.True(
            message.IndexOf("downloading it again", StringComparison.Ordinal) < 300,
            "what to do must come before the evidence, not after it");
    }

    [Fact]
    public void An_incidental_message_is_still_clipped_and_says_so()
    {
        // ClickHouse quotes the offending input back, so these run to kilobytes of file content.
        var noisy = new string('x', 40) + " " + string.Join(' ', Enumerable.Repeat("row", 400));

        var (_, summary) = ImportFailure.Classify(new HttpRequestException(noisy));

        Assert.True(summary.Length <= 500, $"was {summary.Length}");
        Assert.EndsWith("[...]", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clipped_message_never_stops_mid_word()
    {
        var words = string.Join(' ', Enumerable.Repeat("manufacturer", 300));

        var (_, summary) = ImportFailure.Classify(new HttpRequestException(words));
        var body = summary[..^" [...]".Length];

        Assert.All(
            body.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            word => Assert.Equal("manufacturer", word));
    }

    [Fact]
    public void A_single_enormous_token_is_still_clipped_rather_than_discarded()
    {
        var (_, summary) = ImportFailure.Classify(
            new HttpRequestException(new string('y', 4_000)));

        Assert.True(summary.Length <= 500, $"was {summary.Length}");
        Assert.True(summary.Length > 400, $"most of the budget should still be used, was {summary.Length}");
    }
}
