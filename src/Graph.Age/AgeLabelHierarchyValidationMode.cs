// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age;

/// <summary>
/// Controls what the AGE provider does when a physical node label that belongs to a registered
/// label hierarchy was never registered with
/// <see cref="AgeGraphStore.RegisterLabelHierarchy(string, string)"/>.
/// </summary>
/// <remarks>
/// A base-label query expands to the physical tables of the labels registered in the in-process
/// hierarchy only. An unregistered physical label therefore stays invisible to those queries even
/// though it stores rows that declare the registered base label. The provider checks this condition
/// on the first operation against a graph.
/// </remarks>
public enum AgeLabelHierarchyValidationMode
{
    /// <summary>Do not inspect physical labels for missing hierarchy registrations.</summary>
    Off = 0,

    /// <summary>
    /// Inspect physical labels once and log a warning for every label that stores rows declaring a
    /// registered hierarchy label but is itself unregistered. Query results are not affected.
    /// </summary>
    Warn = 1,

    /// <summary>
    /// Inspect physical labels once and fail the first graph operation with a
    /// <see cref="GraphException"/> when an unregistered hierarchy label is found.
    /// </summary>
    Fail = 2,
}
