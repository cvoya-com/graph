// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using Cvoya.Graph.Age.Core;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.CompatibilityTests;

/// <summary>
/// AGE-specific full-text search integration tests that go beyond the provider-neutral contract:
/// the phase-1 SQL runs on the caller's transaction, so it observes that transaction's uncommitted
/// writes.
/// </summary>
public sealed class AgeFullTextSearchIntegrationTests(AgeHarness harness)
    : AgeTest(harness, StoreIsolation.FreshStore)
{
    [Fact]
    public async Task Search_RunsOnCallerTransaction_SeesUncommittedWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transaction = await this.Graph.GetTransactionAsync(ct);

        var person = new Person { FirstName = "Uncommitted", LastName = "TransientToken" };
        await this.Graph.CreateNodeAsync(person, transaction, ct);

        // Phase 1 executes on the same transaction as the write, so the uncommitted node is visible.
        var inTransaction = await this.Graph.SearchNodes<Person>("TransientToken", transaction)
            .ToListAsync(ct);
        Assert.Single(inTransaction);
        Assert.Equal(person.TestKey, inTransaction[0].TestKey);

        await transaction.RollbackAsync();

        // After rollback the write is gone, so a fresh search finds nothing.
        var afterRollback = await this.Graph.SearchNodes<Person>("TransientToken").ToListAsync(ct);
        Assert.Empty(afterRollback);
    }

    [Fact]
    public async Task MixedSearch_AppliesPagingAndTerminalsAfterCombiningBothKinds()
    {
        var ct = TestContext.Current.CancellationToken;
        const string term = "MixedSearchBoundary";
        var first = new Person { FirstName = term, LastName = "Node" };
        var second = new Person { FirstName = "Endpoint", LastName = "Node" };
        await this.Graph.CreateNodeAsync(first, null, ct);
        await this.Graph.CreateNodeAsync(second, null, ct);

        var relationship = new KnowsWell { HowWell = $"{term} relationship" };
        await this.Graph.ConnectAsync(first, relationship, second, cancellationToken: ct);

        var all = await this.Graph.Search(term).ToListAsync(ct);
        Assert.Equal(2, all.Count);

        var paged = await this.Graph.Search(term)
            .Take(1)
            .ToListAsync(ct);
        Assert.Single(paged);

        Assert.Equal(2, await this.Graph.Search(term).CountAsync(ct));
    }

    [Fact]
    public async Task Search_RawNativeElements_DoNotExposeGraphid()
    {
        var ct = TestContext.Current.CancellationToken;
        const string term = "ExternalSearchToken474";
        await using (var transaction = await this.Graph.GetTransactionAsync(ct))
        {
            await using var result = await ((AgeGraphTransaction)transaction).Runner.RunAsync(
                """
                CREATE (source:Person)
                SET source.FirstName = $term, source.LastName = 'External', source.Bio = '',
                    source.inheritance_labels = ['Person']
                CREATE (target:Person)
                SET target.FirstName = 'Endpoint', target.LastName = 'External', target.Bio = '',
                    target.inheritance_labels = ['Person']
                CREATE (source)-[relationship:WORKS_REALLY_WELL_WITH]->(target)
                SET relationship.HowWell = $term
                RETURN true AS created
                """,
                new { term },
                ct);
            var record = await result.SingleAsync(ct);
            Assert.True(record["created"].As<bool>());
            await transaction.CommitAsync();
        }

        var node = Assert.Single(await this.Graph.SearchNodes<Person>(term).ToListAsync(ct));
        var relationship = Assert.Single(
            await this.Graph.SearchRelationships<KnowsWell>(term).ToListAsync(ct));
        var mixed = await this.Graph.Search(term).ToListAsync(ct);

        Assert.Equal(term, node.FirstName);
        Assert.Equal(term, relationship.HowWell);
        Assert.Equal(2, mixed.Count);
    }

    [Fact]
    public async Task DynamicSearch_DiscoversUnregisteredExternalLabelTables()
    {
        var ct = TestContext.Current.CancellationToken;
        const string term = "UnregisteredExternalToken474";
        await using (var transaction = await this.Graph.GetTransactionAsync(ct))
        {
            await using var result = await ((AgeGraphTransaction)transaction).Runner.RunAsync(
                """
                CREATE (source:ExternalSearchNode)
                SET source.Name = $term
                CREATE (target:ExternalSearchNode)
                SET target.Name = 'Endpoint'
                CREATE (source)-[relationship:EXTERNAL_SEARCH_EDGE]->(target)
                SET relationship.Description = $term
                RETURN true AS created
                """,
                new { term },
                ct);
            _ = await result.SingleAsync(ct);
            await transaction.CommitAsync();
        }

        var node = Assert.Single(await this.Graph.SearchNodes<DynamicNode>(term).ToListAsync(ct));
        var relationship = Assert.Single(
            await this.Graph.SearchRelationships<DynamicRelationship>(term).ToListAsync(ct));

        Assert.Contains("ExternalSearchNode", node.Labels);
        Assert.Equal(term, node.Properties["Name"]);
        Assert.Equal("EXTERNAL_SEARCH_EDGE", relationship.Type);
        Assert.Equal(term, relationship.Properties["Description"]);
    }

    [Fact]
    public async Task Search_DuplicateDomainKeys_CorrelatesEveryMatchingGraphid()
    {
        var ct = TestContext.Current.CancellationToken;
        const string term = "DuplicateIdentitySearchToken474";
        const string duplicateKey = "duplicate-search-key-474";
        await this.Graph.CreateNodeAsync(
            new Person { TestKey = duplicateKey, FirstName = "First", LastName = term },
            cancellationToken: ct);
        await this.Graph.CreateNodeAsync(
            new Person { TestKey = duplicateKey, FirstName = "Second", LastName = term },
            cancellationToken: ct);

        var results = await this.Graph.SearchNodes<Person>(term)
            .OrderBy(person => person.FirstName)
            .ToListAsync(ct);

        Assert.Equal(2, results.Count);
        Assert.Equal(["First", "Second"], results.Select(person => person.FirstName));
        Assert.All(results, person => Assert.Equal(duplicateKey, person.TestKey));
    }

    [Fact]
    public async Task Search_ExcludesRetiredUniversalTablesFromExternalRootDiscovery()
    {
        var ct = TestContext.Current.CancellationToken;
        const string term = "ReservedUniversalSearchToken474";
        await using (var transaction = await this.Graph.GetTransactionAsync(ct))
        {
            var runner = ((AgeGraphTransaction)transaction).Runner;
            await runner.EnsureLabelAsync("CvoyaNode", relationship: false, ct);
            await runner.EnsureLabelAsync("CvoyaRelationship", relationship: true, ct);
            await using var result = await runner.RunAsync(
                """
                CREATE (source:CvoyaNode)
                SET source.SearchText = $term
                CREATE (target:CvoyaNode)
                SET target.SearchText = 'Endpoint'
                CREATE (source)-[relationship:CvoyaRelationship]->(target)
                SET relationship.SearchText = $term
                RETURN true AS created
                """,
                new { term },
                ct);
            _ = await result.SingleAsync(ct);
            await transaction.CommitAsync();
        }

        Assert.Empty(await this.Graph.SearchNodes<DynamicNode>(term).ToListAsync(ct));
        Assert.Empty(await this.Graph.SearchRelationships<DynamicRelationship>(term).ToListAsync(ct));
        Assert.Empty(await this.Graph.Search(term).ToListAsync(ct));
    }

    [Fact]
    public async Task Search_DomainIdIsOrdinarySearchableData()
    {
        var ct = TestContext.Current.CancellationToken;
        const string searchableId = "SearchableDomainIdToken474";
        var node = new AtomicOrdinaryIdNode
        {
            Id = searchableId,
            Marker = "Ordinary",
        };
        await this.Graph.CreateNodeAsync(node, cancellationToken: ct);

        var result = Assert.Single(
            await this.Graph.SearchNodes<AtomicOrdinaryIdNode>(searchableId).ToListAsync(ct));

        Assert.Equal(searchableId, result.Id);
    }

    [Fact]
    public async Task GlobalSearch_TreatsARegisteredLabelWithNoIncludedPropertiesAsUnsearchable()
    {
        var ct = TestContext.Current.CancellationToken;
        const string term = "OptedOutStorageToken474";
        await this.Graph.CreateNodeAsync(new SearchOptedOutNode { Secret = term }, cancellationToken: ct);

        // The label owns a native table with no full-text candidate. Catalog discovery must not
        // mistake it for an externally managed label, whose contract is "every string value".
        Assert.Empty(await this.Graph.SearchNodes<SearchOptedOutNode>(term).ToListAsync(ct));
        Assert.Empty(await this.Graph.Search(term).ToListAsync(ct));
    }
}

/// <summary>
/// A registered node type that excludes every string property, including the ordinary <c>Id</c>, from
/// full-text search, so it contributes no phase-one candidate while still owning a native label
/// table.
/// </summary>
#pragma warning disable CG011 // This direct implementation verifies opt-out on an ordinary Id property.
[Node(Label = "SearchOptedOutNode")]
public record SearchOptedOutNode : INode
#pragma warning restore CG011
{
    [Property(IncludeInFullTextSearch = false)]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [Property(IncludeInFullTextSearch = false)]
    public string Secret { get; set; } = string.Empty;

    public IReadOnlyList<string> Labels { get; set; } = [];
}
