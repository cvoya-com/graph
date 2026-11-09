// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age;

/// <summary>
/// Describes one physical node label that stores rows declaring a registered hierarchy label but
/// was never registered itself.
/// </summary>
/// <param name="PhysicalLabel">The AGE label (physical table) that is not registered.</param>
/// <param name="RegisteredAncestor">
/// The registered logical label that the unregistered physical label's rows declare through
/// <c>inheritance_labels</c>.
/// </param>
/// <remarks>
/// Base-label queries expand only over registered labels, so rows in
/// <paramref name="PhysicalLabel"/> are silently omitted until
/// <see cref="AgeGraphStore.RegisterLabelHierarchy(string, string)"/> registers the pair
/// (<paramref name="RegisteredAncestor"/>, <paramref name="PhysicalLabel"/>).
/// </remarks>
public sealed record AgeLabelHierarchyFinding(string PhysicalLabel, string RegisteredAncestor);
