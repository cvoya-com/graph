// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

using Cvoya.Graph;
using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;

namespace Cvoya.Graph.Cypher.Validation;

/// <summary>
/// Validates basic Cypher AST well-formedness.
/// </summary>
public sealed class CypherAstValidator : ICypherPass
{
    /// <inheritdoc />
    public CypherStatement Run(CypherStatement input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var scope = new HashSet<string>(StringComparer.Ordinal);
        ValidateClauses(input.Clauses, scope, input.Parameters);
        return input;
    }

    private static void ValidateClauses(
        IReadOnlyList<ICypherClause> clauses,
        HashSet<string> scope,
        IReadOnlyDictionary<string, object?> parameters)
    {
        foreach (var clause in clauses)
        {
            switch (clause)
            {
                case SetOperationClause setOperation:
                    ValidateClauses(
                        setOperation.First,
                        new HashSet<string>(StringComparer.Ordinal),
                        parameters);
                    ValidateClauses(
                        setOperation.Second,
                        new HashSet<string>(StringComparer.Ordinal),
                        parameters);
                    break;

                case CallSubqueryClause subquery:
                    foreach (var imported in subquery.ImportedVariables)
                    {
                        ValidateAlias(imported, scope);
                    }

                    if (subquery.Body[^1] is not ReturnClause subReturn)
                    {
                        throw new GraphException("A scoped CALL subquery must end with a RETURN clause.");
                    }

                    var innerScope = new HashSet<string>(subquery.ImportedVariables, StringComparer.Ordinal);
                    ValidateClauses(subquery.Body, innerScope, parameters);
                    foreach (var item in subReturn.Items)
                    {
                        var exported = item.Alias ?? (item.Expression as VariableRef)?.Alias;
                        if (exported is null)
                        {
                            continue;
                        }

                        if (!scope.Add(exported))
                        {
                            throw new GraphException(
                                $"Scoped CALL subquery output '{exported}' conflicts with an outer-scope variable.");
                        }
                    }

                    break;

                case MatchClause match:
                    BindMatchAliases(scope, match);
                    break;

                case WhereClause where:
                    ValidateExpression(where.Predicate, scope, parameters);
                    break;

                case WithClause with:
                    ValidateReturnItems(with.Items, scope, parameters);
                    if (!with.Wildcard)
                    {
                        scope = ProjectWithScope(with);
                    }

                    break;

                case UnwindClause unwind:
                    ValidateExpression(unwind.Source, scope, parameters);
                    scope.Add(unwind.Alias);
                    break;

                case CallClause call:
                    ValidateExpressions(call.Arguments, scope, parameters);
                    foreach (var yield in call.Yields)
                    {
                        scope.Add(yield.Alias ?? yield.Name);
                    }

                    break;

                case FullTextSearchClause search:
                    ValidateParameter(search.Query.Name, parameters);
                    scope.Add(search.Alias);
                    if (search.Target == Cvoya.Graph.Querying.SearchRootTarget.Relationships)
                    {
                        scope.Add("src");
                        scope.Add("tgt");
                    }

                    break;

                case EntityProjectionClause projection:
                    ValidateAlias(projection.SourceAlias, scope);
                    if (projection.RelationshipAlias is { } relationshipAlias)
                    {
                        ValidateAlias(relationshipAlias, scope);
                    }

                    if (projection.TargetAlias is { } targetAlias)
                    {
                        ValidateAlias(targetAlias, scope);
                    }

                    foreach (var rowIdentityAlias in projection.RowIdentityAliases)
                    {
                        ValidateAlias(rowIdentityAlias, scope);
                    }

                    foreach (var item in projection.Ordering)
                    {
                        ValidateExpression(item.Expression, scope, parameters);
                    }

                    break;

                case ReturnClause @return:
                    ValidateReturnItems(@return.Items, scope, parameters);
                    break;

                case SetClause set:
                    foreach (var item in set.Items)
                    {
                        if (item.Target is not PropertyAccess and not EscapedPropertyAccess and not PhysicalPropertyAccess)
                        {
                            throw new GraphException("A Cypher SET target must be a property access.");
                        }

                        ValidateExpression(item.Target, scope, parameters);
                        ValidateExpression(item.Value, scope, parameters);
                    }

                    break;

                case DeleteClause delete:
                    foreach (var target in delete.Targets)
                    {
                        ValidateAlias(target.Alias, scope);
                    }

                    break;

                case OrderByClause orderBy:
                    foreach (var item in orderBy.Items)
                    {
                        ValidateExpression(item.Expression, scope, parameters);
                    }

                    break;

                case SkipClause skip:
                    ValidateExpression(skip.Count, scope, parameters);
                    break;

                case LimitClause limit:
                    ValidateExpression(limit.Count, scope, parameters);
                    break;

                default:
                    throw new GraphException($"Unsupported Cypher clause '{clause.GetType().Name}'.");
            }
        }
    }

