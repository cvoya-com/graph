// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying.Cypher.Execution;

using System.Runtime.CompilerServices;
using Cvoya.Graph.Age.Core;
using Cvoya.Graph.Cypher.Ast;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

internal sealed class CypherExecutor
{
    private readonly ILogger<CypherExecutor> logger;

    public CypherExecutor(ILoggerFactory? loggerFactory = null)
    {
        logger = loggerFactory?.CreateLogger<CypherExecutor>() ?? NullLogger<CypherExecutor>.Instance;
    }

    public Task<List<AgeRecord>> ExecuteAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<string> projectionColumns,
        AgeGraphTransaction transaction,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(cypher, parameters, projectionColumns, postProcessing: null, transaction, cancellationToken);

    public async Task<List<AgeRecord>> ExecuteAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<string> projectionColumns,
        CypherPostProcessing? postProcessing,
        AgeGraphTransaction transaction,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(
                new AgeCommand(cypher, parameters, projectionColumns, postProcessing, Plan: null),
                transaction,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<List<AgeRecord>> ExecuteAsync(
        AgeCommand command,
        AgeGraphTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cursor = await transaction.Runner.RunAsync(
            command.Text,
            command.Parameters,
            command.ProjectionColumns,
            command.PostProcessing,
            command.Plan,
            cancellationToken).ConfigureAwait(false);
        await using var cursorLease = cursor.ConfigureAwait(false);
        var records = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        logger.LogDebugCypherExecutor35(records.Count);
        return records;
    }

    public static IAsyncEnumerable<AgeRecord> StreamAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<string> projectionColumns,
        AgeGraphTransaction transaction,
        CancellationToken cancellationToken = default) =>
        StreamAsync(cypher, parameters, projectionColumns, postProcessing: null, transaction, cancellationToken);

    public static async IAsyncEnumerable<AgeRecord> StreamAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<string> projectionColumns,
        CypherPostProcessing? postProcessing,
        AgeGraphTransaction transaction,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var record in StreamAsync(
            new AgeCommand(cypher, parameters, projectionColumns, postProcessing, Plan: null),
            transaction,
            cancellationToken).ConfigureAwait(false))
        {
            yield return record;
        }
    }

    public static async IAsyncEnumerable<AgeRecord> StreamAsync(
        AgeCommand command,
        AgeGraphTransaction transaction,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cursor = await transaction.Runner.RunStreamingAsync(
            command.Text,
            command.Parameters,
            command.ProjectionColumns,
            command.PostProcessing,
            command.Plan,
            cancellationToken).ConfigureAwait(false);
        await using var cursorLease = cursor.ConfigureAwait(false);
        while (await cursor.FetchAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return cursor.Current;
        }
    }
}
