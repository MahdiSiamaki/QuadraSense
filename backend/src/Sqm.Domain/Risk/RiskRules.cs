using System.Globalization;
using Sqm.Domain.Quality;

namespace Sqm.Domain.Risk;

/// <summary>How much an entity's evidence says, from a fact to a person's verdict.</summary>
/// <remarks>
/// A level states the quality of the evidence, not how bad something is. The system assigns the first
/// four; <see cref="ConfirmedFraud"/> exists so that the vocabulary is complete, and only a person may
/// ever record it. Nothing in Phase 4 records it, and <see cref="RiskRules"/> can never return it.
/// </remarks>
public enum RiskLevel
{
    /// <summary>A counted fact, with no judgement.</summary>
    Observation = 0,

    /// <summary>
    /// Out of line, but not evidence about a subscriber: an all-time count, a crossing in a window that
    /// overlaps days the feed-quality monitor flagged, or a day of the whole population.
    /// </summary>
    Anomaly = 1,

    /// <summary>A threshold crossed in a clean window, on clean evidence.</summary>
    RiskSignal = 2,

    /// <summary>Risk signals of two different families on linked entities.</summary>
    SuspiciousPattern = 3,

    /// <summary>A person's verdict. Never assigned by the system.</summary>
    ConfirmedFraud = 4,
}

/// <summary>Which unit a rule counts on. Rules of one family measure one thing and count once.</summary>
public enum RiskFamily
{
    /// <summary>F1: an IMEI - one radio identity, not a phone.</summary>
    Imei,

    /// <summary>F2: a SIM, known by its IMSI.</summary>
    Sim,

    /// <summary>F3: a phone number (MSISDN).</summary>
    Number,
}

/// <summary>A rule, with its unit and the window it reads.</summary>
public enum RiskRule
{
    /// <summary>Distinct SIMs added to an IMEI in 30 days.</summary>
    SharedImeiSims30,

    /// <summary>Distinct numbers added to an IMEI in 30 days.</summary>
    SharedImeiNumbers30,

    /// <summary>Distinct SIMs ever recorded on an IMEI. The spec's "Historical SIM". Anomaly at most.</summary>
    SharedImeiSimsEver,

    /// <summary>SIMs on an IMEI not yet removed by the feed, dated bindings only. The spec's "Active SIM". Anomaly at most.</summary>
    SharedImeiSimsNotRemoved,

    /// <summary>Distinct IMEIs a SIM was added to in 30 days.</summary>
    HighDeviceCount30,

    /// <summary>Distinct IMEIs a SIM was added to in 7 days.</summary>
    RapidDeviceChange7,

    /// <summary>Distinct IMEIs over distinct TACs a SIM was added to in 20 days; both must cross.</summary>
    Randomisation20,

    /// <summary>SIM changes on a number in 7 days, by the dashboard's same-day definition.</summary>
    RepeatedSimChange7,
}

/// <summary>What a rule counts, over which window, and which feed defects can inflate it.</summary>
/// <param name="Rule">The rule.</param>
/// <param name="Family">The unit it counts on.</param>
/// <param name="WindowDays">The window, or null for an all-time count.</param>
/// <param name="Unit">The counted unit, named the way the UI names it.</param>
/// <param name="What">The rest of the sentence: what was counted, relative to the entity.</param>
/// <param name="RelevantChecks">
/// The feed-quality checks whose flagged days cap this rule at <see cref="RiskLevel.Anomaly"/> when
/// its window overlaps them.
/// </param>
public sealed record RiskRuleSpec(
    RiskRule Rule,
    RiskFamily Family,
    int? WindowDays,
    string Unit,
    string What,
    IReadOnlyList<FeedQualityCheck> RelevantChecks)
{
    /// <summary>True when the rule reads a window and may therefore reach <see cref="RiskLevel.RiskSignal"/>.</summary>
    public bool Windowed => WindowDays is not null;
}

/// <summary>The window a measure was taken over. Calendar days; the feed has no time of day.</summary>
/// <param name="From">First day, inclusive.</param>
/// <param name="To">Last day, inclusive: the data-through date.</param>
/// <param name="DaysWithData">How many of those days had a file.</param>
public sealed record RiskWindow(DateOnly From, DateOnly To, int DaysWithData)
{
    /// <summary>Calendar days in the window.</summary>
    public int Days => To.DayNumber - From.DayNumber + 1;

    /// <summary>True when the date falls inside the window.</summary>
    public bool Contains(DateOnly date) => date >= From && date <= To;
}

