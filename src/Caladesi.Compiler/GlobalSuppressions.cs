using System.Diagnostics.CodeAnalysis;

// Exhaustive enum switches are impractical here: SyntaxKind, BinaryOperatorKind, MethodAttributes etc. have hundreds of members,
// and an explicit `_` arm is the intended way to say "everything else". Populating the arms would also change behavior.
[assembly: SuppressMessage("Style", "IDE0010:Add missing cases", Justification = "Switches over large enums intentionally use a default arm.")]
[assembly: SuppressMessage("Style", "IDE0072:Add missing cases", Justification = "Switch expressions over large enums intentionally use a discard arm.")]
[assembly: SuppressMessage("Style", "IDE0130:Namespace does not match folder structure", Justification = "Core compiler types (Compilation, Conversion, ...) live in the root Caladesi.Compiler namespace by design.")]
