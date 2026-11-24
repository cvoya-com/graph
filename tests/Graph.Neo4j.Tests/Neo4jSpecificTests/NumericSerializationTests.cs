// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Neo4j.Tests;

#pragma warning disable CS8605

using Cvoya.Graph.Neo4j.Serialization;

public class NumericSerializationTests
{
    [Fact]
    public void ConvertToNeo4jValue_Decimal_ReturnsDouble()
    {
        // Arrange
        decimal decimalValue = 123.456m;

        // Act
        var result = SerializationBridge.ToNeo4jValue(decimalValue);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<double>(result);
        Assert.Equal(123.456d, (double)result, 10); // 10 decimal places precision
    }

    [Fact]
    public void ConvertToNeo4jValue_Float_ReturnsDouble()
    {
        // Arrange
        float floatValue = 78.9f;

        // Act
        var result = SerializationBridge.ToNeo4jValue(floatValue);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<double>(result);
        Assert.Equal(78.9d, (double)result, 5); // 5 decimal places precision for float
    }

    [Fact]
    public void ConvertFromNeo4jValue_DoubleToDecimal_ReturnsDecimal()
    {
        // Arrange
        double doubleValue = 456.789;

        // Act
        var result = SerializationBridge.FromNeo4jValue(doubleValue, typeof(decimal));

        // Assert
        Assert.NotNull(result);
        Assert.IsType<decimal>(result);
        Assert.Equal(456.789m, (decimal)result);
    }