/// <summary>One measured value of one rule for one entity.</summary>
/// <param name="Rule">The rule it feeds.</param>
/// <param name="Value">The clean count: feed defects already set aside.</param>
/// <param name="Window">The window, for a windowed rule; null for an all-time count.</param>
/// <param name="Tacs">For <see cref="RiskRule.Randomisation20"/>: the distinct TACs among the IMEIs.</param>
public sealed record RiskMeasure(RiskRule Rule, long Value, RiskWindow? Window, long? Tacs = null);

/// <summary>Everything measured for one entity, and how much of it the feed-defect screens set aside.</summary>
/// <param name="Family">The entity's unit.</param>
/// <param name="Measures">Its measures.</param>
/// <param name="AddsInWindow">Every add in the longest window, before the screens.</param>
/// <param name="AddsSetAside">Of those, the adds the screens set aside as feed defects.</param>
public sealed record RiskEvidence(
    RiskFamily Family,
    IReadOnlyList<RiskMeasure> Measures,
    long AddsInWindow = 0,
    long AddsSetAside = 0);

/// <summary>A rule's threshold: a value is out of line when it is MORE than this.</summary>
/// <param name="Value">The count threshold.</param>
/// <param name="Tacs">For <see cref="RiskRule.Randomisation20"/>, the TAC threshold that must also be crossed.</param>
public sealed record RiskThreshold(long Value, long? Tacs = null);

/// <summary>The rule set in force: thresholds, the defect-share limit, and the version that names them.</summary>
/// <param name="Thresholds">Per rule. A rule with no threshold is not judged: there is no measured basis for one.</param>
/// <param name="MaxDefectShare">
/// Above this share of adds set aside as feed defects, an entity is not assessable and belongs to the
/// data-quality view only.
/// </param>
/// <param name="Version">A short hash of the whole rule set, quoted in every reason.</param>
public sealed record RiskSettings(
    IReadOnlyDictionary<RiskRule, RiskThreshold> Thresholds,
    double MaxDefectShare,
    string Version);

/// <summary>A day the feed-quality monitor flagged, and for what.</summary>
/// <param name="Date">The day.</param>
/// <param name="Check">The check that flagged it.</param>
public sealed record FlaggedDay(DateOnly Date, FeedQualityCheck Check);

/// <summary>One rule's verdict on one entity, with the sentence that explains it.</summary>
/// <param name="Rule">The rule.</param>
/// <param name="Level">What this rule alone says.</param>
/// <param name="Value">The measured value.</param>
/// <param name="Threshold">The threshold it was compared with; null when the rule has none.</param>
/// <param name="Capped">True when the level was held at Anomaly by flagged feed days.</param>
/// <param name="Text">The explanation, written by the server.</param>
public sealed record RiskReason(RiskRule Rule, RiskLevel Level, long Value, long? Threshold, bool Capped, string Text);

/// <summary>An entity's level, and every reason behind it.</summary>
/// <param name="Level">The highest level any rule gave.</param>
/// <param name="Assessable">
/// False when most of the entity's evidence was feed defects: it is listed under data quality only.
/// </param>
/// <param name="Reasons">One per measured rule, highest level first.</param>
public sealed record RiskAssessment(RiskLevel Level, bool Assessable, IReadOnlyList<RiskReason> Reasons);

/// <summary>
/// Turns measures into levels and reasons. The only place a level is decided.
/// </summary>
/// <remarks>
/// <para>
/// Pure, like <see cref="FeedQualityRules"/>: measures, flagged days and settings in; a level and
/// server-written reasons out. The worker stores measures and never judges; the API judges when it
/// reads, so a threshold can change without recomputing anything.
/// </para>
/// <para>
/// <b>Why lifetime counts stop at Anomaly.</b> "Ever" and "not removed" counts accumulate nine months of
/// ordinary churn and stale bindings: of the SIMs not removed from exactly two IMEIs, 83.7% sit on two
/// different models (cap_imsi_two, 2026-09-30). They cannot be cleaned of the feed defects either -
/// that would need every event since the dump. Product owner's decision, 2026-10-02.
/// </para>
/// <para>
/// <b>Why flagged days cap a window.</b> Row-level screens are not proven complete: rows matching no
/// known defect signature rose by about half after 2026-09-16. Until a measurement shows the cleaned
/// recent distribution matches the clean reference, a window that overlaps days flagged for a check
/// relevant to the rule shows an Anomaly, never a Risk Signal. Product owner's decision, 2026-10-02.
/// </para>
/// </remarks>
public static class RiskRules
{
    private static readonly FeedQualityCheck[] ImeiChecks = [FeedQualityCheck.ShiftedImei, FeedQualityCheck.MultiNumberSim];
    private static readonly FeedQualityCheck[] SimChecks = [FeedQualityCheck.ShiftedImei, FeedQualityCheck.MultiNumberSim, FeedQualityCheck.MalformedImei];
    private static readonly FeedQualityCheck[] NumberChecks = [FeedQualityCheck.MultiNumberSim];

