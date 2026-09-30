using System.Globalization;
using System.Text.RegularExpressions;
using Sqm.Contracts.Explorer;

namespace Sqm.Application.Explorer;

/// <summary>A node of a checked condition tree.</summary>
public abstract record CheckedFilter(bool Not);

/// <summary>A checked group.</summary>
/// <param name="Logic">How the children combine.</param>
/// <param name="Children">Its nodes.</param>
/// <param name="Not">Negated.</param>
public sealed record CheckedGroup(ExplorerLogic Logic, IReadOnlyList<CheckedFilter> Children, bool Not) : CheckedFilter(Not);

/// <summary>A checked condition, its values parsed to the field's type.</summary>
/// <param name="Field">The catalogue name of a field - or, in Having, a measure's name.</param>
/// <param name="Type">How the values were parsed.</param>
/// <param name="Operator">The comparison.</param>
/// <param name="Values">
/// Parsed: <see cref="ulong"/> for a number or SIM, <see cref="string"/> for a handset, TAC, text
/// or label, <see cref="DateOnly"/> for a date, <see cref="bool"/>, <see cref="long"/> for a
/// count. A starts-with value stays the digits or text typed.
/// </param>
/// <param name="Not">Negated.</param>
public sealed record CheckedCondition(
    string Field, ExplorerFieldType Type, ExplorerOperator Operator, IReadOnlyList<object> Values, bool Not)
    : CheckedFilter(Not);

/// <summary>A checked measure, with the column alias the compiler gives it.</summary>
/// <param name="Name">As the caller named it.</param>
/// <param name="Alias">m0, m1, ... - never the caller's text in SQL.</param>
/// <param name="Aggregate">What it computes.</param>
/// <param name="Field">What it computes it over.</param>
/// <param name="ActiveOnly">Only active bindings.</param>
/// <param name="Type">Number for counts, Date for Min and Max.</param>
public sealed record CheckedMeasure(
    string Name, string Alias, ExplorerAggregate Aggregate, string? Field, bool ActiveOnly, ExplorerFieldType Type);

/// <summary>An output column.</summary>
/// <param name="Name">Field or measure name.</param>
/// <param name="Label">Heading.</param>
/// <param name="Type">Its type.</param>
/// <param name="Measure">The measure, when it is one.</param>
public sealed record CheckedColumn(string Name, string Label, ExplorerFieldType Type, CheckedMeasure? Measure);

/// <summary>A query that passed every check, ready to plan.</summary>
/// <param name="Definition">The dataset.</param>
/// <param name="Where">Which rows; null for all.</param>
/// <param name="GroupBy">Grouping fields.</param>
/// <param name="Measures">Measures.</param>
/// <param name="Having">Which groups.</param>
/// <param name="Columns">What comes back, in order.</param>
/// <param name="Sort">Ordering, by column name.</param>
/// <param name="Page">1-based.</param>
/// <param name="PageSize">Rows per page.</param>
/// <param name="FromDate">Events: the first day read.</param>
/// <param name="ToDate">Events: the last day read.</param>
/// <param name="Permissions">What the caller must hold for this query.</param>
public sealed record CheckedExplorerQuery(
    ExplorerDatasetDefinition Definition,
    CheckedFilter? Where,
    IReadOnlyList<string> GroupBy,
    IReadOnlyList<CheckedMeasure> Measures,
    CheckedFilter? Having,
    IReadOnlyList<CheckedColumn> Columns,
    IReadOnlyList<ExplorerSort> Sort,
    int Page,
    int PageSize,
    DateOnly? FromDate,
    DateOnly? ToDate,
    IReadOnlySet<string> Permissions)
{
    /// <summary>Grouped or aggregated: one row per group, or one row for everything.</summary>
    public bool Aggregated => GroupBy.Count > 0 || Measures.Count > 0;

    /// <summary>Rows skipped before this page.</summary>
    public int Offset => (Page - 1) * PageSize;
}

/// <summary>One problem with a query, located so the builder can show it where it is.</summary>
/// <param name="Path">Where: e.g. <c>where.children[2].values[0]</c>, <c>measures[1].field</c>.</param>
/// <param name="Message">What is wrong, in words.</param>
public sealed record ExplorerProblem(string Path, string Message);

