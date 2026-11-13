// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using System.Linq.Expressions;
using System.Reflection;
using Cvoya.Graph.Age;
using Cvoya.Graph.Age.Querying;
using Cvoya.Graph.Age.Querying.Cypher.Visitors.Core;
using Cvoya.Graph.CompatibilityTests;

/// <summary>
/// Provider-free tests for the full-text expression rewriter's detection and rewrite halves. Phase 1
/// (the database call) is not exercised here; ids are supplied directly.
/// </summary>
public sealed class AgeFullTextSearchRewriterTests
{
    private static readonly MethodInfo SearchDefinition = typeof(GraphQueryableExtensions)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(GraphQueryableExtensions.Search) && m.IsGenericMethodDefinition);

    private static readonly MethodInfo PathSegmentsDefinition = typeof(GraphTraversalExtensions)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(GraphTraversalExtensions.PathSegments)
            && m.IsGenericMethodDefinition
            && m.GetParameters().Length == 1);

    private static MethodCallExpression Search<T>(Expression source, string query) =>
        Expression.Call(SearchDefinition.MakeGenericMethod(typeof(T)), source, Expression.Constant(query));

    private static ParameterExpression Root<T>() =>
        Expression.Parameter(typeof(IGraphQueryable<T>), "src");

    [Fact]
    public void LocateSearches_FindsSearchByMethodInfo_NotByName()
    {
        var search = Search<Person>(Root<Person>(), "cloud");

        var located = AgeFullTextSearchRewriter.LocateSearches(search);

        Assert.Single(located);
        Assert.Same(search, located[0]);
        Assert.True(AgeFullTextSearchRewriter.IsSearch(search));
    }

    [Fact]
    public void ApplyRewrite_ReplacesSearchWithPrivateGraphidPredicate_RecoveringElementType()
    {
        var root = Root<Person>();
        var search = Search<Person>(root, "cloud");
        var graphIds = new long[] { 101, 202 };

        var rewritten = AgeFullTextSearchRewriter.ApplyRewrite(
            search,
            new Dictionary<MethodCallExpression, long[]> { [search] = graphIds });

        // Search(source, "cloud") becomes Where(source, e => graphIds.Contains(NativeIdentity(e))).
        var where = Assert.IsAssignableFrom<MethodCallExpression>(rewritten);
        Assert.Equal(nameof(GraphQueryableExtensions.Where), where.Method.Name);
        Assert.Equal(typeof(Person), where.Method.GetGenericArguments()[0]);
        Assert.Same(root, where.Arguments[0]);

        var predicate = Assert.IsAssignableFrom<LambdaExpression>(StripQuote(where.Arguments[1]));
        Assert.Equal(typeof(Person), predicate.Parameters[0].Type);
        var contains = Assert.IsAssignableFrom<MethodCallExpression>(predicate.Body);
        Assert.Equal(nameof(Enumerable.Contains), contains.Method.Name);
        // The captured graphid set is the constant first argument; the value is an internal marker,
        // never a member access to IEntity.Id.
        var constant = Assert.IsAssignableFrom<ConstantExpression>(contains.Arguments[0]);
        Assert.Equal(graphIds, Assert.IsType<long[]>(constant.Value));
        var nativeIdentity = Assert.IsAssignableFrom<MethodCallExpression>(contains.Arguments[1]);
        Assert.Equal("NativeIdentity", nativeIdentity.Method.Name);
        Assert.Same(predicate.Parameters[0], nativeIdentity.Arguments[0]);
    }

    [Fact]
    public void ApplyRewrite_SearchAsTraversalSource_PreservesTraversalOverIdFilter()
    {
        var root = Root<Person>();
        var search = Search<Person>(root, "q");
        var traversal = Expression.Call(
            PathSegmentsDefinition.MakeGenericMethod(typeof(Person), typeof(KnowsWell), typeof(Person)),
            search);
        var graphIds = new long[] { 101, 202 };

        var rewritten = Assert.IsAssignableFrom<MethodCallExpression>(AgeFullTextSearchRewriter.ApplyRewrite(
            traversal,
            new Dictionary<MethodCallExpression, long[]> { [search] = graphIds }));

        Assert.Equal(nameof(GraphTraversalExtensions.PathSegments), rewritten.Method.Name);
        var where = Assert.IsAssignableFrom<MethodCallExpression>(rewritten.Arguments[0]);
        Assert.Equal(nameof(GraphQueryableExtensions.Where), where.Method.Name);
        Assert.Same(root, where.Arguments[0]);
    }

    [Fact]
    public async Task BuildGraphIdFilter_LowersToNativeIdentityWithoutPublicIdAccess()
    {
        await using var store = new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "translation");
        var source = store.Graph.Nodes<Person>();
        var expression = AgeFullTextSearchRewriter.BuildGraphIdFilter(
            source.Expression,
            typeof(Person),
            [101, 202]);
        var visitor = new CypherQueryVisitor(typeof(Person));

        visitor.Visit(expression);

        // AGE 1.8.0 segfaults on id(x) IN <list parameter>, so identity membership must render as a
        // filtered list comprehension and never as an IN with id() on the left.
        Assert.DoesNotContain("id(src) IN", visitor.Query.Text, StringComparison.Ordinal);
        Assert.Contains(
            "size([__cvoya_collection_item IN $",
            visitor.Query.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            "__cvoya_collection_item = id(src)",
            visitor.Query.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("src.Id IN", visitor.Query.Text, StringComparison.Ordinal);
    }

    private static Expression StripQuote(Expression expression) =>
        expression is UnaryExpression { NodeType: ExpressionType.Quote } quote ? quote.Operand : expression;
}
