using System.Globalization;
using Sqm.Api.Auth;
using Sqm.Application.Abstractions;
using Sqm.Application.Identity;
using Sqm.Contracts.Lookup;
using Sqm.Domain.Identifiers;

namespace Sqm.Api.Endpoints;

/// <summary>Search by IMSI, and read one SIM's history.</summary>
/// <remarks>
/// <para>
/// POST, with the term in the body, for the same reason the subscriber lookup is a POST: a GET
/// would put a real SIM identity into web-server access logs, browser history and any
/// <c>Referer</c> the page emits. Masking cannot undo that - by the time the page is drawn the
/// identifier is already in three logs.
/// </para>
/// <para>
/// Both routes require <c>lookup.imsi</c>. Whether the identifiers come back complete or redacted
/// is decided separately, by <c>identifier.reveal</c>, and applied here rather than in the client.
/// </para>
/// </remarks>
public static class ImsiEndpoints
{
    /// <summary>Largest page the server will return, whatever the client asks for.</summary>
    /// <remarks>
    /// A ten-digit prefix matches 557,158 rows in the worst measured bucket. Without a cap, one
    /// request could ask for all of them and a page would try to render them.
    /// </remarks>
    private const int MaxPageSize = 200;

    /// <summary>Largest history window returned in one response.</summary>
    private const int MaxHistoryEvents = 500;

