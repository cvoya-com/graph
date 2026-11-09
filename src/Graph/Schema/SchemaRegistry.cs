// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;

/// <summary>
/// Registry for schema information that can be shared between strongly-typed and dynamic entities.
/// This registry discovers and manages schema information for all INode and IRelationship types.
/// </summary>
/// <remarks>
/// If an assembly can only be loaded partially, schemas from its loadable types are registered and
/// the loader failures are written to <see cref="Trace"/>. The assembly remains eligible for discovery
/// and is retried on a later schema miss; successfully registered types may be encountered again safely.
/// </remarks>
public class SchemaRegistry : IDisposable
{
    // ConcurrentDictionary rather than Dictionary: writes only ever happen under _semaphore (so
    // there's no write/write race), but reads must be safe to run lock-free, concurrently with a
    // writer holding the semaphore (e.g. a lazy rescan triggered by another thread). That's
    // exactly what ConcurrentDictionary.TryGetValue guarantees without taking any lock itself.
    // Case-insensitive keys: a node type maps to exactly one label and no two loaded types may share a
    // label (case-insensitive). This matches the resolver, which compares stored labels case-insensitively
    // (Neo4j labels are case-sensitive, but GraphModel controls their casing on write, so "Person" and
    // "person" can never both be legitimately loaded - treating them as distinct would only let two types
    // register under labels the reader cannot tell apart).
    private readonly ConcurrentDictionary<string, EntitySchemaInfo> _nodeSchemas = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, EntitySchemaInfo> _relationshipSchemas = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Assembly> _scannedAssemblies = new();
    private readonly ConcurrentQueue<PartialScanDiagnostic> _pendingPartialScanDiagnostics = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Action<Assembly, IReadOnlyList<Exception>> _partialScanDiagnostic;
    private volatile bool _isInitialized;
    private volatile bool _hasComplexProperties;

    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaRegistry"/> class.
    /// </summary>
    public SchemaRegistry()
        : this(TracePartialScan)
    {
    }

    internal SchemaRegistry(Action<Assembly, IReadOnlyList<Exception>> partialScanDiagnostic)
    {
        ArgumentNullException.ThrowIfNull(partialScanDiagnostic);
        _partialScanDiagnostic = partialScanDiagnostic;
    }

    /// <summary>
    /// Gets whether the schema registry has been initialized.
    /// </summary>
    public bool IsInitialized => _isInitialized;

    /// <summary>
    /// Gets whether any registered entity type declares a complex property.
    /// </summary>
    /// <remarks>
    /// AGE stores complex properties as provider-owned nodes reachable through marked
    /// relationships. A graph whose registered types declare no complex property has no such
    /// nodes, so query lowering can omit the correlated owner-isolation check. The value is
    /// monotonic while the registry is populated and resets with <see cref="ClearAsync"/>.
    /// </remarks>
    internal bool HasComplexProperties => _hasComplexProperties;

    /// <summary>
    /// Asynchronously initializes the schema registry by discovering all INode and IRelationship types
    /// in all loaded assemblies and populating the registry with their schema information.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Quick check without lock for performance
        if (_isInitialized)
            return;

        // Use async-safe semaphore
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check pattern
            if (_isInitialized)
                return;

