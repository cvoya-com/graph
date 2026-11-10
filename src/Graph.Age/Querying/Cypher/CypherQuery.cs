// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying.Cypher;

using Cvoya.Graph.Cypher.Ast;

/// <summary>
/// Represents a Cypher query with its parameters and optional transaction context.
/// </summary>
/// <param name="Text">The rendered Cypher text.</param>
/// <param name="Parameters">The parameters referenced by <paramref name="Text"/>.</param>
/// <param name="GraphPathTypes">
/// When this query is a decomposed <c>TraversePaths</c> result (one row per hop, tagged with
/// <c>pathIndex</c>/<c>hopIndex</c> columns), the source/relationship/target types needed to
/// deserialize each hop; <see langword="null"/> for all other queries.
/// </param>
/// <param name="ProjectionColumns">The exact projected column names in rendered order.</param>
/// <param name="PostProcessing">
/// Ordering, paging, and aggregate combination that AGE cannot express after a union and that the
/// SQL wrapper applies around <paramref name="Text"/>.
/// </param>
/// <param name="SumUnionCountBranches">
/// When the statement is a UNION ALL of one global <c>count(...)</c> per physical label table,
/// the provider must sum the returned branch counts into the single scalar that a LINQ
/// <c>Count()</c>/<c>LongCount()</c> expects. The branches are disjoint because an AGE vertex is
/// stored in exactly one label table.
/// </param>
internal sealed record CypherQuery(
    string Text,
    IReadOnlyDictionary<string, object?> Parameters,
    (Type Source, Type Relationship, Type Target)? GraphPathTypes = null,
    IReadOnlyList<string>? ProjectionColumns = null,
    CypherPostProcessing? PostProcessing = null,
    bool SumUnionCountBranches = false)
{
    /// <summary>
    /// Creates an empty query.
    /// </summary>
    public static CypherQuery Empty { get; } = new(
        string.Empty,
        new Dictionary<string, object?>(),
        ProjectionColumns: []);
}
