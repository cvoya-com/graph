// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

using Cvoya.Graph;
using Cvoya.Graph.Age;
using Cvoya.Graph.InMemory;
using Cvoya.Graph.Neo4j;

namespace Documentation;

internal static class DocumentationSnippets
{
    private static async Task RootQuickStartAsync()
    {
        // snippet-start: root-quick-start
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
        // snippet-end: root-quick-start
    }

    private static async Task RelationshipCreationAsync(IGraph graph)
    {
        // snippet-start: relationship-creation
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
        // snippet-end: relationship-creation
    }

    private static async Task SetBasedMutationAsync(IGraph graph)
    {
        // snippet-start: set-based-mutation
        var adults = graph.Nodes<Person>().Where(person => person.Age >= 18);

        var updated = await adults.UpdateAsync(setters => setters
            .SetProperty(person => person.Name, person => person.Name)
            .SetProperty(person => person.PreviousAddresses, new List<Address?>()));

        var deleted = await graph.Nodes<Person>()
            .Where(person => person.Email.EndsWith("@expired.example"))
            .DeleteAsync(cascadeDelete: true);
        // snippet-end: set-based-mutation
        Console.WriteLine($"Updated {updated} and deleted {deleted} person(s).");
    }

    private static async Task Neo4jQuickStartAsync()
    {
        // snippet-start: neo4j-quick-start
        // Configure connection
        await using var store =
            new Neo4jGraphStore("neo4j+s://your-server:7687", "neo4j", "your-password");
        var graph = store.Graph;

        // Use CVOYA graph APIs
        var users = await graph.Nodes<User>()
            .Where(u => u.IsActive)
            .OrderBy(u => u.CreatedDate)
            .ToListAsync();
        // snippet-end: neo4j-quick-start
        Console.WriteLine($"Found {users.Count} active user(s).");
    }

    private static async Task AgeQuickStartAsync()
    {
        // snippet-start: age-quick-start
        await using var store = new AgeGraphStore(
            "Host=localhost;Port=5456;Username=postgres;Password=postgres;Database=postgres",
            graphName: "my_graph");

        await store.CreateGraphIfNotExistsAsync();
        var graph = store.Graph;

        var activeUsers = await graph.Nodes<User>()
            .Where(user => user.IsActive)
            .ToListAsync();
        // snippet-end: age-quick-start
        Console.WriteLine($"Found {activeUsers.Count} active user(s).");
    }

    private static async Task InMemoryQuickStartAsync()
    {
        // snippet-start: in-memory-quick-start
        await using var store = new InMemoryGraphStore();
        var graph = store.Graph;

        await graph.CreateNodeAsync(new Person { Name = "Alice" });
        var alice = await graph.Nodes<Person>().Where(p => p.Name == "Alice").SingleAsync();
        // snippet-end: in-memory-quick-start
        Console.WriteLine(alice.Name);
    }
}
