using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using Sqm.Application.Explorer;
using Sqm.Contracts.Explorer;

namespace Sqm.Infrastructure.ClickHouse.Explorer;

/// <summary>Which physical copy a query reads, and how.</summary>
/// <param name="Table">The table, unqualified: it resolves against the connection's database, which is what lets the tests point the engine at a scratch one.</param>
/// <param name="Final">Whether it needs FINAL (the ReplacingMergeTree copies of current state).</param>
/// <param name="Description">In words, for the plan.</param>
internal sealed record ExplorerSource(string Table, bool Final, string Description);

/// <summary>A compiled query: its SQL, its bound values, and what the router decided.</summary>
/// <param name="Sql">Parameterised SQL; every value is a <c>{pN:Type}</c> placeholder.</param>
/// <param name="Parameters">The values, by placeholder name.</param>
/// <param name="Source">What is read.</param>
/// <param name="Access">KeyRead, RangeRead, IndexRead or Scan.</param>
/// <param name="Notes">Why, in words.</param>
internal sealed record CompiledExplorerQuery(
    string Sql, IReadOnlyDictionary<string, string> Parameters, ExplorerSource Source, string Access, IReadOnlyList<string> Notes);

/// <summary>
/// Turns a checked Explorer query into ClickHouse SQL.
/// </summary>
/// <remarks>
/// <para>
/// <b>The second half of the injection boundary</b> (the first is <see cref="ExplorerValidator"/>).
/// Field names reach SQL only through <see cref="Expressions"/>, a map written here; values reach it
/// only as bound parameters. Nothing a caller typed is ever part of the SQL text.
/// </para>
/// <para>
/// <b>Reading the right copy is most of the work.</b> Current state exists three times, sorted by
/// number, by SIM and by handset, and a filter on anything but the sort key reads all of it -
/// measured at 736 million rows for an IMSI on the number-ordered copy against 360 thousand on the
/// SIM-ordered one. So the compiler looks at what every row must satisfy (the top-level And) and
/// reads the copy that condition is the key of. Each such condition also gets an implied range
/// added beside it, never changing the answer, only letting the primary index seek.
/// </para>
/// <para>
/// A brand or model becomes the TACs it names and then IMEI ranges, because a TAC is the first
/// eight digits of an IMEI: 5 TACs measured at 2.8 million rows read, 300 at 26.7 million. Filtering
/// the stored <c>tac</c> column instead reads the whole table, 708 million rows, because that
/// column is not what the table is sorted by.
/// </para>
/// </remarks>
internal static class ExplorerSqlCompiler
{
    /// <summary>The GSMA columns every device field is read from, joined by TAC.</summary>
    /// <remarks>
    /// Empty strings become NULL, and with <c>join_use_nulls</c> a TAC GSMA does not know gives NULL
    /// too - so "is empty" means one thing: no model on record.
    /// </remarks>
    private const string TacJoin = """
        LEFT JOIN (
            SELECT tac,
                   nullIf(coalesce(nullIf(brandName, ''), manufacturer), '') AS brand,
                   nullIf(marketingName, '')                                 AS model,
                   nullIf(manufacturer, '')                                  AS manufacturer,
                   nullIf(deviceType, '')                                    AS deviceType,
                   nullIf(trim(operatingSystem), '')                         AS operatingSystem
            FROM tac
        ) AS t ON t.tac = b.tac
        """;

    /// <summary>Where each catalogue field is read from. The only path from a field name to SQL.</summary>
    internal static readonly FrozenDictionary<ExplorerDataset, FrozenDictionary<string, string>> Expressions =
        new Dictionary<ExplorerDataset, FrozenDictionary<string, string>>
        {
            [ExplorerDataset.Bindings] = Shared().Concat(new Dictionary<string, string>
            {
                ["active"] = "b.active",
                ["lastChangeDate"] = "b.last_change_date",
            }).ToFrozenDictionary(StringComparer.Ordinal),
            [ExplorerDataset.Events] = Shared().Concat(new Dictionary<string, string>
            {
                ["date"] = "b.data_date",
                ["change"] = "toString(b.label)",
            }).ToFrozenDictionary(StringComparer.Ordinal),
        }.ToFrozenDictionary();

