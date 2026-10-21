using System.Collections.Immutable;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Bound;

public abstract class BoundNode
{
    public SyntaxNode? Syntax { get; init; }
}

// =============================================================================== expressions

public abstract class BoundExpression : BoundNode
{
    protected BoundExpression(TypeSymbol? type) => Type = type;
    /// <summary>Static type; null for expressions that need a target type (lambdas, object literals).</summary>
    public TypeSymbol? Type { get; }
    public virtual ConstantValue? ConstantValue => null;
    public bool HasErrors => Type?.IsErrorType == true || this is BoundBadExpression;
}

public sealed class BoundBadExpression(ImmutableArray<BoundNode> children, TypeSymbol? type = null) : BoundExpression(type ?? ErrorTypeSymbol.Instance)
{
    public ImmutableArray<BoundNode> Children { get; } = children;
}

public sealed class BoundLiteral(ConstantValue value, TypeSymbol type) : BoundExpression(type)
{
    public ConstantValue Value { get; } = value;
    public override ConstantValue? ConstantValue => Value;
}

public sealed class BoundLocal(LocalSymbol local) : BoundExpression(local.Type)
{
    public LocalSymbol Local { get; } = local;
    public override ConstantValue? ConstantValue => Local.IsConst ? Local.ConstantValue : null;
}

public sealed class BoundParameter(ParameterSymbol parameter) : BoundExpression(parameter.Type)
{
    public ParameterSymbol Parameter { get; } = parameter;
}

public sealed class BoundThis(TypeSymbol type) : BoundExpression(type);

public sealed class BoundBaseReference(TypeSymbol type) : BoundExpression(type);

public sealed class BoundFieldAccess(BoundExpression? receiver, FieldSymbol field) : BoundExpression(field.Type)
{
    public BoundExpression? Receiver { get; } = receiver;
    public FieldSymbol Field { get; } = field;
    public override ConstantValue? ConstantValue => Field.IsConst ? Field.ConstantValue : null;
}

public sealed class BoundPropertyAccess(BoundExpression? receiver, PropertySymbol property, ImmutableArray<BoundExpression> arguments)
    : BoundExpression(property.Type)
{
    public BoundExpression? Receiver { get; } = receiver;
    public PropertySymbol Property { get; } = property;
    public ImmutableArray<BoundExpression> Arguments { get; } = arguments;
    public bool IsBaseAccess { get; init; }
}

public sealed class BoundCall(BoundExpression? receiver, MethodSymbol method, ImmutableArray<BoundExpression> arguments, TypeSymbol? type = null)
    : BoundExpression(type ?? method.ReturnType)
{
    public BoundExpression? Receiver { get; } = receiver;
    public MethodSymbol Method { get; } = method;
    public ImmutableArray<BoundExpression> Arguments { get; } = arguments;
    public bool IsBaseCall { get; init; }
}

public sealed class BoundMemberInit(Symbol member, BoundExpression value)
{
    public Symbol Member { get; } = member;
    public BoundExpression Value { get; } = value;
}

public sealed class BoundObjectCreation(MethodSymbol? constructor, TypeSymbol type, ImmutableArray<BoundExpression> arguments,
    ImmutableArray<BoundMemberInit> initializers) : BoundExpression(type)
{
    public MethodSymbol? Constructor { get; } = constructor;
    public ImmutableArray<BoundExpression> Arguments { get; } = arguments;
    public ImmutableArray<BoundMemberInit> Initializers { get; } = initializers;
}

public sealed class BoundDefaultValue(TypeSymbol type) : BoundExpression(type);

public sealed class BoundArrayCreation(ArrayTypeSymbol type, BoundExpression? length, ImmutableArray<BoundExpression> initializers)
    : BoundExpression(type)
{
    public new ArrayTypeSymbol Type => (ArrayTypeSymbol)base.Type!;
    public BoundExpression? Length { get; } = length;
    /// <summary>Non-default when the array is created from an element list.</summary>
    public ImmutableArray<BoundExpression> Initializers { get; } = initializers;
}

public sealed class BoundArrayAccess(BoundExpression array, BoundExpression index, TypeSymbol elementType) : BoundExpression(elementType)
{
    public BoundExpression Array { get; } = array;
    public BoundExpression Index { get; } = index;
}

