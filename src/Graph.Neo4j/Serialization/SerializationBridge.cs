// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Neo4j.Serialization;

using System.Collections;
using System.Globalization;
using Cvoya.Graph.Serialization.Results;
using global::Neo4j.Driver;


/// <summary>
/// Provides methods for converting between Neo4j values and .NET types.
/// </summary>
internal static class SerializationBridge
{
    internal const string MetadataPropertyName = "__metadata__";
    private const string TypeNameKey = "type";

    /// <summary>
    /// Converts a .NET value to a Neo4j-compatible value.
    /// </summary>
    public static object? ToNeo4jValue(object? value)
    {
        return value switch
        {
            null => null,

            // Neo4j native types - pass through
            INode or IRelationship or IPath => value,

            // Primitives and strings
            string or bool or char => value,
            sbyte or byte or short or ushort or int or uint or long or ulong => value,
            double => value,
            decimal or float => Convert.ToDouble(value, CultureInfo.InvariantCulture),

            // Convert enums to their string representation
            Enum enumValue => enumValue.ToString(),

            // Cvoya.Graph types
            Graph.Point location => new global::Neo4j.Driver.Point(
                srId: 4979, // WGS84 for lat/lon
                x: location.Longitude,
                y: location.Latitude,
                z: location.Height
            ),

            // Temporal types
            DateTime dt => dt.ToUniversalTime(),
            DateTimeOffset dto => dto.UtcDateTime,
            DateOnly date => date.ToDateTime(TimeOnly.MinValue),
            // Stored as raw ticks (not TotalMilliseconds) to preserve full 100ns
            // precision; TimeOnly.FromDateTime(DateTime.UtcNow) routinely carries
            // sub-millisecond ticks that a millisecond-granularity round trip would
            // silently truncate.
            TimeOnly time => time.Ticks,
            TimeSpan ts => ts.TotalMilliseconds,

            // Convert Guid to string
            Guid guid => guid.ToString(),

            // Convert Uri to string
            Uri uri => uri.ToString(),

            // Byte arrays stay as-is (Neo4j supports them)
            byte[] => value,

            // Handle collections
            IDictionary dict => ConvertDictionary(dict),
            IEnumerable enumerable => ConvertCollection(enumerable),

            // For any other type, convert to string
            _ => value.ToString()
        };
    }

    /// <summary>
    /// Converts a Neo4j value to a .NET type.
    /// </summary>
    public static object? FromNeo4jValue(object? value, Type targetType)
    {
        if (value == null)
            return targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null
                ? Activator.CreateInstance(targetType)
                : null;

        // Handle nullable types
        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        // Direct assignment if types match
        if (underlyingType.IsAssignableFrom(value.GetType()))
            return value;

        // Handle Neo4j types
        if (value is INode node && typeof(INode).IsAssignableFrom(underlyingType))
            return node;

        if (value is IRelationship rel && typeof(IRelationship).IsAssignableFrom(underlyingType))
            return rel;

        // Handle conversions
        return underlyingType switch
        {
            _ when underlyingType == typeof(string) => value.ToString(),
            _ when underlyingType == typeof(char) => Convert.ToChar(value, CultureInfo.InvariantCulture),
            _ when underlyingType.IsEnum => ConvertToEnum(value, underlyingType),

            // Cvoya.Graph types
            _ when underlyingType == typeof(Graph.Point) => ConvertToPoint(value),

            // Numeric conversions
            _ when underlyingType == typeof(sbyte) => Convert.ToSByte(value, CultureInfo.InvariantCulture),
            _ when underlyingType == typeof(byte) => Convert.ToByte(value, CultureInfo.InvariantCulture),
            _ when underlyingType == typeof(short) => Convert.ToInt16(value, CultureInfo.InvariantCulture),
            _ when underlyingType == typeof(ushort) => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
            _ when underlyingType == typeof(int) => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            _ when underlyingType == typeof(uint) => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
            _ when underlyingType == typeof(long) => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            _ when underlyingType == typeof(ulong) => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
            _ when underlyingType == typeof(float) => Convert.ToSingle(value, CultureInfo.InvariantCulture),
            _ when underlyingType == typeof(double) => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            _ when underlyingType == typeof(decimal) => GraphValueConverter.ConvertToDecimal(value),

            // Temporal conversions
            _ when underlyingType == typeof(DateTime) => ConvertToDateTime(value),
            _ when underlyingType == typeof(DateTimeOffset) => ConvertToDateTimeOffset(value),
            _ when underlyingType == typeof(DateOnly) => DateOnly.FromDateTime(ConvertToDateTime(value)),
            _ when underlyingType == typeof(TimeOnly) => new TimeOnly(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            _ when underlyingType == typeof(TimeSpan) => ConvertToTimeSpan(value),

            // Other conversions
            _ when underlyingType == typeof(Guid) => Guid.Parse(value.ToString()!),
            _ when underlyingType == typeof(Uri) => new Uri(value.ToString()!),
            _ when underlyingType == typeof(byte[]) && value is byte[] => value,

            // Collections
            _ when IsDictionaryType(underlyingType) => ConvertToDictionary(value, underlyingType),
            _ when IsCollectionType(underlyingType) => ConvertToCollection(value, underlyingType),

            // Fallback
            _ => Convert.ChangeType(value, underlyingType, CultureInfo.InvariantCulture)
        };
    }

