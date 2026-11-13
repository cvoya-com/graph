// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph;

using System.Collections.Concurrent;
using System.Reflection;


/// <summary>
/// Provider-neutral label resolution for graph entity types and properties, including the reverse
/// label-to-type lookups used to recover a type from stored data.
/// </summary>
public static class Labels
{
    // Node labels and relationship types are separate namespaces in the graph model. Keep their reverse
    // caches separate so warming one kind can never replace the other kind's entry for the same text.
    private static readonly ConcurrentDictionary<string, Type> NodeLabelToTypeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Type> RelationshipLabelToTypeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<Type, string> TypeToLabelCache = new();
    private static readonly ConcurrentDictionary<PropertyInfo, string> PropertyToLabelCache = new();
    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo> LabelToPropertyCache = new();
    private static readonly ConcurrentDictionary<(Type targetType, string label), Type?> MostDerivedTypeCache = new();

    /// <summary>
    /// Gets the label associated with an object. It returns the label of the object's actual type,
    /// not the type of the variable used to hold the object when this method is called.
    /// </summary>
    /// <param name="obj">The object</param>
    /// <returns>The label</returns>
    /// <exception cref="GraphException">Thrown when the type doesn't have a valid name</exception>
    public static string GetLabelFromObject(object obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        var type = obj.GetType();
        return GetLabelFromType(type);
    }

    /// <summary>
    /// Gets the label associated with a .NET type.
    /// </summary>
    /// <param name="type">The .NET type</param>
    /// <returns>The label</returns>
    /// <exception cref="GraphException">Thrown when the type doesn't have a valid name</exception>
    public static string GetLabelFromType(Type type)
    {
        TypeToLabelCache.TryGetValue(type, out var label);

        if (label is not null)
        {
            return label;
        }

        label = ResolveLabelFromType(type);

        // CG008/CG009 mirror: two distinct node types (or two distinct relationship types) must not resolve
        // to the same label. Re-resolving the same type - including a different closed form of the same
        // generic definition, see NormalizeForGenericIdentity - is always a silent cache hit. A node label
        // colliding with a relationship type of the same name is explicitly NOT a collision (they are
        // different namespaces in the graph model), so the check only fires within the same kind.
        CacheReverseLookup(type, label);
        TypeToLabelCache[type] = label;
        return label;
    }

    private static void CacheReverseLookup(Type type, string label)
    {
        var cache = GetReverseLookupCache(type);
        if (cache is null)
        {
            return;
        }

        // GetOrAdd atomically returns the existing entry or inserts this type, so concurrent
        // registration of the same label is deterministic and same-kind collisions always surface.
        var existingType = cache.GetOrAdd(label, type);
        if (!AreSameTypeIdentity(existingType, type))
        {
            throw new GraphException(
                $"Label '{label}' is used by both '{existingType.FullName}' and '{type.FullName}'.");
        }
    }

    private static ConcurrentDictionary<string, Type>? GetReverseLookupCache(Type type)
        => GetGraphEntityKind(type) is { } kind ? GetReverseLookupCache(kind) : null;

    /// <summary>
    /// Determines whether <paramref name="left"/> and <paramref name="right"/> should be treated as the same
    /// type identity for label-collision purposes: either they are literally the same <see cref="Type"/>, or
    /// they are different closed constructions of the same generic type definition (e.g. <c>MyNode&lt;int&gt;</c>
    /// and <c>MyNode&lt;string&gt;</c> both normalize to <c>MyNode&lt;&gt;</c>).
    /// </summary>
    private static bool AreSameTypeIdentity(Type left, Type right)
        => NormalizeForGenericIdentity(left) == NormalizeForGenericIdentity(right);

    private static Type NormalizeForGenericIdentity(Type type)
        => type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;

    private static string ResolveLabelFromType(Type type)
    {
        string? label = null;

        // Check for custom label from Node attribute
        var nodeAttr = type.GetCustomAttribute<NodeAttribute>(inherit: false);
        if (nodeAttr?.Label is { Length: > 0 })
        {
            label = nodeAttr.Label;
        }

        // Check for custom label from Relationship attribute
        var relAttr = type.GetCustomAttribute<RelationshipAttribute>(inherit: false);
        if (relAttr?.Label is { Length: > 0 })
        {
            label = relAttr.Label;
        }

        // Fall back to the type name with backticks removed
        return label ?? type.Name.Replace("`", "") ?? throw new GraphException($"Type '{type}' does not have a valid name.");
    }