public enum ConversionKind
{
    Identity,
    ImplicitNumeric,
    ExplicitNumeric,
    /// <summary>T to T | null (value types wrap in Nullable&lt;T&gt;; reference types are unchanged).</summary>
    ToNullable,
    /// <summary>T | null to T (assertion/narrowing; throws for null value types).</summary>
    FromNullable,
    Boxing,
    Unboxing,
    ImplicitReference,
    ExplicitReference,
    UserDefined,
    NullLiteral,
    /// <summary>Wrap a value as a union case (calls the union constructor).</summary>
    UnionWrap,
    /// <summary>Extract a case from a union value (narrowing).</summary>
    UnionExtract,
    /// <summary>Nullable&lt;S&gt; to Nullable&lt;T&gt; and similar lifted conversions.</summary>
    LiftedNumeric,
    EnumNumeric,
    /// <summary>Compile-time-only conversion that changes the static type without code (e.g. narrowing of erased nullable refs).</summary>
    TypeOnly,
    /// <summary>Type parameter to/from a constraint or object.</summary>
    TypeParameter,
}

public sealed class BoundConversion(BoundExpression operand, ConversionKind kind, TypeSymbol type, MethodSymbol? method = null)
    : BoundExpression(type)
{
    public BoundExpression Operand { get; } = operand;
    public ConversionKind Kind { get; } = kind;
    public MethodSymbol? Method { get; } = method;
    /// <summary>For union conversions: the index of the case (0-based).</summary>
    public int CaseIndex { get; init; } = -1;
    public override ConstantValue? ConstantValue => ConstantFolded;
    public ConstantValue? ConstantFolded { get; init; }
}

public enum UnaryOperatorKind { Plus, Minus, Not, BitwiseNot, UserDefined }

public sealed class BoundUnary(UnaryOperatorKind kind, BoundExpression operand, TypeSymbol type, MethodSymbol? method = null) : BoundExpression(type)
{
    public UnaryOperatorKind Kind { get; } = kind;
    public BoundExpression Operand { get; } = operand;
    public MethodSymbol? Method { get; } = method;
    public ConstantValue? Folded { get; init; }
    public override ConstantValue? ConstantValue => Folded;
}

public enum BinaryOperatorKind
{
    Add, Subtract, Multiply, Divide, Remainder, ShiftLeft, ShiftRight, UnsignedShiftRight, And, Or, Xor,
    Equal, NotEqual, LessThan, LessThanOrEqual, GreaterThan, GreaterThanOrEqual,
    /// <summary>Operator implemented by a method (decimal, BigInteger, string concat helpers, user types).</summary>
    UserDefined,
}

public sealed class BoundBinary(BinaryOperatorKind kind, BoundExpression left, BoundExpression right, TypeSymbol type,
    TypeSymbol operandType, MethodSymbol? method = null) : BoundExpression(type)
{
    public BinaryOperatorKind Kind { get; } = kind;
    public BoundExpression Left { get; } = left;
    public BoundExpression Right { get; } = right;
    public TypeSymbol OperandType { get; } = operandType;
    public MethodSymbol? Method { get; } = method;
    public ConstantValue? Folded { get; init; }
    public override ConstantValue? ConstantValue => Folded;
}

public sealed class BoundLogicalBinary(bool isAnd, BoundExpression left, BoundExpression right, TypeSymbol type) : BoundExpression(type)
{
    public bool IsAnd { get; } = isAnd;
    public BoundExpression Left { get; } = left;
    public BoundExpression Right { get; } = right;
    public ConstantValue? Folded { get; init; }
    public override ConstantValue? ConstantValue => Folded;
}

public sealed class BoundAssignment(BoundExpression target, BoundExpression value) : BoundExpression(target.Type)
{
    public BoundExpression Target { get; } = target;
    public BoundExpression Value { get; } = value;
    /// <summary>True when the value of the assignment expression is not consumed.</summary>
    public bool IsDiscarded { get; init; }
}

public sealed class BoundConditional(BoundExpression condition, BoundExpression whenTrue, BoundExpression whenFalse, TypeSymbol type) : BoundExpression(type)
{
    public BoundExpression Condition { get; } = condition;
    public BoundExpression WhenTrue { get; } = whenTrue;
    public BoundExpression WhenFalse { get; } = whenFalse;
}

public sealed class BoundCoalesce(BoundExpression left, BoundExpression right, TypeSymbol type) : BoundExpression(type)
{
    public BoundExpression Left { get; } = left;
    public BoundExpression Right { get; } = right;
}

public sealed class BoundConditionalReceiver(TypeSymbol type) : BoundExpression(type);

public sealed class BoundConditionalAccess(BoundExpression receiver, BoundConditionalReceiver placeholder, BoundExpression whenNotNull, TypeSymbol? type)
    : BoundExpression(type)
{
    public BoundExpression Receiver { get; } = receiver;
    public BoundConditionalReceiver Placeholder { get; } = placeholder;
    public BoundExpression WhenNotNull { get; } = whenNotNull;
}

