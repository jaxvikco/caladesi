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
        public List<(NarrowPath Path, TypeSymbol Type)> Narrowings { get; } = [];
        public int TypeCaseCount { get; set; }
        public int ValueCaseCount { get; set; }
    }

    private BoundBlock BindSwitch(SwitchStatementSyntax sw)
    {
        BoundExpression subject = BindValue(sw.Expression);
        if (subject is BoundUnbound u && !u.IsLambda)
        {
            subject = u.BindToType(null);
        }

        TypeSymbol subjectType = subject.Type ?? ErrorType;
        if (subject.Type == null && subject is not BoundBadExpression)
        {
            ReportNotAValue(subject, sw.Expression);
        }

        Symbol? owner = LocalOwner;
        LocalSymbol tmp = new("<switch>", subjectType, true, owner) { IsSynthesized = true };
        Scope scope = PushScope(ScopeKind.Switch);
        LabelSymbol breakLabel = new("break");
        scope.BreakLabel = breakLabel;
        scope.LabelName = _pendingLabel;
        _pendingLabel = null;

        NarrowPath? subjectPath = PathOf(subject);
        ImmutableDictionary<NarrowPath, TypeSymbol> savedNarrow = _narrow;
        ImmutableArray<BoundStatement>.Builder dispatch = ImmutableArray.CreateBuilder<BoundStatement>();
        List<SectionInfo> sections = [];
        LabelSymbol? defaultLabel = null;
        HashSet<int> coveredCases = [];
        bool coveredNull = false;
        NamedTypeSymbol? unionType = subjectType.StripNullable() as NamedTypeSymbol;
        bool isUnion = unionType is { IsUnionType: true };
        bool enumSubject = unionType is { TypeKind: TypeKind.Enum };
        HashSet<long> coveredEnum = [];

        foreach (SwitchSectionSyntax section in sw.Sections)
        {
            SectionInfo info = new() { Label = new LabelSymbol("case") };
            sections.Add(info);
            foreach (SwitchLabelSyntax label in section.Labels)
            {
                if (label.IsDefault)
                {
                    if (defaultLabel != null)
                    {
                        Report(Descriptors.DuplicateDefault, label);
                    }

                    defaultLabel = info.Label;
                    continue;
                }
                if (label.Value == null)
                {
                    continue;
                }

                BoundExpression caseExpr = BindExpression(label.Value);
                if (caseExpr is BoundBadExpression)
                {
                    continue;
                }

                BoundExpression test;
                if (caseExpr is BoundTypeExpression te)
                {
                    TypeSymbol caseType = te.TypeSym;
                    info.TypeCaseCount++;
                    BoundLocal subj = new(tmp) { Syntax = sw.Expression };
                    if (isUnion && unionType!.UnionCases.Select((c, i) => (c, i)).FirstOrDefault(x => x.c.Equals(caseType)) is { c: not null } hit && hit.c.Equals(caseType))
                    {
                        int idx = unionType.UnionCases.ToList().FindIndex(c => c.Equals(caseType));
                        BoundExpression operand = subj;
                        if (subjectType is NullableTypeSymbol)
                        {
                            operand = new BoundConversion(subj, ConversionKind.FromNullable, subjectType.StripNullable()) { Syntax = label };
                        }

                        test = subjectType is NullableTypeSymbol
                            ? new BoundLogicalBinary(true, new BoundBinary(BinaryOperatorKind.NotEqual, subj, new BoundLiteral(ConstantValue.Null, NullTypeSymbol.Instance), BoolType, ObjectType),
                                new BoundTypeTest(operand, caseType, BoolType, TypeTestKind.UnionCase, idx) { Syntax = label }, BoolType)
                            : new BoundTypeTest(operand, caseType, BoolType, TypeTestKind.UnionCase, idx) { Syntax = label };
                        _ = coveredCases.Add(idx);
                    }
                    else
                    {
                        Conversion conv = ClassifyExplicit(null, subjectType.StripNullable(), caseType);
                        if (!conv.Exists && !subjectType.IsErrorType)
                        {
                            Report(Descriptors.InstanceOfImpossible, label.Value, subjectType.ToDisplayString(), caseType.ToDisplayString());
                            continue;
                        }
                        test = new BoundTypeTest(subj, caseType, BoolType) { Syntax = label };
                    }
                    if (subjectPath != null)
                    {
                        info.Narrowings.Add((subjectPath, caseType));
                    }
                }
                else
                {
                    info.ValueCaseCount++;
                    BoundExpression value = caseExpr;
                    if (value is BoundUnbound)
                    {
                        value = ConvertImplicit(value, subjectType, label.Value);
                    }

                    if (value.Type == null) { ReportNotAValue(value, label.Value); continue; }
                    if (value is BoundLiteral { Value.IsNull: true })
                    {
                        coveredNull = true;
                    }

                    if (enumSubject)
                    {
                        if (unionType is SourceLiteralEnumSymbol literalEnum && caseExpr.ConstantValue is { Value: string literalText })
                        {
                            if (literalEnum.FindMember(literalText)?.ConstantValue is { Value: { } lv })
                            {
                                _ = coveredEnum.Add(Convert.ToInt64(lv));
                            }
                        }
                        else if (value.ConstantValue is { Value: { } enumConst } && enumConst is not string)
                        {
                            _ = coveredEnum.Add(Convert.ToInt64(enumConst));
                        }
                    }
                    test = BindEquality(label.Value, new BoundLocal(tmp) { Syntax = sw.Expression }, value, BinaryOperatorKind.Equal);
                    if (test.HasErrors)
                    {
                        continue;
                    }
                }
                dispatch.Add(new BoundConditionalGoto(test, info.Label, true) { Syntax = label });
            }
        }

        // exhaustiveness for unions
        if (isUnion && defaultLabel == null)
        {
            List<string> missing = [];
            ImmutableArray<TypeSymbol> cases = unionType!.UnionCases;
            for (int i = 0; i < cases.Length; i++)
            {
                if (!coveredCases.Contains(i))
                {
                    missing.Add(cases[i].ToDisplayString());
                }
            }

            if (subjectType is NullableTypeSymbol && !coveredNull)
            {
                missing.Add("null");
            }

            if (missing.Count > 0)
            {
                Report(Descriptors.NonExhaustiveSwitch, sw.Expression, string.Join(", ", missing));
            }
        }

        bool enumExhaustive = false;
        if (enumSubject && defaultLabel == null)
        {
            List<FieldSymbol> members = [.. unionType!.GetMembers().OfType<FieldSymbol>().Where(f => f.IsConst && f.ConstantValue is { Value: not null })];
            List<FieldSymbol> missingMembers = [.. members.Where(f => !coveredEnum.Contains(Convert.ToInt64(f.ConstantValue!.Value.Value)))];
            enumExhaustive = missingMembers.Count == 0 && members.Count > 0 && (subjectType is not NullableTypeSymbol || coveredNull);
            if (!enumExhaustive && unionType is SourceLiteralEnumSymbol && missingMembers.Count > 0)
            {
                Report(Descriptors.NonExhaustiveSwitch, sw.Expression, string.Join(", ", missingMembers.Select(f => '"' + ((f as SourceFieldSymbol)?.LiteralText ?? f.Name) + '"')));
            }
        }

        ImmutableArray<BoundStatement>.Builder bodies = ImmutableArray.CreateBuilder<BoundStatement>();
        bool lastCompletes = true;
        for (int i = 0; i < sw.Sections.Count; i++)
        {
            SwitchSectionSyntax section = sw.Sections[i];
            SectionInfo info = sections[i];
            _narrow = savedNarrow;
            // the previous section falling through into this one means the case test was not what got us here
            if ((i == 0 || !lastCompletes) && info.Narrowings.Count == 1 && info.TypeCaseCount == 1 && info.ValueCaseCount == 0 && !sw.Sections[i].Labels.Any(l => l.IsDefault))
            {
                _narrow = _narrow.SetItem(info.Narrowings[0].Path, info.Narrowings[0].Type);
            }

            bodies.Add(new BoundLabel(info.Label) { Syntax = section });
            ImmutableArray<BoundStatement> stmts = BindStatementList(section.Statements);
            BoundBlock blk = new([], stmts) { Syntax = section };
            bodies.Add(blk);
            lastCompletes = CanCompleteNormally(blk);
        }
        _narrow = savedNarrow;
        bool breakUsed = scope.BreakUsed;
        ImmutableArray<LocalSymbol> switchLocals = [.. scope.Locals];
        PopScope();
        BoundBlock node = new(switchLocals.Add(tmp),
        [
            new BoundLocalDeclaration(tmp, subject.Type == null ? null : subject) { Syntax = sw },
            .. dispatch,
            new BoundGoto(defaultLabel ?? breakLabel) { Syntax = sw },
            .. bodies,
            new BoundLabel(breakLabel) { Syntax = sw },
        ])
        { Syntax = sw };
        bool hasDefault = defaultLabel != null || enumExhaustive || (isUnion && coveredCases.Count == unionType!.UnionCases.Length && (subjectType is not NullableTypeSymbol || coveredNull));
        bool completes = breakUsed || !hasDefault || lastCompletes || sw.Sections.Count == 0;
        _flow[node] = completes;
        return node;
    }
}
