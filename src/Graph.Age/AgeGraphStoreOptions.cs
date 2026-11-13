// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age;

/// <summary>
/// Configures optional Apache AGE provider behavior.
/// </summary>
public sealed class AgeGraphStoreOptions
{
    /// <summary>
    /// Gets or sets how native shortest-path execution is resolved. The default is
    /// <see cref="AgeFeatureMode.Auto"/>, which enables it when the connected server is Apache AGE
    /// 1.8.0 or later and exposes the native shortest-path functions.
    /// </summary>
    /// <remarks>
    /// Set this to <see cref="AgeFeatureMode.Disabled"/> when a store must keep the pre-1.8
    /// behavior even though the server supports the native functions.
    /// </remarks>
    public AgeFeatureMode NativeShortestPath { get; set; } = AgeFeatureMode.Auto;

    /// <summary>
    /// Gets or sets how native Cypher predicate functions (<c>any</c>, <c>all</c>, <c>none</c>,
    /// <c>single</c>) are resolved. The default is <see cref="AgeFeatureMode.Auto"/>, which enables
    /// them when the connected server is Apache AGE 1.8.0 or later.
    /// </summary>
    public AgeFeatureMode NativePredicateFunctions { get; set; } = AgeFeatureMode.Auto;
}
