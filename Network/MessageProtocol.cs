using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using DistributedWebCrawler.Models;

namespace DistributedWebCrawler.Network;

// Протокол обмена сообщениями между мастером и воркером по TCP.
public static class MessageProtocol
{
    // Настройки сериализации JSON. JsonStringEnumConverter записывает enum
    // как строку ("Assign"), а не число (3) — так сообщения читаемы при отладке.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // Защита от некорректных данных: не принимаем "сообщения" больше 64 МБ.
    private const int MaxMessageBytes = 64 * 1024 * 1024;

    // Отправить одно сообщение в сетевой поток
    public static async Task SendAsync(NetworkStream stream, Message message, CancellationToken ct)
    {
        // Превращаем объект в массив байт
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);

        // Готовим 4-байтовый префикс с длиной
        byte[] lengthPrefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, payload.Length);

        // Пишем сначала длину, потом сами данные, и сбрасываем буфер в сеть.
        await stream.WriteAsync(lengthPrefix, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    // Прочитать одно сообщение из потока
    public static async Task<Message?> ReceiveAsync(NetworkStream stream, CancellationToken ct)
    {
        // Читаем 4 байта длины.
        byte[]? lengthBytes = await ReadExactlyAsync(stream, 4, ct);
        if (lengthBytes is null)
            return null; // соединение закрылось

        int length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (length <= 0 || length > MaxMessageBytes)
            throw new InvalidOperationException($"Некорректная длина сообщения: {length}");

        // Читаем ровно length байт — это и есть JSON сообщения.
        byte[]? payload = await ReadExactlyAsync(stream, length, ct);
        if (payload is null)
            return null;

        // Превращаем JSON обратно в объект Message.
        return JsonSerializer.Deserialize<Message>(payload, JsonOptions);
    }

    // Прочитать РОВНО count байт. Сеть может отдавать данные по частям,
    // поэтому читаем в цикле, пока не наберём нужное количество
    private static async Task<byte[]?> ReadExactlyAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0)
                // другая сторона закрыла соединение
                return null;
            offset += read;
        }
        return buffer;
    }
}
