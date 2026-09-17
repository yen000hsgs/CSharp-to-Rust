# CSharp-to-Rust
A tool to convert C# code to Rust

## Extraction and requirements agents

The [C# extractor](.agents/agents/csharp-extractor.agent.md) uses a required
Roslyn helper to collect compiler facts, then writes a source-backed report
for the orchestrator. Full compiler JSON stays on disk; bounded views keep
agent input focused. New snapshots omit the unused assembly catalog without
changing compiler binding or actual source dependency relationships.

The [requirements collector](.agents/agents/requirements-collector.agent.md)
uses that evidence to author compact behavioral requirements in `document.json`.
Its `document.context.json` companion retains snapshot association, evidence
links, coverage, and unresolved decisions. The orchestrator must enforce
`--require-ready` before dispatching downstream work; structural validation
alone does not establish semantic parity.

Both agents have Copilot discovery entry points under `.github\agents` and
canonical definitions and skills under `.agents`. See
[extractor usage](docs/extractor.md) and
[collector usage](docs/requirements-collector.md) for assignments and helper
commands.

Build and exercise both helpers and the calculator sample from the repository
root with the .NET 8 SDK (or a newer SDK with the .NET 8 runtime):

```powershell
dotnet build CSharpToRust.sln
dotnet test CSharpToRust.sln
```

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

## Requirements collector

Start with [the Markdown agent definition](.agents/agents/requirements-collector.agent.md),
which lists its skills, inputs, outputs, and workflow. It accepts extraction
evidence and produces a compact `document.json` for downstream agents: public
APIs, identified behaviors/errors, and concrete cases. A separate
`document.context.json` retains snapshot association, evidence links, coverage,
and unresolved decisions without filling the specification with provenance.
Optional Markdown is rendered from the same JSON.

The .NET 8 helper prepares new drafts, validates the document/context pair,
enforces readiness with `--require-ready`, and renders the readable view; it
does not infer behavior. GenTest consumes the feature document, and Code also
needs the generated tests and manifest. Original source/tests stay available
through the orchestrator. Historical flat requirements use explicit legacy
commands rather than being silently treated as feature documents.

See [the collector workflow and schema](docs/requirements-collector.md) for
local commands, agent selection, artifact safety, and validation rules.
