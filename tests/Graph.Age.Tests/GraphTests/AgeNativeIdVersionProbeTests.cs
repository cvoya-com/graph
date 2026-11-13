// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using Npgsql;

/// <summary>
/// Covers the fresh-database version probe: a store opened before the age extension exists must not
/// permanently select the portable native-id lowering, and must detect the version after
/// provisioning creates the extension.
/// </summary>
/// <remarks>
/// The data source is created without <c>UseAge()</c> on purpose. That helper runs
/// <c>CREATE EXTENSION IF NOT EXISTS age</c> on the first connection, which would hide the
/// extension-absent path; the store's own probe and <c>LOAD 'age'</c> sequence are still valid
/// against an externally owned data source.
/// </remarks>
public sealed class AgeNativeIdVersionProbeTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("AGE_CONNECTION_STRING")
        ?? "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres";

    [Fact]
    public async Task ExtensionCreatedAfterFirstOpen_IsDetectedOnANextConnection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databaseName = "nul103_probe_" + Guid.NewGuid().ToString("N");
        var adminConnectionString = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = "postgres",
        }.ConnectionString;
        var scratchConnectionString = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;

        await using var admin = new NpgsqlDataSourceBuilder(adminConnectionString).Build();
        await ExecuteAsync(admin, $"CREATE DATABASE \"{databaseName}\"", cancellationToken);

        AgeGraphStore? store = null;
        NpgsqlDataSource? dataSource = null;
        try
        {
            // Deliberately not UseAge(): the extension stays absent until the test provisions it.
            dataSource = new NpgsqlDataSourceBuilder(scratchConnectionString).Build();
            store = new AgeGraphStore(dataSource, "cvoya_native_id_probe");

            // The fresh database has no age extension. Opening the connection must succeed through
            // LOAD 'age', leave the version unknown, and keep the portable lowering.
            await using (var connection = await store.OpenConnectionAsync(cancellationToken))
            {
                Assert.Equal(databaseName, connection.Database);
            }

            Assert.False(store.SupportsNativeIdInPredicates);
            Assert.Equal(1, store.NativeIdInPredicateProbeCount);

            // Absence is not cached as a version: the next connection probes again.
            await using (var connection = await store.OpenConnectionAsync(cancellationToken))
            {
            }

            Assert.False(store.SupportsNativeIdInPredicates);
            Assert.Equal(2, store.NativeIdInPredicateProbeCount);

            // Provisioning creates the extension after the store first opened.
            await using (var provisioning = new NpgsqlConnection(scratchConnectionString))
            {
                await provisioning.OpenAsync(cancellationToken);
                await ExecuteAsync(provisioning, "CREATE EXTENSION IF NOT EXISTS age", cancellationToken);

                var version = await ScalarAsync(
                    provisioning,
                    "SELECT extversion FROM pg_extension WHERE extname = 'age'",
                    cancellationToken);
                Assert.NotNull(version);

                // The next connection must observe the new extension instead of staying on the
                // portable path for the lifetime of the store.
                await using (var connection = await store.OpenConnectionAsync(cancellationToken))
                {
                }

                Assert.Equal(3, store.NativeIdInPredicateProbeCount);
                Assert.Equal(
                    Version.Parse(version!) < new Version(1, 8),
                    store.SupportsNativeIdInPredicates);

                // A known version is terminal; later connections do not probe again.
                await using (var connection = await store.OpenConnectionAsync(cancellationToken))
                {
                }

                Assert.Equal(3, store.NativeIdInPredicateProbeCount);
            }
        }
        finally
        {
            if (store is not null)
            {
                await store.DisposeAsync();
            }

            if (dataSource is not null)
            {
                await dataSource.DisposeAsync();
            }

            await ExecuteAsync(
                admin,
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
                CancellationToken.None);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100",
        Justification = "Test-constructed SQL over a generated GUID database name; no user input.")]
    private static async Task ExecuteAsync(
        NpgsqlDataSource dataSource,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(commandText);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100",
        Justification = "Test-constructed SQL over a generated GUID database name; no user input.")]
    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100",
        Justification = "Fixed catalog query; no user input.")]
    private static async Task<string?> ScalarAsync(
        NpgsqlConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }
}
