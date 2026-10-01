namespace Sqm.Contracts.Quality;

/// <summary>Each day's file judged against the ordinary days, and what "ordinary" was.</summary>
/// <param name="Reference">The days the judgement is made against.</param>
/// <param name="Days">The requested days, oldest first.</param>
public sealed record FeedQualityResponse(FeedQualityReferenceInfo Reference, IReadOnlyList<FeedQualityDayInfo> Days);

/// <summary>The reference window and the rule applied against it.</summary>
/// <param name="From">First reference day.</param>
/// <param name="To">Last reference day.</param>
/// <param name="Days">How many reference days had been measured.</param>
/// <param name="Multiplier">A rate above this many times the reference days' median is flagged.</param>
/// <param name="MinimumRate">No rate below this is flagged, 0 to 1.</param>
public sealed record FeedQualityReferenceInfo(DateOnly From, DateOnly To, int Days, double Multiplier, double MinimumRate);

/// <summary>One day's counts and findings.</summary>
/// <param name="Date">The business date.</param>
/// <param name="Rows">Rows in the day's file.</param>
/// <param name="Sims">Distinct SIMs in it.</param>
/// <param name="UnknownDeviceRows">Rows with the 000000 sentinel.</param>
/// <param name="MalformedImeiRows">Rows whose IMEI is neither 14 digits nor the sentinel.</param>
/// <param name="UnknownTacRows">14-digit IMEIs with a TAC GSMA does not know.</param>
/// <param name="ShiftedImeiRows">Of those, IMEIs that look shifted by one digit.</param>
/// <param name="MultiNumberSims">SIMs with more than one number on the day.</param>
/// <param name="MultiNumberSimRows">Rows those SIMs carry.</param>
/// <param name="Flagged">True when any check is out of line.</param>
/// <param name="Findings">Every check, flagged or not.</param>
public sealed record FeedQualityDayInfo(
    DateOnly Date,
    long Rows,
    long Sims,
    long UnknownDeviceRows,
    long MalformedImeiRows,
    long UnknownTacRows,
    long ShiftedImeiRows,
    long MultiNumberSims,
    long MultiNumberSimRows,
    bool Flagged,
    IReadOnlyList<FeedQualityFindingInfo> Findings);

/// <summary>One check of one day.</summary>
/// <param name="Check">ShiftedImei, MultiNumberSim, MalformedImei or UnknownDevice.</param>
/// <param name="Rate">The day's rate, 0 to 1.</param>
/// <param name="Threshold">Above this the day is flagged; null without reference days.</param>
/// <param name="ReferenceMedian">The reference days' median rate.</param>
/// <param name="Flagged">True when out of line.</param>
/// <param name="Explanation">What was measured, against what.</param>
public sealed record FeedQualityFindingInfo(
    string Check, double Rate, double? Threshold, double? ReferenceMedian, bool Flagged, string Explanation);
