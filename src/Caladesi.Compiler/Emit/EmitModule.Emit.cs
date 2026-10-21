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

public sealed class EmitResult
{
    public EmitResult(bool success, ImmutableArray<Diagnostic> diagnostics)
    {
        Success = success;
        Diagnostics = diagnostics;
    }

    public bool Success { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }
}

internal sealed partial class EmitModule
{
    private readonly BlobBuilder _ilStream = new();
    private MethodBodyStreamEncoder _bodies;
    private LoweredProgram _program = null!;
    private readonly List<(EntityHandle Parent, EntityHandle Ctor, BlobHandle Value)> _attributes = new();
    private readonly List<(EntityHandle Owner, int Index, string Name, GenericParameterAttributes Attrs, TypeParameterSymbol Symbol)> _genericParams = new();
    private readonly Dictionary<MethodDefinitionHandle, List<SequencePoint>> _sequencePoints = new();
    private readonly Dictionary<MethodDefinitionHandle, (List<LocalSlot> Locals, StandaloneSignatureHandle Sig, int Length, SourceFile? File)> _methodDebug = new();
    private MethodDefinitionHandle _entryPoint;

    public MethodDefinitionHandle EntryPoint => _entryPoint;

    private int _paramRow = 1;
    private readonly Dictionary<MethodSymbol, int> _firstParam = new();

    // ------------------------------------------------------------------ top level

