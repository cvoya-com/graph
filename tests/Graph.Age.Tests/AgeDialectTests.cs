// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using Cvoya.Graph.Age.Querying.Cypher;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.Age.Querying.Cypher.Visitors.Core;
using Cvoya.Graph.CompatibilityTests;

public sealed class AgeDialectTests
{
    [Fact]
    public void DeclaresOnlyVerifiedCapabilities()
    {
        var capabilities = AgeDialect.Instance.Capabilities;

        Assert.Equal(
            CapabilitySet.Of(
                GraphCapability.FullTextSearch,
                GraphCapability.Transactions,
                GraphCapability.ComplexPropertyCascade,
                GraphCapability.MultiLabelMatch,
                GraphCapability.LabelFiltering,
                GraphCapability.OptionalTraversal,
                GraphCapability.CallSubqueries,
                GraphCapability.PatternSizeProjection,
                GraphCapability.GroupByAggregation,
                GraphCapability.RelationshipPredicates,
                GraphCapability.SetOperations,
                GraphCapability.NullElementsInSimpleCollections),
            capabilities);
    }

    [Fact]
    public async Task RejectsBareEntityOrderingWhileAllowingScalarProjectedOrdering()
    {
        await using var store = new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "translation");
        var entityQuery = store.Graph.Nodes<Person>().OrderBy(person => person);
        var entityVisitor = new CypherQueryVisitor(entityQuery.ElementType);

        var exception = Assert.Throws<GraphQueryTranslationException>(() =>
            entityVisitor.Visit(entityQuery.Expression));

        Assert.Contains("OrderByEntity", exception.Message, StringComparison.Ordinal);

        var scalarQuery = store.Graph.Nodes<Person>()
            .Select(person => person.FirstName)
            .Distinct()
            .OrderBy(name => name)
            .Take(2);
        var scalarVisitor = new CypherQueryVisitor(scalarQuery.ElementType);
        scalarVisitor.Visit(scalarQuery.Expression);
        Assert.Contains("ORDER BY", scalarVisitor.Query.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnsafeGraphNames()
    {
        Assert.Throws<ArgumentException>(() => new AgeGraphStore(
            "Host=localhost;Username=postgres;Password=postgres;Database=postgres",
            "graph'; DROP TABLE users; --"));
    }

    [Fact]
    public void RequiresExplicitConnectionCredentials()
    {
        Assert.Throws<InvalidOperationException>(() => new AgeGraphStore("   ", "safe_graph"));
    }

    [Fact]
    public void ChoosesDollarQuoteTagAbsentFromCypher()
    {
        var tag = AgeQueryRunner.ChooseDollarQuoteTag("RETURN '$cvoya_age_0$' AS text");

        Assert.Equal("$cvoya_age_1$", tag);
    }

    [Fact]
    public void PropertyAccessQuotesReservedKeywordsButLeavesOrdinaryNamesStable()
    {
        Assert.Equal("src.`match`", AgeDialect.Instance.RenderPropertyAccess("src", "match", false));
        Assert.Equal("src.Name", AgeDialect.Instance.RenderPropertyAccess("src", "Name", false));
    }

    [Fact]
    public void RendersLabelHierarchyAsGinFriendlyMapContainment()
    {
        var rendered = AgeDialect.Instance.RenderLabelTest(
            "src",
            ["Vertrag", "Müller-😀-東京"],
            value => $"'{value}'");

        Assert.Equal(
            "(('Vertrag' IN labels(src) OR properties(src) @> { inheritance_labels: ['Vertrag'] }) OR " +
            "('Müller-😀-東京' IN labels(src) OR properties(src) @> { inheritance_labels: ['Müller-😀-東京'] }))",
            rendered);
        Assert.DoesNotContain("coalesce", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotesOnlyValidatedSqlProjectionColumns()
    {
        Assert.Equal("\"safe_column\"", AgeSqlIdentifier.Quote("safe_column", "projection column"));
        Assert.Throws<ArgumentException>(() => AgeSqlIdentifier.Quote("value agtype); DROP TABLE users; --", "projection column"));
    }

    [Fact]
    public void RenderCollectionContains_NeverPutsNativeIdentityOnTheLeftOfIn()
    {
        var rendered = AgeDialect.Instance.RenderCollectionContains("$graphIds", "id(src)");

        Assert.DoesNotContain("id(src) IN", rendered, StringComparison.Ordinal);
        Assert.StartsWith("size([__cvoya_collection_item IN $graphIds WHERE ", rendered, StringComparison.Ordinal);
        Assert.Contains("__cvoya_collection_item = id(src)", rendered, StringComparison.Ordinal);
        // toString() cannot be applied to a native graph id.
        Assert.DoesNotContain("toString(id(src))", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderCollectionContains_KeepsNullAwareRenderingForStoredCollections()
    {
        var rendered = AgeDialect.Instance.RenderCollectionContains("src.Tags", "src.Name");

        Assert.Contains("__cvoya_collection_item = src.Name", rendered, StringComparison.Ordinal);
        // AGE 1.7 needs the toString() null test for agtype null parameters; keep the shared form.
        Assert.Contains(
            "toString(__cvoya_collection_item) IS NULL AND toString(src.Name) IS NULL",
            rendered,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RenderCollectionContains_UsesNativeAnyPredicateForStoredCollectionsOnAge18()
    {
        var dialect = AgeDialect.ForServer(
            new AgeServerCapabilities(new Version(1, 8, 0), AgeFeature.NativePredicateFunctions));

        var identity = dialect.RenderCollectionContains("$graphIds", "id(src)");
        var stored = dialect.RenderCollectionContains("src.Tags", "src.Name");

        // AGE 1.8's any() transform mis-types a graphid comparison, so identity keeps the list
        // form even though the predicate functions are available.
        Assert.StartsWith("size([__cvoya_collection_item IN $graphIds WHERE ", identity, StringComparison.Ordinal);
        Assert.DoesNotContain("any(", identity, StringComparison.Ordinal);
        Assert.Contains("__cvoya_collection_item = id(src)", identity, StringComparison.Ordinal);
        Assert.StartsWith("any(__cvoya_collection_item IN src.Tags WHERE ", stored, StringComparison.Ordinal);
        Assert.Contains(
            "toString(__cvoya_collection_item) IS NULL AND toString(src.Name) IS NULL",
            stored,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RenderCollectionContains_KeepsListComprehensionForAge17()
    {
        var dialect = AgeDialect.ForServer(
            new AgeServerCapabilities(new Version(1, 7, 0), AgeFeature.None));

        var identity = dialect.RenderCollectionContains("$graphIds", "id(src)");

        Assert.StartsWith("size([__cvoya_collection_item IN $graphIds WHERE ", identity, StringComparison.Ordinal);
        Assert.DoesNotContain("any(", identity, StringComparison.Ordinal);
    }
}
