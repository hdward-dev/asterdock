using System.Text.Json.Nodes;

namespace NetworkAccelerator.Core.Models;

public sealed record ProxyNode(
    string Tag,
    string Type,
    string Server,
    int ServerPort,
    JsonObject Outbound);
