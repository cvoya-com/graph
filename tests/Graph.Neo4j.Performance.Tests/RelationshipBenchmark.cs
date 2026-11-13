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
public class RelationshipBenchmark
{
    private static readonly DateTime BenchmarkReferenceDate = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Random _random = new();
    private Neo4jGraphStore _graphStore = null!;
    private IGraph _graph = null!;
    private List<Person> _persons = null!;
    private List<Company> _companies = null!;

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

        _graphStore = new Neo4jGraphStore(
            connectionString,
            username,
            password,
            "PerformanceBenchmark"
        );

        _graph = _graphStore.Graph;

        // Generate test data
        var personFaker = new Faker<Person>()
            .RuleFor(p => p.TestKey, f => f.Random.Guid().ToString())
            .RuleFor(p => p.FirstName, f => f.Name.FirstName())
            .RuleFor(p => p.LastName, f => f.Name.LastName())
            .RuleFor(p => p.Email, f => f.Internet.Email())
            .RuleFor(p => p.DateOfBirth, f => f.Date.Past(50, BenchmarkReferenceDate.AddYears(-18)));

        var companyFaker = new Faker<Company>()
            .RuleFor(c => c.TestKey, f => f.Random.Guid().ToString())
            .RuleFor(c => c.Name, f => f.Company.CompanyName())
            .RuleFor(c => c.Industry, f => f.Commerce.Department());

        _persons = personFaker.Generate(200);
        _companies = companyFaker.Generate(50);

        // Create nodes
        await using var transaction = await _graph.GetTransactionAsync();
        foreach (var person in _persons)
        {
            await _graph.CreateNodeAsync(person, transaction);
        }
        foreach (var company in _companies)
        {
            await _graph.CreateNodeAsync(company, transaction);
        }
        await transaction.CommitAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        // Clean up test data
        await using var transaction = await _graph.GetTransactionAsync();

        await _graph.Relationships<WorksAt>(transaction).DeleteAsync();
        await _graph.Nodes<Person>(transaction).DeleteAsync();
        await _graph.Nodes<Company>(transaction).DeleteAsync();

        await transaction.CommitAsync();
        await _graphStore.DisposeAsync();
    }

    [Benchmark]
    public async Task CreateSingleRelationship()
    {
        var person = _persons[_random.Next(_persons.Count)];
        var company = _companies[_random.Next(_companies.Count)];

        var relationship = new WorksAt
        {
            Position = "Software Developer",
            StartDate = BenchmarkReferenceDate.AddYears(-_random.Next(1, 5))
        };

        await _graph.CreateRelationshipAsync(
            _graph.Nodes<Person>().Where(candidate => candidate.TestKey == person.TestKey),
            relationship,
            _graph.Nodes<Company>().Where(candidate => candidate.TestKey == company.TestKey));
    }

    [Benchmark]
    public async Task CreateMultipleRelationships()
    {
        for (int i = 0; i < 10; i++)
        {
            var person = _persons[_random.Next(_persons.Count)];
            var company = _companies[_random.Next(_companies.Count)];

            var relationship = new WorksAt
            {
                Position = "Developer",
                StartDate = BenchmarkReferenceDate.AddYears(-_random.Next(1, 5))
            };
            await _graph.CreateRelationshipAsync(
                _graph.Nodes<Person>().Where(candidate => candidate.TestKey == person.TestKey),
                relationship,
                _graph.Nodes<Company>().Where(candidate => candidate.TestKey == company.TestKey));
        }
    }

    [Benchmark]
    public async Task TraverseFromPersonToCompany()
    {
        var person = _persons[_random.Next(_persons.Count)];

        // Discarded on purpose: this benchmark measures traversal execution cost, not the result -
        // the assignment would otherwise be a useless-local (CodeQL cs/useless-assignment-to-local).
        _ = await _graph.Nodes<Person>()
            .Where(p => p.TestKey == person.TestKey)
            .Traverse<WorksAt, Company>()
            .ToListAsync();
    }

    [Benchmark]
    public async Task TraverseFromCompanyToEmployees()
    {
        var company = _companies[_random.Next(_companies.Count)];

        // Discarded on purpose - see TraverseFromPersonToCompany above.
        _ = await _graph.Nodes<Company>()
            .Where(c => c.TestKey == company.TestKey)
            .Traverse<WorksAt, Person>()
            .ToListAsync();
    }

    [Benchmark]
    public async Task ComplexTraversal()
    {
        // Find all people who work at companies in "Technology" industry.
        // Discarded on purpose - see TraverseFromPersonToCompany above.
#pragma warning disable CA1866 // Benchmark the provider-translated string overload.
        _ = await _graph.Nodes<Person>()
            .Where(p => p.FirstName.StartsWith("A"))
            .Traverse<WorksAt, Company>()
            .Where(c => c.Industry.Contains("Technology"))
            .ToListAsync();
#pragma warning restore CA1866
    }
}

[Node("Company")]
public record Company : Node
{
    public string TestKey { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Industry { get; set; } = string.Empty;
}

[Relationship("WORKS_AT")]
public record WorksAt : Relationship
{
    public string Position { get; set; } = string.Empty;

    [Property(Label = "start_date")]
    public DateTime StartDate { get; set; }
}
