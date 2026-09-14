namespace Sqm.Infrastructure.DataImport;

/// <summary>
/// Wraps a stream and reports how many bytes have been read from it.
/// </summary>
/// <remarks>
/// Progress during a bulk insert has to be observed somewhere, and the only place that knows
/// without asking the server is the stream being consumed. Bytes are a proxy for rows, but an
/// honest one: the caller knows the file size, so a byte percentage is accurate even before a
/// single row has been parsed, where a row estimate would not be.
/// </remarks>
internal sealed class CountingStream(Stream inner, Action<long> onBytesRead) : Stream
{
    private long _totalRead;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => _totalRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Report(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Report(read);
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
            .ConfigureAwait(false);
        Report(read);
        return read;
    }

    private void Report(int read)
    {
        if (read <= 0)
        {
            return;
        }

        _totalRead += read;
        onBytesRead(_totalRead);
    }

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
