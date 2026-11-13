// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying.Cypher.Lowering;

using System.Globalization;
using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;
using Cvoya.Graph.Cypher.Validation;

/// <summary>
/// Rewrites native-id membership tests so AGE 1.8 never receives
/// <c>id(alias) IN &lt;agtype list parameter&gt;</c>.
/// </summary>
/// <remarks>
/// AGE 1.8.0 returns <c>graphid</c> from <c>id()</c> while a list parameter is <c>agtype</c>;
/// mixing the two in <c>IN</c> segfaults in the <c>IN</c> transformation. Casting the identity to
/// <c>agtype</c> makes both operands the same type, keeps the native list comparison, and avoids
/// the <c>XX000: not a common type</c> failure of a mixed literal list. A casted <c>IN</c> is a
/// portable hash-shaped membership that stays under the multi-alias target; the alternative
/// row-preserving comprehension is O(rows x ids).
/// <para>
/// AGE 1.7 supports the native <c>id(alias) IN &lt;list&gt;</c> form without the cast, so the pass
/// is disabled when the connected server reports a pre-1.8 AGE extension. The store detects the
/// extension version once and switches the lowering off; an undetected version keeps the pass
/// enabled so an unknown server can never receive a shape that is known to crash 1.8.
/// </para>
/// <para>
/// A positive top-level conjunct is rewritten to one <c>UNWIND</c> per distinct
/// <c>(parameter, native alias)</c> pair plus <c>id(alias) = variable</c>. The alias is keyed by
/// both the parameter and the native element, so two native aliases filtered by the same parameter
/// remain independent instead of being forced to the same id. Inserting the <c>UNWIND</c> before
/// the owning <c>MATCH</c> keeps the id equality adjacent to the label pattern so the planner still
/// uses the native id lookup. For a predicate attached to a <c>WITH</c> the <c>UNWIND</c> is
/// followed by <c>WITH *</c>, which keeps the unwound variable in scope after the projection.
/// </para>
/// <para>
/// Stacking one <c>UNWIND</c> per alias would make the row source <c>|ids|^aliases</c>. When a
/// predicate needs more than one distinct <c>(parameter, native alias)</c> key, every membership
/// in that predicate uses the casted <c>IN</c> form instead, so the page/search shape with a single
/// alias keeps the index-friendly <c>UNWIND</c> fast path while a multi-alias predicate never
/// materializes the cross product.
/// </para>
/// <para>
/// A membership under <c>OR</c>, <c>NOT</c>, or another non-conjunct operator is also rewritten to
/// the casted <c>IN</c> form, which evaluates on the current row and cannot multiply or drop rows
/// the way a row-producing <c>UNWIND</c> would. A literal list such as <c>id(n) IN [1, 2]</c> uses
/// the same cast. A literal list with a non-integer element fails closed.
/// </para>
/// <para>
/// An <c>OPTIONAL MATCH</c> predicate is rejected with a translation exception on AGE 1.8. A
/// row-producing <c>UNWIND</c> would drop the null row, and rejecting the shape is safer than
/// emitting a predicate whose optional-match behavior is unverified. A membership whose parameter
/// is not a sequence of 64-bit ids is rejected for the same reason.
/// </para>
/// </remarks>
internal sealed class AgeNativeIdListPass : ICypherPass
{
    private const string UnwindAliasPrefix = "__cvoya_native_id_";

    private readonly bool nativeIdInPredicatesSupported;

    /// <summary>Initializes a native-id list lowering pass.</summary>
    /// <param name="nativeIdInPredicatesSupported">
    /// <see langword="true"/> when the connected AGE server supports the native
    /// <c>id(alias) IN &lt;list&gt;</c> form (AGE 1.7). The pass then leaves the statement
    /// unchanged, because the native predicate is supported and faster than a rewrite. The
    /// default is the AGE 1.8-safe lowering, which is also used while the server version is still
    /// unknown.
    /// </param>
    public AgeNativeIdListPass(bool nativeIdInPredicatesSupported = false) =>
        this.nativeIdInPredicatesSupported = nativeIdInPredicatesSupported;

