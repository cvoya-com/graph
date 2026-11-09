// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Entities;

using Cvoya.Graph.Age.Core;
using Cvoya.Graph.Age.Querying.Cypher;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.Age.Serialization;
using Cvoya.Graph.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;


/// <summary>
/// Handles the creation and management of complex properties as separate nodes in the graph.
/// </summary>
internal sealed class ComplexPropertyManager(AgeGraphContext context)
{
    private readonly ILogger<ComplexPropertyManager> logger = context.LoggerFactory?.CreateLogger<ComplexPropertyManager>()
        ?? NullLogger<ComplexPropertyManager>.Instance;

    /// <summary>
    /// A value node awaiting creation: the serialized entity plus the relationship type and
    /// parameter maps needed to create it under its parent.
    /// </summary>
    private sealed record PendingValueNode(
        string ParentElementId,
        string RelationshipType,
        EntityInfo Entity,
        Dictionary<string, object?> NodeProperties,
        Dictionary<string, object?> RelationshipProperties);

    /// <summary>
    /// A transiently-addressed value node used by subgraph batching. Correlation tokens are
    /// operation-local plumbing and are kept separate from the serialized property maps. The parent
    /// is addressed by its token plus its storage label so a batch command can only ever bind
    /// within the label scope it planned for.
    /// </summary>
    internal sealed record SubgraphValueNode(
        string ParentCorrelationToken,
        string ParentStorageLabel,
        string CorrelationToken,
        EntityInfo Entity,
        Dictionary<string, object?> NodeProperties,
        Dictionary<string, object?> RelationshipProperties);

