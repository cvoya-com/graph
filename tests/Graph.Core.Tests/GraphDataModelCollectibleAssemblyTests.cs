// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Core.Tests;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

/// <summary>
/// Proves that the shared derived-type index and the complex-depth cache never retain a type from
/// a collectible plugin assembly, and that a later plan safely rebuilds after the plugin unloads.
/// </summary>
/// <remarks>
/// The fixtures compile their types into a real collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>;
/// a cached <see cref="Type"/> value roots that context, so the helper's forced-GC reclamation check
/// fails when either cache keeps a collectible type alive. The cross-assembly cases also prove that
/// a depth bound is not silently truncated to the assembly that declares the base type.
/// </remarks>
[Trait("Area", "GraphDataModel")]
[Collection(GraphDataModelCollectionDefinition.Name)]
public sealed class GraphDataModelCollectibleAssemblyTests(ITestOutputHelper output)
{
    [Fact]
    public void ConstructedGenericWithCollectibleArgument_DoesNotRetainThePlugin()
    {
        GraphDataModel.ResetDerivedTypeIndexForTests();
        try
        {
            var reclaimed = CollectiblePluginAssembly.Run(
                [
                    """
                    namespace CollectibleGenericArgument;

                    public sealed class PluginValue
                    {
                        public string Name { get; set; } = "";
                    }
                    """,
                ],
                [["CollectibleGenericArgument.PluginValue"]],
                types =>
                {
                    var constructed = typeof(GenericDepthHolder<>).MakeGenericType(types[0][0]);
                    Assert.False(constructed.Assembly.IsCollectible);
                    Assert.True(constructed.IsCollectible);
                    Assert.Equal(1, GraphDataModel.GetComplexPropertyDepth(constructed));
                });

            Assert.True(reclaimed, "The depth cache retained a constructed generic type containing a plugin argument.");
        }
        finally
        {
            GraphDataModel.ResetDerivedTypeIndexForTests();
        }
    }

    [Fact]
    public void CollectibleComplexType_IsNotRetainedByTheSharedDerivedTypeOrDepthCaches()
    {
        GraphDataModel.ResetDerivedTypeIndexForTests();
        var collectibleDepth = -1;
        var buildsBeforeCollectibleQuery = 0;
        RuntimeLabelCollisionFixtureAssembly.Run(
            """
            namespace CollectibleGraph;

            public class CollectibleRoot
            {
                public CollectibleChild Child { get; set; } = new();
            }

            public class CollectibleChild
            {
                public string Name { get; set; } = "";
            }

            public class CollectibleDerived : CollectibleRoot
            {
                public CollectibleExtra Extra { get; set; } = new();
            }

            public class CollectibleExtra
            {
                public CollectibleMore More { get; set; } = new();
            }

            public class CollectibleMore
            {
                public string Value { get; set; } = "";
            }
            """,
            ["CollectibleGraph.CollectibleRoot"],
            types =>
            {
                collectibleDepth = GraphDataModel.GetComplexPropertyDepth(types[0]);

                // Warm the shared index while the collectible assembly is loaded. The build
                // skips the plugin's types, but its assembly weak snapshot must still notice
                // the later unload.
                Assert.Equal(1, GraphDataModel.GetComplexPropertyDepth(typeof(LocalNonSealedHolder)));
                buildsBeforeCollectibleQuery = GraphDataModel.DerivedTypeIndexBuildCount;
            });

        // The collectible root declares one level; its derived type adds a nested value at depth
        // two, so the transient plugin scan must have observed CollectibleDerived as well.
        Assert.Equal(2, collectibleDepth);
        Assert.Equal(1, buildsBeforeCollectibleQuery);

        // The factory has already forced a full GC and throws if the context survived. Reaching
        // here proves the caches did not root the plugin. The plan-time check must observe the
        // changed assembly identity set, and the next uncached measurement must rebuild.
        var generationBeforePlan = GraphDataModel.DerivedTypeIndexGeneration;
        GraphDataModel.EnsureDerivedTypeIndexCurrent();
        Assert.True(
            GraphDataModel.DerivedTypeIndexGeneration > generationBeforePlan,
            "The plan-time check did not observe the unloaded collectible assembly.");

        Assert.Equal(2, GraphDataModel.GetComplexPropertyDepth(typeof(LocalTwoLevelHolder)));
        Assert.Equal(buildsBeforeCollectibleQuery + 1, GraphDataModel.DerivedTypeIndexBuildCount);

        GraphDataModel.ResetDerivedTypeIndexForTests();
    }

    [Fact]
    public void CoreBaseWithCollectibleDerived_MeasuresThePluginNestedDepthAndUnloads()
    {
        GraphDataModel.ResetDerivedTypeIndexForTests();
        var measuredDepth = -1;
        try
        {
            // Warm the depth cache without the plugin. Loading the plugin must invalidate the
            // cached bound, otherwise the second measurement returns the stale depth one.
            Assert.Equal(1, GraphDataModel.GetComplexPropertyDepth(typeof(CollectiblePluginOwner)));

            var reclaimed = CollectiblePluginAssembly.Run(
                [
                    """
                    namespace CollectibleCrossAssembly;

                    public class PluginDerived
                        : Cvoya.Graph.Core.Tests.CollectiblePluginBase
                    {
                        public PluginExtra Extra { get; set; } = new();
                    }

                    public sealed class PluginExtra
                    {
                        public string Value { get; set; } = "";
                    }
                    """,
                ],
                [["CollectibleCrossAssembly.PluginDerived"]],
                types =>
                {
                    Assert.Equal("PluginDerived", types[0][0].Name);
                    measuredDepth = GraphDataModel.GetComplexPropertyDepth(typeof(CollectiblePluginOwner));
                });

            Assert.Equal(2, measuredDepth);
            Assert.True(
                reclaimed,
                "The collectible plugin AssemblyLoadContext was not reclaimed; a plugin Type was retained.");
        }
        finally
        {
            GraphDataModel.ResetDerivedTypeIndexForTests();
        }
    }