    private static Dictionary<string, string> Shared() => new(StringComparer.Ordinal)
    {
        ["msisdn"] = "b.msisdn",
        ["imsi"] = "b.imsi",
        ["imei"] = "b.imei",
        ["tac"] = "b.tac",
        ["brand"] = "t.brand",
        ["model"] = "t.model",
        ["manufacturer"] = "t.manufacturer",
        ["deviceType"] = "t.deviceType",
        ["operatingSystem"] = "t.operatingSystem",
    };

    /// <summary>The same device fields, on the GSMA table itself - for turning them into TACs.</summary>
    private static readonly FrozenDictionary<string, string> TacTableExpressions = new Dictionary<string, string>
    {
        ["brand"] = "nullIf(coalesce(nullIf(brandName, ''), manufacturer), '')",
        ["model"] = "nullIf(marketingName, '')",
        ["manufacturer"] = "nullIf(manufacturer, '')",
        ["deviceType"] = "nullIf(deviceType, '')",
        ["operatingSystem"] = "nullIf(trim(operatingSystem), '')",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Device fields: read through the GSMA join, and resolvable to TACs.</summary>
    internal static readonly FrozenSet<string> DeviceFields = TacTableExpressions.Keys.ToFrozenSet(StringComparer.Ordinal);

    private static readonly ExplorerSource ByNumber = new("binding_current", true, "Current state, read by number");
    private static readonly ExplorerSource BySim = new("binding_by_imsi", true, "Current state, read by SIM");
    private static readonly ExplorerSource ByHandset = new("binding_by_imei", true, "Current state, read by handset");
    private static readonly ExplorerSource EventLog = new("binding_event", false, "The dated event log");

    /// <summary>
    /// The conditions every row must meet: those in the top-level And, not negated. Only these can
    /// choose a copy or add a range; a condition under an Or or a Not narrows nothing for certain.
    /// </summary>
    internal static IEnumerable<CheckedCondition> RequiredConditions(CheckedFilter? filter) => filter switch
    {
        CheckedCondition { Not: false } c => [c],
        CheckedGroup { Not: false, Logic: ExplorerLogic.And } g => g.Children.SelectMany(RequiredConditions),
        _ => [],
    };

    /// <summary>
    /// The device conditions a TAC lookup can narrow by: positive ones, every row must meet.
    /// </summary>
    /// <remarks>
    /// Not equal and not in are left out on purpose: they also match rows whose TAC GSMA does not
    /// know, which no TAC from the GSMA table can stand for.
    /// </remarks>
    internal static IReadOnlyList<CheckedCondition> ResolvableDeviceConditions(CheckedExplorerQuery query) =>
        [.. RequiredConditions(query.Where).Where(c => DeviceFields.Contains(c.Field)
            && c.Operator is ExplorerOperator.Equals or ExplorerOperator.In or ExplorerOperator.Contains or ExplorerOperator.StartsWith)];

    /// <summary>The SQL that lists the TACs the device conditions name, one more than the cap.</summary>
    internal static (string Sql, IReadOnlyDictionary<string, string> Parameters) TacLookup(
        IReadOnlyList<CheckedCondition> conditions, int cap)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var predicates = conditions.Select(c => Predicate(c, TacTableExpressions[c.Field], nullable: true, parameters));
        var sql = $"SELECT DISTINCT tac FROM tac WHERE {string.Join(" AND ", predicates)} LIMIT {cap + 1}";
        return (sql, parameters);
    }

