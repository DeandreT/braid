using System;
using System.IO;
using System.Text.Json;

namespace Weft.Ai.Wire;

internal static class AiRequestWriter
{
    internal static byte[] Write(AiProfileSpec profile, AiRoundRequest request)
    {
        using var output = new LimitedBuffer(AiCeilings.RequestBytes);
        using (var writer = new Utf8JsonWriter(output))
        {
            bool chat = profile.Dialect == AiDialect.ChatCompletionsSseV1;
            writer.WriteStartObject(); writer.WriteString("model", profile.Model); writer.WriteBoolean("stream", true);
            if (chat)
            {
                writer.WriteNumber("n", 1); writer.WriteBoolean("store", false);
                writer.WriteNumber("max_completion_tokens", request.MaxOutputTokens);
                writer.WriteStartObject("stream_options"); writer.WriteBoolean("include_usage", true); writer.WriteEndObject();
            }
            else
            {
                writer.WriteStartObject("options"); writer.WriteNumber("num_predict", request.MaxOutputTokens); writer.WriteEndObject();
                if (profile.RequestThinkFalse) writer.WriteBoolean("think", false);
            }
            writer.WriteStartArray("messages");
            foreach (AiMessage message in request.Messages)
            {
                writer.WriteStartObject();
                switch (message)
                {
                    case AiSystemMessage system:
                        writer.WriteString("role", "system"); writer.WriteString("content", system.Text); break;
                    case AiUserMessage user:
                        writer.WriteString("role", "user"); writer.WriteString("content", user.Text); break;
                    case AiAssistantMessage assistant:
                        writer.WriteString("role", "assistant"); writer.WriteString("content", assistant.Text);
                        if (assistant.ToolCalls.Length > 0)
                        {
                            writer.WriteStartArray("tool_calls");
                            for (int index = 0; index < assistant.ToolCalls.Length; index++)
                            {
                                AiToolIntent call = assistant.ToolCalls[index]; writer.WriteStartObject();
                                if (chat) writer.WriteString("id", call.CallId);
                                writer.WriteString("type", "function"); writer.WriteStartObject("function");
                                if (!chat) writer.WriteNumber("index", index);
                                writer.WriteString("name", call.Name);
                                if (chat) writer.WriteString("arguments", call.Arguments.Json);
                                else { writer.WritePropertyName("arguments"); writer.WriteRawValue(call.Arguments.Json, true); }
                                writer.WriteEndObject(); writer.WriteEndObject();
                            }
                            writer.WriteEndArray();
                        }
                        break;
                    case AiToolMessage tool:
                        writer.WriteString("role", "tool"); writer.WriteString("content", tool.Result.Json);
                        writer.WriteString(chat ? "tool_call_id" : "tool_name", chat ? tool.CallId : tool.Name); break;
                    default: throw AiGuard.Protocol(AiFailureCode.InvalidRequest);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (request.Tools.Length > 0)
            {
                writer.WriteStartArray("tools");
                foreach (AiToolDefinition tool in request.Tools)
                {
                    writer.WriteStartObject(); writer.WriteString("type", "function"); writer.WriteStartObject("function");
                    writer.WriteString("name", tool.Name); writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("parameters"); writer.WriteRawValue(tool.Parameters.Json, true);
                    writer.WriteEndObject(); writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject(); writer.Flush();
        }
        return output.Copy();
    }
    private sealed class LimitedBuffer : Stream
    {
        private readonly MemoryStream _stream = new MemoryStream();
        private readonly int _maximum;
        internal LimitedBuffer(int maximum) { _maximum = maximum; }
        internal byte[] Copy() => _stream.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _stream.Length;
        public override long Position { get => _stream.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > _maximum - _stream.Length) throw AiGuard.Protocol(AiFailureCode.BudgetExceeded);
            _stream.Write(buffer);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_stream.TryGetBuffer(out ArraySegment<byte> bytes) && bytes.Array is not null)
                    Array.Clear(bytes.Array, 0, bytes.Array.Length);
                _stream.Dispose();
            }
            base.Dispose(disposing);
        }
        public override string ToString() => nameof(LimitedBuffer);
    }
}
