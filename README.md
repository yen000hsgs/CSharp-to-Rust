# CSharp-to-Rust
A tool to convert C# code to Rust

## Sample C# calculator SDK

`samples\Calculator` is a reusable .NET 8 class library that can serve as SDK input
to the converter. Use `samples\Calculator\Calculator.csproj` as the conversion
target. The library has no console entry point or external package dependencies.
It supports addition, subtraction, multiplication, and division using C#
`decimal` values, including negative and fractional numbers.

Install the .NET 8 SDK (or a newer SDK with the .NET 8 runtime). Run the commands
below from the repository root.

### Use the SDK

Reference `samples\Calculator\Calculator.csproj` from a consuming C# project,
then call its public API:

```csharp
using CalculatorSample;

decimal sum = Calculator.Add(2m, 3m);               // 5
decimal difference = Calculator.Subtract(7m, 10m); // -3
decimal product = Calculator.Multiply(-2m, 3m);    // -6
decimal quotient = Calculator.Divide(5m, 2m);     // 2.5
```

Division by zero throws `DivideByZeroException` with the message
`Cannot divide by zero.` to the caller. Arithmetic outside the `decimal` range
throws `OverflowException`. The SDK does not print errors or terminate the
calling process. Arithmetic follows C# `decimal` precision and range limits.

Build the library or create a local NuGet package:

```powershell
dotnet build samples\Calculator\Calculator.csproj
dotnet pack samples\Calculator\Calculator.csproj --configuration Release
```

The package is named `CSharpToRust.Sample.Calculator` and is written to
`samples\Calculator\bin\Release`. Packaging does not publish it to a feed.

### Project structure

- `samples\Calculator`: SDK project, containing the public `Calculator` API.
- `samples\Calculator.Tests`: xUnit tests for arithmetic, division by zero, and overflow.
- `samples\Calculator.sln`: solution containing the SDK and tests.

### Run tests

```powershell
dotnet test samples\Calculator.sln
```
