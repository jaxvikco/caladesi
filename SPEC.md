# Caladesi language specification

Caladesi is a statically typed .NET language with TypeScript syntax. Source files use the `.cala` extension. The compiler (`calac`)
emits IL directly; it is not a transpiler. Everything in this document compiles, runs and has unit and end-to-end coverage
(see `tests/Caladesi.Compiler.Tests` and `samples/`).

Diagnostics have stable codes `CAL0001…`. Messages quoted below are abbreviated.

## 1. Files, namespaces, modules

* There are no `namespace` declarations. The namespace of a file is the project's `RootNamespace` plus the
  folder path of the file relative to the project directory, each folder name PascalCased
  (`geo/shapes.cala` in root namespace `App` → namespace `App.Geo`).
* `export` makes a declaration public. Without it a declaration is `internal`. `export default` does not exist (`CAL0026`).
* Top-level functions, `const`s and `let`s of a file are static members of a static class named after the PascalCased file
  name (`shapes.cala` → `Shapes`). If the name collides with another type in the namespace the class gets a `Module` suffix.
  Top-level variables are static fields; exported ones are public. A top-level variable cannot have a ref struct type such as `Span<T>` (CAL0326).
* Top-level statements are allowed in exactly one file of an `Exe` project (`CAL0171`); they form the program's entry method.
  The alternative is a function `main(args: string[])` or `main()` returning `void`, `int`, `Task` or `Task<int>`.
  Top-level `await` is allowed; async entry points are bridged to a synchronous `Main`.
* A library project may not have top-level statements (`CAL0172`).

## 2. Imports

```ts
import { List } from "System.Collections.Generic";   // a bare string names a .NET namespace
import * as IO from "System.IO";                      // namespace alias
import "System.Linq";                                 // wildcard: all types and extension methods of the namespace
import { Point, distance } from "./geometry";         // a relative path names a local file (extension omitted)
import type { Point } from "./geometry";              // erased; identical to a normal import for types
```

* Implicit global imports (configurable with `<CaladesiGlobalImport Include="…"/>`): `System`, `System.Collections.Generic`,
  `System.Linq`, `System.Threading.Tasks`.
* A named import of a namespace member may omit the `Attribute` suffix (`import { AttributeUsage } from "System"`).
* A missing module is `CAL0102`, a missing member `CAL0103`.

## 3. Types

| Caladesi | CLR |
|---|---|
| `int long short byte sbyte uint ulong ushort float double decimal nint nuint char` | the same .NET primitive |
| `string` `bool` `void` `object` | the same .NET types |
| `bigint` | `System.Numerics.BigInteger` |
| `never` | the bottom type |
| `T[]` | CLR array |
| `readonly T[]` | `IReadOnlyList<T>` |
| `[A, B]` | `ValueTuple<A, B>` |
| `(a: A) => R` / `() => void` | `Func<A, R>` / `Action` |
| `type Id = string` | erased alias |

* Numeric literals: integers infer `int` (then `long`, then `ulong` if they do not fit), decimal literals infer `double`.
  Suffixes: `10L`, `1.5f`, `1.5m`, `10n` (bigint), `10u`, `10UL`. A one-character string literal converts to `char`.
* `any` (`CAL0201`) and `undefined` (`CAL0311`) are errors; use `object` and `null`.
* **Strict nullability.** `T | null` or `T?` for reference types (tracked through nullable metadata) and `Nullable<T>` for value
  types. Operators `?.`, `??`, `??=`, `!`. `T | null` is not assignable to `T` (`CAL0283`); `null` is not assignable to `T`
  (`CAL0278`). Null checks, `instanceof`, and `switch` narrow variables, parameters, fields and properties (also top-level
  variables), including inside `&&`, `||`, `?:` and after early returns. A declaration initializer and a later assignment narrow
  alike. A narrowed field, `this`-or-object member path or static variable goes back to its declared type after a call into
  Caladesi code or a delegate, and a variable that a lambda or local function assigns does too once the lambda exists
  (calls into referenced assemblies keep the narrowing). `x instanceof T` with no possible overlap is warning `CAL0241`.
* Tuples support array-style destructuring and constant indexing: `const [a, b] = t; t[0]`.
* Conversions: numeric widening is implicit; narrowing needs `as`. `as T` is a checked cast, `x satisfies T` a compile-time check.
  `/` between integers is integer division. `**` is `Math.Pow` (`BigInteger.Pow` for bigint). `>>>` is an unsigned shift.
