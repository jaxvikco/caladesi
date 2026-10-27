![Caladesi](https://raw.githubusercontent.com/jaxvikco/caladesi/main/branding/png/wordmark-on-light-2000.png)

# Caladesi.Runtime

Caladesi is a toy .NET language with TypeScript syntax. It has no compelling reason to exist, and I probably wouldn't use it
for professional work. The point is to explore language design, compiler construction, and .NET interoperability. Most of
this project was written by AI agents.

Runtime support library for assemblies produced by the Caladesi compiler, including support for Caladesi union types.

## Usage

Caladesi projects using `Caladesi.Sdk` reference this package automatically. You normally don't need to install it
separately.

When a published Caladesi library depends on this package, NuGet restores it as a dependency for consumers, including C#
projects. The compiler and build SDK aren't needed just to use the compiled library.

## Getting started with Caladesi

To write Caladesi code, start with [Caladesi.Templates](https://www.nuget.org/packages/Caladesi.Templates) rather than
installing the Runtime package directly. See the [repository](https://github.com/jaxvikco/caladesi) for getting-started
instructions.

## Requirements

Targets .NET 10.

## Learn more

- [Source and documentation](https://github.com/jaxvikco/caladesi)
- [Language specification](https://github.com/jaxvikco/caladesi/blob/main/SPEC.md)
- [Issue tracker](https://github.com/jaxvikco/caladesi/issues)
