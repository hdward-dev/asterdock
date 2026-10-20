using System.Diagnostics;
using VMCube.Module.Services;
using URemote.Core;

internal static class SpiceFileChecks
{
    public static async Task SendToExistingAsync(string dataRoot, string helper, string file)
    {
        using var local = new LocalVmService(dataRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var vm = (await local.ReadAsync(timeout.Token)).First(x => x.State == "running");
        var connection = await local.GetConsoleAsync(vm.Id, timeout.Token);
        var session = new SpiceDesktopSession(helperPath: helper);
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Frame += _ => frame.TrySetResult();
        session.FileTransferAvailability += (available, hint) => { Console.WriteLine("Agent: " + hint); if (available) agent.TrySetResult(); };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var running = session.RunAsync(new(LocalDesktopProtocol.Vnc, connection.Endpoint.Host, connection.Endpoint.Port, Password: connection.Password), stop.Token);
        try
        {
            await Task.WhenAll(frame.Task, agent.Task).WaitAsync(timeout.Token);
            var started = Stopwatch.StartNew();
            await session.SendFileAsync(file, new ImmediateProgress(p => Console.WriteLine($"File progress: {p.Transferred}/{p.Total}")), timeout.Token);
            Console.WriteLine($"Existing guest acknowledged file in {started.ElapsedMilliseconds}ms: {Path.GetFileName(file)}");
        }
        finally { stop.Cancel(); try { await running; } catch (OperationCanceledException) { } }
    }
    public static async Task RunAsync()
    {
        if (OperatingSystem.IsWindows()) return; // Unix protocol fixture; real worker tests run on every packaged platform.
        var root = Path.Combine(Path.GetTempPath(), "vmcube-file-checks-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var helper = Path.Combine(root, "worker");
            await File.WriteAllTextAsync(helper, """
#!/usr/bin/env python3
import sys, struct, os
r=sys.stdin.buffer; w=sys.stdout.buffer
def exact(n):
 b=r.read(n)
 if len(b)!=n: sys.exit(0)
 return b
def event(i,s,a=0,b=0,msg=''):
 p=struct.pack('<QQ',a,b)+msg.encode(); w.write(b'VMFT'+struct.pack('<III',i,s,len(p))+p);w.flush()
def frame():
 w.write(b'VMSP'+struct.pack('<III',1,1,4)+bytes([1,2,3,255]));w.flush()
exact(struct.unpack('<I',exact(4))[0])
if sys.argv[2]=='12346':
 w.write(b'VMFT'+struct.pack('<III',0,0,0xffffffff));w.flush();sys.exit(0)
event(0,0,1,0,'ready');frame()
waiting=None
while True:
 k,i,n,_,_=struct.unpack('<IIIII',exact(20))
 if k==3:
  path=exact(n).decode(); assert os.path.isabs(path); size=os.path.getsize(path)
  if 'refused' in path: event(i,3,0,0,'guest refused')
  elif 'disconnect' in path: sys.exit(0)
  elif 'cancel' in path: waiting=i;event(i,1,1,size)
  else:
   event(i,1,size//2,size);frame();event(i,1,size,size);event(i,2,size,size)
 elif k==4:
  assert waiting==i;event(i,4);waiting=None
""");
            File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var session = new SpiceDesktopSession(helperPath: helper);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var frameCount = 0;
            session.Frame += _ => { frameCount++; ready.TrySetResult(); };
            var running = session.RunAsync(new(LocalDesktopProtocol.Vnc, "127.0.0.1", 12345), timeout.Token);
            await ready.Task.WaitAsync(timeout.Token);
            if (!session.CanTransferFiles) throw new Exception("Agent availability did not reach managed transport");
            var file = Path.Combine(root, "中文 空格 ' ; $.bin"); await File.WriteAllBytesAsync(file, new byte[123456]);
            var updates = new List<SpiceFileProgress>();
            await session.SendFileAsync(file, new ImmediateProgress(updates.Add), timeout.Token);
            if (frameCount < 2 || updates.Last() != new SpiceFileProgress(123456, 123456)) throw new Exception("Transfer/frame multiplexing or UTF-8 path failed");
            try { await session.SendFileAsync(root, token: timeout.Token); throw new Exception("Directory accepted"); } catch (ArgumentException) { }
            try { await session.SendFileAsync(Path.Combine(root, "missing"), token: timeout.Token); throw new Exception("Missing file accepted"); } catch (ArgumentException) { }
            var refused = Path.Combine(root, "refused.bin"); await File.WriteAllBytesAsync(refused, [1]);
            try { await session.SendFileAsync(refused, token: timeout.Token); throw new Exception("Guest refusal ignored"); } catch (InvalidOperationException e) when (e.Message.Contains("guest refused")) { }
            var cancelled = Path.Combine(root, "cancel.bin"); await File.WriteAllBytesAsync(cancelled, new byte[100]);
            using var cancel = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var transfer = session.SendFileAsync(cancelled, new ImmediateProgress(_ => started.TrySetResult()), cancel.Token);
            await started.Task.WaitAsync(timeout.Token);
            try { await session.SendFileAsync(file, token: timeout.Token); throw new Exception("Concurrent transfer accepted"); } catch (InvalidOperationException) { }
            cancel.Cancel();
            try { await transfer.WaitAsync(timeout.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
            await session.SendFileAsync(file, token: timeout.Token);
            var disconnected = Path.Combine(root, "disconnect.bin"); await File.WriteAllBytesAsync(disconnected, [1]);
            try { await session.SendFileAsync(disconnected, token: timeout.Token); throw new Exception("Disconnect reported success"); } catch (IOException) { }
            await running.WaitAsync(timeout.Token);
            if (session.CanTransferFiles) throw new Exception("Disconnected session still permits transfers");
            try { await session.SendFileAsync(file); throw new Exception("Offline transfer accepted"); } catch (InvalidOperationException) { }
            var malformed = new SpiceDesktopSession(helperPath: helper);
            try { await malformed.RunAsync(new(LocalDesktopProtocol.Vnc, "127.0.0.1", 12346), timeout.Token); throw new Exception("Oversized file event accepted"); }
            catch (InvalidDataException) { }
            Console.WriteLine("SPICE file protocol passed: UTF-8 paths, progress/frame multiplexing, guest refusal, validation, cancellation, next transfer and disconnect.");
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class ImmediateProgress(Action<SpiceFileProgress> callback) : IProgress<SpiceFileProgress>
    { public void Report(SpiceFileProgress value) => callback(value); }
}