* `==` and `!=` are the equality operators (`op_Equality` when defined, else value/reference equality). There is no `===`/`!==`.

## 4. Declarations

```ts
let counter = 0;            // mutable
const limit = 10;           // immutable (`CAL0207` on assignment)
```

### Functions

```ts
function add(a: int, b: int = 10, ...rest: int[]): int { return a + b + rest.Sum(); }
function over(x: int): string { return "int"; }
function over(x: string): string { return "string"; }   // overloads compile to real CLR overloads
const f = (x: int) => x * 2;                              // arrows become lambdas; (x: int) => … has the natural type Func<int,int>
```

* Default parameters become optional parameters; `...xs` becomes `params`.
* Lambdas capture variables by reference. Captured variables live in compiler-generated display classes; `for (let i…)`
  creates one `i` for the whole loop (C# semantics, not JavaScript's per-iteration binding).
* Local functions are hoisted and may be recursive.
* **Extension methods:** an exported top-level function whose first parameter is `this`:
  `export function shout(this s: string): string { … }`. Callers `import` the file; C# sees a real extension method.

### Classes and interfaces

```ts
interface Shape { readonly name: string; area(): double; }
abstract class Base implements Shape {
  static count = 0;
  protected id: int;
  constructor(public readonly name: string) { Base.count++; this.id = Base.count; }   // parameter properties
  abstract area(): double;
}
class Rect extends Base {
  constructor(public w: double, public h: double) { super("rect"); }
  override area(): double { return this.w * this.h; }
  get perimeter(): double { return 2 * (this.w + this.h); }
  set width(v: double) { this.w = v; }
}
sealed class Final {}
```

* Members are public by default; `private`, `protected`, `internal` are supported. Classes are unsealed unless `sealed`.
* Data members become auto-properties; `private` members become fields. `readonly` members are get-only.
* `override` is required (`CAL0161`); interface implementation is checked (`CAL0164`). Overriding and interface matching
  treat `foo` and `Foo` as the same name, and the emitted member takes the name of the base member (so C# interfaces can be
  implemented with natural TypeScript casing).
* A derived class without a constructor inherits its base class constructors (TypeScript semantics).
* Member names are emitted verbatim: an Caladesi member `shout` is `shout` to C# callers.
* Interfaces are CLR interfaces. Generic interfaces support `in`/`out` variance.

### Object types, records and structs

```ts
type Point = { x: int; y: int; label?: string };        // sealed record class
struct type Vec = { x: double; y: double };              // readonly record struct
const p: Point = { x: 1, y: 2 };                         // target-typed object literal → new Point(1, 2)
const q = { ...p, y: 5 };                                // → `with` expression
const v = new Vec(1.5, 2);
```

* Optional properties `x?: T` are nullable and default to `null`; `readonly` properties are init-only.
* Records have value equality (`==`), `ToString`, `GetHashCode`, `Equals`, `<Clone>$`, and a primary constructor.
* An object literal is typed by its target: a record or class → constructor call or object initializer; a dictionary →
  indexer assignments. Without a target it becomes a compiler-generated sealed record (internal, generic over its property
  types) usable within the declaring method. A leading single spread is supported (`{ ...p, x: 3 }`).

### Generics

`<T>` on classes, interfaces, functions and aliases. Constraints: `T extends IComparable<T> & new()`, `T extends object`,
`T extends struct`/`class`/`notnull`. `in`/`out` variance on interface and delegate type parameters.

### Enums and unions

```ts
enum Color { Red, Green = 5, Blue }                      // CLR enum
type Dir = "up" | "down";                                // generated enum; string literals convert at compile time
type Shape = Circle | Square;                            // generated readonly struct with a tag, implicit conversions, [CaladesiUnion]
function f(x: int | string) { … }                        // inline union: Caladesi.Runtime.Union<int, string> (up to 8 cases)
```

* Narrowing: `x instanceof T`, null checks, `switch (x) { case Circle: … }`. A `switch` over a union, over a string-literal
  union and over `null`-able unions must be exhaustive unless it has `default` (`CAL0266`).
* The union encoding (`Tag` + `Value` fields, one constructor per case, implicit conversions, `[CaladesiUnion]`) is chosen so it
  can migrate to C# unions later (see DECISIONS.md).

### Collections

* `[1, 2, 3]` and `[...a, ...b]` are collection expressions; the target type decides: `T[]`, `List<T>`, `Span<T>`,
  `ReadOnlySpan<T>`, `ImmutableArray<T>` and any `[CollectionBuilder]` type, `IEnumerable<T>`, `IReadOnlyList<T>`,
  `IList<T>`, and any type with `Add`. With no target the type is `T[]` for the best common element type; a tuple target
  builds a tuple.
* Ref structs (`Span<T>`) cannot be top-level variables (they would become static fields); declare them inside functions.

## 5. Statements and expressions

* `if`, `for`, `for…of`, `while`, `do…while`, `switch`, labeled `break`/`continue`, `try`/`catch`/`finally` with typed
  catches (`catch (e: IOException)`), `throw`. `for…in` is an error (`CAL0021`).
* `using x = …;` and `await using x = …;` dispose at the end of the enclosing block.
* Object and array destructuring in declarations, parameters and assignments, including defaults and nesting.
* Template literals `` `a${b}` `` become string concatenation/interpolation.
* Compound assignments including `??=`, `&&=`, `||=`; `++`/`--`.
* `typeof(T)` is the .NET type object; `x instanceof T` is a type test.

### Out parameters

A call may omit the trailing `out` parameters of a .NET method; the call then evaluates to a tuple of the return value (unless the
method returns `void`) followed by the out values:

```ts
const [ok, value] = int.TryParse("42");                  // [bool, int]
const [found, item] = dictionary.TryGetValue("key");     // [bool, V]
```

`ref` parameters are not supported from Caladesi source.

### Throw expressions

`x ?? throw new ArgumentNullException("x")` and `cond ? value : throw …` — `throw` is an expression of type `never`.

### Async and iterators

```ts
async function load(): Task<string> { await Task.Delay(1); return "x"; }
function* count(n: int): IEnumerable<int> { for (let i = 0; i < n; i++) yield i; }
async function* ticks(): IAsyncEnumerable<int> { await Task.Delay(1); yield 1; }
for await (const t of ticks()) { … }
```

`async`/`await` map to `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>` and `void`; generators map to `IEnumerable<T>`/`IEnumerator<T>`,
async generators to `IAsyncEnumerable<T>`/`IAsyncEnumerator<T>`. `await` is allowed in `try`, `catch`, `finally` and
nested expressions; `yield` inside `try/finally` runs the `finally` on early disposal.

## 6. Decorators

```ts
@AttributeUsage(AttributeTargets.All, { AllowMultiple: true })
class TagAttribute extends Attribute { constructor(public name: string) { super(); } Note: string = ""; }

@Tag("x", { Note: "named property" })
class Thing { @Tag("p") value = 1; @Tag("m") run(@Tag("arg") x: int) {} }
```

A decorator is an attribute application on classes, methods, properties, fields and parameters. The `Attribute` suffix may
be omitted. A trailing object literal assigns attribute properties/fields. Arguments must be constants, `typeof` or arrays.

## 7. Unsupported on purpose

`var`, `any`, `undefined`, `===`/`!==`, `for…in`, `export default`, `namespace`, `delete`, `declare`, intersection types other than generic
constraints, object type literals outside type aliases, `async` constructors.

## 8. Tooling

* **Project SDK:** `<Project Sdk="Caladesi.Sdk/1.0.0">` (layered on `Microsoft.NET.Sdk`; project files use `.calaproj`). `dotnet build/run/test/pack/publish`
  all work. Default compile items are `**/*.cala`. Supports `ProjectReference` (Caladesi ⇄ C#), `PackageReference`, `RootNamespace`,
  `AssemblyName`, `OutputType`, `TreatWarningsAsErrors`, `Nullable`, `<CaladesiGlobalImport/>`, incremental builds.
* **Tests:** write MSTest in Caladesi (`@TestClass`, `@TestMethod`); the SDK generates the Microsoft.Testing.Platform entry point.
* **Templates:** `dotnet new calaconsole | calalib | calatest` (install `Caladesi.Templates`).
* **Language server:** `cala-ls` (stdio JSON-RPC): diagnostics, hover, completion, go to definition, find references,
  rename, document symbols, semantic tokens, signature help, formatting. The VS Code extension in `editors/vscode` launches it.

## Known limitations

* `await` inside a conditional access chain (`p?.m(await x)`) is reported as CAL0325; assign the awaited value to a local first.
