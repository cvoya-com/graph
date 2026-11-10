// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying;

using Cvoya.Graph;

/// <summary>
/// Maps a logical node label to every label that can store an instance of that logical type
/// (the label itself plus all transitively derived labels).
/// </summary>
/// <remarks>
/// <para>
/// The hierarchy has two sources:
/// </para>
/// <list type="bullet">
/// <item>
/// CLR inheritance of types carrying <see cref="NodeAttribute"/>, discovered lazily from the
/// loaded assemblies the first time a label is queried.
/// </item>
/// <item>
/// Explicit runtime registrations for labels that are not backed by a CLR type, definitions as <see cref="DynamicNode"/>.
/// </item>
/// </list>
/// <para>
/// The registry is process-wide because labels and their physical AGE tables are process-wide
/// conventions. Registrations are additive; no entry can be removed while the process runs.
/// </para>
/// </remarks>
internal sealed class AgeTypeHierarchyRegistry
{
    private static readonly Lazy<AgeTypeHierarchyRegistry> LazyInstance =
        new(() => new AgeTypeHierarchyRegistry(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly object gate = new();
    private readonly Dictionary<string, HashSet<string>> subLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> clrScannedLabels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the process-wide hierarchy registry.</summary>
    public static AgeTypeHierarchyRegistry Instance => LazyInstance.Value;

    /// <summary>
    /// Registers <paramref name="childLabel"/> as a direct subtype of <paramref name="parentLabel"/>.
    /// Registering the same pair twice is a no-op.
    /// </summary>
    /// <param name="parentLabel">The label of the base logical type.</param>
    /// <param name="childLabel">The label of the derived logical type.</param>
    public void Register(string parentLabel, string childLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(childLabel);
        if (string.Equals(parentLabel, childLabel, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (gate)
        {
            AddSubLabel(parentLabel, childLabel);
        }
    }

    /// <summary>
    /// Gets all labels that can store an instance of <paramref name="label"/>, including
    /// <paramref name="label"/> itself, in breadth-first order.
    /// </summary>
    /// <param name="label">The logical label of the queried type.</param>
    /// <returns>The label itself followed by every transitively derived label.</returns>
    public IReadOnlyList<string> GetSubLabels(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        lock (gate)
        {
            EnsureClrHierarchy(label);
            var result = new List<string> { label };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { label };
            var queue = new Queue<string>();
            queue.Enqueue(label);

            while (queue.Count > 0)
            {
                if (!subLabels.TryGetValue(queue.Dequeue(), out var children))
                {
                    continue;
                }

                foreach (var child in children)
                {
                    if (seen.Add(child))
                    {
                        result.Add(child);
                        queue.Enqueue(child);
                    }
                }
            }

            return result;
        }
    }

    /// <summary>Gets whether <paramref name="label"/> has at least one registered subtype.</summary>
    /// <param name="label">The logical label.</param>
    public bool HasSubLabels(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        lock (gate)
        {
            EnsureClrHierarchy(label);
            return subLabels.TryGetValue(label, out var children) && children.Count > 0;
        }
    }

    /// <summary>
    /// Gets a snapshot of every logical label that participates in a known hierarchy, both as a
    /// parent and as a child.
    /// </summary>
    /// <remarks>
    /// The snapshot contains CLR hierarchies that have already been discovered lazily and every
    /// explicit <see cref="Register"/> call. It does not force discovery for labels that have not
    /// been queried yet, so callers must treat a missing label as "not known yet", not as "not a
    /// hierarchy label".
    /// </remarks>
    /// <returns>The known logical labels, ordered case-insensitively.</returns>
    public IReadOnlyList<string> GetKnownLabels()
    {
        lock (gate)
        {
            var labels = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (parentLabel, children) in subLabels)
            {
                labels.Add(parentLabel);
                foreach (var childLabel in children)
                {
                    labels.Add(childLabel);
                }
            }

            return [.. labels];
        }
    }

    private void AddSubLabel(string parentLabel, string childLabel)
    {
        if (!subLabels.TryGetValue(parentLabel, out var children))
        {
            children = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            subLabels[parentLabel] = children;
        }

        children.Add(childLabel);
    }

    /// <summary>
    /// Lazily adds the CLR inheritance chain for a label the first time it is queried. Resolving
    /// the type from the label walks the loaded assemblies, exactly as the shared planner's
    /// <see cref="Labels.GetCompatibleLabels"/> does, but only for labels that are actually
    /// queried. Labels without a CLR type (for example FormConfig types registered through
    /// <see cref="Register"/>) simply have no discovered descendants.
    /// </summary>
    private void EnsureClrHierarchy(string label)
    {
        if (!clrScannedLabels.Add(label))
        {
            return;
        }

        Type type;
        try
        {
            type = Labels.GetTypeFromLabel(label);
        }
        catch (GraphException)
        {
            // A dynamic or complex-value label has no INode CLR type. Runtime registrations cover
            // the former; the latter always uses a containment predicate.
            return;
        }

        if (!typeof(INode).IsAssignableFrom(type))
        {
            return;
        }

        foreach (var compatibleLabel in Labels.GetCompatibleLabels(type))
        {
            if (!string.Equals(compatibleLabel, label, StringComparison.OrdinalIgnoreCase))
            {
                AddSubLabel(label, compatibleLabel);
            }
        }
    }
}
