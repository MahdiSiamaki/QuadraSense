namespace Sqm.Application.Planning;

/// <summary>
/// The configuration section <c>OperatorBands</c>: the LTE and NR bands the operator's network
/// actually uses, which the band view (N2) matches the catalogue against.
/// </summary>
/// <remarks>
/// Empty by default, and on purpose: the bands are a fact about the network that only the operator
/// can supply, and a guessed list would turn every match into a claim. With either list empty the
/// view shows every band's population and says the match is not configured.
/// </remarks>
public sealed class OperatorBandsOptions
{
    /// <summary>The configuration section's name.</summary>
    public const string SectionName = "OperatorBands";

    /// <summary>LTE (E-UTRA) band numbers: 3 for band 3. Bound from <c>OperatorBands__Lte__0</c> and on.</summary>
    public IList<int> Lte { get; } = [];

    /// <summary>NR band numbers: 78 for n78. Bound from <c>OperatorBands__Nr__0</c> and on.</summary>
    public IList<int> Nr { get; } = [];

    /// <summary>The LTE bands, distinct and in order.</summary>
    public IReadOnlyList<int> LteBands => [.. Lte.Distinct().Order()];

    /// <summary>The NR bands, distinct and in order.</summary>
    public IReadOnlyList<int> NrBands => [.. Nr.Distinct().Order()];

    /// <summary>What is wrong with these settings, if anything.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Lte.Any(b => b is < 1 or > 255))
        {
            problems.Add("OperatorBands:Lte holds E-UTRA band numbers, 1 to 255.");
        }

        if (Nr.Any(b => b is < 1 or > 1024))
        {
            problems.Add("OperatorBands:Nr holds NR band numbers, 1 to 1024 (78 for n78).");
        }

        return problems;
    }
}
