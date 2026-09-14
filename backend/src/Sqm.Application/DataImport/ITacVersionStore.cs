namespace Sqm.Application.DataImport;

/// <summary>
/// The versioned TAC dimension in the analytics store.
/// </summary>
/// <remarks>
/// Every version is loaded in full and kept; the "active" one is a pointer, not a copy. That
/// makes activation instant and rollback identical to activation, which is the property the
/// manual-activation workflow depends on: a version that turns out to be wrong is undone by
/// pointing at the previous one, not by reloading it.
/// </remarks>
public interface ITacVersionStore
{
    /// <summary>The next unused version number.</summary>
    Task<int> AllocateVersionIdAsync(CancellationToken ct);

    /// <summary>Which version the product is currently resolving TACs against.</summary>
    Task<int?> GetActiveVersionIdAsync(CancellationToken ct);

    /// <summary>Streams a GSMA CSV into a new version. Does not activate it.</summary>
    /// <returns>Rows written.</returns>
    Task<long> LoadVersionAsync(
        int versionId, Stream csv, Action<long>? onBytesRead, CancellationToken ct);

    /// <summary>Checks a loaded version for the defects that matter, in the store.</summary>
    /// <remarks>
    /// Done in SQL after loading rather than row by row in the worker. The file is 206 MB of
    /// quoted CSV whose columns are all strings, so there is no type to get wrong - the real
    /// questions are about the set as a whole (are TACs unique, are they 8 digits, what date
    /// does the data claim), and those are set questions, which SQL answers far better than a
    /// row loop.
    /// </remarks>
    Task<TacVersionCheck> CheckVersionAsync(int versionId, CancellationToken ct);

    /// <summary>Compares two loaded versions.</summary>
    Task<TacVersionDiff> DiffAsync(int versionId, int againstVersionId, CancellationToken ct);

    /// <summary>Points the product at a version.</summary>
    Task ActivateAsync(int versionId, CancellationToken ct);

    /// <summary>Removes a loaded version's rows. Used when a version fails its checks.</summary>
    Task DropVersionAsync(int versionId, CancellationToken ct);
}

/// <summary>What a loaded TAC version looks like on inspection.</summary>
/// <param name="RowCount">Rows in the version.</param>
/// <param name="DistinctTacs">Distinct TAC values.</param>
/// <param name="MalformedTacs">TACs that are not exactly 8 digits.</param>
/// <param name="BlankManufacturers">Rows with no manufacturer, which would show as unattributed.</param>
/// <param name="LatestUpdateDate">
/// The newest <c>lastUpdatedDate</c> in the file, as text. Corroborates the date in the file
/// name: a snapshot named September that contains nothing newer than March is the wrong file.
/// </param>
public sealed record TacVersionCheck(
    long RowCount,
    long DistinctTacs,
    long MalformedTacs,
    long BlankManufacturers,
    string? LatestUpdateDate);

/// <summary>How one TAC version differs from another.</summary>
/// <param name="Added">TACs present in the new version and not the old.</param>
/// <param name="Removed">TACs present in the old version and not the new.</param>
/// <param name="Updated">TACs in both, with at least one attribute changed.</param>
/// <param name="Unchanged">TACs identical in both.</param>
/// <param name="AffectedActiveBindings">
/// How many currently active bindings resolve to a TAC that this version adds, removes or
/// changes. The count that says whether the diff matters: 1,600 changed TACs on models nobody
/// carries is noise, and 40 changed TACs covering eight million handsets is not.
/// </param>
public sealed record TacVersionDiff(
    int Added, int Removed, int Updated, int Unchanged, long AffectedActiveBindings);
