namespace CalculatorSample;

public static class Calculator
{
    public static decimal Add(decimal left, decimal right) => left + right;

    public static decimal Subtract(decimal left, decimal right) => left - right;

    public static decimal Multiply(decimal left, decimal right) => left * right;

    public static decimal Divide(decimal dividend, decimal divisor)
    {
        if (divisor == 0m)
        {
            throw new DivideByZeroException("Cannot divide by zero.");
        }

        return dividend / divisor;
    }
}
