// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph;

using System.Linq.Expressions;
using System.Reflection;

/// <summary>
/// Resolves lambda property selectors to the provider-neutral label and stored property names used
/// by <see cref="PropertyIndexDefinition"/> and <see cref="PropertyAggregateDefinition"/>.
/// </summary>
internal static class IndexExpressionResolver
{
    private const string ExpectedShape =
        "Expected a single, direct instance property access such as 'entity => entity.Property'.";

    /// <summary>Resolves one type-safe node property index declaration.</summary>
    /// <typeparam name="TEntity">The node type.</typeparam>
    /// <typeparam name="TProperty">The selected property type.</typeparam>
    /// <param name="selector">The property selector.</param>
    /// <param name="kind">The access pattern.</param>
    /// <returns>The resolved declaration.</returns>
    /// <exception cref="ArgumentException">The selector is not a simple instance property.</exception>
    /// <exception cref="GraphException">The property is ignored or its schema is duplicated.</exception>
    internal static PropertyIndexDefinition Resolve<TEntity, TProperty>(
        Expression<Func<TEntity, TProperty>> selector,
        PropertyIndexKind kind)
        where TEntity : INode
    {
        var resolved = ResolveProperty(selector, nameof(selector));
        return new PropertyIndexDefinition(resolved.Label, resolved.Name, kind);
    }

    /// <summary>Resolves one type-safe node-property aggregate declaration.</summary>
    /// <typeparam name="TEntity">The node type.</typeparam>
    /// <typeparam name="TGroupKey">The selected group-key property type.</typeparam>
    /// <typeparam name="TValue">The selected aggregate value property type.</typeparam>
    /// <param name="groupKey">The group-key property selector.</param>
    /// <param name="value">The numeric aggregate value property selector.</param>
    /// <param name="functions">The aggregate functions the provider may maintain.</param>
    /// <returns>The resolved declaration.</returns>
    /// <exception cref="ArgumentException">
    /// A selector is not a simple instance property, both selectors resolve to the same stored
    /// property, the value CLR type is not numeric, or no supported function is requested.
    /// </exception>
    /// <exception cref="GraphException">A selected property is ignored or its schema is duplicated.</exception>
    internal static PropertyAggregateDefinition ResolveAggregate<TEntity, TGroupKey, TValue>(
        Expression<Func<TEntity, TGroupKey>> groupKey,
        Expression<Func<TEntity, TValue>> value,
        AggregateFunction functions)
        where TEntity : INode
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        ArgumentNullException.ThrowIfNull(value);

        var group = ResolveProperty(groupKey, nameof(groupKey));
        var aggregateValue = ResolveProperty(value, nameof(value));

        if (string.Equals(group.Name, aggregateValue.Name, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The group-key and value selectors both resolve to the stored property " +
                $"'{aggregateValue.Name}' on label '{aggregateValue.Label}'. Choose two distinct properties.",
                nameof(value));
        }

        if (!IsSupportedNumericType(aggregateValue.ClrType))
        {
            throw new ArgumentException(
                $"The value selector '{value}' resolves to '{typeof(TValue).FullName}', which is not a " +
                "supported numeric aggregate type. Use a supported numeric property such as int, long, " +
                "short, byte, float, double, or decimal (including nullable forms).",
                nameof(value));
        }

