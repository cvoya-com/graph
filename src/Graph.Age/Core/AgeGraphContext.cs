// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Core;

using Cvoya.Graph.Age.Entities;
using Cvoya.Graph.Age.Schema;
using Cvoya.Graph.Serialization;
using Microsoft.Extensions.Logging;

internal sealed class AgeGraphContext
{
    private AgeNodeManager? nodeManager;
    private AgeRelationshipManager? relationshipManager;
    private AgeSubgraphManager? subgraphManager;
    private ComplexPropertyManager? complexPropertyManager;
    private EntityFactory? entityFactory;
    private AgeSchemaManager? schemaManager;

    public AgeGraphContext(
        AgeGraph graph,
        AgeGraphStore store,
        ILoggerFactory? loggerFactory,
        SchemaRegistry schemaRegistry)
    {
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Store = store ?? throw new ArgumentNullException(nameof(store));
        LoggerFactory = loggerFactory;
        SchemaRegistry = schemaRegistry ?? throw new ArgumentNullException(nameof(schemaRegistry));
    }

    public AgeGraph Graph { get; }

    public AgeGraphStore Store { get; }

    public string GraphName => Store.GraphName;

    /// <summary>Gets whether the store's AGE version accepts the native <c>IN</c> id form.</summary>
    internal bool SupportsNativeIdInPredicates => Store.SupportsNativeIdInPredicates;

    public ILoggerFactory? LoggerFactory { get; }

    public SchemaRegistry SchemaRegistry { get; }

    internal AgeNodeManager NodeManager => nodeManager ??= new(this);

    internal AgeRelationshipManager RelationshipManager => relationshipManager ??= new(this);

    internal AgeSubgraphManager SubgraphManager => subgraphManager ??= new(this);

    internal ComplexPropertyManager ComplexPropertyManager => complexPropertyManager ??= new(this);

    internal EntityFactory EntityFactory => entityFactory ??= new(LoggerFactory);

    internal AgeSchemaManager SchemaManager => schemaManager ??= new(this, SchemaRegistry);
}
