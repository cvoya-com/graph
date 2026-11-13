// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using Cvoya.Graph.CompatibilityTests;

/// <summary>
/// Regression coverage for Apache AGE 1.8.0's <c>id(...) IN &lt;list&gt;</c> backend crash.
/// Each test drives the public API that used to emit that shape and then queries again so a
/// backend that died (and is recovering) fails the test instead of passing silently.
/// </summary>
public sealed class AgeListParameterRegressionTests(AgeHarness harness)
    : AgeTest(harness, StoreIsolation.FreshStore)
{
    [Fact]
    public async Task FullTextSearch_MultipleGraphIds_ReturnsAllMatchesAndLeavesBackendHealthy()
    {
        var ct = TestContext.Current.CancellationToken;
        const string token = "ListParameterSearchToken";
        var first = new IAttributeValidationTests.PersonWithFullTextSearchProperties
        {
            FirstName = token,
            LastName = "One",
            Email = "list-parameter-one@example.com",
        };
        var second = new IAttributeValidationTests.PersonWithFullTextSearchProperties
        {
            FirstName = token,
            LastName = "Two",
            Email = "list-parameter-two@example.com",
        };
        await this.Graph.CreateNodeAsync(first, null, ct);
        await this.Graph.CreateNodeAsync(second, null, ct);

        var results = await this.Graph.SearchNodes<IAttributeValidationTests.PersonWithFullTextSearchProperties>(token).ToListAsync(ct);

        Assert.Equal(2, results.Count);
        // The membership filter used id(alias) IN $graphIds before the fix; this count proves the
        // backend survived the query rather than restarting into recovery mode.
        Assert.Equal(
            2,
            await this.Graph.Nodes<IAttributeValidationTests.PersonWithFullTextSearchProperties>().CountAsync(ct));
    }

    [Fact]
    public async Task UpdatingComplexCollection_ParentIds_ReplacesItemsAndLeavesBackendHealthy()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = new ContractAddressCollectionOwner
        {
            Addresses =
            [
                new ContractAddressValue { Street = "Original 1", City = "Seattle" },
                new ContractAddressValue { Street = "Original 2", City = "Tacoma" },
            ],
        };
        await this.Graph.CreateNodeAsync(owner, null, ct);

        var replacement = new List<ContractAddressValue>
        {
            new() { Street = "Replacement 1", City = "Denver" },
        };
        var affected = await this.Graph.SelectNode(owner).UpdateAsync(
            setters => setters.SetProperty(candidate => candidate.Addresses, replacement),
            ct);

        Assert.Equal(1, affected);
        var fetched = await this.Graph.FindNodeAsync(owner, cancellationToken: ct);
        Assert.Equal(replacement, fetched.Addresses);
        // The complex-property metadata replacement used id(parent) IN $parentGraphIds.
        Assert.Equal(
            1,
            await this.Graph.Nodes<ContractAddressCollectionOwner>().CountAsync(ct));
    }

    [Fact]
    public async Task UpdatingUniqueNode_ExcludesSelectedIds_AndLeavesBackendHealthy()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = new AtomicMutationNode
        {
            KeyGroup = "list-parameter",
            KeyCode = "first",
            Email = "first@example.com",
            Marker = "before",
        };
        var second = new AtomicMutationNode
        {
            KeyGroup = "list-parameter",
            KeyCode = "second",
            Email = "second@example.com",
        };
        await this.Graph.CreateNodeAsync(first, null, ct);
        await this.Graph.CreateNodeAsync(second, null, ct);

        // Uniqueness revalidation excludes the selected node via id(candidate) IN $selectedIds
        // before the fix; a non-unique update must still succeed for the selected node.
        var affected = await SelectByKey("first").UpdateAsync(
            setters => setters.SetProperty(candidate => candidate.Marker, "after"),
            ct);

        Assert.Equal(1, affected);
        var fetched = await SelectByKey("first").SingleAsync(ct);
        Assert.Equal("after", fetched.Marker);

        // The exclusion must remain correct: moving onto the other row's unique email is rejected.
        var violation = await Assert.ThrowsAsync<GraphException>(() =>
            SelectByKey("second").UpdateAsync(
                setters => setters.SetProperty(candidate => candidate.Email, "first@example.com"),
                ct));
        Assert.Contains("unique", violation.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, await this.Graph.Nodes<AtomicMutationNode>().CountAsync(ct));

        IGraphQueryable<AtomicMutationNode> SelectByKey(string keyCode) =>
            this.Graph.Nodes<AtomicMutationNode>()
                .Where(candidate => candidate.KeyGroup == "list-parameter" && candidate.KeyCode == keyCode);
    }
}
