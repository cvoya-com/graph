---
---

# Core concepts

CVOYA Graph separates a portable domain model from provider-private storage identity. Applications
work with typed nodes, relationships, query selections, and path segments; providers decide how to
identify and correlate physical rows inside a database transaction.

## Install a provider

Install the provider for the backing store. The provider brings the core and serialization
dependencies:

```bash
dotnet add package Cvoya.Graph.Neo4j
dotnet add package Cvoya.Graph.Analyzers
```

Other in-tree providers are `Cvoya.Graph.Age` and `Cvoya.Graph.InMemory`.

## Identity-free entities

`IEntity` is an empty marker shared by nodes and relationships:

```csharp
public interface IEntity { }

public interface INode : IEntity
{
    IReadOnlyList<string> Labels { get; }
}

public interface IRelationship : IEntity
{
    string Type { get; }
}
```

There is no universal public ID. `Node` and `Relationship` provide the runtime metadata members,
but they do not add identity, relationship endpoints, or relationship direction:

```csharp
[Node(Label = "Person")]
public record Person : Node
{
    public string Name { get; set; } = string.Empty;
}

[Relationship(Label = "KNOWS")]
public record Knows : Relationship
{
    public DateTime Since { get; set; }
}
```

Properties named `Id`, `Direction`, `StartNodeId`, or any similar name are legal ordinary
properties. Their names have no convention-based meaning.

Providers may use Neo4j element IDs, AGE graphids, or in-memory handles internally. Those values
are private, non-portable, and never enter a typed entity or dynamic property bag.

## Optional domain keys

Add `[Property(IsKey = true)]` only when the domain has a stable lookup tuple:

```csharp
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

All key properties on one entity form one composite key ordered by mapped property name. A key:

- is unique within the mapped label or relationship type;
- must contain non-null, graph-storable scalar components;
- implies required and indexed behavior;
- is not provider-native identity;
- is not an implicit mutation or endpoint selector; and
- is not automatically immutable.

A model with no `IsKey` members is valid. Create and query it through ordinary properties:

```csharp
[Node(Label = "AuditEntry")]
public record AuditEntry : Node
{
    public DateTime RecordedAt { get; init; }
    public string Message { get; init; } = string.Empty;
}

await graph.CreateNodeAsync(new AuditEntry
{
    RecordedAt = DateTime.UtcNow,
    Message = "Imported",
});

var imported = await graph.Nodes<AuditEntry>()
    .Where(entry => entry.Message == "Imported")
    .ToListAsync();
```

See [Attributes and configuration](attributes.md#optional-domain-keys) for the complete constraint
rules.

## Labels and relationship types

`[Node]` and `[Relationship]` map CLR types to native graph names. Without an attribute, the CLR
type name is used. Typed entities have one primary mapped label/type; dynamic nodes may expose
multiple stored labels.

`INode.Labels` and `IRelationship.Type` report provider-populated runtime metadata. Use them for
inspection and dynamic filtering, not as hidden identity or relationship ownership.

Polymorphic node queries expand the requested CLR hierarchy to every compatible registered mapped
label. `graph.Nodes<Asset>()` can therefore return registered `Vehicle` and `Building` subtypes
without adding a universal root label to the database.

## Properties

Unannotated public instance properties are mapped by name. `[Property]` can change the storage
label, exclude a member, declare a domain key, request an index/unique constraint, require a value,
or exclude a string from full-text search.

Supported scalar values include strings, booleans, numeric types, enums, GUIDs, URI, temporal
types, `byte[]`, and `Cvoya.Graph.Point`. Native-sized integers (`nint`, `nuint`, `IntPtr`,
`UIntPtr`) are not graph-storable.

### Simple collections

One-dimensional arrays and supported generic collection interfaces/classes round-trip as ordered
simple collections. All in-tree providers preserve:

- the declared element type, including empty and all-null collections;
- exact item order and count; and
- every null position when the element annotation permits null.

```csharp
public record Message : Node
{
    public List<string?> Tags { get; set; } = [];
    public int?[] Scores { get; set; } = [];
}
```

A null targeting a non-nullable element fails with an indexed `GraphException`. Provider companion
properties used to encode null positions are private and never appear in typed/dynamic results.

### Complex properties

A user-defined value object on a node is stored as owned graph structure:

```csharp
public record Address
{
    public string City { get; init; } = string.Empty;
}

public record Person : Node
{
    public Address? Home { get; set; }
    public List<Address?> PreviousHomes { get; set; } = [];
}
```

Conceptually, `Home` becomes `(:Person)-[:Home]->(:Address)`. A complex collection uses one
relationship for each non-null item and private metadata for its logical length, element type, and
null positions. Empty, all-null, polymorphic, and mixed null/non-null collections therefore
round-trip without collapsing positions.

Complex properties are supported on nodes; relationship properties must be simple. Replacing or
deleting an owner cleans up its owned complex subtree. `[ComplexProperty(RelationshipType = "...")]`
changes the semantic relationship type.

## Creating nodes and relationships

Create one standalone node:

```csharp
await graph.CreateNodeAsync(new Person { Name = "Alice" });
```

Create two new endpoints and their relationship atomically:

```csharp
await graph.CreateAsync(
    new Person { Name = "Alice" },
    new Knows { Since = DateTime.UtcNow },
    new Person { Name = "Bob" });
