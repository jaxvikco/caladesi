using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

public sealed partial class Binder
{
    private sealed class SectionInfo
    {
        public required LabelSymbol Label { get; init; }
        public List<(NarrowPath Path, TypeSymbol Type)> Narrowings { get; } = new();
        public int TypeCaseCount { get; set; }
        public int ValueCaseCount { get; set; }
    }

    private BoundStatement BindSwitch(SwitchStatementSyntax sw)
    {
        var subject = BindValue(sw.Expression);
        if (subject is BoundUnbound u && !u.IsLambda) subject = u.BindToType(null);
        var subjectType = subject.Type ?? ErrorType;
        if (subject.Type == null && subject is not BoundBadExpression) ReportNotAValue(subject, sw.Expression);
        var owner = LocalOwner;
        var tmp = new LocalSymbol("<switch>", subjectType, true, owner) { IsSynthesized = true };
        var scope = PushScope(ScopeKind.Switch);
        var breakLabel = new LabelSymbol("break");
        scope.BreakLabel = breakLabel;
        scope.LabelName = _pendingLabel;
        _pendingLabel = null;

        var subjectPath = PathOf(subject);
        var savedNarrow = _narrow;
        var dispatch = ImmutableArray.CreateBuilder<BoundStatement>();
        var sections = new List<SectionInfo>();
        LabelSymbol? defaultLabel = null;
        var coveredCases = new HashSet<int>();
        var coveredNull = false;
        var unionType = subjectType.StripNullable() as NamedTypeSymbol;
        var isUnion = unionType is { IsUnionType: true };
        var enumSubject = unionType is { TypeKind: TypeKind.Enum };
        var coveredEnum = new HashSet<long>();

        foreach (var section in sw.Sections)
        {
            var info = new SectionInfo { Label = new LabelSymbol("case") };
            sections.Add(info);
            foreach (var label in section.Labels)
            {
                if (label.IsDefault)
                {
                    if (defaultLabel != null) Report(Descriptors.DuplicateDefault, label);
                    defaultLabel = info.Label;
                    continue;
                }
                if (label.Value == null) continue;
                var caseExpr = BindExpression(label.Value);
                if (caseExpr is BoundBadExpression) continue;
                BoundExpression test;
                if (caseExpr is BoundTypeExpression te)
                {
                    var caseType = te.TypeSym;
                    info.TypeCaseCount++;
                    var subj = new BoundLocal(tmp) { Syntax = sw.Expression };
                    if (isUnion && unionType!.UnionCases.Select((c, i) => (c, i)).FirstOrDefault(x => x.c.Equals(caseType)) is { c: not null } hit && hit.c.Equals(caseType))
                    {
                        var idx = unionType.UnionCases.ToList().FindIndex(c => c.Equals(caseType));
                        BoundExpression operand = subj;
                        if (subjectType is NullableTypeSymbol) operand = new BoundConversion(subj, ConversionKind.FromNullable, subjectType.StripNullable()) { Syntax = label };
                        test = subjectType is NullableTypeSymbol
                            ? new BoundLogicalBinary(true, new BoundBinary(BinaryOperatorKind.NotEqual, subj, new BoundLiteral(ConstantValue.Null, NullTypeSymbol.Instance), BoolType, ObjectType),
                                new BoundTypeTest(operand, caseType, BoolType, TypeTestKind.UnionCase, idx) { Syntax = label }, BoolType)
                            : new BoundTypeTest(operand, caseType, BoolType, TypeTestKind.UnionCase, idx) { Syntax = label };
                        coveredCases.Add(idx);
                    }
                    else
                    {
                        var conv = ClassifyExplicit(null, subjectType.StripNullable(), caseType);
                        if (!conv.Exists && !subjectType.IsErrorType)
                        {
                            Report(Descriptors.InstanceOfImpossible, label.Value, subjectType.ToDisplayString(), caseType.ToDisplayString());
                            continue;
                        }
                        test = new BoundTypeTest(subj, caseType, BoolType) { Syntax = label };
                    }
                    if (subjectPath != null) info.Narrowings.Add((subjectPath, caseType));
                }
                else
                {
                    info.ValueCaseCount++;
                    var value = caseExpr;
                    if (value is BoundUnbound) value = ConvertImplicit(value, subjectType, label.Value);
                    if (value.Type == null) { ReportNotAValue(value, label.Value); continue; }
                    if (value is BoundLiteral { Value.IsNull: true }) coveredNull = true;
                    if (enumSubject)
                    {
                        if (unionType is SourceLiteralEnumSymbol literalEnum && caseExpr.ConstantValue is { Value: string literalText })
                        {
                            if (literalEnum.FindMember(literalText)?.ConstantValue is { Value: { } lv }) coveredEnum.Add(Convert.ToInt64(lv));
                        }
                        else if (value.ConstantValue is { Value: { } enumConst } && enumConst is not string) coveredEnum.Add(Convert.ToInt64(enumConst));
                    }
                    test = BindEquality(label.Value, new BoundLocal(tmp) { Syntax = sw.Expression }, value, BinaryOperatorKind.Equal);
                    if (test.HasErrors) continue;
                }
                dispatch.Add(new BoundConditionalGoto(test, info.Label, true) { Syntax = label });
            }
        }

        // exhaustiveness for unions
        if (isUnion && defaultLabel == null)
        {
            var missing = new List<string>();
            var cases = unionType!.UnionCases;
            for (var i = 0; i < cases.Length; i++)
                if (!coveredCases.Contains(i)) missing.Add(cases[i].ToDisplayString());
            if (subjectType is NullableTypeSymbol && !coveredNull) missing.Add("null");
            if (missing.Count > 0) Report(Descriptors.NonExhaustiveSwitch, sw.Expression, string.Join(", ", missing));
        }

        var enumExhaustive = false;
        if (enumSubject && defaultLabel == null)
        {
            var members = unionType!.GetMembers().OfType<FieldSymbol>().Where(f => f.IsConst && f.ConstantValue is { Value: not null }).ToList();
            var missingMembers = members.Where(f => !coveredEnum.Contains(Convert.ToInt64(f.ConstantValue!.Value.Value))).ToList();
            enumExhaustive = missingMembers.Count == 0 && members.Count > 0 && (subjectType is not NullableTypeSymbol || coveredNull);
            if (!enumExhaustive && unionType is SourceLiteralEnumSymbol && missingMembers.Count > 0)
                Report(Descriptors.NonExhaustiveSwitch, sw.Expression, string.Join(", ", missingMembers.Select(f => '"' + ((f as SourceFieldSymbol)?.LiteralText ?? f.Name) + '"')));
        }

        var bodies = ImmutableArray.CreateBuilder<BoundStatement>();
        var lastCompletes = true;
        for (var i = 0; i < sw.Sections.Count; i++)
        {
            var section = sw.Sections[i];
            var info = sections[i];
            _narrow = savedNarrow;
            if (info.Narrowings.Count == 1 && info.TypeCaseCount == 1 && info.ValueCaseCount == 0 && !sw.Sections[i].Labels.Any(l => l.IsDefault))
                _narrow = _narrow.SetItem(info.Narrowings[0].Path, info.Narrowings[0].Type);
            bodies.Add(new BoundLabel(info.Label) { Syntax = section });
            var stmts = BindStatementList(section.Statements);
            var blk = new BoundBlock(ImmutableArray<LocalSymbol>.Empty, stmts) { Syntax = section };
            bodies.Add(blk);
            lastCompletes = CanCompleteNormally(blk);
        }
        _narrow = savedNarrow;
        var breakUsed = scope.BreakUsed;
        var switchLocals = scope.Locals.ToImmutableArray();
        PopScope();

        var all = ImmutableArray.CreateBuilder<BoundStatement>();
        all.Add(new BoundLocalDeclaration(tmp, subject.Type == null ? null : subject) { Syntax = sw });
        all.AddRange(dispatch);
        all.Add(new BoundGoto(defaultLabel ?? breakLabel) { Syntax = sw });
        all.AddRange(bodies);
        all.Add(new BoundLabel(breakLabel) { Syntax = sw });
        var node = new BoundBlock(switchLocals.Add(tmp), all.ToImmutable()) { Syntax = sw };
        var hasDefault = defaultLabel != null || enumExhaustive || (isUnion && coveredCases.Count == unionType!.UnionCases.Length && (subjectType is not NullableTypeSymbol || coveredNull));
        var completes = breakUsed || !hasDefault || lastCompletes || sw.Sections.Count == 0;
        _flow[node] = completes;
        return node;
    }
}
