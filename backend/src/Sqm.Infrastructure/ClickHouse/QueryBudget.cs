using System.Globalization;

namespace Sqm.Infrastructure.ClickHouse;

/// <summary>
/// What one query may spend, enforced by ClickHouse rather than by this process.
/// </summary>
/// <remarks>
/// <para>
/// Sent as settings on the request, so the server stops the query at the limit - the only place a
/// limit can be relied on. A timeout in this process abandons the response and leaves the server
/// working, which is the failure <c>cancel_http_readonly_queries_on_client_close</c> exists for.
/// </para>
/// <para>
/// Every limit throws rather than truncating (<c>*_overflow_mode = throw</c>). A result silently
/// cut at a row count reads as the whole answer, and nothing on screen would say otherwise.
/// </para>
/// </remarks>
/// <param name="MaxRowsToRead">Rows the query may read from storage, all tables together.</param>
/// <param name="MaxExecutionSeconds">Wall-clock seconds on the server.</param>
/// <param name="MaxResultRows">Rows the result may hold.</param>
internal sealed record QueryBudget(
    long? MaxRowsToRead = null,
    int? MaxExecutionSeconds = null,
    long? MaxResultRows = null)
{
    /// <summary>Adds the limits to a request's settings.</summary>
    public void WriteTo(IDictionary<string, string> settings)
    {
        if (MaxRowsToRead is { } rows)
        {
            settings["max_rows_to_read"] = rows.ToString(CultureInfo.InvariantCulture);
            settings["read_overflow_mode"] = "throw";
        }

        if (MaxExecutionSeconds is { } seconds)
        {
            settings["max_execution_time"] = seconds.ToString(CultureInfo.InvariantCulture);
            settings["timeout_overflow_mode"] = "throw";
        }

        if (MaxResultRows is { } result)
        {
            settings["max_result_rows"] = result.ToString(CultureInfo.InvariantCulture);
            settings["result_overflow_mode"] = "throw";
        }
    }
}

/// <summary>A query the server stopped at one of its limits.</summary>
/// <remarks>
/// An <see cref="InvalidOperationException"/>, as every ClickHouse failure through
/// <see cref="ClickHouseJsonQuery"/> was before it, so no existing handler changes meaning.
/// </remarks>
public sealed class QueryBudgetExceededException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public QueryBudgetExceededException()
    {
        Limit = string.Empty;
    }

    /// <summary>Creates the exception.</summary>
    public QueryBudgetExceededException(string message)
        : base(message)
    {
        Limit = string.Empty;
    }

    /// <summary>Creates the exception.</summary>
    public QueryBudgetExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
        Limit = string.Empty;
    }

    /// <summary>Creates the exception for a named limit.</summary>
    /// <param name="limit">Which limit: rows read, execution time, memory or result size.</param>
    /// <param name="code">ClickHouse's error code.</param>
    /// <param name="message">The server's own first line.</param>
    public QueryBudgetExceededException(string limit, int code, string message)
        : base(message)
    {
        Limit = limit;
        Code = code;
    }

    /// <summary>Which limit stopped the query.</summary>
    public string Limit { get; }

    /// <summary>ClickHouse's error code.</summary>
    public int Code { get; }
}

/// <summary>What the server's plan says a query would read.</summary>
/// <param name="Tables">One entry per table the query reads.</param>
internal sealed record QueryEstimate(IReadOnlyList<TableEstimate> Tables)
{
    /// <summary>Rows across every table.</summary>
    public long Rows => Tables.Sum(t => t.Rows);

    /// <summary>Granules across every table.</summary>
    public long Marks => Tables.Sum(t => t.Marks);
}

/// <summary>One table's share of a <see cref="QueryEstimate"/>.</summary>
/// <param name="Table">Database and table.</param>
/// <param name="Parts">Data parts that would be opened.</param>
/// <param name="Rows">Rows those parts' selected granules hold.</param>
/// <param name="Marks">Granules selected.</param>
internal sealed record TableEstimate(string Table, long Parts, long Rows, long Marks);
