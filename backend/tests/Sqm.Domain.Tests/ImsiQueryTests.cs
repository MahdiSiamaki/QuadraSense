using Sqm.Domain.Identifiers;

namespace Sqm.Domain.Tests;

/// <summary>
/// The rule that decides what counts as a searchable IMSI.
/// </summary>
/// <remarks>
/// Every case here cites the measurement it comes from. The minimum prefix length is not a
/// preference: it is what the distribution of 295,013,916 IMSIs makes usable.
/// </remarks>
public sealed class ImsiQueryTests
{
    [Theory]
    [InlineData("4")]
    [InlineData("43211")]
    [InlineData("432113")]
    [InlineData("432113991")]
    public void A_prefix_shorter_than_ten_digits_is_refused(string input)
    {
        // Measured: 5 digits -> 1 distinct value, i.e. the whole table. 8 digits -> 86 buckets
        // averaging 3,426,414 rows with a worst case of 20,139,179. Ten is the first length whose
        // result is small enough to page through.
        Assert.False(ImsiQuery.TryParse(input, out _, out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void The_refusal_names_the_reason_rather_than_the_rule()
    {
        Assert.False(ImsiQuery.TryParse("43211", out _, out var problem));

        // "Minimum 10 digits" invites the reaction that the limit is arbitrary. The fact that
        // 43211 is on every row in the dataset does not.
        Assert.Contains("43211", problem, StringComparison.Ordinal);
        Assert.Contains("whole network", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Ten_digits_is_accepted_as_a_prefix()
    {
        Assert.True(ImsiQuery.TryParse("4321139917", out var query, out var problem));
        Assert.Null(problem);
        Assert.False(query.Value.IsExact);
    }

    [Fact]
    public void A_prefix_becomes_a_numeric_range_not_a_string_match()
    {
        // This is what makes prefix search fast: the range is a contiguous span of UInt64 that the
        // primary index of an IMSI-ordered table can seek to. A LIKE would read the whole column.
        Assert.True(ImsiQuery.TryParse("4321139917", out var query, out _));

        Assert.Equal(432_113_991_700_000UL, query.Value.Low);
        Assert.Equal(432_113_991_799_999UL, query.Value.High);
        Assert.Equal(100_000UL, query.Value.RangeSize);
    }

    [Theory]
    [InlineData("4321139917", 100_000UL)]
    [InlineData("432113991761", 1_000UL)]
    [InlineData("43211399176133", 10UL)]
    public void The_range_narrows_by_a_factor_of_ten_per_digit(string input, ulong expected)
    {
        Assert.True(ImsiQuery.TryParse(input, out var query, out _));
        Assert.Equal(expected, query.Value.RangeSize);
    }

    [Fact]
    public void Fifteen_digits_is_exact_and_its_range_is_a_single_value()
    {
        Assert.True(ImsiQuery.TryParse("432113991761332", out var query, out _));

        Assert.True(query.Value.IsExact);
        Assert.True(query.Value.IsWellFormed);
        Assert.Equal(query.Value.Low, query.Value.High);
        Assert.Equal(1UL, query.Value.RangeSize);
    }

    [Theory]
    [InlineData("4321121129462907", 16)]
    [InlineData("43211293212406914", 17)]
    public void Sixteen_and_seventeen_digit_anomalies_stay_searchable(string input, int digits)
    {
        // 28 rows in the dataset are this shape. They are wrong, and an operator has to be able to
        // find them; refusing to search for them would make the anomaly invisible rather than fixed.
        Assert.True(ImsiQuery.TryParse(input, out var query, out _));

        Assert.True(query.Value.IsExact);
        Assert.False(query.Value.IsWellFormed);
        Assert.Equal(digits, query.Value.DigitCount);
    }

    [Fact]
    public void Eighteen_digits_is_refused()
    {
        Assert.False(ImsiQuery.TryParse("432112911294629071", out _, out var problem));
        Assert.Contains("18 digits", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("432 11 3991761332")]
    [InlineData("432-11-3991761332")]
    [InlineData("  432113991761332  ")]
    [InlineData("432.11.3991761332")]
    public void Separators_people_paste_are_ignored(string input)
    {
        Assert.True(ImsiQuery.TryParse(input, out var query, out _));
        Assert.Equal("432113991761332", query.Value.Digits);
        Assert.Equal(432_113_991_761_332UL, query.Value.Low);
    }

    [Theory]
    [InlineData("43211399176133a")]
    [InlineData("imsi 432113991761332")]
    [InlineData("432113991761332O")]
    public void A_letter_is_a_typo_and_is_refused_rather_than_dropped(string input)
    {
        // Dropping it would turn "432113991761332O" into a valid 15-digit search for a different
        // SIM than the one the user meant.
        Assert.False(ImsiQuery.TryParse(input, out _, out var problem));
        Assert.Contains("digits only", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_input_is_refused_without_a_confusing_message()
    {
        Assert.False(ImsiQuery.TryParse("", out _, out var problem));
        Assert.Equal("Enter an IMSI.", problem);

        Assert.False(ImsiQuery.TryParse(null, out _, out problem));
        Assert.Equal("Enter an IMSI.", problem);
    }

    [Fact]
    public void Two_different_prefixes_never_select_overlapping_ranges()
    {
        // The invariant that makes paging correct: a row belongs to exactly one prefix of a given
        // length, so a search cannot return the same binding under two different terms.
        Assert.True(ImsiQuery.TryParse("4321139917", out var a, out _));
        Assert.True(ImsiQuery.TryParse("4321139918", out var b, out _));

        Assert.True(a.Value.High < b.Value.Low);
    }

    [Fact]
    public void A_longer_prefix_is_contained_by_its_shorter_one()
    {
        Assert.True(ImsiQuery.TryParse("4321139917", out var wide, out _));
        Assert.True(ImsiQuery.TryParse("432113991761", out var narrow, out _));

        Assert.True(narrow.Value.Low >= wide.Value.Low);
        Assert.True(narrow.Value.High <= wide.Value.High);
    }
}
