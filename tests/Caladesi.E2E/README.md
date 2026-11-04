# Caladesi.E2E

End-to-end tests: build and run real Caladesi projects through the real `dotnet` CLI and the packed `Caladesi.Sdk`.

## Running

```sh
eng/bootstrap.sh
export CALADESI_BOOTSTRAP_DIR="$PWD/artifacts/bootstrap"
export CALADESI_VERSION=1.0.0
dotnet test --project tests/Caladesi.E2E        # whole suite (about 40 s)
dotnet test --solution Caladesi.slnx            # unit tests + E2E
```

On the first call of a run the harness executes `eng/pack.sh` into `artifacts/e2e/packages`
(`Caladesi.Runtime`, `Caladesi.Sdk` with `calac` and the MSBuild tasks inside, `Caladesi.Templates`; version 1.0.0).
Every sample is then built against that feed only, with an isolated `NUGET_PACKAGES` folder
(`artifacts/e2e/nuget-packages`; its `caladesi.*` entries are deleted at the start of each run so a re-packed SDK is
never shadowed by a cached copy). Samples therefore need no network, except samples with `expected.test` or
`needs-nuget-org.txt`, which also get nuget.org as a source (MSTest packages).

Environment variables:

| Variable | Effect |
| --- | --- |
| `CALADESI_E2E_FEED=<dir>` | Use an already packed feed and skip `eng/pack.sh`. |
| `CALADESI_BOOTSTRAP_DIR=<dir>` | Trusted native CLI/task required when the harness packs; there is no C# fallback. |
| `CALADESI_VERSION=1.0.0` | Pack the version pinned by the integration fixtures. |
| `CALADESI_E2E_KEEP=1` | Keep the temp work directory of passing samples too (failing ones are always kept; the path is in the failure message). |

To pack by hand: `CALADESI_VERSION=1.0.0 eng/pack.sh` with the bootstrap set above
(output: `artifacts/packages`, override with `CALADESI_PACK_OUT`).
To try a package by hand, put a `nuget.config` with that folder as a source next to your project.

## Samples

Every directory `samples/<name>/` that contains (at any depth) a `*.calaproj` is one test case. The whole directory
tree is copied to a temp directory together with a generated `nuget.config`, so a sample may hold several projects
(Caladesi and C#).

**Entry project** (the one that is built and run), first match wins:

1. `entry.txt`: first non-blank line is the project path relative to the sample root (e.g. `app/app.calaproj`).
2. `app.calaproj` in the sample root.
3. The only `*.calaproj` / `*.csproj` in the sample root.

**Files in a sample** (all optional except the projects):

| File | Meaning |
| --- | --- |
| `expected.stdout` | Exact stdout of the run (`\r\n` normalised to `\n`). |
| `expected.exitcode` | Expected exit code of the run (default `0`). |
| `expected.diagnostics` | One diagnostic id (`CAL0023`) per line. `dotnet build` must then **fail** and its output must contain every id. Nothing is run. |
| `expected.test` | Marks a test project: run with `dotnet test --project <entry> --no-build` (a Microsoft.Testing.Platform `global.json` is written if the sample has none). Each non-blank line must appear in the output, e.g. `succeeded: 1`. |
| `args.txt` | Command-line arguments for the run, one per line. |
| `needs-nuget-org.txt` | Add nuget.org as a package source (implied by `expected.test`). |
| `ignore.txt` | Skip the sample (inconclusive); the content is the reason. |

Flow: `dotnet build <entry>` (full output shown on failure), then `dotnet run --no-build --project <entry>`
(or `dotnet test`).

## Current samples

One folder per language area (`classes`, `generics`, `collections`, `nullability-unions-enums`, `unions-inheritance-generics`,
`expressions-and-statements`, `operators-and-members`, `out-parameters`, `constraints-and-destructuring`, `decorators`,
`async-await`, `iterators`, `closures`, `imports`, `interop-interfaces`, `main-exit-code`), project-level scenarios
(`library-reference`, `csharp-reference`, `csharp-consumes-caladesi`, `nuget-package`, `mstest-basic`) and diagnostics
(`errors-*`). Single-file samples are also run in memory by `Caladesi.Compiler.Tests` (`SampleProgramTests`).
