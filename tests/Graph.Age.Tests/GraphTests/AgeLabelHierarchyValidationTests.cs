// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Cvoya.Graph.Age;
using Microsoft.Extensions.Logging;
using Npgsql;
using Npgsql.Age;

/// <summary>
/// Covers NUL-48 F4: a physical label that belongs to a registered hierarchy but was never
/// registered with <see cref="AgeGraphStore.RegisterLabelHierarchy(string, string)"/> is silently
/// omitted from base-label queries.
/// </summary>
public sealed class AgeLabelHierarchyValidationTests(AgeGraphCleanupFixture graphCleanup)
{
    private const string RootLabel = "ValidationRoot";

    private static readonly string ConnectionString = Environment.GetEnvironmentVariable("AGE_CONNECTION_STRING")
        ?? "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres";

    [Node(RootLabel)]
    public record ValidationRoot : Node
    {
        public string Name { get; set; } = string.Empty;
    }

    [Node("ValidationClrRoot582")]
    public record ValidationClrRoot582 : Node
    {
        public string Name { get; set; } = string.Empty;
    }

    [Node("ValidationClrDerived582")]
    public record ValidationClrDerived582 : ValidationClrRoot582;

    [Fact]
    public async Task UnregisteredChildLabel_IsReportedAndFailsStartup_UntilItIsRegistered()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var registeredChild = $"ValidationRegistered_{Guid.NewGuid():N}";
        var missingChild = $"ValidationMissing_{Guid.NewGuid():N}";
        AgeGraphStore.RegisterLabelHierarchy(RootLabel, registeredChild);

