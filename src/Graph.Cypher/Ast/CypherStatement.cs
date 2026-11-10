// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

using System.Collections.ObjectModel;
using Cvoya.Graph.Cypher.Internal;

namespace Cvoya.Graph.Cypher.Ast;

/// <summary>
/// Represents a complete Cypher statement and its parameter values.
/// </summary>
public sealed record CypherStatement
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CypherStatement"/> class.
    /// </summary>
    /// <param name="clauses">The ordered clauses that make up the statement.</param>
    /// <param name="parameters">The parameter values available to the statement.</param>
    public CypherStatement(IReadOnlyList<ICypherClause> clauses, IReadOnlyDictionary<string, object?> parameters)
        : this(clauses, parameters, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CypherStatement"/> class.
    /// </summary>
    /// <param name="clauses">The ordered clauses that make up the statement.</param>
    /// <param name="parameters">The parameter values available to the statement.</param>
    /// <param name="pathTypes">The path materialization types, when the statement returns decomposed paths.</param>
    public CypherStatement(
        IReadOnlyList<ICypherClause> clauses,
        IReadOnlyDictionary<string, object?> parameters,
        CypherPathTypes? pathTypes)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        Clauses = ArgumentValidation.RequiredList(clauses, nameof(clauses));
        Parameters = CopyParameters(parameters);
        PathTypes = pathTypes;
    }

    /// <summary>
    /// Gets the ordered clauses that make up the statement.
    /// </summary>
    public IReadOnlyList<ICypherClause> Clauses { get; }

    /// <summary>
    /// Gets the parameter values available to the statement.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; }

    /// <summary>Gets path materialization metadata, when present.</summary>
    public CypherPathTypes? PathTypes { get; }

    /// <summary>
    /// Gets row shaping that a provider must apply around the rendered statement because its
    /// engine cannot express the clauses after a <c>UNION</c>.
    /// </summary>
    /// <remarks>
    /// Provider-internal. The shared planner never sets this; a provider lowering pass sets it
    /// only when it moved global ordering, paging, or aggregate combination out of Cypher.
    /// </remarks>
    internal CypherPostProcessing? PostProcessing { get; init; }

    private static ReadOnlyDictionary<string, object?> CopyParameters(IReadOnlyDictionary<string, object?> parameters)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (name, value) in parameters)
        {
            var validatedName = ArgumentValidation.RequiredName(name, nameof(parameters));
            copy.Add(validatedName, value);
        }

        return new ReadOnlyDictionary<string, object?>(copy);
    }
}
