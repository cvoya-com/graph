// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.CompatibilityTests.Tests;

using System.Reflection;

/// <summary>
/// Pins <see cref="ComplianceInventory"/>'s reflection-based counts against ground truth: the
/// suite assembly's actual <c>I*Tests</c> interfaces and their <see cref="RequiresCapabilityAttribute"/>
/// attribution.
/// </summary>
public sealed class ComplianceInventoryTests
{
    /// <summary>
    /// The total is a snapshot of the suite at the time this test was written. It is expected to
    /// change as the suite grows - when it does, update this constant alongside the PR that adds
    /// or removes test methods, so this test keeps proving "the reflection count matches reality"
    /// rather than silently drifting. Statically-skipped facts (<c>[Fact(Skip = ...)]</c>) are
    /// excluded: they can never execute on any provider, so they play no part in the strict-mode
    /// required method inventory.
    /// </summary>
    // +9 for #120: eight of the nine pattern-comprehension tests were un-skipped once correlated
    // collection projections landed — Basic/Filtered/TimeBased/Ordered/Aggregated/Grouped pattern
    // comprehensions and TraversePathAndGroupBy (gated on CallSubqueries), plus the ungated
    // CanCombineNodeAndRelationshipQueries — and one cross-provider edge-case contract was added.
    // +5 for #300: CanProjectRelationshipCounts was un-skipped and four relationship-count parity
    // cases were added for physical direction, derived types, self-loops, and expression validation.
    // +3 for #294: capability-certifying tests in IAdvancedQueryTests -
    // CanQueryPolymorphicBaseTypeAcrossSubtypeLabels (MultiLabelMatch), CanOrderByBareEntity
    // (OrderByEntity), and CanProjectComplexCollectionSize (PatternSizeProjection). OptionalTraversal
    // uses the two pre-existing null-propagating navigation contracts in IQueryTests.
    // +10 for ISubgraphCreationTests (atomic node–relationship–node subgraph create, #45).
    // +3 for #288: three new IFullTextSearchTests methods (multi-term AND, whole-token vs sub-token,
    // and metacharacter robustness).
    // +5 for #295: positive search-as-source coverage for Traverse, PathSegments, TraversePaths,
    // an empty search result, and a post-traversal search filter over a search-root traversal
    // (replacing #288's temporary rejection test, +4 net).
    // +10 net for #306 scalar-key GroupBy: +11 IGroupByTests methods (gated on GroupByAggregation),
    // -1 for the removed IAdvancedQueryTests.GroupByThrowsNotSupportedUntilIssue100 (now supported).
    // +1 for #307: IAdvancedQueryTests.UnsupportedGroupedProjectionShapeIsRejectedConsistently -
    // pins that an unsupported correlated grouped-projection member (Take over the group) is rejected
    // with the same GraphQueryTranslationException on every provider.
    // +5 for #318: three correlated-composition cases and two scalar-projection parity cases.
    // +6 for #96: relationship expansion/existence (2), shortest paths, public optional traversal,
    // typed set operations, and label filtering capability contracts.
    // +1 for the #348 review: a source label filter eliminates rows through an optional traversal
    // instead of degrading into a preserved null-target row.
    // +5 for #341: parameterless LongCount over a scalar group — Enumerable and Queryable forms,
    // both projection forms, the empty-source contract, and the LongCount(predicate) rejection.
    // +2 for #359 and #364: typed and dynamic simple-collection round trips.
    // +4 for #370: typed, dynamic, same-storage-type/different-CLR, and transaction relationship
    // update identity contracts.
    // +7 for #361 and #360: one AllAsync universal-quantification contract (including projection)
    // plus six AverageAsync result-type/empty-null contracts, including the complete source and
    // selector overload matrix.
    // +3 for #367/#411: typed node and relationship property labels with spaces, keywords,
    // punctuation, structural characters, and an embedded backtick round-trip through CRUD and
    // query through predicate/projection/ordering/complex-property navigation.
    // +1 for #405: a simple collection nested inside a dynamic node's dictionary/complex property
    // value round-trips through create/get.
    // +1 for #385: freshly converted dynamic nodes and relationships round-trip with derived metadata.
    // +2 for #366: same-provider cross-store transaction misuse is rejected by reads/queries and by
    // every CRUD operation, leaving both stores unchanged and the transaction usable on its owner.
    // +4 for #374: full-text sanitization inside set-operation branches - per-branch ALL-terms
    // union, duplicate-preserving concat with an empty-token branch, empty-token/metacharacter/
    // sub-token branch neutralization, and a left-associated three-branch union.
    // +3 for #436/#437: absent value/reference constructor arguments reject implicit defaults,
    // and reference scalar collection projections preserve null rows and positional order.
    // +4 for #380/#444: a self-loop is traversed once per direction shape and yields one path
    // segment and one path, while parallel self-loops and opposite-direction relationships preserve
    // one result per physical relationship across node, segment-end, and path-end projections.
    // +3 for #464: single-key tuple enforcement, explicit uniqueness on a composite-key component,
    // and key-value scope across distinct node labels and relationship types.
    // +9 for #466: zero/cancelled mutations, search and relationship-existence selection,
    // ordered-window updates, relationship mutation, atomic/cascading node deletion,
    // dynamic-property mutation without exposing provider-native identities, and role-specific
    // exact-one endpoint cardinality failures.
    // +7 for #475: key/unique updates, selected-set swaps and duplicate rollback, unselected
    // collision rollback, relationship uniqueness, and ordinary domain properties named Id.
    // +3 for #476: independent mixed scalar/complex replacement, polymorphic ordered collection
    // replacement plus null/empty clearing, and dynamic complex/scalar representation transitions.
    // +1 for #383: negative ElementAt/ElementAtOrDefault parity across empty/nonempty reference,
    // nullable-value, and non-nullable-value query shapes.
    // -2 for #477-#480: the endpoint-bearing relationship and provider identity compatibility
    // cases were retired with those pre-v1 surfaces.
    // +4 for #403: nullable simple collections across typed/dynamic nodes and relationships, plus
    // atomic set-based transition and rollback behavior.
    // +5 for #423/#433: nullable and non-nullable collection-element schemas across stored-property
    // materialization and constructor projections, including independent container nullability.
    // +4 for #425: typed and dynamic nullable complex collections, real-child cascade cleanup,
    // and atomic dense/sparse/all-null/empty mutation transitions.
    // +1 for #520: native byte-array scalar create/read/update behavior across providers.
    // +8 for #525: composed predicate, collection-membership, expression-method, projection,
    // ordering/paging, duplicate/aggregate, terminal, and deterministic-rejection contracts.
    // +5 for #481/#498: one shared duplicate ordinary-Id creation contract plus four final
    // provider-boundary certifications for the identity-free public shape, ordinary Id/Direction
    // domain data, external native rows, and read-only no-provision behavior.
    // +1 for NUL-75: deleting a node that only has an incoming user relationship must preflight
    // both directions (AGE 1.8 regression from the NUL-73 unidirectional fix).
    // +2: synchronous hierarchy/count composition and overlapping-edge delete preflight.
    private const int ExpectedTotalTestMethods = 504;

