// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph;

/// <summary>
/// Implemented by providers that can materialize the property indexes and aggregates declared with
/// a <see cref="SchemaRegistry"/> into provider-owned storage.
/// </summary>
/// <remarks>
/// Maintenance is explicit and never runs as an implicit side effect of a query or an ordinary
/// write. A provider that does not implement this interface simply keeps its ordinary behavior for
/// declared definitions. Both operations must be idempotent and safe to retry after a partial
/// failure.
/// </remarks>
public interface IGraphIndexMaintenance
{
    /// <summary>
    /// Creates or refreshes every provider structure required by the declared indexes and
    /// aggregates, and backfills existing data.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>A report describing the work this call performed.</returns>
    Task<GraphIndexMaintenanceReport> EnsureIndexesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every provider structure created by <see cref="EnsureIndexesAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>A report describing the work this call performed.</returns>
    Task<GraphIndexMaintenanceReport> DropIndexesAsync(CancellationToken cancellationToken = default);
}
