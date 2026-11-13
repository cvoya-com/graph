// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Graph.Performance.Tests;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cvoya.Graph;
using Cvoya.Graph.Age;
using Npgsql;
using Npgsql.Age;

/// <summary>
/// Measures the nGen-shaped declared-index scenarios (L08, L11, L12, L16-L18, L19) against the
/// ordinary AGE path and against the path selected by definitions registered through
/// <see cref="SchemaRegistry"/>.
/// </summary>
/// <remarks>
/// The harness is a documented extension of <c>Graph.Performance.Tests</c>. It does not contain
/// provider code and it does not create physical structures itself: the companion tables come from
/// <see cref="IGraphIndexMaintenance.EnsureIndexesAsync"/>. The only deployment setup is a GIN index
/// on the AGE <c>properties</c> column, which nGen's production graphs already have; it is required
/// for the L08 containment shape.
/// </remarks>
internal static class AgeIndexBenchmarkHarness
{
    private const string BaseLabel = "NgenContract";
    private const string ChildLabel = "NgenKaufContract";
    private const string DocumentLabel = "NgenDocument";
    private const string DocumentChildLabel = "NgenDocumentChild";
    private const string OpenDocumentLabel = "NgenOpenDocument";
    private const string OpenDocumentChildLabel = "NgenOpenDocumentChild";
    private const string LookupLabel = "NgenLookup";
    private const string DefaultConnectionString =
        "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly string[] Statuses =
    [
        "aktiv",
        "inaktiv",
        "entwurf",
        "archiviert",
        "gesperrt",
        "neu",
    ];

    /// <summary>Runs the harness and writes JSON and Markdown results.</summary>
    internal static async Task RunAsync(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("AGE_CONNECTION_STRING") ?? DefaultConnectionString;
        var nodeCount = ParseOption(args, "--nodes", 30_000);
        var iterations = ParseOption(args, "--iterations", 20);
        var warmups = ParseOption(args, "--warmups", 3);
        var outputDirectory = GetOption(args, "--output") ?? Path.Combine(Directory.GetCurrentDirectory(), "benchmarks");
        var graphName = GetOption(args, "--graph") ?? "cvoya_age_index_benchmark";
        var nonSealed = args.Contains("--non-sealed", StringComparer.Ordinal);

        Directory.CreateDirectory(outputDirectory);
        var cancellationToken = CancellationToken.None;

        await DropGraphAsync(connectionString, graphName, cancellationToken);

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseAge();
        await using var dataSource = builder.Build();

        AgeGraphStore.RegisterLabelHierarchy(BaseLabel, ChildLabel);
        if (nonSealed)
        {
            AgeGraphStore.RegisterLabelHierarchy(OpenDocumentLabel, OpenDocumentChildLabel);
        }
        else
        {
            AgeGraphStore.RegisterLabelHierarchy(DocumentLabel, DocumentChildLabel);
        }

        var registry = CreateNgenRegistry(nonSealed);
        await using var baselineStore = new AgeGraphStore(dataSource, graphName, new SchemaRegistry());
        await using var declaredStore = new AgeGraphStore(dataSource, graphName, registry);
        await baselineStore.CreateGraphIfNotExistsAsync(cancellationToken);

        Console.WriteLine(
            "Seeding {0:N0} nGen-shaped nodes into '{1}' (base {2}, child {3}, document address {4})...",
            nodeCount,
            graphName,
            BaseLabel,
            ChildLabel,
            nonSealed ? "non-sealed" : "sealed");
        var seedStopwatch = Stopwatch.StartNew();
        await SeedAsync(baselineStore.Graph, nodeCount, nonSealed, cancellationToken);
        seedStopwatch.Stop();
        Console.WriteLine("Seeded in {0:F1} s.", seedStopwatch.Elapsed.TotalSeconds);

        await CreateGinIndexesAsync(connectionString, graphName, nonSealed, cancellationToken);

        var unicodeTarget = BuildUnicodeName(nodeCount);
        var baselineScenarios = CreateScenarios(baselineStore.Graph, unicodeTarget, nonSealed, cancellationToken);
        var declaredScenarios = CreateScenarios(declaredStore.Graph, unicodeTarget, nonSealed, cancellationToken);

        Console.WriteLine("Measuring baseline AGE path...");
        var baseline = new Dictionary<string, Measurement>(StringComparer.Ordinal);
        foreach (var scenario in baselineScenarios)
        {
            baseline[scenario.Id] = await MeasureAsync(
                scenario.Run,
                warmups,
                iterations,
                cancellationToken);
            Console.WriteLine(
                "  {0}: median {1:F3} ms, p95 {2:F3} ms",
                scenario.Id,
                baseline[scenario.Id].MedianMs,
                baseline[scenario.Id].P95Ms);
        }

        Console.WriteLine("Ensuring declared indexes...");
        var ensure = await declaredStore.EnsureIndexesAsync(cancellationToken);
        Console.WriteLine(
            "  inspected {0}, created {1}, backfilled {2:N0} rows",
            ensure.DefinitionsInspected,
            ensure.StructuresCreated,
            ensure.RowsBackfilled);

        var l08Matches = await declaredStore.Graph.Nodes<NgenLookup>()
            .Where(lookup => lookup.Name == unicodeTarget)
            .ToListAsync(cancellationToken);
        Console.WriteLine("L08 matching rows: {0}", l08Matches.Count);

        Console.WriteLine("Measuring declared-index path...");
        var declared = new Dictionary<string, Measurement>(StringComparer.Ordinal);
        foreach (var scenario in declaredScenarios)
        {
            declared[scenario.Id] = await MeasureAsync(
                scenario.Run,
                warmups,
                iterations,
                cancellationToken);
            Console.WriteLine(
                "  {0}: median {1:F3} ms, p95 {2:F3} ms",
                scenario.Id,
                declared[scenario.Id].MedianMs,
                declared[scenario.Id].P95Ms);
        }

        var report = new BenchmarkReport(
            DateTimeOffset.UtcNow,
            await ReadAgeVersionAsync(connectionString, cancellationToken),
            await ReadPostgresVersionAsync(connectionString, cancellationToken),
            nodeCount,
            iterations,
            warmups,
            "generalized (__idx)" +
                (nonSealed ? ", non-sealed Address" : ", sealed Address"),
            declaredScenarios.Select(scenario => new ScenarioResult(
                scenario.Id,
                scenario.Name,
                Math.Round(baseline[scenario.Id].MedianMs, 3),
                Math.Round(baseline[scenario.Id].P95Ms, 3),
                Math.Round(declared[scenario.Id].MedianMs, 3),
                Math.Round(declared[scenario.Id].P95Ms, 3),
                scenario.TargetP95Ms,
                scenario.TargetNote)).ToArray());

        var stamp = report.GeneratedAtUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(outputDirectory, $"age-index-{stamp}.json");
        var markdownPath = Path.Combine(outputDirectory, $"age-index-{stamp}.md");
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(report, JsonOptions),
            cancellationToken);
        await File.WriteAllTextAsync(markdownPath, RenderMarkdown(report), cancellationToken);