```

Connect two existing nodes by passing query selections:

```csharp
var alice = graph.Nodes<Person>().Where(person => person.Name == "Alice");
var bob = graph.Nodes<Person>().Where(person => person.Name == "Bob");

await graph.CreateRelationshipAsync(
    alice,
    new Knows { Since = DateTime.UtcNow },
    bob);
```

Each selected endpoint is frozen and must resolve to exactly one node. Zero or multiple matches
throw instead of choosing an arbitrary endpoint.

Hybrid overloads support selected source/new target and new source/selected target. Use
`CreateSelfLoopAsync` when one new node is both endpoints:

```csharp
await graph.CreateAsync(
    alice,
    new Knows(),
    new Person { Name = "Charlie" });

await graph.CreateSelfLoopAsync(
    new Person { Name = "Self" },
    new Knows());
```

The relationship object owns only relationship properties. Endpoint selection and storage
direction are command intent passed separately.

## Query roots and async terminals

`IGraph` exposes synchronous, I/O-free query roots:

```csharp
IGraphQueryable<Person> people = graph.Nodes<Person>();
IGraphQueryable<Knows> relationships = graph.Relationships<Knows>();
```

I/O happens when a terminal executes or the query is enumerated:

```csharp
var names = await people
    .Where(person => person.Name.StartsWith("A"))
    .OrderBy(person => person.Name)
    .Select(person => person.Name)
    .ToListAsync();
```

Async terminals accept cancellation tokens. `FirstAsync`, `LastAsync`, and `SingleAsync` follow
standard LINQ empty-sequence behavior: the non-defaulting forms throw, while
`FirstOrDefaultAsync`, `LastOrDefaultAsync`, and `SingleOrDefaultAsync` return default when empty.
`Single*` also throws when more than one row matches.

Do not order by a whole entity when writing portable queries. Order by one or more scalar
properties. Neo4j declares whole-entity ordering; AGE and in-memory deliberately do not.

## Set-based mutation

Updates and deletes target a query, not a detached entity:

```csharp
var selected = graph.Nodes<Person>()
    .Where(person => person.Name == "Alice");

var updated = await selected.UpdateAsync(setters => setters
    .SetProperty(person => person.Name, "Alice Smith"));

var deleted = await graph.Nodes<Person>()
    .Where(person => person.Name == "Temporary")
    .DeleteAsync(cascadeDelete: true);
```

The provider freezes and de-duplicates the selected target set inside the write transaction.
`SetProperty` is typed and accepts constants/captured values or an expression over the current
entity. Scalar, constrained, collection, and complex-property replacements share the same surface;
constraint validation and owned-subtree replacement happen atomically. Relationship queryables
support `UpdateAsync` and a relationship-specific `DeleteAsync()` overload.

## Traversal and orientation

Use `PathSegments` when you need the endpoints, relationship, and physical orientation:

```csharp
var segments = await graph.Nodes<Person>()
    .Where(person => person.Name == "Alice")
    .PathSegments<Person, Knows, Person>(
        GraphTraversalDirection.Both)
    .ToListAsync();
```

For each `IGraphPathSegment`:

- `StartNode` and `EndNode` follow the query segment;
- `Relationship` contains only its domain properties and runtime `Type`; and
- `Direction` says how the physical edge is oriented relative to those two returned nodes.

`Outgoing` means the stored edge runs `StartNode -> EndNode`; `Incoming` means it runs
`EndNode -> StartNode`. A self-loop reports `Outgoing` deterministically. Traversal direction is a
query option, not a relationship property.

Use `Traverse` when only target nodes are needed and `TraversePaths` when full multi-hop paths are
needed. Shortest-path operators require `GraphCapability.ShortestPath`: Neo4j and in-memory support
them natively, and Apache AGE supports them on AGE 1.8.0 or later through the native
`age_shortest_path` / `age_all_shortest_paths` functions. AGE 1.7 rejects them during translation.

## Transactions and resource ownership

Provider stores own database drivers, pools, and other backing resources. `IGraph` does not:

```csharp
await using var store = new InMemoryGraphStore();
var graph = store.Graph;

await using var transaction = await graph.GetTransactionAsync();
try
{
    await graph.CreateNodeAsync(
        new Person { Name = "Alice" },
        transaction);
    await transaction.CommitAsync();
}
catch
{
    await transaction.RollbackAsync();
    throw;
}
```

Queries that participate in a transaction must receive it when their root is created:
`graph.Nodes<Person>(transaction)`.

## Native storage and external data

Neo4j and AGE store normal typed labels, relationship types, and properties in native graph
storage. Compatible rows inserted by a native database client can participate in typed, dynamic,
traversal, and full-text queries without a universal CVOYA root or CLR discriminator.

Read-only operations do not provision graphs, labels/types, functions, indexes, or constraints.
Write paths provision only the artifacts they require. `RecreateManagedIndexesAsync` touches only
artifacts whose provider ownership can be proven; AGE and in-memory own none and complete as no-ops.

## Pre-v1 data boundary

Alpha-era models and databases built around universal IDs, relationship endpoint IDs,
relationship-owned direction, universal root labels/types, or legacy metadata are unsupported.
There is no compatibility reader, dual-write period, automatic migration/backfill, or automatic
database rewrite. Back up the data, update the source model, then recreate/reimport or perform your
own reviewed transformation. See [Migration from 0.x](migration-0.x.md).
