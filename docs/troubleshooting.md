---
---

# 🔧 Troubleshooting Guide

This guide helps you diagnose and resolve common issues when working with CVOYA graph.

## 🚨 Common Issues and Solutions

### Connection Issues

#### Problem: "Unable to connect to Neo4j database"

```text
Neo4jException: Could not perform discovery for server neo4j://localhost:7687
```

**Solutions:**

1. **Check Neo4j is running**

   ```bash
   # From a repository checkout; prefers Podman and falls back to Docker.
   ./scripts/containers/start-neo4j.sh
   ```

2. **Verify connection string format**

   ```csharp
   // ✅ Correct formats:
   var local = "neo4j://localhost:7687";          // Local unencrypted
   var encrypted = "neo4j+s://your-server:7687";  // Remote encrypted
   var selfSigned = "neo4j+ssc://your-server:7687"; // Encrypted with self-signed cert

   // ❌ Common mistakes:
   var missingPort = "neo4j://localhost";         // Missing port
   var wrongProtocol = "http://localhost:7474";   // Wrong protocol (that's HTTP interface)
   ```

3. **Check credentials**

   ```csharp
   // Repository container defaults are neo4j/password.
   var store = new Neo4jGraphStore("bolt://localhost:7687", "neo4j", "password");
   var graph = store.Graph;
   ```

#### Problem: "Connection timeout"

```text
TimeoutException: The request timed out after 30 seconds
```

**Solutions:**

1. **Increase timeout settings**

   ```csharp
   var driver = GraphDatabase.Driver(
       connectionString,
       AuthTokens.Basic(username, password),
       config => config
           .WithConnectionTimeout(TimeSpan.FromSeconds(60))
           .WithConnectionAcquisitionTimeout(TimeSpan.FromSeconds(60)));
   var store = new Neo4jGraphStore(driver);
   var graph = store.Graph;
   ```

2. **Check network connectivity**

   ```bash
   telnet your-server 7687
   # Should connect if Neo4j is accessible
   ```

### Query Issues

#### Problem: "No nodes found" when you expect results

```csharp
var users = await graph.Nodes<User>().ToListAsync();
// Returns empty list when you expect data
```

**Debugging steps:**

1. **Check node labels in database**

   ```cypher
   // In Neo4j Browser:
   MATCH (n) RETURN DISTINCT labels(n)
   ```

2. **Verify entity class labels**

   ```csharp
   [Node("User")]  // Must match label in database
   public record User : Node
   {
   }
   ```

3. **Check case sensitivity**

   ```csharp
   [Node("user")]
   public record LowercaseUser : Node;

   [Node("User")]
   public record User : Node; // Different label from "user"
   ```

4. **Enable query logging**

   ```csharp
   using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
   var neo4jGraphStore = new Neo4jGraphStore(
       "neo4j://localhost:7687",
       "neo4j",
       "password",
       loggerFactory: loggerFactory);
   ```

#### Problem: "Base-label queries miss rows from a newly created label" (Apache AGE)

```csharp
// Dokument has twelve registered FormConfig sub-labels, and a new Dokument_Typ13 exists
var documents = await graph.Nodes<Dokument>().ToListAsync();
// Returns the old count; the 5,000 rows in Dokument_Typ13 are missing
```

**Cause:** AGE stores every label in a separate table and has no parent-label metadata. Base-label
queries expand over the labels registered in the process (typed `[Node]` inheritance plus
`AgeGraphStore.RegisterLabelHierarchy`). A label created at runtime is not discovered, so its rows
are silently omitted.

**Solution:** register every physical child label before the first query, including every new
`FormConfig` label version:

```csharp
AgeGraphStore.RegisterLabelHierarchy("Dokument", "Dokument_Typ13");

await using var store = new AgeGraphStore(connectionString, "my_graph");
await store.CreateGraphIfNotExistsAsync();
```

The provider validates the catalog on the first graph operation. `AgeGraphStore.LabelHierarchyValidation`
is `Warn` by default, so a missing registration appears as a warning instead of a silently smaller
result. Set it to `Fail` to stop startup, or call
`store.FindUnregisteredLabelHierarchyAsync()` to inspect the findings explicitly.

#### Problem: "Invalid cast" when querying

```text
InvalidCastException: Unable to cast object of type 'System.String' to type 'System.Int32'
```

