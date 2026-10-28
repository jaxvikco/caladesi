extern alias SelfHostRuntime;

using System.Reflection;
using Caladesi.Compiler.Tests.Emit;
using BootstrapUnion = Caladesi.Runtime.Union<int, string>;
using PortUnion = SelfHostRuntime::Caladesi.Runtime.Union<int, string>;

namespace Caladesi.Compiler.Tests.SelfHostRuntime;

[TestClass]
public sealed class RuntimeParityTests
{
    private static readonly Assembly s_bootstrap = typeof(BootstrapUnion).Assembly;
    private static readonly Assembly s_port = typeof(PortUnion).Assembly;

    [TestMethod]
    public void Runtime_AllAritiesPreservePublicAbiAndLocalMarkerIdentity()
    {
        Assert.AreNotEqual(s_bootstrap, s_port);
        Assert.IsFalse(s_port.GetReferencedAssemblies().Any(a => a.Name == s_bootstrap.GetName().Name));
        Type marker = s_port.GetType("Caladesi.Runtime.CaladesiUnionAttribute", throwOnError: true)!;
        Assert.AreEqual(typeof(Attribute), marker.BaseType);
        Assert.AreEqual(AttributeTargets.Struct, marker.GetCustomAttribute<AttributeUsageAttribute>()!.ValidOn);
        for (int arity = 2; arity <= 8; arity++)
        {
            Type expected = Union(s_bootstrap, arity);
            Type actual = Union(s_port, arity);
            Assert.AreEqual(expected.FullName, actual.FullName);
            Assert.IsTrue(actual.IsValueType && actual.IsSealed);
            CollectionAssert.AreEquivalent(Api(expected), Api(actual), $"Arity {arity}");
            Assert.HasCount(arity, actual.GetConstructors());
            Assert.AreEqual(arity, actual.GetMethods().Count(m => m.Name == "op_Implicit"));
            Assert.IsTrue(actual.GetFields().All(f => f.IsInitOnly && !f.IsStatic));
            Assert.AreEqual(typeof(int), actual.GetField("Tag")!.FieldType);
            Assert.AreEqual(typeof(object), actual.GetField("Value")!.FieldType);
            Assert.IsTrue(actual.CustomAttributes.Any(a => a.AttributeType == marker));
            Assert.IsTrue(actual.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute"));
            Type equatable = actual.GetInterfaces().Single();
            Assert.AreEqual(typeof(IEquatable<>), equatable.GetGenericTypeDefinition());
            Assert.IsTrue(actual == equatable.GenericTypeArguments[0].GetGenericTypeDefinition());
        }
    }

    [TestMethod]
    public void Runtime_AllAritiesMatchDefaultNullValueReferenceAndDuplicateGenericCases()
    {
        Type[] types = [typeof(int), typeof(string), typeof(bool), typeof(double), typeof(long), typeof(decimal), typeof(Guid), typeof(object)];
        object?[] values = [42, "hello", true, 1.25, 45L, 3.5m, Guid.Empty, new Payload(3)];
        for (int arity = 2; arity <= 8; arity++)
        {
            Compare(Union(s_bootstrap, arity).MakeGenericType(types[..arity]),
                Union(s_port, arity).MakeGenericType(types[..arity]), values[..arity]);
            Compare(Union(s_bootstrap, arity).MakeGenericType(Enumerable.Repeat(typeof(object), arity).ToArray()),
                Union(s_port, arity).MakeGenericType(Enumerable.Repeat(typeof(object), arity).ToArray()),
                Enumerable.Range(0, arity).Select(i => i % 2 == 0 ? null : (object)new Payload(i)).ToArray());
            Compare(Union(s_bootstrap, arity).MakeGenericType(Enumerable.Repeat(typeof(object), arity).ToArray()),
                Union(s_port, arity).MakeGenericType(Enumerable.Repeat(typeof(object), arity).ToArray()),
                Enumerable.Repeat<object?>(new NullText(), arity).ToArray());
        }
    }

    [TestMethod]
    public void Runtime_AllEmittedMethodsPassIlVerification()
    {
        List<string> errors = ILVerifier.Verify(s_port.GetName().Name!, File.ReadAllBytes(s_port.Location));
        Assert.IsEmpty(errors, string.Join(Environment.NewLine, errors));
    }

    [TestMethod]
    public void Runtime_ConstructedMetadataIdentityAndInterfaceDispatchWork()
    {
        PortUnion left = 42;
        PortUnion right = 42;
        PortUnion other = "42";
        Assert.IsTrue(((IEquatable<PortUnion>)left).Equals(right));
        Assert.IsTrue(left == right);
        Assert.IsTrue(left != other);
        Assert.AreEqual(HashCode.Combine(1, (object)42), left.GetHashCode());
        Assert.IsFalse(left.Equals((object)new BootstrapUnion(42)));
        Assert.IsTrue(typeof(PortUnion).GetInterfaces().Contains(typeof(IEquatable<PortUnion>)));
    }

    private static void Compare(Type expected, Type actual, object?[] values)
    {
        ConstructorInfo[] expectedCtors = expected.GetConstructors().OrderBy(c => c.MetadataToken).ToArray();
        ConstructorInfo[] actualCtors = actual.GetConstructors().OrderBy(c => c.MetadataToken).ToArray();
        List<object> expectedValues = [Activator.CreateInstance(expected)!];
        List<object> actualValues = [Activator.CreateInstance(actual)!];
        for (int i = 0; i < values.Length; i++)
        {
            object left = expectedCtors[i].Invoke([values[i]]);
            object right = actualCtors[i].Invoke([values[i]]);
            expectedValues.Add(left);
            actualValues.Add(right);
            Assert.AreEqual(i + 1, actual.GetField("Tag")!.GetValue(right));
            Assert.AreEqual(values[i], actual.GetField("Value")!.GetValue(right));
            MethodInfo conversion = actual.GetMethods().Where(m => m.Name == "op_Implicit").OrderBy(m => m.MetadataToken).ElementAt(i);
            Assert.IsTrue(right.Equals(conversion.Invoke(null, [values[i]])));
        }
        MethodInfo eq = actual.GetMethod("Equals", [actual])!;
        MethodInfo opEq = actual.GetMethod("op_Equality")!;
        MethodInfo opNe = actual.GetMethod("op_Inequality")!;
        for (int i = 0; i < expectedValues.Count; i++)
        {
            object left = expectedValues[i];
            object right = actualValues[i];
            Assert.AreEqual(left.GetHashCode(), right.GetHashCode());
            Assert.AreEqual(left.ToString(), right.ToString());
            Assert.IsFalse(right.Equals(null));
            Assert.IsFalse(right.Equals("not a union"));
            Assert.IsFalse(right.Equals(left), "Different assembly identities must not compare equal.");
            for (int j = 0; j < expectedValues.Count; j++)
            {
                bool equal = left.Equals(expectedValues[j]);
                Assert.AreEqual(equal, right.Equals(actualValues[j]));
                Assert.AreEqual(equal, eq.Invoke(right, [actualValues[j]]));
                Assert.AreEqual(equal, opEq.Invoke(null, [right, actualValues[j]]));
                Assert.AreEqual(!equal, opNe.Invoke(null, [right, actualValues[j]]));
            }
        }
    }

    private static Type Union(Assembly assembly, int arity)
    {
        return assembly.GetType("Caladesi.Runtime.Union`" + arity, throwOnError: true)!;
    }

    private static string[] Api(Type type)
    {
        return type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m switch
            {
                FieldInfo f => $"field {f.Name}: {Signature(f.FieldType)} {f.IsInitOnly}",
                MethodBase method => $"{method.Name}({string.Join(",", method.GetParameters().Select(p => Signature(p.ParameterType)))})"
                    + (method is MethodInfo info ? $": {Signature(info.ReturnType)} {info.IsStatic} {info.IsSpecialName}" : ""),
                _ => m.ToString()!,
            }).Order().ToArray();
    }

    private static string Signature(Type type)
    {
        return type.IsGenericParameter ? "!" + type.GenericParameterPosition
            : type.IsGenericType ? type.GetGenericTypeDefinition().FullName + "<" + string.Join(",", type.GenericTypeArguments.Select(Signature)) + ">"
            : type.FullName!;
    }

    private sealed record Payload(int Value);

    private sealed class NullText
    {
        public override string ToString() => null!;
    }
}
