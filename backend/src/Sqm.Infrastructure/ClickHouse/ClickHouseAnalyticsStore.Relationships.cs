using System.Globalization;
using Microsoft.Extensions.Logging;
using Sqm.Application.Abstractions;
using Sqm.Contracts.Lookup;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>
/// The relationships between a number, a SIM and a handset.
/// </summary>
/// <remarks>
/// <para>
/// The binding grain is <c>(msisdn, imsi, imei)</c>, so every one of these relationships is
/// already a fact in the data rather than something to be inferred - the work here is reaching it
/// from whichever of the three the caller happens to hold. Each direction reads the copy of
/// current state that is sorted for it: <c>binding_current</c> by MSISDN, <c>binding_by_imsi</c>
/// by IMSI, <c>binding_by_imei</c> by IMEI. All three are the same rows; picking the right one
/// turns a 315-million-row scan into a primary-key read.
/// </para>
/// <para>
/// <b>Handset pairing is the one thing here that is derived, and it is derived narrowly.</b> A
/// dual-SIM phone carries two IMEIs, one per radio, and nothing in the feed says which two belong
/// together. What can be shown is a pair where the same phone number has been seen on both, and
/// the two IMEIs are consecutive within one TAC. Measured on TAC 86453906 (Redmi Note 12S,
/// 151,113 handsets), pairs sharing a number by serial distance:
/// </para>
/// <code>
///   distance 1     4,760
///   distance 2         0
///   distance 3         1
///   distance 1000      0
/// </code>
/// <para>
/// That is not a tendency, it is a signature: consecutive serials sharing a subscriber are the two
/// radios of one handset, and nothing else produces it. The same test on TAC 35004012
/// (Galaxy A54 5G) returns <b>zero</b> at distance 1, so this is a property of how some
/// manufacturers allocate and not a universal rule - see <c>RelationshipEndpoints</c> for what the
/// product says about that.
/// </para>
/// </remarks>
public sealed partial class ClickHouseAnalyticsStore
{
    [LoggerMessage(EventId = 1400, Level = LogLevel.Information,
        Message = "Relationship lookup on {Kind}: {Nodes} related identifiers in {ElapsedMs} ms")]
    private partial void LogRelationship(RelatedKind kind, int nodes, long elapsedMs);

    /// <summary>
    /// Largest number of bindings one identifier may pull back.
    /// </summary>
    /// <remarks>
    /// One SIM in this dataset carries 1,216 handsets. That is a real row and worth seeing, but
    /// an explorer is not the place to render it whole, and an unbounded read here is how one
    /// outlier becomes a slow page for everybody. The caller is told when the cap bit.
    /// </remarks>
    private const int MaxBindings = 500;

    /// <summary>
    /// How many handsets are checked for a physical pair.
    /// </summary>
    /// <remarks>
    /// Each handset contributes four candidates - two neighbouring serials and two twin-TAC
    /// positions - so this bounds the width of one IN list against a table sorted by IMEI. Forty
    /// handsets is far past what a real subscriber has and still a cheap set of point reads.
    /// </remarks>
    private const int MaxPairProbes = 40;