    public async Task CreateComplexPropertiesAsync(
        AgeQueryRunner transaction,
        string parentId,
        EntityInfo entity,
        CancellationToken cancellationToken = default)
    {
        await CreateComplexPropertiesCoreAsync(
            transaction,
            [(parentId, entity)],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces selected owned subtrees below frozen AGE graphids without persisting public or
    /// transient identity properties on the new value nodes or marker relationships.
    /// </summary>
    internal async Task ReplaceGraphIdBoundComplexPropertiesAsync(
        AgeQueryRunner transaction,
        IReadOnlyList<long> parentGraphIds,
        IReadOnlyList<string> relationshipTypesToClear,
        IReadOnlyList<string> propertyNamesToClear,
        EntityInfo replacementEntity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(parentGraphIds);
        ArgumentNullException.ThrowIfNull(relationshipTypesToClear);
        ArgumentNullException.ThrowIfNull(propertyNamesToClear);
        ArgumentNullException.ThrowIfNull(replacementEntity);
        if (parentGraphIds.Count == 0 || relationshipTypesToClear.Count == 0)
        {
            return;
        }

        var orderedGraphIds = parentGraphIds.Distinct().Order().ToArray();
        await ReplaceComplexCollectionMetadataAsync(
            transaction,
            orderedGraphIds,
            propertyNamesToClear,
            replacementEntity,
            cancellationToken).ConfigureAwait(false);
        await DeleteSelectedComplexPropertiesAsync(
            transaction,
            orderedGraphIds,
            relationshipTypesToClear,
            cancellationToken).ConfigureAwait(false);

        await CreateComplexPropertiesCoreAsync(
            transaction,
            [.. orderedGraphIds.Select(graphId => (graphId.ToString(System.Globalization.CultureInfo.InvariantCulture), replacementEntity))],
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceComplexCollectionMetadataAsync(
        AgeQueryRunner transaction,
        IReadOnlyList<long> parentGraphIds,
        IReadOnlyList<string> propertyNamesToClear,
        EntityInfo replacementEntity,
        CancellationToken cancellationToken)
    {
        var companions = propertyNamesToClear
            .SelectMany(ComplexCollectionStorageCodec.GetCompanionPropertyNames)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var metadata = ComplexCollectionStorageCodec.EncodeProperties(
            replacementEntity.ComplexProperties,
            SerializationBridge.ToAgeValue);
        if (companions.Length == 0 && metadata.Count == 0)
        {
            return;
        }

        var clauses = new List<string>();
        if (companions.Length > 0)
        {
            clauses.Add("REMOVE " + string.Join(", ", companions.Select(name =>
                $"parent.{CypherIdentifier.Escape(name, "complex-collection companion name")}")));
        }

        var parameters = new Dictionary<string, object?>
        {
            ["parentGraphIds"] = parentGraphIds,
        };
        if (metadata.Count > 0)
        {
            var assignments = metadata.Select((item, index) =>
            {
                var parameterName = $"complexCollectionMetadata{index}";
                parameters[parameterName] = item.Value;
                return $"parent.{CypherIdentifier.Escape(item.Key, "complex-collection companion name")} = ${parameterName}";
            });
            clauses.Add("SET " + string.Join(", ", assignments));
        }

        var result = await transaction.RunAsync(
            $"MATCH (parent) WHERE size([parentGraphId IN $parentGraphIds WHERE id(parent) = parentGraphId]) > 0 " +
            $"{string.Join(" ", clauses)} RETURN count(parent) AS affectedCount",
            parameters,
            cancellationToken).ConfigureAwait(false);
        _ = await result.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateComplexPropertiesCoreAsync(
        AgeQueryRunner transaction,
        List<(string ParentElementId, EntityInfo Entity)> roots,
        CancellationToken cancellationToken)
    {
        // Breadth-first: create all value nodes of one nesting level with a single UNWIND
        // statement, instead of one statement per node.
        var currentLevel = roots;

        for (var depth = 0; currentLevel.Count > 0; depth++)
        {
            var pending = CollectPendingValueNodes(currentLevel, depth);
            currentLevel = await CreateLevelAsync(transaction, pending, cancellationToken).ConfigureAwait(false);
        }
    }

    internal IReadOnlyList<IReadOnlyList<SubgraphValueNode>> PlanSubgraphValueNodes(
        IReadOnlyList<(string CorrelationToken, string StorageLabel, EntityInfo Entity)> roots,
        Func<string> createCorrelationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(createCorrelationToken);

        var levels = new List<IReadOnlyList<SubgraphValueNode>>();
        var currentLevel = roots
            .Select(root => (
                ParentCorrelationToken: root.CorrelationToken,
                ParentStorageLabel: root.StorageLabel,
                root.Entity))
            .ToList();

        for (var depth = 0; currentLevel.Count > 0; depth++)
        {
            var pending = new List<SubgraphValueNode>();
            foreach (var (parentCorrelationToken, parentStorageLabel, entity) in currentLevel)
            {
                var children = CollectPendingValueNodes([(parentCorrelationToken, entity)], depth);
                pending.AddRange(children.Select(child => new SubgraphValueNode(
                    child.ParentElementId,
                    parentStorageLabel,
                    createCorrelationToken(),
                    child.Entity,
                    child.NodeProperties,
                    child.RelationshipProperties)));
            }

            if (pending.Count == 0)
            {
                break;
            }

            levels.Add(pending);

            // Every value node is created under the shared physical value-node label, so the next
            // level's parent scope is that label rather than the root's storage label.
            currentLevel = pending.Select(child => (
                ParentCorrelationToken: child.CorrelationToken,
                ParentStorageLabel: SerializationBridge.ComplexNodeLabel,
                child.Entity)).ToList();
        }

        return levels;
    }

    private List<PendingValueNode> CollectPendingValueNodes(
        List<(string ParentElementId, EntityInfo Entity)> level,
        int depth)
    {
        var pending = new List<PendingValueNode>();

        foreach (var (parentElementId, entity) in level)
        {
            if (entity.ComplexProperties.Count == 0)
                continue;

            if (depth >= GraphDataModel.DefaultDepthAllowed &&
                entity.ComplexProperties.Values.Any(property => property.Value is not null))
            {
                throw new GraphException(
                    $"Complex properties cannot exceed {GraphDataModel.DefaultDepthAllowed} levels of depth.");
            }

            foreach (var (propertyName, complexProperty) in entity.ComplexProperties)
            {
                switch (complexProperty.Value)
                {
                    case EntityInfo childEntity:
                        pending.Add(CreatePendingValueNode(
                            parentElementId,
                            propertyName,
                            complexProperty,
                            childEntity,
                            sequenceNumber: 0));
                        break;

                    case EntityCollection collection:
                        var index = 0;
                        foreach (var item in collection.Entities)
                        {
                            if (item is not null)
                            {
                                pending.Add(CreatePendingValueNode(
                                    parentElementId,
                                    propertyName,
                                    complexProperty,
                                    item,
                                    index));
                            }

                            index++;
                        }
                        break;

                    case null:
                        logger.LogDebugComplexPropertyManager88(propertyName);
                        continue;

                    default:
                        logger.LogWarningComplexPropertyManager92(complexProperty.Value.GetType().Name, propertyName);
                        throw new GraphException(
                            $"Unsupported complex property type: {complexProperty.Value.GetType().Name} for property {propertyName}");
                }
            }
        }

        return pending;
    }

    private static PendingValueNode CreatePendingValueNode(
        string parentElementId,
        string propertyName,
        Property property,
        EntityInfo entity,
        int sequenceNumber)
    {
        var relationshipType = property.RelationshipType ?? (property.PropertyInfo is null
            ? GraphDataModel.PropertyNameToRelationshipTypeName(propertyName)
            : GraphDataModel.GetComplexPropertyRelationshipType(property.PropertyInfo));

        var nodeProps = SerializationHelpers.SerializeSimpleProperties(entity);
        nodeProps[ComplexPropertyStorage.NodeMarkerProperty] = true;
        nodeProps["inheritance_labels"] = entity.ActualLabels.Count > 0
            ? entity.ActualLabels
            : [entity.Label];
        var relProps = new Dictionary<string, object?>
        {
            ["SequenceNumber"] = sequenceNumber,
            [ComplexPropertyStorage.RelationshipMarkerProperty] = true,
            ["inheritance_labels"] = new[] { relationshipType },
        };
        return new PendingValueNode(parentElementId, relationshipType, entity, nodeProps, relProps);
    }

    /// <summary>
    /// Creates every pending value node of one nesting level with one UNWIND statement and returns
    /// the created nodes paired with their entities so the next level can be created under them.
    /// </summary>
    private async Task<List<(string ParentElementId, EntityInfo Entity)>> CreateLevelAsync(
        AgeQueryRunner transaction,
        List<PendingValueNode> pending,
        CancellationToken cancellationToken)
    {
        var nextLevel = new List<(string ParentElementId, EntityInfo Entity)>(pending.Count);

        if (pending.Count == 0)
        {
            return nextLevel;
        }

        var nodePropertyNames = pending
            .SelectMany(node => node.NodeProperties.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var relationshipPropertyNames = pending
            .SelectMany(node => node.RelationshipProperties.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var rows = pending.Select((node, rowId) => new Dictionary<string, object?>
        {
            ["rowId"] = rowId,
            ["parentId"] = node.ParentElementId,
            ["nodeProperties"] = ExpandProperties(node.NodeProperties, nodePropertyNames),
            ["relationshipProperties"] = ExpandProperties(
                node.RelationshipProperties,
                relationshipPropertyNames),
        }).ToList();

        var nodeSet = BuildSetClauseFromRowMap("complex", "row.nodeProperties", nodePropertyNames);
        var relationshipSet = BuildSetClauseFromRowMap(
            "r",
            "row.relationshipProperties",
            relationshipPropertyNames);

        var cypher = @$"
            UNWIND $rows AS row
            MATCH (parent)
            WHERE id(parent) = toInteger(row.parentId)
            CREATE (parent)-[r:{SerializationBridge.ComplexRelationshipType}]->(complex:{SerializationBridge.ComplexNodeLabel})
            {relationshipSet}
            {nodeSet}
            RETURN row.rowId AS rowId, id(complex) AS nodeId";

        var result = await transaction.RunAsync(
            cypher,
            new Dictionary<string, object?> { ["rows"] = rows },
            cancellationToken).ConfigureAwait(false);
        var records = await result.ToListAsync(cancellationToken).ConfigureAwait(false);

        if (records.Count != pending.Count)
        {
            throw new GraphException("Failed to create complex property node");
        }

        foreach (var record in records)
        {
            var rowId = record["rowId"].As<int>();
            var complexNodeId = record["nodeId"].As<string>()
                ?? throw new GraphException("Failed to create complex property node");
            nextLevel.Add((complexNodeId, pending[rowId].Entity));
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebugComplexPropertyManager202(
                pending.Count,
                pending.Select(node => node.RelationshipType).Distinct(StringComparer.Ordinal).Count());
        }

        return nextLevel;
    }

    internal static Dictionary<string, object?> ExpandProperties(
        Dictionary<string, object?> properties,
        IReadOnlyList<string> propertyNames)
    {
        return propertyNames.ToDictionary(
            propertyName => propertyName,
            propertyName => properties.TryGetValue(propertyName, out var value) ? value : null,
            StringComparer.Ordinal);
    }

    internal static string BuildSetClauseFromRowMap(
        string alias,
        string rowMap,
        IReadOnlyList<string> propertyNames)
    {
        var assignments = propertyNames.Select(propertyName =>
        {
            var escapedPropertyName = CypherIdentifier.Escape(propertyName, "property name");
            return $"{alias}.{escapedPropertyName} = {rowMap}.{escapedPropertyName}";
        });

        return $"SET {string.Join(", ", assignments)}";
    }

    public async Task UpdateComplexPropertiesAsync(
        AgeQueryRunner transaction,
        string parentId,
        EntityInfo entity,
        CancellationToken cancellationToken = default)
    {
        // First, delete all existing complex property relationships
        await DeleteExistingComplexPropertiesAsync(transaction, parentId, cancellationToken).ConfigureAwait(false);

        // Then create the new ones
        await CreateComplexPropertiesAsync(transaction, parentId, entity, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteExistingComplexPropertiesAsync(
        AgeQueryRunner transaction,
        string parentId,
        CancellationToken cancellationToken)
    {
        // AGE 1.7 lacks ALL(...); the index-based comprehension keeps Neo4j's semantics: every
        // relationship in the path must be a complex-property relationship.
        var findCypher = $@"
            MATCH (parent)
            WHERE id(parent) = toInteger($parentId)
            MATCH (parent)-[propertyRels*1..{GraphDataModel.DefaultDepthAllowed}]->(propertyNode)
            WHERE size([age_hop IN range(0, size(propertyRels) - 1) WHERE propertyRels[toInteger(age_hop)].{ComplexPropertyStorage.RelationshipMarkerProperty} = true]) = size(propertyRels)
            RETURN DISTINCT id(propertyNode) AS propertyNodeId";

        var result = await transaction.RunAsync(findCypher, new { parentId }, cancellationToken).ConfigureAwait(false);
        var propertyNodes = await result.ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var propertyNode in propertyNodes)
        {
            await transaction.RunAsync(
                "MATCH (propertyNode) WHERE id(propertyNode) = toInteger($propertyNodeId) DETACH DELETE propertyNode RETURN true AS deleted",
                new { propertyNodeId = propertyNode["propertyNodeId"].As<string>() }, cancellationToken).ConfigureAwait(false);
        }

        logger.LogDebugComplexPropertyManager270(propertyNodes.Count, parentId);
    }

    private async Task DeleteSelectedComplexPropertiesAsync(
        AgeQueryRunner transaction,
        IReadOnlyList<long> parentGraphIds,
        IReadOnlyList<string> relationshipTypes,
        CancellationToken cancellationToken)
    {
        var findCypher = $@"
            MATCH (parent)-[propertyRels*1..{GraphDataModel.DefaultDepthAllowed}]->(propertyNode)
            WHERE size([parentGraphId IN $parentGraphIds WHERE id(parent) = parentGraphId]) > 0
              AND size([age_hop IN range(0, size(propertyRels) - 1)
                        WHERE propertyRels[toInteger(age_hop)].{ComplexPropertyStorage.RelationshipMarkerProperty} = true]) = size(propertyRels)
              AND propertyRels[toInteger(0)].inheritance_labels[toInteger(0)] IN $relationshipTypes
            RETURN DISTINCT id(propertyNode) AS propertyNodeId";

        var result = await transaction.RunAsync(
            findCypher,
            new Dictionary<string, object?>
            {
                ["parentGraphIds"] = parentGraphIds,
                ["relationshipTypes"] = relationshipTypes,
            },
            cancellationToken).ConfigureAwait(false);
        var propertyNodes = await result.ToListAsync(cancellationToken).ConfigureAwait(false);
        var propertyNodeIds = propertyNodes
            .Select(propertyNode => propertyNode["propertyNodeId"].As<string>())
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var propertyNodeId in propertyNodeIds)
        {
            var deletion = await transaction.RunAsync(
                "MATCH (propertyNode) WHERE id(propertyNode) = toInteger($propertyNodeId) " +
                "DETACH DELETE propertyNode RETURN true AS deleted",
                new { propertyNodeId },
                cancellationToken).ConfigureAwait(false);
            _ = await deletion.ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebugComplexPropertyManager270(propertyNodeIds.Length, string.Join(",", parentGraphIds));
        }
    }

}