    /// <summary>Compiles a checked query.</summary>
    /// <param name="query">The query.</param>
    /// <param name="resolvedTacs">
    /// The TACs its device conditions name, when they were looked up and were few enough to read by;
    /// null otherwise.
    /// </param>
    internal static CompiledExplorerQuery Compile(CheckedExplorerQuery query, IReadOnlyList<string>? resolvedTacs)
    {
        ArgumentNullException.ThrowIfNull(query);

        var dataset = query.Definition.Dataset;
        var expressions = Expressions[dataset];
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var notes = new List<string>();

        // ------------------------------------------------------------ route
        var required = RequiredConditions(query.Where).ToList();
        var (source, access, hints) = dataset == ExplorerDataset.Events
            ? RouteEvents(required, parameters, notes)
            : RouteBindings(required, resolvedTacs, parameters, notes);

        // ------------------------------------------------------------ where
        var where = new List<string>();
        if (query.Where is { } filter)
        {
            where.Add(Filter(filter, expressions, parameters));
        }

        where.AddRange(hints);

        // ------------------------------------------------------------ select
        var select = new List<string>();
        var groupExpressions = new List<string>();

        for (var i = 0; i < query.Columns.Count; i++)
        {
            var column = query.Columns[i];
            select.Add($"{(column.Measure is { } m ? Measure(m, expressions) : expressions[column.Name])} AS c{i}");
        }

        foreach (var g in query.GroupBy)
        {
            groupExpressions.Add(expressions[g]);
        }

        // One pass for the page and the total, so they cannot disagree. After GROUP BY the window
        // counts groups, which is the total a grouped page needs.
        select.Add("count() OVER () AS total");

        var needsTac = query.Columns.Any(c => DeviceFields.Contains(c.Name))
            || query.GroupBy.Any(DeviceFields.Contains)
            || query.Measures.Any(m => m.Field is { } f && DeviceFields.Contains(f))
            || Mentions(query.Where, DeviceFields);

        var sql = new StringBuilder();
        sql.Append("SELECT ").AppendJoin(", ", select).AppendLine();
        sql.Append("FROM ").Append(source.Table).Append(" AS b").Append(source.Final ? " FINAL" : string.Empty).AppendLine();

        if (needsTac)
        {
            sql.AppendLine(TacJoin);
        }

        if (where.Count > 0)
        {
            sql.Append("WHERE ").AppendJoin("\n  AND ", where).AppendLine();
        }

        if (groupExpressions.Count > 0)
        {
            sql.Append("GROUP BY ").AppendJoin(", ", groupExpressions).AppendLine();
        }

        if (query.Having is { } having)
        {
            var aliases = query.Columns
                .Select((c, i) => (c, i))
                .Where(x => x.c.Measure is not null)
                .ToDictionary(x => x.c.Name, x => $"c{x.i}", StringComparer.Ordinal);
            sql.Append("HAVING ").AppendLine(Filter(having, aliases, parameters));
        }

        sql.Append("ORDER BY ").AppendJoin(", ", Ordering(query, source)).AppendLine();
        sql.Append(CultureInfo.InvariantCulture, $"LIMIT {query.PageSize} OFFSET {query.Offset}");

        if (needsTac)
        {
            sql.AppendLine().Append("SETTINGS join_use_nulls = 1");
        }

        return new CompiledExplorerQuery(sql.ToString(), parameters, source, access, notes);
    }

