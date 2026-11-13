// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Core.Tests;

// Loaded fixture types must disappear before another test's process-wide schema scan can see them.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerializationCacheIsolation
{
    public const string Name = "SerializationCache";
}