    /// <summary>Every rule, in the order reasons are listed.</summary>
    public static IReadOnlyList<RiskRuleSpec> Catalogue { get; } =
    [
        new(RiskRule.SharedImeiSims30, RiskFamily.Imei, 30, "SIMs (IMSIs)", "added to this IMEI", ImeiChecks),
        new(RiskRule.SharedImeiNumbers30, RiskFamily.Imei, 30, "numbers", "added to this IMEI", ImeiChecks),
        new(RiskRule.SharedImeiSimsEver, RiskFamily.Imei, null, "SIMs (IMSIs)", "recorded on this IMEI since the initial dump window", []),
        new(RiskRule.SharedImeiSimsNotRemoved, RiskFamily.Imei, null, "SIMs (IMSIs)", "not yet removed from this IMEI by the feed (dated bindings only; not presence)", []),
        new(RiskRule.HighDeviceCount30, RiskFamily.Sim, 30, "IMEIs", "this SIM (IMSI) was added to", SimChecks),
        new(RiskRule.RapidDeviceChange7, RiskFamily.Sim, 7, "IMEIs", "this SIM (IMSI) was added to", SimChecks),
        new(RiskRule.Randomisation20, RiskFamily.Sim, 20, "IMEIs", "this SIM (IMSI) was added to", SimChecks),
        new(RiskRule.RepeatedSimChange7, RiskFamily.Number, 7, "SIM changes", "on this number", NumberChecks),
    ];

    /// <summary>A rule's specification.</summary>
    public static RiskRuleSpec Spec(RiskRule rule) =>
        Catalogue.FirstOrDefault(s => s.Rule == rule)
        ?? throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule.");

    /// <summary>Judges one entity.</summary>
    /// <param name="evidence">What was measured.</param>
    /// <param name="settings">The rule set in force.</param>
    /// <param name="flaggedDays">Days the feed-quality monitor flagged, with the check that flagged each.</param>
    public static RiskAssessment Assess(RiskEvidence evidence, RiskSettings settings, IEnumerable<FlaggedDay> flaggedDays)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(flaggedDays);

        var flagged = flaggedDays.ToList();
        var assessable = evidence.AddsInWindow <= 0
            || (double)evidence.AddsSetAside / evidence.AddsInWindow <= settings.MaxDefectShare;

        var reasons = new List<RiskReason>(evidence.Measures.Count);

        foreach (var measure in evidence.Measures)
        {
            var spec = Spec(measure.Rule);
            if (spec.Family != evidence.Family)
            {
                throw new ArgumentException(
                    $"{measure.Rule} counts on {spec.Family}, not on {evidence.Family}.", nameof(evidence));
            }

            reasons.Add(Judge(spec, measure, settings, flagged, assessable));
        }

        reasons.Sort((a, b) => b.Level != a.Level
            ? b.Level.CompareTo(a.Level)
            : Order(a.Rule).CompareTo(Order(b.Rule)));

