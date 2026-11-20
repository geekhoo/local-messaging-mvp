using System.Text;

namespace LocalMessaging.Client;

/// <summary>A decoded SSE event. HasExplicitId distinguishes cursor-bearing frames from controls.</summary>
public sealed record SseFrame(string Event, string Data, string? Id, bool HasExplicitId, int? RetryMilliseconds);

/// <summary>Incremental, bounded SSE parser. Feed may end at any UTF-8, CR/LF, or field boundary.</summary>
public sealed class SseParser
{
    public const int MaximumFrameBytes = 256 * 1024;
    private readonly Decoder decoder = new UTF8Encoding(false, true).GetDecoder();
    private readonly StringBuilder line = new();
    private readonly StringBuilder data = new();
    private string eventName = "";
    private string? lastId;
    private bool explicitId;
    private int? retry;
    private bool skipLf;
    private bool firstCharacter = true;
    private int dataBytes;

    public IReadOnlyList<SseFrame> Feed(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        int count = decoder.GetChars(bytes, chars, false);
        var output = new List<SseFrame>();
        for (int i = 0; i < count; i++) Accept(chars[i], output);
        return output;
    }

    public void Complete()
    {
        Span<char> tail = stackalloc char[2];
        _ = decoder.GetChars(ReadOnlySpan<byte>.Empty, tail, true);
        // SSE dispatch requires a blank line. Never apply an incomplete final event.
        line.Clear();
        data.Clear();
        dataBytes = 0;
    }

    private void Accept(char value, List<SseFrame> output)
    {
        if (firstCharacter)
        {
            firstCharacter = false;
            if (value == '\uFEFF') return;
        }
        if (skipLf)
        {
            skipLf = false;
            if (value == '\n') return;
        }
        if (value is '\r' or '\n')
        {
            ProcessLine(output);
            skipLf = value == '\r';
            return;
        }
        if (line.Length >= MaximumFrameBytes) throw new InvalidDataException("SSE line exceeds the frame budget.");
        line.Append(value);
    }

    private void ProcessLine(List<SseFrame> output)
    {
        var text = line.ToString();
        line.Clear();
        if (Encoding.UTF8.GetByteCount(text) > MaximumFrameBytes)
            throw new InvalidDataException("SSE line exceeds the byte budget.");
        if (text.Length == 0)
        {
            if (data.Length > 0)
            {
                data.Length--; // Remove the final LF added for the data field.
                output.Add(new SseFrame(eventName.Length == 0 ? "message" : eventName,
                    data.ToString(), lastId, explicitId, retry));
            }
            data.Clear();
            eventName = "";
            explicitId = false;
            retry = null;
            dataBytes = 0;
            return;
        }
        if (text[0] == ':') return;
        var colon = text.IndexOf(':');
        var field = colon < 0 ? text : text[..colon];
        var value = colon < 0 ? "" : text[(colon + 1)..];
        if (value.StartsWith(' ')) value = value[1..];
        switch (field)
        {
            case "data":
                dataBytes = checked(dataBytes + Encoding.UTF8.GetByteCount(value) + 1);
                if (dataBytes > MaximumFrameBytes) throw new InvalidDataException("SSE frame exceeds the byte budget.");
                data.Append(value).Append('\n');
                break;
            case "event":
                eventName = value;
                break;
            case "id" when !value.Contains('\0'):
                lastId = value;
                explicitId = true;
                break;
            case "retry" when value.Length > 0 && value.All(c => c is >= '0' and <= '9'):
                if (int.TryParse(value, out var delay)) retry = Math.Clamp(delay, 0, 15000);
                break;
        }
    }
}
