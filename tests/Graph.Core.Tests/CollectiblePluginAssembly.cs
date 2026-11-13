// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Core.Tests;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Compiles one or more C# sources into assemblies that are loaded into a single collectible
/// <see cref="AssemblyLoadContext"/> for tests that need a real cross-assembly plugin hierarchy.
/// </summary>
/// <remarks>
/// A later source can derive from a type in an earlier source because each earlier image is added
/// as a metadata reference. Loading all images into one collectible context models a plugin package
/// that ships several assemblies, and the forced-GC check proves that measuring the hierarchy did
/// not retain a plugin <see cref="Type"/> in a static cache.
/// </remarks>
internal static class CollectiblePluginAssembly
{
    private static readonly Lazy<MetadataReference[]> References = new(BuildReferences);

    /// <summary>
    /// Compiles and loads the sources, runs <paramref name="action"/> against the resolved types,
    /// unloads the context, and forces a full GC pass.
    /// </summary>
    /// <param name="sources">The C# sources; each becomes one assembly in load order.</param>
    /// <param name="typeNames">The type names to resolve per source assembly.</param>
    /// <param name="action">The measurement to run while the plugin assemblies are loaded.</param>
    /// <returns><see langword="true"/> when the collectible context was reclaimed.</returns>
    public static bool Run(
        string[] sources,
        string[][] typeNames,
        Action<Type[][]> action)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(typeNames);
        ArgumentNullException.ThrowIfNull(action);

        if (sources.Length == 0 || sources.Length != typeNames.Length)
        {
            throw new ArgumentException("Each source requires exactly one type-name set.", nameof(typeNames));
        }

        var contextReference = LoadAndRun(sources, typeNames, action);

        // Force the collectible context to actually be reclaimed before returning, so a cache that
        // rooted a plugin Type fails the assertion instead of leaking into a later test.
        for (var attempt = 0; contextReference.IsAlive && attempt < 10; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        return !contextReference.IsAlive;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndRun(
        string[] sources,
        string[][] typeNames,
        Action<Type[][]> action)
    {
        var images = new List<byte[]>(sources.Length);
        var references = new List<MetadataReference>(References.Value);
        foreach (var source in sources)
        {
            var image = Compile(source, references);
            images.Add(image);
            references.Add(MetadataReference.CreateFromImage(image));
        }

        var context = new AssemblyLoadContext(name: null, isCollectible: true);
        try
        {
            var assemblies = new Assembly[images.Count];
            for (var index = 0; index < images.Count; index++)
            {
                using var stream = new MemoryStream(images[index]);
                assemblies[index] = context.LoadFromStream(stream);
            }

            var types = new Type[assemblies.Length][];
            for (var index = 0; index < assemblies.Length; index++)
            {
                types[index] = typeNames[index]
                    .Select(name => assemblies[index].GetType(name)
                        ?? throw new InvalidOperationException(
                            $"Type '{name}' was not found in compiled fixture assembly '{assemblies[index].GetName().Name}'."))
                    .ToArray();
            }

            action(types);
        }
        finally
        {
            // Keep production caches intact: clearing them here would hide retention bugs from
            // the reclamation assertion below.
            context.Unload();
        }

        return new WeakReference(context);
    }

    private static byte[] Compile(string source, IReadOnlyCollection<MetadataReference> references)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName: $"CollectiblePlugin_{Guid.NewGuid():N}",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);
        if (!emitResult.Success)
        {
            var diagnostics = string.Join('\n', emitResult.Diagnostics.Select(d => d.ToString()));
            throw new InvalidOperationException($"Fixture source failed to compile:\n{diagnostics}");
        }

        return peStream.ToArray();
    }

    private static MetadataReference[] BuildReferences()
    {
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator);

        var references = trustedAssemblies
            .Where(path => Path.GetFileName(path) is
                "System.Private.CoreLib.dll" or "System.Runtime.dll" or "netstandard.dll")
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(INode).Assembly.Location));

        // The cross-assembly shape uses a non-collectible base type declared next to this helper,
        // so the plugin source must be able to reference the test assembly.
        references.Add(MetadataReference.CreateFromFile(typeof(CollectiblePluginAssembly).Assembly.Location));
        return [.. references];
    }
}
