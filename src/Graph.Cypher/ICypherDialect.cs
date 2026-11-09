// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;

namespace Cvoya.Graph.Cypher;

/// <summary>
/// Defines the syntax and user-visible capabilities of a Cypher implementation.
/// </summary>
public interface ICypherDialect
{
    /// <summary>Gets the dialect name used in translation diagnostics.</summary>
    string Name { get; }

    /// <summary>Gets the graph features supported by this dialect.</summary>
    CapabilitySet Capabilities { get; }

    /// <summary>
    /// Gets whether a missing-property comparison can be lowered to a native key-existence
    /// predicate instead of an <c>IS NULL</c> check on a property access.
    /// </summary>
    /// <remarks>
    /// The lowerer only applies the rewrite to a direct simple property on a typed node or
    /// relationship, and only for <c>== null</c>. Dialects that opt in must render
    /// <see cref="CypherBinaryOperator.PropertyKeyExists"/> and must guarantee that a stored null
    /// value is omitted from the property map, so key absence and a null value are equivalent.
    /// </remarks>
    bool SupportsPropertyKeyExistence => false;

    /// <summary>Renders a parameter reference.</summary>
    /// <param name="name">The parameter name without a prefix.</param>
    string RenderParameter(string name);

    /// <summary>Renders a property or stored entity-ID access.</summary>
    /// <param name="target">The already-rendered target expression.</param>
    /// <param name="property">The stored property name.</param>
    /// <param name="escape">
    /// When <see langword="true"/>, the property name is a dynamic (untrusted) identifier and is
    /// always escaped. When <see langword="false"/>, it is a compile-time data-model property
    /// identifier and is rendered byte-stably: bare when it is a plain symbolic name, escaped only
    /// when it would otherwise break out of the identifier (spaces, punctuation, backticks).
    /// </param>
    string RenderPropertyAccess(string target, string property, bool escape);

    /// <summary>Renders access to an already-encoded physical property name.</summary>
    string RenderPhysicalPropertyAccess(string target, string property) =>
        RenderPropertyAccess(target, property, escape: true);

    /// <summary>Renders logical access to a stored simple-collection property.</summary>
    string RenderCollectionPropertyAccess(string target, string property, bool escape) =>
        RenderPropertyAccess(target, property, escape);

    /// <summary>Renders membership in a stored simple collection.</summary>
    string RenderCollectionContains(string collection, string item) =>
        Capabilities.Has(GraphCapability.NullElementsInSimpleCollections)
            ? $"size([__cvoya_collection_item IN {collection} WHERE " +
                $"__cvoya_collection_item = {item} OR " +
                $"(toString(__cvoya_collection_item) IS NULL AND toString({item}) IS NULL)]) > 0"
            : $"{item} IN {collection}";

    /// <summary>
    /// Encodes a logical constant assignment as one or more atomic physical property assignments.
    /// </summary>
    IReadOnlyList<CypherStoredPropertyValue> EncodePropertyValue(
        string property,
        Type? declaredType,
        object? value) =>
        [new(property, value)];

    /// <summary>Gets the physical payload name for a logical property.</summary>
    string GetPropertyStorageName(string property) => property;

    /// <summary>Gets private companion properties that a non-collection assignment must clear.</summary>
    IReadOnlyList<string> GetPropertyCompanionStorageNames(string property) => [];

    /// <summary>Renders transaction-local native identity for a bound graph element.</summary>
    /// <param name="target">The already-rendered node or relationship expression.</param>
    string RenderNativeElementIdentity(string target);

    /// <summary>Maps a provider-neutral function name to dialect syntax.</summary>
    /// <param name="function">The provider-neutral function name.</param>
    string RenderFunctionName(string function);

    /// <summary>Gets the translation strategy for a provider-neutral function.</summary>
    /// <param name="function">The provider-neutral function name.</param>
    CypherFunctionBehavior GetFunctionBehavior(string function);

    /// <summary>Renders labels in a node pattern, without the leading colon.</summary>
    /// <param name="labels">The labels to render.</param>
    string RenderNodeLabels(IReadOnlyList<string> labels);

    /// <summary>Renders types in a relationship pattern, without the leading colon.</summary>
    /// <param name="types">The relationship types to render.</param>
    string RenderRelationshipTypes(IReadOnlyList<string> types);

    /// <summary>Renders a relationship-depth suffix, including the leading asterisk.</summary>
    /// <param name="depth">The validated depth range.</param>
    string RenderDepth(DepthRange depth);

    /// <summary>Renders a label-membership predicate.</summary>
    /// <param name="target">The already-rendered target expression.</param>
    /// <param name="labels">The labels to test.</param>
    /// <param name="renderLiteral">A renderer for dialect string literals.</param>
    string RenderLabelTest(string target, IReadOnlyList<string> labels, Func<string, string> renderLiteral);

    /// <summary>
    /// Renders a full-text search clause in this dialect's syntax, or throws
    /// <see cref="GraphQueryTranslationException"/> if the dialect does not support full-text search.
    /// </summary>
    /// <param name="clause">The full-text search clause to render.</param>
    /// <param name="context">Renderer services (expressions, literals) for the dialect to use.</param>
    /// <remarks>
    /// The default implementation declines full-text search; dialects that declare
    /// <see cref="GraphCapability.FullTextSearch"/> override it. This keeps the whole full-text
    /// scaffolding — procedure/index names and any mixed-entity subquery shape — private to the
    /// dialect rather than hard-coded in the shared renderer.
    /// </remarks>
    string RenderFullTextSearch(FullTextSearchClause clause, ICypherRenderContext context) =>
        throw new GraphQueryTranslationException(
            $"The {Name} dialect does not support capability {GraphCapability.FullTextSearch}.");

    /// <summary>Gets the stored marker property that identifies owned complex-property relationships.</summary>
    string ComplexPropertyRelationshipMarker { get; }

    /// <summary>
    /// Gets whether the shared planner must exclude marker-owned complex-value storage from
    /// ordinary roots and traversals. Dialects that perform this isolation in a lowering pass
    /// should leave the default value unchanged.
    /// </summary>
    bool RequiresPlannerComplexStorageIsolation => false;
}
