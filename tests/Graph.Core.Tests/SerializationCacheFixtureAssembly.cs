// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Core.Tests;

using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static class SerializationCacheFixtureAssembly
{
    private static readonly Lazy<MetadataReference[]> References = new(BuildReferences);

    internal static (byte[] Image, string AssemblyIdentity) Compile(string source)
    {
        var compilation = CSharpCompilation.Create(
            $"SerializationCacheFixture_{Guid.NewGuid():N}",
            [CSharpSyntaxTree.ParseText(source)],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join('\n', result.Diagnostics));
        return (stream.ToArray(), compilation.Assembly.Identity.GetDisplayName());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static WeakReference LoadAndRun(byte[] image, Action<AssemblyLoadContext, System.Reflection.Assembly> action)
    {
        var context = new AssemblyLoadContext(name: null, isCollectible: true);
        try
        {
            using var stream = new MemoryStream(image);
            action(context, context.LoadFromStream(stream));
        }
        finally
        {
            context.Unload();
        }

        return new WeakReference(context);
    }

    internal static void AssertUnloaded(WeakReference contextReference)
    {
        // The loading/action frame has returned. Do not clear production caches: their lifetime is
        // exactly what these tests exercise, rather than the factory/schema/serializer registries.
        for (var attempt = 0; contextReference.IsAlive && attempt < 10; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(contextReference.IsAlive, "The result-materialization caches retained the collectible fixture.");
    }

    private static MetadataReference[] BuildReferences()
    {
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator);
        return [
            .. trustedAssemblies
                .Where(path => Path.GetFileName(path) is
                    "System.Private.CoreLib.dll" or "System.Runtime.dll" or "netstandard.dll")
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)),
            MetadataReference.CreateFromFile(typeof(Node).Assembly.Location),
        ];
    }
}
