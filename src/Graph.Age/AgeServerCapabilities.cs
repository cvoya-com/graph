// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age;

/// <summary>
/// Describes the Apache AGE extension version and optional features available on the connected
/// PostgreSQL server.
/// </summary>
/// <param name="Version">The installed <c>age</c> extension version.</param>
/// <param name="Features">The optional features detected for this server.</param>
public sealed record AgeServerCapabilities(Version Version, AgeFeature Features)
{
    /// <summary>
    /// Gets a value indicating whether native shortest-path functions are available.
    /// </summary>
    public bool SupportsNativeShortestPath => Features.HasFlag(AgeFeature.NativeShortestPath);

    /// <summary>
    /// Gets a value indicating whether native Cypher predicate functions are available.
    /// </summary>
    public bool SupportsNativePredicateFunctions => Features.HasFlag(AgeFeature.NativePredicateFunctions);
}
