// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph;

/// <summary>Describes the work an <see cref="IGraphIndexMaintenance"/> call performed.</summary>
/// <param name="GraphName">The graph the operation ran against.</param>
/// <param name="DefinitionsInspected">The number of declared definitions the call inspected.</param>
/// <param name="StructuresCreated">The number of provider structures the call created.</param>
/// <param name="StructuresRemoved">The number of provider structures the call removed.</param>
/// <param name="RowsBackfilled">The number of data rows the call copied into provider structures.</param>
/// <remarks>
/// <para>
/// Both operations are repeatable. A second ensure reports zero created structures and still
/// refreshes derived rows; a second drop reports zero removed structures.
/// </para>
/// <para>
/// <see cref="DefinitionsInspected"/> counts declared definitions, not physical structures. A drop
/// uses the current declaration snapshot, so after the registry is cleared it reports zero
/// definitions while still removing the structures recorded in the provider catalog. The exception
/// is a graph provisioned before that catalog existed and with no current declaration: the legacy
/// fallback reports the companion tables it actually removed because there is no declaration to
/// count.
/// </para>
/// <para>
/// <see cref="StructuresCreated"/> and <see cref="StructuresRemoved"/> count tables <em>and</em>
/// missing indexes. <see cref="CreatedTableNames"/> and <see cref="RemovedTableNames"/> name only
/// the table subset: a structure count can be larger than the matching name list when an existing
/// table received a missing index. Both lists are empty for a repeat call that changes nothing.
/// </para>
/// </remarks>
public sealed record GraphIndexMaintenanceReport(
    string GraphName,
    int DefinitionsInspected,
    int StructuresCreated,
    int StructuresRemoved,
    long RowsBackfilled)
{
    /// <summary>
    /// Gets the provider table names this call actually created, in deterministic label order.
    /// </summary>
    /// <remarks>
    /// The list covers per-label companion tables and aggregate tables. It excludes indexes, so it
    /// can be shorter than <see cref="StructuresCreated"/>.
    /// </remarks>
    public IReadOnlyList<string> CreatedTableNames { get; init; } = [];

    /// <summary>
    /// Gets the provider table names this call actually removed, in deterministic catalog order.
    /// </summary>
    /// <remarks>
    /// The list covers per-label companion tables and aggregate tables. It excludes dropped
    /// indexes and the provider catalog table, so it can be shorter than
    /// <see cref="StructuresRemoved"/>.
    /// <para>
    /// The order follows the provider catalog query, which orders by structure kind before label,
    /// so aggregate tables for every label precede the companion tables. The order is stable but
    /// not label-sorted; use the name list as a set when label grouping matters.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> RemovedTableNames { get; init; } = [];
}
