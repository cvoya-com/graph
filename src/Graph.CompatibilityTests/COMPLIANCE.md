# CVOYA graph compatibility compliance report

A provider is **compliant** when its compatibility run reports N passed cases / M skipped-with-a-declared-capability-reason / 0 failed and the strict `ComplianceGuard` confirms that every method identity required by the declared capabilities executed. Copy this template into your provider's repository/package and fill it in per release.

| | |
|---|---|
| Provider | \<Cvoya.Graph.YourProvider\> \<provider version\> |
| Compliance suite | Cvoya.Graph.CompatibilityTests \<suite version\> |
| Backing store | \<e.g. "Apache AGE 1.7 / PostgreSQL 16"\> |
| Date / CI run | \<date\> / \<link\> |

## Declared capabilities

| Capability | Declared | Note |
|---|---|---|
| FullTextSearch | Yes / No | |
| Transactions | Yes / No | |
| NestedTransactions | Yes / No | |
| ComplexPropertyCascade | Yes / No | |
| CallSubqueries | Yes / No | |
| PatternSizeProjection | Yes / No | |
| MultiLabelMatch | Yes / No | |
| LabelFiltering | Yes / No | |
| OrderByEntity | Yes / No | |
| ShortestPath | Yes / No | |
| OptionalTraversal | Yes / No | |
| GroupByAggregation | Yes / No | |
| RelationshipPredicates | Yes / No | |
| SetOperations | Yes / No | |
| NullElementsInSimpleCollections | Yes / No | |
| PropertyIndexes | Yes / No | Optional `IGraphIndexMaintenance`; no packaged TCK surface yet |
| PropertyAggregateIndexes | Yes / No | Optional `IGraphIndexMaintenance`; no packaged TCK surface yet |

## Results

| Executed cases | Passed cases | Required method identities covered | Skipped (capability) | Failed cases |
|---|---|---|---|---|
| N | N | \<value\> / \<value\> | M | 0 |

Required method identities for this capability set: `ComplianceInventory.MinimumExecuted(declared)` = \<value\>; covered = \<same value\>.

The executed/passed values are runtime test-case counts. A multi-row `[Theory]` contributes several
cases but only one method identity; the strict guard checks the identity inventory and lists any
missing declaring-interface-plus-signature identities.

Reproduce:

```bash
GRAPHMODEL_COMPLIANCE_STRICT=1 dotnet test <your-test-project> --report-trx
```

A failed run (any `Failed` count above 0, or the `ComplianceGuard` assembly fixture throwing) means the provider is not compliant with this suite version. A skip is only compliant when it carries a capability skip reason of the form `Capability '<Name>' not declared by provider '<ProviderName>' (Cvoya.Graph.CompatibilityTests <version>)` for a capability the table above marks "No" - any other skip or failure needs investigation.

The strict guard also rejects a provider binding that replaces a packaged default test body. Such
an override can execute assertions opposite to the inherited method name while recording the same
inventory identity. Legitimate divergence must be represented by a truthfully named packaged TCK
method and an explicit capability/expectation gate.

The repository's TCK meta-test project deliberately reports exactly two skips:
`CapabilitySkipTests.RequiresUndeclaredCapability_IsSkippedNotExecuted` and
`InterfaceLevelCapabilitySkipTests.InterfaceGatedTest_MustSkip`. They prove method- and
interface-level capability gating and are not provider certification results or inventory methods.