        Console.WriteLine();
        Console.WriteLine(RenderMarkdown(report));
        Console.WriteLine("JSON: {0}", jsonPath);
        Console.WriteLine("Markdown: {0}", markdownPath);

        if (!args.Contains("--keep-graph", StringComparer.Ordinal))
        {
            await declaredStore.DropIndexesAsync(cancellationToken);
            await DropGraphAsync(connectionString, graphName, cancellationToken);
        }
    }

    private static IReadOnlyList<Scenario> CreateScenarios(
        IGraph graph,
        string unicodeTarget,
        bool nonSealed,
        CancellationToken cancellationToken) =>
    [
        new Scenario(
            "L08",
            "Unicode equality on a declared string property",
            () => graph.Nodes<NgenLookup>()
                .Where(lookup => lookup.Name == unicodeTarget)
                .ToListAsync(cancellationToken),
            1.13),
        new Scenario(
            "L11",
            "GROUP BY Status (count)",
            () => graph.Nodes<NgenContract>()
                .GroupBy(contract => contract.Status)
                .Select(group => new { group.Key, Count = group.Count() })
                .ToListAsync(cancellationToken),
            7.42),
        new Scenario(
            "L12",
            "DISTINCT Status",
            () => graph.Nodes<NgenContract>()
                .Select(contract => contract.Status)
                .Distinct()
                .ToListAsync(cancellationToken),
            7.10),
        new Scenario(
            "L16",
            "AVG Kaufpreis per Status",
            () => graph.Nodes<NgenContract>()
                .GroupBy(contract => contract.Status)
                .Select(group => new { group.Key, Value = group.Average(contract => contract.Kaufpreis) })
                .ToListAsync(cancellationToken),
            0.38),
        new Scenario(
            "L17",
            "MIN Kaufpreis per Status",
            () => graph.Nodes<NgenContract>()
                .GroupBy(contract => contract.Status)
                .Select(group => new { group.Key, Value = group.Min(contract => contract.Kaufpreis) })
                .ToListAsync(cancellationToken),
            0.61),
        new Scenario(
            "L18",
            "MAX Kaufpreis per Status",
            () => graph.Nodes<NgenContract>()
                .GroupBy(contract => contract.Status)
                .Select(group => new { group.Key, Value = group.Max(contract => contract.Kaufpreis) })
                .ToListAsync(cancellationToken),
            0.71),
        new Scenario(
            "L19",
            nonSealed
                ? "Complex property paging, non-sealed Address (OrderBy Name, Skip 200, Take 100)"
                : "Complex property paging, sealed Address (OrderBy Name, Skip 200, Take 100)",
            nonSealed
                ? () => graph.Nodes<NgenOpenDocument>()
                    .OrderBy(document => document.Name)
                    .Skip(200)
                    .Take(100)
                    .ToListAsync(cancellationToken)
                : () => graph.Nodes<NgenDocument>()
                    .OrderBy(document => document.Name)
                    .Skip(200)
                    .Take(100)
                    .ToListAsync(cancellationToken),
            30.72),
    ];

    private static SchemaRegistry CreateNgenRegistry(bool nonSealed)
    {
        var registry = new SchemaRegistry();
        var labels = new List<string> { BaseLabel, ChildLabel, LookupLabel };
        labels.AddRange(nonSealed
            ? [OpenDocumentLabel, OpenDocumentChildLabel]
            : [DocumentLabel, DocumentChildLabel]);
        foreach (var label in labels)
        {
            registry.AddPropertyIndex(new PropertyIndexDefinition(label, "Name"));
            registry.AddPropertyIndex(new PropertyIndexDefinition(label, "Name", PropertyIndexKind.Ordering));

            // The contract scenarios use the historical Status/Kaufpreis property names and the
            // lookup label uses Region/Amount. Both resolve to the same generalized __idx layout,
            // so the harness measures the only physical path the provider has.
            var lookup = string.Equals(label, LookupLabel, StringComparison.Ordinal);
            var group = lookup ? "Region" : "Status";
            var value = lookup ? "Amount" : "Kaufpreis";
            registry.AddPropertyIndex(new PropertyIndexDefinition(label, group));
            registry.AddPropertyAggregate(new PropertyAggregateDefinition(
                label,
                group,
                value,
                AggregateFunction.Count | AggregateFunction.Sum | AggregateFunction.Min |
                AggregateFunction.Max | AggregateFunction.Average));
        }

        return registry;
    }

    private static async Task SeedAsync(
        IGraph graph,
        int nodeCount,
        bool nonSealed,
        CancellationToken cancellationToken)
    {
        const int batchSize = 500;
        var unicodeIndex = UnicodeIndex(nodeCount);
        for (var offset = 0; offset < nodeCount; offset += batchSize)
        {
            var end = Math.Min(offset + batchSize, nodeCount);
            await using var transaction = await graph.GetTransactionAsync(cancellationToken);
            for (var index = offset; index < end; index++)
            {
                await graph.CreateNodeAsync(
                    CreateNode(index, nodeCount, unicodeIndex, nonSealed),
                    transaction,
                    cancellationToken);
            }

            await transaction.CommitAsync();
            Console.WriteLine("  seeded {0:N0}/{1:N0}", end, nodeCount);
        }
    }

    private static INode CreateNode(int index, int nodeCount, int unicodeIndex, bool nonSealed)
    {
        var name = index == unicodeIndex
            ? BuildUnicodeName(nodeCount)
            : string.Create(CultureInfo.InvariantCulture, $"Contract-{index:D7}");
        var city = string.Create(CultureInfo.InvariantCulture, $"City-{index % 97:D2}");
        var street = string.Create(CultureInfo.InvariantCulture, $"Street-{index % 389:D3}");
        var sealedAddress = index % 10 == 0
            ? new NgenAddress
            {
                City = city,
                Street = street,
            }
            : null;
        var status = index % 11 == 0 ? null : Statuses[index % Statuses.Length];

        if (index % 10 == 0)
        {
            if (nonSealed)
            {
                var openAddress = new NgenOpenAddress
                {
                    City = city,
                    Street = street,
                };
                return index % 20 == 10
                    ? new NgenOpenDocumentChild
                    {
                        Name = name,
                        Status = status,
                        Kaufpreis = 1_000 + (index % 5_000),
                        Address = openAddress,
                    }
                    : new NgenOpenDocument
                    {
                        Name = name,
                        Status = status,
                        Kaufpreis = 1_000 + (index % 5_000),
                        Address = openAddress,
                    };
            }

            if (index % 20 == 10)
            {
                return new NgenDocumentChild
                {
                    Name = name,
                    Status = status,
                    Kaufpreis = 1_000 + (index % 5_000),
                    Address = sealedAddress,
                };
            }

            return new NgenDocument
            {
                Name = name,
                Status = status,
                Kaufpreis = 1_000 + (index % 5_000),
                Address = sealedAddress,
            };
        }

        if (index % 10 == 1)
        {
            return new NgenLookup
            {
                Name = name,
            };
        }

        return index % 3 == 0
            ? new NgenKaufContract
            {
                Name = name,
                Status = status,
                Kaufpreis = 1_000 + (index % 5_000),
                Address = sealedAddress,
            }
            : new NgenContract
            {
                Name = name,
                Status = status,
                Kaufpreis = 1_000 + (index % 5_000),
                Address = sealedAddress,
            };
    }

    private static int UnicodeIndex(int nodeCount)
    {
        var candidate = Math.Max(0, nodeCount / 2);
        while (candidate > 0 && candidate % 10 != 1)
        {
            candidate--;
        }

        return candidate;
    }

    private static string BuildUnicodeName(int nodeCount) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Müller-😀-東京-<b>&\"quote\"-{nodeCount:D6}");

    private static async Task<Measurement> MeasureAsync(
        Func<Task> action,
        int warmups,
        int iterations,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < warmups; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await action().ConfigureAwait(false);
        }

        var samples = new List<double>(iterations);
        for (var index = 0; index < iterations; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            await action().ConfigureAwait(false);
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var medianIndex = samples.Count / 2;
        var p95Index = Math.Max(0, (int)Math.Ceiling(samples.Count * 0.95) - 1);
        return new Measurement(samples[medianIndex], samples[p95Index]);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The statements contain only benchmark-controlled, quoted identifiers; no user input.")]
    private static async Task CreateGinIndexesAsync(
        string connectionString,
        string graphName,
        bool nonSealed,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var labels = new List<string> { BaseLabel, ChildLabel, LookupLabel };
        labels.AddRange(nonSealed
            ? [OpenDocumentLabel, OpenDocumentChildLabel]
            : [DocumentLabel, DocumentChildLabel]);
        foreach (var label in labels)
        {
            // nGen deployments already carry a GIN index on the AGE properties column; the L08
            // containment shape depends on it. The provider does not own this index.
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText =
                    $"CREATE INDEX IF NOT EXISTS {Quote("idx_" + label + "_props_gin")} " +
                    $"ON {Quote(graphName)}.{Quote(label)} USING gin (properties)";
                await command.ExecuteNonQueryAsync(cancellationToken);

                command.CommandText = $"ANALYZE {Quote(graphName)}.{Quote(label)}";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async Task<string> ReadAgeVersionAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = "SELECT extversion FROM pg_extension WHERE extname = 'age'";
            return Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture) ?? "unknown";
        }
    }

    private static async Task<string> ReadPostgresVersionAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = "SHOW server_version";
            return Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture) ?? "unknown";
        }
    }

    private static async Task DropGraphAsync(
        string connectionString,
        string graphName,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = """
                LOAD 'age';
                SET search_path = ag_catalog, "$user", public;
                SELECT ag_catalog.drop_graph(@name, true)
                WHERE EXISTS (
                    SELECT 1 FROM ag_catalog.ag_graph WHERE name = @name);
                """;
            command.Parameters.AddWithValue("name", graphName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string RenderMarkdown(BenchmarkReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# AGE declared-index benchmark");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Generated: {report.GeneratedAtUtc:O}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- AGE: {report.AgeVersion}, PostgreSQL: {report.PostgresVersion}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Nodes: {report.NodeCount:N0} (nGen-shaped base/child hierarchy)");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Iterations: {report.Iterations} after {report.Warmups} warmups, single client");
        builder.AppendLine("- Baseline: store with an empty SchemaRegistry (ordinary AGE path).");
        builder.AppendLine("- Declared: same data, definitions registered through SchemaRegistry, companion structures created by EnsureIndexesAsync.");
        builder.AppendLine("- Targets are the PR-05 numbers measured on AGE 1.8.0/PG 18.6 with 611k nodes and 8 clients; the absolute target is shown for orientation, not as an apples-to-apples comparison.");
        builder.AppendLine();
        builder.AppendLine("| Scenario | Baseline median (ms) | Baseline p95 (ms) | Declared median (ms) | Declared p95 (ms) | Speedup (p95) | PR-05 target p95 (ms) | Absolute target met |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|");
        foreach (var scenario in report.Scenarios)
        {
            var speedup = scenario.BaselineP95Ms / Math.Max(scenario.DeclaredP95Ms, 0.0001);
            var met = scenario.DeclaredP95Ms <= scenario.TargetP95Ms && scenario.DeclaredP95Ms < scenario.BaselineP95Ms;
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {scenario.Id} {scenario.Name} | {scenario.BaselineMedianMs:F3} | {scenario.BaselineP95Ms:F3} | " +
                $"{scenario.DeclaredMedianMs:F3} | {scenario.DeclaredP95Ms:F3} | {speedup:F2}x | " +
                $"{scenario.TargetP95Ms:F2} | {(met ? "yes" : "no")} |");
        }

        return builder.ToString();
    }

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static int ParseOption(string[] args, string name, int fallback) =>
        int.TryParse(GetOption(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private sealed record Scenario(
        string Id,
        string Name,
        Func<Task> Run,
        double TargetP95Ms,
        string? TargetNote = null);

    private sealed record Measurement(double MedianMs, double P95Ms);

    internal sealed record ScenarioResult(
        string Id,
        string Name,
        double BaselineMedianMs,
        double BaselineP95Ms,
        double DeclaredMedianMs,
        double DeclaredP95Ms,
        double TargetP95Ms,
        string? TargetNote);

    internal sealed record BenchmarkReport(
        DateTimeOffset GeneratedAtUtc,
        string AgeVersion,
        string PostgresVersion,
        int NodeCount,
        int Iterations,
        int Warmups,
        string Variant,
        IReadOnlyList<ScenarioResult> Scenarios);

    [Node(BaseLabel)]
    public record NgenContract : Node
    {
        public string Name { get; set; } = string.Empty;
        public string? Status { get; set; }
        public long Kaufpreis { get; set; }
        public NgenAddress? Address { get; set; }
    }

    [Node(ChildLabel)]
    public sealed record NgenKaufContract : NgenContract;

    [Node(DocumentChildLabel)]
    public sealed record NgenDocumentChild : NgenDocument;

    [Node(LookupLabel)]
    public sealed record NgenLookup : Node
    {
        public string Name { get; set; } = string.Empty;
        public string? Region { get; set; }
        public long Amount { get; set; }
    }

    [Node(DocumentLabel)]
    public record NgenDocument : Node
    {
        public string Name { get; set; } = string.Empty;
        public string? Status { get; set; }
        public string? Region { get; set; }
        public long Kaufpreis { get; set; }
        public long Amount { get; set; }
        public NgenAddress? Address { get; set; }
    }

    public sealed class NgenAddress
    {
        public string City { get; set; } = string.Empty;
        public string Street { get; set; } = string.Empty;
    }

    [Node(OpenDocumentChildLabel)]
    public sealed record NgenOpenDocumentChild : NgenOpenDocument;

    [Node(OpenDocumentLabel)]
    public record NgenOpenDocument : Node
    {
        public string Name { get; set; } = string.Empty;
        public string? Status { get; set; }
        public string? Region { get; set; }
        public long Kaufpreis { get; set; }
        public long Amount { get; set; }
        public NgenOpenAddress? Address { get; set; }
    }

    /// <summary>
    /// The common non-sealed <c>Address</c>/<c>Profile</c> shape. The depth bound must use one hop
    /// for it, including a loaded derived type that adds no nested complex property.
    /// </summary>
    public class NgenOpenAddress
    {
        public string City { get; set; } = string.Empty;
        public string Street { get; set; } = string.Empty;
    }

    public sealed class NgenOpenPostalAddress : NgenOpenAddress
    {
    }
}
