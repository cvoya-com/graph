// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Schema;

/// <summary>Describes one declared text equality property and its physical index column.</summary>
/// <param name="Label">The node label the column belongs to.</param>
/// <param name="Property">The stored property name the column mirrors.</param>
/// <param name="TableName">The physical per-label index table name.</param>
/// <param name="ColumnName">The physical text column name.</param>
/// <param name="IndexName">The B-tree index name, or <see langword="null"/> when the column needs no index.</param>
internal sealed record AgeIndexedProperty(
    string Label,
    string Property,
    string TableName,
    string ColumnName,
    string? IndexName);
