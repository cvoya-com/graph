// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.CompatibilityTests;

using Cvoya.Graph.Querying;
using Cvoya.Graph.Querying.Commands;

/// <summary>Provider contract tests for native target selection and set-based command execution.</summary>
public interface IGraphCommandTests : IGraphTest
{
    [Fact]
    public async Task ByteArrayScalar_RoundTripsAndSupportsConstantUpdate()
    {
        var marker = $"command-binary-{Guid.NewGuid():N}";
        var original = new byte[] { 0, 1, 127, 128, 255 };
        var updated = new byte[] { 255, 128, 127, 1, 0 };
        await Graph.CreateNodeAsync(
            new BinaryPropertyNode { TestKey = marker, Data = original },
            cancellationToken: TestContext.Current.CancellationToken);

        var created = await Graph.Nodes<BinaryPropertyNode>()
            .Where(candidate => candidate.TestKey == marker && candidate.Data != null)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(original, created.Data);

        var affected = await Graph.Nodes<BinaryPropertyNode>()
            .Where(candidate => candidate.TestKey == marker)
            .UpdateAsync(
                setters => setters.SetProperty(candidate => candidate.Data, updated),
                TestContext.Current.CancellationToken);
        var stored = await Graph.Nodes<BinaryPropertyNode>()
            .SingleAsync(candidate => candidate.TestKey == marker, TestContext.Current.CancellationToken);

        Assert.Equal(1, affected);
        Assert.Equal(updated, stored.Data);
    }

