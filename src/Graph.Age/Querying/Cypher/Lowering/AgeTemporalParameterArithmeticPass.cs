// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying.Cypher.Lowering;

using System.Globalization;
using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;
using Cvoya.Graph.Cypher.Validation;

/// <summary>
/// Lowers temporal wrappers and parameter-only temporal arithmetic into AGE-compatible parameters.
/// </summary>
internal sealed class AgeTemporalParameterArithmeticPass : ICypherPass
{
    /// <inheritdoc />
    public CypherStatement Run(CypherStatement input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var rewriter = new TemporalRewriter(input.Parameters);
        var clauses = rewriter.RewriteClauses(input.Clauses);
        return rewriter.Changed
            ? new CypherStatement(clauses, rewriter.Parameters, input.PathTypes)
            : input;
    }

    private sealed class TemporalRewriter
    {
        private readonly Dictionary<string, object?> parameters;
        private int parameterIndex;

        public TemporalRewriter(IReadOnlyDictionary<string, object?> parameters)
        {
            this.parameters = new Dictionary<string, object?>(parameters, StringComparer.Ordinal);
        }

        public bool Changed { get; private set; }

        public IReadOnlyDictionary<string, object?> Parameters => parameters;

        public ICypherClause[] RewriteClauses(IReadOnlyList<ICypherClause> clauses) =>
            clauses.Select(RewriteClause).ToArray();

        private ICypherClause RewriteClause(ICypherClause clause) => clause switch
        {
            WhereClause where => new WhereClause(Rewrite(where.Predicate)),
            WithClause { Wildcard: true } => WithClause.All,
            WithClause with => new WithClause(RewriteItems(with.Items), with.Distinct),
            ReturnClause @return => new ReturnClause(RewriteItems(@return.Items), @return.Distinct),
            SetClause set => new SetClause(set.Items
                .Select(item => new SetItem(Rewrite(item.Target), Rewrite(item.Value)))
                .ToArray()),
            CallSubqueryClause subquery => new CallSubqueryClause(
                subquery.ImportedVariables,
                RewriteClauses(subquery.Body)),
            CallClause call => CallClause.WithAliasedYields(
                call.Procedure,
                call.Arguments.Select(Rewrite).ToArray(),
                call.Yields),
            UnwindClause unwind => new UnwindClause(Rewrite(unwind.Source), unwind.Alias),
            OrderByClause orderBy => new OrderByClause(orderBy.Items
                .Select(item => new OrderByItem(Rewrite(item.Expression), item.Descending))
                .ToArray()),
            SkipClause skip => new SkipClause(Rewrite(skip.Count)),
            LimitClause limit => new LimitClause(Rewrite(limit.Count)),
            EntityProjectionClause projection => new EntityProjectionClause(
                projection.Shape,
                projection.SourceAlias,
                projection.RelationshipAlias,
                projection.TargetAlias,
                projection.LoadSourceProperties,
                projection.LoadTargetProperties,
                projection.IncludePathCoordinates,
                projection.Ordering
                    .Select(item => new OrderByItem(Rewrite(item.Expression), item.Descending))
                    .ToArray(),
                projection.RowIdentityAliases)
            {
                SourceComplexPropertyDepth = projection.SourceComplexPropertyDepth,
                TargetComplexPropertyDepth = projection.TargetComplexPropertyDepth,
            },
            _ => clause,
        };

        private ReturnItem[] RewriteItems(IReadOnlyList<ReturnItem> items) => items
            .Select(item => new ReturnItem(Rewrite(item.Expression), item.Alias))
            .ToArray();

