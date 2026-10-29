<h1 align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="branding/png/wordmark-on-dark-2000.png">
    <img src="branding/png/wordmark-on-light-2000.png" width="420" alt="Caladesi">
  </picture>
</h1>

Caladesi is a toy .NET language with TypeScript syntax. It has no compelling reason to exist, and I probably wouldn't use it
for professional work. The point is to explore language design, compiler construction, and .NET interoperability. Most of
this project was written by AI agents.

Source files are `.cala`; the compiler emits IL directly (it is not a transpiler), so Caladesi assemblies are ordinary
.NET assemblies that interoperate with C# and other .NET languages in both directions.

```ts
import { List } from "System.Collections.Generic";

type Point = { x: int; y: int };

class Shape {
  constructor(public readonly name: string) {}
  area(): double { return 0; }
}

async function main(args: string[]): Task<int> {
  const points: List<Point> = [{ x: 1, y: 2 }, { x: 3, y: 4 }];
  for (const p of points) Console.WriteLine(`${p.x},${p.y}`);
  await Task.Delay(10);
  return 0;
}
```

## Try it

Requires .NET 10 or later. For editor support, install the Caladesi extension from the
[VS Code Marketplace](https://marketplace.visualstudio.com/items?itemName=jaxvik.caladesi) or
[Open VSX](https://open-vsx.org/extension/jaxvik/caladesi).

```sh
dotnet new install Caladesi.Templates  # install the templates from NuGet.org
dotnet new calaconsole -o hello
cd hello && dotnet run
```

A project file is a normal SDK-style project that uses the Caladesi SDK:

```xml
<Project Sdk="Caladesi.Sdk/1.0.1"> <!-- x-release-please-version -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
```

`dotnet build/run/test/pack/publish` work as usual. Caladesi projects can reference C# projects and NuGet packages and the other way round.

The language is described in [SPEC.md](SPEC.md); design decisions are in [DECISIONS.md](DECISIONS.md).

## Repository layout

| Path | What |
|---|---|
| `src/Caladesi.Compiler` | lexer, parser, symbols, binder, lowering, IL/PDB emit, semantic model, formatter (AOT/trim compatible) |
| `src/Caladesi.Cli` | `calac`, the command-line compiler |
| `src/Caladesi.Runtime` | runtime support (union types, attributes) |
| `src/Caladesi.Build.Tasks` | the MSBuild task that runs `calac` |
| `src/Caladesi.Sdk` | the `Caladesi.Sdk` MSBuild SDK package |
| `src/Caladesi.Templates` | `dotnet new` templates |
| `src/Caladesi.LanguageServer` | `cala-ls`, the language server (AOT compatible) |
| `editors/vscode` | VS Code extension |
| `tests/` | unit tests, language-server tests, end-to-end tests |
| `samples/` | programs used by the end-to-end suite (each has `expected.stdout` or `expected.diagnostics`) |
| `eng/` | build, packing, AOT check and helper scripts |

## Building and testing

For contributors working from a clone, these commands build and test the source. `eng/pack.sh` creates local NuGet packages
in `artifacts/packages`; the end-to-end suite uses a local feed to test projects against those packages.

The SDK version is pinned in `global.json`; everything targets `net10.0`.

```sh
eng/build.sh                          # dotnet build Caladesi.slnx
dotnet test --solution Caladesi.slnx  # unit tests + language server tests + end-to-end tests (packs the SDK, builds and runs samples)
eng/aot-check.sh                      # publishes compiler, runtime, CLI and language server with PublishAot and fails on trim/AOT warnings
eng/pack.sh                           # packs Caladesi.Sdk, Caladesi.Runtime and Caladesi.Templates into artifacts/packages
eng/package-vscode.sh                 # packages the VS Code extension with cala-ls bundled into artifacts/vscode
eng/cala-run.sh file.cala             # compile and run a single file with the locally built compiler
dotnet run --project playground       # run playground/Program.cala, built with the compiler and SDK from this repo
```

The end-to-end suite lives in `tests/Caladesi.E2E` (see [its README](tests/Caladesi.E2E/README.md)): it packs the SDK into a
throwaway feed, copies each folder in `samples/` to a temp directory, builds and runs it with the real `dotnet` CLI, and
compares stdout, exit codes and diagnostics.

### Native repeated-bootstrap verification

The explicit native verification entry point is:

```sh
dotnet msbuild eng/SelfHost.proj -t:Verify -p:BootstrapDirectory=/absolute/path/to/trusted-native-bootstrap
# Equivalent: eng/build.sh --self-host -p:BootstrapDirectory=/absolute/path/to/trusted-native-bootstrap
```

Supply trusted **existing native** artifacts, not the C# bootstrap renamed to native filenames. The directory must contain
`calac-selfhost.dll`, its `.deps.json` and `.runtimeconfig.json`, `Caladesi.Compiler.SelfHost.dll`,
`Caladesi.Runtime.SelfHost.dll`, and `Caladesi.Build.Tasks.SelfHost.dll`, together with all their runtime dependencies.
For example, retain the complete native CLI output directory and add the native build-task output and its dependencies.
Use the pinned .NET SDK; normal NuGet restore sources/cache must also be available. This target does not download
bootstrap artifacts, build C# projects, or establish the provenance of the trusted input.

The standard MSBuild target restores the five native `.calaproj` projects non-recursively, then builds runtime, compiler,
build task, CLI and language server in dependency order with `BuildProjectReferences=false`. Stage1 uses the supplied
CLI and task; stage2 uses stage1's CLI and task; stage3 uses stage2's. References resolve to outputs within the current
stage, while compiler/task paths are global MSBuild properties. Each stage starts with an empty isolated output directory
under `artifacts/selfhost/release/stage1`, `stage2`, or `stage3`; the trusted bootstrap must be outside this output root.
Override `Configuration` or `SelfHostArtifactsDirectory` explicitly if needed. These are framework-dependent managed
builds, not Native AOT publishes.

Verification requires matching SHA256 hashes for stage2/stage3 compiler, runtime, task, CLI and language-server DLLs and
portable PDBs, plus CLI/language-server dependency and runtime-configuration files. Missing outputs or any byte mismatch
fail the command; outputs remain available for diagnosis. `-t:Stage1`, `-t:Stage2`, and `-t:Stage3` stop after that stage
(rebuilding the preceding stages); only `Verify` compares hashes. Adding this infrastructure does **not** establish that
the current migration has passed verification. C# bootstrap projects and default packaging remain in place until the
actual commands pass and the resulting native toolchain is validated.

For an explicitly requested release-identity proof, add `-p:SelfHostReleaseIdentity=true`. All three output stages then
use `calac.dll`, `Caladesi.Compiler.dll`, `Caladesi.Runtime.dll`, `Caladesi.Build.Tasks.dll`, and `cala-ls.dll`, while
their artifact project-directory names retain `.SelfHost`. This changes actual compilation identities, not filenames
after compilation. The trusted input still defaults to the native suffixed names above; independently add
`-p:BootstrapReleaseIdentity=true` only when supplying a trusted native bootstrap already compiled with stable identities.
Neither switch builds or retires the C# bootstrap. Packaging must run `Verify` successfully for the selected identity
mode before consuming that invocation's stage3 files; existing stage directories alone are not evidence of verification.
Every public stage invocation deletes `NativeVerificationManifest` (default: `verified.txt` under the self-host output
root) before validating inputs. Only successful `Verify` writes it, recording `SelfHostReleaseIdentity`, `Version`,
`Configuration`, `ArtifactsRoot`, every required stage3 artifact's SHA256, and a final `Verified=true` line. Consumers
must check the selected identity/version/configuration/root and current file hashes, not merely manifest existence.

## License

The Caladesi repository is licensed under the [MIT License](LICENSE).

## The compiler

`calac` is Roslyn-like: an error-tolerant recursive-descent parser produces an immutable full-fidelity tree; the binder produces a bound
tree with `CAL0001…` diagnostics; lowering turns closures, async methods, iterators and records into plain classes and methods; the
emitter writes metadata, IL and a portable PDB with `System.Reflection.Metadata` (no `Reflection.Emit`), deterministically.
`Compilation` + `SemanticModel` are the API the language server uses.

## Editor support

`editors/vscode` contains the VS Code extension (TextMate grammar plus a client for `cala-ls`), published to the
[VS Code Marketplace](https://marketplace.visualstudio.com/items?itemName=jaxvik.caladesi) and
[Open VSX](https://open-vsx.org/extension/jaxvik/caladesi). To work on it, run these inside that folder:

```sh
npm install
npm run compile     # type-check and build to ./out
npm run bundle      # bundle the client (and vscode-languageclient) into out/extension.js
npm run package     # produce caladesi.vsix (bundled, so no node_modules are shipped)
```

Press F5 in VS Code with that folder open to launch an Extension Development Host. When no server is configured or
bundled and `cala-ls` is not on the `PATH`, the extension falls back to the development build in
`src/Caladesi.LanguageServer/bin/{Debug,Release}/net10.0/`.
