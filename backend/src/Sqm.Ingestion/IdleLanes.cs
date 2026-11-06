using Microsoft.Extensions.Options;
using Sqm.Application.DataImport;

namespace Sqm.Ingestion;

/// <summary>How many idle tasks the worker runs at once, from what the engine has to give.</summary>
/// <remarks>
/// <para>
/// Read where the system runs, because no constant fits the laptop, the VM and OpenShift. The answer is
/// one lane whenever it cannot be known, which is how the worker has always run.
/// </para>
/// <para>
/// <b>A brake, not an engine.</b> A step that fails leaves one lane for <see cref="ImportWorkerOptions.IdleBrake"/>:
/// whatever failed it, its retries then have the machine to themselves, and a run is failed only after
/// three tries. Tasks here take minutes, so a controller would learn too slowly to be worth having
/// (ADR-021).
/// </para>
/// </remarks>
public sealed partial class IdleLanes(
    IEngineCapacityReader capacity,
    IOptions<ImportWorkerOptions> options,
    TimeProvider clock,
    ILogger<IdleLanes> logger)
{
    /// <summary>How long a reading stands. What it reads changes with a deployment, not by the minute.</summary>
    internal static readonly TimeSpan ReadEvery = TimeSpan.FromMinutes(10);

    private readonly ImportWorkerOptions _options = options.Value;

    private DateTimeOffset _nextRead = DateTimeOffset.MinValue;
    private int _lanes = 1;
    private int _reported;
    private bool _unreadable;

    /// <summary>Written from any lane, read on the claim loop.</summary>
    private long _brakedUntilTicks;

    [LoggerMessage(EventId = 3410, Level = LogLevel.Information,
        Message = "Idle work runs in {Lanes} lane(s): {MemoryBytes} bytes, {Cores} cores, {StepPeakBytes} at most in a step")]
    private partial void LogRead(int lanes, long memoryBytes, int cores, long stepPeakBytes);

    [LoggerMessage(EventId = 3411, Level = LogLevel.Warning,
        Message = "The engine's capacity could not be read; idle work runs in one lane until it can")]
    private partial void LogUnreadable(Exception exception);

    [LoggerMessage(EventId = 3412, Level = LogLevel.Warning,
        Message = "An idle step failed; one lane until {Until}")]
    private partial void LogBraked(DateTimeOffset until);

    /// <summary>The lanes to run now. Called from the claim loop alone.</summary>
    public async Task<int> CountAsync(CancellationToken ct)
    {
        if (_options.MaxIdleLanes <= 1)
        {
            return 1;
        }

        var now = clock.GetUtcNow();
        if (now >= _nextRead)
        {
            await ReadAsync(ct).ConfigureAwait(false);
            _nextRead = now + ReadEvery;
        }

        return now.UtcTicks < Volatile.Read(ref _brakedUntilTicks)
            ? 1
            : Math.Min(_lanes, _options.MaxIdleLanes);
    }

    /// <summary>A step failed.</summary>
    public void StepFailed()
    {
        var until = clock.GetUtcNow() + _options.IdleBrake;
        Volatile.Write(ref _brakedUntilTicks, until.UtcTicks);
        LogBraked(until);
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        try
        {
            var read = await capacity.ReadCapacityAsync(ct).ConfigureAwait(false);
            _lanes = read.Lanes;
            _unreadable = false;

            if (_lanes != _reported)
            {
                _reported = _lanes;
                LogRead(_lanes, read.MemoryBytes, read.Cores, read.StepPeakBytes);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lanes = 1;

            if (!_unreadable)
            {
                _unreadable = true;
                LogUnreadable(ex);
            }
        }
    }
}
