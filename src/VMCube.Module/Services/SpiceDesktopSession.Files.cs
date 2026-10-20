using System.Buffers.Binary;
using System.Text;

namespace VMCube.Module.Services;

public sealed record SpiceFileProgress(long Transferred, long Total);

public sealed partial class SpiceDesktopSession
{
    private sealed record PendingFile(uint Id, IProgress<SpiceFileProgress>? Progress)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object _fileGate = new();
    private readonly SemaphoreSlim _fileSerial = new(1, 1);
    private PendingFile? _pendingFile;
    private int _nextFileId;
    private volatile bool _fileTransferAvailable;
    private CancellationTokenSource? _transportStop;
    public bool CanTransferFiles => _ready && _fileTransferAvailable;
    public string FileTransferHint { get; private set; } = "等待 SPICE 文件传输通道；请使用包含文件传输组件的 VMCUBE。";
    public event Action<bool, string>? FileTransferAvailability;

    /// <summary>Sends one regular file through the existing SPICE agent connection. Completion means guest acknowledgement.</summary>
    public async Task SendFileAsync(string path, IProgress<SpiceFileProgress>? progress = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!CanTransferFiles) throw new InvalidOperationException(FileTransferHint);
        if (!await _fileSerial.WaitAsync(0, token)) throw new InvalidOperationException("已有文件正在发送，请等待完成或取消。");
        PendingFile? pending = null;
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full) || Directory.Exists(full)) throw new ArgumentException("请选择可读取的文件；文件夹请先压缩后发送。");
            var encoded = new UTF8Encoding(false, true).GetBytes(full);
            if (encoded.Length > 32768 || full.Contains('\0')) throw new ArgumentException("文件路径过长或包含无效字符。");
            pending = new(unchecked((uint)Interlocked.Increment(ref _nextFileId)), progress);
            if (pending.Id == 0) throw new InvalidOperationException("请重新连接后继续发送文件。");
            lock (_fileGate) _pendingFile = pending;
            var packet = new byte[20 + encoded.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(packet, 3);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), pending.Id);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), encoded.Length);
            encoded.CopyTo(packet, 20);
            await _input.Writer.WriteAsync(packet, token);
            try { await pending.Completion.Task.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                var cancel = new byte[20]; BinaryPrimitives.WriteUInt32LittleEndian(cancel, 4);
                BinaryPrimitives.WriteUInt32LittleEndian(cancel.AsSpan(4), pending.Id);
                if (!_input.Writer.TryWrite(cancel)) _transportStop?.Cancel();
                // Do not allow a second transfer to overlap an unacknowledged cancellation.
                try { await pending.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { _transportStop?.Cancel(); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException) { }
                throw;
            }
        }
        finally
        {
            lock (_fileGate) { if (ReferenceEquals(_pendingFile, pending)) _pendingFile = null; }
            _fileSerial.Release();
        }
    }
    private void ReceiveTransferEvent(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
    {
        var id = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        var state = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var current = BinaryPrimitives.ReadInt64LittleEndian(payload);
        var total = BinaryPrimitives.ReadInt64LittleEndian(payload[8..]);
        if (current < 0 || total < 0 || state > 4) throw new InvalidDataException("SPICE 文件传输状态无效。");
        var message = new UTF8Encoding(false, true).GetString(payload[16..]);
        if (id == 0 && state == 0)
        {
            _fileTransferAvailable = current == 1; FileTransferHint = message;
            FileTransferAvailability?.Invoke(_fileTransferAvailable, message); return;
        }
        if (id == 0 || state == 0 || current > total) throw new InvalidDataException("SPICE 文件传输进度无效。");
        PendingFile? pending; lock (_fileGate) pending = _pendingFile;
        if (pending?.Id != id) return;
        switch (state)
        {
            case 1: pending.Progress?.Report(new(current, total)); break;
            case 2: pending.Progress?.Report(new(total, total)); pending.Completion.TrySetResult(); break;
            case 3: pending.Completion.TrySetException(new InvalidOperationException("文件发送失败：" + message)); break;
            case 4: pending.Completion.TrySetCanceled(); break;
        }
    }
    private void EndFileTransfers()
    {
        _fileTransferAvailable = false; FileTransferHint = "连接已断开，文件传输已停止。";
        FileTransferAvailability?.Invoke(false, FileTransferHint);
        lock (_fileGate) _pendingFile?.Completion.TrySetException(new IOException(FileTransferHint));
    }
}
