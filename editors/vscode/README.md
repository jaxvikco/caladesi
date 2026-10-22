# Caladesi for VS Code

Language support for Caladesi, a statically typed .NET language with
TypeScript-like syntax (`.cala` files).

## Features

- Syntax highlighting (TextMate grammar) for comments, strings, template literals, numeric literals (with
  suffixes), keywords, types, `struct type`, decorators and operators.
- Language configuration: comment toggling, bracket matching and auto-closing, indentation, folding.
- A thin client for the `cala-ls` language server: diagnostics, hover, completion, go to definition, find
  references, rename, outline (document symbols), semantic highlighting, signature help and formatting.

## Language server

The extension starts `cala-ls` over stdio. It is located in this order:

1. `caladesi.server.path`: a native `cala-ls` executable, or an `cala-ls.dll` that is run with `dotnet`.
2. `server/cala-ls` (or `server/cala-ls.dll`) inside the extension.
3. `cala-ls` on the `PATH`.
4. The development build `src/Caladesi.LanguageServer/bin/{Debug,Release}/net10.0/cala-ls.dll` of the repository this
   extension lives in.

Files inside an `.calaproj` are analysed with the project's compile items and reference assemblies (read through
`dotnet msbuild`; the project is restored on first use). Other `.cala` files are analysed standalone against the
.NET targeting pack and `Caladesi.Runtime.dll`.

## Settings

| Setting | Description |
| --- | --- |
| `caladesi.server.path` | Native `cala-ls` executable, or `cala-ls.dll` run with `dotnet`. |
| `caladesi.server.dotnetPath` | `dotnet` executable used for the server dll and for evaluating projects. |
| `caladesi.server.runtimePath` | `Caladesi.Runtime.dll` (or its folder) for files without a project. |
| `caladesi.trace.server` | `off`, `messages` or `verbose` protocol tracing. |

## Commands

- `Caladesi: Restart Language Server` (`caladesi.restartServer`)

## Development

```sh
npm install
npm run compile     # type-check and build to ./out
npm run bundle      # bundle the client (and vscode-languageclient) into out/extension.js
npm run package     # produce caladesi.vsix (bundled, so no node_modules are shipped)
```

Press F5 in VS Code with this folder open to launch an Extension Development Host.
