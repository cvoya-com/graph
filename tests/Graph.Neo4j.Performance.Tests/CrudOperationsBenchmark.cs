// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Graph.Performance.Tests;

#pragma warning disable RS1035 // Do not use APIs banned for analyzers

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Bogus;
using Cvoya.Graph;
using Cvoya.Graph.Neo4j;
using global::Neo4j.Driver;

[MemoryDiagnoser]
[MarkdownExporter]
[HtmlExporter]
public class CrudOperationsBenchmark
{
    private static readonly DateTime BenchmarkReferenceDate = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Random _random = new();
    private Neo4jGraphStore _graphStore = null!;
    private IGraph _graph = null!;
    private List<Person> _testPersons = null!;
    private List<string> _personKeys = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var connectionString = Environment.GetEnvironmentVariable("NEO4J_URI") ?? "bolt://localhost:7687";
        var username = Environment.GetEnvironmentVariable("NEO4J_USER") ?? "neo4j";
        var password = Environment.GetEnvironmentVariable("NEO4J_PASSWORD") ?? "password";

        var driver = GraphDatabase.Driver(connectionString, AuthTokens.Basic(username, password));
        await using (var session = driver.AsyncSession())
        {
            var result = await session.RunAsync($"CREATE OR REPLACE DATABASE {"PerformanceBenchmark"}");
            await result.ConsumeAsync();
        }

        // Wait for database to be ready
        var maxWaitTime = TimeSpan.FromSeconds(30);
        var pollInterval = TimeSpan.FromMilliseconds(500);
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < maxWaitTime)
        {
            try
            {
                await using var testSession = driver.AsyncSession(o => o.WithDatabase("PerformanceBenchmark"));
                var result = await testSession.RunAsync("RETURN 1");
                await result.ConsumeAsync();
                break; // Database is ready
            }
            catch (Exception)
            {
                // Database not ready yet, wait and retry
                await Task.Delay(pollInterval);
            }
        }

        _graphStore = new Neo4jGraphStore(connectionString, username, password, "PerformanceBenchmark");
        _graph = _graphStore.Graph;

        // Generate test data
        var faker = new Faker<Person>()
            .RuleFor(p => p.TestKey, f => f.Random.Guid().ToString())
            .RuleFor(p => p.FirstName, f => f.Name.FirstName())
            .RuleFor(p => p.LastName, f => f.Name.LastName())
            .RuleFor(p => p.Email, f => f.Internet.Email())
            .RuleFor(p => p.DateOfBirth, f => f.Date.Past(50, BenchmarkReferenceDate.AddYears(-18)));

        _testPersons = faker.Generate(1000);
        _personKeys = _testPersons.Select(p => p.TestKey).ToList();

        // Pre-populate some data
        await using var transaction = await _graph.GetTransactionAsync();
        foreach (var person in _testPersons.Take(500))
        {
            await _graph.CreateNodeAsync(person, transaction);
        }
        await transaction.CommitAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        // Clean up test data
        await using var transaction = await _graph.GetTransactionAsync();
        await _graph.Nodes<Person>(transaction).DeleteAsync();
        await transaction.CommitAsync();

        await _graphStore.DisposeAsync();
    }

    [Benchmark]
    public async Task CreateSingleNode()
    {
        var person = _testPersons[_random.Next(_testPersons.Count)];
        await _graph.CreateNodeAsync(person);
    }

    [Benchmark]
    public async Task CreateMultipleNodes()
    {
        var persons = _testPersons.Take(10).Select(p => new Person
        {
            TestKey = Guid.NewGuid().ToString(),
            FirstName = p.FirstName,
            LastName = p.LastName,
            Email = p.Email,
            DateOfBirth = p.DateOfBirth
        }).ToList();

        foreach (var person in persons)
        {
            await _graph.CreateNodeAsync(person);
        }
    }

    [Benchmark]
    public async Task ReadSingleNode()
    {
        var key = _personKeys[_random.Next(_personKeys.Count)];
        _ = await _graph.Nodes<Person>().Where(person => person.TestKey == key).SingleAsync();
    }

    [Benchmark]
    public async Task QueryNodes()
    {
        // Discarded on purpose: this benchmark measures query execution cost, not the result -
        // the assignment would otherwise be a useless-local (CodeQL cs/useless-assignment-to-local).
#pragma warning disable CA1866 // Benchmark the provider-translated string overload.
        _ = await _graph.Nodes<Person>()
            .Where(p => p.FirstName.StartsWith("A"))
            .Take(10)
            .ToListAsync();
#pragma warning restore CA1866
    }

    [Benchmark]
    public async Task QueryWithComplexFilter()
    {
        var cutoffDate = BenchmarkReferenceDate.AddYears(-30);
        // Discarded on purpose - see QueryNodes above.
        _ = await _graph.Nodes<Person>()
            .Where(p => p.DateOfBirth > cutoffDate && p.Email.Contains("@gmail.com"))
            .OrderBy(p => p.LastName)
            .Take(20)
            .ToListAsync();
    }

    [Benchmark]
    public async Task UpdateNode()
    {
        var key = _personKeys[_random.Next(_personKeys.Count)];
        await _graph.Nodes<Person>()
            .Where(person => person.TestKey == key)
            .UpdateAsync(setters => setters.SetProperty(
                person => person.Email,
                $"updated_{BenchmarkReferenceDate.Ticks}@example.com"));
    }
}

[Node("Person")]
public record Person : Node
{
    public string TestKey { get; set; } = string.Empty;

    [Property(Label = "first_name")]
    public string FirstName { get; set; } = string.Empty;

    [Property(Label = "last_name")]
    public string LastName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    [Property(Label = "birth_date")]
    public DateTime DateOfBirth { get; set; }
}
