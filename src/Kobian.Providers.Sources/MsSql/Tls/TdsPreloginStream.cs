namespace Kobian.Providers.Sources.MsSql.Tls;

/// <summary>
/// Strumień „TLS w pakietach PRELOGIN” (MS-TDS 2.2.6.5, TDS 7.x): podczas handshake TLS każdy rekord TLS jest przenoszony w ładunku
/// pakietu TDS typu 0x12. Używany wyłącznie do pobrania certyfikatu serwera (BL-40 cz. 5) — po handshake połączenie jest zamykane,
/// więc przełączenie na „surowy” TLS po logowaniu nie jest potrzebne. Symetryczny: ta sama klasa obsługuje stronę serwera w testach.
/// </summary>
public sealed class TdsPreloginStream(Stream inner) : Stream
{
    public const byte PreloginType = 0x12;
    public const int HeaderLength = 8;
    /// <summary>Maksymalny rozmiar pakietu TDS (MS-TDS: 32 767 B) — dłuższy nagłówek = protokół niezgodny.</summary>
    public const int MaxPacketLength = 32767;
    /// <summary>Limit kolejnych pustych pakietów (sam nagłówek) — złośliwy serwer nie może trzymać pętli odczytu do limitu czasu (SEC-3.3, it. 75).</summary>
    public const int MaxEmptyPackets = 16;
    private const int WriteChunk = 4096 - HeaderLength;
    private int _remaining;
    private int _emptyPackets;
    private byte _packetId = 1;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        while (_remaining == 0)
        {
            var header = new byte[HeaderLength];
            if (!await ReadExactAsync(inner, header, cancellationToken).ConfigureAwait(false)) return 0;
            var length = (header[2] << 8) | header[3];
            if (header[0] != PreloginType || length < HeaderLength || length > MaxPacketLength) throw new InvalidDataException("Unexpected TDS packet during TLS handshake.");
            _remaining = length - HeaderLength;
            if (_remaining == 0 && ++_emptyPackets > MaxEmptyPackets) throw new InvalidDataException("Too many empty TDS packets during TLS handshake.");
        }
        _emptyPackets = 0;

        var n = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
        if (n == 0) throw new EndOfStreamException("TDS packet truncated.");
        _remaining -= n;
        return n;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (buffer.Length > 0)
        {
            var chunk = buffer[..Math.Min(buffer.Length, WriteChunk)];
            await inner.WriteAsync(Header(PreloginType, chunk.Length, _packetId++), cancellationToken).ConfigureAwait(false);
            await inner.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            buffer = buffer[chunk.Length..];
        }
        await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Nagłówek pakietu TDS: typ, status EOM (0x01), długość BE, SPID 0, numer pakietu, okno 0.</summary>
    public static byte[] Header(byte type, int payloadLength, byte packetId)
    {
        var total = payloadLength + HeaderLength;
        if (total > MaxPacketLength) throw new ArgumentOutOfRangeException(nameof(payloadLength));
        return [type, 0x01, (byte)(total >> 8), (byte)total, 0, 0, packetId, 0];
    }

    internal static async Task<bool> ReadExactAsync(Stream s, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException("TDS packet truncated.");
            }
            read += n;
        }
        return true;
    }
}
