// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

global using Cvoya.Graph.Logging;

using Microsoft.Extensions.Logging;

namespace Cvoya.Graph.Logging;

internal static partial class LogMessages
{
    // LoggerExtensions emitted numeric event ID 0; preserve it during this mechanical migration.
    [LoggerMessage(EventId = 0, Level = LogLevel.Information, Message = "Graph initialized for AGE graph '{GraphName}'")]
    internal static partial void LogInformationAgeGraph49(this ILogger logger, global::System.String graphName);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Beginning new transaction")]
    internal static partial void LogDebugAgeGraph61(this ILogger logger);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Successfully began transaction")]
    internal static partial void LogDebugAgeGraph66(this ILogger logger);

    [LoggerMessage(EventId = 0, Level = LogLevel.Error, Message = "Failed to begin transaction")]
    internal static partial void LogErrorAgeGraph80(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Building nodes queryable for type {NodeType}")]
    internal static partial void LogDebugAgeGraph89(this ILogger logger, global::System.String nodeType);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Building relationships queryable for type {RelationshipType}")]
    internal static partial void LogDebugAgeGraph104(this ILogger logger, global::System.String relationshipType);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Creating node of type {NodeType}")]
    internal static partial void LogDebugAgeGraph160(this ILogger logger, global::System.String nodeType);

    [LoggerMessage(EventId = 0, Level = LogLevel.Error, Message = "Failed to create node of type {NodeType}")]
    internal static partial void LogErrorAgeGraph182(this ILogger logger, Exception exception, global::System.String nodeType);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Building dynamic nodes queryable")]
    internal static partial void LogDebugAgeGraph457(this ILogger logger);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Building dynamic relationships queryable")]
    internal static partial void LogDebugAgeGraph467(this ILogger logger);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Failed to roll back an uncommitted AGE transaction during disposal")]
    internal static partial void LogWarningAgeGraphTransaction70(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Failed to dispose an AGE transaction after a failed begin")]
    internal static partial void LogWarningAgeGraphTransaction127(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Failed to roll back cancelled transaction")]
    internal static partial void LogWarningTransactionHelpers54(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Failed to roll back cancelled transaction")]
    internal static partial void LogWarningTransactionHelpers58(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Failed to roll back cancelled transaction")]
    internal static partial void LogWarningTransactionHelpers62(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 0, Level = LogLevel.Error, Message = "{ErrorMessage}")]
    internal static partial void LogErrorTransactionHelpers70(this ILogger logger, Exception exception, global::System.String errorMessage);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Failed to roll back failed transaction")]
    internal static partial void LogWarningTransactionHelpers83(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Failed to roll back failed transaction")]
    internal static partial void LogWarningTransactionHelpers87(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Failed to roll back failed transaction")]
    internal static partial void LogWarningTransactionHelpers91(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Failed to dispose transaction after a failed operation")]
    internal static partial void LogWarningTransactionHelpers107(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Creating node of type {NodeType}")]
    internal static partial void LogDebugAgeNodeManager44(this ILogger logger, global::System.String nodeType);

    [LoggerMessage(EventId = 0, Level = LogLevel.Information, Message = "Created node of type {NodeType}")]
    internal static partial void LogInformationAgeNodeManager73(this ILogger logger, global::System.String nodeType);

    [LoggerMessage(EventId = 0, Level = LogLevel.Error, Message = "Error creating node of type {NodeType}")]
    internal static partial void LogErrorAgeNodeManager79(this ILogger logger, Exception exception, global::System.String nodeType);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Skipping null complex property {PropertyName}")]
    internal static partial void LogDebugComplexPropertyManager88(this ILogger logger, global::System.String propertyName);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Unsupported complex property type: {PropertyType} for property {PropertyName}")]
    internal static partial void LogWarningComplexPropertyManager92(this ILogger logger, global::System.String propertyType, global::System.String propertyName);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Created {Count} complex property node(s) across {RelationshipTypeCount} semantic relationship type(s)")]
    internal static partial void LogDebugComplexPropertyManager202(this ILogger logger, global::System.Int32 count, global::System.Int32 relationshipTypeCount);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Deleted {DeletedCount} complex property relationships for parent {ParentId}")]
    internal static partial void LogDebugComplexPropertyManager270(this ILogger logger, global::System.Int32 deletedCount, global::System.String parentId);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "AGE query returned {RecordCount} records")]
    internal static partial void LogDebugAgeQueryRunner87(this ILogger logger, global::System.Int32 recordCount);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Streaming AGE Cypher query with {ParameterCount} parameters and {ColumnCount} projected columns: {Query}")]
    internal static partial void LogDebugAgeQueryRunner159(this ILogger logger, global::System.Int32 parameterCount, global::System.Int32 columnCount, global::System.String query);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Executing AGE Cypher query with {ParameterCount} parameters and {ColumnCount} projected columns: {Query}")]
    internal static partial void LogDebugAgeQueryRunner167(this ILogger logger, global::System.Int32 parameterCount, global::System.Int32 columnCount, global::System.String query);

    [LoggerMessage(EventId = 0, Level = LogLevel.Error, Message = "AGE query execution failed; correlation ID {CorrelationId}")]
    internal static partial void LogErrorAgeQueryRunner192(this ILogger logger, Exception exception, global::System.String correlationId);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Executing query for type {Type}")]
    internal static partial void LogDebugCypherEngine60(this ILogger logger, global::System.String type);

    [LoggerMessage(EventId = 0, Level = LogLevel.Error, Message = "Failed to execute query for type {Type}")]
    internal static partial void LogErrorCypherEngine91(this ILogger logger, Exception exception, global::System.String type);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Streaming query for type {Type}")]
    internal static partial void LogDebugCypherEngine103(this ILogger logger, global::System.String type);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Generated Cypher: {Cypher}")]
    internal static partial void LogDebugCypherEngine274(this ILogger logger, global::System.String cypher);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Generated Cypher parameter names: {ParameterNames}; count: {ParameterCount}")]
    internal static partial void LogDebugCypherEngine275(this ILogger logger, global::System.String[] parameterNames, global::System.Int32 parameterCount);

    [LoggerMessage(EventId = 0, Level = LogLevel.Trace, Message = "Generated Cypher parameters: {Parameters}")]
    internal static partial void LogTraceCypherEngine283(this ILogger logger, global::System.Collections.Generic.IReadOnlyDictionary<global::System.String, global::System.Object?> parameters);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "AGE query returned {Count} records")]
    internal static partial void LogDebugCypherExecutor35(this ILogger logger, global::System.Int32 count);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Planning graph query rooted at {RootType}")]
    internal static partial void LogDebugCypherQueryVisitor36(this ILogger logger, global::System.Type rootType);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Added Cypher parameter names: {ParameterNames}; count: {ParameterCount}")]
    internal static partial void LogDebugCypherQueryVisitor53(this ILogger logger, global::System.String[] parameterNames, global::System.Int32 parameterCount);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Streaming async query for result type: {ResultType}")]
    internal static partial void LogDebugGraphQueryProvider71(this ILogger logger, global::System.String resultType);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Executing async query for result type: {ResultType}")]
    internal static partial void LogDebugGraphQueryProvider75(this ILogger logger, global::System.String resultType);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Expression type: {ExpressionType}")]
    internal static partial void LogDebugGraphQueryProvider77(this ILogger logger, global::System.String expressionType);

    [LoggerMessage(EventId = 0, Level = LogLevel.Error, Message = "Error executing query")]
    internal static partial void LogErrorGraphQueryProvider86(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Failed to roll back streaming query transaction during cleanup")]
    internal static partial void LogWarningGraphQueryProvider89(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Failed to dispose streaming query enumerator during cleanup")]
    internal static partial void LogWarningGraphQueryProviderEnumeratorDisposalFailure(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Failed to dispose streaming query transaction during cleanup")]
    internal static partial void LogWarningGraphQueryProviderTransactionDisposalFailure(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "{Indent}Method: {Method} from {DeclaringType}")]
    internal static partial void LogDebugGraphQueryProvider97(this ILogger logger, global::System.String indent, global::System.String method, global::System.String? declaringType);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "{Indent}Constant: {Type}")]
    internal static partial void LogDebugGraphQueryProvider110(this ILogger logger, global::System.String indent, global::System.String type);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Initialized AGE schema metadata")]
    internal static partial void LogDebugAgeSchemaManager49(this ILogger logger);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Physical node label '{PhysicalLabel}' stores rows that declare the registered hierarchy label '{RegisteredAncestor}', but the physical label is not registered. Base-label queries omit those rows; call AgeGraphStore.RegisterLabelHierarchy('{RegisteredAncestor}', '{PhysicalLabel}') before querying.")]
    internal static partial void LogWarningUnregisteredLabelHierarchy(this ILogger logger, global::System.String physicalLabel, global::System.String registeredAncestor);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Runtime label-hierarchy validation could not inspect every physical label; registration gaps may be unreported")]
    internal static partial void LogWarningLabelHierarchyValidationFailed(this ILogger logger, Exception exception);

}
