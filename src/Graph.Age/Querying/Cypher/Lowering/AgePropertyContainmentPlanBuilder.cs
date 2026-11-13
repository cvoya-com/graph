// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying.Cypher.Lowering;

using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.Age.Schema;
using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;
using BinaryExpression = Cvoya.Graph.Cypher.Ast.Expressions.BinaryExpression;

/// <summary>
/// Recognizes a single-label equality filter on a string property and answers it with the GIN
/// containment operator on <c>properties</c>.
/// </summary>
/// <remarks>
/// The ordinary lowering renders <c>agtype_access_operator(properties, '"Prop"') = $p</c>, which
/// PostgreSQL cannot turn into an index scan. For a top-level string property the equivalent
/// <c>properties @&gt; jsonb_build_object('Prop', $p)</c> uses the existing GIN index. The builder is
/// deliberately narrow: one plain root match, one equality predicate, a string CLR member, and the
/// empty-complex-property entity projection. Anything else keeps the AGE path.
/// </remarks>
internal static class AgePropertyContainmentPlanBuilder
{
    internal static AgeSqlPlan? TryBuild(
        CypherStatement statement,
        Expression queryExpression,
        AgeIndexCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(queryExpression);
        ArgumentNullException.ThrowIfNull(catalog);

        if (statement.PostProcessing is not null ||
            statement.Clauses.Count != 3 ||
            statement.Clauses[0] is not MatchClause { Optional: false, Patterns.Count: 1 } match ||
            statement.Clauses[1] is not WhereClause where ||
            statement.Clauses[2] is not ReturnClause { Distinct: false } @return ||
            match.Patterns[0].Elements is not [NodePattern { Alias: not null, Labels.Count: 1 } node])
        {
            return null;
        }

        if (where.Predicate is not BinaryExpression { Op: CypherBinaryOperator.Equal } binary ||
            !TryGetProperty(binary.Left, node.Alias!, out var property))
        {
            return null;
        }

        // AGE tables are keyed by the physical label. Resolve once so a non-native logical label
        // selects the same companion table as its hierarchy branches.
        var physicalLabel = catalog.ResolveLabel(node.Labels[0]);
        if (!catalog.TryGetIndex(physicalLabel, property, PropertyIndexKind.Equality, out _))
        {
            return null;
        }

        var stringMember = new StringMemberFinder(property);
        stringMember.Visit(queryExpression);
        if (stringMember.Count == 0)
        {
            return null;
        }

        string? parameterName = null;
        string? literal = null;
        switch (binary.Right)
        {
            case QueryParameter parameter:
                parameterName = parameter.Name;
                break;
            case Literal { Value: string text }:
                literal = text;
                break;
            default:
                return null;
        }

        if (!TryGetEntityProjection(@return, node.Alias!, out var projectionAlias))
        {
            return null;
        }

        var valueExpression = parameterName is not null
            ? "jsonb_build_object(" +
              AgeIndexTable.QuoteLiteral(property, "property name") + ", ((" +
              AgeIndexTable.ParameterToken + "::text::jsonb) ->> " +
              AgeIndexTable.QuoteLiteral(parameterName, "parameter name") + "))"
            : "jsonb_build_object(" +
              AgeIndexTable.QuoteLiteral(property, "property name") + ", " +
              AgeIndexTable.QuoteLiteral(literal!, "property value") + ")";
        var sql = new StringBuilder()
            .Append("SELECT ag_catalog.agtype_build_map(")
            .Append("'Node'::text, ag_catalog.agtype_typecast_vertex(ag_catalog.agtype_build_map(")
            .Append("'id'::text, __src.id, 'label'::text, ")
            .Append(AgeIndexTable.QuoteLiteral(physicalLabel, "label name"))
            .Append(", 'properties'::text, __src.properties)), ")
            .Append("'ComplexProperties'::text, ag_catalog.agtype_build_list()) AS ")
            .Append(AgeIndexTable.QuoteIdentifier(projectionAlias, "projection alias"))
            .Append(" FROM ").Append(AgeIndexTable.GraphToken).Append('.')
            .Append(AgeIndexTable.QuoteIdentifier(physicalLabel, "label name"))
            .Append(" AS __src WHERE __src.properties @> (")
            .Append(valueExpression)
            .Append(")::text::ag_catalog.agtype");
        if (parameterName is not null)
        {
            sql.Append(" AND ((").Append(AgeIndexTable.ParameterToken)
                .Append("::text::jsonb) ->> ")
                .Append(AgeIndexTable.QuoteLiteral(parameterName, "parameter name"))
                .Append(") IS NOT NULL");
        }

        return new AgeSqlPlan([physicalLabel], sql.ToString(), "containment", [physicalLabel]);
    }

    private static bool TryGetProperty(CypherExpression expression, string alias, out string property)
    {
        switch (expression)
        {
            case PropertyAccess { Target: VariableRef { Alias: var target }, Property: var name }
                when string.Equals(target, alias, StringComparison.Ordinal):
                property = name;
                return true;
            case EscapedPropertyAccess { Target: VariableRef { Alias: var target }, Property: var name }
                when string.Equals(target, alias, StringComparison.Ordinal):
                property = name;
                return true;
            default:
                property = string.Empty;
                return false;
        }
    }

    private static bool TryGetEntityProjection(ReturnClause @return, string alias, out string projectionAlias)
    {
        projectionAlias = string.Empty;
        if (@return.Items is not [ReturnItem { Alias: not null } item] ||
            item.Expression is not MapExpression { Entries.Count: 2 } map)
        {
            return false;
        }

        var node = map.Entries.FirstOrDefault(entry => entry.Key == "Node");
        var complex = map.Entries.FirstOrDefault(entry => entry.Key == "ComplexProperties");
        if (node?.Value is not VariableRef { Alias: var nodeAlias } ||
            !string.Equals(nodeAlias, alias, StringComparison.Ordinal) ||
            complex?.Value is not ListExpression { Items.Count: 0 })
        {
            return false;
        }

        projectionAlias = AgeEntityProjectionPass.NormalizeProjectionColumn(item.Alias!);
        return true;
    }

    private sealed class StringMemberFinder(string property) : ExpressionVisitor
    {
        public int Count { get; private set; }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Type == typeof(string) &&
                string.Equals(StoredName(node.Member), property, StringComparison.Ordinal))
            {
                Count++;
            }

            return base.VisitMember(node);
        }

        private static string StoredName(MemberInfo member) =>
            member is PropertyInfo propertyInfo &&
            propertyInfo.GetCustomAttribute<PropertyAttribute>(inherit: true)?.Label is { Length: > 0 } label
                ? label
                : member.Name.Replace("`", string.Empty, StringComparison.Ordinal);
    }
}
