// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Entities;

using System.Reflection;
using Cvoya.Graph.Age.Core;
using Cvoya.Graph.Age.Querying;
using Cvoya.Graph.Age.Querying.Cypher;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.Age.Serialization;
using Cvoya.Graph.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;


internal sealed class AgeNodeManager(AgeGraphContext context)
{
    private readonly ILogger<AgeNodeManager> _logger = context.LoggerFactory?.CreateLogger<AgeNodeManager>()
        ?? NullLogger<AgeNodeManager>.Instance;
    private readonly EntityFactory _serializer = new EntityFactory(context.LoggerFactory);
    private readonly ComplexPropertyManager _complexPropertyManager = new(context);

    public async Task<TNode> CreateNodeAsync<TNode>(
        TNode node,
        AgeGraphTransaction transaction,
        CancellationToken cancellationToken = default)
        where TNode : class, Graph.INode
    {
        ArgumentNullException.ThrowIfNull(node);

        _logger.LogDebugAgeNodeManager44(typeof(TNode).Name);

        try
        {
            _ = await CreateNodeForCommandAsync(node, transaction, cancellationToken).ConfigureAwait(false);

            _logger.LogInformationAgeNodeManager73(typeof(TNode).Name);

            return node;
        }
        catch (Exception ex) when (ex is not GraphException and not OperationCanceledException)
        {
            _logger.LogErrorAgeNodeManager79(ex, typeof(TNode).Name);
            throw new GraphException("Failed to create node.", ex);
        }
    }

    /// <summary>Creates a command-owned endpoint and returns its transaction-local AGE graphid.</summary>
    internal async Task<long> CreateNodeForCommandAsync(
        Graph.INode node,
        AgeGraphTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        GraphDataModel.EnsureNoReferenceCycle(node);
        GraphDataModel.EnsureComplexPropertyDepth(node);
        ValidateNodeProperties(node);

        var entity = _serializer.Serialize(node);
        await ValidateNodeUniquenessAsync(node, transaction.Runner, excludeGraphId: null, cancellationToken)
            .ConfigureAwait(false);
        if (entity.ComplexProperties.Count > 0)
        {
            await transaction.Runner
                .EnsureLabelAsync(SerializationBridge.ComplexNodeLabel, relationship: false, cancellationToken)
                .ConfigureAwait(false);
            await transaction.Runner
                .EnsureLabelAsync(SerializationBridge.ComplexRelationshipType, relationship: true, cancellationToken)
                .ConfigureAwait(false);
        }

        var graphId = await CreateMainNodeAsync(entity, transaction.Runner, cancellationToken).ConfigureAwait(false);
        await _complexPropertyManager.CreateComplexPropertiesAsync(
            transaction.Runner,
            graphId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            entity,
            cancellationToken).ConfigureAwait(false);
        return graphId;
    }

    private static async Task<long> CreateMainNodeAsync(
        EntityInfo entity,
        AgeQueryRunner transaction,
        CancellationToken cancellationToken)
    {
        var storageLabel = SerializationBridge.GetRootStorageName(entity.Label, relationship: false);
        var simpleProperties = BuildNodeProperties(entity);

        await transaction.EnsureLabelAsync(storageLabel, relationship: false, cancellationToken).ConfigureAwait(false);
        var parameters = new Dictionary<string, object?>();
        var setClause = AgeCypherProperties.BuildSetClause("n", simpleProperties, parameters, "nodeProperty");
        var physicalLabel = CypherIdentifier.Escape(storageLabel, "node label");
        var cypher = $"CREATE (n:{physicalLabel}) {setClause} RETURN id(n) AS nodeId";

        var result = await transaction.RunAsync(cypher, parameters, cancellationToken).ConfigureAwait(false);
        var record = await GetSingleRecordAsync(result, cancellationToken).ConfigureAwait(false);

        return record["nodeId"].As<long>();
    }

