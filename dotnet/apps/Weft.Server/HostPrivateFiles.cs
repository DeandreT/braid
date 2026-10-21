using Weft.Storage.FileSystem;

namespace Weft.Server;

/// <summary>The shared protected host-file admission used by every Session delivery.</summary>
internal static class HostPrivateFiles
{
    internal static string ReadText(string path, int maximum, bool removeTerminalLf = false) =>
        ProtectedHostFiles.ReadText(path, maximum, removeTerminalLf);
}
