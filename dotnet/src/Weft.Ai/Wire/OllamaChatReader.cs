using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;

namespace Weft.Ai.Wire;

internal sealed class OllamaChatReader
{
    private readonly AiTurnLease _turn;
    private readonly AiCancellation _cancellation;
    private readonly Guid _round;
    private readonly List<AiToolIntent> _calls = new List<AiToolIntent>();
    internal OllamaChatReader(AiTurnLease turn, Guid round, AiCancellation cancellation) { _turn = turn; _round = round; _cancellation = cancellation; }
    internal async IAsyncEnumerable<AiDecoded> ReadAsync(Stream source, [EnumeratorCancellation] CancellationToken ct)
    {
        using var lines = new AiLineReader(source, _turn, _cancellation);
        while (true)
        {
            AiLine? line = await lines.ReadAsync(ct).ConfigureAwait(false);
            if (line is null) throw AiGuard.Protocol(AiFailureCode.TruncatedResponse);
            if (line.Ending == AiLineEnding.Cr) throw AiGuard.Protocol();
            if (line.Text.Length == 0) throw AiGuard.Protocol();
            _turn.ChargeFrame();
            using JsonDocument document = AiJson.Parse(line.Text);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw AiGuard.Protocol();
            if (root.TryGetProperty("error", out _)) throw AiGuard.Protocol(AiFailureCode.ProviderUnavailable);
            if (!root.TryGetProperty("done", out JsonElement done) || done.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw AiGuard.Protocol();
            if (root.TryGetProperty("message", out JsonElement message) && message.ValueKind != JsonValueKind.Null)
            {
                if (message.ValueKind != JsonValueKind.Object) throw AiGuard.Protocol();
                string? role = AiJson.OptionalString(message, "role");
                if (role is not null && role != "assistant") throw AiGuard.Protocol();
                // Thinking and any other optional metadata remain bounded by the
                // whole strict JSON parser; no hidden value is exposed/retained.
                string? text = AiJson.OptionalString(message, "content");
                if (message.TryGetProperty("tool_calls", out JsonElement calls) && calls.ValueKind != JsonValueKind.Null)
                {
                    if (calls.ValueKind != JsonValueKind.Array) throw AiGuard.Protocol();
                    if (calls.GetArrayLength() > AiCeilings.Calls - _calls.Count) throw AiGuard.Protocol(AiFailureCode.BudgetExceeded);
                    foreach (JsonElement call in calls.EnumerateArray()) AppendCall(call);
                }
                if (text is { Length: > 0 }) yield return new AiDecodedText(text);
            }
            if (!done.GetBoolean())
            {
                if (line.Ending == AiLineEnding.Eof) throw AiGuard.Protocol(AiFailureCode.TruncatedResponse);
                continue;
            }
            string reason = AiJson.RequiredString(root, "done_reason");
            AiFinishReason finish = reason switch
            {
                "stop" => _calls.Count == 0 ? AiFinishReason.Stop : AiFinishReason.ToolCalls,
                "length" => AiFinishReason.OutputLimit,
                _ => throw AiGuard.Protocol(AiFailureCode.UnsupportedResponse)
            };
            long? input = AiJson.Count(root, "prompt_eval_count"), output = AiJson.Count(root, "eval_count");
            AiTokenUsage usage = AiTokenUsage.Create(input, output, null);
            ImmutableArray<AiToolIntent> intents = finish == AiFinishReason.ToolCalls ? ImmutableArray.CreateRange(_calls) : ImmutableArray<AiToolIntent>.Empty;
            if (intents.Length > 0) _turn.ChargeCalls(intents.Length);
            yield return new AiDecodedEnd(finish, intents, usage, input is not null || output is not null);
            yield break;
        }
    }
    private void AppendCall(JsonElement call)
    {
        if (call.ValueKind != JsonValueKind.Object) throw AiGuard.Protocol();
        string? type = AiJson.OptionalString(call, "type");
        if (type is not null && type != "function") throw AiGuard.Protocol(AiFailureCode.UnsupportedResponse);
        if (!call.TryGetProperty("function", out JsonElement function) || function.ValueKind != JsonValueKind.Object) throw AiGuard.Protocol();
        if (function.TryGetProperty("index", out JsonElement index)
            && (index.ValueKind != JsonValueKind.Number || AiJson.Index(index) != _calls.Count)) throw AiGuard.Protocol();
        string name = AiJson.RequiredString(function, "name");
        if (!function.TryGetProperty("arguments", out JsonElement arguments) || arguments.ValueKind != JsonValueKind.Object) throw AiGuard.Protocol();
        // Absent native identity means a distinct ordered intent, even when
        // name/arguments equal an earlier call. Never infer semantic deduplication.
        string id = "weft_" + _round.ToString("N") + "_" + _calls.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string json = arguments.GetRawText();
        if (AiGuard.Utf8.GetByteCount(json) > AiCeilings.ArgumentBytes) throw AiGuard.Protocol(AiFailureCode.BudgetExceeded);
        _calls.Add(AiToolIntent.Create(id, name, AiJsonObject.ParseArguments(json)));
    }
    public override string ToString() => nameof(OllamaChatReader);
}
