using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sqm.Domain.Risk;

namespace Sqm.Application.Risk;

/// <summary>
/// The risk rule set (configuration section <c>Risk</c>): thresholds, storage floors and how the
/// snapshot is computed.
/// </summary>
/// <remarks>
/// <para>
/// <b>No threshold has a default.</b> Every one is cut from the measured distribution on a clean
/// reference window, at the value that yields the number of entities analysts can review per list
/// (the product owner's decision of 2026-10-02; the measurements and the cuts are in ADR-014). A
/// rule without a threshold is not judged at all, and until any rule has one the risk endpoints say
/// so rather than show verdicts built on invented numbers.
/// </para>
/// <para>
/// <b>The version</b> is a short hash of every value here. It is quoted in every reason and every
/// audit entry, so a level seen later can be explained by the rules in force when it was given.
/// </para>
/// </remarks>
public sealed class RiskOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Risk";

    /// <summary>Thresholds, per rule: a value is out of line when it is MORE than this.</summary>
    public RiskThresholdOptions Thresholds { get; set; } = new();

    /// <summary>
    /// Above this share of an entity's adds set aside as feed defects, it is not assessable and is
    /// listed under data quality only. Unset until measured (M5).
    /// </summary>
    public double? MaxDefectShare { get; set; }

    /// <summary>The smallest values the snapshot keeps a row for.</summary>
    public RiskFloorOptions Floors { get; set; } = new();

    /// <summary>How the snapshot is computed.</summary>
    public RiskComputeOptions Compute { get; set; } = new();

    /// <summary>The thresholds as the domain reads them; rules without one are left out.</summary>
    public IReadOnlyDictionary<RiskRule, RiskThreshold> ThresholdsByRule()
    {
        var t = Thresholds;
        var map = new Dictionary<RiskRule, RiskThreshold>();

        void Add(RiskRule rule, long? value, long? tacs = null)
        {
            if (value is { } v)
            {
                map[rule] = new RiskThreshold(v, tacs);
            }
        }

        Add(RiskRule.SharedImeiSims30, t.SharedImeiSims30);
        Add(RiskRule.SharedImeiNumbers30, t.SharedImeiNumbers30);
        Add(RiskRule.SharedImeiSimsEver, t.SharedImeiSimsEver);
        Add(RiskRule.SharedImeiSimsNotRemoved, t.SharedImeiSimsNotRemoved);
        Add(RiskRule.HighDeviceCount30, t.HighDeviceCount30);
        Add(RiskRule.RapidDeviceChange7, t.RapidDeviceChange7);
        if (t.Randomisation20Tacs is not null)
        {
            Add(RiskRule.Randomisation20, t.Randomisation20Imeis, t.Randomisation20Tacs);
        }

        Add(RiskRule.RepeatedSimChange7, t.RepeatedSimChange7);
        return map;
    }

    /// <summary>The settings the domain judges with, carrying this rule set's version.</summary>
    public RiskSettings Settings() => new(ThresholdsByRule(), MaxDefectShare ?? 1.0, Version());

    /// <summary>An 8-character hash of every value that affects a verdict or a stored row.</summary>
    public string Version()
    {
        var t = Thresholds;
        var f = Floors;
        var text = string.Join('|',
            t.SharedImeiSims30, t.SharedImeiNumbers30, t.SharedImeiSimsEver, t.SharedImeiSimsNotRemoved,
            t.HighDeviceCount30, t.RapidDeviceChange7, t.Randomisation20Imeis, t.Randomisation20Tacs,
            t.RepeatedSimChange7,
            MaxDefectShare?.ToString("R", CultureInfo.InvariantCulture),
            f.SimImeis30, f.ImeiSims30, f.ImeiSimsEver, f.ImeiSimsNotRemoved,
            RiskComputeOptions.DefinitionVersion);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];
    }

    /// <summary>
    /// What is wrong with this rule set, or empty. A threshold below its storage floor would be
    /// answered from rows that were never stored, so it is refused.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        var t = Thresholds;
        var f = Floors;

        void Floor(string name, long? threshold, int floor)
        {
            // A row is kept when its value reaches the floor; a threshold T lists values above T,
            // so every such value is stored when T + 1 >= floor.
            if (threshold is { } v && v + 1 < floor)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Risk:Thresholds:{name} is {v}, below the storage floor {floor}: values between them are not stored."));
            }
        }

        Floor(nameof(t.SharedImeiSims30), t.SharedImeiSims30, f.ImeiSims30);
        Floor(nameof(t.SharedImeiNumbers30), t.SharedImeiNumbers30, f.ImeiSims30);
        Floor(nameof(t.SharedImeiSimsEver), t.SharedImeiSimsEver, f.ImeiSimsEver);
        Floor(nameof(t.SharedImeiSimsNotRemoved), t.SharedImeiSimsNotRemoved, f.ImeiSimsNotRemoved);
        Floor(nameof(t.HighDeviceCount30), t.HighDeviceCount30, f.SimImeis30);
        Floor(nameof(t.RapidDeviceChange7), t.RapidDeviceChange7, f.SimImeis30);
        Floor(nameof(t.Randomisation20Imeis), t.Randomisation20Imeis, f.SimImeis30);

        if ((t.Randomisation20Imeis is null) != (t.Randomisation20Tacs is null))
        {
            problems.Add("Risk:Thresholds:Randomisation20Imeis and Randomisation20Tacs are set together or not at all.");
        }

        if (MaxDefectShare is < 0 or > 1)
        {
            problems.Add("Risk:MaxDefectShare is a share, between 0 and 1.");
        }

        if (Compute.Chunks is < 1 or > 256)
        {
            problems.Add("Risk:Compute:Chunks is between 1 and 256.");
        }

        return problems;
    }
}

