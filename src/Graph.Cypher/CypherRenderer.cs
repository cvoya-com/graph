// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Cypher;

using System.Globalization;
using System.Text;
using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;
using Cvoya.Graph.Cypher.Validation;

/// <summary>Renders the shared Cypher AST using an <see cref="ICypherDialect"/>.</summary>
public sealed class CypherRenderer : ICypherRenderContext
{
    private readonly ICypherDialect dialect;

    /// <summary>Initializes a renderer for a specific dialect.</summary>
    public CypherRenderer(ICypherDialect dialect)
    {
        this.dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
    }

    /// <summary>Renders a validated statement, its parameters, and its exact projection schema.</summary>
    public CypherRenderResult Render(CypherStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

#if DEBUG
        new CypherAstValidator().Run(statement);
#endif

        var builder = new StringBuilder();
        foreach (var clause in statement.Clauses)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            RenderClause(builder, clause);
        }

        return new CypherRenderResult(
            builder.ToString().Trim(),
            statement.Parameters,
            GetProjectionColumns(statement));
    }

    private void RenderClause(StringBuilder builder, ICypherClause clause)
    {
        switch (clause)
        {
            case MatchClause match:
                builder.Append(match.Optional ? "OPTIONAL MATCH " : "MATCH ");
                builder.Append(string.Join(", ", match.Patterns.Select(RenderPattern)));
                break;

            case WhereClause where:
                builder.Append("WHERE ").Append(RenderExpression(where.Predicate));
                break;

            case WithClause with:
                builder.Append("WITH ");
                if (with.Distinct)
                {
                    builder.Append("DISTINCT ");
                }

                builder.Append(with.Wildcard ? "*" : string.Join(", ", with.Items.Select(RenderReturnItem)));
                break;

            case ReturnClause @return:
                builder.Append("RETURN ");
                if (@return.Distinct)
                {
                    builder.Append("DISTINCT ");
                }

                builder.Append(string.Join(", ", @return.Items.Select(RenderReturnItem)));
                break;

            case SetClause set:
                builder.Append("SET ")
                    .Append(string.Join(", ", set.Items.Select(item =>
                        $"{RenderExpression(item.Target)} = {RenderExpression(item.Value)}")));
                break;

            case DeleteClause delete:
                builder.Append(delete.Detach ? "DETACH DELETE " : "DELETE ")
                    .Append(string.Join(", ", delete.Targets.Select(target => RenderExpression(target))));
                break;

            case CallSubqueryClause subquery:
                RenderCallSubquery(builder, subquery);
                break;

            case CallClause call:
                builder.Append("CALL ")
                    .Append(call.Procedure)
                    .Append('(')
                    .Append(string.Join(", ", call.Arguments.Select(argument => RenderExpression(argument))))
                    .Append(')');
                if (call.Yields.Count > 0)
                {
                    builder.Append(" YIELD ")
                        .Append(string.Join(", ", call.Yields.Select(yield =>
                            yield.Alias is null ? yield.Name : $"{yield.Name} AS {yield.Alias}")));
                }

                break;

            case FullTextSearchClause search:
                builder.Append(dialect.RenderFullTextSearch(search, this));
                break;

            case UnwindClause unwind:
                builder.Append("UNWIND ")
                    .Append(RenderExpression(unwind.Source))
                    .Append(" AS ")
                    .Append(unwind.Alias);
                break;

            case OrderByClause orderBy:
                builder.Append("ORDER BY ")
                    .Append(string.Join(", ", orderBy.Items.Select(item =>
                        RenderExpression(item.Expression) + (item.Descending ? " DESC" : string.Empty))));
                break;

            case SkipClause skip:
                builder.Append("SKIP ").Append(RenderExpression(skip.Count));
                break;

            case LimitClause limit:
                builder.Append("LIMIT ").Append(RenderExpression(limit.Count));
                break;

            case EntityProjectionClause projection:
                RenderEntityProjection(builder, projection);
                break;

            case SetOperationClause setOperation:
                RenderClauseSequence(builder, setOperation.First);
                builder.AppendLine()
                    .Append(setOperation.PreserveDuplicates ? "UNION ALL" : "UNION")
                    .AppendLine();
                RenderClauseSequence(builder, setOperation.Second);
                break;

            default:
                throw new GraphException($"Unsupported Cypher clause '{clause.GetType().Name}'.");
        }
    }

    private void RenderClauseSequence(StringBuilder builder, IReadOnlyList<ICypherClause> clauses)
    {
        for (var index = 0; index < clauses.Count; index++)
        {
            if (index > 0)
                builder.AppendLine();
            RenderClause(builder, clauses[index]);
        }
    }

    private string RenderPattern(PathPattern pattern)
    {
        var builder = new StringBuilder();
        if (pattern.Alias is not null)
        {
            builder.Append(pattern.Alias).Append(" = ");
        }

        if (pattern.Selection != PathSelection.All)
        {
            builder.Append(pattern.Selection == PathSelection.Shortest
                ? "shortestPath("
                : "allShortestPaths(");
        }

        for (var index = 0; index < pattern.Elements.Count; index++)
        {
            switch (pattern.Elements[index])
            {
                case NodePattern node:
                    builder.Append('(');
                    if (node.Alias is not null)
                    {
                        builder.Append(node.Alias);
                    }

                    if (node.Labels.Count > 0)
                    {
                        builder.Append(':').Append(dialect.RenderNodeLabels(node.Labels));
                    }

                    builder.Append(')');
                    break;

                case RelationshipPattern relationship:
                    RenderRelationship(builder, relationship);
                    break;
            }
        }

        if (pattern.Selection != PathSelection.All)
        {
            builder.Append(')');
        }

        return builder.ToString();
    }

    private void RenderRelationship(StringBuilder builder, RelationshipPattern relationship)
    {
        if (relationship.Direction == CypherDirection.Incoming)
        {
            builder.Append("<-");
        }
        else
        {
            builder.Append('-');
        }

        builder.Append('[');
        if (relationship.Alias is not null)
        {
            builder.Append(relationship.Alias);
        }

        if (relationship.Types.Count > 0)
        {
            // The AST carries type names individually; the alternation join happens here, so a
            // literal '|' inside a type name escapes as part of that name rather than splitting it.
            builder.Append(':').Append(dialect.RenderRelationshipTypes(relationship.Types));
        }

        if (relationship.Depth is not null)
        {
            builder.Append(dialect.RenderDepth(relationship.Depth));
        }

        builder.Append(']');
        if (relationship.Direction == CypherDirection.Outgoing)
        {
            builder.Append("->");
        }
        else
        {
            builder.Append('-');
        }
    }

    private string RenderReturnItem(ReturnItem item)
    {
        var expression = RenderExpression(item.Expression);
        return item.Alias is null ? expression : $"{expression} AS {item.Alias}";
    }

    private void RenderCallSubquery(StringBuilder builder, CallSubqueryClause subquery)
    {
        builder.Append("CALL {");
        if (subquery.ImportedVariables.Count > 0)
        {
            builder.AppendLine().Append("  WITH ").Append(string.Join(", ", subquery.ImportedVariables));
        }

        foreach (var clause in subquery.Body)
        {
            var inner = new StringBuilder();
            RenderClause(inner, clause);
            builder.AppendLine().Append("  ").Append(inner.ToString().Replace("\n", "\n  ", StringComparison.Ordinal));
        }

        builder.AppendLine().Append('}');
    }

    private string RenderExpression(CypherExpression expression, CypherBinaryOperator? parentOperator = null)
    {
        return expression switch
        {
            VariableRef variable => variable.Alias,
            PropertyAccess property => dialect.RenderPropertyAccess(
                RenderExpression(property.Target), property.Property, escape: false),
            EscapedPropertyAccess property => dialect.RenderPropertyAccess(
                RenderExpression(property.Target), property.Property, escape: true),
            PhysicalPropertyAccess property => dialect.RenderPhysicalPropertyAccess(
                RenderExpression(property.Target), property.Property),
            CollectionPropertyAccess property => dialect.RenderCollectionPropertyAccess(
                RenderExpression(property.Target), property.Property, property.Escape),
            CollectionContainsExpression contains => RenderCollectionContains(contains),
            NativeElementIdentity identity => dialect.RenderNativeElementIdentity(
                RenderExpression(identity.Target)),
            QueryParameter parameter => dialect.RenderParameter(parameter.Name),
            Literal literal => RenderLiteral(literal.Value),
            FunctionCall function => RenderFunction(function),
            BinaryExpression binary => RenderBinary(binary, parentOperator),
            UnaryExpression unary => RenderUnary(unary),
            LabelTest label => RenderLabelTest(label),
            ListExpression list => $"[{string.Join(", ", list.Items.Select(item => RenderExpression(item)))}]",
            ListComprehensionExpression comprehension => RenderListComprehension(comprehension),
            ReduceExpression reduce => RenderReduce(reduce),
            AllExpression all => RenderAll(all),
            MapExpression map => $"{{ {string.Join(", ", map.Entries.Select(entry => $"{entry.Key}: {RenderExpression(entry.Value)}"))} }}",
            EntityProjectionExpression entity => RenderEntityProjectionExpression(entity),
            IndexExpression index => $"{RenderExpression(index.Target)}[{RenderExpression(index.Index)}]",
            CaseExpression @case => RenderCase(@case),
            ConjunctionExpression conjunction => string.Join(" AND ", conjunction.Predicates.Select(item => RenderExpression(item))),
            PatternSubqueryExpression subquery => RenderPatternSubquery(subquery),
            PatternComprehensionExpression comprehension => RenderPatternComprehension(comprehension),
            _ => throw new GraphException($"Unsupported Cypher expression '{expression.GetType().Name}'."),
        };
    }

    private string RenderCollectionContains(CollectionContainsExpression contains)
    {
        var collection = RenderExpression(contains.Collection);
        var item = RenderExpression(contains.Item);
        return dialect.RenderCollectionContains(collection, item);
    }

    private string RenderEntityProjectionExpression(EntityProjectionExpression expression)
    {
        if (!expression.LoadComplexProperties)
        {
            return $"{{ Node: {expression.Alias}, ComplexProperties: [] }}";
        }

        var alias = expression.Alias;
        return $@"{{ Node: {alias}, ComplexProperties: reduce(flat = [], propertyPath IN [
            ({alias})-[propertyRelationships*1..{GraphDataModel.DefaultDepthAllowed}]->(propertyNode)
            WHERE ALL(propertyRelationship IN propertyRelationships WHERE propertyRelationship.{dialect.ComplexPropertyRelationshipMarker} = true) |
            [i IN range(0, size(propertyRelationships) - 1) | {{
                ParentNode: startNode(propertyRelationships[i]),
                Relationship: propertyRelationships[i],
                SequenceNumber: propertyRelationships[i].SequenceNumber,
                Property: endNode(propertyRelationships[i])
            }}]
        ] | flat + propertyPath) }}";
    }

    private string RenderFunction(FunctionCall function)
    {
        if (function.Name == "math.power" && function.Arguments.Count == 2)
        {
            return $"({RenderExpression(function.Arguments[0])} ^ {RenderExpression(function.Arguments[1])})";
        }

        var name = dialect.RenderFunctionName(function.Name);

        return $"{name}({string.Join(", ", function.Arguments.Select(argument => RenderExpression(argument)))})";
    }

    private string RenderBinary(BinaryExpression binary, CypherBinaryOperator? parentOperator)
    {
        var op = binary.Op switch
        {
            CypherBinaryOperator.And => "AND",
            CypherBinaryOperator.Or => "OR",
            CypherBinaryOperator.Equal => "=",
            CypherBinaryOperator.NotEqual => "<>",
            CypherBinaryOperator.LessThan => "<",
            CypherBinaryOperator.LessThanOrEqual => "<=",
            CypherBinaryOperator.GreaterThan => ">",
            CypherBinaryOperator.GreaterThanOrEqual => ">=",
            CypherBinaryOperator.Add => "+",
            CypherBinaryOperator.Subtract => "-",
            CypherBinaryOperator.Multiply => "*",
            CypherBinaryOperator.Divide => "/",
            CypherBinaryOperator.Modulo => "%",
            CypherBinaryOperator.StartsWith => "STARTS WITH",
            CypherBinaryOperator.EndsWith => "ENDS WITH",
            CypherBinaryOperator.Contains => "CONTAINS",
            CypherBinaryOperator.In => "IN",
            CypherBinaryOperator.Matches => "=~",
            CypherBinaryOperator.PropertyContainment => "@>",
            CypherBinaryOperator.PropertyKeyExists => "?",
            _ => throw new GraphException($"Unsupported Cypher binary operator '{binary.Op}'."),
        };

        var text = $"{RenderExpression(binary.Left, binary.Op)} {op} {RenderExpression(binary.Right, binary.Op)}";
        if (binary.Op is CypherBinaryOperator.And or CypherBinaryOperator.Or)
        {
            return $"({text})";
        }

        if (parentOperator is CypherBinaryOperator.Equal or CypherBinaryOperator.NotEqual or
            CypherBinaryOperator.LessThan or CypherBinaryOperator.LessThanOrEqual or
            CypherBinaryOperator.GreaterThan or CypherBinaryOperator.GreaterThanOrEqual or
            CypherBinaryOperator.Matches or CypherBinaryOperator.PropertyContainment &&
            binary.Op is CypherBinaryOperator.Add or CypherBinaryOperator.Subtract or
                CypherBinaryOperator.Multiply or CypherBinaryOperator.Divide or CypherBinaryOperator.Modulo)
        {
            return $"({text})";
        }

        return text;
    }

    private string RenderUnary(UnaryExpression unary)
    {
        var operand = RenderExpression(unary.Operand);
        return unary.Op switch
        {
            CypherUnaryOperator.Not => $"NOT ({operand})",
            CypherUnaryOperator.IsNull => $"{operand} IS NULL",
            CypherUnaryOperator.IsNotNull => $"{operand} IS NOT NULL",
            // Parenthesized: -(a + b) must not render as -a + b.
            CypherUnaryOperator.Negate => $"-({operand})",
            _ => throw new GraphException($"Unsupported Cypher unary operator '{unary.Op}'."),
        };
    }

    private string RenderLabelTest(LabelTest label)
    {
        return dialect.RenderLabelTest(
            RenderExpression(label.Target),
            label.Labels,
            item => RenderLiteral(item));
    }

    private string RenderCase(CaseExpression expression)
    {
        var builder = new StringBuilder()
            .Append("CASE WHEN ")
            .Append(RenderExpression(expression.Condition))
            .Append(" THEN ")
            .Append(RenderExpression(expression.WhenTrue));
        if (expression.WhenFalse is not null)
        {
            builder.Append(" ELSE ").Append(RenderExpression(expression.WhenFalse));
        }

        return builder.Append(" END").ToString();
    }

    private string RenderPatternSubquery(PatternSubqueryExpression expression)
    {
        var keyword = expression.Kind == PatternSubqueryKind.Exists ? "EXISTS" : "COUNT";
        var predicate = expression.Predicate is null ? string.Empty : $" WHERE {RenderExpression(expression.Predicate)}";
        return $"{keyword} {{ MATCH {RenderPattern(expression.Pattern)}{predicate} }}";
    }

    private string RenderPatternComprehension(PatternComprehensionExpression expression)
    {
        var predicate = expression.Predicate is null ? string.Empty : $" WHERE {RenderExpression(expression.Predicate)}";
        return $"[{RenderPattern(expression.Pattern)}{predicate} | {RenderExpression(expression.Projection)}]";
    }

    private string RenderListComprehension(ListComprehensionExpression expression)
    {
        var predicate = expression.Predicate is null ? string.Empty : $" WHERE {RenderExpression(expression.Predicate)}";
        var projection = expression.Projection is null ? string.Empty : $" | {RenderExpression(expression.Projection)}";
        return $"[{expression.IteratorAlias} IN {RenderExpression(expression.Source)}{predicate}{projection}]";
    }

    private string RenderReduce(ReduceExpression expression)
    {
        return $"reduce({expression.AccumulatorAlias} = {RenderExpression(expression.Seed)}, " +
            $"{expression.IteratorAlias} IN {RenderExpression(expression.Source)} | " +
            $"{RenderExpression(expression.Reducer)})";
    }

    private string RenderAll(AllExpression expression)
    {
        return $"ALL({expression.IteratorAlias} IN {RenderExpression(expression.Source)} WHERE " +
            $"{RenderExpression(expression.Predicate)})";
    }

    private static string RenderLiteral(object? value)
    {
        return value switch
        {
            null => "null",
            string text => RenderStringLiteral(text),
            char character => RenderStringLiteral(character.ToString()),
            bool boolean => boolean ? "true" : "false",
            float number => RenderFloating(number),
            double number => RenderFloating(number),
            decimal number => RenderFloating(number),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => throw new GraphException(
                $"Cannot render a literal of type '{value.GetType().Name}' into Cypher text; " +
                "pass the value as a query parameter instead."),
        };
    }

    private static string RenderStringLiteral(string text)
    {
        // Backslashes first: escaping quotes introduces backslashes that must not be re-escaped.
        var escaped = text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
        return $"'{escaped}'";
    }

    private static string RenderFloating<T>(T number)
        where T : IFormattable
    {
        var text = number.ToString(null, CultureInfo.InvariantCulture);

        // Exponent notation is already a valid Cypher float literal; appending ".0" would corrupt it.
        if (text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var digits = text.StartsWith('-') ? text.AsSpan(1) : text.AsSpan();
        if (digits.Length > 0 && !digits.ContainsAnyExceptInRange('0', '9'))
        {
            return text + ".0";
        }

        // NaN and Infinity have no Cypher literal form.
        throw new GraphException($"Cannot render '{text}' as a Cypher float literal.");
    }

    /// <summary>
    /// Renders an expression on behalf of a dialect that owns a clause's syntax
    /// (see <see cref="ICypherDialect.RenderFullTextSearch"/>).
    /// </summary>
    string ICypherRenderContext.RenderExpression(CypherExpression expression) =>
        RenderExpression(expression);

    /// <summary>
    /// Renders a CLR value as a Cypher literal on behalf of a dialect that owns a clause's syntax.
    /// </summary>
    string ICypherRenderContext.RenderLiteral(object? value) => RenderLiteral(value);

    private string[] GetProjectionColumns(CypherStatement statement)
    {
        var projection = statement.Clauses.LastOrDefault(clause => clause is
            ReturnClause or EntityProjectionClause or SetOperationClause);

        return projection switch
        {
            ReturnClause @return => @return.Items
                .Select(item => item.Alias ?? RenderExpression(item.Expression))
                .ToArray(),
            EntityProjectionClause { Shape: EntityProjectionShape.Node } => ["Node"],
            EntityProjectionClause { Shape: EntityProjectionShape.Relationship } => ["Relationship"],
            EntityProjectionClause { IncludePathCoordinates: true } => ["pathIndex", "hopIndex", "PathSegment"],
            EntityProjectionClause => ["PathSegment"],
            SetOperationClause setOperation => GetProjectionColumns(
                new CypherStatement(setOperation.First, statement.Parameters)),
            _ => [],
        };
    }

    private void RenderEntityProjection(StringBuilder builder, EntityProjectionClause projection)
    {
        RenderProjectionOrderingCapture(builder, projection.Ordering);
        RenderEntityProjectionBody(builder, projection);
    }

    private void RenderEntityProjectionBody(StringBuilder builder, EntityProjectionClause projection)
    {
        if (projection.Shape == EntityProjectionShape.Node)
        {
            if (!projection.LoadSourceProperties)
            {
                RenderSimpleNodeProjection(builder, projection);
                return;
            }

            RenderNodePropertyLoad(builder, projection);
            return;
        }

        if (projection.Shape == EntityProjectionShape.Relationship)
        {
            RenderProjectionResultStart(builder, projection.Ordering);
            builder.Append(projection.SourceAlias).Append(" AS Relationship");
            CompleteProjectionResult(builder, projection.Ordering, "Relationship");
            return;
        }

        if (projection.IncludePathCoordinates)
        {
            RenderProjectionResultStart(builder, projection.Ordering);
            builder.Append("pathIndex, hopIndex, { StartNode: { Node: ")
                .Append(projection.SourceAlias)
                .Append(", ComplexProperties: [] }, Relationship: ")
                .Append(projection.RelationshipAlias)
                .Append(", EndNode: { Node: ")
                .Append(projection.TargetAlias)
                .Append(", ComplexProperties: [] } } AS PathSegment");
            CompleteProjectionResult(
                builder,
                projection.Ordering,
                "pathIndex, hopIndex, PathSegment");
            return;
        }

        if (!projection.LoadSourceProperties && !projection.LoadTargetProperties)
        {
            RenderProjectionResultStart(builder, projection.Ordering);
            builder.Append("{\n")
                .Append("    StartNode: { Node: ").Append(projection.SourceAlias).Append(", ComplexProperties: [] },\n")
                .Append("    Relationship: ").Append(projection.RelationshipAlias).Append(",\n")
                .Append("    EndNode: { Node: ").Append(projection.TargetAlias).Append(", ComplexProperties: [] }\n")
                .Append("} AS PathSegment");
            CompleteProjectionResult(builder, projection.Ordering, "PathSegment");
            return;
        }

        RenderPathSegmentPropertyLoad(builder, projection);
    }

    private void RenderProjectionOrderingCapture(
        StringBuilder builder,
        IReadOnlyList<OrderByItem> ordering)
    {
        if (ordering.Count == 0)
        {
            return;
        }

        builder.Append("WITH *");
        for (var index = 0; index < ordering.Count; index++)
        {
            builder.Append(",\n    ")
                .Append(RenderExpression(ordering[index].Expression))
                .Append(" AS ")
                .Append(ProjectionOrderAlias(index));
        }

        builder.AppendLine();
    }

    private static void RenderProjectionOrdering(
        StringBuilder builder,
        IReadOnlyList<OrderByItem> ordering)
    {
        if (ordering.Count == 0)
        {
            return;
        }

        builder.Append("\nORDER BY ");
        for (var index = 0; index < ordering.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(ProjectionOrderAlias(index));
            if (ordering[index].Descending)
            {
                builder.Append(" DESC");
            }
        }
    }

    private static string ProjectionOrderAlias(int index) => $"__projectionOrder{index}";

    private static void AppendProjectionOrderAliases(
        StringBuilder builder,
        IReadOnlyList<OrderByItem> ordering)
    {
        for (var index = 0; index < ordering.Count; index++)
        {
            builder.Append(", ").Append(ProjectionOrderAlias(index));
        }
    }

    private static void AppendAliases(StringBuilder builder, IReadOnlyList<string> aliases)
    {
        foreach (var alias in aliases)
        {
            builder.Append(", ").Append(alias);
        }
    }

    private static void RenderProjectionResultStart(
        StringBuilder builder,
        IReadOnlyList<OrderByItem> ordering)
    {
        builder.Append(ordering.Count == 0 ? "RETURN " : "WITH ");
    }

    private static void CompleteProjectionResult(
        StringBuilder builder,
        IReadOnlyList<OrderByItem> ordering,
        string resultColumns)
    {
        if (ordering.Count == 0)
        {
            return;
        }

        AppendProjectionOrderAliases(builder, ordering);
        RenderProjectionOrdering(builder, ordering);
        builder.Append("\nRETURN ").Append(resultColumns);
    }

    private static void RenderSimpleNodeProjection(
        StringBuilder builder,
        EntityProjectionClause projection)
    {
        RenderProjectionResultStart(builder, projection.Ordering);
        builder.Append("{\n")
            .Append("                Node: ").Append(projection.SourceAlias).Append(",\n")
            .Append("                ComplexProperties: []\n")
            .Append("            } AS Node");
        CompleteProjectionResult(builder, projection.Ordering, "Node");
    }

    private void RenderNodePropertyLoad(StringBuilder builder, EntityProjectionClause projection)
    {
        var alias = projection.SourceAlias;
        builder.Append("OPTIONAL MATCH src_path = (").Append(alias)
            .Append(")-[rels*1..").Append(GraphDataModel.DefaultDepthAllowed).Append("]->(prop)\n")
            .Append("WHERE ALL(rel IN rels WHERE rel.").Append(dialect.ComplexPropertyRelationshipMarker).Append(" = true)\n")
            .Append("WITH ").Append(alias);
        AppendAliases(builder, projection.RowIdentityAliases);
        AppendProjectionOrderAliases(builder, projection.Ordering);
        builder.Append(",\n")
            .Append("    CASE WHEN src_path IS NULL THEN [] ELSE [i IN range(0, size(rels) - 1) | {\n")
            .Append("        ParentNode: CASE WHEN i = 0 THEN ").Append(alias).Append(" ELSE nodes(src_path)[i] END,\n")
            .Append("        Relationship: rels[i],\n")
            .Append("        SequenceNumber: rels[i].SequenceNumber,\n")
            .Append("        Property: nodes(src_path)[i + 1]\n")
            .Append("    }] END AS src_property_path\n")
            .Append("WITH ").Append(alias);
        AppendAliases(builder, projection.RowIdentityAliases);
        AppendProjectionOrderAliases(builder, projection.Ordering);
        builder.Append(", reduce(flat = [], path IN collect(src_property_path) | flat + path) AS src_properties\n");
        RenderProjectionResultStart(builder, projection.Ordering);
        builder.Append("{\n")
            .Append("    Node: ").Append(alias).Append(",\n")
            .Append("    ComplexProperties: src_properties\n")
            .Append("} AS Node");
        CompleteProjectionResult(builder, projection.Ordering, "Node");
    }

    private void RenderPathSegmentPropertyLoad(StringBuilder builder, EntityProjectionClause projection)
    {
        var sourceAlias = projection.SourceAlias;
        var relationshipAlias = projection.RelationshipAlias!;
        var targetAlias = projection.TargetAlias!;

        if (projection.LoadSourceProperties)
        {
            AppendPathPropertyLoad(
                builder,
                projection,
                sourceAlias,
                relationshipAlias,
                targetAlias,
                sourceAlias,
                "src",
                "rels",
                "prop");
            builder.AppendLine();
        }
        else
        {
            builder.Append("WITH ").Append(sourceAlias).Append(", ").Append(relationshipAlias).Append(", ")
                .Append(targetAlias);
            AppendProjectionOrderAliases(builder, projection.Ordering);
            builder.Append(", [] AS src_properties\n");
        }

        if (projection.LoadTargetProperties)
        {
            AppendPathPropertyLoad(
                builder,
                projection,
                sourceAlias,
                relationshipAlias,
                targetAlias,
                targetAlias,
                "tgt",
                "trels",
                "tprop");
            builder.AppendLine();
        }
        else
        {
            builder.Append("WITH ").Append(sourceAlias).Append(", ").Append(relationshipAlias).Append(", ")
                .Append(targetAlias).Append(", src_properties");
            AppendProjectionOrderAliases(builder, projection.Ordering);
            builder.Append(", [] AS tgt_properties\n");
        }

        RenderProjectionResultStart(builder, projection.Ordering);
        builder.Append("{\n")
            .Append("    StartNode: { Node: ").Append(sourceAlias).Append(", ComplexProperties: src_properties },\n")
            .Append("    Relationship: ").Append(relationshipAlias).Append(",\n")
            .Append("    EndNode: { Node: ").Append(targetAlias).Append(", ComplexProperties: tgt_properties }\n")
            .Append("} AS PathSegment");
        CompleteProjectionResult(builder, projection.Ordering, "PathSegment");
    }

    private void AppendPathPropertyLoad(
        StringBuilder builder,
        EntityProjectionClause projection,
        string sourceAlias,
        string relationshipAlias,
        string targetAlias,
        string ownerAlias,
        string prefix,
        string relationshipsAlias,
        string propertyAlias)
    {
        builder.Append("OPTIONAL MATCH ").Append(prefix).Append("_path = (").Append(ownerAlias)
            .Append(")-[").Append(relationshipsAlias).Append("*1..").Append(GraphDataModel.DefaultDepthAllowed)
            .Append("]->(").Append(propertyAlias).Append(")\n")
            .Append("WHERE ALL(rel IN ").Append(relationshipsAlias).Append(" WHERE rel.")
            .Append(dialect.ComplexPropertyRelationshipMarker).Append(" = true)\n")
            .Append("WITH ").Append(sourceAlias).Append(", ").Append(relationshipAlias).Append(", ")
            .Append(targetAlias);
        AppendProjectionOrderAliases(builder, projection.Ordering);
        if (prefix == "tgt")
        {
            builder.Append(", src_properties");
        }

        builder.Append(", CASE WHEN ").Append(prefix).Append("_path IS NULL THEN [] ELSE [i IN range(0, size(")
            .Append(relationshipsAlias).Append(") - 1) | {\n")
            .Append("    ParentNode: CASE WHEN i = 0 THEN ").Append(ownerAlias).Append(" ELSE nodes(")
            .Append(prefix).Append("_path)[i] END,\n")
            .Append("    Relationship: ").Append(relationshipsAlias).Append("[i],\n")
            .Append("    SequenceNumber: ").Append(relationshipsAlias).Append("[i].SequenceNumber,\n")
            .Append("    Property: nodes(").Append(prefix).Append("_path)[i + 1]\n")
            .Append("}] END AS ").Append(prefix).Append("_property_path\n")
            .Append("WITH ").Append(sourceAlias).Append(", ").Append(relationshipAlias).Append(", ")
            .Append(targetAlias);
        AppendProjectionOrderAliases(builder, projection.Ordering);
        if (prefix == "tgt")
        {
            builder.Append(", src_properties");
        }

        builder.Append(", reduce(flat = [], path IN collect(").Append(prefix)
            .Append("_property_path) | flat + path) AS ").Append(prefix).Append("_properties\n");
    }
}
