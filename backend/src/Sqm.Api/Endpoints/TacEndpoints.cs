using Sqm.Application.DataImport;

namespace Sqm.Api.Endpoints;

/// <summary>TAC version review and activation.</summary>
/// <remarks>
/// Activation is separated from import for the same reason it is manual: it is the only action
/// in the platform that changes what every user sees. Uploading a TAC file is a Data Operator's
/// job; deciding that the product should believe it is an Administrator's.
/// </remarks>
public static partial class TacEndpoints
{
    [LoggerMessage(EventId = 1200, Level = LogLevel.Error,
        Message = "TAC version {VersionLabel} was activated in the operational store but the "
                  + "analytics switch failed; reverting the operational record")]
    private static partial void LogAnalyticsSwitchFailed(
        ILogger logger, string versionLabel, Exception exception);

    /// <summary>Registers the TAC routes.</summary>
    public static IEndpointRouteBuilder MapTacEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/tac-versions").WithTags("TAC");

        group.MapGet("/", ListAsync)
            .WithName("ListTacVersions")
            .WithSummary("Every TAC version, with its diff against the one it would replace.");

        group.MapPost("/{id:long}/activate", ActivateAsync)
            .WithName("ActivateTacVersion")
            .WithSummary("Makes a version the one the product resolves TACs against.");

        return app;
    }

    private static async Task<IResult> ListAsync(
        IImportJobRepository repository, CancellationToken ct)
    {
        var versions = await repository.ListTacVersionsAsync(ct).ConfigureAwait(false);
        return Results.Ok(versions);
    }

    /// <summary>
    /// Activates a version: PostgreSQL first, then the analytics store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two databases have to agree, and there is no transaction spanning them, so the order is
    /// chosen for which failure is survivable. PostgreSQL commits first; if the analytics switch
    /// then fails, the operational record is rolled back and the previous version restored, and
    /// the caller is told plainly that nothing changed.
    /// </para>
    /// <para>
    /// The opposite order — analytics first — would change the manufacturer shown on every
    /// screen before anything recorded that it had happened, and a failure would leave no trace
    /// of why the product started reporting different numbers.
    /// </para>
    /// </remarks>
    private static async Task<IResult> ActivateAsync(
        long id,
        IImportJobRepository repository,
        ITacVersionStore tacStore,
        HttpContext http,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var actor = http.User.Identity?.Name is { Length: > 0 } name ? name : "anonymous@pre-auth";
        var logger = loggerFactory.CreateLogger("Sqm.Api.TacActivation");

        TacActivationResult? activation;
        try
        {
            activation = await repository.ActivateTacVersionAsync(id, actor, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                title: "Cannot activate this version",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }

        if (activation is null)
        {
            return Results.NotFound();
        }

        try
        {
            await tacStore.ActivateAsync(activation.AnalyticsVersionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogAnalyticsSwitchFailed(logger, activation.VersionLabel, ex);

            await repository.RevertTacActivationAsync(
                activation.TacVersionId, activation.PreviousTacVersionId, actor,
                $"Analytics activation failed: {ex.Message}", CancellationToken.None)
                .ConfigureAwait(false);

            return Results.Problem(
                title: "Activation failed",
                detail: "The analytics store could not be switched to this version, so nothing "
                    + "was changed. The previously active version is still in effect.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        await repository.WriteAuditAsync(
            actor, "tac.activate.completed", null, null, activation.TacVersionId,
            http.TraceIdentifier,
            new
            {
                activated = activation.VersionLabel,
                replaced = activation.PreviousVersionLabel,
            },
            ct).ConfigureAwait(false);

        return Results.Ok(new TacActivationResponse(
            activation.TacVersionId,
            activation.VersionLabel,
            activation.PreviousVersionLabel,
            activation.PreviousVersionLabel is null
                ? $"{activation.VersionLabel} is now the active TAC version."
                : $"{activation.VersionLabel} is now active, replacing "
                  + $"{activation.PreviousVersionLabel}. Dashboard figures will reflect the new "
                  + "mapping once the marts are next refreshed."));
    }
}

/// <summary>What an activation changed.</summary>
/// <param name="TacVersionId">The version now active.</param>
/// <param name="VersionLabel">Its label.</param>
/// <param name="ReplacedVersionLabel">The version it replaced, if any.</param>
/// <param name="Message">What happened, in plain words.</param>
public sealed record TacActivationResponse(
    long TacVersionId, string VersionLabel, string? ReplacedVersionLabel, string Message);