    private static object ConvertToEnum(object value, Type enumType)
    {
        return value switch
        {
            string str => Enum.Parse(enumType, str, ignoreCase: true),
            _ when IsNumericType(value.GetType()) => Enum.ToObject(enumType, value),
            _ => throw new ArgumentException($"Cannot convert {value.GetType()} to enum {enumType}")
        };
    }

    private static Graph.Point ConvertToPoint(object value)
    {
        return value switch
        {
            global::Neo4j.Driver.Point neo4jPoint => new Graph.Point { Longitude = neo4jPoint.X, Latitude = neo4jPoint.Y, Height = neo4jPoint.Z }, // Note: Neo4j uses X=lon, Y=lat, Z=height
            _ => throw new ArgumentException($"Cannot convert {value.GetType()} to Location")
        };
    }

    private static bool IsNumericType(Type type)
    {
        return type == typeof(sbyte) || type == typeof(byte) ||
               type == typeof(short) || type == typeof(ushort) ||
               type == typeof(int) || type == typeof(uint) ||
               type == typeof(long) || type == typeof(ulong) ||
               type == typeof(float) || type == typeof(double) ||
               type == typeof(decimal);
    }

    /// <summary>
    /// Gets the version-independent, assembly-qualified scalar name used to persist a concrete CLR
    /// type in Neo4j.
    /// </summary>
    public static string GetAssemblyQualifiedTypeName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var assemblyQualifiedName = type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
        return RemoveAssemblyVersions(assemblyQualifiedName);
    }

    /// <summary>
    /// Creates metadata dictionary for a node or relationship.
    /// </summary>
    public static Dictionary<string, object> CreateMetadata(Type type)
    {
        return new Dictionary<string, object>
        {
            [MetadataPropertyName] = new Dictionary<string, object>
            {
                [TypeNameKey] = GetAssemblyQualifiedTypeName(type)
            }
        };
    }

    /// <summary>
    /// Extracts type information from metadata if present.
    /// </summary>
    public static Type? GetTypeFromMetadata(IReadOnlyDictionary<string, object>? properties)
    {
        if (properties == null)
            return null;

        if (!properties.TryGetValue(MetadataPropertyName, out var metadata))
            return null;

        var typeName = metadata switch
        {
            string scalar => scalar,
            IReadOnlyDictionary<string, object> metaDict
                when metaDict.TryGetValue(TypeNameKey, out var value) => value as string,
            _ => null
        };

        return typeName is null ? null : Type.GetType(typeName);
    }

    private static string RemoveAssemblyVersions(string assemblyQualifiedName)
    {
        const string marker = ", Version=";
        var versionStart = assemblyQualifiedName.IndexOf(marker, StringComparison.Ordinal);
        if (versionStart < 0)
        {
            return assemblyQualifiedName;
        }

        var result = new System.Text.StringBuilder(assemblyQualifiedName.Length);
        var copyStart = 0;
        while (versionStart >= 0)
        {
            result.Append(assemblyQualifiedName, copyStart, versionStart - copyStart);
            var versionEnd = assemblyQualifiedName.IndexOfAny([',', ']'], versionStart + marker.Length);
            if (versionEnd < 0)
            {
                return result.ToString();
            }

            copyStart = versionEnd;
            versionStart = assemblyQualifiedName.IndexOf(marker, copyStart, StringComparison.Ordinal);
        }

        result.Append(assemblyQualifiedName, copyStart, assemblyQualifiedName.Length - copyStart);
        return result.ToString();
    }

