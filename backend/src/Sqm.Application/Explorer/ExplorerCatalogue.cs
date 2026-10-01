using System.Collections.Frozen;
using Sqm.Application.Identity;
using Sqm.Contracts.Explorer;

namespace Sqm.Application.Explorer;

/// <summary>The kind of value a field holds, which decides how its values are parsed.</summary>
public enum ExplorerFieldType
{
    /// <summary>A phone number: digits, stored as a number.</summary>
    Msisdn,

    /// <summary>A SIM identity: digits, stored as a number.</summary>
    Imsi,

    /// <summary>A handset identity: digits, stored as text so leading zeros survive.</summary>
    Imei,

    /// <summary>The eight-digit type allocation code: the model, from the IMEI.</summary>
    Tac,

    /// <summary>Free text from the GSMA record.</summary>
    Text,

    /// <summary>A calendar date, yyyy-MM-dd.</summary>
    Date,

    /// <summary>true or false.</summary>
    Boolean,

    /// <summary>One of a fixed set of words.</summary>
    Label,

    /// <summary>A computed count. Measures only.</summary>
    Number,
}

/// <summary>A field the Explorer offers: what it is, what may be done with it, who may see it.</summary>
/// <param name="Name">The catalogue name the API uses.</param>
/// <param name="Label">For a heading.</param>
/// <param name="Type">How its values are parsed.</param>
/// <param name="Operators">What a condition may do with it.</param>
/// <param name="Groupable">Whether it can be grouped by.</param>
/// <param name="Permission">The lookup permission it needs - to filter on it, show it or group by it.</param>
/// <param name="Description">What it means, and what it does not.</param>
/// <param name="Labels">For a Label field, the values it may hold.</param>
public sealed record ExplorerField(
    string Name,
    string Label,
    ExplorerFieldType Type,
    FrozenSet<ExplorerOperator> Operators,
    bool Groupable,
    string? Permission,
    string Description,
    IReadOnlyList<string>? Labels = null);

/// <summary>A dataset: its fields, and the columns shown when none are asked for.</summary>
/// <param name="Dataset">Which one.</param>
/// <param name="Label">For the builder.</param>
/// <param name="Description">What a row is.</param>
/// <param name="Fields">Its fields, by name.</param>
/// <param name="DefaultColumns">Shown when a query names no columns, if the caller may see them.</param>
public sealed record ExplorerDatasetDefinition(
    ExplorerDataset Dataset,
    string Label,
    string Description,
    FrozenDictionary<string, ExplorerField> Fields,
    IReadOnlyList<string> DefaultColumns);

/// <summary>
/// Every field the Explorer can filter, show, group or count by. The allow-list: a request names
/// fields from here or is refused, so no request can reach a column this does not list.
/// </summary>
/// <remarks>
/// <para>
/// Logical only. Which physical column or expression serves a field is the compiler's business
/// (<c>Sqm.Infrastructure.ClickHouse.Explorer</c>), and a test holds the two lists together.
/// </para>
/// <para>
/// The wording of the descriptions is part of the product. "Active" is the one to read twice: the
/// feed adds a binding and later removes it, and active means only that the remove has not come
/// yet. 70.5% of bindings that ended lasted a single day, so it is not a claim that the SIM is in
/// the handset now.
/// </para>
/// </remarks>
public static class ExplorerCatalogue
{
    private static readonly FrozenSet<ExplorerOperator> IdentifierOps = new[]
    {
        ExplorerOperator.Equals, ExplorerOperator.NotEquals, ExplorerOperator.In,
        ExplorerOperator.NotIn, ExplorerOperator.StartsWith,
    }.ToFrozenSet();

    private static readonly FrozenSet<ExplorerOperator> TextOps = new[]
    {
        ExplorerOperator.Equals, ExplorerOperator.NotEquals, ExplorerOperator.In,
        ExplorerOperator.NotIn, ExplorerOperator.Contains, ExplorerOperator.StartsWith,
        ExplorerOperator.IsNull, ExplorerOperator.IsNotNull,
    }.ToFrozenSet();