/// <summary>Per-rule thresholds. All unset until calibrated.</summary>
public sealed class RiskThresholdOptions
{
    /// <summary>F1: distinct SIMs added to an IMEI in 30 days.</summary>
    public long? SharedImeiSims30 { get; set; }

    /// <summary>F1: distinct numbers added to an IMEI in 30 days.</summary>
    public long? SharedImeiNumbers30 { get; set; }

    /// <summary>F1: distinct SIMs ever on an IMEI. Never above Anomaly.</summary>
    public long? SharedImeiSimsEver { get; set; }

    /// <summary>F1: SIMs on an IMEI not yet removed by the feed, dated bindings. Never above Anomaly.</summary>
    public long? SharedImeiSimsNotRemoved { get; set; }

    /// <summary>F2: distinct IMEIs a SIM was added to in 30 days.</summary>
    public long? HighDeviceCount30 { get; set; }

    /// <summary>F2: distinct IMEIs a SIM was added to in 7 days.</summary>
    public long? RapidDeviceChange7 { get; set; }

    /// <summary>F2: the randomisation shape's IMEI count over 20 days.</summary>
    public long? Randomisation20Imeis { get; set; }

    /// <summary>F2: the randomisation shape's TAC count over 20 days; both must cross.</summary>
    public long? Randomisation20Tacs { get; set; }

    /// <summary>F3: SIM changes on a number in 7 days.</summary>
    public long? RepeatedSimChange7 { get; set; }
}

/// <summary>
/// The smallest values the snapshot keeps a row for. Every threshold must be at least its floor
/// minus one; see <see cref="RiskOptions.Problems"/>.
/// </summary>
public sealed class RiskFloorOptions
{
    /// <summary>A SIM's row is kept when its 30-day IMEI count, clean or raw, reaches this.</summary>
    public int SimImeis30 { get; set; } = 6;

    /// <summary>An IMEI's window row is kept when its 30-day SIM count, clean or raw, reaches this.</summary>
    public int ImeiSims30 { get; set; } = 6;

    /// <summary>An IMEI's lifetime row is kept when its SIMs ever reach this...</summary>
    public int ImeiSimsEver { get; set; } = 21;

    /// <summary>...or its dated SIMs not removed reach this.</summary>
    public int ImeiSimsNotRemoved { get; set; } = 6;
}

/// <summary>How the snapshot is computed.</summary>
public sealed class RiskComputeOptions
{
    /// <summary>
    /// Bumped whenever what a stored measure means changes, so every run built under the old
    /// meaning becomes stale.
    /// </summary>
    public const int DefinitionVersion = 1;

    /// <summary>Key-range chunks per table. One chunk is built per idle moment of the worker.</summary>
    public int Chunks { get; set; } = 24;

    /// <summary>The server-side time limit of one chunk's statement, below the ingestion client's 30 minutes.</summary>
    public int MaxStatementSeconds { get; set; } = 1500;
}
