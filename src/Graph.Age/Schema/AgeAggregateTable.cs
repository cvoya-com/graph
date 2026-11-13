// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Schema;

/// <summary>
/// Describes the provider-owned per-label aggregate companion table that keeps
/// <c>count</c>/<c>sum</c>/<c>min</c>/<c>max</c> of one numeric property per group.
/// </summary>
/// <remarks>
/// The table is maintained by triggers on the per-row <see cref="AgeIndexTable"/>, which the
/// provider write path already synchronizes in the same transaction as the AGE label change. It
/// therefore never has to scan the (wide) AGE label table during a query.
/// <para>
/// A group key uses <see cref="AgePhysicalName.GroupKeySql"/>: the JSON encoding of the group value
/// for real strings and the JSON <c>null</c> literal for SQL NULL. PostgreSQL primary keys cannot
/// hold NULL, a missing group key is still a real group in Cypher, and because every real string is
/// JSON-quoted no valid string value can share the NULL key.
/// </para>
/// <para>
/// Every trigger first takes a transaction-scoped advisory lock per affected group key, in key
/// order, before it reads the per-row companion table. Under READ COMMITTED each statement of the
/// trigger sees a fresh snapshot, so a rebuild that waited for another writer's lock observes that
/// writer's committed rows when it computes the replacement aggregates; without the lock the
/// <c>EXCLUDED</c> assignments could overwrite a just-committed rebuild with a stale calculation.
/// </para>
/// </remarks>
internal static class AgeAggregateTable
{
    internal const string GroupKeyColumn = "group_key";
    internal const string GroupTextColumn = "group_text";
    internal const string CountColumn = "cnt";
    internal const string SumColumn = "sum_value";
    internal const string MinColumn = "min_value";
    internal const string MaxColumn = "max_value";

    /// <summary>Returns the aggregate companion table name for one declaration.</summary>
    internal static string TableName(AgeAggregateIndex aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return aggregate.TableName;
    }

    /// <summary>Returns the qualified aggregate companion table name.</summary>
    internal static string QualifiedTable(string graphNameOrToken, AgeAggregateIndex aggregate) =>
        $"{AgeIndexTable.QuoteIdentifier(graphNameOrToken, "graph name")}." +
        $"{AgeIndexTable.QuoteIdentifier(TableName(aggregate), "aggregate table name")}";

    /// <summary>Builds the idempotent aggregate table statement for one declaration.</summary>
    internal static string CreateTableSql(string graphName, AgeAggregateIndex aggregate)
    {
        var i = AgeIndexTable.QuoteIdentifier;
        return $"CREATE TABLE IF NOT EXISTS {QualifiedTable(graphName, aggregate)} (" +
            $"{i(GroupKeyColumn, "column name")} text PRIMARY KEY, " +
            $"{i(GroupTextColumn, "column name")} text, " +
            $"{i(CountColumn, "column name")} bigint NOT NULL DEFAULT 0, " +
            $"{i(SumColumn, "column name")} numeric NOT NULL DEFAULT 0, " +
            $"{i(MinColumn, "column name")} numeric, " +
            $"{i(MaxColumn, "column name")} numeric)";
    }

    /// <summary>Builds the idempotent aggregate table removal statement for one declaration.</summary>
    internal static string DropTableSql(string graphName, AgeAggregateIndex aggregate) =>
        $"DROP TABLE IF EXISTS {QualifiedTable(graphName, aggregate)} CASCADE";

    /// <summary>Builds the statement that clears the aggregate cache before a rebuild.</summary>
    internal static string ClearSql(string graphName, AgeAggregateIndex aggregate) =>
        $"DELETE FROM {QualifiedTable(graphName, aggregate)}";

