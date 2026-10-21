using System.Collections.Immutable;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Symbols.Source;

/// <summary>The static class generated for a source file: holds its top-level functions, variables and entry point.</summary>
public sealed class SourceModuleClassSymbol : SourceNamedType
{
    private readonly string _className;
    private SourceMethodSymbol? _entry;
    private SourceMethodSymbol? _userMain;

    public SourceModuleClassSymbol(Compilation compilation, SourceFile file, string className)
        : base(compilation, file, null, className, null, Accessibility.Public)
    {
        _className = className;
    }

    public override TypeKind TypeKind => TypeKind.Class;
    public override bool IsAbstract => true;
    public override bool IsSealed => true;
    public override bool IsStatic => true;
    public override bool IsStaticClass => true;
    public override NamedTypeSymbol? BaseType => Compilation.GetSpecialType(SpecialType.Object);

    /// <summary>The compiler-generated entry method holding top-level statements, if this file has any.</summary>
    public SourceMethodSymbol? TopLevelEntry
    {
        get { GetMembers(); return _entry; }
    }

    public SourceMethodSymbol? UserMain
    {
        get { GetMembers(); return _userMain; }
    }

    public ImmutableArray<(SourceFieldSymbol Field, VariableDeclaratorSyntax Declarator)> VariableInitializers { get; private set; } =
        ImmutableArray<(SourceFieldSymbol, VariableDeclaratorSyntax)>.Empty;

    private bool HasTopLevelAwait() => TopLevelAwaitScanner.Contains(File.Tree.Root);

    public bool HasExtensionMethods => GetMembers().OfType<MethodSymbol>().Any(m => m.IsExtensionMethod);

    protected override ImmutableArray<Symbol> BuildMembers()
    {
        var b = ImmutableArray.CreateBuilder<Symbol>();
        var vars = ImmutableArray.CreateBuilder<(SourceFieldSymbol, VariableDeclaratorSyntax)>();
        var hasStatements = false;

        foreach (var s in File.Tree.Root.Statements)
        {
            switch (s)
            {
                case FunctionDeclarationSyntax f when !f.Identifier.IsMissing:
                {
                    var mods = new ModifierSet(f.Modifiers);
                    var isMain = f.Identifier.Text == "main" && mods.IsExport == false || f.Identifier.Text == "main";
                    var m = new SourceMethodSymbol(Compilation, File, this, f.Identifier.Text, MethodKind.Ordinary, mods, f,
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
                        _userMain ??= m;
                    break;
                }
                case VariableStatementSyntax v:
                {
                    var mods = new ModifierSet(v.Modifiers);
                    var decl = v.Declaration;
                    if (decl.IsUsing)
                    {
                        hasStatements = true; // using at top level lives in the entry method
                        break;
                    }
                    foreach (var d in decl.Declarators)
                    {
                        if (d.Identifier is not { IsMissing: false } id)
                        {
                            hasStatements = true; // destructuring declarations at top level run in the entry method
                            continue;
                        }
                        var acc = mods.IsExport ? Accessibility.Public : Accessibility.Internal;
                        var dd = d;
                        var isConst = decl.IsConst;
                        var field = new SourceFieldSymbol(Compilation, File, this, id.Text, acc, true, false, false, d, null,
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
            var isAsyncEntry = HasTopLevelAwait();
            _entry = new SourceMethodSymbol(Compilation, File, this, "<Main>$", MethodKind.Ordinary, default, null, Accessibility.Private)
            {
                IsStaticFlag = true,
                ForceAsync = isAsyncEntry,
                ExplicitReturnType = Compilation.GetSpecialType(isAsyncEntry ? SpecialType.Task : SpecialType.Void),
                SynthesizedKind = SynthesizedBodyKind.Custom,
                ExplicitParameters = ImmutableArray.Create<ParameterSymbol>(
                    new ParameterSymbol("args", new ArrayTypeSymbol(Compilation.GetSpecialType(SpecialType.String), Compilation.GetSpecialType(SpecialType.Array)), 0, null)),
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
                ExplicitParameters = ImmutableArray<ParameterSymbol>.Empty,
            });
        }
        return b.ToImmutable();
    }
}

internal static class TopLevelAwaitScanner
{
    public static bool Contains(SyntaxNode node)
    {
        foreach (var c in node.ChildNodes())
        {
            switch (c)
            {
                case AwaitExpressionSyntax: return true;
                case FunctionDeclarationSyntax or ClassDeclarationSyntax or InterfaceDeclarationSyntax or ArrowFunctionSyntax or FunctionExpressionSyntax: continue;
                case ForOfStatementSyntax { AwaitKeyword: not null }: return true;
                case VariableDeclarationListSyntax { AwaitKeyword: not null }: return true;
            }
            if (Contains(c)) return true;
        }
        return false;
    }
}

internal static class SourceModuleVariables
{
    public static TypeSymbol BindType(SourceNamedType container, SourceFieldSymbol field, VariableDeclaratorSyntax d)
    {
        var c = container.Compilation;
        var binder = c.CreateBinder(container.File, container, c.DeclarationDiagnostics);
        if (d.TypeAnnotation != null) return binder.BindType(d.TypeAnnotation.Type);
        if (d.Initializer != null) return binder.InferVariableType(d.Initializer.Value);
        c.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.TypeAnnotationRequired, container.File.FilePath, d.Span));
        return ErrorTypeSymbol.Instance;
    }

    public static ConstantValue? EvaluateConstant(SourceNamedType container, SourceFieldSymbol field, VariableDeclaratorSyntax d)
    {
        if (d.Initializer == null) return null;
        var c = container.Compilation;
        var binder = c.CreateBinder(container.File, container, new DiagnosticBag());
        return binder.TryEvaluateConstant(d.Initializer.Value, field.Type);
    }
}