    /// <summary>
    /// A stable order, so page two starts where page one ended: the requested sorts, then enough
    /// columns to make every row distinct.
    /// </summary>
    private static List<string> Ordering(CheckedExplorerQuery query, ExplorerSource source)
    {
        var order = new List<string>();
        var index = query.Columns.Select((c, i) => (c.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.Ordinal);

        foreach (var s in query.Sort)
        {
            order.Add($"c{index[s.Field]} {(s.Descending ? "DESC" : "ASC")}");
        }

        if (query.Aggregated)
        {
            // Largest first when nothing was asked for, then every group column: groups are
            // distinct by those, so ties are broken the same way every time.
            if (query.Sort.Count == 0 && query.Columns.FirstOrDefault(c => c.Measure is not null) is { } first)
            {
                order.Add($"c{index[first.Name]} DESC");
            }

            order.AddRange(query.Columns.Select((c, i) => (c, i)).Where(x => x.c.Measure is null).Select(x => $"c{x.i} ASC"));
            return order.Count > 0 ? order : ["total"];
        }

        // Plain rows: the source's own key is unique per row, and is what it is sorted by.
        order.AddRange(source == EventLog
            ? ["b.data_date DESC", "b.msisdn", "b.imsi", "b.imei", "b.label"]
            : source == BySim ? ["b.imsi", "b.msisdn", "b.imei"]
            : source == ByHandset ? ["b.imei", "b.msisdn", "b.imsi"]
            : ["b.msisdn", "b.imsi", "b.imei"]);
        return order;
    }

    private static string Measure(CheckedMeasure m, IReadOnlyDictionary<string, string> expressions)
    {
        var activeOnly = m.ActiveOnly ? "b.active = 1" : null;

        return m.Aggregate switch
        {
            ExplorerAggregate.Count => activeOnly is null ? "count()" : $"countIf({activeOnly})",

            // A handset is a 14-digit IMEI: the 000000 sentinel is one value shared by millions of
            // bindings and is not a handset - the same rule as every handset count on the dashboard.
            ExplorerAggregate.CountDistinct when m.Field == "imei" =>
                $"uniqExactIf(b.imei, length(b.imei) = 14{(activeOnly is null ? string.Empty : " AND " + activeOnly)})",
            ExplorerAggregate.CountDistinct => activeOnly is null
                ? $"uniqExact({expressions[m.Field!]})"
                : $"uniqExactIf({expressions[m.Field!]}, {activeOnly})",

            ExplorerAggregate.Min => $"min({expressions[m.Field!]})",
            ExplorerAggregate.Max => $"max({expressions[m.Field!]})",
            _ => throw new ArgumentOutOfRangeException(nameof(m), m.Aggregate, "Unknown aggregate."),
        };
    }

    private static bool Mentions(CheckedFilter? filter, FrozenSet<string> fields) => filter switch
    {
        CheckedCondition c => fields.Contains(c.Field),
        CheckedGroup g => g.Children.Any(child => Mentions(child, fields)),
        _ => false,
    };

    private static string Filter(CheckedFilter filter, IReadOnlyDictionary<string, string> expressions, Dictionary<string, string> parameters)
    {
        var text = filter switch
        {
            CheckedGroup g => "(" + string.Join(g.Logic == ExplorerLogic.And ? " AND " : " OR ",
                g.Children.Select(c => Filter(c, expressions, parameters))) + ")",
            CheckedCondition c => Predicate(c, expressions[c.Field], nullable: c.Field is var f && (DeviceFields.Contains(f) || f == "lastChangeDate"), parameters),
            _ => throw new ArgumentOutOfRangeException(nameof(filter)),
        };

        return filter.Not ? $"NOT {text}" : text;
    }

    /// <summary>One condition as SQL, every value bound.</summary>
    /// <param name="c">The condition.</param>
    /// <param name="expression">Where the field is read from.</param>
    /// <param name="nullable">
    /// Whether the field can be empty. If so, not-equal and not-in also match the empty rows: "model
    /// is not Galaxy A01" is read as including handsets whose model is unknown.
    /// </param>
    /// <param name="parameters">Where the bound values are collected.</param>
    private static string Predicate(CheckedCondition c, string expression, bool nullable, Dictionary<string, string> parameters)
    {
        string Bind(object value)
        {
            var name = $"p{parameters.Count}";
            var (type, text) = value switch
            {
                ulong u => ("UInt64", u.ToString(CultureInfo.InvariantCulture)),
                long l => ("Int64", l.ToString(CultureInfo.InvariantCulture)),
                DateOnly d => ("Date", d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                bool b => ("UInt8", b ? "1" : "0"),
                string s => ("String", s),
                _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported value."),
            };

            parameters[name] = text;
            return $"{{{name}:{type}}}";
        }

        string List() => string.Join(", ", c.Values.Select(Bind));

        return c.Operator switch
        {
            ExplorerOperator.Equals => $"{expression} = {Bind(c.Values[0])}",
            ExplorerOperator.NotEquals => nullable
                ? $"({expression} IS NULL OR {expression} != {Bind(c.Values[0])})"
                : $"{expression} != {Bind(c.Values[0])}",
            ExplorerOperator.GreaterThan => $"{expression} > {Bind(c.Values[0])}",
            ExplorerOperator.GreaterOrEqual => $"{expression} >= {Bind(c.Values[0])}",
            ExplorerOperator.LessThan => $"{expression} < {Bind(c.Values[0])}",
            ExplorerOperator.LessOrEqual => $"{expression} <= {Bind(c.Values[0])}",
            ExplorerOperator.Between => $"{expression} BETWEEN {Bind(c.Values[0])} AND {Bind(c.Values[1])}",
            ExplorerOperator.In => $"{expression} IN ({List()})",
            ExplorerOperator.NotIn => nullable
                ? $"({expression} IS NULL OR {expression} NOT IN ({List()}))"
                : $"{expression} NOT IN ({List()})",
            ExplorerOperator.Contains => $"positionCaseInsensitiveUTF8({expression}, {Bind(c.Values[0])}) > 0",

            // Identifiers stored as numbers are compared as text, so 912 matches every number that
            // begins 912, whatever its length. The router adds the ranges that let the index seek.
            ExplorerOperator.StartsWith when c.Type is ExplorerFieldType.Msisdn or ExplorerFieldType.Imsi =>
                $"startsWith(toString({expression}), {Bind(c.Values[0])})",
            ExplorerOperator.StartsWith when c.Type == ExplorerFieldType.Text =>
                $"startsWith(lowerUTF8({expression}), {Bind(((string)c.Values[0]).ToLowerInvariant())})",
            ExplorerOperator.StartsWith => $"startsWith({expression}, {Bind(c.Values[0])})",

            ExplorerOperator.IsNull => $"{expression} IS NULL",
            ExplorerOperator.IsNotNull => $"{expression} IS NOT NULL",
            _ => throw new ArgumentOutOfRangeException(nameof(c), c.Operator, "Unknown operator."),
        };
    }

    // ================================================================= routing

    private static (ExplorerSource, string, List<string>) RouteBindings(
        List<CheckedCondition> required, IReadOnlyList<string>? resolvedTacs,
        Dictionary<string, string> parameters, List<string> notes)
    {
        // Exact identifiers first - one seek - then prefixes and TACs, which are ranges.
        var exact = new[] { ExplorerOperator.Equals, ExplorerOperator.In };
        var byField = required.ToLookup(c => c.Field, StringComparer.Ordinal);

        CheckedCondition? Pick(string field, bool exactOnly) =>
            byField[field].FirstOrDefault(c => exact.Contains(c.Operator) || (!exactOnly && c.Operator == ExplorerOperator.StartsWith));

        var hints = new List<string>();

        foreach (var exactOnly in new[] { true, false })
        {
            if (Pick("msisdn", exactOnly) is { } m)
            {
                AddNumericRange(m, "b.msisdn", hints, parameters);
                notes.Add("Read by number: the copy of current state sorted by phone number.");
                return (ByNumber, exactOnly ? "KeyRead" : "RangeRead", hints);
            }

            if (Pick("imsi", exactOnly) is { } s)
            {
                AddNumericRange(s, "b.imsi", hints, parameters);
                notes.Add("Read by SIM: the copy of current state sorted by IMSI.");
                return (BySim, exactOnly ? "KeyRead" : "RangeRead", hints);
            }

            if (Pick("imei", exactOnly) is { } h)
            {
                if (h.Operator == ExplorerOperator.StartsWith)
                {
                    hints.Add(StringRange("b.imei", (string)h.Values[0], parameters));
                }

                notes.Add("Read by handset: the copy of current state sorted by IMEI.");
                return (ByHandset, exactOnly ? "KeyRead" : "RangeRead", hints);
            }
        }

        if (byField["tac"].FirstOrDefault(c => c.Operator is ExplorerOperator.Equals or ExplorerOperator.In or ExplorerOperator.StartsWith) is { } t)
        {
            hints.Add("(" + string.Join(" OR ", t.Values.Select(v => StringRange("b.imei", (string)v, parameters))) + ")");
            notes.Add("Read by TAC: a TAC is the first eight digits of an IMEI, so each is a range of the handset-ordered copy.");
            return (ByHandset, "RangeRead", hints);
        }

        if (resolvedTacs is { Count: > 0 })
        {
            hints.Add("(" + string.Join(" OR ", resolvedTacs.Select(v => StringRange("b.imei", v, parameters))) + ")");
            notes.Add($"Read by model: the device conditions name {resolvedTacs.Count:N0} TAC(s), each a range of the handset-ordered copy.");
            return (ByHandset, "RangeRead", hints);
        }

        notes.Add("No number, SIM, handset, TAC or model every row must match, so all of current state is read. Add one to read a small part of it.");
        return (ByNumber, "Scan", hints);
    }

    private static (ExplorerSource, string, List<string>) RouteEvents(
        List<CheckedCondition> required, Dictionary<string, string> parameters, List<string> notes)
    {
        var hints = new List<string>();

        if (required.FirstOrDefault(c => c.Field == "msisdn" && c.Operator is ExplorerOperator.Equals or ExplorerOperator.In) is not null)
        {
            notes.Add("Read by number within each day of the range: the event log is sorted by phone number.");
            return (EventLog, "KeyRead", hints);
        }

        if (required.FirstOrDefault(c => c.Field == "msisdn" && c.Operator == ExplorerOperator.StartsWith) is { } prefix)
        {
            AddNumericRange(prefix, "b.msisdn", hints, parameters);
            notes.Add("Read by a range of numbers within each day of the range.");
            return (EventLog, "RangeRead", hints);
        }

        if (required.FirstOrDefault(c => c.Field is "imsi" or "imei" && c.Operator is ExplorerOperator.Equals or ExplorerOperator.In) is { } indexed)
        {
            notes.Add($"Read through the {(indexed.Field == "imsi" ? "SIM" : "handset")} index within each day of the range; only the days' blocks that can hold it are opened.");
            return (EventLog, "IndexRead", hints);
        }

        notes.Add("Every event in each day of the range is read. A number, SIM or handset reads only a small part of each day.");
        return (EventLog, "Scan", hints);
    }

    /// <summary>
    /// For a number or SIM stored as an integer: ranges the primary index can seek, implied by the
    /// exact condition beside them. A prefix becomes one range per length a value could have.
    /// </summary>
    private static void AddNumericRange(
        CheckedCondition c, string column, List<string> hints, Dictionary<string, string> parameters)
    {
        if (c.Operator != ExplorerOperator.StartsWith)
        {
            return;   // = and IN on the key column are already what the index seeks by.
        }

        var digits = (string)c.Values[0];
        var ranges = new List<string>();

        // Every length from the prefix's own to 15: 912 is 912, 9120..9129, ... Most are empty; the
        // usual length is where the rows are, and the others cost the index nothing to rule out.
        for (var length = Math.Max(digits.Length, 1); length <= 15; length++)
        {
            var scale = Pow10(length - digits.Length);
            var low = ulong.Parse(digits, CultureInfo.InvariantCulture) * scale;
            var high = low + scale - 1;
            var lo = $"p{parameters.Count}";
            parameters[lo] = low.ToString(CultureInfo.InvariantCulture);
            var hi = $"p{parameters.Count}";
            parameters[hi] = high.ToString(CultureInfo.InvariantCulture);
            ranges.Add($"({column} BETWEEN {{{lo}:UInt64}} AND {{{hi}:UInt64}})");
        }

        hints.Add("(" + string.Join(" OR ", ranges) + ")");
    }

    private static ulong Pow10(int exponent)
    {
        ulong result = 1;
        for (var i = 0; i < exponent; i++)
        {
            result *= 10;
        }

        return result;
    }

    /// <summary>
    /// Every string beginning with <paramref name="prefix"/> as a range: from the prefix, up to the
    /// prefix followed by ':' - the character after '9' - so the index can seek it.
    /// </summary>
    private static string StringRange(string column, string prefix, Dictionary<string, string> parameters)
    {
        var lo = $"p{parameters.Count}";
        parameters[lo] = prefix;
        var hi = $"p{parameters.Count}";
        parameters[hi] = prefix + ":";
        return $"({column} >= {{{lo}:String}} AND {column} < {{{hi}:String}})";
    }
}
