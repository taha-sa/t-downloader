using System.Buffers.Binary;

namespace TDownloader.Host;

/// <summary>Chrome native messaging çerçevesi: 4 bayt little-endian uzunluk + UTF-8 JSON.</summary>
public static class NativeMessaging
{
    /// <summary>Chrome'un host→tarayıcı sınırı (1 MB). Bu uygulama her iki yönde de aynı sınırı uygular.</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>
    /// Bir mesaj okur. Akış temiz biçimde (mesaj sınırında) biterse null döner.
    /// Sınırı aşan mesajın gövdesi okunup atılır ve <see cref="MessageTooLargeException"/> fırlatılır
    /// (akış senkron kalır, çağıran devam edebilir). Yarım başlık/gövde için <see cref="EndOfStreamException"/>.
    /// </summary>
    public static async Task<byte[]?> ReadAsync(Stream input, CancellationToken ct = default)
    {
        var header = new byte[4];
        int got = await ReadFullAsync(input, header, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < 4) throw new EndOfStreamException("Yarım uzunluk başlığı.");

        uint len = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (len > MaxMessageBytes)
        {
            await DiscardAsync(input, len, ct).ConfigureAwait(false);
            throw new MessageTooLargeException(len);
        }

        var body = new byte[len];
        if (len > 0)
        {
            int n = await ReadFullAsync(input, body, ct).ConfigureAwait(false);
            if (n < len) throw new EndOfStreamException("Yarım mesaj gövdesi.");
        }
        return body;
    }

    /// <summary>Bir mesaj yazar ve flush eder. Sınırı aşarsa hiçbir şey yazmadan <see cref="MessageTooLargeException"/>.</summary>
    public static async Task WriteAsync(Stream output, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (payload.Length > MaxMessageBytes) throw new MessageTooLargeException((uint)payload.Length);
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.Span.CopyTo(frame.AsSpan(4));
        await output.WriteAsync(frame, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadFullAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static async Task DiscardAsync(Stream s, long count, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        while (count > 0)
        {
            int n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, count)), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Atılan gövde yarım kaldı.");
            count -= n;
        }
    }
}

public sealed class MessageTooLargeException(uint size)
    : Exception($"Mesaj çok büyük: {size} bayt (sınır {NativeMessaging.MaxMessageBytes}).")
{
    public uint Size { get; } = size;
}