public enum TypeTestKind { Reference, UnionCase }

public sealed class BoundTypeTest(BoundExpression operand, TypeSymbol target, TypeSymbol boolType, TypeTestKind kind = TypeTestKind.Reference, int caseIndex = -1)
    : BoundExpression(boolType)
{
    public BoundExpression Operand { get; } = operand;
    public TypeSymbol Target { get; } = target;
    public TypeTestKind Kind { get; } = kind;
    public int CaseIndex { get; } = caseIndex;
}

public sealed class BoundLambda(LambdaSymbol symbol, BoundBlock body, TypeSymbol delegateType) : BoundExpression(delegateType)
{
    public LambdaSymbol Symbol { get; } = symbol;
    public BoundBlock Body { get; } = body;
}

public sealed class BoundDelegateCreation(BoundExpression? receiver, MethodSymbol method, TypeSymbol delegateType) : BoundExpression(delegateType)
{
    public BoundExpression? Receiver { get; } = receiver;
    public MethodSymbol Method { get; } = method;
}

public sealed class BoundSequence(ImmutableArray<LocalSymbol> locals, ImmutableArray<BoundStatement> sideEffects, BoundExpression value)
    : BoundExpression(value.Type)
{
    public ImmutableArray<LocalSymbol> Locals { get; } = locals;
    public ImmutableArray<BoundStatement> SideEffects { get; } = sideEffects;
    public BoundExpression Value { get; } = value;
}

public sealed class BoundTypeOf(TypeSymbol operand, TypeSymbol systemType) : BoundExpression(systemType)
{
    public TypeSymbol Operand { get; } = operand;
}

public sealed class AwaitInfo(MethodSymbol getAwaiter, PropertySymbol? isCompleted, MethodSymbol? isCompletedGetter, MethodSymbol getResult, TypeSymbol awaiterType)
{
    public MethodSymbol GetAwaiter { get; } = getAwaiter;
    public PropertySymbol? IsCompleted { get; } = isCompleted;
    public MethodSymbol? IsCompletedGetter { get; } = isCompletedGetter;
    public MethodSymbol GetResult { get; } = getResult;
    public TypeSymbol AwaiterType { get; } = awaiterType;
}

public sealed class BoundAwait(BoundExpression operand, AwaitInfo info, TypeSymbol type) : BoundExpression(type)
{
    public BoundExpression Operand { get; } = operand;
    public AwaitInfo Info { get; } = info;
}

public sealed class BoundThrowExpression(BoundExpression operand, TypeSymbol type) : BoundExpression(type)
{
    public BoundExpression Operand { get; } = operand;
}

// ---- intermediate (non-value) expressions produced while binding names

public sealed class BoundTypeExpression(TypeSymbol type) : BoundExpression(type)
{
    public TypeSymbol TypeSym => Type!;
}

public sealed class BoundNamespaceExpression(NamespaceSymbol ns) : BoundExpression(null)
{
    public NamespaceSymbol Namespace { get; } = ns;
}

public sealed class BoundModuleExpression(SourceFile file) : BoundExpression(null)
{
    public SourceFile File { get; } = file;
}

public sealed class BoundMethodGroup(BoundExpression? receiver, string name, ImmutableArray<MethodSymbol> candidates,
    ImmutableArray<TypeSymbol> typeArguments, bool receiverIsExtension = false) : BoundExpression(null)
{
    public BoundExpression? Receiver { get; } = receiver;
    public string Name { get; } = name;
    public ImmutableArray<MethodSymbol> Candidates { get; } = candidates;
    public ImmutableArray<TypeSymbol> TypeArguments { get; } = typeArguments;
    public bool ReceiverIsExtension { get; } = receiverIsExtension;
    public bool IsBaseAccess { get; init; }
}

/// <summary>Placeholder for something that needs a target type: lambdas, object literals, collection expressions.</summary>
public sealed class BoundUnbound(SyntaxNode syntax, Func<TypeSymbol?, BoundExpression> bindToType, TypeSymbol? naturalType = null)
    : BoundExpression(naturalType)
{
    public SyntaxNode SyntaxNode { get; } = syntax;
    public Func<TypeSymbol?, BoundExpression> BindToType { get; } = bindToType;
    public bool IsLambda { get; init; }
    public bool IsObjectLiteral { get; init; }
    public bool IsCollection { get; init; }
    public LambdaInfo? Lambda { get; init; }
    public ObjectLiteralInfo? ObjectLiteral { get; init; }
    public CollectionInfo? Collection { get; init; }
}

// =============================================================================== statements

public abstract class BoundStatement : BoundNode;

