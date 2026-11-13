// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Graph.Performance.Tests;

using BenchmarkDotNet.Running;

public class Program
{
    /// <summary>
    /// The main entry point for the AGE performance tests.
    /// </summary>
    /// <param name="args">Command line arguments.</param>
    public static async Task Main(string[] args)
    {
        if (args.Contains("--age-index-harness"))
        {
            await AgeIndexBenchmarkHarness.RunAsync(args);
            return;
        }

        // Allow BenchmarkDotNet selection via args for local development
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, PerformanceBenchmarkConfig.Create());
    }
}
