# Design decisions

Each entry records a choice that is not obvious from the language specification, with the reason.

## Compiler architecture

* **Roslyn-like layering.** Hand-written lexer and recursive-descent parser → immutable full-fidelity syntax tree (trivia and
  spans, error tolerant, missing tokens synthesized, skipped tokens folded into trivia so `tree.ToFullString()` equals the
  source) → `Compilation`/symbols (source and metadata via `System.Reflection.Metadata`) → binder (bound tree) → lowering →
  IL emit with `MetadataBuilder`/`InstructionEncoder` and a portable PDB.
* **No `Reflection.Emit`, no reflection, no `dynamic`.** `Caladesi.Compiler`, `Caladesi.Cli`, `Caladesi.Runtime` and `Caladesi.LanguageServer`
  are AOT/trim compatible (`eng/aot-check.sh` publishes each with `PublishAot` and fails on any IL2xxx/IL3xxx warning).
  `Caladesi.Build.Tasks` is exempt: it only shells out to `calac`.
* **Deterministic output.** The module version id is a hash of the content; `EmitIsDeterministic` guards it.
* **Never crash on bad input.** `calac` turns internal failures into `CALC9999`; a mutation fuzz test
  (`RobustnessTests`) feeds damaged samples through binding and emit.
* **A `ReferenceSet` is not thread safe.** Metadata symbols are materialized lazily. Unit tests run serially; the language
  server binds on one thread.
