// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Cypher.Ast;

/// <summary>
/// Describes row shaping that a dialect must apply around a rendered set operation because the
/// engine cannot express it inside one Cypher statement.
/// </summary>
/// <remarks>
/// Apache AGE parses <c>ORDER BY</c>/<c>SKIP</c>/<c>LIMIT</c> after a <c>UNION</c> as part of the
/// final branch instead of the combined result, and it cannot execute <c>CALL { }</c> subqueries.
/// A dialect that expands a label hierarchy into <c>UNION ALL</c> therefore moves the global
/// ordering, paging, and aggregate combination into its execution wrapper. This metadata is the
/// provider-neutral hand-off between lowering and that wrapper.
/// </remarks>
internal sealed record CypherPostProcessing(
    IReadOnlyList<CypherPostSort> Sort,
    long? Skip,
    long? Limit,
    int ResultColumnCount,
    CypherPostAggregation? Aggregation,
    bool Distinct = false);

/// <summary>Orders the combined set-operation result by one projected column.</summary>
/// <param name="ColumnIndex">Zero-based index into the rendered projection columns.</param>
/// <param name="Descending">Whether the column is ordered descending.</param>
internal sealed record CypherPostSort(int ColumnIndex, bool Descending);

/// <summary>Combines partial aggregate rows that lowering produced per union branch.</summary>
/// <param name="GroupColumnIndexes">Projection column indexes that form the grouping key.</param>
/// <param name="Aggregates">Partial aggregate columns and the function that combines them.</param>
internal sealed record CypherPostAggregation(
    IReadOnlyList<int> GroupColumnIndexes,
    IReadOnlyList<CypherPostAggregate> Aggregates);

/// <summary>Combines one partial aggregate projection column.</summary>
/// <param name="ColumnIndex">Zero-based index into the rendered projection columns.</param>
/// <param name="Function">The combining function name, for example <c>sum</c>.</param>
internal sealed record CypherPostAggregate(int ColumnIndex, string Function);
