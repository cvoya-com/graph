# Collectible plugin types

The AGE provider discovers derived CLR types when it bounds complex-property hydration. A
collectible `AssemblyLoadContext` must be able to unload after its plugin types are used, so the
provider deliberately does not cache plugin `Type` objects. This document explains the behavior
and its limits for plugin architectures.

## Example

```csharp
using System.Reflection;
using System.Runtime.Loader;
using Cvoya.Graph;
using Cvoya.Graph.Age;

// Shared contract (loaded in the default context).
public abstract class PluginDocument : Node
{
    public string Name { get; set; } = string.Empty;
    public PluginMetadata? Metadata { get; set; }
}

public sealed class PluginMetadata
{
    public string Owner { get; set; } = string.Empty;
}

// Plugin assembly (collectible context) defines:
// [Node("PluginContract")]
// public sealed class PluginContract : PluginDocument
// {
//     public PluginAddress? Extra { get; set; }
// }

var context = new AssemblyLoadContext("cvoya-plugin", isCollectible: true);
var assembly = context.LoadFromAssemblyPath("/plugins/Contracts.Plugin.dll");
_ = assembly.GetType("Contracts.Plugin.PluginContract", throwOnError: true);

var registry = new SchemaRegistry();
registry.Node<PluginDocument>()
    .AddPropertyIndex(document => document.Name);

await using (var store = new AgeGraphStore(connectionString, "contracts", registry))
{
    await store.CreateGraphIfNotExistsAsync(cancellationToken);
    await store.EnsureIndexesAsync(cancellationToken);

    // The derived PluginContract / Extra values hydrate because the plugin assembly is loaded
    // before this query is planned.
    var document = await store.Graph.Nodes<PluginDocument>()
        .Where(document => document.Name == "agreement-1")
        .SingleAsync(cancellationToken);
    Console.WriteLine(document.Name);
}

// All materialized plugin instances are out of scope; the collectible context can unload.
context.Unload();
```

Loading and registering plugin labels is host-specific. What this document guarantees is the
provider's type-discovery behavior, not a plugin loader. Load the plugin assembly before the first
query that needs its derived types; a collectible plugin assembly is scanned transiently at plan
time rather than cached.

## What changes in the database

This feature creates **no tables, columns, indexes, functions, triggers, or catalog rows**. It is a
read-time type-discovery behavior for complex-property hydration.

The relevant state is entirely in the process:

- a shared derived-type index is built for a set of loaded assembly identities;
- a `Type` cached in that index would root its collectible load context, so collectible assemblies
  are excluded from the shared index;
- a collectible base type is measured against a transient scan of all loaded assemblies;
- a constructed generic type with a collectible argument also bypasses the depth cache, even when
  its generic definition belongs to a non-collectible assembly;
- a non-collectible base type uses the shared index plus a transient scan of loaded plugin
  assemblies;
- only the resulting integer depth bound is cached. No plugin `Type` or assembly object is retained.

## Scale-out and multiple clients

- Each process discovers its own loaded assemblies. Different clients may compute different depth
  bounds for the same stored data; each hydrates exactly the types it can materialize.
- No database locks, DDL, or coordination are involved.
- A client that has not loaded a plugin assembly materializes the shared base shape; it does not
  fail and does not cache a plugin type from another process.
- If a plugin is unloaded and a different assembly with the same identity set is loaded, the
  identity-set comparison rebuilds the index instead of mistaking the set for unchanged.
- Operators should ensure the plugin load path is available before serving queries, and should
  recycle the process after changing plugins if the host caches materialized plugin instances.

## Accelerated query shapes

Handled:

- materializing a shared base type whose concrete plugin-derived value adds nested complex
  properties;
- collections of plugin-derived values when the plugin assembly is loaded;
- reads that omit the complex property (no plugin scan is needed for that shape);
- unloading a collectible plugin after all plugin instances and the store are released.

Not handled / limited:

- dynamic, interface-typed, or otherwise unresolvable shapes fall back to the global
  `GraphDataModel.DefaultDepthAllowed` bound (currently 5);
- a plugin type loaded **after** a query has already planned a shape is not part of that query's
  bound; plan the query after loading the plugin;
- a shape deeper than the bound fails with a `GraphException`; it is never silently truncated.

## Fallback and idempotency

- **No declaration:** plugin-derived hydration needs no index or aggregate declaration.
- **Repeated reads:** deterministic; the type bound is recomputed when the loaded-assembly identity
  set changes.
- **Unload:** the collectible context can unload once the host drops its own references; the
  provider retains no plugin `Type`.
- **Failed plugin load:** the host sees the ordinary loader exception; the provider does not cache a
  partial type set.
- **No runtime fallback to a cached plugin type:** a collectible type is never stored in the shared
  index.

## Breaking change and cleanup

This feature has no `Status`/`Kaufpreis` physical names and creates no database objects to clean up.
The general breaking change applies to declared index/aggregate objects; see
[declared equality indexes](01-declared-equality-indexes.md#breaking-change-and-cleanup).

Unloading a plugin is the cleanup operation: release the store, every materialized plugin entity,
and the plugin's own references, then call `AssemblyLoadContext.Unload()`. There is no runtime
fallback that rehydrates a plugin type after its assembly is gone.

## Risks and verification checklist

Risks:

- a host that caches materialized plugin entities or delegates roots the load context and prevents
  unloading, even though the provider itself does not;
- every change to the loaded-assembly identity set can rebuild the derived-type index; avoid loading
  and unloading plugins per request;
- plugin-derived nested values deepen the hydration bound and can make a wide read expensive;
- different clients may see different materialized shapes for the same stored data.

Verify:

```csharp
var weak = new WeakReference(context);
await store.DisposeAsync();
context.Unload();
for (var attempt = 0; attempt < 10 && weak.IsAlive; attempt++)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
}
if (weak.IsAlive)
{
    throw new InvalidOperationException("The plugin load context is still rooted.");
}
```

Also assert that a query planned before a plugin load materializes the base shape, and a query
planned after the load materializes the derived nested shape. If both return the same stored rows,
the provider's type bound is behaving correctly.
