using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler.Emit;

/// <summary>
/// Closure conversion for one method body. Every lambda becomes a method; variables captured by lambdas move into
/// "display class" fields (one class per scope that declares captured variables, chained through parent fields).
/// A lambda is hosted on the display class of the innermost scope it captures from, on the containing type when it
/// only needs <c>this</c>, and as a static method when it captures nothing.
/// </summary>
internal sealed class ClosureLowerer
{
    private enum HostKind { Static, ClassInstance, Env }

    private sealed class Scope
    {
        public Scope? Lexical;
        public int Depth;
        public Ctx Owner = null!;
        public readonly List<Symbol> Declared = [];
        public readonly HashSet<Symbol> Captured = [];
        public readonly List<Symbol> CapturedOrdered = [];
        public Env? Env;
    }

    /// <summary>A body that becomes its own method: the original method (Node == null) or one lambda.</summary>
    private sealed class Ctx
    {
        public BoundLambda? Node;
        public Ctx? Parent;
        public Scope? DefScope;
        public Scope RootScope = null!;
        public readonly HashSet<Symbol> FreeVars = [];
        public bool UsesThis;
        public HostKind Kind;
        public Env? Host;
        public SynthesizedMethodSymbol? Method;
        public MethodSymbol? MethodRef;
        public bool Rewritten;
    }

    private sealed class Env
    {
        public Scope Scope = null!;
        public SynthesizedNamedType Class = null!;
        public NamedTypeSymbol Closed = null!;
        public LocalSymbol Local = null!;
        public readonly Dictionary<Symbol, FieldSymbol> Fields = [];
        public Env? Parent;
        public FieldSymbol? ParentField;
        public bool NeedsThis;
        public FieldSymbol? ThisField;
    }

    private readonly LoweredProgram _program;
    private readonly MethodSymbol _method;
    private readonly NamedTypeSymbol _container;
    private readonly int _methodOrdinal;
    private int _scopeCounter;
    private int _lambdaCounter;

    private readonly List<Scope> _scopes = [];
    private readonly List<Ctx> _ctxs = [];
    private readonly Dictionary<object, Scope> _scopeOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Symbol, Scope> _decl = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BoundLambda, Ctx> _lambdaCtx = new(ReferenceEqualityComparer.Instance);
    private Ctx _rootCtx = null!;
    private int _lambdaCount;

    private ClosureLowerer(Compilation c, LoweredProgram program, MethodSymbol method, NamedTypeSymbol container, int methodOrdinal)
    {
        Comp = c;
        _program = program;
        _method = method;
        _container = container;
        _methodOrdinal = methodOrdinal;
    }

    public static BoundBlock Lower(Compilation c, LoweredProgram program, MethodSymbol method, NamedTypeSymbol container, int methodOrdinal, BoundBlock body)
    {
        ClosureLowerer l = new(c, program, method, container, methodOrdinal);
        l.Analyze(body);
        if (l._lambdaCount == 0)
        {
            return body;
        }

        l.Setup();
        return l.RewriteBody(l._rootCtx, body);
    }

    // ------------------------------------------------------------------ analysis

    private Scope NewScope(Scope? lexical, Ctx owner)
    {
        Scope s = new() { Lexical = lexical, Depth = (lexical?.Depth ?? -1) + 1, Owner = owner };
        _scopes.Add(s);
        return s;
    }

    private void Declare(Symbol sym, Scope scope)
    {
        if (_decl.ContainsKey(sym))
        {
            return;
        }

        _decl[sym] = scope;
        scope.Declared.Add(sym);
    }

    private void Analyze(BoundBlock body)
    {
        _rootCtx = new Ctx();
        _ctxs.Add(_rootCtx);
        Scope root = NewScope(null, _rootCtx);
        _rootCtx.RootScope = root;
        foreach (ParameterSymbol p in _method.Parameters)
        {
            Declare(p, root);
        }

        _ = new Analyzer(this, _rootCtx, root).Visit(body);
    }

    private sealed class Analyzer(ClosureLowerer o, Ctx ctx, Scope scope) : BoundTreeRewriter
    {
        private Ctx _ctx = ctx;
        private Scope _scope = scope;