    /// <inheritdoc />
    public CypherStatement Run(CypherStatement input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // AGE 1.7 handles the native IN form; rewriting it would add a row source for no gain.
        if (nativeIdInPredicatesSupported)
        {
            return input;
        }

        var parameters = input.Parameters;
        var rewritten = RewriteClauses(input.Clauses, parameters, out var changed);
        return changed ? input.WithClauses(rewritten) : input;
    }

    private static List<ICypherClause> RewriteClauses(
        IReadOnlyList<ICypherClause> clauses,
        IReadOnlyDictionary<string, object?> parameters,
        out bool changed)
    {
        changed = false;
        var result = new List<ICypherClause>(clauses.Count);
        ICypherClause? previous = null;
        foreach (var clause in clauses)
        {
            switch (clause)
            {
                case WhereClause where:
                    {
                        // A predicate on an OPTIONAL MATCH keeps the null row when nothing matches.
                        // A row-producing UNWIND would drop that row, and the optional-match shape
                        // is not part of the verified lowering, so fail closed rather than emit a
                        // predicate whose nullable behavior is unproven.
                        if (previous is MatchClause { Optional: true })
                        {
                            if (ContainsNativeIdMembership(where.Predicate))
                            {
                                throw new GraphQueryTranslationException(
                                    "AGE cannot evaluate a native-id list predicate on an OPTIONAL MATCH. " +
                                    "Filter the native ids before the optional match.");
                            }

                            result.Add(where);
                            break;
                        }

                        var hadMembership = ContainsNativeIdMembership(where.Predicate);

                        // One UNWIND is a row source; two stacked UNWINDs make the intermediate
                        // row count the product of the id counts. A predicate that would need more
                        // than one row source uses the casted IN comparison for every membership
                        // instead, so a page/search shape (one alias) still gets the index-friendly
                        // fast path.
                        var unwindKeys = new HashSet<RewriteKey>();
                        CollectUnwindKeys(where.Predicate, unwindKeys);
                        var context = new RewriteContext();
                        var predicate = RewritePredicate(
                            where.Predicate,
                            parameters,
                            context,
                            allowUnwind: unwindKeys.Count <= 1);
                        if (predicate is null)
                        {
                            if (hadMembership)
                            {
                                throw UnhandledShape();
                            }

                            result.Add(where);
                            break;
                        }

                        if (ContainsNativeIdMembership(predicate))
                        {
                            throw UnhandledShape();
                        }

                        changed = true;
                        if (context.Unwinds.Count == 0)
                        {
                            // A predicate that only used the casted IN comparison needs no row source.
                            result.Add(new WhereClause(predicate));
                            break;
                        }

                        if (previous is MatchClause { Optional: false })
                        {
                            // Keep the id equality adjacent to its MATCH so the planner can push the
                            // native id lookup into the label scan. Inserting the UNWIND between the
                            // MATCH and WHERE would keep the predicate valid but turn a page-style id
                            // lookup into a full label scan in every UNION branch.
                            var index = result.Count - 1;
                            foreach (var (parameterName, alias) in context.Unwinds)
                            {
                                result.Insert(index++, new UnwindClause(
                                    new QueryParameter(parameterName),
                                    alias));
                            }

                            result.Add(new WhereClause(predicate));
                            break;
                        }

                        foreach (var (parameterName, alias) in context.Unwinds)
                        {
                            result.Add(new UnwindClause(new QueryParameter(parameterName), alias));
                        }

                        // A WHERE attached to a WITH cannot see an earlier UNWIND variable after the
                        // projection. Re-attach it to a wildcard WITH that carries the unwound
                        // variable; this predicate is a post-projection filter, so no pushdown is lost.
                        result.Add(WithClause.All);
                        result.Add(new WhereClause(predicate));
                        break;
                    }

                case SetOperationClause setOperation:
                    {
                        var first = RewriteClauses(setOperation.First, parameters, out var firstChanged);
                        var second = RewriteClauses(setOperation.Second, parameters, out var secondChanged);
                        if (!firstChanged && !secondChanged)
                        {
                            result.Add(setOperation);
                            break;
                        }

                        changed = true;
                        result.Add(new SetOperationClause(first, second, setOperation.PreserveDuplicates));
                        break;
                    }

                default:
                    result.Add(clause);
                    break;
            }

            previous = clause;
        }

        return result;
    }

