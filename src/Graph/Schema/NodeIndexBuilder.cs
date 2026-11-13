// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph;

using System.Linq.Expressions;

/// <summary>
/// Fluent, type-safe property-index and aggregate declarations for one node type.
/// </summary>
/// <typeparam name="TNode">The node type whose label and stored property names are resolved.</typeparam>
/// <remarks>
/// The label comes from <see cref="NodeAttribute"/> (or the type-name fallback) and the stored
/// property name from <see cref="PropertyAttribute.Label"/>, using the same schema metadata as
/// <see cref="SchemaRegistry.AddPropertyIndex(PropertyIndexDefinition)"/> and
/// <see cref="SchemaRegistry.AddPropertyAggregate(PropertyAggregateDefinition)"/>. A builder is
/// created through <see cref="SchemaRegistry.Node{TNode}"/>; every declaration is added to that
/// registry.
/// </remarks>
/// <example>
/// <code>
/// var registry = new SchemaRegistry();
/// registry.Node&lt;Contract&gt;()
///     .AddPropertyIndex(contract =&gt; contract.Status)
///     .AddPropertyIndex(contract =&gt; contract.Name, PropertyIndexKind.Ordering)
///     .AddPropertyAggregate(
///         contract =&gt; contract.Status,
///         contract =&gt; contract.Amount,
///         AggregateFunction.Count | AggregateFunction.Sum);
/// </code>
/// </example>
public sealed class NodeIndexBuilder<TNode>
    where TNode : INode
{
    private readonly SchemaRegistry registry;

    internal NodeIndexBuilder(SchemaRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        this.registry = registry;
    }

    /// <summary>Registers a property-index declaration for this node type.</summary>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <param name="property">A simple instance property selector, such as <c>c =&gt; c.Status</c>.</param>
    /// <param name="kind">
    /// The access pattern. <see cref="PropertyIndexKind.Equality"/> is the default.
    /// </param>
    /// <returns>This builder, so declarations can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="property"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The selector is not a single, direct instance property of <typeparamref name="TNode"/>.
    /// </exception>
    /// <exception cref="GraphException">
    /// The selected property is ignored or the node type has a duplicate property label.
    /// </exception>
    public NodeIndexBuilder<TNode> AddPropertyIndex<TProperty>(
        Expression<Func<TNode, TProperty>> property,
        PropertyIndexKind kind = PropertyIndexKind.Equality)
    {
        registry.AddPropertyIndex(IndexExpressionResolver.Resolve(property, kind));
        return this;
    }

    /// <summary>Registers a typed aggregate declaration for this node type.</summary>
    /// <typeparam name="TGroupKey">The group-key property type.</typeparam>
    /// <typeparam name="TValue">The numeric aggregate value property type.</typeparam>
    /// <param name="groupKey">
    /// A simple instance property selector for the group key, such as <c>c =&gt; c.Status</c>.
    /// </param>
    /// <param name="value">
    /// A simple instance property selector for the numeric value, such as <c>c =&gt; c.Amount</c>.
    /// </param>
    /// <param name="functions">The aggregate functions the provider may maintain.</param>
    /// <returns>This builder, so declarations can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="groupKey"/> or <paramref name="value"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A selector is not a single, direct instance property of <typeparamref name="TNode"/>; both
    /// selectors resolve to the same stored property; <typeparamref name="TValue"/> is not a
    /// supported numeric type; or the function mask contains no supported function.
    /// </exception>
    /// <exception cref="GraphException">
    /// A selected property is ignored, has a duplicate label, or the same declaration conflicts with
    /// schema metadata.
    /// </exception>
    /// <remarks>
    /// Both stored names are resolved from the same <see cref="NodeAttribute"/> and
    /// <see cref="PropertyAttribute.Label"/> metadata as <see cref="AddPropertyIndex{TProperty}"/>.
    /// The value type is validated at declaration time as <c>byte</c>, <c>sbyte</c>, <c>short</c>,
    /// <c>ushort</c>, <c>int</c>, <c>uint</c>, <c>long</c>, <c>ulong</c>, <c>float</c>,
    /// <c>double</c>, or <c>decimal</c>, including their nullable forms. The provider still owns
    /// maintenance-time numeric validation. Repeating the same resolved
    /// <c>(label, groupKey, value)</c> declaration merges the function flags additively and never
    /// duplicates storage.
    /// </remarks>
    public NodeIndexBuilder<TNode> AddPropertyAggregate<TGroupKey, TValue>(
        Expression<Func<TNode, TGroupKey>> groupKey,
        Expression<Func<TNode, TValue>> value,
        AggregateFunction functions)
    {
        registry.AddPropertyAggregate(
            IndexExpressionResolver.ResolveAggregate(groupKey, value, functions));
        return this;
    }
}
