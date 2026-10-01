namespace Sqm.Contracts.Explorer;

/// <summary>What an Explorer query reads.</summary>
public enum ExplorerDataset
{
    /// <summary>
    /// Current state: every (number, SIM, handset) binding ever seen, and whether it is still
    /// active - which means "not yet removed by the feed", not "in the handset right now".
    /// </summary>
    Bindings,

    /// <summary>The dated add and remove events. A date range is required.</summary>
    Events,
}

/// <summary>How a condition compares a field with its values.</summary>
public enum ExplorerOperator
{
    /// <summary>Equal to the one value.</summary>
    Equals,

    /// <summary>Not equal to the one value. For a text field, an unknown value counts as not equal.</summary>
    NotEquals,

    /// <summary>Greater than the one value.</summary>
    GreaterThan,

    /// <summary>Greater than or equal to the one value.</summary>
    GreaterOrEqual,

    /// <summary>Less than the one value.</summary>
    LessThan,

    /// <summary>Less than or equal to the one value.</summary>
    LessOrEqual,

    /// <summary>Equal to any of the values.</summary>
    In,

    /// <summary>Equal to none of the values. For a text field, an unknown value counts as none of them.</summary>
    NotIn,

    /// <summary>Text containing the value, ignoring case.</summary>
    Contains,

    /// <summary>Beginning with the value: digits for an identifier, text for a name.</summary>
    StartsWith,

    /// <summary>Between the two values, both included.</summary>
    Between,

    /// <summary>Has no value - for a model, the handset's TAC is not in the GSMA database.</summary>
    IsNull,

    /// <summary>Has a value.</summary>
    IsNotNull,
}

/// <summary>How a group combines its children.</summary>
public enum ExplorerLogic
{
    /// <summary>All must hold.</summary>
    And,

    /// <summary>At least one must hold.</summary>
    Or,
}

/// <summary>
/// One node of a condition tree: either a group of children, or a single condition.
/// </summary>
/// <param name="Logic">For a group: how the children combine.</param>
/// <param name="Children">For a group: its conditions and sub-groups.</param>
/// <param name="Field">For a condition: the field, by catalogue name.</param>
/// <param name="Operator">For a condition: the comparison.</param>
/// <param name="Values">For a condition: the values, as text. One for most operators, two for
/// Between, one or more for In and NotIn, none for IsNull and IsNotNull.</param>
/// <param name="Not">Negates the node, group or condition.</param>
public sealed record ExplorerFilter(
    ExplorerLogic? Logic = null,
    IReadOnlyList<ExplorerFilter>? Children = null,
    string? Field = null,
    ExplorerOperator? Operator = null,
    IReadOnlyList<string>? Values = null,
    bool Not = false);

/// <summary>What a measure computes over each group.</summary>
public enum ExplorerAggregate
{
    /// <summary>How many rows: bindings, or events.</summary>
    Count,

    /// <summary>How many different values of a field, counted exactly.</summary>
    CountDistinct,

    /// <summary>The smallest value of a date field.</summary>
    Min,

    /// <summary>The largest value of a date field.</summary>
    Max,
}

/// <summary>A value computed for each group.</summary>
/// <param name="Name">How the result column and the Having conditions refer to it.</param>
/// <param name="Aggregate">What is computed.</param>
/// <param name="Field">The field, for CountDistinct, Min and Max.</param>
/// <param name="ActiveOnly">Bindings only: count only bindings still active.</param>
public sealed record ExplorerMeasure(string Name, ExplorerAggregate Aggregate, string? Field = null, bool ActiveOnly = false);

/// <summary>An ordering.</summary>
/// <param name="Field">A result column: a field, or a measure's name.</param>
/// <param name="Descending">Largest first.</param>
public sealed record ExplorerSort(string Field, bool Descending = false);

