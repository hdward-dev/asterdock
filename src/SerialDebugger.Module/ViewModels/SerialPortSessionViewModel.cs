using Avalonia.Media;
using Avalonia.Threading;
using SerialDebugger.Module.Models;
using SerialDebugger.Module.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Text;

namespace SerialDebugger.Module.ViewModels;

public sealed class SerialPortSessionViewModel : INotifyPropertyChanged, IDisposable
{
    private const int MaximumLogEntries = 500;
    public const double DefaultTileWidth = 720;
    public const double MinimumTileWidth = 520;
    public const double DefaultReceiveAreaHeight = 188;
    private readonly object _portGate = new();
    private readonly List<SerialLogEntry> _entries = [];
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private SerialPort? _port;
    private CancellationTokenSource? _loopCancellation;
    private bool _isRefreshingPorts;
    private bool _disposed;
    private string _title;
    private string _portName;
    private int _baudRate;
    private int _dataBits = 8;
    private string _stopBits = "1";
    private string _parity = "无";
    private string _flowControl = "无";
    private string _encodingName = "UTF-8";
    private string _lineEnding = "CRLF";
    private bool _isConnected;
    private bool _isBusy;
    private bool _isCollapsed;
    private bool _isSettingsExpanded;
    private bool _isReceiveExpanded = true;
    private bool _isSendExpanded = true;
    private bool _isQuickCommandsExpanded;
    private bool _isProtocolExpanded;
    private bool _isHexMode = true;
    private bool _showTimestamp = true;
    private bool _autoScroll = true;
    private bool _loopSendEnabled;
    private int _cycleIntervalMs = 1000;
    private string _sendText = "AA 55 01 00 FF";
    private string _searchText = string.Empty;
    private string _receiveText = "等待接收数据…";
    private string _statusText = "未连接";
    private long _txBytes;
    private long _rxBytes;
    private long _txFrames;
    private long _rxFrames;
    private long _errorCount;
    private double _tileWidth = DefaultTileWidth;
    private double _receiveAreaHeight = DefaultReceiveAreaHeight;

