using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Weft.BrowserQualification;

// Source-linked into the owning tests; neither assembly references the other.
internal sealed class QualificationFailure : Exception
{
    internal QualificationFailure() : base("Owned HTTPS qualification failed.") { }
    public override string ToString() => nameof(QualificationFailure);
}

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal static class QualificationWire
{
    internal const int Maximum = 65_536;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    internal static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition) { if (!condition) throw new QualificationFailure(); }
    internal static T Parse<T>(ReadOnlySpan<byte> bytes)
    {
        Require(bytes.Length is > 0 and <= Maximum);
        _ = new UTF8Encoding(false, true).GetString(bytes);
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        Unique(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new QualificationFailure();
    }
    private static void Unique(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject()) { Require(names.Add(property.Name)); Unique(property.Value); }
        }
        else if (item.ValueKind == JsonValueKind.Array) foreach (var child in item.EnumerateArray()) Unique(child);
    }
    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, token);
        int size = BinaryPrimitives.ReadInt32BigEndian(prefix); Require(size is > 0 and <= Maximum);
        var bytes = new byte[size];
        try { await stream.ReadExactlyAsync(bytes, token); return Parse<T>(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        try
        {
            Require(bytes.Length is > 0 and <= Maximum);
            var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, bytes.Length);
            await stream.WriteAsync(prefix, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static async Task<QualificationReply> CallAsync(string root, string path, QualificationRequest request)
    {
        request.Validate(); PrivateQualificationFiles.RequireSocket(root, path);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), budget.Token);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await WriteAsync(stream, request, budget.Token);
        socket.Shutdown(SocketShutdown.Send);
        var response = await ReadAsync<QualificationReply>(stream, budget.Token);
        QualificationWire.Require(await stream.ReadAsync(new byte[1], budget.Token) == 0);
        Require(response.Version == 1 && response.Outcome == "ok"); return response;
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed partial record QualificationRequest
{
    public required int Version { get; init; }
    public required string Operation { get; init; }
    public required string? AuthorizationUrl { get; init; }
    public required string? TokenCase { get; init; }
    internal void Validate()
    {
        QualificationWire.Require(Version == 1 && Operation is "authorize" or "counts" or "restart" or "stop" or "register" or "withdraw" or "seal" or "replay" or "networkBegin" or "revoke" or "barrier");
        QualificationWire.Require(Operation is "register" or "withdraw" ? Admission is not null : Admission is null);
        Admission?.Validate();
        QualificationWire.Require(Operation == "authorize" ? CallbackCase is "good" or "badState" : CallbackCase is null);
        QualificationWire.Require(Operation == "authorize"
            ? AuthorizationUrl is { Length: > 0 and <= 16384 } && TokenCase is "good" or "badNonce"
            : AuthorizationUrl is null && TokenCase is null);
    }
    internal static QualificationRequest Control(string operation) => new()
    { Version = 1, Operation = operation, AuthorizationUrl = null, TokenCase = null, Admission = null, CallbackCase = null };
}

internal sealed partial record QualificationReply
{
    public required int Version { get; init; }
    public required string Outcome { get; init; }
    public int Status { get; init; }
    public string? CallbackUrl { get; init; }
    public int AuthorizationRequests { get; init; }
    public int TokenRequests { get; init; }
    public int AssertionsAccepted { get; init; }
    public int PkceAccepted { get; init; }
    public int Unexpected { get; init; }
    public long SessionRows { get; init; }
    public long ActiveRows { get; init; }
    public long RevokedRows { get; init; }
    public bool EpochChanged { get; init; }
    public bool RingUnchanged { get; init; }
    public int ProcessGeneration { get; init; }
    internal static QualificationReply Ok() => new() { Version = 1, Outcome = "ok" };
}

internal sealed partial record QualificationHostInput
{
    public required int Version { get; init; }
    public required string Root { get; init; }
    public required string SocketPath { get; init; }
    public required string Origin { get; init; }
    public required string WebRoot { get; init; }
    public required Dictionary<string, string> Assets { get; init; }
    public required Guid Organization { get; init; }
    public required string ApplicationName { get; init; }
    public required string KeyDirectory { get; init; }
    public required string DataCertificateFile { get; init; }
    public required string DataKeyFile { get; init; }
    public required string TlsCertificateFile { get; init; }
    public required string TlsKeyFile { get; init; }
    public required string ReaderConnection { get; init; }
    public required string WriterConnection { get; init; }
    public required string IdentityConnection { get; init; }
    public required string CatalogConnection { get; init; }
}

internal sealed record QualificationAdmission
{
    public required int Version { get; init; }
    public required bool Approved { get; init; }
    public required string Root { get; init; }
    public required string SourceRoot { get; init; }
    public required string ProbeAssembly { get; init; }
    public required string ProbeSha256 { get; init; }
    public required string ServerAssembly { get; init; }
    public required string ServerSha256 { get; init; }
    public required Dictionary<string, string> Assets { get; init; }
    public required string Node { get; init; }
    public required string NodeSha256 { get; init; }
    public required string Npm { get; init; }
    public required string NpmSha256 { get; init; }
    public required string Chromium { get; init; }
    public required string ChromiumSha256 { get; init; }
    public required string Certutil { get; init; }
    public required string CertutilSha256 { get; init; }
}

// One length-prefixed request/response per connection. A second frame is not dispatched.
// Accepted sockets and queued handlers remain owned even after the caller disconnects.
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed partial class QualificationSocketServer : IAsyncDisposable
{
    private readonly Socket listener;
    private readonly string path;
    private readonly CancellationTokenSource intake = new();
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly List<Task> workers = [];
    private readonly Func<QualificationRequest, Task<QualificationReply>> handler;
    private readonly Task accepting;
    private Task? closing;
    private int messages;
    internal bool Settled { get; private set; }
    internal QualificationSocketServer(string root, string path, Func<QualificationRequest, Task<QualificationReply>> handler)
    {
        PrivateQualificationFiles.RequireChild(root, path); this.path = path; this.handler = handler;
        listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            PrivateQualificationFiles.RequireSocket(root, path); listener.Listen(8); accepting = AcceptAsync();
        }
        catch { listener.Dispose(); throw new QualificationFailure(); }
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var socket = await listener.AcceptAsync(intake.Token);
                if (Interlocked.Increment(ref messages) > 2048) { socket.Dispose(); throw new QualificationFailure(); }
                var original = ServeAsync(socket);
                // The original task catches and categorizes its own failure before returning.
                lock (workers) workers.Add(original);
            }
        }
        catch (OperationCanceledException) when (intake.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (intake.IsCancellationRequested) { }
        catch (SocketException) when (intake.IsCancellationRequested) { }
        catch { Interlocked.Increment(ref failed); }
    }
    private async Task ServeAsync(Socket socket)
    {
        using (socket)
        await using (var stream = new NetworkStream(socket, ownsSocket: false))
        {
            bool entered = false;
            try
            {
                using var read = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var request = await QualificationWire.ReadAsync<QualificationRequest>(stream, read.Token); request.Validate();
                QualificationWire.Require(await stream.ReadAsync(new byte[1], read.Token) == 0);
                await admission.WaitAsync(); entered = true;
                var original = handler(request); var response = await original;
                using var write = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                Task originalWrite = QualificationWire.WriteAsync(stream, response, write.Token);
                try { await originalWrite; }
                catch
                {
                    // Count only a fault/cancellation of the actual owned write,
                    // never merely a caller disconnect or handler failure.
                    Interlocked.Increment(ref failedWrites); throw;
                }
            }
            catch { Interlocked.Increment(ref failed); }
            finally { if (entered) admission.Release(); }
        }
    }
    public ValueTask DisposeAsync() => new(CloseAsync());
}

