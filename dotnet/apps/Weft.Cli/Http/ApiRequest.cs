using System.Text;

namespace Weft.Cli.Http;

/// <summary>
/// One request to a documented operation. It is built once and replayed unchanged on retry, so a retried
/// mutation always carries the same idempotency identity. Only the operation's declared query parameters
/// and headers can be set.
/// </summary>
internal sealed class ApiRequest
{
    private readonly Dictionary<string, string> _path = new(StringComparer.Ordinal);
    private readonly List<KeyValuePair<string, string>> _query = [];
    private readonly List<KeyValuePair<string, string>> _headers = [];

    public ApiRequest(ApiOperation operation) => Operation = operation ?? throw new ArgumentNullException(nameof(operation));

    public ApiOperation Operation { get; }

    /// <summary>Creates the request body for each attempt; null sends an empty body.</summary>
    public Func<HttpContent>? Content { get; private set; }

    public IReadOnlyList<KeyValuePair<string, string>> Headers => _headers;

    public ApiRequest Path(string name, string value)
    {
        if (!Operation.Path.Contains("{" + name + "}", StringComparison.Ordinal))
            throw new InvalidOperationException($"{Operation.OperationId} has no path parameter {name}.");
        ArgumentException.ThrowIfNullOrEmpty(value);
        _path[name] = value;
        return this;
    }

    public ApiRequest Query(string name, string? value)
    {
        if (!Operation.Query.Contains(name, StringComparer.Ordinal))
            throw new InvalidOperationException($"{Operation.OperationId} does not declare the query parameter {name}.");
        if (value is not null)
            _query.Add(new(name, value));
        return this;
    }

    public ApiRequest Header(string name, string value)
    {
        if (!Operation.Headers.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{Operation.OperationId} does not declare the header {name}.");
        _headers.Add(new(name, value));
        return this;
    }

    public ApiRequest Body(Func<HttpContent> content)
    {
        if (Operation.RequestMediaType is null)
            throw new InvalidOperationException($"{Operation.OperationId} has no request body.");
        Content = content;
        return this;
    }

    /// <summary>The origin-relative target with every path value escaped once.</summary>
    public string Target()
    {
        var builder = new StringBuilder();
        var template = Operation.Path;
        var index = 0;
        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            if (open < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }
            var close = template.IndexOf('}', open);
            builder.Append(template, index, open - index);
            var name = template[(open + 1)..close];
            if (!_path.TryGetValue(name, out var value))
                throw new InvalidOperationException($"{Operation.OperationId} is missing the path parameter {name}.");
            builder.Append(Uri.EscapeDataString(value));
            index = close + 1;
        }
        for (var i = 0; i < _query.Count; i++)
            builder.Append(i == 0 ? '?' : '&').Append(Uri.EscapeDataString(_query[i].Key)).Append('=')
                .Append(Uri.EscapeDataString(_query[i].Value));
        return builder.ToString();
    }
}