    /// <inheritdoc />
    public async Task<RelationshipGraph> GetRelationshipsAsync(
        RelatedKind kind, string identifier, RelationshipSections allowed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        var (table, column, type) = kind switch
        {
            RelatedKind.Msisdn => ("sqm.binding_current", "msisdn", "UInt64"),
            RelatedKind.Imsi => ("sqm.binding_by_imsi", "imsi", "UInt64"),
            RelatedKind.Imei => ("sqm.binding_by_imei", "imei", "String"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown identifier."),
        };

        var sql = $$"""
            SELECT
                b.msisdn, b.imsi, b.imei, b.tac,
                coalesce(nullIf(t.brandName, ''), t.manufacturer) AS brand,
                t.marketingName, t.deviceType,
                b.active,
                b.last_change_date
            FROM {{table}} AS b FINAL
            LEFT JOIN sqm.tac AS t ON t.tac = b.tac
            WHERE b.{{column}} = {value:{{type}}}
            ORDER BY b.active DESC, b.msisdn, b.imsi, b.imei
            LIMIT {{MaxBindings + 1}}
            """;

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["value"] = identifier,
        };

        var result = await JsonQuery.ExecuteAsync(sql, parameters, ct).ConfigureAwait(false);

        var truncated = result.Rows.Count > MaxBindings;
        var rows = truncated ? result.Rows.Take(MaxBindings).ToList() : result.Rows;

        // Accumulate each neighbour once, counting how many bindings reach it and how many of
        // those are still in force. A handset a subscriber used and replaced is still connected -
        // hiding it would answer a different question from the one being asked.
        var subscribers = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        var sims = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        var handsets = new Dictionary<string, Accumulator>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var msisdn = ClickHouseJsonResult.UInt64(row, 0).ToString(CultureInfo.InvariantCulture);
            var imsi = ClickHouseJsonResult.UInt64(row, 1).ToString(CultureInfo.InvariantCulture);
            var imei = ClickHouseJsonResult.Text(row, 2) ?? string.Empty;
            var active = ClickHouseJsonResult.Int32(row, 7) == 1;
            var confirmed = ClickHouseJsonResult.Date(row, 8);

            Add(subscribers, msisdn, active, confirmed, null, null, null, null);
            Add(sims, imsi, active, confirmed, null, null, null, null);
            Add(handsets, imei, active, confirmed,
                ClickHouseJsonResult.NullIfEmpty(row, 3),
                ClickHouseJsonResult.NullIfEmpty(row, 4),
                ClickHouseJsonResult.NullIfEmpty(row, 5),
                ClickHouseJsonResult.NullIfEmpty(row, 6));
        }

        // The centre is not its own neighbour.
        _ = kind switch
        {
            RelatedKind.Msisdn => subscribers.Remove(identifier),
            RelatedKind.Imsi => sims.Remove(identifier),
            _ => handsets.Remove(identifier),
        };

        var paired = allowed.Handsets && handsets.Count > 0
            ? await FindPairedHandsetsAsync(kind, identifier, handsets.Keys, rows, ct)
                .ConfigureAwait(false)
            : [];

        LogRelationship(kind, subscribers.Count + sims.Count + handsets.Count, result.ElapsedMs);

        var withheld = new List<string>(3);
        if (!allowed.Subscribers) withheld.Add("subscribers");
        if (!allowed.Sims) withheld.Add("sims");
        if (!allowed.Handsets) withheld.Add("handsets");

