---
---

# Migrate a 0.x application to the v1 model

Version 1 establishes an identity-free public entity model and native provider storage. This is a
deliberate pre-v1 compatibility break, not an in-place database upgrade.

## Read this before changing data

Back up every database before attempting a manual transition.

CVOYA Graph does not ship or support:

- a reader for alpha-era universal IDs, endpoint IDs, universal roots, or legacy metadata;
- a mixed legacy/native read mode;
- dual writes;
- an automatic migration or backfill;
- automatic deletion, relabeling, or rewriting of an existing database; or
- a supported SQL/Cypher transformation script.

Update and validate the application model first. Then create a clean v1 graph and reimport from a
trusted source, or design and review your own data transformation outside the library. Do not point
the v1 provider at alpha data and assume it will reinterpret it.

## 1. Remove universal entity identity

`IEntity` is now an empty marker. `Node` and `Relationship` no longer generate or expose an `Id`.

Before:

```csharp
public record Person : Node
{
    // In 0.x this was inherited and treated as universal graph identity.
}
```

After, choose deliberately between keyless and domain-keyed models:

```csharp
[Node(Label = "AuditEntry")]
public record AuditEntry : Node
{
    public DateTime RecordedAt { get; init; }
    public string Message { get; init; } = string.Empty;
}

[Node(Label = "Customer")]
public record Customer : Node
{
    [Property(IsKey = true)]
    public string Tenant { get; init; } = string.Empty;

    [Property(IsKey = true)]
    public string CustomerNumber { get; init; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
}
```

Multiple `IsKey` properties form one composite tuple. Keys are optional domain constraints, not
provider-native identity, implicit mutation targets, or endpoints. Key components are non-null
graph-storable scalars and imply required/indexed behavior.

If the domain genuinely has a property named `Id`, keep it:

```csharp
public record ExternalRecord : Node
{
    public string Id { get; init; } = string.Empty; // ordinary mapped data
}
```

Add `IsKey = true` only if that property is the domain key. Its name alone has no meaning.

## 2. Remove relationship-owned endpoints and direction

`IRelationship` now contains only runtime `Type`. The `Relationship` base record has no endpoint
IDs and no direction:

```csharp
[Relationship(Label = "KNOWS")]
public record Knows : Relationship
{
    public DateTime Since { get; init; }
}
```

Do not copy `StartNodeId`, `EndNodeId`, or provider element IDs into the new relationship type.
Endpoint intent belongs to the create command. Physical orientation is returned on
`IGraphPathSegment.Direction`, relative to the segment's `StartNode` and `EndNode`.

A domain property named `Direction` remains legal ordinary data:

```csharp
public record ShippingInstruction : Relationship
{
    public string Direction { get; init; } = string.Empty;
}
```

It does not control edge orientation.

## 3. Replace create calls with endpoint intent

Create a standalone node with `CreateNodeAsync`:

```csharp
await graph.CreateNodeAsync(new Person { Email = "alice@example.com" });
```

Create two new endpoints and the relationship atomically:

```csharp
await graph.CreateAsync(
    new Person { Email = "alice@example.com" },
    new Knows { Since = DateTime.UtcNow },
    new Person { Email = "bob@example.com" });
```

Connect existing endpoints with exactly-one selections:

```csharp
var alice = graph.Nodes<Person>()
    .Where(person => person.Email == "alice@example.com");
var bob = graph.Nodes<Person>()
    .Where(person => person.Email == "bob@example.com");

await graph.CreateRelationshipAsync(alice, new Knows(), bob);
```

Both selections are frozen in the write transaction and must resolve to exactly one node. A zero
or multi-row selection fails. This explicit contract replaces hidden ID lookup.

Hybrid and self-loop shapes are public:

```csharp
// Existing source, new target.
await graph.CreateAsync(
    alice,
    new Knows(),
    new Person { Email = "charlie@example.com" });

// New source, existing target.
await graph.CreateAsync(
    new Person { Email = "dana@example.com" },
    new Knows(),
    bob);

// One new node used as both endpoints.
await graph.CreateSelfLoopAsync(
    new Person { Email = "self@example.com" },
    new Knows());
```