    [Fact]
    public void TotalTestMethods_MatchesKnownSuiteSize()
    {
        Assert.Equal(ExpectedTotalTestMethods, ComplianceInventory.TotalTestMethods);
    }

    [Fact]
    public void TotalTestMethods_MatchesIndependentReflectionCount()
    {
        // Re-derives the count independently of ComplianceInventory's own implementation, so this
        // test can't pass merely because both counts share the same (potentially buggy) logic.
        var expected = typeof(IGraphTest).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.IsPublic && typeof(IGraphTest).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Count(m => m.GetCustomAttributes(inherit: false).Any(a => a is FactAttribute { Skip: null }));

        Assert.Equal(expected, ComplianceInventory.TotalTestMethods);
    }

    [Fact]
    public void EveryCapabilityHasRunnableCoverageOrAnExplicitNoSurfaceRecord()
    {
        GraphCapability[] recordOnly =
        [
            GraphCapability.NestedTransactions,
            // Index maintenance is an optional interface, not an IGraph method, so the packaged
            // TCK has no provider-binding surface to gate yet. Providers still declare the
            // capability and the provider-implementers guide documents the contract.
            GraphCapability.PropertyIndexes,
            GraphCapability.PropertyAggregateIndexes,
        ];

        var covered = typeof(IGraphTest).Assembly.GetTypes()
            .Where(type => type.IsInterface && type.IsPublic && typeof(IGraphTest).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttribute<FactAttribute>(inherit: false) is { Skip: null })
                .SelectMany(method => method.GetCustomAttributes<RequiresCapabilityAttribute>(inherit: false)
                    .Concat(type.GetCustomAttributes<RequiresCapabilityAttribute>(inherit: false))))
            .Select(attribute => attribute.Capability)
            .Distinct()
            .Order()
            .ToArray();
        var expected = Enum.GetValues<GraphCapability>()
            .Except(recordOnly)
            .Order()
            .ToArray();

