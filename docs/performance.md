---
title: Performance Guide
layout: default
---

# 🚀 Performance Guide

This guide provides best practices and optimization techniques for maximizing performance when using CVOYA graph.

## 📊 General Performance Principles

### 1. Query Optimization

- **Use specific queries** instead of loading large datasets
- **Project only needed properties** to reduce data transfer
- **Use pagination** for large result sets

### 2. Connection Management

- **Use connection pooling** (enabled by default in Neo4j provider)
- **Reuse graph instances** instead of creating new ones
- **Configure timeouts** appropriately for your use case

### 3. Transaction Efficiency

- **Batch operations** within transactions
- **Keep transactions short** to avoid locks
- **Use explicit transactions** for multiple operations

## 🎯 Querying Best Practices

### Efficient Node Queries

```csharp
// ✅ Good - specific query with projection
var userEmails = await graph.Nodes<User>()
    .Where(u => u.IsActive && u.CreatedDate > DateTime.Now.AddDays(-30))
    .Select(u => new { u.Email })
    .ToListAsync();

// ❌ Avoid - loading full objects when only emails needed
var users = await graph.Nodes<User>()
    .Where(u => u.IsActive)
    .ToListAsync();
var emails = users.Select(u => u.Email).ToList();
```

### Counting Without Materializing

```csharp
// ✅ Good - one server-side count aggregate
var activeUsers = await graph.Nodes<User>()
    .CountAsync(u => u.IsActive);

// ❌ Avoid - loads and deserializes every entity just to count it
var allUsers = await graph.Nodes<User>().ToListAsync();
var userCount = allUsers.Count;
```

`Count`/`LongCount` and their async variants translate to a server-side `count(...)` aggregate.
Treat `.ToList().Count` as forbidden in hot paths and large result sets.

### Efficient Relationship Traversal

```csharp
// ✅ Good - depth-limited traversal
var nearbyFriends = await graph.Nodes<User>()
    .Where(u => u.Email == userEmail)
    .Traverse<FriendOf, Person>(minDepth: 1, maxDepth: 2)  // Limit to 2 degrees of separation
    .Where(friend => friend.City == "Seattle")
    .ToListAsync();

// ❌ Avoid - unlimited depth traversal
var allConnections = await graph.Nodes<User>()
    .Where(u => u.Email == userEmail)
    .Traverse<FriendOf, Person>(minDepth: 1, maxDepth: 100) // Large depth limit
    .ToListAsync();
```

### Pagination for Large Results

```csharp
// ✅ Good - paginated results
var pageSize = 100;
var page = 0;

var firstPage = await graph.Nodes<User>()
    .OrderBy(u => u.CreatedDate)
    .Skip(page * pageSize)
    .Take(pageSize)
    .ToListAsync();

// For very large datasets, consider cursor-based pagination
var lastSeenDate = DateTime.UtcNow.AddDays(-1);
var nextPage = await graph.Nodes<User>()
    .Where(u => u.CreatedDate > lastSeenDate)
    .OrderBy(u => u.CreatedDate)
    .Take(pageSize)
    .ToListAsync();
```

## 🔧 Neo4j-Specific Optimizations

### Connection Configuration

```csharp
// ✅ Optimized connection settings
var driver = GraphDatabase.Driver(
    "neo4j+s://your-server:7687",
    AuthTokens.Basic("username", "password"),
    config => config
        .WithMaxConnectionLifetime(TimeSpan.FromMinutes(30))
        .WithMaxConnectionPoolSize(100)
        .WithConnectionAcquisitionTimeout(TimeSpan.FromSeconds(30))
        .WithConnectionTimeout(TimeSpan.FromSeconds(30)));

var store = new Neo4jGraphStore(driver);
var graph = store.Graph;
```

### Efficient Batch Operations

```csharp
// ✅ Good - batch create in single transaction
await using var transaction = await graph.GetTransactionAsync();

var users = new List<User>();
for (int i = 0; i < 1000; i++)
{
    users.Add(new User { Email = $"user{i}@example.com" });
}

foreach (var user in users)
{
    await graph.CreateNodeAsync(user, transaction: transaction);
}
await transaction.CommitAsync();

// ❌ Avoid - individual transactions
foreach (var user in users)
{
    await graph.CreateNodeAsync(user);  // Separate transaction per node!
}
```

### Complex Property Optimization

