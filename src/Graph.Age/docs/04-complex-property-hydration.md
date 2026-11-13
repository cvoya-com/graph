# Complex property hydration

Complex properties are nested CLR objects stored as first-class graph structure. When a query
materializes the owner, the AGE provider hydrates the declared complex value and its nested values
automatically, bounded by the declared shape rather than by the global safety ceiling.

## Example

```csharp
using Cvoya.Graph;
using Cvoya.Graph.Age;

public sealed class Address
{
    public string City { get; set; } = string.Empty;
    public string Street { get; set; } = string.Empty;
}

[Node("Person")]
public sealed record Person : Node
{
    public string Name { get; set; } = string.Empty;

    [ComplexProperty(RelationshipType = "LIVES_AT")]
    public Address? Home { get; set; }

    public List<Address>? FormerHomes { get; set; }
}

await using var store = new AgeGraphStore(connectionString, "people");
await store.CreateGraphIfNotExistsAsync(cancellationToken);

await store.Graph.CreateNodeAsync(
    new Person
    {
        Name = "Ada",
        Home = new Address { City = "Berlin", Street = "Hauptstrasse 1" },
        FormerHomes =
        [
            new Address { City = "Leipzig", Street = "Ring 2" },
            null,
            new Address { City = "Dresden", Street = "Elb 3" },
        ],
    },
    cancellationToken: cancellationToken);

// Materializing Person hydrates Home and the ordered FormerHomes collection.
var person = await store.Graph.Nodes<Person>()
    .Where(person => person.Name == "Ada")
    .SingleAsync(cancellationToken);
```

Complex properties need no index declaration. They are part of the entity schema and are handled by
the provider for every read that materializes the owner.

## What changes in the database

The provider stores each complex value as graph structure, not as a property bag on the owner:

```text
(:Person {properties})-[:LIVES_AT]->(:Address {properties})
(:Person)-[:FormerHomes {SequenceNumber: 0}]->(:Address)
(:Person)-[:FormerHomes {SequenceNumber: 2}]->(:Address)
```

The relationship type defaults to the CLR property name and can be overridden with
`[ComplexProperty(RelationshipType = "...")]`.

Collections additionally store four private owner companions keyed by the Base64Url-encoded
physical property label:

- logical length (`__cvoya_sc:v1:c:l:<name>`);
- null indexes (`__cvoya_sc:v1:c:n:<name>`);
- declared element type identity (`__cvoya_sc:v1:c:t:<name>`);
- semantic relationship type (`__cvoya_sc:v1:c:r:<name>`).

This preserves empty collections, null slots, ordering, and the declared element type without
creating fake child nodes. Every occurrence is a separate value node, even when two owners reference
the same in-memory object.

Writes create or replace the value subgraph in the owner's transaction. Replacing a collection
clears the old companions and value relationships and writes the new set atomically. Deleting an
owner cascades to its owned value nodes. The provider never infers complex-property edges from a
reserved relationship-name prefix; it uses the declared property mapping.

## Scale-out and multiple clients

Complex-property hydration is read-time behavior; it creates no declarations, DDL, or advisory
locks.

- Several clients may read and write the same graph concurrently. Owner writes use the ordinary
  transaction and uniqueness rules; one owner's value subgraph is not shared with another.
- A reader in another process sees committed value nodes and relationships immediately. No
  maintenance call is required.
- Hydration depth depends on the projected CLR type and the derived types loaded in the process. A
  process that has loaded a plugin-derived type may hydrate a deeper derived-only value than a
  process that has not. Both are correct for the data those processes can materialize.
- If a process loads a collectible plugin assembly, the provider measures the depth against a
  transient scan of loaded assemblies rather than a cached shared type index, so the plugin's types
  are observed without rooting the collectible load context. See
  [collectible plugin types](06-collectible-plugin-types.md).
- Operators must not delete value nodes or companion properties manually while owners are live.
  Delete through the provider so the whole owned subgraph is removed.

## Accelerated query shapes

Hydration is not a query accelerator in the declared-index sense; it is the correct materialization
of a declared shape.

Fully hydrated:

- materializing the owner entity (`Nodes<Person>().Where(...).ToListAsync()`);
- projecting the complex property itself (`Select(person => person.Home)`);
- typed and dynamic projections that resolve the stored runtime type;
- collections with declared null slots and order.

Deliberately avoided:

- projecting only scalar properties (`Select(person => person.Name)`) does not load value nodes;
- a read type that omits the property leaves it unpopulated;
- an unresolvable, interface-typed, or dynamic shape falls back to the global depth bound
  `GraphDataModel.DefaultDepthAllowed` (currently 5) as a safety ceiling, never unbounded.

Cycle detection runs on write and rejects a reference cycle. A path deeper than the bound is
rejected with a `GraphException` rather than silently truncated.

## Fallback and idempotency

- **No declaration:** complex properties work without any index/aggregate declaration; they are
  schema behavior, not an optional feature.
- **Repeated reads:** idempotent; nothing is cached in the database and no maintenance is involved.
- **Repeated writes:** a replacement clears and rewrites the owned subgraph atomically; re-running
  the same write with the same value yields the same graph.
- **Partial/corrupt storage:** a duplicate, out-of-range, or missing collection slot, a partial
  companion set, or an incompatible element type fails with `GraphException` instead of returning a
  silently truncated value.
- **Unresolvable shapes:** fall back to the global depth bound; do not assume infinite auto-load.

## Breaking change and cleanup

The `Status`/`Kaufpreis` physical-name removal does not change complex-property storage names:
complex properties never used those compatibility names. The general breaking change is documented
in [declared equality indexes](01-declared-equality-indexes.md#breaking-change-and-cleanup) and
applies to declared index/aggregate objects only.

For preview cleanup of complex-property data, remove owners through the provider so cascade deletion
runs. If a direct SQL/Cypher cleanup is unavoidable, remove the owned value nodes and their
relationships together with the four companion properties; there is no runtime fallback that
reconstructs them from partial data.

## Risks and verification checklist

Risks:

- each value occurrence adds a node and at least one relationship; wide or deeply nested shapes
  multiply storage and write cost;
- auto-loading a deep shape on a large page is expensive; project only the needed properties;
- non-sealed property types may include derived-only nested values, increasing the bound;
- manual deletes can orphan value nodes and companions;
- the depth bound (5) rejects deeper shapes rather than storing them.

Verify:

```sql
-- Value nodes and relationships for one owner.
MATCH (p:Person {Name: 'Ada'})-[:LIVES_AT]->(a:Address)
RETURN a.City, a.Street;

MATCH (p:Person {Name: 'Ada'})-[r:FormerHomes]->(a:Address)
RETURN r.SequenceNumber, a.City ORDER BY r.SequenceNumber;
```

Then read the owner through the typed API and assert the same city/street values, the collection
length, and the null slot position. Delete the owner through the provider and confirm the value
nodes and relationships are gone. Compare a scalar-only projection before and after deletion to
confirm it never depended on the value subgraph.