        return new RelationshipGraph(
            Centre: identifier,
            Kind: kind,
            Found: rows.Count > 0,
            Subscribers: allowed.Subscribers ? Materialise(subscribers, RelatedKind.Msisdn) : [],
            Sims: allowed.Sims ? Materialise(sims, RelatedKind.Imsi) : [],
            Handsets: allowed.Handsets ? Materialise(handsets, RelatedKind.Imei) : [],
            Paired: paired,
            Withheld: withheld,
            Truncated: truncated,
            ElapsedMs: result.ElapsedMs,
            RowsExamined: result.RowsRead);
    }

    /// <summary>
    /// Finds handsets that share a physical device with one of the handsets already found.
    /// </summary>
    /// <remarks>
    /// The rule is deliberately strict: the candidate must be the adjacent serial within the same
    /// TAC <em>and</em> must share a phone number with its partner. Adjacency alone is not
    /// evidence - within one TAC roughly a quarter of serials are adjacent to another live handset
    /// simply because the model sold well. It is the shared subscriber that makes it a pair, and
    /// measured at distance 2 and 3 that shared subscriber never appears.
    /// </remarks>
    private async Task<IReadOnlyList<PairedHandset>> FindPairedHandsetsAsync(
        RelatedKind kind,
        string identifier,
        IEnumerable<string> handsets,
        IReadOnlyList<System.Text.Json.JsonElement> rows,
        CancellationToken ct)
    {
        // Which numbers reach each handset, and what model it is, from the rows already in hand -
        // no second read. The model matters because the twin-TAC candidate crosses a TAC boundary
        // on purpose, so "same marketing name" is what keeps that from reaching a different phone.
        var numbersByHandset = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var modelByHandset = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var imei = ClickHouseJsonResult.Text(row, 2) ?? string.Empty;
            var msisdn = ClickHouseJsonResult.UInt64(row, 0).ToString(CultureInfo.InvariantCulture);
            if (imei.Length != 14) continue;

            if (!numbersByHandset.TryGetValue(imei, out var set))
            {
                numbersByHandset[imei] = set = new HashSet<string>(StringComparer.Ordinal);
            }
            set.Add(msisdn);
            modelByHandset[imei] = ClickHouseJsonResult.NullIfEmpty(row, 5);
        }

        if (kind == RelatedKind.Imei && identifier.Length == 14)
        {
            numbersByHandset.TryAdd(identifier, [.. numbersByHandset.Values.SelectMany(v => v)]);
        }

        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var imei in numbersByHandset.Keys.Take(MaxPairProbes))
        {
            foreach (var neighbour in PartnerCandidates(imei))
            {
                candidates[neighbour] = imei;
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        var list = string.Join(", ", candidates.Keys.Select((_, i) => $"{{p{i}:String}}"));
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var candidate in candidates.Keys)
        {
            parameters[$"p{index++}"] = candidate;
        }

        var sql = $$"""
            SELECT b.imei, b.msisdn, t.marketingName
            FROM sqm.binding_by_imei AS b FINAL
            LEFT JOIN sqm.tac AS t ON t.tac = b.tac
            WHERE b.imei IN ({{list}})
            LIMIT 5000
            """;

        var result = await JsonQuery.ExecuteAsync(sql, parameters, ct).ConfigureAwait(false);

        var shared = new Dictionary<string, (HashSet<string> Numbers, string? Model)>(
            StringComparer.Ordinal);

        foreach (var row in result.Rows)
        {
            var candidate = ClickHouseJsonResult.Text(row, 0) ?? string.Empty;
            var msisdn = ClickHouseJsonResult.UInt64(row, 1).ToString(CultureInfo.InvariantCulture);
            var model = ClickHouseJsonResult.NullIfEmpty(row, 2);

            if (!candidates.TryGetValue(candidate, out var origin)) continue;
            if (!numbersByHandset.TryGetValue(origin, out var numbers)) continue;
            if (!numbers.Contains(msisdn)) continue;          // proximity without a shared number is not evidence

            // Both radios of one handset are the same product, and the GSMA database says so even
            // when the two IMEIs sit in different TACs.
            if (modelByHandset.TryGetValue(origin, out var originModel)
                && originModel is not null && model is not null
                && !string.Equals(originModel, model, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!shared.TryGetValue(candidate, out var entry))
            {
                shared[candidate] = entry = (new HashSet<string>(StringComparer.Ordinal), model);
            }
            entry.Numbers.Add(msisdn);
        }

        return [.. shared
            .Select(kv => new PairedHandset(kv.Key, kv.Value.Model, kv.Value.Numbers.Count))
            .OrderByDescending(p => p.SharedSubscribers)
            .ThenBy(p => p.Imei, StringComparer.Ordinal)];
    }

    /// <summary>TAC + 100 with the six-digit serial untouched, as an IMEI offset.</summary>
    private const ulong TwinTacOffset = 100_000_000;

    /// <summary>
    /// Where a handset's other radio would be, under the two schemes seen in this data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no single rule, and assuming there was made the first version of this blind to the
    /// largest vendor on the network.
    /// </para>
    /// <para>
    /// <b>Adjacent serial, same TAC</b> (+1). Xiaomi and Redmi. Measured on TAC 86453906: 4,760
    /// pairs sharing a phone number at distance 1, and 0 at distances 2, 3 and 1000.
    /// </para>
    /// <para>
    /// <b>Identical serial, twin TAC</b> (+100,000,000). Samsung. The GSMA database carries both
    /// TACs under the same marketing name - 2,737 TACs across 362 models have such a twin - and a
    /// handset's two IMEIs sit one in each. Measured on Galaxy A51, TACs 35446411 and 35446511:
    /// <b>3,290</b> pairs sharing a number at that offset, 0 at +1 and 0 at +200,000,000.
    /// </para>
    /// <para>
    /// The second scheme was found by checking the explorer against a handset whose two numbers
    /// were known to share it. The rule that missed it looked right and had been measured - on the
    /// wrong vendor. The first test agreed with the first rule, which is exactly why it held.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> PartnerCandidates(string imei)
    {
        if (imei.Length != 14 || !ulong.TryParse(imei, out var value))
        {
            yield break;
        }

        // Same TAC, neighbouring serial. Confined to the TAC because serial 000000 and 999999 sit
        // next to a different model, and a pair across that boundary is two unrelated handsets.
        var tac = imei[..8];

        if (value >= 1 && Format(value - 1) is { } before
            && before.StartsWith(tac, StringComparison.Ordinal))
        {
            yield return before;
        }

        if (Format(value + 1) is { } after && after.StartsWith(tac, StringComparison.Ordinal))
        {
            yield return after;
        }

        // Twin TAC, identical serial. Deliberately NOT confined to the TAC - crossing it is the
        // point - so the model check and the shared-subscriber check carry the weight instead.
        if (value >= TwinTacOffset && Format(value - TwinTacOffset) is { } lower)
        {
            yield return lower;
        }

        if (value <= ulong.MaxValue - TwinTacOffset
            && Format(value + TwinTacOffset) is { } upper)
        {
            yield return upper;
        }
    }

    /// <summary>A 14-digit IMEI, or null when the arithmetic left that range.</summary>
    private static string? Format(ulong value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return text.Length <= 14 ? text.PadLeft(14, '0') : null;
    }

    private static void Add(
        Dictionary<string, Accumulator> into, string value, bool active, DateOnly? confirmed,
        string? tac, string? brand, string? model, string? deviceType)
    {
        if (string.IsNullOrEmpty(value)) return;

        if (!into.TryGetValue(value, out var acc))
        {
            into[value] = acc = new Accumulator
            {
                Tac = tac, Brand = brand, MarketingName = model, DeviceType = deviceType,
            };
        }

        acc.Bindings++;
        if (active) acc.ActiveBindings++;
        if (confirmed is { } d && (acc.LastConfirmed is null || d > acc.LastConfirmed))
        {
            acc.LastConfirmed = d;
        }
    }

    private static IReadOnlyList<RelatedNode> Materialise(
        Dictionary<string, Accumulator> from, RelatedKind kind) =>
        [.. from
            .Select(kv => new RelatedNode(
                kv.Key, kind, kv.Value.Bindings, kv.Value.ActiveBindings, kv.Value.LastConfirmed,
                kv.Value.Tac, kv.Value.Brand, kv.Value.MarketingName, kv.Value.DeviceType))
            .OrderByDescending(n => n.ActiveBindings > 0)
            .ThenByDescending(n => n.LastConfirmed ?? DateOnly.MinValue)
            .ThenBy(n => n.Value, StringComparer.Ordinal)];

    private sealed class Accumulator
    {
        public int Bindings { get; set; }

        public int ActiveBindings { get; set; }

        public DateOnly? LastConfirmed { get; set; }

        public string? Tac { get; init; }

        public string? Brand { get; init; }

        public string? MarketingName { get; init; }

        public string? DeviceType { get; init; }
    }
}