    public EmitResult Emit(Stream peStream, Stream? pdbStream, string? pdbFileName)
    {
        var diagnostics = Compilation.GetDiagnostics();
        if (diagnostics.Any(d => d.EffectiveSeverity == DiagnosticSeverity.Error))
            return new EmitResult(false, diagnostics);

        _bodies = new MethodBodyStreamEncoder(_ilStream);
        _program = Lowerer.Lower(Compilation);
        // lowering may bind more code and discover more errors
        diagnostics = Compilation.GetDiagnostics();

        var options = Compilation.Options;
        var mvid = Metadata.ReserveGuid();
        var moduleName = options.AssemblyName + (options.OutputKind == OutputKind.ConsoleApplication ? ".exe" : ".dll");
        Metadata.AddAssembly(Metadata.GetOrAddString(options.AssemblyName), options.AssemblyVersion, default, default, 0, AssemblyHashAlgorithm.Sha1);
        Metadata.AddModule(0, Metadata.GetOrAddString(moduleName), mvid.Handle, default, default);
        var moduleType = Metadata.AddTypeDefinition(default, default, Metadata.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        _ = moduleType;

        AssignHandles();
        EmitTypes();
        EmitAssemblyAttributes();
        FlushAttributesAndGenericParameters();

        _entryPoint = _program.EntryPoint is { } ep && _methodDefs.TryGetValue(ep, out var eh) ? eh : default;

        var metadataRoot = new MetadataRootBuilder(Metadata, "v4.0.30319", suppressValidation: false);
        var debugDir = new DebugDirectoryBuilder();
        if (pdbStream != null) EmitPdb(debugDir, pdbStream, pdbFileName ?? (options.AssemblyName + ".pdb"), metadataRoot);

        var header = options.OutputKind == OutputKind.ConsoleApplication
            ? PEHeaderBuilder.CreateExecutableHeader()
            : PEHeaderBuilder.CreateLibraryHeader();
        var peBuilder = new ManagedPEBuilder(header, metadataRoot, _ilStream, debugDirectoryBuilder: debugDir,
            entryPoint: _entryPoint, flags: CorFlags.ILOnly,
            deterministicIdProvider: content => BlobContentId.FromHash(SHA256.HashData(ConcatBlobs(content))[..20]));
        var peBlob = new BlobBuilder();
        var contentId = peBuilder.Serialize(peBlob);
        new BlobWriter(mvid.Content).WriteGuid(contentId.Guid);
        peBlob.WriteContentTo(peStream);
        return new EmitResult(true, diagnostics);
    }

    private static byte[] ConcatBlobs(IEnumerable<Blob> blobs)
    {
        var ms = new MemoryStream();
        foreach (var b in blobs)
        {
            var seg = b.GetBytes();
            ms.Write(seg.Array!, seg.Offset, seg.Count);
        }
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ handle assignment

    private readonly Dictionary<NamedTypeSymbol, (int Field, int Method, int Prop)> _firstRows = new();
    private readonly Dictionary<NamedTypeSymbol, List<Symbol>> _emitMembers = new();

    private bool IsEmitted(Symbol m) => m is FieldSymbol or MethodSymbol or PropertySymbol;

    private void AssignHandles()
    {
        int nextType = 2, nextField = 1, nextMethod = 1, nextProp = 1;
        foreach (var type in _program.Types)
        {
            var th = MetadataTokens.TypeDefinitionHandle(nextType++);
            RegisterTypeDefinition(type, th);
            var members = _program.MembersOf(type).Where(IsEmitted).ToList();
            _emitMembers[type] = members;
            _firstRows[type] = (nextField, nextMethod, nextProp);
            if (type.TypeKind == TypeKind.Enum) nextField++;
            foreach (var m in members)
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
        if (t.ContainingSymbol is NamedTypeSymbol) return "";
        return (t.ContainingSymbol as NamespaceSymbol)?.ToDisplayString() ?? "";
    }

    private static TypeAttributes TypeVisibility(NamedTypeSymbol t, bool nested) => t.DeclaredAccessibility switch
    {
        Accessibility.Public => nested ? TypeAttributes.NestedPublic : TypeAttributes.Public,
        Accessibility.Private => nested ? TypeAttributes.NestedPrivate : TypeAttributes.NotPublic,
        Accessibility.Protected => nested ? TypeAttributes.NestedFamily : TypeAttributes.NotPublic,
        _ => nested ? TypeAttributes.NestedAssembly : TypeAttributes.NotPublic,
    };

    private void EmitTypes()
    {
        var typeIndex = new Dictionary<NamedTypeSymbol, TypeDefinitionHandle>();
        foreach (var t in _program.Types) typeIndex[t] = (TypeDefinitionHandle)_typeHandles[t];

        foreach (var type in _program.Types)
        {
            var handle = typeIndex[type];
            _classTypeParameterMap = (type as SynthesizedNamedType)?.MethodTypeParameterMap;
            var members = _emitMembers[type];
            var (firstField, firstMethod, firstProp) = _firstRows[type];
            var nested = type.ContainingSymbol is NamedTypeSymbol;
            var attrs = TypeVisibility(type, nested);
            switch (type.TypeKind)
            {
                case TypeKind.Interface: attrs |= TypeAttributes.Interface | TypeAttributes.Abstract; break;
                case TypeKind.Struct: attrs |= TypeAttributes.Sealed | TypeAttributes.SequentialLayout; break;
                case TypeKind.Enum: attrs |= TypeAttributes.Sealed; break;
                default:
                    if (type.IsStatic) attrs |= TypeAttributes.Abstract | TypeAttributes.Sealed;
                    else
                    {
                        if (type.IsAbstract) attrs |= TypeAttributes.Abstract;
                        if (type.IsSealed) attrs |= TypeAttributes.Sealed;
                    }
                    break;
            }
            if (!members.OfType<MethodSymbol>().Any(m => m.MethodKind == MethodKind.StaticConstructor) && type.TypeKind != TypeKind.Interface)
                attrs |= TypeAttributes.BeforeFieldInit;

            EntityHandle baseType = default;
            if (type.TypeKind != TypeKind.Interface)
            {
                var b = type.BaseType;
                if (b != null) baseType = GetTypeEntity(b);
            }

            var emitted = Metadata.AddTypeDefinition(attrs, string.IsNullOrEmpty(NamespaceOf(type)) ? default : Metadata.GetOrAddString(NamespaceOf(type)),
                Metadata.GetOrAddString(type.MetadataName), baseType,
                MetadataTokens.FieldDefinitionHandle(firstField), MetadataTokens.MethodDefinitionHandle(firstMethod));
            if (emitted != handle) throw new InvalidOperationException("Type row mismatch for " + type.Name);

            if (type.ContainingSymbol is NamedTypeSymbol outer && typeIndex.TryGetValue(outer, out var outerHandle))
                Metadata.AddNestedType(handle, outerHandle);

            foreach (var tp in type.TypeParameters) AddGenericParameterRow(handle, tp);
            foreach (var iface in type.Interfaces) Metadata.AddInterfaceImplementation(handle, GetTypeEntity(iface));

            // attributes on the type
            EmitTypeAttributes(type, handle);

            if (type.TypeKind == TypeKind.Enum)
            {
                var under = ((NamedTypeSymbol)type).EnumUnderlyingType!;
                var bb = new BlobBuilder();
                EncodeType(new BlobEncoder(bb).FieldSignature(), under);
                Metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName, Metadata.GetOrAddString("value__"), Metadata.GetOrAddBlob(bb));
            }

            // fields
            foreach (var f in members.OfType<FieldSymbol>()) EmitField(f);
            // methods
            foreach (var m in members.OfType<MethodSymbol>()) EmitMethod(type, m);
            // properties
            var props = members.OfType<PropertySymbol>().ToList();
            if (props.Count > 0)
            {
                Metadata.AddPropertyMap(handle, MetadataTokens.PropertyDefinitionHandle(firstProp));
                foreach (var p in props) EmitProperty(p);
            }
            EmitMethodImpls(type, handle);
            _classTypeParameterMap = null;
        }
        _ = typeIndex;
    }

    private void EmitMethodImpls(NamedTypeSymbol type, TypeDefinitionHandle handle)
    {
        if (type is not (SourceClassSymbol or SourceRecordSymbol)) return;
        foreach (var iface in type.AllInterfaces)
        {
            foreach (var im in iface.GetMembers().OfType<MethodSymbol>())
            {
                if (im.IsStatic || im.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor) continue;
                var impl = DeclarationChecker.FindImplementation(type, im, requireOverride: false);
                if (impl == null || !_methodDefs.ContainsKey(impl.OriginalDefinition)) continue;
                if (impl.MetadataName == im.MetadataName && impl.ContainingType != null && ReferenceEquals(impl.ContainingType.OriginalDefinition, type.OriginalDefinition)) continue;
                if (impl.MetadataName == im.MetadataName) continue;
                Metadata.AddMethodImplementation(handle, _methodDefs[impl.OriginalDefinition], GetMethodEntity(im));
            }
        }
    }

    private void AddGenericParameterRow(EntityHandle owner, TypeParameterSymbol tp)
    {
        var attrs = tp.Variance switch
        {
            VarianceKind.In => GenericParameterAttributes.Contravariant,
            VarianceKind.Out => GenericParameterAttributes.Covariant,
            _ => GenericParameterAttributes.None,
        };
        if (tp.HasReferenceTypeConstraint) attrs |= GenericParameterAttributes.ReferenceTypeConstraint;
        if (tp.HasValueTypeConstraint) attrs |= GenericParameterAttributes.NotNullableValueTypeConstraint;
        if (tp.HasConstructorConstraint) attrs |= GenericParameterAttributes.DefaultConstructorConstraint;
        _genericParams.Add((owner, tp.Ordinal, tp.Name, attrs, tp));
    }

    // ------------------------------------------------------------------ fields

    private void EmitField(FieldSymbol f)
    {
        var attrs = f.DeclaredAccessibility switch
        {
            Accessibility.Public => FieldAttributes.Public,
            Accessibility.Protected => FieldAttributes.Family,
            Accessibility.ProtectedOrInternal => FieldAttributes.FamORAssem,
            Accessibility.Internal => FieldAttributes.Assembly,
            _ => FieldAttributes.Private,
        };
        if (f.IsStatic) attrs |= FieldAttributes.Static;
        if (f.IsConst) attrs |= FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault;
        else if (f.IsReadOnly) attrs |= FieldAttributes.InitOnly;
        var handle = Metadata.AddFieldDefinition(attrs, Metadata.GetOrAddString(f.Name), EncodeFieldSignature(f.Type));
        if (handle != _fieldDefs[f.OriginalDefinition]) throw new InvalidOperationException("Field row mismatch for " + f.Name);
        if (f.IsConst)
        {
            var cv = f.ConstantValue?.Value;
            if (f.Type.TypeKind == TypeKind.Enum && cv != null) cv = Convert.ToInt32(cv);
            Metadata.AddConstant(handle, cv);
        }
        if (f is SourceFieldSymbol { IsBackingField: true }) { }
    }

    // ------------------------------------------------------------------ properties

    private void EmitProperty(PropertySymbol p)
    {
        var handle = Metadata.AddProperty(PropertyAttributes.None, Metadata.GetOrAddString(p.Name), EncodePropertySignature(p));
        if (handle != _propertyDefs[p.OriginalDefinition]) throw new InvalidOperationException("Property row mismatch");
        if (p.GetMethod != null && _methodDefs.TryGetValue(p.GetMethod.OriginalDefinition, out var g))
            Metadata.AddMethodSemantics(handle, MethodSemanticsAttributes.Getter, g);
        if (p.SetMethod != null && _methodDefs.TryGetValue(p.SetMethod.OriginalDefinition, out var s))
            Metadata.AddMethodSemantics(handle, MethodSemanticsAttributes.Setter, s);
    }

    // ------------------------------------------------------------------ methods

    private MethodAttributes AttributesOf(NamedTypeSymbol type, MethodSymbol m)
    {
        var attrs = m.DeclaredAccessibility switch
        {
            Accessibility.Public => MethodAttributes.Public,
            Accessibility.Protected => MethodAttributes.Family,
            Accessibility.ProtectedOrInternal => MethodAttributes.FamORAssem,
            Accessibility.Internal => MethodAttributes.Assembly,
            _ => MethodAttributes.Private,
        };
        attrs |= MethodAttributes.HideBySig;
        if (m.IsStatic) attrs |= MethodAttributes.Static;
        var isInterface = type.TypeKind == TypeKind.Interface;
        if (m.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor)
        {
            attrs |= MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
            attrs &= ~MethodAttributes.HideBySig;
            attrs |= MethodAttributes.HideBySig;
            return attrs;
        }
        if (m.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.UserDefinedOperator or MethodKind.Conversion) attrs |= MethodAttributes.SpecialName;
        if (!m.IsStatic)
        {
            var virt = m.IsVirtual || m.IsAbstract || m.IsOverride || isInterface;
            var implementsInterface = !isInterface && type.TypeKind != TypeKind.Enum && type.AllInterfaces.Length > 0 && m.DeclaredAccessibility == Accessibility.Public && !virt
                && m.MethodKind is MethodKind.Ordinary or MethodKind.PropertyGet or MethodKind.PropertySet;
            if (virt || implementsInterface)
            {
                attrs |= MethodAttributes.Virtual;
                if (!m.IsOverride) attrs |= MethodAttributes.NewSlot;
                if (m.IsAbstract || isInterface && m.IsAbstract) attrs |= MethodAttributes.Abstract;
                if (m.IsSealed && m.IsOverride || implementsInterface) attrs |= MethodAttributes.Final;
            }
        }
        if (isInterface && m.IsAbstract) attrs |= MethodAttributes.Abstract;
        return attrs;
    }

    private void EmitMethod(NamedTypeSymbol type, MethodSymbol m)
    {
        var attrs = AttributesOf(type, m);
        var defHandle = _methodDefs[m.OriginalDefinition];

        // body
        int bodyOffset = -1;
        StandaloneSignatureHandle localSig = default;
        ILBuilder? il = null;
        SourceFile? file = null;
        var hasBody = !(attrs.HasFlag(MethodAttributes.Abstract));
        if (hasBody)
        {
            il = new ILBuilder();
            _program.MethodFiles.TryGetValue(m, out file);
            EmitBodyIL(type, m, il, file);
            localSig = BuildLocalSignature(il);
            bodyOffset = _bodies.AddMethodBody(il.Encoder, il.MaxStack, localSig, il.Locals.Count > 0 ? MethodBodyAttributes.InitLocals : MethodBodyAttributes.None);
        }

        var paramList = MetadataTokens.ParameterHandle(_paramRow);
        var name = m.MetadataName;
        var handle = Metadata.AddMethodDefinition(attrs, MethodImplAttributes.IL | MethodImplAttributes.Managed, Metadata.GetOrAddString(name),
            EncodeMethodSignatureWithModifiers(m), bodyOffset, paramList);
        if (handle != defHandle) throw new InvalidOperationException("Method row mismatch for " + m.Name);

        foreach (var p in m.Parameters)
        {
            var pa = ParameterAttributes.None;
            if (p.RefKind == RefKind.Out) pa |= ParameterAttributes.Out;
            if (p.IsOptional) pa |= ParameterAttributes.Optional;
            if (p.DefaultValue.HasValue) pa |= ParameterAttributes.HasDefault;
            var ph = Metadata.AddParameter(pa, Metadata.GetOrAddString(p.Name), p.Ordinal + 1);
            _paramRow++;
            if (p.DefaultValue.HasValue)
            {
                var v = p.DefaultValue.Value.Value;
                var pt = p.Type.StripNullable();
                if (pt.TypeKind == TypeKind.Enum && v != null) v = Convert.ToInt32(v);
                Metadata.AddConstant(ph, v);
            }
            if (p.IsParams) AddAttribute(ph, "System.ParamArrayAttribute");
        }
        foreach (var tp in m.TypeParameters) AddGenericParameterRow(handle, tp);
        if (m.IsExtensionMethod && m.IsStatic) AddAttribute(handle, "System.Runtime.CompilerServices.ExtensionAttribute");

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
        var def = m.OriginalDefinition;
        var bb = new BlobBuilder();
        var enc = new BlobEncoder(bb).MethodSignature(SignatureCallingConvention.Default, def.Arity, isInstanceMethod: !def.IsStatic);
        enc.Parameters(def.Parameters.Length, out var ret, out var ps);
        var rt = def.ReturnType;
        if (def.IsInitOnly && Compilation.GetWellKnownType("System.Runtime.CompilerServices.IsExternalInit") is { } iei)
            ret.CustomModifiers().AddModifier(GetTypeDefOrRef(iei), isOptional: false);
        if (rt.IsVoid) ret.Void();
        else EncodeType(ret.Type(def.ReturnsByRef), rt);
        EncodeParameters(ps, def.Parameters);
        return Metadata.GetOrAddBlob(bb);
    }

    private StandaloneSignatureHandle BuildLocalSignature(ILBuilder il)
    {
        if (il.Locals.Count == 0) return default;
        var bb = new BlobBuilder();
        var enc = new BlobEncoder(bb).LocalVariableSignature(il.Locals.Count);
        foreach (var l in il.Locals)
        {
            var t = enc.AddVariable().Type();
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
        if (_program.Bodies.TryGetValue(m, out var body))
        {
            new MethodBodyEmitter(this, m, il, file).EmitBody(body);
            return;
        }
        // constructor of a type without explicit body: call base .ctor
        if (m.MethodKind == MethodKind.Constructor)
        {
            il.Ldarg(0);
            var b = type.BaseType;
            var baseCtor = b?.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0);
            if (baseCtor != null) il.Call(ILOpCode.Call, GetMethodEntity(baseCtor), 1, 0); else il.Op(ILOpCode.Pop, -1);
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
        var t = Compilation.GetWellKnownType(attributeTypeName);
        if (t == null) return;
        var ctor = t.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0);
        if (ctor == null) return;
        var bb = new BlobBuilder();
        bb.WriteUInt16(1);
        bb.WriteUInt16(0);
        _attributes.Add((parent, GetMethodEntity(ctor), Metadata.GetOrAddBlob(bb)));
    }

    private void EmitTypeAttributes(NamedTypeSymbol type, TypeDefinitionHandle handle)
    {
        if (type is SourceModuleClassSymbol mod && mod.HasExtensionMethods) AddAttribute(handle, "System.Runtime.CompilerServices.ExtensionAttribute");
        if (type is SourceUnionSymbol) AddCaladesiUnionAttribute(handle);
        if (type.TypeKind == TypeKind.Struct && type.IsReadOnlyStruct) AddAttribute(handle, "System.Runtime.CompilerServices.IsReadOnlyAttribute");
        if (type is SourceNamedType snt) EmitDecorators(snt.DecoratorSyntaxes, handle, snt);
    }

    private void AddCaladesiUnionAttribute(EntityHandle handle) => AddAttribute(handle, "Caladesi.Runtime.CaladesiUnionAttribute");

    private void EmitDecorators(ImmutableArray<DecoratorSyntax> decorators, EntityHandle parent, SourceNamedType context)
    {
        foreach (var d in decorators)
        {
            var info = new AttributeBinder(Compilation, context.File).Bind(d, context);
            if (info == null) continue;
            var (ctor, blob) = info.Value;
            _attributes.Add((parent, GetMethodEntity(ctor), Metadata.GetOrAddBlob(blob)));
        }
    }

    private void EmitAssemblyAttributes()
    {
        var options = Compilation.Options;
        if (options.TargetFramework != null && Compilation.GetWellKnownType("System.Runtime.Versioning.TargetFrameworkAttribute") is { } tfa)
        {
            var ctor = tfa.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 1);
            if (ctor != null)
            {
                var bb = new BlobBuilder();
                bb.WriteUInt16(1);
                bb.WriteSerializedString(options.TargetFramework);
                var display = options.TargetFrameworkDisplayName;
                if (display != null)
                {
                    bb.WriteUInt16(1);
                    bb.WriteByte(0x54); // property
                    bb.WriteByte(0x0E); // string
                    bb.WriteSerializedString("FrameworkDisplayName");
                    bb.WriteSerializedString(display);
                }
                else bb.WriteUInt16(0);
                _attributes.Add((EntityHandle.AssemblyDefinition, GetMethodEntity(ctor), Metadata.GetOrAddBlob(bb)));
            }
        }
    }

    private void FlushAttributesAndGenericParameters()
    {
        // generic parameters must be sorted by owner (TypeOrMethodDef coded index)
        var gps = _genericParams
            .Select(g => (g, key: CodedIndex.TypeOrMethodDef(g.Owner)))
            .OrderBy(x => x.key).ThenBy(x => x.g.Index).Select(x => x.g).ToList();
        foreach (var g in gps)
        {
            var h = Metadata.AddGenericParameter(g.Owner, g.Attrs, Metadata.GetOrAddString(g.Name), g.Index);
            _classTypeParameterMap = (g.Symbol.Container as SynthesizedNamedType)?.MethodTypeParameterMap;
            foreach (var c in g.Symbol.ConstraintTypes)
                Metadata.AddGenericParameterConstraint(h, GetTypeEntity(c));
            _classTypeParameterMap = null;
        }
        foreach (var a in _attributes.OrderBy(a => CodedIndex.HasCustomAttribute(a.Parent)))
            Metadata.AddCustomAttribute(a.Parent, a.Ctor, a.Value);
    }

    // ------------------------------------------------------------------ debug info

    private void EmitPdb(DebugDirectoryBuilder debugDir, Stream pdbStream, string pdbFileName, MetadataRootBuilder metadataRoot)
    {
        var pdb = new MetadataBuilder();
        var documents = new Dictionary<SourceFile, DocumentHandle>();
        foreach (var f in Compilation.SourceFiles)
        {
            var text = f.Tree.Text.Text;
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            var hash = SHA256.HashData(bytes);
            documents[f] = pdb.AddDocument(pdb.GetOrAddDocumentName(f.FilePath), pdb.GetOrAddGuid(new Guid("8829d00f-11b8-4213-878b-770e8597ac16")),
                pdb.GetOrAddBlob(hash), pdb.GetOrAddGuid(new Guid("3f5162f8-07c6-11d3-9053-00c04fa302a1")));
        }
        var importScope = pdb.AddImportScope(default, pdb.GetOrAddBlob(Array.Empty<byte>()));
        var methodCount = pdb.GetRowCount(TableIndex.MethodDebugInformation);
        _ = methodCount;
        var rowCount = Metadata.GetRowCount(TableIndex.MethodDef);
        for (var row = 1; row <= rowCount; row++)
        {
            var mh = MetadataTokens.MethodDefinitionHandle(row);
            if (!_methodDebug.TryGetValue(mh, out var dbg) || dbg.File == null || !_sequencePoints.TryGetValue(mh, out var pts) || pts.Count == 0)
            {
                pdb.AddMethodDebugInformation(default, default);
                continue;
            }
            var blob = EncodeSequencePoints(pts, dbg.Sig);
            pdb.AddMethodDebugInformation(documents[dbg.File], pdb.GetOrAddBlob(blob));
        }
        // local scopes: one scope per method listing its named locals
        var varIndex = 1;
        for (var row = 1; row <= rowCount; row++)
        {
            var mh = MetadataTokens.MethodDefinitionHandle(row);
            if (!_methodDebug.TryGetValue(mh, out var dbg)) continue;
            var named = dbg.Locals.Where(l => l.Name != null).ToList();
            if (named.Count == 0) continue;
            pdb.AddLocalScope(mh, importScope, MetadataTokens.LocalVariableHandle(varIndex), default, 0, Math.Max(1, dbg.Length));
            foreach (var l in named)
            {
                pdb.AddLocalVariable(LocalVariableAttributes.None, l.Index, pdb.GetOrAddString(l.Name!));
                varIndex++;
            }
        }
        var entryPoint = _program.EntryPoint is { } ep && _methodDefs.TryGetValue(ep, out var eh) ? eh : default;
        var pdbBuilder = new PortablePdbBuilder(pdb, metadataRoot.Sizes.RowCounts, entryPoint,
            content => BlobContentId.FromHash(SHA256.HashData(ConcatBlobs(content))[..20]));
        var pdbBlob = new BlobBuilder();
        var pdbId = pdbBuilder.Serialize(pdbBlob);
        pdbBlob.WriteContentTo(pdbStream);
        debugDir.AddCodeViewEntry(pdbFileName, pdbId, pdbBuilder.FormatVersion);
    }

    private static byte[] EncodeSequencePoints(List<SequencePoint> pts, StandaloneSignatureHandle sig)
    {
        var bb = new BlobBuilder();
        bb.WriteCompressedInteger(sig.IsNil ? 0 : MetadataTokens.GetRowNumber(sig));
        var previousOffset = 0;
        var firstPoint = true;
        var previousStartLine = 0;
        var previousStartColumn = 0;
        foreach (var p in pts)
        {
            var deltaOffset = firstPoint ? p.Offset : p.Offset - previousOffset;
            if (!firstPoint && deltaOffset < 0) continue;
            bb.WriteCompressedInteger(deltaOffset);
            previousOffset = p.Offset;
            firstPoint = false;
            if (p.IsHidden)
            {
                bb.WriteCompressedInteger(0);
                bb.WriteCompressedInteger(0);
                continue;
            }
            var dl = p.EndLine - p.StartLine;
            bb.WriteCompressedInteger(dl);
            var dc = p.EndColumn - p.StartColumn;
            if (dl == 0) bb.WriteCompressedInteger(dc); else bb.WriteCompressedSignedInteger(dc);
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
