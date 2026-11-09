// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

using Cvoya.Graph.Age.Entities;
using Cvoya.Graph.Cypher;
using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;
using Cvoya.Graph.Serialization;

namespace Cvoya.Graph.Age.Querying.Cypher;

/// <summary>
/// Defines Apache AGE's Cypher syntax and supported graph-query capabilities.
/// </summary>
public sealed class AgeDialect : ICypherDialect
{
    private static readonly GraphCapability[] BaseCapabilityValues =
    [
        GraphCapability.FullTextSearch,
        GraphCapability.Transactions,
        GraphCapability.ComplexPropertyCascade,
        GraphCapability.MultiLabelMatch,
        GraphCapability.LabelFiltering,
        GraphCapability.OptionalTraversal,
        GraphCapability.CallSubqueries,
        GraphCapability.PatternSizeProjection,
        GraphCapability.GroupByAggregation,
        GraphCapability.RelationshipPredicates,
        GraphCapability.SetOperations,
        GraphCapability.NullElementsInSimpleCollections,
    ];

    // Apache AGE 1.8.0 added native age_shortest_path/age_all_shortest_paths. AGE 1.7.0 has
    // neither function, so the capability must stay absent there rather than being advertised
    // unconditionally by the provider.
    private static readonly GraphCapability[] NativeCapabilityValues =
    [
        .. BaseCapabilityValues,
        GraphCapability.ShortestPath,
    ];

    // The shared planner may represent scalar projected sort keys and path-decomposition order
    // coordinates as variable references. Let it construct those internal rows, while the AGE
    // expression validator rejects caller-authored whole-entity ordering before planning.
    private static readonly CapabilitySet PlanningCapabilities = CapabilitySet.Of(
        [
            .. BaseCapabilityValues,
            GraphCapability.OrderByEntity,
        ]);

    private static readonly CapabilitySet NativePlanningCapabilities = CapabilitySet.Of(
        [
            .. BaseCapabilityValues,
            GraphCapability.ShortestPath,
            GraphCapability.OrderByEntity,
        ]);

    private static readonly CapabilitySet CommandPlanningCapabilities = CapabilitySet.Of(
        GraphCapability.FullTextSearch,
        GraphCapability.Transactions,
        GraphCapability.ComplexPropertyCascade,
        GraphCapability.MultiLabelMatch,
        GraphCapability.LabelFiltering,
        GraphCapability.OptionalTraversal,
        GraphCapability.CallSubqueries,
        GraphCapability.PatternSizeProjection,
        GraphCapability.GroupByAggregation,
        GraphCapability.SetOperations,
        GraphCapability.NullElementsInSimpleCollections,
        GraphCapability.OrderByEntity,
        GraphCapability.RelationshipPredicates);

    private static readonly CapabilitySet SupportedCapabilities = CapabilitySet.Of(BaseCapabilityValues);
    private static readonly CapabilitySet NativeSupportedCapabilities = CapabilitySet.Of(NativeCapabilityValues);

    private readonly CapabilitySet capabilities;
    private readonly bool nativeShortestPath;

    /// <summary>Gets the first Apache AGE version that exposes the native shortest-path functions.</summary>
    internal static Version MinimumNativeShortestPathVersion { get; } = new(1, 8, 0);

    /// <summary>Initializes the Apache AGE dialect.</summary>
    /// <remarks>This parameterless dialect describes the AGE 1.7 baseline.</remarks>
    public AgeDialect()
        : this(SupportedCapabilities, nativeShortestPath: false)
    {
    }

    private AgeDialect(CapabilitySet capabilities, bool nativeShortestPath)
    {
        this.capabilities = capabilities;
        this.nativeShortestPath = nativeShortestPath;
    }

    /// <summary>Gets the shared Apache AGE 1.7 dialect instance.</summary>
    public static AgeDialect Instance { get; } = new();

    internal static AgeDialect PlanningInstance { get; } = new(PlanningCapabilities, nativeShortestPath: false);

    private static AgeDialect NativeInstance { get; } = new(NativeSupportedCapabilities, nativeShortestPath: true);

    private static AgeDialect NativePlanningInstance { get; } =
        new(NativePlanningCapabilities, nativeShortestPath: true);

