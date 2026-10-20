using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Weft.Ai;

public sealed class AiToolDefinition
{
    private AiToolDefinition(string name, string description, AiJsonObject parameters)
    { Name = name; Description = description; Parameters = parameters; }
    public string Name { get; }
    public string Description { get; }
    public AiJsonObject Parameters { get; }
    public static AiToolDefinition Create(string name, string description, AiJsonObject parameters)
    {
        AiGuard.ToolName(name); AiGuard.Bytes(description, AiCeilings.DescriptionBytes);
        if (parameters is null || parameters.ByteCount > AiCeilings.SchemaBytes) throw AiGuard.Invalid();
        return new AiToolDefinition(name, description, parameters);
    }
    public override string ToString() => nameof(AiToolDefinition);
}
public sealed class AiToolIntent
{
    private AiToolIntent(string callId, string name, AiJsonObject arguments)
    { CallId = callId; Name = name; Arguments = arguments; }
    public string CallId { get; }
    public string Name { get; }
    public AiJsonObject Arguments { get; }
    internal int Bytes => AiGuard.Utf8.GetByteCount(CallId) + AiGuard.Utf8.GetByteCount(Name) + Arguments.ByteCount;
    public static AiToolIntent Create(string callId, string name, AiJsonObject arguments)
    {
        AiGuard.Token(callId, 128); AiGuard.ToolName(name);
        if (arguments is null || arguments.ByteCount > AiCeilings.ArgumentBytes) throw AiGuard.Invalid();
        return new AiToolIntent(callId, name, arguments);
    }
    public override string ToString() => nameof(AiToolIntent);
}
public abstract class AiMessage
{
    private protected AiMessage() { }
    internal abstract int Bytes { get; }
    public sealed override string ToString() => nameof(AiMessage);
}
public sealed class AiSystemMessage : AiMessage
{
    private AiSystemMessage(string text, int bytes) { Text = text; Bytes = bytes; }
    public string Text { get; }
    internal override int Bytes { get; }
    public static AiSystemMessage Create(string text) => new AiSystemMessage(text, AiGuard.Bytes(text, AiCeilings.MessageBytes));
}
public sealed class AiUserMessage : AiMessage
{
    private AiUserMessage(string text, int bytes) { Text = text; Bytes = bytes; }
    public string Text { get; }
    internal override int Bytes { get; }
    public static AiUserMessage Create(string text) => new AiUserMessage(text, AiGuard.Bytes(text, AiCeilings.MessageBytes, false));
}
public sealed class AiAssistantMessage : AiMessage
{
    private AiAssistantMessage(string text, ImmutableArray<AiToolIntent> calls, int bytes)
    { Text = text; ToolCalls = calls; Bytes = bytes; }
    public string Text { get; }
    public ImmutableArray<AiToolIntent> ToolCalls { get; }
    internal override int Bytes { get; }
    public static AiAssistantMessage Create(string text, IEnumerable<AiToolIntent> toolCalls)
    {
        int bytes = AiGuard.Bytes(text, AiCeilings.MessageBytes);
        ImmutableArray<AiToolIntent> owned = AiGuard.Copy(toolCalls, AiCeilings.Calls);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (AiToolIntent call in owned)
        { if (!ids.Add(call.CallId)) throw AiGuard.Invalid(); bytes += call.Bytes; }
        if (bytes > AiCeilings.MessageBytes || (text.Length == 0 && owned.Length == 0)) throw AiGuard.Invalid();
        return new AiAssistantMessage(text, owned, bytes);
    }
}
public sealed class AiToolMessage : AiMessage
{
    private AiToolMessage(string callId, string name, AiJsonObject result, int bytes)
    { CallId = callId; Name = name; Result = result; Bytes = bytes; }
    public string CallId { get; }
    public string Name { get; }
    public AiJsonObject Result { get; }
    internal override int Bytes { get; }
    public static AiToolMessage Create(string callId, string name, AiJsonObject result)
    {
        int bytes = AiGuard.Bytes(AiGuard.Token(callId, 128), 128) + AiGuard.Bytes(AiGuard.ToolName(name), 64);
        if (result is null || result.ByteCount > AiCeilings.ResultBytes) throw AiGuard.Invalid();
        return new AiToolMessage(callId, name, result, bytes + result.ByteCount);
    }
}
public sealed class AiRoundRequest
{
    private AiRoundRequest(Guid id, ImmutableArray<AiMessage> messages, ImmutableArray<AiToolDefinition> tools, int output)
    { RoundId = id; Messages = messages; Tools = tools; MaxOutputTokens = output; }
    public Guid RoundId { get; }
    public ImmutableArray<AiMessage> Messages { get; }
    public ImmutableArray<AiToolDefinition> Tools { get; }
    public int MaxOutputTokens { get; }
    internal bool UsesTools
    {
        get
        {
            if (Tools.Length > 0) return true;
            foreach (AiMessage message in Messages)
                if (message is AiToolMessage || message is AiAssistantMessage assistant && assistant.ToolCalls.Length > 0) return true;
            return false;
        }
    }
    public static AiRoundRequest Create(Guid roundId, IEnumerable<AiMessage> messages,
        IEnumerable<AiToolDefinition> tools, int maxOutputTokens)
    {
        if (roundId == Guid.Empty || maxOutputTokens is < 1 or > 2048) throw AiGuard.Invalid();
        ImmutableArray<AiMessage> ownedMessages = AiGuard.Copy(messages, AiCeilings.Messages);
        ImmutableArray<AiToolDefinition> ownedTools = AiGuard.Copy(tools, AiCeilings.Definitions);
        if (ownedMessages.Length == 0 || ownedMessages[^1] is not (AiUserMessage or AiToolMessage)) throw AiGuard.Invalid();
        int messageBytes = 0, schemaBytes = 0;
        var toolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (AiToolDefinition tool in ownedTools)
        {
            schemaBytes += tool.Parameters.ByteCount;
            if (schemaBytes > AiCeilings.SchemasTotalBytes || !toolNames.Add(tool.Name)) throw AiGuard.Invalid();
        }
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        ImmutableArray<AiToolIntent> pending = ImmutableArray<AiToolIntent>.Empty;
        int expected = 0;
        for (int i = 0; i < ownedMessages.Length; i++)
        {
            AiMessage message = ownedMessages[i]; messageBytes += message.Bytes;
            if (messageBytes > AiCeilings.MessageTotalBytes) throw AiGuard.Invalid();
            if (pending.Length > expected)
            {
                AiToolIntent call = pending[expected++];
                if (message is not AiToolMessage result || result.CallId != call.CallId || result.Name != call.Name) throw AiGuard.Invalid();
                continue;
            }
            if (message is AiToolMessage || message is AiSystemMessage && i != 0) throw AiGuard.Invalid();
            if (message is AiAssistantMessage assistant)
            {
                pending = assistant.ToolCalls; expected = 0;
                foreach (AiToolIntent call in pending)
                    if (!callIds.Add(call.CallId) || !toolNames.Contains(call.Name)) throw AiGuard.Invalid();
            }
        }
        if (expected != pending.Length) throw AiGuard.Invalid();
        return new AiRoundRequest(roundId, ownedMessages, ownedTools, maxOutputTokens);
    }
    public override string ToString() => nameof(AiRoundRequest);
}
