// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;

namespace Cvoya.Graph.Serialization.Results;

internal static class GraphValueConverter
{
    internal const string MetadataPropertyName = "__metadata__";
    private const string TypeNameKey = "type";

    // Successful metadata resolutions are stable for the life of the process: a Type that
    // Type.GetType already resolved cannot become a different Type after another assembly loads.
    // Cache only successes so an unresolved name can still resolve after a later assembly load.
    private static readonly ConcurrentDictionary<string, Type> ResolvedMetadataTypes = new(StringComparer.Ordinal);

    public static object? ConvertTo(object? value, Type targetType)
    {
        return ConvertTo(value, targetType, context: null);
    }

    public static object? ConvertTo(
        object? value,
        Type targetType,
        string propertyName,
        bool isElementNullable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        return ConvertTo(
            value,
            targetType,
            new ConversionContext(
                Subject: $"Collection property '{propertyName}'",
                ParameterName: null,
                Declaration: null,
                ElementNullableOverride: isElementNullable,
                UnknownIsNullable: false));
    }

    public static object? ConvertTo(object? value, Type targetType, ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var parameterName = parameter.Name ?? $"param{parameter.Position}";
        return ConvertTo(
            value,
            targetType,
            new ConversionContext(
                Subject: $"Constructor parameter '{parameterName}'",
                ParameterName: parameterName,
                Declaration: NullabilityDerivation.ForParameter(parameter),
                ElementNullableOverride: null,
                UnknownIsNullable: true));
    }