**Solutions:**

1. **Check property types match database**

   ```csharp
   // If the database has a string property but your model expects an int:
   [Node(Label = "User")]
   public record User : Node
   {
       public string ExternalId { get; init; } = string.Empty;
       // Not: public int ExternalId { get; set; }
   }
   ```

2. **Use nullable types for optional properties**

   ```csharp
   [Node(Label = "User")]
   public record User : Node
   {
       public int? Age { get; set; }           // Nullable if not always present
       public DateTime? CreatedDate { get; set; }  // Nullable for optional dates
   }
   ```

### Transaction Issues

#### Problem: "Transaction already committed/rolled back"

```text
InvalidOperationException: Cannot execute query on committed/rolled back transaction
```

**Solution:**

```csharp
var user = new User { Email = "one@example.com" };
var anotherUser = new User { Email = "two@example.com" };

// ❌ Reusing disposed transaction
await using var completedTransaction = await graph.GetTransactionAsync();
await graph.CreateNodeAsync(user, completedTransaction);
await completedTransaction.CommitAsync();
await graph.CreateNodeAsync(anotherUser, completedTransaction);  // Error - transaction already committed

// ✅ Create new transaction or don't commit until all operations complete
await using var transaction = await graph.GetTransactionAsync();
await graph.CreateNodeAsync(user, transaction);
await graph.CreateNodeAsync(anotherUser, transaction);
await transaction.CommitAsync();  // Commit both operations
```

#### Problem: "Deadlock detected"

```text
TransientException: Deadlock detected
```

**Solutions:**

1. **Keep transactions short**

   ```csharp
   // ✅ Short transaction
   await using var shortTransaction = await graph.GetTransactionAsync();
   await graph.CreateNodeAsync(user, shortTransaction);
   await shortTransaction.CommitAsync();

   // ❌ Long-running transaction
   await using var longTransaction = await graph.GetTransactionAsync();
   await SomeLongRunningOperation();  // Holds locks too long
   await graph.CreateNodeAsync(user, longTransaction);
   await longTransaction.CommitAsync();
   ```

2. **Retry logic for deadlocks**

   ```csharp
   public async Task<T> ExecuteWithRetry<T>(Func<Task<T>> operation, int maxRetries = 3)
   {
       for (int attempt = 0; attempt < maxRetries; attempt++)
       {
           try
           {
               return await operation();
           }
           catch (TransientException ex) when (ex.Message.Contains("Deadlock"))
           {
               if (attempt == maxRetries - 1) throw;
               await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)));
           }
       }
       throw new InvalidOperationException("Should not reach here");
   }
   ```

### Performance Issues

#### Problem: "Queries are very slow"

**Debugging steps:**

1. **Enable query profiling**

   ```csharp
   using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
   var neo4jGraphStore = new Neo4jGraphStore(
       "neo4j://localhost:7687",
       "neo4j",
       "password",
       loggerFactory: loggerFactory);
   ```

2. **Look for full table scans**

   ```cypher
   // Profile a slow query:
   PROFILE MATCH (u:User) WHERE u.email = 'test@example.com' RETURN u
   ```

3. **Check for unlimited traversals**

   ```csharp
   // ❌ Large depth - could traverse a large path of the graph
   var largeConnections = await graph.Nodes<User>()
       .Traverse<FriendOf, User>(minDepth: 1, maxDepth: 100)
       .ToListAsync();

   // ✅ Limited depth
   var limitedConnections = await graph.Nodes<User>()
       .Traverse<FriendOf, User>(minDepth: 1, maxDepth: 3)  // Max 3 hops
       .ToListAsync();
   ```

#### Problem: "Out of memory" with large datasets

**Solutions:**

1. **Use pagination**

   ```csharp
   // ❌ Loading everything at once
   var allUsers = await graph.Nodes<User>().ToListAsync();

   // ✅ Paginated loading
   var pageSize = 1000;
   var page = 0;
   List<User> batch;
   do
   {
       batch = await graph.Nodes<User>()
           .OrderBy(u => u.Email)
           .Skip(page * pageSize)
           .Take(pageSize)
           .ToListAsync();

       ProcessBatch(batch);
       page++;
   } while (batch.Count == pageSize);
   ```