* **Closure conversion before state machines.** Lambdas become methods on per-scope display classes (nested, generic over the
  enclosing method's type parameters through a type-parameter map applied at signature encoding time). Async methods and
  iterators are then rewritten to state machine classes; their locals are hoisted into fields.

## Language

* **Integer division stays integer.** `7 / 2` is `3`, because `7` is `int`. `7.0 / 2` is `3.5`. This follows C#, not JavaScript.
* **`for (let i …)` captures one variable** for the whole loop (C#), not one per iteration (JavaScript).
* **Overloads are real CLR overloads.** Several function declarations with the same name and different parameter lists are
  allowed; each has its own body.
* **Members are emitted verbatim** (`shout`, not `Shout`), so C# callers see exactly the names written in Caladesi. Override and
  interface matching flips the case of the first letter so a lower-case Caladesi method can implement a C# interface member; the
  emitted member then takes the name of the base member.
* **All instance members of unsealed classes are virtual** (TypeScript semantics); `override` is required when redeclaring.
* **A derived class without a constructor inherits the base constructors.** TypeScript does this; C# does not. Caladesi emits one
  forwarding constructor per accessible base constructor.
* **Object types are records.** `type P = { … }` is a sealed record class; `struct type` is a readonly record struct. Optional
  members are nullable with a `null` default.
* **Object literals without a target** become a compiler-generated internal record generic over its property types (like C#
  anonymous types). Only a single leading spread is supported.
* **`===` is `op_Equality` when present**, otherwise reference or value equality as appropriate. `==` is rejected with a fix-it.
* **Fields are initialized after the base constructor call**, in declaration order, before the rest of the constructor body.
* **Top-level variables are static fields** of the file's module class; their initializers run in the entry method (or the
  static constructor for library projects). Consequently ref struct types cannot be top-level variables.
* **Module class name conflicts** get a `Module` suffix (`Shapes` vs. a type `Shapes`).
* **Integer literal rule:** `int`, else `long`, else `ulong`; decimal literals are `double`.
* **`char`** is available as a type; a one-character string literal converts to it.
* **Destructuring defaults** are only allowed for nullable element types, because the default applies when the value is `null`.

* **Narrowing follows TypeScript for initializers and assignments, and is stricter after calls.** A declaration initializer
  narrows exactly like an assignment (`let s: string | null = "x"; s.Length` compiles, also for top-level variables). A narrowed
  field (through `this`, another object, or a static/top-level variable) is reset to its declared type after any call into
  Caladesi code, constructor, or delegate invocation, and a variable assigned inside a lambda or local function is reset once
  that lambda is created and after such calls, because the callee may assign it. Calls into referenced assemblies keep the
  narrowing: they cannot assign source variables. The reset lets `x instanceof T` be asked again after the callee ran, so
  `CAL0241` ("can never be an instance") is a warning, not an error; with unsound narrowing it was a hard error on correct code.
  Loops are not re-analysed for calls in their body.

## Unions and enums

* **Named unions** (`type Shape = Circle | Square`) are generated readonly structs with fields `Tag` (1-based, 0 = empty) and
  `Value` (`object?`), one constructor per case, implicit conversions from each case, `ToString`/`Equals`/`GetHashCode`, and
  `[CaladesiUnion]`. **Inline unions** use `Caladesi.Runtime.Union<T1…T8>`, which has the same shape.
* **Case order is canonical**: inline union cases are sorted by their display string, so `int | string` and `string | int` are
  the same type.
* **Designed to migrate to C# unions.** C# is expected to add a `union` declaration whose members are the case types and whose
  lowering is a tag plus a value with `Is`/`TryGet` style access. Keeping the marker attribute, the constructor-per-case shape,
  and implicit conversions means the generated struct can later be replaced by the language-supported form without changing
  how Caladesi source uses it; consumers from C# see a documented, pattern-friendly struct today.
* **String-literal unions** (`"up" | "down"`) become CLR enums; member names are the literal text with non-identifier
  characters replaced by `_` (prefixed with `_` if it starts with a digit). String literals convert at compile time.
* **Exhaustiveness** is checked for union, string-literal-union and nullable-union `switch` statements without `default`.

## Async, iterators, decorators

* **State machines are classes** (not structs) to keep lowering simple; they use the standard async method builders, so
  `await` interoperates with all awaitable patterns. `try/finally` that contains a suspension is lowered to a catch-all that
  parks the exception, runs the finally code as ordinary statements and replays pending jumps, so no `finally` handler ever
  runs because of a suspension.
* **Async iterators** run the same state machine once per `MoveNextAsync`, using a fresh `AsyncTaskMethodBuilder<bool>` each
  time; a `yield` completes it with `true`, the end of the body with `false`.
* **Iterator disposal** resumes the machine in "disposing" mode so enclosing `finally` code runs.
* **Decorators map to attributes** (class, method, property, field, parameter targets). Named attribute values are written
  as a trailing object literal because TypeScript decorators have no named-argument syntax.
* **The test entry point is generated source.** `calac -testentry` appends a `main` that creates the Microsoft.Testing.Platform
  builder and calls every `TestingPlatformBuilderHook` the MSBuild items declare (MSTest's last). The standard MSBuild
  generator only understands C#, VB and F#.

## Tooling

* **Project extension `.calaproj`**, SDK `Caladesi.Sdk` layered on `Microsoft.NET.Sdk` (`LanguageTargets` override, as F# does).
* **`calac` ships as a framework-dependent app inside the SDK package** and is started through the `dotnet` host by the
  `CaladesiCompile` ToolTask, so the SDK is RID independent.
* **`Caladesi.Runtime` is an implicit package reference** (like FSharp.Core); it holds the union types and attributes.
* **Language server:** hand-rolled JSON-RPC over stdio, `JsonDocument`/`Utf8JsonWriter` only (no reflection-based serialization), one `Compilation` per project
  loaded through `dotnet msbuild -getProperty/-getItem`.
* **Language server threading:** one reader thread parses framed JSON-RPC messages; a single worker thread handles them in
  arrival order, so the compiler (not thread safe) is only used from that thread. Diagnostics run after the queue has been quiet
  for 150 ms, which coalesces bursts of edits; requests never wait for diagnostics and always see the latest text because the
  `Compilation` is rebuilt lazily from the open-document overlay (only changed files are re-parsed).
* **Language server projects:** the nearest `*.calaproj` (up to the workspace root) is evaluated with
  `dotnet msbuild -getProperty -getItem -target:ResolveAssemblyReferences,FindReferenceAssembliesForReferences`
  (an unrestored project is restored once). Files without a project, or whose project fails to load, are analysed standalone against
  the targeting pack and `Caladesi.Runtime.dll` next to the server. Project references are not built; only package and framework
  references are visible.
