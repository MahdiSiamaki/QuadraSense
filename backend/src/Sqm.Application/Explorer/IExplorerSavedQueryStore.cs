using Sqm.Contracts.Explorer;

namespace Sqm.Application.Explorer;

/// <summary>A query saved under a name by its owner.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">What its owner called it.</param>
/// <param name="Description">Optional notes.</param>
/// <param name="Query">The definition, never results.</param>
/// <param name="CreatedAt">When it was first saved.</param>
/// <param name="UpdatedAt">When it last changed.</param>
public sealed record SavedExplorerQuery(
    long Id, string Name, string Description, ExplorerQueryRequest Query, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>What a save did.</summary>
public enum SavedQueryOutcome
{
    /// <summary>Saved.</summary>
    Saved,

    /// <summary>The owner already has a query by that name.</summary>
    NameTaken,

    /// <summary>No such query of this owner's.</summary>
    NotFound,
}

/// <summary>
/// My Queries. Every method takes the owner, and nothing reads or writes another person's queries
/// - private to the owner, decided by the product owner on 2026-09-30.
/// </summary>
public interface IExplorerSavedQueryStore
{
    /// <summary>The owner's queries, most recently changed first.</summary>
    Task<IReadOnlyList<SavedExplorerQuery>> ListAsync(long ownerId, CancellationToken ct);

    /// <summary>One of the owner's queries, or null.</summary>
    Task<SavedExplorerQuery?> GetAsync(long ownerId, long id, CancellationToken ct);

    /// <summary>Saves a new query.</summary>
    Task<(SavedQueryOutcome Outcome, SavedExplorerQuery? Query)> CreateAsync(
        long ownerId, string name, string description, ExplorerQueryRequest query, CancellationToken ct);

    /// <summary>Changes one of the owner's queries.</summary>
    Task<(SavedQueryOutcome Outcome, SavedExplorerQuery? Query)> UpdateAsync(
        long ownerId, long id, string name, string description, ExplorerQueryRequest query, CancellationToken ct);

    /// <summary>Deletes one of the owner's queries; false when there was none.</summary>
    Task<bool> DeleteAsync(long ownerId, long id, CancellationToken ct);
}