2. **Use streaming**

```csharp
   // ✅ Process one at a time
   await foreach (var user in graph.Nodes<User>())
   {
       ProcessUser(user);
   }
```

### Analyzer Issues

#### Problem: Analyzer warnings in IDE

**Understanding diagnostics:**

| Rule ID | Description | Severity |
| --- | --- | --- |
| **CG001** | Missing parameterless constructor | Error |
| **CG002** | Property must have public accessors | Error |
| **CG003** | Property cannot be graph interface type | Error |
| **CG004** | Invalid property type for node | Error |
| **CG005** | Invalid property type for relationship | Error |
| **CG006** | Complex type contains graph interface types | Error |
| **CG007** | Duplicate property attribute label | Error |
| **CG008** | Duplicate relationship attribute label | Error |
| **CG009** | Duplicate node attribute label | Error |
| **CG010** | Circular reference without nullable | Error |
| **CG011** | Type should inherit from Node/Relationship instead of implementing directly | Warning |
| **CG012** | [Node]/[Relationship] on a type that doesn't implement the matching interface | Warning |
| **CG013** | Both [Node] and [Relationship] applied to the same type | Error |
| **CG014** | Graph entity types (INode/IRelationship) must be reference types | Error |
| **CG015** | [ComplexProperty] has no effect on the configured property | Warning |
| **CG016** | Open generic graph entities are not supported | Error |
| **CG018** | Invalid graph key or ignored property declaration | Error |

**Solutions:**
See the [Analyzers README](https://github.com/cvoya-com/graph/blob/main/src/Graph.Analyzers/README.md) for detailed examples of each warning and how to fix them.

## 🔍 Debugging Techniques

### 1. Enable Detailed Logging

```csharp
using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var neo4jGraphStore = new Neo4jGraphStore(
    "neo4j://localhost:7687",
    "neo4j",
    "password",
    loggerFactory: loggerFactory);
```

### 2. Inspect Generated Cypher

```csharp
// The logger will show queries like:
// MATCH (n:User) WHERE n.email = $p1 RETURN n
// Parameters: p1 = "test@example.com"
```

### 3. Test Queries Directly in Neo4j Browser

1. Copy the generated Cypher from logs
2. Paste into Neo4j Browser (http://localhost:7474)
3. Run with same parameters
4. Use `PROFILE` or `EXPLAIN` to understand performance

### 4. Verify Database State

```cypher
// Check what's actually in the database:
MATCH (n) RETURN labels(n), count(n)  // Node counts by label
MATCH ()-[r]->() RETURN type(r), count(r)  // Relationship counts by type
MATCH (n:User) RETURN n LIMIT 10  // Sample user nodes
```

### 5. Connection Testing

```csharp
public async Task<bool> TestConnection(string connectionString, string username, string password)
{
    try
    {
        await using var store = new Neo4jGraphStore(connectionString, username, password);
        await store.Graph.Nodes<DynamicNode>().Take(1).ToListAsync();
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Connection failed: {ex.Message}");
        return false;
    }
}
```

## 📞 Getting Help

### 1. Gather Information

When reporting issues, include:

- **CVOYA graph version**
- **.NET version**
- **Neo4j version**
- **Full error message and stack trace**
- **Minimal code example** that reproduces the issue
- **Generated Cypher query** (if applicable)

### 2. Check Documentation

- [Getting Started Guide](https://github.com/cvoya-com/graph/blob/main/README.md)
- [Core Concepts](./core-concepts.md)
- [Performance Guide](./performance.md)

### 3. Search Existing Issues

- Check [GitHub Issues](https://github.com/cvoya-com/graph/issues)
- Look for similar problems and solutions

### 4. Create Minimal Reproduction

```csharp
// Example minimal reproduction case:
public record TestNode : Node
{
    public string Name { get; set; } = string.Empty;
}

// The issue occurs when:
public static class Reproduction
{
    public static async Task ReproduceAsync()
    {
        await using var store = new Neo4jGraphStore("neo4j://localhost:7687", "neo4j", "password");
        var graph = store.Graph;
        var node = new TestNode { Id = "1", Name = "Test" };
        await graph.CreateNodeAsync(node);  // Fails here with [specific error]
    }
}
```

Remember: The more specific and complete your issue report, the faster we can help! 🚀
