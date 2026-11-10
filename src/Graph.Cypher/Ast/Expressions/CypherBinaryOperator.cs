// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Cypher.Ast.Expressions;

/// <summary>
/// Defines supported Cypher binary operators.
/// </summary>
public enum CypherBinaryOperator
{
    /// <summary>
    /// Logical conjunction.
    /// </summary>
    And,

    /// <summary>
    /// Logical disjunction.
    /// </summary>
    Or,

    /// <summary>
    /// Equality comparison.
    /// </summary>
    Equal,

    /// <summary>
    /// Inequality comparison.
    /// </summary>
    NotEqual,

    /// <summary>
    /// Less-than comparison.
    /// </summary>
    LessThan,

    /// <summary>
    /// Less-than-or-equal comparison.
    /// </summary>
    LessThanOrEqual,

    /// <summary>
    /// Greater-than comparison.
    /// </summary>
    GreaterThan,

    /// <summary>
    /// Greater-than-or-equal comparison.
    /// </summary>
    GreaterThanOrEqual,

    /// <summary>
    /// Addition.
    /// </summary>
    Add,

    /// <summary>
    /// Subtraction.
    /// </summary>
    Subtract,

    /// <summary>
    /// Multiplication.
    /// </summary>
    Multiply,

    /// <summary>
    /// Division.
    /// </summary>
    Divide,

    /// <summary>
    /// Modulo.
    /// </summary>
    Modulo,

    /// <summary>
    /// Prefix string comparison.
    /// </summary>
    StartsWith,

    /// <summary>
    /// Suffix string comparison.
    /// </summary>
    EndsWith,

    /// <summary>
    /// Containment comparison.
    /// </summary>
    Contains,

    /// <summary>
    /// Collection membership comparison.
    /// </summary>
    In,

    /// <summary>
    /// Regular expression comparison.
    /// </summary>
    Matches,

    /// <summary>
    /// Map containment comparison (<c>@&gt;</c>): the left map contains every key/value pair of
    /// the right map.
    /// </summary>
    /// <remarks>
    /// Only dialects that expose a native containment operator can render this comparison. The
    /// Apache AGE dialect uses it to keep a hierarchy label filter on the GIN-indexed
    /// <c>properties</c> column instead of scanning an array property.
    /// </remarks>
    PropertyContainment,

    /// <summary>
    /// Property-key existence test (<c>?</c>): the left map contains the named key.
    /// </summary>
    /// <remarks>
    /// Only dialects that expose a native key-existence operator can render this comparison. The
    /// Apache AGE dialect uses the negated form for missing-property filters so the predicate no
    /// longer has to evaluate <c>agtype_access_operator</c> for every row.
    /// </remarks>
    PropertyKeyExists
}
