using Sqm.Api.Auth;
using Sqm.Api.Infrastructure;
using Sqm.Application.Dashboards;
using Sqm.Application.Identity;
using Sqm.Contracts.Dashboards;

namespace Sqm.Api.Endpoints;

/// <summary>Dashboard exports: data for an image or a printout, and CSV (ADR-016 §11).</summary>
/// <remarks>
/// Both need data.export as well as dashboard.view, and both are POSTs: the request carries the
/// definition and the lens, which never belong in a URL. Every rule lives in
/// <see cref="DashboardExportService"/>; here a request is bound and the outcome written out.
/// </remarks>
public static partial class DashboardExportEndpoints
{
    [LoggerMessage(EventId = 1400, Level = LogLevel.Error, Message = "{What}")]
    private static partial void LogWidgetFailed(ILogger logger, string what, Exception exception);

    /// <summary>Logs a widget that failed to load for an export; the export says "error" for it.</summary>
    public static void ReportFailure(ILogger logger, string what, Exception exception) => LogWidgetFailed(logger, what, exception);

    /// <summary>The header naming a CSV file's scope: visible, topN or full.</summary>
    public const string ScopeHeader = "X-Export-Scope";

    /// <summary>Registers the export routes.</summary>
    public static IEndpointRouteBuilder MapDashboardExportEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/dashboards/export").WithTags("Dashboards")
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.DashboardView)
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.DataExport);

        group.MapPost("/snapshot", SnapshotAsync)
            .WithName("PrepareDashboardSnapshot")
            .WithSummary("Each widget's data for an image or a printout, loaded for the caller with their permissions.");

        group.MapPost("/csv", CsvAsync)
            .WithName("ExportDashboardCsv")
            .WithSummary("Widgets' rows as CSV - one widget as a file, several as a ZIP - with the scope in the name.");

        return app;
    }

    private static async Task<IResult> SnapshotAsync(
        DashboardSnapshotRequest request, HttpContext http, DashboardExportService service, CancellationToken ct)
    {
        var result = await service.SnapshotAsync(CurrentUser.Require(http), request, CurrentUser.Context(http), ct).ConfigureAwait(false);
        return DashboardBuilderEndpoints.ToResult(result, Results.Ok);
    }

    private static async Task<IResult> CsvAsync(
        DashboardCsvRequest request, HttpContext http, DashboardExportService service, CancellationToken ct)
    {
        var result = await service.CsvAsync(CurrentUser.Require(http), request, CurrentUser.Context(http), ct).ConfigureAwait(false);
        return DashboardBuilderEndpoints.ToResult(result, export =>
        {
            var file = DashboardCsvWriter.Write(export);
            http.Response.Headers[ScopeHeader] = file.Scope;
            return Results.File(file.Content, file.ContentType, file.FileName);
        });
    }
}
