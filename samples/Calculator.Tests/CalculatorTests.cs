namespace CalculatorSample.Tests;

public class CalculatorTests
{
    [Fact]
    public void CalculatorAssembly_IsAClassLibrary()
    {
        Assert.Null(typeof(Calculator).Assembly.EntryPoint);
    }

    [Theory]
    [InlineData(2, 3, 5)]
    [InlineData(-2, 3, 1)]
    [InlineData(0, 3, 3)]
    public void Add_ReturnsSum(int left, int right, int expected)
    {
        Assert.Equal((decimal)expected, Calculator.Add(left, right));
    }

    [Theory]
    [InlineData(7, 3, 4)]
    [InlineData(2, 5, -3)]
    [InlineData(-2, -3, 1)]
    public void Subtract_ReturnsDifference(int left, int right, int expected)
    {
        Assert.Equal((decimal)expected, Calculator.Subtract(left, right));
    }

    [Theory]
    [InlineData(4, 3, 12)]
    [InlineData(-4, 3, -12)]
    [InlineData(0, 3, 0)]
    public void Multiply_ReturnsProduct(int left, int right, int expected)
    {
        Assert.Equal((decimal)expected, Calculator.Multiply(left, right));
    }

    [Theory]
    [InlineData(8, 2, 4)]
    [InlineData(8, -2, -4)]
    [InlineData(0, 2, 0)]
    public void Divide_ReturnsQuotient(int dividend, int divisor, int expected)
    {
        Assert.Equal((decimal)expected, Calculator.Divide(dividend, divisor));
    }

    [Fact]
    public void Add_PreservesDecimalPrecision()
    {
        Assert.Equal(0.3m, Calculator.Add(0.1m, 0.2m));
    }

    [Fact]
    public void Subtract_SupportsFractions()
    {
        Assert.Equal(1.25m, Calculator.Subtract(3.75m, 2.5m));
    }

    [Fact]
    public void Multiply_SupportsFractions()
    {
        Assert.Equal(3.125m, Calculator.Multiply(1.25m, 2.5m));
    }

    [Fact]
    public void Divide_DoesNotTruncateFractions()
    {
        Assert.Equal(2.5m, Calculator.Divide(5m, 2m));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    [InlineData(-5)]
    public void Divide_ByZero_Throws(int dividend)
    {
        var exception = Assert.Throws<DivideByZeroException>(
            () => Calculator.Divide(dividend, 0m));

        Assert.Equal("Cannot divide by zero.", exception.Message);
    }

    [Fact]
    public void Add_BeyondDecimalRange_Throws()
    {
        Assert.Throws<OverflowException>(() => Calculator.Add(decimal.MaxValue, 1m));
    }

    [Fact]
    public void Subtract_BeyondDecimalRange_Throws()
    {
        Assert.Throws<OverflowException>(() => Calculator.Subtract(decimal.MinValue, 1m));
    }

    [Fact]
    public void Multiply_BeyondDecimalRange_Throws()
    {
        Assert.Throws<OverflowException>(() => Calculator.Multiply(decimal.MaxValue, 2m));
    }

    [Fact]
    public void Divide_BeyondDecimalRange_Throws()
    {
        Assert.Throws<OverflowException>(() => Calculator.Divide(decimal.MaxValue, 0.1m));
    }
}
