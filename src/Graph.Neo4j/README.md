**Downloadable open-source computer software from [CVOYA](https://cvoya.com).** See the
[CVOYA software catalog](https://cvoya.com/software).

# Cvoya.Graph.Neo4j

[![NuGet](https://img.shields.io/nuget/v/Cvoya.Graph.Neo4j.svg)](https://www.nuget.org/packages/Cvoya.Graph.Neo4j/)
[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)

**Neo4j provider** for CVOYA graph - enables your applications to work with Neo4j graph databases using the CVOYA graph abstractions and LINQ-style querying.

## 🚀 Quick Start

```bash
dotnet add package Cvoya.Graph.Neo4j
```

<!-- checked-snippet: examples/Playground/Documentation/UsingDirectives.cs#neo4j-usings; examples/Playground/DocumentationSnippets.cs#neo4j-quick-start -->
```csharp
using Cvoya.Graph;
using Cvoya.Graph.Neo4j;

// Configure connection
await using var store =
    new Neo4jGraphStore("neo4j+s://your-server:7687", "neo4j", "your-password");
var graph = store.Graph;

// Use CVOYA graph APIs
var users = await graph.Nodes<User>()
    .Where(u => u.IsActive)
    .OrderBy(u => u.CreatedDate)
    .ToListAsync();
```

## 📦 Core Features

- **Full CVOYA graph compatibility** - Works seamlessly with all CVOYA graph abstractions
- **Cypher query generation** - Automatic translation from LINQ to optimized Cypher
- **Connection pooling** - Built-in connection management and pooling
- **Transaction support** - Full ACID transactions with Neo4j
- **Index management** - Automatic index creation from entity attributes
- **Complex type handling** - Serialization of complex properties and collections

Neo4j owns physical node and relationship identity. CVOYA Graph does not expose it through
`IEntity`; use optional `[Property(IsKey = true)]` domain keys or ordinary domain predicates when
an operation needs to select an entity. Keyless entities remain valid.

Ordinary read-only query execution does not initialize schema or create indexes. Write paths
initialize the schema artifacts required by the registered model.

The Neo4j provider stores `decimal` properties as double-precision values. Reading them back as
`decimal` rounds to 15 significant digits on both .NET 10 and .NET 11.

## 🏗️ Architecture

This provider implements:

- **`IGraph`** - Main graph operations against Neo4j
- **`IGraphQueryProvider`** - LINQ-to-Cypher query translation
- **`IGraphTransaction`** - Neo4j transaction wrapper
- **Connection management** - Efficient Neo4j driver usage

## 🔧 Configuration

```csharp
var store = new Neo4jGraphStore("neo4j+s://localhost:7687", "neo4j", "password");
var graph = store.Graph;
```

### Concurrent schema initialization

Independent `Neo4jGraphStore` instances may safely initialize the same database at the same time,
including configured constraints, range indexes, and the provider's general full-text indexes.
Retryable Neo4j schema failures are replayed through driver-managed transactions. If concurrent
creation reports a non-retryable schema conflict, the provider treats it as success only when the
installed object's name, entity type, labels or relationship types, properties, and schema kind are
equivalent to the requested definition. An incompatible existing object still fails with the
original Neo4j error.

The provider-owned general full-text indexes (`node_fulltext_index` and `rel_fulltext_index`) are
the exception: their exact names and matching node/relationship full-text kinds are reserved as
the ownership marker. Their definition is derived from the whole registered model, so it
legitimately changes as the model evolves. When their installed definition no longer matches the
current model, the provider drops and recreates them during initialization instead of failing.

`RecreateManagedIndexesAsync` rebuilds only positively owned indexes. A configured range index is
owned only when its deterministic name, kind, entity type, label/type, and properties all match the
current registered model. A stale range index that is no longer described by that model is
preserved because its ownership can no longer be proved. The two exact reserved full-text indexes
remain positively owned when their model-derived definition is stale; they are rebuilt when still
configured and removed when the current model no longer requires them. External indexes,
same-named incompatible range indexes, and every uniqueness-backed index are preserved.

Calls on one graph instance are serialized. Independent equivalent callers converge on the same
installed definitions through metadata-checked conflict handling. Callers using different models
against the same database must coordinate their maintenance operations. Successful completion
means every configured managed index is online and usable. Cancellation or failure can leave only
a positively owned index absent or not yet online; retrying the operation restores the configured
managed set without expanding the ownership boundary.

## 📚 Documentation

For comprehensive documentation, examples, and best practices:

**🌐 [Complete Documentation](https://github.com/cvoya-com/graph/)**

## 🔗 Related Packages

- **[Cvoya.Graph.Neo4j](https://www.nuget.org/packages/Cvoya.Graph.Neo4j/)** - Neo4j database provider
- **[Cvoya.Graph.Serialization](https://www.nuget.org/packages/Cvoya.Graph.Serialization/)** - Object serialization framework
- **[Cvoya.Graph.Serialization.CodeGen](https://www.nuget.org/packages/Cvoya.Graph.Serialization.CodeGen/)** - Code generation for performant serialization/deserialization
- **[Cvoya.Graph.Analyzers](https://www.nuget.org/packages/Cvoya.Graph.Analyzers/)** - Compile-time code analyzers

## 🤝 Contributing

Contributions are welcome! Please see our [Contributing Guide](https://github.com/cvoya-com/graph/blob/main/CONTRIBUTING.md).

## 📄 License

This project is licensed under the Apache License 2.0 - see the [LICENSE](https://github.com/cvoya-com/graph/blob/main/LICENSE) file for details.

---

**Need help?** Check the [troubleshooting guide](https://github.com/cvoya-com/graph/blob/main/docs/troubleshooting.md) or [open an issue](https://github.com/cvoya-com/graph/issues).