        public override BoundNode? Visit(BoundNode? node)
        {
            if (node is BoundBaseReference)
            {
                MarkThis();
            }

            return base.Visit(node);
        }

        private Scope Push(IEnumerable<Symbol> locals, object key)
        {
            Scope s = o.NewScope(_scope, _ctx);
            foreach (Symbol l in locals)
            {
                o.Declare(l, s);
            }

            o._scopeOf[key] = s;
            _scope = s;
            return s;
        }

        private void Pop()
        {
            _scope = _scope.Lexical!;
        }

        private void MarkThis()
        {
            for (Ctx? c = _ctx; c != null; c = c.Parent)
            {
                c.UsesThis = true;
            }
        }

        private void Reference(Symbol sym)
        {
            if (!o._decl.TryGetValue(sym, out Scope? declScope))
            {
                declScope = _ctx.RootScope;
                o.Declare(sym, declScope);
            }
            if (declScope.Owner == _ctx)
            {
                return;
            }

            if (declScope.Captured.Add(sym))
            {
                declScope.CapturedOrdered.Add(sym);
            }

            for (Ctx? c = _ctx; c != null && c != declScope.Owner; c = c.Parent)
            {
                _ = c.FreeVars.Add(sym);
            }
        }

        protected override BoundNode VisitBlock(BoundBlock n)
        {
            _ = Push(n.Locals, n);
            BoundNode r = base.VisitBlock(n);
            Pop();
            return r;
        }

        protected override BoundNode VisitFor(BoundFor n)
        {
            _ = Push(n.Locals, n);
            BoundNode r = base.VisitFor(n);
            Pop();
            return r;
        }

        protected override BoundNode VisitSequence(BoundSequence n)
        {
            _ = Push(n.Locals, n);
            BoundNode r = base.VisitSequence(n);
            Pop();
            return r;
        }

        protected override BoundNode VisitTry(BoundTry n)
        {
            _ = Visit(n.Body);
            foreach (BoundCatch c in n.Catches)
            {
                _ = Push(c.Local != null ? [c.Local] : [], c);
                _ = Visit(c.Body);
                Pop();
            }
            if (n.Finally != null)
            {
                _ = Visit(n.Finally);
            }

            return n;
        }

        protected override BoundNode VisitLocalDeclaration(BoundLocalDeclaration n)
        {
            o.Declare(n.Local, _scope);
            return base.VisitLocalDeclaration(n);
        }

        protected override BoundNode VisitLocal(BoundLocal n)
        {
            Reference(n.Local);
            return n;
        }

        protected override BoundNode VisitParameter(BoundParameter n)
        {
            Reference(n.Parameter);
            return n;
        }

        protected override BoundNode VisitThis(BoundThis n)
        {
            MarkThis();
            return n;
        }

        protected override BoundNode VisitLambda(BoundLambda n)
        {
            if (o._lambdaCtx.ContainsKey(n))
            {
                return n;
            }

            o._lambdaCount++;
            Ctx ctx = new() { Node = n, Parent = _ctx, DefScope = _scope };
            o._ctxs.Add(ctx);
            o._lambdaCtx[n] = ctx;
            Scope root = o.NewScope(_scope, ctx);
            ctx.RootScope = root;
            foreach (ParameterSymbol p in n.Symbol.Parameters)
            {
                o.Declare(p, root);
            }

            Ctx savedCtx = _ctx;
            Scope savedScope = _scope;
            _ctx = ctx;
            _scope = root;
            _ = Visit(n.Body);
            _ctx = savedCtx;
            _scope = savedScope;
            return n;
        }
    }

    // ------------------------------------------------------------------ display classes and lambda methods

    private Compilation Comp { get; }

    private NamedTypeSymbol ContainerSelf => _container.IsGenericType ? _container.Construct(_container.TypeParameters.CastArray<TypeSymbol>()) : _container;

    private bool IsGenericMethod => _method.TypeParameters.Length > 0;

