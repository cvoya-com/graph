// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using Cvoya.Graph.CompatibilityTests;

/// <summary>Regression coverage for filters on absent simple properties.</summary>
public sealed class MissingPropertyFilterTests(AgeHarness harness) : AgeTest(harness)
{
    [Node("MissingPropertyPerson")]
    public sealed record MissingPropertyPerson : Node
    {
        public string Name { get; set; } = string.Empty;

        public string? Note { get; set; }
    }

    [Fact]
    public async Task MissingPropertyFilter_ReturnsOnlyAbsentValuesAndPreservesUnicode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var unicodeName = "Müller-😀-東京-<b>&\"quote\"";

        await Graph.CreateNodeAsync(
            new MissingPropertyPerson { Name = "without-note" },
            cancellationToken: cancellationToken);
        await Graph.CreateNodeAsync(
            new MissingPropertyPerson { Name = "with-note", Note = "present" },
            cancellationToken: cancellationToken);
        await Graph.CreateNodeAsync(
            new MissingPropertyPerson { Name = unicodeName },
            cancellationToken: cancellationToken);

        var missing = await Graph.Nodes<MissingPropertyPerson>()
            .Where(person => person.Note == null)
            .ToListAsync(cancellationToken);

        Assert.Equal(2, missing.Count);
        Assert.All(missing, person => Assert.Null(person.Note));
        Assert.Contains(missing, person => person.Name == unicodeName);

        var present = await Graph.Nodes<MissingPropertyPerson>()
            .Where(person => person.Note != null)
            .ToListAsync(cancellationToken);

        var single = Assert.Single(present);
        Assert.Equal("with-note", single.Name);
        Assert.Equal("present", single.Note);
    }
}
