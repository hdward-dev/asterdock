namespace NetworkAccelerator.Core.Models;

public sealed record ExitIpInfo(
    string Ip,
    string CountryCode,
    string Organization,
    string Region,
    string NetworkKind);
