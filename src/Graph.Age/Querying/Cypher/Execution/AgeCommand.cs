// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying.Cypher.Execution;

using Cvoya.Graph.Cypher.Ast;

/// <summary>Carries one rendered statement and its optional typed execution plan.</summary>
/// <param name="Text">The rendered Cypher text. It never carries a plan marker.</param>
/// <param name="Parameters">The parameters referenced by <paramref name="Text"/>.</param>
/// <param name="ProjectionColumns">The exact projected column names in rendered order.</param>
/// <param name="PostProcessing">The row shaping the SQL wrapper applies around the Cypher text.</param>
/// <param name="Plan">The optional provider plan for the statement.</param>
internal sealed record AgeCommand(
    string Text,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyList<string> ProjectionColumns,
    CypherPostProcessing? PostProcessing,
    AgeExecutionPlan? Plan);
