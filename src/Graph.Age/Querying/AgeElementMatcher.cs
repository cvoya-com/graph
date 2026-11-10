// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying;

using Cvoya.Graph.Age.Entities;
using Cvoya.Graph.Age.Serialization;
using Cvoya.Graph.Cypher.Ast.Expressions;

/// <summary>
/// Defines AGE's native logical label/type policy and marker-protected complex-value matching.
/// </summary>
internal static class AgeElementMatcher
{
    internal const string InheritanceLabelsProperty = "inheritance_labels";

    internal static CypherExpression NodePredicate(
        CypherExpression target,
        IReadOnlyList<string> labels) => new BinaryExpression(
            CypherBinaryOperator.And,
            HierarchyNodePredicate(target),
            new BinaryExpression(
                CypherBinaryOperator.Or,
                // Native rows carry the label directly and need no CVOYA metadata; only rows with
                // multiple logical labels store inheritance_labels.
                Alternatives(
                    labels,
                    label => new BinaryExpression(
                        CypherBinaryOperator.In,
                        new Literal(label),
                        new FunctionCall("labels", [target]))),
                Alternatives(labels, label => HierarchyPredicate(target, label))));

    internal static CypherExpression RelationshipPredicate(
        CypherExpression target,
        IReadOnlyList<string> types) => Alternatives(types, type =>
        {
            var storageType = SerializationBridge.GetRootStorageName(type, relationship: true);
            CypherExpression physicalType = new BinaryExpression(
                CypherBinaryOperator.Equal,
                new FunctionCall("type", [target]),
                new Literal(storageType));
            if (SerializationBridge.IsEncodedRootStorageName(storageType, relationship: true))
            {
                physicalType = new BinaryExpression(
                    CypherBinaryOperator.And,
                    physicalType,
                    HierarchyPredicate(target, type));
            }

            return new BinaryExpression(
                CypherBinaryOperator.Or,
                physicalType,
                new BinaryExpression(
                    CypherBinaryOperator.And,
                    ComplexRelationshipPredicate(target),
                    HierarchyPredicate(target, type)));
        });

    internal static string NodePredicate(string alias, string parameterName) =>
        $"(size([age_label IN labels({alias}) WHERE age_label = {parameterName}]) > 0 OR " +
        $"((NOT '{SerializationBridge.ComplexNodeLabel}' IN labels({alias}) OR " +
        $"coalesce({alias}.{ComplexPropertyStorage.NodeMarkerProperty}, false) = true) AND " +
        $"{RenderHierarchyPredicate(alias, parameterName)}))";

    internal static string RelationshipPredicate(string alias, string parameterName) =>
        $"(type({alias}) = {parameterName} OR " +
        $"{RenderHierarchyPredicate(alias, parameterName)})";

    internal static string UserRootMatch(string alias, string properties = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        var ownerRelationship = $"{alias}_cvoya_owner_relationship";
        var ownerCount = $"{alias}_cvoya_owner_count";
        return $"""
            MATCH ({alias}{properties})
            OPTIONAL MATCH ()-[{ownerRelationship}]->({alias})
            WITH {alias}, count(CASE WHEN coalesce({ownerRelationship}.{ComplexPropertyStorage.RelationshipMarkerProperty}, false) = true THEN {ownerRelationship} ELSE null END) AS {ownerCount}
            WHERE {ownerCount} = 0
              AND NOT '{SerializationBridge.ComplexNodeLabel}' IN labels({alias})
            """;
    }

    internal static string UserRelationshipPredicate(string alias) =>
        $"coalesce({alias}.{ComplexPropertyStorage.RelationshipMarkerProperty}, false) = false AND " +
        $"type({alias}) <> '{SerializationBridge.ComplexRelationshipType}'";

    private static BinaryExpression HierarchyNodePredicate(CypherExpression target) =>
        new(
            CypherBinaryOperator.Or,
            new UnaryExpression(
                CypherUnaryOperator.Not,
                new BinaryExpression(
                    CypherBinaryOperator.In,
                    new Literal(SerializationBridge.ComplexNodeLabel),
                    new FunctionCall("labels", [target]))),
            new BinaryExpression(
                CypherBinaryOperator.Equal,
                new FunctionCall(
                    "coalesce",
                    [
                        new PropertyAccess(target, ComplexPropertyStorage.NodeMarkerProperty),
                        new Literal(false),
                    ]),
                new Literal(true)));

    private static BinaryExpression HierarchyPredicate(CypherExpression target, string label) =>
        new(
            CypherBinaryOperator.PropertyContainment,
            new FunctionCall("properties", [target]),
            new MapExpression(
            [
                new MapEntry(
                    InheritanceLabelsProperty,
                    new ListExpression([new Literal(label)])),
            ]));

    private static string RenderHierarchyPredicate(string target, string valueExpression) =>
        $"properties({target}) @> {{ {InheritanceLabelsProperty}: [{valueExpression}] }}";

    private static BinaryExpression ComplexRelationshipPredicate(CypherExpression target) =>
        new BinaryExpression(
            CypherBinaryOperator.Equal,
            new FunctionCall(
                "coalesce",
                [
                    new PropertyAccess(target, ComplexPropertyStorage.RelationshipMarkerProperty),
                    new Literal(false),
                ]),
            new Literal(true));

    private static CypherExpression Alternatives(
        IReadOnlyList<string> names,
        Func<string, CypherExpression> build)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            throw new ArgumentException("At least one logical label or type is required.", nameof(names));
        }

        return names.Select(build).Aggregate((left, right) =>
            new BinaryExpression(CypherBinaryOperator.Or, left, right));
    }
}
