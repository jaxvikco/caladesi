using System.Collections.Immutable;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Symbols.Source;

/// <summary>The static class generated for a source file: holds its top-level functions, variables and entry point.</summary>
public sealed class SourceModuleClassSymbol(Compilation compilation, SourceFile file, string className) : SourceNamedType(compilation, file, null, className, file.Namespace, Accessibility.Public)
{
    private SourceMethodSymbol? _entry;
    private SourceMethodSymbol? _userMain;

    public override TypeKind TypeKind => TypeKind.Class;
    public override bool IsAbstract => true;
    public override bool IsSealed => true;
    public override bool IsStatic => true;
    public override bool IsStaticClass => true;
    public override NamedTypeSymbol? BaseType => Compilation.GetSpecialType(SpecialType.Object);

    /// <summary>The compiler-generated entry method holding top-level statements, if this file has any.</summary>
    public SourceMethodSymbol? TopLevelEntry
    {
        get { _ = GetMembers(); return _entry; }
    }

    public SourceMethodSymbol? UserMain
    {
        get { _ = GetMembers(); return _userMain; }
    }

    public ImmutableArray<(SourceFieldSymbol Field, VariableDeclaratorSyntax Declarator)> VariableInitializers { get; private set; } =
        [];

    private bool HasTopLevelAwait()
    {
        return TopLevelAwaitScanner.Contains(File.Tree.Root);
    }

    public bool HasExtensionMethods => GetMembers().OfType<MethodSymbol>().Any(m => m.IsExtensionMethod);

    protected override ImmutableArray<Symbol> BuildMembers()
    {
        ImmutableArray<Symbol>.Builder b = ImmutableArray.CreateBuilder<Symbol>();
        ImmutableArray<(SourceFieldSymbol, VariableDeclaratorSyntax)>.Builder vars = ImmutableArray.CreateBuilder<(SourceFieldSymbol, VariableDeclaratorSyntax)>();
        bool hasStatements = false;

        foreach (StatementSyntax s in File.Tree.Root.Statements)
        {
            switch (s)
            {
                case FunctionDeclarationSyntax f when !f.Identifier.IsMissing:
                    {
                        ModifierSet mods = new(f.Modifiers);
                        bool isMain = (f.Identifier.Text == "main" && !mods.IsExport) || f.Identifier.Text == "main";
                        SourceMethodSymbol m = new(Compilation, File, this, f.Identifier.Text, MethodKind.Ordinary, mods, f,
                            mods.IsExport ? Accessibility.Public : Accessibility.Internal)
                        {
                            IsStaticFlag = true,
                            ParameterList = f.Parameters,
                            TypeParameterList = f.TypeParameters,
                            ReturnAnnotation = f.ReturnType,
                            BodySyntax = f.Body,
                            IsGeneratorSyntax = f.AsteriskToken != null,
                        };
                        b.Add(m);
                        if (isMain && f.Identifier.Text == "main" && Compilation.Options.OutputKind == OutputKind.ConsoleApplication)
                        {
                            _userMain ??= m;
                        }

                        break;
                    }
                case VariableStatementSyntax v:
                    {
                        ModifierSet mods = new(v.Modifiers);
                        VariableDeclarationListSyntax decl = v.Declaration;
                        if (decl.IsUsing)
                        {
                            hasStatements = true; // using at top level lives in the entry method
                            break;
                        }
                        foreach (VariableDeclaratorSyntax d in decl.Declarators)
                        {
                            if (d.Identifier is not { IsMissing: false } id)
                            {
                                hasStatements = true; // destructuring declarations at top level run in the entry method
                                continue;
                            }
                            Accessibility acc = mods.IsExport ? Accessibility.Public : Accessibility.Internal;
                            VariableDeclaratorSyntax dd = d;
                            bool isConst = decl.IsConst;
                            SourceFieldSymbol field = new(Compilation, File, this, id.Text, acc, true, false, false, d, null,
                                f => SourceModuleVariables.BindType(this, f, dd))
                            {
                                IsModuleVariable = true,
                                Declarator = d,
                                IsConstDeclaration = isConst,
                                ConstantFactory = isConst ? f => SourceModuleVariables.EvaluateConstant(this, f, dd) : null,
                                InitializerSyntax = d.Initializer?.Value,
                            };
                            b.Add(field);
                            vars.Add((field, d));
                        }
                        break;
                    }
                case ImportDeclarationSyntax or TypeAliasDeclarationSyntax or ClassDeclarationSyntax or InterfaceDeclarationSyntax or EnumDeclarationSyntax:
                    break;
                case EmptyStatementSyntax:
                    break;
                default:
                    hasStatements = true;
                    break;
            }
        }
        VariableInitializers = vars.ToImmutable();

        if (hasStatements || File.TopLevelStatements.Length > 0)
        {
            bool isAsyncEntry = HasTopLevelAwait();
            _entry = new SourceMethodSymbol(Compilation, File, this, "<Main>$", MethodKind.Ordinary, default, null, Accessibility.Private)
            {
                IsStaticFlag = true,
                ForceAsync = isAsyncEntry,
                ExplicitReturnType = Compilation.GetSpecialType(isAsyncEntry ? SpecialType.Task : SpecialType.Void),
                SynthesizedKind = SynthesizedBodyKind.Custom,
                ExplicitParameters = [new ParameterSymbol("args", new ArrayTypeSymbol(Compilation.GetSpecialType(SpecialType.String), Compilation.GetSpecialType(SpecialType.Array)), 0, null)],
                SyntheticBodyFactory = m => Compilation.CreateBinder(File, m, Compilation.BodyDiagnostics).BindTopLevelStatements(m, this),
            };
            b.Add(_entry);
        }
        else if (vars.Count > 0 && vars.Any(v => v.Item2.Initializer != null))
        {
            b.Add(new SourceMethodSymbol(Compilation, File, this, ".cctor", MethodKind.StaticConstructor, default, null, Accessibility.Private)
            {
                SynthesizedKind = SynthesizedBodyKind.StaticConstructor,
                IsStaticFlag = true,
                ExplicitParameters = [],
            });
        }
        return b.ToImmutable();
    }
}

