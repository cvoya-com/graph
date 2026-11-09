// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using Cvoya.Graph.Age.Core;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.Age.Serialization;
using Cvoya.Graph.CompatibilityTests;

/// <summary>Native AGE shape and external-data interoperability coverage.</summary>
public sealed class AgeNativeInteropTests(AgeHarness harness)
    : AgeTest(harness, StoreIsolation.FreshStore)
{
    [Node("Mapped Person")]
    public sealed record MappedPerson : Node
    {
        public string TestKey { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = string.Empty;
    }

    [Relationship("MAPPED KNOWS")]
    public sealed record MappedKnows : Relationship
    {
        public string TestKey { get; set; } = Guid.NewGuid().ToString("N");
    }

    [Fact]
    public async Task RawNativeRowsWithoutCvoyaMetadata_AreTypedDynamicAndTraversable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var marker = $"raw-{Guid.NewGuid():N}";
        await RunRawAsync(
            """
            CREATE (source:Person)
            SET source.FirstName = $sourceName, source.LastName = $marker, source.Age = 31,
                source.inheritance_labels = ['Person']
            CREATE (target:Person)
            SET target.FirstName = $targetName, target.LastName = $marker, target.Age = 32,
                target.inheritance_labels = ['Person']
            CREATE (source)-[relationship:KNOWS]->(target)
            SET relationship.Since = $since
            RETURN true AS created
            """,
            new
            {
                sourceName = "Raw source",
                targetName = "Raw target",
                marker,
                since = DateTime.UnixEpoch.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            },
            cancellationToken);

        var typed = await Graph.Nodes<Person>()
            .Where(person => person.LastName == marker)
            .OrderBy(person => person.FirstName)
            .ToListAsync(cancellationToken);
        var dynamicNodes = await Graph.DynamicNodes().ToListAsync(cancellationToken);
        var relationships = await Graph.Relationships<Knows>().ToListAsync(cancellationToken);
        var traversed = await Graph.Nodes<Person>()
            .Where(person => person.FirstName == "Raw source")
            .Traverse<Knows, Person>()
            .ToListAsync(cancellationToken);

        Assert.Equal(["Raw source", "Raw target"], typed.Select(person => person.FirstName));
        Assert.Equal(2, dynamicNodes.Count);
        Assert.All(dynamicNodes, node => Assert.Equal(["Person"], node.Labels));
        Assert.DoesNotContain(dynamicNodes, node => node.Labels.Contains("CvoyaNode", StringComparer.Ordinal));
        Assert.Single(relationships);
        Assert.Equal("KNOWS", relationships[0].Type);
        Assert.Equal("Raw target", Assert.Single(traversed).FirstName);
    }

    [Fact]
    public async Task DynamicRootQueriesExcludeProviderOwnedComplexValueNodes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var person = new PersonWithComplexProperty
        {
            FirstName = "Root",
            Address = new AddressValue { Street = "Value node", City = "Nowhere" },
        };
        await Graph.CreateNodeAsync(person, cancellationToken: cancellationToken);

        var dynamicNodes = await Graph.DynamicNodes().ToListAsync(cancellationToken);

        var root = Assert.Single(dynamicNodes);
        Assert.Equal(person.FirstName, root.Properties[nameof(Person.FirstName)]);
        Assert.DoesNotContain("CvoyaNode", root.Labels);
    }

    [Fact]
    public async Task SubgraphWritesUseStableNativeStorageForNonSymbolicMappedNames()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = new MappedPerson { Name = "Source" };
        var target = new MappedPerson { Name = "Target" };
        var relationship = new MappedKnows();

        await Graph.CreateAsync(
            source,
            relationship,
            target,
            cancellationToken: cancellationToken);

        var shape = await RunRawForRecordAsync(
            """
            MATCH (source {TestKey: $sourceKey})
                  -[relationship {TestKey: $relationshipKey}]->
                  (target {TestKey: $targetKey})
            RETURN head(labels(source)) AS sourceLabel,
                   type(relationship) AS relationshipType,
                   source.inheritance_labels[0] AS logicalNodeLabel,
                   relationship.inheritance_labels[0] AS logicalRelationshipType,
                   relationship.Type IS NULL AS noStoredType
            """,
            new
            {
                sourceKey = source.TestKey,
                relationshipKey = relationship.TestKey,
                targetKey = target.TestKey,
            },
            cancellationToken);

        Assert.Equal(
            SerializationBridge.GetRootStorageName("Mapped Person", relationship: false),
            shape["sourceLabel"].As<string>());
        Assert.Equal(
            SerializationBridge.GetRootStorageName("MAPPED KNOWS", relationship: true),
            shape["relationshipType"].As<string>());
        Assert.Equal("Mapped Person", shape["logicalNodeLabel"].As<string>());
        Assert.Equal("MAPPED KNOWS", shape["logicalRelationshipType"].As<string>());
        Assert.True(shape["noStoredType"].As<bool>());
        Assert.Equal("Source", (await Graph.FindNodeAsync(source, cancellationToken: cancellationToken)).Name);
        Assert.Equal(
            relationship.TestKey,
            (await Graph.FindRelationshipAsync(relationship, cancellationToken: cancellationToken)).TestKey);
    }

    [Fact]
    public async Task NewWritesUseLogicalNativeStorage_AndPlainIdsAreNotImplicitlyUnique()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var duplicateId = $"duplicate-{Guid.NewGuid():N}";
        var targetId = $"target-{Guid.NewGuid():N}";
        var source = new AtomicOrdinaryIdNode { Id = duplicateId, Marker = "First" };
        var duplicate = new AtomicOrdinaryIdNode { Id = duplicateId, Marker = "Second" };
        var target = new AtomicOrdinaryIdNode { Id = targetId, Marker = "Target" };
        await Graph.CreateNodeAsync(source, cancellationToken: cancellationToken);
        await Graph.CreateNodeAsync(target, cancellationToken: cancellationToken);
        var sourceQuery = Graph.Nodes<AtomicOrdinaryIdNode>().Where(node => node.Id == duplicateId);
        var targetQuery = Graph.Nodes<AtomicOrdinaryIdNode>().Where(node => node.Id == targetId);
        await Graph.CreateRelationshipAsync(
            sourceQuery,
            new AtomicMutationRelationship { Code = Guid.NewGuid().ToString("N") },
            targetQuery,
            cancellationToken: cancellationToken);
        await Graph.CreateRelationshipAsync(
            sourceQuery,
            new AtomicMutationRelationship { Code = Guid.NewGuid().ToString("N") },
            targetQuery,
            cancellationToken: cancellationToken);
        await Graph.CreateNodeAsync(duplicate, cancellationToken: cancellationToken);

        var shape = await RunRawForRecordAsync(
            """
            MATCH (source:AtomicOrdinaryIdNode {ordinary_id: $sourceId})
                  -[relationship:ATOMIC_MUTATION_RELATIONSHIP]->
                  (target:AtomicOrdinaryIdNode {ordinary_id: $targetId})
            RETURN head(labels(source)) AS sourceLabel,
                   type(relationship) AS relationshipType,
                   source.inheritance_labels[0] AS nodeLabel,
                   source.__graphModelEntityKind__ IS NULL AS noEntityKind,
                   relationship.Type IS NULL AS noStoredType,
                   relationship.inheritance_labels IS NULL AS noRelationshipHierarchy,
                   count(relationship) AS relationshipCount
            """,
            new
            {
                sourceId = source.Id,
                targetId = target.Id,
            },
            cancellationToken);

        Assert.Equal("AtomicOrdinaryIdNode", shape["sourceLabel"].As<string>());
        Assert.Equal("ATOMIC_MUTATION_RELATIONSHIP", shape["relationshipType"].As<string>());
        Assert.Equal("AtomicOrdinaryIdNode", shape["nodeLabel"].As<string>());
        Assert.True(shape["noEntityKind"].As<bool>());
        Assert.True(shape["noStoredType"].As<bool>());
        Assert.True(shape["noRelationshipHierarchy"].As<bool>());
        Assert.Equal(2, shape["relationshipCount"].As<int>());
        Assert.Equal(2, await Graph.Nodes<AtomicOrdinaryIdNode>()
            .Where(node => node.Id == duplicateId)
            .CountAsync(cancellationToken));
        Assert.Equal(2, await GraphCommandExtensions.UpdateAsync(
            Graph.Nodes<AtomicOrdinaryIdNode>().Where(node => node.Id == duplicateId),
            setters => setters.SetProperty(node => node.Marker, "Updated"),
            cancellationToken));
    }

    [Fact]
    public async Task SetBasedMutationKeepsFrozenNativeIdentityWhenIdPropertyChanges()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var oldId = $"before-{Guid.NewGuid():N}";
        var person = new AtomicOrdinaryIdNode { Id = oldId, Marker = "Before" };
        await Graph.CreateNodeAsync(person, cancellationToken: cancellationToken);
        var newId = $"changed-{Guid.NewGuid():N}";

        var affected = await GraphCommandExtensions.UpdateAsync(
            Graph.Nodes<AtomicOrdinaryIdNode>().Where(candidate => candidate.Id == oldId),
            setters => setters
                .SetProperty(candidate => candidate.Id, newId)
                .SetProperty(candidate => candidate.Marker, "After"),
            cancellationToken);

        Assert.Equal(1, affected);
        Assert.Equal("After", (await Graph.Nodes<AtomicOrdinaryIdNode>()
            .Where(candidate => candidate.Id == newId)
            .SingleAsync(cancellationToken)).Marker);
        Assert.Empty(await Graph.Nodes<AtomicOrdinaryIdNode>()
            .Where(candidate => candidate.Id == oldId)
            .ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task ConstrainedNodeUpdateSeesRawNativeRowsButNotUnrelatedLabels()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var marker = $"atomic-node-{Guid.NewGuid():N}";
        var externalEmail = $"external-{Guid.NewGuid():N}@example.test";
        var unrelatedEmail = $"unrelated-{Guid.NewGuid():N}@example.test";
        await Graph.CreateNodeAsync(
            new AtomicMutationNode
            {
                KeyGroup = marker,
                KeyCode = "selected",
                Email = $"selected-{Guid.NewGuid():N}@example.test",
                Marker = marker,
            },
            cancellationToken: cancellationToken);
        await RunRawAsync(
            """
            CREATE (external:AtomicMutationNode)
            SET external.KeyGroup = $marker,
                external.KeyCode = 'external',
                external.Email = $externalEmail,
                external.Marker = 'raw-native'
            CREATE (unrelated:CvoyaNode)
            SET unrelated.inheritance_labels = ['AtomicMutationNode'],
                unrelated.Email = $unrelatedEmail,
                unrelated.Marker = 'unrelated-label'
            RETURN true AS created
            """,
            new { marker, externalEmail, unrelatedEmail },
            cancellationToken);

        await Assert.ThrowsAsync<GraphException>(() => GraphCommandExtensions.UpdateAsync(
            Graph.Nodes<AtomicMutationNode>().Where(candidate => candidate.Marker == marker),
            setters => setters
                .SetProperty(candidate => candidate.Email, externalEmail)
                .SetProperty(candidate => candidate.Marker, "must-roll-back"),
            cancellationToken));

        var affected = await GraphCommandExtensions.UpdateAsync(
            Graph.Nodes<AtomicMutationNode>().Where(candidate => candidate.Marker == marker),
            setters => setters.SetProperty(candidate => candidate.Email, unrelatedEmail),
            cancellationToken);
        var storedEmail = await Graph.Nodes<AtomicMutationNode>()
            .Where(candidate => candidate.Marker == marker)
            .Select(candidate => candidate.Email)
            .SingleAsync(cancellationToken);

        Assert.Equal(1, affected);
        Assert.Equal(unrelatedEmail, storedEmail);
    }

    [Fact]
    public async Task ConstrainedRelationshipUpdateSeesRawNativeRowsButNotUnrelatedTypes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var marker = $"atomic-relationship-{Guid.NewGuid():N}";
        var externalCode = $"external-{Guid.NewGuid():N}";
        var unrelatedCode = $"unrelated-{Guid.NewGuid():N}";
        var source = new Person { FirstName = marker + "-source" };
        var target = new Person { FirstName = marker + "-target" };
        await Graph.CreateNodeAsync(source, cancellationToken: cancellationToken);
        await Graph.CreateNodeAsync(target, cancellationToken: cancellationToken);
        await Graph.ConnectAsync(
            source,
            new AtomicMutationRelationship
            {
                Code = $"selected-{Guid.NewGuid():N}",
                Marker = marker,
            },
            target,
            cancellationToken: cancellationToken);
        await RunRawAsync(
            """
            MATCH (source:Person {TestKey: $sourceKey}), (target:Person {TestKey: $targetKey})
            CREATE (source)-[external:ATOMIC_MUTATION_RELATIONSHIP]->(target)
            SET external.Code = $externalCode, external.Marker = 'raw-native'
            CREATE (source)-[unrelated:CvoyaRelationship]->(target)
            SET unrelated.Type = 'ATOMIC_MUTATION_RELATIONSHIP',
                unrelated.Code = $unrelatedCode,
                unrelated.Marker = 'unrelated-type'
            RETURN true AS created
            """,
            new
            {
                sourceKey = source.TestKey,
                targetKey = target.TestKey,
                externalCode,
                unrelatedCode,
            },
            cancellationToken);

        await Assert.ThrowsAsync<GraphException>(() => GraphCommandExtensions.UpdateAsync(
            Graph.Relationships<AtomicMutationRelationship>().Where(candidate => candidate.Marker == marker),
            setters => setters
                .SetProperty(candidate => candidate.Code, externalCode)
                .SetProperty(candidate => candidate.Marker, "must-roll-back"),
            cancellationToken));

        var affected = await GraphCommandExtensions.UpdateAsync(
            Graph.Relationships<AtomicMutationRelationship>().Where(candidate => candidate.Marker == marker),
            setters => setters.SetProperty(candidate => candidate.Code, unrelatedCode),
            cancellationToken);
        var storedCode = await Graph.Relationships<AtomicMutationRelationship>()
            .Where(candidate => candidate.Marker == marker)
            .Select(candidate => candidate.Code)
            .SingleAsync(cancellationToken);

        Assert.Equal(1, affected);
        Assert.Equal(unrelatedCode, storedCode);
    }

    [Fact]
    public async Task RelationshipCommandsSupportEveryEndpointIntentAndExplicitSelfLoop()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var selectedSource = new Person { FirstName = "Selected source" };
        var selectedTarget = new Person { FirstName = "Selected target" };
        await Graph.CreateNodeAsync(selectedSource, cancellationToken: cancellationToken);
        await Graph.CreateNodeAsync(selectedTarget, cancellationToken: cancellationToken);
        var sourceQuery = Graph.Nodes<Person>().Where(node => node.TestKey == selectedSource.TestKey);
        var targetQuery = Graph.Nodes<Person>().Where(node => node.TestKey == selectedTarget.TestKey);

        await Graph.CreateRelationshipAsync(
            sourceQuery,
            new Knows(),
            targetQuery,
            RelationshipDirection.Outgoing,
            cancellationToken);
        await Graph.CreateAsync(
            sourceQuery,
            new Knows(),
            new Person { FirstName = "New target" },
            cancellationToken: cancellationToken);
        await Graph.CreateAsync(
            new Person { FirstName = "New source" },
            new Knows(),
            targetQuery,
            cancellationToken: cancellationToken);

        var allNewSource = new Person { FirstName = "All-new source" };
        var allNewTarget = new Person { FirstName = "All-new target" };
        await Graph.CreateAsync(
            allNewSource,
            new Knows(),
            allNewTarget,
            cancellationToken: cancellationToken);
        var selfLoop = new Person { FirstName = "Self loop" };
        await Graph.CreateSelfLoopAsync(
            selfLoop,
            new Knows(),
            cancellationToken: cancellationToken);

        var equalSource = new Person { TestKey = $"equal-{Guid.NewGuid():N}", FirstName = "Equal" };
        var equalTarget = equalSource with { };
        Assert.Equal(equalSource, equalTarget);
        Assert.NotSame(equalSource, equalTarget);
        await Graph.CreateAsync(
            equalSource,
            new Knows(),
            equalTarget,
            cancellationToken: cancellationToken);

        Assert.Equal(6, await Graph.Relationships<Knows>().CountAsync(cancellationToken));
        Assert.Equal(2, await Graph.Nodes<Person>()
            .Where(node => node.TestKey == equalSource.TestKey)
            .CountAsync(cancellationToken));
        var loopCount = await RunRawForRecordAsync(
            """
            MATCH (node:Person {TestKey: $key})-[relationship:KNOWS]->(node)
            RETURN count(relationship) AS loopCount
            """,
            new { key = selfLoop.TestKey },
            cancellationToken);
        Assert.Equal(1, loopCount["loopCount"].As<int>());
        var syntheticDirectionCount = await RunRawForRecordAsync(
            "MATCH ()-[relationship:KNOWS]->() WHERE relationship.Direction IS NOT NULL " +
            "RETURN count(relationship) AS relationshipCount",
            new { },
            cancellationToken);
        Assert.Equal(0, syntheticDirectionCount["relationshipCount"].As<int>());
    }

    [Fact]
    public async Task FailedRelationshipCommandRollsBackItsCallerTransactionSavepoint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var createdBeforeFailure = new Person { FirstName = "Must roll back" };
        var invalidTarget = new DynamicNode(
            ["invalid\nlabel"],
            new Dictionary<string, object?> { ["name"] = "invalid" });
        var survivor = new Person { FirstName = "Caller can continue" };

        await using (var transaction = await Graph.GetTransactionAsync(cancellationToken))
        {
            await Assert.ThrowsAsync<GraphException>(() => Graph.CreateAsync(
                createdBeforeFailure,
                new Knows(),
                invalidTarget,
                RelationshipDirection.Outgoing,
                transaction,
                cancellationToken));

            await Graph.CreateNodeAsync(survivor, transaction, cancellationToken);
            await transaction.CommitAsync();
        }

        Assert.Empty(await Graph.Nodes<Person>()
            .Where(candidate => candidate.TestKey == createdBeforeFailure.TestKey)
            .ToListAsync(cancellationToken));
        Assert.Equal(
            survivor.TestKey,
            (await Graph.FindNodeAsync(survivor, cancellationToken: cancellationToken)).TestKey);
    }

    private async Task RunRawAsync(string cypher, object parameters, CancellationToken cancellationToken)
    {
        await using var transaction = await Graph.GetTransactionAsync(cancellationToken);
        await using var result = await ((AgeGraphTransaction)transaction).Runner
            .RunAsync(cypher, parameters, cancellationToken);
        _ = await result.SingleAsync(cancellationToken);
        await transaction.CommitAsync();
    }

    private async Task<AgeRecord> RunRawForRecordAsync(
        string cypher,
        object parameters,
        CancellationToken cancellationToken)
    {
        await using var transaction = await Graph.GetTransactionAsync(cancellationToken);
        await using var result = await ((AgeGraphTransaction)transaction).Runner
            .RunAsync(cypher, parameters, cancellationToken);
        var record = await result.SingleAsync(cancellationToken);
        await transaction.CommitAsync();
        return record;
    }

}