    /// <summary>
    /// Gets the planning dialect for command selections. Command selections end in a scalar
    /// native-id projection and share the same relationship-existence lowering as read queries.
    /// </summary>
    internal static AgeDialect CommandPlanningInstance { get; } = new(CommandPlanningCapabilities, nativeShortestPath: false);

    /// <summary>Gets the dialect matching the connected AGE server version.</summary>
    /// <param name="version">The installed AGE extension version.</param>
    internal static AgeDialect ForVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version >= MinimumNativeShortestPathVersion ? NativeInstance : Instance;
    }

    /// <summary>Parses an AGE <c>extversion</c> string, tolerating suffixes and two-part versions.</summary>
    /// <param name="extversion">The value of <c>pg_extension.extversion</c>.</param>
    internal static Version ParseServerVersion(string extversion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extversion);
        var numeric = new string(extversion.TakeWhile(character => char.IsAsciiDigit(character) || character == '.').ToArray())
            .TrimEnd('.');
        return Version.TryParse(numeric, out var parsed)
            ? new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0))
            : new Version(0, 0, 0, 0);
    }

    /// <summary>Gets the dialect variant that plans read queries for this server.</summary>
    internal AgeDialect Planning => nativeShortestPath ? NativePlanningInstance : PlanningInstance;

    /// <summary>Gets whether native shortest-path functions are available on this server.</summary>
    internal bool SupportsNativeShortestPath => nativeShortestPath;

    /// <inheritdoc/>
    public string Name => "Apache AGE";

    /// <inheritdoc/>
    /// <remarks>
    /// AGE-compatible lowering implements correlated collections, pattern counts and existence
    /// filters, multi-label matches, scalar ordering, and optional traversal before rendering. Full-text search is lowered
    /// earlier still, at the expression level, to a two-phase Postgres text-search query
    /// (<see cref="Querying.AgeFullTextSearch"/>).
    /// Scalar-key grouping uses AGE's native grouping and aggregate support through the shared
    /// structured <c>WITH</c> plan. Capabilities describe user-visible read behavior, whether
    /// native or lowered.
    /// </remarks>
    public CapabilitySet Capabilities => capabilities;

    /// <summary>
    /// Gets whether AGE lowers <c>== null</c> on a simple property to a key-existence test.
    /// </summary>
    /// <remarks>
    /// AGE writes omit null properties, so a missing key is the stored representation of a null
    /// value. The negated key-existence predicate avoids evaluating <c>agtype_access_operator</c>
    /// for every candidate row; see <see cref="CypherBinaryOperator.PropertyKeyExists"/>.
    /// </remarks>
    public bool SupportsPropertyKeyExistence => true;

    /// <inheritdoc/>
    /// <remarks>
    /// Unreachable backstop. AGE's two-phase full-text lowering removes the search expression before
    /// the shared planner and renderer run; reaching this hook therefore indicates a provider bug.
    /// This collapses the four former full-text name members into a single throwing hook (issue #292).
    /// </remarks>
    public string RenderFullTextSearch(FullTextSearchClause clause, ICypherRenderContext context) =>
        throw FullTextNotSupported();

    /// <inheritdoc/>
    public string ComplexPropertyRelationshipMarker => ComplexPropertyStorage.RelationshipMarkerProperty;

    /// <inheritdoc/>
    public string RenderParameter(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return $"${name}";
    }

    /// <inheritdoc/>
    public string RenderPropertyAccess(string target, string property, bool escape)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (string.IsNullOrWhiteSpace(property))
        {
            _ = CypherIdentifier.Escape(property, "property name");
        }

        property = SimpleCollectionStorageCodec.GetPayloadPropertyName(property);
        // A compile-time data-model property identifier (escape == false, including custom
        // [Property(Label = ...)] names) is escaped only when it is not a plain symbolic name, so
        // ordinary properties stay byte-stable while spaces/punctuation/backticks can no longer break
        // out of the identifier. Dynamic access (escape == true, #150) is always quoted.
        return $"{target}.{(escape
            ? CypherIdentifier.Escape(property, "property name")
            : CypherIdentifier.EscapeIfNeeded(property, "property name"))}";
    }

    /// <inheritdoc/>
    public string RenderPhysicalPropertyAccess(string target, string property)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        return $"{target}.{CypherIdentifier.Escape(property, "physical property name")}";
    }

    /// <inheritdoc/>
    public string RenderCollectionPropertyAccess(string target, string property, bool escape) =>
        RenderPropertyAccess(target, property, escape);

    /// <inheritdoc/>
    public IReadOnlyList<CypherStoredPropertyValue> EncodePropertyValue(
        string property,
        Type? declaredType,
        object? value) =>
        SimpleCollectionStorageCodec.EncodeValue(
                property,
                declaredType,
                value,
                omitNullPayloads: false,
                static item => item)
            .Select(item => new CypherStoredPropertyValue(item.StorageName, item.Value))
            .ToArray();

    /// <inheritdoc/>
    public string GetPropertyStorageName(string property) =>
        SimpleCollectionStorageCodec.GetPayloadPropertyName(property);

    /// <inheritdoc/>
    public IReadOnlyList<string> GetPropertyCompanionStorageNames(string property) =>
        SimpleCollectionStorageCodec.GetCompanionPropertyNames(property);

    /// <inheritdoc/>
    public string RenderNativeElementIdentity(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        return $"id({target})";
    }

    /// <inheritdoc/>
    public string RenderFunctionName(string function)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(function);
        return function switch
        {
            "temporal.datetime" => "datetime",
            "temporal.localDateTime" => "localdatetime",
            "temporal.date" => "date",
            "temporal.time" => "time",
            "temporal.duration" => "duration",
            "math.ceiling" => "ceil",
            _ => function,
        };
    }

    /// <inheritdoc/>
    public CypherFunctionBehavior GetFunctionBehavior(string function)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(function);
        return function switch
        {
            "string.join" or
            "string.indexOf" or
            "string.lastIndexOf" or
            "string.padLeft" or
            "string.padRight" or
            "string.compareTo" => CypherFunctionBehavior.Unsupported,
            _ => CypherFunctionBehavior.Render,
        };
    }

    /// <inheritdoc/>
    public string RenderNodeLabels(IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        return string.Join('|', labels.Select(label => CypherIdentifier.EscapeIfNeeded(label, "node label")));
    }

    /// <inheritdoc/>
    public string RenderRelationshipTypes(IReadOnlyList<string> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        return string.Join('|', types.Select(type => CypherIdentifier.EscapeIfNeeded(type, "relationship type")));
    }

    /// <inheritdoc/>
    public string RenderDepth(DepthRange depth)
    {
        ArgumentNullException.ThrowIfNull(depth);
        return depth.Max == int.MaxValue
            ? $"*{depth.Min}.."
            : $"*{depth.Min}..{depth.Max}";
    }

    /// <inheritdoc/>
    public string RenderLabelTest(string target, IReadOnlyList<string> labels, Func<string, string> renderLiteral)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(renderLiteral);

        var conditions = labels.Select(item =>
            $"({renderLiteral(item)} IN labels({target}) OR " +
            $"properties({target}) @> {{ {AgeElementMatcher.InheritanceLabelsProperty}: [{renderLiteral(item)}] }})").ToArray();
        return conditions.Length == 1 ? conditions[0] : $"({string.Join(" OR ", conditions)})";
    }

    /// <inheritdoc/>
    /// <remarks>
    /// AGE 1.8.0 segfaults when <c>id()</c> is the left operand of <c>IN</c>, so native identity
    /// membership is rendered as a filtered list comprehension instead. Other collection items keep
    /// the shared null-aware rendering: AGE 1.7 does not match an agtype null parameter with
    /// <c>IS NULL</c>, so the <c>toString()</c> form remains necessary there.
    /// </remarks>
    public string RenderCollectionContains(string collection, string item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item);

        // NativeElementIdentity is the only item AGE renders as id(...); it is never null.
        if (item.StartsWith("id(", StringComparison.Ordinal))
        {
            return $"size([__cvoya_collection_item IN {collection} WHERE " +
                $"__cvoya_collection_item = {item}]) > 0";
        }

        return $"size([__cvoya_collection_item IN {collection} WHERE " +
            $"__cvoya_collection_item = {item} OR " +
            $"(toString(__cvoya_collection_item) IS NULL AND toString({item}) IS NULL)]) > 0";
    }

    private static GraphQueryTranslationException FullTextNotSupported() => new(
        $"The Apache AGE dialect does not support capability {GraphCapability.FullTextSearch}.");
}