internal static class TopLevelAwaitScanner
{
    public static bool Contains(SyntaxNode node)
    {
        foreach (SyntaxNode c in node.ChildNodes())
        {
            switch (c)
            {
                case AwaitExpressionSyntax: return true;
                case FunctionDeclarationSyntax or ClassDeclarationSyntax or InterfaceDeclarationSyntax or ArrowFunctionSyntax or FunctionExpressionSyntax: continue;
                case ForOfStatementSyntax { AwaitKeyword: not null }: return true;
                case VariableDeclarationListSyntax { AwaitKeyword: not null }: return true;
            }
            if (Contains(c))
            {
                return true;
            }
        }
        return false;
    }
}

internal static class SourceModuleVariables
{
    public static TypeSymbol BindType(SourceNamedType container, SourceFieldSymbol _1, VariableDeclaratorSyntax d)
    {
        Compilation c = container.Compilation;
        Binder binder = c.CreateBinder(container.File, container, c.DeclarationDiagnostics);
        if (d.TypeAnnotation != null)
        {
            return binder.BindType(d.TypeAnnotation.Type);
        }

        if (d.Initializer != null)
        {
            binder.EnableTopLevelAwait();
            return binder.InferVariableType(d.Initializer.Value);
        }
        c.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.TypeAnnotationRequired, container.File.FilePath, d.Span));
        return ErrorTypeSymbol.Instance;
    }

    public static ConstantValue? EvaluateConstant(SourceNamedType container, SourceFieldSymbol field, VariableDeclaratorSyntax d)
    {
        if (d.Initializer == null)
        {
            return null;
        }

        Compilation c = container.Compilation;
        Binder binder = c.CreateBinder(container.File, container, new DiagnosticBag());
        ConstantValue? cv = binder.TryEvaluateConstant(d.Initializer.Value, field.Type);
        // IL literals exist only for primitives, strings and enums; decimal and bigint constants are initialized at run time
        return cv is { Value: decimal or System.Numerics.BigInteger } ? null : cv;
    }
}