Pass `RelationshipDirection.Incoming` to a create overload only when the physical edge must be
stored opposite the source/target command intent. Most models use the default `Outgoing`.

## 4. Replace detached-entity mutation with set-based mutation

Whole-entity update/delete APIs have been removed. Select the target rows, then apply typed
setters or delete the selection:

```csharp
var selected = graph.Nodes<Person>()
    .Where(person => person.Email == "alice@example.com");

var affected = await selected.UpdateAsync(setters => setters
    .SetProperty(person => person.Age, person => person.Age + 1)
    .SetProperty(person => person.Profile, new Profile { DisplayName = "Alice" }));

var deleted = await graph.Nodes<Person>()
    .Where(person => person.Email.EndsWith("@expired.example"))
    .DeleteAsync(cascadeDelete: true);
```

`SetProperty` covers scalar, constrained, collection, and complex properties. Key/unique/required
validation and complex-subtree replacement happen in the write transaction. Node deletion refuses
user-defined relationships unless `cascadeDelete: true`; relationship selections use their own
`DeleteAsync()` overload.

The selected set is frozen and de-duplicated before mutation, so a traversal that reaches one
entity more than once still mutates it once.

## 5. Use synchronous query roots

Building a query performs no I/O. Query roots therefore no longer have `Async` suffixes and are not
awaited:

| 0.x | v1 |
| --- | --- |
| `await graph.NodesAsync<Person>()` | `graph.Nodes<Person>()` |
| `await graph.RelationshipsAsync<Knows>()` | `graph.Relationships<Knows>()` |
| `await graph.DynamicNodesAsync()` | `graph.DynamicNodes()` |
| `await graph.DynamicRelationshipsAsync()` | `graph.DynamicRelationships()` |
| `await graph.SearchNodesAsync<Person>(text)` | `graph.SearchNodes<Person>(text)` |
| `await graph.SearchRelationshipsAsync<Knows>(text)` | `graph.SearchRelationships<Knows>(text)` |

Execute I/O with an async terminal or `await foreach`:

```csharp
var people = await graph.Nodes<Person>()
    .Where(person => person.Active)
    .ToListAsync(cancellationToken);

await foreach (var person in graph.Nodes<Person>()
    .WithCancellation(cancellationToken))
{
    // Incremental provider streaming.
}
```

`IGraphQueryable<T>` is the one queryable type for nodes, relationships, projections, and scalars.
The old per-kind queryable interfaces are gone.

## 6. Update async terminal expectations

The terminal names and empty-sequence behavior follow standard LINQ:

- `FirstAsync`, `LastAsync`, and `SingleAsync` throw `InvalidOperationException` on an empty
  sequence.
- `FirstOrDefaultAsync`, `LastOrDefaultAsync`, and `SingleOrDefaultAsync` return default on empty.
- `SingleAsync` and `SingleOrDefaultAsync` throw when more than one row matches.
- `AverageAsync` returns the standard LINQ numeric result type; nullable empty/all-null inputs
  return null, while non-nullable empty inputs throw.
- Every async terminal accepts `CancellationToken`.

Replace code that relied on `FirstAsync` or `SingleAsync` returning null with the corresponding
`OrDefault` form.

## 7. Update traversal calls

Depth and direction are options on the traversal itself; free-floating `WithDepth`/`Direction`
modifiers are gone:

```csharp
var friends = await graph.Nodes<Person>()
    .Where(person => person.Email == "alice@example.com")
    .Traverse<Knows, Person>(options => options
        .Depth(1, 3)
        .Direction(GraphTraversalDirection.Both))
    .ToListAsync();
```

`Traverse<TRelationship, TEnd>` and `TraversePaths<TRelationship, TEnd>` use two generic type
arguments. `PathSegments<TStart, TRelationship, TEnd>` retains all three because its result exposes
the typed start node.

Use path segments to inspect a relationship's endpoints and orientation:

