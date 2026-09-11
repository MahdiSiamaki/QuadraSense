using Sqm.Domain.Bindings;
using Sqm.Domain.Identifiers;

namespace Sqm.Domain.Validation;

/// <summary>Outcome of validating one raw delta row.</summary>
public readonly record struct RowValidationResult
{
    private RowValidationResult(
        BindingChange? change,
        IReadOnlyList<ValidationRule> rules,
        RuleSeverity severity)
    {
        Change = change;
        Rules = rules;
        Severity = severity;
    }

    /// <summary>The parsed change, present when the row is usable.</summary>
    public BindingChange? Change { get; }

    /// <summary>Every rule that fired, including informational ones.</summary>
    public IReadOnlyList<ValidationRule> Rules { get; }

    /// <summary>The most severe outcome across all fired rules.</summary>
    public RuleSeverity Severity { get; }

    /// <summary>True when the row should be written to the event log.</summary>
    public bool IsLoadable => Change is not null && Severity is RuleSeverity.Info or RuleSeverity.Warning;

    /// <summary>True when the whole file must be refused.</summary>
    public bool RejectsFile => Severity == RuleSeverity.Reject;

    internal static RowValidationResult Loadable(BindingChange change, List<ValidationRule> rules)
    {
        var severity = rules.Count == 0 ? RuleSeverity.Info : rules.Max(ValidationRules.SeverityOf);
        return new RowValidationResult(change, rules, severity);
    }

    internal static RowValidationResult Failed(List<ValidationRule> rules) =>
        new(null, rules, rules.Count == 0 ? RuleSeverity.Quarantine : rules.Max(ValidationRules.SeverityOf));
}

/// <summary>
/// Validates one row of a daily delta file: <c>msisdn,imsi,imei,label</c>.
/// </summary>
/// <remarks>
/// <para>
/// The column order comes from the <c>1.config</c> sidecar the source ships with every batch
/// (<c>{"msisdn":0,"imsi":1,"imei":2,"label":3}</c>), which is treated as the authoritative contract —
/// <b>not</b> the CSV header. A header can be correct while the columns beneath it are reordered; the
/// sidecar is what the source system actually promises.
/// </para>
/// <para>
/// Note that this differs from the initial dump, whose column order is <c>imei,imsi,msisdn</c> — the
/// reverse. Assuming one order for both files would silently swap every identifier in the system.
/// </para>
/// </remarks>
public static class DeltaRowValidator
{
    /// <summary>Number of fields a delta row must have.</summary>
    public const int ExpectedFieldCount = 4;

    /// <summary>
    /// Validates a single row.
    /// </summary>
    /// <param name="msisdnRaw">Raw MSISDN field.</param>
    /// <param name="imsiRaw">Raw IMSI field.</param>
    /// <param name="imeiRaw">Raw IMEI field.</param>
    /// <param name="labelRaw">Raw label field: <c>add</c> or <c>remove</c>.</param>
    /// <param name="sequence">Delivery sequence of the file this row came from.</param>
    public static RowValidationResult Validate(
        string? msisdnRaw,
        string? imsiRaw,
        string? imeiRaw,
        string? labelRaw,
        int sequence)
    {
        var rules = new List<ValidationRule>();

        if (!Msisdn.TryParse(msisdnRaw, out var msisdn))
        {
            rules.Add(ValidationRule.MsisdnNonNumeric);
        }
        else if (!msisdn.Value.IsWellFormed)
        {
            rules.Add(ValidationRule.MsisdnUnexpectedLength);
        }

        if (!Imsi.TryParse(imsiRaw, out var imsi))
        {
            rules.Add(ValidationRule.ImsiNonNumeric);
        }
        else if (!imsi.Value.IsWellFormed)
        {
            rules.Add(ValidationRule.ImsiUnexpectedLength);
        }

        if (!Imei.TryParse(imeiRaw, out var imei))
        {
            rules.Add(ValidationRule.ImeiNonNumeric);
        }
        else
        {
            switch (imei.Value.Quality)
            {
                case ImeiQuality.UnknownDevice:
                    rules.Add(ValidationRule.ImeiUnknownDevice);
                    break;
                case ImeiQuality.Malformed:
                    rules.Add(ValidationRule.ImeiMalformedLength);
                    break;
                case ImeiQuality.WellFormed:
                default:
                    break;
            }
        }

        if (!TryParseLabel(labelRaw, out var label))
        {
            rules.Add(ValidationRule.LabelUnrecognised);
        }

        var parseFailed =
            msisdn is null || imsi is null || imei is null || label is null ||
            rules.Any(r => ValidationRules.SeverityOf(r) is RuleSeverity.Quarantine or RuleSeverity.Reject);

        if (parseFailed)
        {
            return RowValidationResult.Failed(rules);
        }

        var change = new BindingChange(
            new BindingKey(msisdn!.Value, imsi!.Value, imei!.Value),
            label!.Value,
            sequence);

        return RowValidationResult.Loadable(change, rules);
    }

    /// <summary>
    /// Parses the label. Deliberately strict: only exactly <c>add</c> and <c>remove</c> are accepted.
    /// </summary>
    /// <remarks>
    /// Across all 677,580,701 delta rows, no other value has ever appeared. Accepting a fuzzy match would
    /// trade a loud, correct failure for a silent, wrong state.
    /// </remarks>
    private static bool TryParseLabel(string? raw, out ChangeLabel? label)
    {
        label = raw?.Trim() switch
        {
            "add" => ChangeLabel.Add,
            "remove" => ChangeLabel.Remove,
            _ => null,
        };
        return label is not null;
    }
}
