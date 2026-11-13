// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph;

/// <summary>
/// Optional provider capabilities. Names describe user-visible features or query constructs and
/// are stable across releases: members are only ever added, never renamed or removed, so a
/// provider's declared <see cref="CapabilitySet"/> keeps meaning what it meant when it was
/// written. Consumers include query-surface dialects (which use these members to describe what a
/// backing store can translate) and the compatibility test suite (which uses them to decide which
/// contract tests to skip for a provider that has not declared a given capability).
/// </summary>
public enum GraphCapability
{
    /// <summary>
    /// Server-side full-text search over indexed string properties (e.g. Neo4j's
    /// <c>db.index.fulltext</c>).
    /// </summary>
    FullTextSearch,

    /// <summary>
    /// Explicit, provider-managed transactions via <see cref="IGraphTransaction"/>.
    /// </summary>
    Transactions,

    /// <summary>
    /// Transactions nested within an already-open transaction (savepoint-style semantics where an
    /// inner transaction can commit or roll back independently of its enclosing one). Reserved for
    /// a future transaction API that exposes an enclosing transaction or savepoint.
    /// </summary>
    NestedTransactions,

    /// <summary>
    /// Cascading create/delete of complex-property subtrees (nested owned objects persisted as
    /// part of their owning node or relationship).
    /// </summary>
    ComplexPropertyCascade,

    /// <summary>
    /// Nested subquery execution equivalent to Cypher's <c>CALL {}</c>, used for constructs such
    /// as grouped aggregation over a correlated subquery.
    /// </summary>
    CallSubqueries,

    /// <summary>
    /// Projection of relationship-pattern sizes (e.g. Cypher's <c>size(&lt;pattern&gt;)</c>).
    /// </summary>
    PatternSizeProjection,

    /// <summary>
    /// Matching against multiple labels/types in a single pattern (e.g. Cypher's <c>:A|B</c>).
    /// </summary>
    MultiLabelMatch,

    /// <summary>
    /// Filtering node query scopes by caller-supplied stored labels with explicit any/all
    /// semantics.
    /// </summary>
    LabelFiltering,

    /// <summary>
    /// Ordering results by an entire node or relationship variable, rather than by one of its
    /// properties.
    /// </summary>
    OrderByEntity,

    /// <summary>
    /// Typed shortest-path and all-shortest-path traversal queries.
    /// </summary>
    ShortestPath,

    /// <summary>
    /// Traversals where a relationship hop may be absent from the result without excluding the
    /// matched entity (optional/outer-join-style traversal). Backs optional complex-property
    /// navigation: a query that reads through a complex property some owners may lack lowers to an
    /// <c>OPTIONAL MATCH</c>, so owners without the property survive (with a null leaf) rather than
    /// being filtered out.
    /// </summary>
    OptionalTraversal,

    /// <summary>
    /// Grouping a node set by a scalar key and projecting per-group aggregates
    /// (<c>GroupBy(x =&gt; x.Key).Select(g =&gt; new { g.Key, g.Count(), g.Sum(...), ... })</c> and the
    /// equivalent result-selector overload). Lowers to an implicit-grouping <c>WITH … RETURN …</c>
    /// where non-aggregated terms form the grouping key. Distinct from <see cref="CallSubqueries"/>,
    /// which backs the correlated collection-projection shape (grouping path segments by their start
    /// node); this member backs scalar-key aggregation grouping.
    /// </summary>
    GroupByAggregation,

    /// <summary>
    /// Relationship predicates evaluated as part of traversal expansion and relationship-existence
    /// pattern matching.
    /// </summary>
    RelationshipPredicates,

    /// <summary>Typed distinct union and bag-preserving concatenation of compatible query shapes.</summary>
    SetOperations,

    /// <summary>
    /// Simple-property collections preserve null elements, their positions, and their declared
    /// element type across persistence, materialization, updates, and supported predicates.
    /// </summary>
    NullElementsInSimpleCollections,

    /// <summary>
    /// The provider can materialize declared <see cref="PropertyIndexDefinition"/> instances into
    /// query-accelerating structures through <see cref="IGraphIndexMaintenance"/>.
    /// </summary>
    PropertyIndexes,

    /// <summary>
    /// The provider can materialize declared <see cref="PropertyAggregateDefinition"/> instances into
    /// derived aggregate structures through <see cref="IGraphIndexMaintenance"/>.
    /// </summary>
    PropertyAggregateIndexes
}
