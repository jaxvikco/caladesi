# Full-tree code review, 2026-10-22

Scope: the whole repository at `f1af422` (findings originally made at `b4ca49a` and re-verified unchanged at `f1af422`).
Build, compiler and language-server unit tests, and the 31 E2E samples were all green, so nothing below is covered by an existing test.

Every finding marked **verified** was reproduced with the locally built `calac`; the program is in `repro/` under the finding's number.
Run one with `review/repro/run.sh review/repro/<file>.cala` after `dotnet build Caladesi.slnx`.
Findings marked **traced** were established by reading the code path and were not executed.

Each item is self-contained: file and line, the input, what happens, what should happen. They are independent and can be fixed in any order.
A fix should add the reproducer (or a reduced form) as a unit test or sample.

## Blocking: wrong results, invalid IL, or compiler crashes on valid programs

1. **Unconstrained `T` to `object` never boxes.** `src/Caladesi.Compiler/Binding/Binder.Conversions.cs:90` returns true for a type parameter to `object` before the type-parameter branch, so the conversion is classified ImplicitReference and the emitter emits no `box`. `a.Equals(b)` or `Console.WriteLine(a)` with `a: T`, `T = int`, throws InvalidProgramException. `T | null` to `object | null` (classified TypeOnly) and `a!` to `object` NRE. Fix: classify TypeParameter-to-reference as Boxing (emit `box !!T`). Verified: `01a`, `01b`, `01c`, `01d`.
2. **`finally` is skipped on early disposal of iterators** whenever the yield expression gets rewritten (hoisted local, parameter, `this.field`, loop variable). `src/Caladesi.Compiler/Emit/Lowering/TryRewriter.cs:27` keys `DisposeTargets` by node reference; the Hoister rebuilds the yield node (`Bound/BoundTreeRewriter.cs:365`), so the lookup at `Emit/StateMachineLowerer.cs:353` misses and the dispose path jumps to the end label. Only literal `yield 1` works, which is what the samples test. Sync and async iterators, `break` out of `for` / `for await`, and manual `Dispose`. Fix: key by syntax, or carry the target on the node. Verified: `02a`, `02b`, `02c`.
3. **`return` / `break` / `continue` inside `finally`** emits `leave` out of the handler: InvalidProgramException at first call, no diagnostic. `_inFinally` in `Emit/MethodBodyEmitter.cs` is incremented but never read; `Binding/Binder.Statements.cs:894` has no check. Fix: report a diagnostic (C# CS0157). Verified: `03a`, `03b`.
4. **Bare `throw;` in a lambda declared inside a catch** passes the rethrow check because `_inCatch` is not reset in `Binding/Binder.Lambdas.cs` BindLambdaBody; the closure method emits `rethrow` with no handler. `Binding/Binder.Statements.cs:794`. Verified: `04`.
5. **MethodSpec cache keyed on type-argument display names** (`Emit/EmitModule.Signatures.cs:240`): a class `T` and a method `T` collide, so `id(this.v)` inside `Box<T>` reuses the `!!0` encoding built for `pick<T>`. BadImageFormatException on load. Fix: key on symbol identity (or ordinal + kind). Verified: `05`.
6. **Ref-returning members of constructed generics lose the by-ref flag.** `SubstitutedMethodSymbol` and `ConstructedMethodSymbol` don't override `ReturnsByRef` (`Symbols/MemberSymbols.cs:52,100`), so `Emit/MethodBodyEmitter.Expressions.cs:305` skips `ldobj`. `const sp: Span<int> = [1,2,3]; sp[0]` returns the pointer bits. Verified: `06`.
7. **Struct member writes through a field, property or array element mutate a copy**, silently. `this.pos.X = 1` with `pos` an auto-property is a no-op (C# reports CS1612); `this.ppos.X += 1` and `this.arr[0].X += 1` are no-ops because BuildCompound's Stabilize copies the receiver into a temp, while plain `this.arr[0].Y = 5` works. `Binding/Binder.Assignment.cs:295`; EmitAddress has no property case. Fix: reject property receivers; take addresses (ldflda/ldelema) in the compound path. Verified: `07`.
8. **`int` to a union's `double` case** pushes the int and calls the `double` constructor (`Emit/MethodBodyEmitter.Conversions.cs:101`, the comment says the conversion should happen but no code does it): InvalidProgramException. Verified: `08`.
9. **`double` to `decimal` truncates**: decimal has no `op_Implicit(double)`, so `Emit/MethodBodyEmitter.Conversions.cs:197` falls back through `conv.i4`; `3.75 as decimal` is 3. Fix: use `op_Explicit` for floating sources. Verified: `09`.
10. **`double` to `ulong` uses `conv.i8`** (`Emit/MethodBodyEmitter.Conversions.cs:238`): `1e19 as ulong` gives `long.MaxValue`. Verified: `10`.
11. **Extension method group to delegate** always pushes `ldnull` as the receiver (`Emit/MethodBodyEmitter.Expressions.cs:766`): `const f: () => string = "abc".shout; f()` NREs. Fix: closed delegate over the first argument. Verified: `11`.
12. **Nullable value type equality with a different-width numeric is always false**: `x == 1` where `x: long | null = 1L`. `Binding/Binder.Operators.cs:479` computes the common type and then ignores it, boxing both raw operands into `Object.Equals`. Verified: `12`.
13. **Override with a different return type is accepted** and becomes a new vtable slot: `B.f(): int`, `D.f(): string`, `(b as B).f()` calls `B.f`. `Symbols/Source/SourceMembers.cs:267` SignaturesMatch ignores return type. Verified: `13`.
14. **Interface property implemented with the wrong type** compiles and throws TypeLoadException at load; FindImplementation never compares property types. `Compilation/DeclarationChecker.cs:193` (and the abstract-property check at `:165`). Verified: `14`.
15. **Extending any BCL collection** (`class MyList extends List<int> {}`) gets eight spurious CAL0164 errors because explicitly implemented interface members of metadata base types are not imported (private methods are skipped in `Symbols/Metadata/MetadataNamedType.cs` GetMembers). `Compilation/DeclarationChecker.cs:171`. Verified: `15`.
16. **Overriding a C# PascalCase abstract property with camelCase fails entirely**: `override get canRead()` on `Stream` reports CAL0160 "no member to override" plus CAL0163 for every abstract property, while camelCase method overrides on the same class resolve. `Compilation/DeclarationChecker.cs:165` compares exact names; the property override lookup does not flip case. Verified: `16`.
17. **Narrowing leaks past control flow.** Each compiles cleanly and fails at runtime. Verified: `17a` to `17d`.
    - `do { if (s == null) break; } while (false); s.Length` (BindDo discards the saved state, `Binding/Binder.Statements.cs:484`).
    - `for (...; i += s.Length) { if (s == null) continue; }` (the incrementor is bound with the body-end state, `Binding/Binder.Statements.cs:521`).
    - `switch` fallthrough into a type case applies the next case's union narrowing; a Circle reaches `x.side` and throws InvalidCastException (`Binding/Binder.Switch.cs:140`).
    - `instanceof` false branch on `A | B | null` drops the null possibility (`Binding/Binder.Narrowing.cs:184`).
18. **Labeled block exited with `break label`** is treated as not completing normally, so a missing return is not reported and the function silently returns the default value. `Binding/Binder.Statements.cs:848` ignores `BreakUsed`. Verified: `18`.
19. **Compiler crashes (CALC9999 or process death) on valid or near-valid input.** All verified: `19a` to `19k`.
    - `function f(): never` and `(): never =>`: "Cannot encode type never" at `Emit/EmitModule.Signatures.cs:161`.
    - `await using` in a non-async function: BoundAwait reaches emit; `Binding/Binder.Statements.cs:334` lacks the check BindForOf has.
    - `[1, 2][0]` and `[1, 2].Length`: BoundUnbound reaches emit.
    - An IIFE `((y: int) => y * 2)(4)`: BoundBadExpression reaches emit with no diagnostic reported.
    - Duplicate object-literal keys (targeted or anonymous): ArgumentException at `Binding/Binder.Literals.cs:500`.
    - `"\u{D800}"`: ArgumentOutOfRangeException at `Syntax/Lexer.cs:438`.
    - `class C { static a = C.a; }`: stack overflow; `Symbols/Source/SourceMembers.cs:469` SourcePropertySymbol.Type has no reentrancy guard (fields have one and report CAL0150).
    - 20000 nested parentheses: stack overflow; the parser has no depth guard (`Syntax/Parser.Expressions.cs:367`).
20. **The shipped `calatest` template still uses `!==`** (`src/Caladesi.Templates/templates/calatest/Tests.cala:7`), which commit 75effa0 removed, so every `dotnet new calatest` project fails to build. The sample copy in `samples/mstest-basic` was updated, the template was not. Verified by reading.

## Should fix

21. **Top-level `using` breaks later top-level variables**: statements after it bind as locals while functions use the uninitialized module field. `using s = ...; let counter = 1; function bump() { counter++ } bump(); counter` prints 1. `Binding/Binder.Methods.cs:334`. Verified: `21`.
22. **`using` followed by a local function** hoists the function twice and reports a spurious CAL0233. `Binding/Binder.Statements.cs:347`. Verified: `22`.
23. **Ambiguous overloads are never reported** on the normal call path; `f(int, long)` / `f(long, int)` with `f(1, 2)` silently picks the first. `Binding/Binder.Overloads.cs:83` (`report:false` returns `applicable[0]`). Verified: `23`.
24. **Duplicate overloads differing only by nullability** (`f(string)`, `f(string | null)`) compile to two identical MethodDefs. `Compilation/DeclarationChecker.cs:83`. Verified: `24`.
25. **Target-typed `new List()` against `object`** constructs the open generic; the assembly fails with BadImageFormatException. `Binding/Binder.Calls.cs:204`. Verified: `25`.
26. **Narrowing is never invalidated by calls or lambdas that assign the narrowed variable or field**, and CAL0241 ("can never be an instance") turns the unsound narrowing into a hard error on correct code: `if (o instanceof int) { mut(); if (o instanceof string) ... }`. Same for `this.v` after `this.reset()`. Verified: `26`.
27. **Initializer vs assignment narrowing is inconsistent**: `let s: string | null = "x"; s.Length` is CAL0283, but `let s: string | null = null; s = "x"; s.Length` is accepted. Verified: `27`.
28. **Spurious nullability errors**: `x ?? y` with `int | null` and `long` is typed `long | null` (`Binding/Binder.Operators.cs:125`); `string | null + string | null` is rejected while `string | null + string` is accepted (`Binding/Binder.Operators.cs:202`). Verified: `28a`, `28b`.
29. **calac exits 0 after printing a CAL0100 reference error** and still writes the DLL. `src/Caladesi.Cli/Program.cs` derives the exit code from `result.Success` only; `refDiags` are printed but not counted. Verified: `29` with `-ref:/missing/Foo.dll`.
30. **Lexer and parser.** Verified: `30a` to `30d`.
    - `0b102` lexes as 6: binary literals accept hex digits (`Syntax/Lexer.cs:268`).
    - `a < b >= c` is parsed as an assignment to the generic name `a<b>` (`Syntax/Parser.Expressions.cs:234`).
    - `c ? (x) : y => y * 2` fails to parse; the arrow speculation accepts a return-type annotation here (`Syntax/Parser.Expressions.cs:103`).
    - `typeof(T).Name` cannot be parsed: `typeof` is handled as a unary expression with no postfix (`Syntax/Parser.Expressions.cs:189`); the error is "Cannot access a member of a 'void' value" plus a cascade.
31. **Spread of an anonymous record** reports "Cannot spread a value of type X into X" because the synthesized record has no clone method. `Binding/Binder.Literals.cs:622`. SPEC says a leading spread is supported. Verified: `31`.

## Minor

32. `const a: Action = Console.ReadLine` compiles (non-void method group to a void delegate, `Binding/Binder.Overloads.cs:240`). Verified: `32`.
33. `1 + Color.Red` is rejected while `Color.Red + 1` works (`Binding/Binder.Operators.cs:322`). Verified: `33`.
34. `-2 ** 2` silently evaluates to 4; TypeScript requires parentheses (`Syntax/Parser.Expressions.cs:173`). Verified: `34`.
35. `1e400` silently becomes Infinity; the OverflowException arm at `Syntax/Lexer.cs:332` is dead on modern .NET. Verified: `35`.
36. `NaN` and `Infinity` now fall through to "Cannot find name. Did you forget an import?" while `undefined` and `any` get targeted messages (`Binding/Binder.Expressions.cs:276`); SPEC.md section 7 was not updated for commit b4ca49a. `===` likewise produces two unrelated errors instead of one targeted one. Verified: `36`.
37. `export default` recovery drops the `export` token and decorators from the tree, breaking round-trip (`Syntax/Parser.Declarations.cs:110`). Traced.
38. The formatter trims trailing whitespace inside the first line of a multi-line template literal, changing the string value (`Formatting/Formatter.cs:63`, protection only applies when the line starts after the token). Traced.
39. Language server: U+2028/U+2029 are counted as line breaks by `Text/SourceText.cs:57` but not by LSP; a `$/cancelRequest` for an already-answered id is kept in `_cancelled` forever (`LanguageServer.cs:107`); a didChange range past end of line clamps to the start of the next line and eats the newline (`Text/SourceText.cs` GetPosition). Traced.
40. The build task writes multi-line `NoWarn` / `DefineConstants` into the response file unnormalized (`src/Caladesi.Build.Tasks/CaladesiCompile.cs:91,108`), so the tail becomes source-file arguments. Traced.
41. `src/Caladesi.Sdk/targets/Caladesi.props:6` and the three template `.calaproj` files hard-code 1.0.0 while `eng/pack.sh` packs with `CALADESI_VERSION`, so any other version produces an SDK referencing a runtime that is not in the feed.
42. Decimal and DateTime parameter defaults from metadata are dropped (`Symbols/Metadata/MetadataMembers.cs:213` reads only HasDefault), and overriding a `sealed override` is not diagnosed (the check at `Compilation/DeclarationChecker.cs:186` is unreachable). Traced.
43. Tests: the language-server project tests reuse a stale packed feed after the first run (`tests/Caladesi.LanguageServer.Tests/ProjectEnvironment.cs:45`); `EmitIsDeterministic` compiles each sample's `Program.cala` alone, so multi-file samples fail to compile and skip the byte comparison (`tests/Caladesi.Compiler.Tests/Robustness/RobustnessTests.cs:117`).

## Claims rejected after testing

These were raised during review and did not reproduce; listed so nobody re-chases them.

- `==` on `string | null` falling back to reference equality (value equality works, records too).
- `flag ? [1, 2] : [3]` crashing the compiler (compiles and runs).
- `super.m()` inside a lambda or an async method using the wrong `this` (works).
- `a == b` on an unconstrained `T` producing invalid IL (runs).
- CoreCompile keeping a stale assembly after a source file is deleted (it recompiles).