/// <summary>A query, as the Explorer's builder composes it. Never SQL.</summary>
/// <param name="Dataset">What is read.</param>
/// <param name="Where">Which rows.</param>
/// <param name="Columns">Without grouping: the fields to return. Empty for the dataset's defaults.</param>
/// <param name="GroupBy">The fields to group by.</param>
/// <param name="Measures">With grouping: what to compute for each group.</param>
/// <param name="Having">With grouping: which groups, by their measures.</param>
/// <param name="Sort">Ordering, first to last.</param>
/// <param name="Page">1-based page.</param>
/// <param name="PageSize">Rows per page.</param>
public sealed record ExplorerQueryRequest(
    ExplorerDataset Dataset,
    ExplorerFilter? Where = null,
    IReadOnlyList<string>? Columns = null,
    IReadOnlyList<string>? GroupBy = null,
    IReadOnlyList<ExplorerMeasure>? Measures = null,
    ExplorerFilter? Having = null,
    IReadOnlyList<ExplorerSort>? Sort = null,
    int Page = 1,
    int PageSize = 50);

/// <summary>What the server's plan says a query will cost, before it runs.</summary>
/// <param name="Source">What is read, in words.</param>
/// <param name="Access">KeyRead, RangeRead, IndexRead or Scan.</param>
/// <param name="EstimatedRows">Rows the plan reads, from ClickHouse's own estimate.</param>
/// <param name="Verdict">Light, Moderate, Heavy, or Refused when over the budget.</param>
/// <param name="BudgetRows">The most rows a query may read.</param>
/// <param name="Notes">What decided the plan, and how to narrow a refused one.</param>
public sealed record ExplorerPlanInfo(
    string Source, string Access, long EstimatedRows, string Verdict, long BudgetRows, IReadOnlyList<string> Notes);

/// <summary>One result column.</summary>
/// <param name="Name">A field's catalogue name, or a measure's name.</param>
/// <param name="Label">For a heading.</param>
/// <param name="Type">Msisdn, Imsi, Imei, Tac, Text, Date, Boolean, Label or Number.</param>
/// <param name="Masked">True when the values are redacted because the caller lacks identifier.reveal.</param>
public sealed record ExplorerColumnInfo(string Name, string Label, string Type, bool Masked);

/// <summary>A page of results.</summary>
/// <param name="Columns">The columns, in order.</param>
/// <param name="Rows">Each row's values in column order: identifiers and text as strings, dates as yyyy-MM-dd, counts as numbers.</param>
/// <param name="Total">Rows (or groups) matching, across all pages.</param>
/// <param name="Reachable">How many of them paging can reach; the rest need a narrower query.</param>
/// <param name="Page">This page.</param>
/// <param name="PageSize">Rows per page.</param>
/// <param name="Plan">What it was estimated to cost.</param>
/// <param name="ElapsedMs">What it took on the server.</param>
/// <param name="RowsRead">What it actually read.</param>
public sealed record ExplorerResultResponse(
    IReadOnlyList<ExplorerColumnInfo> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    long Total,
    long Reachable,
    int Page,
    int PageSize,
    ExplorerPlanInfo Plan,
    long ElapsedMs,
    long RowsRead);

/// <summary>A field the builder can offer.</summary>
/// <param name="Name">Catalogue name.</param>
/// <param name="Label">For the builder.</param>
/// <param name="Type">As in <see cref="ExplorerColumnInfo.Type"/>.</param>
/// <param name="Operators">The operators it accepts.</param>
/// <param name="Groupable">Whether it can be grouped by.</param>
/// <param name="Permission">The lookup permission needed to filter on, show or group by it; null when none.</param>
/// <param name="Description">What it means, including what it does not mean.</param>
public sealed record ExplorerFieldInfo(
    string Name, string Label, string Type, IReadOnlyList<string> Operators, bool Groupable,
    string? Permission, string Description);

/// <summary>A dataset the builder can offer.</summary>
/// <param name="Dataset">Its name.</param>
/// <param name="Label">For the builder.</param>
/// <param name="Description">What a row is.</param>
/// <param name="Fields">Its fields.</param>
public sealed record ExplorerDatasetInfo(
    string Dataset, string Label, string Description, IReadOnlyList<ExplorerFieldInfo> Fields);

/// <summary>Everything the builder can offer, and the limits it works within.</summary>
/// <param name="Datasets">The datasets.</param>
/// <param name="MaxPageSize">Rows per page, at most.</param>
/// <param name="MaxReachableRows">Rows paging can reach.</param>
/// <param name="BudgetRows">Rows a query may read.</param>
public sealed record ExplorerCatalogueResponse(
    IReadOnlyList<ExplorerDatasetInfo> Datasets, int MaxPageSize, int MaxReachableRows, long BudgetRows);