    /// <summary>
    /// Gets the label associated with a property.
    /// </summary>
    /// <param name="propertyInfo">The .NET property</param>
    /// <returns>The label</returns>
    /// <exception cref="GraphException">Thrown when the property doesn't have a valid name</exception>
    public static string GetLabelFromProperty(PropertyInfo propertyInfo)
    {
        ArgumentNullException.ThrowIfNull(propertyInfo.DeclaringType);

        PropertyToLabelCache.TryGetValue(propertyInfo, out var label);

        if (label is not null)
        {
            return label;
        }

        var propertyAttr = propertyInfo.GetCustomAttribute<PropertyAttribute>(inherit: false);
        label = ResolveLabelFromProperty(propertyInfo, propertyAttr);

        var cacheKey = (propertyInfo.DeclaringType, label);

        // CG007 mirror (partial - see remarks): re-resolving the same property is always a silent cache
        // hit; a different property on the same declaring type resolving to the same label is a collision.
        // This only sees collisions between properties that share a declaring type, since PropertyInfo
        // .DeclaringType for an inherited property is the base type that declares it, not whatever derived
        // type it is queried through - SchemaRegistry.CreateEntitySchemaInfo is the authoritative,
        // full-inheritance-chain mirror of CG007; this is a narrower, complementary check.
        if (LabelToPropertyCache.TryGetValue(cacheKey, out var existingProperty) &&
            !AreSameProperty(existingProperty, propertyInfo))
        {
            throw new GraphException(
                $"Property label '{label}' on '{propertyInfo.DeclaringType.FullName}' is used by both " +
                $"'{propertyInfo.DeclaringType.FullName}.{existingProperty.Name}' and " +
                $"'{propertyInfo.DeclaringType.FullName}.{propertyInfo.Name}'.");
        }

        PropertyToLabelCache[propertyInfo] = label;
        LabelToPropertyCache[cacheKey] = propertyInfo;
        return label;
    }

    private static bool AreSameProperty(PropertyInfo left, PropertyInfo right)
        => left == right || (left.DeclaringType == right.DeclaringType && left.Name == right.Name);

    internal static string ResolveLabelFromProperty(PropertyInfo propertyInfo)
    {
        var propertyAttr = propertyInfo.GetCustomAttribute<PropertyAttribute>(inherit: false);
        return ResolveLabelFromProperty(propertyInfo, propertyAttr);
    }

    private static string ResolveLabelFromProperty(PropertyInfo propertyInfo, PropertyAttribute? propertyAttr)
    {
        if (propertyAttr?.Label is { Length: > 0 })
        {
            return propertyAttr.Label;
        }

        // Fall back to the property name with backticks removed
        return propertyInfo.Name.Replace("`", "") ?? throw new GraphException($"Property '{propertyInfo}' does not have a valid name.");
    }

    /// <summary>
    /// Finds the .NET type for a given label.
    /// </summary>
    /// <param name="label">The label</param>
    /// <returns>The .NET type associated with that label.</returns>
    /// <exception cref="GraphException">If no .NET type was found for the given label.</exception>
    /// <remarks>
    /// Node labels and relationship types occupy separate namespaces and may share the same text. Because
    /// this legacy overload has no entity-kind parameter, it deterministically prefers a node when both a
    /// node and relationship match, regardless of cache warm order. Matching is case-insensitive, while
    /// multiple distinct matches within either entity kind are rejected. This is a Model-layer utility for
    /// recovering a type from a stored label - the portability path when a node's stored metadata type is
    /// not loadable (a different app, or the type was renamed/moved). A provider may use it or implement its
    /// own equivalent resolution; the Neo4j provider does the latter.
    /// </remarks>
    public static Type GetTypeFromLabel(string label)
    {
        ArgumentNullException.ThrowIfNull(label);

        // Always probe the node namespace first rather than returning a warmed relationship entry. That
        // preserves the documented legacy rule independently of prior calls and concurrent cache warming.
        return ResolveTypeFromLabel(label, GraphEntityKind.Node, targetType: null)
            ?? ResolveTypeFromLabel(label, GraphEntityKind.Relationship, targetType: null)
            ?? throw new GraphException($"No type found for label '{label}'.");
    }