    /// <summary>
    /// Rewrites positive conjuncts and casted membership tests. Returns
    /// <see langword="null"/> when the predicate is unchanged, and a literal when the parameter
    /// list is empty.
    /// </summary>
    private static CypherExpression? RewritePredicate(
        CypherExpression predicate,
        IReadOnlyDictionary<string, object?> parameters,
        RewriteContext context,
        bool allowUnwind)
    {
        switch (predicate)
        {
            case BinaryExpression { Op: CypherBinaryOperator.And } binary:
                {
                    var left = RewritePredicate(binary.Left, parameters, context, allowUnwind);
                    var right = RewritePredicate(binary.Right, parameters, context, allowUnwind);
                    return left is null && right is null
                        ? null
                        : new BinaryExpression(
                            CypherBinaryOperator.And,
                            left ?? binary.Left,
                            right ?? binary.Right);
                }

            case ConjunctionExpression conjunction:
                {
                    List<CypherExpression>? rewritten = null;
                    for (var index = 0; index < conjunction.Predicates.Count; index++)
                    {
                        var item = RewritePredicate(
                            conjunction.Predicates[index],
                            parameters,
                            context,
                            allowUnwind);
                        if (item is not null)
                        {
                            rewritten ??= [.. conjunction.Predicates];
                            rewritten[index] = item;
                        }
                    }

                    return rewritten is null ? null : new ConjunctionExpression(rewritten);
                }

            case BinaryExpression { Op: CypherBinaryOperator.Or } binary:
                {
                    // A membership under OR cannot use a row-producing UNWIND. Rewrite both sides and
                    // every nested conjunct with the row-preserving form instead.
                    var left = RewritePredicate(binary.Left, parameters, context, allowUnwind: false);
                    var right = RewritePredicate(binary.Right, parameters, context, allowUnwind: false);
                    return left is null && right is null
                        ? null
                        : new BinaryExpression(
                            CypherBinaryOperator.Or,
                            left ?? binary.Left,
                            right ?? binary.Right);
                }

            case UnaryExpression { Op: CypherUnaryOperator.Not } unary:
                {
                    if (unary.Operand is BinaryExpression membership && IsNativeIdMembership(membership))
                    {
                        return RewriteMembership(
                            membership,
                            parameters,
                            context,
                            allowUnwind: false,
                            negated: true);
                    }

                    var operand = RewritePredicate(unary.Operand, parameters, context, allowUnwind: false);
                    return operand is null ? null : new UnaryExpression(CypherUnaryOperator.Not, operand);
                }

            case BinaryExpression membership when IsNativeIdMembership(membership):
                return RewriteMembership(membership, parameters, context, allowUnwind, negated: false);

            case CollectionContainsExpression
            {
                Item: NativeElementIdentity identity,
                Collection: QueryParameter or ListExpression,
            } contains:
                return RewriteMembership(
                    new BinaryExpression(CypherBinaryOperator.In, identity, contains.Collection),
                    parameters,
                    context,
                    allowUnwind,
                    negated: false);

            default:
                return null;
        }
    }

