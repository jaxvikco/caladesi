using System.Reflection.Metadata;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Emit;

/// <summary>Binds a decorator to an attribute constructor and its serialized argument blob.</summary>
internal sealed class AttributeBinder(Compilation compilation, SourceFile file)
{
    public (MethodSymbol Constructor, BlobBuilder Blob)? Bind(DecoratorSyntax decorator, SourceNamedType context)
    {
        _ = compilation;
        _ = file;
        _ = decorator;
        _ = context;
        return null;
    }
}