    /// <summary>Builds the statement that rebuilds the aggregate table from the per-row companion table.</summary>
    internal static string PopulateSql(string graphName, AgeAggregateIndex aggregate)
    {
        var status = AgeIndexTable.QualifiedTable(graphName, aggregate.Label, aggregate.IndexTableName);
        var i = AgeIndexTable.QuoteIdentifier;
        var group = i(aggregate.GroupColumnName, "column name");
        var value = i(aggregate.ValueColumnName, "column name");
        var key = AgePhysicalName.GroupKeySql(group);
        return
            $"INSERT INTO {QualifiedTable(graphName, aggregate)} " +
            $"({i(GroupKeyColumn, "column name")}, {i(GroupTextColumn, "column name")}, " +
            $"{i(CountColumn, "column name")}, {i(SumColumn, "column name")}, " +
            $"{i(MinColumn, "column name")}, {i(MaxColumn, "column name")}) " +
            $"SELECT {key}, {group}, " +
            $"count({value})::bigint, " +
            $"COALESCE(sum({value}), 0)::numeric, " +
            $"min({value}), max({value}) " +
            $"FROM {status} " +
            $"GROUP BY {group} " +
            $"ON CONFLICT ({i(GroupKeyColumn, "column name")}) DO UPDATE SET " +
            $"{i(GroupTextColumn, "column name")} = EXCLUDED.{i(GroupTextColumn, "column name")}, " +
            $"{i(CountColumn, "column name")} = EXCLUDED.{i(CountColumn, "column name")}, " +
            $"{i(SumColumn, "column name")} = EXCLUDED.{i(SumColumn, "column name")}, " +
            $"{i(MinColumn, "column name")} = EXCLUDED.{i(MinColumn, "column name")}, " +
            $"{i(MaxColumn, "column name")} = EXCLUDED.{i(MaxColumn, "column name")}";
    }

    /// <summary>
    /// Builds the statement that takes the per-group advisory locks for every group present in the
    /// per-row companion table, in key order, before a maintenance rebuild reads them.
    /// </summary>
    internal static string LockGroupsSql(string graphName, AgeAggregateIndex aggregate)
    {
        var status = AgeIndexTable.QualifiedTable(graphName, aggregate.Label, aggregate.IndexTableName);
        var group = AgeIndexTable.QuoteIdentifier(aggregate.GroupColumnName, "column name");
        return
            $"SELECT {AdvisoryLockCall("k", graphName, aggregate)} FROM (" +
            $"SELECT DISTINCT {AgePhysicalName.GroupKeySql(group)} AS k FROM {status}" +
            $") AS affected_keys ORDER BY k";
    }

    /// <summary>Builds the row-level function that maintains the aggregate table on insert.</summary>
    internal static string CreateInsertFunctionSql(string graphName, AgeAggregateIndex aggregate)
    {
        var table = QualifiedTable(graphName, aggregate);
        var i = AgeIndexTable.QuoteIdentifier;
        var group = i(aggregate.GroupColumnName, "column name");
        var value = i(aggregate.ValueColumnName, "column name");
        return $"""
            CREATE OR REPLACE FUNCTION {FunctionName(graphName, aggregate, "ins")}() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                PERFORM {AdvisoryLockCall(AgePhysicalName.GroupKeySql($"NEW.{group}"), graphName, aggregate)};
                INSERT INTO {table} AS a
                    ({i(GroupKeyColumn, "column name")}, {i(GroupTextColumn, "column name")},
                     {i(CountColumn, "column name")}, {i(SumColumn, "column name")},
                     {i(MinColumn, "column name")}, {i(MaxColumn, "column name")})
                VALUES (
                    {AgePhysicalName.GroupKeySql($"NEW.{group}")},
                    NEW.{group},
                    CASE WHEN NEW.{value} IS NULL THEN 0 ELSE 1 END,
                    COALESCE(NEW.{value}, 0),
                    NEW.{value},
                    NEW.{value})
                ON CONFLICT ({i(GroupKeyColumn, "column name")}) DO UPDATE SET
                    {i(GroupTextColumn, "column name")} = EXCLUDED.{i(GroupTextColumn, "column name")},
                    {i(CountColumn, "column name")} = a.{i(CountColumn, "column name")} + EXCLUDED.{i(CountColumn, "column name")},
                    {i(SumColumn, "column name")} = a.{i(SumColumn, "column name")} + EXCLUDED.{i(SumColumn, "column name")},
                    {i(MinColumn, "column name")} = LEAST(a.{i(MinColumn, "column name")}, EXCLUDED.{i(MinColumn, "column name")}),
                    {i(MaxColumn, "column name")} = GREATEST(a.{i(MaxColumn, "column name")}, EXCLUDED.{i(MaxColumn, "column name")});
                RETURN NULL;
            END;
            $fn$
            """;
    }

