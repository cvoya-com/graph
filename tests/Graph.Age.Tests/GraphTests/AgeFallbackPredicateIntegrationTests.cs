// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using Cvoya.Graph.CompatibilityTests;

/// <summary>
/// Regression coverage for the containment fallback that sorts and pages a label hierarchy.
/// The provider renders the hierarchy predicate as <c>properties(n) @&gt; { inheritance_labels: [...] }</c>,
/// which keeps the filter on the GIN-indexed <c>properties</c> column. These tests pin the result
/// rows, including rows with missing properties and Unicode values, rather than the SQL plan.
/// </summary>
public sealed class AgeFallbackPredicateIntegrationTests(AgeHarness harness) : AgeTest(harness)
{
    [Node("Fallback Predicate Base")]
    public record FallbackPredicateBase : Node
    {
        public string Name { get; set; } = string.Empty;
        public string? Status { get; set; }
    }

    [Node("Fallback Predicate Child")]
    public sealed record FallbackPredicateChild : FallbackPredicateBase;

    [Fact]
    public async Task PagingWithPropertyFilter_HandlesHierarchyMissingPropertiesAndUnicode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        AgeGraphStore.RegisterLabelHierarchy("Fallback Predicate Base", "Fallback Predicate Child");

        FallbackPredicateBase[] rows =
        [
            new FallbackPredicateBase { Name = "Alpha", Status = "aktiv" },
            new FallbackPredicateChild { Name = "Bravo", Status = "aktiv" },
            new FallbackPredicateBase { Name = "Charlie", Status = "aktiv" },
            new FallbackPredicateChild { Name = "Müller-😀-東京", Status = "aktiv" },
            new FallbackPredicateBase { Name = "Delta", Status = null },
            new FallbackPredicateChild { Name = "Echo", Status = "inaktiv" },
        ];
        foreach (var row in rows)
        {
            await Graph.CreateNodeAsync(row, null, cancellationToken);
        }

        // OrderBy/Skip/Take forces the containment fallback; the page after the first active row
        // is Charlie and the Unicode child, so a child-table miss cannot hide behind the first row.
        var page = await Graph.Nodes<FallbackPredicateBase>()
            .Where(node => node.Status == "aktiv")
            .OrderBy(node => node.Name)
            .Skip(2)
            .Take(2)
            .ToListAsync(cancellationToken);

        Assert.Equal(["Charlie", "Müller-😀-東京"], page.Select(node => node.Name));

        // An exact Unicode value must survive the same predicate lowering and paging path.
        var unicodeMatch = await Graph.Nodes<FallbackPredicateBase>()
            .Where(node => node.Name == "Müller-😀-東京")
            .OrderBy(node => node.Name)
            .Take(1)
            .ToListAsync(cancellationToken);

        Assert.Equal(["Müller-😀-東京"], unicodeMatch.Select(node => node.Name));

        // Delta was created without Status at all; null filtering and paging must still see it.
        var missingProperty = await Graph.Nodes<FallbackPredicateBase>()
            .Where(node => node.Status == null)
            .OrderBy(node => node.Name)
            .Skip(0)
            .Take(1)
            .ToListAsync(cancellationToken);

        Assert.Equal(["Delta"], missingProperty.Select(node => node.Name));
    }
}
