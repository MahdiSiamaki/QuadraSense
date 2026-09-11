namespace Sqm.Domain.Validation;

/// <summary>How the pipeline reacts when a rule fires.</summary>
public enum RuleSeverity
{
    /// <summary>Expected characteristic of the feed. Counted and charted; the row is kept.</summary>
    Info,

    /// <summary>Unusual but usable. The row is kept and flagged.</summary>
    Warning,

    /// <summary>The row cannot be trusted. It is quarantined with its raw text for review.</summary>
    Quarantine,

    /// <summary>The file itself is not what we expect. Nothing from it is ingested.</summary>
    Reject,
}

/// <summary>
/// The data-quality rules, each with the rate actually measured in discovery.
/// </summary>
/// <remarks>
/// The measured rates matter operationally: they let us alert on <i>drift from known-good</i> rather than
/// on invented thresholds. See <c>docs/architecture/08-observability.md</c>.
/// </remarks>
public enum ValidationRule
{
    /// <summary>IMEI is the <c>000000</c> sentinel. Measured: 6.97% initial, 2.96% delta.</summary>
    ImeiUnknownDevice,

    /// <summary>IMEI is numeric but not 14 digits. Measured: ~0.02%. Kept, excluded from TAC join.</summary>
    ImeiMalformedLength,

    /// <summary>IMEI contains a non-digit. Measured: zero occurrences.</summary>
    ImeiNonNumeric,

    /// <summary>MSISDN is not 10 digits. Measured: 24 rows of 125.9M.</summary>
    MsisdnUnexpectedLength,

    /// <summary>MSISDN contains a non-digit or is empty. Measured: zero occurrences.</summary>
    MsisdnNonNumeric,

    /// <summary>IMSI is not 15 digits. Measured: 9 rows of 125.9M.</summary>
    ImsiUnexpectedLength,

    /// <summary>IMSI contains a non-digit or is empty. Measured: zero occurrences.</summary>
    ImsiNonNumeric,

    /// <summary>Label is neither <c>add</c> nor <c>remove</c>. Measured: zero occurrences in 677.6M rows.</summary>
    LabelUnrecognised,

    /// <summary>The row does not have the expected number of fields.</summary>
    FieldCountMismatch,

    /// <summary>TAC is well formed but absent from the GSMA database. Measured: 0.20% of rows.</summary>
    TacNotFound,
}

/// <summary>Maps each rule to the action the pipeline takes.</summary>
public static class ValidationRules
{
    /// <summary>
    /// The severity of each rule.
    /// </summary>
    /// <remarks>
    /// Note that the two highest-volume rules are <see cref="RuleSeverity.Info"/>, not errors. The
    /// <c>000000</c> sentinel is a legitimate business category covering ~7% of the initial dump; treating
    /// it as corrupt would discard 8.8M real subscriber records and misreport the device population.
    /// </remarks>
    public static RuleSeverity SeverityOf(ValidationRule rule) => rule switch
    {
        ValidationRule.ImeiUnknownDevice => RuleSeverity.Info,
        ValidationRule.TacNotFound => RuleSeverity.Info,

        ValidationRule.ImeiMalformedLength => RuleSeverity.Warning,

        ValidationRule.MsisdnUnexpectedLength => RuleSeverity.Quarantine,
        ValidationRule.ImsiUnexpectedLength => RuleSeverity.Quarantine,
        ValidationRule.MsisdnNonNumeric => RuleSeverity.Quarantine,
        ValidationRule.ImsiNonNumeric => RuleSeverity.Quarantine,
        ValidationRule.ImeiNonNumeric => RuleSeverity.Quarantine,
        ValidationRule.FieldCountMismatch => RuleSeverity.Quarantine,

        // A label we do not recognise means the feed's semantics have changed. Folding such a file
        // would produce a silently wrong state, so the whole file is refused.
        ValidationRule.LabelUnrecognised => RuleSeverity.Reject,

        _ => RuleSeverity.Quarantine,
    };

    /// <summary>True when a row that triggered this rule is still loaded.</summary>
    public static bool KeepsRow(ValidationRule rule) =>
        SeverityOf(rule) is RuleSeverity.Info or RuleSeverity.Warning;
}