    /// <summary>Builds the statement-level function that rebuilds affected groups after an update.</summary>
    internal static string CreateUpdateFunctionSql(string graphName, AgeAggregateIndex aggregate) =>
        CreateRebuildFunctionSql(graphName, aggregate, "upd", includeNewRows: true);

    /// <summary>Builds the statement-level function that rebuilds affected groups after a delete.</summary>
    internal static string CreateDeleteFunctionSql(string graphName, AgeAggregateIndex aggregate) =>
        CreateRebuildFunctionSql(graphName, aggregate, "del", includeNewRows: false);

    private static string CreateRebuildFunctionSql(
        string graphName,
        AgeAggregateIndex aggregate,
        string operation,
        bool includeNewRows)
    {
        var table = QualifiedTable(graphName, aggregate);
        var status = AgeIndexTable.QualifiedTable(graphName, aggregate.Label, aggregate.IndexTableName);
        var i = AgeIndexTable.QuoteIdentifier;
        var group = i(aggregate.GroupColumnName, "column name");
        var value = i(aggregate.ValueColumnName, "column name");
        var newKeys = includeNewRows
            ? $"""
              UNION
                      SELECT DISTINCT {AgePhysicalName.GroupKeySql(group)}
                      FROM new_rows
              """
            : string.Empty;
        var keys = $"""
            SELECT DISTINCT {AgePhysicalName.GroupKeySql(group)} AS k
                    FROM old_rows
                    {newKeys}
            """;
        // Taken before any input is read and in key order, so concurrent rebuilds of overlapping
        // groups serialize instead of deadlocking, and each later statement - which sees a fresh
        // READ COMMITTED snapshot - observes the rows of writers whose lock it waited for.
        var lockStatement = $"""
            PERFORM {AdvisoryLockCall("k", graphName, aggregate)}
            FROM (
                {keys}
            ) AS affected_keys(k)
            ORDER BY k;
            """;
        var present = $"""
            SELECT {AgePhysicalName.GroupKeySql($"s.{group}")} AS k,
                           s.{group} AS {i(GroupTextColumn, "column name")},
                           count(s.{value})::bigint AS cnt,
                           COALESCE(sum(s.{value}), 0)::numeric AS sum_value,
                           min(s.{value}) AS min_value,
                           max(s.{value}) AS max_value
                    FROM {status} AS s
                    JOIN keys ON keys.k = {AgePhysicalName.GroupKeySql($"s.{group}")}
                    GROUP BY s.{group}
            """;
        return $"""
            CREATE OR REPLACE FUNCTION {FunctionName(graphName, aggregate, operation)}() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                {lockStatement}
                WITH keys AS (
                    {keys}
                )
                DELETE FROM {table} AS a
                USING keys
                WHERE a.{i(GroupKeyColumn, "column name")} = keys.k
                  AND NOT EXISTS (
                      SELECT 1 FROM {status} AS s
                      WHERE {AgePhysicalName.GroupKeySql($"s.{group}")} = a.{i(GroupKeyColumn, "column name")});
                WITH keys AS (
                    {keys}
                ), present AS (
                    {present}
                )
                INSERT INTO {table}
                    ({i(GroupKeyColumn, "column name")}, {i(GroupTextColumn, "column name")},
                     {i(CountColumn, "column name")}, {i(SumColumn, "column name")},
                     {i(MinColumn, "column name")}, {i(MaxColumn, "column name")})
                SELECT k, {i(GroupTextColumn, "column name")}, cnt, sum_value, min_value, max_value FROM present
                ON CONFLICT ({i(GroupKeyColumn, "column name")}) DO UPDATE SET
                    {i(GroupTextColumn, "column name")} = EXCLUDED.{i(GroupTextColumn, "column name")},
                    {i(CountColumn, "column name")} = EXCLUDED.{i(CountColumn, "column name")},
                    {i(SumColumn, "column name")} = EXCLUDED.{i(SumColumn, "column name")},
                    {i(MinColumn, "column name")} = EXCLUDED.{i(MinColumn, "column name")},
                    {i(MaxColumn, "column name")} = EXCLUDED.{i(MaxColumn, "column name")};
                RETURN NULL;
            END;
            $fn$
            """;
    }

