namespace Sqm.Ingestion.Processing;

/// <summary>
/// A read-only pass-through that notices a carriage return not followed by a line feed.
/// </summary>
/// <remarks>
/// <para>
/// Validation reads lines with a <see cref="StreamReader"/>, which treats CR, LF and CRLF alike -
/// so a file ending its lines with a bare CR validates cleanly. ClickHouse does not agree:
/// measured on 26.7, such a file streamed as CSV imports zero rows without an error. The
/// validator cannot see the difference, so this watches the bytes as they go past.
/// </para>
/// <para>
/// Costs one <c>IndexOf</c> per buffer. CRLF, the common Windows ending, is not flagged: ClickHouse
/// reads it correctly.
/// </para>
/// </remarks>
internal sealed class BareCarriageReturnProbe(Stream inner) : Stream
{
    private bool _pendingCr;

    /// <summary>Whether a bare CR has been seen in what has been read so far.</summary>
    public bool Seen { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Inspect(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Inspect(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void Inspect(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            // End of stream: a CR as the very last byte had no LF after it.
            Seen |= _pendingCr;
            _pendingCr = false;
            return;
        }

        if (Seen)
        {
            return;
        }

        // A CR that ended the previous buffer is bare unless this one starts with LF.
        if (_pendingCr && data[0] != (byte)'\n')
        {
            Seen = true;
            return;
        }

        var rest = data;
        while (rest.IndexOf((byte)'\r') is var at and >= 0)
        {
            if (at == rest.Length - 1)
            {
                _pendingCr = true;
                return;
            }

            if (rest[at + 1] != (byte)'\n')
            {
                Seen = true;
                return;
            }

            rest = rest[(at + 2)..];
        }

        _pendingCr = false;
    }
}
