// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

using Cvoya.Graph.Cypher.Internal;

namespace Cvoya.Graph.Cypher.Ast;

/// <summary>
/// Represents the provider-neutral wire projection used to materialize a node, relationship, or path segment.
/// </summary>
public sealed record EntityProjectionClause : ICypherClause
{
    /// <summary>Initializes an entity wire projection.</summary>
    /// <param name="shape">The projection shape.</param>
    /// <param name="sourceAlias">The projected entity alias, or the source node alias for a path segment.</param>
    /// <param name="relationshipAlias">The relationship alias for a path segment.</param>
    /// <param name="targetAlias">The target node alias for a path segment.</param>
    /// <param name="loadSourceProperties">Whether declared complex properties are loaded from the source node.</param>
    /// <param name="loadTargetProperties">Whether declared complex properties are loaded from the target node.</param>
    public EntityProjectionClause(
        EntityProjectionShape shape,
        string sourceAlias,
        string? relationshipAlias,
        string? targetAlias,
        bool loadSourceProperties,
        bool loadTargetProperties)
        : this(
            shape,
            sourceAlias,
            relationshipAlias,
            targetAlias,
            loadSourceProperties,
            loadTargetProperties,
            includePathCoordinates: false,
            ordering: [],
            rowIdentityAliases: [])
    {
    }

    /// <summary>Initializes an entity wire projection.</summary>
    /// <param name="shape">The projection shape.</param>
    /// <param name="sourceAlias">The projected entity alias, or the source node alias for a path segment.</param>
    /// <param name="relationshipAlias">The relationship alias for a path segment.</param>
    /// <param name="targetAlias">The target node alias for a path segment.</param>
    /// <param name="loadSourceProperties">Whether declared complex properties are loaded from the source node.</param>
    /// <param name="loadTargetProperties">Whether declared complex properties are loaded from the target node.</param>
    /// <param name="includePathCoordinates">Whether the projection includes graph-path and hop indexes.</param>
    public EntityProjectionClause(
        EntityProjectionShape shape,
        string sourceAlias,
        string? relationshipAlias,
        string? targetAlias,
        bool loadSourceProperties,
        bool loadTargetProperties,
        bool includePathCoordinates)
        : this(
            shape,
            sourceAlias,
            relationshipAlias,
            targetAlias,
            loadSourceProperties,
            loadTargetProperties,
            includePathCoordinates,
            ordering: [],
            rowIdentityAliases: [])
    {
    }

    /// <summary>Initializes an entity wire projection.</summary>
    /// <param name="shape">The projection shape.</param>
    /// <param name="sourceAlias">The projected entity alias, or the source node alias for a path segment.</param>
    /// <param name="relationshipAlias">The relationship alias for a path segment.</param>
    /// <param name="targetAlias">The target node alias.</param>
    /// <param name="loadSourceProperties">Whether declared complex properties are loaded from the source node.</param>
    /// <param name="loadTargetProperties">Whether declared complex properties are loaded from the target node.</param>
    /// <param name="includePathCoordinates">Whether the projection includes graph-path and hop indexes.</param>
    /// <param name="ordering">The result ordering to restore after entity materialization.</param>
    public EntityProjectionClause(
        EntityProjectionShape shape,
        string sourceAlias,
        string? relationshipAlias,
        string? targetAlias,
        bool loadSourceProperties,
        bool loadTargetProperties,
        bool includePathCoordinates,
        IReadOnlyList<OrderByItem> ordering)
        : this(
            shape,
            sourceAlias,
            relationshipAlias,
            targetAlias,
            loadSourceProperties,
            loadTargetProperties,
            includePathCoordinates,
            ordering,
            rowIdentityAliases: [])
    {
    }

