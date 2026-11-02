namespace Sqm.Infrastructure.ClickHouse;

/// <summary>
/// The per-model planning statements the API reads live and the import worker writes once when a run
/// publishes (analytics migration 032): one definition, so the two cannot drift apart.
/// </summary>
/// <remarks>
/// Every statement reads the delivery's per-model mart at <c>{seq:UInt16}</c>, with unqualified table
/// names: both callers send the database with the request.
/// </remarks>
internal static class PlanningModelSql
{
    /// <summary>What a 5G model's record states of its architecture: SA, NSA, both or neither.</summary>
    public const string ArchitectureExpression =
        "multiIf(r.sa = 1 AND r.nsa = 1, 'arch-both', r.sa = 1, 'arch-sa', r.nsa = 1, 'arch-nsa', 'arch-not-stated')";

    /// <summary>
    /// The delivery's per-model rows (<c>agg_tac_population</c>) for the models <paramref name="condition"/>
    /// (on <c>r</c>, tac_radio_active) holds of, each with <paramref name="attributes"/> of its profile or
    /// catalogue record (on <c>r</c> and <c>t</c>) as <c>a1</c>, <c>a2</c>... - looked up by TAC, not joined.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The uniq() states must not pass through a join.</b> Joined to the catalogue for its names, the IoT
    /// fleet by vendor took 1.84 GiB, and the four NTN classes 796 MiB, over the read path's 476 MiB cap;
    /// looked up instead, 380 and 386 MiB, and the LTE bands 382 MiB (2026-10-27, delivery 246; turning
    /// join side-swapping off changed nothing). The profiles of the models in question go into arrays once,
    /// and each row finds its own with <c>transform</c>.
    /// </para>
    /// <para>
    /// <b>The mart, not the run's breakdown.</b> Both give the same figures - the NTN classes agree to the
    /// binding - but the mart is ordered by (delivery, TAC), so a TAC condition skips every other model's
    /// states: 33-46 MiB read. The run's breakdown is ordered by class first, and the IoT fleet read all
    /// 447 MiB of it and used 597 MiB.
    /// </para>
    /// </remarks>
    public static string ModelRows(string condition, string match, params string[] attributes)
    {
        var arrays = string.Concat(attributes.Select(a => $", groupArray({a})"));
        var columns = string.Concat(attributes.Select((_, n) => $", m.{n + 2}[ix] AS a{n + 1}"));
        return $$"""
            WITH (SELECT (groupArray(r.tac){{arrays}})
                  FROM tac_radio_active AS r LEFT JOIN tac AS t ON t.tac = r.tac
                  WHERE {{condition}}) AS m
            SELECT p.tac AS tac, p.subscribers AS numbers, p.bindings AS bindings, p.handsets AS handsets,
                   transform(p.tac, m.1, arrayEnumerate(m.1), toUInt32(0)) AS ix{{columns}}
            FROM agg_tac_population AS p
            WHERE p.seq = {seq:UInt16} AND p.tac IN ({{ModelTacs(condition)}}) {{match}}
            """;
    }

    /// <summary>
    /// The TACs a condition on <c>r</c> holds of, for <c>p.tac IN (...)</c>: on the mart's key, so the
    /// states of every other model are not read.
    /// </summary>
    public static string ModelTacs(string condition) => $"SELECT r.tac FROM tac_radio_active AS r WHERE {condition}";

    /// <summary>
    /// The population of the models in each class of <paramref name="expression"/> among those
    /// <paramref name="where"/> holds of: <c>k, n, b, h</c> - class, numbers, bindings, handsets.
    /// </summary>
    public static string ClassRows(string expression, string where, string match) => $$"""
        SELECT a1 AS k, uniqMerge(numbers) AS n, sum(bindings) AS b, uniqMerge(handsets) AS h
        FROM ({{ModelRows(where, match, expression)}})
        GROUP BY k
        """;

    /// <summary>
    /// Every band of one technology a model lists (<paramref name="column"/>, an array on <c>r</c>), with
    /// the population of the models listing it: <c>band, n, b, h</c>.
    /// </summary>
    public static string BandRows(string column, string match) => $$"""
        SELECT band, uniqMerge(numbers) AS n, sum(bindings) AS b, uniqMerge(handsets) AS h
        FROM (SELECT numbers, bindings, handsets, arrayJoin(a1) AS band
              FROM ({{ModelRows($"notEmpty({column})", match, column)}}))
        GROUP BY band
        """;

    /// <summary>
    /// The item written after every other figure of a view. Figures without it are a write that was cut
    /// short - a band or a class may be missing - and the view is read live instead.
    /// </summary>
    public const string FiguresComplete = "complete";

    /// <summary>
    /// The band view's figures that no filter or setting changes, as <c>item, n, b, h</c>: every LTE
    /// and NR band (<c>lte:3</c>, <c>nr:78</c>), the 5G architecture classes, and the ambiguous models.
    /// One statement per part, so each is measured and bounded on its own.
    /// </summary>
    public static IReadOnlyList<string> BandFigureParts() =>
    [
        $"SELECT concat('lte:', toString(band)) AS item, n, b, h FROM ({BandRows("r.lte_bands", string.Empty)})",
        $"SELECT concat('nr:', toString(band)) AS item, n, b, h FROM ({BandRows("r.nr_bands", string.Empty)})",
        $"SELECT k AS item, n, b, h FROM ({ClassRows(ArchitectureExpression, "r.five_g = 1", string.Empty)})",
        $"SELECT k AS item, n, b, h FROM ({ClassRows("'ambiguous'", "notEmpty(r.unparsed)", string.Empty)})",
    ];
}