        return new PropertyAggregateDefinition(
            group.Label,
            group.Name,
            aggregateValue.Name,
            functions);
    }

    private static ResolvedProperty ResolveProperty<TEntity, TProperty>(
        Expression<Func<TEntity, TProperty>> selector,
        string parameterName)
        where TEntity : INode
    {
        ArgumentNullException.ThrowIfNull(selector);

        var parameter = selector.Parameters[0];
        if (IsIndexerAccess(selector.Body))
        {
            throw InvalidShape(
                selector,
                describedMember: Describe(selector.Body),
                reason: "it refers to an indexer",
                parameterName: parameterName);
        }

        if (selector.Body is not MemberExpression memberExpression)
        {
            throw InvalidShape(
                selector,
                describedMember: Describe(selector.Body),
                reason: "it is not a single member access",
                parameterName: parameterName);
        }

        if (memberExpression.Member is FieldInfo field)
        {
            throw InvalidShape(
                selector,
                $"{field.DeclaringType?.FullName}.{field.Name}",
                "it refers to a field, not a property",
                parameterName: parameterName);
        }

        if (memberExpression.Member is not PropertyInfo property)
        {
            throw InvalidShape(
                selector,
                $"{memberExpression.Member.DeclaringType?.FullName}.{memberExpression.Member.Name}",
                "it does not refer to a property",
                parameterName: parameterName);
        }

        if (property.GetMethod is null || property.GetMethod.IsStatic)
        {
            throw InvalidShape(
                selector,
                $"{property.DeclaringType?.FullName}.{property.Name}",
                "it refers to a static property or has no getter",
                parameterName: parameterName);
        }

        if (property.GetIndexParameters().Length > 0)
        {
            throw InvalidShape(
                selector,
                $"{property.DeclaringType?.FullName}.{property.Name}",
                "it refers to an indexer",
                parameterName: parameterName);
        }

        if (!ReferenceEquals(memberExpression.Expression, parameter))
        {
            var owner = memberExpression.Expression is MemberExpression nested
                ? $"{nested.Member.DeclaringType?.FullName}.{nested.Member.Name}"
                : Describe(memberExpression.Expression);
            throw InvalidShape(
                selector,
                $"{owner}.{property.Name}",
                "it refers to a nested member instead of a direct property of the selected type",
                parameterName: parameterName);
        }

        var schema = SchemaRegistry.DescribeEntity(typeof(TEntity));
        if (!schema.Properties.TryGetValue(property.Name, out var propertySchema))
        {
            throw new ArgumentException(
                $"The property '{property.DeclaringType?.FullName}.{property.Name}' is not a stored " +
                $"property of '{typeof(TEntity).FullName}'. {ExpectedShape}",
                parameterName);
        }

        if (propertySchema.Ignore)
        {
            throw new GraphException(
                $"The property '{property.DeclaringType?.FullName}.{property.Name}' is declared with " +
                "PropertyAttribute.Ignore and is never stored, so it cannot be declared in an " +
                "index or aggregate. " +
                ExpectedShape);
        }

        return new ResolvedProperty(schema.Label, propertySchema.Name, property.PropertyType);
    }

    private static bool IsSupportedNumericType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(byte) ||
            type == typeof(sbyte) ||
            type == typeof(short) ||
            type == typeof(ushort) ||
            type == typeof(int) ||
            type == typeof(uint) ||
            type == typeof(long) ||
            type == typeof(ulong) ||
            type == typeof(float) ||
            type == typeof(double) ||
            type == typeof(decimal);
    }

    private static bool IsIndexerAccess(Expression body) =>
        body is BinaryExpression { NodeType: ExpressionType.ArrayIndex } ||
        (body is MethodCallExpression call &&
         call.Method.IsSpecialName &&
         string.Equals(call.Method.Name, "get_Item", StringComparison.Ordinal));

    private static ArgumentException InvalidShape(
        LambdaExpression selector,
        string describedMember,
        string reason,
        string parameterName) =>
        new(
            $"The property selector '{selector}' is not supported: {reason} " +
            $"(member: {describedMember}). {ExpectedShape}",
            parameterName);

    private readonly record struct ResolvedProperty(string Label, string Name, Type ClrType);

    private static string Describe(Expression? expression) => expression switch
    {
        null => "<static>",
        MemberExpression member => $"{member.Member.DeclaringType?.FullName}.{member.Member.Name}",
        MethodCallExpression call => call.Method.Name,
        BinaryExpression binary => $"binary operator '{binary.NodeType}'",
        UnaryExpression unary => $"conversion to '{unary.Type.Name}'",
        _ => expression.GetType().Name,
    };
}
