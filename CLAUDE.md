# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Purpose

Sandbox for prototyping and experimenting with new .NET technologies, patterns, and frameworks. Each prototype is self-contained in `prototypes/<name>/` with its own solution, projects, and README.

## Build & Test Commands

Each prototype has its own `.slnx` solution file. Always work from the prototype's directory:

```bash
# Build a specific prototype
dotnet build prototypes/<name>/<SolutionName>.slnx

# Run tests (only fluent-source-gen has tests currently)
dotnet test prototypes/fluent-source-gen/FluentSourceGen.slnx

# Run a single test by name
dotnet test prototypes/fluent-source-gen/FluentSourceGen.slnx --filter "FullyQualifiedName~TestMethodName"

# Run a prototype's executable
dotnet run --project prototypes/<name>/src/<ProjectName>/<ProjectName>.csproj
```

## Architecture

### Prototype Layout

Each prototype follows this structure:
```
prototypes/<name>/
├── <Name>.slnx          # Modern XML solution format (not legacy .sln)
├── src/
│   ├── <Name>.Contracts/ # Shared types, interfaces, messages
│   └── <Name>.<Impl>/   # Implementation projects
├── tests/                # Test projects (when applicable)
├── examples/             # Example usage (when applicable)
└── README.md             # Purpose, architecture, usage
```

### Active Prototypes

- **fluent-source-gen** — Fluent API for building Roslyn source generators. Core library targets `netstandard2.0`, tests use TUnit on `net9.0`.
- **grpc-bidirectional-channel** — Full-duplex gRPC protocol with correlation-based request/response. Targets `net10.0` with Native AOT support. Protocol library on `netstandard2.0`.
- **actor-consensus-comparison** — Side-by-side Proto.Actor vs Akka.NET implementing Bully leader election across 3 nodes. Targets `net10.0`.

## Conventions

- **Solution format**: `.slnx` (modern XML-based), not legacy `.sln`
- **Language version**: C# preview for `net10.0` projects, latest for others
- **Nullable reference types**: Always enabled
- **Implicit usings**: Always enabled
- **Message types**: Sealed records for immutable messages and DTOs
- **API style**: Fluent interfaces with method chaining
- **Async patterns**: `ValueTask` preferred, async/await throughout
- **Error handling**: Result types (e.g., `DuplexResult<T>`) over exceptions for expected failures

## Git Workflow

- **Branch naming**: `claude/<prototype-name>-<suffix>`
- **Base branch**: `main`
- Each prototype gets its own feature branch and PR
- Commit frequently with descriptive messages
- Use `/new-prototype` prompt (`.claude/prompts/new-prototype.md`) to scaffold new prototypes
