using System.Globalization;
using ClickHouse.Client.ADO;
using Sqm.Contracts.Dashboard;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>
/// Turns a <see cref="DashboardFilter"/> into a WHERE clause plus bound parameters.
/// </summary>
/// <remarks>
/// Every user-supplied value becomes a <b>named parameter</b> (<c>{name:Type}</c> in ClickHouse syntax).
/// No value is ever concatenated into SQL. Column names come only from <see cref="AnalyticsSchema"/>,
/// which is a compile-time allow-list.
/// </remarks>
internal sealed class FilterBuilder
{
    private readonly List<string> _conditions = [];
    private readonly Dictionary<string, (object Value, string Type)> _parameters = [];

    /// <summary>Builds the filter for the current-state tables.</summary>
    public static FilterBuilder ForCurrentState(DashboardFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var b = new FilterBuilder();

        b.AddStringEquals("t.manufacturer", filter.Manufacturer, "f_manufacturer");
        b.AddStringEquals("t.deviceType", filter.DeviceType, "f_devicetype");
        b.AddStringEquals("trim(t.operatingSystem)", filter.OperatingSystem, "f_os");
        b.AddStringEquals("b.tac", filter.Tac, "f_tac");
        b.AddStringEquals(
            "coalesce(nullIf(v.vendor_canonical, ''), t.manufacturer)",
            filter.VendorCanonical,
            "f_vendor");

        if (!string.IsNullOrWhiteSpace(filter.MsisdnPrefix))
        {
            // Validated as digits before binding. A prefix match on a numeric column is expressed as a
            // range on the string form so the primary index can still be used.
            var prefix = filter.MsisdnPrefix.Trim();
            if (!IsAllDigits(prefix) || prefix.Length > DashboardFilter.MaxMsisdnPrefixDigits)
            {
                throw new ArgumentException(
                    $"MSISDN prefix must be up to {DashboardFilter.MaxMsisdnPrefixDigits} digits.", nameof(filter));
            }

            b._conditions.Add("startsWith(toString(b.msisdn), {f_msisdn_prefix:String})");
            b._parameters["f_msisdn_prefix"] = (prefix, "String");
        }

        if (!filter.IncludeUnknownDevice)
        {
            // The 000000 sentinel yields an empty materialised tac.
            b._conditions.Add("b.tac != ''");
        }

        return b;
    }

    /// <summary>Adds a bounded range on delivery sequence. Used by the event-log queries.</summary>
    public FilterBuilder WithSequenceRange(int? from, int? to)
    {
        if (from is not null)
        {
            _conditions.Add("e.seq >= {f_seq_from:Int32}");
            _parameters["f_seq_from"] = (from.Value, "Int32");
        }

        if (to is not null)
        {
            _conditions.Add("e.seq <= {f_seq_to:Int32}");
            _parameters["f_seq_to"] = (to.Value, "Int32");
        }

        return this;
    }

    private void AddStringEquals(string column, string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        _conditions.Add($"{column} = {{{parameterName}:String}}");
        _parameters[parameterName] = (value.Trim(), "String");
    }

    /// <summary>The WHERE clause, or <c>1</c> when nothing is filtered.</summary>
    public string WhereClause =>
        _conditions.Count == 0 ? "1" : string.Join(" AND ", _conditions);

    /// <summary>Binds every collected parameter onto the command.</summary>
    public void Bind(ClickHouseCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        foreach (var (name, (value, _)) in _parameters)
        {
            var p = command.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            command.Parameters.Add(p);
        }
    }

    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s)
        {
            if (c is < '0' or > '9') return false;
        }
        return true;
    }

    /// <summary>Formats a limit safely. Limits are integers we clamp, never user text.</summary>
    public static string ClampLimit(int requested, int max = 500) =>
        Math.Clamp(requested, 1, max).ToString(CultureInfo.InvariantCulture);
}
