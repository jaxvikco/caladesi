# Caladesi for VS Code

Caladesi is a toy .NET language with TypeScript syntax. It has no compelling reason to exist, and I probably wouldn't use it
for professional work. The point is to explore language design, compiler construction, and .NET interoperability. Most of
this project was written by AI agents.

This extension adds Caladesi language support for `.cala` files.

## Features

- Syntax highlighting for comments, strings, template literals, numeric literals, keywords, types, decorators and operators
- Diagnostics, hover, completion, signature help and semantic highlighting
- Go to definition, find references and rename
- Outline (document symbols) and formatting
- Comment toggling, bracket matching, auto-closing, indentation and folding
- `.calaproj` project files open as XML, like `.csproj`

## Requirements

.NET 10 SDK or later. The language server reads your `.calaproj` with `dotnet`, and restores it the first time you open it.

## Getting started

```sh
dotnet new install Caladesi.Templates
dotnet new calaconsole -o hello
code hello
```

## Settings

| Setting | Description |
| --- | --- |
| `caladesi.server.path` | Native `cala-ls` executable, or `cala-ls.dll` run with `dotnet`. |
| `caladesi.server.dotnetPath` | `dotnet` executable used for the server dll and for evaluating projects. |
| `caladesi.server.runtimePath` | `Caladesi.Runtime.dll` (or its folder) for files without a project. |
| `caladesi.trace.server` | `off`, `messages` or `verbose` protocol tracing. |

## Commands

- `Caladesi: Restart Language Server`

## Building from source

Run `eng/bootstrap.sh` from the repository root to explicitly build and test a native
bootstrap with the retained C# oracle, then
`CALADESI_BOOTSTRAP_DIR="$PWD/artifacts/bootstrap" eng/package-vscode.sh`.
Packaging verifies three native stages with stable release identities and tests LSP
initialize, diagnostics, hover and shutdown before bundling the native language server.
It never publishes the C# language server or silently creates a bootstrap.

## Learn more

- [Source and documentation](https://github.com/jaxvikco/caladesi)
- [Language specification](https://github.com/jaxvikco/caladesi/blob/main/SPEC.md)
- [Samples](https://github.com/jaxvikco/caladesi/tree/main/samples)
- [Issue tracker](https://github.com/jaxvikco/caladesi/issues)