    internal static Dictionary<string, object?> BuildNodeProperties(EntityInfo entity)
    {
        var properties = SerializationHelpers.SerializeSimpleProperties(entity)
            .Where(pair => pair.Key != SimpleCollectionStorageCodec.GetPayloadPropertyName(nameof(Graph.INode.Labels)) &&
                pair.Key != SimpleCollectionStorageCodec.GetNullIndexesPropertyName(nameof(Graph.INode.Labels)) &&
                pair.Key != SimpleCollectionStorageCodec.GetElementTypePropertyName(nameof(Graph.INode.Labels)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        // Every hierarchy vertex carries its full logical label chain so the containment fallback
        // can answer a hierarchy query without a labels() predicate, which PostgreSQL cannot turn
        // into a GIN index scan. See ADR-0008: the upstream
        // `'X' IN labels(v) OR 'X' IN inheritance_labels` shape is deliberately not used. A single
        // native label needs no hierarchy property because its physical table already identifies it.
        var inheritanceLabels = ResolveInheritanceLabels(entity);
        if (inheritanceLabels.Count > 0)
        {
            properties[AgeElementMatcher.InheritanceLabelsProperty] = inheritanceLabels;
        }

        if (entity.ActualType.IsConstructedGenericType)
        {
            properties[SerializationBridge.MetadataPropertyName] =
                SerializationBridge.CreateScalarMetadata(entity.ActualType);
        }

        return properties;
    }

    private static IReadOnlyList<string> ResolveInheritanceLabels(EntityInfo entity)
    {
        // Dynamic nodes and explicitly labelled instances own their logical label list. Typed
        // nodes derive it from the explicitly attributed CLR base-type chain, root label first.
        if (entity.ActualLabels.Count > 0)
        {
            return [.. entity.ActualLabels];
        }

        var labels = new List<string>();
        for (var type = entity.ActualType;
             type is not null && type != typeof(Graph.Node) && typeof(Graph.INode).IsAssignableFrom(type);
             type = type.BaseType)
        {
            var label = Labels.GetLabelFromType(type);
            if (!labels.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                labels.Insert(0, label);
            }
        }

        return labels.Count > 0 ? labels : [entity.Label];
    }

    internal void ValidateNodeProperties<TNode>(TNode node) where TNode : class, Graph.INode
    {
        // For DynamicNode, validate against existing schemas if any
        if (node is DynamicNode dynamicNode)
        {
            // Labels are Cypher identifiers, not values: reject hostile labels here, before any
            // Cypher is built, rather than relying solely on escaping at the point of interpolation.
            foreach (var dynamicNodeLabel in dynamicNode.Labels)
            {
                CypherIdentifier.Validate(dynamicNodeLabel, "node label");
                SerializationBridge.ValidateRootStorageName(dynamicNodeLabel, "node label");
            }

            ValidateDynamicNodeProperties(dynamicNode);
            return;
        }

        var label = Labels.GetLabelFromType(node.GetType());
        SerializationBridge.ValidateRootStorageName(label, "node label");
        var schema = context.SchemaManager.GetSchemaRegistry().GetNodeSchema(label);

        if (schema == null) return;

        foreach (var (propertyName, propertySchema) in schema.Properties)
        {
            var property = node.GetType().GetProperty(propertyName);
            if (property == null) continue;

            var value = property.GetValue(node);

            // Validate required fields
            if (propertySchema.IsRequired &&
                (value == null || (value is string stringValue && string.IsNullOrWhiteSpace(stringValue))))
            {
                throw new GraphException($"Property '{propertyName}' on {label} is required and cannot be null or empty.");
            }

            // Validate custom validation rules
            if (propertySchema.Validation is { } validation && value is not null)
            {
                ValidatePropertyValue(propertyName, value, validation, label);
            }
        }
    }

    private async Task ValidateNodeUniquenessAsync<TNode>(
        TNode node,
        AgeQueryRunner transaction,
        long? excludeGraphId,
        CancellationToken cancellationToken)
        where TNode : class, Graph.INode
    {
        var checks = BuildNodeUniquenessChecks(node, excludeGraphId);

        // Take every lock before the first probe: holding them through the write that follows makes
        // probe-then-write atomic against a competing transaction claiming the same values.
        await transaction.AcquireUniquenessLocksAsync(
            checks.Select(check => check.LockKey).ToArray(),
            cancellationToken).ConfigureAwait(false);

        foreach (var check in checks)
        {
            var result = await transaction.RunAsync(
                check.Cypher,
                check.Parameters,
                cancellationToken).ConfigureAwait(false);
            var count = (await result.SingleAsync(cancellationToken).ConfigureAwait(false))["duplicateCount"].As<long>();
            if (count > 0)
            {
                throw new GraphException(check.ErrorMessage);
            }
        }
    }

    /// <summary>
    /// Builds the uniqueness probes for <paramref name="node"/>. A <see cref="DynamicNode"/> is
    /// checked against the registered schema of every label it carries, reading values from its
    /// property bag by mapped storage name - the same resolution
    /// <see cref="ValidateDynamicNodeProperties"/> uses for required/validation rules, so a dynamic
    /// write cannot bypass constraints a typed write of the same label would honour.
    /// </summary>
    internal IReadOnlyList<AgeUniquenessCheck> BuildNodeUniquenessChecks<TNode>(
        TNode node,
        long? excludeGraphId)
        where TNode : class, Graph.INode
    {
        if (node is DynamicNode dynamicNode)
        {
            return dynamicNode.Labels
                .Select(dynamicLabel => context.SchemaManager.GetSchemaRegistry().GetNodeSchema(dynamicLabel))
                .OfType<EntitySchemaInfo>()
                .DistinctBy(schema => schema.Label, StringComparer.OrdinalIgnoreCase)
                .SelectMany(schema => BuildChecksForSchema(
                    schema,
                    property => dynamicNode.Properties.GetValueOrDefault(property.Name),
                    excludeGraphId))
                .ToArray();
        }

        var label = Labels.GetLabelFromType(node.GetType());
        var schema = context.SchemaManager.GetSchemaRegistry().GetNodeSchema(label);
        return schema is null
            ? []
            : BuildChecksForSchema(schema, property => property.PropertyInfo.GetValue(node), excludeGraphId);
    }

    private List<AgeUniquenessCheck> BuildChecksForSchema(
        EntitySchemaInfo schema,
        Func<PropertySchemaInfo, object?> readValue,
        long? excludeGraphId)
    {
        var label = schema.Label;
        var checks = new List<AgeUniquenessCheck>();
        var keyProperties = schema.GetKeyProperties().ToArray();
        if (keyProperties.Length > 0)
        {
            checks.Add(BuildCheck(keyProperties, "composite key"));
        }

        foreach (var property in schema.Properties.Values.Where(
                     property => property.IsUnique && (!property.IsKey || schema.HasCompositeKey())))
        {
            checks.Add(BuildCheck([property], $"unique property '{property.Name}'"));
        }

        return checks;

        AgeUniquenessCheck BuildCheck(
            IReadOnlyList<PropertySchemaInfo> properties,
            string constraint)
        {
            var parameters = new Dictionary<string, object?>
            {
                ["entityLabel"] = label,
                ["excludeGraphId"] = excludeGraphId,
            };
            var predicates = new List<string>
            {
                AgeElementMatcher.NodePredicate("n", "$entityLabel"),
            };
            if (excludeGraphId is not null)
            {
                predicates.Add("id(n) <> $excludeGraphId");
            }

            var values = new List<object?>(properties.Count);
            for (var index = 0; index < properties.Count; index++)
            {
                var property = properties[index];
                var parameterName = $"uniqueValue{index}";
                var value = SerializationBridge.ToAgeValue(readValue(property));
                parameters[parameterName] = value;
                values.Add(value);
                var storageName = SimpleCollectionStorageCodec.GetPayloadPropertyName(property.Name);
                predicates.Add($"n.{CypherIdentifier.Escape(storageName, "property name")} = ${parameterName}");
            }

            var constraintKey = AgeUniquenessCheck.BuildConstraintKey(label, constraint, values);
            return new AgeUniquenessCheck(
                $"{AgeElementMatcher.UserRootMatch("n")} AND {string.Join(" AND ", predicates)} RETURN count(n) AS duplicateCount",
                parameters,
                $"Node '{label}' violates {constraint} uniqueness.",
                constraintKey,
                AgeUniquenessLockKey.Compute(
                    context.GraphName,
                    AgeUniquenessLockKey.NodeEntityKind,
                    label,
                    constraint,
                    values));
        }
    }

    private void ValidateDynamicNodeProperties(DynamicNode node)
    {
        // Check each label to see if there's a corresponding schema
        foreach (var label in node.Labels)
        {
            var schema = context.SchemaManager.GetSchemaRegistry().GetNodeSchema(label);
            if (schema == null) continue;

            // Found a schema for this label, validate the dynamic node against it
            var validatedProperties = new HashSet<string>();

            foreach (var (propertyName, propertySchema) in schema.Properties)
            {
                // Use the mapped property name from the schema (from PropertyAttribute.Label)
                var mappedPropertyName = propertySchema.Name;

                // Check if the property exists in the dynamic node's properties
                if (!node.Properties.TryGetValue(mappedPropertyName, out var value))
                {
                    // Property doesn't exist in dynamic node
                    if (propertySchema.IsRequired)
                    {
                        throw new GraphException($"Property '{mappedPropertyName}' on {label} is required but not provided in DynamicNode.");
                    }
                    continue;
                }

                // Mark this property as validated
                validatedProperties.Add(mappedPropertyName);

                // Validate required fields
                if (propertySchema.IsRequired &&
                    (value == null || (value is string stringValue && string.IsNullOrWhiteSpace(stringValue))))
                {
                    throw new GraphException($"Property '{mappedPropertyName}' on {label} is required and cannot be null or empty.");
                }

                // Validate custom validation rules
                if (propertySchema.Validation is { } validation && value is not null)
                {
                    ValidatePropertyValue(mappedPropertyName, value, validation, label);
                }

                // Validate enum values
                if (value is not null)
                {
                    ValidateEnumValue(mappedPropertyName, value, propertySchema.PropertyInfo.PropertyType, label);
                }
            }

            // Check for extra properties that don't exist in the schema
            var extraProperty = node.Properties.Keys.FirstOrDefault(propertyName => !validatedProperties.Contains(propertyName));
            if (extraProperty is not null)
            {
                throw new GraphException($"Property '{extraProperty}' on {label} is not defined in the schema and cannot be used.");
            }
        }
    }

    private static void ValidatePropertyValue(string propertyName, object value, PropertyValidation validation, string entityLabel)
    {
        // MinValue validation
        if (validation.MinValue is not null &&
            value is IComparable minComparable &&
            minComparable.CompareTo(validation.MinValue) < 0)
        {
            throw new GraphException($"Property '{propertyName}' on {entityLabel} must be greater than or equal to {validation.MinValue}. Current value: {value}");
        }

        // MaxValue validation
        if (validation.MaxValue is not null &&
            value is IComparable maxComparable &&
            maxComparable.CompareTo(validation.MaxValue) > 0)
        {
            throw new GraphException($"Property '{propertyName}' on {entityLabel} must be less than or equal to {validation.MaxValue}. Current value: {value}");
        }

        // MinLength validation
        if (validation.MinLength is not null &&
            value is string minLengthValue &&
            minLengthValue.Length < validation.MinLength)
        {
            throw new GraphException($"Property '{propertyName}' on {entityLabel} must have a minimum length of {validation.MinLength}. Current length: {minLengthValue.Length}");
        }

        // MaxLength validation
        if (validation.MaxLength is not null &&
            value is string maxLengthValue &&
            maxLengthValue.Length > validation.MaxLength)
        {
            throw new GraphException($"Property '{propertyName}' on {entityLabel} must have a maximum length of {validation.MaxLength}. Current length: {maxLengthValue.Length}");
        }

        // Pattern validation
        if (!string.IsNullOrEmpty(validation.Pattern) &&
            value is string patternValue &&
            !System.Text.RegularExpressions.Regex.IsMatch(patternValue, validation.Pattern))
        {
            throw new GraphException($"Property '{propertyName}' on {entityLabel} must match the pattern '{validation.Pattern}'. Current value: {patternValue}");
        }
    }

    private static void ValidateEnumValue(string propertyName, object value, Type propertyType, string entityLabel)
    {
        // Check if the property type is an enum
        if (propertyType.IsEnum)
        {
            // If the value is a string, try to parse it as the enum
            if (value is string stringValue)
            {
                if (!Enum.TryParse(propertyType, stringValue, ignoreCase: true, out _))
                {
                    var validValues = string.Join(", ", Enum.GetNames(propertyType));
                    throw new GraphException($"Property '{propertyName}' on {entityLabel} must be a valid enum value. Valid values are: {validValues}. Current value: {stringValue}");
                }
            }
            // If the value is not a string, check if it can be converted to the enum
            else if (!Enum.IsDefined(propertyType, value))
            {
                var validValues = string.Join(", ", Enum.GetNames(propertyType));
                throw new GraphException($"Property '{propertyName}' on {entityLabel} must be a valid enum value. Valid values are: {validValues}. Current value: {value}");
            }
        }
    }

    private static async Task<AgeRecord> GetSingleRecordAsync(AgeResultCursor result, CancellationToken cancellationToken)
    {
        return await result.SingleAsync(cancellationToken).ConfigureAwait(false);
    }
}
