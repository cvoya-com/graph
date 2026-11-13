// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

using Cvoya.Graph.Cypher.Internal;

namespace Cvoya.Graph.Cypher.Ast.Expressions;

/// <summary>
/// Represents a provider-specific type cast such as <c>id(n)::agtype</c>.
/// </summary>
/// <remarks>
/// The node is internal because only a provider lowering pass may introduce a cast: the
/// provider-neutral planner and other dialects never produce one. A dialect that renders the node
/// must accept the <c>::</c> syntax for the requested type.
/// </remarks>
internal sealed record CastExpression : CypherExpression
{
    /// <summary>Initializes a type cast around an expression.</summary>
    /// <param name="operand">The expression to cast.</param>
    /// <param name="targetType">The provider type name to cast to.</param>
    public CastExpression(CypherExpression operand, string targetType)
    {
        Operand = operand ?? throw new ArgumentNullException(nameof(operand));
        TargetType = ArgumentValidation.RequiredName(targetType, nameof(targetType));
    }

    /// <summary>Gets the expression being cast.</summary>
    public CypherExpression Operand { get; }

    /// <summary>Gets the provider type name to cast to.</summary>
    public string TargetType { get; }
}