    private static Type? ResolveTypeFromLabel(string label, GraphEntityKind kind, Type? targetType)
    {
        var cache = GetReverseLookupCache(kind);
        if (cache.TryGetValue(label, out var cachedType) &&
            (targetType is null || targetType.IsAssignableFrom(cachedType)))
        {
            return cachedType;
        }

        // Select within the whole entity kind, never within the assignability boundary: the reverse
        // caches hold the kind-wide owner of a label, so a boundary-filtered winner must not be selected
        // or cached (it could mask a same-kind collision and poison later unbounded lookups). The
        // boundary applies to the return value only. Abstract types are included to match
        // GetLabelFromType, which resolves labels for abstract bases too; interfaces carry no label.
        var candidates = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(GetLoadableTypes)
            .Where(candidate => !candidate.IsInterface && GetGraphEntityKind(candidate) == kind)
            // Case-insensitive to match the reverse caches and the process-wide uniqueness rule, so a
            // cold-cache lookup resolves the same type a warm one would regardless of label casing.
            .Where(candidate => string.Equals(ResolveLabelFromType(candidate), label, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var type = SelectTypeFromReverseLookupCandidates(label, candidates);

        // Warm both cache directions through the forward path so the canonical attribute/type-name
        // casing is cached rather than this query's casing, and so the same-kind collision rule runs
        // against the kind-wide winner.
        GetLabelFromType(type);

        return targetType is null || targetType.IsAssignableFrom(type) ? type : null;
    }

    private static ConcurrentDictionary<string, Type> GetReverseLookupCache(GraphEntityKind kind)
        => kind == GraphEntityKind.Node ? NodeLabelToTypeCache : RelationshipLabelToTypeCache;

    private enum GraphEntityKind
    {
        Node,
        Relationship,
    }

    /// <summary>
    /// Finds the .NET property for a given label.
    /// </summary>
    /// <param name="label">The label</param>
    /// <param name="enclosingType">The type that contains the property</param>
    /// <returns>The .NET property associated with that label.</returns>
    /// <exception cref="GraphException">If no .NET property was found for the given label.</exception>
    public static PropertyInfo GetPropertyFromLabel(string label, Type enclosingType)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(enclosingType);

        LabelToPropertyCache.TryGetValue((enclosingType, label), out var propertyInfo);

        if (propertyInfo is not null)
        {
            return propertyInfo;
        }

        // Graph entities persist public instance properties; keep reverse lookup on that same boundary.
        var candidates = enclosingType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(prop => ResolveLabelFromProperty(prop) == label)
            .ToList();

        if (candidates.Count == 0)
        {
            throw new GraphException($"No property found for label '{label}' on '{enclosingType.FullName}'.");
        }

        propertyInfo = SelectPropertyFromReverseLookupCandidates(label, enclosingType, candidates);
        LabelToPropertyCache[(enclosingType, label)] = propertyInfo;
        PropertyToLabelCache[propertyInfo] = label;
        return propertyInfo;
    }

    private static Type SelectTypeFromReverseLookupCandidates(string label, IReadOnlyCollection<Type> candidates)
    {
        var distinctCandidates = candidates
            .GroupBy(NormalizeForGenericIdentity)
            .Select(group => group.First())
            .ToList();

        // Candidates are pre-filtered to a single entity kind, so any two distinct identities collide.
        if (distinctCandidates.Count > 1)
        {
            throw new GraphException(
                $"Label '{label}' is used by multiple graph entity types: " +
                string.Join(", ", distinctCandidates.Select(FormatTypeName)) + ".");
        }

        return distinctCandidates[0];
    }

    private static PropertyInfo SelectPropertyFromReverseLookupCandidates(
        string label,
        Type enclosingType,
        IReadOnlyCollection<PropertyInfo> candidates)
    {
        var distinctCandidates = new List<PropertyInfo>();
        foreach (var candidate in candidates.Where(candidate =>
            !distinctCandidates.Any(existing => AreSameProperty(existing, candidate))))
        {
            distinctCandidates.Add(candidate);
        }

        if (distinctCandidates.Count > 1)
        {
            throw new GraphException(
                $"Property label '{label}' on '{enclosingType.FullName}' is used by multiple properties: " +
                string.Join(", ", distinctCandidates.Select(FormatPropertyName)) + ".");
        }

        return distinctCandidates[0];
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static type => type is not null).Cast<Type>();
        }
        catch (NotSupportedException)
        {
            return [];
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (FileLoadException)
        {
            return [];
        }
        catch (BadImageFormatException)
        {
            return [];
        }
        catch (TypeLoadException)
        {
            return [];
        }
    }

    private static string FormatTypeName(Type type)
        => $"'{type.FullName}'";

    private static string FormatPropertyName(PropertyInfo property)
        => $"'{property.DeclaringType?.FullName}.{property.Name}'";