    [Fact]
    [RequiresCapability(GraphCapability.NullElementsInSimpleCollections)]
    public async Task SimpleCollectionUpdate_PreservesNullTransitionsAndDynamicType()
    {
        var marker = $"command-null-collection-{Guid.NewGuid():N}";
        var originalUniqueValue = $"collection-original-{Guid.NewGuid():N}";
        var updatedUniqueValue = $"collection-updated-{Guid.NewGuid():N}";
        var node = new NullableCollectionCommandNode
        {
            Marker = marker,
            UniqueValue = originalUniqueValue,
            Values = ["before"],
        };
        await Graph.CreateNodeAsync(node, cancellationToken: TestContext.Current.CancellationToken);

        var mixed = new List<string?> { null, "middle", null, null };
        var affected = await Graph.Nodes<NullableCollectionCommandNode>()
            .Where(candidate => candidate.Marker == marker)
            .UpdateAsync(
                setters => setters.SetProperty(candidate => candidate.Values, mixed),
                TestContext.Current.CancellationToken);
        var stored = await Graph.Nodes<NullableCollectionCommandNode>()
            .SingleAsync(candidate => candidate.Marker == marker, TestContext.Current.CancellationToken);
        Assert.Equal(1, affected);
        Assert.Equal(mixed, stored.Values);

        affected = await Graph.Nodes<NullableCollectionCommandNode>()
            .Where(candidate => candidate.Marker == marker)
            .UpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.UniqueValue, updatedUniqueValue)
                    .SetProperty(candidate => candidate.Values, mixed),
                TestContext.Current.CancellationToken);
        stored = await Graph.Nodes<NullableCollectionCommandNode>()
            .SingleAsync(candidate => candidate.Marker == marker, TestContext.Current.CancellationToken);
        Assert.Equal(1, affected);
        Assert.Equal(updatedUniqueValue, stored.UniqueValue);
        Assert.Equal(mixed, stored.Values);

        var allNull = new List<string?> { null, null };
        affected = await Graph.Nodes<NullableCollectionCommandNode>()
            .Where(candidate => candidate.Marker == marker)
            .UpdateAsync(
                setters => setters.SetProperty(candidate => candidate.Values, allNull),
                TestContext.Current.CancellationToken);
        stored = await Graph.Nodes<NullableCollectionCommandNode>()
            .SingleAsync(candidate => candidate.Marker == marker, TestContext.Current.CancellationToken);
        Assert.Equal(1, affected);
        Assert.Equal(allNull, stored.Values);

        var empty = new List<string?>();
        affected = await Graph.Nodes<NullableCollectionCommandNode>()
            .Where(candidate => candidate.Marker == marker)
            .UpdateAsync(
                setters => setters.SetProperty(candidate => candidate.Values, empty),
                TestContext.Current.CancellationToken);
        stored = await Graph.Nodes<NullableCollectionCommandNode>()
            .SingleAsync(candidate => candidate.Marker == marker, TestContext.Current.CancellationToken);
        Assert.Equal(1, affected);
        Assert.Empty(stored.Values);

        var nonNull = new List<string?> { "after-empty" };
        affected = await Graph.Nodes<NullableCollectionCommandNode>()
            .Where(candidate => candidate.Marker == marker)
            .UpdateAsync(
                setters => setters.SetProperty(candidate => candidate.Values, nonNull),
                TestContext.Current.CancellationToken);
        stored = await Graph.Nodes<NullableCollectionCommandNode>()
            .SingleAsync(candidate => candidate.Marker == marker, TestContext.Current.CancellationToken);
        Assert.Equal(1, affected);
        Assert.Equal(nonNull, stored.Values);

        var occupiedUniqueValue = $"collection-occupied-{Guid.NewGuid():N}";
        await Graph.CreateNodeAsync(
            new NullableCollectionCommandNode
            {
                Marker = $"{marker}-blocker",
                UniqueValue = occupiedUniqueValue,
                Values = ["blocker"],
            },
            cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<GraphException>(() => Graph.Nodes<NullableCollectionCommandNode>()
            .Where(candidate => candidate.Marker == marker)
            .UpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.UniqueValue, occupiedUniqueValue)
                    .SetProperty(candidate => candidate.Values, new List<string?> { null, "must-not-commit" }),
                TestContext.Current.CancellationToken));
        stored = await Graph.Nodes<NullableCollectionCommandNode>()
            .SingleAsync(candidate => candidate.Marker == marker, TestContext.Current.CancellationToken);
        Assert.Equal(updatedUniqueValue, stored.UniqueValue);
        Assert.Equal(nonNull, stored.Values);

        var label = $"DynamicNullCollection{Guid.NewGuid():N}";
        await Graph.CreateNodeAsync(
            new DynamicNode([label], new Dictionary<string, object?> { ["value"] = new string?[] { null, "before" } }),
            cancellationToken: TestContext.Current.CancellationToken);
        affected = await Graph.DynamicNodes().OfLabel(label)
            .UpdateAsync(
                setters => setters.SetProperty(candidate => candidate.Properties["value"], "scalar"),
                TestContext.Current.CancellationToken);
        var dynamicStored = await Graph.DynamicNodes().OfLabel(label)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, affected);
        Assert.Equal("scalar", dynamicStored.Properties["value"]);

        var dynamicAllNull = new int?[] { null, null, null };
        affected = await Graph.DynamicNodes().OfLabel(label)
            .UpdateAsync(
                setters => setters.SetProperty(candidate => candidate.Properties["value"], dynamicAllNull),
                TestContext.Current.CancellationToken);
        dynamicStored = await Graph.DynamicNodes().OfLabel(label)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, affected);
        Assert.Equal(dynamicAllNull, Assert.IsType<List<int?>>(dynamicStored.Properties["value"]));

        var deletedTyped = await Graph.Nodes<NullableCollectionCommandNode>()
            .Where(candidate => candidate.Marker == marker)
            .DeleteAsync(cancellationToken: TestContext.Current.CancellationToken);
        var deletedDynamic = await Graph.DynamicNodes().OfLabel(label)
            .DeleteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, deletedTyped);
        Assert.Equal(1, deletedDynamic);
        Assert.Null(await Graph.Nodes<NullableCollectionCommandNode>()
            .SingleOrDefaultAsync(candidate => candidate.Marker == marker, TestContext.Current.CancellationToken));
        Assert.Null(await Graph.DynamicNodes().OfLabel(label)
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task KeyAndUniqueUpdate_CommitsCompleteFinalStateAndExactCount()
    {
        var marker = $"command-constraint-single-{Guid.NewGuid():N}";
        var node = new AtomicMutationNode
        {
            KeyGroup = marker,
            KeyCode = "old-key",
            Email = $"old-{marker}@example.com",
            Marker = marker,
        };
        await Graph.CreateNodeAsync(node, cancellationToken: TestContext.Current.CancellationToken);

        var affected = await GraphCommandExtensions.UpdateAsync(
            Graph.Nodes<AtomicMutationNode>().Where(candidate => candidate.Marker == marker),
            setters => setters
                .SetProperty(candidate => candidate.KeyCode, "new-key")
                .SetProperty(candidate => candidate.Email, $"new-{marker}@example.com"),
            TestContext.Current.CancellationToken);

        var stored = await Graph.Nodes<AtomicMutationNode>()
            .SingleAsync(candidate => candidate.Marker == marker, TestContext.Current.CancellationToken);
        Assert.Equal(1, affected);
        Assert.Equal("new-key", stored.KeyCode);
        Assert.Equal($"new-{marker}@example.com", stored.Email);
    }

    [Fact]
    public async Task KeyAndUniqueUpdate_AllowsValidSelectedSetSwap()
    {
        var marker = $"command-constraint-swap-{Guid.NewGuid():N}";
        var firstKey = $"first-{Guid.NewGuid():N}";
        var secondKey = $"second-{Guid.NewGuid():N}";
        var firstEmail = $"first-{Guid.NewGuid():N}@example.com";
        var secondEmail = $"second-{Guid.NewGuid():N}@example.com";
        await Graph.CreateNodeAsync(new AtomicMutationNode
        {
            KeyGroup = marker,
            KeyCode = firstKey,
            Email = firstEmail,
            Marker = marker,
        }, cancellationToken: TestContext.Current.CancellationToken);
        await Graph.CreateNodeAsync(new AtomicMutationNode
        {
            KeyGroup = marker,
            KeyCode = secondKey,
            Email = secondEmail,
            Marker = marker,
        }, cancellationToken: TestContext.Current.CancellationToken);

        var affected = await GraphCommandExtensions.UpdateAsync(
            Graph.Nodes<AtomicMutationNode>().Where(candidate => candidate.Marker == marker),
            setters => setters
                .SetProperty(
                    candidate => candidate.KeyCode,
                    candidate => candidate.KeyCode == firstKey ? secondKey : firstKey)
                .SetProperty(
                    candidate => candidate.Email,
                    candidate => candidate.Email == firstEmail ? secondEmail : firstEmail),
            TestContext.Current.CancellationToken);

        var stored = await Graph.Nodes<AtomicMutationNode>()
            .Where(candidate => candidate.Marker == marker)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, affected);
        Assert.Equal([firstKey, secondKey], stored.Select(candidate => candidate.KeyCode).Order());
        Assert.Equal([firstEmail, secondEmail], stored.Select(candidate => candidate.Email).Order());
    }

    [Fact]
    public async Task KeyUpdate_SelectedSetDuplicateRollsBackEverySetter()
    {
        var marker = $"command-key-duplicate-{Guid.NewGuid():N}";
        for (var index = 0; index < 2; index++)
        {
            await Graph.CreateNodeAsync(new AtomicMutationNode
            {
                KeyGroup = marker,
                KeyCode = $"key-{index}",
                Email = $"key-{index}-{Guid.NewGuid():N}@example.com",
                Marker = marker,
            }, cancellationToken: TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<GraphException>(() => GraphCommandExtensions.UpdateAsync(
            Graph.Nodes<AtomicMutationNode>().Where(candidate => candidate.Marker == marker),
            setters => setters
                .SetProperty(candidate => candidate.Marker, marker + "-changed")
                .SetProperty(candidate => candidate.KeyCode, "duplicate-key"),
            TestContext.Current.CancellationToken));

        var stored = await Graph.Nodes<AtomicMutationNode>()
            .Where(candidate => candidate.Marker == marker || candidate.Marker == marker + "-changed")
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, candidate => Assert.Equal(marker, candidate.Marker));
        Assert.Equal(["key-0", "key-1"], stored.Select(candidate => candidate.KeyCode).Order());
    }

    [Fact]
    public async Task UniqueUpdate_SelectedSetDuplicateRollsBackEverySetter()
    {
        var marker = $"command-constraint-duplicate-{Guid.NewGuid():N}";
        var duplicateEmail = $"duplicate-{Guid.NewGuid():N}@example.com";
        for (var index = 0; index < 2; index++)
        {
            await Graph.CreateNodeAsync(new AtomicMutationNode
            {
                KeyGroup = marker,
                KeyCode = $"key-{index}",
                Email = $"before-{index}-{Guid.NewGuid():N}@example.com",
                Marker = marker,
            }, cancellationToken: TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<GraphException>(() => GraphCommandExtensions.UpdateAsync(
            Graph.Nodes<AtomicMutationNode>().Where(candidate => candidate.Marker == marker),
            setters => setters
                .SetProperty(candidate => candidate.Marker, marker + "-changed")
                .SetProperty(candidate => candidate.Email, duplicateEmail),
            TestContext.Current.CancellationToken));

        var stored = await Graph.Nodes<AtomicMutationNode>()
            .Where(candidate => candidate.Marker == marker || candidate.Marker == marker + "-changed")
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, candidate => Assert.Equal(marker, candidate.Marker));
        Assert.DoesNotContain(stored, candidate => candidate.Email == duplicateEmail);
    }

    [Fact]
    public async Task UniqueUpdate_UnselectedCollisionRollsBackEverySetter()
    {
        var marker = $"command-constraint-unselected-{Guid.NewGuid():N}";
        var occupiedEmail = $"occupied-{Guid.NewGuid():N}@example.com";
        await Graph.CreateNodeAsync(new AtomicMutationNode
        {
            KeyGroup = marker,
            KeyCode = "selected",
            Email = $"selected-{Guid.NewGuid():N}@example.com",
            Marker = marker,
        }, cancellationToken: TestContext.Current.CancellationToken);
        await Graph.CreateNodeAsync(new AtomicMutationNode
        {
            KeyGroup = marker,
            KeyCode = "unselected",
            Email = occupiedEmail,
            Marker = marker + "-other",
        }, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GraphException>(() => GraphCommandExtensions.UpdateAsync(
            Graph.Nodes<AtomicMutationNode>().Where(candidate => candidate.Marker == marker),
            setters => setters
                .SetProperty(candidate => candidate.Marker, marker + "-changed")
                .SetProperty(candidate => candidate.Email, occupiedEmail),
            TestContext.Current.CancellationToken));

        var selected = await Graph.Nodes<AtomicMutationNode>()
            .SingleAsync(candidate => candidate.KeyCode == "selected" && candidate.KeyGroup == marker,
                TestContext.Current.CancellationToken);
        Assert.Equal(marker, selected.Marker);
        Assert.NotEqual(occupiedEmail, selected.Email);
    }

    [Fact]
    public async Task RelationshipUniqueUpdate_AllowsValidSelectedSetSwap()
    {
        var marker = $"command-relationship-constraint-{Guid.NewGuid():N}";
        var first = new Person { FirstName = marker + "-first" };
        var second = new Person { FirstName = marker + "-second" };
        await Graph.CreateNodeAsync(first, cancellationToken: TestContext.Current.CancellationToken);
        await Graph.CreateNodeAsync(second, cancellationToken: TestContext.Current.CancellationToken);
        var firstCode = $"first-{Guid.NewGuid():N}";
        var secondCode = $"second-{Guid.NewGuid():N}";
        await Graph.ConnectAsync(
            first,
            new AtomicMutationRelationship { Code = firstCode, Marker = marker },
            second,
            cancellationToken: TestContext.Current.CancellationToken);
        await Graph.ConnectAsync(
            second,
            new AtomicMutationRelationship { Code = secondCode, Marker = marker },
            first,
            cancellationToken: TestContext.Current.CancellationToken);

        var affected = await GraphCommandExtensions.UpdateAsync(
            Graph.Relationships<AtomicMutationRelationship>().Where(candidate => candidate.Marker == marker),
            setters => setters.SetProperty(
                candidate => candidate.Code,
                candidate => candidate.Code == firstCode ? secondCode : firstCode),
            TestContext.Current.CancellationToken);

        var stored = await Graph.Relationships<AtomicMutationRelationship>()
            .Where(candidate => candidate.Marker == marker)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, affected);
        Assert.Equal([firstCode, secondCode], stored.Select(candidate => candidate.Code).Order());
    }

    [Fact]
    public async Task OrdinaryIdUpdate_AllowsDuplicateFinalValuesOnKeylessModel()
    {
        var marker = $"command-ordinary-id-{Guid.NewGuid():N}";
        await Graph.CreateNodeAsync(
            new AtomicOrdinaryIdNode { Marker = marker },
            cancellationToken: TestContext.Current.CancellationToken);
        await Graph.CreateNodeAsync(
            new AtomicOrdinaryIdNode { Marker = marker },
            cancellationToken: TestContext.Current.CancellationToken);
        var duplicatedId = $"ordinary-{Guid.NewGuid():N}";

        var affected = await GraphCommandExtensions.UpdateAsync(
            Graph.Nodes<AtomicOrdinaryIdNode>().Where(candidate => candidate.Marker == marker),
            setters => setters.SetProperty(candidate => candidate.Id, duplicatedId),
            TestContext.Current.CancellationToken);

        var storedIds = await Graph.Nodes<AtomicOrdinaryIdNode>()
            .Where(candidate => candidate.Marker == marker)
            .Select(candidate => candidate.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, affected);
        Assert.Equal([duplicatedId, duplicatedId], storedIds);
    }

    [Fact]
    public async Task ZeroTargetMutations_ReturnZero()
    {
        var missing = $"missing-{Guid.NewGuid():N}";

        var updated = await GraphCommandExtensions.UpdateAsync(
            Graph.Nodes<Person>().Where(person => person.TestKey == missing),
            setters => setters.SetProperty(person => person.FirstName, "never"),
            TestContext.Current.CancellationToken);
        var deleted = await GraphCommandExtensions.DeleteAsync(
            Graph.Relationships<Knows>().Where(relationship => relationship.TestKey == missing),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, updated);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task CancelledMutation_LeavesSelectedEntityUnchanged()
    {
        var person = new Person { FirstName = "before" };
        await Graph.CreateNodeAsync(person, cancellationToken: TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GraphCommandExtensions.UpdateAsync(
            Graph.Nodes<Person>().Where(candidate => candidate.TestKey == person.TestKey),
            setters => setters.SetProperty(candidate => candidate.FirstName, "after"),
            cancellation.Token));

        Assert.Equal(
            "before",
            (await Graph.FindNodeByTestKeyAsync<Person>(
                person.TestKey,
                cancellationToken: TestContext.Current.CancellationToken)).FirstName);
    }

    [Fact]
    [RequiresCapability(GraphCapability.FullTextSearch)]
    public async Task SearchSelection_ComposesWithPredicateBeforeMutation()
    {
        var searchTerm = $"commandsearch{Guid.NewGuid():N}";
        var selected = new Person { FirstName = searchTerm, LastName = "selected", Age = 10 };
        var excluded = new Person { FirstName = searchTerm, LastName = "excluded", Age = 20 };
        await Graph.CreateNodeAsync(selected, cancellationToken: TestContext.Current.CancellationToken);
        await Graph.CreateNodeAsync(excluded, cancellationToken: TestContext.Current.CancellationToken);

        var affected = await GraphCommandExtensions.UpdateAsync(
            Graph.Nodes<Person>()
                .Where(person => person.LastName == "selected")
                .Search(searchTerm),
            setters => setters.SetProperty(person => person.Age, 11),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, affected);
        Assert.Equal(
            11,
            (await Graph.FindNodeByTestKeyAsync<Person>(
                selected.TestKey,
                cancellationToken: TestContext.Current.CancellationToken)).Age);
        Assert.Equal(
            20,
            (await Graph.FindNodeByTestKeyAsync<Person>(
                excluded.TestKey,
                cancellationToken: TestContext.Current.CancellationToken)).Age);
    }

    [Fact]
    public async Task RelationshipExistenceSelection_MutatesOnlyParticipatingNodes()
    {
        var marker = $"command-exists-{Guid.NewGuid():N}";
        var selected = new Person { FirstName = "selected", LastName = marker, Age = 10 };
        var excluded = new Person { FirstName = "excluded", LastName = marker, Age = 20 };
        var endpoint = new Person { FirstName = "endpoint" };
        await Graph.CreateNodeAsync(selected, cancellationToken: TestContext.Current.CancellationToken);
        await Graph.CreateNodeAsync(excluded, cancellationToken: TestContext.Current.CancellationToken);
        await Graph.CreateNodeAsync(endpoint, cancellationToken: TestContext.Current.CancellationToken);
        await Graph.ConnectAsync(
            selected,
            new Knows { Since = DateTime.UnixEpoch.AddDays(1) },
            endpoint,
            cancellationToken: TestContext.Current.CancellationToken);
        var threshold = DateTime.UnixEpoch;

        var affected = await GraphCommandExtensions.UpdateAsync(
            Graph.Nodes<Person>()
                .Where(person => person.LastName == marker)
                .WhereHasRelationship<Person, Knows>(
                    GraphTraversalDirection.Outgoing,
                    relationship => relationship.Since >= threshold),
            setters => setters.SetProperty(person => person.Age, person => person.Age + 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, affected);
        Assert.Equal(
            11,
            (await Graph.FindNodeByTestKeyAsync<Person>(
                selected.TestKey,
                cancellationToken: TestContext.Current.CancellationToken)).Age);
        Assert.Equal(
            20,
            (await Graph.FindNodeByTestKeyAsync<Person>(
                excluded.TestKey,
                cancellationToken: TestContext.Current.CancellationToken)).Age);
    }

    [Fact]
    public async Task SetBasedUpdate_AppliesOrderedWindowAndComputedValues()
    {
        var marker = $"command-window-{Guid.NewGuid():N}";
        foreach (var age in new[] { 30, 10, 20, 40 })
        {
            await Graph.CreateNodeAsync(
                new Person { FirstName = $"person-{age}", LastName = marker, Age = age },
                cancellationToken: TestContext.Current.CancellationToken);
        }

        var affected = await GraphCommandExtensions.UpdateAsync(
            Graph.Nodes<Person>()
                .Where(person => person.LastName == marker)
                .OrderBy(person => person.Age)
                .Skip(1)
                .Take(2),
            setters => setters
                .SetProperty(person => person.FirstName, "selected")
                .SetProperty(person => person.Age, person => person.Age + 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, affected);
        var people = await Graph.Nodes<Person>()
            .Where(person => person.LastName == marker)
            .OrderBy(person => person.Age)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([10, 21, 31, 40], people.Select(person => person.Age));
        Assert.Equal(2, people.Count(person => person.FirstName == "selected"));
    }

    [Fact]
    public async Task ComplexUpdate_ReplacesIndependentOwnedTreesAndScalarStateForEverySelectedNode()
    {
        var marker = $"command-complex-many-{Guid.NewGuid():N}";
        var oldNames = new[] { marker + "-old-first", marker + "-old-second" };
        for (var index = 0; index < oldNames.Length; index++)
        {
            await Graph.CreateNodeAsync(
                new ComplexCommandNode
                {
                    Group = marker,
                    Status = "before",
                    Contact = new CommandContactValue
                    {
                        Name = oldNames[index],
                        Address = new AddressValue { Street = "Old Street", City = "Old City" },
                    },
                },
                cancellationToken: TestContext.Current.CancellationToken);
        }

        var unrelated = new DynamicNode(
            [Labels.GetLabelFromType(typeof(CommandContactValue))],
            new Dictionary<string, object?> { [nameof(CommandContactValue.Name)] = marker + "-unrelated" });
        await Graph.CreateNodeAsync(unrelated, cancellationToken: TestContext.Current.CancellationToken);
        var replacementName = marker + "-replacement";
        var replacement = new CommandContactValue
        {
            Name = replacementName,
            Address = new AddressValue { Street = "New Street", City = "New City" },
        };

        var affected = await GraphCommandExtensions.UpdateAsync(
            Graph.Nodes<ComplexCommandNode>().Where(candidate => candidate.Group == marker),
            setters => setters
                .SetProperty(candidate => candidate.Status, "after")
                .SetProperty(candidate => candidate.Contact, replacement),
            TestContext.Current.CancellationToken);

        var stored = await Graph.Nodes<ComplexCommandNode>()
            .Where(candidate => candidate.Group == marker)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, affected);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, candidate =>
        {
            Assert.Equal("after", candidate.Status);
            Assert.NotNull(candidate.Contact);
            Assert.Equal(replacementName, candidate.Contact.Name);
            Assert.Equal("New Street", candidate.Contact.Address?.Street);
            Assert.Equal("New City", candidate.Contact.Address?.City);
        });
        Assert.Equal(
            2,
            await Harness.CountNodesByPropertyAsync(
                Graph,
                Labels.GetLabelFromType(typeof(CommandContactValue)),
                nameof(CommandContactValue.Name),
                [replacementName],
                TestContext.Current.CancellationToken));
        Assert.Equal(
            0,
            await Harness.CountNodesByPropertyAsync(
                Graph,
                Labels.GetLabelFromType(typeof(CommandContactValue)),
                nameof(CommandContactValue.Name),
                oldNames,
                TestContext.Current.CancellationToken));
        Assert.Equal(
            1,
            await Harness.CountNodesByPropertyAsync(
                Graph,
                Labels.GetLabelFromType(typeof(CommandContactValue)),
                nameof(CommandContactValue.Name),
                [marker + "-unrelated"],
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ComplexUpdate_ClearsNullAndPreservesPolymorphicCollectionOrderAndEmptyCollections()
    {
        var marker = $"command-complex-collection-{Guid.NewGuid():N}";
        var node = new ComplexCommandNode
        {
            Group = marker,
            Contact = new CommandContactValue { Name = marker + "-old" },
            Animals = [new AnimalDescription { Name = "old" }],
        };
        await Graph.CreateNodeAsync(node, cancellationToken: TestContext.Current.CancellationToken);
        CommandContactValue? clearedContact = null;
        List<AnimalDescription> replacement =
        [
            new DogDescription { Name = "Rex", Breed = "Labrador" },
            new PoliceDogDescription
            {
                Name = "K9",
                Breed = "Shepherd",
                Badge = "K9-42",
                Handler = new HandlerDescription { Name = "Officer Diaz" },
            },
            new AnimalDescription { Name = "Generic" },
        ];

        var affected = await GraphCommandExtensions.UpdateAsync(
            Graph.Nodes<ComplexCommandNode>().Where(candidate => candidate.Group == marker),
            setters => setters
                .SetProperty(candidate => candidate.Contact, clearedContact)
                .SetProperty(candidate => candidate.Animals, replacement),
            TestContext.Current.CancellationToken);

        var stored = await Graph.Nodes<ComplexCommandNode>()
            .Where(candidate => candidate.Group == marker)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, affected);
        Assert.Null(stored.Contact);
        Assert.Collection(
            stored.Animals,
            animal => Assert.IsType<DogDescription>(animal),
            animal =>
            {
                var policeDog = Assert.IsType<PoliceDogDescription>(animal);
                Assert.Equal("K9-42", policeDog.Badge);
                Assert.Equal("Officer Diaz", policeDog.Handler?.Name);
            },
            animal => Assert.IsType<AnimalDescription>(animal));

        List<AnimalDescription> empty = [];
        affected = await GraphCommandExtensions.UpdateAsync(
            Graph.Nodes<ComplexCommandNode>().Where(candidate => candidate.Group == marker),
            setters => setters.SetProperty(candidate => candidate.Animals, empty),
            TestContext.Current.CancellationToken);

        stored = await Graph.Nodes<ComplexCommandNode>()
            .Where(candidate => candidate.Group == marker)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, affected);
        Assert.Empty(stored.Animals);
    }

    [Fact]
    public async Task NullableComplexUpdate_ReplacesDenseSparseAllNullAndEmptyLayouts()
    {
        var marker = $"nullable-complex-update-{Guid.NewGuid():N}";
        var node = new NullableComplexCollectionNode
        {
            Marker = marker,
            Animals =
            [
                new AnimalDescription { Name = "first" },
                new DogDescription { Name = "second", Breed = "old" },
            ],
        };
        await Graph.CreateNodeAsync(node, cancellationToken: TestContext.Current.CancellationToken);

        List<AnimalDescription?> sparse =
        [
            null,
            new DogDescription { Name = "Rex", Breed = "Labrador" },
            null,
            null,
            new PoliceDogDescription { Name = "K9", Breed = "Shepherd", Badge = "42" },
            null,
        ];
        var affected = await Graph.Nodes<NullableComplexCollectionNode>()
            .Where(candidate => candidate.Marker == marker)
            .UpdateAsync(
                setters => setters.SetProperty(candidate => candidate.Animals, sparse),
                TestContext.Current.CancellationToken);
        var stored = await Graph.Nodes<NullableComplexCollectionNode>()
            .Where(candidate => candidate.Marker == marker)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, affected);
        Assert.Collection(
            stored.Animals,
            Assert.Null,
            animal => Assert.IsType<DogDescription>(animal),
            Assert.Null,
            Assert.Null,
            animal => Assert.IsType<PoliceDogDescription>(animal),
            Assert.Null);

        List<AnimalDescription?> dense =
        [
            new AnimalDescription { Name = "dense-one" },
            new DogDescription { Name = "dense-two", Breed = "new" },
        ];
        affected = await Graph.Nodes<NullableComplexCollectionNode>()
            .Where(candidate => candidate.Marker == marker)
            .UpdateAsync(
                setters => setters.SetProperty(candidate => candidate.Animals, dense),
                TestContext.Current.CancellationToken);
        stored = await Graph.Nodes<NullableComplexCollectionNode>()
            .Where(candidate => candidate.Marker == marker)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, affected);
        Assert.Collection(stored.Animals, Assert.NotNull, Assert.NotNull);

        List<AnimalDescription?> allNull = [null, null, null];
        affected = await Graph.Nodes<NullableComplexCollectionNode>()
            .Where(candidate => candidate.Marker == marker)
            .UpdateAsync(
                setters => setters.SetProperty(candidate => candidate.Animals, allNull),
                TestContext.Current.CancellationToken);
        stored = await Graph.Nodes<NullableComplexCollectionNode>()
            .Where(candidate => candidate.Marker == marker)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, affected);
        Assert.Equal(3, stored.Animals.Count);
        Assert.All(stored.Animals, Assert.Null);

        List<AnimalDescription?> empty = [];
        affected = await Graph.Nodes<NullableComplexCollectionNode>()
            .Where(candidate => candidate.Marker == marker)
            .UpdateAsync(
                setters => setters.SetProperty(candidate => candidate.Animals, empty),
                TestContext.Current.CancellationToken);
        stored = await Graph.Nodes<NullableComplexCollectionNode>()
            .Where(candidate => candidate.Marker == marker)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, affected);
        Assert.Empty(stored.Animals);
    }

    [Fact]
    public async Task DynamicComplexUpdate_ReplacesComplexAndScalarRepresentationsWithoutSyntheticIdentity()
    {
        var label = $"CommandComplexDynamic{Guid.NewGuid():N}";
        var node = new DynamicNode(
            [label],
            new Dictionary<string, object?>
            {
                ["profile"] = new List<string?> { "before", null, "again" },
                ["status"] = "before",
                ["__graphModelComplexMutationLock"] = "preserve",
            });
        await Graph.CreateNodeAsync(node, cancellationToken: TestContext.Current.CancellationToken);
        var replacement = new Dictionary<string, object?>
        {
            ["name"] = "after",
            ["address"] = new Dictionary<string, object?>
            {
                ["city"] = "Seattle",
                ["lines"] = new[] { "one", "two" },
            },
        };

        var affected = await GraphCommandExtensions.UpdateAsync(
            Graph.DynamicNodes().OfLabel(label),
            setters => setters
                .SetProperty(candidate => candidate.Properties["profile"], replacement)
                .SetProperty(candidate => candidate.Properties["status"], "after"),
            TestContext.Current.CancellationToken);

        var stored = await Graph.DynamicNodes().OfLabel(label)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, affected);
        Assert.Equal("after", stored.Properties["status"]);
        Assert.Equal("preserve", stored.Properties["__graphModelComplexMutationLock"]);
        var profile = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(stored.Properties["profile"]);
        Assert.Equal("after", profile["name"]);
        var address = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(profile["address"]);
        Assert.Equal("Seattle", address["city"]);
        Assert.Equal(
            ["one", "two"],
            Assert.IsAssignableFrom<System.Collections.IEnumerable>(address["lines"])
                .Cast<object?>()
                .Cast<string>());
        Assert.DoesNotContain("Id", profile.Keys);
        Assert.DoesNotContain("__nativeId", profile.Keys);

        affected = await GraphCommandExtensions.UpdateAsync(
            Graph.DynamicNodes().OfLabel(label),
            setters => setters.SetProperty(candidate => candidate.Properties["profile"], "flat"),
            TestContext.Current.CancellationToken);

        stored = await Graph.DynamicNodes().OfLabel(label)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, affected);
        Assert.Equal("flat", stored.Properties["profile"]);
        Assert.Equal("preserve", stored.Properties["__graphModelComplexMutationLock"]);
    }

    [Fact]
    public async Task SetBasedRelationshipUpdateAndDelete_UseSelectedRelationships()
    {
        var first = new Person { FirstName = "first" };
        var second = new Person { FirstName = "second" };
        var relationship = new Knows { Since = DateTime.UnixEpoch };
        await Graph.CreateNodeAsync(first, cancellationToken: TestContext.Current.CancellationToken);
        await Graph.CreateNodeAsync(second, cancellationToken: TestContext.Current.CancellationToken);
        await Graph.ConnectAsync(first, relationship, second, cancellationToken: TestContext.Current.CancellationToken);
        var replacement = DateTime.UnixEpoch.AddDays(1);

        var updated = await GraphCommandExtensions.UpdateAsync(
            Graph.Relationships<Knows>().Where(candidate => candidate.TestKey == relationship.TestKey),
            setters => setters.SetProperty(candidate => candidate.Since, replacement),
            TestContext.Current.CancellationToken);
        var stored = await Graph.FindRelationshipByTestKeyAsync<Knows>(
            relationship.TestKey,
            cancellationToken: TestContext.Current.CancellationToken);
        var deleted = await GraphCommandExtensions.DeleteAsync(
            Graph.Relationships<Knows>().Where(candidate => candidate.TestKey == relationship.TestKey),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, updated);
        Assert.Equal(replacement, stored.Since);
        Assert.Equal(1, deleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Graph.FindRelationshipByTestKeyAsync<Knows>(
            relationship.TestKey,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetBasedNodeDelete_PreflightsAtomicallyAndCascadeRemovesOnlyOwnedSubgraph()
    {
        var marker = $"command-delete-{Guid.NewGuid():N}";
        var complexMarker = $"owned-{Guid.NewGuid():N}";
        var first = new PersonWithComplexProperty
        {
            FirstName = "first",
            LastName = marker,
            Address = new AddressValue { Street = complexMarker, City = complexMarker },
        };
        var second = new PersonWithComplexProperty
        {
            FirstName = "second",
            LastName = marker,
            Address = new AddressValue { Street = complexMarker, City = complexMarker },
        };
        var survivor = new Person { FirstName = "survivor" };
        var relationship = new Knows();
        await Graph.CreateNodeAsync(first, cancellationToken: TestContext.Current.CancellationToken);
        await Graph.CreateNodeAsync(second, cancellationToken: TestContext.Current.CancellationToken);
        await Graph.CreateNodeAsync(survivor, cancellationToken: TestContext.Current.CancellationToken);
        await Graph.ConnectAsync(first, relationship, survivor, cancellationToken: TestContext.Current.CancellationToken);
        var targets = Graph.Nodes<PersonWithComplexProperty>().Where(person => person.LastName == marker);

        await Assert.ThrowsAsync<GraphException>(() => GraphCommandExtensions.DeleteAsync(
            targets,
            cascadeDelete: false,
            TestContext.Current.CancellationToken));

        Assert.Equal(2, await targets.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            2,
            await Harness.CountNodesByPropertyAsync(
                Graph,
                Labels.GetLabelFromType(typeof(AddressValue)),
                nameof(AddressValue.City),
                [complexMarker],
                TestContext.Current.CancellationToken));

        var affected = await GraphCommandExtensions.DeleteAsync(
            targets,
            cascadeDelete: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, affected);
        Assert.Equal(0, await targets.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            0,
            await Harness.CountNodesByPropertyAsync(
                Graph,
                Labels.GetLabelFromType(typeof(AddressValue)),
                nameof(AddressValue.City),
                [complexMarker],
                TestContext.Current.CancellationToken));
        _ = await Graph.FindNodeByTestKeyAsync<Person>(
            survivor.TestKey,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(await Graph.RelationshipsByTestKey<Knows>(relationship.TestKey)
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetBasedNodeDelete_PreflightsIncomingRelationshipsAndCascadeDetachesThem()
    {
        var marker = $"incoming-delete-{Guid.NewGuid():N}";
        var source = new Person { FirstName = "source", LastName = $"{marker}-source" };
        var target = new Person { FirstName = "target", LastName = $"{marker}-target" };
        var relationship = new Knows();
        await Graph.CreateNodeAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        await Graph.CreateNodeAsync(target, cancellationToken: TestContext.Current.CancellationToken);
        await Graph.ConnectAsync(source, relationship, target, cancellationToken: TestContext.Current.CancellationToken);
        var targets = Graph.Nodes<Person>().Where(person => person.LastName == target.LastName);

        await Assert.ThrowsAsync<GraphException>(() => GraphCommandExtensions.DeleteAsync(
            targets,
            cascadeDelete: false,
            TestContext.Current.CancellationToken));

        _ = await Graph.FindNodeByTestKeyAsync<Person>(
            target.TestKey,
            cancellationToken: TestContext.Current.CancellationToken);
        _ = await Graph.FindRelationshipByTestKeyAsync<Knows>(
            relationship.TestKey,
            cancellationToken: TestContext.Current.CancellationToken);

        var affected = await GraphCommandExtensions.DeleteAsync(
            targets,
            cascadeDelete: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, affected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Graph.FindNodeByTestKeyAsync<Person>(
            target.TestKey,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await Graph.RelationshipsByTestKey<Knows>(relationship.TestKey)
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken));
        _ = await Graph.FindNodeByTestKeyAsync<Person>(
            source.TestKey,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>Certifies atomic preflight and exact cascade counts for overlapping incident edges.</summary>
    [Fact]
    public async Task SetBasedNodeDelete_PreflightsSelfLoopsAndEdgesBetweenSelectedNodes()
    {
        var ct = TestContext.Current.CancellationToken;
        var marker = $"overlap-delete-{Guid.NewGuid():N}";
        var first = new Person { FirstName = "first", LastName = marker };
        var second = new Person { FirstName = "second", LastName = marker };
        var outside = new Person { FirstName = "outside" };
        foreach (var person in new[] { first, second, outside })
        {
            await Graph.CreateNodeAsync(person, null, ct);
        }

        var selfLoop = new Knows();
        var selectedEdge = new Knows();
        var incoming = new Knows();
        var outsideLoop = new Knows();
        await Graph.ConnectAsync(first, selfLoop, first, cancellationToken: ct);
        await Graph.ConnectAsync(first, selectedEdge, second, cancellationToken: ct);
        await Graph.ConnectAsync(outside, incoming, second, cancellationToken: ct);
        await Graph.ConnectAsync(outside, outsideLoop, outside, cancellationToken: ct);
        var targets = Graph.Nodes<Person>().Where(person => person.LastName == marker);

        await Assert.ThrowsAsync<GraphException>(() => GraphCommandExtensions.DeleteAsync(
            targets, cascadeDelete: false, ct));
        Assert.Equal(2, await targets.CountAsync(ct));
        foreach (var relationship in new[] { selfLoop, selectedEdge, incoming, outsideLoop })
        {
            _ = await Graph.FindRelationshipByTestKeyAsync<Knows>(relationship.TestKey, null, ct);
        }

        Assert.Equal(2, await GraphCommandExtensions.DeleteAsync(targets, cascadeDelete: true, ct));
        Assert.Equal(0, await targets.CountAsync(ct));
        foreach (var relationship in new[] { selfLoop, selectedEdge, incoming })
        {
            Assert.Null(await Graph.RelationshipsByTestKey<Knows>(relationship.TestKey).SingleOrDefaultAsync(ct));
        }

        _ = await Graph.FindNodeByTestKeyAsync<Person>(outside.TestKey, null, ct);
        _ = await Graph.FindRelationshipByTestKeyAsync<Knows>(outsideLoop.TestKey, null, ct);
    }

    [Fact]
    public async Task ExactOneEndpointSelection_ThrowsRoleSpecificCardinalityFailures()
    {
        var marker = $"command-exact-one-{Guid.NewGuid():N}";
        var missing = $"missing-{Guid.NewGuid():N}";
        await Graph.CreateNodeAsync(
            new Person { FirstName = "first", LastName = marker },
            cancellationToken: TestContext.Current.CancellationToken);
        await Graph.CreateNodeAsync(
            new Person { FirstName = "second", LastName = marker },
            cancellationToken: TestContext.Current.CancellationToken);
        var empty = Graph.Nodes<Person>().Where(person => person.LastName == missing);
        var multiple = Graph.Nodes<Person>().Where(person => person.LastName == marker);
        var provider = Assert.IsAssignableFrom<IGraphCommandProvider>(multiple.Provider);

        var emptyFailure = await Assert.ThrowsAsync<GraphCardinalityException>(() =>
            provider.InWriteTransactionAsync(
                (context, token) => GraphCommandSelection.SelectExactOneAsync(
                    context,
                    new GraphElementSelectionModel(
                        GraphQueryModelBuilder.Build(empty.Expression),
                        GraphElementSelectionMode.ExactOne),
                    empty.Expression,
                    GraphEndpointRole.Source,
                    token),
                TestContext.Current.CancellationToken));
        var multipleFailure = await Assert.ThrowsAsync<GraphCardinalityException>(() =>
            provider.InWriteTransactionAsync(
                (context, token) => GraphCommandSelection.SelectExactOneAsync(
                    context,
                    new GraphElementSelectionModel(
                        GraphQueryModelBuilder.Build(multiple.Expression),
                        GraphElementSelectionMode.ExactOne),
                    multiple.Expression,
                    GraphEndpointRole.Target,
                    token),
                TestContext.Current.CancellationToken));

        Assert.Equal(GraphEndpointRole.Source, emptyFailure.Role);
        Assert.Equal(GraphCardinalityFailure.Empty, emptyFailure.Failure);
        Assert.Equal(GraphEndpointRole.Target, multipleFailure.Role);
        Assert.Equal(GraphCardinalityFailure.Multiple, multipleFailure.Failure);
    }

    [Fact]
    public async Task SetBasedDynamicUpdate_DoesNotExposeProviderNativeIdentity()
    {
        var label = $"CommandDynamic{Guid.NewGuid():N}";
        var originalNames = new[] { "old" };
        var node = new DynamicNode(
            [label],
            new Dictionary<string, object?> { ["score"] = 1, ["names"] = originalNames });
        await Graph.CreateNodeAsync(node, cancellationToken: TestContext.Current.CancellationToken);
        var replacement = new[] { "first", "second" };

        var affected = await GraphCommandExtensions.UpdateAsync(
            Graph.DynamicNodes().OfLabel(label),
            setters => setters
                .SetProperty(candidate => candidate.Properties["score"], 2)
                .SetProperty(candidate => candidate.Properties["names"], replacement),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, affected);
        var updated = await Graph.DynamicNodes().OfLabel(label)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2L, Convert.ToInt64(updated.Properties["score"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(
            replacement,
            Assert.IsAssignableFrom<System.Collections.IEnumerable>(updated.Properties["names"])
                .Cast<object?>()
                .Cast<string>());
        Assert.DoesNotContain("__nativeId", updated.Properties.Keys);
    }
}
