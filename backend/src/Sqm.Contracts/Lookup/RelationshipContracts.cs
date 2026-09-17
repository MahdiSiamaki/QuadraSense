namespace Sqm.Contracts.Lookup;

/// <summary>Which of the three identifiers a node is.</summary>
public enum RelatedKind
{
    /// <summary>A phone number.</summary>
    Msisdn,

    /// <summary>A SIM identity.</summary>
    Imsi,

    /// <summary>A handset.</summary>
    Imei,
}

/// <summary>One identifier connected to the one being explored.</summary>
/// <param name="Value">The identifier itself.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Bindings">How many bindings join it to the centre.</param>
/// <param name="ActiveBindings">How many of those are still in force.</param>
/// <param name="LastConfirmed">
/// The day a daily file last said anything about it, or null when none ever has - meaning it is
/// present only because the initial dump listed it and nothing has removed it since.
/// </param>
/// <param name="Tac">For a handset, its type allocation code.</param>
/// <param name="Brand">For a handset, the GSMA brand.</param>
/// <param name="MarketingName">For a handset, the product name.</param>
/// <param name="DeviceType">For a handset, the GSMA device type.</param>
public sealed record RelatedNode(
    string Value,
    RelatedKind Kind,
    int Bindings,
    int ActiveBindings,
    DateOnly? LastConfirmed,
    string? Tac = null,
    string? Brand = null,
    string? MarketingName = null,
    string? DeviceType = null);

/// <summary>
/// Two IMEIs shown to be the two radios of one physical handset.
/// </summary>
/// <param name="Imei">The partner handset.</param>
/// <param name="MarketingName">Its model, which will match the centre's.</param>
/// <param name="SharedSubscribers">
/// How many phone numbers have been seen on both. This is the evidence: it is what distinguishes
/// a real pair from two handsets that merely came off the line one after another.
/// </param>
public sealed record PairedHandset(string Imei, string? MarketingName, int SharedSubscribers);

/// <summary>Everything connected to one identifier.</summary>
/// <param name="Centre">The identifier that was explored.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Found">False when the identifier is well-formed but appears nowhere.</param>
/// <param name="Subscribers">Phone numbers reached from it.</param>
/// <param name="Sims">SIMs reached from it.</param>
/// <param name="Handsets">Handsets reached from it.</param>
/// <param name="Paired">Handsets shown to share a physical device with one of the above.</param>
/// <param name="Withheld">
/// Sections the caller is not permitted to see, named rather than silently dropped - an empty
/// list and a forbidden list look identical otherwise, and the difference matters.
/// </param>
/// <param name="Truncated">True when the identifier has more bindings than the server will return.</param>
/// <param name="ElapsedMs">What the query cost.</param>
/// <param name="RowsExamined">Rows the engine read. The number that tells an index from a scan.</param>
public sealed record RelationshipGraph(
    string Centre,
    RelatedKind Kind,
    bool Found,
    IReadOnlyList<RelatedNode> Subscribers,
    IReadOnlyList<RelatedNode> Sims,
    IReadOnlyList<RelatedNode> Handsets,
    IReadOnlyList<PairedHandset> Paired,
    IReadOnlyList<string> Withheld,
    bool Truncated,
    long ElapsedMs,
    long RowsExamined);

/// <summary>
/// Which sections of the graph the caller is permitted to see.
/// </summary>
/// <remarks>
/// A relationship has two ends, and seeing it means seeing both. So permission is decided per
/// identifier kind rather than once for the page: a reader who may look up a number but not a
/// handset gets the SIMs and is told plainly that the handsets were withheld, instead of an empty
/// list that reads as "this number has no devices".
/// </remarks>
/// <param name="Subscribers">May see phone numbers.</param>
/// <param name="Sims">May see SIM identities.</param>
/// <param name="Handsets">May see handsets.</param>
public readonly record struct RelationshipSections(bool Subscribers, bool Sims, bool Handsets);
