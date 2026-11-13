// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying.Cypher.Execution;

/// <summary>
/// A provider-internal execution plan that accompanies a lowered Cypher statement. The plan never
/// changes <c>CypherQuery.Text</c>; it travels beside the text through the typed execution pipeline
/// and is ignored when its required physical structures are absent.
/// </summary>
internal abstract record AgeExecutionPlan;

/// <summary>
/// A direct SQL plan that answers a shape the AGE Cypher path cannot accelerate.
/// </summary>
/// <param name="Labels">The physical vertex label tables the plan reads.</param>
/// <param name="Sql">
/// The SQL text. It uses <see cref="Cvoya.Graph.Age.Schema.AgeIndexTable.GraphToken"/> for the quoted
/// graph schema and <see cref="Cvoya.Graph.Age.Schema.AgeIndexTable.ParameterToken"/> for the agtype
/// parameter.
/// </param>
/// <param name="Kind">A short diagnostic label such as <c>group</c>, <c>distinct</c>, or <c>filter</c>.</param>
/// <param name="RequiredTableNames">The physical relations whose presence the plan requires.</param>
internal sealed record AgeSqlPlan(
    IReadOnlyList<string> Labels,
    string Sql,
    string Kind,
    IReadOnlyList<string> RequiredTableNames) : AgeExecutionPlan;

/// <summary>
/// A companion table and text column that can answer the ordering of one paged label from its
/// declared equality B-tree instead of the unindexed AGE property accessor.
/// </summary>
/// <param name="Label">The physical vertex label table the source belongs to.</param>
/// <param name="TableName">The companion table name, relative to the graph schema.</param>
/// <param name="ColumnName">The text column that mirrors the sort property.</param>
internal sealed record AgePageSource(
    string Label,
    string TableName,
    string ColumnName);

/// <summary>
/// A plan that selects the final page of a complex-property query before the variable-length
/// complex-property load runs.
/// </summary>
/// <param name="Labels">The physical vertex label tables the query reads.</param>
/// <param name="Alias">The root variable alias used by the rendered Cypher.</param>
/// <param name="SortProperty">The stored scalar property the final page is ordered by.</param>
/// <param name="Descending">Whether the page is ordered descending.</param>
/// <param name="Skip">The global offset of the final page.</param>
/// <param name="Limit">The global size of the final page.</param>
/// <param name="Sources">
/// One companion source per label when every label declares both equality and ordering for
/// <paramref name="SortProperty"/> and the property is a string; empty when the pre-query must keep
/// the AGE accessor ordering.
/// </param>
/// <param name="ClauseLevelPaging">
/// Whether <c>SKIP</c>/<c>LIMIT</c> are still clauses in the rendered query instead of lifted into
/// post-processing. The runner must remove those clauses after injecting the selected page ids.
/// </param>
internal sealed record AgePageSelectionPlan(
    IReadOnlyList<string> Labels,
    string Alias,
    string SortProperty,
    bool Descending,
    long Skip,
    long Limit,
    IReadOnlyList<AgePageSource> Sources,
    bool ClauseLevelPaging = false) : AgeExecutionPlan;
