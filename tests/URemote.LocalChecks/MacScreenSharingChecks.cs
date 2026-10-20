using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using URemote.Core;

internal static class MacScreenSharingChecks
{
    private const string VersionError = "仅支持 RFB 3.3、3.7 和 3.8 的 VNC 服务。";
    private const string AppleUsernameError = "连接 macOS 屏幕共享需要这台 Mac 的用户名和密码。";
    private const string UnsupportedError = "该 VNC 服务需要暂不支持的认证方式；请选择 VNC 密码认证。";
    private static readonly byte[] Prime = CreatePrime();
    private static readonly BigInteger PrimeValue = new(Prime, isUnsigned: true, isBigEndian: true);

    public static async Task Run(Action<bool, string> check)
    {
        await Version(check, "RFB 003.003\n", "RFB 003.003\n", 3);
        await Version(check, "RFB 003.007\n", "RFB 003.007\n", 7);
        await Version(check, "RFB 003.008\n", "RFB 003.008\n", 8);
        await Version(check, "RFB 003.889\n", "RFB 003.008\n", 8);
        await Reject(check, "", async (stream, token) => await Write(stream, Encoding.ASCII.GetBytes("RFB 004.000\n"), token), VersionError);
        await Reject(check, "", async (stream, token) =>
        {
            await Exchange(stream, "RFB 003.003\n", "RFB 003.003\n", token);
            await Write(stream, Reason(4, "连接被拒绝"), token);
        }, "连接被拒绝");
        await Reject(check, "", async (stream, token) =>
        {
            await Exchange(stream, "RFB 003.007\n", "RFB 003.007\n", token);
            await Write(stream, Reason(1, "服务暂时不可用"), token);
        }, "服务暂时不可用");
        await Reject(check, "", async (stream, token) =>
        {
            await Exchange(stream, "RFB 003.008\n", "RFB 003.008\n", token);
            await Write(stream, [0], token);
            await Write(stream, [0, 0, 0, 0], token);
        }, "VNC 服务拒绝连接。");
        await Reject(check, "secret", async (stream, token) =>
        {
            await Exchange(stream, "RFB 003.008\n", "RFB 003.008\n", token);
            await Write(stream, [1, 1], token);
            check((await Read(stream, 1, token))[0] == 1, "RFB 3.8 None still reports the selected type before SecurityResult");
            await Write(stream, [0, 0, 0, 1], token);
            await Write(stream, Reason(0, "没有权限"), token);
        }, "没有权限");
        await Reject(check, "secret", async (stream, token) =>
        {
            await Exchange(stream, "RFB 003.007\n", "RFB 003.007\n", token);
            await Write(stream, [1, 2], token);
            check((await Read(stream, 1, token))[0] == 2, "RFB 3.7 password authentication is selected");
            var challenge = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
            await Write(stream, challenge, token);
            check((await Read(stream, 16, token)).SequenceEqual(VncAuthentication.Respond("secret", challenge)), "RFB 3.7 password response still matches DES");
            await Write(stream, [0, 0, 0, 1], token);
        }, "VNC 认证失败，请检查密码。");

        await Select(check, [30, 2, 1], "张三", 30);
        await Select(check, [30, 2, 1], "", 2);
        await Select(check, [30, 1], "", 1);
        await Select(check, [2, 1], "张三", 2);
        await Reject(check, "", async (stream, token) =>
        {
            await Exchange(stream, "RFB 003.008\n", "RFB 003.008\n", token);
            await Write(stream, [1, 30], token);
        }, AppleUsernameError);
        await Reject(check, "", async (stream, token) =>
        {
            await Exchange(stream, "RFB 003.003\n", "RFB 003.003\n", token);
            await Write(stream, [0, 0, 0, 30], token);
        }, AppleUsernameError);
        await Reject(check, "张三", async (stream, token) =>
        {
            await Exchange(stream, "RFB 003.008\n", "RFB 003.008\n", token);
            await Write(stream, [2, 33, 36], token);
        }, UnsupportedError);

        await Apple(check, accept: true);
        await Apple(check, accept: false);
    }

    private static async Task Version(Action<bool, string> check, string banner, string reply, int semantics)
    {
        await Complete(check, "", "", async (stream, token) =>
        {
            await Exchange(stream, banner, reply, token);
            check(true, $"{banner.Trim()} negotiates {reply.Trim()}");
            if (semantics == 3) await Write(stream, [0, 0, 0, 1], token);
            else
            {
                await Write(stream, [1, 1], token);
                check((await Read(stream, 1, token))[0] == 1, $"{banner.Trim()} selects no authentication");
            }
            if (semantics == 8) await Write(stream, [0, 0, 0, 0], token);
            check((await Read(stream, 1, token))[0] == 1, $"{banner.Trim()} uses RFB 3.{semantics} semantics");
        });
    }

    private static async Task Select(Action<bool, string> check, byte[] types, string username, byte expected)
    {
        await Complete(check, username, "secret", async (stream, token) =>
        {
            await Exchange(stream, "RFB 003.008\n", "RFB 003.008\n", token);
            await Write(stream, [(byte)types.Length, .. types], token);
            var chosen = (await Read(stream, 1, token))[0];
            check(chosen == expected, $"security types {string.Join(",", types)} with user '{username}' select {expected}");
            if (expected == 30) return;
            if (expected == 2)
            {
                var challenge = Enumerable.Range(8, 16).Select(i => (byte)i).ToArray();
                await Write(stream, challenge, token);
                check((await Read(stream, 16, token)).SequenceEqual(VncAuthentication.Respond("secret", challenge)), "password authentication follows when Apple authentication is not selected");
            }
            await Write(stream, [0, 0, 0, 0], token);
            check((await Read(stream, 1, token))[0] == 1, "selected authentication reaches the shared flag");
        });
    }

