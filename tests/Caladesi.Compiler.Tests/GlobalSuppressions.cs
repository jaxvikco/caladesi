using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Style", "IDE0010:Add missing cases", Justification = "Switches over large enums (HandleKind, ILOpCode, ...) intentionally use a default arm.")]
[assembly: SuppressMessage("Style", "IDE0072:Add missing cases", Justification = "Switch expressions over large enums intentionally use a discard arm.")]
