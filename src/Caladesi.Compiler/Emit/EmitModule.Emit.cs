using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Emit;

public sealed class EmitResult(bool success, ImmutableArray<Diagnostic> diagnostics)
{
    public bool Success { get; } = success;
    public ImmutableArray<Diagnostic> Diagnostics { get; } = diagnostics;
}

internal sealed partial class EmitModule
{
    private readonly BlobBuilder _ilStream = new();
    private MethodBodyStreamEncoder _bodies;
#pragma warning disable IDE0370 // the null-forgiving operator is required: removing it gives CS8625
    private LoweredProgram _program = default!;
#pragma warning restore IDE0370
    private readonly List<(EntityHandle Parent, EntityHandle Ctor, BlobHandle Value)> _attributes = [];
    private readonly List<(EntityHandle Owner, int Index, string Name, GenericParameterAttributes Attrs, TypeParameterSymbol Symbol)> _genericParams = [];
    private readonly Dictionary<MethodDefinitionHandle, List<SequencePoint>> _sequencePoints = [];
    private readonly Dictionary<MethodDefinitionHandle, (List<LocalSlot> Locals, StandaloneSignatureHandle Sig, int Length, SourceFile? File)> _methodDebug = [];

    public MethodDefinitionHandle EntryPoint { get; private set; }

    private int _paramRow = 1;

    // ------------------------------------------------------------------ top level

