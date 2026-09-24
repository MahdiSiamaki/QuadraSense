using Sqm.Application.DataImport;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion.Tests;

/// <summary>
/// What "partially completed" means for a daily file: rows were rejected and are not in the data.
/// </summary>
/// <remarks>
/// Warnings used to count as well, and every daily file carries a few thousand malformed IMEIs, so
/// every day the platform imported read "partially completed" with not one row rejected. The
/// numbers in these tests are the real ones from 31 August 2026.
/// </remarks>
public sealed class ImportStatusTests
{
    [Fact]
    public void A_day_whose_every_row_landed_is_completed_however_many_warnings_it_carries() =>
        Assert.Equal(ImportJobStatus.Completed, SqmDailyProcessor.StatusFor(rejectedRows: 0, warnedRows: 4_661));

    [Fact]
    public void A_clean_day_is_completed() =>
        Assert.Equal(ImportJobStatus.Completed, SqmDailyProcessor.StatusFor(rejectedRows: 0, warnedRows: 0));

    [Fact]
    public void A_day_with_even_one_rejected_row_is_partially_completed() =>
        Assert.Equal(ImportJobStatus.PartiallyCompleted, SqmDailyProcessor.StatusFor(rejectedRows: 1, warnedRows: 0));
}
