// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Core.Tests;

/// <summary>
/// Non-collectible owner whose complex property is declared as <see cref="CollectiblePluginBase"/>.
/// A plugin-derived value adds one nested complex property, so the measured bound must be two.
/// </summary>
public sealed class CollectiblePluginOwner
{
    /// <summary>Gets or sets the declared complex value.</summary>
    public CollectiblePluginBase Address { get; set; } = new();
}
