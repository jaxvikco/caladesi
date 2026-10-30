extern alias SelfHost;

using System.Collections.Immutable;
using System.Reflection;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;
using H = SelfHost::Caladesi.Compiler.Symbols;
using HS = SelfHost::Caladesi.Compiler.Symbols.Source;
using HX = SelfHost::Caladesi.Compiler.Syntax;
using HB = SelfHost::Caladesi.Compiler.Bound;
using HD = SelfHost::Caladesi.Compiler.Diagnostics;
using HM = SelfHost::Caladesi.Compiler.Symbols.Metadata;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class SourceTypesParityTests
{
    [TestMethod]
    [DataRow("export class C<T> { private readonly hidden: int = 2; value?: string; static shared: int = 4; readonly fixed: int; constructor(public item: T, private readonly key: string, protected flag: bool) {} method<U>(x: U): U { return x; } }")]
    [DataRow("abstract class B { constructor(value: int) {} constructor(value: string) {} } class C extends B {} class D extends C { constructor() {} }")]
    [DataRow("class B { constructor() {} } abstract class C extends B {} static class S { static value: int = 3; }")]
    [DataRow("class C { get value(): string { return \"\"; } set value(v: string) {} static get value(): int { return 1; } static set value(v: int) {} readonly p: int; abstract q: int; }")]
    [DataRow("export interface I<T> { readonly value?: T; static other: int; method<U>(value: U): T; defaultMethod(): int { return 2; } get label(): string; set label(v: string); }")]
    [DataRow("enum E { Zero, First = 4, Next, Named = First + 3, Following }")]
    [DataRow("export type Point<T> = { x: T; y?: int; readonly title: string; }; struct type Pair = { left: int; right: string; };")]
    [DataRow("type Direction = \"up\" | \"down\" | \"side-way\" | \"3d\" | \"\";")]
    [DataRow("type First = int | string; type Combined = First | bool | null;")]
    [DataRow("type Wrapped<T> = ( { value: T; } ); type Maybe = int | null; type Alias = string;")]
    public void PairedSyntax_AllDeclaredMembersAndSynthesisMatch(string source)
    {
        Fixture fixture = new(source);
        Assert.HasCount(fixture.File.Types.Length, fixture.Types);
        foreach (var expected in fixture.File.Types)
        {
            var actual = fixture.Types[expected.Name];
            AssertType(expected, actual);
            AssertMembers(expected, actual);
        }
        foreach (var expected in fixture.File.Aliases)
        {
            var actual = fixture.Aliases[expected.Name];
            Assert.AreEqual(expected.Target.ToDisplayString(), actual.Target.ToDisplayString());
            Assert.AreSame(actual.Target, actual.Target);
            Assert.AreEqual(expected.IsExported, actual.IsExported);
            Assert.HasCount(expected.TypeParameters.Length, actual.TypeParameters);
        }
    }

    [TestMethod]
    [DataRow("class A extends B {} class B extends A {}")]
    [DataRow("interface I extends J {} interface J extends I {}")]
    [DataRow("class A<T> extends A<int> {} interface I<T> extends I<int> {}")]
    [DataRow("class C extends string implements int {} interface I extends int {}")]
    [DataRow("type R = { x: int; x: string; method(): void; }; type E = \"a-b\" | \"a b\" | \"ok\";")]
    [DataRow("type U = int | string | int; struct type Invalid = int;")]
    [DataRow("type A = B; type B = A;")]
    public void DeclarationFailures_DiagnosticsAndCycleFallbacksMatch(string source)
    {
        Fixture fixture = new(source);
        foreach (var type in fixture.File.Types)
        {
            _ = type.BaseType;
            _ = type.Interfaces;
            _ = type.GetMembers();
            if (type.IsUnionType)
            {
                _ = type.UnionCases;
            }
        }
        foreach (var alias in fixture.File.Aliases)
        {
            _ = alias.Target;
        }

        foreach (var type in fixture.Types.Values)
        {
            _ = type.BaseType;
            _ = type.Interfaces;
            _ = type.GetMembers();
            if (type.IsUnionType)
            {
                _ = type.UnionCases;
            }
        }
        foreach (var alias in fixture.Aliases.Values)
        {
            _ = alias.Target;
        }

        CollectionAssert.AreEqual(
            OriginalDiagnostics(fixture.Compilation).Items.Select(DiagnosticKey).Order().ToArray(),
            fixture.DeclarationDiagnostics.Items.Select(d => $"{d.Id}|{d.Span.Start}|{d.Message}").Order().ToArray());
    }

    [TestMethod]
    public void GenericOriginalDefinition_CycleDetectionAndConstructedMemberSlotsRemainLocal()
    {
        Fixture fixture = new("class Box<T> { value: T; method(x: T): T { return x; } } class Derived extends Box<int> {}");
        var expected = fixture.File.Types.Single(t => t.Name == "Derived");
        var actual = fixture.Types["Derived"];
        Assert.AreEqual(expected.BaseType!.ToDisplayString(), actual.BaseType!.ToDisplayString());
        Assert.AreSame(fixture.Types["Box"], actual.BaseType.OriginalDefinition);
        var constructed = fixture.Types["Box"].Construct(ImmutableArray.Create<H.TypeSymbol>(fixture.GetSpecialType(H.SpecialType.String)));
        var property = constructed.GetMembers("value").OfType<H.PropertySymbol>().Single();
        Assert.AreSame(constructed, property.ContainingType);
        Assert.AreEqual(H.SpecialType.String, property.Type.SpecialType);
        Assert.AreSame(property.GetMethod, constructed.GetMembers("get_value").OfType<H.MethodSymbol>().Single());
        Assert.AreSame(property, property.GetMethod!.AssociatedSymbol);
        Assert.AreSame(fixture.Types["Box"].GetMembers("value").Single(), property.OriginalSymbolDefinition);
        AssertMembers(expected, actual);
    }

    [TestMethod]
    public void MemberCache_ReentryDoesNotPublishEmptyNameIndexAndResetOnlyClearsIndex()
    {
        Fixture fixture = new("class C {}");
        ReentrantType type = new(fixture, fixture.File);
        Assert.HasCount(1, type.GetMembers("value"));
        Assert.AreEqual(1, type.Builds);
        Assert.IsTrue(type.GetMembers() == type.GetMembers());
        Assert.IsEmpty(type.GetMembers("Value"));
        type.ResetMemberCache();
        Assert.HasCount(1, type.GetMembers("value"));
        Assert.AreEqual(1, type.Builds);
    }

    [TestMethod]
    public void InterfaceSetter_UnannotatedSyntaxUsesPropertyTypedSynthesizedValueParameter()
    {
        Fixture fixture = new("interface I { get value(): int; set value(v); }");
        var expected = fixture.File.Types.Single().GetMembers("value").OfType<SourcePropertySymbol>().Single();
        var actual = fixture.Types["I"].GetMembers("value").OfType<HS.SourcePropertySymbol<SourceFile>>().Single();
        AssertMembers(fixture.File.Types.Single(), fixture.Types["I"]);
        Assert.IsNull(actual.Getter!.Declaration);
        Assert.IsNull(actual.Setter!.Declaration);
        Assert.IsNull(actual.Setter.ParameterList);
        Assert.HasCount(1, actual.Setter.Parameters);
        Assert.AreEqual(expected.Setter!.Parameters[0].Name, actual.Setter.Parameters[0].Name);
        Assert.AreEqual("value", actual.Setter.Parameters[0].Name);
        Assert.AreEqual(H.SpecialType.Int32, actual.Setter.Parameters[0].Type.SpecialType);
        Assert.AreSame(actual.Setter, actual.Setter.Parameters[0].ContainingSymbol);
        Assert.IsFalse(actual.Setter.Parameters[0].IsOptional);
    }

    [TestMethod]
    public void AnonymousRecord_GenericPropertiesConstructorAndEqualityMatch()
    {
        Fixture fixture = new("class C {}");
        SynthesizedAnonymousRecord expected = new(fixture.Compilation, fixture.File, "<>f__AnonymousType0", ["x", "title"]);
        HS.SynthesizedAnonymousRecord<SourceFile> actual = new(fixture, fixture.File, expected.Name, ["x", "title"]);
        AssertType(expected, actual);
        AssertMembers(expected, actual);
        Assert.IsTrue(actual.IsCompilerGenerated);
        var concrete = actual.Construct(ImmutableArray.Create<H.TypeSymbol>(fixture.GetSpecialType(H.SpecialType.Int32), fixture.GetSpecialType(H.SpecialType.String)));
        Assert.AreEqual(H.SpecialType.Int32, concrete.GetMembers("x").OfType<H.PropertySymbol>().Single().Type.SpecialType);
        Assert.AreEqual(H.SpecialType.String, concrete.GetMembers("title").OfType<H.PropertySymbol>().Single().Type.SpecialType);
        Assert.IsEmpty(actual.GetMembers("op_Equality"));
        Assert.IsNull(actual.GetMembers("x").OfType<HS.SourcePropertySymbol<SourceFile>>().Single().Setter);
    }

    [TestMethod]
    [DataRow("export function main(args: string[]): void {} export const answer: int = 42; let title: string;")]
    [DataRow("const x: int = 1;")]
    [DataRow("let x: int;")]
    [DataRow("const money: decimal = 1m; const huge: bigint = 2n;")]
    [DataRow("console.log(\"hello\");")]
    [DataRow("await task;")]
    [DataRow("async function nested(): Task { await task; }")]
    [DataRow("using resource = value;")]
    [DataRow("const { x } = value;")]
    public void ModuleDeclarations_EntriesVariablesAndStaticInitializationMatch(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Fixture fixture = new(source, OutputKind.ConsoleApplication);
        var expected = fixture.File.ModuleClass!;
        var actual = fixture.Module;
        AssertType(expected, actual);
        // Entry bodies/initializers are binder work; inspect declaration signatures only.
        AssertMembers(expected, actual, compareTypes: !source.Contains("await task", StringComparison.Ordinal));
        Assert.AreEqual(expected.TopLevelEntry?.Name, actual.TopLevelEntry?.Name);
        Assert.AreEqual(expected.UserMain?.Name, actual.UserMain?.Name);
        Assert.AreEqual(expected.TopLevelEntry?.IsAsync, actual.TopLevelEntry?.IsAsync);
        Assert.HasCount(expected.VariableInitializers.Length, actual.VariableInitializers);
        for (var i = 0; i < expected.VariableInitializers.Length; i++)
        {
            Assert.AreEqual(expected.VariableInitializers[i].Field.Name, actual.VariableInitializers[i].Item1.Name);
            Assert.AreEqual(expected.VariableInitializers[i].Declarator.Span.Start, actual.VariableInitializers[i].Item2.Span.Start);
        }
    }

    [TestMethod]
    [DataRow("", "_")]
    [DataRow("3d", "_3d")]
    [DataRow("a-b", "a_b")]
    [DataRow("éclair", "éclair")]
    [DataRow("two words", "two_words")]
    public void LiteralEnum_SanitizationMatchesBootstrap(string literal, string name)
    {
        Assert.AreEqual(name, SourceLiteralEnumSymbol.SanitizeName(literal));
        Assert.AreEqual(name, HS.SourceLiteralEnumSymbol<SourceFile>.SanitizeName(literal));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => HS.SourceLiteralEnumSymbol<SourceFile>.SanitizeName(null!));
    }

    [TestMethod]
    public void SourceTypes_ProductionAssemblyNeverReferencesBootstrapCompiler()
    {
        Assert.IsFalse(typeof(HS.SourceNamedType<>).Assembly.GetReferencedAssemblies()
            .Any(a => a.Name == typeof(Compilation).Assembly.GetName().Name));
    }

    private static string DiagnosticKey(Diagnostic d)
    {
        return $"{d.Id}|{d.Span.Start}|{d.Message}";
    }

    private static DiagnosticBag OriginalDiagnostics(Compilation compilation)
    {
        return (DiagnosticBag)typeof(Compilation).GetProperty("DeclarationDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(compilation)!;
    }

    private static void AssertType(NamedTypeSymbol expected, H.NamedTypeSymbol actual)
    {
        AssertSymbol(expected, actual);
        Assert.AreEqual((int)expected.TypeKind, (int)actual.TypeKind);
        Assert.AreEqual(expected.IsRecord, actual.IsRecord);
        Assert.AreEqual(expected.IsUnionType, actual.IsUnionType);
        Assert.AreEqual(expected.IsReadOnlyStruct, actual.IsReadOnlyStruct);
        Assert.AreEqual(expected.BaseType?.ToDisplayString(), actual.BaseType?.ToDisplayString());
        CollectionAssert.AreEqual(expected.Interfaces.Select(t => t.ToDisplayString()).ToArray(),
            actual.Interfaces.Select(t => t.ToDisplayString()).ToArray());
        CollectionAssert.AreEqual(expected.UnionCases.Select(t => t.ToDisplayString()).ToArray(),
            actual.UnionCases.Select(t => t.ToDisplayString()).ToArray());
        Assert.HasCount(expected.TypeParameters.Length, actual.TypeParameters);
        for (var i = 0; i < expected.TypeParameters.Length; i++)
        {
            Assert.AreEqual(expected.TypeParameters[i].Name, actual.TypeParameters[i].Name);
            Assert.AreEqual(expected.TypeParameters[i].Ordinal, actual.TypeParameters[i].Ordinal);
            Assert.AreSame(actual.TypeParameters[i].ContainingSymbol, actual);
        }
        Assert.AreSame(actual.OriginalDefinition, actual);
    }

    private static void AssertSymbol(Symbol expected, H.Symbol actual)
    {
        Assert.AreEqual(expected.Name, actual.Name);
        Assert.AreEqual(expected.MetadataName, actual.MetadataName);
        Assert.AreEqual((int)expected.Kind, (int)actual.Kind);
        Assert.AreEqual((int)expected.DeclaredAccessibility, (int)actual.DeclaredAccessibility);
        Assert.AreEqual(expected.IsStatic, actual.IsStatic);
        Assert.AreEqual(expected.IsAbstract, actual.IsAbstract);
        Assert.AreEqual(expected.IsSealed, actual.IsSealed);
        Assert.AreEqual(expected.IsVirtual, actual.IsVirtual);
        Assert.AreEqual(expected.IsOverride, actual.IsOverride);
        Assert.AreEqual(expected.IsFromSource, actual.IsFromSource);
        Assert.HasCount(expected.Locations.Length, actual.Locations);
        for (var i = 0; i < expected.Locations.Length; i++)
        {
            Assert.AreEqual(expected.Locations[i].Span.Start, actual.Locations[i].Span.Start);
            Assert.AreEqual(expected.Locations[i].Span.Length, actual.Locations[i].Span.Length);
        }
    }

    private static void AssertMembers(NamedTypeSymbol expected, H.NamedTypeSymbol actual, bool compareTypes = true)
    {
        var left = expected.GetMembers();
        var right = actual.GetMembers();
        Assert.HasCount(left.Length, right, expected.Name);
        for (var i = 0; i < left.Length; i++)
        {
            AssertSymbol(left[i], right[i]);
            Assert.AreSame(right[i].ContainingSymbol, actual);
            if (left[i] is SourceFieldSymbol field && right[i] is HS.SourceFieldSymbol<SourceFile> selfField)
            {
                if (compareTypes)
                {
                    Assert.AreEqual(field.Type.ToDisplayString(), selfField.Type.ToDisplayString(), field.Name);
                }

                Assert.AreEqual(field.IsReadOnly, selfField.IsReadOnly);
                Assert.AreEqual(field.IsBackingField, selfField.IsBackingField);
                Assert.AreEqual(field.IsEnumMember, selfField.IsEnumMember);
                Assert.AreEqual(field.LiteralText, selfField.LiteralText);
                if (compareTypes)
                {
                    Assert.AreEqual(field.ConstantValue?.Value, selfField.ConstantValue?.Value);
                    Assert.AreEqual(field.IsConst, selfField.IsConst);
                }
            }
            if (left[i] is SourcePropertySymbol property && right[i] is HS.SourcePropertySymbol<SourceFile> selfProperty)
            {
                if (compareTypes)
                {
                    Assert.AreEqual(property.Type.ToDisplayString(), selfProperty.Type.ToDisplayString(), property.Name);
                }

                Assert.AreEqual(property.IsAutoProperty, selfProperty.IsAutoProperty);
                Assert.AreEqual(property.IsInitOnlyProperty, selfProperty.IsInitOnlyProperty);
                Assert.AreEqual(property.IsOptionalMember, selfProperty.IsOptionalMember);
                Assert.AreEqual(property.BackingField?.Name, selfProperty.BackingField?.Name);
                Assert.AreEqual(property.Getter?.Name, selfProperty.Getter?.Name);
                Assert.AreEqual(property.Setter?.Name, selfProperty.Setter?.Name);
                if (selfProperty.Getter is { } getter)
                {
                    Assert.AreSame(selfProperty, getter.AssociatedSymbol);
                }

                if (selfProperty.Setter is { } setter)
                {
                    Assert.AreSame(selfProperty, setter.AssociatedSymbol);
                }
            }
            if (left[i] is SourceMethodSymbol method && right[i] is HS.SourceMethodSymbol<SourceFile> selfMethod)
            {
                Assert.AreEqual((int)method.MethodKind, (int)selfMethod.MethodKind);
                Assert.AreEqual((int)method.SynthesizedKind, (int)selfMethod.SynthesizedKind);
                Assert.AreEqual(method.UnionCaseIndex, selfMethod.UnionCaseIndex);
                Assert.AreEqual(method.InheritedBaseConstructor?.ContainingType?.Name, selfMethod.InheritedBaseConstructor?.ContainingType?.Name);
                if (compareTypes)
                {
                    Assert.AreEqual(method.ReturnType.ToDisplayString(), selfMethod.ReturnType.ToDisplayString(), method.Name);
                    CollectionAssert.AreEqual(method.Parameters.Select(p => $"{p.Name}:{p.Type.ToDisplayString()}:{p.IsOptional}").ToArray(),
                        selfMethod.Parameters.Select(p => $"{p.Name}:{p.Type.ToDisplayString()}:{p.IsOptional}").ToArray(), method.Name);
                    for (var p = 0; p < method.Parameters.Length; p++)
                    {
                        Assert.AreEqual(method.Parameters[p].OriginalParameter is null, selfMethod.Parameters[p].OriginalParameter is null,
                            $"{method.Name} parameter {p} original slot");
                    }
                }
            }
        }
        foreach (var name in left.Select(s => s.Name).Distinct())
        {
            CollectionAssert.AreEqual(left.Where(s => s.Name == name).Select(s => (int)s.Kind).ToArray(),
                actual.GetMembers(name).Select(s => (int)s.Kind).ToArray());
        }

        Assert.IsTrue(actual.GetMembers() == actual.GetMembers());
    }

    private sealed class ReentrantType(Fixture context, SourceFile file)
        : HS.SourceNamedType<SourceFile>(context, file, null, "Reentrant", context.GetNamespace(file), H.Accessibility.Public)
    {
        public int Builds { get; private set; }
        public override H.TypeKind TypeKind => H.TypeKind.Class;
        protected override ImmutableArray<H.Symbol> BuildMembers()
        {
            Builds++;
            Assert.IsEmpty(GetMembers());
            Assert.IsEmpty(GetMembers("value"));
            return [new HS.SynthesizedFieldSymbol(this, "value", H.ErrorTypeSymbol.Instance)];
        }
    }

    // Paired syntax trees and leaf binder services only: no oracle-built declarations
    // or members are copied into the selfhost symbol graph.
    private sealed class Fixture : HS.SourceTypeContext<SourceFile>
    {
        private readonly HX.SyntaxTree tree;
        private readonly HM.ReferenceSet references = HM.ReferenceSet.Create(TestReferences.Paths);
        private readonly H.NamespaceSymbol ns;
        public Compilation Compilation { get; }
        public SourceFile File { get; }
        public Dictionary<string, HS.SourceNamedType<SourceFile>> Types { get; } = [];
        public Dictionary<string, HS.TypeAliasSymbol<SourceFile>> Aliases { get; } = [];
        public HS.SourceModuleClassSymbol<SourceFile> Module { get; }
        public override HD.DiagnosticBag DeclarationDiagnostics { get; } = new();
        public override bool IsConsoleApplication => Compilation.Options.OutputKind == OutputKind.ConsoleApplication;

        public Fixture(string source, OutputKind kind = OutputKind.DynamicallyLinkedLibrary)
        {
            Compilation = TestCompiler.Create(source, kind);
            File = Compilation.GetFile(Compilation.SyntaxTrees.Single());
            tree = HX.SyntaxTree.ParseText(source, File.FilePath);
            ns = new H.NamespaceSymbol("", null).GetOrAddNamespace(File.NamespaceName);
            foreach (var statement in tree.Root.Statements)
            {
                HS.SourceNamedType<SourceFile>? type = statement switch
                {
                    HX.ClassDeclarationSyntax c => new HS.SourceClassSymbol<SourceFile>(this, File, c, ns),
                    HX.InterfaceDeclarationSyntax i => new HS.SourceInterfaceSymbol<SourceFile>(this, File, i, ns),
                    HX.EnumDeclarationSyntax e => new HS.SourceEnumSymbol<SourceFile>(this, File, e, ns),
                    _ => null
                };
                if (statement is HX.TypeAliasDeclarationSyntax alias)
                {
                    var creation = HS.TypeAliasDeclarations<SourceFile>.Create(this, File, alias);
                    type = creation.Type;
                    if (creation.Alias is { } result)
                    {
                        Aliases.Add(result.Name, result);
                    }
                }
                if (type is not null) { Types.Add(type.Name, type); ns.AddType(type); }
            }
            Module = new(this, File, File.ModuleClassBaseName);
        }

        public override HX.SyntaxTree GetTree(SourceFile file)
        {
            return tree;
        }

        public override string GetFilePath(SourceFile file)
        {
            return file.FilePath;
        }

        public override H.Symbol GetNamespace(SourceFile file)
        {
            return ns;
        }

        public override H.NamedTypeSymbol GetSpecialType(H.SpecialType type)
        {
            return references.GetSpecialType(type)!;
        }

        public override H.NamedTypeSymbol? GetWellKnownType(string name)
        {
            return references.FindType(name);
        }

        public override bool IsSourceNamedType(H.NamedTypeSymbol type)
        {
            return type.OriginalDefinition is HS.SourceNamedType<SourceFile>;
        }

        public override bool HasTopLevelStatements(SourceFile file)
        {
            return file.TopLevelStatements.Length > 0;
        }

        private T Original<T>(HX.SyntaxNode syntax) where T : SyntaxNode
        {
            return File.Tree.Root.DescendantNodesAndSelf().OfType<T>()
            .Single(n => n.Kind == (SyntaxKind)(int)syntax.Kind && n.Span.Start == syntax.Span.Start && n.Span.Length == syntax.Span.Length);
        }

        private Symbol OriginalOwner(H.Symbol owner)
        {
            if (owner is HS.TypeAliasSymbol<SourceFile>)
            {
                return File.Aliases.Single(a => a.Name == owner.Name);
            }

            if (owner is HS.SourceModuleClassSymbol<SourceFile>)
            {
                return File.ModuleClass!;
            }

            if (owner is HS.SourceNamedType<SourceFile>)
            {
                return File.Types.Single(t => t.Name == owner.Name);
            }

            if (owner is HS.SourceMethodSymbol<SourceFile> method)
            {
                var container = (NamedTypeSymbol)OriginalOwner(method.ContainerType);
                return container.GetMembers(method.Name).OfType<SourceMethodSymbol>().First(m =>
                    m.Declaration?.Span.Start == method.Declaration?.Span.Start && (int)m.MethodKind == (int)method.MethodKind);
            }
            return File.ModuleClass!;
        }
        private Binder Binder(H.Symbol owner, DiagnosticBag? diagnostics = null)
        {
            return (Binder)typeof(Compilation).GetMethod("CreateBinder", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Compilation, [File, OriginalOwner(owner), diagnostics ?? new DiagnosticBag()])!;
        }

        private H.TypeSymbol Map(TypeSymbol type)
        {
            if (type is ErrorTypeSymbol)
            {
                return H.ErrorTypeSymbol.Instance;
            }

            if (type is NullTypeSymbol)
            {
                return H.NullTypeSymbol.Instance;
            }

            if (type is NeverTypeSymbol)
            {
                return H.NeverTypeSymbol.Instance;
            }

            if (type is NullableTypeSymbol nullable)
            {
                return new H.NullableTypeSymbol(Map(nullable.Underlying));
            }

            if (type is ArrayTypeSymbol array)
            {
                return new H.ArrayTypeSymbol(Map(array.ElementType), GetSpecialType(H.SpecialType.Array), array.Rank);
            }

            if (type is TypeParameterSymbol parameter)
            {
                H.Symbol owner = parameter.Container is NamedTypeSymbol named ? Types[named.Name] : FindMethod((SourceMethodSymbol)parameter.Container);
                return owner is H.NamedTypeSymbol nt ? nt.TypeParameters.Single(p => p.Name == parameter.Name) :
                    ((H.MethodSymbol)owner).TypeParameters.Single(p => p.Name == parameter.Name);
            }
            var original = (NamedTypeSymbol)type;
            var definition = original.OriginalDefinition is SourceNamedType ? Types[original.Name] :
                references.FindType(original.OriginalDefinition.ToMetadataString())!;
            return original.IsDefinition ? definition : definition.Construct(original.TypeArguments.Select(Map).ToArray());
        }
        private HS.SourceMethodSymbol<SourceFile> FindMethod(SourceMethodSymbol original)
        {
            H.NamedTypeSymbol type = original.ContainerType is SourceModuleClassSymbol ? Module : Types[original.ContainerType.Name];
            return type.GetMembers(original.Name).OfType<HS.SourceMethodSymbol<SourceFile>>().First(m =>
                m.Declaration?.Span.Start == original.Declaration?.Span.Start && (int)m.MethodKind == (int)original.MethodKind);
        }

        public override H.TypeSymbol BindType(SourceFile file, H.Symbol owner, HX.TypeSyntax syntax, bool allowVoid)
        {
            if (syntax is HX.IdentifierNameSyntax identifier)
            {
                if (Types.TryGetValue(identifier.Name, out var named))
                {
                    return named;
                }

                if (Aliases.TryGetValue(identifier.Name, out var alias))
                {
                    return alias.Target;
                }
            }
            return Map(Binder(owner).BindType(Original<TypeSyntax>(syntax), allowVoid));
        }
        public override ImmutableArray<H.ParameterSymbol> BindParameters(SourceFile file, HS.SourceMethodSymbol<SourceFile> method, HX.ParameterListSyntax syntax)
        {
            var parameters = (ImmutableArray<ParameterSymbol>)typeof(Binder)
                .GetMethod("BindParameters", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Binder(method), [OriginalOwner(method), Original<ParameterListSyntax>(syntax)])!;
            return [.. parameters.Select(p => new H.ParameterSymbol(p.Name, Map(p.Type), p.Ordinal, method, (H.RefKind)(int)p.RefKind,
                p.IsParams, p.DefaultValue is { } value ? new H.ConstantValue(value.Value) : null, p.IsOptional, p.IsThis))];
        }
        public override H.TypeSymbol AdjustDeclaredReturnType(SourceFile file, HS.SourceMethodSymbol<SourceFile> method,
            H.TypeSymbol type, HX.TypeSyntax syntax)
        {
            return type;
        }

        public override H.TypeParameterConstraints BindConstraints(SourceFile file, H.Symbol owner, HX.TypeParameterSyntax syntax,
            H.TypeParameterSymbol parameter)
        {
            var originalOwner = OriginalOwner(owner);
            var original = originalOwner is NamedTypeSymbol named ? named.TypeParameters.Single(p => p.Name == parameter.Name) :
                originalOwner is TypeAliasSymbol alias ? alias.TypeParameters.Single(p => p.Name == parameter.Name) :
                ((MethodSymbol)originalOwner).TypeParameters.Single(p => p.Name == parameter.Name);
            var result = (TypeParameterConstraints)typeof(Binder).GetMethod("BindConstraints",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Binder(owner), [Original<TypeParameterSyntax>(syntax), original])!;
            return new(result.HasClass, result.HasStruct, result.HasNew, result.HasNotNull, [.. result.Types.Select(Map)]);
        }
        public override H.TypeSymbol InferVariableType(SourceFile file, H.Symbol owner, HX.ExpressionSyntax expression, bool allowTopLevelAwait)
        {
            var binder = Binder(owner);
            if (allowTopLevelAwait)
            {
                _ = typeof(Binder).GetMethod("EnableTopLevelAwait", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(binder, null);
            }

            return Map(binder.InferVariableType(Original<ExpressionSyntax>(expression)));
        }
        public override H.ConstantValue? EvaluateEnumInitializer(SourceFile file, HS.SourceNamedType<SourceFile> owner,
            HX.ExpressionSyntax expression, ImmutableArray<HS.SourceFieldSymbol<SourceFile>> previous)
        {
            var original = (SourceEnumSymbol)OriginalOwner(owner);
            SourceFieldSymbol[] fields = [.. previous.Select(p => original.GetMembers(p.Name).OfType<SourceFieldSymbol>().Single())];
            var value = (ConstantValue?)typeof(Binder).GetMethod("EvaluateEnumInitializer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Binder(owner), [original, Original<ExpressionSyntax>(expression), fields]);
            return value is null ? null : new H.ConstantValue(value.Value.Value);
        }
        public override H.ConstantValue? TryEvaluateConstant(SourceFile file, H.Symbol owner, HX.ExpressionSyntax expression, H.TypeSymbol type)
        {
            TypeSymbol originalType = type.SpecialType != H.SpecialType.None ? Compilation.GetSpecialType((SpecialType)(int)type.SpecialType) :
                File.Types.Single(t => t.Name == type.Name);
            var value = Binder(owner).TryEvaluateConstant(Original<ExpressionSyntax>(expression), originalType);
            return value is null ? null : new H.ConstantValue(value.Value.Value);
        }
        public override HB.BoundBlock? BindMethodBody(SourceFile file, HS.SourceMethodSymbol<SourceFile> method)
        {
            throw new InvalidOperationException("Source type tests do not bind method bodies");
        }

        public override HB.BoundBlock? BindTopLevelStatements(SourceFile file, HS.SourceMethodSymbol<SourceFile> method,
                    HS.SourceModuleClassSymbol<SourceFile> module)
        {
            throw new InvalidOperationException("Source type tests do not bind entry bodies");
        }
    }
}
