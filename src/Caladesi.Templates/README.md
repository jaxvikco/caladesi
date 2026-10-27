![Caladesi](https://raw.githubusercontent.com/jaxvikco/caladesi/main/branding/png/wordmark-on-light-2000.png)

# Caladesi.Templates

Caladesi is a toy .NET language with TypeScript syntax. It has no compelling reason to exist, and I probably wouldn't use it
for professional work. The point is to explore language design, compiler construction, and .NET interoperability. Most of
this project was written by AI agents.

Project templates for creating Caladesi console applications, class libraries and MSTest test projects.

## Requirements

.NET 10 SDK or later. For editor support, install the Caladesi extension from the
[VS Code Marketplace](https://marketplace.visualstudio.com/items?itemName=jaxvik.caladesi) or
[Open VSX](https://open-vsx.org/extension/jaxvik/caladesi).

## Install

```sh
dotnet new install Caladesi.Templates
```

## Available templates

| Template | Project type |
|---|---|
| `calaconsole` | Console application |
| `calalib` | Class library |
| `calatest` | MSTest project using Microsoft.Testing.Platform |

## Create and run a project

```sh
dotnet new calaconsole -o hello
cd hello
dotnet run
```

Other project types:

```sh
dotnet new calalib -o MyLibrary
dotnet new calatest -o MyTests
```

Generated projects reference the `Caladesi.Sdk` version shipped with the templates. NuGet restore downloads the SDK and
runtime automatically; no separate compiler installation is required.

## Uninstall

```sh
dotnet new uninstall Caladesi.Templates
```

## Learn more

- [Source and documentation](https://github.com/jaxvikco/caladesi)
- [Language specification](https://github.com/jaxvikco/caladesi/blob/main/SPEC.md)
- [Samples](https://github.com/jaxvikco/caladesi/tree/main/samples)
- [Issue tracker](https://github.com/jaxvikco/caladesi/issues)