    private void Setup()
    {
        foreach (Scope? s in _scopes.ToList())
        {
            if (s.Captured.Count > 0)
            {
                CreateEnv(s);
            }
        }

        if (IsGenericMethod && _rootCtx.RootScope.Env == null)
        {
            CreateEnv(_rootCtx.RootScope);
        }

        foreach (Ctx ctx in _ctxs)
        {
            if (ctx.Node == null)
            {
                continue;
            }

            Scope? deepest = null;
            foreach (Symbol v in ctx.FreeVars)
            {
                Scope s = _decl[v];
                if (deepest == null || s.Depth > deepest.Depth)
                {
                    deepest = s;
                }
            }
            if (deepest != null) { ctx.Kind = HostKind.Env; ctx.Host = deepest.Env; }
            else if (IsGenericMethod) { ctx.Kind = HostKind.Env; ctx.Host = _rootCtx.RootScope.Env; }
            else
            {
                ctx.Kind = ctx.UsesThis ? HostKind.ClassInstance : HostKind.Static;
            }
        }

        foreach (Scope s in _scopes)
        {
            if (s.Env == null)
            {
                continue;
            }

            for (Scope? a = s.Lexical; a != null && a.Owner == s.Owner; a = a.Lexical)
            {
                if (a.Env != null) { s.Env.Parent = a.Env; break; }
            }

            s.Env.Parent ??= s.Owner.Host;
        }

        foreach (Ctx ctx in _ctxs)
        {
            if (ctx.UsesThis && ctx.Host != null)
            {
                Top(ctx.Host).NeedsThis = true;
            }
        }

        foreach (Scope s in _scopes)
        {
            if (s.Env != null)
            {
                CreateEnvMembers(s.Env);
            }
        }

        foreach (Ctx ctx in _ctxs)
        {
            if (ctx.Node != null)
            {
                CreateLambdaMethod(ctx);
            }
        }
    }

    private static Env Top(Env e)
    {
        while (e.Parent != null)
        {
            e = e.Parent;
        }

        return e;
    }

    private void CreateEnv(Scope scope)
    {
        if (scope.Env != null)
        {
            return;
        }

        SynthesizedNamedType cls = SynthesizedClassFactory.Create(Comp, _container, _method, "<>c__DisplayClass" + _methodOrdinal + "_" + _scopeCounter++, null, out NamedTypeSymbol? closed);
        Env env = new()
        {
            Scope = scope,
            Class = cls,
            Closed = closed,
            Local = new LocalSymbol("CS$<>8__locals" + _scopeCounter, closed, false, _method) { IsSynthesized = true },
        };
        scope.Env = env;
        _program.Types.Add(cls);
    }

    private void CreateEnvMembers(Env env)
    {
        SynthesizedNamedType cls = env.Class;
        HashSet<string> used = new(StringComparer.Ordinal);
        string Unique(string n)
        {
            string name = n;
            for (int i = 1; !used.Add(name); i++)
            {
                name = n + "_" + i;
            }

            return name;
        }
        NamedTypeSymbol voidType = Comp.GetSpecialType(SpecialType.Void);
        cls.AddMember(new SynthesizedMethodSymbol(cls, ".ctor", MethodKind.Constructor, Accessibility.Public, false, voidType, [])
        { SynthesizedKind = SynthesizedBodyKind.Custom });
        foreach (Symbol sym in env.Scope.CapturedOrdered)
        {
            TypeSymbol type = sym switch { LocalSymbol l => l.Type, ParameterSymbol p => p.Type, _ => throw new InvalidOperationException() };
            SynthesizedFieldSymbol f = new(cls, Unique(sym.Name), type);
            cls.AddMember(f);
            env.Fields[sym] = f;
        }
        if (env.Parent != null)
        {
            env.ParentField = new SynthesizedFieldSymbol(cls, Unique("<>4__parent"), env.Parent.Closed);
            cls.AddMember(env.ParentField);
        }
        if (env.NeedsThis && env.Parent == null)
        {
            env.ThisField = new SynthesizedFieldSymbol(cls, Unique("<>4__this"), ContainerSelf);
            cls.AddMember(env.ThisField);
        }
        if (!ReferenceEquals(env.Closed, cls))
        {
            // fields are accessed through the instantiated type
            foreach (Symbol? key in env.Fields.Keys.ToList())
            {
                env.Fields[key] = ClosedField(env, env.Fields[key]);
            }

            if (env.ParentField != null)
            {
                env.ParentField = ClosedField(env, env.ParentField);
            }

            if (env.ThisField != null)
            {
                env.ThisField = ClosedField(env, env.ThisField);
            }
        }
    }

