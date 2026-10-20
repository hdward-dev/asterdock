namespace NetworkAccelerator.Module.ViewModels;

public sealed record ConnectionLine(
    string Source,
    string Target,
    string Protocol,
    string Outbound,
    string Download,
    string Upload,
    string Duration);