Complex properties are stored as first-class value nodes, so each value adds a node and relationship.
They auto-load recursively for declared read shapes, bounded at five relationship levels. Keep frequently
filtered scalar values near the owning node, and use explicit graph entities when a value needs shared
identity across owners. Collections preserve order through relationship sequence metadata.

```csharp
// For large complex objects, consider splitting into separate nodes
public record User : Node
{
    public string Email { get; set; } = string.Empty;

    // ✅ Good - simple properties on main node
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }

    // ❌ Avoid - very large auto-loaded complex-property subgraphs
    // public List<VeryLargeObject> ComplexData { get; set; }
}

// ✅ Better - separate related entities
public record UserProfile : Node
{
    public string UserId { get; set; } = string.Empty;
    public List<SomeComplexData> ProfileData { get; set; } = new();
}
```

## 📈 Monitoring and Profiling

### Query Performance Monitoring

```csharp
// Enable query logging for development
using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var store = new Neo4jGraphStore(connectionString, username, password, loggerFactory: loggerFactory);
var graph = store.Graph;

// Time critical operations
var stopwatch = Stopwatch.StartNew();
var results = await graph.Nodes<User>().Where(u => u.Email == email).ToListAsync();
stopwatch.Stop();

if (stopwatch.ElapsedMilliseconds > 1000)
{
    _logger.LogWarning($"Slow query detected: {stopwatch.ElapsedMilliseconds}ms");
}
```

### Memory Usage Optimization

```csharp
// ✅ Good - dispose resources properly
await using var store = new Neo4jGraphStore(connectionString, username, password);
var graph = store.Graph;

// ✅ Good - use streaming for large datasets
await foreach (var user in graph.Nodes<User>())
{
    // Process one at a time instead of loading all into memory
    ProcessUser(user);
}

// ❌ Avoid - loading huge datasets into memory
var allUsers = await graph.Nodes<User>().ToListAsync();  // Could use lots of RAM
```

## 🎛️ Configuration Tuning

### Neo4j Driver Settings

```text
Pseudo-code:
Configure connection lifetime, pool size, acquisition timeout, connection timeout,
TLS trust strategy, and logging in the Neo4j driver configuration callback used
when creating the driver.
```

### Application-Level Caching

```csharp
// Consider caching frequently accessed, rarely changing data
private readonly Dictionary<string, User> _cache = new();

public async Task<User?> GetUserAsync(string email)
{
    if (_cache.TryGetValue($"user:{email}", out User cachedUser))
    {
        return cachedUser;
    }

    var user = await graph.Nodes<User>()
        .Where(u => u.Email == email)
        .FirstOrDefaultAsync();

    if (user != null)
    {
        _cache[$"user:{email}"] = user;
    }

    return user;
}
```

## 📋 Performance Checklist

### Before Going to Production

- [ ] **Profile query performance** with realistic data volumes
- [ ] **Test connection pool settings** under load
- [ ] **Set appropriate timeouts** for your use case
- [ ] **Implement proper error handling** for timeouts
- [ ] **Monitor memory usage** with large datasets
- [ ] **Test transaction rollback** scenarios
- [ ] **Verify cleanup** of disposed resources

### Common Performance Issues

- [ ] **Loading too much data** at once
- [ ] **Large depth traversals**
- [ ] **Too many small transactions** instead of batching
- [ ] **Connection pool exhaustion**
- [ ] **Memory leaks** from undisposed resources
- [ ] **N+1 query problems** in relationship traversal

## 🔧 Troubleshooting Slow Queries

### 1. Enable Query Logging

```csharp
using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var store = new Neo4jGraphStore(connectionString, username, password, loggerFactory: loggerFactory);
var graph = store.Graph;
```

### 2. Analyze Generated Cypher

Review the generated Cypher queries in logs:

- Are there unnecessary data transfers?
- Can queries be simplified?

### 3. Use Neo4j Query Profiling

```cypher
PROFILE MATCH (u:User) RETURN u
WHERE u.email = $p0
RETURN ...
```

### 4. Common Query Patterns to Avoid

```csharp
// ❌ Avoid - loading one traversal per user (N+1 problem)
foreach (var user in users)
{
    var friends = await graph.Nodes<User>()
        .Where(candidate => candidate.Email == user.Email)
        .Traverse<FriendOf, User>()
        .ToListAsync();
}

// ✅ Better - select all sources once and traverse in one provider query
var userEmails = users.Select(user => user.Email).ToList();
var friendships = await graph.Nodes<User>()
    .Where(user => userEmails.Contains(user.Email))
    .PathSegments<User, FriendOf, User>()
    .ToListAsync();
```

