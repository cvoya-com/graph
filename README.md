<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/cvoya-graph-mark-dark.svg">
  <img src="assets/cvoya-graph-mark.svg" alt="" width="72" height="72">
</picture>

# CVOYA Graph

**Downloadable open-source computer software from [CVOYA](https://cvoya.com).** See the
[CVOYA software catalog](https://cvoya.com/software).

**[Download the Latest tagged CVOYA Graph source asset (.zip)](https://github.com/cvoya-com/graph/releases/latest/download/cvoya-graph-source.zip)**

The stable URL above follows whichever tagged GitHub Release is marked Latest. For an immutable
download, use the asset on a specific [release](https://github.com/cvoya-com/graph/releases);
install supported binaries from [NuGet](https://www.nuget.org/profiles/cvoya), or browse the
repository when you want the current development branch.

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)
[![.NET](https://img.shields.io/badge/.NET-10.0+-purple.svg)](https://dotnet.microsoft.com/download)
[![GitHub release](https://img.shields.io/github/v/release/cvoya-com/graph)](https://github.com/cvoya-com/graph/releases)
[![CI](https://github.com/cvoya-com/graph/actions/workflows/ci.yml/badge.svg)](https://github.com/cvoya-com/graph/actions/workflows/ci.yml)
[![Documentation](https://github.com/cvoya-com/graph/actions/workflows/docs.yml/badge.svg)](https://github.com/cvoya-com/graph/actions/workflows/docs.yml)
[![Codecov](https://codecov.io/gh/cvoya-com/graph/branch/main/graph/badge.svg)](https://codecov.io/gh/cvoya-com/graph)
[![CodeQL](https://github.com/cvoya-com/graph/actions/workflows/codeql.yml/badge.svg)](https://github.com/cvoya-com/graph/actions/workflows/codeql.yml)

CVOYA Graph is a type-safe .NET graph library with one provider-neutral model for LINQ queries,
transactions, set-based mutations, relationship traversal, serialization, and provider
certification. The repository includes Neo4j, PostgreSQL + Apache AGE, and in-memory providers.

## Model and API

- `IEntity` is an identity-free marker. `INode` adds runtime `Labels`;
  `IRelationship` adds runtime `Type`.
- `Node` and `Relationship` are convenient base records. They do not add IDs, endpoints, or
  direction.
- Domain keys are optional and explicit with `[Property(IsKey = true)]`. Multiple key properties
  form one composite key; keyless models are valid.
- Properties named `Id` or `Direction` are ordinary user properties unless you explicitly
  annotate them.
- Providers keep physical node/edge identity private and use it only to correlate database work.
- Relationships are connected through endpoint intent passed to the create API, not through
  relationship-owned endpoint IDs.
- Queries use `IGraphQueryable<T>` and async terminals. Updates and deletes operate over a frozen,
  distinct query selection.

<!-- checked-snippet: examples/Playground/Documentation/Person.cs#graph-using; examples/Playground/Documentation/Person.cs#root-model-person; examples/Playground/Documentation/Address.cs#root-model-address; examples/Playground/Documentation/AuditEntry.cs#root-model-audit-entry; examples/Playground/Documentation/Knows.cs#root-model-knows -->
```csharp
using Cvoya.Graph;

[Node(Label = "Person")]
public record Person : Node
{
    [Property(IsKey = true)]
    public string Tenant { get; init; } = string.Empty;

    [Property(IsKey = true)]
    public string Email { get; init; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public List<string?> Aliases { get; set; } = [];
    public List<Address?> PreviousAddresses { get; set; } = [];
}

public record Address
{
    public string City { get; init; } = string.Empty;
}

[Node(Label = "AuditEntry")]
public record AuditEntry : Node
{
    public string Message { get; set; } = string.Empty;
}

[Relationship(Label = "KNOWS")]
public record Knows : Relationship
{
    public DateTime Since { get; init; }
}
```

Remove both `IsKey` declarations and `Person` becomes keyless. A key is domain schema metadata,
not provider identity or an implicit mutation target.

## Packages

CVOYA Graph runtime packages include assets for .NET 10 and .NET 11.

The inventory below covers every packable project under `src/`. Package validation derives the
expected set from those projects with [`scripts/verify-package-set.sh`](scripts/verify-package-set.sh)
instead of maintaining a second fixed inventory.

| Package | Purpose |
| --- | --- |
| [`Cvoya.Graph`](https://www.nuget.org/packages/Cvoya.Graph/) | Core model, query, transaction, mutation, and provider contracts |
| [`Cvoya.Graph.Neo4j`](https://www.nuget.org/packages/Cvoya.Graph.Neo4j/) | Neo4j provider |
| [`Cvoya.Graph.Age`](https://www.nuget.org/packages/Cvoya.Graph.Age/) | PostgreSQL + Apache AGE provider |
| [`Cvoya.Graph.InMemory`](https://www.nuget.org/packages/Cvoya.Graph.InMemory/) | In-process reference provider and test double |
| [`Cvoya.Graph.Cypher`](https://www.nuget.org/packages/Cvoya.Graph.Cypher/) | Shared typed Cypher AST, planning, validation, and rendering SPI |
| [`Cvoya.Graph.Serialization`](https://www.nuget.org/packages/Cvoya.Graph.Serialization/) | Provider-neutral serialization and result materialization |
| [`Cvoya.Graph.Serialization.CodeGen`](https://www.nuget.org/packages/Cvoya.Graph.Serialization.CodeGen/) | Incremental entity serializer generator |
| [`Cvoya.Graph.Analyzers`](https://www.nuget.org/packages/Cvoya.Graph.Analyzers/) | Optional compile-time model diagnostics |
| [`Cvoya.Graph.CompatibilityTests`](https://www.nuget.org/packages/Cvoya.Graph.CompatibilityTests/) | Provider compatibility suite (TCK) |

Install the provider for your database; it brings the required core and serialization packages:

```bash
dotnet add package Cvoya.Graph.Neo4j
dotnet add package Cvoya.Graph.Analyzers
```

## Quick start

<!-- checked-snippet: examples/Playground/Documentation/UsingDirectives.cs#neo4j-usings; examples/Playground/DocumentationSnippets.cs#root-quick-start -->
```csharp
using Cvoya.Graph;
using Cvoya.Graph.Neo4j;

await using var store = new Neo4jGraphStore(
    "bolt://localhost:7687",
    "neo4j",
    "password",
    databaseName: "myapp");
var graph = store.Graph;

var alice = new Person
{
    Tenant = "northwest",
    Email = "alice@example.com",
    Name = "Alice",
    Age = 30,
};
var bob = new Person
{
    Tenant = "northwest",
    Email = "bob@example.com",
    Name = "Bob",
    Age = 28,
};

await graph.CreateAsync(
    alice,
    new Knows { Since = DateTime.UtcNow },
    bob);

var aliceSelection = graph.Nodes<Person>()
    .Where(person => person.Tenant == "northwest" &&
                     person.Email == "alice@example.com");

await aliceSelection.UpdateAsync(setters => setters
    .SetProperty(person => person.Age, person => person.Age + 1)
    .SetProperty(person => person.Name, "Alice Smith"));

var connections = await aliceSelection
    .PathSegments<Person, Knows, Person>()
    .ToListAsync();

foreach (var segment in connections)
{
    Console.WriteLine(
        $"{segment.StartNode.Name} --{segment.Relationship.Type}--> " +
        $"{segment.EndNode.Name} ({segment.Direction})");
}
```

`IGraphPathSegment.Direction` reports the physical edge orientation relative to the returned
`StartNode` and `EndNode`. The `Knows` object itself has no endpoint or direction fields.

### Relationship creation

The API supports every selected/new endpoint combination:

<!-- checked-snippet: examples/Playground/DocumentationSnippets.cs#relationship-creation -->
```csharp
var alice = graph.Nodes<Person>()
    .Where(person => person.Email == "alice@example.com");
var bob = graph.Nodes<Person>()
    .Where(person => person.Email == "bob@example.com");

// Two existing endpoints. Each query must select exactly one node.
await graph.CreateRelationshipAsync(alice, new Knows(), bob);

// Existing source and new target.
await graph.CreateAsync(alice, new Knows(), new Person
{
    Tenant = "northwest",
    Email = "charlie@example.com",
    Name = "Charlie",
});

// New source and existing target.
await graph.CreateAsync(new Person
{
    Tenant = "northwest",
    Email = "dana@example.com",
    Name = "Dana",
}, new Knows(), bob);

// One new keyless or keyed node connected to itself.
await graph.CreateSelfLoopAsync(
    new AuditEntry { Message = "Keyless self-loop" },
    new Knows());
```

`IGraph.CreateAsync(source, relationship, target)` creates two new endpoints and the edge
atomically. Selected endpoints use exact-one semantics: zero or multiple rows fail instead of
guessing an endpoint.

### Set-based mutation

<!-- checked-snippet: examples/Playground/DocumentationSnippets.cs#set-based-mutation -->
```csharp
var adults = graph.Nodes<Person>().Where(person => person.Age >= 18);

var updated = await adults.UpdateAsync(setters => setters
    .SetProperty(person => person.Name, person => person.Name)
    .SetProperty(person => person.PreviousAddresses, new List<Address?>()));

var deleted = await graph.Nodes<Person>()
    .Where(person => person.Email.EndsWith("@expired.example"))
    .DeleteAsync(cascadeDelete: true);
```

Updates accept typed scalar, constrained, collection, and complex-property selectors. Providers
validate key/unique/required constraints and replace complex-property subtrees atomically.
Relationship queries have their own `DeleteAsync()` overload. There is no whole-entity update API.

## Provider behavior

Neo4j and AGE store ordinary typed labels, relationship types, and user properties in native graph
storage. Typed, dynamic, traversal, and full-text queries can read compatible rows created by a
native database client without CVOYA metadata. Reads do not provision labels, types, indexes,
functions, or graphs; write paths provision only what they require.

| Surface | Neo4j | Apache AGE | In-memory |
| --- | --- | --- | --- |
| Full-text contract floor | Managed native full-text indexes | PostgreSQL text search over native AGE rows | Index-free whole-token scan |
| Relationship predicates | Yes | Yes, provider-local lowering | Yes |
| `Union` / `Concat` | Yes | Yes, provider-local lowering | Yes |
| Shortest path | Yes | **No in v1.0**; capability-gated TCK cases skip | Yes |
| Whole-entity ordering | Yes | No | No |
| Managed index recreation | Owned range/full-text indexes only | Successful no-op | Successful no-op |

All three providers preserve nullable simple and complex collection elements, exact order, and null
positions. Physical companion properties used by database providers are private implementation
details and never appear in typed or dynamic property bags.

## Pre-v1 data boundary

The v1 model is deliberately incompatible with alpha-era universal IDs, relationship endpoint IDs,
relationship-owned direction, universal root labels/types, and legacy provider metadata.

There is no compatibility reader, mixed legacy/native mode, dual write, automatic backfill, or
supported migration script. Back up existing data, update the application model, then recreate and
reimport into the final native representation or perform your own reviewed transformation. CVOYA
Graph will not delete or rewrite an old database automatically. See the
[v1 migration guide](docs/migration-0.x.md).

## Documentation

- [Core concepts](docs/core-concepts.md)
- [LINQ querying](docs/querying.md)
- [Transactions](docs/transactions.md)
- [Attributes and configuration](docs/attributes.md)
- [Runtime metadata](docs/runtime-metadata.md)
- [Testing with the in-memory provider](docs/testing-with-the-in-memory-provider.md)
- [Provider implementers guide](docs/provider-implementers-guide.md)
- [Neo4j provider](src/Graph.Neo4j/README.md)
- [Apache AGE provider](src/Graph.Age/README.md)
- [Migration from 0.x](docs/migration-0.x.md)
- [API reference](https://oss.cvoya.com/graph/api/)

Compiling examples live under [`examples/`](examples/).

## Build and test

Building the repository requires the .NET 11 SDK. Tests run on both .NET 10 and .NET 11, so install
the .NET 10 runtime as well. Provider-backed tests require their corresponding service.

```bash
dotnet build --configuration Debug

# Service-free projects plus the in-memory provider contract suite
./scripts/run-tests.sh --configuration Debug --lane fast --disable-diff-engine

# Start each provider service and run the full discovered suite
./scripts/run-tests.sh --configuration Debug --lane all --neo4j --age --disable-diff-engine

# Validate the derived package set and package-reference build
dotnet msbuild eng/PackageValidation.proj -target:Validate
```

See [CONTRIBUTING.md](CONTRIBUTING.md), [tests/README.md](tests/README.md), and the
[build-system guide](docs/graph-model-developers.md) for the lane and service details.

## Contributors

CVOYA Graph thanks [Paul Jeschke (`@paule96`)](https://github.com/paule96) for contributing the
original PostgreSQL + Apache AGE provider in
[pull request #66](https://github.com/cvoya-com/graph/pull/66). His work helped shape the
provider-neutral architecture and the AGE provider included today.

## License

Licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE).