```csharp
var segments = await graph.Nodes<Person>()
    .Where(person => person.Email == "alice@example.com")
    .PathSegments<Person, Knows, Person>(GraphTraversalDirection.Both)
    .ToListAsync();

foreach (var segment in segments)
{
    var physicalDirection = segment.Direction;
    var sourceInThisResult = segment.StartNode;
    var targetInThisResult = segment.EndNode;
}
```

There is no `RelationshipDirection.Bidirectional` stored shape. Query both directions with
`GraphTraversalDirection.Both`.

## 8. Remove hidden reference and whole-entity assumptions

There is no public `GraphReference`, hidden identity convention, or portable whole-entity ordering.
Project or compare domain scalar properties:

```csharp
var ordered = await graph.Nodes<Person>()
    .OrderBy(person => person.Email)
    .ThenBy(person => person.Name)
    .ToListAsync();
```

Neo4j declares `GraphCapability.OrderByEntity`; AGE and in-memory do not. Scalar ordering is the
portable contract.

Do not retain a provider element ID beyond the operation that produced it. Provider-native
identity is private and may be database-, graph-, transaction-, or provider-specific.

## 9. Adopt native storage and external-data behavior

Neo4j and AGE v1 write normal mapped labels/types and ordinary user properties. They do not require
universal CVOYA node/relationship roots. Compatible native rows created outside CVOYA Graph can be
read as typed or dynamic entities and can participate in traversal and full-text queries.

Read-only queries do not provision labels, types, indexes, functions, or missing graphs. Write paths
provision only what the operation needs. Managed-index recreation is ownership-bounded: Neo4j
touches only positively identified provider-owned indexes, while AGE and in-memory complete as
successful no-ops.

This is why alpha databases must be recreated/reimported rather than opened in place.

## 10. Preserve nullable collection semantics

V1 preserves nullable elements in simple and complex collections:

```csharp
public record ImportBatch : Node
{
    public List<string?> Codes { get; set; } = [];
    public List<Address?> Stops { get; set; } = [];
}
```

Create, read, replacement, typed/dynamic materialization, and supported predicates preserve exact
count, order, null positions, and declared element type, including empty and all-null collections.
A null targeting a non-nullable element fails. Provider physical encodings are private.

Do not retain a 0.x workaround that removed nulls, inserted sentinels, or exposed companion
properties.

## 11. Account for provider capabilities

The portable contract is explicit:

| Surface | Neo4j | AGE | In-memory |
| --- | --- | --- | --- |
| Full-text search | Yes | Yes | Yes, index-free contract floor |
| Relationship predicates | Yes | Yes | Yes |
| `Union` / `Concat` | Yes | Yes | Yes |
| Nullable simple collection elements | Yes | Yes | Yes |
| Shortest path | Yes | Yes on AGE 1.8+; skipped on 1.7 | Yes |
| Whole-entity ordering | Yes | No | No |

AGE declares `ShortestPath` only on Apache AGE 1.8.0 or later, where the native
`ag_catalog.age_shortest_path` / `ag_catalog.age_all_shortest_paths` functions are available. On AGE
1.7 the capability is absent, the capability-gated compatibility test skips with the standard
missing-capability reason, and translation rejects the operators. `AgeGraphStoreOptions` can force
the feature `Enabled` or `Disabled` per store. AGE `RelationshipPredicates` and `SetOperations` are
supported; do not carry forward old workarounds or documentation that says otherwise.

## Suggested transition order

1. Back up the alpha database and preserve the original application/export toolchain.
2. Update entity base types and remove inherited universal-ID assumptions.
3. Decide which entities are keyless and which need explicit single/composite domain keys.
4. Remove relationship endpoint/direction members that existed only for persistence.
5. Replace create, update, and delete calls with endpoint-intent and set-based APIs.
6. Update synchronous roots, traversal calls, and async terminal expectations.
7. Remove legacy null-collection workarounds and provider identity/reference assumptions.
8. Build the application and examples against v1.
9. Create an empty v1 graph and reimport from a reviewed source.
10. Validate counts, domain-key uniqueness, relationships, orientation, complex properties,
    nullable collections, and provider capabilities before cutover.

The library will not mutate the old database during these steps.