    /// <summary>
    /// Collects the distinct <c>(parameter, native alias)</c> keys that a positive top-level
    /// conjunction would turn into <c>UNWIND</c> row sources. A count above one means stacking the
    /// row sources would build the id cross product.
    /// </summary>
    private static void CollectUnwindKeys(
        CypherExpression predicate,
        HashSet<RewriteKey> keys)
    {
        switch (predicate)
        {
            case CollectionContainsExpression
            {
                Item: NativeElementIdentity { Target: VariableRef containsTarget },
                Collection: QueryParameter containsParameter,
            }:
                keys.Add(new RewriteKey(containsParameter.Name, containsTarget.Alias));
                break;

            case BinaryExpression { Op: CypherBinaryOperator.And } binary:
                CollectUnwindKeys(binary.Left, keys);
                CollectUnwindKeys(binary.Right, keys);
                break;

            case ConjunctionExpression conjunction:
                foreach (var item in conjunction.Predicates)
                {
                    CollectUnwindKeys(item, keys);
                }

                break;

            case BinaryExpression membership when IsNativeIdMembership(membership):
                // Only a parameter membership on a native alias becomes an UNWIND. OR, NOT, and
                // literal lists use the casted IN form and need no key.
                if (membership.Right is QueryParameter parameter &&
                    membership.Left is NativeElementIdentity { Target: VariableRef target })
                {
                    keys.Add(new RewriteKey(parameter.Name, target.Alias));
                }

                break;
        }
    }

    private static CypherExpression RewriteMembership(
        BinaryExpression membership,
        IReadOnlyDictionary<string, object?> parameters,
        RewriteContext context,
        bool allowUnwind,
        bool negated)
    {
        CypherExpression source;
        IReadOnlyList<long> ids;
        string? parameterName = null;
        if (membership.Right is QueryParameter parameter)
        {
            parameterName = parameter.Name;
            if (!parameters.TryGetValue(parameter.Name, out var value) || !TryGetIds(value, out ids))
            {
                throw new GraphQueryTranslationException(
                    $"The native-id list parameter '${parameter.Name}' must be a sequence of 64-bit " +
                    "integer ids. AGE 1.8 cannot evaluate id(...) IN <agtype list parameter>.");
            }

            source = new QueryParameter(parameter.Name);
        }
        else if (membership.Right is ListExpression list)
        {
            ids = GetLiteralIds(list);
            source = list;
        }
        else
        {
            throw UnhandledShape();
        }

        if (ids.Count == 0)
        {
            // `id IN []` is false, so a direct negation of it is true.
            return new Literal(negated);
        }

        if (allowUnwind &&
            !negated &&
            parameterName is not null &&
            membership.Left is NativeElementIdentity { Target: VariableRef target })
        {
            var key = new RewriteKey(parameterName, target.Alias);
            if (!context.AliasByKey.TryGetValue(key, out var alias))
            {
                alias = UnwindAliasPrefix + context.NextAlias.ToString(CultureInfo.InvariantCulture);
                context.NextAlias++;
                context.AliasByKey.Add(key, alias);
                context.Unwinds.Add((parameterName, alias));
            }

            return new BinaryExpression(
                CypherBinaryOperator.Equal,
                membership.Left,
                new VariableRef(alias));
        }

        var castIdentity = new CastExpression(membership.Left, "agtype");
        var casted = new BinaryExpression(CypherBinaryOperator.In, castIdentity, source);
        return negated ? new UnaryExpression(CypherUnaryOperator.Not, casted) : casted;
    }

    private static bool IsNativeIdMembership(BinaryExpression expression) =>
        expression.Op == CypherBinaryOperator.In &&
        expression.Left is NativeElementIdentity &&
        expression.Right is QueryParameter or ListExpression;

    private static List<long> GetLiteralIds(ListExpression list)
    {
        var ids = new List<long>(list.Items.Count);
        foreach (var item in list.Items)
        {
            if (!TryGetLiteralId(item, out var id))
            {
                throw new GraphQueryTranslationException(
                    "A literal native-id list must contain only 64-bit integer literals. " +
                    "AGE 1.8 cannot evaluate id(...) IN <literal list> for any other element type.");
            }

            ids.Add(id);
        }

        return ids;
    }

