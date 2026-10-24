using System.Text;

namespace Weft.Cli.Http;

/// <summary>One dispatched server-sent event.</summary>
internal sealed record ServerSentEvent(string? Id, string Event, string Data);

/// <summary>
/// A bounded reader for <c>text/event-stream</c> (the WHATWG event-stream format): <c>id</c>, <c>event</c> and
/// <c>data</c> fields, comment lines and blank-line dispatch. Lines and events are size-limited so a hostile
/// stream cannot grow memory without bound.
/// </summary>
internal sealed class ServerSentEventReader(Stream stream)
{
    internal const int MaximumLineBytes = 1 << 20;
    internal const int MaximumEventBytes = 4 << 20;

    private readonly StreamReader _reader = new(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false,
        bufferSize: 16 * 1024, leaveOpen: true);
    private readonly char[] _character = new char[1];
    private bool _afterCarriageReturn;

    /// <summary>The next event, or null when the stream ends. A trailing undispatched event is discarded.</summary>
    public async Task<ServerSentEvent?> ReadAsync(CancellationToken cancellationToken)
    {
        string? id = null;
        string? name = null;
        var data = new StringBuilder();
        var hasData = false;
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken);
            if (line is null)
                return null;
            if (line.Length == 0)
            {
                if (hasData)
                    return new ServerSentEvent(id, name ?? "message", data.ToString());
                id = null;
                name = null;
                continue;
            }
            if (line[0] == ':')
                continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];
            switch (field)
            {
                case "id" when !value.Contains('\0'):
                    id = value;
                    break;
                case "event":
                    name = value;
                    break;
                case "data":
                    if (hasData)
                        data.Append('\n');
                    data.Append(value);
                    hasData = true;
                    if (data.Length > MaximumEventBytes)
                        throw Invalid();
                    break;
            }
        }
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        while (true)
        {
            var read = await _reader.ReadAsync(_character.AsMemory(), cancellationToken);
            if (read == 0)
                return line.Length == 0 ? null : line.ToString();
            var character = _character[0];
            // CR, LF and CRLF each end a line; the LF of a CRLF pair is skipped.
            var skip = _afterCarriageReturn && character == '\n';
            _afterCarriageReturn = character == '\r';
            if (skip)
                continue;
            if (character is '\n' or '\r')
                return line.ToString();
            line.Append(character);
            if (line.Length > MaximumLineBytes)
                throw Invalid();
        }
    }

    private static CliException Invalid() =>
        new(CliExitCode.Transport, "The server's event stream was not a valid Weft response.", "invalidResponse");
}
