// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using Cvoya.Graph.Age.Querying;
using Cvoya.Graph.Age.Querying.Cypher;
using Cvoya.Graph.Age.Querying.Cypher.Lowering;
using Cvoya.Graph.Cypher;
using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;

public sealed class AgeLabelPatternPassTests
{
    private readonly AgeLabelPatternPass pass = new();
    private readonly AgeUnionAllExpansionPass expansion = new();
    private readonly CypherRenderer renderer = new(AgeDialect.Instance);

    [Fact]
    public void ExpandsAlternativeLabelsIntoUnionAllBranches()
    {
        var statement = Statement(
            Match(
                optional: false,
                new NodePattern("src", ["AlternativePerson", "AlternativeManager"]),
                Relationship("knows", ["AlternativeKnows", "AlternativeWorksWith"]),
                new NodePattern("tgt", [])),
            Return("src"));

        var rendered = renderer.Render(expansion.Run(pass.Run(statement)));

        Assert.Contains("MATCH (src:AlternativePerson)-[knows]->(tgt)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (src:AlternativeManager)-[knows]->(tgt)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("UNION ALL", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("AlternativePerson|AlternativeManager", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("'AlternativePerson' IN coalesce(src.inheritance_labels", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("'AlternativeManager' IN coalesce(src.inheritance_labels", rendered.Text, StringComparison.Ordinal);

        Assert.Contains("type(knows) = 'AlternativeKnows'", rendered.Text, StringComparison.Ordinal);
        Assert.Contains(
            "properties(knows) @> { inheritance_labels: ['AlternativeKnows'] }",
            rendered.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("coalesce(knows.inheritance_labels", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("knows.__graphModelComplexProperty", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandsSingleLabelWithRegisteredSubtypesIntoUnionAllBranches()
    {
        AgeTypeHierarchyRegistry.Instance.Register("HierarchyContract", "HierarchyKaufvertrag");
        AgeTypeHierarchyRegistry.Instance.Register("HierarchyKaufvertrag", "HierarchyMieteGewerbe");

        var statement = Statement(
            Match(optional: false, new NodePattern("v", ["HierarchyContract"])),
            Return("v"));

        var rendered = renderer.Render(expansion.Run(pass.Run(statement)));

        Assert.Contains("MATCH (v:HierarchyContract)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (v:HierarchyKaufvertrag)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (v:HierarchyMieteGewerbe)", rendered.Text, StringComparison.Ordinal);
        Assert.Equal(2, rendered.Text.Split("UNION ALL", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("'HierarchyContract' IN coalesce(v.inheritance_labels", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsLeafLabelInMatchWithoutHierarchyPredicate()
    {
        var statement = Statement(
            Match(optional: false, new NodePattern("src", ["LeafOnlyLabel"])),
            Return("src"));

        var rendered = renderer.Render(pass.Run(statement));

        Assert.Contains("MATCH (src:LeafOnlyLabel)", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("inheritance_labels", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("'LeafOnlyLabel' IN labels(src)", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FallsBackToContainmentWhenTwoNodesCannotShareOneUnion()
    {
        var statement = Statement(
            new MatchClause(
                [
                    new PathPattern([new NodePattern("a", ["FallbackA", "FallbackB"])]),
                    new PathPattern([new NodePattern("b", ["FallbackC", "FallbackD"])]),
                ],
                optional: false),
            Return("a"));

        var rendered = renderer.Render(expansion.Run(pass.Run(statement)));

        Assert.DoesNotContain("UNION ALL", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (a), (b)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains(
            "properties(a) @> { inheritance_labels: ['FallbackA'] }",
            rendered.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            "properties(b) @> { inheritance_labels: ['FallbackC'] }",
            rendered.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("'FallbackA' IN labels(a)", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandsGlobalCountIntoOneDisjointCountBranchPerPhysicalLabel()
    {
        AgeTypeHierarchyRegistry.Instance.Register("CountContract", "CountKaufvertrag");

        var statement = Statement(
            Match(optional: false, new NodePattern("v", ["CountContract"])),
            new ReturnClause(
                [new ReturnItem(new FunctionCall("count", [new VariableRef("v")]), null)],
                distinct: false));

        var rendered = renderer.Render(expansion.Run(pass.Run(statement)));

        // Each AGE vertex lives in exactly one physical label table, so the per-label counts are
        // disjoint and the provider may sum them into one value without double counting.
        Assert.Contains("MATCH (v:CountContract)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (v:CountKaufvertrag)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("RETURN count(v)", rendered.Text, StringComparison.Ordinal);
        Assert.Equal(1, rendered.Text.Split("UNION ALL", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("inheritance_labels", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FallsBackToContainmentForGroupedAggregation()
    {
        AgeTypeHierarchyRegistry.Instance.Register("GroupedContract", "GroupedKaufvertrag");

        var statement = Statement(
            Match(optional: false, new NodePattern("src", ["GroupedContract"])),
            new WithClause(
                [
                    new ReturnItem(new PropertyAccess(new VariableRef("src"), "Number"), "__key"),
                    new ReturnItem(new FunctionCall("count", [new VariableRef("src")]), "__a0"),
                ],
                distinct: false),
            Return("__key"));

        var rendered = renderer.Render(expansion.Run(pass.Run(statement)));

        // A grouping key can occur in more than one label table; expanding first would emit one
        // group per branch instead of one group for the whole hierarchy.
        Assert.DoesNotContain("UNION ALL", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (src)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains(
            "properties(src) @> { inheritance_labels: ['GroupedContract'] }",
            rendered.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "properties(src) @> { inheritance_labels: ['GroupedKaufvertrag'] }",
            rendered.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("'GroupedContract' IN labels(src)", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("coalesce(src.inheritance_labels", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("count(src) AS __a0", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FallsBackToContainmentInsideOptionalMatch()
    {
        var statement = Statement(
            Match(optional: true, new NodePattern("src", ["OptionalA", "OptionalB"])),
            Return("src"));

        var rendered = renderer.Render(expansion.Run(pass.Run(statement)));

        Assert.DoesNotContain("UNION ALL", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (src)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains(
            "properties(src) @> { inheritance_labels: ['OptionalA'] }",
            rendered.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("'OptionalA' IN labels(src)", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CollapsesPlannerExpandedHierarchyToTheQueriedRoot()
    {
        AgeTypeHierarchyRegistry.Instance.Register("CollapseRoot", "CollapseChild");
        AgeTypeHierarchyRegistry.Instance.Register("CollapseChild", "CollapseLeaf");

        // The planner represents Nodes<CollapseRoot>() as the queried label plus every compatible
        // subtype. An OPTIONAL MATCH keeps the containment fallback, so the fallback must use one
        // containment term for the queried root instead of one GIN scan per subtype.
        var statement = Statement(
            Match(optional: true, new NodePattern(
                "src",
                ["CollapseRoot", "CollapseChild", "CollapseLeaf"])),
            Return("src"));

        var rendered = renderer.Render(pass.Run(statement));

        Assert.Contains(
            "properties(src) @> { inheritance_labels: ['CollapseRoot'] }",
            rendered.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("inheritance_labels: ['CollapseChild']", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("inheritance_labels: ['CollapseLeaf']", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("'CollapseRoot' IN labels(src)", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsGenuineLabelAlternativesInTheContainmentFallback()
    {
        var statement = Statement(
            Match(optional: true, new NodePattern("src", ["UnrelatedA", "UnrelatedB"])),
            Return("src"));

        var rendered = renderer.Render(pass.Run(statement));

        Assert.Contains(
            "properties(src) @> { inheritance_labels: ['UnrelatedA'] }",
            rendered.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            "properties(src) @> { inheritance_labels: ['UnrelatedB'] }",
            rendered.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratesRelationshipAliasesInPatternOrder()
    {
        var statement = Statement(
            Match(
                optional: false,
                new NodePattern("src", []),
                Relationship(alias: null, ["Knows", "WorksWith"]),
                new NodePattern("mid", []),
                Relationship(alias: null, ["Likes", "Avoids"]),
                new NodePattern("tgt", [])),
            Return("tgt"));

        var lowered = pass.Run(statement);
        var rendered = renderer.Render(lowered);

        Assert.Contains("MATCH (src)-[age_relationship_0]->(mid)-[age_relationship_1]->(tgt)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("type(age_relationship_0) = 'Knows'", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("type(age_relationship_1) = 'Likes'", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ChecksOnlyFirstHopForOptionalVariableLengthRelationships()
    {
        var statement = Statement(
            Match(
                optional: true,
                new NodePattern("src", []),
                Relationship(alias: null, ["Knows", "WorksWith"], new DepthRange(1, 4)),
                new NodePattern("tgt", [])),
            Return("tgt"));

        var rendered = renderer.Render(pass.Run(statement));

        Assert.Contains("type(age_relationship_0[toInteger(0)]) = 'Knows'", rendered.Text, StringComparison.Ordinal);
        Assert.Contains(
            "properties(age_relationship_0[toInteger(0)]) @> { inheritance_labels: ['Knows'] }",
            rendered.Text,
            StringComparison.Ordinal);
        Assert.Contains("age_relationship_0[toInteger(0)].__graphModelComplexProperty", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ChecksEveryHopForRequiredVariableLengthRelationships()
    {
        var statement = Statement(
            Match(
                optional: false,
                new NodePattern("src", []),
                Relationship("relationships", ["Knows", "WorksWith"], new DepthRange(1, 4)),
                new NodePattern("tgt", [])),
            Return("tgt"));

        var lowered = pass.Run(statement);
        var rendered = renderer.Render(lowered);

        Assert.Contains("type(relationships[toInteger(age_hop)]) = 'Knows'", rendered.Text, StringComparison.Ordinal);
        Assert.Contains(
            "properties(relationships[toInteger(age_hop)]) @> { inheritance_labels: ['Knows'] }",
            rendered.Text,
            StringComparison.Ordinal);
        Assert.Contains("relationships[toInteger(age_hop)].__graphModelComplexProperty", rendered.Text, StringComparison.Ordinal);
        _ = Assert.IsType<WhereClause>(lowered.Clauses[1]);
        Assert.Contains("size([age_hop IN range", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MergesExistingPredicatesAndKeepsConsecutiveMatchClausesScoped()
    {
        var statement = Statement(
            Match(
                optional: false,
                new NodePattern("src", ["ScopedA", "ScopedB"])),
            new WhereClause(new BinaryExpression(
                CypherBinaryOperator.Equal,
                new PropertyAccess(new VariableRef("src"), "Active"),
                new Literal(true))),
            Match(
                optional: false,
                new NodePattern("other", ["ScopedC", "ScopedD"])),
            Return("other"));

        var rendered = renderer.Render(pass.Run(statement));

        // Two expandable nodes fall back to the containment predicate.
        Assert.Contains("MATCH (src)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains(
            "properties(src) @> { inheritance_labels: ['ScopedA'] }",
            rendered.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("'ScopedA' IN labels(src)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("src.Active = true", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (other)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains(
            "properties(other) @> { inheritance_labels: ['ScopedD'] }",
            rendered.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LowersMatchClausesInsideCallSubqueries()
    {
        var statement = Statement(
            Match(
                optional: false,
                new NodePattern("src", [])),
            new CallSubqueryClause(
                ["src"],
                [
                    Match(
                        optional: false,
                        new NodePattern("src", []),
                        Relationship("knows", ["Knows", "WorksWith"]),
                        new NodePattern("tgt", ["SubqueryA", "SubqueryB"])),
                    Return("tgt"),
                ]),
            Return("tgt"));

        var rendered = renderer.Render(pass.Run(statement));

        Assert.Contains("CALL {", rendered.Text, StringComparison.Ordinal);
        Assert.Contains(
            "properties(tgt) @> { inheritance_labels: ['SubqueryA'] }",
            rendered.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            "properties(tgt) @> { inheritance_labels: ['SubqueryB'] }",
            rendered.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("'SubqueryA' IN labels(tgt)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("type(knows) = 'Knows'", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsSingleLeafLabelInMatch()
    {
        var statement = Statement(
            Match(
                optional: false,
                new NodePattern("src", ["LeafOnlyLabel"])),
            Return("src"));

        var rendered = renderer.Render(pass.Run(statement));

        Assert.Contains("MATCH (src:LeafOnlyLabel)", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("inheritance_labels", rendered.Text, StringComparison.Ordinal);
        // The physical label table cannot contain provider value nodes, so no per-row
        // root-isolation subquery is needed.
        Assert.DoesNotContain("__graphModelComplexProperty", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectsPredicatesAcrossCommaSeparatedPatternsInOneMatch()
    {
        var statement = Statement(
            new MatchClause(
                [
                    new PathPattern([new NodePattern("a", ["CommaPerson"])]),
                    new PathPattern([new NodePattern("b", ["CommaCompany"])]),
                ],
                optional: false),
            Return("a"));

        var rendered = renderer.Render(pass.Run(statement));

        // Two leaf labels have no union to build; each keeps its native label in MATCH.
        Assert.Contains("MATCH (a:CommaPerson), (b:CommaCompany)", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("inheritance_labels", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcludesInternalComplexElementsWhenNoLabelsOrTypesExist()
    {
        var statement = Statement(
            Match(
                optional: false,
                new NodePattern("src", []),
                Relationship("knows", []),
                new NodePattern("tgt", [])),
            Return("tgt"));

        var rendered = renderer.Render(pass.Run(statement));

        Assert.DoesNotContain("age_owner_relationship_0.__graphModelComplexProperty", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("knows.__graphModelComplexProperty", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ComplexPropertyNavigation_AcceptsOwnedAndCollidingDomainRelationships()
    {
        var statement = Statement(
            Match(
                optional: false,
                new NodePattern("src", []),
                Relationship(
                    "primary",
                    ["ContractPrimaryAddress"],
                    isComplexProperty: true),
                new NodePattern("address", ["ContractAddressValue"])),
            Return("src"));

        var rendered = renderer.Render(pass.Run(statement));

        Assert.Contains("type(primary) = 'ContractPrimaryAddress'", rendered.Text, StringComparison.Ordinal);
        Assert.Contains(
            "properties(primary) @> { inheritance_labels: ['ContractPrimaryAddress'] }",
            rendered.Text,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            rendered.Text.Split(
                "primary.__graphModelComplexProperty",
                StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void ComplexPropertyNavigationWithoutLogicalType_StaysMarkerIsolated()
    {
        var statement = Statement(
            Match(
                optional: false,
                new NodePattern("src", []),
                Relationship(
                    "primary",
                    [],
                    isComplexProperty: true),
                new NodePattern("address", [])),
            Return("src"));

        var rendered = renderer.Render(pass.Run(statement));

        Assert.Contains(
            "coalesce(primary.__graphModelComplexProperty, false) = true",
            rendered.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("type(primary) <> 'CvoyaRelationship'", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandsPagingThroughUnionAllAndLiftsOrderingAndPaging()
    {
        AgeTypeHierarchyRegistry.Instance.Register("PagingContract", "PagingKaufvertrag");
        AgeTypeHierarchyRegistry.Instance.Register("PagingKaufvertrag", "PagingMiete");

        var statement = Statement(
            Match(optional: false, new NodePattern("src", ["PagingContract"])),
            new ReturnClause(
                [new ReturnItem(new VariableRef("src"), "Node")],
                distinct: false),
            new OrderByClause(
                [new OrderByItem(new PropertyAccess(new VariableRef("src"), "Name"), descending: false)]),
            new SkipClause(new Literal(1_000)),
            new LimitClause(new Literal(100)));

        var lowered = expansion.Run(pass.Run(statement));
        var rendered = renderer.Render(lowered);

        // The label tables of the whole hierarchy are opened directly, but the global sort and
        // page cannot live after a union in AGE, so they are lifted to the SQL wrapper.
        Assert.Contains("MATCH (src:PagingContract)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (src:PagingKaufvertrag)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (src:PagingMiete)", rendered.Text, StringComparison.Ordinal);
        Assert.Equal(2, rendered.Text.Split("UNION ALL", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("'PagingContract' IN labels(src)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains(
            "RETURN src AS Node, src.Name AS __age_union_sort0",
            rendered.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SKIP 1000", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("LIMIT 100", rendered.Text, StringComparison.Ordinal);

        var postProcessing = Assert.IsType<CypherPostProcessing>(lowered.PostProcessing);
        Assert.Equal(1, postProcessing.ResultColumnCount);
        Assert.Equal(1_000, postProcessing.Skip);
        Assert.Equal(100, postProcessing.Limit);
        var sort = Assert.Single(postProcessing.Sort);
        Assert.Equal(1, sort.ColumnIndex);
        Assert.False(sort.Descending);
        Assert.Null(postProcessing.Aggregation);
    }

    [Fact]
    public void ExpandsDistinctAndDeduplicatesInWrapper()
    {
        AgeTypeHierarchyRegistry.Instance.Register("DistinctContract", "DistinctKaufvertrag");

        var statement = Statement(
            Match(optional: false, new NodePattern("src", ["DistinctContract"])),
            new ReturnClause(
                [new ReturnItem(new PropertyAccess(new VariableRef("src"), "Status"), "Status")],
                distinct: true));

        var lowered = expansion.Run(pass.Run(statement));
        var rendered = renderer.Render(lowered);

        Assert.Contains("UNION ALL", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\nUNION\n", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("RETURN DISTINCT", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (src:DistinctContract)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (src:DistinctKaufvertrag)", rendered.Text, StringComparison.Ordinal);
        var postProcessing = Assert.IsType<CypherPostProcessing>(lowered.PostProcessing);
        Assert.True(postProcessing.Distinct);
    }

    [Fact]
    public void CombinesAdditiveAggregatesAcrossBranches()
    {
        AgeTypeHierarchyRegistry.Instance.Register("AggregateContract", "AggregateKaufvertrag");

        var statement = Statement(
            Match(optional: false, new NodePattern("src", ["AggregateContract"])),
            new WithClause(
                [
                    new ReturnItem(new PropertyAccess(new VariableRef("src"), "Status"), "__key"),
                    new ReturnItem(new FunctionCall("count", [new VariableRef("src")]), "__a0"),
                ],
                distinct: false),
            new ReturnClause(
                [
                    new ReturnItem(new VariableRef("__key"), "Key"),
                    new ReturnItem(new VariableRef("__a0"), "C"),
                ],
                distinct: false));

        var lowered = expansion.Run(pass.Run(statement));
        var rendered = renderer.Render(lowered);

        Assert.Contains("UNION ALL", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (src:AggregateContract)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (src:AggregateKaufvertrag)", rendered.Text, StringComparison.Ordinal);

        var postProcessing = Assert.IsType<CypherPostProcessing>(lowered.PostProcessing);
        var aggregation = Assert.IsType<CypherPostAggregation>(postProcessing.Aggregation);
        Assert.Equal([0], aggregation.GroupColumnIndexes);
        var aggregate = Assert.Single(aggregation.Aggregates);
        Assert.Equal(1, aggregate.ColumnIndex);
        Assert.Equal("sum", aggregate.Function);
    }

    [Fact]
    public void KeepsFallbackForAggregatesWithoutLosslessMerge()
    {
        AgeTypeHierarchyRegistry.Instance.Register("AverageContract", "AverageKaufvertrag");

        var statement = Statement(
            Match(optional: false, new NodePattern("src", ["AverageContract"])),
            new WithClause(
                [
                    new ReturnItem(new PropertyAccess(new VariableRef("src"), "Status"), "__key"),
                    new ReturnItem(new FunctionCall("avg", [new VariableRef("src")]), "__a0"),
                ],
                distinct: false),
            new ReturnClause(
                [
                    new ReturnItem(new VariableRef("__key"), "Key"),
                    new ReturnItem(new VariableRef("__a0"), "C"),
                ],
                distinct: false));

        var rendered = renderer.Render(expansion.Run(pass.Run(statement)));

        Assert.DoesNotContain("UNION ALL", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (src)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains(
            "properties(src) @> { inheritance_labels: ['AverageContract'] }",
            rendered.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OmitsOwnerIsolationWhenSchemaHasNoComplexProperties()
    {
        var statement = Statement(
            new MatchClause(
                [
                    new PathPattern([new NodePattern("a", ["NoComplexA1", "NoComplexA2"])]),
                    new PathPattern([new NodePattern("b", ["NoComplexB1", "NoComplexB2"])]),
                ],
                optional: false),
            Return("a"));

        var withoutComplexProperties = new AgeLabelPatternPass(hasComplexProperties: false);
        var rendered = renderer.Render(withoutComplexProperties.Run(statement));

        Assert.DoesNotContain("age_owner_", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("__graphModelComplexProperty", rendered.Text, StringComparison.Ordinal);
        Assert.Contains(
            "properties(a) @> { inheritance_labels: ['NoComplexA1'] }",
            rendered.Text,
            StringComparison.Ordinal);

        var withComplexProperties = renderer.Render(pass.Run(statement));
        Assert.Contains("age_owner_", withComplexProperties.Text, StringComparison.Ordinal);
    }

    private static CypherStatement Statement(params ICypherClause[] clauses) =>
        new(clauses, new Dictionary<string, object?>(StringComparer.Ordinal));

    private static MatchClause Match(bool optional, params PatternElement[] elements) =>
        new([new PathPattern(elements)], optional);

    private static RelationshipPattern Relationship(
        string? alias,
        IReadOnlyList<string> types,
        DepthRange? depth = null,
        bool isComplexProperty = false) =>
        new(alias, CypherDirection.Outgoing, depth, types, isComplexProperty);

    private static ReturnClause Return(string alias) =>
        new([new ReturnItem(new VariableRef(alias), null)], distinct: false);
}
