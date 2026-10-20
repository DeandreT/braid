using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Weft.Ai.Wire;

internal enum AiLineEnding { Lf, CrLf, Cr, Eof }
internal sealed class AiLine
{
    internal AiLine(string text, AiLineEnding ending) { Text = text; Ending = ending; }
    internal string Text { get; }
    internal AiLineEnding Ending { get; }
    public override string ToString() => nameof(AiLine);
}
internal sealed class AiLineReader : IDisposable
{
    private readonly Stream _source;
    private readonly AiTurnLease _turn;
    private readonly AiCancellation _cancellation;
    private readonly byte[] _buffer = new byte[8192];
    private readonly byte[] _line = new byte[AiCeilings.LineBytes];
    private int _position, _length, _pending = -1, _wire;
    private bool _skipLf;
    internal AiLineReader(Stream source, AiTurnLease turn, AiCancellation cancellation) { _source = source; _turn = turn; _cancellation = cancellation; }
    private async ValueTask<int> NextAsync(CancellationToken token)
    {
        if (_skipLf)
        {
            _skipLf = false;
            int following = await NextAsync(token).ConfigureAwait(false);
            return following == '\n' ? await NextAsync(token).ConfigureAwait(false) : following;
        }
        if (_pending >= 0) { int pending = _pending; _pending = -1; return pending; }
        if (_position < _length) return _buffer[_position++];
        TimeSpan remaining = _turn.Remaining;
        if (remaining <= TimeSpan.Zero) { _cancellation.Expire(); throw AiGuard.Protocol(AiFailureCode.DeadlineExceeded); }
        _length = await _cancellation.ReadAsync(_source, _buffer.AsMemory(), AiRoundClient.Min(remaining, TimeSpan.FromSeconds(30))).ConfigureAwait(false);
        _position = 0;
        if (_length == 0) return -1;
        if (_length > AiCeilings.RoundWireBytes - _wire) throw AiGuard.Protocol(AiFailureCode.BudgetExceeded);
        _wire += _length; _turn.ChargeWire(_length);
        return _buffer[_position++];
    }
    internal async ValueTask<AiLine?> ReadAsync(CancellationToken token, bool allowBareCr = false)
    {
        int count = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int value = await NextAsync(token).ConfigureAwait(false);
            if (value < 0) return count == 0 ? null : Decode(count, AiLineEnding.Eof);
            if (value == '\n') return Decode(count, AiLineEnding.Lf);
            if (value == '\r')
            {
                if (allowBareCr) { _skipLf = true; return Decode(count, AiLineEnding.Cr); }
                int next = await NextAsync(token).ConfigureAwait(false);
                if (next >= 0 && next != '\n') _pending = next;
                return Decode(count, next == '\n' ? AiLineEnding.CrLf : AiLineEnding.Cr);
            }
            if (count >= _line.Length) throw AiGuard.Protocol(AiFailureCode.BudgetExceeded);
            _line[count++] = (byte)value;
        }
    }
    private AiLine Decode(int count, AiLineEnding ending)
    {
        try { return new AiLine(AiGuard.Utf8.GetString(_line, 0, count), ending); }
        catch (DecoderFallbackException) { throw AiGuard.Protocol(); }
        finally { Array.Clear(_line, 0, count); }
    }
    public void Dispose() { Array.Clear(_buffer, 0, _buffer.Length); Array.Clear(_line, 0, _line.Length); }
    public override string ToString() => nameof(AiLineReader);
}