    public SerialPortSessionViewModel(
        string title,
        string portName,
        int baudRate,
        IBrush accentBrush)
    {
        _title = title;
        _portName = portName;
        _baudRate = baudRate;
        AccentBrush = accentBrush;
        QuickCommands =
        [
            new QuickCommand("读取设备信息", "AA 55 01 00 01"),
            new QuickCommand("启动采集", "AA 55 01 00 02"),
            new QuickCommand("停止采集", "AA 55 01 00 03"),
            new QuickCommand("设备复位", "AA 55 01 00 FF")
        ];
        RefreshPorts();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ReceiveTextUpdated;

    public static IReadOnlyList<int> BaudRateOptions { get; } =
        [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];
    public static IReadOnlyList<int> DataBitsOptions { get; } = [5, 6, 7, 8];
    public static IReadOnlyList<string> StopBitsOptions { get; } = ["1", "1.5", "2"];
    public static IReadOnlyList<string> ParityOptions { get; } = ["无", "奇校验", "偶校验", "Mark", "Space"];
    public static IReadOnlyList<string> FlowControlOptions { get; } = ["无", "RTS/CTS", "XON/XOFF"];
    public static IReadOnlyList<string> EncodingOptions { get; } = ["UTF-8", "ASCII"];
    public static IReadOnlyList<string> LineEndingOptions { get; } = ["无", "CRLF", "LF", "CR"];

    public ObservableCollection<SerialPortDevice> AvailablePorts { get; } = [];
    public ObservableCollection<QuickCommand> QuickCommands { get; }
    public IBrush AccentBrush { get; }
    public IReadOnlyList<int> BaudRates => BaudRateOptions;
    public IReadOnlyList<int> DataBitChoices => DataBitsOptions;
    public IReadOnlyList<string> StopBitChoices => StopBitsOptions;
    public IReadOnlyList<string> ParityChoices => ParityOptions;
    public IReadOnlyList<string> FlowControlChoices => FlowControlOptions;
    public IReadOnlyList<string> EncodingChoices => EncodingOptions;
    public IReadOnlyList<string> LineEndingChoices => LineEndingOptions;

    public string Title { get => _title; set => SetField(ref _title, value); }
    public string PortName
    {
        get => _portName;
        set
        {
            var normalized = value ?? string.Empty;
            if (!SetField(ref _portName, normalized)) return;
            OnPropertyChanged(nameof(SelectedPort));
            OnPropertyChanged(nameof(PortSummary));
        }
    }
    public SerialPortDevice? SelectedPort
    {
        get => AvailablePorts.FirstOrDefault(port =>
            string.Equals(port.PortName, PortName, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (_isRefreshingPorts && value is null) return;
            PortName = value?.PortName ?? string.Empty;
        }
    }
    public int BaudRate { get => _baudRate; set { if (SetField(ref _baudRate, value)) OnPropertyChanged(nameof(PortSummary)); } }
    public int DataBits { get => _dataBits; set { if (SetField(ref _dataBits, value)) OnPropertyChanged(nameof(PortSummary)); } }
    public string StopBits { get => _stopBits; set { if (SetField(ref _stopBits, value)) OnPropertyChanged(nameof(PortSummary)); } }
    public string Parity { get => _parity; set { if (SetField(ref _parity, value)) OnPropertyChanged(nameof(PortSummary)); } }
    public string FlowControl { get => _flowControl; set => SetField(ref _flowControl, value); }
    public string EncodingName { get => _encodingName; set { if (SetField(ref _encodingName, value)) RebuildReceiveText(); } }
    public string LineEnding { get => _lineEnding; set => SetField(ref _lineEnding, value); }
    public string PortSummary => $"{BaudRate} · {DataBits}{ParitySummary}{StopBits}";
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetField(ref _isConnected, value)) return;
            OnPropertyChanged(nameof(IsDisconnected));
            OnPropertyChanged(nameof(ConnectionButtonText));
            OnPropertyChanged(nameof(ConnectionStatusText));
        }
    }
    public bool IsDisconnected => !IsConnected;
    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public string ConnectionButtonText => IsConnected ? "断开" : "连接";
    public string ConnectionStatusText => IsConnected ? "已连接" : "未连接";
    public bool IsCollapsed { get => _isCollapsed; set => SetField(ref _isCollapsed, value); }
    public bool IsCardExpanded => !IsCollapsed;
    public bool IsSettingsExpanded { get => _isSettingsExpanded; set => SetField(ref _isSettingsExpanded, value); }
    public bool IsReceiveExpanded { get => _isReceiveExpanded; set => SetField(ref _isReceiveExpanded, value); }
    public bool IsSendExpanded { get => _isSendExpanded; set => SetField(ref _isSendExpanded, value); }
    public bool IsQuickCommandsExpanded { get => _isQuickCommandsExpanded; set => SetField(ref _isQuickCommandsExpanded, value); }
    public bool IsProtocolExpanded { get => _isProtocolExpanded; set => SetField(ref _isProtocolExpanded, value); }
    public bool IsHexMode
    {
        get => _isHexMode;
        set
        {
            if (!SetField(ref _isHexMode, value)) return;
            OnPropertyChanged(nameof(IsTextMode));
            RebuildReceiveText();
        }
    }
    public bool IsTextMode => !IsHexMode;
    public bool ShowTimestamp { get => _showTimestamp; set { if (SetField(ref _showTimestamp, value)) RebuildReceiveText(); } }
    public bool AutoScroll { get => _autoScroll; set => SetField(ref _autoScroll, value); }
    public bool LoopSendEnabled
    {
        get => _loopSendEnabled;
        set
        {
            if (!SetField(ref _loopSendEnabled, value)) return;
            if (!value) StopLoopSending();
        }
    }
    public int CycleIntervalMs { get => _cycleIntervalMs; set => SetField(ref _cycleIntervalMs, Math.Clamp(value, 50, 60000)); }
    public string SendText { get => _sendText; set => SetField(ref _sendText, value); }
    public string SearchText { get => _searchText; set { if (SetField(ref _searchText, value)) RebuildReceiveText(); } }
    public string ReceiveText { get => _receiveText; private set => SetField(ref _receiveText, value); }
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public long TxBytes { get => _txBytes; private set => SetField(ref _txBytes, value); }
    public long RxBytes { get => _rxBytes; private set => SetField(ref _rxBytes, value); }
    public long TxFrames { get => _txFrames; private set => SetField(ref _txFrames, value); }
    public long RxFrames { get => _rxFrames; private set => SetField(ref _rxFrames, value); }
    public long ErrorCount { get => _errorCount; private set => SetField(ref _errorCount, value); }
    public double TileWidth { get => _tileWidth; set => SetField(ref _tileWidth, Math.Max(MinimumTileWidth, value)); }
    public double ReceiveAreaHeight { get => _receiveAreaHeight; set => SetField(ref _receiveAreaHeight, Math.Max(DefaultReceiveAreaHeight, value)); }
    public string StatisticsText => $"TX {TxFrames} · RX {RxFrames} · 错误 {ErrorCount}";

    private string ParitySummary => Parity switch
    {
        "无" => "N",
        "奇校验" => "O",
        "偶校验" => "E",
        "Mark" => "M",
        "Space" => "S",
        _ => "N"
    };

    public void RefreshPorts()
    {
        try
        {
            var current = PortName;
            var ports = SerialPortDiscovery.GetConnectedPorts();

            var listChanged = AvailablePorts.Count != ports.Count ||
                              AvailablePorts.Where((port, index) =>
                                  !Equals(port, ports[index])).Any();
            if (listChanged)
            {
                _isRefreshingPorts = true;
                try
                {
                    AvailablePorts.Clear();
                    foreach (var port in ports) AvailablePorts.Add(port);
                }
                finally
                {
                    _isRefreshingPorts = false;
                }
            }

            if (!IsConnected && !string.IsNullOrWhiteSpace(current) &&
                ports.All(port => !string.Equals(port.PortName, current, StringComparison.OrdinalIgnoreCase)))
            {
                PortName = string.Empty;
                StatusText = "原串口已移除，请重新选择";
            }
            else if (AvailablePorts.Count == 0)
            {
                StatusText = "未检测到已连接的串口设备";
            }

            OnPropertyChanged(nameof(SelectedPort));
        }
        catch (Exception exception)
        {
            StatusText = $"扫描失败：{exception.GetBaseException().Message}";
        }
    }

    public async Task ToggleConnectionAsync()
    {
        if (IsBusy) return;
        if (IsConnected)
        {
            await DisconnectAsync().ConfigureAwait(false);
            return;
        }

        await ConnectAsync().ConfigureAwait(false);
    }

    public async Task ConnectAsync()
    {
        if (IsConnected || IsBusy) return;
        RefreshPorts();
        if (string.IsNullOrWhiteSpace(PortName))
        {
            StatusText = AvailablePorts.Count == 0 ? "未检测到已连接的串口设备" : "请选择串口";
            return;
        }
        if (AvailablePorts.All(port => !string.Equals(port.PortName, PortName, StringComparison.OrdinalIgnoreCase)))
        {
            PortName = string.Empty;
            StatusText = "串口已不存在，请重新选择";
            return;
        }

        IsBusy = true;
        StatusText = "正在连接…";
        try
        {
            var port = await Task.Run(CreateAndOpenPort).ConfigureAwait(false);
            if (_disposed)
            {
                port.Dispose();
                return;
            }

            lock (_portGate) _port = port;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsConnected = true;
                StatusText = $"{PortName} 已连接";
            });
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ErrorCount++;
                OnPropertyChanged(nameof(StatisticsText));
                StatusText = $"连接失败：{exception.GetBaseException().Message}";
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    public async Task DisconnectAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StopLoopSending();
        SerialPort? port;
        lock (_portGate)
        {
            port = _port;
            _port = null;
        }

        try
        {
            if (port is not null) await Task.Run(() => ClosePort(port)).ConfigureAwait(false);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsConnected = false;
                IsBusy = false;
                StatusText = "未连接";
            });
        }
    }

    public async Task TriggerSendAsync(Func<byte[], Task>? linkedSend = null)
    {
        byte[] payload;
        try
        {
            payload = BuildPayload();
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
            return;
        }

        try
        {
            await SendPayloadAsync(payload).ConfigureAwait(false);
            if (linkedSend is not null) await linkedSend(payload).ConfigureAwait(false);
            if (LoopSendEnabled) StartLoopSending(payload, linkedSend);
        }
        catch (Exception exception)
        {
            await ReportErrorAsync($"发送失败：{exception.GetBaseException().Message}").ConfigureAwait(false);
        }
    }

    public async Task SendQuickCommandAsync(QuickCommand command, Func<byte[], Task>? linkedSend = null)
    {
        SendText = command.Payload;
        IsHexMode = true;
        await TriggerSendAsync(linkedSend).ConfigureAwait(false);
    }

    public async Task SendPayloadAsync(byte[] payload)
    {
        SerialPort port;
        lock (_portGate)
            port = _port ?? throw new InvalidOperationException($"{Title} 尚未连接");

        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() => port.Write(payload, 0, payload.Length)).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => AddEntryCore("TX", payload));
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public void ClearLogs()
    {
        _entries.Clear();
        TxBytes = 0;
        RxBytes = 0;
        TxFrames = 0;
        RxFrames = 0;
        ErrorCount = 0;
        ReceiveText = "等待接收数据…";
        OnPropertyChanged(nameof(StatisticsText));
        ReceiveTextUpdated?.Invoke(this, EventArgs.Empty);
    }

    public SerialPortProfile CreateProfile() => new(
        PortName,
        BaudRate,
        DataBits,
        StopBits,
        Parity,
        FlowControl,
        EncodingName,
        LineEnding,
        SendText,
        TileWidth);

    public void ApplyProfile(SerialPortProfile profile)
    {
        PortName = profile.PortName;
        BaudRate = profile.BaudRate;
        DataBits = profile.DataBits;
        StopBits = profile.StopBits;
        Parity = profile.Parity;
        FlowControl = profile.FlowControl;
        EncodingName = profile.EncodingName;
        LineEnding = profile.LineEnding;
        SendText = profile.SendText;
        TileWidth = profile.TileWidth;
        RefreshPorts();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopLoopSending();
        SerialPort? port;
        lock (_portGate)
        {
            port = _port;
            _port = null;
        }
        if (port is not null) ClosePort(port);
        IsConnected = false;
    }

    private SerialPort CreateAndOpenPort()
    {
        var port = new SerialPort(PortName, BaudRate, ParseParity(), DataBits, ParseStopBits())
        {
            Handshake = ParseHandshake(),
            ReadTimeout = 500,
            WriteTimeout = 1000
        };
        port.DataReceived += Port_DataReceived;
        port.ErrorReceived += Port_ErrorReceived;
        try
        {
            port.Open();
            return port;
        }
        catch
        {
            port.DataReceived -= Port_DataReceived;
            port.ErrorReceived -= Port_ErrorReceived;
            port.Dispose();
            throw;
        }
    }

    private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (sender is not SerialPort port) return;
        try
        {
            var count = port.BytesToRead;
            if (count <= 0) return;
            var bytes = new byte[count];
            var read = port.Read(bytes, 0, bytes.Length);
            if (read <= 0) return;
            if (read != bytes.Length) Array.Resize(ref bytes, read);
            var captured = bytes;
            Dispatcher.UIThread.Post(() => AddEntryCore("RX", captured), DispatcherPriority.Background);
        }
        catch (Exception exception)
        {
            _ = ReportErrorAsync($"接收失败：{exception.GetBaseException().Message}");
        }
    }

    private void Port_ErrorReceived(object sender, SerialErrorReceivedEventArgs e) =>
        _ = ReportErrorAsync($"串口错误：{e.EventType}");

    private void AddEntryCore(string direction, byte[] payload)
    {
        var copy = payload.ToArray();
        _entries.Add(new SerialLogEntry(DateTimeOffset.Now, direction, copy));
        while (_entries.Count > MaximumLogEntries) _entries.RemoveAt(0);

        if (direction == "RX")
        {
            RxBytes += copy.Length;
            RxFrames++;
        }
        else
        {
            TxBytes += copy.Length;
            TxFrames++;
        }
        OnPropertyChanged(nameof(StatisticsText));
        StatusText = direction == "RX" ? $"收到 {copy.Length} 字节" : $"已发送 {copy.Length} 字节";
        RebuildReceiveText();
    }

    private void RebuildReceiveText()
    {
        var filter = SearchText.Trim();
        var lines = _entries.Select(FormatEntry);
        if (filter.Length > 0)
            lines = lines.Where(line => line.Contains(filter, StringComparison.OrdinalIgnoreCase));
        var text = string.Join(Environment.NewLine, lines);
        ReceiveText = text.Length == 0 ? "等待接收数据…" : text;
        ReceiveTextUpdated?.Invoke(this, EventArgs.Empty);
    }

    private string FormatEntry(SerialLogEntry entry)
    {
        var prefix = ShowTimestamp ? $"[{entry.Timestamp:HH:mm:ss.fff}]  " : string.Empty;
        var body = IsHexMode
            ? Convert.ToHexString(entry.Payload).InsertSeparators()
            : GetEncoding().GetString(entry.Payload).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        return $"{prefix}{entry.Direction}  {body}";
    }

    private byte[] BuildPayload()
    {
        if (!IsHexMode)
        {
            var ending = LineEnding switch
            {
                "CRLF" => "\r\n",
                "LF" => "\n",
                "CR" => "\r",
                _ => string.Empty
            };
            var text = SendText + ending;
            if (text.Length == 0) throw new InvalidOperationException("请输入发送内容");
            return GetEncoding().GetBytes(text);
        }

        var compact = SendText.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);
        compact = new string(compact.Where(character => !char.IsWhiteSpace(character) && character is not ',' and not '-' and not ':').ToArray());
        if (compact.Length == 0) throw new InvalidOperationException("请输入 HEX 数据");
        if (compact.Length % 2 != 0) throw new InvalidOperationException("HEX 数据必须由完整字节组成");
        if (compact.Any(character => !Uri.IsHexDigit(character))) throw new InvalidOperationException("HEX 数据包含无效字符");

        var result = new byte[compact.Length / 2];
        for (var index = 0; index < result.Length; index++)
            result[index] = byte.Parse(compact.AsSpan(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return result;
    }

    private void StartLoopSending(byte[] payload, Func<byte[], Task>? linkedSend)
    {
        StopLoopSending();
        _loopCancellation = new CancellationTokenSource();
        var token = _loopCancellation.Token;
        var interval = TimeSpan.FromMilliseconds(CycleIntervalMs);
        _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(interval);
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    await SendPayloadAsync(payload).ConfigureAwait(false);
                    if (linkedSend is not null) await linkedSend(payload).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                await ReportErrorAsync($"循环发送已停止：{exception.GetBaseException().Message}").ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => LoopSendEnabled = false);
            }
        }, token);
    }

    private void StopLoopSending()
    {
        var cancellation = Interlocked.Exchange(ref _loopCancellation, null);
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private async Task ReportErrorAsync(string message)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ErrorCount++;
            OnPropertyChanged(nameof(StatisticsText));
            StatusText = message;
        });
    }

    private Encoding GetEncoding() => EncodingName == "ASCII" ? Encoding.ASCII : Encoding.UTF8;
    private Parity ParseParity() => Parity switch
    {
        "奇校验" => System.IO.Ports.Parity.Odd,
        "偶校验" => System.IO.Ports.Parity.Even,
        "Mark" => System.IO.Ports.Parity.Mark,
        "Space" => System.IO.Ports.Parity.Space,
        _ => System.IO.Ports.Parity.None
    };
    private System.IO.Ports.StopBits ParseStopBits() => StopBits switch
    {
        "1.5" => System.IO.Ports.StopBits.OnePointFive,
        "2" => System.IO.Ports.StopBits.Two,
        _ => System.IO.Ports.StopBits.One
    };
    private Handshake ParseHandshake() => FlowControl switch
    {
        "RTS/CTS" => Handshake.RequestToSend,
        "XON/XOFF" => Handshake.XOnXOff,
        _ => Handshake.None
    };

    private void ClosePort(SerialPort port)
    {
        port.DataReceived -= Port_DataReceived;
        port.ErrorReceived -= Port_ErrorReceived;
        try
        {
            if (port.IsOpen) port.Close();
        }
        catch
        {
        }
        finally
        {
            port.Dispose();
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName == nameof(IsCollapsed)) OnPropertyChanged(nameof(IsCardExpanded));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed record SerialLogEntry(DateTimeOffset Timestamp, string Direction, byte[] Payload);
}

public sealed record SerialPortProfile(
    string PortName,
    int BaudRate,
    int DataBits,
    string StopBits,
    string Parity,
    string FlowControl,
    string EncodingName,
    string LineEnding,
    string SendText,
    double TileWidth = SerialPortSessionViewModel.DefaultTileWidth);

internal static class HexTextExtensions
{
    public static string InsertSeparators(this string value)
    {
        if (value.Length <= 2) return value;
        var builder = new StringBuilder(value.Length + value.Length / 2);
        for (var index = 0; index < value.Length; index += 2)
        {
            if (index > 0) builder.Append(' ');
            builder.Append(value, index, Math.Min(2, value.Length - index));
        }
        return builder.ToString();
    }
}