    private static bool TryGetLiteralId(CypherExpression expression, out long id)
    {
        if (expression is Literal literal)
        {
            switch (literal.Value)
            {
                case sbyte value:
                    id = value;
                    return true;
                case byte value:
                    id = value;
                    return true;
                case short value:
                    id = value;
                    return true;
                case ushort value:
                    id = value;
                    return true;
                case int value:
                    id = value;
                    return true;
                case uint value:
                    id = value;
                    return true;
                case long value:
                    id = value;
                    return true;
                case ulong value when value <= long.MaxValue:
                    id = (long)value;
                    return true;
            }
        }

        id = 0;
        return false;
    }

    private static GraphQueryTranslationException UnhandledShape() =>
        new(
            "The native-id list predicate has a shape that the AGE lowering cannot rewrite safely " +
            "(it is nested inside an operator other than AND, OR, or NOT). " +
            "Move the native-id filter into a top-level conjunction.");

    private static bool ContainsNativeIdMembership(CypherExpression expression) =>
        expression switch
        {
            BinaryExpression membership when IsNativeIdMembership(membership) => true,
            BinaryExpression binary => ContainsNativeIdMembership(binary.Left) ||
                ContainsNativeIdMembership(binary.Right),
            UnaryExpression unary => ContainsNativeIdMembership(unary.Operand),
            CastExpression cast => ContainsNativeIdMembership(cast.Operand),
            ConjunctionExpression conjunction => conjunction.Predicates.Any(ContainsNativeIdMembership),
            FunctionCall function => function.Arguments.Any(ContainsNativeIdMembership),
            ListExpression list => list.Items.Any(ContainsNativeIdMembership),
            ListComprehensionExpression comprehension =>
                ContainsNativeIdMembership(comprehension.Source) ||
                (comprehension.Predicate is not null && ContainsNativeIdMembership(comprehension.Predicate)) ||
                (comprehension.Projection is not null && ContainsNativeIdMembership(comprehension.Projection)),
            CaseExpression @case =>
                ContainsNativeIdMembership(@case.Condition) ||
                ContainsNativeIdMembership(@case.WhenTrue) ||
                (@case.WhenFalse is not null && ContainsNativeIdMembership(@case.WhenFalse)),
            IndexExpression index => ContainsNativeIdMembership(index.Target) ||
                ContainsNativeIdMembership(index.Index),
            PropertyAccess property => ContainsNativeIdMembership(property.Target),
            EscapedPropertyAccess property => ContainsNativeIdMembership(property.Target),
            PhysicalPropertyAccess property => ContainsNativeIdMembership(property.Target),
            CollectionPropertyAccess property => ContainsNativeIdMembership(property.Target),
            CollectionContainsExpression contains => ContainsNativeIdMembership(contains.Collection) ||
                ContainsNativeIdMembership(contains.Item),
            LabelTest label => ContainsNativeIdMembership(label.Target),
            MapExpression map => map.Entries.Any(entry => ContainsNativeIdMembership(entry.Value)),
            PatternSubqueryExpression subquery => subquery.Predicate is not null &&
                ContainsNativeIdMembership(subquery.Predicate),
            PatternComprehensionExpression comprehension =>
                ContainsNativeIdMembership(comprehension.Projection) ||
                (comprehension.Predicate is not null && ContainsNativeIdMembership(comprehension.Predicate)),
            _ => false,
        };

    private static bool TryGetIds(object? value, out IReadOnlyList<long> ids)
    {
        switch (value)
        {
            case long[] array:
                ids = array;
                return true;
            case IReadOnlyList<long> list:
                ids = list;
                return true;
            case IEnumerable<long> sequence:
                ids = sequence.ToArray();
                return true;
            default:
                ids = [];
                return false;
        }
    }

    private readonly record struct RewriteKey(string ParameterName, string TargetAlias);

    private sealed class RewriteContext
    {
        public Dictionary<RewriteKey, string> AliasByKey { get; } = new();

        public List<(string ParameterName, string Alias)> Unwinds { get; } = [];

        public int NextAlias { get; set; }
    }
}