        var graphName = graphCleanup.CreateGraphName("cvoya_label_validation");
        await using var dataSource = CreateDataSource();
        var logs = new CapturingLoggerProvider();
        await using var store = new AgeGraphStore(dataSource, graphName, loggerFactory: logs);
        store.LabelHierarchyValidation = AgeLabelHierarchyValidationMode.Off;
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        try
        {
            await CreateVertexLabelAsync(dataSource, graphName, RootLabel, cancellationToken);
            await CreateVertexLabelAsync(dataSource, graphName, registeredChild, cancellationToken);
            await CreateVertexLabelAsync(dataSource, graphName, missingChild, cancellationToken);
            await InsertRowsAsync(dataSource, graphName, RootLabel, [RootLabel], rowCount: 1, cancellationToken);
            await InsertRowsAsync(
                dataSource,
                graphName,
                registeredChild,
                [RootLabel, registeredChild],
                rowCount: 2,
                cancellationToken);
            await InsertRowsAsync(
                dataSource,
                graphName,
                missingChild,
                [RootLabel, missingChild],
                rowCount: 3,
                cancellationToken);

            // The registered child is visible; the equally populated, unregistered child is not.
            var visibleBeforeRegistration = await store.Graph.Nodes<ValidationRoot>().ToListAsync(cancellationToken);
            Assert.Equal(3, visibleBeforeRegistration.Count);

            var findings = await store.FindUnregisteredLabelHierarchyAsync(cancellationToken);
            var finding = Assert.Single(findings);
            Assert.Equal(missingChild, finding.PhysicalLabel);
            Assert.Equal(RootLabel, finding.RegisteredAncestor);

            // Warn mode reports the same condition without failing the operation.
            store.LabelHierarchyValidation = AgeLabelHierarchyValidationMode.Warn;
            await store.CreateGraphIfNotExistsAsync(cancellationToken);
            Assert.Contains(
                logs.Messages,
                message => message.Contains(missingChild, StringComparison.Ordinal) &&
                    message.Contains("not registered", StringComparison.Ordinal));

            store.LabelHierarchyValidation = AgeLabelHierarchyValidationMode.Fail;
            var exception = await Assert.ThrowsAsync<GraphException>(
                () => store.CreateGraphIfNotExistsAsync(cancellationToken));
            Assert.Contains(missingChild, exception.Message, StringComparison.Ordinal);
            Assert.Contains(RootLabel, exception.Message, StringComparison.Ordinal);

            // Registering the missing pair makes the rows visible and the startup validation pass.
            AgeGraphStore.RegisterLabelHierarchy(RootLabel, missingChild);
            store.LabelHierarchyValidation = AgeLabelHierarchyValidationMode.Warn;
            Assert.Empty(await store.FindUnregisteredLabelHierarchyAsync(cancellationToken));

            await store.CreateGraphIfNotExistsAsync(cancellationToken);
            var visibleAfterRegistration = await store.Graph.Nodes<ValidationRoot>().ToListAsync(cancellationToken);
            Assert.Equal(6, visibleAfterRegistration.Count);
        }
        finally
        {
            AgeGraphStore.RegisterLabelHierarchy(RootLabel, missingChild);
        }
    }

    [Fact]
    public async Task FirstOperationInFailMode_DetectsUnregisteredPhysicalLabel_WithoutPriorQuery()
    {
        // The CLR hierarchy of ValidationClrRoot582 is discovered only from the store's schema; no
        // query may run before the validation, otherwise lazy discovery would mask the gap.
        var cancellationToken = TestContext.Current.CancellationToken;
        var missingChild = $"ValidationClrMissing_{Guid.NewGuid():N}";

        var graphName = graphCleanup.CreateGraphName("cvoya_label_validation_clr");
        await using var dataSource = CreateDataSource();
        await using var setupStore = new AgeGraphStore(dataSource, graphName, loggerFactory: new CapturingLoggerProvider());
        setupStore.LabelHierarchyValidation = AgeLabelHierarchyValidationMode.Off;
        await setupStore.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await CreateVertexLabelAsync(dataSource, graphName, "ValidationClrRoot582", cancellationToken);
        await CreateVertexLabelAsync(dataSource, graphName, "ValidationClrDerived582", cancellationToken);
        await CreateVertexLabelAsync(dataSource, graphName, missingChild, cancellationToken);
        await InsertRowsAsync(
            dataSource,
            graphName,
            missingChild,
            ["ValidationClrRoot582", missingChild],
            rowCount: 1,
            cancellationToken);

        await using var failStore = new AgeGraphStore(dataSource, graphName, loggerFactory: new CapturingLoggerProvider());
        failStore.LabelHierarchyValidation = AgeLabelHierarchyValidationMode.Fail;
        var exception = await Assert.ThrowsAsync<GraphException>(
            () => failStore.CreateGraphIfNotExistsAsync(cancellationToken));
        Assert.Contains(missingChild, exception.Message, StringComparison.Ordinal);
        Assert.Contains("ValidationClrRoot582", exception.Message, StringComparison.Ordinal);
    }

    private static NpgsqlDataSource CreateDataSource()
    {
        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.UseAge();
        return builder.Build();
    }

    private static async Task<NpgsqlConnection> OpenAgeConnectionAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "LOAD 'age'; SET search_path = ag_catalog, \"$user\", public";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private sealed class CapturingLoggerProvider : ILoggerFactory, ILogger
    {
        public List<string> Messages { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The graph name and label are test-controlled identifiers validated by " +
            "AgeSqlIdentifier and embedded as quoted literals because AGE declares cstring parameters.")]
    private static async Task CreateVertexLabelAsync(
        NpgsqlDataSource dataSource,
        string graphName,
        string label,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAgeConnectionAsync(dataSource, cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.CommandText =
            $"SELECT ag_catalog.create_vlabel('{graphName.Replace("'", "''", StringComparison.Ordinal)}', " +
            $"'{label.Replace("'", "''", StringComparison.Ordinal)}')";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The graph name and label are test-controlled identifiers validated by " +
            "AgeSqlIdentifier; values are bound as parameters.")]
    private static async Task InsertRowsAsync(
        NpgsqlDataSource dataSource,
        string graphName,
        string label,
        IReadOnlyList<string> inheritanceLabels,
        int rowCount,
        CancellationToken cancellationToken)
    {
        var table = $"{AgeSqlIdentifier.Quote(graphName, "graph name")}." +
            $"{AgeSqlIdentifier.Quote(label, "node label")}";
        var properties = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Name"] = label,
            ["inheritance_labels"] = inheritanceLabels,
        });

        await using var connection = await OpenAgeConnectionAsync(dataSource, cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.CommandText = $"""
            INSERT INTO {table} (properties)
            SELECT @properties::ag_catalog.agtype
            FROM generate_series(1, @rowCount)
            """;
        command.Parameters.AddWithValue("properties", properties);
        command.Parameters.AddWithValue("rowCount", rowCount);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
