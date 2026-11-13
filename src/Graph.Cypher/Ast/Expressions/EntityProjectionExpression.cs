// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

using Cvoya.Graph.Cypher.Internal;

namespace Cvoya.Graph.Cypher.Ast.Expressions;

/// <summary>
/// Represents a node-valued expression in the provider materialization shape.
/// </summary>
public sealed record EntityProjectionExpression : CypherExpression
{
    /// <summary>
    /// Initializes a node-valued projection expression with the default complex-property depth.
    /// </summary>
    /// <remarks>
    /// This overload is kept as a distinct constructor so the existing two-argument CLR signature
    /// remains binary-compatible; the depth-taking overload below is additive.
    /// </remarks>
    /// <param name="alias">The node variable to project.</param>
    /// <param name="loadComplexProperties">Whether declared complex properties must be included.</param>
    public EntityProjectionExpression(string alias, bool loadComplexProperties)
        : this(alias, loadComplexProperties, GraphDataModel.DefaultDepthAllowed)
    {
    }

    /// <summary>Initializes a node-valued projection expression.</summary>
    /// <param name="alias">The node variable to project.</param>
    /// <param name="loadComplexProperties">Whether declared complex properties must be included.</param>
    /// <param name="complexPropertyDepth">
    /// The maximum complex-property relationship depth for the projected type. Defaults to the
    /// storage bound so callers that cannot resolve the type keep the conservative behavior.
    /// </param>
    public EntityProjectionExpression(
        string alias,
        bool loadComplexProperties,
        int complexPropertyDepth = GraphDataModel.DefaultDepthAllowed)
    {
        Alias = ArgumentValidation.RequiredName(alias, nameof(alias));
        LoadComplexProperties = loadComplexProperties;
        ArgumentOutOfRangeException.ThrowIfNegative(complexPropertyDepth);
        ComplexPropertyDepth = complexPropertyDepth;
    }

    /// <summary>Gets the node variable to project.</summary>
    public string Alias { get; }

    /// <summary>Gets whether declared complex properties must be included.</summary>
    public bool LoadComplexProperties { get; }

    /// <summary>Gets the maximum complex-property relationship depth for the projected type.</summary>
    public int ComplexPropertyDepth { get; }
}
