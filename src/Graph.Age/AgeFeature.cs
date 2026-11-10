// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age;

/// <summary>
/// Optional Apache AGE server capabilities that a provider can detect and use.
/// </summary>
[Flags]
public enum AgeFeature
{
    /// <summary>No optional server capability is enabled.</summary>
    None = 0,

    /// <summary>
    /// The native <c>ag_catalog.age_shortest_path</c> and
    /// <c>ag_catalog.age_all_shortest_paths</c> set-returning functions. They were added in Apache
    /// AGE 1.8.0 and are the only way to execute the typed shortest-path query surface on AGE,
    /// because the server does not implement Cypher's <c>shortestPath()</c> syntax.
    /// </summary>
    NativeShortestPath = 1,

    /// <summary>
    /// The Cypher predicate functions <c>any()</c>, <c>all()</c>, <c>none()</c>, and <c>single()</c>.
    /// They were added in Apache AGE 1.8.0 and let collection membership be tested without building
    /// an intermediate list.
    /// </summary>
    NativePredicateFunctions = 2,
}
