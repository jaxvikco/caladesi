# Changelog

## 1.0.0 (2026-10-27)


### Features

* add a playground project built with the in-repo compiler and SDK ([c0945dd](https://github.com/jaxvikco/caladesi/commit/c0945dd6cf99982eaf4f123f97a3efec7243f058))
* **binder:** compilation, source symbols, binder with conversions, overload resolution, inference, narrowing and diagnostics ([77c7960](https://github.com/jaxvikco/caladesi/commit/77c7960ff79f0cd06781e25fd329fb7f473ec581))
* **cli:** calac command-line driver with response files and MSBuild-style diagnostics ([55debd9](https://github.com/jaxvikco/caladesi/commit/55debd997ba207edbba94d796897f65eaab81cc7))
* decorators as attributes, inline unions in runtime, enum switch exhaustiveness, misc binder fixes ([1ae28d0](https://github.com/jaxvikco/caladesi/commit/1ae28d0572ab664a42cdfb033ad7991239e5b026))
* diagnostics for await in conditional access (CAL0325) and ref struct top-level variables (CAL0326) ([92b4bde](https://github.com/jaxvikco/caladesi/commit/92b4bdebbad42fcd1a4ea0311d8488ea99fde9af))
* **emit:** async entry point bridge, top-level await variables ([9479ea1](https://github.com/jaxvikco/caladesi/commit/9479ea1ed584c3ed1dfe83fdb2341d8254ced75b))
* **emit:** IL/PE/PDB emission for core constructs; run hello world in memory ([9f8e004](https://github.com/jaxvikco/caladesi/commit/9f8e0042eae7be13e5206371b08b6ba014e3216a))
* **emit:** records, unions, inherited ctors, generic member refs; ILVerify-checked emit tests ([975128e](https://github.com/jaxvikco/caladesi/commit/975128ea24e91b8a2a0b8b21320497063af38739))
* generated Microsoft.Testing.Platform entry point for Caladesi MSTest projects ([18fa7f6](https://github.com/jaxvikco/caladesi/commit/18fa7f6fdcac4b99fab4e3d1ac58c8e20c9cf5c0))
* interface bridges (IEnumerable&lt;T&gt; etc.), implementation signature checks, PDB test ([00bd9cf](https://github.com/jaxvikco/caladesi/commit/00bd9cfc00e9de93b6dd4cd84958848282e83202))
* language server (cala-ls) and VS Code extension ([d7c7f3c](https://github.com/jaxvikco/caladesi/commit/d7c7f3c4c58b5c92548a34cd3f9d27c494c690fa))
* **lowering:** async iterators and for-await support ([bb6d750](https://github.com/jaxvikco/caladesi/commit/bb6d750a9cd2d511bab1097c5a698a804a963c8f))
* **lowering:** async state machines and iterators with await spilling and resumable try/finally ([2d015a2](https://github.com/jaxvikco/caladesi/commit/2d015a2ef45cf15715570f2378f2f05d5739d3b1))
* **lowering:** closure conversion with display classes, natural lambda types ([4ca7907](https://github.com/jaxvikco/caladesi/commit/4ca79079b2c3df7247d3d04669ca041f63e4fe0c))
* out-parameter tuples, throw expressions, more samples ([84ac0bc](https://github.com/jaxvikco/caladesi/commit/84ac0bc24c689f34ebf16726b5d9071518f662a0))
* **parser:** full-fidelity lexer and error-tolerant parser; feat(vscode): extension scaffold ([821af3d](https://github.com/jaxvikco/caladesi/commit/821af3d59204598fa47071b5f065b41c2d2ab9e1))
* publish the extension and NuGet packages from CI ([#11](https://github.com/jaxvikco/caladesi/issues/11)) ([08622a7](https://github.com/jaxvikco/caladesi/commit/08622a7452387ae9b898eb32c8fafef663b78e6a))
* record import specifiers in the semantic model so references and rename include them ([78cc9ee](https://github.com/jaxvikco/caladesi/commit/78cc9ee942e59cf26048829fff58196124539073))
* **sdk:** Caladesi.Sdk, MSBuild task, templates, E2E harness and samples; fix module namespaces, metadata string defaults ([0f9f11a](https://github.com/jaxvikco/caladesi/commit/0f9f11ad324d8d16db2539771cdc426e8654168e))
* **semantics:** SemanticModel (hover, definition, references, completion, signature help, outline, semantic tokens), formatter; fix inheritance cycles; spec and decisions docs ([2604068](https://github.com/jaxvikco/caladesi/commit/26040687a08b881d7bb83af24d1109afa48228e0))
* **symbols:** symbol model, type substitution and metadata import via System.Reflection.Metadata ([5b7766d](https://github.com/jaxvikco/caladesi/commit/5b7766d561e75875e89642a2936d07c991e8cadd))


### Bug Fixes

* bind static constructor initializers for diagnostics; readonly token for top-level const ([3e8799d](https://github.com/jaxvikco/caladesi/commit/3e8799dafdf27f66f682d3965d34cc7421ace666))
* **completion:** share member lookup rules ([#6](https://github.com/jaxvikco/caladesi/issues/6)) ([0132c48](https://github.com/jaxvikco/caladesi/commit/0132c489e0d8dec42eb399e3100d68b18bc0ec99))
* generic null checks, nested union flattening; more samples ([7205224](https://github.com/jaxvikco/caladesi/commit/7205224b57d99a0cd38ee67b31865eb0cd66f9bd))
* **lsp:** ignore null JSON-RPC results ([#5](https://github.com/jaxvikco/caladesi/issues/5)) ([42e80f8](https://github.com/jaxvikco/caladesi/commit/42e80f871f818076866fb8830264c3173b6d282a))
* overload resolution tie-breaks, static initializers, span/builder collection targets, tuple indexing ([0d6466a](https://github.com/jaxvikco/caladesi/commit/0d6466a7a7bfebda16c63c579287e51e6fecaadb))
* write-only property reads, samples as in-memory unit tests, README ([778e57a](https://github.com/jaxvikco/caladesi/commit/778e57a12539d536f4e9ec9c7b211fca37f1c37d))
