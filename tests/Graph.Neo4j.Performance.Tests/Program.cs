// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Graph.Performance.Tests;

using BenchmarkDotNet.Running;

public class Program
{
    /// <summary>
    /// The main entry point for the Neo4j performance tests.
    /// </summary>
    /// <param name="args">Command line arguments.</param>
    public static void Main(string[] args)
    {
        var config = PerformanceBenchmarkConfig.Create();

        // Run all benchmarks automatically (no interactive selection)
        if (args.Length == 0 || args.Contains("--all"))
        {
            Console.WriteLine("Running all benchmarks...");
            BenchmarkRunner.Run<CrudOperationsBenchmark>(config);
            BenchmarkRunner.Run<RelationshipBenchmark>(config);
        }
        else
        {
            // Allow specific benchmark selection via args for local development
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
        }
    }
}
