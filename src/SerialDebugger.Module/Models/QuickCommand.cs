namespace SerialDebugger.Module.Models;

public sealed record QuickCommand(
    string Name,
    string Payload,
    bool IsHexMode = true,
    string EncodingName = "UTF-8",
    string LineEnding = "CRLF");
