// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Serialization.Results;

using System.Collections;
using System.Reflection;
using Cvoya.Graph.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;


/// <summary>Materializes provider-neutral graph records into requested CLR result shapes.</summary>
public sealed class GraphResultMaterializer
{
    private readonly EntityFactory _entityFactory;
    private readonly GraphResultProcessor _resultProcessor;
    private readonly ILogger<GraphResultMaterializer> _logger;

    /// <summary>Initializes a shared result materializer.</summary>
    public GraphResultMaterializer(EntityFactory entityFactory, ILoggerFactory? loggerFactory)
    {
        _entityFactory = entityFactory ?? throw new ArgumentNullException(nameof(entityFactory));
        _logger = loggerFactory?.CreateLogger<GraphResultMaterializer>() ?? NullLogger<GraphResultMaterializer>.Instance;
        _resultProcessor = new GraphResultProcessor(_entityFactory, loggerFactory);
    }

    /// <summary>Materializes a buffered result set.</summary>
    public async Task<T?> MaterializeAsync<T>(
        IReadOnlyList<GraphRecord> records,
        (Type Source, Type Relationship, Type Target)? graphPathTypes = null,
        CancellationToken cancellationToken = default)
    {
        var targetType = typeof(T);
        var elementType = GraphResultTypeHelpers.GetTargetTypeIfCollection(targetType);
        var isCollectionType = targetType != elementType;

        _logger.LogDebugGraphResultMaterializer38(targetType.Name, elementType.Name, isCollectionType, records.Count);

        // Handle empty results consistently
        if (!records.Any())
        {
            return isCollectionType
                ? ConvertToCollectionType<T>([], elementType)
                : default;
        }

        // TraversePaths decomposes a variable-length path into one row per hop; group those rows
        // back into ordered IGraphPath instances instead of going through the single-element
        // EntityInfo pipeline below (which has no notion of "many rows make up one result").
        if (graphPathTypes is { } types && elementType == typeof(IGraphPath))
        {
            var paths = MaterializeGraphPaths(records, types);
            return isCollectionType
                ? ConvertToCollectionType<T>([.. paths], elementType)
                : (T?)paths.FirstOrDefault();
        }

        // Process records to EntityInfo objects
        var entityInfos = await _resultProcessor.ProcessAsync(
            records,
            elementType,
            cancellationToken).ConfigureAwait(false);
        _logger.LogDebugGraphResultMaterializer65(entityInfos.Count);

        var result = isCollectionType
            ? MaterializeAsCollection<T>(entityInfos, elementType)
            : MaterializeSingleElement<T>(entityInfos.FirstOrDefault(), elementType);

        _logger.LogDebugGraphResultMaterializer71(result?.GetType().Name ?? "null");
        if (result is IEnumerable enumerable && !(result is string))
        {
            var count = enumerable.Cast<object>().Count();
            _logger.LogDebugGraphResultMaterializer75(count);
        }

        return result;
    }

    /// <summary>Materializes one result record for streaming execution.</summary>
    public async Task<T?> MaterializeRecordAsync<T>(
        GraphRecord record,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var targetType = typeof(T);
        var entityInfos = await _resultProcessor.ProcessAsync(
            [record],
            targetType,
            cancellationToken).ConfigureAwait(false);
        _logger.LogDebugGraphResultMaterializer93(entityInfos.Count);

        return MaterializeSingleElement<T>(entityInfos.FirstOrDefault(), targetType);
    }

