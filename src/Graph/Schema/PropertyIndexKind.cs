// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph;

/// <summary>Describes the access pattern a declared property index is intended to accelerate.</summary>
public enum PropertyIndexKind
{
    /// <summary>Equality and containment predicates over one scalar property.</summary>
    Equality,

    /// <summary>
    /// Eligibility for ordering and paging over one scalar property. This kind is a plan gate, not
    /// a request to create physical storage; the application or provider must already have an
    /// accessor index for the property.
    /// </summary>
    Ordering,
}
