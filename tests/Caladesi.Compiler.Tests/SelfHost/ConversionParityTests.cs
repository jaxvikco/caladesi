extern alias SelfHost;

using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using SelfConversion = SelfHost::Caladesi.Compiler.Conversion;
using SelfConversions = SelfHost::Caladesi.Compiler.Conversions;
using SelfKind = SelfHost::Caladesi.Compiler.Bound.ConversionKind;
using SelfConstant = SelfHost::Caladesi.Compiler.Symbols.ConstantValue;
using SelfLambda = SelfHost::Caladesi.Compiler.Bound.LambdaSymbol;
using SelfError = SelfHost::Caladesi.Compiler.Symbols.ErrorTypeSymbol;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class ConversionParityTests
{
    [TestMethod]
    public void Conversion_DefaultAndIdentityMatchBootstrap()
    {
        AssertConversion(default, default);
        AssertConversion(Conversion.None, SelfConversions.None);
        AssertConversion(Conversion.Identity, SelfConversions.Identity);
        AssertConversion(default(Conversion).WithNullableWrap(), SelfConversions.WithNullableWrap(default));
        Assert.IsFalse(SelfConversions.IsIdentity(default));
        Assert.IsTrue(SelfConversions.IsIdentity(SelfConversions.Identity));
    }

    [TestMethod]
    public void Conversion_FactoriesAndNullableWrapMatchEveryKind()
    {
        foreach (ConversionKind kind in Enum.GetValues<ConversionKind>())
        {
            SelfKind selfKind = (SelfKind)(int)kind;
            foreach (int caseIndex in new[] { int.MinValue, -1, 0, 7, int.MaxValue })
            {
                foreach (bool isImplicit in new[] { false, true })
                {
                    Conversion original = new(kind, isImplicit, caseIndex: caseIndex);
                    SelfConversion migrated = SelfConversions.Create(selfKind, isImplicit, null, caseIndex);
                    AssertConversion(original, migrated);
                    AssertConversion(original.WithNullableWrap(), SelfConversions.WithNullableWrap(migrated));
                    AssertConversion(original.WithNullableWrap().WithNullableWrap(),
                        SelfConversions.WithNullableWrap(SelfConversions.WithNullableWrap(migrated)));
                    Assert.IsFalse(migrated.WrapNullable);
                }

                AssertConversion(Conversion.Implicit(kind, caseIndex: caseIndex),
                    SelfConversions.Implicit(selfKind, null, caseIndex));
                AssertConversion(Conversion.Explicit(kind, caseIndex: caseIndex),
                    SelfConversions.Explicit(selfKind, null, caseIndex));
            }

            AssertConversion(new Conversion(kind, true), SelfConversions.Create(selfKind, true));
            AssertConversion(Conversion.Implicit(kind), SelfConversions.Implicit(selfKind));
            AssertConversion(Conversion.Explicit(kind), SelfConversions.Explicit(selfKind));
        }
    }

    [TestMethod]
    public void Conversion_MethodAndConstantPayloadsSurviveWrapping()
    {
        LambdaSymbol method = new(ErrorTypeSymbol.Instance, "convert", false, false);
        SelfLambda selfMethod = new(SelfError.Instance, "convert", false, false);
        foreach (object? value in new object?[] { null, false, 0, 42L, 3.5, "literal" })
        {
            ConstantValue constant = new(value);
            SelfConstant selfConstant = new(value);
            Conversion original = Conversion.Implicit(ConversionKind.UserDefined, method, 3, constant);
            SelfConversion migrated = SelfConversions.Implicit(SelfKind.UserDefined, selfMethod, 3, selfConstant);
            AssertConversion(original, migrated);
            AssertConversion(original.WithNullableWrap(), SelfConversions.WithNullableWrap(migrated));
            Assert.AreSame(selfMethod, migrated.Method);
            Assert.AreSame(selfConstant, migrated.Constant);
            SelfConversion wrapped = SelfConversions.WithNullableWrap(migrated);
            Assert.AreSame(selfMethod, wrapped.Method);
            Assert.AreSame(selfConstant, wrapped.Constant);
        }

        AssertConversion(Conversion.Explicit(ConversionKind.UserDefined, method),
            SelfConversions.Explicit(SelfKind.UserDefined, selfMethod));
    }

    [TestMethod]
    public void Conversion_UndefinedKindsRetainValues()
    {
        foreach (int value in new[] { int.MinValue, -1, int.MaxValue })
        {
            AssertConversion(new Conversion((ConversionKind)value, true),
                SelfConversions.Create((SelfKind)value, true));
        }
    }

    private static void AssertConversion(Conversion expected, SelfConversion actual)
    {
        Assert.AreEqual(expected.Exists, actual.Exists);
        Assert.AreEqual((int)expected.Kind, (int)actual.Kind);
        Assert.AreEqual(expected.IsImplicit, actual.IsImplicit);
        Assert.AreEqual(expected.CaseIndex, actual.CaseIndex);
        Assert.AreEqual(expected.WrapNullable, actual.WrapNullable);
        Assert.AreEqual(expected.IsIdentity, SelfConversions.IsIdentity(actual));
        Assert.AreEqual(expected.Method?.Name, actual.Method?.Name);
        Assert.AreEqual(expected.Constant is null, actual.Constant is null);
        Assert.AreEqual(expected.Constant?.Value, actual.Constant?.Value);
    }
}