    [Fact]
    public void CollectibleBaseWithDerivedInAnotherCollectibleAssembly_MeasuresDepthTwoAndUnloads()
    {
        GraphDataModel.ResetDerivedTypeIndexForTests();
        var measuredDepth = -1;
        try
        {
            var reclaimed = CollectiblePluginAssembly.Run(
                [
                    """
                    namespace CollectibleBaseAssembly;

                    public class PluginBase
                    {
                        public PluginLeaf Leaf { get; set; } = new();
                    }

                    public sealed class PluginLeaf
                    {
                        public string Value { get; set; } = "";
                    }
                    """,
                    """
                    namespace CollectibleDerivedAssembly;

                    public sealed class PluginDerived : CollectibleBaseAssembly.PluginBase
                    {
                        public PluginMiddle Extra { get; set; } = new();
                    }

                    public sealed class PluginMiddle
                    {
                        public CollectibleBaseAssembly.PluginLeaf More { get; set; } = new();
                    }
                    """,
                ],
                [
                    ["CollectibleBaseAssembly.PluginBase"],
                    ["CollectibleDerivedAssembly.PluginDerived"],
                ],
                types =>
                {
                    // The base is collectible and its derived type lives in another collectible
                    // assembly; the base alone measures one level and the derived type adds a
                    // second, so only an all-loaded-assembly scan observes depth two.
                    Assert.Same(types[0][0], types[1][0].BaseType);
                    measuredDepth = GraphDataModel.GetComplexPropertyDepth(types[0][0]);
                });

            Assert.Equal(2, measuredDepth);
            Assert.True(
                reclaimed,
                "The collectible plugin hierarchy was not reclaimed; a plugin Type was retained.");
        }
        finally
        {
            GraphDataModel.ResetDerivedTypeIndexForTests();
        }
    }

    [Fact]
    public void ManyCollectibleDerivedTypes_MeasureDepthTwoAndDropTheTransientHierarchy()
    {
        const int derivedTypeCount = 200;
        var source = new StringBuilder();
        source.AppendLine("namespace CollectibleManyTypes;");
        source.AppendLine("public sealed class PluginExtra { public string Value { get; set; } = \"\"; }");
        for (var index = 0; index < derivedTypeCount; index++)
        {
            source.AppendLine(
                CultureInfo.InvariantCulture,
                $"public sealed class PluginDerived{index} : Cvoya.Graph.Core.Tests.CollectiblePluginBase {{ public PluginExtra Extra{index} {{ get; set; }} = new(); }}");
        }

        var typeNames = Enumerable.Range(0, derivedTypeCount)
            .Select(index => $"CollectibleManyTypes.PluginDerived{index}")
            .ToArray();

        GraphDataModel.ResetDerivedTypeIndexForTests();
        var measuredDepth = -1;
        var firstCall = TimeSpan.Zero;
        var transientScan = TimeSpan.Zero;
        try
        {
            var reclaimed = CollectiblePluginAssembly.Run(
                [source.ToString()],
                [typeNames],
                types =>
                {
                    Assert.Equal(derivedTypeCount, types[0].Length);

                    // The first call also builds the shared non-collectible index. Time it, then
                    // use a second cache key so the next measurement isolates the transient scan
                    // of the plugin assemblies themselves.
                    var first = Stopwatch.StartNew();
                    measuredDepth = GraphDataModel.GetComplexPropertyDepth(typeof(CollectiblePluginOwner));
                    first.Stop();
                    firstCall = first.Elapsed;

                    var transient = Stopwatch.StartNew();
                    var repeatedDepth =
                        GraphDataModel.GetComplexPropertyDepth(typeof(CollectiblePluginOwner), maximumDepth: 4);
                    transient.Stop();
                    transientScan = transient.Elapsed;

                    Assert.Equal(measuredDepth, repeatedDepth);
                });

            output.WriteLine(
                "Plugin scan of {0} collectible derived types: first call with shared-index build {1:F2} ms; isolated transient scan {2:F2} ms.",
                derivedTypeCount,
                firstCall.TotalMilliseconds,
                transientScan.TotalMilliseconds);

            Assert.Equal(2, measuredDepth);
            Assert.True(
                reclaimed,
                "The large collectible plugin hierarchy was not reclaimed; a plugin Type was retained.");
        }
        finally
        {
            GraphDataModel.ResetDerivedTypeIndexForTests();
        }
    }

    private sealed class GenericDepthHolder<T>
    {
        public T? Value { get; set; }
    }

    [SuppressMessage(
        "Performance",
        "CA1852",
        Justification = "Must stay non-sealed to exercise the derived-type index path.")]
    private class LocalNonSealedHolder
    {
        public LocalLeaf Value { get; set; } = new();
    }

    private sealed class LocalLeaf
    {
        public string Name { get; set; } = "";
    }

    [SuppressMessage(
        "Performance",
        "CA1852",
        Justification = "Must stay non-sealed to exercise the derived-type index path.")]
    private class LocalTwoLevelHolder
    {
        public LocalMiddle Value { get; set; } = new();
    }

    private sealed class LocalMiddle
    {
        public LocalLeaf Leaf { get; set; } = new();
    }
}
