// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.Cypher.Ast;

public sealed class AgeQueryRunnerTests
{
    [Theory]
    [InlineData(false, "_ag_label_vertex")]
    [InlineData(true, "_ag_label_edge")]
    public void BuildElementLockSql_LocksThePhysicalBaseTable(bool relationship, string expectedTable)
    {
        var sql = AgeQueryRunner.BuildElementLockSql("graph_name", relationship);

        Assert.Contains($"\"graph_name\".\"{expectedTable}\"", sql, StringComparison.Ordinal);
        Assert.Contains("id::text::bigint = ANY(@nativeIds)", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY id::text::bigint", sql, StringComparison.Ordinal);
        Assert.EndsWith("FOR UPDATE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCommandText_AppliesPostUnionOrderingAndPaging()
    {
        const string cypher =
            "MATCH (src:A) RETURN src AS Node, src.Name AS __age_union_sort0\n" +
            "UNION ALL\n" +
            "MATCH (src:B) RETURN src AS Node, src.Name AS __age_union_sort0";

        var postProcessing = new CypherPostProcessing(
            [new CypherPostSort(1, Descending: false)],
            Skip: 1_000,
            Limit: 100,
            ResultColumnCount: 1,
            Aggregation: null);

        var sql = AgeQueryRunner.BuildCommandText(
            "qa_r1",
            cypher,
            ["Node", "__age_union_sort0"],
            postProcessing);

        Assert.StartsWith("SELECT age_result.\"Node\" FROM ag_catalog.cypher('qa_r1'", sql, StringComparison.Ordinal);
        Assert.Contains("AS age_result (\"Node\" agtype, \"__age_union_sort0\" agtype)", sql, StringComparison.Ordinal);
        Assert.EndsWith(
            "ORDER BY age_result.\"__age_union_sort0\" ASC OFFSET 1000 LIMIT 100",
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT age_result.\"Node\", age_result.\"__age_union_sort0\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCommandText_CombinesPartialAggregates()
    {
        const string cypher =
            "MATCH (src:A) WITH src.Status AS k, count(src) AS c RETURN k AS Key, c AS C\n" +
            "UNION ALL\n" +
            "MATCH (src:B) WITH src.Status AS k, count(src) AS c RETURN k AS Key, c AS C";

        var postProcessing = new CypherPostProcessing(
            Sort: [],
            Skip: null,
            Limit: null,
            ResultColumnCount: 2,
            Aggregation: new CypherPostAggregation(
                [0],
                [new CypherPostAggregate(1, "sum")]));

        var sql = AgeQueryRunner.BuildCommandText("qa_r1", cypher, ["Key", "C"], postProcessing);

        Assert.Contains(
            "SELECT age_result.\"Key\", " +
            "sum((age_result.\"C\")::text::numeric)::text::ag_catalog.agtype AS \"C\" " +
            "FROM ag_catalog.cypher('qa_r1'",
            sql,
            StringComparison.Ordinal);
        Assert.EndsWith("GROUP BY age_result.\"Key\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCommandText_CombinesPartialAverageWithCount()
    {
        const string cypher =
            "MATCH (src:A) WITH src.Status AS k, sum(src.Amount) AS s, " +
            "count(src.Amount) AS c RETURN k AS Key, s AS Average, c AS c\n" +
            "UNION ALL\n" +
            "MATCH (src:B) WITH src.Status AS k, sum(src.Amount) AS s, " +
            "count(src.Amount) AS c RETURN k AS Key, s AS Average, c AS c";

        var postProcessing = new CypherPostProcessing(
            Sort: [],
            Skip: null,
            Limit: null,
            ResultColumnCount: 2,
            Aggregation: new CypherPostAggregation(
                [0],
                [new CypherPostAggregate(1, "avg", AuxiliaryColumnIndex: 2)]));

        var sql = AgeQueryRunner.BuildCommandText(
            "qa_r1",
            cypher,
            ["Key", "Average", "c"],
            postProcessing);

        Assert.Contains(
            "(sum((age_result.\"Average\")::text::numeric) / " +
            "NULLIF(sum((age_result.\"c\")::text::numeric), 0))::text::ag_catalog.agtype AS \"Average\"",
            sql,
            StringComparison.Ordinal);
        Assert.EndsWith("GROUP BY age_result.\"Key\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCommandText_CombinesMinMaxWithAgtypeComparison()
    {
        const string cypher =
            "MATCH (src:A) WITH src.Status AS k, min(src.Amount) AS n, max(src.Amount) AS x " +
            "RETURN k AS Key, n AS Minimum, x AS Maximum\n" +
            "UNION ALL\n" +
            "MATCH (src:B) WITH src.Status AS k, min(src.Amount) AS n, max(src.Amount) AS x " +
            "RETURN k AS Key, n AS Minimum, x AS Maximum";

        var postProcessing = new CypherPostProcessing(
            Sort: [],
            Skip: null,
            Limit: null,
            ResultColumnCount: 3,
            Aggregation: new CypherPostAggregation(
                [0],
                [
                    new CypherPostAggregate(1, "min"),
                    new CypherPostAggregate(2, "max"),
                ]));

        var sql = AgeQueryRunner.BuildCommandText(
            "qa_r1",
            cypher,
            ["Key", "Minimum", "Maximum"],
            postProcessing);

        Assert.Contains(
            "ag_catalog.age_min(age_result.\"Minimum\") AS \"Minimum\"",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ag_catalog.age_max(age_result.\"Maximum\") AS \"Maximum\"",
            sql,
            StringComparison.Ordinal);
        Assert.EndsWith("GROUP BY age_result.\"Key\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCommandText_DeduplicatesDistinctProjection()
    {
        var postProcessing = new CypherPostProcessing(
            Sort: [],
            Skip: 10,
            Limit: 5,
            ResultColumnCount: 1,
            Aggregation: null,
            Distinct: true);

        var sql = AgeQueryRunner.BuildCommandText(
            "qa_r1",
            "MATCH (src:A) RETURN src.Status AS Status UNION ALL MATCH (src:B) RETURN src.Status AS Status",
            ["Status"],
            postProcessing);

        Assert.StartsWith("SELECT DISTINCT age_result.\"Status\"", sql, StringComparison.Ordinal);
        Assert.EndsWith("OFFSET 10 LIMIT 5", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCommandText_LeavesPlainStatementsUnchanged()
    {
        var sql = AgeQueryRunner.BuildCommandText(
            "qa_r1",
            "MATCH (src:A) RETURN src AS Node",
            ["Node"],
            postProcessing: null);

        Assert.Equal(
            "SELECT age_result.\"Node\" FROM ag_catalog.cypher('qa_r1', " +
            "$cvoya_age_0$MATCH (src:A) RETURN src AS Node$cvoya_age_0$, @agtypeParams) " +
            "AS age_result (\"Node\" agtype)",
            sql);
    }

    [Fact]
    public void MaskQuotedContent_PreservesLengthAndMasksEscapedDelimiterContent()
    {
        const string cypher =
            "RETURN src.`net`` worth, (order by)`, 'literal\\' RETURN, (limit)' ORDER BY src.Id";

        var masked = AgeQueryRunner.MaskQuotedContent(cypher);

        Assert.Equal(cypher.Length, masked.Length);
        Assert.DoesNotContain("worth", masked, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("literal", masked, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" ORDER BY src.Id", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeProjectionAliases_IgnoresKeywordsAndPunctuationInsideStringLiterals()
    {
        const string cypher =
            "MATCH (src:Person) RETURN 'value, (order by) RETURN limit', src.Name LIMIT 1";

        var normalized = AgeQueryRunner.NormalizeProjectionAliases(
            cypher,
            ["value, (order by) RETURN limit", "Name"]);

        Assert.Equal(
            "MATCH (src:Person) RETURN 'value, (order by) RETURN limit' AS age_column_0, " +
            "src.Name LIMIT 1",
            normalized.Cypher);
        Assert.Equal(["age_column_0", "Name"], normalized.Columns);
    }

    [Fact]
    public void NormalizeProjectionAliases_IgnoresKeywordsAndPunctuationInsideEscapedIdentifiers()
    {
        const string cypher =
            "MATCH (src:Person) RETURN src.`net, worth (order by)`, src.Name ORDER BY src.`net, worth (order by)`";

        var normalized = AgeQueryRunner.NormalizeProjectionAliases(
            cypher,
            ["net, worth (order by)", "Name"]);

        Assert.Equal(
            "MATCH (src:Person) RETURN src.`net, worth (order by)` AS age_column_0, src.Name " +
            "ORDER BY src.`net, worth (order by)`",
            normalized.Cypher);
        Assert.Equal(["age_column_0", "Name"], normalized.Columns);
    }

    [Fact]
    public void NormalizeProjectionAliases_IgnoresDoubledBacktickAndLimitInsideEscapedIdentifier()
    {
        const string cypher = "MATCH (src:Person) RETURN src.`note`` LIMIT, (x)` LIMIT 1";

        var normalized = AgeQueryRunner.NormalizeProjectionAliases(
            cypher,
            ["note` LIMIT, (x)"]);

        Assert.Equal(
            "MATCH (src:Person) RETURN src.`note`` LIMIT, (x)` AS age_column_0 LIMIT 1",
            normalized.Cypher);
        Assert.Equal(["age_column_0"], normalized.Columns);
    }

    [Fact]
    public void InferProjectionColumns_UsesOriginalEscapedIdentifiersAtMaskedBoundaries()
    {
        const string cypher =
            "MATCH (src:Person) RETURN src.`net, worth (order by)`, src.`note`` value` AS `output alias`";

        var columns = AgeQueryRunner.InferProjectionColumns(cypher);

        Assert.Equal(["net, worth (order by)", "output alias"], columns);
    }

    [Fact]
    public async Task StreamingCursorFetchesOnDemandAndDisposesAbandonedSource()
    {
        var source = new RecordingAgeRecordSource(
            Record(("value", 1L)),
            Record(("value", 2L)));
        var cursor = new AgeResultCursor(source, distinct: false);
        using var cts = new CancellationTokenSource();

        Assert.Equal(0, source.ReadCount);
        Assert.True(await cursor.FetchAsync(cts.Token));
        Assert.Equal(1L, cursor.Current["value"]);
        Assert.Equal(1, source.ReadCount);
        Assert.Equal(cts.Token, source.LastCancellationToken);

        await cursor.DisposeAsync();

        Assert.Equal(1, source.DisposeCount);
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task StreamingCursorAppliesDistinctWithoutBufferingAllRecords()
    {
        var source = new RecordingAgeRecordSource(
            Record(("value", 1L)),
            Record(("value", 1L)),
            Record(("value", 2L)));
        await using var cursor = new AgeResultCursor(source, distinct: true);

        Assert.True(await cursor.FetchAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1L, cursor.Current["value"]);
        Assert.Equal(1, source.ReadCount);

        Assert.True(await cursor.FetchAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2L, cursor.Current["value"]);
        Assert.Equal(3, source.ReadCount);
    }

    [Fact]
    public async Task StreamingCursorRejectsPreCancelledEnumeration()
    {
        var source = new RecordingAgeRecordSource(Record(("value", 1L)));
        await using var cursor = new AgeResultCursor(source, distinct: false);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await cursor.FetchAsync(cts.Token));

        Assert.Equal(0, source.ReadCount);
    }

    private static AgeRecord Record(params (string Key, object? Value)[] values) =>
        new(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private sealed class RecordingAgeRecordSource(params AgeRecord[] records) : IAgeRecordSource
    {
        private int index = -1;

        public AgeRecord Current { get; private set; } = null!;

        public int ReadCount { get; private set; }

        public int DisposeCount { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCancellationToken = cancellationToken;
            ReadCount++;
            index++;
            if (index >= records.Length)
            {
                return ValueTask.FromResult(false);
            }

            Current = records[index];
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
