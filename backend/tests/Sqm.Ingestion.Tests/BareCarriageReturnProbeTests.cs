using System.Text;
using Sqm.Ingestion.Processing;

namespace Sqm.Ingestion.Tests;

/// <summary>The probe sees a bare CR wherever the read boundaries fall.</summary>
public sealed class BareCarriageReturnProbeTests
{
    [Theory]
    [InlineData("a,b\r\nc,d\r\n", false)]
    [InlineData("a,b\nc,d\n", false)]
    [InlineData("a,b\rc,d\r", true)]
    [InlineData("a,b\r\nc,d\r", true)] // CR as the very last byte
    [InlineData("a,b\r\nc\rd\r\n", true)]
    public async Task Found_one_byte_at_a_time_as_in_one_read(string text, bool bare)
    {
        foreach (var chunk in new[] { 1, 2, 3, 1 << 20 })
        {
            await using var probe = new BareCarriageReturnProbe(
                new Chunked(Encoding.ASCII.GetBytes(text), chunk));

            await probe.CopyToAsync(Stream.Null, CancellationToken.None);

            Assert.True(bare == probe.Seen, $"chunk {chunk}: expected {bare}, saw {probe.Seen}");
        }
    }

    /// <summary>A stream that never returns more than <paramref name="size"/> bytes per read.</summary>
    private sealed class Chunked(byte[] data, int size) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(size, buffer.Length)], ct);

        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(size, count));
    }
}