        // Rebuilds are unconditional; Changed is set only where a temporal construct is actually
        // rewritten, so record equality over list-typed members (always reference-unequal after a
        // rebuild) never forces a spurious statement copy.
        private CypherExpression Rewrite(CypherExpression expression) =>
            expression switch
            {
                BinaryExpression binary => RewriteBinary(binary),
                UnaryExpression unary => new UnaryExpression(unary.Op, Rewrite(unary.Operand)),
                PropertyAccess property => new PropertyAccess(Rewrite(property.Target), property.Property),
                EscapedPropertyAccess property => new EscapedPropertyAccess(
                    Rewrite(property.Target),
                    property.Property),
                PhysicalPropertyAccess property => new PhysicalPropertyAccess(Rewrite(property.Target), property.Property),
                CollectionPropertyAccess property => new CollectionPropertyAccess(
                    Rewrite(property.Target),
                    property.Property,
                    property.Escape),
                CollectionContainsExpression contains => new CollectionContainsExpression(
                    Rewrite(contains.Collection),
                    Rewrite(contains.Item)),
                NativeElementIdentity identity => new NativeElementIdentity(Rewrite(identity.Target)),
                FunctionCall function => RewriteFunction(function),
                LabelTest label => new LabelTest(Rewrite(label.Target), label.Labels),
                ListExpression list => new ListExpression(list.Items.Select(Rewrite).ToArray()),
                ListComprehensionExpression comprehension => new ListComprehensionExpression(
                    Rewrite(comprehension.Source),
                    comprehension.IteratorAlias,
                    comprehension.Predicate is null ? null : Rewrite(comprehension.Predicate),
                    comprehension.Projection is null ? null : Rewrite(comprehension.Projection)),
                ReduceExpression reduce => new ReduceExpression(
                    reduce.AccumulatorAlias,
                    Rewrite(reduce.Seed),
                    reduce.IteratorAlias,
                    Rewrite(reduce.Source),
                    Rewrite(reduce.Reducer)),
                AllExpression all => new AllExpression(
                    all.IteratorAlias,
                    Rewrite(all.Source),
                    Rewrite(all.Predicate)),
                MapExpression map => new MapExpression(map.Entries
                    .Select(entry => new MapEntry(entry.Key, Rewrite(entry.Value)))
                    .ToArray()),
                IndexExpression index => new IndexExpression(Rewrite(index.Target), Rewrite(index.Index)),
                CaseExpression @case => new CaseExpression(
                    Rewrite(@case.Condition),
                    Rewrite(@case.WhenTrue),
                    @case.WhenFalse is null ? null : Rewrite(@case.WhenFalse)),
                ConjunctionExpression conjunction => new ConjunctionExpression(
                    conjunction.Predicates.Select(Rewrite).ToArray()),
                PatternSubqueryExpression subquery => new PatternSubqueryExpression(
                    subquery.Kind,
                    subquery.Pattern,
                    subquery.Predicate is null ? null : Rewrite(subquery.Predicate)),
                PatternComprehensionExpression comprehension => new PatternComprehensionExpression(
                    comprehension.Pattern,
                    Rewrite(comprehension.Projection),
                    comprehension.Predicate is null ? null : Rewrite(comprehension.Predicate)),
                _ => expression,
            };

        private CypherExpression RewriteBinary(BinaryExpression binary)
        {
            var rewritten = new BinaryExpression(binary.Op, Rewrite(binary.Left), Rewrite(binary.Right));
            if (TryFoldTemporalArithmetic(rewritten, out var parameter))
            {
                return parameter;
            }

            return TryShiftTemporalArithmeticComparison(rewritten, out var comparison)
                ? comparison
                : rewritten;
        }

        private CypherExpression RewriteFunction(FunctionCall function)
        {
            var arguments = function.Arguments.Select(Rewrite).ToArray();
            if (arguments.Length == 0 && TryEvaluateParameterFreeTemporal(function.Name, out var value))
            {
                return AddParameter(value);
            }

            if (arguments.Length == 1 && function.Name.StartsWith("temporal.", StringComparison.Ordinal))
            {
                Changed = true;
                return arguments[0];
            }

            return new FunctionCall(function.Name, arguments);
        }

        private bool TryFoldTemporalArithmetic(
            BinaryExpression expression,
            out QueryParameter parameter)
        {
            parameter = null!;
            if (expression is not
                {
                    Op: CypherBinaryOperator.Add,
                    Left: QueryParameter valueParameter,
                    Right: MapExpression { Entries: [var duration] }
                } ||
                duration.Value is not QueryParameter amountParameter ||
                !IsDurationUnit(duration.Key) ||
                !parameters.TryGetValue(valueParameter.Name, out var rawValue) ||
                !parameters.TryGetValue(amountParameter.Name, out var rawAmount) ||
                rawValue is null || rawAmount is null)
            {
                return false;
            }

            var amount = Convert.ToDouble(rawAmount, CultureInfo.InvariantCulture);
            if (!TryGetDateTimeOffset(rawValue, out var value))
            {
                return false;
            }

            value = duration.Key.ToLowerInvariant() switch
            {
                "days" => value.AddDays(amount),
                "hours" => value.AddHours(amount),
                "minutes" => value.AddMinutes(amount),
                "seconds" => value.AddSeconds(amount),
                "milliseconds" => value.AddMilliseconds(amount),
                "months" => value.AddMonths(Convert.ToInt32(amount, CultureInfo.InvariantCulture)),
                "years" => value.AddYears(Convert.ToInt32(amount, CultureInfo.InvariantCulture)),
                _ => value,
            };
            parameter = AddParameter(value.ToUniversalTime());
            return true;
        }

        private bool TryShiftTemporalArithmeticComparison(
            BinaryExpression expression,
            out BinaryExpression comparison)
        {
            comparison = null!;
            if (!IsComparison(expression.Op))
            {
                return false;
            }

            if (TryShiftTemporalOperand(expression.Left, expression.Right, out var left, out var right) ||
                TryShiftTemporalOperand(expression.Right, expression.Left, out right, out left))
            {
                comparison = new BinaryExpression(expression.Op, left, right);
                return true;
            }

            return false;
        }

