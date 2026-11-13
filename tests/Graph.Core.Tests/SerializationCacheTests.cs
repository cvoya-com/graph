// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Core.Tests;

using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Cvoya.Graph.Serialization;
using Cvoya.Graph.Serialization.Results;

[Collection(SerializationCacheIsolation.Name)]
public class SerializationCacheTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MetadataResolution_DoesNotRetainCollectibleGenericArguments(bool wireEntries, bool mapMetadata)
    {
        var fixture = SerializationCacheFixtureAssembly.Compile("public sealed class Payload;");
        var contextReference = SerializationCacheFixtureAssembly.LoadAndRun(fixture.Image, (context, assembly) =>
        {
            var closedType = typeof(List<>).MakeGenericType(assembly.GetType("Payload", throwOnError: true)!);
            Assert.False(closedType.Assembly.IsCollectible);
            Assert.True(closedType.IsCollectible);
            using var scope = context.EnterContextualReflection();
            for (var pass = 0; pass < 3; pass++)
            {
                Assert.Same(closedType, ResolveMetadata(closedType.AssemblyQualifiedName!, wireEntries, mapMetadata));
            }
        });

        SerializationCacheFixtureAssembly.AssertUnloaded(contextReference);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MetadataResolution_RespectsContextAfterDefaultCacheWarmup(bool wireEntries, bool mapMetadata)
    {
        var fixture = SerializationCacheFixtureAssembly.Compile("public sealed class ContextualPayload;");
        using var defaultScope = AssemblyLoadContext.Default.EnterContextualReflection();
        using var stream = new MemoryStream(fixture.Image);
        var defaultAssembly = AssemblyLoadContext.Default.LoadFromStream(stream);
        var defaultType = defaultAssembly.GetType("ContextualPayload", throwOnError: true)!;
        var typeName = defaultType.AssemblyQualifiedName!;
        Assert.Same(defaultType, ResolveMetadata(typeName, wireEntries, mapMetadata));

        var contextReference = SerializationCacheFixtureAssembly.LoadAndRun(fixture.Image, (context, assembly) =>
        {
            var expected = assembly.GetType("ContextualPayload", throwOnError: true)!;
            using var scope = context.EnterContextualReflection();
            Assert.NotSame(defaultType, expected);
            Assert.Same(expected, Type.GetType(typeName));
            for (var pass = 0; pass < 3; pass++)
            {
                Assert.Same(expected, ResolveMetadata(typeName, wireEntries, mapMetadata));
            }
        });

        SerializationCacheFixtureAssembly.AssertUnloaded(contextReference);
        Assert.Same(defaultType, ResolveMetadata(typeName, wireEntries, mapMetadata));
    }

    [Fact]
    public void NodeProcessing_DoesNotRetainCollectibleGenericArguments()
    {
        var fixture = SerializationCacheFixtureAssembly.Compile("public sealed class Payload;");
        var contextReference = SerializationCacheFixtureAssembly.LoadAndRun(fixture.Image, (context, assembly) =>
        {
            var closedType = typeof(SerializationCacheNode<>).MakeGenericType(assembly.GetType("Payload", throwOnError: true)!);
            using var scope = context.EnterContextualReflection();
            var record = NodeRecord("GenericNode", new Dictionary<string, GraphValue>
            {
                ["__metadata__"] = GraphValue.Scalar(closedType.AssemblyQualifiedName),
            });
            var info = Process(record, typeof(Node));

            Assert.Same(closedType, info.ActualType);
            Assert.Contains(nameof(INode.Labels), info.SimpleProperties.Keys);
        });

        SerializationCacheFixtureAssembly.AssertUnloaded(contextReference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LabelDiscovery_DoesNotRetainCollectibleTargetTypes(bool genericTarget)
    {
        var fixture = SerializationCacheFixtureAssembly.Compile("""
            using Cvoya.Graph;
            public sealed class Payload;
            public sealed record LoadedNode : Node;
            """);
        var contextReference = SerializationCacheFixtureAssembly.LoadAndRun(fixture.Image, (_, assembly) =>
        {
            var targetType = genericTarget
                ? typeof(SerializationCacheNode<>).MakeGenericType(assembly.GetType("Payload", throwOnError: true)!)
                : assembly.GetType("LoadedNode", throwOnError: true)!;
            var info = Process(NodeRecord("LoadedNode"), targetType);

            Assert.Same(targetType, info.ActualType);
            Assert.Contains(nameof(INode.Labels), info.SimpleProperties.Keys);
        });

        SerializationCacheFixtureAssembly.AssertUnloaded(contextReference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LabelDiscovery_RecoversAfterAssemblyLoads(bool relationship)
    {
        var label = $"SerializationCache_{Guid.NewGuid():N}";
        var fixture = SerializationCacheFixtureAssembly.Compile($$"""
            using Cvoya.Graph;
            [Node("{{label}}")] public sealed record LoadedNode : Node;
            [Relationship("{{label}}")] public sealed record LoadedRelationship : Relationship;
            """);
        var targetType = relationship ? typeof(Relationship) : typeof(Node);
        var record = relationship ? RelationshipRecord(label) : NodeRecord(label);
        Assert.Same(targetType, Process(record, targetType).ActualType);
        Assert.Same(targetType, Process(record, targetType).ActualType);

        var contextReference = SerializationCacheFixtureAssembly.LoadAndRun(fixture.Image, (_, assembly) =>
        {
            var expected = assembly.GetType(relationship ? "LoadedRelationship" : "LoadedNode", throwOnError: true)!;
            Assert.Same(expected, Process(record, targetType).ActualType);
            Assert.Same(expected, Process(record, targetType).ActualType);
        });

        SerializationCacheFixtureAssembly.AssertUnloaded(contextReference);
        Assert.Same(targetType, Process(record, targetType).ActualType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmLabelDiscovery_ReusesSuccessfulIndexes(bool relationship)
    {
        var targetType = relationship ? typeof(Relationship) : typeof(Node);
        var expectedType = relationship ? typeof(WarmRelationship) : typeof(WarmNode);
        var record = relationship ? RelationshipRecord(expectedType.Name) : NodeRecord(expectedType.Name);
        Assert.Same(expectedType, Process(record, targetType).ActualType);
        Assert.Same(expectedType, Process(record, targetType).ActualType);
        var buildsBefore = GraphResultProcessor.LabelIndexBuildCountForTesting(targetType);

        var assemblyLoads = 0;
        AssemblyLoadEventHandler onAssemblyLoad = (_, _) => Interlocked.Increment(ref assemblyLoads);
        AppDomain.CurrentDomain.AssemblyLoad += onAssemblyLoad;
        try
        {
            for (var pass = 0; pass < 100; pass++)
            {
                Assert.Same(expectedType, Process(record, targetType).ActualType);
            }
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyLoad -= onAssemblyLoad;
        }

        // A warm-up can itself load an assembly and invalidate its index. After that first rebuild,
        // each further rebuild must be justified by an observed assembly load, never by node count.
        Assert.InRange(
            GraphResultProcessor.LabelIndexBuildCountForTesting(targetType) - buildsBefore,
            0,
            Volatile.Read(ref assemblyLoads) + 1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MetadataMiss_RecoversAfterAssemblyLoads(bool wireEntries, bool mapMetadata)
    {
        var fixture = SerializationCacheFixtureAssembly.Compile("public sealed class LoadedType;");
        var contextReference = ResolveMissingThenLoadedMetadata(fixture.Image, fixture.AssemblyIdentity, wireEntries, mapMetadata);

        SerializationCacheFixtureAssembly.AssertUnloaded(contextReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ResolveMissingThenLoadedMetadata(byte[] image, string assemblyIdentity, bool wireEntries, bool mapMetadata)
    {
        var context = new AssemblyLoadContext(name: null, isCollectible: true);
        try
        {
            using var scope = context.EnterContextualReflection();
            using var stream = new MemoryStream(image);
            var typeName = $"LoadedType, {assemblyIdentity}";
            Assert.Null(ResolveMetadata(typeName, wireEntries, mapMetadata));
            Assert.Null(ResolveMetadata(typeName, wireEntries, mapMetadata));
            stream.Position = 0;
            var assembly = context.LoadFromStream(stream);
            var expected = assembly.GetType("LoadedType", throwOnError: true)!;
            Assert.Same(expected, ResolveMetadata(typeName, wireEntries, mapMetadata));
            Assert.Same(expected, ResolveMetadata(typeName, wireEntries, mapMetadata));
        }
        finally
        {
            context.Unload();
        }

        return new WeakReference(context);
    }

    private static Type? ResolveMetadata(string typeName, bool wireEntries, bool mapMetadata)
    {
        if (wireEntries)
        {
            return GraphValueConverter.GetTypeFromMetadata(new Dictionary<string, GraphValue>
            {
                ["__metadata__"] = mapMetadata
                    ? GraphValue.Map(new Dictionary<string, GraphValue> { ["type"] = GraphValue.Scalar(typeName) })
                    : GraphValue.Scalar(typeName),
            });
        }

        return GraphValueConverter.GetTypeFromMetadata(new Dictionary<string, object>
        {
            ["__metadata__"] = mapMetadata
                ? new Dictionary<string, object> { ["type"] = typeName }
                : typeName,
        });
    }

    private static GraphRecord NodeRecord(string label, Dictionary<string, GraphValue>? properties = null) =>
        new(new Dictionary<string, GraphValue>
        {
            ["Node"] = GraphValue.Map(new Dictionary<string, GraphValue>
            {
                ["Node"] = GraphValue.Node("node", [label], properties ?? []),
                ["ComplexProperties"] = GraphValue.List([]),
            }),
        });

    private static GraphRecord RelationshipRecord(string label) =>
        new(new Dictionary<string, GraphValue>
        {
            ["Relationship"] = GraphValue.Relationship("relationship", label, "start", "end", new Dictionary<string, GraphValue>()),
        });

    private static EntityInfo Process(GraphRecord record, Type targetType) =>
        Assert.Single(new GraphResultProcessor(new EntityFactory())
            .ProcessAsync([record], targetType, TestContext.Current.CancellationToken).GetAwaiter().GetResult());

    private sealed record WarmNode : Node;

    private sealed record WarmRelationship : Relationship;
}

internal sealed record SerializationCacheNode<T> : Node;
