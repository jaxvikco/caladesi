![Caladesi](https://raw.githubusercontent.com/jaxvikco/caladesi/main/branding/png/wordmark-on-light-2000.png)

# Caladesi.Sdk

Caladesi is a toy .NET language with TypeScript syntax. It has no compelling reason to exist, and I probably wouldn't use it
for professional work. The point is to explore language design, compiler construction, and .NET interoperability. Most of
this project was written by AI agents.

Caladesi.Sdk provides the compiler and MSBuild integration for `.calaproj` projects. Compiled programs are ordinary .NET
assemblies.

## Requirements

.NET 10 SDK or later. For editor support, install the Caladesi extension from the
[VS Code Marketplace](https://marketplace.visualstudio.com/items?itemName=jaxvik.caladesi) or
[Open VSX](https://open-vsx.org/extension/jaxvik/caladesi).

## Create a project

Install the project templates, then create and run a console app:

```sh
dotnet new install Caladesi.Templates
dotnet new calaconsole -o hello
cd hello
dotnet run
```

## Use the SDK directly

Create an `app.calaproj` with:

```xml
<Project Sdk="Caladesi.Sdk/1.0.1"> <!-- x-release-please-version -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
```

Add `Program.cala`:

```ts
Console.WriteLine("Hello from Caladesi!");
```

Run with `dotnet run`. The SDK references `Caladesi.Runtime` automatically.
Normal `dotnet build/run/test/pack/publish` commands are supported.

## Learn more

- [Source and documentation](https://github.com/jaxvikco/caladesi)
- [Language specification](https://github.com/jaxvikco/caladesi/blob/main/SPEC.md)
- [Samples](https://github.com/jaxvikco/caladesi/tree/main/samples)
- [Issue tracker](https://github.com/jaxvikco/caladesi/issues)
