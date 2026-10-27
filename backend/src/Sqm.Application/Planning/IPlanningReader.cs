using Sqm.Contracts.Planning;

namespace Sqm.Application.Planning;

/// <summary>The models a planning view is narrowed to, by the dashboard's own three properties of a TAC.</summary>
/// <param name="Vendor">The normalised vendor (the vendor map's canonical name, else the GSMA manufacturer).</param>
/// <param name="Model">The GSMA marketing name.</param>
/// <param name="DeviceType">The GSMA device type.</param>
public sealed record PlanningFilter(string? Vendor, string? Model, string? DeviceType)
{
    /// <summary>No filter.</summary>
    public static readonly PlanningFilter None = new(null, null, null);

    /// <summary>Whether any property is set.</summary>
    public bool Any => !string.IsNullOrWhiteSpace(Vendor) || !string.IsNullOrWhiteSpace(Model) || !string.IsNullOrWhiteSpace(DeviceType);
}

/// <summary>A page of numbers as read: the number itself, before masking.</summary>
public sealed record PlanningNumberRead(ulong Msisdn, int Reason, int Handsets, int Bindings, IReadOnlyList<string> Models);

/// <summary>Reads the published planning snapshot (analytics migration 030) and the per-model marts beside it.</summary>
/// <remarks>Every method answers null when there is no published run, or the run named is not held.</remarks>
public interface IPlanningReader
{
    /// <summary>The published runs, the one being built and the radio profiles' state.</summary>
    Task<PlanningStatusResponse> GetStatusAsync(CancellationToken ct);

    /// <summary>N1 for a run - the newest when none is named.</summary>
    Task<SunsetResponse?> GetSunsetAsync(ulong? runId, PlanningFilter filter, CancellationToken ct);

    /// <summary>N3 for a run.</summary>
    Task<EsimResponse?> GetEsimAsync(ulong? runId, PlanningFilter filter, CancellationToken ct);

    /// <summary>N5 for a run.</summary>
    Task<IotResponse?> GetIotAsync(ulong? runId, PlanningFilter filter, CancellationToken ct);

    /// <summary>N2 for a run's delivery, matched against the operator's bands.</summary>
    Task<BandsResponse?> GetBandsAsync(ulong? runId, PlanningFilter filter, OperatorBandsOptions bands, CancellationToken ct);

    /// <summary>N6 for a run's delivery.</summary>
    Task<NtnResponse?> GetNtnAsync(ulong? runId, PlanningFilter filter, CancellationToken ct);

    /// <summary>N4 for a run that still holds its rows.</summary>
    Task<LifecycleResponse?> GetLifecycleAsync(ulong? runId, PlanningFilter filter, CancellationToken ct);

    /// <summary>
    /// One class of a view (<c>sunset</c>, <c>esim</c>, <c>iot</c>, <c>bands</c> or <c>ntn</c>) broken
    /// down by <c>vendor</c>, <c>model</c>, <c>deviceType</c> or <c>tac</c>, largest first. A
    /// <c>bands</c> class names the operator's bands only through <paramref name="bands"/>.
    /// </summary>
    Task<PlanningBreakdownResponse?> GetBreakdownAsync(
        ulong? runId, string view, string planningClass, string by, PlanningFilter filter, int limit,
        OperatorBandsOptions bands, CancellationToken ct);

    /// <summary>A page of the numbers in one class, ordered by number.</summary>
    Task<(PlanningRunInfo Run, IReadOnlyList<PlanningNumberRead> Rows, long Total)?> ListNumbersAsync(
        ulong? runId, string view, string planningClass, string? reason, PlanningFilter filter, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// How many numbers a campaign list of one class would hold - the same condition as the list - or
    /// null when the run no longer holds its numbers, or the list is narrowed by model for a class whose
    /// numbers keep none (ready, and every eSIM class).
    /// </summary>
    Task<long?> CountNumbersAsync(ulong runId, string view, string planningClass, string? reason, PlanningFilter filter, CancellationToken ct);

    /// <summary>Every number of one class, in order, one at a time: a campaign list, never held whole.</summary>
    /// <returns>Numbers handed over.</returns>
    Task<long> StreamNumbersAsync(
        ulong runId, string view, string planningClass, string? reason, PlanningFilter filter,
        Func<PlanningNumberRead, ValueTask> onRow, CancellationToken ct);
}
