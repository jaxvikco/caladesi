# Caladesi for VS Code

Language support for Caladesi, a statically typed .NET language with
TypeScript-like syntax (`.cala` files).

## Features

- Syntax highlighting (TextMate grammar) for comments, strings, template literals,
  numeric literals, keywords, types, decorators and operators.
- Language configuration: comment toggling, bracket matching and auto-closing, indentation, folding.
- A thin client for the `cala-ls` language server (diagnostics, completion, hover, and so on,
  as provided by the server).

## Settings

| Setting | Description |
| --- | --- |
| `caladesi.server.path` | Path to the `cala-ls` executable. Empty: use bundled `server/cala-ls`, then `cala-ls` on `PATH`. |
| `caladesi.trace.server` | `off`, `messages` or `verbose` protocol tracing. |

## Commands

- `Caladesi: Restart Language Server` (`caladesi.restartServer`)

## Development

```sh
npm install
npm run compile     # build to ./out
npm run package     # produce a .vsix
```

Press F5 in VS Code with this folder open to launch an Extension Development Host.
