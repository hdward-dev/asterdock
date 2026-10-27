namespace NetworkAccelerator.Core.Models;

public sealed record ConnectionRow(
    string Id,
    string SourceText,
    string TargetText,
    string ProtocolText,
    string OutboundText,
    long Download,
    long Upload,
    DateTimeOffset Start,
    bool IsDns);
