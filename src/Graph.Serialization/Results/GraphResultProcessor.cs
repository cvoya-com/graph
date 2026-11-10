// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Serialization.Results;

using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Cvoya.Graph.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;


/// <summary>
/// Reassembles provider-neutral graph records into serialization representations for CLR materialization.
/// </summary>
public sealed class GraphResultProcessor
{
    private readonly EntityFactory _entityFactory;
    private readonly ILogger<GraphResultProcessor> _logger;

    // Label-to-CLR-type discovery used to run a full AppDomain type scan for every materialized
    // node. Cache the scan and the per-target-type label maps instead; an AssemblyLoad invalidates
    // both because a newly loaded assembly can contribute the concrete type a label refers to.
    private static readonly ConcurrentDictionary<Type, LabelIndex> NodeLabelIndexes = new();
    private static readonly ConcurrentDictionary<Type, LabelIndex> RelationshipLabelIndexes = new();
    private static readonly ConcurrentDictionary<Type, string> TypeLabels = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> LabelsProperties = new();
    private static readonly ConcurrentDictionary<Type, int> LabelIndexBuildsForTests = new();
    private static long _assemblyVersion;
    private static Type[]? _knownConcreteTypes;
    private static long _knownConcreteTypesVersion = -1;

    static GraphResultProcessor()
    {
        AppDomain.CurrentDomain.AssemblyLoad += static (_, _) =>
            Interlocked.Increment(ref _assemblyVersion);
    }

    private sealed record LabelIndex(long Version, Dictionary<string, Type> ByLabel);

    private static readonly List<ComplexProperty> NoComplexProperties = [];

    internal static int LabelIndexBuildCountForTesting(Type targetType) =>
        LabelIndexBuildsForTests.TryGetValue(targetType, out var count) ? count : 0;

    internal static void ResetLabelIndexBuildsForTesting() => LabelIndexBuildsForTests.Clear();

    private sealed record ComplexProperty(
        GraphValue ParentNode,
        GraphValue Relationship,
        int SequenceNumber,
        GraphValue Property);
    private sealed record NodeResult(GraphValue Node, List<ComplexProperty> ComplexProperties);
    private sealed record PathSegmentResult(
        NodeResult StartNode,
        GraphValue Relationship,
        NodeResult EndNode);

    /// <summary>Initializes a shared graph-result processor.</summary>
    public GraphResultProcessor(EntityFactory entityFactory, ILoggerFactory? loggerFactory = null)
    {
        _entityFactory = entityFactory ?? throw new ArgumentNullException(nameof(entityFactory));
        _logger = loggerFactory?.CreateLogger<GraphResultProcessor>() ?? NullLogger<GraphResultProcessor>.Instance;
    }

    /// <summary>Processes result records for a requested CLR target type.</summary>
    public Task<List<EntityInfo>> ProcessAsync(
        IReadOnlyList<GraphRecord> records,
        Type targetType,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebugGraphResultProcessor44(targetType.Name);

        cancellationToken.ThrowIfCancellationRequested();

        // Handle path segments specially
        if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(IGraphPathSegment<,,>))
        {
            return Task.FromResult(ProcessPathSegments(records, targetType));
        }

        // Handle nodes
        if (typeof(Graph.INode).IsAssignableFrom(targetType))
        {
            return Task.FromResult(ProcessNodes(records, targetType));
        }

        // Handle relationships
        if (typeof(Graph.IRelationship).IsAssignableFrom(targetType))
        {
            return Task.FromResult(ProcessRelationships(records, targetType));
        }

        // Handle IEntity - polymorphic entity search results
        if (targetType == typeof(Graph.IEntity))
        {
            return Task.FromResult(ProcessMixedEntities(records));
        }