    private static FieldSymbol ClosedField(Env env, FieldSymbol def)
    {
        return env.Closed.GetMembers(def.Name).OfType<FieldSymbol>().First();
    }

    private void CreateLambdaMethod(Ctx ctx)
    {
        LambdaSymbol lambda = ctx.Node!.Symbol;
        int ordinal = _lambdaCounter++;
        string name = "<" + _method.Name + ">b__" + _methodOrdinal + "_" + ordinal;
        NamedTypeSymbol hostType = ctx.Kind == HostKind.Env ? ctx.Host!.Class : _container;
        bool isStatic = ctx.Kind == HostKind.Static;
        Accessibility acc = ctx.Kind == HostKind.Env ? Accessibility.Internal : Accessibility.Private;
        SynthesizedMethodSymbol m = new(hostType, name, MethodKind.LambdaMethod, acc, isStatic, lambda.ReturnType, lambda.Parameters)
        {
            SynthesizedKind = SynthesizedBodyKind.Custom,
            SourceMethod = _method,
            LambdaOrigin = lambda,
            IsAsyncMethod = lambda.IsAsync,
            IsIteratorMethod = lambda.IsIterator,
        };
        ctx.Method = m;
        if (ctx.Kind == HostKind.Env)
        {
            ctx.Host!.Class.AddMember(m);
        }
        else
        {
            if (!_program.ExtraMembers.TryGetValue(_container, out List<Symbol>? list))
            {
                _program.ExtraMembers[_container] = list = [];
            }

            list.Add(m);
        }
        ctx.MethodRef = ctx.Kind == HostKind.Env && !ReferenceEquals(ctx.Host!.Closed, ctx.Host.Class)
            ? ctx.Host.Closed.GetMembers(name).OfType<MethodSymbol>().First()
            : m;
        if (_program.MethodFiles.TryGetValue(_method, out SourceFile? file))
        {
            _program.MethodFiles[m] = file;
        }
    }

    // ------------------------------------------------------------------ rewriting

    private BoundBlock RewriteBody(Ctx ctx, BoundBlock body)
    {
        Rewriter rewriter = new(this, ctx);
        return rewriter.RewriteCtxBody(ctx, body);
    }

    private sealed class Rewriter(ClosureLowerer o, Ctx ctx) : BoundTreeRewriter
    {
        private Ctx _ctx = ctx;

        public BoundBlock RewriteCtxBody(Ctx target, BoundBlock body)
        {
            Ctx saved = _ctx;
            _ctx = target;
            Scope root = target.RootScope;
            List<BoundStatement> prelude = [];
            List<LocalSymbol> locals = [];
            if (root.Env != null)
            {
                locals.Add(root.Env.Local);
                prelude.AddRange(EnvPrelude(root.Env));
                foreach (Symbol sym in root.CapturedOrdered)
                {
                    if (sym is not ParameterSymbol p)
                    {
                        continue;
                    }

                    prelude.Add(Assign(new BoundFieldAccess(new BoundLocal(root.Env.Local), root.Env.Fields[sym]), new BoundParameter(p)));
                }
            }
            BoundBlock rewritten = (BoundBlock)Visit(body)!;
            _ctx = saved;
            if (prelude.Count == 0)
            {
                return rewritten;
            }

            prelude.Add(rewritten);
            return new BoundBlock([.. locals], [.. prelude]) { Syntax = body.Syntax };
        }

        private static BoundExpressionStatement Assign(BoundExpression target, BoundExpression value)
        {
            return new BoundExpressionStatement(new BoundAssignment(target, value) { IsDiscarded = true });
        }

