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
/// <b>People come first.</b> A run under way gives way while the API is reading for people, because on
/// the VM what they share is the disk: one run beside them took a chart from 789 to 3,060 ms. ClickHouse's
/// workloads cannot help, measured: they do not schedule a local disk. It counts rows read, not sessions,
/// because an open tab polls, and that is what tells the two apart: a poll reads under 6,000 rows a minute.
/// After <see cref="ImportWorkerOptions.IdleYieldLimit"/> the run goes on in one lane, so that a busy site
/// cannot starve it.
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

    /// <summary>How often to ask whether people are reading: the worker polls every five seconds.</summary>
    internal static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(30);

    private readonly ImportWorkerOptions _options = options.Value;

    private DateTimeOffset _nextRead = DateTimeOffset.MinValue;
    private int _lanes = 1;
    private int _reported;
    private bool _unreadable;

    private DateTimeOffset _nextLook = DateTimeOffset.MinValue;
    private bool _peopleReading;
    private DateTimeOffset? _givingWaySince;
    private bool _waitedLongEnough;

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

    [LoggerMessage(EventId = 3413, Level = LogLevel.Information,
        Message = "People are reading; background runs give way until they stop")]
    private partial void LogGivingWay();

    [LoggerMessage(EventId = 3414, Level = LogLevel.Information,
        Message = "Background runs have waited {Limit}; they go on in one lane beside people")]
    private partial void LogWaitedLongEnough(TimeSpan limit);

    [LoggerMessage(EventId = 3415, Level = LogLevel.Information,
        Message = "People have stopped reading; background runs carry on")]
    private partial void LogPeopleGone();

    /// <summary>The lanes to run now, none while background runs give way. Called from the claim loop alone.</summary>
    /// <param name="moreToDo">Whether a run is under way. With nothing held back there is nothing to give way.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<int> CountAsync(bool moreToDo, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        if (moreToDo && await PeopleAreReadingAsync(now, ct).ConfigureAwait(false))
        {
            if (_givingWaySince is null)
            {
                _givingWaySince = now;
                LogGivingWay();
            }

            if (now - _givingWaySince < _options.IdleYieldLimit)
            {
                return 0;
            }

            if (!_waitedLongEnough)
            {
                _waitedLongEnough = true;
                LogWaitedLongEnough(_options.IdleYieldLimit);
            }

            return 1;
        }

        if (_givingWaySince is not null)
        {
            _givingWaySince = null;
            _waitedLongEnough = false;
            LogPeopleGone();
        }

        if (_options.MaxIdleLanes <= 1)
        {
            return 1;
        }

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

    private async Task<bool> PeopleAreReadingAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_options.IdleYieldRows <= 0)
        {
            return false;
        }

        if (now < _nextLook)
        {
            return _peopleReading;
        }

        _nextLook = now + LookEvery;

        try
        {
            var rows = await capacity.ReadPeoplesRowsAsync(_options.IdleYieldWindow, ct).ConfigureAwait(false);
            _peopleReading = rows >= _options.IdleYieldRows;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing says anyone is there, and work held back on a guess could wait for ever.
            _peopleReading = false;
        }

        return _peopleReading;
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
