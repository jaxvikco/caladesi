using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Caladesi.Compiler.Tests.Fixtures;

/// <summary>A C# library whose optional parameters are stored as attributes (decimal, DateTime) rather than as metadata constants.</summary>
public static class MetadataDefaults
{
    public static decimal Echo(decimal value = 1.5m)
    {
        return value;
    }

    public static decimal Negative(decimal value = -12.340m)
    {
        return value;
    }

    public static long Ticks([Optional, DateTimeConstant(637000000000000000L)] DateTime when)
    {
        return when.Ticks;
    }
}

/// <summary>Members for checking overrides of sealed members from metadata.</summary>
public class SealedBase
{
    public virtual string Name()
    {
        return "base";
    }
}

public class SealedMiddle : SealedBase
{
    public sealed override string Name()
    {
        return "middle";
    }
}