        private IEnumerable<BoundStatement> EnvPrelude(Env env)
        {
            MethodSymbol ctor = env.Closed.InstanceConstructors.First();
            yield return Assign(new BoundLocal(env.Local), new BoundObjectCreation(ctor, env.Closed, [], []));
            if (env.ParentField != null)
            {
                yield return Assign(new BoundFieldAccess(new BoundLocal(env.Local), env.ParentField), AccessEnv(env.Parent!));
            }

            if (env.ThisField != null)
            {
                yield return Assign(new BoundFieldAccess(new BoundLocal(env.Local), env.ThisField), ThisExpr());
            }
        }

        /// <summary>The display class instance of <paramref name="target"/> as seen from the current body.</summary>
        private BoundExpression AccessEnv(Env target)
        {
            if (target.Scope.Owner == _ctx)
            {
                return new BoundLocal(target.Local);
            }

            Env host = _ctx.Host ?? throw new InvalidOperationException("closure host missing");
            BoundExpression expr = new BoundThis(host.Closed);
            Env e = host;
            while (!ReferenceEquals(e, target))
            {
                expr = new BoundFieldAccess(expr, e.ParentField ?? throw new InvalidOperationException("closure chain broken"));
                e = e.Parent!;
            }
            return expr;
        }

        private BoundExpression ThisExpr()
        {
            if (_ctx.Host == null)
            {
                return new BoundThis(o.ContainerSelf);
            }

            BoundExpression expr = new BoundThis(_ctx.Host.Closed);
            Env e = _ctx.Host;
            while (e.Parent != null)
            {
                expr = new BoundFieldAccess(expr, e.ParentField!);
                e = e.Parent;
            }
            return new BoundFieldAccess(expr, e.ThisField ?? throw new InvalidOperationException("closure this missing"));
        }

        private bool IsCaptured(Symbol sym)
        {
            return o._decl.TryGetValue(sym, out Scope? s) && s.Captured.Contains(sym);
        }

        private BoundFieldAccess VarRef(Symbol sym)
        {
            Scope scope = o._decl[sym];
            return new BoundFieldAccess(AccessEnv(scope.Env!), scope.Env!.Fields[sym]);
        }

        private static ImmutableArray<LocalSymbol> Remaining(ImmutableArray<LocalSymbol> locals, Scope scope, Env? env)
        {
            ImmutableArray<LocalSymbol>.Builder b = ImmutableArray.CreateBuilder<LocalSymbol>();
            foreach (LocalSymbol l in locals)
            {
                if (!scope.Captured.Contains(l))
                {
                    b.Add(l);
                }
            }

            if (env != null)
            {
                b.Add(env.Local);
            }

            return b.ToImmutable();
        }

        protected override BoundNode VisitLocal(BoundLocal n)
        {
            return IsCaptured(n.Local) ? VarRef(n.Local) : n;
        }

        protected override BoundNode VisitParameter(BoundParameter n)
        {
            return IsCaptured(n.Parameter) ? VarRef(n.Parameter) : n;
        }

        protected override BoundNode VisitThis(BoundThis n)
        {
            return _ctx.Host != null ? ThisExpr() : n;
        }

        protected override BoundNode VisitLocalDeclaration(BoundLocalDeclaration n)
        {
            return !IsCaptured(n.Local)
                ? base.VisitLocalDeclaration(n)
                : n.Initializer == null
                ? new BoundNoOp { Syntax = n.Syntax }
                : new BoundExpressionStatement(new BoundAssignment(VarRef(n.Local), VisitExpr(n.Initializer)) { Syntax = n.Syntax, IsDiscarded = true }) { Syntax = n.Syntax };
        }

        protected override BoundNode VisitBlock(BoundBlock n)
        {
            ImmutableArray<BoundStatement> stmts = VisitStmts(n.Statements);
            if (!o._scopeOf.TryGetValue(n, out Scope? sc) || (sc.Captured.Count == 0 && sc.Env == null))
            {
                return stmts == n.Statements ? n : new BoundBlock(n.Locals, stmts) { Syntax = n.Syntax };
            }

            List<BoundStatement> all = [];
            if (sc.Env != null)
            {
                all.AddRange(EnvPrelude(sc.Env));
            }

            all.AddRange(stmts);
            return new BoundBlock(Remaining(n.Locals, sc, sc.Env), [.. all]) { Syntax = n.Syntax };
        }