    /// <summary>Builds the trigger creation statements for one declaration.</summary>
    internal static IEnumerable<string> CreateTriggerSql(string graphName, AgeAggregateIndex aggregate)
    {
        var status = AgeIndexTable.QualifiedTable(graphName, aggregate.Label, aggregate.IndexTableName);
        var i = AgeIndexTable.QuoteIdentifier;
        yield return $"DROP TRIGGER IF EXISTS {i(TriggerName(aggregate, "ins"), "trigger name")} ON {status}";
        yield return $"CREATE TRIGGER {i(TriggerName(aggregate, "ins"), "trigger name")} " +
            $"AFTER INSERT ON {status} FOR EACH ROW EXECUTE FUNCTION {FunctionName(graphName, aggregate, "ins")}()";
        yield return $"DROP TRIGGER IF EXISTS {i(TriggerName(aggregate, "upd"), "trigger name")} ON {status}";
        yield return $"CREATE TRIGGER {i(TriggerName(aggregate, "upd"), "trigger name")} " +
            $"AFTER UPDATE ON {status} REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows " +
            $"FOR EACH STATEMENT EXECUTE FUNCTION {FunctionName(graphName, aggregate, "upd")}()";
        yield return $"DROP TRIGGER IF EXISTS {i(TriggerName(aggregate, "del"), "trigger name")} ON {status}";
        yield return $"CREATE TRIGGER {i(TriggerName(aggregate, "del"), "trigger name")} " +
            $"AFTER DELETE ON {status} REFERENCING OLD TABLE AS old_rows " +
            $"FOR EACH STATEMENT EXECUTE FUNCTION {FunctionName(graphName, aggregate, "del")}()";
    }

    /// <summary>Builds the statements that remove the trigger functions of one declaration.</summary>
    internal static IEnumerable<string> DropFunctionSql(string graphName, AgeAggregateIndex aggregate)
    {
        foreach (var operation in new[] { "ins", "upd", "del" })
        {
            yield return $"DROP FUNCTION IF EXISTS {FunctionName(graphName, aggregate, operation)}() CASCADE";
        }
    }

    /// <summary>
    /// Builds the call that takes the transaction-scoped advisory lock for one group key. The lock
    /// namespace is derived from the graph and aggregate table, so unrelated groups and graphs never
    /// share a lock; a hash collision can only add waiting, never merge two groups.
    /// </summary>
    private static string AdvisoryLockCall(string keyExpression, string graphName, AgeAggregateIndex aggregate)
    {
        var seed = AgeIndexTable.QuoteLiteral(
            graphName + "\u0001" + aggregate.TableName,
            "advisory lock namespace");
        return
            $"pg_advisory_xact_lock(hashtextextended({keyExpression}, hashtextextended({seed}, 0)))";
    }

    private static string FunctionName(string graphName, AgeAggregateIndex aggregate, string operation) =>
        $"{AgeIndexTable.QuoteIdentifier(graphName, "graph name")}." +
        $"{AgeIndexTable.QuoteIdentifier(FunctionBaseName(aggregate, operation), "function name")}";

    private static string FunctionBaseName(AgeAggregateIndex aggregate, string operation) =>
        AgePhysicalName.AggregateFunction(
            aggregate.Label,
            aggregate.GroupKeyProperty,
            aggregate.ValueProperty,
            operation);

    private static string TriggerName(AgeAggregateIndex aggregate, string operation) =>
        AgePhysicalName.AggregateTrigger(
            aggregate.Label,
            aggregate.GroupKeyProperty,
            aggregate.ValueProperty,
            operation);
}
