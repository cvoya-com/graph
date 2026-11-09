// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Neo4j.Translation.Tests;

using System.Linq.Expressions;
using System.Reflection;
using Cvoya.Graph;
using Cvoya.Graph.Neo4j.Translation.Tests.Harness;
using Cvoya.Graph.Neo4j.Translation.Tests.Model;

/// <summary>
/// Covers the "marker method" terminal LINQ operations (First/Single/Last/Any/All/Count/
/// LongCount/Sum/Average/Min/Max/Contains/ElementAt). These are represented in the expression
/// tree via internal marker methods (e.g. <c>FirstAsyncMarker</c>) that
/// <c>CypherQueryVisitor.HandleLinqMethod</c> dispatches on - see
/// <see cref="MarkerExpressions"/> for why they're built directly rather than via the public
/// async extension methods (which execute eagerly).
/// </summary>
public class TerminalOperationsTranslationTests : TranslationTestBase
{
    [Fact]
    public Task ToListAsync_MaterializesResults()
    {
        var source = Root.Nodes<Person>();
        var expr = MarkerExpressions.Call<Person>("ToListAsyncMarker", source.Expression);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task ToArrayAsync_MaterializesResults()
    {
        var source = Root.Nodes<Person>();
        var expr = MarkerExpressions.Call<Person>("ToArrayAsyncMarker", source.Expression);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task First_NoPredicate()
    {
        var source = Root.Nodes<Person>();
        var expr = MarkerExpressions.Call<Person>("FirstAsyncMarker", source.Expression);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task FirstOrDefault_NoPredicate()
    {
        var source = Root.Nodes<Person>();
        var expr = MarkerExpressions.Call<Person>("FirstOrDefaultAsyncMarker", source.Expression);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task Single_NoPredicate()
    {
        var source = Root.Nodes<Person>();
        var expr = MarkerExpressions.Call<Person>("SingleAsyncMarker", source.Expression);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task SingleOrDefault_NoPredicate()
    {
        var source = Root.Nodes<Person>();
        var expr = MarkerExpressions.Call<Person>("SingleOrDefaultAsyncMarker", source.Expression);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task Last_NoPredicate()
    {
        var source = Root.Nodes<Person>().OrderBy(p => p.LastName);
        var expr = MarkerExpressions.Call<Person>("LastAsyncMarker", source.Expression);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task LastOrDefault_NoPredicate()
    {
        var source = Root.Nodes<Person>().OrderBy(p => p.LastName);
        var expr = MarkerExpressions.Call<Person>("LastOrDefaultAsyncMarker", source.Expression);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Theory]
    [InlineData("LastAsyncMarker")]
    [InlineData("LastOrDefaultAsyncMarker")]
    public void LastTerminals_WithoutOrderBy_ThrowGraphException(string markerName)
    {
        var source = Root.Nodes<Person>();
        var expr = MarkerExpressions.Call<Person>(markerName, source.Expression);

        var exception = Assert.Throws<GraphException>(() => CypherTranslator.Translate(typeof(Person), expr));

        Assert.Contains("OrderBy", exception.Message);
    }

    [Fact]
    public Task Any_NoPredicate()
    {
        var source = Root.Nodes<Person>();
        var expr = MarkerExpressions.Call<Person>("AnyAsyncMarker", source.Expression);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task All_WithPredicate()
    {
        var source = Root.Nodes<Person>();
        Expression<Func<Person, bool>> predicate = p => p.Age >= 18;
        var expr = MarkerExpressions.Call<Person>("AllAsyncMarker", source.Expression, predicate);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task All_AfterScalarProjection_RewritesPredicateToSourceExpression()
    {
        var source = Root.Nodes<Person>().Select(p => p.Age);
        Expression<Func<int, bool>> predicate = age => age >= 18;
        var expr = MarkerExpressions.Call<int>("AllAsyncMarker", source.Expression, predicate);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public async Task All_WithNullPredicate_ThrowsArgumentNullException()
    {
        var source = Root.Nodes<Person>();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            source.AllAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public Task Count_NoPredicate()
    {
        var source = Root.Nodes<Person>();
        var expr = MarkerExpressions.Call<Person>("CountAsyncMarker", source.Expression);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task OrderByThenCount_DropsOrderingFromAggregate()
    {
        var source = Root.Nodes<Person>().OrderBy(p => p.LastName);
        var expr = MarkerExpressions.Call<Person>("CountAsyncMarker", source.Expression);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task OrderByTakeThenCount_PreservesOrderingInPipedWith()
    {
        var source = Root.Nodes<Person>().OrderBy(p => p.LastName).Take(5);
        var expr = MarkerExpressions.Call<Person>("CountAsyncMarker", source.Expression);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task TakeThenCount_PaginationPipedBeforeAggregate()
    {
        var source = Root.Nodes<Person>().Take(5);
        var expr = MarkerExpressions.Call<Person>("CountAsyncMarker", source.Expression);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task SkipThenCount_PaginationPipedBeforeAggregate()
    {
        var source = Root.Nodes<Person>().Skip(5);
        var expr = MarkerExpressions.Call<Person>("CountAsyncMarker", source.Expression);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task TakeThenSum_PaginationPipedBeforeAggregate()
    {
        var source = Root.Nodes<Person>().Take(5);
        Expression<Func<Person, int>> selector = p => p.Age;
        var expr = MarkerExpressions.Call<Person>("SumAsyncMarker", source.Expression, selector);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task SkipThenSum_PaginationPipedBeforeAggregate()
    {
        var source = Root.Nodes<Person>().Skip(5);
        Expression<Func<Person, int>> selector = p => p.Age;
        var expr = MarkerExpressions.Call<Person>("SumAsyncMarker", source.Expression, selector);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task TakeThenAverage_PaginationPipedBeforeAggregate()
    {
        var source = Root.Nodes<Person>().Take(5);
        Expression<Func<Person, int>> selector = p => p.Age;
        var expr = MarkerExpressions.Call<Person>("AverageAsyncMarker", source.Expression, selector);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task SkipThenAverage_PaginationPipedBeforeAggregate()
    {
        var source = Root.Nodes<Person>().Skip(5);
        Expression<Func<Person, int>> selector = p => p.Age;
        var expr = MarkerExpressions.Call<Person>("AverageAsyncMarker", source.Expression, selector);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task TakeThenMin_PaginationPipedBeforeAggregate()
    {
        var source = Root.Nodes<Person>().Take(5);
        Expression<Func<Person, int>> selector = p => p.Age;
        var expr = MarkerExpressions.Call<Person>("MinAsyncMarker", source.Expression, selector);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task SkipThenMin_PaginationPipedBeforeAggregate()
    {
        var source = Root.Nodes<Person>().Skip(5);
        Expression<Func<Person, int>> selector = p => p.Age;
        var expr = MarkerExpressions.Call<Person>("MinAsyncMarker", source.Expression, selector);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task TakeThenMax_PaginationPipedBeforeAggregate()
    {
        var source = Root.Nodes<Person>().Take(5);
        Expression<Func<Person, int>> selector = p => p.Age;
        var expr = MarkerExpressions.Call<Person>("MaxAsyncMarker", source.Expression, selector);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task SkipThenMax_PaginationPipedBeforeAggregate()
    {
        var source = Root.Nodes<Person>().Skip(5);
        Expression<Func<Person, int>> selector = p => p.Age;
        var expr = MarkerExpressions.Call<Person>("MaxAsyncMarker", source.Expression, selector);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task SynchronousCount_NoPredicate()
    {
        var source = Root.Nodes<Person>();
        var expr = QueryableTerminalCall(source, nameof(Queryable.Count));
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task SynchronousLongCount_NoPredicate()
    {
        var source = Root.Nodes<Person>();
        var expr = QueryableTerminalCall(source, nameof(Queryable.LongCount));
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task SynchronousCount_WithPredicate()
    {
        var source = Root.Nodes<Person>();
        Expression<Func<Person, bool>> predicate = p => p.Age > 18;
        var expr = QueryableTerminalCall(source, nameof(Queryable.Count), Expression.Quote(predicate));
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task Count_WithPredicate()
    {
        var source = Root.Nodes<Person>();
        Expression<Func<Person, bool>> predicate = p => p.Age > 18;
        var expr = MarkerExpressions.Call<Person>("CountAsyncMarker", source.Expression, predicate);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task TakeThenCount_WithPredicate_ThrowsInsteadOfReordering()
    {
        var source = Root.Nodes<Person>().OrderBy(p => p.Age).Take(3);
        Expression<Func<Person, bool>> predicate = p => p.Age >= 3;
        var expr = MarkerExpressions.Call<Person>("CountAsyncMarker", source.Expression, predicate);
        return VerifyTranslationThrows(typeof(Person), expr);
    }

    [Fact]
    public Task TakeThenWhereThenCount_PipesPredicateAfterPagingBeforeAggregate()
    {
        var source = Root.Nodes<Person>()
            .OrderBy(p => p.Age)
            .Take(3)
            .Where(p => p.Age >= 3);
        var expr = MarkerExpressions.Call<Person>("CountAsyncMarker", source.Expression);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task LongCount_NoPredicate()
    {
        var source = Root.Nodes<Person>();
        var expr = MarkerExpressions.Call<Person>("LongCountAsyncMarker", source.Expression);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task Sum_WithSelector()
    {
        var source = Root.Nodes<Person>();
        Expression<Func<Person, int>> selector = p => p.Age;
        var expr = MarkerExpressions.Call<Person>("SumAsyncMarker", source.Expression, selector);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task Average_WithSelector()
    {
        var source = Root.Nodes<Person>();
        Expression<Func<Person, int>> selector = p => p.Age;
        var expr = MarkerExpressions.Call<Person>("AverageAsyncMarker", source.Expression, selector);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task Min_WithSelector()
    {
        var source = Root.Nodes<Person>();
        Expression<Func<Person, int>> selector = p => p.Age;
        var expr = MarkerExpressions.Call<Person>("MinAsyncMarker", source.Expression, selector);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task Max_WithSelector()
    {
        var source = Root.Nodes<Person>();
        Expression<Func<Person, int>> selector = p => p.Age;
        var expr = MarkerExpressions.Call<Person>("MaxAsyncMarker", source.Expression, selector);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task OrderByThenMax_DropsOrderingFromAggregate()
    {
        var source = Root.Nodes<Person>().OrderBy(p => p.LastName);
        Expression<Func<Person, int>> selector = p => p.Age;
        var expr = MarkerExpressions.Call<Person>("MaxAsyncMarker", source.Expression, selector);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public async Task MinMaxAsync_NonNullableSelectors_ExecuteSingleAggregateQuery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await AssertSingleAggregateExecutionAsync(
            "MinAsyncMarker",
            source => source.MinAsync(p => p.Age, cancellationToken));

        await AssertSingleAggregateExecutionAsync(
            "MaxAsyncMarker",
            source => source.MaxAsync(p => p.Age, cancellationToken));
    }

    [Fact]
    public Task Contains_OnScalarProjection()
    {
        var source = Root.Nodes<Person>().Select(p => p.FirstName);
        var expr = MarkerExpressions.Call<string>("ContainsAsyncMarker", source.Expression, Expression.Constant("Alice"));
        return VerifyTranslation(typeof(string), expr);
    }

    [Fact]
    public Task Contains_OnScalarProjection_NullItem()
    {
        var source = Root.Nodes<Person>().Select(p => p.FirstName);
        var expr = MarkerExpressions.Call<string>("ContainsAsyncMarker", source.Expression, Expression.Constant(null, typeof(string)));
        return VerifyTranslation(typeof(string), expr);
    }

    [Fact]
    public Task Contains_OnConcreteNodeQueryableConstant()
    {
        var source = Root.ConcreteNodeQueryableConstant<Person>().Select(p => p.FirstName);
        var expr = MarkerExpressions.Call<string>("ContainsAsyncMarker", source.Expression, Expression.Constant("Alice"));
        return VerifyTranslation(typeof(string), expr);
    }

    [Fact]
    public Task ElementAt_ByIndex()
    {
        var source = Root.Nodes<Person>().OrderBy(p => p.LastName);
        var expr = MarkerExpressions.Call<Person>("ElementAtAsyncMarker", source.Expression, Expression.Constant(3));
        return VerifyTranslation(typeof(Person), expr);
    }

    /// <summary>
    /// The ElementAt index is a first-class terminal operand (#210); it supersedes explicit
    /// paging operators rather than composing with them, matching the pre-existing behavior of
    /// the paging-based encoding it replaced.
    /// </summary>
    [Fact]
    public Task ElementAt_AfterSkip_IndexSupersedesPaging()
    {
        var source = Root.Nodes<Person>().OrderBy(p => p.LastName).Skip(2);
        var expr = MarkerExpressions.Call<Person>("ElementAtAsyncMarker", source.Expression, Expression.Constant(3));
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task DistinctThenCount_ProjectsDistinctBeforeAggregate()
    {
        var source = Root.Nodes<Person>().Select(p => p.LastName).Distinct();
        var expr = MarkerExpressions.Call<string>("CountAsyncMarker", source.Expression);
        return VerifyTranslation(typeof(string), expr);
    }

    [Fact]
    public Task DistinctIdentityThenCount_PipesDistinctRows()
    {
        var source = Root.Nodes<Person>().Distinct();
        var expr = MarkerExpressions.Call<Person>("CountAsyncMarker", source.Expression);
        return VerifyTranslation(typeof(Person), expr);
    }

    [Fact]
    public Task ElementAtOrDefault_ByIndex()
    {
        var source = Root.Nodes<Person>().OrderBy(p => p.LastName);
        var expr = MarkerExpressions.Call<Person>("ElementAtOrDefaultAsyncMarker", source.Expression, Expression.Constant(3));
        return VerifyTranslation(typeof(Person), expr);
    }

    private static async Task AssertSingleAggregateExecutionAsync(
        string expectedMarkerName,
        Func<IGraphQueryable<Person>, Task<int>> execute)
    {
        var provider = new FakeGraphQueryProvider(allowExecution: true);
        provider.ExecutionResult = 10;
        var source = CreateExecutableRoot(provider);

        var result = await execute(source);

        Assert.Equal(10, result);
        var expression = Assert.Single(provider.ExecutedExpressions);
        var methodCall = Assert.IsAssignableFrom<MethodCallExpression>(expression);
        Assert.Equal(expectedMarkerName, methodCall.Method.Name);
        Assert.DoesNotContain(provider.ExecutedExpressions.OfType<MethodCallExpression>(), call => call.Method.Name == "AnyAsyncMarker");
    }

    private static FakeGraphNodeQueryable<Person> CreateExecutableRoot(FakeGraphQueryProvider provider)
    {
        var placeholder = new FakeGraphNodeQueryable<Person>(provider, Expression.Constant(null, typeof(Expression)));
        var expression = Expression.Constant(placeholder, typeof(IGraphQueryable<Person>));
        return new FakeGraphNodeQueryable<Person>(provider, expression);
    }

    /// <summary>
    /// Builds the real synchronous <see cref="Queryable"/> terminal call so dispatch is exercised by
    /// method identity rather than through the internal async marker stand-ins.
    /// </summary>
    private static MethodCallExpression QueryableTerminalCall(
        IQueryable source,
        string methodName,
        params Expression[] additionalArguments)
    {
        var method = typeof(Queryable)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(candidate => candidate.Name == methodName && candidate.IsGenericMethodDefinition)
            .Where(candidate => candidate.GetParameters().Length == additionalArguments.Length + 1)
            .Single()
            .MakeGenericMethod(source.ElementType);
        return Expression.Call(method, [source.Expression, .. additionalArguments]);
    }
}
