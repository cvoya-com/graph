// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Querying;

using System.Reflection;

internal static class LinqOperatorDispatch
{
    private static readonly Dictionary<MethodInfo, LinqOperator> Table = BuildTable();

    public static LinqOperator? Resolve(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);

        var key = method.IsGenericMethod ? method.GetGenericMethodDefinition() : method;
        return Table.TryGetValue(key, out var op) ? op : null;
    }

    public static IReadOnlyDictionary<MethodInfo, LinqOperator> AllRegisteredMethods => Table;

    private static Dictionary<MethodInfo, LinqOperator> BuildTable()
    {
        var table = new Dictionary<MethodInfo, LinqOperator>();

        void AddAll(Type declaringType, string name, LinqOperator op)
        {
            foreach (var candidate in declaringType
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                .Where(method => method.Name == name))
            {
                table[candidate] = op;
            }
        }

        AddAll(typeof(Queryable), nameof(Queryable.Where), LinqOperator.Where);
        AddAll(typeof(Queryable), nameof(Queryable.Select), LinqOperator.Select);
        AddAll(typeof(Queryable), nameof(Queryable.OrderBy), LinqOperator.OrderBy);
        AddAll(typeof(Queryable), nameof(Queryable.OrderByDescending), LinqOperator.OrderByDescending);
        AddAll(typeof(Queryable), nameof(Queryable.ThenBy), LinqOperator.ThenBy);
        AddAll(typeof(Queryable), nameof(Queryable.ThenByDescending), LinqOperator.ThenByDescending);
        AddAll(typeof(Queryable), nameof(Queryable.Take), LinqOperator.Take);
        AddAll(typeof(Queryable), nameof(Queryable.Skip), LinqOperator.Skip);
        AddAll(typeof(Queryable), nameof(Queryable.Distinct), LinqOperator.Distinct);
        AddAll(typeof(Queryable), nameof(Queryable.First), LinqOperator.First);
        AddAll(typeof(Queryable), nameof(Queryable.FirstOrDefault), LinqOperator.First);
        // The synchronous Queryable terminals must resolve by identity just like their async
        // marker counterparts; otherwise Queryable.Count()/LongCount() throw at translation time
        // even though the planner and both providers already implement TerminalOperation.Count.
        AddAll(typeof(Queryable), nameof(Queryable.Count), LinqOperator.Count);
        AddAll(typeof(Queryable), nameof(Queryable.LongCount), LinqOperator.Count);
        AddAll(typeof(Queryable), nameof(Queryable.Single), LinqOperator.Single);
        AddAll(typeof(Queryable), nameof(Queryable.SingleOrDefault), LinqOperator.Single);
        AddAll(typeof(Queryable), nameof(Queryable.Contains), LinqOperator.Contains);
        AddAll(typeof(Queryable), nameof(Queryable.SelectMany), LinqOperator.SelectMany);
        AddAll(typeof(Queryable), nameof(Queryable.GroupBy), LinqOperator.GroupBy);
        AddAll(typeof(Queryable), nameof(Queryable.Join), LinqOperator.Join);
        AddAll(typeof(Queryable), nameof(Queryable.Union), LinqOperator.Union);
        AddAll(typeof(Queryable), nameof(Queryable.Concat), LinqOperator.Concat);

        AddAll(typeof(GraphQueryableExtensions), nameof(GraphQueryableExtensions.Where), LinqOperator.Where);
        AddAll(typeof(GraphQueryableExtensions), nameof(GraphQueryableExtensions.Select), LinqOperator.Select);
        AddAll(typeof(GraphQueryableExtensions), nameof(GraphQueryableExtensions.SelectMany), LinqOperator.SelectMany);
        AddAll(typeof(GraphQueryableExtensions), nameof(GraphQueryableExtensions.OrderBy), LinqOperator.OrderBy);
        AddAll(typeof(GraphQueryableExtensions), nameof(GraphQueryableExtensions.OrderByDescending), LinqOperator.OrderByDescending);
        AddAll(typeof(GraphQueryableExtensions), nameof(GraphQueryableExtensions.ThenBy), LinqOperator.ThenBy);
        AddAll(typeof(GraphQueryableExtensions), nameof(GraphQueryableExtensions.ThenByDescending), LinqOperator.ThenByDescending);
        AddAll(typeof(GraphQueryableExtensions), nameof(GraphQueryableExtensions.Skip), LinqOperator.Skip);
        AddAll(typeof(GraphQueryableExtensions), nameof(GraphQueryableExtensions.Take), LinqOperator.Take);
        AddAll(typeof(GraphQueryableExtensions), nameof(GraphQueryableExtensions.Distinct), LinqOperator.Distinct);
        AddAll(typeof(GraphQueryableExtensions), nameof(GraphQueryableExtensions.GroupBy), LinqOperator.GroupBy);
        AddAll(typeof(GraphQueryableExtensions), nameof(GraphQueryableExtensions.Union), LinqOperator.Union);
        AddAll(typeof(GraphQueryableExtensions), nameof(GraphQueryableExtensions.Concat), LinqOperator.Concat);

        AddAll(typeof(QueryTerminals), nameof(QueryTerminals.ToListAsyncMarker), LinqOperator.ToListOrArray);
        AddAll(typeof(QueryTerminals), nameof(QueryTerminals.ToArrayAsyncMarker), LinqOperator.ToListOrArray);
        AddAll(typeof(QueryTerminals), nameof(QueryTerminals.FirstAsyncMarker), LinqOperator.First);
        AddAll(typeof(QueryTerminals), nameof(QueryTerminals.FirstOrDefaultAsyncMarker), LinqOperator.First);
        AddAll(typeof(QueryTerminals), nameof(QueryTerminals.SingleAsyncMarker), LinqOperator.Single);
        AddAll(typeof(QueryTerminals), nameof(QueryTerminals.SingleOrDefaultAsyncMarker), LinqOperator.Single);
        AddAll(typeof(QueryTerminals), nameof(QueryTerminals.LastAsyncMarker), LinqOperator.Last);
        AddAll(typeof(QueryTerminals), nameof(QueryTerminals.LastOrDefaultAsyncMarker), LinqOperator.Last);
        AddAll(typeof(QueryTerminals), nameof(QueryTerminals.AnyAsyncMarker), LinqOperator.Any);
        AddAll(typeof(QueryTerminals), nameof(QueryTerminals.AllAsyncMarker), LinqOperator.All);
        AddAll(typeof(QueryTerminals), nameof(QueryTerminals.CountAsyncMarker), LinqOperator.Count);
        AddAll(typeof(QueryTerminals), nameof(QueryTerminals.LongCountAsyncMarker), LinqOperator.Count);
        AddAll(typeof(QueryTerminals), nameof(QueryTerminals.SumAsyncMarker), LinqOperator.Sum);
        AddAll(typeof(QueryTerminals), nameof(QueryTerminals.AverageAsyncMarker), LinqOperator.Average);
        AddAll(typeof(QueryTerminals), nameof(QueryTerminals.MinAsyncMarker), LinqOperator.Min);
        AddAll(typeof(QueryTerminals), nameof(QueryTerminals.MaxAsyncMarker), LinqOperator.Max);
        AddAll(typeof(QueryTerminals), nameof(QueryTerminals.ContainsAsyncMarker), LinqOperator.Contains);
        AddAll(typeof(QueryTerminals), nameof(QueryTerminals.ElementAtAsyncMarker), LinqOperator.ElementAt);
        AddAll(typeof(QueryTerminals), nameof(QueryTerminals.ElementAtOrDefaultAsyncMarker), LinqOperator.ElementAtOrDefault);

        AddAll(typeof(QueryableAsyncExtensions), nameof(QueryableAsyncExtensions.SumAsync), LinqOperator.Sum);
        AddAll(typeof(QueryableAsyncExtensions), nameof(QueryableAsyncExtensions.AverageAsync), LinqOperator.Average);

        AddAll(typeof(GraphQueryableExtensions), nameof(GraphQueryableExtensions.Search), LinqOperator.Search);
        AddAll(typeof(GraphTraversalExtensions), nameof(GraphTraversalExtensions.PathSegments), LinqOperator.PathSegments);
        AddAll(typeof(GraphTraversalExtensions), nameof(GraphTraversalExtensions.TraversePaths), LinqOperator.TraversePaths);
        AddAll(typeof(GraphTraversalExtensions), nameof(GraphTraversalExtensions.ShortestPath), LinqOperator.ShortestPath);
        AddAll(typeof(GraphTraversalExtensions), nameof(GraphTraversalExtensions.AllShortestPaths), LinqOperator.AllShortestPaths);
        AddAll(typeof(GraphTraversalExtensions), nameof(GraphTraversalExtensions.OptionalTraverse), LinqOperator.OptionalTraverse);
        AddAll(typeof(GraphTraversalExtensions), "WithTraversalOptions", LinqOperator.TraversalOptions);
        AddAll(typeof(GraphTraversalExtensions), "WithRelationshipPredicate", LinqOperator.RelationshipPredicate);
        AddAll(typeof(GraphRelationshipPredicateExtensions), nameof(GraphRelationshipPredicateExtensions.WhereHasRelationship), LinqOperator.WhereHasRelationship);
        AddAll(typeof(GraphLabelExtensions), nameof(GraphLabelExtensions.OfLabel), LinqOperator.LabelFilter);
        AddAll(typeof(GraphLabelExtensions), nameof(GraphLabelExtensions.OfLabels), LinqOperator.LabelFilter);

        return table;
    }
}
