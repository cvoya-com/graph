// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using Cvoya.Graph.Age.Core;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.CompatibilityTests;

/// <summary>
/// Execution-level semantics guards for label-hierarchy expansion versus the containment
/// fallback. Every case seeds the same five row kinds — a provider base row, a provider CLR
/// derived row, a runtime-registered child row, a raw native base row without CVOYA metadata,
/// and a raw native derived row without metadata — and asserts the query result, so divergences
/// between the UNION ALL expansion and the fallback are caught locally.
/// </summary>
public sealed class AgeHierarchySemanticsTests(AgeHarness harness)
    : AgeTest(harness, StoreIsolation.FreshStore)
{
    [Node("Sem582Base")]
    public record Sem582Base : Node
    {
        public string TestKey { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = string.Empty;

        public string? Status { get; set; }
    }

    [Node("Sem582Derived")]
    public sealed record Sem582Derived : Sem582Base;

    private async Task<string> SeedAsync(CancellationToken cancellationToken)
    {
        var marker = $"sem582-{Guid.NewGuid():N}";
        var runtimeLabel = $"Sem582Runtime_{Guid.NewGuid():N}";
        AgeGraphStore.RegisterLabelHierarchy("Sem582Base", runtimeLabel);

        await Graph.CreateNodeAsync(
            new Sem582Base { TestKey = marker, Name = "base", Status = "x" },
            cancellationToken: cancellationToken);
        await Graph.CreateNodeAsync(
            new Sem582Derived { TestKey = marker, Name = "derived", Status = "y" },
            cancellationToken: cancellationToken);
        await RunRawAsync(
            $"""
            CREATE (runtime:{runtimeLabel})
            SET runtime.TestKey = $marker, runtime.Name = 'runtime', runtime.Status = 'x',
                runtime.inheritance_labels = ['Sem582Base', '{runtimeLabel}']
            CREATE (rawBase:Sem582Base)
            SET rawBase.TestKey = $marker, rawBase.Name = 'rawbase', rawBase.Status = 'y'
            CREATE (rawDerived:Sem582Derived)
            SET rawDerived.TestKey = $marker, rawDerived.Name = 'rawderived', rawDerived.Status = 'x'
            RETURN true AS created
            """,
            new { marker },
            cancellationToken);
        return marker;
    }

    [Fact]
    public async Task BaseQuery_CountsEveryRowKind()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var marker = await SeedAsync(cancellationToken);

        var count = await Graph.Nodes<Sem582Base>()
            .Where(node => node.TestKey == marker)
            .CountAsync(cancellationToken);

        Assert.Equal(5, count);
    }

    [Fact]
    public async Task UnionOfHierarchyQuery_PreservesDistinctScope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var marker = await SeedAsync(cancellationToken);

        var query = Graph.Nodes<Sem582Base>()
            .Where(node => node.TestKey == marker)
            .Select(node => node.Name);
        var names = await query.Union(query).ToListAsync(cancellationToken);

        Assert.Equal(
            ["base", "derived", "rawbase", "rawderived", "runtime"],
            names.OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ConcatOfPagedHierarchyBranches_ExecutesWithoutProjectionMismatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var marker = await SeedAsync(cancellationToken);

        var branch = Graph.Nodes<Sem582Base>()
            .Where(node => node.TestKey == marker)
            .OrderBy(node => node.Name)
            .Take(2)
            .Select(node => node.Name);
        var names = await branch.Concat(branch).ToListAsync(cancellationToken);

        Assert.Equal(4, names.Count);
        Assert.Equal(["base", "base", "derived", "derived"], names.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task GroupBy_WithKey_ReturnsPerGroupCounts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var marker = await SeedAsync(cancellationToken);

        var groups = await Graph.Nodes<Sem582Base>()
            .Where(node => node.TestKey == marker)
            .GroupBy(node => node.Status)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        Assert.Equal(2, groups.Count);
        Assert.Equal(3, groups.Single(group => group.Key == "x").Count);
        Assert.Equal(2, groups.Single(group => group.Key == "y").Count);
    }

    [Fact]
    public async Task GroupBy_WithoutKeyInProjection_ReturnsPerGroupCountsNotOneMergedRow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var marker = await SeedAsync(cancellationToken);

        // LINQ semantics: one count per group. A merged expansion would wrongly return [5].
        var totals = await Graph.Nodes<Sem582Base>()
            .Where(node => node.TestKey == marker)
            .GroupBy(node => node.Status)
            .Select(group => group.Count())
            .ToListAsync(cancellationToken);

        Assert.Equal([2, 3], totals.OrderBy(count => count).ToArray());
    }

    [Fact]
    public async Task GroupBy_WithCollidingOutputAlias_ReturnsPerGroupRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var marker = await SeedAsync(cancellationToken);

        // The output alias collides with the planner's grouping-key alias; the result must still
        // be one row per group, not a single merged row.
        var rows = await Graph.Nodes<Sem582Base>()
            .Where(node => node.TestKey == marker)
            .GroupBy(node => node.Status)
            .Select(group => new { __key = group.Count() })
            .ToListAsync(cancellationToken);

        Assert.Equal(2, rows.Count);
        Assert.Equal([2, 3], rows.Select(row => row.__key).OrderBy(count => count).ToArray());
    }

    private async Task RunRawAsync(string cypher, object parameters, CancellationToken cancellationToken)
    {
        await using var transaction = await Graph.GetTransactionAsync(cancellationToken);
        await using var result = await ((AgeGraphTransaction)transaction).Runner
            .RunAsync(cypher, parameters, cancellationToken);
        _ = await result.SingleAsync(cancellationToken);
        await transaction.CommitAsync();
    }
}
