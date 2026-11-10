// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using Cvoya.Graph.Age.Querying.Cypher.Visitors.Core;
using Cvoya.Graph.CompatibilityTests;

public sealed class AgeLabelFilterTranslationTests
{
    [Fact]
    public async Task RendersAnyAndAllLabelValuesWithoutIdentifierInterpolation()
    {
        await using var store = new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "translation");
        var query = store.Graph.Nodes<Person>()
            .OfLabels(GraphLabelMatch.Any, "Contractor", "Freelancer")
            .OfLabels(GraphLabelMatch.All, "Active", "Verified");

        var visitor = new CypherQueryVisitor(query.ElementType);
        visitor.Visit(query.Expression);
        var text = visitor.Query.Text;

        Assert.Contains("'Contractor' IN labels(src)", text, StringComparison.Ordinal);
        Assert.Contains("'Freelancer' IN labels(src)", text, StringComparison.Ordinal);
        Assert.Contains("'Active' IN labels(src)", text, StringComparison.Ordinal);
        Assert.Contains("'Verified' IN labels(src)", text, StringComparison.Ordinal);
        Assert.Contains(
            "properties(src) @> { inheritance_labels: ['Contractor'] }",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("coalesce(src.inheritance_labels", text, StringComparison.Ordinal);
        Assert.DoesNotContain(":Contractor", text, StringComparison.Ordinal);
        Assert.DoesNotContain(":Freelancer", text, StringComparison.Ordinal);
    }
}