    [Fact]
    public void ConvertFromNeo4jValue_DoubleToFloat_ReturnsFloat()
    {
        // Arrange
        double doubleValue = 456.789;

        // Act
        var result = SerializationBridge.FromNeo4jValue(doubleValue, typeof(float));

        // Assert
        Assert.NotNull(result);
        Assert.IsType<float>(result);
        Assert.Equal(456.789f, (float)result, 3); // 3 decimal places precision for float
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-123.456)]
    [InlineData(999999.999999)]
    // [InlineData(double.MinValue)] // Remove - too small for decimal
    // [InlineData(double.MaxValue)] // Remove - too large for decimal
    public void ConvertToNeo4jValue_VariousDecimalValues_ReturnsDouble(double expectedValue)
    {
        // Arrange
        decimal decimalValue = (decimal)expectedValue;

        // Act
        var result = SerializationBridge.ToNeo4jValue(decimalValue);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<double>(result);
        Assert.Equal(expectedValue, (double)result, 10);
    }

    [Theory]
    [InlineData(1E+28)] // Large but within decimal range
    [InlineData(-1E+28)] // Large negative but within decimal range
    public void ConvertToNeo4jValue_LargeDecimalValues_ReturnsDouble(double expectedValue)
    {
        // Arrange
        decimal decimalValue = (decimal)expectedValue;

        // Act
        var result = SerializationBridge.ToNeo4jValue(decimalValue);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<double>(result);
        Assert.Equal(expectedValue, (double)result, 10);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(-78.9f)]
    [InlineData(float.MinValue)]
    [InlineData(float.MaxValue)]
    public void ConvertToNeo4jValue_VariousFloatValues_ReturnsDouble(float floatValue)
    {
        // Act
        var result = SerializationBridge.ToNeo4jValue(floatValue);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<double>(result);
        Assert.Equal((double)floatValue, (double)result, 5);
    }

    [Fact]
    public void RoundTripConversion_Decimal_PreservesValue()
    {
        // Arrange
        decimal originalValue = 123.456789m;

        // Act
        var neo4jValue = SerializationBridge.ToNeo4jValue(originalValue);
        var roundTripValue = SerializationBridge.FromNeo4jValue(neo4jValue, typeof(decimal));

        // Assert
        Assert.Equal(originalValue, (decimal)roundTripValue);
    }

    [Fact]
    public void RoundTripConversion_Float_PreservesValueWithinPrecision()
    {
        // Arrange
        float originalValue = 78.9f;

        // Act
        var neo4jValue = SerializationBridge.ToNeo4jValue(originalValue);
        var roundTripValue = SerializationBridge.FromNeo4jValue(neo4jValue, typeof(float));

        // Assert
        Assert.Equal(originalValue, (float)roundTripValue, 5);
    }

    [Fact]
    public void ConvertToNeo4jValue_Decimal_ConvertsToDouble()
    {
        // Arrange
        decimal decimalValue = 123.456m;

        // Act
        var result = SerializationBridge.ToNeo4jValue(decimalValue);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<double>(result);
        Assert.Equal(123.456d, (double)result, 10);
    }

    [Fact]
    public void ConvertToNeo4jValue_Float_ConvertsToDouble()
    {
        // Arrange
        float floatValue = 789.123f;

        // Act
        var result = SerializationBridge.ToNeo4jValue(floatValue);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<double>(result);
        Assert.Equal(789.123d, (double)result, 3);
    }

    [Theory]
    [InlineData(123.456)]
    [InlineData(123.456f)]
    [InlineData(123)]
    [InlineData(123L)]
    public void ConvertFromNeo4jValue_ToDecimal_ConvertsCorrectly(object inputValue)
    {
        // Act
        var result = SerializationBridge.FromNeo4jValue(inputValue, typeof(decimal));

        // Assert
        Assert.NotNull(result);
        Assert.IsType<decimal>(result);
        Assert.True((decimal)result >= 123m);
    }

    [Fact]
    public void ConvertFromNeo4jValue_DoubleToDecimal_PreservesValue()
    {
        // Arrange
        double doubleValue = 123.456;

        // Act
        var result = SerializationBridge.FromNeo4jValue(doubleValue, typeof(decimal));

        // Assert
        Assert.Equal(123.456m, (decimal)result);
    }

    [Fact]
    public void ConvertFromNeo4jValue_FloatToDecimal_PreservesValue()
    {
        // Arrange
        float floatValue = 123.456f;

        // Act
        var result = SerializationBridge.FromNeo4jValue(floatValue, typeof(decimal));

        // Assert
        Assert.Equal(123.456m, (decimal)result);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(7.922816251426434e28)]
    public void ConvertFromNeo4jValue_ToDecimal_OutOfRangeThrows(object inputValue)
    {
        Assert.Throws<OverflowException>(() => SerializationBridge.FromNeo4jValue(inputValue, typeof(decimal)));
    }

    public static IEnumerable<object[]> FloatConversionTestData =>
        new List<object[]>
        {
                new object[] { 789.123 },
                new object[] { 789 },
                new object[] { 789L },
                new object[] { 789.123m }
        };

    [Theory]
    [MemberData(nameof(FloatConversionTestData))]
    public void ConvertFromNeo4jValue_ToFloat_ConvertsCorrectly(object inputValue)
    {
        // Act
        var result = SerializationBridge.FromNeo4jValue(inputValue, typeof(float));

        // Assert
        Assert.NotNull(result);
        Assert.IsType<float>(result);
        Assert.True((float)result >= 789f);
    }

    [Fact]
    public void ConvertFromNeo4jValue_DoubleToFloat_PreservesValue()
    {
        // Arrange
        double doubleValue = 789.123;

        // Act
        var result = SerializationBridge.FromNeo4jValue(doubleValue, typeof(float));

        // Assert
        Assert.Equal(789.123f, (float)result, 3);
    }

    [Theory]
    [InlineData(789.123f)]
    [InlineData(789)]
    [InlineData(789L)]
    [InlineData(789.123)]
    public void ConvertFromNeo4jValue_ToDouble_ConvertsCorrectly(object inputValue)
    {
        // Act
        var result = SerializationBridge.FromNeo4jValue(inputValue, typeof(double));

        // Assert
        Assert.NotNull(result);
        Assert.IsType<double>(result);
        Assert.True((double)result >= 789d);
    }

    [Fact]
    public void ConvertFromNeo4jValue_FloatToDouble_PreservesValue()
    {
        // Arrange
        float floatValue = 789.123f;

        // Act
        var result = SerializationBridge.FromNeo4jValue(floatValue, typeof(double));

        // Assert
        Assert.Equal((double)floatValue, (double)result);
    }

    [Fact]
    public void ConvertFromNeo4jValue_DecimalToDouble_PreservesValue()
    {
        // Arrange
        decimal decimalValue = 789.123m;

        // Act
        var result = SerializationBridge.FromNeo4jValue(decimalValue, typeof(double));

        // Assert
        Assert.Equal(789.123d, (double)result, 10);
    }

    [Fact]
    public void ConvertFromNeo4jValue_IntToFloat_ConvertsCorrectly()
    {
        // Arrange
        int intValue = 789;

        // Act
        var result = SerializationBridge.FromNeo4jValue(intValue, typeof(float));

        // Assert
        Assert.Equal(789f, (float)result);
    }

    [Fact]
    public void ConvertFromNeo4jValue_LongToFloat_ConvertsCorrectly()
    {
        // Arrange
        long longValue = 789L;

        // Act
        var result = SerializationBridge.FromNeo4jValue(longValue, typeof(float));

        // Assert
        Assert.Equal(789f, (float)result);
    }

    [Fact]
    public void ConvertFromNeo4jValue_IntToDouble_ConvertsCorrectly()
    {
        // Arrange
        int intValue = 789;

        // Act
        var result = SerializationBridge.FromNeo4jValue(intValue, typeof(double));

        // Assert
        Assert.Equal(789d, (double)result);
    }

    [Fact]
    public void ConvertFromNeo4jValue_LongToDouble_ConvertsCorrectly()
    {
        // Arrange
        long longValue = 789L;

        // Act
        var result = SerializationBridge.FromNeo4jValue(longValue, typeof(double));

        // Assert
        Assert.Equal(789d, (double)result);
    }
}