            InitializeCore();
        }
        finally
        {
            ReleaseSemaphoreAndReportPartialScans();
        }
    }

    /// <summary>
    /// Gets the schema information for a node label.
    /// </summary>
    /// <remarks>
    /// Initializes the registry on first use, so callers never need a prior
    /// <see cref="InitializeAsync"/> call; a miss on an already-initialized registry rescans
    /// <see cref="AppDomain.CurrentDomain"/> for late-loaded assemblies.
    /// </remarks>
    /// <param name="label">The node label.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>The schema information for the node label, or null if not found.</returns>
    public async Task<EntitySchemaInfo?> GetNodeSchemaAsync(string label, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        // Lock-free fast path: once a label is registered it never becomes unregistered other
        // than via ClearAsync, so a hit here is always correct without taking the semaphore.
        // ConcurrentDictionary.TryGetValue is safe to call concurrently with a writer holding
        // the semaphore below.
        if (_isInitialized && _nodeSchemas.TryGetValue(label, out var cached))
        {
            return cached;
        }

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isInitialized)
            {
                InitializeCore();
            }
            else if (!_nodeSchemas.ContainsKey(label))
            {
                RegisterGraphEntityTypes(AppDomain.CurrentDomain.GetAssemblies());
            }

            return _nodeSchemas.TryGetValue(label, out var schema) ? schema : null;
        }
        finally
        {
            ReleaseSemaphoreAndReportPartialScans();
        }
    }

    /// <summary>
    /// Gets the schema information for a relationship type.
    /// </summary>
    /// <remarks>
    /// Initializes the registry on first use - see the remarks on <see cref="GetNodeSchemaAsync"/>,
    /// which apply here identically.
    /// </remarks>
    /// <param name="type">The relationship type.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>The schema information for the relationship type, or null if not found.</returns>
    public async Task<EntitySchemaInfo?> GetRelationshipSchemaAsync(string type, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        // See the fast-path comment in GetNodeSchemaAsync.
        if (_isInitialized && _relationshipSchemas.TryGetValue(type, out var cached))
        {
            return cached;
        }

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isInitialized)
            {
                InitializeCore();
            }
            else if (!_relationshipSchemas.ContainsKey(type))
            {
                RegisterGraphEntityTypes(AppDomain.CurrentDomain.GetAssemblies());
            }

            return _relationshipSchemas.TryGetValue(type, out var schema) ? schema : null;
        }
        finally
        {
            ReleaseSemaphoreAndReportPartialScans();
        }
    }

    /// <summary>
    /// Gets the schema information for a node label (synchronous version for use after initialization).
    /// </summary>
    /// <param name="label">The node label.</param>
    /// <returns>The schema information for the node label, or null if not found.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the registry is not initialized.</exception>
    public EntitySchemaInfo? GetNodeSchema(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        if (!_isInitialized)
            throw new InvalidOperationException("Schema registry must be initialized before accessing schema information synchronously.");

        if (_nodeSchemas.TryGetValue(label, out var cached))
        {
            return cached;
        }

        _semaphore.Wait();
        try
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Schema registry must be initialized before accessing schema information synchronously.");

            if (!_nodeSchemas.ContainsKey(label))
            {
                RegisterGraphEntityTypes(AppDomain.CurrentDomain.GetAssemblies());
            }

            return _nodeSchemas.TryGetValue(label, out var schema) ? schema : null;
        }
        finally
        {
            ReleaseSemaphoreAndReportPartialScans();
        }
    }

    /// <summary>
    /// Gets the schema information for a relationship type (synchronous version for use after initialization).
    /// </summary>
    /// <param name="type">The relationship type.</param>
    /// <returns>The schema information for the relationship type, or null if not found.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the registry is not initialized.</exception>
    public EntitySchemaInfo? GetRelationshipSchema(string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        if (!_isInitialized)
            throw new InvalidOperationException("Schema registry must be initialized before accessing schema information synchronously.");

        if (_relationshipSchemas.TryGetValue(type, out var cached))
        {
            return cached;
        }

        _semaphore.Wait();
        try
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Schema registry must be initialized before accessing schema information synchronously.");

            if (!_relationshipSchemas.ContainsKey(type))
            {
                RegisterGraphEntityTypes(AppDomain.CurrentDomain.GetAssemblies());
            }

            return _relationshipSchemas.TryGetValue(type, out var schema) ? schema : null;
        }
        finally
        {
            ReleaseSemaphoreAndReportPartialScans();
        }
    }

    /// <summary>
    /// Gets all registered node labels.
    /// </summary>
    /// <remarks>
    /// Initializes the registry on first use, then returns a point-in-time snapshot of the
    /// labels registered so far. If the registry is already initialized, the snapshot is
    /// returned lock-free without forcing a rescan of
    /// <see cref="AppDomain.CurrentDomain"/> for late-loaded assemblies - unlike
    /// <see cref="GetNodeSchemaAsync"/>/<see cref="GetNodeSchema"/>, a miss on a specific label
    /// has no bearing here, so there is no natural rescan trigger. Look up a specific label or
    /// type, which does rescan on miss, if a late-loaded assembly's types must be reflected here.
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>An enumerable of registered node labels.</returns>
    public async Task<IEnumerable<string>> GetRegisteredNodeLabelsAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            return [.. _nodeSchemas.Keys];
        }

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isInitialized)
            {
                InitializeCore();
            }

            return [.. _nodeSchemas.Keys];
        }
        finally
        {
            ReleaseSemaphoreAndReportPartialScans();
        }
    }

    /// <summary>
    /// Gets all registered relationship types.
    /// </summary>
    /// <remarks>
    /// Initializes the registry on first use, then returns a point-in-time snapshot of the types
    /// registered so far. See the rescan-boundary remarks on
    /// <see cref="GetRegisteredNodeLabelsAsync"/>, which apply here identically.
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>An enumerable of registered relationship types.</returns>
    public async Task<IEnumerable<string>> GetRegisteredRelationshipTypesAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            return [.. _relationshipSchemas.Keys];
        }

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isInitialized)
            {
                InitializeCore();
            }

            return [.. _relationshipSchemas.Keys];
        }
        finally
        {
            ReleaseSemaphoreAndReportPartialScans();
        }
    }

    /// <summary>
    /// Asynchronously clears all schema information and resets the initialization state.
    /// </summary>
    /// <remarks>
    /// After clearing, the next asynchronous lookup lazily re-initializes the registry, while
    /// the synchronous getters throw until that (or <see cref="InitializeAsync"/>) has happened.
    /// </remarks>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _nodeSchemas.Clear();
            _relationshipSchemas.Clear();
            _scannedAssemblies.Clear();
            _isInitialized = false;
            _hasComplexProperties = false;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private void InitializeCore()
    {
        // All callers hold _semaphore so discovery and the initialized flag publish atomically.
        RegisterGraphEntityTypes(AppDomain.CurrentDomain.GetAssemblies());
        _isInitialized = true;
    }

    private void TrackComplexProperties(Type entityType)
    {
        if (!_hasComplexProperties && GraphDataModel.GetComplexProperties(entityType).Any())
        {
            _hasComplexProperties = true;
        }
    }

    private void RegisterNodeType(Type nodeType, List<string> collisions)
    {
        TrackComplexProperties(nodeType);
        var label = GetLabelFromType(nodeType);
        var owner = GetLabelOwner<NodeAttribute>(NormalizeForIdentity(nodeType));

        if (_nodeSchemas.TryGetValue(label, out var existing))
        {
            var existingOwner = GetLabelOwner<NodeAttribute>(NormalizeForIdentity(existing.Type));
            if (existingOwner != owner)
            {
                collisions.Add(
                    $"Node label '{label}' is used by both '{existing.Type.FullName}' and '{nodeType.FullName}'.");
            }

            return;
        }

        var schema = CreateEntitySchemaInfo(nodeType, label, collisions);
        _nodeSchemas[label] = schema;
    }

    private void RegisterRelationshipType(Type relationshipType, List<string> collisions)
    {
        TrackComplexProperties(relationshipType);
        var type = GetLabelFromType(relationshipType);
        var owner = GetLabelOwner<RelationshipAttribute>(NormalizeForIdentity(relationshipType));

        if (_relationshipSchemas.TryGetValue(type, out var existing))
        {
            var existingOwner = GetLabelOwner<RelationshipAttribute>(NormalizeForIdentity(existing.Type));
            if (existingOwner != owner)
            {
                collisions.Add(
                    $"Relationship type '{type}' is used by both '{existing.Type.FullName}' and '{relationshipType.FullName}'.");
            }

            return;
        }

        var schema = CreateEntitySchemaInfo(relationshipType, type, collisions);
        _relationshipSchemas[type] = schema;
    }

    /// <summary>
    /// Normalizes a closed generic type to its generic type definition for identity comparisons, so that
    /// e.g. <c>MyNode&lt;int&gt;</c> and <c>MyNode&lt;string&gt;</c> - distinct <see cref="Type"/> objects
    /// that share a label because the fallback label strips generic arity backticks - are treated as the
    /// same type. A genuinely different type that merely shares the label still collides.
    /// </summary>
    private static Type NormalizeForIdentity(Type type)
        => type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;

    /// <summary>
    /// Finds the type in <paramref name="type"/>'s inheritance chain (including itself) that directly
    /// declares <typeparamref name="TAttribute"/>. If no type in the chain declares it, <paramref name="type"/>
    /// itself is returned (the type-name fallback case).
    /// </summary>
    /// <remarks>
    /// This is the "owner" of a resolved label: mirroring CG008/CG009, a type without its own
    /// <see cref="NodeAttribute"/>/<see cref="RelationshipAttribute"/> is never a collision candidate in the
    /// analyzer - it only silently inherits its label from an ancestor. Two types that resolve to the same
    /// label collide only when they have different owners; sharing an owner means one is legitimately
    /// inheriting the other's (or a common ancestor's) label.
    /// </remarks>
    private static Type GetLabelOwner<TAttribute>(Type type) where TAttribute : Attribute
    {
        // Callers pass an already-normalized (generic-definition) type - see NormalizeForIdentity - so the
        // base-type chain from here on is well-defined without needing further generic handling.
        var current = type;
        while (current is not null)
        {
            if (current.GetCustomAttribute<TAttribute>(inherit: false) is not null)
            {
                return current;
            }

            current = current.BaseType;
        }

        return type;
    }

    private static EntitySchemaInfo CreateEntitySchemaInfo(Type entityType, string label, List<string> collisions)
    {
        var properties = new Dictionary<string, PropertySchemaInfo>();
        var allProperties = GetEffectivePublicInstanceProperties(entityType);

        // CG007 mirror: within one entity type's full inheritance chain, two properties must not resolve
        // to the same storage label (case-sensitive, matching the analyzer's StringComparer.Ordinal use).
        var resolvedLabels = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

        foreach (var property in allProperties)
        {
            var propertySchema = CreatePropertySchemaInfo(property);
            properties[property.Name] = propertySchema;

            var resolvedLabel = propertySchema.Name;
            if (resolvedLabels.TryGetValue(resolvedLabel, out var existingProperty))
            {
                collisions.Add(
                    $"Property label '{resolvedLabel}' on '{entityType.FullName}' is used by both " +
                    $"'{existingProperty.DeclaringType?.FullName}.{existingProperty.Name}' and " +
                    $"'{property.DeclaringType?.FullName}.{property.Name}'.");
            }
            else
            {
                resolvedLabels[resolvedLabel] = property;
            }
        }

        return new EntitySchemaInfo
        {
            Type = entityType,
            Label = label,
            Properties = properties
        };
    }

    private static PropertySchemaInfo CreatePropertySchemaInfo(PropertyInfo property)
    {
        var attribute = property.GetCustomAttribute<PropertyAttribute>();
        ValidatePropertySchema(property, attribute);

        var isKey = attribute?.IsKey ?? false;

        var includeInFullTextSearch = property.PropertyType == typeof(string)
            && (attribute?.IncludeInFullTextSearch ?? true);

        return new PropertySchemaInfo
        {
            PropertyInfo = property,
            // Resolve through the shared rule without populating the narrow runtime collision
            // cache: this registry aggregates every collision across the full inheritance chain.
            Name = Labels.ResolveLabelFromProperty(property),
            IsIndexed = isKey || (attribute?.IsIndexed ?? false),
            IsKey = isKey,
            IsUnique = attribute?.IsUnique ?? false,
            IsRequired = isKey || (attribute?.IsRequired ?? false),
            Ignore = attribute?.Ignore ?? false,
            IncludeInFullTextSearch = includeInFullTextSearch,
            Validation = new PropertyValidation
            {
                MinLength = attribute?.MinLength,
                MaxLength = attribute?.MaxLength,
                Pattern = attribute?.Pattern
            }
        };
    }

    /// <summary>
    /// Applies the runtime property-schema rules shared by every provider before schema metadata is published.
    /// </summary>
    private static void ValidatePropertySchema(PropertyInfo property, PropertyAttribute? attribute)
    {
        if (attribute?.Ignore == true)
        {
            var conflictingFlags = new List<string>();
            if (attribute.IsKey)
            {
                conflictingFlags.Add(nameof(PropertyAttribute.IsKey));
            }

            if (attribute.IsUnique)
            {
                conflictingFlags.Add(nameof(PropertyAttribute.IsUnique));
            }

            if (attribute.IsIndexed)
            {
                conflictingFlags.Add(nameof(PropertyAttribute.IsIndexed));
            }

            if (attribute.IsRequired)
            {
                conflictingFlags.Add(nameof(PropertyAttribute.IsRequired));
            }

            if (conflictingFlags.Count > 0)
            {
                throw new GraphException(
                    $"Property '{property.DeclaringType?.FullName}.{property.Name}' cannot combine " +
                    $"{nameof(PropertyAttribute.Ignore)} with {string.Join(", ", conflictingFlags)}.");
            }

            return;
        }

        PropertyConstraintValidation.Validate(property);

        if (FindUnsupportedPropertyShape(property.PropertyType) is { } unsupported)
        {
            var memberPath = string.IsNullOrEmpty(unsupported.MemberPath)
                ? property.Name
                : $"{property.Name}.{unsupported.MemberPath}";
            throw new GraphException(
                $"Property '{property.DeclaringType?.FullName}.{property.Name}' contains unsupported serialized member " +
                $"'{memberPath}' using {DescribeUnsupportedShape(unsupported.Kind)} '{unsupported.Type}'." +
                ExplainUnsupportedShape(unsupported.Kind));
        }

        if (attribute is null || !attribute.IsKey)
        {
            return;
        }

        var propertyType = property.PropertyType;
        var isNullable = Nullable.GetUnderlyingType(propertyType) is not null ||
            (!propertyType.IsValueType &&
             new NullabilityInfoContext().Create(property).ReadState == NullabilityState.Nullable);
        if (isNullable)
        {
            throw new GraphException(
                $"Key property '{property.DeclaringType?.FullName}.{property.Name}' must be non-nullable.");
        }

        if (!GraphDataModel.IsSimple(propertyType))
        {
            throw new GraphException(
                $"Key property '{property.DeclaringType?.FullName}.{property.Name}' must be a graph-storable scalar value; " +
                $"'{propertyType}' is not supported.");
        }
    }

    private static UnsupportedPropertyShape? FindUnsupportedPropertyShape(Type type)
    {
        return FindUnsupportedPropertyShape(type, string.Empty, []);
    }

    private static UnsupportedPropertyShape? FindUnsupportedPropertyShape(
        Type type,
        string memberPath,
        HashSet<Type> visited)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;
        if (GetUnsupportedTypeKind(underlyingType) is { } kind)
        {
            return new UnsupportedPropertyShape(underlyingType, memberPath, kind);
        }

        if (GraphDataModel.IsDictionary(underlyingType))
        {
            return new UnsupportedPropertyShape(underlyingType, memberPath, UnsupportedShapeKind.Dictionary);
        }

        if (GraphDataModel.IsSimple(underlyingType) || !visited.Add(underlyingType))
        {
            return null;
        }

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(underlyingType))
        {
            var elementType = underlyingType.IsArray
                ? underlyingType.GetElementType()
                : underlyingType.IsGenericType
                    ? underlyingType.GetGenericArguments().FirstOrDefault()
                    : null;

            return elementType is null
                ? null
                : FindUnsupportedPropertyShape(elementType, memberPath, visited);
        }

        // Any other shape serializes as a complex value. Reflection already returns inherited
        // public instance properties here, matching the analyzer and generator's effective walk.
        foreach (var property in GetSerializedProperties(underlyingType))
        {
            var nestedPath = string.IsNullOrEmpty(memberPath)
                ? property.Name
                : $"{memberPath}.{property.Name}";
            if (FindUnsupportedPropertyShape(property.PropertyType, nestedPath, visited) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static IEnumerable<PropertyInfo> GetSerializedProperties(Type type) =>
        GetEffectivePublicInstanceProperties(type)
            .Where(property => property.GetCustomAttribute<PropertyAttribute>()?.Ignore != true);

    private static IEnumerable<PropertyInfo> GetEffectivePublicInstanceProperties(Type type)
    {
        var seenProperties = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var property in current.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (property.GetMethod is null ||
                    property.GetIndexParameters().Length > 0 ||
                    !seenProperties.Add(property.Name))
                {
                    continue;
                }

                yield return property;
            }
        }
    }

    private static UnsupportedShapeKind? GetUnsupportedTypeKind(Type type)
    {
        if (type == typeof(IntPtr) || type == typeof(UIntPtr))
        {
            return UnsupportedShapeKind.NativeSizedInteger;
        }

        if (typeof(Delegate).IsAssignableFrom(type))
        {
            return UnsupportedShapeKind.Delegate;
        }

        var fullName = type.FullName ?? type.ToString();
        if (fullName.StartsWith("System.Threading.Tasks.", StringComparison.Ordinal) ||
            fullName.StartsWith("System.IO.", StringComparison.Ordinal) ||
            fullName.StartsWith("System.Net.", StringComparison.Ordinal) ||
            fullName.StartsWith("System.Reflection.", StringComparison.Ordinal) ||
            fullName.StartsWith("System.Runtime.", StringComparison.Ordinal))
        {
            return UnsupportedShapeKind.Framework;
        }

        return null;
    }

    private static string DescribeUnsupportedShape(UnsupportedShapeKind kind) => kind switch
    {
        UnsupportedShapeKind.NativeSizedInteger => "native-sized integer type",
        UnsupportedShapeKind.Delegate => "delegate type",
        UnsupportedShapeKind.Dictionary => "dictionary type",
        _ => "framework type",
    };

    /// <summary>
    /// Trailing sentence appended to the schema-validation failure, for the shapes whose rejection
    /// is not self-explanatory from the type name alone.
    /// </summary>
    private static string ExplainUnsupportedShape(UnsupportedShapeKind kind) => kind switch
    {
        UnsupportedShapeKind.NativeSizedInteger => " IntPtr and UIntPtr cannot be stored as graph property values.",
        _ => string.Empty,
    };

    private enum UnsupportedShapeKind
    {
        NativeSizedInteger,
        Delegate,
        Dictionary,
        Framework,
    }

    private sealed record UnsupportedPropertyShape(Type Type, string MemberPath, UnsupportedShapeKind Kind);

    private static string GetLabelFromType(Type type)
    {
        // Priority: NodeAttribute/RelationshipAttribute → Labels.GetLabelFromType
        var nodeAttr = type.GetCustomAttribute<NodeAttribute>();
        if (nodeAttr?.Label is { Length: > 0 })
        {
            return nodeAttr.Label;
        }

        var relAttr = type.GetCustomAttribute<RelationshipAttribute>();
        if (relAttr?.Label is { Length: > 0 })
        {
            return relAttr.Label;
        }

        // Fall back to Labels utility
        return Labels.GetLabelFromType(type);
    }

    /// <remarks>
    /// If an aggregated <see cref="GraphException"/> is thrown, non-colliding schemas and scanned-assembly
    /// marks registered before the throw remain; the exception is expected to be fatal for this registry.
    /// </remarks>
    private void RegisterGraphEntityTypes(IEnumerable<Assembly> assemblies)
    {
        // Collisions are collected across the whole scan (rather than thrown on the first one found) so a
        // single aggregated GraphException lists every conflict, letting the caller fix them all in one pass.
        var collisions = new List<string>();

        // Late-loaded assemblies are discovered lazily on schema misses instead of via
        // AppDomain.AssemblyLoad so the registry has no event-subscription lifetime to manage.
        foreach (var assembly in assemblies)
        {
            if (_scannedAssemblies.Contains(assembly))
            {
                continue;
            }

            var discovery = DiscoverGraphEntityTypes(assembly);
            if (discovery.IsComplete)
            {
                // Mark complete before registration so the existing fatal-collision behavior retains
                // its scanned-assembly mark when registration throws.
                _scannedAssemblies.Add(assembly);
            }
            else
            {
                // A later schema miss will retry this assembly. Types registered below can be
                // encountered again safely because registration is idempotent by type identity.
                _pendingPartialScanDiagnostics.Enqueue(
                    new PartialScanDiagnostic(assembly, discovery.LoaderExceptions));
            }

            foreach (var nodeType in discovery.NodeTypes)
            {
                RegisterNodeType(nodeType, collisions);
            }

            foreach (var relationshipType in discovery.RelationshipTypes)
            {
                RegisterRelationshipType(relationshipType, collisions);
            }
        }

        if (collisions.Count > 0)
        {
            throw new GraphException(
                $"Duplicate label(s) detected while registering graph entity types:{Environment.NewLine}" +
                string.Join(Environment.NewLine, collisions.Select(c => $"  - {c}")));
        }
    }

    private static GraphEntityTypeDiscovery DiscoverGraphEntityTypes(Assembly assembly)
    {
        var nodeTypes = new List<Type>();
        var relationshipTypes = new List<Type>();
        IReadOnlyList<Exception> loaderExceptions = [];
        var isComplete = true;

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            types = [.. exception.Types.OfType<Type>().Distinct()];
            loaderExceptions = exception.LoaderExceptions?.OfType<Exception>().ToArray() ?? [];
            isComplete = false;
        }

        foreach (var type in types.Where(static type => !type.IsAbstract && !type.IsInterface))
        {
            if (typeof(INode).IsAssignableFrom(type) &&
                type != typeof(DynamicNode))
            {
                nodeTypes.Add(type);
            }
            else if (typeof(IRelationship).IsAssignableFrom(type) &&
                type != typeof(DynamicRelationship))
            {
                relationshipTypes.Add(type);
            }
        }

        return new GraphEntityTypeDiscovery(nodeTypes, relationshipTypes, isComplete, loaderExceptions);
    }

    private void ReleaseSemaphoreAndReportPartialScans()
    {
        _semaphore.Release();

        // Diagnostics are an external, best-effort side effect. Run them only after releasing the
        // registry semaphore so a trace listener can neither block registration by throwing nor
        // deadlock the registry by re-entering a schema lookup.
        while (_pendingPartialScanDiagnostics.TryDequeue(out var diagnostic))
        {
            try
            {
                _partialScanDiagnostic(diagnostic.Assembly, diagnostic.LoaderExceptions);
            }
            catch (Exception)
            {
                // A diagnostic sink must not change schema discovery behavior.
            }
        }
    }

    private static void TracePartialScan(Assembly assembly, IReadOnlyList<Exception> loaderExceptions)
    {
        var details = loaderExceptions.Count == 0
            ? "No loader exception details were provided."
            : string.Join(
                Environment.NewLine,
                loaderExceptions.Select(exception => $"{exception.GetType().FullName}: {exception.Message}"));

        Trace.TraceWarning(
            "Schema discovery partially loaded assembly '{0}'. Loadable graph entity types were registered; " +
            "the assembly will be retried on a later schema miss.{1}{2}",
            assembly.FullName,
            Environment.NewLine,
            details);
    }

    private sealed record GraphEntityTypeDiscovery(
        IReadOnlyList<Type> NodeTypes,
        IReadOnlyList<Type> RelationshipTypes,
        bool IsComplete,
        IReadOnlyList<Exception> LoaderExceptions);

    private sealed record PartialScanDiagnostic(
        Assembly Assembly,
        IReadOnlyList<Exception> LoaderExceptions);

    /// <summary>
    /// Disposes the semaphore used for concurrency control.
    /// </summary>
    public void Dispose()
    {
        _semaphore?.Dispose();
        GC.SuppressFinalize(this);
    }
}