    public EmitResult Emit(Stream peStream, Stream? pdbStream, string? pdbFileName)
    {
        ImmutableArray<Diagnostic> diagnostics = Compilation.GetDiagnostics();
        if (diagnostics.Any(d => d.EffectiveSeverity == DiagnosticSeverity.Error))
        {
            return new EmitResult(false, diagnostics);
        }

        _bodies = new MethodBodyStreamEncoder(_ilStream);
        try { _program = Lowerer.Lower(Compilation); }
        catch (Lowering.LoweringNotSupportedException ex)
        {
            string? file = ex.Syntax?.SyntaxTree?.FilePath;
            Diagnostic d = Diagnostic.Create(Descriptors.UnsupportedConstruct, file, ex.Syntax?.Span ?? default, ex.Message);
            return new EmitResult(false, diagnostics.Add(d));
        }
        // lowering may bind more code and discover more errors
        diagnostics = Compilation.GetDiagnostics();

        CompilationOptions options = Compilation.Options;
        ReservedBlob<GuidHandle> mvid = Metadata.ReserveGuid();
        string moduleName = options.AssemblyName + (options.OutputKind == OutputKind.ConsoleApplication ? ".exe" : ".dll");
        _ = Metadata.AddAssembly(Metadata.GetOrAddString(options.AssemblyName), options.AssemblyVersion, default, default, 0, AssemblyHashAlgorithm.Sha1);
        _ = Metadata.AddModule(0, Metadata.GetOrAddString(moduleName), mvid.Handle, default, default);
        TypeDefinitionHandle moduleType = Metadata.AddTypeDefinition(default, default, Metadata.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        _ = moduleType;

        AssignHandles();
        EmitTypes();
        EmitAssemblyAttributes();
        FlushAttributesAndGenericParameters();

        EntryPoint = _program.EntryPoint is { } ep && _methodDefs.TryGetValue(ep, out MethodDefinitionHandle eh) ? eh : default;

        MetadataRootBuilder metadataRoot = new(Metadata, "v4.0.30319", suppressValidation: false);
        DebugDirectoryBuilder debugDir = new();
        if (pdbStream != null)
        {
            EmitPdb(debugDir, pdbStream, pdbFileName ?? (options.AssemblyName + ".pdb"), metadataRoot);
        }

        PEHeaderBuilder header = options.OutputKind == OutputKind.ConsoleApplication
            ? PEHeaderBuilder.CreateExecutableHeader()
            : PEHeaderBuilder.CreateLibraryHeader();
        ManagedPEBuilder peBuilder = new(header, metadataRoot, _ilStream, debugDirectoryBuilder: debugDir,
            entryPoint: EntryPoint, flags: CorFlags.ILOnly,
            deterministicIdProvider: content => BlobContentId.FromHash(SHA256.HashData(ConcatBlobs(content))[..20]));
        BlobBuilder peBlob = new();
        BlobContentId contentId = peBuilder.Serialize(peBlob);
        new BlobWriter(mvid.Content).WriteGuid(contentId.Guid);
        peBlob.WriteContentTo(peStream);
        return new EmitResult(true, diagnostics);
    }

    private static byte[] ConcatBlobs(IEnumerable<Blob> blobs)
    {
        using MemoryStream ms = new();
        foreach (Blob b in blobs)
        {
            ArraySegment<byte> seg = b.GetBytes();
            ms.Write(seg.Array!, seg.Offset, seg.Count);
        }
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ handle assignment

    private readonly Dictionary<NamedTypeSymbol, (int Field, int Method, int Prop)> _firstRows = [];
    private readonly Dictionary<NamedTypeSymbol, List<Symbol>> _emitMembers = [];

    private bool IsEmitted(Symbol m)
    {
        return m is FieldSymbol or MethodSymbol or PropertySymbol;
    }

    private void AssignHandles()
    {
        int nextType = 2, nextField = 1, nextMethod = 1, nextProp = 1;
        foreach (NamedTypeSymbol type in _program.Types)
        {
            TypeDefinitionHandle th = MetadataTokens.TypeDefinitionHandle(nextType++);
            RegisterTypeDefinition(type, th);
            List<Symbol> members = [.. _program.MembersOf(type).Where(IsEmitted)];
            _emitMembers[type] = members;
            _firstRows[type] = (nextField, nextMethod, nextProp);
            if (type.TypeKind == TypeKind.Enum)
            {
                nextField++;
            }

            foreach (Symbol? m in members)
            {
                switch (m)
                {
                    case FieldSymbol f:
                        RegisterField(f, MetadataTokens.FieldDefinitionHandle(nextField++));
                        break;
                    case MethodSymbol ms:
                        RegisterMethod(ms, MetadataTokens.MethodDefinitionHandle(nextMethod++));
                        break;
                    case PropertySymbol p:
                        RegisterProperty(p, MetadataTokens.PropertyDefinitionHandle(nextProp++));
                        break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ types

    private static string NamespaceOf(NamedTypeSymbol t)
    {
        return t.ContainingSymbol is NamedTypeSymbol ? "" : (t.ContainingSymbol as NamespaceSymbol)?.ToDisplayString() ?? "";
    }

    private static TypeAttributes TypeVisibility(NamedTypeSymbol t, bool nested)
    {
        return t.DeclaredAccessibility switch
        {
            Accessibility.Public => nested ? TypeAttributes.NestedPublic : TypeAttributes.Public,
            Accessibility.Private => nested ? TypeAttributes.NestedPrivate : TypeAttributes.NotPublic,
            Accessibility.Protected => nested ? TypeAttributes.NestedFamily : TypeAttributes.NotPublic,
            _ => nested ? TypeAttributes.NestedAssembly : TypeAttributes.NotPublic,
        };
    }

    private void EmitTypes()
    {
        Dictionary<NamedTypeSymbol, TypeDefinitionHandle> typeIndex = [];
        foreach (NamedTypeSymbol t in _program.Types)
        {
            typeIndex[t] = (TypeDefinitionHandle)_typeHandles[t];
        }

        foreach (NamedTypeSymbol type in _program.Types)
        {
            TypeDefinitionHandle handle = typeIndex[type];
            _classTypeParameterMap = (type as SynthesizedNamedType)?.MethodTypeParameterMap;
            List<Symbol> members = _emitMembers[type];
            (int firstField, int firstMethod, int firstProp) = _firstRows[type];
            bool nested = type.ContainingSymbol is NamedTypeSymbol;
            TypeAttributes attrs = TypeVisibility(type, nested);
            switch (type.TypeKind)
            {
                case TypeKind.Interface: attrs |= TypeAttributes.Interface | TypeAttributes.Abstract; break;
                case TypeKind.Struct: attrs |= TypeAttributes.Sealed | TypeAttributes.SequentialLayout; break;
                case TypeKind.Enum: attrs |= TypeAttributes.Sealed; break;
                default:
                    if (type.IsStatic)
                    {
                        attrs |= TypeAttributes.Abstract | TypeAttributes.Sealed;
                    }
                    else
                    {
                        if (type.IsAbstract)
                        {
                            attrs |= TypeAttributes.Abstract;
                        }

                        if (type.IsSealed)
                        {
                            attrs |= TypeAttributes.Sealed;
                        }
                    }
                    break;
            }
            if (!members.OfType<MethodSymbol>().Any(m => m.MethodKind == MethodKind.StaticConstructor) && type.TypeKind != TypeKind.Interface)
            {
                attrs |= TypeAttributes.BeforeFieldInit;
            }

            EntityHandle baseType = default;
            if (type.TypeKind != TypeKind.Interface)
            {
                NamedTypeSymbol? b = type.BaseType;
                if (b != null)
                {
                    baseType = GetTypeEntity(b);
                }
            }

            TypeDefinitionHandle emitted = Metadata.AddTypeDefinition(attrs, string.IsNullOrEmpty(NamespaceOf(type)) ? default : Metadata.GetOrAddString(NamespaceOf(type)),
                Metadata.GetOrAddString(type.MetadataName), baseType,
                MetadataTokens.FieldDefinitionHandle(firstField), MetadataTokens.MethodDefinitionHandle(firstMethod));
            if (emitted != handle)
            {
                throw new InvalidOperationException("Type row mismatch for " + type.Name);
            }

            if (type.ContainingSymbol is NamedTypeSymbol outer && typeIndex.TryGetValue(outer, out TypeDefinitionHandle outerHandle))
            {
                Metadata.AddNestedType(handle, outerHandle);
            }

            foreach (TypeParameterSymbol tp in type.TypeParameters)
            {
                AddGenericParameterRow(handle, tp);
            }

            foreach (NamedTypeSymbol iface in type.Interfaces)
            {
                _ = Metadata.AddInterfaceImplementation(handle, GetTypeEntity(iface));
            }

            // attributes on the type
            EmitTypeAttributes(type, handle);

            if (type.TypeKind == TypeKind.Enum)
            {
                NamedTypeSymbol under = type.EnumUnderlyingType!;
                BlobBuilder bb = new();
                EncodeType(new BlobEncoder(bb).FieldSignature(), under);
                _ = Metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName, Metadata.GetOrAddString("value__"), Metadata.GetOrAddBlob(bb));
            }

            // fields
            foreach (FieldSymbol f in members.OfType<FieldSymbol>())
            {
                EmitField(f);
            }
            // methods
            foreach (MethodSymbol m in members.OfType<MethodSymbol>())
            {
                EmitMethod(type, m);
            }
            // properties
            List<PropertySymbol> props = [.. members.OfType<PropertySymbol>()];
            if (props.Count > 0)
            {
                Metadata.AddPropertyMap(handle, MetadataTokens.PropertyDefinitionHandle(firstProp));
                foreach (PropertySymbol? p in props)
                {
                    EmitProperty(p);
                }
            }
            EmitMethodImpls(type, handle);
            _classTypeParameterMap = null;
        }
        _ = typeIndex;
    }

    private void EmitMethodImpls(NamedTypeSymbol type, TypeDefinitionHandle handle)
    {
        foreach (MethodSymbol method in _emitMembers[type].OfType<MethodSymbol>())
        {
            foreach (MethodSymbol impl in method.ExplicitInterfaceImplementations)
            {
                _ = Metadata.AddMethodImplementation(handle, _methodDefs[method.OriginalDefinition], GetMethodEntity(impl));
            }
        }

        if (type is not (SourceClassSymbol or SourceRecordSymbol))
        {
            return;
        }

        foreach (NamedTypeSymbol iface in type.AllInterfaces)
        {
            foreach (MethodSymbol im in iface.GetMembers().OfType<MethodSymbol>())
            {
                if (im.IsStatic || im.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor)
                {
                    continue;
                }

                MethodSymbol? impl = DeclarationChecker.FindImplementation(type, im, requireOverride: false);
                if (impl == null || !_methodDefs.ContainsKey(impl.OriginalDefinition))
                {
                    continue;
                }

                if (impl.MetadataName == im.MetadataName && impl.ContainingType != null && ReferenceEquals(impl.ContainingType.OriginalDefinition, type.OriginalDefinition))
                {
                    continue;
                }

                if (impl.MetadataName == im.MetadataName)
                {
                    continue;
                }

                _ = Metadata.AddMethodImplementation(handle, _methodDefs[impl.OriginalDefinition], GetMethodEntity(im));
            }
        }
    }

    private void AddGenericParameterRow(EntityHandle owner, TypeParameterSymbol tp)
    {
        GenericParameterAttributes attrs = tp.Variance switch
        {
            VarianceKind.In => GenericParameterAttributes.Contravariant,
            VarianceKind.Out => GenericParameterAttributes.Covariant,
            _ => GenericParameterAttributes.None,
        };
        if (tp.HasReferenceTypeConstraint)
        {
            attrs |= GenericParameterAttributes.ReferenceTypeConstraint;
        }

        if (tp.HasValueTypeConstraint)
        {
            attrs |= GenericParameterAttributes.NotNullableValueTypeConstraint;
        }

        if (tp.HasConstructorConstraint)
        {
            attrs |= GenericParameterAttributes.DefaultConstructorConstraint;
        }

        _genericParams.Add((owner, tp.Ordinal, tp.Name, attrs, tp));
    }

    // ------------------------------------------------------------------ fields

    private void EmitField(FieldSymbol f)
    {
        FieldAttributes attrs = f.DeclaredAccessibility switch
        {
            Accessibility.Public => FieldAttributes.Public,
            Accessibility.Protected => FieldAttributes.Family,
            Accessibility.ProtectedOrInternal => FieldAttributes.FamORAssem,
            Accessibility.Internal => FieldAttributes.Assembly,
            _ => FieldAttributes.Private,
        };
        if (f.IsStatic)
        {
            attrs |= FieldAttributes.Static;
        }

        if (f.IsConst)
        {
            attrs |= FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault;
        }
        else if (f.IsReadOnly)
        {
            attrs |= FieldAttributes.InitOnly;
        }

        FieldDefinitionHandle handle = Metadata.AddFieldDefinition(attrs, Metadata.GetOrAddString(f.Name), EncodeFieldSignature(f.Type));
        if (handle != _fieldDefs[f.OriginalDefinition])
        {
            throw new InvalidOperationException("Field row mismatch for " + f.Name);
        }

        if (f.IsConst)
        {
            object? cv = f.ConstantValue?.Value;
            if (f.Type.TypeKind == TypeKind.Enum && cv != null)
            {
                cv = Convert.ToInt32(cv);
            }

            _ = Metadata.AddConstant(handle, cv);
        }
        if (f is SourceFieldSymbol { IsBackingField: false, Declaration: PropertyDeclarationSyntax fd } && f.ContainingSymbol is SourceNamedType fieldOwner)
        {
            EmitDecorators(DecoratorSyntaxHelper.Of(fd), handle, fieldOwner.File, fieldOwner);
        }
    }

    // ------------------------------------------------------------------ properties

    private void EmitProperty(PropertySymbol p)
    {
        PropertyDefinitionHandle handle = Metadata.AddProperty(PropertyAttributes.None, Metadata.GetOrAddString(p.Name), EncodePropertySignature(p));
        if (handle != _propertyDefs[p.OriginalDefinition])
        {
            throw new InvalidOperationException("Property row mismatch");
        }

        if (p.GetMethod != null && _methodDefs.TryGetValue(p.GetMethod.OriginalDefinition, out MethodDefinitionHandle g))
        {
            Metadata.AddMethodSemantics(handle, MethodSemanticsAttributes.Getter, g);
        }

        if (p.SetMethod != null && _methodDefs.TryGetValue(p.SetMethod.OriginalDefinition, out MethodDefinitionHandle s))
        {
            Metadata.AddMethodSemantics(handle, MethodSemanticsAttributes.Setter, s);
        }

        if (p is SourcePropertySymbol { Declaration: PropertyDeclarationSyntax pd } && p.ContainingSymbol is SourceNamedType owner)
        {
            EmitDecorators(DecoratorSyntaxHelper.Of(pd), handle, owner.File, owner);
        }
    }

    // ------------------------------------------------------------------ methods

    private static MethodAttributes AttributesOf(NamedTypeSymbol type, MethodSymbol m)
    {
        MethodAttributes attrs = m.DeclaredAccessibility switch
        {
            Accessibility.Public => MethodAttributes.Public,
            Accessibility.Protected => MethodAttributes.Family,
            Accessibility.ProtectedOrInternal => MethodAttributes.FamORAssem,
            Accessibility.Internal => MethodAttributes.Assembly,
            _ => MethodAttributes.Private,
        };
        attrs |= MethodAttributes.HideBySig;
        if (m.IsStatic)
        {
            attrs |= MethodAttributes.Static;
        }

        bool isInterface = type.TypeKind == TypeKind.Interface;
        if (m.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor)
        {
            attrs |= MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
            attrs &= ~MethodAttributes.HideBySig;
            attrs |= MethodAttributes.HideBySig;
            return attrs;
        }
        if (m.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.UserDefinedOperator or MethodKind.Conversion)
        {
            attrs |= MethodAttributes.SpecialName;
        }

        if (!m.IsStatic)
        {
            if (m.ExplicitInterfaceImplementations.Length > 0)
            {
                return (attrs & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot;
            }

            bool virt = m.IsVirtual || m.IsAbstract || m.IsOverride || isInterface;
            bool implementsInterface = !isInterface && type.TypeKind != TypeKind.Enum && type.AllInterfaces.Length > 0 && m.DeclaredAccessibility == Accessibility.Public && !virt
                && m.MethodKind is MethodKind.Ordinary or MethodKind.PropertyGet or MethodKind.PropertySet;
            if (virt || implementsInterface)
            {
                attrs |= MethodAttributes.Virtual;
                if (!m.IsOverride)
                {
                    attrs |= MethodAttributes.NewSlot;
                }

                if (m.IsAbstract || (isInterface && m.IsAbstract))
                {
                    attrs |= MethodAttributes.Abstract;
                }

                if ((m.IsSealed && m.IsOverride) || implementsInterface)
                {
                    attrs |= MethodAttributes.Final;
                }
            }
        }
        if (isInterface && m.IsAbstract)
        {
            attrs |= MethodAttributes.Abstract;
        }

        return attrs;
    }

    private void EmitMethod(NamedTypeSymbol type, MethodSymbol m)
    {
        MethodAttributes attrs = AttributesOf(type, m);
        MethodDefinitionHandle defHandle = _methodDefs[m.OriginalDefinition];

        // body
        int bodyOffset = -1;
        StandaloneSignatureHandle localSig = default;
        ILBuilder? il = null;
        SourceFile? file = null;
        bool hasBody = !attrs.HasFlag(MethodAttributes.Abstract);
        if (hasBody)
        {
            il = new ILBuilder();
            _ = _program.MethodFiles.TryGetValue(m, out file);
            EmitBodyIL(type, m, il, file);
            localSig = BuildLocalSignature(il);
            bodyOffset = _bodies.AddMethodBody(il.Encoder, il.MaxStack, localSig, il.Locals.Count > 0 ? MethodBodyAttributes.InitLocals : MethodBodyAttributes.None);
        }

        ParameterHandle paramList = MetadataTokens.ParameterHandle(_paramRow);
        string name = m.MetadataName;
        MethodDefinitionHandle handle = Metadata.AddMethodDefinition(attrs, MethodImplAttributes.IL | MethodImplAttributes.Managed, Metadata.GetOrAddString(name),
            EncodeMethodSignatureWithModifiers(m), bodyOffset, paramList);
        if (handle != defHandle)
        {
            throw new InvalidOperationException("Method row mismatch for " + m.Name);
        }

        if (m is SourceMethodSymbol { Declaration: { } methodDecl } && type is SourceNamedType ownerType)
        {
            EmitDecorators(DecoratorSyntaxHelper.Of(methodDecl), handle, ownerType.File, ownerType);
        }

        foreach (ParameterSymbol p in m.Parameters)
        {
            ParameterAttributes pa = ParameterAttributes.None;
            if (p.RefKind == RefKind.Out)
            {
                pa |= ParameterAttributes.Out;
            }

            if (p.IsOptional)
            {
                pa |= ParameterAttributes.Optional;
            }

            if (p.DefaultValue.HasValue)
            {
                pa |= ParameterAttributes.HasDefault;
            }

            ParameterHandle ph = Metadata.AddParameter(pa, Metadata.GetOrAddString(p.Name), p.Ordinal + 1);
            _paramRow++;
            if (p.DefaultValue.HasValue)
            {
                object? v = p.DefaultValue.Value.Value;
                TypeSymbol pt = p.Type.StripNullable();
                if (pt.TypeKind == TypeKind.Enum && v != null)
                {
                    v = Convert.ToInt32(v);
                }

                _ = Metadata.AddConstant(ph, v);
            }
            if (p.IsParams)
            {
                AddAttribute(ph, "System.ParamArrayAttribute");
            }

            if (type is SourceNamedType paramOwner && p.DeclaringSyntax != null)
            {
                EmitDecorators(DecoratorSyntaxHelper.Of(p.DeclaringSyntax), ph, paramOwner.File, paramOwner);
            }
        }
        foreach (TypeParameterSymbol tp in m.TypeParameters)
        {
            AddGenericParameterRow(handle, tp);
        }

        if (m.IsExtensionMethod && m.IsStatic)
        {
            AddAttribute(handle, "System.Runtime.CompilerServices.ExtensionAttribute");
        }

        if (il != null && file != null)
        {
            _methodDebug[handle] = (il.Locals, localSig, il.Offset, file);
            _sequencePoints[handle] = il.SequencePoints;
        }
        else if (il != null)
        {
            _methodDebug[handle] = (il.Locals, localSig, il.Offset, null);
        }
    }

    private BlobHandle EncodeMethodSignatureWithModifiers(MethodSymbol m)
    {
        MethodSymbol def = m.OriginalDefinition;
        BlobBuilder bb = new();
        MethodSignatureEncoder enc = new BlobEncoder(bb).MethodSignature(SignatureCallingConvention.Default, def.Arity, isInstanceMethod: !def.IsStatic);
        enc.Parameters(def.Parameters.Length, out ReturnTypeEncoder ret, out ParametersEncoder ps);
        TypeSymbol rt = def.ReturnType;
        if (def.IsInitOnly && Compilation.GetWellKnownType("System.Runtime.CompilerServices.IsExternalInit") is { } iei)
        {
            _ = ret.CustomModifiers().AddModifier(GetTypeDefOrRef(iei), isOptional: false);
        }

        if (rt.ReturnsNothing)
        {
            ret.Void();
        }
        else
        {
            EncodeType(ret.Type(def.ReturnsByRef), rt);
        }

        EncodeParameters(ps, def.Parameters);
        return Metadata.GetOrAddBlob(bb);
    }

    private StandaloneSignatureHandle BuildLocalSignature(ILBuilder il)
    {
        if (il.Locals.Count == 0)
        {
            return default;
        }

        BlobBuilder bb = new();
        LocalVariablesEncoder enc = new BlobEncoder(bb).LocalVariableSignature(il.Locals.Count);
        foreach (LocalSlot l in il.Locals)
        {
            SignatureTypeEncoder t = enc.AddVariable().Type();
            EncodeType(t, l.Type);
        }
        return Metadata.AddStandaloneSignature(Metadata.GetOrAddBlob(bb));
    }

    private void EmitBodyIL(NamedTypeSymbol type, MethodSymbol m, ILBuilder il, SourceFile? file)
    {
        if (m is SourceMethodSymbol { SynthesizedKind: var kind } sm && kind is not (SynthesizedBodyKind.None or SynthesizedBodyKind.Custom
            or SynthesizedBodyKind.DefaultConstructor or SynthesizedBodyKind.InheritedConstructor or SynthesizedBodyKind.StaticConstructor))
        {
            new SynthesizedBodyEmitter(this, sm, il).Emit();
            return;
        }
        if (m is SynthesizedMethodSymbol { SynthesizedKind: not SynthesizedBodyKind.None and not SynthesizedBodyKind.Custom } synth)
        {
            new SynthesizedBodyEmitter(this, synth, il).Emit();
            return;
        }
        if (_program.Bodies.TryGetValue(m, out BoundBlock? body))
        {
            new MethodBodyEmitter(this, m, il, file).EmitBody(body);
            return;
        }
        // constructor of a type without explicit body: call base .ctor
        if (m.MethodKind == MethodKind.Constructor)
        {
            il.Ldarg(0);
            NamedTypeSymbol? b = type.BaseType;
            MethodSymbol? baseCtor = b?.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0);
            if (baseCtor != null)
            {
                il.Call(ILOpCode.Call, GetMethodEntity(baseCtor), 1, 0);
            }
            else
            {
                il.Op(ILOpCode.Pop, -1);
            }

            il.Op(ILOpCode.Ret, 0);
            return;
        }
        if (m.MethodKind == MethodKind.StaticConstructor)
        {
            il.Op(ILOpCode.Ret, 0);
            return;
        }
        // missing body (error recovery): throw
        il.Op(ILOpCode.Ldnull, 1);
        il.Op(ILOpCode.Throw, -1);
    }

    // ------------------------------------------------------------------ attributes

    private void AddAttribute(EntityHandle parent, string attributeTypeName)
    {
        NamedTypeSymbol? t = Compilation.GetWellKnownType(attributeTypeName);
        if (t == null)
        {
            return;
        }

        MethodSymbol? ctor = t.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0);
        if (ctor == null)
        {
            return;
        }

        BlobBuilder bb = new();
        bb.WriteUInt16(1);
        bb.WriteUInt16(0);
        _attributes.Add((parent, GetMethodEntity(ctor), Metadata.GetOrAddBlob(bb)));
    }

    private void EmitTypeAttributes(NamedTypeSymbol type, TypeDefinitionHandle handle)
    {
        if (type is SourceModuleClassSymbol mod && mod.HasExtensionMethods)
        {
            AddAttribute(handle, "System.Runtime.CompilerServices.ExtensionAttribute");
        }

        if (type is SourceUnionSymbol)
        {
            AddCaladesiUnionAttribute(handle);
        }

        if (type.TypeKind == TypeKind.Struct && type.IsReadOnlyStruct)
        {
            AddAttribute(handle, "System.Runtime.CompilerServices.IsReadOnlyAttribute");
        }

        if (type is SourceNamedType snt)
        {
            EmitDecorators(snt.DecoratorSyntaxes, handle, snt.File, snt);
        }
    }

    private void AddCaladesiUnionAttribute(EntityHandle handle)
    {
        AddAttribute(handle, "Caladesi.Runtime.CaladesiUnionAttribute");
    }

    private void EmitDecorators(IEnumerable<DecoratorSyntax> decorators, EntityHandle parent, SourceFile file, Symbol context)
    {
        foreach (DecoratorSyntax d in decorators)
        {
            (MethodSymbol Constructor, BlobBuilder Blob)? info = new AttributeBinder(Compilation, file).Bind(d, context);
            if (info == null)
            {
                continue;
            }

            (MethodSymbol? ctor, BlobBuilder? blob) = info.Value;
            _attributes.Add((parent, GetMethodEntity(ctor), Metadata.GetOrAddBlob(blob)));
        }
    }

    private void EmitAssemblyAttributes()
    {
        CompilationOptions options = Compilation.Options;
        if (options.TargetFramework != null && Compilation.GetWellKnownType("System.Runtime.Versioning.TargetFrameworkAttribute") is { } tfa)
        {
            MethodSymbol? ctor = tfa.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 1);
            if (ctor != null)
            {
                BlobBuilder bb = new();
                bb.WriteUInt16(1);
                bb.WriteSerializedString(options.TargetFramework);
                string? display = options.TargetFrameworkDisplayName;
                if (display != null)
                {
                    bb.WriteUInt16(1);
                    bb.WriteByte(0x54); // property
                    bb.WriteByte(0x0E); // string
                    bb.WriteSerializedString("FrameworkDisplayName");
                    bb.WriteSerializedString(display);
                }
                else
                {
                    bb.WriteUInt16(0);
                }

                _attributes.Add((EntityHandle.AssemblyDefinition, GetMethodEntity(ctor), Metadata.GetOrAddBlob(bb)));
            }
        }
    }

    private void FlushAttributesAndGenericParameters()
    {
        // generic parameters must be sorted by owner (TypeOrMethodDef coded index)
        List<(EntityHandle Owner, int Index, string Name, GenericParameterAttributes Attrs, TypeParameterSymbol Symbol)> gps = [.. _genericParams
            .Select(g => (g, key: CodedIndex.TypeOrMethodDef(g.Owner)))
            .OrderBy(x => x.key).ThenBy(x => x.g.Index).Select(x => x.g)];
        foreach ((EntityHandle Owner, int Index, string Name, GenericParameterAttributes Attrs, TypeParameterSymbol Symbol) g in gps)
        {
            GenericParameterHandle h = Metadata.AddGenericParameter(g.Owner, g.Attrs, Metadata.GetOrAddString(g.Name), g.Index);
            _classTypeParameterMap = (g.Symbol.Container as SynthesizedNamedType)?.MethodTypeParameterMap;
            foreach (TypeSymbol c in g.Symbol.ConstraintTypes)
            {
                _ = Metadata.AddGenericParameterConstraint(h, GetTypeEntity(c));
            }

            _classTypeParameterMap = null;
        }
        foreach ((EntityHandle Parent, EntityHandle Ctor, BlobHandle Value) in _attributes.OrderBy(a => CodedIndex.HasCustomAttribute(a.Parent)))
        {
            _ = Metadata.AddCustomAttribute(Parent, Ctor, Value);
        }
    }

    // ------------------------------------------------------------------ debug info

    private void EmitPdb(DebugDirectoryBuilder debugDir, Stream pdbStream, string pdbFileName, MetadataRootBuilder metadataRoot)
    {
        MetadataBuilder pdb = new();
        Dictionary<SourceFile, DocumentHandle> documents = [];
        foreach (SourceFile f in Compilation.SourceFiles)
        {
            string text = f.Tree.Text.Text;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            byte[] hash = SHA256.HashData(bytes);
            documents[f] = pdb.AddDocument(pdb.GetOrAddDocumentName(f.FilePath), pdb.GetOrAddGuid(new Guid("8829d00f-11b8-4213-878b-770e8597ac16")),
                pdb.GetOrAddBlob(hash), pdb.GetOrAddGuid(new Guid("3f5162f8-07c6-11d3-9053-00c04fa302a1")));
        }
        ImportScopeHandle importScope = pdb.AddImportScope(default, pdb.GetOrAddBlob(Array.Empty<byte>()));
        int methodCount = pdb.GetRowCount(TableIndex.MethodDebugInformation);
        _ = methodCount;
        int rowCount = Metadata.GetRowCount(TableIndex.MethodDef);
        for (int row = 1; row <= rowCount; row++)
        {
            MethodDefinitionHandle mh = MetadataTokens.MethodDefinitionHandle(row);
            if (!_methodDebug.TryGetValue(mh, out (List<LocalSlot> Locals, StandaloneSignatureHandle Sig, int Length, SourceFile? File) dbg) || dbg.File == null || !_sequencePoints.TryGetValue(mh, out List<SequencePoint>? pts) || pts.Count == 0)
            {
                _ = pdb.AddMethodDebugInformation(default, default);
                continue;
            }
            byte[] blob = EncodeSequencePoints(pts, dbg.Sig);
            _ = pdb.AddMethodDebugInformation(documents[dbg.File], pdb.GetOrAddBlob(blob));
        }
        // local scopes: one scope per method listing its named locals
        int varIndex = 1;
        for (int row = 1; row <= rowCount; row++)
        {
            MethodDefinitionHandle mh = MetadataTokens.MethodDefinitionHandle(row);
            if (!_methodDebug.TryGetValue(mh, out (List<LocalSlot> Locals, StandaloneSignatureHandle Sig, int Length, SourceFile? File) dbg))
            {
                continue;
            }

            List<LocalSlot> named = [.. dbg.Locals.Where(l => l.Name != null)];
            if (named.Count == 0)
            {
                continue;
            }

            _ = pdb.AddLocalScope(mh, importScope, MetadataTokens.LocalVariableHandle(varIndex), default, 0, Math.Max(1, dbg.Length));
            foreach (LocalSlot? l in named)
            {
                _ = pdb.AddLocalVariable(LocalVariableAttributes.None, l.Index, pdb.GetOrAddString(l.Name!));
                varIndex++;
            }
        }
        MethodDefinitionHandle entryPoint = _program.EntryPoint is { } ep && _methodDefs.TryGetValue(ep, out MethodDefinitionHandle eh) ? eh : default;
        PortablePdbBuilder pdbBuilder = new(pdb, metadataRoot.Sizes.RowCounts, entryPoint,
            content => BlobContentId.FromHash(SHA256.HashData(ConcatBlobs(content))[..20]));
        BlobBuilder pdbBlob = new();
        BlobContentId pdbId = pdbBuilder.Serialize(pdbBlob);
        pdbBlob.WriteContentTo(pdbStream);
        debugDir.AddCodeViewEntry(pdbFileName, pdbId, pdbBuilder.FormatVersion);
    }

    private static byte[] EncodeSequencePoints(List<SequencePoint> pts, StandaloneSignatureHandle sig)
    {
        BlobBuilder bb = new();
        bb.WriteCompressedInteger(sig.IsNil ? 0 : MetadataTokens.GetRowNumber(sig));
        int previousOffset = 0;
        bool firstPoint = true;
        int previousStartLine = 0;
        int previousStartColumn = 0;
        foreach (SequencePoint p in pts)
        {
            int deltaOffset = firstPoint ? p.Offset : p.Offset - previousOffset;
            if (!firstPoint && deltaOffset < 0)
            {
                continue;
            }

            bb.WriteCompressedInteger(deltaOffset);
            previousOffset = p.Offset;
            firstPoint = false;
            if (p.IsHidden)
            {
                bb.WriteCompressedInteger(0);
                bb.WriteCompressedInteger(0);
                continue;
            }
            int dl = p.EndLine - p.StartLine;
            bb.WriteCompressedInteger(dl);
            int dc = p.EndColumn - p.StartColumn;
            if (dl == 0)
            {
                bb.WriteCompressedInteger(dc);
            }
            else
            {
                bb.WriteCompressedSignedInteger(dc);
            }

            if (previousStartLine == 0)
            {
                bb.WriteCompressedInteger(p.StartLine);
                bb.WriteCompressedInteger(p.StartColumn);
            }
            else
            {
                bb.WriteCompressedSignedInteger(p.StartLine - previousStartLine);
                bb.WriteCompressedSignedInteger(p.StartColumn - previousStartColumn);
            }
            previousStartLine = p.StartLine;
            previousStartColumn = p.StartColumn;
        }
        return bb.ToArray();
    }
}