        var level = reasons.Count == 0 ? RiskLevel.Observation : reasons[0].Level;
        return new RiskAssessment(level, assessable, reasons);
    }

    /// <summary>
    /// The level of a group of linked entities - a SIM with its number and its IMEIs: a Suspicious
    /// Pattern when Risk Signals of at least two different families hold among them.
    /// </summary>
    /// <param name="linked">Each linked entity's family and assessed level.</param>
    public static RiskLevel Pattern(IEnumerable<(RiskFamily Family, RiskLevel Level)> linked)
    {
        ArgumentNullException.ThrowIfNull(linked);

        var items = linked.ToList();
        var signalling = items.Where(i => i.Level >= RiskLevel.RiskSignal).Select(i => i.Family).Distinct().Count();

        if (signalling >= 2)
        {
            return RiskLevel.SuspiciousPattern;
        }

        // Never above Risk Signal from here: a Suspicious Pattern needs two families, and Confirmed
        // Fraud is a person's.
        return items.Count == 0 ? RiskLevel.Observation : (RiskLevel)Math.Min((int)items.Max(i => i.Level), (int)RiskLevel.RiskSignal);
    }

    private static RiskReason Judge(
        RiskRuleSpec spec, RiskMeasure measure, RiskSettings settings, List<FlaggedDay> flagged, bool assessable)
    {
        var window = measure.Window;
        if (spec.Windowed && window is null)
        {
            throw new ArgumentException($"{spec.Rule} is a windowed rule and needs its window.", nameof(measure));
        }

        var counted = Counted(spec, measure, window);

        if (!settings.Thresholds.TryGetValue(spec.Rule, out var threshold))
        {
            return new RiskReason(spec.Rule, RiskLevel.Observation, measure.Value, null, false,
                $"{counted}. Not judged: this rule has no calibrated threshold (rules {settings.Version}).");
        }

        var crossed = measure.Value > threshold.Value
            && (threshold.Tacs is null || (measure.Tacs ?? 0) > threshold.Tacs.Value);
        var limit = Limit(spec, threshold);

        if (!crossed)
        {
            return new RiskReason(spec.Rule, RiskLevel.Observation, measure.Value, threshold.Value, false,
                $"{counted} ({limit}; rules {settings.Version}).");
        }

        if (!spec.Windowed)
        {
            return new RiskReason(spec.Rule, RiskLevel.Anomaly, measure.Value, threshold.Value, false,
                $"{counted} ({limit}; rules {settings.Version}). An all-time count accumulates ordinary churn and stale "
                + "bindings, so this is an anomaly, not a risk signal.");
        }

        if (!assessable)
        {
            return new RiskReason(spec.Rule, RiskLevel.Anomaly, measure.Value, threshold.Value, true,
                $"{counted} ({limit}; rules {settings.Version}). Most of this entity's evidence in the window was set aside "
                + "as feed defects, so it is not assessed as a risk.");
        }

        var overlapping = flagged
            .Where(f => window!.Contains(f.Date) && spec.RelevantChecks.Contains(f.Check))
            .ToList();

        if (overlapping.Count > 0)
        {
            var days = overlapping.Select(f => f.Date).Distinct().Order().ToList();
            // In the order the feed-quality findings are reported, so the two pages read alike.
            var checks = string.Join(", ", FeedQualityRules.Checks
                .Where(c => overlapping.Any(f => f.Check == c))
                .Select(Name));
            return new RiskReason(spec.Rule, RiskLevel.Anomaly, measure.Value, threshold.Value, true,
                Invariant($"{counted} ({limit}; rules {settings.Version}). Shown as an anomaly: {days.Count} of the window's days ")
                + Invariant($"are flagged by the feed-quality monitor ({checks}; {days[0]:yyyy-MM-dd} to {days[^1]:yyyy-MM-dd}), ")
                + "so the count may carry feed defects.");
        }

        return new RiskReason(spec.Rule, RiskLevel.RiskSignal, measure.Value, threshold.Value, false,
            $"Potential risk signal: {Lower(counted)} ({limit}; rules {settings.Version}).");
    }

    private static string Counted(RiskRuleSpec spec, RiskMeasure measure, RiskWindow? window)
    {
        var value = measure.Value.ToString("N0", CultureInfo.InvariantCulture);
        var tacs = measure.Rule == RiskRule.Randomisation20 && measure.Tacs is { } t
            ? Invariant($" across {t:N0} TACs")
            : string.Empty;

        var subject = spec.Family switch
        {
            RiskFamily.Sim => Invariant($"This SIM (IMSI) was added to {value} different IMEIs{tacs}"),
            _ => Invariant($"{value} {spec.Unit} {spec.What}"),
        };

        return window is null
            ? subject
            : subject + Invariant($" in the {window.Days} days to {window.To:yyyy-MM-dd} ({window.DaysWithData} of {window.Days} days have data)");
    }

    private static string Limit(RiskRuleSpec spec, RiskThreshold threshold) =>
        threshold.Tacs is { } tacs
            ? Invariant($"thresholds: more than {threshold.Value:N0} {spec.Unit} and more than {tacs:N0} TACs")
            : Invariant($"threshold: more than {threshold.Value:N0}");

    private static string Name(FeedQualityCheck check) => check switch
    {
        FeedQualityCheck.ShiftedImei => "shifted IMEIs",
        FeedQualityCheck.MultiNumberSim => "SIMs with several numbers",
        FeedQualityCheck.MalformedImei => "malformed IMEIs",
        FeedQualityCheck.UnknownDevice => "unknown devices",
        _ => check.ToString(),
    };

    private static int Order(RiskRule rule) => (int)rule;

    private static string Lower(string sentence) =>
        sentence.Length > 0 && char.IsUpper(sentence[0]) && !sentence.StartsWith("SIM", StringComparison.Ordinal)
            ? char.ToLowerInvariant(sentence[0]) + sentence[1..]
            : sentence;

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