    private static readonly FrozenSet<ExplorerOperator> DateOps = new[]
    {
        ExplorerOperator.Equals, ExplorerOperator.NotEquals, ExplorerOperator.GreaterThan,
        ExplorerOperator.GreaterOrEqual, ExplorerOperator.LessThan, ExplorerOperator.LessOrEqual,
        ExplorerOperator.Between,
    }.ToFrozenSet();

    private static readonly FrozenSet<ExplorerOperator> LabelOps = new[]
    {
        ExplorerOperator.Equals, ExplorerOperator.NotEquals, ExplorerOperator.In, ExplorerOperator.NotIn,
    }.ToFrozenSet();

    /// <summary>The operators a measure accepts in Having.</summary>
    public static readonly FrozenSet<ExplorerOperator> MeasureOps = new[]
    {
        ExplorerOperator.Equals, ExplorerOperator.NotEquals, ExplorerOperator.GreaterThan,
        ExplorerOperator.GreaterOrEqual, ExplorerOperator.LessThan, ExplorerOperator.LessOrEqual,
        ExplorerOperator.Between,
    }.ToFrozenSet();

    // The identity and device fields both datasets share.
    private static IEnumerable<ExplorerField> Shared() =>
    [
        new("msisdn", "Number", ExplorerFieldType.Msisdn, IdentifierOps, true, Permissions.LookupSubscriber,
            "The phone number (MSISDN). Starts-with reads a range of numbers."),
        new("imsi", "SIM", ExplorerFieldType.Imsi, IdentifierOps, true, Permissions.LookupImsi,
            "The SIM identity (IMSI). Every IMSI here begins 43211, so a prefix needs about ten digits to narrow anything."),
        new("imei", "Handset", ExplorerFieldType.Imei, IdentifierOps, true, Permissions.LookupImei,
            "The handset identity (IMEI, 14 digits). A dual-SIM phone has two, one per radio, and nothing in the feed says which two belong together - an IMEI is not a physical device."),
        new("tac", "TAC", ExplorerFieldType.Tac, IdentifierOps, true, null,
            "The first eight digits of the IMEI: the model allocation."),
        new("brand", "Brand", ExplorerFieldType.Text, TextOps, true, null,
            "GSMA brand, or the manufacturer where the brand is not given. Empty when the TAC is not in the GSMA database."),
        new("model", "Model", ExplorerFieldType.Text, TextOps, true, null,
            "GSMA marketing name. Empty when the TAC is not in the GSMA database."),
        new("manufacturer", "Manufacturer", ExplorerFieldType.Text, TextOps, true, null,
            "GSMA manufacturer, as registered - Samsung has six spellings."),
        new("deviceType", "Device type", ExplorerFieldType.Text, TextOps, true, null,
            "GSMA device type: Smartphone, Modem, IoT Device and so on."),
        new("operatingSystem", "Operating system", ExplorerFieldType.Text, TextOps, true, null,
            "GSMA operating system."),
    ];

    /// <summary>Current state: one row per binding ever seen.</summary>
    public static ExplorerDatasetDefinition Bindings { get; } = new(
        ExplorerDataset.Bindings,
        "Bindings",
        "One row per number + SIM + handset combination ever seen, and whether it is still active.",
        Shared().Concat(
        [
            new("active", "Active", ExplorerFieldType.Boolean,
                new[] { ExplorerOperator.Equals }.ToFrozenSet(), true, null,
                "Not yet removed by the feed. Not a claim that the SIM is in the handset now: 70.5% of bindings that ended lasted one day."),
            new("lastChangeDate", "Last change", ExplorerFieldType.Date,
                DateOps.Union([ExplorerOperator.IsNull, ExplorerOperator.IsNotNull]).ToFrozenSet(), true, null,
                "The day the feed last added or removed this binding. Empty when only the initial dump (2025-12-27 to 2026-01-25) listed it and no daily file has mentioned it since."),
        ]).ToFrozenDictionary(f => f.Name, StringComparer.Ordinal),
        ["msisdn", "imsi", "imei", "brand", "model", "active", "lastChangeDate"]);

