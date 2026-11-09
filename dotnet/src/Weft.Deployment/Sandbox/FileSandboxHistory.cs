using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weft.Deployment.Sandbox;

/// <summary>
/// Sandbox history in a private local directory: one JSON file per message under its target, at most
/// <see cref="MaximumPerTarget"/> per target, oldest removed first. Local simulation only; inputs and outputs are kept.
/// </summary>
public sealed class FileSandboxHistory : ISandboxHistory
{
    public const int MaximumPerTarget = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly string root;
    private readonly SemaphoreSlim gate = new(1, 1);

    public FileSandboxHistory(string root)
    {
        if (!Path.IsPathFullyQualified(root) || Path.GetFullPath(root) != root) throw new ArgumentException("The sandbox history directory must be an absolute path.");
        this.root = root;
    }

    public async Task DeliverAsync(SandboxMessage message, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await gate.WaitAsync(token);
        try
        {
            var directory = Path.Combine(root, message.TargetId.ToString("D"));
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var path = Path.Combine(directory, message.SentAt.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) + "-" + message.MessageId.ToString("D") + ".json");
            var partial = path + ".partial";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            try
            {
                await using (var stream = new FileStream(partial, options)) await JsonSerializer.SerializeAsync(stream, message, Json, token);
                File.Move(partial, path, overwrite: true);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
            foreach (var old in Directory.EnumerateFiles(directory, "*-" + message.MessageId.ToString("D") + ".json").Where(old => old != path)) File.Delete(old);
            foreach (var expired in Files(directory).Skip(MaximumPerTarget)) File.Delete(expired);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<SandboxMessage>> ListAsync(Guid targetId, int limit, CancellationToken token = default)
    {
        if (limit is < 1 or > LocalSandboxService.MaximumHistoryPage) throw new ArgumentException("Invalid history limit.");
        await gate.WaitAsync(token);
        try
        {
            var directory = Path.Combine(root, targetId.ToString("D"));
            if (!Directory.Exists(directory)) return [];
            var result = new List<SandboxMessage>();
            foreach (var path in Files(directory).Take(limit))
                if (await ReadFileAsync(path, token) is { } message) result.Add(message);
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task<SandboxMessage?> ReadAsync(Guid targetId, Guid messageId, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            var directory = Path.Combine(root, targetId.ToString("D"));
            if (!Directory.Exists(directory)) return null;
            var path = Directory.EnumerateFiles(directory, "*-" + messageId.ToString("D") + ".json").SingleOrDefault();
            return path is null ? null : await ReadFileAsync(path, token) is { } message && message.TargetId == targetId && message.MessageId == messageId ? message : null;
        }
        finally { gate.Release(); }
    }

    // File names start with the send time, so name order is newest first.
    private static IEnumerable<string> Files(string directory) =>
        Directory.EnumerateFiles(directory, "*.json").OrderByDescending(Path.GetFileName, StringComparer.Ordinal);

    private static async Task<SandboxMessage?> ReadFileAsync(string path, CancellationToken token)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<SandboxMessage>(stream, Json, token);
        }
        catch (FileNotFoundException) { return null; }
    }
}
