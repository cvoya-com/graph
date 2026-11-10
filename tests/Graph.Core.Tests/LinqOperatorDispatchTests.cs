// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Core.Tests;

using System.Reflection;
using Cvoya.Graph.Querying;

[Trait("Area", "QueryModelBuilder")]
public class LinqOperatorDispatchTests
{
    [Fact]
    public void EveryOperatorIsRegisteredByAtLeastOneMethod()
    {
        var registered = LinqOperatorDispatch.AllRegisteredMethods.Values.ToHashSet();
        var missing = Enum.GetValues<LinqOperator>().Where(value => !registered.Contains(value)).ToList();

        Assert.True(missing.Count == 0, $"Unregistered LINQ operators: {string.Join(", ", missing)}.");
    }

    [Fact]
    public void EveryRegisteredMethodResolvesByMethodIdentity()
    {
        foreach (var registration in LinqOperatorDispatch.AllRegisteredMethods)
        {
            Assert.Equal(registration.Value, LinqOperatorDispatch.Resolve(registration.Key));
        }
    }

    [Theory]
    [InlineData(nameof(Queryable.Count))]
    [InlineData(nameof(Queryable.LongCount))]
    public void SynchronousQueryableCountOperators_ResolveToCount(string methodName)
    {
        var method = typeof(Queryable)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(candidate => candidate.Name == methodName && candidate.IsGenericMethodDefinition)
            .Where(candidate => candidate.GetParameters().Length == 1)
            .Single()
            .MakeGenericMethod(typeof(object));

        Assert.Equal(LinqOperator.Count, LinqOperatorDispatch.Resolve(method));
    }

    [Fact]
    public void SameNamedUnknownMethodDoesNotResolve()
    {
        var method = typeof(UnrelatedOperators).GetMethod(nameof(UnrelatedOperators.Where))!;

        Assert.Null(LinqOperatorDispatch.Resolve(method));
    }

    private static class UnrelatedOperators
    {
        public static void Where()
        {
        }
    }
}