    private static object? ConvertTo(
        object? value,
        Type targetType,
        ConversionContext? context)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        if (value is null)
        {
            if (context?.Declaration is { } declaration &&
                !declaration.AllowsNull(context.UnknownIsNullable))
            {
                throw CreateNullValueException(targetType, context.ParameterName);
            }

            return targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null
                ? Activator.CreateInstance(targetType)
                : null;
        }

        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlyingType.IsInstanceOfType(value))
        {
            ValidateNullElements(value, underlyingType, context);
            return value;
        }

        if (underlyingType == typeof(string))
            return value.ToString();
        if (underlyingType == typeof(char))
            return System.Convert.ToChar(value, CultureInfo.InvariantCulture);
        if (underlyingType.IsEnum)
        {
            return value is string text
                ? Enum.Parse(underlyingType, text, ignoreCase: true)
                : Enum.ToObject(underlyingType, value);
        }

        if (underlyingType == typeof(DateTime))
        {
            return value switch
            {
                DateTimeOffset offset => offset.UtcDateTime,
                DateOnly date => date.ToDateTime(TimeOnly.MinValue),
                _ => System.Convert.ToDateTime(value, CultureInfo.InvariantCulture),
            };
        }

        if (underlyingType == typeof(DateTimeOffset))
        {
            return value switch
            {
                DateTimeOffset offset => offset,
                DateTime dateTime => new DateTimeOffset(dateTime),
                _ => DateTimeOffset.Parse(value.ToString()!, CultureInfo.InvariantCulture),
            };
        }

        if (underlyingType == typeof(DateOnly))
        {
            return value is DateTime dateTime
                ? DateOnly.FromDateTime(dateTime)
                : DateOnly.Parse(value.ToString()!, CultureInfo.InvariantCulture);
        }

        if (underlyingType == typeof(TimeOnly))
        {
            return value switch
            {
                DateTimeOffset offset => TimeOnly.FromTimeSpan(offset.TimeOfDay),
                DateTime dateTime => TimeOnly.FromDateTime(dateTime),
                TimeSpan timeSpan => TimeOnly.FromTimeSpan(timeSpan),
                long ticks => new TimeOnly(ticks),
                _ => TimeOnly.Parse(value.ToString()!, CultureInfo.InvariantCulture),
            };
        }

        if (underlyingType == typeof(TimeSpan))
        {
            return value switch
            {
                double milliseconds => TimeSpan.FromMilliseconds(milliseconds),
                long milliseconds => TimeSpan.FromMilliseconds(milliseconds),
                _ => TimeSpan.Parse(value.ToString()!, CultureInfo.InvariantCulture),
            };
        }

        if (underlyingType == typeof(Guid))
            return Guid.Parse(value.ToString()!);
        if (underlyingType == typeof(Uri))
            return new Uri(value.ToString()!);
        if (underlyingType == typeof(byte[]) && value is string base64)
            return System.Convert.FromBase64String(base64);

        if (TryConvertCollection(
            value,
            underlyingType,
            context,
            out var collection))
        {
            return collection;
        }

        return System.Convert.ChangeType(value, underlyingType, CultureInfo.InvariantCulture);
    }

    public static Type? GetTypeFromMetadata(IReadOnlyDictionary<string, object>? properties)
    {
        if (properties is null || !properties.TryGetValue(MetadataPropertyName, out var metadata))
        {
            return null;
        }

        // The provider-neutral wire convention is { "type": "assembly-qualified name" }.
        // Backends such as Neo4j cannot persist map-valued entity properties, so providers may
        // store the assembly-qualified name as a scalar under the same reserved property instead.
        var typeName = metadata switch
        {
            string scalar => scalar,
            IReadOnlyDictionary<string, object> map when map.TryGetValue(TypeNameKey, out var value) => value as string,
            _ => null,
        };

        return ResolveTypeName(typeName);
    }

    /// <summary>
    /// Resolves the metadata type directly from the wire entries so materialization does not have
    /// to convert the whole property bag (including every scalar and collection) just to read it.
    /// </summary>
    public static Type? GetTypeFromMetadata(IReadOnlyDictionary<string, GraphValue>? entries)
    {
        if (entries is null || !entries.TryGetValue(MetadataPropertyName, out var metadata))
        {
            return null;
        }

        var typeName = metadata.Kind switch
        {
            GraphValueKind.Scalar => metadata.ScalarValue as string,
            GraphValueKind.Map when metadata.Entries.TryGetValue(TypeNameKey, out var value)
                && value.Kind == GraphValueKind.Scalar
                => value.ScalarValue as string,
            _ => null,
        };

        return ResolveTypeName(typeName);
    }

    private static Type? ResolveTypeName(string? typeName)
    {
        if (typeName is null)
        {
            return null;
        }

        if (ResolvedMetadataTypes.TryGetValue(typeName, out var cached))
        {
            return cached;
        }

        var resolved = Type.GetType(typeName);
        if (resolved != null && !resolved.Assembly.IsCollectible)
        {
            // A collectible type must not be retained by a process-wide cache; it would keep its
            // AssemblyLoadContext alive and defeat runtime unload.
            ResolvedMetadataTypes.TryAdd(typeName, resolved);
        }

        return resolved;
    }

    private static bool TryConvertCollection(
        object value,
        Type targetType,
        ConversionContext? context,
        out object? result)
    {
        result = null;
        if (value is not IEnumerable source || value is string)
        {
            return false;
        }

        var elementType = GraphResultTypeHelpers.GetCollectionElementType(targetType);
        if (elementType is null)
        {
            return false;
        }

        var elementContext = context?.ForElement(elementType);
        var values = new List<object?>();
        var index = 0;
        foreach (var item in source)
        {
            if (item is null && context?.AllowsElementNull(elementType, elementContext) == false)
            {
                throw CreateNullElementException(context.Subject, elementType, index);
            }

            values.Add(ConvertTo(item, elementType, elementContext));
            index++;
        }

        if (targetType.IsArray)
        {
            var array = Array.CreateInstance(elementType, values.Count);
            for (index = 0; index < values.Count; index++)
            {
                array.SetValue(values[index], index);
            }

            result = array;
            return true;
        }

        var listType = typeof(List<>).MakeGenericType(elementType);
        var list = (IList)Activator.CreateInstance(listType)!;
        foreach (var item in values)
        {
            list.Add(item);
        }

        if (!targetType.IsAssignableFrom(listType))
        {
            return false;
        }

        result = list;
        return true;
    }

    private static void ValidateNullElements(
        object value,
        Type targetType,
        ConversionContext? context)
    {
        if (context is null ||
            value is not IEnumerable source ||
            value is string)
        {
            return;
        }

        var elementType = GraphResultTypeHelpers.GetCollectionElementType(targetType);
        if (elementType is null)
        {
            return;
        }

        // A CLR collection of a non-nullable value type cannot contain null, so scanning it
        // (boxing every element — e.g. each byte of a byte[] blob property) proves nothing.
        var sourceElementType = GraphResultTypeHelpers.GetCollectionElementType(value.GetType());
        if (sourceElementType is not null &&
            sourceElementType.IsValueType &&
            Nullable.GetUnderlyingType(sourceElementType) is null)
        {
            return;
        }

        var elementContext = context.ForElement(elementType);
        var index = 0;
        foreach (var item in source)
        {
            if (item is null && context.AllowsElementNull(elementType, elementContext) == false)
            {
                throw CreateNullElementException(context.Subject, elementType, index);
            }

            if (item is not null && GraphResultTypeHelpers.GetCollectionElementType(elementType) is not null)
            {
                ValidateNullElements(item, elementType, elementContext);
            }

            index++;
        }
    }

    internal static GraphException CreateNullValueException(Type targetType, string? parameterName) =>
        new(parameterName is null
            ? $"Cannot materialize null into non-nullable type '{targetType.FullName}'."
            : $"Cannot materialize null into non-nullable type '{targetType.FullName}' for '{parameterName}'.");

    internal static GraphException CreateNullElementException(
        string subject,
        Type elementType,
        int index)
    {
        return new GraphException(
            $"{subject} contains a null element at index {index}, " +
            $"but its target element type '{elementType}' is non-nullable.");
    }

    internal static GraphException CreateInvalidComplexCollectionElementException(
        string propertyLabel,
        Type elementType,
        int index,
        Type? actualType)
    {
        return actualType is null
            ? new GraphException(
                $"Complex collection property '{propertyLabel}' contains a null element at index {index}, " +
                $"but its target element type '{elementType}' does not allow null elements.")
            : new GraphException(
                $"Complex collection property '{propertyLabel}' contains an element of type '{actualType}' at index {index}, " +
                $"which is not assignable to its target element type '{elementType}'.");
    }

    private sealed record ConversionContext(
        string Subject,
        string? ParameterName,
        NullabilityDeclaration? Declaration,
        bool? ElementNullableOverride,
        bool UnknownIsNullable)
    {
        public ConversionContext ForElement(Type elementType) => new(
            Subject,
            ParameterName,
            Declaration?.GetElement(elementType),
            ElementNullableOverride: null,
            UnknownIsNullable: false);

        public bool? AllowsElementNull(Type elementType, ConversionContext? elementContext)
        {
            if (ElementNullableOverride is { } elementNullable)
            {
                return elementNullable;
            }

            if (elementContext?.Declaration is { } declaration)
            {
                return declaration.AllowsNull(unknownIsNullable: false);
            }

            if (ParameterName is null)
            {
                return null;
            }

            if (Nullable.GetUnderlyingType(elementType) is not null)
            {
                return true;
            }

            return false;
        }
    }
}
