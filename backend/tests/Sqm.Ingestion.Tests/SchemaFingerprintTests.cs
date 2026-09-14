using Sqm.Application.DataImport;

namespace Sqm.Ingestion.Tests;

/// <summary>
/// Tests for the rule that decides whether a changed header can still be imported.
/// </summary>
/// <remarks>
/// The reorder case is the one that matters most, and it is not hypothetical. ClickHouse's
/// <c>CSVWithNames</c> matches by header name, and this project has already shipped a version
/// where eleven TAC columns were renamed to placeholders: the load reported the correct row
/// count and every one of those columns silently received nothing. It surfaced weeks later,
/// when an eSIM query returned zero.
/// </remarks>
public class SchemaFingerprintTests
{
    private static readonly string[] Contract = ["msisdn", "imsi", "imei", "label"];

    [Fact]
    public void The_same_columns_produce_the_same_fingerprint()
    {
        Assert.Equal(
            SchemaFingerprint.Compute(Contract),
            SchemaFingerprint.Compute(["msisdn", "imsi", "imei", "label"]));
    }

    [Fact]
    public void Case_and_padding_are_formatting_not_contract()
    {
        Assert.Equal(
            SchemaFingerprint.Compute(Contract),
            SchemaFingerprint.Compute([" MSISDN", "Imsi ", "IMEI", " Label "]));
    }

    [Fact]
    public void Order_is_part_of_the_contract()
    {
        Assert.NotEqual(
            SchemaFingerprint.Compute(Contract),
            SchemaFingerprint.Compute(["imsi", "msisdn", "imei", "label"]));
    }

    [Fact]
    public void Appending_a_column_is_accepted_with_a_warning()
    {
        var (verdict, explanation) = SchemaFingerprint.Compare(
            Contract, ["msisdn", "imsi", "imei", "label", "source_system"]);

        Assert.Equal(SchemaVerdict.AcceptedWithWarning, verdict);
        Assert.Contains("source_system", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_a_column_is_rejected_and_the_message_names_it()
    {
        var (verdict, explanation) = SchemaFingerprint.Compare(
            Contract, ["msisdn", "imsi", "imei"]);

        Assert.Equal(SchemaVerdict.Rejected, verdict);
        Assert.Contains("label", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Reordering_is_rejected_even_though_every_column_is_present()
    {
        // The case that loads perfectly and is entirely wrong.
        var (verdict, explanation) = SchemaFingerprint.Compare(
            Contract, ["msisdn", "imei", "imsi", "label"]);

        Assert.Equal(SchemaVerdict.Rejected, verdict);
        Assert.Contains("reordered", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Renaming_a_column_is_rejected()
    {
        var (verdict, _) = SchemaFingerprint.Compare(
            Contract, ["msisdn", "imsi", "device_imei", "label"]);

        Assert.Equal(SchemaVerdict.Rejected, verdict);
    }

    [Fact]
    public void An_explanation_is_always_something_a_person_can_act_on()
    {
        string[][] changes =
        [
            ["msisdn", "imsi", "imei"],
            ["msisdn", "imei", "imsi", "label"],
            ["msisdn", "imsi", "imei", "label", "extra"],
        ];

        foreach (var incoming in changes)
        {
            var (_, explanation) = SchemaFingerprint.Compare(Contract, incoming);

            Assert.NotEmpty(explanation);
            Assert.EndsWith(".", explanation, StringComparison.Ordinal);
        }
    }
}
