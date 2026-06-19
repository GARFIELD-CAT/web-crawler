using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using DistributedWebCrawler.Models;

namespace DistributedWebCrawler.Network;

/// <summary>
/// Протокол обмена сообщениями между мастером и воркером по TCP.
///
/// Проблема: TCP — это "поток байтов", в нём нет границ сообщений.
/// Если просто слать JSON друг за другом, получатель не поймёт, где кончается
/// одно сообщение и начинается другое.
///
/// Решение (классическое — "кадрирование длиной"): перед каждым сообщением
/// шлём 4 байта с его длиной. Получатель сначала читает 4 байта (узнаёт длину),
/// затем читает ровно столько байт и получает целое сообщение.
///
///   [ 4 байта: длина ][ N байт: JSON-текст сообщения ]
/// </summary>
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

    /// <summary>Отправить одно сообщение в сетевой поток.</summary>
    public static async Task SendAsync(NetworkStream stream, Message message, CancellationToken ct)
    {
        // 1) Превращаем объект в массив байт (JSON в кодировке UTF-8).
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);

        // 2) Готовим 4-байтовый префикс с длиной (big-endian — стандартный сетевой порядок байт).
        byte[] lengthPrefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, payload.Length);

        // 3) Пишем сначала длину, потом сами данные, и сбрасываем буфер в сеть.
        await stream.WriteAsync(lengthPrefix, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>
    /// Прочитать одно сообщение из потока.
    /// Возвращает null, если соединение закрыто другой стороной.
    /// </summary>
    public static async Task<Message?> ReceiveAsync(NetworkStream stream, CancellationToken ct)
    {
        // 1) Читаем 4 байта длины.
        byte[]? lengthBytes = await ReadExactlyAsync(stream, 4, ct);
        if (lengthBytes is null)
            return null; // соединение закрылось

        int length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (length <= 0 || length > MaxMessageBytes)
            throw new InvalidOperationException($"Некорректная длина сообщения: {length}");

        // 2) Читаем ровно length байт — это и есть JSON сообщения.
        byte[]? payload = await ReadExactlyAsync(stream, length, ct);
        if (payload is null)
            return null;

        // 3) Превращаем JSON обратно в объект Message.
        return JsonSerializer.Deserialize<Message>(payload, JsonOptions);
    }

    /// <summary>
    /// Прочитать РОВНО count байт. Сеть может отдавать данные по частям,
    /// поэтому читаем в цикле, пока не наберём нужное количество.
    /// Возвращает null, если соединение закрылось до того, как набрали count байт.
    /// </summary>
    private static async Task<byte[]?> ReadExactlyAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0)
                return null; // другая сторона закрыла соединение
            offset += read;
        }
        return buffer;
    }
}
