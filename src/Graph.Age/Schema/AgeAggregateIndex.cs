// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Schema;

/// <summary>Describes one declared aggregate and the physical structure that maintains it.</summary>
/// <param name="Label">The node label the aggregate belongs to.</param>
/// <param name="GroupKeyProperty">The stored property that forms the group key.</param>
/// <param name="ValueProperty">The stored numeric property that is aggregated.</param>
/// <param name="Functions">The aggregate functions that must be maintained.</param>
/// <param name="TableName">The physical aggregate table name.</param>
/// <param name="IndexTableName">The per-label index table that carries the group and value columns.</param>
/// <param name="GroupColumnName">The text column in the per-label index table that holds the group key.</param>
/// <param name="ValueColumnName">The numeric column in the per-label index table that holds the value.</param>
internal sealed record AgeAggregateIndex(
    string Label,
    string GroupKeyProperty,
    string ValueProperty,
    AggregateFunction Functions,
    string TableName,
    string IndexTableName,
    string GroupColumnName,
    string ValueColumnName);
