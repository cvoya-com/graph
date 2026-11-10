// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying.Cypher.Lowering;

using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;
using Cvoya.Graph.Cypher.Validation;

/// <summary>Normalizes every leaf projection in an AGE set-operation tree to one rendered shape.</summary>
internal sealed class AgeSetOperationProjectionPass : ICypherPass
{
    public CypherStatement Run(CypherStatement input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var changed = false;
        var clauses = RewriteContainers(input.Clauses, ref changed);
        return changed
            ? new CypherStatement(clauses, input.Parameters, input.PathTypes)
            {
                PostProcessing = input.PostProcessing,
            }
            : input;
    }

    private static ICypherClause[] RewriteContainers(
        IReadOnlyList<ICypherClause> clauses,
        ref bool changed)
    {
        var rewritten = clauses.ToArray();
        for (var index = 0; index < rewritten.Length; index++)
        {
            if (rewritten[index] is not SetOperationClause setOperation)
            {
                continue;
            }

            var first = RewriteContainers(setOperation.First, ref changed);
            var second = RewriteContainers(setOperation.Second, ref changed);
            var aliases = GetProjectionAliases(first);
            ValidateProjectionWidth(second, aliases.Length);
            first = ApplyProjectionAliases(first, aliases, ref changed);
            second = ApplyProjectionAliases(second, aliases, ref changed);
            rewritten[index] = new SetOperationClause(first, second, setOperation.PreserveDuplicates);
            changed = true;
        }

        return rewritten;
    }

    private static string[] GetProjectionAliases(IReadOnlyList<ICypherClause> clauses)
    {
        var projection = FindFirstLeafProjection(clauses);
        return projection.Items
            .Select((item, index) => item.Alias ??
                (item.Expression as VariableRef)?.Alias ??
                $"age_column_{index}")
            .ToArray();
    }

    private static void ValidateProjectionWidth(IReadOnlyList<ICypherClause> clauses, int expected)
    {
        var actual = FindFirstLeafProjection(clauses).Items.Count;
        if (actual != expected)
        {
            throw new GraphQueryTranslationException(
                $"Apache AGE set-operation branches must project the same number of values; " +
                $"the first branch projects {expected} and the second projects {actual}.");
        }
    }

    private static ReturnClause FindFirstLeafProjection(IReadOnlyList<ICypherClause> clauses)
    {
        for (var index = clauses.Count - 1; index >= 0; index--)
        {
            switch (clauses[index])
            {
                case ReturnClause projection:
                    return projection;
                case SetOperationClause setOperation:
                    return FindFirstLeafProjection(setOperation.First);
            }
        }

        throw new GraphQueryTranslationException(
            "Apache AGE set-operation lowering requires every branch to end in a RETURN projection.");
    }

    private static ICypherClause[] ApplyProjectionAliases(
        IReadOnlyList<ICypherClause> clauses,
        IReadOnlyList<string> aliases,
        ref bool changed)
    {
        var rewritten = clauses.ToArray();
        for (var index = rewritten.Length - 1; index >= 0; index--)
        {
            switch (rewritten[index])
            {
                case ReturnClause projection:
                    if (projection.Items.Count != aliases.Count)
                    {
                        throw new GraphQueryTranslationException(
                            "Apache AGE set-operation branches must project the same number of values.");
                    }

                    var items = projection.Items
                        .Select((item, itemIndex) =>
                        {
                            // A variable that already renders as the target alias needs no
                            // explicit alias. Keeping the original item also keeps a grouped
                            // key projection readable instead of emitting "__key AS __key".
                            if (item.Alias is null &&
                                item.Expression is VariableRef variable &&
                                string.Equals(variable.Alias, aliases[itemIndex], StringComparison.Ordinal))
                            {
                                return item;
                            }

                            return new ReturnItem(item.Expression, aliases[itemIndex]);
                        })
                        .ToArray();
                    changed |= items.Where((item, itemIndex) =>
                        !string.Equals(item.Alias, projection.Items[itemIndex].Alias, StringComparison.Ordinal)).Any();
                    rewritten[index] = new ReturnClause(items, projection.Distinct);
                    return rewritten;

                case SetOperationClause setOperation:
                    var first = ApplyProjectionAliases(setOperation.First, aliases, ref changed);
                    var second = ApplyProjectionAliases(setOperation.Second, aliases, ref changed);
                    rewritten[index] = new SetOperationClause(
                        first,
                        second,
                        setOperation.PreserveDuplicates);
                    return rewritten;
            }
        }

        throw new GraphQueryTranslationException(
            "Apache AGE set-operation lowering requires every branch to end in a RETURN projection.");
    }
}