    private static DateTime ConvertToDateTime(object value)
    {
        return value switch
        {
            DateTime dt => dt,
            DateTimeOffset dto => dto.UtcDateTime,
            ZonedDateTime zdt => ConvertZonedDateTimeToDateTime(zdt),
            LocalDateTime ldt => ldt.ToDateTime(),
            string s => DateTime.Parse(s, CultureInfo.InvariantCulture),
            _ => Convert.ToDateTime(value, CultureInfo.InvariantCulture)
        };
    }

    private static DateTimeOffset ConvertToDateTimeOffset(object value)
    {
        return value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(dt),
            ZonedDateTime zdt => ConvertZonedDateTimeToDateTimeOffset(zdt),
            string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture),
            _ => new DateTimeOffset(Convert.ToDateTime(value, CultureInfo.InvariantCulture))
        };
    }

    private static TimeSpan ConvertToTimeSpan(object value)
    {
        return value switch
        {
            TimeSpan ts => ts,
            double ms => TimeSpan.FromMilliseconds(ms),
            long ms => TimeSpan.FromMilliseconds(ms),
            string s => TimeSpan.Parse(s, CultureInfo.InvariantCulture),
            _ => TimeSpan.FromMilliseconds(Convert.ToDouble(value, CultureInfo.InvariantCulture))
        };
    }

    private static DateTime ConvertZonedDateTimeToDateTime(ZonedDateTime zdt)
    {
        try
        {
            return zdt.ToDateTimeOffset().UtcDateTime;
        }
        catch (ValueTruncationException)
        {
            // Handle nanosecond precision mismatch by using a different approach
            // Convert to string and parse back to avoid precision issues
            var zdtString = zdt.ToString();
            return DateTime.Parse(zdtString, CultureInfo.InvariantCulture).ToUniversalTime();
        }
    }

    private static DateTimeOffset ConvertZonedDateTimeToDateTimeOffset(ZonedDateTime zdt)
    {
        try
        {
            return zdt.ToDateTimeOffset();
        }
        catch (ValueTruncationException)
        {
            // Handle nanosecond precision mismatch by using a different approach
            // Convert to string and parse back to avoid precision issues
            var zdtString = zdt.ToString();
            return DateTimeOffset.Parse(zdtString, CultureInfo.InvariantCulture);
        }
    }

    private static Dictionary<string, object?> ConvertDictionary(IDictionary dict)
    {
        var result = new Dictionary<string, object?>();
        foreach (DictionaryEntry entry in dict)
        {
            var key = entry.Key?.ToString() ?? string.Empty;
            result[key] = ToNeo4jValue(entry.Value);
        }
        return result;
    }

    private static List<object?> ConvertCollection(IEnumerable enumerable)
    {
        var list = new List<object?>();
        foreach (var item in enumerable)
        {
            list.Add(ToNeo4jValue(item));
        }
        return list;
    }

    private static bool IsDictionaryType(Type type)
    {
        return type.IsGenericType &&
               (type.GetGenericTypeDefinition() == typeof(Dictionary<,>) ||
                type.GetGenericTypeDefinition() == typeof(IDictionary<,>));
    }

    private static bool IsCollectionType(Type type)
    {
        return type.IsArray ||
               (type.IsGenericType && type.GetInterfaces().Any(i =>
                   i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)));
    }

    private static object? ConvertToDictionary(object value, Type targetType)
    {
        if (value is not IDictionary sourceDict)
            return null;

        var keyType = targetType.GetGenericArguments()[0];
        var valueType = targetType.GetGenericArguments()[1];
        var dictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
        var dict = (IDictionary)Activator.CreateInstance(dictType)!;

        foreach (DictionaryEntry entry in sourceDict)
        {
            var key = Convert.ChangeType(entry.Key, keyType, CultureInfo.InvariantCulture);
            var val = FromNeo4jValue(entry.Value, valueType);
            dict.Add(key, val);
        }

        return dict;
    }

    private static object? ConvertToCollection(object value, Type targetType)
    {
        if (value is not IEnumerable sourceCollection)
            return null;

        var elementType = targetType.IsArray
            ? targetType.GetElementType()!
            : targetType.GetGenericArguments()[0];

        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;

        foreach (var item in sourceCollection)
        {
            list.Add(FromNeo4jValue(item, elementType));
        }

        return targetType.IsArray
            ? list.GetType().GetMethod("ToArray")!.Invoke(list, null)
            : list;
    }
}