    /// <summary>
    /// Materializes whole native path values, one per record, into graph paths. The AGE query
    /// pipeline decomposes native shortest-path values in SQL and hydrates them through
    /// <see cref="MaterializeAsync{T}"/>; this entry point remains for callers that hold whole
    /// path values (for example from a direct native-function call) whose element types declare
    /// no complex properties.
    /// </summary>
    public T? MaterializeNativeGraphPaths<T>(
        IReadOnlyList<GraphRecord> records,
        (Type Source, Type Relationship, Type Target) graphPathTypes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();

        var targetType = typeof(T);
        var elementType = GraphResultTypeHelpers.GetTargetTypeIfCollection(targetType);
        var isCollectionType = targetType != elementType;
        if (elementType != typeof(IGraphPath))
        {
            throw new GraphException(
                $"Native graph paths can only materialize as {nameof(IGraphPath)} or a collection of it.");
        }

        var paths = new List<IGraphPath>(records.Count);
        foreach (var record in records)
        {
            var path = record["p"];
            var hops = _resultProcessor.ProcessNativeGraphPath(
                path,
                graphPathTypes.Source,
                graphPathTypes.Relationship,
                graphPathTypes.Target);
            paths.Add(MaterializeGraphPath(hops, graphPathTypes));
        }

        return isCollectionType
            ? ConvertToCollectionType<T>([.. paths], elementType)
            : (T?)paths.FirstOrDefault();
    }

    /// <summary>Processes one decomposed graph-path hop.</summary>
    public GraphResultProcessor.GraphPathHop ProcessGraphPathHop(
        GraphRecord record,
        (Type Source, Type Relationship, Type Target) graphPathTypes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return _resultProcessor.ProcessGraphPathHop(
            record,
            graphPathTypes.Source,
            graphPathTypes.Relationship,
            graphPathTypes.Target);
    }

    /// <summary>Materializes an ordered set of decomposed hops as one graph path.</summary>
    public IGraphPath MaterializeGraphPath(
        IReadOnlyList<GraphResultProcessor.GraphPathHop> orderedHops,
        (Type Source, Type Relationship, Type Target) graphPathTypes)
    {
        var segments = new List<IGraphPathSegment>(orderedHops.Count);
        foreach (var hop in orderedHops.OrderBy(h => h.HopIndex))
        {
            var startNode = (Graph.INode)MaterializeSingleElement<object>(hop.StartNode, graphPathTypes.Source)!;
            var relationship = (Graph.IRelationship)MaterializeSingleElement<object>(hop.Relationship, graphPathTypes.Relationship)!;
            var endNode = (Graph.INode)MaterializeSingleElement<object>(hop.EndNode, graphPathTypes.Target)!;
            segments.Add(new GraphPathHopSegment(startNode, relationship, endNode, hop.Direction));
        }

        if (segments.Count == 0)
            throw new GraphException("Cannot materialize an empty graph path.");

        return new GraphPath(segments[0].StartNode, segments[^1].EndNode, segments);
    }

    private List<IGraphPath> MaterializeGraphPaths(IReadOnlyList<GraphRecord> records, (Type Source, Type Relationship, Type Target) types)
    {
        var hops = _resultProcessor.ProcessGraphPathHops(
            records,
            types.Source,
            types.Relationship,
            types.Target);

        var paths = new List<IGraphPath>();
        var orderedHopsByPath = hops
            .GroupBy(h => h.PathIndex)
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(h => h.HopIndex).ToList());
        foreach (var orderedHops in orderedHopsByPath)
        {
            paths.Add(MaterializeGraphPath(orderedHops, types));
        }

