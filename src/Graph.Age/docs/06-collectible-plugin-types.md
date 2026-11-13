# Collectible types in depth planning

The AGE provider discovers derived CLR types when it bounds complex-property hydration.
`GraphDataModel` measures loaded plugin types without retaining them in its shared derived-type
index or depth cache. This keeps the new planning caches from preventing a collectible
`AssemblyLoadContext` from unloading.

This guarantee covers depth planning. It does not establish an end-to-end unload contract for
schema registries, serializers, label-resolution caches, materialized entities, or host delegates.
A host that materializes plugin entities must account for those other references separately.

## Example

This example measures a plugin type without materializing entities or registering it in a store.
Keep the assembly and type references inside a separate method, so they leave scope before the
host checks whether the context was reclaimed.

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Cvoya.Graph;

var weak = MeasurePluginDepth("/plugins/Contracts.Plugin.dll");
for (var attempt = 0; attempt < 10 && weak.IsAlive; attempt++)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
}
if (weak.IsAlive)
{
    throw new InvalidOperationException("The plugin load context is still rooted.");
}

[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference MeasurePluginDepth(string assemblyPath)
{
    var context = new AssemblyLoadContext("cvoya-depth-plugin", isCollectible: true);
    try
    {
        var assembly = context.LoadFromAssemblyPath(assemblyPath);
        var type = assembly.GetType("Contracts.Plugin.PluginContract", throwOnError: true)!;
        Console.WriteLine(GraphDataModel.GetComplexPropertyDepth(type));
    }
    finally
    {
        context.Unload();
    }

    return new WeakReference(context);
}
```

Loading and registering plugin labels remains host-specific. Load the plugin assembly before a
query that needs its derived types is planned. A later load changes future depth measurements;
it does not change the bound of a query that was already planned.

## What changes in the database

Depth discovery creates **no tables, columns, indexes, functions, triggers, or catalog rows**.
Its state is entirely in the process:

- a shared derived-type index is built for a set of loaded assembly identities;
- collectible assemblies are excluded from that shared index;
- a collectible root type is measured against a transient scan of loaded assemblies;
- a constructed generic type with a collectible argument also bypasses the depth cache, even when
  its generic definition belongs to a non-collectible assembly;
- a non-collectible base type uses the shared index plus a transient scan of loaded plugin
  assemblies;
- shared depth-cache keys contain non-collectible types and the values are integer bounds.

## Multiple clients and query shapes

Each process discovers its own loaded assemblies. Different clients can compute different depth
bounds for the same declared type. Depth discovery needs no database locks, DDL, or coordination.
The host must ensure it can materialize the labels it reads; a missing plugin is not a guarantee
that an abstract shared base can be instantiated instead.

The planner accounts for loaded derived types that add nested complex properties, including
collections of derived values. Dynamic, interface-typed, and unresolved root shapes retain the
global `GraphDataModel.DefaultDepthAllowed` bound (currently 5). Measurement is bounded by the
configured maximum; the existing storage and traversal validation rules still apply.

Repeated measurements reuse the non-collectible index. Changes to the loaded-assembly identity
set invalidate it, including replacing assemblies without changing the assembly count. No index
or aggregate declaration is needed for this behavior.

## Cleanup and verification

For a depth-only plugin lifecycle, release the host's assembly/type references and request
`AssemblyLoadContext.Unload()`. The planning caches do not require a manual clear. This says
nothing about references created by other provider or application components.

The regression suite verifies reclamation without clearing the production caches first. It
covers collectible roots, derived types across plugin assemblies, non-collectible bases with
collectible derived types, and constructed generics containing collectible arguments. It also
checks that derived nested properties contribute to the measured depth and that later planning
observes changes in the loaded assemblies.

To verify a host integration, test the depth-only lifecycle separately from any schema or entity
materialization lifecycle. Use a weak reference and scoped helper as above, and verify the depth
before and after loading a derived type. Keep plugin loading out of the request path when possible:
identity-set changes can require rebuilding the index, and deeper loaded types can increase the
work needed to hydrate an entity.