        Assert.Equal(expected, covered);
    }

    [Fact]
    public void MinimumExecuted_All_EqualsTotalTestMethods()
    {
        Assert.Equal(ComplianceInventory.TotalTestMethods, ComplianceInventory.MinimumExecuted(CapabilitySet.All));
    }

    [Fact]
    public void ExpectedMethodIdentities_DeclaredCapabilities_SelectExactRunnableSet()
    {
        var declared = CapabilitySet.All.Except(GraphCapability.FullTextSearch);
        var identities = ComplianceInventory.ExpectedMethodIdentities(declared);
        var fullTextMethod = typeof(IFullTextSearchTests)
            .GetMethod(nameof(IFullTextSearchTests.CanSearchNodesWithFullTextSearch))!;

        Assert.Equal(ComplianceInventory.MinimumExecuted(declared), identities.Count);
        Assert.DoesNotContain(ComplianceInventory.MethodIdentity(fullTextMethod), identities);
        Assert.All(
            ComplianceInventory.ExpectedTestMethods(declared),
            method => Assert.Contains(ComplianceInventory.MethodIdentity(method), identities));
    }

    [Fact]
    public void MethodIdentity_OverloadsAreDistinct()
    {
        var identities = typeof(IOverloadedMethods).GetMethods()
            .Select(ComplianceInventory.MethodIdentity)
            .ToArray();

        Assert.Equal(2, identities.Length);
        Assert.Equal(2, identities.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(identities, identity => identity.EndsWith(".Execute(System.Int32)", StringComparison.Ordinal));
        Assert.Contains(identities, identity => identity.EndsWith(".Execute(System.String)", StringComparison.Ordinal));
    }

    [Fact]
    public void MinimumExecuted_AllExceptFullTextSearch_ExcludesFullTextSearchGatedMethods()
    {
        // Ground truth derived independently: every runnable test method gated on FullTextSearch,
        // whether by an interface-level attribute (IFullTextSearchTests) or a method-level one
        // (full-text tests living in otherwise ungated interfaces).
        var gatedMethodCount = typeof(IGraphTest).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.IsPublic && typeof(IGraphTest).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes(inherit: false).Any(a => a is FactAttribute { Skip: null }))
                .Select(m => (Interface: t, Method: m)))
            .Count(pair =>
                pair.Method.GetCustomAttributes<RequiresCapabilityAttribute>(inherit: false)
                    .Concat(pair.Interface.GetCustomAttributes<RequiresCapabilityAttribute>(inherit: false))
                    .Any(a => a.Capability == GraphCapability.FullTextSearch));

        var declared = CapabilitySet.All.Except(GraphCapability.FullTextSearch);

        Assert.True(gatedMethodCount > 0);
        Assert.Equal(
            ComplianceInventory.TotalTestMethods - gatedMethodCount,
            ComplianceInventory.MinimumExecuted(declared));
    }

    [Fact]
    public void MinimumExecuted_EmptySet_ExcludesEveryCapabilityGatedMethod()
    {
        var minimum = ComplianceInventory.MinimumExecuted(default);

        Assert.True(minimum > 0, "At least one mandatory (non-capability-gated) test must exist.");
        Assert.True(minimum < ComplianceInventory.TotalTestMethods,
            "IFullTextSearchTests methods require a capability, so declaring none must exclude them.");
    }

    [Fact]
    public void ExpectedCapabilitySkips_IsTotalMinusMinimumExecuted()
    {
        var declared = CapabilitySet.All.Except(GraphCapability.FullTextSearch);

        Assert.Equal(
            ComplianceInventory.TotalTestMethods - ComplianceInventory.MinimumExecuted(declared),
            ComplianceInventory.ExpectedCapabilitySkips(declared));
    }

    [Fact]
    public void ExpectedCapabilitySkips_All_IsZero()
    {
        Assert.Equal(0, ComplianceInventory.ExpectedCapabilitySkips(CapabilitySet.All));
    }

    [Fact]
    public void IFullTextSearchTests_IsAttributedWithRequiresCapability()
    {
        var attribute = typeof(IFullTextSearchTests).GetCustomAttribute<RequiresCapabilityAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(GraphCapability.FullTextSearch, attribute.Capability);
    }

    private interface IOverloadedMethods
    {
        void Execute(int value);

        void Execute(string value);
    }
}
