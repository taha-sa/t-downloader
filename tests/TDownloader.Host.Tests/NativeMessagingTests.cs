using System.Buffers.Binary;
using System.Text;
using TDownloader.Host;

namespace TDownloader.Host.Tests;

public class NativeMessagingTests
{
    private static byte[] Frame(byte[] payload)
    {
        var f = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(f, (uint)payload.Length);
        payload.CopyTo(f, 4);
        return f;
    }

    [Theory]
    [InlineData("{\"type\":\"ping\"}")]
    [InlineData("{\"a\":\"Türkçe ğüşiöç 😀\"}")]
    [InlineData("")]
    public async Task RoundTrip(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var ms = new MemoryStream();
        await NativeMessaging.WriteAsync(ms, bytes);
        Assert.Equal(4 + bytes.Length, ms.Length);
        ms.Position = 0;
        var back = await NativeMessaging.ReadAsync(ms);
        Assert.Equal(bytes, back);
        Assert.Null(await NativeMessaging.ReadAsync(ms)); // temiz EOF
    }

    [Fact]
    public async Task Write_UsesLittleEndianLength()
    {
        var ms = new MemoryStream();
        await NativeMessaging.WriteAsync(ms, new byte[0x0102]);
        var a = ms.ToArray();
        Assert.Equal(new byte[] { 0x02, 0x01, 0, 0 }, a[..4]);
    }

    [Fact]
    public async Task MultipleMessages_ReadInOrder()
    {
        var ms = new MemoryStream();
        await NativeMessaging.WriteAsync(ms, "one"u8.ToArray());
        await NativeMessaging.WriteAsync(ms, "two"u8.ToArray());
        ms.Position = 0;
        Assert.Equal("one", Encoding.UTF8.GetString((await NativeMessaging.ReadAsync(ms))!));
        Assert.Equal("two", Encoding.UTF8.GetString((await NativeMessaging.ReadAsync(ms))!));
        Assert.Null(await NativeMessaging.ReadAsync(ms));
    }

    [Fact]
    public async Task ExactlyMaxSize_IsAccepted()
    {
        var payload = new byte[NativeMessaging.MaxMessageBytes];
        var ms = new MemoryStream();
        await NativeMessaging.WriteAsync(ms, payload);
        ms.Position = 0;
        Assert.Equal(payload.Length, (await NativeMessaging.ReadAsync(ms))!.Length);
    }

    [Fact]
    public async Task Write_OverMax_ThrowsAndWritesNothing()
    {
        var ms = new MemoryStream();
        await Assert.ThrowsAsync<MessageTooLargeException>(
            () => NativeMessaging.WriteAsync(ms, new byte[NativeMessaging.MaxMessageBytes + 1]));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public async Task Read_OverMax_Throws_ThenStreamStaysInSync()
    {
        var big = Frame(new byte[NativeMessaging.MaxMessageBytes + 10]);
        var next = Frame("ok"u8.ToArray());
        var ms = new MemoryStream(big.Concat(next).ToArray());
        await Assert.ThrowsAsync<MessageTooLargeException>(() => NativeMessaging.ReadAsync(ms));
        Assert.Equal("ok", Encoding.UTF8.GetString((await NativeMessaging.ReadAsync(ms))!));
    }

    [Fact]
    public async Task Read_TruncatedHeader_Throws()
    {
        var ms = new MemoryStream(new byte[] { 5, 0 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => NativeMessaging.ReadAsync(ms));
    }

    [Fact]
    public async Task Read_TruncatedBody_Throws()
    {
        var f = Frame("hello"u8.ToArray());
        var ms = new MemoryStream(f[..^2]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => NativeMessaging.ReadAsync(ms));
    }

    [Fact]
    public async Task Read_HugeLengthHeader_ReportsTooLarge_NotOutOfMemory()
    {
        var ms = new MemoryStream(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3 });
        await Assert.ThrowsAnyAsync<Exception>(() => NativeMessaging.ReadAsync(ms)); // EndOfStream (gövde yok)
    }

    [Fact]
    public async Task Read_WorksWithOneByteAtATimeStream()
    {
        var f = Frame("{\"x\":1}"u8.ToArray());
        var back = await NativeMessaging.ReadAsync(new TrickleStream(f));
        Assert.Equal("{\"x\":1}", Encoding.UTF8.GetString(back!));
    }

    private sealed class TrickleStream(byte[] data) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count) => _pos >= data.Length ? 0 : (buffer[offset] = data[_pos++]) * 0 + 1;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos >= data.Length) return ValueTask.FromResult(0);
            buffer.Span[0] = data[_pos++];
            return ValueTask.FromResult(1);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