        private bool TryShiftTemporalOperand(
            CypherExpression temporalOperand,
            CypherExpression boundOperand,
            out CypherExpression rewrittenTemporal,
            out CypherExpression rewrittenBound)
        {
            rewrittenTemporal = null!;
            rewrittenBound = null!;
            if (temporalOperand is not BinaryExpression
                {
                    Op: CypherBinaryOperator.Add,
                    Left: var temporalValue,
                    Right: MapExpression { Entries: [var duration] }
                } ||
                boundOperand is not QueryParameter boundParameter ||
                !IsDurationUnit(duration.Key) ||
                !TryGetDurationAmount(duration.Value, out var amount) ||
                !parameters.TryGetValue(boundParameter.Name, out var rawBound) ||
                rawBound is null ||
                !TryAdjustTemporalValue(rawBound, duration.Key, -amount, out var adjustedBound))
            {
                return false;
            }

            rewrittenTemporal = temporalValue;
            rewrittenBound = AddParameter(adjustedBound);
            return true;
        }

        private bool TryGetDurationAmount(CypherExpression expression, out double amount)
        {
            object? raw = expression switch
            {
                QueryParameter parameter when parameters.TryGetValue(parameter.Name, out var value) => value,
                Literal literal => literal.Value,
                _ => null,
            };

            if (raw is null)
            {
                amount = default;
                return false;
            }

            try
            {
                amount = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
            {
                amount = default;
                return false;
            }
        }

        private static bool TryAdjustTemporalValue(
            object rawValue,
            string unit,
            double amount,
            out object adjusted)
        {
            if (rawValue is DateTime dateTime)
            {
                adjusted = Add(dateTime, unit, amount);
                return true;
            }

            if (rawValue is DateTimeOffset dateTimeOffset)
            {
                adjusted = Add(dateTimeOffset, unit, amount);
                return true;
            }

            if (DateTimeOffset.TryParse(
                Convert.ToString(rawValue, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
            {
                adjusted = Add(parsed, unit, amount);
                return true;
            }

            adjusted = null!;
            return false;
        }

        private static DateTime Add(DateTime value, string unit, double amount) =>
            unit.ToLowerInvariant() switch
            {
                "days" => value.AddDays(amount),
                "hours" => value.AddHours(amount),
                "minutes" => value.AddMinutes(amount),
                "seconds" => value.AddSeconds(amount),
                "milliseconds" => value.AddMilliseconds(amount),
                "months" => value.AddMonths(Convert.ToInt32(amount, CultureInfo.InvariantCulture)),
                "years" => value.AddYears(Convert.ToInt32(amount, CultureInfo.InvariantCulture)),
                _ => value,
            };

        private static DateTimeOffset Add(DateTimeOffset value, string unit, double amount) =>
            unit.ToLowerInvariant() switch
            {
                "days" => value.AddDays(amount),
                "hours" => value.AddHours(amount),
                "minutes" => value.AddMinutes(amount),
                "seconds" => value.AddSeconds(amount),
                "milliseconds" => value.AddMilliseconds(amount),
                "months" => value.AddMonths(Convert.ToInt32(amount, CultureInfo.InvariantCulture)),
                "years" => value.AddYears(Convert.ToInt32(amount, CultureInfo.InvariantCulture)),
                _ => value,
            };

        private static bool IsComparison(CypherBinaryOperator op) => op is
            CypherBinaryOperator.Equal or
            CypherBinaryOperator.NotEqual or
            CypherBinaryOperator.LessThan or
            CypherBinaryOperator.LessThanOrEqual or
            CypherBinaryOperator.GreaterThan or
            CypherBinaryOperator.GreaterThanOrEqual;

        private static bool TryEvaluateParameterFreeTemporal(string name, out object value)
        {
            value = name switch
            {
                "temporal.localDateTime" => DateTime.Now,
                "temporal.date" => DateTime.Today.ToString(
                    "yyyy-MM-dd'T'00:00:00.0000000",
                    CultureInfo.InvariantCulture),
                "temporal.time" => TimeOnly.FromDateTime(DateTime.Now),
                "temporal.datetime" => DateTime.UtcNow,
                _ => null!,
            };
            return value is not null;
        }

        private static bool TryGetDateTimeOffset(object rawValue, out DateTimeOffset value)
        {
            if (rawValue is DateTime dateTime)
            {
                value = new DateTimeOffset(dateTime);
                return true;
            }

            if (rawValue is DateTimeOffset dateTimeOffset)
            {
                value = dateTimeOffset;
                return true;
            }

            return DateTimeOffset.TryParse(
                Convert.ToString(rawValue, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out value);
        }

        private static bool IsDurationUnit(string unit) => unit.ToLowerInvariant() is
            "days" or
            "hours" or
            "minutes" or
            "seconds" or
            "milliseconds" or
            "months" or
            "years";

        private QueryParameter AddParameter(object value)
        {
            string name;
            do
            {
                name = $"age_temporal_{parameterIndex++}";
            }
            while (parameters.ContainsKey(name));

            parameters[name] = value;
            Changed = true;
            return new QueryParameter(name);
        }
    }
}
