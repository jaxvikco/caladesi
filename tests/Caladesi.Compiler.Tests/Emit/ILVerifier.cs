using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ILVerify;

namespace Caladesi.Compiler.Tests.Emit;

/// <summary>Runs ILVerify over an emitted assembly so invalid IL is reported with a method and a reason.</summary>
internal static class ILVerifier
{
    private sealed class Resolver(string selfName, byte[] self) : IResolver
    {
        private static readonly string s_runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        private readonly Dictionary<string, PEReader?> _cache = [];

        public PEReader? ResolveAssembly(AssemblyNameInfo assemblyName)
        {
            string name = assemblyName.Name ?? "";
            return _cache.TryGetValue(name, out PEReader? cached) ? cached : (_cache[name] = Load(name));
        }

        private PEReader? Load(string name)
        {
            if (name == selfName)
            {
                return new PEReader(new MemoryStream(self));
            }

            string path = Path.Combine(s_runtimeDir, name + ".dll");
            if (!File.Exists(path))
            {
                string local = Path.Combine(AppContext.BaseDirectory, name + ".dll");
                if (!File.Exists(local))
                {
                    return null;
                }

                path = local;
            }
            return new PEReader(File.OpenRead(path));
        }

        public PEReader? ResolveModule(AssemblyNameInfo referencingAssembly, string fileName)
        {
            return null;
        }
    }

    public static List<string> Verify(string assemblyName, byte[] image)
    {
        Verifier verifier = new(new Resolver(assemblyName, image), new VerifierOptions { IncludeMetadataTokensInErrorMessages = true });
        verifier.SetSystemModuleName(new AssemblyNameInfo("System.Private.CoreLib"));
        List<string> errors = [];
        using PEReader reader = new(new MemoryStream(image));
        MetadataReader md = reader.GetMetadataReader();
        HashSet<MethodDefinitionHandle> dumped = [];
        List<string> dumps = [];
        foreach (VerificationResult? r in verifier.Verify(reader))
        {
            string where = "?";
            if (!r.Method.IsNil)
            {
                MethodDefinition m = md.GetMethodDefinition(r.Method);
                where = md.GetString(md.GetTypeDefinition(m.GetDeclaringType()).Name) + "." + md.GetString(m.Name);
            }
            if (!r.Method.IsNil && dumped.Add(r.Method))
            {
                dumps.Add(ILDump.Method(reader, md, r.Method));
            }

            errors.Add($"{r.Message} [{where}] ({r.Code}) {string.Join(", ", (r.ErrorArguments ?? []).Select(a => a.Name + "=" + a.Value))}");
        }
        if (errors.Count > 0)
        {
            errors.AddRange(dumps);
        }

        return errors;
    }
}
