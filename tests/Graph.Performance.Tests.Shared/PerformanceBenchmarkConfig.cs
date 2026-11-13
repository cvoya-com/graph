// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Graph.Performance.Tests;

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

/// <summary>
/// Shared BenchmarkDotNet configuration for the provider-specific performance test executables.
/// Uses only the in-process toolchain to avoid project reference issues.
/// </summary>
public static class PerformanceBenchmarkConfig
{
    public static IConfig Create() =>
        ManualConfig.Create(DefaultConfig.Instance)
            .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance))
            .WithOptions(ConfigOptions.DisableOptimizationsValidator);
}