/// <summary>
/// Checks an Explorer request against the catalogue and the limits, and parses every value.
/// </summary>
/// <remarks>
/// The injection boundary is here and in the compiler together. Here: a field is a name from the
/// catalogue or the request is refused, an operator is one the field accepts, and every value is
/// parsed to its type - digits for identifiers, a real date for dates. There: values only ever
/// reach SQL as bound parameters. Neither alone would be enough.
/// </remarks>
public static partial class ExplorerValidator
{
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,31}$")]
    private static partial Regex MeasureName();

    private const int MaxTextLength = 200;
    private const int MaxColumns = 20;
    private const int MaxMeasures = 10;
    private const int MaxSorts = 3;

    /// <summary>Checks a request. Either a checked query or problems, never both.</summary>
    public static (CheckedExplorerQuery? Query, IReadOnlyList<ExplorerProblem> Problems) Check(
        ExplorerQueryRequest request, ExplorerOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<ExplorerProblem>();

        if (!Enum.IsDefined(request.Dataset))
        {
            return (null, [new("dataset", "Unknown dataset.")]);
        }

        var definition = ExplorerCatalogue.For(request.Dataset);
        var fieldsUsed = new HashSet<string>(StringComparer.Ordinal);
        var counter = new Counter();

        var where = request.Where is null
            ? null
            : CheckNode(request.Where, "where", 1, definition, options, problems, fieldsUsed, counter, measures: null);

        // ------------------------------------------------------------ measures
        var measures = new List<CheckedMeasure>();
        var measureNames = new HashSet<string>(StringComparer.Ordinal);

        var requested = request.Measures ?? [];
        if (requested.Count > MaxMeasures)
        {
            problems.Add(new("measures", $"At most {MaxMeasures} measures."));
        }

        for (var i = 0; i < Math.Min(requested.Count, MaxMeasures); i++)
        {
            var m = requested[i];
            var path = $"measures[{i}]";

            if (m is null || string.IsNullOrWhiteSpace(m.Name) || !MeasureName().IsMatch(m.Name))
            {
                problems.Add(new(path + ".name", "A name of letters, digits and _, starting with a letter, up to 32 characters."));
                continue;
            }

            if (definition.Fields.ContainsKey(m.Name) || !measureNames.Add(m.Name))
            {
                problems.Add(new(path + ".name", $"\"{m.Name}\" is already a field or another measure's name."));
                continue;
            }

            if (m.ActiveOnly && request.Dataset != ExplorerDataset.Bindings)
            {
                problems.Add(new(path + ".activeOnly", "Only bindings are active or not."));
            }

            ExplorerFieldType type;
            switch (m.Aggregate)
            {
                case ExplorerAggregate.Count when m.Field is null:
                    type = ExplorerFieldType.Number;
                    break;
                case ExplorerAggregate.Count:
                    problems.Add(new(path + ".field", "Count counts rows; for different values use CountDistinct."));
                    continue;
                case ExplorerAggregate.CountDistinct when m.Field is not null && definition.Fields.ContainsKey(m.Field):
                    type = ExplorerFieldType.Number;
                    break;
                case ExplorerAggregate.Min or ExplorerAggregate.Max
                    when m.Field is not null && definition.Fields.TryGetValue(m.Field, out var f) && f.Type == ExplorerFieldType.Date:
                    type = ExplorerFieldType.Date;
                    break;
                case ExplorerAggregate.Min or ExplorerAggregate.Max:
                    problems.Add(new(path + ".field", "Min and Max take a date field."));
                    continue;
                default:
                    problems.Add(new(path + ".field", m.Field is null ? "CountDistinct needs a field." : $"\"{m.Field}\" is not a field of {definition.Label}."));
                    continue;
            }

            measures.Add(new CheckedMeasure(m.Name, $"m{measures.Count}", m.Aggregate, m.Field, m.ActiveOnly, type));
        }

        // ------------------------------------------------------------ grouping and columns
        var groupBy = new List<string>();
        foreach (var (name, i) in (request.GroupBy ?? []).Select((n, i) => (n, i)))
        {
            if (name is null || !definition.Fields.TryGetValue(name, out var f))
            {
                problems.Add(new($"groupBy[{i}]", $"\"{name}\" is not a field of {definition.Label}."));
            }
            else if (!f.Groupable)
            {
                problems.Add(new($"groupBy[{i}]", $"{f.Label} cannot be grouped by."));
            }
            else if (groupBy.Contains(name))
            {
                problems.Add(new($"groupBy[{i}]", $"{f.Label} is grouped by twice."));
            }
            else
            {
                groupBy.Add(name);
                fieldsUsed.Add(name);
            }
        }

        var columns = new List<CheckedColumn>();
        var aggregated = groupBy.Count > 0 || measures.Count > 0;

        if (aggregated)
        {
            if (request.Columns is { Count: > 0 })
            {
                problems.Add(new("columns", "A grouped query returns its groups and measures; leave columns empty."));
            }

            if (groupBy.Count > 0 && measures.Count == 0)
            {
                problems.Add(new("measures", "A grouped query needs at least one measure - Count, for how many rows each group has."));
            }

            columns.AddRange(groupBy.Select(g => Column(definition.Fields[g])));
            columns.AddRange(measures.Select(m => new CheckedColumn(m.Name, m.Name, m.Type, m)));
        }
        else
        {
            var names = request.Columns is { Count: > 0 } asked ? asked : definition.DefaultColumns;
            if (names.Count > MaxColumns)
            {
                problems.Add(new("columns", $"At most {MaxColumns} columns."));
            }

            foreach (var (name, i) in names.Take(MaxColumns).Select((n, i) => (n, i)))
            {
                if (name is null || !definition.Fields.TryGetValue(name, out var f))
                {
                    problems.Add(new($"columns[{i}]", $"\"{name}\" is not a field of {definition.Label}."));
                }
                else if (columns.Any(c => c.Name == name))
                {
                    problems.Add(new($"columns[{i}]", $"{f.Label} is listed twice."));
                }
                else
                {
                    columns.Add(Column(f));
                    fieldsUsed.Add(name);
                }
            }
        }

        // ------------------------------------------------------------ having
        CheckedFilter? having = null;
        if (request.Having is not null)
        {
            if (measures.Count == 0)
            {
                problems.Add(new("having", "Having filters groups by their measures; add a measure first."));
            }
            else
            {
                having = CheckNode(request.Having, "having", 1, definition, options, problems, fieldsUsed, counter,
                    measures.ToDictionary(m => m.Name, StringComparer.Ordinal));
            }
        }

        // ------------------------------------------------------------ sort and page
        var sort = request.Sort ?? [];
        if (sort.Count > MaxSorts)
        {
            problems.Add(new("sort", $"At most {MaxSorts} orderings."));
        }

        foreach (var (s, i) in sort.Take(MaxSorts).Select((s, i) => (s, i)))
        {
            if (s is null || !columns.Any(c => c.Name == s.Field))
            {
                problems.Add(new($"sort[{i}].field", "Sort by a column the query returns."));
            }
        }

        if (request.PageSize < 1 || request.PageSize > options.MaxPageSize)
        {
            problems.Add(new("pageSize", $"Between 1 and {options.MaxPageSize} rows per page."));
        }
        else if (request.Page < 1 || (long)request.Page * request.PageSize > options.MaxReachableRows)
        {
            problems.Add(new("page", $"Paging reaches the first {options.MaxReachableRows:N0} rows; narrow the query to see past them."));
        }

        // ------------------------------------------------------------ the event log needs dates
        var (fromDate, toDate) = request.Dataset == ExplorerDataset.Events ? DateBounds(where) : (null, null);

        if (request.Dataset == ExplorerDataset.Events && (fromDate is null || toDate is null))
        {
            problems.Add(new("where", "Events need a date range: a Date condition with both ends - Between, or a pair such as >= and <= - at the top level of the conditions."));
        }

        if (problems.Count > 0)
        {
            return (null, problems);
        }

        var permissions = new HashSet<string>(StringComparer.Ordinal) { Identity.Permissions.ExplorerQuery };
        foreach (var name in fieldsUsed)
        {
            if (definition.Fields.TryGetValue(name, out var f) && f.Permission is { } p)
            {
                permissions.Add(p);
            }
        }

        return (new CheckedExplorerQuery(
            definition, where, groupBy, measures, having, columns, sort, request.Page, request.PageSize,
            fromDate, toDate, permissions), []);
    }

    private static CheckedColumn Column(ExplorerField f) => new(f.Name, f.Label, f.Type, null);

    private sealed class Counter
    {
        public int Conditions { get; set; }
    }

    private static CheckedFilter? CheckNode(
        ExplorerFilter node, string path, int depth, ExplorerDatasetDefinition definition, ExplorerOptions options,
        List<ExplorerProblem> problems, HashSet<string> fieldsUsed, Counter counter,
        IReadOnlyDictionary<string, CheckedMeasure>? measures)
    {
        if (depth > options.MaxDepth)
        {
            problems.Add(new(path, $"Groups nest at most {options.MaxDepth} deep."));
            return null;
        }

        var isGroup = node.Logic is not null || node.Children is not null;
        var isCondition = node.Field is not null || node.Operator is not null || node.Values is not null;

        if (isGroup == isCondition)
        {
            problems.Add(new(path, "Either a group (logic and children) or a condition (field, operator, values)."));
            return null;
        }

        if (isGroup)
        {
            if (node.Logic is not { } logic || !Enum.IsDefined(logic) || node.Children is not { Count: > 0 } children)
            {
                problems.Add(new(path, "A group needs And or Or, and at least one child."));
                return null;
            }

            var checkedChildren = new List<CheckedFilter>(children.Count);
            for (var i = 0; i < children.Count; i++)
            {
                if (children[i] is null)
                {
                    problems.Add(new($"{path}.children[{i}]", "Empty."));
                    continue;
                }

                if (CheckNode(children[i], $"{path}.children[{i}]", depth + 1, definition, options, problems, fieldsUsed, counter, measures) is { } c)
                {
                    checkedChildren.Add(c);
                }
            }

            return checkedChildren.Count == children.Count ? new CheckedGroup(logic, checkedChildren, node.Not) : null;
        }

        if (++counter.Conditions > options.MaxConditions)
        {
            if (counter.Conditions == options.MaxConditions + 1)
            {
                problems.Add(new(path, $"At most {options.MaxConditions} conditions in one query."));
            }

            return null;
        }

        if (node.Operator is not { } op || !Enum.IsDefined(op))
        {
            problems.Add(new($"{path}.operator", "Missing or unknown operator."));
            return null;
        }

        ExplorerFieldType type;
        System.Collections.Frozen.FrozenSet<ExplorerOperator> allowed;
        IReadOnlyList<string>? labels = null;
        string label;

        if (measures is not null)
        {
            if (node.Field is null || !measures.TryGetValue(node.Field, out var m))
            {
                problems.Add(new($"{path}.field", "In Having, a condition names one of the query's measures."));
                return null;
            }

            type = m.Type;
            allowed = ExplorerCatalogue.MeasureOps;
            label = m.Name;
        }
        else
        {
            if (node.Field is null || !definition.Fields.TryGetValue(node.Field, out var f))
            {
                problems.Add(new($"{path}.field", $"\"{node.Field}\" is not a field of {definition.Label}."));
                return null;
            }

            type = f.Type;
            allowed = f.Operators;
            labels = f.Labels;
            label = f.Label;
            fieldsUsed.Add(f.Name);
        }

        if (!allowed.Contains(op))
        {
            problems.Add(new($"{path}.operator", $"{label} does not take {op}."));
            return null;
        }

        var raw = node.Values ?? [];
        var (min, max) = op switch
        {
            ExplorerOperator.IsNull or ExplorerOperator.IsNotNull => (0, 0),
            ExplorerOperator.Between => (2, 2),
            ExplorerOperator.In or ExplorerOperator.NotIn => (1, options.MaxInValues),
            _ => (1, 1),
        };

        if (raw.Count < min || raw.Count > max)
        {
            problems.Add(new($"{path}.values", min == max
                ? $"{op} takes {(min == 0 ? "no values" : min == 1 ? "one value" : $"{min} values")}."
                : $"{op} takes 1 to {max:N0} values."));
            return null;
        }

        var values = new List<object>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var parsed = Parse(raw[i], type, op, labels, out var why);
            if (parsed is null)
            {
                problems.Add(new($"{path}.values[{i}]", why));
            }
            else
            {
                values.Add(parsed);
            }
        }

        if (values.Count != raw.Count)
        {
            return null;
        }

        if (op == ExplorerOperator.Between && Comparer<object>.Default.Compare(values[0], values[1]) > 0)
        {
            problems.Add(new($"{path}.values", "Between takes the lower value first."));
            return null;
        }

        return new CheckedCondition(node.Field!, type, op, values, node.Not);
    }

    /// <summary>Parses one value to its field's type, or says why it cannot be.</summary>
    private static object? Parse(string? value, ExplorerFieldType type, ExplorerOperator op, IReadOnlyList<string>? labels, out string why)
    {
        why = string.Empty;
        var text = value?.Trim() ?? string.Empty;
        var prefix = op == ExplorerOperator.StartsWith;

        switch (type)
        {
            case ExplorerFieldType.Msisdn:
                // As the lookup reads it: 0913 123 4567, +98 913 123 4567 and 9131234567 are one
                // number, stored without the trunk 0 or the country code. A prefix loses its 0 too.
                if (!Domain.Identifiers.Msisdn.TryParse(text, out var phone) || phone.Value.DigitCount > 15)
                {
                    why = "A phone number: digits, with spaces, dashes or a leading 0 or +98 if you like.";
                    return null;
                }

                return prefix ? phone.Value.ToString() : phone.Value.Value;

            case ExplorerFieldType.Imsi:
                if (!Digits(text, 1, 15))
                {
                    why = "Digits only, up to 15.";
                    return null;
                }

                return prefix ? text : ulong.Parse(text, CultureInfo.InvariantCulture);

            case ExplorerFieldType.Imei:
                if (!Digits(text, 1, 16))
                {
                    why = "Digits only, up to 16.";
                    return null;
                }

                return text;

            case ExplorerFieldType.Tac:
                if (!Digits(text, prefix ? 1 : 8, 8))
                {
                    why = prefix ? "Digits only, up to 8." : "Eight digits.";
                    return null;
                }

                return text;

            case ExplorerFieldType.Text:
                if (text.Length is 0 or > MaxTextLength)
                {
                    why = $"Text of 1 to {MaxTextLength} characters.";
                    return null;
                }

                return text;

            case ExplorerFieldType.Label:
                if (labels is null || !labels.Contains(text, StringComparer.Ordinal))
                {
                    why = $"One of: {string.Join(", ", labels ?? [])}.";
                    return null;
                }

                return text;

            case ExplorerFieldType.Date:
                if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    why = "A date, yyyy-MM-dd.";
                    return null;
                }

                return date;

            case ExplorerFieldType.Boolean:
                if (!bool.TryParse(text, out var flag))
                {
                    why = "true or false.";
                    return null;
                }

                return flag;

            case ExplorerFieldType.Number:
                if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    why = "A whole number, 0 or more.";
                    return null;
                }

                return number;

            default:
                why = "Unsupported field.";
                return null;
        }
    }

    private static bool Digits(string text, int min, int max) =>
        text.Length >= min && text.Length <= max && text.All(char.IsAsciiDigit);

    /// <summary>
    /// The date range an event query reads, from the date conditions every row must meet: the
    /// top-level And, not negated. A date inside an Or or a Not does not bound anything.
    /// </summary>
    private static (DateOnly? From, DateOnly? To) DateBounds(CheckedFilter? where)
    {
        DateOnly? from = null, to = null;

        IEnumerable<CheckedCondition> Required(CheckedFilter? f) => f switch
        {
            CheckedCondition { Not: false } c => [c],
            CheckedGroup { Not: false, Logic: ExplorerLogic.And } g => g.Children.SelectMany(Required),
            _ => [],
        };

        foreach (var c in Required(where).Where(c => c.Field == "date"))
        {
            var a = (DateOnly)c.Values[0];
            switch (c.Operator)
            {
                case ExplorerOperator.Equals:
                    from = Later(from, a);
                    to = Earlier(to, a);
                    break;
                case ExplorerOperator.Between:
                    from = Later(from, a);
                    to = Earlier(to, (DateOnly)c.Values[1]);
                    break;
                case ExplorerOperator.GreaterOrEqual:
                    from = Later(from, a);
                    break;
                case ExplorerOperator.GreaterThan:
                    from = Later(from, a.AddDays(1));
                    break;
                case ExplorerOperator.LessOrEqual:
                    to = Earlier(to, a);
                    break;
                case ExplorerOperator.LessThan:
                    to = Earlier(to, a.AddDays(-1));
                    break;
            }
        }

        return (from, to);

        static DateOnly Later(DateOnly? current, DateOnly d) => current is { } c && c > d ? c : d;
        static DateOnly Earlier(DateOnly? current, DateOnly d) => current is { } c && c < d ? c : d;
    }
}
