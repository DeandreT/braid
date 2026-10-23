using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Weft.Ai.Wire;

internal sealed class ChatCompletionsReader
{
    private readonly AiTurnLease _turn;
    private readonly AiCancellation _cancellation;
    private readonly Dictionary<int, Call> _calls = new Dictionary<int, Call>();
    private AiFinishReason? _finish;
    private AiTokenUsage _usage = AiTokenUsage.Unknown;
    private bool _usageObserved, _refused;
    internal ChatCompletionsReader(AiTurnLease turn, AiCancellation cancellation) { _turn = turn; _cancellation = cancellation; }
    internal async IAsyncEnumerable<AiDecoded> ReadAsync(Stream source, [EnumeratorCancellation] CancellationToken ct)
    {
        using var lines = new AiLineReader(source, _turn, _cancellation);
        var data = new StringBuilder(); int dataBytes = 0; bool seen = false, firstLine = true;
        while (true)
        {
            AiLine? line = await lines.ReadAsync(ct, true).ConfigureAwait(false);
            if (line is null || line.Ending == AiLineEnding.Eof) throw AiGuard.Protocol(AiFailureCode.TruncatedResponse);
            string lineText = line.Text;
            if (firstLine) { firstLine = false; if (lineText.StartsWith('\uFEFF')) lineText = lineText.Substring(1); }
            if (lineText.Length == 0)
            {
                if (!seen) continue;
                _turn.ChargeFrame();
                ImmutableArray<AiDecoded> decoded = Parse(data.ToString());
                data.Clear(); dataBytes = 0; seen = false;
                foreach (AiDecoded item in decoded)
                {
                    yield return item;
                    if (item is AiDecodedEnd) yield break;
                }
                continue;
            }
            if (lineText[0] == ':') continue;
            int colon = lineText.IndexOf(':');
            string field = colon < 0 ? lineText : lineText.Substring(0, colon);
            if (!StringComparer.Ordinal.Equals(field, "data")) continue;
            string value = colon < 0 ? string.Empty : lineText.Substring(colon + 1);
            if (value.StartsWith(' ')) value = value.Substring(1);
            int add = AiGuard.Utf8.GetByteCount(value) + (seen ? 1 : 0);
            if (add > AiCeilings.LineBytes - dataBytes) throw AiGuard.Protocol(AiFailureCode.BudgetExceeded);
            if (seen) data.Append('\n'); data.Append(value); dataBytes += add; seen = true;
        }
    }
    private ImmutableArray<AiDecoded> Parse(string data)
    {
        if (data == "[DONE]")
        {
            if (_finish is null) throw AiGuard.Protocol(AiFailureCode.TruncatedResponse);
            AiFinishReason reason = _refused ? AiFinishReason.Refused : _finish.Value;
            ImmutableArray<AiToolIntent> calls = reason == AiFinishReason.ToolCalls ? CompleteCalls() : ImmutableArray<AiToolIntent>.Empty;
            return ImmutableArray.Create<AiDecoded>(new AiDecodedEnd(reason, calls, _usage, false));
        }
        using JsonDocument document = AiJson.Parse(data);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw AiGuard.Protocol();
        if (root.TryGetProperty("error", out _)) throw AiGuard.Protocol(AiFailureCode.ProviderUnavailable);
        if (root.TryGetProperty("object", out JsonElement kind))
        {
            if (kind.ValueKind != JsonValueKind.String) throw AiGuard.Protocol();
            if (kind.GetString() == "") return ParseAnnotation(root);
            if (kind.GetString() != "chat.completion.chunk") throw AiGuard.Protocol(AiFailureCode.UnsupportedResponse);
        }
        if (root.TryGetProperty("id", out JsonElement identity) && identity.ValueKind != JsonValueKind.String) throw AiGuard.Protocol();
        if (!root.TryGetProperty("choices", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array) throw AiGuard.Protocol();
        if (choices.GetArrayLength() > 1) throw AiGuard.Protocol(AiFailureCode.UnsupportedResponse);
        string? text = null;
        if (choices.GetArrayLength() == 1)
        {
            JsonElement choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("index", out JsonElement index)
                || index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out int ordinal)) throw AiGuard.Protocol();
            if (ordinal != 0) throw AiGuard.Protocol(AiFailureCode.UnsupportedResponse);
            if (!choice.TryGetProperty("delta", out JsonElement delta) || delta.ValueKind != JsonValueKind.Object) throw AiGuard.Protocol();
            text = ParseDelta(delta);
            string? finish = AiJson.OptionalString(choice, "finish_reason");
            if (finish is not null)
            {
                if (_finish is not null) throw AiGuard.Protocol();
                _finish = finish switch
                {
                    "stop" => AiFinishReason.Stop,
                    "tool_calls" => AiFinishReason.ToolCalls,
                    "length" => AiFinishReason.OutputLimit,
                    "content_filter" => AiFinishReason.ContentFiltered,
                    _ => throw AiGuard.Protocol(AiFailureCode.UnsupportedResponse)
                };
            }
        }
        bool observed = false;
        if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind != JsonValueKind.Null)
        {
            if (usage.ValueKind != JsonValueKind.Object || _usageObserved || _finish is null) throw AiGuard.Protocol();
            _usage = AiTokenUsage.Create(AiJson.Count(usage, "prompt_tokens"), AiJson.Count(usage, "completion_tokens"), AiJson.Count(usage, "total_tokens"));
            _usageObserved = true; observed = true;
        }
        if (choices.GetArrayLength() == 0 && !observed) throw AiGuard.Protocol();
        var decoded = ImmutableArray.CreateBuilder<AiDecoded>();
        // Validate the whole frame first, then latch its reported accounting
        // before same-frame text can exhaust a downstream text/event budget.
        if (observed) decoded.Add(new AiDecodedUsage(_usage));
        if (text is { Length: > 0 }) decoded.Add(new AiDecodedText(text));
        return decoded.ToImmutable();
    }
    // Azure OpenAI content-filter annotations have an empty object type: prompt results before the completion and,
    // with an asynchronous filter, results for text already sent. They carry no text, tool call or usage; one that
    // ends the completion as filtered replaces an earlier finish, since its text was already streamed.
    private ImmutableArray<AiDecoded> ParseAnnotation(JsonElement root)
    {
        if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind != JsonValueKind.Null) throw AiGuard.Protocol();
        if (!root.TryGetProperty("choices", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array) throw AiGuard.Protocol();
        if (choices.GetArrayLength() > 1) throw AiGuard.Protocol(AiFailureCode.UnsupportedResponse);
        if (choices.GetArrayLength() == 0) return ImmutableArray<AiDecoded>.Empty;
        JsonElement choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("index", out JsonElement index)
            || index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out int ordinal)) throw AiGuard.Protocol();
        if (ordinal != 0) throw AiGuard.Protocol(AiFailureCode.UnsupportedResponse);
        if (choice.TryGetProperty("delta", out JsonElement delta) && delta.ValueKind != JsonValueKind.Null) throw AiGuard.Protocol();
        string? finish = AiJson.OptionalString(choice, "finish_reason");
        if (finish is not null && finish != "content_filter") throw AiGuard.Protocol();
        if (finish is not null) _finish = AiFinishReason.ContentFiltered;
        return ImmutableArray<AiDecoded>.Empty;
    }
    private string? ParseDelta(JsonElement delta)
    {
        foreach (string unsupported in new[] { "audio", "function_call" })
            if (delta.TryGetProperty(unsupported, out JsonElement value) && value.ValueKind != JsonValueKind.Null)
                throw AiGuard.Protocol(AiFailureCode.UnsupportedResponse);
        string? role = AiJson.OptionalString(delta, "role");
        if (role is not null && role != "assistant") throw AiGuard.Protocol(AiFailureCode.UnsupportedResponse);
        string? text = AiJson.OptionalString(delta, "content");
        string? refusal = AiJson.OptionalString(delta, "refusal");
        if (_finish is not null && (text is { Length: > 0 } || refusal is { Length: > 0 })) throw AiGuard.Protocol();
        if (refusal is { Length: > 0 }) _refused = true;
        if (delta.TryGetProperty("tool_calls", out JsonElement calls) && calls.ValueKind != JsonValueKind.Null)
        {
            if (calls.ValueKind != JsonValueKind.Array) throw AiGuard.Protocol();
            if (calls.GetArrayLength() > AiCeilings.Calls) throw AiGuard.Protocol(AiFailureCode.BudgetExceeded);
            if (_finish is not null && calls.GetArrayLength() > 0) throw AiGuard.Protocol();
            foreach (JsonElement item in calls.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out JsonElement index) || index.ValueKind != JsonValueKind.Number) throw AiGuard.Protocol();
                int ordinal = AiJson.Index(index);
                if (!_calls.TryGetValue(ordinal, out Call? call)) { call = new Call(); _calls.Add(ordinal, call); }
                call.Append(item);
            }
        }
        return text;
    }
    private ImmutableArray<AiToolIntent> CompleteCalls()
    {
        if (_calls.Count == 0) throw AiGuard.Protocol();
        var result = ImmutableArray.CreateBuilder<AiToolIntent>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < _calls.Count; i++)
        {
            if (!_calls.TryGetValue(i, out Call? call)) throw AiGuard.Protocol();
            AiToolIntent intent = call.Complete();
            if (!ids.Add(intent.CallId)) throw AiGuard.Protocol();
            result.Add(intent);
        }
        _turn.ChargeCalls(result.Count);
        return result.ToImmutable();
    }
    public override string ToString() => nameof(ChatCompletionsReader);
    private sealed class Call
    {
        private string? _id, _name, _type;
        private readonly StringBuilder _arguments = new StringBuilder();
        private int _bytes;
        private bool _sawArguments;
        internal void Append(JsonElement item)
        {
            string? id = AiJson.OptionalString(item, "id");
            if (id is not null) AiGuard.Token(id, 128);
            Agree(ref _id, id);
            Agree(ref _type, AiJson.OptionalString(item, "type"));
            if (_type is not null && _type != "function") throw AiGuard.Protocol(AiFailureCode.UnsupportedResponse);
            if (!item.TryGetProperty("function", out JsonElement function) || function.ValueKind == JsonValueKind.Null) return;
            if (function.ValueKind != JsonValueKind.Object) throw AiGuard.Protocol();
            string? name = AiJson.OptionalString(function, "name");
            if (name is not null) AiGuard.ToolName(name);
            Agree(ref _name, name);
            string? arguments = AiJson.OptionalString(function, "arguments");
            if (arguments is null) return;
            int count = AiGuard.Utf8.GetByteCount(arguments);
            if (count > AiCeilings.ArgumentBytes - _bytes) throw AiGuard.Protocol(AiFailureCode.BudgetExceeded);
            _arguments.Append(arguments); _bytes += count; _sawArguments = true;
        }
        private static void Agree(ref string? current, string? next)
        {
            if (next is null) return;
            if (current is not null && current != next) throw AiGuard.Protocol();
            current = next;
        }
        internal AiToolIntent Complete()
        {
            if (_id is null || _name is null || _type != "function" || !_sawArguments) throw AiGuard.Protocol();
            return AiToolIntent.Create(_id, _name, AiJsonObject.ParseArguments(_arguments.ToString()));
        }
        public override string ToString() => nameof(Call);
    }
}