    private static void BindMatchAliases(HashSet<string> scope, MatchClause match)
    {
        foreach (var pattern in match.Patterns)
        {
            if (pattern.Alias is not null)
            {
                scope.Add(pattern.Alias);
            }

            foreach (var element in pattern.Elements)
            {
                switch (element)
                {
                    case NodePattern { Alias: not null } node:
                        scope.Add(node.Alias);
                        break;

                    case RelationshipPattern { Alias: not null } relationship:
                        scope.Add(relationship.Alias);
                        break;
                }
            }
        }
    }

    private static HashSet<string> ProjectWithScope(WithClause with)
    {
        var scope = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in with.Items)
        {
            if (item.Alias is not null)
            {
                scope.Add(item.Alias);
            }
            else if (item.Expression is VariableRef variable)
            {
                scope.Add(variable.Alias);
            }
        }

        return scope;
    }

    private static void ValidateReturnItems(
        IReadOnlyList<ReturnItem> items,
        IReadOnlySet<string> scope,
        IReadOnlyDictionary<string, object?> parameters)
    {
        foreach (var item in items)
        {
            ValidateExpression(item.Expression, scope, parameters);
        }
    }

    private static void ValidateExpressions(
        IReadOnlyList<CypherExpression> expressions,
        IReadOnlySet<string> scope,
        IReadOnlyDictionary<string, object?> parameters)
    {
        foreach (var expression in expressions)
        {
            ValidateExpression(expression, scope, parameters);
        }
    }

    private static void ValidateExpression(
        CypherExpression expression,
        IReadOnlySet<string> scope,
        IReadOnlyDictionary<string, object?> parameters)
    {
        switch (expression)
        {
            case VariableRef variable:
                ValidateAlias(variable.Alias, scope);
                break;

            case PropertyAccess property:
                ValidateExpression(property.Target, scope, parameters);
                break;

            case EscapedPropertyAccess property:
                ValidateExpression(property.Target, scope, parameters);
                break;

            case PhysicalPropertyAccess property:
                ValidateExpression(property.Target, scope, parameters);
                break;

            case CollectionPropertyAccess property:
                ValidateExpression(property.Target, scope, parameters);
                break;

            case CollectionContainsExpression contains:
                ValidateExpression(contains.Collection, scope, parameters);
                ValidateExpression(contains.Item, scope, parameters);
                break;

            case NativeElementIdentity identity:
                ValidateExpression(identity.Target, scope, parameters);
                break;

            case CastExpression cast:
                ValidateExpression(cast.Operand, scope, parameters);
                break;

            case QueryParameter parameter:
                ValidateParameter(parameter.Name, parameters);
                break;

            case FunctionCall function:
                ValidateExpressions(function.Arguments, scope, parameters);
                break;

            case BinaryExpression binary:
                ValidateExpression(binary.Left, scope, parameters);
                ValidateExpression(binary.Right, scope, parameters);
                break;

            case UnaryExpression unary:
                ValidateExpression(unary.Operand, scope, parameters);
                break;

            case LabelTest label:
                ValidateExpression(label.Target, scope, parameters);
                break;

            case ListExpression list:
                ValidateExpressions(list.Items, scope, parameters);
                break;

            case ListComprehensionExpression comprehension:
                {
                    ValidateExpression(comprehension.Source, scope, parameters);
                    var comprehensionScope = new HashSet<string>(scope, StringComparer.Ordinal)
                    {
                        comprehension.IteratorAlias,
                    };
                    if (comprehension.Predicate is not null)
                    {
                        ValidateExpression(comprehension.Predicate, comprehensionScope, parameters);
                    }

                    if (comprehension.Projection is not null)
                    {
                        ValidateExpression(comprehension.Projection, comprehensionScope, parameters);
                    }

                    break;
                }

            case ReduceExpression reduce:
                {
                    ValidateExpression(reduce.Seed, scope, parameters);
                    ValidateExpression(reduce.Source, scope, parameters);
                    var reduceScope = new HashSet<string>(scope, StringComparer.Ordinal)
                    {
                        reduce.AccumulatorAlias,
                        reduce.IteratorAlias,
                    };
                    ValidateExpression(reduce.Reducer, reduceScope, parameters);
                    break;
                }

            case AllExpression all:
                {
                    ValidateExpression(all.Source, scope, parameters);
                    var allScope = new HashSet<string>(scope, StringComparer.Ordinal)
                    {
                        all.IteratorAlias,
                    };
                    ValidateExpression(all.Predicate, allScope, parameters);
                    break;
                }

            case MapExpression map:
                ValidateExpressions(map.Entries.Select(entry => entry.Value).ToArray(), scope, parameters);
                break;

            case EntityProjectionExpression entity:
                ValidateAlias(entity.Alias, scope);
                break;

            case IndexExpression index:
                ValidateExpression(index.Target, scope, parameters);
                ValidateExpression(index.Index, scope, parameters);
                break;

            case CaseExpression @case:
                ValidateExpression(@case.Condition, scope, parameters);
                ValidateExpression(@case.WhenTrue, scope, parameters);
                if (@case.WhenFalse is not null)
                {
                    ValidateExpression(@case.WhenFalse, scope, parameters);
                }
                break;

            case ConjunctionExpression conjunction:
                ValidateExpressions(conjunction.Predicates, scope, parameters);
                break;

            case PatternSubqueryExpression subquery:
                {
                    var subqueryScope = new HashSet<string>(scope, StringComparer.Ordinal);
                    BindPatternAliases(subqueryScope, subquery.Pattern);
                    if (subquery.Predicate is not null)
                    {
                        ValidateExpression(subquery.Predicate, subqueryScope, parameters);
                    }

                    break;
                }

            case PatternComprehensionExpression comprehension:
                {
                    var comprehensionScope = new HashSet<string>(scope, StringComparer.Ordinal);
                    BindPatternAliases(comprehensionScope, comprehension.Pattern);
                    if (comprehension.Predicate is not null)
                    {
                        ValidateExpression(comprehension.Predicate, comprehensionScope, parameters);
                    }

                    ValidateExpression(comprehension.Projection, comprehensionScope, parameters);
                    break;
                }

            case Literal:
                break;

            default:
                throw new GraphException($"Unsupported Cypher expression '{expression.GetType().Name}'.");
        }
    }

    private static void BindPatternAliases(HashSet<string> scope, PathPattern pattern)
    {
        if (pattern.Alias is not null)
        {
            scope.Add(pattern.Alias);
        }

        foreach (var element in pattern.Elements)
        {
            switch (element)
            {
                case NodePattern { Alias: not null } node:
                    scope.Add(node.Alias);
                    break;
                case RelationshipPattern { Alias: not null } relationship:
                    scope.Add(relationship.Alias);
                    break;
            }
        }
    }

    private static void ValidateAlias(string alias, IReadOnlySet<string> scope)
    {
        if (!scope.Contains(alias))
        {
            throw new GraphException($"Cypher variable '{alias}' is not bound by a preceding clause.");
        }
    }

    private static void ValidateParameter(string name, IReadOnlyDictionary<string, object?> parameters)
    {
        if (!parameters.ContainsKey(name))
        {
            throw new GraphException($"Cypher parameter '{name}' is not defined in the statement parameters.");
        }
    }
}