## 📊 Benchmarking

Create benchmarks for critical operations:

```csharp
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class CVOYA graphBenchmarks
{
    private Neo4jGraphStore _store = null!;
    private IGraph _graph = null!;

    private const string ConnectionString = "neo4j://localhost:7687";
    private const string Username = "neo4j";
    private const string Password = "password";

    [GlobalSetup]
    public void Setup()
    {
        _store = new Neo4jGraphStore(ConnectionString, Username, Password);
        _graph = _store.Graph;
    }

    [Benchmark]
    public async Task<List<User>> QueryUsers()
    {
        return await _graph.Nodes<User>()
            .Where(u => u.IsActive)
            .Take(1000)
            .ToListAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _store.DisposeAsync();
    }
}
```

Remember: **Measure first, optimize second**. Use profiling tools to identify actual bottlenecks rather than guessing.

## Performance Testing and Benchmarking

CVOYA graph includes comprehensive performance testing infrastructure using BenchmarkDotNet to ensure consistent performance across releases and detect regressions.

## 📊 Available Benchmarks

### CRUD Operations Benchmark

- **Node creation**: Bulk creation of nodes with realistic data
- **Node retrieval**: Query performance with various filters
- **Node updates**: Batch update operations
- **Node deletion**: Cleanup performance testing
- **Memory allocation**: Tracks memory usage patterns

### Relationship Benchmark

- **Relationship creation**: Performance of creating relationships between nodes
- **Graph traversal**: Different traversal patterns and depths
- **Complex queries**: Multi-hop traversals and filtering
- **Relationship updates**: Modification performance
- **Memory footprint**: Relationship-specific memory analysis

## 🚀 Running Benchmarks Locally

### Quick Start

Use the provided scripts for easy benchmark execution:

#### PowerShell (Windows)

```powershell
# Run all benchmarks
./scripts/run-benchmarks.ps1

# Run specific benchmark types
./scripts/run-benchmarks.ps1 -Mode crud
./scripts/run-benchmarks.ps1 -Mode relationships

# Interactive selection (choose specific benchmarks)
./scripts/run-benchmarks.ps1 -Mode interactive

# Custom output directory
./scripts/run-benchmarks.ps1 -OutputDir "./my-benchmarks"
```

#### Bash (macOS/Linux)

```bash
# Run all benchmarks
./scripts/run-benchmarks.sh

# Run specific benchmark types
./scripts/run-benchmarks.sh --mode crud
./scripts/run-benchmarks.sh --mode relationships

# Interactive selection
./scripts/run-benchmarks.sh --mode interactive

# Custom output directory
./scripts/run-benchmarks.sh --output "./my-benchmarks"
```

### Manual Execution

You can also run benchmarks directly:

```bash
# Build the performance test project
dotnet build tests/Graph.Performance.Tests --configuration Benchmark

# Run all benchmarks (non-interactive)
dotnet run --project tests/Graph.Performance.Tests --configuration Benchmark -- --all

# Run specific benchmark class
dotnet run --project tests/Graph.Performance.Tests --configuration Benchmark -- --filter "*CrudOperations*"

# Interactive mode (local development only)
dotnet run --project tests/Graph.Performance.Tests --configuration Benchmark
```

## 🔄 CI/CD Integration

### Automated Performance Testing

The repository keeps a disabled performance-workflow template at
`.github/workflows/performance.yml.disable`; performance benchmarks are currently an explicit
local/release-engineering activity, not a required hosted PR gate. The template is designed to:

1. **Use non-interactive mode** with the `--all` parameter.
2. **Generate multiple report formats**:
   - HTML reports for human readability
   - JSON reports for programmatic analysis
   - Markdown summaries for PR comments
3. **Store artifacts** for 30 days.
4. **Compare performance** against a baseline when available.

### Non-Interactive Mode

The performance tests are configured to run automatically in CI environments:

- **Default behavior**: When no arguments or `--all` is passed, all benchmarks run without user interaction
- **CI-optimized configuration**: Uses in-process toolchain for faster execution
- **Validation disabled**: Skips optimization validators that require user input

### Benchmark Configuration

CVOYA graph uses a special **Benchmark** build configuration that combines:

