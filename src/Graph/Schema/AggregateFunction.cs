// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph;

/// <summary>Identifies the aggregate functions a provider may maintain for a declared property.</summary>
[Flags]
public enum AggregateFunction
{
    /// <summary>No aggregate function.</summary>
    None = 0,

    /// <summary>Count of non-null values.</summary>
    Count = 1,

    /// <summary>Sum of numeric values.</summary>
    Sum = 2,

    /// <summary>Minimum value.</summary>
    Min = 4,

    /// <summary>Maximum value.</summary>
    Max = 8,

    /// <summary>Average of numeric values.</summary>
    Average = 16,
}