    /// <summary>Initializes an entity wire projection.</summary>
    /// <param name="shape">The projection shape.</param>
    /// <param name="sourceAlias">The projected entity alias, or the source node alias for a path segment.</param>
    /// <param name="relationshipAlias">The relationship alias for a path segment.</param>
    /// <param name="targetAlias">The target node alias.</param>
    /// <param name="loadSourceProperties">Whether declared complex properties are loaded from the source node.</param>
    /// <param name="loadTargetProperties">Whether declared complex properties are loaded from the target node.</param>
    /// <param name="includePathCoordinates">Whether the projection includes graph-path and hop indexes.</param>
    /// <param name="ordering">The result ordering to restore after entity materialization.</param>
    /// <param name="rowIdentityAliases">Additional aliases that distinguish input rows while node properties are loaded.</param>
    public EntityProjectionClause(
        EntityProjectionShape shape,
        string sourceAlias,
        string? relationshipAlias,
        string? targetAlias,
        bool loadSourceProperties,
        bool loadTargetProperties,
        bool includePathCoordinates,
        IReadOnlyList<OrderByItem> ordering,
        IReadOnlyList<string> rowIdentityAliases)
    {
        Shape = ArgumentValidation.DefinedEnum(shape, nameof(shape));
        SourceAlias = ArgumentValidation.RequiredName(sourceAlias, nameof(sourceAlias));
        RelationshipAlias = ArgumentValidation.OptionalName(relationshipAlias, nameof(relationshipAlias));
        TargetAlias = ArgumentValidation.OptionalName(targetAlias, nameof(targetAlias));
        LoadSourceProperties = loadSourceProperties;
        LoadTargetProperties = loadTargetProperties;
        IncludePathCoordinates = includePathCoordinates;
        Ordering = ArgumentValidation.List(ordering, nameof(ordering));
        RowIdentityAliases = ArgumentValidation.StringList(rowIdentityAliases, nameof(rowIdentityAliases));

        if (shape == EntityProjectionShape.PathSegment &&
            (RelationshipAlias is null || TargetAlias is null))
        {
            throw new ArgumentException("Path-segment projections require relationship and target aliases.", nameof(shape));
        }

        if (RowIdentityAliases.Count != RowIdentityAliases.Distinct(StringComparer.Ordinal).Count())
        {
            throw new ArgumentException("Row-identity aliases must be unique.", nameof(rowIdentityAliases));
        }

        if (RowIdentityAliases.Contains(SourceAlias, StringComparer.Ordinal) ||
            RelationshipAlias is not null && RowIdentityAliases.Contains(RelationshipAlias, StringComparer.Ordinal) ||
            TargetAlias is not null && RowIdentityAliases.Contains(TargetAlias, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Row-identity aliases must not repeat an entity projection alias.",
                nameof(rowIdentityAliases));
        }
    }

    /// <summary>Gets the projection shape.</summary>
    public EntityProjectionShape Shape { get; }

    /// <summary>Gets the projected entity alias, or the source node alias for a path segment.</summary>
    public string SourceAlias { get; }

    /// <summary>Gets the relationship alias for a path segment.</summary>
    public string? RelationshipAlias { get; }

    /// <summary>Gets the target node alias for a path segment.</summary>
    public string? TargetAlias { get; }

    /// <summary>Gets whether declared complex properties are loaded from the source node.</summary>
    public bool LoadSourceProperties { get; }

    /// <summary>Gets whether declared complex properties are loaded from the target node.</summary>
    public bool LoadTargetProperties { get; }

    /// <summary>Gets whether the projection includes graph-path and hop indexes.</summary>
    public bool IncludePathCoordinates { get; }

    /// <summary>Gets the result ordering restored after entity materialization.</summary>
    public IReadOnlyList<OrderByItem> Ordering { get; }

    /// <summary>
    /// Gets additional aliases that distinguish input rows while node properties are loaded.
    /// </summary>
    public IReadOnlyList<string> RowIdentityAliases { get; }

    /// <summary>
    /// Gets or sets the maximum complex-property relationship depth for the source node.
    /// </summary>
    /// <remarks>
    /// The planner sets this from the projected CLR type so the provider can bound its
    /// complex-property traversal instead of always using the global storage range. The default
    /// is the conservative storage bound for callers that cannot resolve the type.
    /// </remarks>
    public int SourceComplexPropertyDepth { get; init; } = GraphDataModel.DefaultDepthAllowed;

    /// <summary>
    /// Gets or sets the maximum complex-property relationship depth for the target node.
    /// </summary>
    /// <inheritdoc cref="SourceComplexPropertyDepth" />
    public int TargetComplexPropertyDepth { get; init; } = GraphDataModel.DefaultDepthAllowed;
}