    internal static void ClearCachesForTesting()
    {
        NodeLabelToTypeCache.Clear();
        RelationshipLabelToTypeCache.Clear();
        TypeToLabelCache.Clear();
        PropertyToLabelCache.Clear();
        LabelToPropertyCache.Clear();
        MostDerivedTypeCache.Clear();
        GraphDataModel.ClearComplexPropertyDepthCache();
    }

    /// <summary>
    /// Finds the most derived type that matches the given label and is assignable to the target type.
    /// </summary>
    /// <param name="targetType">The base type that the result must be assignable to (cannot be an interface)</param>
    /// <param name="label">The label to match</param>
    /// <returns>The type that matches the label and is assignable to targetType,
    /// or null if no matching type is found</returns>
    /// <exception cref="GraphException">
    /// If the label is claimed by multiple distinct types within the resolved entity kind.
    /// </exception>
    /// <remarks>
    /// Graph entity targets resolve within their own kind (node or relationship) and assignability
    /// boundary; non-entity targets keep the node-first rule documented on
    /// <see cref="GetTypeFromLabel"/>. Results - including "no match" - are cached per
    /// (targetType, label) for the process lifetime, like every other cache on this class.
    /// </remarks>
    public static Type? GetMostDerivedType(Type targetType, string label)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentNullException.ThrowIfNull(label);

        if (targetType.IsInterface)
        {
            throw new ArgumentException("Target type cannot be an interface", nameof(targetType));
        }

        var cacheKey = (targetType, label);

        // Honor negative entries too: an unresolvable (targetType, label) pair would otherwise rescan
        // every loaded assembly on each call.
        if (MostDerivedTypeCache.TryGetValue(cacheKey, out var cachedType))
        {
            return cachedType;
        }

        var kind = GetGraphEntityKind(targetType);
        Type? resolvedType;
        if (kind is not null)
        {
            resolvedType = ResolveTypeFromLabel(label, kind.Value, targetType);
        }
        else
        {
            // Non-entity targets (e.g. object) keep the legacy node-first rule of GetTypeFromLabel. The
            // kind-scoped resolver keeps "label unknown" (null) distinct from a same-kind collision,
            // which throws here exactly as it does on the kind-aware path above.
            var typeFromLabel = ResolveTypeFromLabel(label, GraphEntityKind.Node, targetType: null)
                ?? ResolveTypeFromLabel(label, GraphEntityKind.Relationship, targetType: null);
            resolvedType = typeFromLabel is not null && targetType.IsAssignableFrom(typeFromLabel)
                ? typeFromLabel
                : null;
        }

        MostDerivedTypeCache[cacheKey] = resolvedType;
        return resolvedType;
    }

    private static GraphEntityKind? GetGraphEntityKind(Type type)
    {
        if (typeof(INode).IsAssignableFrom(type))
        {
            return GraphEntityKind.Node;
        }

        return typeof(IRelationship).IsAssignableFrom(type)
            ? GraphEntityKind.Relationship
            : null;
    }

    /// <summary>
    /// Gets all labels that are compatible with the target type, considering inheritance hierarchies.
    /// This includes the target type's label and all labels of types that derive from the target type.
    /// </summary>
    /// <param name="targetType">The base type to find compatible labels for</param>
    /// <returns>A list of labels that represent types assignable to the target type</returns>
    /// <remarks>
    /// Returns one label per type (<see cref="GetLabelFromType"/>) - the target type's label plus the label
    /// of each concrete type that derives from it. This is how a polymorphic query
    /// (e.g. <c>Nodes&lt;Person&gt;()</c>) matches subtypes: the class hierarchy is expanded to the set of
    /// compatible labels at query-construction time. Only subtypes the registry has discovered
    /// (their assembly loaded) are included.
    /// </remarks>
    public static List<string> GetCompatibleLabels(Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);

        var labels = new List<string>();

        // Always include the target type's own label
        labels.Add(GetLabelFromType(targetType));

        // Find all types in loaded assemblies that derive from the target type
        var derivedTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(assembly =>
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // Handle cases where some types can't be loaded
                    return ex.Types.Where(t => t != null).Cast<Type>();
                }
                catch
                {
                    return Enumerable.Empty<Type>();
                }
            })
            .Where(t => t.IsClass && !t.IsAbstract &&
                       targetType.IsAssignableFrom(t) &&
                       t != targetType) // Exclude the target type itself since we already added it
            .ToList();

        // Add labels for all derived types
        foreach (var derivedType in derivedTypes)
        {
            var derivedLabel = GetLabelFromType(derivedType);
            if (!labels.Contains(derivedLabel))
            {
                labels.Add(derivedLabel);
            }
        }

        return labels;
    }
}