    private static async Task Apple(Action<bool, string> check, bool accept)
    {
        const ushort generator = 2;
        var serverPrivate = new BigInteger(5);
        var serverPublic = Fixed(BigInteger.ModPow(generator, serverPrivate, PrimeValue), Prime.Length);
        var act = async (NetworkStream stream, CancellationToken token) =>
        {
            await Exchange(stream, "RFB 003.889\n", "RFB 003.008\n", token);
            await Write(stream, [1, 30], token);
            check((await Read(stream, 1, token))[0] == 30, "macOS RFB 003.889 with a username selects Apple Remote Desktop");
            var header = new byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(header, generator);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)Prime.Length);
            await Write(stream, header, token);
            await Write(stream, Prime, token);
            await Write(stream, serverPublic, token);
            var ciphertext = await Read(stream, 128, token);
            var clientPublic = new BigInteger(await Read(stream, Prime.Length, token), isUnsigned: true, isBigEndian: true);
            check(clientPublic > 1 && clientPublic < PrimeValue, "client Diffie-Hellman public key is in range");
            var shared = Fixed(BigInteger.ModPow(clientPublic, serverPrivate, PrimeValue), Prime.Length);
            using var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = MD5.HashData(shared);
            var plain = aes.CreateDecryptor().TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            try
            {
                check(Field(plain, 0) == "张三" && Field(plain, 64) == "päss", "Apple Remote Desktop ciphertext decrypts to the username and password");
                check(RandomPadding(plain, 0) && RandomPadding(plain, 64), "credential fields are NUL-terminated and padded with random bytes");
            }
            finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(shared); }
            if (accept)
            {
                await Write(stream, [0, 0, 0, 0], token);
                check((await Read(stream, 1, token))[0] == 1, "accepted Apple authentication continues the RFB handshake");
                return;
            }
            await Write(stream, [0, 0, 0, 1], token);
            await Write(stream, Reason(0, "账户或密码不正确"), token);
        };
        if (accept) await Complete(check, "张三", "päss", act);
        else await Reject(check, "päss", act, "账户或密码不正确", "张三");
    }

    private static async Task Complete(Action<bool, string> check, string username, string password, Func<NetworkStream, CancellationToken, Task> server) =>
        await Run(username, password, server, error =>
        {
            if (error is not (IOException or SocketException or ObjectDisposedException)) throw error ?? new Exception("FAIL: client ended before the server closed");
        });

    private static async Task Reject(Action<bool, string> check, string password, Func<NetworkStream, CancellationToken, Task> server, string message, string username = "") =>
        await Run(username, password, async (stream, token) =>
        {
            await server(stream, token);
            var one = new byte[1];
            try
            {
                if (await stream.ReadAsync(one, token) != 0) throw new Exception("FAIL: client continued after " + message);
            }
            catch (IOException) { }
        }, error => check(error is not null && error.Message.Contains(message, StringComparison.Ordinal), message));

    private static async Task Run(string username, string password, Func<NetworkStream, CancellationToken, Task> server, Action<Exception?> assert)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var running = new VncDesktopSession().RunAsync(new(LocalDesktopProtocol.Vnc, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, username, password), timeout.Token);
        using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
        tcp.NoDelay = true;
        Exception? serverError = null;
        try { await server(tcp.GetStream(), timeout.Token); }
        catch (Exception e) { serverError = e; }
        finally { tcp.Close(); }
        Exception? clientError = null;
        try { await running.WaitAsync(timeout.Token); }
        catch (Exception e) { clientError = e; }
        if (serverError is not null) throw serverError;
        assert(clientError);
    }

    private static async Task Exchange(NetworkStream stream, string banner, string reply, CancellationToken token)
    {
        await Write(stream, Encoding.ASCII.GetBytes(banner), token);
        var actual = Encoding.ASCII.GetString(await Read(stream, 12, token));
        if (actual != reply) throw new Exception($"FAIL: expected {reply.Trim()} but client sent {actual.Trim()}");
    }

    private static byte[] Reason(int prefix, string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var packet = new byte[prefix + 4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(prefix), (uint)body.Length);
        body.CopyTo(packet, prefix + 4);
        return packet;
    }

    private static byte[] CreatePrime()
    {
        var prime = new byte[16];
        prime[13] = 0x01;
        prime[15] = 0x01;
        return prime;
    }

    private static byte[] Fixed(BigInteger value, int length)
    {
        var raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length == length) return raw;
        if (raw.Length > length) return raw[^length..];
        var padded = new byte[length];
        raw.CopyTo(padded.AsSpan(length - raw.Length));
        return padded;
    }

    private static string Field(byte[] plain, int offset)
    {
        var end = Array.IndexOf(plain, (byte)0, offset, 64);
        if (end < 0) throw new Exception("FAIL: credential field is not NUL-terminated");
        return Encoding.UTF8.GetString(plain, offset, end - offset);
    }

    private static bool RandomPadding(byte[] plain, int offset)
    {
        var end = Array.IndexOf(plain, (byte)0, offset, 64);
        if (end < 0 || end == offset + 63) return false;
        for (var i = end + 1; i < offset + 64; i++) if (plain[i] != 0) return true;
        return false;
    }

    private static async Task<byte[]> Read(NetworkStream stream, int size, CancellationToken token)
    {
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    private static async Task Write(NetworkStream stream, byte[] bytes, CancellationToken token)
    {
        foreach (var value in bytes) await stream.WriteAsync(new[] { value }, token);
    }
}
