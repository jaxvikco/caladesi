using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Style", "IDE0130:Namespace does not match folder structure", Justification = "Files under Features/ are partial pieces of LanguageServer and share its namespace by design.")]
[assembly: SuppressMessage("Naming", "CA1724:Type names should not match namespaces", Justification = "The server type is named after the product namespace on purpose; renaming it would break every caller.", Scope = "type", Target = "~T:Caladesi.LanguageServer.LanguageServer")]
