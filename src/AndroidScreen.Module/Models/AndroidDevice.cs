namespace AndroidScreen.Module.Models;

public sealed record AndroidDevice(string Serial, string Model, string State)
{
    public bool IsAvailable => State == "device";
    public string Label => $"{Model} · {State switch { "device" => Serial.Contains(':') ? "无线" : "USB", "unauthorized" => "请在手机上授权", "offline" => "离线", _ => State }}";
    public override string ToString() => Label;
}
