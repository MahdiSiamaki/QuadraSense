using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion.Tests;

/// <summary>
/// Tests for the rules that decide which rows reach the analytics store.
/// </summary>
/// <remarks>
/// The accept/warn/reject split is not a style choice, and these tests pin the measured
/// behaviour behind it. Two cases matter more than the rest:
/// <list type="bullet">
///   <item><description>
///     <c>000000</c> is the source's unknown-device sentinel and appears on 8,776,237 bindings.
///     A validator that rejected it would silently drop 7% of the population, and the totals
///     would stop matching the source with nothing on screen to explain it.
///   </description></item>
///   <item><description>
///     31,209 bindings carry an IMEI that is neither 14 digits nor the sentinel. They are real
///     bindings, so they are imported - with a warning, because the count is a data-quality
///     signal that should be visible rather than absorbed.
///   </description></item>
/// </list>
/// </remarks>
public class SqmRowValidatorTests
{
    private static (RowVerdict Verdict, List<RowFinding> Findings) Check(string line)
    {
        var findings = new List<RowFinding>();
        var verdict = SqmRowValidator.Validate(line, findings);
        return (verdict, findings);
    }

    [Fact]
    public void A_well_formed_row_is_accepted_with_no_findings()
    {
        var (verdict, findings) = Check("9140257910,432110123456789,35209012345678,add");

        Assert.Equal(RowVerdict.Accept, verdict);
        Assert.Empty(findings);
    }

    [Fact]
    public void The_unknown_device_sentinel_is_accepted_not_flagged()
    {
        // Measured on 8,776,237 bindings. This is the single most important case in the file:
        // treating it as malformed would drop 7% of the population without saying so.
        var (verdict, findings) = Check("9140257910,432110123456789,000000,add");

        Assert.Equal(RowVerdict.Accept, verdict);
        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("352090123456", "too short")]
    [InlineData("3520901234567890", "too long")]
    public void An_imei_of_the_wrong_length_is_imported_with_a_warning(string imei, string why)
    {
        var (verdict, findings) = Check($"9140257910,432110123456789,{imei},add");

        Assert.Equal(RowVerdict.Warn, verdict);
        Assert.Equal(SqmRules.ImeiLength, Assert.Single(findings).RuleCode);
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    public void A_non_numeric_imei_is_imported_with_a_warning()
    {
        // The analytics column is a String, so the value is representable. It is still worth
        // counting, which is why it warns rather than passing silently.
        var (verdict, findings) = Check("9140257910,432110123456789,35209012ABCDEF,add");

        Assert.Equal(RowVerdict.Warn, verdict);
        Assert.Equal(SqmRules.ImeiNotNumeric, Assert.Single(findings).RuleCode);
    }

    [Theory]
    [InlineData("nine,432110123456789,35209012345678,add", SqmRules.MsisdnNotNumeric)]
    [InlineData("9140257910,imsi,35209012345678,add", SqmRules.ImsiNotNumeric)]
    [InlineData("9140257910,432110123456789,35209012345678,changed", SqmRules.LabelUnknown)]
    public void A_value_the_store_cannot_represent_is_rejected(string line, string expectedRule)
    {
        var (verdict, findings) = Check(line);

        Assert.Equal(RowVerdict.Reject, verdict);
        Assert.Contains(findings, f => f.RuleCode == expectedRule);
    }

    [Theory]
    [InlineData("9140257910,432110123456789,35209012345678")]
    [InlineData("9140257910,432110123456789,35209012345678,add,extra")]
    public void A_row_with_the_wrong_number_of_fields_is_rejected(string line)
    {
        var (verdict, findings) = Check(line);

        Assert.Equal(RowVerdict.Reject, verdict);
        Assert.Equal(SqmRules.FieldCount, findings[0].RuleCode);
    }

    [Fact]
    public void A_row_can_break_several_rules_and_the_strongest_verdict_wins()
    {
        // A rejectable MSISDN alongside a merely odd IMEI. The row must not be imported, and
        // both findings must survive so the quarantine view shows the whole picture rather than
        // whichever rule happened to run first.
        var (verdict, findings) = Check("nope,432110123456789,1234,add");

        Assert.Equal(RowVerdict.Reject, verdict);
        Assert.Contains(findings, f => f.RuleCode == SqmRules.MsisdnNotNumeric);
        Assert.Contains(findings, f => f.RuleCode == SqmRules.ImeiLength);
    }

    [Fact]
    public void Surrounding_whitespace_does_not_change_the_verdict()
    {
        var (verdict, _) = Check(" 9140257910 , 432110123456789 , 35209012345678 , add ");

        Assert.Equal(RowVerdict.Accept, verdict);
    }

    [Fact]
    public void Remove_is_a_valid_label()
    {
        var (verdict, findings) = Check("9140257910,432110123456789,35209012345678,remove");

        Assert.Equal(RowVerdict.Accept, verdict);
        Assert.Empty(findings);
    }

    [Fact]
    public void Every_rule_has_an_explanation_a_person_can_read()
    {
        // A quarantine screen showing "SQM_IMEI_NOT_NUMERIC: 31,209" and nothing else makes the
        // operator guess. Each rule owes the reader a sentence.
        string[] rules =
        [
            SqmRules.FieldCount, SqmRules.MsisdnNotNumeric, SqmRules.ImsiNotNumeric,
            SqmRules.LabelUnknown, SqmRules.ImeiEmpty, SqmRules.ImeiNotNumeric, SqmRules.ImeiLength,
        ];

        foreach (var rule in rules)
        {
            var description = SqmRowValidator.DescribeRule(rule);

            Assert.NotEqual(rule, description);
            Assert.EndsWith(".", description, StringComparison.Ordinal);
        }
    }
}