        return paths;
    }

    private T MaterializeAsCollection<T>(List<EntityInfo> entityInfos, Type elementType)
    {
        _logger.LogDebugGraphResultMaterializer156(entityInfos.Count, elementType.Name);

        var elements = entityInfos
            .Select(entityInfo => MaterializeSingleElement<object>(entityInfo, elementType))
            .ToList();

        _logger.LogDebugGraphResultMaterializer164(elements.Count);

        var result = ConvertToCollectionType<T>(elements!, elementType);
        _logger.LogDebugGraphResultMaterializer167(result?.GetType().Name ?? "null");

        return result;
    }

    private TResult? MaterializeSingleElement<TResult>(EntityInfo? entityInfo, Type elementType)
    {
        if (entityInfo is null)
            return default(TResult);

        // Check if we should use the actual type from EntityInfo instead of elementType
        var typeToDeserialize = entityInfo.ActualType ?? elementType;
        var canDeserialize = _entityFactory.CanDeserialize(typeToDeserialize);

        _logger.LogDebugGraphResultMaterializer181(elementType.Name, entityInfo.ActualType?.Name ?? "null", canDeserialize);

        var result = canDeserialize
            ? _entityFactory.Deserialize(entityInfo)
            : CreateObjectFromEntityInfo(entityInfo, elementType);

        return result is null ? default(TResult) : (TResult)result;
    }

    private object? CreateObjectFromEntityInfo(EntityInfo entityInfo, Type targetType)
    {
        if (targetType == typeof(object) && entityInfo.SimpleProperties.Count == 1 && entityInfo.ComplexProperties.Count == 0)
        {
            return CreateSimpleValue(entityInfo, targetType);
        }

        // Handle path segments specially
        if (IsPathSegmentType(targetType))
        {
            return CreatePathSegmentFromEntityInfo(entityInfo, targetType);
        }

        // Handle simple value projections
        if (GraphDataModel.IsSimple(targetType))
        {
            return CreateSimpleValue(entityInfo, targetType);
        }

        // Handle complex object construction
        return CreateComplexObject(entityInfo, targetType);
    }

    private object CreatePathSegmentFromEntityInfo(EntityInfo entityInfo, Type interfaceType)
    {
        var genericArgs = interfaceType.GetGenericArguments();
        var concreteType = typeof(GraphPathSegment<,,>).MakeGenericType(genericArgs);

        // Extract the three components from the path segment EntityInfo
        var startNodeEntityInfo = GetComplexProperty(entityInfo, "StartNode");
        var relationshipEntityInfo = GetComplexProperty(entityInfo, "Relationship");
        var endNodeEntityInfo = GetComplexProperty(entityInfo, "EndNode");

        // Materialize each component
        var startNode = MaterializeSingleElement<object>(startNodeEntityInfo, genericArgs[0]);
        var relationship = MaterializeSingleElement<object>(relationshipEntityInfo, genericArgs[1]);
        var endNode = MaterializeSingleElement<object>(endNodeEntityInfo, genericArgs[2]);
        var direction = entityInfo.SimpleProperties.TryGetValue(nameof(IGraphPathSegment.Direction), out var property) &&
            property.Value is SimpleValue value
                ? (RelationshipDirection)(ConvertValueToTargetType(value.Object, typeof(RelationshipDirection))
                    ?? RelationshipDirection.Outgoing)
                : throw new GraphException("Path segment projection did not include physical direction.");

        // Find and invoke the constructor
        var constructor = concreteType.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == 4)
            ?? throw new InvalidOperationException($"No 4-parameter constructor found for {concreteType.Name}");

        return constructor.Invoke([startNode, relationship, endNode, direction]);
    }

    private static EntityInfo? GetComplexProperty(EntityInfo entityInfo, string propertyName)
    {
        return entityInfo.ComplexProperties.TryGetValue(propertyName, out var property)
            && property.Value is EntityInfo nestedEntityInfo
            ? nestedEntityInfo
            : null;
    }

    private static object? CreateSimpleValue(EntityInfo entityInfo, Type targetType)
    {
        if (entityInfo.SimpleProperties.Count != 1 || entityInfo.ComplexProperties.Count != 0)
        {
            throw new GraphException(
                $"Cannot materialize type '{targetType}' from a projection with " +
                $"{entityInfo.SimpleProperties.Count} simple and {entityInfo.ComplexProperties.Count} complex values.");
        }

        var singleProperty = entityInfo.SimpleProperties.Values.First();
        if (singleProperty.Value is not SimpleValue simpleValue)
        {
            throw new GraphException(
                $"Cannot materialize type '{targetType}' from projection value '{singleProperty.Value?.GetType()}'.");
        }

        if (simpleValue.Object is null && IsNonNullableValueType(targetType))
        {
            throw GraphValueConverter.CreateNullValueException(targetType, parameterName: null);
        }

        return ConvertValueToTargetType(simpleValue.Object, targetType);
    }

    private static object? ConvertValueToTargetType(object? value, Type targetType)
    {
        // Convert provider-neutral scalar values to the requested CLR type.
        return GraphValueConverter.ConvertTo(value, targetType);
    }

    private object? CreateComplexObject(EntityInfo entityInfo, Type targetType)
    {
        // Use the actual type from EntityInfo instead of the target type
        // This allows interfaces like IRelationship to be materialized as their concrete types (e.g., Knows, Friend)
        var typeToInstantiate = entityInfo.ActualType ?? targetType;

        _logger.LogDebugGraphResultMaterializer278(typeToInstantiate.Name, entityInfo.SimpleProperties.Count, entityInfo.ComplexProperties.Count);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebugGraphResultMaterializer283(string.Join(", ", entityInfo.SimpleProperties.Keys));
            _logger.LogDebugGraphResultMaterializer285(string.Join(", ", entityInfo.ComplexProperties.Select(kvp => $"{kvp.Key}:{kvp.Value.Value?.GetType().Name ?? "null"}")));
        }

        var constructors = typeToInstantiate.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .ToList();

        if (constructors.Count == 0)
            throw new InvalidOperationException($"Type {typeToInstantiate.Name} has no accessible constructors");

        // Try each constructor until we find one that works
        foreach (var constructor in constructors)
        {
            var parameters = constructor.GetParameters();
            var values = new object?[parameters.Length];

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebugGraphResultMaterializer306(parameters.Length, string.Join(", ", parameters.Select(p => $"{p.Name}:{p.ParameterType.Name}")));
            }

            for (int i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                var paramName = param.Name ?? $"param{i}";
                var matchingProperty = FindPropertyInEntityInfo(entityInfo, paramName);

                _logger.LogDebugGraphResultMaterializer316(paramName, matchingProperty != null ? "FOUND" : "NOT_FOUND", matchingProperty?.Value?.GetType().Name ?? "null");

                values[i] = matchingProperty?.Value switch
                {
                    SimpleValue simpleValue => ConvertToParameterType(simpleValue.Object, param),
                    EntityInfo complexEntityInfo => MaterializeSingleElement<object>(complexEntityInfo, param.ParameterType),
                    EntityCollection entityCollection => MaterializeEntityCollection(
                        entityCollection,
                        param,
                        matchingProperty.Label),
                    _ => GetMissingParameterValue(param)
                };

                _logger.LogDebugGraphResultMaterializer327(paramName, values[i]?.GetType().Name ?? "null");
            }

            try
            {
                // Use constructor.Invoke instead of Activator.CreateInstance to avoid ambiguity.
                var result = constructor.Invoke(values);
                _logger.LogDebugGraphResultMaterializer335(typeToInstantiate.Name);
                return result;
            }
            catch (Exception ex) when (ex is ArgumentException or AmbiguousMatchException or TargetParameterCountException)
            {
                _logger.LogDebugGraphResultMaterializer340(ex.Message);
                // The values do not fit this constructor's shape; try the next constructor.
                continue;
            }
        }

        throw new InvalidOperationException($"Could not find a suitable constructor for type {typeToInstantiate.Name}");
    }


    private static Property? FindPropertyInEntityInfo(EntityInfo entityInfo, string paramName)
    {
        // Try exact matches first
        if (entityInfo.SimpleProperties.TryGetValue(paramName, out var simpleProperty))
            return simpleProperty;

        if (entityInfo.ComplexProperties.TryGetValue(paramName, out var complexProperty))
            return complexProperty;

        // Try case-insensitive matches
        return entityInfo.SimpleProperties
            .FirstOrDefault(kv => string.Equals(kv.Key, paramName, StringComparison.OrdinalIgnoreCase)).Value
            ?? entityInfo.ComplexProperties
               .FirstOrDefault(kv => string.Equals(kv.Key, paramName, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static object? ConvertToParameterType(object? value, ParameterInfo parameter)
    {
        return GraphValueConverter.ConvertTo(value, parameter.ParameterType, parameter);
    }

    private static object? GetMissingParameterValue(ParameterInfo parameter)
    {
        if (parameter.HasDefaultValue)
        {
            return parameter.DefaultValue;
        }

        if (NullabilityDerivation.IsParameterNullable(parameter))
        {
            return null;
        }

        throw GraphValueConverter.CreateNullValueException(
            parameter.ParameterType,
            ParameterName(parameter));
    }

    private static bool IsNonNullableValueType(Type type) =>
        type.IsValueType && Nullable.GetUnderlyingType(type) is null;

    private static string ParameterName(ParameterInfo parameter) =>
        parameter.Name ?? $"param{parameter.Position}";

    private static bool IsPathSegmentType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IGraphPathSegment<,,>);

    private object? MaterializeEntityCollection(
        EntityCollection collection,
        ParameterInfo parameter,
        string propertyLabel)
    {
        var targetType = parameter.ParameterType;
        var elementType = GraphResultTypeHelpers.GetTargetTypeIfCollection(targetType);
        var elements = new List<object?>(collection.Entities.Count);
        var isComplexCollection = !GraphDataModel.IsSimple(elementType);

        bool? isElementNullable = null;
        var index = 0;
        foreach (var entity in collection.Entities)
        {
            var value = entity is null
                ? null
                : MaterializeSingleElement<object>(entity, elementType);
            if (value is null)
            {
                isElementNullable ??= NullabilityDerivation.IsElementNullable(parameter, elementType);
                if (isElementNullable is false)
                {
                    throw isComplexCollection
                        ? GraphValueConverter.CreateInvalidComplexCollectionElementException(
                            propertyLabel,
                            elementType,
                            index,
                            actualType: null)
                        : GraphValueConverter.CreateNullElementException(
                            $"Constructor parameter '{ParameterName(parameter)}'",
                            elementType,
                            index);
                }
            }
            else if (isComplexCollection)
            {
                var actualType = value.GetType();
                if (!(Nullable.GetUnderlyingType(elementType) ?? elementType).IsAssignableFrom(actualType))
                {
                    throw GraphValueConverter.CreateInvalidComplexCollectionElementException(
                        propertyLabel,
                        elementType,
                        index,
                        actualType);
                }
            }

            elements.Add(value);
            index++;
        }

        return BuildCollection(elements, elementType, targetType);
    }

    private static object BuildCollection(List<object?> items, Type elementType, Type targetType)
    {
        if (targetType.IsArray)
        {
            var array = Array.CreateInstance(elementType, items.Count);
            for (var index = 0; index < items.Count; index++)
            {
                array.SetValue(items[index], index);
            }

            return array;
        }

        var listType = typeof(List<>).MakeGenericType(elementType);
        var list = (IList)Activator.CreateInstance(listType)!;
        foreach (var item in items)
        {
            list.Add(item);
        }

        return list;
    }

    private static T ConvertToCollectionType<T>(List<object?> items, Type elementType)
    {
        var targetType = typeof(T);

        if (targetType.IsArray)
        {
            var array = Array.CreateInstance(elementType, items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                array.SetValue(items[i], i);
            }
            return Cast<T>(array);
        }

        if (targetType.IsGenericType)
        {
            var genericTypeDefinition = targetType.GetGenericTypeDefinition();

            if (genericTypeDefinition == typeof(List<>) ||
                genericTypeDefinition == typeof(IList<>) ||
                genericTypeDefinition == typeof(ICollection<>) ||
                genericTypeDefinition == typeof(IEnumerable<>))
            {
                var listType = typeof(List<>).MakeGenericType(elementType);
                var list = (IList)Activator.CreateInstance(listType)!;
                foreach (var item in items)
                {
                    list.Add(item);
                }
                return (T)list;
            }
        }

        throw new NotSupportedException($"Cannot convert results to collection type {targetType}");
    }

    private static T Cast<T>(object value)
    {
        return (T)value;
    }
}
