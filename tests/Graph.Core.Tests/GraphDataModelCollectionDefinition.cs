// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Core.Tests;

/// <summary>
/// Serializes the tests that mutate <see cref="GraphDataModel"/>'s process-wide derived-type index
/// and complex-depth cache. The collection is non-parallel so a test in another collection cannot
/// build or reset the shared index between a warmup and its build-count assertion.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GraphDataModelCollectionDefinition
{
    /// <summary>The collection name shared by every GraphDataModel test class.</summary>
    public const string Name = "GraphDataModel";
}
