---
---

# Provider Implementers Guide

This guide documents the current provider contract. It describes what a provider must implement today and the storage/query conventions that must remain compatible across providers.

The shared `GraphQueryModel`, Cypher dialect SPI, neutral result wire model, and reusable provider certification suite have landed. See [Implementing a Cypher provider](#implementing-a-cypher-provider) and [Certifying a provider](#certifying-a-provider).

## Public SPI

A provider exposes a store type that owns database connectivity and returns an `IGraph` instance. The in-tree provider uses `Neo4jGraphStore` as the public type and keeps `Neo4jGraph` internal. New providers should follow the same shape: one public `XGraphStore` type, optional provider-specific extension methods, and an internal `IGraph` implementation.

The public interfaces to implement are:

- `IGraph` in `src/Graph/IGraph.cs`: CRUD, synchronous query roots, full-text search, schema/index provisioning, and transactions.
- `IGraphTransaction` in `src/Graph/IGraphTransaction.cs`: `CommitAsync()`, `RollbackAsync()`, and `IAsyncDisposable`.
- `IGraphQueryProvider` in `src/Graph/GraphQueryable/IGraphQueryProvider.cs`: expression execution, query creation, and async terminal execution.
- `IGraphQueryable<T>` in `src/Graph/GraphQueryable/`: the single LINQ root returned by `IGraph.Nodes<TNode>`/`Relationships<TRelationship>`/`DynamicNodes`/`DynamicRelationships`/search methods. Node-only and relationship-only operators (e.g. traversal) are gated by generic constraints on the operator itself (`where T : INode`), not by a separate receiver interface; the former per-kind queryable aliases have been removed.

`IGraph` methods accept an optional `IGraphTransaction`. A null transaction means the provider creates a per-operation or per-query execution transaction and owns its lifecycle. A non-null transaction means the caller owns commit/rollback/disposal, and the provider must reject foreign transaction implementations with a clear graph exception.

The provider store owns database connectivity and releases provider resources. `IGraph` instances do not own provider resources and are not disposable. If a store accepts an externally-owned driver/client, the store must not dispose that external object; `Neo4jGraphStore(IDriver, ...)` follows that rule.

## Entities And Schema

Domain node types satisfy `INode`; relationship types satisfy `IRelationship`. `IEntity` is an
identity-free marker. Application models should normally inherit from the base `Node` and
`Relationship` records, because direct interface implementation triggers analyzer warning CG011
unless the model needs full control. The base records implement only provider-populated runtime
metadata (`INode.Labels` and `IRelationship.Type`); they do not add identity, endpoints, or
direction. A caller property named `Id` or `Direction` is ordinary mapped data unless explicitly
configured otherwise.

Domain keys are optional schema metadata. Every `[Property(IsKey = true)]` member on an entity type
forms one key tuple, ordered by mapped property name. Key components must be non-null,
graph-storable scalars; they imply required/indexed semantics but are not immutable, implicit
mutation targets, relationship endpoints, or provider-native element identity. Keyless models are
valid. Providers enforce key uniqueness while keeping the physical identity used to correlate a
particular stored node or edge private.

Labels and relationship types come from attributes first:

- `[Node("Person")]` controls the node label. A node type maps to exactly one label, unique (case-insensitive) across all node types loaded in the process; `SchemaRegistry` throws a `GraphException` on a collision.
- `[Relationship("KNOWS")]` controls the relationship type (also one per type, unique case-insensitively).
- Without an attribute, `Labels.GetLabelFromType` derives a label/type from the CLR type name.
- Dynamic nodes use `DynamicNode.Labels` (an arbitrary, caller-managed label set); dynamic relationships use `DynamicRelationship.Type`.

Runtime metadata properties are provider-populated. `INode.Labels` and `IRelationship.Type` are empty before persistence for base records and should be populated after create/read based on actual stored labels/types.

`SchemaRegistry` reflects node and relationship schemas from CLR types and attributes. Providers are responsible for initializing schema and indexes before operations that require them. `RecreateManagedIndexesAsync()` may rebuild only artifacts whose ownership the provider can prove from deterministic names plus installed schema metadata; it must preserve unproven indexes and constraint-owned indexes, and successful completion means every rebuilt artifact is usable. A provider that owns no index artifacts implements the operation as a successful, cancellation-aware no-op. Neo4j owns current model-matching range indexes and two exact reserved full-text indexes; AGE and InMemory own none.

The v1.0 constraint contract rejects `IsKey` and `IsUnique` on every simple-collection
property, independent of element nullability. `SchemaRegistry` and generated `PropertySchema`
construction apply the same provider-neutral validation before a provider can create a physical
constraint or plan mutation uniqueness preflight. Providers must not define collection equality,
hashing, or private-companion constraint semantics as an extension of these flags.

## Marker-Method Protocol

Async terminal LINQ operators are represented in expression trees by internal marker methods in `src/Graph/GraphQueryable/QueryTerminals.cs`. `QueryableAsyncExtensions` builds `MethodCallExpression` nodes for these marker methods, then calls `IGraphQueryProvider.ExecuteAsync`. `QueryTerminals` is `internal`, not public API — a provider needs `InternalsVisibleTo` from `Cvoya.Graph` (already granted to `Cvoya.Graph.Neo4j`) to reference its members directly.

Providers recognize marker methods (and the rest of the LINQ surface) by `MethodInfo` identity — comparing a call's generic method definition (or the method itself, for non-generic methods) against a table built once via reflection — not by matching `MethodInfo.Name` as a string. The Neo4j provider's table lives in `CypherQueryVisitor`'s `LinqOperatorDispatch` helper; a new provider should build an equivalent table rather than switching on method names, since name-based dispatch cannot distinguish overloads and can silently mis-dispatch if an unrelated method happens to share a name.

Marker overload families to support:

- Materializers: `ToListAsyncMarker`, `ToArrayAsyncMarker`, `ToDictionaryAsyncMarker`, `ToLookupAsyncMarker`.
- Element operators: `FirstAsyncMarker`, `FirstOrDefaultAsyncMarker`, `LastAsyncMarker`, `LastOrDefaultAsyncMarker`, `SingleAsyncMarker`, `SingleOrDefaultAsyncMarker`, `ElementAtAsyncMarker`, `ElementAtOrDefaultAsyncMarker`.
- Quantifiers/counts: `AnyAsyncMarker`, `AllAsyncMarker`, `ContainsAsyncMarker`, `CountAsyncMarker`, `LongCountAsyncMarker`.
- Aggregates: `SumAsyncMarker`, `AverageAsyncMarker`, `MinAsyncMarker`, `MaxAsyncMarker`.

`AverageAsyncMarker` is closed over the numeric input type, not necessarily the public terminal
result type. In particular, `int` and `long` inputs use an input-typed marker while
`ExecuteAsync<double>` materializes the LINQ result. Nullable inputs similarly materialize the
corresponding nullable result. Providers must inspect the marker input/selector type for aggregate
translation and use the requested `ExecuteAsync<TResult>` type for result shaping.

The current Neo4j visitor dispatch table covers:

- Standard LINQ (both `System.Linq.Queryable`'s methods, reached when a chain degrades to plain `IQueryable<T>`, and `GraphQueryableExtensions`'s own graph-typed-chain-preserving equivalents): `Where`, `Select`, `OrderBy`, `OrderByDescending`, `ThenBy`, `ThenByDescending`, `Take`, `Skip`, `Distinct`, `SelectMany`, `GroupBy`, `Join`, `Union`, `Concat`.
- Async marker terminals: `ToListAsyncMarker`, `ToArrayAsyncMarker`, `FirstAsyncMarker`, `FirstOrDefaultAsyncMarker`, `SingleAsyncMarker`, `SingleOrDefaultAsyncMarker`, `LastAsyncMarker`, `LastOrDefaultAsyncMarker`, `AnyAsyncMarker`, `AllAsyncMarker`, `CountAsyncMarker`, `LongCountAsyncMarker`, `SumAsyncMarker`, `AverageAsyncMarker`, `MinAsyncMarker`, `MaxAsyncMarker`, `ContainsAsyncMarker`, `ElementAtAsyncMarker`, `ElementAtOrDefaultAsyncMarker`.
- Direct async names accepted by Neo4j (built by `QueryableAsyncExtensions.SumAsync`/`AverageAsync` alongside the marker path): `SumAsync`, `AverageAsync`.
- Graph extensions: `PathSegments` (including its direction overload), `TraversePaths`, `ShortestPath`, `AllShortestPaths`, `OptionalTraverse`, `WhereHasRelationship`, `OfLabel`, `OfLabels`, `Search`, plus the private `WithTraversalOptions` expression marker emitted by canonical traversal overloads.
- Synchronous fallbacks currently accepted for a subset: `First`, `FirstOrDefault`.

Note: `ReverseTraverse` is intentionally *not* in the dispatch table. It is a client-side extension method that eagerly composes `PathSegments`, the private incoming traversal-options marker, and `Select(ps => ps.EndNode)`, then calls `source.Provider.CreateQuery<T>` immediately rather than deferring. The literal method call never reaches the visitor as a `MethodCallExpression` node, so registering a handler for it would be dead code (this was a finding from the #80 characterization work).

**Two-arg traversal surface (issue #94, "Option C").** `Traverse<TRel, TEnd>`, `TraverseRelationships<TRel, TEnd>`, and `TraversePaths<TRel, TEnd>` are declared `this IGraphQueryable<INode> source` — the start node type is not one of the method's own generic arguments (`IGraphQueryable<T>` is covariant, so any `IGraphQueryable<TStart>` where `TStart : INode` converts to `IGraphQueryable<INode>` at the call site). A provider must therefore recover the start type from the **source expression's static element type** (`TypeHelpers.GetElementType(node.Arguments[0].Type)` in the Neo4j visitor), not from the call's generic arguments — by the time a `TraversePaths` `MethodCallExpression` is visited, its own generic arguments are only `(TRel, TEnd)`. `PathSegments<TStart, TRel, TEnd>` is unaffected: it keeps all three type arguments (its result type names the start type), so its dispatch handler still reads `TStart` directly off the call's generic arguments. The former three-arg compatibility overloads are removed; a provider only needs to translate the two-arg shape (or, for `PathSegments`-derived compositions like `Traverse`, the `PathSegments` call the two-arg operator builds internally).

**Operators chained after `TraversePaths` — lower paths before hops.** Keep the captured variable-length path as one semantic row while applying path predicates/projections, pagination, and cardinality terminals. The shared model supports `Where`, `Select`, `Take`, `Skip`, `Count`, and `Any`; only a path-valued materialization is decomposed into the per-hop wire shape consumed by `GraphResultProcessor.GraphPathHop`. The builder retains a single whitelist choke point for everything else: an unsupported operator must throw a named exception rather than translate against a hop alias. It also enforces composition order — the model applies path predicates before projection and pagination, so a `Where` that textually follows `Select`/`Take`/`Skip` on a path query is rejected at build time instead of silently filtering the wrong row set (an identity `Select(p => p)` is dropped as a projection no-op). Providers should consume this shared model boundary and preserve the same translates-correctly-or-throws-clearly dichotomy.

Not every marker family is fully dispatched by Neo4j today. A new provider should match current behavior for compatibility and clearly fail unsupported methods with `GraphException` rather than falling back to client-side execution unexpectedly.

## Storage Conventions

Cross-provider compatibility depends on matching these conventions exactly.

### Physical identity

Provider-native node and relationship identity is private plumbing. It may be used inside one
query or write transaction to de-duplicate selected rows, correlate command results, or connect
endpoints, but it is neither portable nor part of `IEntity`. Do not materialize it into typed or
dynamic property bags, infer an `Id` convention, or make users persist endpoint identifiers.

Relationships own only their mapped properties and runtime `Type`. The graph command API receives
endpoint intent separately: selected endpoints are frozen as exactly-one node selections, new
endpoints are serialized as new nodes, and providers connect the resulting native identities in
the same transaction. `IGraphPathSegment.Direction` reports the physical stored orientation
relative to the segment's `StartNode` and `EndNode`; no direction or endpoint data is written back
onto the relationship object.

### Labels And Types

For typed nodes, store labels derived from `[Node]` or the CLR type fallback. For dynamic nodes, store `DynamicNode.Labels` exactly. Neo4j also stores the runtime `Labels` property on the node to support materialization.

For typed relationships, store the relationship type from `[Relationship]` or the CLR type fallback. For dynamic relationships, store `DynamicRelationship.Type` exactly.

When dynamic or serialized complex-property types need database-safe labels, the current runtime serializer uses a Neo4j-safe CLR type encoding (`SanitizeTypeNameForNeo4j` in `EntityFactory`). Treat that as a de facto current convention only; naming cleanup is expected as the provider abstraction matures.

### Simple Properties

Simple properties are properties whose types pass `GraphDataModel.IsSimple` or `IsCollectionOfSimple`: supported primitives, enums, string, `Cvoya.Graph.Point`, temporal types, `decimal`, `Guid`, byte arrays, `Uri`, and collections of simple values. A `byte[]` is always one native simple scalar, even though it is also collection-shaped in .NET; it never uses the simple-collection payload and companion representation described below. Native-sized integers (`IntPtr`/`UIntPtr`, including `nint`/`nuint`) are primitive CLR types but are not graph-storable values. Providers should serialize supported simple properties directly to backend-native property values where possible.

During typed materialization, a null collection element is valid only when the consumer property's element schema is nullable. A null targeting a non-nullable value or reference element fails with `GraphException`; the diagnostic identifies the physical property, target element type, and zero-based element index. Providers must preserve null elements and their positions when the declared element schema permits them. Reference-element schemas that carry no nullable reference metadata (nullable-oblivious consumer code) are treated as non-nullable.

#### Null-bearing simple collections

`GraphCapability.NullElementsInSimpleCollections` certifies the complete logical contract: simple-property collections on typed and dynamic nodes and relationships preserve their count, order, null positions, and element type through create, replacement, read, delete, and supported predicates. A provider must not declare the capability for a partial implementation.

Providers whose native property lists accept null elements may keep the collection native. Neo4j property lists cannot contain nulls, so the Neo4j provider uses this versioned physical representation for every simple collection:

- the logical property stores a native list payload; null entries are omitted, while non-null entries retain their original order;
- `__cvoya_sc:v1:n:<name>` stores the exact zero-based null indexes;
- `__cvoya_sc:v1:t:<name>` stores the version-independent assembly-qualified element-type token; and
- `<name>` in each companion is the Base64Url-encoded UTF-8 logical property name.

The logical length is the payload count plus the null-index count, so leading, middle, trailing, repeated, empty, and all-null values are unambiguous. Query property access reconstructs the logical list with a server-side list expression before supported predicate evaluation; `Contains` preserves .NET null-membership semantics. Unsupported collection operations still fail during model validation or planning, before provider I/O.

AGE uses the same versioned companion names and element-type token, but keeps nulls in its native payload list; its null-index companion must exactly match those native positions. The in-memory provider retains `SimpleCollection.ElementType` directly and needs no physical companions. These choices still produce the same provider-neutral typed and dynamic materialization.

The namespace is collision-free for provider writes. A caller property whose logical name starts `__cvoya_sc:v1:` is stored as `__cvoya_sc:v1:u:<name>`, using the same reversible Base64Url encoding. Unreserved property names remain byte-for-byte native. Readers reverse user-name escaping, consume only a complete valid companion set, and never expose companions through typed or dynamic materialization. Entity replacement writes the payload and companions together and removes stale state; set-based property replacement writes or clears the complete set in one statement; entity deletion removes it with the owner. This is the stable-v1 representation only—there is no alpha-database reader, migration, or dual-write path.

Projections have no property schema to consult, so results materialized through a constructor — records, DTOs, anonymous types — derive the same rules from the constructor parameter's own nullability metadata. A present or absent null targeting a required non-nullable value parameter fails with `GraphException` rather than becoming `default(T)`; a named reference parameter whose metadata is non-nullable fails the same way. Optional parameters retain their declared defaults, and nullable, nullable-oblivious, or anonymous-type scalar parameters preserve null (an anonymous type does not retain the projected expression's reference-nullability contract). Collection validation is recursive: a null at any nesting level targeting a non-nullable element fails with an indexed diagnostic naming the constructor parameter, while nullable elements preserve nulls and order. Complex-object collections use the same element contract.

Collection elements without nullable-reference metadata fail closed. This includes anonymous-type constructor parameters and unconstrained generic parameters: the runtime metadata for unconstrained `T` and `T?` is indistinguishable after the declaring type is closed. Use a named projection with concrete element annotations, or a reference-constrained generic parameter (`where T : class`) whose `T`/`T?` metadata remains distinguishable, when null elements are valid. Top-level scalar collection results preserve one output position per processed row; reference and nullable-value elements may therefore contain null, while a null targeting a non-nullable value element fails before the collection is constructed.

`[Property]` controls storage names, key/index/full-text inclusion, and required/ignored semantics through `SchemaRegistry`. Relationship entities may only have simple properties; the Neo4j provider rejects complex properties on relationships.

When **dynamic** entities are materialized, a simple-collection wire value (a `GraphValue.List` of scalars) is reconstructed as a canonical `List<T>` in the property bag — the caller's original container type is not retained. Providers that declare `NullElementsInSimpleCollections` attach the stored element-type token to the wire list, so empty and all-null collections retain `T`; otherwise `T` is inferred from non-null runtime wire values, with mixed or all-null collections falling back to `object`. A value-type element type is promoted to `Nullable<T>` when nulls are present. Element order is preserved, and empty collections stay empty. Map-valued wire properties (`GraphValue.Map`) are not simple collections; they pass through to the property bag as dictionaries.

### Complex Properties

Complex properties are first-class graph structure. A property named `Home` whose CLR type is
`Address` is stored as:

```text
(:Person)-[:Home]->(:Address)
```

The relationship type is the property name by convention. `[ComplexProperty(RelationshipType =
"LIVES_AT")]` overrides it. Providers should use
`GraphDataModel.GetComplexPropertyRelationshipType` so generated and reflection-based serializers
agree on the mapping.

Collections of complex properties use one relationship per non-null collection item and store the
item's original zero-based index in the relationship's `SequenceNumber` property. The owner also
stores four private companions: logical length (`__cvoya_sc:v1:c:l:<name>`), null indexes
(`__cvoya_sc:v1:c:n:<name>`), and the declared element-type identity
(`__cvoya_sc:v1:c:t:<name>`), plus the semantic relationship type
(`__cvoya_sc:v1:c:r:<name>`). `<name>` is the Base64Url-encoded physical property label. Keeping the
namespace beneath the simple-collection prefix lets the existing user-property escaping protect it
from collisions. Empty and all-null collections therefore have an explicit representation without
creating fake nodes or relationships. Persisting the relationship type also lets dynamic readers
correlate children unambiguously when `[ComplexProperty(RelationshipType = "...")]` overrides the
property-name convention.

Readers reconstruct the ordered slot list from the union of real child `SequenceNumber` values and
declared null indexes. Every index from zero through logical length minus one must occur exactly once;
a duplicate, out-of-range index, missing child, partial companion set, or incompatible element type is
corrupt storage and fails with `GraphException`. `PropertySchema.IsElementNullable` reflects the CLR
element annotation for both simple and complex collections. A nullable declaration preserves null
slots; a non-nullable declaration still rejects a null with an indexed diagnostic. Derived runtime
elements remain valid when assignable to the declared element type. Replacements clear and write all
four companions atomically with the real child relationships, and cascade deletion touches only real
value nodes. This representation is the current write contract; providers are not required to migrate
previously deployed storage that predates it.

Every occurrence is a separate value node, even when two owners reference the same in-memory object.
Providers assign any physical correlation identity privately; it is never a public CVOYA Graph
property or convention. Value nodes and their property-named relationships remain visible to
ordinary graph queries. Providers may add an internal relationship marker for cascade cleanup, but
must not infer complex-property edges from a reserved relationship-name prefix.

When **dynamic** entities are materialized, a complex-property value is reconstructed into the property bag using one canonical shape, shared by dynamic nodes and dynamic relationships so the same stored value round-trips identically regardless of owner: a single complex value becomes a `Dictionary<string, object?>` keyed by the stored (physical) property labels, and a complex-property collection becomes a `List<Dictionary<string, object?>?>`. A simple collection nested inside such a dictionary follows the canonical `List<T>` rule above, and a nested complex value recurses into a further dictionary, so the whole subtree survives the round trip. A dynamic entity has no CLR type to materialize into, so the value is never rehydrated as a typed instance. Providers that decompose complex values into storage nodes must remove those nodes' synthetic `Id` and `Labels` members when rebuilding the dictionary; only caller-supplied value members belong in the materialized shape.

Declared properties auto-load recursively. Writes and reads must reject cycles and paths deeper than
`GraphDataModel.DefaultDepthAllowed` (currently 5). A read type that omits the property leaves it
unpopulated; callers can still traverse the semantic relationship and co-load the owner and value via
the normal path-segment projection.

### Type Metadata

The neutral metadata convention is a property named `__metadata__` containing a `type` key with the
version-independent assembly-qualified CLR type name when runtime type recovery is needed. The
identity retains the assembly name but omits assembly-version components so ordinary package
upgrades do not change a model type's identity. `GraphResultProcessor` reads
this shape from neutral wire values before falling back to node labels or relationship type for
polymorphic resolution. Providers must preserve the shape on writes and adapt the metadata map like
any other result map. A backend that cannot persist a map-valued entity property may store the
assembly-qualified name as a scalar under the same reserved property; the materializer accepts both
forms.

Relationship updates must treat the authoritative storage/logical type and exact concrete CLR type
as immutable identity. Compare both before replacing any properties, and keep the comparison in the
same guarded mutation as the write (or repeat it in the write predicate after a lookup). A failed
identity check must leave properties, endpoints, direction, and all type metadata unchanged. Changing
identity is an explicit delete-and-recreate operation, never an implicit update.

## Behavioral Contracts

Mutations must validate entity constraints before writing: non-null entities, optional key and
unique constraints, required properties, exact-one endpoint selections in the same graph/transaction
scope, no reference cycles, and bounded complex-property depth. Cycle detection is implemented by
`GraphDataModel.HasReferenceCycle` / `EnsureNoReferenceCycle`; depth validation uses
`EnsureComplexPropertyDepth`.

Queries must stay provider-side for supported LINQ operators. Unsupported operators should fail with a clear `GraphException`. Avoid silent client-side evaluation unless the operator explicitly materializes results.

Transaction behavior:

- Null transaction: create a short-lived transaction/session for the operation or query execution.
- Caller transaction: execute inside the supplied provider transaction and do not commit or roll it back automatically.
- `CommitAsync()` completes the transaction; `RollbackAsync()` aborts it; disposal should clean up uncommitted transactions.

Full-text search is part of `IGraph`: `Search`, `SearchNodes`, `SearchRelationships`, and typed overloads — thin synchronous conveniences over the `.Search()` LINQ operator (building a queryable performs no I/O). Providers should respect `[Property(IncludeInFullTextSearch = false)]`, support dynamic entities, and initialize required full-text indexes. A typed node search is also a valid source for `Traverse`, `PathSegments`, and `TraversePaths`; providers must preserve the searched node scope through every subsequent traversal step and LINQ operator. Mixed node-and-relationship search remains non-traversable because it has no single typed node scope.

Exception behavior follows the public API contract: provider/backend failures are wrapped in
`GraphException`; endpoint selections that return zero or multiple rows fail; set mutations return
the distinct affected-entity count; and invalid caller input preserves argument exceptions where
the public API already does so.

## Contract-Test Reuse

The provider contract suite is the `Cvoya.Graph.CompatibilityTests` package
(`src/Graph.CompatibilityTests`) - see
[Certifying a provider](#certifying-a-provider) below for the full workflow. It mostly defines test
interfaces with default xUnit test methods; running the package alone proves little because
providers must bind those interfaces in a provider-specific test project.

The in-memory provider is the worked harness example:

- `tests/Graph.InMemory.Tests/Infrastructure/InMemoryHarness.cs` implements `IGraphProviderTestHarness` with no external infrastructure.
- `tests/Graph.InMemory.Tests/InMemoryTest.cs` derives from `CompatibilityTest` and exposes `IGraph Graph`.
- Concrete classes in `tests/Graph.InMemory.Tests/GraphTests/` inherit `InMemoryTest` and implement one or more `Cvoya.Graph.CompatibilityTests.I...Tests` interfaces.
- Provider-specific tests live beside the inherited contract tests.

A new provider test project follows the same three-piece shape (harness → intermediate base class → one-line interface bindings). `examples/CompatibilityTests.SampleHarness` is a compiling skeleton; `tests/Graph.InMemory.Tests` is the in-tree worked implementation, and `tests/Graph.Age.Tests` demonstrates the same SPI with an external database.

## Shared Query Translation

### Level-1 GraphQueryModel

`Cvoya.Graph.Querying.GraphQueryModel` is the public, provider-neutral semantic query model. Its
roots, predicates, traversal steps, projection, ordering, paging, and terminal operation describe what a
query asks for without choosing a query language. Providers that do not target Cypher may consume this
model directly; the expression-to-model builder remains internal so the public LINQ surface and its
recognition table evolve as one release unit.

Recognition uses `MethodInfo` identity against the shared dispatch table, never method-name strings. Before
recognition, the front-end enforces configurable expression-tree bounds (10,000 nodes and depth 100 by
default). Funcletization evaluates parameter-free closure values and method calls; expressions referencing a
query parameter are not evaluated during translation. This is a structural allowlist, not a security sandbox
for application code.

## Implementing A Cypher Provider

A minimal Cypher provider supplies three provider-specific pieces: an `ICypherDialect`, an executor, and a driver-to-wire adapter. It reuses `GraphQueryModel`, `CypherQueryPlanner`, `CypherRenderer`, `GraphResultMaterializer`, and the compatibility suite. Do not fork the planner, renderer, complex-property reassembly, polymorphic type resolution, or path stitching.

### 1. Declare the dialect

Implement `ICypherDialect` from `Cvoya.Graph.Cypher`. The interface owns every syntax choice that can vary by backend:

- parameter references, mapped property access, and provider-private element-identity access;
- identifier escaping, node-label lists, relationship-type lists, and depth ranges;
- provider-neutral function names such as `temporal.datetime` and `string.join`;
- label predicates and the complex-property relationship marker;
- full-text search rendering (`RenderFullTextSearch`) — the dialect owns the whole clause, including any procedure/index names and mixed-entity subquery shape; and
- a `CapabilitySet` using the same `GraphCapability` enum as the compatibility suite.

`RenderFullTextSearch(FullTextSearchClause, ICypherRenderContext)` has a default implementation that throws `GraphQueryTranslationException`, so a dialect that does not declare `FullTextSearch` need not implement it. Dialects that do declare it render the clause using the supplied `ICypherRenderContext` (expression and literal rendering) and keep every backend-specific name (Neo4j's `db.index.fulltext.*` procedures and `*_fulltext_index` indexes) private to the dialect rather than in the shared renderer.

`GetFunctionBehavior` distinguishes functions rendered by the backend, parameter-free functions evaluated on the client, and unsupported functions. Client evaluation binds a query parameter; it never inlines the value. A function marked `EvaluateOnClient` fails translation if its arguments depend on a server-side expression. AGE can therefore client-evaluate zero-argument temporal constructors without pretending to support temporal arithmetic over stored properties.

Declare only supported capabilities. The planner rejects reachable unsupported constructs before execution with `GraphQueryTranslationException`; the message names the construct, the exact `GraphCapability` member, and the dialect. Current translation-time checks cover `FullTextSearch`, `CallSubqueries`, `PatternSizeProjection`, `MultiLabelMatch`, `LabelFiltering`, `OrderByEntity`, `OptionalTraversal`, `GroupByAggregation`, `RelationshipPredicates`, `ShortestPath`, and `SetOperations`. Transaction capabilities remain execution/store concerns.

`LabelFiltering` gates non-empty `OfLabel` and `OfLabels` operations over typed or dynamic node
scopes. `Any` is a disjunction over requested labels; `All` is a conjunction. An empty request is
an identity operation and reaches no provider. Treat labels as values, never identifiers: lower
them through the dialect's label-test/literal rendering path, validate their bound node alias, and
apply them before projection and paging. All in-tree providers implement the contract.

`RelationshipPredicates` gates both traversal-option `WhereRelationship` predicates and
`WhereHasRelationship` existence patterns. A variable-length traversal applies its predicate to
every relationship while expanding a candidate path; an existence filter lowers directly to an
anchored relationship pattern. Providers must implement those semantics or decline the capability—
post-hoc client filtering and silently ignoring the predicate are not conforming implementations.
All in-tree providers implement the contract. AGE preserves variable-path predicates through
indexed list filtering and lowers anchored existence patterns through grouped optional matches.

`ShortestPath` gates both `ShortestPath<TRel, TEnd>` and `AllShortestPaths<TRel, TEnd>`.
Implementations select paths independently for every source/endpoint pair, evaluate the endpoint
predicate before selection, exclude the source as an endpoint, and return one or all ties at the
minimum positive hop count. Neo4j lowers these to `shortestPath(...)` and
`allShortestPaths(...)`; in-memory performs the equivalent expansion-time selection. Apache AGE
1.8.0 lowers them to the native `ag_catalog.age_shortest_path` / `ag_catalog.age_all_shortest_paths`
set-returning functions, because AGE still rejects the Cypher pattern syntax. AGE 1.7.0 has neither
function and declines the capability.

Capabilities may be determined per connected backend version. The AGE harness reports the connected
store's version-gated capability set: `ShortestPath` is present on 1.8.0 and later and absent on
1.7.0.

`SetOperations` gates typed and standard-LINQ `Union` plus typed `Concat`. Both operands are planned
independently with disjoint parameter namespaces. `Union` uses distinct row semantics; `Concat`
uses `UNION ALL` and must not acquire implicit distinctness. Providers validate compatible entity
or scalar projection shapes at the shared model boundary. Repeated same-kind left chains are
represented as recursive binary `UnionFragment` models: `Union` removes duplicates across the
complete tree, while `Concat` preserves operand order and duplicates. Parameter prefixes must remain
disjoint recursively. Flat mixed chains are rejected by the builder; explicitly nested mixed trees
retain their declared grouping. All in-tree providers implement the contract; AGE sends every
branch through its provider-local lowering pipeline and normalizes the result aliases.

`PatternSizeProjection` gates every relationship-count pattern subquery a projection can produce. The node relationship-count (degree) surface `CountRelationships<TRel>(direction)` lowers to a `COUNT { MATCH (src)-[:REL]->() }` / `size((src)-[:REL]->())` subquery. Relationship direction is physical (matching traversal), compatible derived relationship labels participate, and an undirected self-loop counts once. A provider that declines the capability rejects it at translation time. Complex-property collection `.Count` instead reads the owner's stored logical length so null slots are counted without requiring this capability.

A provider should decline every capability its native dialect or structured lowering cannot preserve.
AGE, for example, declares `CallSubqueries` and `PatternSizeProjection` only because its provider-local
AST pass replaces the unsupported syntax with equivalent grouped matches; it still declines
`NestedTransactions`, and it declares `ShortestPath` only on servers that expose the native
shortest-path functions. Do not emulate an unsupported feature with a semantically weaker query
while still declaring its capability.

Declaring `FullTextSearch` guarantees: case-insensitive, exact-token (whole-word) matching; a multi-term query matches an entity iff ALL terms match, in any order and at any distance; the matched property set is exactly the entity's own `[Property(IncludeInFullTextSearch)]` string properties (string-only by construction; for dynamic entities, all string property values). Text on complex-property value nodes is NOT part of the owning entity's match set. Ranking, stemming, phrase adjacency, prefix/wildcard, and matching beyond the floor are provider-defined: the TCK asserts nothing about them and never asserts a non-match for near-tokens (only for sub-tokens, which must not match). Search result order is unspecified; ordering comes only from explicit `OrderBy`. Providers share one definition of a "term" via `FullTextQueryTokenizer` (split on any non-letter/non-digit, lowercase invariant, drop empties) and each lower it into their own engine syntax; the shared planner keeps the raw query string.

Provider-specific dynamic-entity accessor methods must carry `[CypherDynamicEntityAccessor]`. The planner checks the attribute on the exact `MethodInfo`; a matching method or declaring-type name is not sufficient.

### 2. Plan and render

Build the shared semantic model, then pass the same dialect instance to planning and rendering:

```csharp
var model = GraphQueryModelBuilder.Build(expression);
var statement = new CypherQueryPlanner(dialect).Plan(model);
CypherRenderResult rendered = new CypherRenderer(dialect).Render(statement);
```

`CypherRenderResult` carries query text, the parameter map, and the exact ordered projection-column names. The column schema is part of the executor contract, not optional metadata: Apache AGE needs it to build the mandatory `AS ("alias" agtype, ...)` SQL column-definition list without reparsing `RETURN`. Preserve alias spelling and casing. Parameter transport remains executor-specific; for example, AGE may serialize the map into one `agtype` parameter even though in-query references use `$name`.

### 3. Adapt driver values

The executor sends `CypherRenderResult.Text` and parameters to the backend and adapts every returned row into `GraphRecord`. Build values only through the validated `GraphValue` factories:

- `Scalar` for provider-neutral CLR scalars (`long`, `decimal`, `DateOnly`, `DateTimeOffset`, `Point`, and so on);
- `Node` with a wire-local opaque provider element ID, adapter-populated labels, and recursively
  adapted properties;
- `Relationship` with wire-local opaque provider element/end-point IDs, type, and properties;
- `Path` for an alternating node/relationship sequence;
- `List` and `Map` for recursive collection/projection values.

Factories defensively copy collections and reject invalid path shapes or null entries (represent
null as `GraphValue.Scalar(null)`). Adapters must not leak driver types. Wire-local IDs exist only
to correlate returned values and paths; never materialize them into domain properties. Preserve
integer versus floating-point values and decimal precision; stringify neither wire IDs nor numerics
merely to simplify conversion. For AGE, reconstruct inheritance labels from its stored
`inheritance_labels` representation before calling `GraphValue.Node`.

Pass buffered records to `GraphResultMaterializer.MaterializeAsync<T>` or individual records to
`MaterializeRecordAsync<T>`. The shared materializer owns complex-property subtree assembly,
label/`__metadata__` polymorphism, scalar conversion, projections, and path stitching. Path
segments receive endpoints and orientation; relationship domain objects do not. An adapter
translates values only; it must not choose CLR domain types or rebuild owned-property graphs.

### Provider-facing shared types

The implementation seam is intentionally small:

- `ICypherDialect`;
- `CypherRenderResult`;
- `GraphRecord`;
- `GraphValue`; and
- `GraphResultProcessor.GraphPathHop` only when streaming decomposed `IGraphPath` rows.

`CypherRenderer`, `GraphResultMaterializer`, and `GraphResultProcessor` are shared services, not provider implementations. The in-tree `Neo4jRecordAdapter` is the reference boundary: it is driver-only and contains no materialization policy.

## Certifying A Provider

The `Cvoya.Graph.CompatibilityTests` package is a shippable TCK: a harness SPI, a capability registry so backends that legitimately lack a feature (e.g. server-side full-text search) skip rather than fail, and a compliance guard that catches a mis-wired provider project (one that omits required contract methods) instead of letting it silently "pass" with incomplete coverage.

### 1. Implement the harness SPI

```csharp
public sealed class MyProviderHarness : IGraphProviderTestHarness
{
    public string ProviderName => "MyCompany.CVOYA graph.MyProvider";

    // Declare only what your backing store actually supports. A test gated by an unlisted
    // capability skips; infrastructure failures and all ungated failures still fail.
    public CapabilitySet Capabilities => CapabilitySet.Of(
        GraphCapability.Transactions,
        GraphCapability.ComplexPropertyCascade);

    public ValueTask InitializeAsync() => /* start/connect infrastructure once per test class */;
    public ValueTask DisposeAsync() => /* release it */;

    public ValueTask<IGraph> GetGraphAsync(StoreIsolation isolation, CancellationToken ct)
    {
        // StoreIsolation.CleanSharedStore: reuse the per-class store, wipe its data.
        // StoreIsolation.FreshStore: provision a brand-new store (needed where a data wipe alone
        // doesn't reset auxiliary state, e.g. full-text index state).
        // StoreIsolation.IndependentStore: return an ADDITIONAL, distinct store INSTANCE that
        // coexists with every store already handed to the running test - do not reset, replace, or
        // dispose the earlier one. Cross-store transaction-ownership tests hold two live stores at
        // once and assert on store identity, so the second store MAY share the first one's
        // database; doing so is the stronger test and costs no extra infrastructure.
        // Throw GraphProviderUnavailableException if infrastructure cannot start or be reached.
        // It always fails the test; strict mode does not change that result.
    }

    public ValueTask SeedExternalGraphAsync(IGraph graph, string marker, CancellationToken ct)
    {
        // Use the backend's native API (not CVOYA writes/provisioning) to create the two keyless
        // ContractExternalNode rows and outgoing ContractExternalRelationship fixture described by
        // IGraphProviderTestHarness. This certifies typed/dynamic/native interoperability.
    }

    public ValueTask<IReadOnlyCollection<string>> GetStoreArtifactsAsync(
        IGraph graph,
        CancellationToken ct)
    {
        // Return stable, sorted identities for backend labels/tables, indexes, constraints,
        // functions, and equivalent infrastructure. The read-only contract compares snapshots.
    }

    public ValueTask<int> CountNodesByPropertyAsync(
        IGraph graph,
        string label,
        string propertyName,
        IReadOnlyCollection<string> values,
        CancellationToken ct)
    {
        // Execute the narrowest provider-native count needed by the complex-property orphan
        // contracts. This is not a general raw-query escape hatch.
    }

    public bool IsExpectedConcurrentUpdateException(Exception exception)
    {
        // Return true only for provider-specific serialization, deadlock, or retryable write
        // conflicts that may legitimately reject one of two same-node updates.
    }
}
```

### 2. Extend `CompatibilityTest` once

```csharp
public abstract class MyProviderTest(MyProviderHarness harness)
    : CompatibilityTest(harness), IClassFixture<MyProviderHarness>;
```

### 3. Bind the `I*Tests` interfaces

One line per suite interface (see `src/Graph.CompatibilityTests/I*.cs` for the full set):

```csharp
public class BasicTests(MyProviderHarness h) : MyProviderTest(h), IBasicTests;
public class FullTextSearchTests(MyProviderHarness h) : MyProviderTest(h, StoreIsolation.FreshStore), IFullTextSearchTests;
public class ProviderContractTests(MyProviderHarness h) : MyProviderTest(h, StoreIsolation.FreshStore), IProviderContractTests;
// ... one per interface
```

### 4. Arm the compliance guard

```csharp
[assembly: AssemblyFixture(typeof(Cvoya.Graph.CompatibilityTests.ComplianceGuard))]
```

The guard is unarmed by default. Set `GRAPHMODEL_COMPLIANCE_STRICT=1` to enforce the complete
capability-eligible execution floor; CI compliance lanes should always run this way:

```bash
GRAPHMODEL_COMPLIANCE_STRICT=1 dotnet test <your-test-project> --report-trx
```

`GraphProviderUnavailableException` always fails, in local and strict runs alike.
`CompatibilityTest` does not catch or translate it. Strict mode changes only the execution-floor
guard: it ensures every capability-eligible packaged TCK method was bound and executed, so a
mis-wired provider project cannot pass with incomplete coverage.

The guard records the declaring interface plus full method signature after each successful store
acquisition, then compares those identities with the exact capability-eligible inventory. Multiple
rows of one `[Theory]` still cover only that one method, and provider-specific tests cannot cover a
missing TCK method. A failure lists every missing identity and the declared capability set. Strict
mode also fails with a wiring error when the xUnit host does not expose the running `MethodInfo`.
Provider binding classes must inherit default test bodies unchanged: replacing one can assert the
opposite behavior while retaining its interface method identity, so the guard rejects every such
mapping. Put a legitimate divergence in a truthfully named packaged TCK method and gate it with the
applicable capability/expectation instead.

### 5. Read the results

- **Capability skips** carry a fixed, parseable reason: `Capability '<Name>' not declared by provider '<ProviderName>' (Cvoya.Graph.CompatibilityTests <version>)`. Any other skip or a nonzero failure count needs investigation.
- **The compliance report**: fill in `COMPLIANCE.md` (template in
  `src/Graph.CompatibilityTests/COMPLIANCE.md`) from your TRX results - N passed cases / M
  skipped-by-declared-capability / 0 failed. Report
  `ComplianceInventory.MinimumExecuted(yourDeclaredCapabilities)` separately as the required
  method-identity inventory; theory rows can make N larger than that method count.
- **"Compatible"** means: 0 failed, every skip is a declared-capability skip, and the strict guard confirms that every capability-eligible method identity executed.

See `examples/CompatibilityTests.SampleHarness` for a minimal compiling skeleton of all three pieces, and `tests/Graph.InMemory.Tests` for the full in-tree worked implementation.

### 6. Capability coverage map

Every optional `GraphCapability` is either certified by a `[RequiresCapability]`-gated test (which runs where the harness declares the capability and skips-with-reason where it doesn't) or recorded here as having no certifiable user surface yet. The last three columns show what the in-tree harnesses do today. Neo4j declares every capability with a user-visible surface; in-memory and AGE also declare features they provide through equivalent evaluation or lowering rather than native Cypher syntax.

| Capability | Certifying test(s) | Attribution | Neo4j | in-memory | AGE |
|---|---|---|---|---|---|
| `FullTextSearch` | `IFullTextSearchTests` (all methods) | interface | pass | pass | pass |
| `Transactions` | `ITransactionTests` (all methods) | interface | pass | pass | pass |
| `ComplexPropertyCascade` | `IComplexObjectGraphSerializationTests` (all methods) | interface | pass | pass | pass |
| `CallSubqueries` | `IAdvancedQueryTests` correlated-collection pattern comprehensions and grouped-projection rejection cases | method | pass | pass | pass |
| `PatternSizeProjection` | `IAdvancedQueryTests.CanProjectRelationshipCounts` (node degree via `CountRelationships<TRel>(direction)`) | method | pass | pass | pass |
| `MultiLabelMatch` | `IAdvancedQueryTests.CanQueryPolymorphicBaseTypeAcrossSubtypeLabels` | method | pass | pass | pass |
| `LabelFiltering` | `IQueryTraversalTests.LabelFilters_PinSubtypeDynamicAnyAllEmptyCompositionAndSafetySemantics` | method | pass | pass | pass |
| `OrderByEntity` | `IAdvancedQueryTests.CanOrderByBareEntity` | method | pass | skip | skip |
| `OptionalTraversal` | `IQueryTests.Navigation{Equality,Projection}_MissingComplexProperty*`, `IQueryTraversalTests.OptionalTraverse_PreservesUnmatchedRowsAndPinsMatchDirectionAndProjectionSemantics`, `IQueryTraversalTests.OptionalTraverse_SourceLabelFilterEliminatesRowsBeforeTheLeftMatch` (also gated on `LabelFiltering`) | method | pass | pass | pass |
| `GroupByAggregation` | `IGroupByTests` (all methods) | interface | pass | pass | pass |
| `NestedTransactions` | _record only_ | — | — | — | — |
| `RelationshipPredicates` | `IQueryTraversalTests.VariableTraversal_RelationshipPredicateFiltersEveryExpandedHop`, `IQueryTraversalTests.WhereHasRelationship_RespectsDirectionPredicateAndSelfRelationships` | method | pass | pass | pass |
| `ShortestPath` | `IQueryTraversalTests.ShortestPaths_PinSelectionEndpointDirectionNoPathAndSameNodeSemantics` | method | pass | pass | pass (1.8) / skip (1.7) |
| `SetOperations` | `IQueryTraversalTests.TypedUnionAndConcat_PinDistinctBagAndScalarProjectionSemantics` | method | pass | pass | pass |
| `NullElementsInSimpleCollections` | `IBasicTests` and `IGraphCommandTests` null-bearing collection cases | method | pass | pass | pass |

The correlated grouped-projection grammar (`GroupBy(seg => seg.StartNode).Select(g => new { … })`)
is a shared contract, not a per-provider concern: the recognized per-member operations (`Select`,
`Where`, `OrderBy`/`OrderByDescending`, `Count`, `Average`/`Sum`/`Min`/`Max`, nested `GroupBy`, or a
group-key projection) are defined once in `CorrelatedGroupProjectionValidation` and enforced up-front
by every provider. A member that steps outside that grammar (for example `First`/`Take`/`Skip` over
the group) must be rejected with the same `GraphQueryTranslationException` and message everywhere —
the TCK rejection cases also pin collection-without-`Select`, multiple-`Select`, and
operation-after-`Select` composition so a provider does not silently execute a shape it cannot lower.
`ScalarGroupByValidation` provides the corresponding shared boundary for scalar grouping: key and
aggregate projections only (`Count`/`LongCount`/`Sum`/`Average`/`Min`/`Max`, all lowered to the
provider's aggregation stage, with `LongCount` mapping to the same count aggregate as `Count`),
without collection projections or filtering inside a group.

One capability is recorded rather than gated because it has no user-drivable surface to certify:

- **`NestedTransactions`** — the public transaction API takes no parent transaction or savepoint, so there is no nested operation to drive. No in-tree provider declares it. When the API gains that surface, add a gated test with the feature.
