// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Core.Tests;

/// <summary>
/// Non-collectible base used by a collectible plugin type. It must be public because the emitted
/// plugin assembly is not a friend of the test assembly.
/// </summary>
public class CollectiblePluginBase
{
    /// <summary>Gets or sets a simple value.</summary>
    public string City { get; set; } = string.Empty;
}