    /// <summary>Registers the IMSI routes.</summary>
    public static IEndpointRouteBuilder MapImsiEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/lookup/imsi")
            .WithTags("Lookup")
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.LookupImsi);

        group.MapPost("/search", SearchAsync)
            .WithName("SearchByImsi")
            .WithSummary("Bindings for a complete IMSI, or for a prefix of at least ten digits.");

        group.MapPost("/history", HistoryAsync)
            .WithName("GetImsiHistory")
            .WithSummary("Dated add and remove events for one IMSI over a date range.");

        return app;
    }

    // =======================================================================

    private static async Task<IResult> SearchAsync(
        ImsiSearchRequest request,
        HttpContext http,
        IDeviceAnalyticsStore store,
        IAuditLog audit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!ImsiQuery.TryParse(request.Imsi, out var term, out var problem))
        {
            // The message explains why, not merely that: "every IMSI here starts 43211, so a
            // shorter prefix matches the whole network" is actionable in a way that "invalid
            // input" is not.
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["imsi"] = [problem!] },
                title: "That is not a searchable IMSI");
        }

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize <= 0 ? 50 : request.PageSize, 1, MaxPageSize);

        var outcome = await store.SearchByImsiAsync(
            new ImsiSearchCriteria(
                term.Value,
                request.DeviceType,
                request.Manufacturer,
                request.ActiveOnly ?? false,
                request.From,
                request.To,
                Offset: (page - 1) * pageSize,
                Limit: pageSize),
            ct).ConfigureAwait(false);

        var user = CurrentUser.Require(http);
        var reveal = user.Can(Permissions.IdentifierReveal);

        await audit.WriteAsync(new AuditEntry(
            ActorName: user.Username,
            Action: Permissions.LookupImsi,
            Category: AuditCategory.Data,
            Outcome: AuditOutcome.Success,
            ActorUserId: user.UserId,
            TargetType: "imsi",
            Ip: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            CorrelationId: http.TraceIdentifier,
            // The IMSI itself is NOT recorded, for the same reason the subscriber lookup does not
            // record the MSISDN: an audit log full of identifiers is a second copy of the data it
            // exists to protect. How many digits were given, and how many rows came back, answer
            // every useful question about search behaviour without holding any of them.
            Detail: new Dictionary<string, object?>
            {
                ["digits"] = term.Value.DigitCount,
                ["kind"] = term.Value.IsExact ? "exact" : "prefix",
                ["results"] = outcome.Total,
                ["masked"] = !reveal,
                ["elapsedMs"] = outcome.ElapsedMs,
            }), ct).ConfigureAwait(false);

        return Results.Ok(new ImsiSearchResponse(
            Term: term.Value.Digits,
            IsExact: term.Value.IsExact,
            WellFormed: term.Value.IsWellFormed,
            Total: outcome.Total,
            Page: page,
            PageSize: pageSize,
            Identifiers: reveal ? IdentifierVisibility.Full : IdentifierVisibility.Masked,
            Items: [.. outcome.Rows.Select(row => Project(row, reveal, term.Value))],
            Summary: outcome.Facts is null ? null : new ImsiSummary(
                outcome.Facts.DistinctSubscribers,
                outcome.Facts.DistinctHandsets,
                outcome.Facts.ActiveBindings,
                outcome.Facts.FirstSeen,
                outcome.Facts.LastSeen,
                outcome.Facts.EverTouchedByDailyFile),
            Timing: new SearchTiming(outcome.ElapsedMs, outcome.RowsExamined)));
    }

    private static async Task<IResult> HistoryAsync(
        ImsiHistoryRequest request,
        HttpContext http,
        IDeviceAnalyticsStore store,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!ImsiQuery.TryParse(request.Imsi, out var term, out var problem))
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["imsi"] = [problem!] });
        }

        // History is per SIM. A prefix would mean "the history of up to 557,158 bindings", which
        // is a report, not a lookup - and it would read every partition in the range.
        if (!term.Value.IsExact)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["imsi"] = ["History needs a complete IMSI. Search by prefix first, then open one."],
            });
        }

        if (request.From is { } from && request.To is { } to && from > to)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["from"] = ["The start of the range is after its end."],
            });
        }

        var outcome = await store.GetImsiHistoryAsync(
            term.Value.Low, request.From, request.To, MaxHistoryEvents, ct).ConfigureAwait(false);

        var reveal = CurrentUser.Require(http).Can(Permissions.IdentifierReveal);

        return Results.Ok(new ImsiHistoryResponse(
            Imsi: term.Value.Digits,
            From: request.From,
            To: request.To,
            Events: [.. outcome.Events.Select(e => new ImsiHistoryEvent(
                e.Date,
                e.Sequence,
                reveal ? Digits(e.Msisdn) : IdentifierMask.Msisdn(e.Msisdn),
                reveal ? e.Imei : IdentifierMask.Imei(e.Imei),
                e.Manufacturer,
                e.MarketingName,
                e.Added))],
            Truncated: outcome.Truncated,
            Timing: new SearchTiming(outcome.ElapsedMs, outcome.RowsExamined)));
    }

    /// <summary>
    /// Projects a row for this caller, masking what they may not see.
    /// </summary>
    /// <remarks>
    /// The IMSI is masked only past the digits the caller typed. Redacting an identifier back to
    /// the person who supplied it protects nothing and makes the result unreadable; what needs
    /// masking is what the search <i>revealed</i> - the rest of the IMSI on a prefix search, and
    /// the number and handset on any search.
    /// </remarks>
    private static ImsiMatch Project(ImsiBindingRow row, bool reveal, ImsiQuery term)
    {
        var imsi = Digits(row.Imsi);

        return new ImsiMatch(
            Imsi: reveal ? imsi : IdentifierMask.EchoOfInput(term.Digits, imsi),
            Msisdn: reveal ? Digits(row.Msisdn) : IdentifierMask.Msisdn(row.Msisdn),
            Imei: reveal ? row.Imei : IdentifierMask.Imei(row.Imei),
            // The TAC is a device model, not a person. It stays visible either way: hiding it
            // would remove the device intelligence this product exists to provide, and it
            // identifies nobody.
            Tac: row.Tac,
            Manufacturer: row.Manufacturer,
            MarketingName: row.MarketingName,
            DeviceType: row.DeviceType,
            OperatingSystem: row.OperatingSystem,
            IsActive: row.IsActive,
            LastChangeDate: row.LastChangeDate);
    }

    private static string Digits(ulong value) =>
        value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Request for one IMSI's dated history.</summary>
/// <remarks>
/// <c>From</c> and <c>To</c> bound the business date inclusively. They prune partitions in the
/// event log, so a narrow window is dramatically cheaper than an open one.
/// </remarks>
public sealed record ImsiHistoryRequest(string Imsi, DateOnly? From = null, DateOnly? To = null);
