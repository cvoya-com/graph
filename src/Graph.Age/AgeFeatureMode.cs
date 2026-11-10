// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age;

/// <summary>
/// Controls how a version-gated Apache AGE feature is resolved for an <see cref="AgeGraphStore"/>.
/// </summary>
public enum AgeFeatureMode
{
    /// <summary>
    /// Use the feature when the connected Apache AGE server advertises it. This is the default and
    /// keeps a store working against both AGE 1.7 and AGE 1.8.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Require the feature. Store initialization fails with a clear error when the connected server
    /// does not support it, instead of silently falling back to a slower or unsupported path.
    /// </summary>
    Enabled = 1,

    /// <summary>
    /// Never use the feature, even when the connected server supports it.
    /// </summary>
    Disabled = 2,
}