        protected override BoundNode VisitFor(BoundFor n)
        {
            BoundStatement? i = VisitStmtOpt(n.Initializer);
            BoundExpression? c = VisitExprOpt(n.Condition);
            BoundStatement? inc = VisitStmtOpt(n.Increment);
            BoundStatement b = VisitStmt(n.Body);
            if (!o._scopeOf.TryGetValue(n, out Scope? sc) || (sc.Captured.Count == 0 && sc.Env == null))
            {
                return new BoundFor(n.Locals, i, c, inc, b, n.BreakLabel, n.ContinueLabel) { Syntax = n.Syntax };
            }

            BoundFor loop = new(Remaining(n.Locals, sc, null), i, c, inc, b, n.BreakLabel, n.ContinueLabel) { Syntax = n.Syntax };
            if (sc.Env == null)
            {
                return loop;
            }

            List<BoundStatement> all = [.. EnvPrelude(sc.Env), loop];
            return new BoundBlock([sc.Env.Local], [.. all]) { Syntax = n.Syntax };
        }

        protected override BoundNode VisitSequence(BoundSequence n)
        {
            ImmutableArray<BoundStatement> effects = VisitStmts(n.SideEffects);
            BoundExpression value = VisitExpr(n.Value);
            if (!o._scopeOf.TryGetValue(n, out Scope? sc) || (sc.Captured.Count == 0 && sc.Env == null))
            {
                return effects == n.SideEffects && ReferenceEquals(value, n.Value) ? n : new BoundSequence(n.Locals, effects, value) { Syntax = n.Syntax };
            }

            List<BoundStatement> all = [];
            if (sc.Env != null)
            {
                all.AddRange(EnvPrelude(sc.Env));
            }

            all.AddRange(effects);
            return new BoundSequence(Remaining(n.Locals, sc, sc.Env), [.. all], value) { Syntax = n.Syntax };
        }

        protected override BoundNode VisitTry(BoundTry n)
        {
            BoundBlock body = (BoundBlock)Visit(n.Body)!;
            ImmutableArray<BoundCatch>.Builder catches = ImmutableArray.CreateBuilder<BoundCatch>(n.Catches.Length);
            foreach (BoundCatch c in n.Catches)
            {
                BoundBlock cb = (BoundBlock)Visit(c.Body)!;
                if (c.Local != null && o._scopeOf.TryGetValue(c, out Scope? sc) && sc.Captured.Contains(c.Local))
                {
                    LocalSymbol temp = new(c.Local.Name, c.Local.Type, false, c.Local.Container) { IsSynthesized = true };
                    List<BoundStatement> all =
                    [
                        .. EnvPrelude(sc.Env!),
                        Assign(new BoundFieldAccess(new BoundLocal(sc.Env!.Local), sc.Env.Fields[c.Local]), new BoundLocal(temp)),
                        cb,
                    ];
                    catches.Add(new BoundCatch(temp, c.ExceptionType, new BoundBlock([sc.Env.Local], [.. all])));
                }
                else
                {
                    catches.Add(new BoundCatch(c.Local, c.ExceptionType, cb));
                }
            }
            BoundBlock? fin = n.Finally == null ? null : (BoundBlock)Visit(n.Finally)!;
            return new BoundTry(body, catches.MoveToImmutable(), fin) { Syntax = n.Syntax };
        }

        protected override BoundNode VisitLambda(BoundLambda n)
        {
            Ctx target = o._lambdaCtx[n];
            if (!target.Rewritten)
            {
                target.Rewritten = true;
                BoundBlock body = RewriteCtxBody(target, n.Body);
                o._program.Bodies[target.Method!] = body;
            }
            BoundExpression? receiver = target.Kind switch
            {
                HostKind.Static => null,
                HostKind.ClassInstance => ThisExpr(),
                _ => AccessEnv(target.Host!),
            };
            return new BoundDelegateCreation(receiver, target.MethodRef!, n.Type!) { Syntax = n.Syntax };
        }
    }
}
