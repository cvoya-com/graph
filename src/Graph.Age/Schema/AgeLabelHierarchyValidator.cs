// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Schema;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Cvoya.Graph.Age.Querying;
using Cvoya.Graph.Age.Serialization;
using Npgsql;
using NpgsqlTypes;

/// <summary>
/// Detects physical node labels that belong to a registered hierarchy but were never registered
/// with <see cref="AgeTypeHierarchyRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// AGE keeps labels in a flat catalog and has no parent-label metadata. The provider therefore
/// derives inheritance from the <c>inheritance_labels</c> property that it writes on nodes with
/// multiple logical labels. A physical label is reported when it is not itself registered and at
/// least one of its rows declares a registered logical label in <c>inheritance_labels</c>.
/// </para>
/// <para>
/// The containment probe uses the GIN index on <c>properties</c> when one exists and short-circuits
/// on the first matching row, so validation inspects a label table without reading it in full.
/// A label whose rows do not declare the registered base label carries no hierarchy information and
/// cannot be attributed to that hierarchy; it is not reported.
/// </para>
/// </remarks>
internal static class AgeLabelHierarchyValidator
{
    private static readonly HashSet<string> ReservedLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        SerializationBridge.ComplexNodeLabel,
        "_ag_label_vertex",
    };

    /// <summary>
    /// Finds physical node labels whose rows declare a registered logical label but whose own label
    /// is missing from <paramref name="knownLabels"/>.
    /// </summary>
    /// <param name="connection">An AGE-enabled connection.</param>
    /// <param name="transaction">The transaction to enlist the probes in, or <see langword="null"/>.</param>
    /// <param name="graphName">The AGE graph whose catalog is inspected.</param>
    /// <param name="knownLabels">
    /// Every logical label that currently participates in a registered hierarchy, as reported by
    /// <see cref="AgeTypeHierarchyRegistry.GetKnownLabels"/>.
    /// </param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The findings ordered by physical label and registered ancestor.</returns>
    public static async Task<IReadOnlyList<AgeLabelHierarchyFinding>> FindUnregisteredAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string graphName,
        IReadOnlyList<string> knownLabels,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphName);
        ArgumentNullException.ThrowIfNull(knownLabels);

        var logicalLabels = knownLabels
            .Where(label => !ReservedLabels.Contains(label))
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (logicalLabels.Count == 0)
        {
            return [];
        }

        var knownPhysicalLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var logicalLabel in logicalLabels)
        {
            knownPhysicalLabels.Add(SerializationBridge.GetRootStorageName(logicalLabel, relationship: false));
        }

        var physicalLabels = await ReadVertexLabelsAsync(connection, transaction, graphName, cancellationToken)
            .ConfigureAwait(false);
        var unregistered = physicalLabels
            .Where(label => !ReservedLabels.Contains(label) && !knownPhysicalLabels.Contains(label))
            .ToList();
        if (unregistered.Count == 0)
        {
            return [];
        }

        var findings = new List<AgeLabelHierarchyFinding>();
        foreach (var physicalLabel in unregistered)
        {
            foreach (var logicalLabel in logicalLabels)
            {
                if (await DeclaresAncestorAsync(
                        connection,
                        transaction,
                        graphName,
                        physicalLabel,
                        logicalLabel,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    findings.Add(new AgeLabelHierarchyFinding(physicalLabel, logicalLabel));
                    break;
                }
            }
        }

        return findings;
    }

    private static async Task<List<string>> ReadVertexLabelsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string graphName,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = """
            SELECT label.name
            FROM ag_catalog.ag_label AS label
            JOIN ag_catalog.ag_graph AS graph ON graph.graphid = label.graph
            WHERE graph.name = @graphName
              AND label.kind = 'v'
            ORDER BY label.name
            """;
        command.Parameters.AddWithValue("graphName", graphName);

        var labels = new List<string>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerLease = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            labels.Add(reader.GetString(0));
        }

        return labels;
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The graph name and physical label are catalog-vetted AGE identifiers validated " +
            "by AgeSqlIdentifier; the ancestor label is bound as a text parameter.")]
    private static async Task<bool> DeclaresAncestorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string graphName,
        string physicalLabel,
        string ancestorLabel,
        CancellationToken cancellationToken)
    {
        var graph = AgeSqlIdentifier.Quote(graphName, "graph name");
        var table = AgeSqlIdentifier.Quote(physicalLabel, "physical node label");

        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT EXISTS (
                SELECT 1
                FROM {graph}.{table}
                WHERE properties OPERATOR(ag_catalog.@>) @containment::ag_catalog.agtype)
            """;
        command.Parameters.Add(new NpgsqlParameter("containment", NpgsqlDbType.Text)
        {
            Value = JsonSerializer.Serialize(new Dictionary<string, string[]>
            {
                ["inheritance_labels"] = [ancestorLabel],
            }),
        });

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}