public sealed class BoundBlock(ImmutableArray<LocalSymbol> locals, ImmutableArray<BoundStatement> statements) : BoundStatement
{
    public ImmutableArray<LocalSymbol> Locals { get; } = locals;
    public ImmutableArray<BoundStatement> Statements { get; } = statements;
}

public sealed class BoundStatementList(ImmutableArray<BoundStatement> statements) : BoundStatement
{
    public ImmutableArray<BoundStatement> Statements { get; } = statements;
}

public sealed class BoundExpressionStatement(BoundExpression expression) : BoundStatement
{
    public BoundExpression Expression { get; } = expression;
}

public sealed class BoundLocalDeclaration(LocalSymbol local, BoundExpression? initializer) : BoundStatement
{
    public LocalSymbol Local { get; } = local;
    public BoundExpression? Initializer { get; } = initializer;
}

public sealed class BoundIf(BoundExpression condition, BoundStatement consequence, BoundStatement? alternative) : BoundStatement
{
    public BoundExpression Condition { get; } = condition;
    public BoundStatement Consequence { get; } = consequence;
    public BoundStatement? Alternative { get; } = alternative;
}

public sealed class BoundWhile(BoundExpression condition, BoundStatement body, LabelSymbol breakLabel, LabelSymbol continueLabel) : BoundStatement
{
    public BoundExpression Condition { get; } = condition;
    public BoundStatement Body { get; } = body;
    public LabelSymbol BreakLabel { get; } = breakLabel;
    public LabelSymbol ContinueLabel { get; } = continueLabel;
}

public sealed class BoundDoWhile(BoundExpression condition, BoundStatement body, LabelSymbol breakLabel, LabelSymbol continueLabel) : BoundStatement
{
    public BoundExpression Condition { get; } = condition;
    public BoundStatement Body { get; } = body;
    public LabelSymbol BreakLabel { get; } = breakLabel;
    public LabelSymbol ContinueLabel { get; } = continueLabel;
}

public sealed class BoundFor(ImmutableArray<LocalSymbol> locals, BoundStatement? initializer, BoundExpression? condition, BoundStatement? increment,
    BoundStatement body, LabelSymbol breakLabel, LabelSymbol continueLabel) : BoundStatement
{
    public ImmutableArray<LocalSymbol> Locals { get; } = locals;
    public BoundStatement? Initializer { get; } = initializer;
    public BoundExpression? Condition { get; } = condition;
    public BoundStatement? Increment { get; } = increment;
    public BoundStatement Body { get; } = body;
    public LabelSymbol BreakLabel { get; } = breakLabel;
    public LabelSymbol ContinueLabel { get; } = continueLabel;
}

public sealed class BoundGoto(LabelSymbol label) : BoundStatement
{
    public LabelSymbol Label { get; } = label;
}

public sealed class BoundConditionalGoto(BoundExpression condition, LabelSymbol label, bool jumpIfTrue) : BoundStatement
{
    public BoundExpression Condition { get; } = condition;
    public LabelSymbol Label { get; } = label;
    public bool JumpIfTrue { get; } = jumpIfTrue;
}

public sealed class BoundLabel(LabelSymbol label) : BoundStatement
{
    public LabelSymbol Label { get; } = label;
}

public sealed class BoundReturn(BoundExpression? expression) : BoundStatement
{
    public BoundExpression? Expression { get; } = expression;
}

public sealed class BoundThrow(BoundExpression? expression) : BoundStatement
{
    public BoundExpression? Expression { get; } = expression;
}

public sealed class BoundCatch(LocalSymbol? local, TypeSymbol exceptionType, BoundBlock body)
{
    public LocalSymbol? Local { get; } = local;
    public TypeSymbol ExceptionType { get; } = exceptionType;
    public BoundBlock Body { get; } = body;
}

public sealed class BoundTry(BoundBlock body, ImmutableArray<BoundCatch> catches, BoundBlock? finallyBlock) : BoundStatement
{
    public BoundBlock Body { get; } = body;
    public ImmutableArray<BoundCatch> Catches { get; } = catches;
    public BoundBlock? Finally { get; } = finallyBlock;
}

public sealed class BoundYieldReturn(BoundExpression expression) : BoundStatement
{
    public BoundExpression Expression { get; } = expression;
}

public sealed class BoundYieldBreak : BoundStatement;

public sealed class BoundNoOp : BoundStatement;

/// <summary>A source statement sequence point marker wrapping another statement (used for debug info).</summary>
public sealed class BoundSequencePoint(BoundStatement statement, SyntaxNode syntaxNode) : BoundStatement
{
    public BoundStatement Statement { get; } = statement;
    public SyntaxNode SyntaxNodeForSpan { get; } = syntaxNode;
}
