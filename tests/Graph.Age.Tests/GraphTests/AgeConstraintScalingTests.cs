// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.CompatibilityTests;

/// <summary>
/// Bounds the selected-identity filter used by unique-constraint validation. The old literal OR
/// chain grew with the selection; the list-comprehension predicate is one parameter expression.
/// </summary>
public sealed class AgeConstraintScalingTests(AgeHarness harness)
    : AgeTest(harness, StoreIsolation.FreshStore)
{
    [Fact]
    public void SelectedIdentityExclusionPredicate_IsIndependentOfSelectionSize()
    {
        Assert.Contains("$" + CypherEngine.SelectedIdsParameterName, CypherEngine.SelectedIdentityExclusionPredicate, StringComparison.Ordinal);
        Assert.Contains("size([", CypherEngine.SelectedIdentityExclusionPredicate, StringComparison.Ordinal);
        Assert.DoesNotContain(" OR ", CypherEngine.SelectedIdentityExclusionPredicate, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"id\(candidate\) = \d", CypherEngine.SelectedIdentityExclusionPredicate);
        Assert.True(
            CypherEngine.SelectedIdentityExclusionPredicate.Length < 200,
            $"The exclusion predicate must stay bounded; got {CypherEngine.SelectedIdentityExclusionPredicate.Length} characters.");
    }

    [Fact]
    public async Task BulkUpdateWithUniqueConstraint_ValidatesMoreThanOneThousandSelectedNodes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const int nodeCount = 1_100;
        var marker = "bulk-constraint-" + Guid.NewGuid().ToString("N");

        for (var index = 0; index < nodeCount; index++)
        {
            await this.Graph.CreateNodeAsync(
                new IAttributeValidationTests.PersonWithUniqueProperties
                {
                    Email = $"{marker}-{index}@example.test",
                    SocialSecurityNumber = $"{marker}-ssn-{index}",
                    Department = marker,
                    FirstName = "Before",
                },
                cancellationToken: cancellationToken);
        }

        var affected = await GraphCommandExtensions.UpdateAsync(
            this.Graph.Nodes<IAttributeValidationTests.PersonWithUniqueProperties>()
                .Where(candidate => candidate.Department == marker),
            setters => setters.SetProperty(candidate => candidate.FirstName, "After"),
            cancellationToken);

        Assert.Equal(nodeCount, affected);
        Assert.Equal(
            nodeCount,
            await this.Graph.Nodes<IAttributeValidationTests.PersonWithUniqueProperties>()
                .Where(candidate => candidate.Department == marker && candidate.FirstName == "After")
                .CountAsync(cancellationToken));
    }
}
