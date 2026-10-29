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

## Building release packages from source

Release packaging requires a trusted, already runnable native compiler and native
MSBuild task, including their dependencies:

```sh
CALADESI_BOOTSTRAP_DIR=/path/to/native-bootstrap eng/pack.sh
```

By default the bootstrap uses the development names (`calac-selfhost.dll`,
`Caladesi.Compiler.SelfHost.dll`, `Caladesi.Runtime.SelfHost.dll`,
`Caladesi.Build.Tasks.SelfHost.dll`, plus the CLI's dependency/runtime configuration
files). Set `CALADESI_BOOTSTRAP_RELEASE_IDENTITY=true` when the trusted bootstrap
already uses the stable release names instead.

The script builds three isolated native stages with stable release identities,
requires byte-identical stage2/stage3 outputs, and packs only stage3 assemblies.
It then builds and runs the console template with the packaged SDK in a private
NuGet cache before copying packages to `artifacts/packages`. Failure at any step
leaves the existing output feed unchanged. There is no C# fallback.

`CALADESI_VERSION` overrides the repository version; `CONFIG`,
`CALADESI_PACK_WORK`, and `CALADESI_PACK_OUT` override the build configuration,
isolated working directory, and final feed. The SDK/template projects and
`eng/Caladesi.Runtime.Package.csproj` are source-free packaging wrappers and must
be packed through this script. The C# compiler,
runtime, and task projects remain available as development/bootstrap oracles;
they are not release package payloads.

## Learn more

- [Source and documentation](https://github.com/jaxvikco/caladesi)
- [Language specification](https://github.com/jaxvikco/caladesi/blob/main/SPEC.md)
- [Samples](https://github.com/jaxvikco/caladesi/tree/main/samples)
- [Issue tracker](https://github.com/jaxvikco/caladesi/issues)