// Linux standard O_NOFOLLOW/openat/statx. No product private-file implementation is copied.
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal static class PrivateQualificationFiles
{
    private const int DirectoryFlag = 0x10000, NoFollow = 0x20000, CloseExec = 0x80000, NonBlock = 0x800;
    private const int EmptyPath = 0x1000, NoFollowStat = 0x100;
    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct Stat
    {
        internal uint Mask, BlockSize;
        internal ulong Attributes;
        internal uint Links, Uid, Gid;
        internal ushort Mode, Spare;
        internal ulong Inode, Size;
    }
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)] private static extern int OpenAt(int fd, string path, int flags);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] private static extern int StatAt(int fd, string path, int flags, uint mask, out Stat value);
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint User();
    private static SafeFileHandle Handle(int fd)
    { QualificationWire.Require(fd >= 0); return new SafeFileHandle((IntPtr)fd, ownsHandle: true); }
    private static Stat Inspect(SafeFileHandle handle)
    {
        QualificationWire.Require(StatAt(handle.DangerousGetHandle().ToInt32(), "", EmptyPath | NoFollowStat, 0x7ff, out var value) == 0);
        return value;
    }
    private static SafeFileHandle Root(string root)
    {
        QualificationWire.Require(Path.IsPathFullyQualified(root) && Path.GetFullPath(root) == root && root != "/");
        SafeFileHandle current = Handle(Open("/", DirectoryFlag | NoFollow | CloseExec));
        try
        {
            foreach (string part in root[1..].Split('/'))
            {
                QualificationWire.Require(part.Length > 0 && part is not ("." or ".."));
                var next = Handle(OpenAt(current.DangerousGetHandle().ToInt32(), part, DirectoryFlag | NoFollow | CloseExec));
                current.Dispose(); current = next;
            }
            var value = Inspect(current);
            QualificationWire.Require(value.Uid == User() && (value.Mode & 0xffff) == 0x41c0); // directory 0700
            return current;
        }
        catch { current.Dispose(); throw; }
    }
    internal static void RequireChild(string root, string path)
    {
        using var directory = Root(root);
        QualificationWire.Require(Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path && Path.GetDirectoryName(path) == root);
    }
    internal static byte[] Read(string root, string path)
    {
        RequireChild(root, path); using var directory = Root(root);
        using var handle = Handle(OpenAt(directory.DangerousGetHandle().ToInt32(), Path.GetFileName(path), NoFollow | CloseExec | NonBlock));
        var before = Inspect(handle);
        QualificationWire.Require(before.Uid == User() && before.Mode == 0x8180 && before.Links == 1 && before.Size is > 0 and <= QualificationWire.Maximum);
        var bytes = new byte[checked((int)before.Size)];
        int offset = 0;
        while (offset < bytes.Length)
        { int read = RandomAccess.Read(handle, bytes.AsSpan(offset), offset); QualificationWire.Require(read > 0); offset += read; }
        Span<byte> extra = stackalloc byte[1];
        QualificationWire.Require(RandomAccess.Read(handle, extra, offset) == 0);
        var after = Inspect(handle); QualificationWire.Require(after.Size == before.Size && after.Inode == before.Inode && after.Mode == before.Mode && after.Links == 1);
        return bytes;
    }
    internal static void RequireSocket(string root, string path)
    {
        RequireChild(root, path); using var directory = Root(root);
        QualificationWire.Require(StatAt(directory.DangerousGetHandle().ToInt32(), Path.GetFileName(path), NoFollowStat, 0x7ff, out var value) == 0
            && value.Uid == User() && value.Mode == 0xc180);
    }
    internal static T Input<T>(string root, string path)
    { var bytes = Read(root, path); try { return QualificationWire.Parse<T>(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); } }
}