    /// <summary>The dated events.</summary>
    public static ExplorerDatasetDefinition Events { get; } = new(
        ExplorerDataset.Events,
        "Events",
        "One row per add or remove in a daily file, dated. A date range is required.",
        Shared().Concat(
        [
            new("date", "Date", ExplorerFieldType.Date, DateOps, true, null,
                "The business date of the daily file."),
            new("change", "Change", ExplorerFieldType.Label, LabelOps, true, null,
                "add or remove.", ["add", "remove"]),
        ]).ToFrozenDictionary(f => f.Name, StringComparer.Ordinal),
        ["date", "change", "msisdn", "imsi", "imei", "model"]);

    /// <summary>A dataset's definition.</summary>
    public static ExplorerDatasetDefinition For(ExplorerDataset dataset) => dataset switch
    {
        ExplorerDataset.Bindings => Bindings,
        ExplorerDataset.Events => Events,
        _ => throw new ArgumentOutOfRangeException(nameof(dataset), dataset, "Unknown dataset."),
    };

    /// <summary>Every dataset.</summary>
    public static IReadOnlyList<ExplorerDatasetDefinition> All { get; } = [Bindings, Events];
}

/// <summary>What one Explorer query may cost and return. Decided by the product owner, 2026-09-30.</summary>
public sealed class ExplorerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Explorer";

    /// <summary>Rows per page, at most.</summary>
    public int MaxPageSize { get; set; } = 500;

    /// <summary>Rows paging can reach; past this, the query must be narrowed (or exported, later).</summary>
    public int MaxReachableRows { get; set; } = 10_000;

    /// <summary>
    /// Rows a query may read. Checked against the plan's estimate before running, and enforced by
    /// the server while it runs. One number, SIM or handset reads 0.1-0.5 million; one pass over
    /// current state reads ~700 million.
    /// </summary>
    public long BudgetRows { get; set; } = 100_000_000;

    /// <summary>Seconds a query may run on the server.</summary>
    public int BudgetSeconds { get; set; } = 30;

    /// <summary>Up to this many rows read is Light.</summary>
    public long LightRows { get; set; } = 5_000_000;

    /// <summary>Up to this many rows read is Moderate; past it and within the budget, Heavy.</summary>
    public long ModerateRows { get; set; } = 30_000_000;

    /// <summary>Conditions in one query, at most.</summary>
    public int MaxConditions { get; set; } = 40;

    /// <summary>How deeply groups may nest.</summary>
    public int MaxDepth { get; set; } = 6;

    /// <summary>Values in one In or NotIn list.</summary>
    public int MaxInValues { get; set; } = 1_000;

    /// <summary>
    /// A brand or model filter is turned into the TACs it matches, so the handset-ordered copy can be
    /// read by range. Past this many TACs the ranges stop paying - 300 measured at 26.7M rows - and
    /// the filter is applied without them.
    /// </summary>
    public int MaxResolvedTacs { get; set; } = 300;

    /// <summary>Explorer queries running at once, across everybody. Past it, a query is refused as busy.</summary>
    public int MaxConcurrentQueries { get; set; } = 4;

    /// <summary>Threads one query may use. Two: a grouped count is faster, and four at once still leave the imports room.</summary>
    public int QueryThreads { get; set; } = 2;

    /// <summary>
    /// Memory one query may use on the server, of the 5.2 GiB it has, spilling to disk past a third
    /// of it. Four queries at this cap are 6 GB in theory; in practice a key read uses a few MB.
    /// </summary>
    public long QueryMemoryBytes { get; set; } = 1_500_000_000;
}
