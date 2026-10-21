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
        public readonly List<Symbol> Declared = new();
        public readonly HashSet<Symbol> Captured = new();
        public readonly List<Symbol> CapturedOrdered = new();
        public Env? Env;
    }

    /// <summary>A body that becomes its own method: the original method (Node == null) or one lambda.</summary>
    private sealed class Ctx
    {
        public BoundLambda? Node;
        public Ctx? Parent;
        public Scope? DefScope;
        public Scope RootScope = null!;
        public readonly HashSet<Symbol> FreeVars = new();
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
        public readonly Dictionary<Symbol, FieldSymbol> Fields = new();
        public Env? Parent;
        public FieldSymbol? ParentField;
        public bool NeedsThis;
        public FieldSymbol? ThisField;
    }

    private readonly Compilation _c;
    private readonly LoweredProgram _program;
    private readonly MethodSymbol _method;
    private readonly NamedTypeSymbol _container;
    private readonly int _methodOrdinal;
    private int _scopeCounter;
    private int _lambdaCounter;

    private readonly List<Scope> _scopes = new();
    private readonly List<Ctx> _ctxs = new();
    private readonly Dictionary<object, Scope> _scopeOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Symbol, Scope> _decl = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BoundLambda, Ctx> _lambdaCtx = new(ReferenceEqualityComparer.Instance);
    private Ctx _rootCtx = null!;
    private int _lambdaCount;

    private ClosureLowerer(Compilation c, LoweredProgram program, MethodSymbol method, NamedTypeSymbol container, int methodOrdinal)
    {
        _c = c;
        _program = program;
        _method = method;
        _container = container;
        _methodOrdinal = methodOrdinal;
    }

    public static BoundBlock Lower(Compilation c, LoweredProgram program, MethodSymbol method, NamedTypeSymbol container, int methodOrdinal, BoundBlock body)
    {
        var l = new ClosureLowerer(c, program, method, container, methodOrdinal);
        l.Analyze(body);
        if (l._lambdaCount == 0) return body;
        l.Setup();
        return l.RewriteBody(l._rootCtx, body);
    }

    // ------------------------------------------------------------------ analysis

    private Scope NewScope(Scope? lexical, Ctx owner)
    {
        var s = new Scope { Lexical = lexical, Depth = (lexical?.Depth ?? -1) + 1, Owner = owner };
        _scopes.Add(s);
        return s;
    }

    private void Declare(Symbol sym, Scope scope)
    {
        if (_decl.ContainsKey(sym)) return;
        _decl[sym] = scope;
        scope.Declared.Add(sym);
    }

    private void Analyze(BoundBlock body)
    {
        _rootCtx = new Ctx();
        _ctxs.Add(_rootCtx);
        var root = NewScope(null, _rootCtx);
        _rootCtx.RootScope = root;
        foreach (var p in _method.Parameters) Declare(p, root);
        new Analyzer(this, _rootCtx, root).Visit(body);
    }

    private sealed class Analyzer(ClosureLowerer o, Ctx ctx, Scope scope) : BoundTreeRewriter
    {
        private Ctx _ctx = ctx;
        private Scope _scope = scope;

        public override BoundNode? Visit(BoundNode? node)
        {
            if (node is BoundBaseReference) MarkThis();
            return base.Visit(node);
        }

        private Scope Push(IEnumerable<Symbol> locals, object key)
        {
            var s = o.NewScope(_scope, _ctx);
            foreach (var l in locals) o.Declare(l, s);
            o._scopeOf[key] = s;
            _scope = s;
            return s;
        }

        private void Pop() => _scope = _scope.Lexical!;

        private void MarkThis()
        {
            for (var c = _ctx; c != null; c = c.Parent) c.UsesThis = true;
        }

        private void Reference(Symbol sym)
        {
            if (!o._decl.TryGetValue(sym, out var declScope))
            {
                declScope = _ctx.RootScope;
                o.Declare(sym, declScope);
            }
            if (declScope.Owner == _ctx) return;
            if (declScope.Captured.Add(sym)) declScope.CapturedOrdered.Add(sym);
            for (var c = _ctx; c != null && c != declScope.Owner; c = c.Parent) c.FreeVars.Add(sym);
        }

        protected override BoundNode VisitBlock(BoundBlock n)
        {
            Push(n.Locals, n);
            var r = base.VisitBlock(n);
            Pop();
            return r;
        }

        protected override BoundNode VisitFor(BoundFor n)
        {
            Push(n.Locals, n);
            var r = base.VisitFor(n);
            Pop();
            return r;
        }

        protected override BoundNode VisitSequence(BoundSequence n)
        {
            Push(n.Locals, n);
            var r = base.VisitSequence(n);
            Pop();
            return r;
        }

        protected override BoundNode VisitTry(BoundTry n)
        {
            Visit(n.Body);
            foreach (var c in n.Catches)
            {
                Push(c.Local != null ? [c.Local] : [], c);
                Visit(c.Body);
                Pop();
            }
            if (n.Finally != null) Visit(n.Finally);
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
            if (o._lambdaCtx.ContainsKey(n)) return n;
            o._lambdaCount++;
            var ctx = new Ctx { Node = n, Parent = _ctx, DefScope = _scope };
            o._ctxs.Add(ctx);
            o._lambdaCtx[n] = ctx;
            var root = o.NewScope(_scope, ctx);
            ctx.RootScope = root;
            foreach (var p in n.Symbol.Parameters) o.Declare(p, root);
            var savedCtx = _ctx;
            var savedScope = _scope;
            _ctx = ctx;
            _scope = root;
            Visit(n.Body);
            _ctx = savedCtx;
            _scope = savedScope;
            return n;
        }
    }

    // ------------------------------------------------------------------ display classes and lambda methods

    private Compilation Comp => _c;

    private NamedTypeSymbol ContainerSelf => _container.IsGenericType ? _container.Construct(_container.TypeParameters.CastArray<TypeSymbol>()) : _container;

    private bool IsGenericMethod => _method.TypeParameters.Length > 0;

    private void Setup()
    {
        foreach (var s in _scopes.ToList())
            if (s.Captured.Count > 0) CreateEnv(s);
        if (IsGenericMethod && _rootCtx.RootScope.Env == null) CreateEnv(_rootCtx.RootScope);

        foreach (var ctx in _ctxs)
        {
            if (ctx.Node == null) continue;
            Scope? deepest = null;
            foreach (var v in ctx.FreeVars)
            {
                var s = _decl[v];
                if (deepest == null || s.Depth > deepest.Depth) deepest = s;
            }
            if (deepest != null) { ctx.Kind = HostKind.Env; ctx.Host = deepest.Env; }
            else if (IsGenericMethod) { ctx.Kind = HostKind.Env; ctx.Host = _rootCtx.RootScope.Env; }
            else ctx.Kind = ctx.UsesThis ? HostKind.ClassInstance : HostKind.Static;
        }

        foreach (var s in _scopes)
        {
            if (s.Env == null) continue;
            for (var a = s.Lexical; a != null && a.Owner == s.Owner; a = a.Lexical)
                if (a.Env != null) { s.Env.Parent = a.Env; break; }
            s.Env.Parent ??= s.Owner.Host;
        }

        foreach (var ctx in _ctxs)
            if (ctx.UsesThis && ctx.Host != null) Top(ctx.Host).NeedsThis = true;

        foreach (var s in _scopes)
            if (s.Env != null) CreateEnvMembers(s.Env);

        foreach (var ctx in _ctxs)
            if (ctx.Node != null) CreateLambdaMethod(ctx);
    }

    private static Env Top(Env e)
    {
        while (e.Parent != null) e = e.Parent;
        return e;
    }

    private void CreateEnv(Scope scope)
    {
        if (scope.Env != null) return;
        var objectType = _c.GetSpecialType(SpecialType.Object);
        var cls = new SynthesizedNamedType("<>c__DisplayClass" + _methodOrdinal + "_" + _scopeCounter++, _container, TypeKind.Class, Accessibility.Private, objectType);
        var classTps = _container.TypeParameters;
        var methodTps = _method.TypeParameters;
        NamedTypeSymbol closed = cls;
        if (classTps.Length + methodTps.Length > 0)
        {
            var map = new Dictionary<TypeParameterSymbol, int>();
            var tps = ImmutableArray.CreateBuilder<TypeParameterSymbol>();
            var args = ImmutableArray.CreateBuilder<TypeSymbol>();
            foreach (var tp in classTps)
            {
                tps.Add(CopyTypeParameter(tp, tp.Ordinal, cls));
                args.Add(tp);
            }
            foreach (var tp in methodTps)
            {
                var ordinal = classTps.Length + tp.Ordinal;
                tps.Add(CopyTypeParameter(tp, ordinal, cls));
                args.Add(tp);
                map[tp] = ordinal;
            }
            cls.SetTypeParameters(tps.ToImmutable());
            cls.OwnArityValue = methodTps.Length;
            cls.MethodTypeParameterMap = map;
            closed = cls.Construct(args.ToImmutable());
        }
        var env = new Env
        {
            Scope = scope,
            Class = cls,
            Closed = closed,
            Local = new LocalSymbol("CS$<>8__locals" + _scopeCounter, closed, false, _method) { IsSynthesized = true },
        };
        scope.Env = env;
        _program.Types.Add(cls);
    }

    private static TypeParameterSymbol CopyTypeParameter(TypeParameterSymbol tp, int ordinal, Symbol container) =>
        new(tp.Name, ordinal, container, VarianceKind.None, _ => new TypeParameterConstraints(
            tp.HasReferenceTypeConstraint, tp.HasValueTypeConstraint, tp.HasConstructorConstraint && !tp.HasValueTypeConstraint, tp.HasNotNullConstraint, tp.ConstraintTypes));

    private void CreateEnvMembers(Env env)
    {
        var cls = env.Class;
        var used = new HashSet<string>(StringComparer.Ordinal);
        string Unique(string n)
        {
            var name = n;
            for (var i = 1; !used.Add(name); i++) name = n + "_" + i;
            return name;
        }
        var voidType = _c.GetSpecialType(SpecialType.Void);
        cls.AddMember(new SynthesizedMethodSymbol(cls, ".ctor", MethodKind.Constructor, Accessibility.Public, false, voidType, ImmutableArray<ParameterSymbol>.Empty)
        { SynthesizedKind = SynthesizedBodyKind.Custom });
        foreach (var sym in env.Scope.CapturedOrdered)
        {
            var type = sym switch { LocalSymbol l => l.Type, ParameterSymbol p => p.Type, _ => throw new InvalidOperationException() };
            var f = new SynthesizedFieldSymbol(cls, Unique(sym.Name), type);
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
            foreach (var key in env.Fields.Keys.ToList()) env.Fields[key] = ClosedField(env, env.Fields[key]);
            if (env.ParentField != null) env.ParentField = ClosedField(env, env.ParentField);
            if (env.ThisField != null) env.ThisField = ClosedField(env, env.ThisField);
        }
    }

    private static FieldSymbol ClosedField(Env env, FieldSymbol def) =>
        env.Closed.GetMembers(def.Name).OfType<FieldSymbol>().First();

    private void CreateLambdaMethod(Ctx ctx)
    {
        var lambda = ctx.Node!.Symbol;
        var ordinal = _lambdaCounter++;
        var name = "<" + _method.Name + ">b__" + _methodOrdinal + "_" + ordinal;
        var hostType = ctx.Kind == HostKind.Env ? (NamedTypeSymbol)ctx.Host!.Class : _container;
        var isStatic = ctx.Kind == HostKind.Static;
        var acc = ctx.Kind == HostKind.Env ? Accessibility.Internal : Accessibility.Private;
        var m = new SynthesizedMethodSymbol(hostType, name, MethodKind.LambdaMethod, acc, isStatic, lambda.ReturnType, lambda.Parameters)
        {
            SynthesizedKind = SynthesizedBodyKind.Custom,
            SourceMethod = _method,
            LambdaOrigin = lambda,
            IsAsyncMethod = lambda.IsAsync,
            IsIteratorMethod = lambda.IsIterator,
        };
        ctx.Method = m;
        if (ctx.Kind == HostKind.Env) ctx.Host!.Class.AddMember(m);
        else
        {
            if (!_program.ExtraMembers.TryGetValue(_container, out var list)) _program.ExtraMembers[_container] = list = new List<Symbol>();
            list.Add(m);
        }
        ctx.MethodRef = ctx.Kind == HostKind.Env && !ReferenceEquals(ctx.Host!.Closed, ctx.Host.Class)
            ? ctx.Host.Closed.GetMembers(name).OfType<MethodSymbol>().First()
            : m;
        if (_program.MethodFiles.TryGetValue(_method, out var file)) _program.MethodFiles[m] = file;
    }

    // ------------------------------------------------------------------ rewriting

    private BoundBlock RewriteBody(Ctx ctx, BoundBlock body)
    {
        var rewriter = new Rewriter(this, ctx);
        return rewriter.RewriteCtxBody(ctx, body);
    }

    private sealed class Rewriter(ClosureLowerer o, Ctx ctx) : BoundTreeRewriter
    {
        private Ctx _ctx = ctx;

        public BoundBlock RewriteCtxBody(Ctx target, BoundBlock body)
        {
            var saved = _ctx;
            _ctx = target;
            var root = target.RootScope;
            var prelude = new List<BoundStatement>();
            var locals = new List<LocalSymbol>();
            if (root.Env != null)
            {
                locals.Add(root.Env.Local);
                prelude.AddRange(EnvPrelude(root.Env));
                foreach (var sym in root.CapturedOrdered)
                {
                    if (sym is not ParameterSymbol p) continue;
                    prelude.Add(Assign(new BoundFieldAccess(new BoundLocal(root.Env.Local), root.Env.Fields[sym]), new BoundParameter(p)));
                }
            }
            var rewritten = (BoundBlock)Visit(body)!;
            _ctx = saved;
            if (prelude.Count == 0) return rewritten;
            prelude.Add(rewritten);
            return new BoundBlock(locals.ToImmutableArray(), prelude.ToImmutableArray()) { Syntax = body.Syntax };
        }

        private static BoundStatement Assign(BoundExpression target, BoundExpression value) =>
            new BoundExpressionStatement(new BoundAssignment(target, value) { IsDiscarded = true });

        private IEnumerable<BoundStatement> EnvPrelude(Env env)
        {
            var ctor = env.Closed.InstanceConstructors.First();
            yield return Assign(new BoundLocal(env.Local), new BoundObjectCreation(ctor, env.Closed, ImmutableArray<BoundExpression>.Empty, ImmutableArray<BoundMemberInit>.Empty));
            if (env.ParentField != null)
                yield return Assign(new BoundFieldAccess(new BoundLocal(env.Local), env.ParentField), AccessEnv(env.Parent!));
            if (env.ThisField != null)
                yield return Assign(new BoundFieldAccess(new BoundLocal(env.Local), env.ThisField), ThisExpr());
        }

        /// <summary>The display class instance of <paramref name="target"/> as seen from the current body.</summary>
        private BoundExpression AccessEnv(Env target)
        {
            if (target.Scope.Owner == _ctx) return new BoundLocal(target.Local);
            var host = _ctx.Host ?? throw new InvalidOperationException("closure host missing");
            BoundExpression expr = new BoundThis(host.Closed);
            var e = host;
            while (!ReferenceEquals(e, target))
            {
                expr = new BoundFieldAccess(expr, e.ParentField ?? throw new InvalidOperationException("closure chain broken"));
                e = e.Parent!;
            }
            return expr;
        }

        private BoundExpression ThisExpr()
        {
            if (_ctx.Host == null) return new BoundThis(o.ContainerSelf);
            BoundExpression expr = new BoundThis(_ctx.Host.Closed);
            var e = _ctx.Host;
            while (e.Parent != null)
            {
                expr = new BoundFieldAccess(expr, e.ParentField!);
                e = e.Parent;
            }
            return new BoundFieldAccess(expr, e.ThisField ?? throw new InvalidOperationException("closure this missing"));
        }

        private bool IsCaptured(Symbol sym) => o._decl.TryGetValue(sym, out var s) && s.Captured.Contains(sym);

        private BoundExpression VarRef(Symbol sym)
        {
            var scope = o._decl[sym];
            return new BoundFieldAccess(AccessEnv(scope.Env!), scope.Env!.Fields[sym]);
        }

        private static ImmutableArray<LocalSymbol> Remaining(ImmutableArray<LocalSymbol> locals, Scope scope, Env? env)
        {
            var b = ImmutableArray.CreateBuilder<LocalSymbol>();
            foreach (var l in locals) if (!scope.Captured.Contains(l)) b.Add(l);
            if (env != null) b.Add(env.Local);
            return b.ToImmutable();
        }

        protected override BoundNode VisitLocal(BoundLocal n) => IsCaptured(n.Local) ? VarRef(n.Local) : n;
        protected override BoundNode VisitParameter(BoundParameter n) => IsCaptured(n.Parameter) ? VarRef(n.Parameter) : n;
        protected override BoundNode VisitThis(BoundThis n) => _ctx.Host != null ? ThisExpr() : n;

        protected override BoundNode VisitLocalDeclaration(BoundLocalDeclaration n)
        {
            if (!IsCaptured(n.Local)) return base.VisitLocalDeclaration(n);
            if (n.Initializer == null) return new BoundNoOp { Syntax = n.Syntax };
            return new BoundExpressionStatement(new BoundAssignment(VarRef(n.Local), VisitExpr(n.Initializer)) { Syntax = n.Syntax, IsDiscarded = true }) { Syntax = n.Syntax };
        }

        protected override BoundNode VisitBlock(BoundBlock n)
        {
            var stmts = VisitStmts(n.Statements);
            if (!o._scopeOf.TryGetValue(n, out var sc) || (sc.Captured.Count == 0 && sc.Env == null))
                return stmts == n.Statements ? n : new BoundBlock(n.Locals, stmts) { Syntax = n.Syntax };
            var all = new List<BoundStatement>();
            if (sc.Env != null) all.AddRange(EnvPrelude(sc.Env));
            all.AddRange(stmts);
            return new BoundBlock(Remaining(n.Locals, sc, sc.Env), all.ToImmutableArray()) { Syntax = n.Syntax };
        }

        protected override BoundNode VisitFor(BoundFor n)
        {
            var i = VisitStmtOpt(n.Initializer);
            var c = VisitExprOpt(n.Condition);
            var inc = VisitStmtOpt(n.Increment);
            var b = VisitStmt(n.Body);
            if (!o._scopeOf.TryGetValue(n, out var sc) || sc.Captured.Count == 0 && sc.Env == null)
                return new BoundFor(n.Locals, i, c, inc, b, n.BreakLabel, n.ContinueLabel) { Syntax = n.Syntax };
            var loop = new BoundFor(Remaining(n.Locals, sc, null), i, c, inc, b, n.BreakLabel, n.ContinueLabel) { Syntax = n.Syntax };
            if (sc.Env == null) return loop;
            var all = new List<BoundStatement>(EnvPrelude(sc.Env)) { loop };
            return new BoundBlock([sc.Env.Local], all.ToImmutableArray()) { Syntax = n.Syntax };
        }

        protected override BoundNode VisitSequence(BoundSequence n)
        {
            var effects = VisitStmts(n.SideEffects);
            var value = VisitExpr(n.Value);
            if (!o._scopeOf.TryGetValue(n, out var sc) || sc.Captured.Count == 0 && sc.Env == null)
                return effects == n.SideEffects && ReferenceEquals(value, n.Value) ? n : new BoundSequence(n.Locals, effects, value) { Syntax = n.Syntax };
            var all = new List<BoundStatement>();
            if (sc.Env != null) all.AddRange(EnvPrelude(sc.Env));
            all.AddRange(effects);
            return new BoundSequence(Remaining(n.Locals, sc, sc.Env), all.ToImmutableArray(), value) { Syntax = n.Syntax };
        }

        protected override BoundNode VisitTry(BoundTry n)
        {
            var body = (BoundBlock)Visit(n.Body)!;
            var catches = ImmutableArray.CreateBuilder<BoundCatch>(n.Catches.Length);
            foreach (var c in n.Catches)
            {
                var cb = (BoundBlock)Visit(c.Body)!;
                if (c.Local != null && o._scopeOf.TryGetValue(c, out var sc) && sc.Captured.Contains(c.Local))
                {
                    var temp = new LocalSymbol(c.Local.Name, c.Local.Type, false, c.Local.Container) { IsSynthesized = true };
                    var all = new List<BoundStatement>(EnvPrelude(sc.Env!))
                    {
                        Assign(new BoundFieldAccess(new BoundLocal(sc.Env!.Local), sc.Env.Fields[c.Local]), new BoundLocal(temp)),
                        cb,
                    };
                    catches.Add(new BoundCatch(temp, c.ExceptionType, new BoundBlock([sc.Env.Local], all.ToImmutableArray())));
                }
                else catches.Add(new BoundCatch(c.Local, c.ExceptionType, cb));
            }
            var fin = n.Finally == null ? null : (BoundBlock)Visit(n.Finally)!;
            return new BoundTry(body, catches.MoveToImmutable(), fin) { Syntax = n.Syntax };
        }

        protected override BoundNode VisitLambda(BoundLambda n)
        {
            var target = o._lambdaCtx[n];
            if (!target.Rewritten)
            {
                target.Rewritten = true;
                var body = RewriteCtxBody(target, n.Body);
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
