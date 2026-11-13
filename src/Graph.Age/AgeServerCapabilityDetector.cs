// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age;

using Npgsql;

/// <summary>
/// Detects the installed Apache AGE version and the optional server features the provider can use.
/// </summary>
internal static class AgeServerCapabilityDetector
{
    private static readonly Version NativeShortestPathMinimumVersion = new(1, 8, 0);

    private const string VersionSql = "SELECT extversion FROM pg_extension WHERE extname = 'age'";

    // Both functions were introduced by AGE 1.8.0. Probing the catalog (rather than trusting the
    // extension version alone) keeps feature detection correct for partial or distro-patched builds.
    private const string NativeShortestPathProbeSql =
        "SELECT " +
        "to_regprocedure('ag_catalog.age_shortest_path(ag_catalog.agtype,ag_catalog.agtype,ag_catalog.agtype,ag_catalog.agtype,ag_catalog.agtype,ag_catalog.agtype,ag_catalog.agtype)') IS NOT NULL " +
        "AND " +
        "to_regprocedure('ag_catalog.age_all_shortest_paths(ag_catalog.agtype,ag_catalog.agtype,ag_catalog.agtype,ag_catalog.agtype,ag_catalog.agtype,ag_catalog.agtype,ag_catalog.agtype)') IS NOT NULL";

    public static async Task<AgeServerCapabilities> DetectAsync(
        NpgsqlConnection connection,
        AgeGraphStoreOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        var version = await ReadVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        var supportsVersionedFeatures = version >= NativeShortestPathMinimumVersion;

        var features = AgeFeature.None;

        if (supportsVersionedFeatures)
        {
            if (options.NativePredicateFunctions != AgeFeatureMode.Disabled)
            {
                features |= AgeFeature.NativePredicateFunctions;
            }

            if (options.NativeShortestPath != AgeFeatureMode.Disabled)
            {
                var hasFunctions = await ProbeNativeShortestPathAsync(connection, cancellationToken)
                    .ConfigureAwait(false);
                if (hasFunctions)
                {
                    features |= AgeFeature.NativeShortestPath;
                }
                else if (options.NativeShortestPath == AgeFeatureMode.Enabled)
                {
                    throw new GraphException(
                        $"Apache AGE {version} does not expose the native shortest-path functions " +
                        "'ag_catalog.age_shortest_path' and 'ag_catalog.age_all_shortest_paths', " +
                        "which AgeGraphStoreOptions.NativeShortestPath requires.");
                }
            }
        }
        else if (options.NativeShortestPath == AgeFeatureMode.Enabled)
        {
            throw new GraphException(
                $"AgeGraphStoreOptions.NativeShortestPath requires Apache AGE 1.8.0 or later, " +
                $"but the connected server has AGE {version}.");
        }
        else if (options.NativePredicateFunctions == AgeFeatureMode.Enabled)
        {
            throw new GraphException(
                $"AgeGraphStoreOptions.NativePredicateFunctions requires Apache AGE 1.8.0 or later, " +
                $"but the connected server has AGE {version}.");
        }

        return new AgeServerCapabilities(version, features);
    }

    private static async Task<Version> ReadVersionAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.CommandText = VersionSql;

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not string text || string.IsNullOrWhiteSpace(text))
        {
            throw new GraphException(
                "The connected PostgreSQL database does not have the Apache AGE extension installed.");
        }

        if (!Version.TryParse(text, out var version))
        {
            throw new GraphException(
                $"The connected PostgreSQL database reports an unrecognized Apache AGE version '{text}'.");
        }

        return version;
    }

    private static async Task<bool> ProbeNativeShortestPathAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.CommandText = NativeShortestPathProbeSql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}
