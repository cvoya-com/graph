// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph;

using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;


/// <summary>
/// Checks to enforce behavior expected by the graph model.
/// </summary>
public static class GraphDataModel
{
    /// <summary>
    /// The default maximum depth allowed for traversing complex properties of an entity.
    /// This is used to prevent infinite recursion when traversing complex properties that may reference other entities.
    /// The default value is set to 5, which means that the traversal will stop after 5 levels of depth.
    /// </summary>
    public const int DefaultDepthAllowed = 5;

    /// <summary>
    /// Converts a property name to the conventional semantic relationship type used for a complex property.
    /// </summary>
    /// <param name="propertyName"> The name of the property to convert</param>
    /// <returns>The relationship type name for the property.</returns>
    public static string PropertyNameToRelationshipTypeName(string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        return propertyName;
    }

    /// <summary>
    /// Gets the semantic relationship type used to store a complex property.
    /// </summary>
    /// <param name="property">The complex property.</param>
    /// <returns>The configured relationship type, or the property name by convention.</returns>
    public static string GetComplexPropertyRelationshipType(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);

        var configured = property.GetCustomAttribute<ComplexPropertyAttribute>(inherit: true)?.RelationshipType;
        return string.IsNullOrWhiteSpace(configured) ? property.Name : configured;
    }

    /// <summary>
    /// Converts a semantic complex-property relationship type back to its conventional property name.
    /// </summary>
    /// <remarks>
    /// The reverse mapping is exact only for the convention-based case (relationship type equals the
    /// property name). When <see cref="ComplexPropertyAttribute.RelationshipType"/> overrides the
    /// type, the override is returned unchanged; the mapping is not invertible under overrides.
    /// </remarks>
    /// <param name="relationshipTypeName">The relationship type name to convert.</param>
    /// <returns>The conventional property name.</returns>
    public static string RelationshipTypeNameToPropertyName(string relationshipTypeName) => relationshipTypeName;

    /// <summary>
    /// Gets the simple properties of a type.
    /// </summary>
    /// <param name="type">The type to examine</param>
    /// <returns>>An enumerable of simple properties</returns>
    public static IEnumerable<PropertyInfo> GetSimpleProperties(Type type) =>
        type.GetProperties()
            .Where(p => IsSimple(p.PropertyType) || IsCollectionOfSimple(p.PropertyType));

    /// <summary>
    /// Gets the complex properties of a type.
    /// </summary>
    /// <param name="type">The type to examine</param>
    /// <returns>An enumerable of complex properties</returns>
    public static IEnumerable<PropertyInfo> GetComplexProperties(Type type) =>
        type.GetProperties()
            .Where(p => IsComplex(p.PropertyType) || IsCollectionOfComplex(p.PropertyType));

    private static readonly ConcurrentDictionary<(Type Type, int MaximumDepth), int> ComplexPropertyDepths = new();
    private static readonly object DerivedTypeIndexLock = new();
    private static IReadOnlyDictionary<Type, Type[]>? derivedTypesByBase;
    private static int derivedTypeIndexBuildCount;
    private static int derivedTypeIndexGeneration;
    private static WeakReference<Assembly>[] derivedTypeIndexAssemblies = [];

    static GraphDataModel()
    {
        derivedTypeIndexAssemblies = SnapshotAssemblies(AppDomain.CurrentDomain.GetAssemblies());

        // The bound of a non-sealed value type includes the derived types that are loaded. A
        // derived type discovered after the first query must invalidate the memoized depth and the
        // shared derived-type index, or hydration would keep the smaller bound of the earlier type
        // set.
        AppDomain.CurrentDomain.AssemblyLoad += static (_, _) => InvalidateDerivedTypeIndex();
    }

    /// <summary>
    /// Gets a counter that changes whenever the set of loaded assemblies invalidates the
    /// derived-type index.
    /// </summary>
    internal static int DerivedTypeIndexGeneration => Volatile.Read(ref derivedTypeIndexGeneration);

    /// <summary>Gets how many times the shared derived-type index has been built (test seam).</summary>
    internal static int DerivedTypeIndexBuildCount => Volatile.Read(ref derivedTypeIndexBuildCount);

    /// <summary>
    /// Checks the loaded-assembly set at plan time and invalidates a derived-type index that a
    /// missed or concurrent load made stale.
    /// </summary>
    internal static void EnsureDerivedTypeIndexCurrent() =>
        EnsureDerivedTypeIndexCurrent(AppDomain.CurrentDomain.GetAssemblies());

    /// <summary>
    /// Checks the supplied loaded-assembly identity set and invalidates a derived-type index built
    /// from a different set. The array overload is a test seam; production always supplies the
    /// current AppDomain set.
    /// </summary>
    internal static void EnsureDerivedTypeIndexCurrent(Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        lock (DerivedTypeIndexLock)
        {
            if (AssembliesMatch(derivedTypeIndexAssemblies, assemblies))
            {
                return;
            }

            InvalidateDerivedTypeIndex();
            Volatile.Write(ref derivedTypeIndexAssemblies, SnapshotAssemblies(assemblies));
        }
    }

    /// <summary>Resets the derived-type index and depth cache for deterministic tests.</summary>
    internal static void ResetDerivedTypeIndexForTests()
    {
        lock (DerivedTypeIndexLock)
        {
            Volatile.Write(ref derivedTypesByBase, null);
            Volatile.Write(ref derivedTypeIndexBuildCount, 0);
            Volatile.Write(
                ref derivedTypeIndexAssemblies,
                SnapshotAssemblies(AppDomain.CurrentDomain.GetAssemblies()));
        }

        ComplexPropertyDepths.Clear();
    }

    /// <summary>
    /// Snapshots the loaded assemblies as weak references. The snapshot is compared on every plan,
    /// so it must not keep a collectible plugin assembly alive after its unload. The runtime returns
    /// each loaded assembly once, so an order-insensitive identity-set comparison is sufficient.
    /// </summary>
    private static WeakReference<Assembly>[] SnapshotAssemblies(Assembly[] assemblies)
    {
        var snapshot = new WeakReference<Assembly>[assemblies.Length];
        for (var index = 0; index < assemblies.Length; index++)
        {
            snapshot[index] = new WeakReference<Assembly>(assemblies[index]);
        }

        return snapshot;
    }

    /// <summary>
    /// Compares the stored weak identity snapshot with the current loaded assemblies in one pass.
    /// A missing weak target means the assembly was collected (unloaded); comparing by identity
    /// still rejects a same-count unload-plus-load swap, which a count comparison would accept.
    /// </summary>
    private static bool AssembliesMatch(
        WeakReference<Assembly>[] snapshot,
        Assembly[] current)
    {
        if (snapshot.Length != current.Length)
        {
            return false;
        }

        var loaded = new HashSet<Assembly>(current, ReferenceEqualityComparer.Instance);
        foreach (var reference in snapshot)
        {
            if (!reference.TryGetTarget(out var assembly) || !loaded.Contains(assembly))
            {
                return false;
            }
        }

        return true;
    }

    private static void InvalidateDerivedTypeIndex()
    {
        ClearComplexPropertyDepthCache();
        Interlocked.Increment(ref derivedTypeIndexGeneration);
    }


    /// <summary>
    /// Gets the maximum number of complex-property relationships between an instance of
    /// <paramref name="type"/> and its deepest declared complex value.
    /// </summary>
    /// <remarks>
    /// The result is the bound a read-side complex-property traversal needs for the type; for a
    /// depth-1 property such as <c>Address</c> it is one, so the traversal does not have to scan
    /// the global <see cref="DefaultDepthAllowed"/> range. Collection element types contribute
    /// their element depth, and a type that recursively references itself is reported as
    /// <paramref name="maximumDepth"/>: writes reject such data, but legacy rows must never be
    /// under-hydrated.
    /// <para>
    /// A non-sealed value type can store an instance of a derived type, and hydration resolves the
    /// concrete type from the stored metadata. The bound therefore also walks every concrete type
    /// derived from the property type that is loaded in the current process, so a derived-only
    /// nested complex value keeps its full depth without falling back to the global maximum. The
    /// derived set follows the same loaded-assembly rule as label resolution; the cache is cleared
    /// when a new assembly is loaded.
    /// </para>
    /// <para>
    /// A collectible plugin assembly is never retained by the shared derived-type index or depth
    /// cache: a cached <see cref="Type"/> would root its assembly load context and prevent the
    /// plugin from unloading. A collectible type is measured against a transient all-loaded-assembly
    /// index instead. A non-collectible base can also be extended by a collectible plugin type, so
    /// while plugin assemblies are loaded the shared index is combined with a transient scan of
    /// those plugins. Only the resulting integer is cached; no plugin <see cref="Type"/> survives
    /// the measurement.
    /// </para>
    /// </remarks>
    /// <param name="type">The root entity or complex value type.</param>
    /// <param name="maximumDepth">The absolute storage bound; defaults to <see cref="DefaultDepthAllowed"/>.</param>
    /// <returns>The deepest declared relationship count, between zero and <paramref name="maximumDepth"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumDepth"/> is less than one.</exception>
    public static int GetComplexPropertyDepth(Type type, int maximumDepth = DefaultDepthAllowed)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDepth, 1);

        // A collectible type may unload as soon as its plugin is released. A Type value roots
        // its AssemblyLoadContext, so a cached key or value would keep the whole plugin alive.
        // Measure a collectible root against a transient index over every loaded assembly, so a
        // hierarchy split across several plugin assemblies is measured as one set; the index is
        // dropped as soon as this call returns.
        // A constructed generic can be collectible through an argument even when its defining
        // assembly is not collectible.
        if (type.IsCollectible)
        {
            var transientScope = DerivedTypeScope.ForAllLoadedAssemblies();
            return MeasureDeclaredAndDerivedDepth(
                type,
                0,
                maximumDepth,
                new HashSet<Type>(),
                transientScope);
        }

        return ComplexPropertyDepths.GetOrAdd((type, maximumDepth), static key =>
        {
            // A non-collectible base can still be extended by a collectible plugin type. The
            // shared index excludes collectible assemblies, so combine it with a transient scan of
            // the loaded plugin assemblies for the duration of this measurement. The transient map
            // is dropped with the scope; only the resulting integer reaches the cache.
            var scope = DerivedTypeScope.ForCurrentProcess();
            var path = new HashSet<Type>();
            return MeasureDeclaredAndDerivedDepth(key.Type, 0, key.MaximumDepth, path, scope);
        });
    }

    internal static void ClearComplexPropertyDepthCache()
    {
        ComplexPropertyDepths.Clear();
        Volatile.Write(ref derivedTypesByBase, null);
    }

    private static int MeasureComplexPropertyDepth(
        Type type,
        int depth,
        int maximumDepth,
        HashSet<Type> path,
        DerivedTypeScope scope)
    {
        var effectiveType = GetCollectionElementType(type) ?? type;
        if (!path.Add(effectiveType))
        {
            return maximumDepth;
        }

        var measured = depth;
        foreach (var property in GetComplexProperties(effectiveType))
        {
            var childType = GetCollectionElementType(property.PropertyType) ?? property.PropertyType;
            var childDepth = depth + 1;
            measured = Math.Max(
                measured,
                childDepth >= maximumDepth
                    ? maximumDepth
                    : MeasureDeclaredAndDerivedDepth(childType, childDepth, maximumDepth, path, scope));
        }

        path.Remove(effectiveType);
        return measured;
    }

    /// <summary>
    /// Measures the declared property type and, for a non-sealed type, every concrete derived type
    /// whose properties can be materialized from an instance stored as the declared type.
    /// </summary>
    private static int MeasureDeclaredAndDerivedDepth(
        Type type,
        int depth,
        int maximumDepth,
        HashSet<Type> path,
        DerivedTypeScope scope)
    {
        var effectiveType = GetCollectionElementType(type) ?? type;
        var measured = MeasureComplexPropertyDepth(effectiveType, depth, maximumDepth, path, scope);
        if (effectiveType.IsSealed || effectiveType.IsValueType)
        {
            return measured;
        }

        foreach (var derivedType in scope.GetDerivedTypes(effectiveType))
        {
            measured = Math.Max(
                measured,
                MeasureComplexPropertyDepth(derivedType, depth, maximumDepth, path, scope));
        }

        return measured;
    }

    /// <summary>
    /// A per-measurement view of the derived types that are currently loaded.
    /// </summary>
    /// <remarks>
    /// The shared index is reused for non-collectible types so the common path stays amortized. A
    /// collectible plugin hierarchy cannot live in that index, because a <see cref="Type"/> value
    /// roots its <see cref="System.Runtime.Loader.AssemblyLoadContext"/>. The scope therefore
    /// builds every plugin hierarchy into transient maps that are dropped with the scope when the
    /// top-level measurement returns. Only those maps live for the duration of one call; nothing
    /// is retained statically.
    /// </remarks>
    private sealed class DerivedTypeScope
    {
        private readonly Assembly[] assemblies;
        private readonly Assembly[] collectibleAssemblies;
        private readonly bool allAssemblies;
        private IReadOnlyDictionary<Type, Type[]>? collectibleIndex;
        private IReadOnlyDictionary<Type, Type[]>? allAssembliesIndex;
        private IReadOnlyDictionary<Type, Type[]>? sharedIndex;

        private DerivedTypeScope(Assembly[] assemblies, bool allAssemblies)
        {
            this.assemblies = assemblies;
            this.allAssemblies = allAssemblies;
            collectibleAssemblies = allAssemblies
                ? []
                : assemblies.Where(assembly => assembly.IsCollectible).ToArray();
        }

        public static DerivedTypeScope ForCurrentProcess() =>
            new(AppDomain.CurrentDomain.GetAssemblies(), allAssemblies: false);

        public static DerivedTypeScope ForAllLoadedAssemblies() =>
            new(AppDomain.CurrentDomain.GetAssemblies(), allAssemblies: true);

        public Type[] GetDerivedTypes(Type type)
        {
            // A collectible root must be measured against every loaded assembly: its derived type
            // can live in another collectible plugin assembly. The all-assembly map is transient
            // and shared by every step of this one measurement.
            if (allAssemblies || type.Assembly.IsCollectible)
            {
                var transient = allAssembliesIndex ??=
                    BuildDerivedTypeIndex(assemblies, includeCollectibleAssemblies: true);
                return Lookup(transient, type);
            }

            sharedIndex ??= GetDerivedTypeIndex();
            var sharedTypes = Lookup(sharedIndex, type);
            if (collectibleAssemblies.Length == 0)
            {
                return sharedTypes;
            }

            // A non-collectible base can be extended by a plugin type. Scan only the collectible
            // assemblies for the plugin half; the shared index already covers the rest.
            collectibleIndex ??= BuildDerivedTypeIndex(
                collectibleAssemblies,
                includeCollectibleAssemblies: true);
            return Merge(sharedTypes, Lookup(collectibleIndex, type));
        }

        private static Type[] Lookup(IReadOnlyDictionary<Type, Type[]> index, Type type) =>
            index.TryGetValue(type, out var derivedTypes) ? derivedTypes : [];

        private static Type[] Merge(Type[] sharedTypes, Type[] pluginTypes)
        {
            if (pluginTypes.Length == 0)
            {
                return sharedTypes;
            }

            if (sharedTypes.Length == 0)
            {
                return pluginTypes;
            }

            var merged = new Type[sharedTypes.Length + pluginTypes.Length];
            sharedTypes.CopyTo(merged, 0);
            pluginTypes.CopyTo(merged, sharedTypes.Length);
            return merged;
        }
    }

    /// <summary>
    /// Builds one derived-type map per assembly generation. A per-type all-assembly scan makes the
    /// first query for each non-sealed type pay for every loaded assembly; a single pass amortizes
    /// that cost across all types and keeps the lookup O(1).
    /// </summary>
    /// <remarks>
    /// Collectible assemblies are excluded from the shared map. A <see cref="Type"/> value roots
    /// its <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, so indexing a plugin type would
    /// defeat the weak assembly snapshot and prevent the plugin from unloading.
    /// </remarks>
    private static IReadOnlyDictionary<Type, Type[]> GetDerivedTypeIndex()
    {
        var index = Volatile.Read(ref derivedTypesByBase);
        if (index is not null)
        {
            return index;
        }

        lock (DerivedTypeIndexLock)
        {
            index = Volatile.Read(ref derivedTypesByBase);
            if (index is not null)
            {
                return index;
            }

            // Snapshot the assembly identity set used for the build. A load or unload that
            // happens while the index is built leaves an identity-set mismatch, so the next
            // plan-time check rebuilds instead of keeping an index that predates the type set.
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            index = BuildDerivedTypeIndex(assemblies);
            Volatile.Write(ref derivedTypesByBase, index);
            Interlocked.Increment(ref derivedTypeIndexBuildCount);
            Volatile.Write(ref derivedTypeIndexAssemblies, SnapshotAssemblies(assemblies));
            return index;
        }
    }

    private static Dictionary<Type, Type[]> BuildDerivedTypeIndex(
        Assembly[] assemblies,
        bool includeCollectibleAssemblies = false)
    {
        var byBase = new Dictionary<Type, List<Type>>();

        foreach (var assembly in assemblies)
        {
            if (!includeCollectibleAssemblies && assembly.IsCollectible)
            {
                continue;
            }

            Type?[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types;
            }
            catch (Exception exception) when (IsReflectionFailure(exception))
            {
                continue;
            }

            foreach (var candidate in types)
            {
                if (candidate is null ||
                    candidate.IsAbstract ||
                    candidate.IsInterface ||
                    candidate.ContainsGenericParameters)
                {
                    continue;
                }

                Type[] interfaces;
                try
                {
                    interfaces = candidate.GetInterfaces();
                }
                catch (Exception exception) when (IsReflectionFailure(exception))
                {
                    // A collectible, plugin, or partially loadable type cannot participate in a
                    // hydration hierarchy; skip it instead of failing the whole index build.
                    continue;
                }

                for (var baseType = candidate.BaseType;
                     baseType is not null && baseType != typeof(object);
                     baseType = baseType.BaseType)
                {
                    AddCandidate(byBase, baseType, candidate);
                }

                foreach (var interfaceType in interfaces)
                {
                    AddCandidate(byBase, interfaceType, candidate);
                }
            }
        }

        return byBase
            .Where(pair => includeCollectibleAssemblies || !pair.Key.Assembly.IsCollectible)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Distinct().OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray());
    }

    private static bool IsReflectionFailure(Exception exception) =>
        exception is NotSupportedException or FileNotFoundException or FileLoadException or
        BadImageFormatException or TypeLoadException;

    private static void AddCandidate(Dictionary<Type, List<Type>> byBase, Type baseType, Type candidate)
    {
        try
        {
            if (baseType.IsAssignableFrom(candidate))
            {
                if (!byBase.TryGetValue(baseType, out var candidates))
                {
                    candidates = [];
                    byBase.Add(baseType, candidates);
                }

                candidates.Add(candidate);
            }
        }
        catch (ArgumentException)
        {
            // Some dynamic or collectible types cannot be compared; they cannot participate in a
            // hydration hierarchy either.
        }
    }

    /// <summary>
    /// Gets the simple and complex properties of an object.
    /// </summary>
    /// <param name="obj">The object to examine</param>
    /// <returns>A tuple containing dictionaries of simple and complex properties</returns>
    public static (IDictionary<PropertyInfo, object?>, IDictionary<PropertyInfo, object?>) GetSimpleAndComplexProperties(object obj)
    {
        var type = obj.GetType();
        var simpleProperties = GetSimpleProperties(type).ToDictionary(p => p, p => p.GetValue(obj));
        var complexProperties = GetComplexProperties(type).ToDictionary(p => p, p => p.GetValue(obj));

        return (simpleProperties, complexProperties);
    }

    /// <summary>
    /// Ensures that an entity has no reference cycles.
    /// </summary>
    /// <param name="entity">The entity to check</param>
    /// <exception cref="GraphException">Thrown if a reference cycle is detected</exception>
    public static void EnsureNoReferenceCycle(this IEntity entity)
    {
        if (HasReferenceCycle(entity))
        {
            throw new GraphException($"Reference cycle detected in entity type '{entity.GetType().Name}'.");
        }
    }

    /// <summary>
    /// Ensures that an entity's complex-property graph does not exceed the supported storage depth.
    /// </summary>
    /// <param name="entity">The entity to check.</param>
    /// <param name="maximumDepth">The maximum number of complex-property relationships from the entity root.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumDepth"/> is less than one.</exception>
    /// <exception cref="GraphException">The complex-property graph is deeper than <paramref name="maximumDepth"/>.</exception>
    public static void EnsureComplexPropertyDepth(
        this IEntity entity,
        int maximumDepth = DefaultDepthAllowed)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDepth, 1);

        var currentPath = new HashSet<object>(ReferenceEqualityComparer.Instance);
        CheckComplexPropertyDepth(entity, depth: 0, maximumDepth, currentPath);
    }

    /// <summary>
    /// Ensures that a value assigned directly to a root complex property is acyclic and does not
    /// exceed the supported storage depth. The assigned value itself is one relationship below
    /// the owning graph node.
    /// </summary>
    internal static void EnsureComplexPropertyValueDepth(
        object? value,
        int maximumDepth = DefaultDepthAllowed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDepth, 1);
        if (value is null)
        {
            return;
        }

        var currentPath = new HashSet<object>(ReferenceEqualityComparer.Instance);
        CheckComplexPropertyDepth(value, depth: 1, maximumDepth, currentPath);
    }

    /// <summary>
    /// Enforces representation constraints for an <see cref="INode"/>.
    /// </summary>
    /// <param name="node">The node to enforce constraints for</param>
    /// <typeparam name="T">The type of the node</typeparam>
    /// <exception cref="GraphException">Thrown if the given node violates any of the constraints</exception>
    public static void EnforceGraphConstraintsForNode<T>(T node)
        where T : class, INode
    {
        EnforceGraphConstraintsForEntity(node);

        EnforceGraphConstraintsForNodeType<T>();
        // Enforce additional graph constraints as needed
        // TODO: Check the property types

        // TODO: in case of an IEnumerable, it MUST be generic.
        // Otherwise, we cannot determine the type of the elements in the collection
        // when deserializing the node.

        // TODO: Prevent the use of IDictionary.
    }

    /// <summary>
    /// Enforces representation constraints for an <see cref="INode"/>.
    /// </summary>
    /// <typeparam name="T">The type of the node</typeparam>
    /// <exception cref="GraphException">Thrown if the given node violates any of the constraints</exception>
    public static void EnforceGraphConstraintsForNodeType<T>()
        where T : class, INode
    {
        // Enforce additional graph constraints as needed
        // TODO: Check the property types
    }

    /// <summary>
    /// Enforces representation constraints for an <see cref="IEntity"/>.
    /// </summary>
    public static void EnforceGraphConstraintsForEntity<T>(T entity)
        where T : IEntity
    {
        // Ensure the entity is not null
        ArgumentNullException.ThrowIfNull(entity, nameof(entity));

        // Ensure the entity has no reference cycles
        entity.EnsureNoReferenceCycle();
        entity.EnsureComplexPropertyDepth();
    }

    /// <summary>
    /// Enforces representation constraints for an <see cref="IRelationship"/>.
    /// </summary>
    /// <typeparam name="T">The type of the relationship</typeparam>
    /// <param name="relationship">The relationship to enforce constraints for</param>
    /// <exception cref="GraphException">Thrown if the given relationship violates any of the constraints</exception>
    public static void EnforceGraphConstraintsForRelationship<T>(T relationship)
        where T : class, IRelationship
    {
        EnforceGraphConstraintsForEntity(relationship);

        EnforceGraphConstraintsForRelationshipType<T>();
    }

    /// <summary>
    /// Enforces representation constraints for an <see cref="IRelationship"/>.
    /// </summary>
    /// <typeparam name="T">The type of the relationship</typeparam>
    /// <exception cref="GraphException">Thrown if the given relationship violates any of the constraints</exception>
    public static void EnforceGraphConstraintsForRelationshipType<T>()
        where T : class, IRelationship
    {
        // Enforce additional graph constraints as needed
        // TODO: Check the property types
    }

    /// <summary>
    /// Checks if a type is considered to be a "simple" type in the context of the graph model.
    /// A "simple" type is one that can be used as the type of the property of an <see cref="INode"/> .
    /// The full list of simple types includes:
    /// <list type="bullet">
    /// <item>Supported .NET primitive types (excluding <see cref="IntPtr"/> and <see cref="UIntPtr"/>)</item>
    /// <item>All .NET enum types</item>
    /// <item><see cref="string"/></item>
    /// <item><see cref="Point"/></item>
    /// <item><see cref="DateTime"/></item>
    /// <item><see cref="DateTimeOffset"/></item>
    /// <item><see cref="TimeSpan"/></item>
    /// <item><see cref="TimeOnly"/></item>
    /// <item><see cref="DateOnly"/></item>
    /// <item><see cref="Guid"/></item>
    /// <item>Byte arrays</item>
    /// <item><see cref="Uri"/></item>
    /// </list>
    /// </summary>
    public static bool IsSimple(Type type)
    {
        // Handle nullable value types
        type = Nullable.GetUnderlyingType(type) ?? type;

        return type switch
        {
            _ when type.IsPrimitive && type != typeof(IntPtr) && type != typeof(UIntPtr) => true,
            _ when type.IsEnum => true,
            _ when type == typeof(string) => true,
            _ when type == typeof(Point) => true,
            _ when type == typeof(DateTime) => true,
            _ when type == typeof(DateTimeOffset) => true,
            _ when type == typeof(TimeSpan) => true,
            _ when type == typeof(TimeOnly) => true,
            _ when type == typeof(DateOnly) => true,
            _ when type == typeof(decimal) => true,
            _ when type == typeof(Guid) => true,
            _ when type == typeof(byte[]) => true,
            _ when type == typeof(Uri) => true,
            _ => false
        };
    }

    /// <summary>
    /// Checks if a type is considered to be a "complex" type in the context of the graph model.
    /// A "complex" type is one that is not a simple type and can be used as the type of the property of an <see cref="INode"/>.
    /// Complex types are typically user-defined classes or structs that do not fall into the simple type categories.
    /// Dictionaries are never considered complex; see <see cref="IsDictionary(Type)"/>.
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    public static bool IsComplex(Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

        if (underlyingType == typeof(object))
            return false;

        if (IsSimple(underlyingType) || IsCollectionOfSimple(underlyingType) || IsCollectionOfComplex(underlyingType))
            return false;

        if (IsDictionary(underlyingType))
            return false;

        // Must be a class or struct to be complex (e.g. excludes interfaces like IEnumerable).
        return underlyingType.IsClass || (underlyingType.IsValueType && !underlyingType.IsEnum);
    }

    /// <summary>
    /// Checks if a type is a dictionary shape (a concrete type implementing the non-generic <see cref="IDictionary"/>,
    /// or any type that is or implements <see cref="IDictionary{TKey,TValue}"/> or <see cref="IReadOnlyDictionary{TKey,TValue}"/>).
    /// Dictionaries are not supported as graph properties; see <see cref="IsComplex(Type)"/>, <see cref="IsCollectionOfSimple(Type)"/>,
    /// and <see cref="IsCollectionOfComplex(Type)"/>, all of which exclude dictionary shapes.
    /// </summary>
    public static bool IsDictionary(Type type)
    {
        if (typeof(IDictionary).IsAssignableFrom(type))
            return true;

        if (type.IsGenericType && IsDictionaryGenericTypeDefinition(type.GetGenericTypeDefinition()))
            return true;

        return type.GetInterfaces().Any(i => i.IsGenericType && IsDictionaryGenericTypeDefinition(i.GetGenericTypeDefinition()));
    }

    private static bool IsDictionaryGenericTypeDefinition(Type genericTypeDefinition) =>
        genericTypeDefinition == typeof(IDictionary<,>) || genericTypeDefinition == typeof(IReadOnlyDictionary<,>);

    /// <summary>
    /// Checks if a type is a collection of simple types. See <see cref="IsSimple(Type)"/> for what is considered a simple type.
    /// Dictionaries are never considered a simple collection; see <see cref="IsDictionary(Type)"/>.
    /// </summary>
    public static bool IsCollectionOfSimple(Type type) =>
        type != typeof(string)
        && IsDictionary(type) is false
        && GetCollectionElementType(type) is { } elementType
        && IsSimple(elementType);

    /// <summary>
    /// Checks if a type is a collection of complex types. See <see cref="IsComplex(Type)"/> for what is considered a complex type.
    /// Dictionaries are never considered a supported collection; see <see cref="IsDictionary(Type)"/>.
    /// </summary>
    public static bool IsCollectionOfComplex(Type type) =>
        type != typeof(string)
        && IsDictionary(type) is false
        && GetCollectionElementType(type) is { } elementType
        && IsComplex(elementType);

    private static Type? GetCollectionElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();

        // A type can implement IEnumerable<T> more than once with different element types. Such a
        // shape has no single element type and must not depend on reflection's interface ordering.
        Type? elementType = null;

        if (IsGenericEnumerable(type))
            elementType = type.GetGenericArguments()[0];

        foreach (var interfaceType in type.GetInterfaces())
        {
            if (!IsGenericEnumerable(interfaceType))
                continue;

            var candidate = interfaceType.GetGenericArguments()[0];
            if (elementType is null)
            {
                elementType = candidate;
            }
            else if (elementType != candidate)
            {
                return null;
            }
        }

        return elementType;
    }

    private static bool IsGenericEnumerable(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>);

    /// <summary>
    /// Checks if an object has reference cycles (true cycles, not just shared references).
    /// </summary>
    /// <param name="obj">The object to check</param>
    /// <returns>True if a reference cycle is detected, false otherwise</returns>
    public static bool HasReferenceCycle(object obj)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var currentPath = new HashSet<object>(ReferenceEqualityComparer.Instance);

        return CheckForCycle(obj, visited, currentPath);
    }

    private static bool CheckForCycle(object obj, HashSet<object> visited, HashSet<object> currentPath)
    {
        // Early exit for null
        if (obj == null)
            return false;

        var type = obj.GetType();

        // Skip value types and common immutable types
        if (type.IsValueType || // This covers DateTime, int, bool, etc.
            type == typeof(string) ||
            type == typeof(Type) ||
            type.IsEnum ||
            type.IsPrimitive)
        {
            return false;
        }

        // Skip collections of simple types
        if (IsCollectionOfSimple(type))
        {
            return false;
        }

        // Skip system types that we know don't have cycles
        if (type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true &&
            !type.Namespace.StartsWith("System.Collections", StringComparison.Ordinal))
        {
            return false;
        }

        // Now do the actual cycle detection for reference types
        if (currentPath.Contains(obj))
            return true;

        if (visited.Contains(obj))
            return false;

        visited.Add(obj);
        currentPath.Add(obj);

        try
        {
            if (obj is IDictionary objDict)
            {
                // Handle IDictionary objects
                foreach (var value in objDict.Values)
                {
                    if (value != null && CheckForCycle(value, visited, currentPath))
                        return true;
                }
            }
            else if (obj is IEnumerable enumerable && !(obj is string))
            {
                // Handle IEnumerable objects
                foreach (var item in enumerable)
                {
                    if (item != null && CheckForCycle(item, visited, currentPath))
                        return true;
                }
            }
            else
            {
                // Check all properties recursively
                foreach (var prop in type.GetProperties())
                {
                    if (prop.CanRead && prop.GetIndexParameters().Length == 0)
                    {
                        var value = prop.GetValue(obj);
                        if (value != null && CheckForCycle(value, visited, currentPath))
                            return true;
                    }
                }
            }
            return false;
        }
        finally
        {
            // CRITICAL: Remove from current path when backtracking!
            currentPath.Remove(obj);
        }
    }

    private static void CheckComplexPropertyDepth(
        object value,
        int depth,
        int maximumDepth,
        HashSet<object> currentPath)
    {
        if (depth > maximumDepth)
        {
            throw new GraphException($"Complex properties cannot exceed {maximumDepth} levels of depth.");
        }

        // Only simple values terminate the walk: IsComplex admits non-enum structs, and a complex
        // struct can transitively hold deeply nested reference-type complex properties that must
        // still be depth-validated. (Boxed structs are fresh references, so the cycle set is safe.)
        var valueType = value.GetType();
        if (value is string || (valueType.IsValueType && IsSimple(valueType)))
            return;

        if (!currentPath.Add(value))
        {
            throw new GraphException("Reference cycle detected while checking complex-property depth.");
        }

        try
        {
            if (value is IEnumerable<KeyValuePair<string, object?>> dynamicDictionary)
            {
                foreach (var item in dynamicDictionary.Select(entry => entry.Value).Where(item => item is not null))
                {
                    var itemType = item!.GetType();
                    if (IsComplex(itemType) || IsCollectionOfComplex(itemType) || IsDictionary(itemType) ||
                        IsCollectionOfDictionaries(itemType))
                    {
                        CheckComplexPropertyDepth(item, depth + 1, maximumDepth, currentPath);
                    }
                }

                return;
            }

            if (value is IDictionary dictionary)
            {
                foreach (var item in dictionary.Values.Cast<object?>().Where(item => item is not null))
                {
                    var itemType = item!.GetType();
                    if (IsComplex(itemType) || IsCollectionOfComplex(itemType) || IsDictionary(itemType) ||
                        IsCollectionOfDictionaries(itemType))
                    {
                        CheckComplexPropertyDepth(item, depth + 1, maximumDepth, currentPath);
                    }
                }

                return;
            }

            if (value is IEnumerable enumerable)
            {
                // A collection does not add a level of its own; its items sit at the collection's
                // depth. Callers that hop from an owner to a collection already added the +1
                // (dictionary values above, complex-collection properties below), so both paths
                // count items exactly one level below the owner.
                foreach (var item in enumerable.Cast<object?>().Where(item => item is not null))
                    CheckComplexPropertyDepth(item!, depth, maximumDepth, currentPath);

                return;
            }

            foreach (var property in value.GetType().GetProperties()
                         .Where(property => property.CanRead && property.GetIndexParameters().Length == 0))
            {
                var propertyValue = property.GetValue(value);
                if (propertyValue is null)
                    continue;

                if (IsComplex(property.PropertyType))
                {
                    CheckComplexPropertyDepth(propertyValue, depth + 1, maximumDepth, currentPath);
                }
                else if (IsCollectionOfComplex(property.PropertyType))
                {
                    foreach (var item in ((IEnumerable)propertyValue).Cast<object?>().Where(item => item is not null))
                        CheckComplexPropertyDepth(item!, depth + 1, maximumDepth, currentPath);
                }
                else if (IsDictionary(property.PropertyType))
                {
                    CheckComplexPropertyDepth(propertyValue, depth, maximumDepth, currentPath);
                }
            }
        }
        finally
        {
            currentPath.Remove(value);
        }
    }

    private static bool IsCollectionOfDictionaries(Type type) =>
        type != typeof(string) &&
        !IsDictionary(type) &&
        GetCollectionElementType(type) is { } itemType &&
        IsDictionary(itemType);

    /// <summary>
    /// An equality comparer that uses reference equality (<see cref="object.ReferenceEquals"/>) for comparisons.
    /// Used internally for cycle detection in graph data models.
    /// </summary>
    public sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        /// <summary>
        /// Gets the singleton instance of <see cref="ReferenceEqualityComparer"/>.
        /// </summary>
        public static ReferenceEqualityComparer Instance { get; } = new ReferenceEqualityComparer();

        /// <inheritdoc />
        bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);

        /// <inheritdoc />
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