- **Release-level optimizations**: Full compiler optimizations enabled
- **Project references**: Uses local project references instead of NuGet packages
- **Debug symbols**: Maintains debugging capability for analysis
- **TRACE constants**: Enables performance tracking

This configuration is ideal for:

- ✅ **Local development**: Uses project references, no need for published packages
- ✅ **Performance testing**: Full optimizations like Release builds
- ✅ **CI/CD pipelines**: Consistent behavior across environments
- ✅ **Debugging**: Retain symbols for performance analysis

### Workflow Configuration

The performance workflow runs:

- On pushes to `main` branch
- On pull requests (to detect regressions)
- Can be triggered manually via GitHub UI
- Uses .NET 10 runtime for latest performance optimizations
- Uses **Benchmark** configuration for optimal performance with project references
- Benchmarks run on the host process runtime (.NET 10) for accurate performance measurement
- CVOYA graph targets .NET 10 for production use

## 📈 Interpreting Results

### BenchmarkDotNet Output

Results include:

- **Mean execution time**: Average time per operation
- **Standard deviation**: Consistency of performance
- **Memory allocation**: Managed memory allocated per operation
- **Throughput**: Operations per second (when applicable)

### HTML Reports

Generated HTML reports provide:

- **Interactive charts** comparing different scenarios
- **Detailed statistics** including percentiles
- **Memory analysis** with allocation patterns
- **Historical comparisons** when available

### Performance Baselines

Key performance indicators to monitor:

- **Node creation**: Should handle 1000+ nodes/second
- **Simple queries**: Sub-millisecond response for basic filters
- **Traversals**: Efficient scaling with graph depth
- **Memory usage**: Minimal allocations for read operations

## 🎯 Adding New Benchmarks

### Creating a Benchmark Class

```csharp
[MemoryDiagnoser]
[HtmlExporter]
[MarkdownExporter]
public class MyCustomBenchmark
{
    private IGraph _graph = null!;
    private Neo4jGraphStore _store = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Initialize test data
        _store = CreateTestGraphStore();
        _graph = _store.Graph;
    }

    [Benchmark]
    public async Task<int> MyOperation()
    {
        // Your benchmark code here
        return await _graph.Nodes<MyNode>().CountAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _store.DisposeAsync();
    }

    private static Neo4jGraphStore CreateTestGraphStore()
        => new("neo4j://localhost:7687", "neo4j", "password");
}
```

### Benchmark Attributes

- `[MemoryDiagnoser]`: Tracks memory allocations
- `[HtmlExporter]`: Generates HTML reports
- `[MarkdownExporter]`: Creates markdown summaries
- `[Params]`: Parameterize benchmarks with different inputs
- `[Benchmark]`: Marks methods to benchmark

### Best Practices

1. **Realistic data**: Use representative dataset sizes
2. **Proper setup/cleanup**: Initialize in `[GlobalSetup]`, cleanup in `[GlobalCleanup]`
3. **Avoid optimization**: Don't let compiler optimize away your benchmark
4. **Consistent environment**: Use same configuration across runs
5. **Multiple iterations**: Let BenchmarkDotNet determine optimal iteration count

## 🔧 Configuration

### BenchmarkDotNet Configuration

The performance tests use a custom configuration optimized for CI:

```csharp
var config = DefaultConfig.Instance
    .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance))
    .WithOptions(ConfigOptions.DisableOptimizationsValidator);
```

### Output Formats

Multiple export formats are generated:

- **HTML**: Interactive reports with charts
- **JSON**: Machine-readable data for analysis
- **Markdown**: Summary reports for documentation

### Artifacts

Benchmark results are stored as GitHub artifacts:

- **Retention**: 30 days
- **Location**: `./artifacts/benchmarks`
- **Formats**: All generated reports and raw data

## 🚨 Performance Regression Detection

### Monitoring

- **Automated alerts**: CI fails if performance degrades significantly
- **Trend analysis**: Compare against historical baselines
- **Memory regression**: Track allocation patterns over time

### Thresholds

Performance regression is flagged when:

- **Execution time** increases by >20% for critical operations
- **Memory allocations** increase significantly without justification
- **Throughput** decreases below acceptable levels

### Investigation

When regressions are detected:

1. **Compare reports**: Review before/after performance data
2. **Profile locally**: Use detailed profiling tools for root cause analysis
3. **Isolate changes**: Identify specific commits causing regression
4. **Optimize**: Apply targeted performance improvements