        // Handle projections (EntityInfo)
        return Task.FromResult(ProcessProjections(records, targetType));
    }

    private static PathSegmentResult? DeserializePathSegment(Dictionary<string, object> pathSegmentRecord)
    {
        // Extract the relevant properties from the dictionary
        if (!pathSegmentRecord.TryGetValue("StartNode", out var startNodeObj) ||
            !pathSegmentRecord.TryGetValue("Relationship", out var relationshipObj) ||
            !pathSegmentRecord.TryGetValue("EndNode", out var endNodeObj))
        {
            return null;
        }

        var startNode = DeserializeNode(startNodeObj.As<Dictionary<string, object>>())
                ?? throw new GraphException("Failed to deserialize start node from path segment record.");
        var relationship = relationshipObj.As<GraphValue>()
            ?? throw new GraphException("Failed to deserialize relationship from path segment record.");
        var endNode = DeserializeNode(endNodeObj.As<Dictionary<string, object>>())
            ?? throw new GraphException("Failed to deserialize end node from path segment record.");

        return new PathSegmentResult(startNode, relationship, endNode);
    }

    private static NodeResult? DeserializeNode(Dictionary<string, object> nodeRecord)
    {
        // Extract the relevant properties from the dictionary
        if (!nodeRecord.TryGetValue("Node", out var nodeObj) ||
            !nodeRecord.TryGetValue("ComplexProperties", out var complexPropsObj))
        {
            return null;
        }

        var node = nodeObj.As<GraphValue>();
        var list = complexPropsObj as List<object> ?? [];
        if (list.Count == 0)
        {
            return new NodeResult(node, NoComplexProperties);
        }

        var complexProperties = list
            .OfType<Dictionary<string, object>>()
            .Select(dict => new ComplexProperty(
                ParentNode: dict["ParentNode"].As<GraphValue>(),
                Relationship: dict["Relationship"].As<GraphValue>(),
                SequenceNumber: dict["SequenceNumber"].As<int>(),
                Property: dict["Property"].As<GraphValue>()
        ))
        .GroupBy(cp => cp.Relationship.ElementId, StringComparer.Ordinal)
        .Select(group => group.First())
        .OrderBy(cp => cp.SequenceNumber)
        .ToList();

        return new NodeResult(node, complexProperties);
    }

    private List<EntityInfo> ProcessPathSegments(IReadOnlyList<GraphRecord> records, Type pathSegmentType)
    {
        var results = new List<EntityInfo>(records.Count);
        var genericArgs = pathSegmentType.GetGenericArguments();
        var sourceType = genericArgs[0];  // Person
        var relType = genericArgs[1];     // WorksFor
        var targetType = genericArgs[2];  // Company

        foreach (var record in records)
        {
            var pathSegment = DeserializePathSegment(record["PathSegment"].As<Dictionary<string, object>>())
                ?? throw new GraphException("Failed to deserialize path segment from record.");

            // Process each component
            var startNodeEntityInfo = ProcessSingleNodeResult(
                pathSegment.StartNode,
                sourceType,
                preserveInterfaceShape: true);
            var relEntityInfo = ProcessSingleRelationship(pathSegment.Relationship, relType);
            var endNodeEntityInfo = ProcessSingleNodeResult(
                pathSegment.EndNode,
                targetType,
                preserveInterfaceShape: true);

            // Create the composite path segment EntityInfo
            var pathSegmentEntityInfo = CreatePathSegmentEntityInfo(
                startNodeEntityInfo,
                relEntityInfo,
                endNodeEntityInfo,
                GetPathSegmentDirection(
                    pathSegment.Relationship,
                    pathSegment.StartNode.Node,
                    pathSegment.EndNode.Node),
                pathSegmentType);

            results.Add(pathSegmentEntityInfo);
        }

        return results;
    }

    private List<EntityInfo> ProcessRelationships(IReadOnlyList<GraphRecord> records, Type targetType)
    {
        var results = new List<EntityInfo>(records.Count);

        foreach (var record in records)
        {
            if (record.Values.TryGetValue("Relationship", out var relationshipValue) &&
                relationshipValue.ToObject() is GraphValue { Kind: GraphValueKind.Relationship } relationship)
            {
                results.Add(ProcessSingleRelationship(relationship, targetType));
            }
            else
            {
                throw new GraphException("Relationship projection did not contain a bare relationship value.");
            }
        }

        return results;
    }

    private EntityInfo ProcessSingleNodeResult(
        NodeResult nodeResult,
        Type targetType,
        bool preserveInterfaceShape = false)
    {
        // Use the new recursive deserializer for complex properties
        if (typeof(Graph.DynamicNode).IsAssignableFrom(targetType) ||
            preserveInterfaceShape && targetType == typeof(Graph.INode))
        {
            // An interface-only read shape intentionally omits declared properties, so keep it
            // dynamic instead of rediscovering a concrete type whose required complex properties
            // were not requested by the projection.
            return DeserializeComplexPropertiesForDynamicNode(nodeResult.Node, nodeResult.ComplexProperties, typeof(DynamicNode));
        }

        // For strongly-typed nodes
        return DeserializeComplexPropertiesForTypedNode(nodeResult.Node, nodeResult.ComplexProperties, targetType);
    }

    /// <summary>
    /// Recursively reconstructs the object graph for a node and its complex properties.
    /// </summary>
    private EntityInfo DeserializeComplexPropertiesForTypedNode(
        GraphValue node,
        List<ComplexProperty> allComplexProperties,
        Type nodeType,
        int depth = 0,
        HashSet<string>? visitedNodeIds = null)
    {
        if (depth > GraphDataModel.DefaultDepthAllowed)
        {
            throw new GraphException(
                $"Complex properties cannot exceed {GraphDataModel.DefaultDepthAllowed} levels of depth.");
        }

        var hasComplexProperties = allComplexProperties.Count > 0;
        if (hasComplexProperties || visitedNodeIds is not null)
        {
            visitedNodeIds ??= new HashSet<string>(StringComparer.Ordinal);
            if (!visitedNodeIds.Add(node.ElementId!))
                throw new GraphException("A cycle or shared node was detected in the persisted complex-property graph.");
        }

        // Create the base entity info for this node
        var actualType = DiscoverActualNodeType(node, nodeType);
        Dictionary<string, Property> simpleProperties;
        var label = node.Labels.Count == 0 ? actualType.Name : node.Labels[0];

        // Use dynamic extraction for dynamic nodes (including complex property nodes)
        if (typeof(Graph.DynamicNode).IsAssignableFrom(actualType))
        {
            simpleProperties = ExtractAllSimplePropertiesForDynamicEntity(node.Entries);
            RemoveComplexCollectionStorageProperties(simpleProperties);
        }
        else
        {
            simpleProperties = ExtractSimpleProperties(node.Entries, actualType);
        }

        AddNodeLabelsProperty(simpleProperties, actualType, node.Labels);

        var entityInfo = new EntityInfo(
            ActualType: actualType,
            Label: label,
            ActualLabels: node.Labels,
            SimpleProperties: simpleProperties,
            ComplexProperties: new Dictionary<string, Property>()
        );

        // Resolve the schema from the DISCOVERED concrete type, not the declared/target type.
        // For a base-typed complex property holding a derived instance (e.g. a PoliceDogDescription
        // in a List<AnimalDescription>), nodeType is the base (AnimalDescription) whose schema has
        // none of the derived-only complex properties (Handler); actualType is the concrete type
        // whose schema does. Simple properties already use actualType (see ExtractSimpleProperties
        // above) - this keeps complex-property reconstruction consistent, so derived-only nested
        // complex properties survive the round trip (see #146).
        var schema = _entityFactory.GetSchema(actualType);
        if (schema == null)
        {
            _logger.LogWarningGraphResultProcessor280(actualType.Name);
            return entityInfo;
        }

        // An all-null complex collection has no child rows but still stores its length and null
        // indexes in the node's storage entries, so a missing schema complex property is the only
        // safe reason to skip the rehydration walk.
        if (!hasComplexProperties && schema.ComplexProperties.Count == 0)
        {
            // No complex-property rows and no schema property can match; skip the per-node LINQ
            // search for the common flat-node case.
            return entityInfo;
        }

        // Find all complex properties where this node is the parent
        var directComplexProps = allComplexProperties
            .Where(cp => cp.ParentNode.ElementId == node.ElementId)
            .ToList();

        foreach (var (propertyName, propertySchema) in schema.ComplexProperties)
        {
            var expectedRelType = propertySchema.RelationshipType ??
                GraphDataModel.GetComplexPropertyRelationshipType(propertySchema.PropertyInfo);

            // Find all complex properties for this property
            var matchingProps = directComplexProps
                .Where(cp => cp.Relationship.Type == expectedRelType)
                .ToList();

            var childType = propertySchema.PropertyType == PropertyType.ComplexCollection
                ? propertySchema.ElementType!
                : propertySchema.PropertyInfo.PropertyType;

            if (propertySchema.PropertyType == PropertyType.ComplexCollection)
            {
                var children = matchingProps
                    .Select(cp => (
                        cp.SequenceNumber,
                        DeserializeComplexPropertiesForTypedNode(
                            cp.Property, allComplexProperties, childType, depth + 1, visitedNodeIds)))
                    .ToList();
                var collection = ComplexCollectionStorageCodec.Rehydrate(
                    propertySchema.PropertyName,
                    childType,
                    node.StorageEntries,
                    children,
                    expectedRelType);
                if (collection is null)
                {
                    continue;
                }

                entityInfo.ComplexProperties[propertyName] = new Property(
                    propertySchema.PropertyInfo,
                    propertySchema.PropertyName,
                    propertySchema.IsNullable,
                    collection);
            }
            else if (propertySchema.PropertyType == PropertyType.Complex)
            {
                if (matchingProps.Count == 0)
                {
                    continue;
                }

                var child = DeserializeComplexPropertiesForTypedNode(
                    matchingProps[0].Property, allComplexProperties, childType, depth + 1, visitedNodeIds);

                entityInfo.ComplexProperties[propertyName] = new Property(
                    propertySchema.PropertyInfo,
                    propertySchema.PropertyName,
                    propertySchema.IsNullable,
                    child);
            }
        }

        return entityInfo;
    }

    /// <summary>
    /// Recursively reconstructs the object graph for a node and its complex properties.
    /// </summary>
    private static EntityInfo DeserializeComplexPropertiesForDynamicNode(
        GraphValue node,
        List<ComplexProperty> allComplexProperties,
        Type nodeType,
        int depth = 0,
        HashSet<string>? visitedNodeIds = null)
    {
        if (depth > GraphDataModel.DefaultDepthAllowed)
        {
            throw new GraphException(
                $"Complex properties cannot exceed {GraphDataModel.DefaultDepthAllowed} levels of depth.");
        }

        visitedNodeIds ??= new HashSet<string>(StringComparer.Ordinal);
        if (!visitedNodeIds.Add(node.ElementId!))
            throw new GraphException("A cycle or shared node was detected in the persisted complex-property graph.");

        // Create the base entity info for this node
        Dictionary<string, Property> simpleProperties;

        simpleProperties = ExtractAllSimplePropertiesForDynamicEntity(node.Entries);
        RemoveComplexCollectionStorageProperties(simpleProperties);
        if (depth > 0)
        {
            RemoveDynamicComplexValueStructuralProperties(simpleProperties);
        }

        var entityInfo = new EntityInfo(
            ActualType: nodeType,
            Label: node.Labels.Count == 0 ? "" : node.Labels[0],
            ActualLabels: node.Labels,
            SimpleProperties: simpleProperties,
            ComplexProperties: new Dictionary<string, Property>()
        );

        var directComplexProps = allComplexProperties
            .Where(cp => cp.ParentNode.ElementId == node.ElementId)
            .ToList();

        var relationshipGroups = directComplexProps
            .GroupBy(cp => cp.Relationship.Type)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var metadataNames = ComplexCollectionStorageCodec.GetMetadataLogicalNames(node.StorageEntries);
        var consumedRelationshipTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var propertyName in metadataNames)
        {
            var relationshipType = ComplexCollectionStorageCodec.GetRelationshipType(
                propertyName,
                node.StorageEntries);
            if (!consumedRelationshipTypes.Add(relationshipType))
            {
                throw new GraphException(
                    $"Complex collection metadata assigns relationship type '{relationshipType}' to more than one property.");
            }

            var matches = relationshipGroups.GetValueOrDefault(relationshipType) ?? [];
            var children = matches
                .Select(cp => (
                    cp.SequenceNumber,
                    DeserializeComplexPropertiesForDynamicNode(
                        cp.Property, allComplexProperties, typeof(object), depth + 1, visitedNodeIds)))
                .ToList();

            var value = ComplexCollectionStorageCodec.Rehydrate(
                propertyName,
                typeof(object),
                node.StorageEntries,
                children,
                relationshipType) ?? throw new GraphException(
                    $"Complex collection metadata for '{propertyName}' did not produce a collection.");

            entityInfo.ComplexProperties[propertyName] = new Property(
                PropertyInfo: null!,
                Label: propertyName,
                IsNullable: true,
                Value: value,
                RelationshipType: relationshipType);
        }

        foreach (var (relationshipType, matches) in relationshipGroups
            .Where(group => !consumedRelationshipTypes.Contains(group.Key))
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var propertyName = GraphDataModel.RelationshipTypeNameToPropertyName(relationshipType);
            if (entityInfo.ComplexProperties.ContainsKey(propertyName))
            {
                throw new GraphException(
                    $"Complex relationship type '{relationshipType}' conflicts with collection metadata for property '{propertyName}'.");
            }

            var denseChildren = matches
                .Select(cp => (
                    cp.SequenceNumber,
                    Entity: DeserializeComplexPropertiesForDynamicNode(
                        cp.Property, allComplexProperties, typeof(object), depth + 1, visitedNodeIds)))
                .OrderBy(child => child.SequenceNumber)
                .Select(child => child.Entity)
                .ToList();
            Serialized value = denseChildren.Count == 1
                ? denseChildren[0]
                : new EntityCollection(typeof(object), denseChildren);
            entityInfo.ComplexProperties[propertyName] = new Property(
                PropertyInfo: null!,
                Label: propertyName,
                IsNullable: true,
                Value: value,
                RelationshipType: relationshipType);
        }

        return entityInfo;
    }

    private EntityInfo ProcessSingleRelationship(GraphValue relationship, Type targetType) =>
        CreateEntityInfoFromRelationship(relationship, targetType);

    /// <summary>
    /// A single decomposed hop of a <c>TraversePaths</c> result: which path and hop position it
    /// belongs to, and the (already-deserialized-to-<see cref="EntityInfo"/>) start node,
    /// relationship, and end node for that hop.
    /// </summary>
    public sealed record GraphPathHop(
        int PathIndex,
        int HopIndex,
        EntityInfo StartNode,
        EntityInfo Relationship,
        EntityInfo EndNode,
        RelationshipDirection Direction);

    /// <summary>
    /// Processes the rows produced by <c>CypherQueryVisitor.HandleTraversePaths</c> - one row per
    /// hop, each carrying <c>pathIndex</c>, <c>hopIndex</c>, and a <c>PathSegment</c> column shaped
    /// like the single-hop path-segment projection - into per-hop <see cref="EntityInfo"/> triples,
    /// reusing the same node/relationship deserialization as the single-hop <c>PathSegments</c> path.
    /// </summary>
    public List<GraphPathHop> ProcessGraphPathHops(IReadOnlyList<GraphRecord> records, Type sourceType, Type relationshipType, Type targetType)
    {
        var results = new List<GraphPathHop>(records.Count);

        foreach (var record in records)
        {
            results.Add(ProcessGraphPathHop(record, sourceType, relationshipType, targetType));
        }

        return results;
    }

    /// <summary>Processes one decomposed graph-path hop.</summary>
    public GraphPathHop ProcessGraphPathHop(GraphRecord record, Type sourceType, Type relationshipType, Type targetType)
    {
        var pathIndex = record["pathIndex"].As<int>();
        var hopIndex = record["hopIndex"].As<int>();

        var pathSegment = DeserializePathSegment(record["PathSegment"].As<Dictionary<string, object>>())
            ?? throw new GraphException("Failed to deserialize path segment hop from record.");

        var startNodeEntityInfo = ProcessSingleNodeResult(
            pathSegment.StartNode,
            sourceType,
            preserveInterfaceShape: true);
        var relEntityInfo = ProcessSingleRelationship(pathSegment.Relationship, relationshipType);
        var endNodeEntityInfo = ProcessSingleNodeResult(
            pathSegment.EndNode,
            targetType,
            preserveInterfaceShape: true);

        return new GraphPathHop(
            pathIndex,
            hopIndex,
            startNodeEntityInfo,
            relEntityInfo,
            endNodeEntityInfo,
            GetPathSegmentDirection(
                pathSegment.Relationship,
                pathSegment.StartNode.Node,
                pathSegment.EndNode.Node));
    }

    private List<EntityInfo> ProcessProjections(IReadOnlyList<GraphRecord> records, Type targetType)
    {
        var results = new List<EntityInfo>(records.Count);

        foreach (var record in records)
        {
            var entityInfo = CreateEntityInfoFromProjection(record, targetType);
            results.Add(entityInfo);
        }

        return results;
    }

    private EntityInfo CreateEntityInfoFromProjection(GraphRecord record, Type targetType)
    {
        return BuildProjectionEntityInfo(
            record.Keys.Select(key => new KeyValuePair<string, object?>(key, record[key].ToObject())),
            targetType);
    }

    private EntityInfo BuildProjectionEntityInfo(
        IEnumerable<KeyValuePair<string, object?>> columns,
        Type targetType)
    {
        var simpleProperties = new Dictionary<string, Property>();
        var complexProperties = new Dictionary<string, Property>();

        // Extract all values from the record as simple or complex properties
        foreach (var (key, value) in columns)
        {
            // A correlated collection projection (pattern comprehension) yields a list whose
            // elements are themselves projected structures (maps); build a nested entity per
            // element so the materializer can bind each field to the target element type.
            if (value is System.Collections.IEnumerable and not string &&
                TryBuildProjectionEntityCollection(value, out var projectedCollection))
            {
                complexProperties[key] = new Property(
                    PropertyInfo: null!,
                    Label: key,
                    IsNullable: true,
                    Value: projectedCollection);
                continue;
            }

            // Handle node values specially - convert them to EntityInfo.
            if (value is GraphValue { Kind: GraphValueKind.Node } n)
            {
                // Create EntityInfo for the node and store as complex property
                var nodeEntityInfo = CreateEntityInfoFromNode(n, typeof(Graph.INode));
                complexProperties[key] = new Property(
                    PropertyInfo: null!, // We'll handle this differently for projections
                    Label: key,
                    IsNullable: true, // Assume nullable for projections
                    Value: nodeEntityInfo
                );
                continue;
            }

            // Relationship-valued projections intentionally carry only the relationship value.
            if (value is GraphValue { Kind: GraphValueKind.Relationship } relationshipValue)
            {
                var projectionType = GetProjectionMemberType(targetType, key) ?? typeof(Graph.IRelationship);
                var relEntityInfo = CreateEntityInfoFromRelationship(relationshipValue, projectionType);
                complexProperties[key] = new Property(
                    PropertyInfo: null!, // We'll handle this differently for projections
                    Label: key,
                    IsNullable: true, // Assume nullable for projections
                    Value: relEntityInfo
                );
                continue;
            }

            // Handle complex property structures with Node and ComplexProperties (e.g., { Node: src, ComplexProperties: [...] })
            if (value is IReadOnlyDictionary<string, object> complexPropStructure &&
                complexPropStructure.ContainsKey("Node") &&
                complexPropStructure.ContainsKey("ComplexProperties"))
            {
                // This is a complex property structure - deserialize it properly
                if (complexPropStructure["Node"] is GraphValue { Kind: GraphValueKind.Node } node)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebugGraphResultProcessor528(node.Properties.Count, string.Join(", ", node.Properties.Keys));
                    }

                    // Create the base EntityInfo from the node
                    EntityInfo nodeEntityInfo;

                    // Discover the actual node type from the node labels
                    var actualNodeType = DiscoverActualNodeType(node, typeof(Graph.INode));

                    // Add complex properties if they exist
                    if (complexPropStructure["ComplexProperties"] is IList<object> complexProps && complexProps.Count > 0)
                    {
                        _logger.LogDebugGraphResultProcessor542(complexProps.Count);

                        // Convert the complex properties list to ComplexProperty objects
                        var complexPropertyList = complexProps
                            .OfType<Dictionary<string, object>>()
                            .Select(dict => new ComplexProperty(
                                ParentNode: dict["ParentNode"].As<GraphValue>(),
                                Relationship: dict["Relationship"].As<GraphValue>(),
                                SequenceNumber: dict["SequenceNumber"].As<int>(),
                                Property: dict["Property"].As<GraphValue>()
                            ))
                            .GroupBy(cp => cp.Relationship.ElementId, StringComparer.Ordinal)
                            .Select(group => group.First())
                            .OrderBy(cp => cp.SequenceNumber)
                            .ToList();

                        // Create EntityInfo with complex properties properly deserialized using the actual type
                        nodeEntityInfo = DeserializeComplexPropertiesForTypedNode(node, complexPropertyList, actualNodeType);
                    }
                    else
                    {
                        // No complex properties, just create the base EntityInfo from the node
                        nodeEntityInfo = CreateEntityInfoFromNode(node, actualNodeType);
                    }

                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebugGraphResultProcessor569(nodeEntityInfo.SimpleProperties.Count, string.Join(", ", nodeEntityInfo.SimpleProperties.Keys), nodeEntityInfo.ComplexProperties.Count);
                    }

                    complexProperties[key] = new Property(
                        PropertyInfo: null!, // We'll handle this differently for projections
                        Label: key,
                        IsNullable: true, // Assume nullable for projections
                        Value: nodeEntityInfo
                    );
                    continue;
                }
            }

            // Handle structured path segment objects (e.g., { StartNode: src, Relationship: r, EndNode: tgt })
            if (value is IReadOnlyDictionary<string, object> structuredObject &&
                structuredObject.ContainsKey("StartNode") &&
                structuredObject.ContainsKey("Relationship") &&
                structuredObject.ContainsKey("EndNode"))
            {
                // Check if this is a complex path segment with nested structures
                var startNodeObj = structuredObject["StartNode"];
                var relationshipObj = structuredObject["Relationship"];
                var endNodeObj = structuredObject["EndNode"];

                // Handle complex property structures within path segments
                GraphValue? startNode = null;
                GraphValue? relationship = null;
                GraphValue? endNode = null;

                // Extract start node (could be a complex structure or direct node)
                if (startNodeObj is IReadOnlyDictionary<string, object> startNodeStruct &&
                    startNodeStruct.ContainsKey("Node"))
                {
                    startNode = startNodeStruct["Node"] as GraphValue;
                }
                else if (startNodeObj is GraphValue { Kind: GraphValueKind.Node } directStartNode)
                {
                    startNode = directStartNode;
                }

                // Extract relationship
                relationship = relationshipObj is GraphValue { Kind: GraphValueKind.Relationship } projectedRelationship
                    ? projectedRelationship
                    : null;

                // Extract end node (could be a complex structure or direct node)
                if (endNodeObj is IReadOnlyDictionary<string, object> endNodeStruct &&
                    endNodeStruct.ContainsKey("Node"))
                {
                    endNode = endNodeStruct["Node"] as GraphValue;
                }
                else if (endNodeObj is GraphValue { Kind: GraphValueKind.Node } directEndNode)
                {
                    endNode = directEndNode;
                }

                if (startNode != null && relationship != null && endNode != null)
                {
                    var startNodeEntityInfo = CreateEntityInfoFromNode(startNode, typeof(Graph.INode));
                    var relEntityInfo = CreateEntityInfoFromRelationship(relationship, typeof(Graph.IRelationship));
                    var endNodeEntityInfo = CreateEntityInfoFromNode(endNode, typeof(Graph.INode));

                    var pathSegmentEntityInfo = CreatePathSegmentEntityInfo(
                        startNodeEntityInfo,
                        relEntityInfo,
                        endNodeEntityInfo,
                        GetPathSegmentDirection(relationship, startNode, endNode),
                        typeof(IGraphPathSegment<Graph.INode, Graph.IRelationship, Graph.INode>)
                    );

                    complexProperties[key] = new Property(
                        PropertyInfo: null!, // We'll handle this differently for projections
                        Label: key,
                        IsNullable: true, // Assume nullable for projections
                        Value: pathSegmentEntityInfo
                    );
                    continue;
                }
            }

            // Handle regular values
            try
            {
                var convertedValue = GraphValueConverter.ConvertTo(value, typeof(object));
                if (convertedValue is null && value is not null)
                {
                    throw new InvalidOperationException($"Failed to convert value for property '{key}'");
                }

                // Create a simple property info (we don't have real PropertyInfo for projections)
                simpleProperties[key] = new Property(
                    PropertyInfo: null!, // We'll handle this differently for projections
                    Label: key,
                    IsNullable: true, // Assume nullable for projections
                    Value: new SimpleValue(convertedValue!, convertedValue?.GetType() ?? typeof(object))
                );
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to convert value for property '{key}' of type {value?.GetType().Name ?? "null"}: {ex.Message}", ex);
            }
        }

        return new EntityInfo(
            ActualType: targetType,
            Label: targetType.Name,
            ActualLabels: [],
            SimpleProperties: simpleProperties,
            ComplexProperties: complexProperties
        );
    }

    /// <summary>
    /// Builds an <see cref="EntityCollection"/> for a projected list whose every element is itself a
    /// projected structure (a map), such as a pattern comprehension producing a list of anonymous
    /// objects. Each element becomes a nested projection <see cref="EntityInfo"/> whose fields the
    /// materializer binds to the target element type. Lists of scalars (or empty lists) are declined
    /// so the scalar collection path handles them.
    /// </summary>
    private bool TryBuildProjectionEntityCollection(object? value, out EntityCollection collection)
    {
        collection = null!;
        if (value is not System.Collections.IEnumerable sequence || value is string)
        {
            return false;
        }

        var entities = new List<EntityInfo?>();
        var hasStructuredElement = false;
        foreach (var element in sequence)
        {
            if (element is null)
            {
                entities.Add(null);
                continue;
            }

            if (element is not IReadOnlyDictionary<string, object> map)
            {
                return false;
            }

            // ActualType is left null so the materializer binds each element against the target
            // collection's element type (unknown here, since projection properties carry no
            // PropertyInfo) rather than the placeholder object type.
            var elementInfo = BuildProjectionEntityInfo(
                map.Select(pair => new KeyValuePair<string, object?>(pair.Key, pair.Value)),
                typeof(object)) with
            { ActualType = null! };
            entities.Add(elementInfo);
            hasStructuredElement = true;
        }

        if (!hasStructuredElement)
        {
            return false;
        }

        collection = new EntityCollection(typeof(object), entities);
        return true;
    }

    private static Type? GetProjectionMemberType(Type targetType, string projectionName)
    {
        return targetType.GetProperty(projectionName)?.PropertyType
            ?? targetType.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .FirstOrDefault(p => string.Equals(p.Name, projectionName, StringComparison.OrdinalIgnoreCase))
                ?.ParameterType;
    }

    private static RelationshipDirection GetPathSegmentDirection(
        GraphValue relationship,
        GraphValue pathStartNode,
        GraphValue pathEndNode)
    {
        var endpointsMatchPath =
            relationship.StartNodeElementId == pathStartNode.ElementId &&
            relationship.EndNodeElementId == pathEndNode.ElementId;
        if (endpointsMatchPath)
        {
            // For a self-loop both orientations match; choose Outgoing deterministically.
            return RelationshipDirection.Outgoing;
        }

        if (relationship.StartNodeElementId == pathEndNode.ElementId &&
            relationship.EndNodeElementId == pathStartNode.ElementId)
        {
            return RelationshipDirection.Incoming;
        }

        throw new GraphException(
            "Relationship endpoint element IDs do not match the projected path-segment endpoints.");
    }

    private static EntityInfo CreatePathSegmentEntityInfo(
        EntityInfo sourceEntity,
        EntityInfo relEntity,
        EntityInfo targetEntity,
        RelationshipDirection direction,
        Type pathSegmentType)
    {
        // Store the three components as properties in the path segment EntityInfo
        var complexProperties = new Dictionary<string, Property>
        {
            [nameof(IGraphPathSegment.StartNode)] = new Property(
                PropertyInfo: null!,
                Label: nameof(IGraphPathSegment.StartNode),
                IsNullable: false,
                Value: sourceEntity
            ),
            [nameof(IGraphPathSegment.Relationship)] = new Property(
                PropertyInfo: null!,
                Label: nameof(IGraphPathSegment.Relationship),
                IsNullable: false,
                Value: relEntity
            ),
            [nameof(IGraphPathSegment.EndNode)] = new Property(
                PropertyInfo: null!,
                Label: nameof(IGraphPathSegment.EndNode),
                IsNullable: false,
                Value: targetEntity
            )
        };

        return new EntityInfo(
            ActualType: pathSegmentType,
            Label: typeof(IGraphPathSegment<,,>).Name,
            ActualLabels: [],
            SimpleProperties: new Dictionary<string, Property>
            {
                [nameof(IGraphPathSegment.Direction)] = new Property(
                    typeof(IGraphPathSegment).GetProperty(nameof(IGraphPathSegment.Direction))!,
                    nameof(IGraphPathSegment.Direction),
                    IsNullable: false,
                    new SimpleValue(direction, typeof(RelationshipDirection)))
            },
            ComplexProperties: complexProperties
        );
    }

    private List<EntityInfo> ProcessNodes(IReadOnlyList<GraphRecord> records, Type targetType)
    {
        var results = new List<EntityInfo>(records.Count);

        foreach (var record in records)
        {
            NodeResult? nodeResult = null;

            // Check if this is the new structured format
            if (record.Keys.Contains("Node"))
            {
                nodeResult = DeserializeNode(record["Node"].As<Dictionary<string, object>>())
                    ?? throw new GraphException("Failed to deserialize node from structured record.");
            }
            else
            {
                throw new GraphException("Unable to find node data in record.");
            }

            var entityInfo = ProcessSingleNodeResult(nodeResult, targetType);
            results.Add(entityInfo);
        }

        return results;
    }

    private static Dictionary<string, Property> ExtractAllSimplePropertiesForDynamicEntity(
        IReadOnlyDictionary<string, GraphValue> entries)
    {
        var result = new Dictionary<string, Property>(entries.Count);
        foreach (var (key, wireValue) in entries)
        {
            if (key == GraphValueConverter.MetadataPropertyName)
                continue;

            // Convert one wire value at a time instead of materializing the whole property bag
            // first; flat nodes are the hot path and must not pay for a redundant dictionary.
            var value = wireValue.ToObject();
            Serialized serializedValue;
            // Dictionaries (wire maps) are not simple collections; they pass through as scalar values.
            if (value is IEnumerable enumerable and not string and not byte[] and not IDictionary)
            {
                var items = enumerable.Cast<object?>().ToList();
                var elementType = enumerable is GraphValue.TypedGraphValueList typed
                    ? typed.ElementType
                    : InferDynamicCollectionElementType(items);
                serializedValue = CreateSimpleCollection(key, items, elementType);
            }
            else
            {
                // Driver-specific scalar values have already been normalized by the wire adapter.
                var convertedValue = GraphValueConverter.ConvertTo(value, value?.GetType() ?? typeof(object));
                serializedValue = new SimpleValue(convertedValue!, convertedValue?.GetType() ?? typeof(object));
            }

            result[key] = new Property(
                PropertyInfo: null!,
                Label: key,
                IsNullable: value == null,
                Value: serializedValue
            );
        }
        return result;
    }

    private static void RemoveDynamicComplexValueStructuralProperties(
        Dictionary<string, Property> simpleProperties)
    {
        simpleProperties.Remove(nameof(Graph.INode.Labels));
    }

    private static void RemoveComplexCollectionStorageProperties(
        Dictionary<string, Property> simpleProperties)
    {
        foreach (var name in simpleProperties.Keys
            .Where(ComplexCollectionStorageCodec.IsMetadataProperty)
            .ToArray())
        {
            simpleProperties.Remove(name);
        }
    }

    private static Type InferDynamicCollectionElementType(IReadOnlyList<object?> items)
    {
        var elementTypes = items
            .Where(item => item is not null)
            .Select(item => item!.GetType())
            .Distinct()
            .ToList();

        if (elementTypes.Count != 1)
        {
            return typeof(object);
        }

        var elementType = elementTypes[0];
        if (items.Any(item => item is null) &&
            elementType.IsValueType &&
            Nullable.GetUnderlyingType(elementType) is null)
        {
            return typeof(Nullable<>).MakeGenericType(elementType);
        }

        return elementType;
    }

    private EntityInfo CreateEntityInfoFromNode(GraphValue node, Type targetType)
    {
        // Discover the actual type from metadata, labels, or fall back to target type
        var actualType = DiscoverActualNodeType(node, targetType);

        Dictionary<string, Property> simpleProperties;
        var label = node.Labels.Count == 0 ? actualType.Name : node.Labels[0];

        // Handle dynamic nodes differently
        if (typeof(Graph.DynamicNode).IsAssignableFrom(actualType))
        {
            simpleProperties = ExtractAllSimplePropertiesForDynamicEntity(node.Entries);
            RemoveComplexCollectionStorageProperties(simpleProperties);
        }
        else
        {
            simpleProperties = ExtractSimpleProperties(node.Entries, actualType);
        }

        AddNodeLabelsProperty(simpleProperties, actualType, node.Labels);

        return new EntityInfo(
            ActualType: actualType,
            Label: label,
            ActualLabels: node.Labels,
            SimpleProperties: simpleProperties,
            ComplexProperties: new Dictionary<string, Property>()
        );
    }

    private static void AddNodeLabelsProperty(
        Dictionary<string, Property> simpleProperties,
        Type actualType,
        IReadOnlyList<string> labels)
    {
        var labelsProperty = actualType.Assembly.IsCollectible
            ? actualType.GetProperty(nameof(Graph.INode.Labels))
            : LabelsProperties.GetOrAdd(
                actualType,
                static type => type.GetProperty(nameof(Graph.INode.Labels)));
        if (labelsProperty is null)
        {
            return;
        }

        simpleProperties[nameof(Graph.INode.Labels)] = new Property(
            PropertyInfo: labelsProperty,
            Label: nameof(Graph.INode.Labels),
            IsNullable: false,
            Value: new SimpleCollection(
                labels.Select(label => new SimpleValue(label, typeof(string))).ToList(),
                typeof(string)));
    }

    private EntityInfo CreateEntityInfoFromRelationship(GraphValue relationship, Type targetType)
    {
        // Discover the actual type from metadata, type, or fall back to target type
        var actualType = DiscoverActualRelationshipType(relationship, targetType);

        var label = relationship.Type;
        if (string.IsNullOrEmpty(label))
        {
            label = actualType.Name;
        }
        Dictionary<string, Property> simpleProperties;

        // Handle dynamic relationships differently
        if (typeof(Graph.DynamicRelationship).IsAssignableFrom(actualType))
        {
            simpleProperties = ExtractAllSimplePropertiesForDynamicEntity(relationship.Entries);
        }
        else
        {
            simpleProperties = ExtractSimpleProperties(relationship.Entries, actualType);
        }

        // Add Type property for all relationships (both dynamic and typed)
        // This enables filtering by type in LINQ queries
        var typeProperty = actualType.GetProperty(nameof(Graph.IRelationship.Type));
        if (typeProperty != null)
        {
            simpleProperties[nameof(Graph.IRelationship.Type)] = new Property(
                PropertyInfo: typeProperty,
                Label: nameof(Graph.IRelationship.Type),
                IsNullable: false,
                Value: new SimpleValue(label, typeof(string))
            );
        }

        return new EntityInfo(
            ActualType: actualType,
            Label: label,
            ActualLabels: [label],
            SimpleProperties: simpleProperties,
            ComplexProperties: new Dictionary<string, Property>()
        );
    }

    /// <summary>
    /// Discovers the actual type of a node using metadata, labels, and compatibility checks.
    /// </summary>
    private Type DiscoverActualNodeType(GraphValue node, Type targetType)
    {
        // Special handling for dynamic entities
        if (targetType == typeof(Graph.DynamicNode))
        {
            _logger.LogDebugGraphResultProcessor1107(targetType.Name);
            return typeof(Graph.DynamicNode);
        }

        // Step 1: Try to get type from stored metadata
        var metadataType = GraphValueConverter.GetTypeFromMetadata(node.Entries);
        if (metadataType != null && IsCompatibleType(metadataType, targetType))
        {
            _logger.LogDebugGraphResultProcessor1116(metadataType.Name, targetType.Name);
            return metadataType;
        }

        // Step 2: Try to discover from node labels
        var labelType = DiscoverTypeFromNodeLabels(node.Labels, targetType);
        if (labelType != null)
        {
            _logger.LogDebugGraphResultProcessor1125(labelType.Name, targetType.Name);
            return labelType;
        }

        // Step 3: Fall back to target type
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebugGraphResultProcessor1133(targetType.Name, string.Join(", ", node.Labels));
        }
        return targetType;
    }

    /// <summary>
    /// Discovers the actual type of a relationship using metadata, type, and compatibility checks.
    /// </summary>
    private Type DiscoverActualRelationshipType(GraphValue relationship, Type targetType)
    {
        // Special handling for dynamic entities
        if (targetType == typeof(Graph.DynamicRelationship))
        {
            _logger.LogDebugGraphResultProcessor1147(targetType.Name);
            return typeof(Graph.DynamicRelationship);
        }

        // Step 1: Try to get type from stored metadata
        var metadataType = GraphValueConverter.GetTypeFromMetadata(relationship.Entries);
        if (metadataType != null && IsCompatibleType(metadataType, targetType))
        {
            _logger.LogDebugGraphResultProcessor1156(metadataType.Name, targetType.Name);
            return metadataType;
        }

        // Step 2: Try to discover from relationship type/label
        var labelType = DiscoverTypeFromRelationshipType(relationship.Type, targetType);
        if (labelType != null)
        {
            _logger.LogDebugGraphResultProcessor1165(labelType.Name, targetType.Name);
            return labelType;
        }

        // Step 3: Fall back to target type
        _logger.LogDebugGraphResultProcessor1171(targetType.Name, relationship.Type ?? "null");
        return targetType;
    }

    /// <summary>
    /// Checks if a discovered type is compatible with the target type (same or derived).
    /// </summary>
    private static bool IsCompatibleType(Type discoveredType, Type targetType)
    {
        // The discovered type must be assignable to the target type
        // This means discoveredType is the same as targetType or derives from it
        return targetType.IsAssignableFrom(discoveredType);
    }

    /// <summary>
    /// Attempts to discover a more specific type from node labels.
    /// </summary>
    private static Type? DiscoverTypeFromNodeLabels(IReadOnlyList<string> labels, Type targetType)
    {
        if (labels.Count == 0)
            return null;

        // The index is built per target type (not per node). It must NOT be scoped to INode types:
        // targetType here can be a plain complex-property POCO (e.g. the element type of a List<T>
        // complex property) that never implements INode, yet its concrete subtype is exactly what
        // the stored label encodes and what we need to recover.
        var labelIndex = GetLabelIndex(targetType, relationships: false);
        foreach (var label in labels)
        {
            if (!string.IsNullOrEmpty(label) && labelIndex.TryGetValue(label, out var matchingType))
                return matchingType;
        }

        return null;
    }

    /// <summary>
    /// Attempts to discover a more specific type from relationship type.
    /// </summary>
    private static Type? DiscoverTypeFromRelationshipType(string? relationshipType, Type targetType)
    {
        if (string.IsNullOrEmpty(relationshipType))
            return null;

        return GetLabelIndex(targetType, relationships: true).TryGetValue(relationshipType, out var matchingType)
            ? matchingType
            : null;
    }

    /// <summary>
    /// Gets the label-to-type map for one target type, rebuilding it after an assembly load.
    /// </summary>
    private static Dictionary<string, Type> GetLabelIndex(Type targetType, bool relationships)
    {
        var cache = relationships ? RelationshipLabelIndexes : NodeLabelIndexes;
        var version = Volatile.Read(ref _assemblyVersion);
        if (cache.TryGetValue(targetType, out var cached) && cached.Version == version)
            return cached.ByLabel;

        LabelIndexBuildsForTests.AddOrUpdate(targetType, 1, static (_, count) => count + 1);
        var byLabel = BuildLabelIndex(targetType, relationships);

        // Never retain types from a collectible AssemblyLoadContext: the cache would keep that
        // context alive and break runtime unload expectations. Such assemblies are rare, so pay the
        // scan per call instead of caching.
        if (AreAllAssembliesCacheable(AppDomain.CurrentDomain.GetAssemblies()))
        {
            cache[targetType] = new LabelIndex(version, byLabel);
        }
        else
        {
            cache.TryRemove(targetType, out _);
        }

        return byLabel;
    }

    private static Dictionary<string, Type> BuildLabelIndex(Type targetType, bool relationships)
    {
        var byLabel = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in GetKnownConcreteTypes())
        {
            if (relationships && !typeof(Graph.IRelationship).IsAssignableFrom(type))
                continue;
            if (!targetType.IsAssignableFrom(type))
                continue;

            // Match both the CLR type name and an explicit [Node]/[Relationship] label, keeping the
            // previous "first type in scan order wins" resolution for duplicate labels.
            byLabel.TryAdd(GetTypeLabel(type), type);
            byLabel.TryAdd(type.Name, type);
        }

        return byLabel;
    }

    /// <summary>
    /// Gets the label for a type, checking for attributes first.
    /// </summary>
    private static string GetTypeLabel(Type type) =>
        type.Assembly.IsCollectible
            ? ComputeTypeLabel(type)
            : TypeLabels.GetOrAdd(type, static t => ComputeTypeLabel(t));

    private static string ComputeTypeLabel(Type type)
    {
        // Check for NodeAttribute or RelationshipAttribute
        var nodeAttr = type.GetCustomAttribute<NodeAttribute>();
        if (nodeAttr != null)
            return nodeAttr.Label ?? type.Name;

        var relAttr = type.GetCustomAttribute<RelationshipAttribute>();
        if (relAttr != null)
            return relAttr.Label ?? type.Name;

        return type.Name;
    }

    /// <summary>
    /// Gets all known concrete classes from loaded assemblies that could be the actual type
    /// behind a node label: both real graph node types (INode) and the plain POCO types used
    /// as complex-property values (which are never INode themselves - see
    /// <see cref="DiscoverTypeFromNodeLabels"/>). The caller narrows this down via an
    /// assignability check against the specific target type it is resolving.
    /// </summary>
    private static Type[] GetKnownConcreteTypes()
    {
        // Read the version before taking the assembly snapshot: a load that races with the scan
        // changes the version and forces a rebuild on the next call instead of pinning a partial scan.
        var version = Volatile.Read(ref _assemblyVersion);
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        if (Volatile.Read(ref _knownConcreteTypesVersion) == version &&
            Volatile.Read(ref _knownConcreteTypes) is { } cached)
        {
            return cached;
        }

        var types = ScanConcreteTypes(assemblies);
        if (AreAllAssembliesCacheable(assemblies))
        {
            Volatile.Write(ref _knownConcreteTypesVersion, version);
            Volatile.Write(ref _knownConcreteTypes, types);
        }

        return types;
    }

    private static Type[] ScanConcreteTypes(Assembly[] assemblies)
    {
        var types = new List<Type>();
        foreach (var assembly in assemblies)
        {
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsClass && !type.IsAbstract &&
                        type != typeof(Graph.DynamicNode) && type != typeof(Graph.DynamicRelationship))
                    {
                        types.Add(type);
                    }
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Handle cases where some types can't be loaded
                foreach (var type in ex.Types)
                {
                    if (type is { IsClass: true, IsAbstract: false } &&
                        type != typeof(Graph.DynamicNode) && type != typeof(Graph.DynamicRelationship))
                    {
                        types.Add(type);
                    }
                }
            }
            catch
            {
                // A dynamic assembly can refuse type enumeration; skip it.
            }
        }

        return [.. types];
    }

    private static bool AreAllAssembliesCacheable(Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            if (assembly.IsCollectible)
                return false;
        }

        return true;
    }

    private Dictionary<string, Property> ExtractSimpleProperties(
        IReadOnlyDictionary<string, GraphValue> entries,
        Type entityType)
    {
        var result = new Dictionary<string, Property>(entries.Count);

        if (!_entityFactory.CanDeserialize(entityType))
            return result;

        var schema = _entityFactory.GetSchema(entityType);
        if (schema?.SimpleProperties == null)
            return result;

        foreach (var (key, wireValue) in entries)
        {
            // Skip metadata properties
            if (key == GraphValueConverter.MetadataPropertyName)
                continue;

            // Check if this property is in the schema
            if (schema.SimpleProperties.TryGetValue(key, out var propertySchema))
            {
                object? value = wireValue.ToObject();
                object convertedValue;
                try
                {
                    convertedValue = GraphValueConverter.ConvertTo(
                        value,
                        propertySchema.PropertyInfo.PropertyType,
                        key,
                        propertySchema.IsElementNullable)
                        ?? throw new GraphException(
                            $"Failed to convert value for property '{key}' to '{propertySchema.PropertyInfo.PropertyType}'.");
                }
                catch (GraphException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidCastException or FormatException or OverflowException)
                {
                    throw new GraphException(
                        $"Failed to convert value for property '{key}' to '{propertySchema.PropertyInfo.PropertyType}'.",
                        exception);
                }

                Property property = new(
                    propertySchema.PropertyInfo,
                    propertySchema.PropertyName,
                    propertySchema.IsNullable,
                    propertySchema.PropertyType == PropertyType.SimpleCollection
                        ? CreateSimpleCollection(key, convertedValue, propertySchema.ElementType!)
                        : new SimpleValue(convertedValue, propertySchema.PropertyInfo.PropertyType));

                result[key] = property;
            }
        }

        return result;
    }

    private static SimpleCollection CreateSimpleCollection(string propertyName, object convertedValue, Type elementType)
    {
        if (convertedValue is string || convertedValue is not IEnumerable enumerable)
        {
            throw new GraphException(
                $"Expected an enumerable value for simple collection property '{propertyName}' with element type " +
                $"'{elementType}', but received '{convertedValue.GetType()}'.");
        }

        var items = new List<SimpleValue>();
        foreach (var item in enumerable)
        {
            items.Add(new SimpleValue(item!, elementType));
        }

        return new SimpleCollection(items, elementType);
    }

    private List<EntityInfo> ProcessMixedEntities(IReadOnlyList<GraphRecord> records)
    {
        var results = new List<EntityInfo>(records.Count);

        foreach (var record in records)
        {
            // Entity search returns a bare node or relationship in the shared entity column.
            if (record.Values.TryGetValue("entity", out var wireEntityValue))
            {
                var entityValue = wireEntityValue.ToObject();
                if (entityValue is GraphValue { Kind: GraphValueKind.Node } node)
                {
                    // Process as a node
                    var nodeEntityInfo = CreateEntityInfoFromNode(node, typeof(Graph.INode));
                    results.Add(nodeEntityInfo);
                }
                else if (entityValue is GraphValue { Kind: GraphValueKind.Relationship } relationship)
                {
                    results.Add(CreateEntityInfoFromRelationship(relationship, typeof(Graph.IRelationship)));
                }
            }
        }

        return results;
    }
}
