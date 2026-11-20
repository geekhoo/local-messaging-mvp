using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using var input = Console.OpenStandardInput();
using var output = Console.OpenStandardOutput();
var configuredWorkspace = Environment.GetEnvironmentVariable("LOCAL_MESSAGING_WORKSPACE");
if (configuredWorkspace is not null && (!Path.IsPathFullyQualified(configuredWorkspace) || !Directory.Exists(configuredWorkspace)))
{
    Console.Error.WriteLine("LOCAL_MESSAGING_WORKSPACE must name an existing absolute directory.");
    Environment.ExitCode = 2;
    return;
}
using var tools = new MessagingTools(configuredWorkspace is null ? Directory.GetCurrentDirectory() : Path.GetFullPath(configuredWorkspace));
await new McpServer(input, output, tools).RunAsync();

internal sealed class McpServer(Stream input, Stream output, MessagingTools tools)
{
    private const int InputLimit = 256 * 1024;
    private const int OutputLimit = 8 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly string[] Versions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
    private static readonly byte[] Newline = [(byte)'\n'];
    private bool initialized;

    internal async Task RunAsync()
    {
        var chunk = new byte[8192];
        var line = new byte[InputLimit];
        var length = 0;
        var oversized = false;
        while (true)
        {
            var count = await input.ReadAsync(chunk);
            if (count == 0) break;
            for (var offset = 0; offset < count;)
            {
                var end = Array.IndexOf(chunk, (byte)'\n', offset, count - offset);
                var part = (end < 0 ? count : end) - offset;
                if (!oversized && part <= InputLimit - length)
                {
                    Array.Copy(chunk, offset, line, length, part);
                    length += part;
                }
                else oversized = true;
                if (end < 0) break;
                await ProcessLineAsync(line.AsMemory(0, length), oversized);
                length = 0;
                oversized = false;
                offset = end + 1;
            }
        }
        if (length > 0 || oversized) await ProcessLineAsync(line.AsMemory(0, length), oversized);
    }

    private async Task ProcessLineAsync(ReadOnlyMemory<byte> line, bool oversized)
    {
        if (oversized)
        {
            await ReplyAsync(Error(null, -32700, "Input line exceeds 256 KiB."));
            return;
        }
        JsonDocument document;
        try
        {
            _ = StrictUtf8.GetCharCount(line.Span);
            document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            await ReplyAsync(Error(null, -32700, "Parse error."));
            return;
        }
        using (document)
        {
            var request = document.RootElement;
            if (request.ValueKind != JsonValueKind.Object || !UniqueProperties(request)
                || !request.TryGetProperty("jsonrpc", out var version) || !StringEquals(version, "2.0")
                || !request.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
            {
                await ReplyAsync(Error(null, -32600, "Invalid Request."));
                return;
            }
            // Notifications (including initialized and cancelled) have no response. This server runs calls serially with deadlines.
            if (!request.TryGetProperty("id", out var id)) return;
            if (id.ValueKind != JsonValueKind.Number && !IsValidString(id))
            {
                await ReplyAsync(Error(null, -32600, "Invalid request ID."));
                return;
            }
            request.TryGetProperty("params", out var parameters);
            if (parameters.ValueKind != JsonValueKind.Undefined && !UniqueProperties(parameters))
            {
                await ReplyAsync(Error(id, -32602, "Expected a params object with unique keys."));
                return;
            }
            object? result;
            try
            {
                result = method.GetString() switch
                {
                    "initialize" => Initialize(parameters),
                    "ping" => new { },
                    "tools/list" when initialized => new { tools = MessagingTools.Definitions },
                    "tools/call" when initialized => await CallAsync(parameters),
                    "tools/list" or "tools/call" => throw new RpcError(-32002, "Initialize the server first."),
                    _ => throw new RpcError(-32601, "Method not found.")
                };
            }
            catch (RpcError ex)
            {
                await ReplyAsync(Error(id, ex.Code, ex.Message));
                return;
            }
            catch (InvalidOperationException)
            {
                await ReplyAsync(Error(id, -32602, "Invalid JSON string or parameters."));
                return;
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, result }, JsonOptions);
            if (bytes.Length > OutputLimit)
                await ReplyAsync(new { jsonrpc = "2.0", id, result = ToolResult(new { code = "output_limit", message = "Response exceeds 8 MiB. Request a smaller limit from the same page token or cursor." }, true) });
            else
                await WriteAsync(bytes);
        }
    }

    private object Initialize(JsonElement parameters)
    {
        if (initialized || parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.String
            || !parameters.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("clientInfo", out var client) || client.ValueKind != JsonValueKind.Object
            || !client.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
            || !client.TryGetProperty("version", out var clientVersion) || clientVersion.ValueKind != JsonValueKind.String)
            throw new RpcError(-32602, "Expected one initialize with protocolVersion, capabilities and clientInfo (name and version).");
        var requested = version.GetString();
        initialized = true;
        return new
        {
            protocolVersion = Versions.Contains(requested) ? requested : Versions[0],
            capabilities = new { tools = new { listChanged = false } },
            serverInfo = new { name = "local-messaging", version = "1.0.0" }
        };
    }

    private async Task<object> CallAsync(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
            throw new RpcError(-32602, "Expected a tool name.");
        var toolName = name.GetString()!;
        if (!MessagingTools.Contains(toolName)) throw new RpcError(-32602, "Unknown tool.");
        parameters.TryGetProperty("arguments", out var arguments);
        var (data, error) = await tools.CallAsync(toolName, arguments);
        return ToolResult(data, error);
    }

    private static object ToolResult(object data, bool error) => new
    {
        content = new[] { new { type = "text", text = JsonSerializer.Serialize(data, JsonOptions) } },
        isError = error
    };

    internal static bool UniqueProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        try { return element.EnumerateObject().All(property => keys.Add(property.Name)); }
        catch (InvalidOperationException) { return false; }
    }

    private static bool StringEquals(JsonElement element, string expected) => DecodeString(element) == expected;
    private static bool IsValidString(JsonElement element) => DecodeString(element) is not null;

    private static string? DecodeString(JsonElement element)
    {
        try { return element.ValueKind == JsonValueKind.String ? element.GetString() : null; }
        catch (InvalidOperationException) { return null; }
    }

    private static object Error(JsonElement? id, int code, string message) => new { jsonrpc = "2.0", id, error = new { code, message } };
    private Task ReplyAsync(object response) => WriteAsync(JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions));
    private async Task WriteAsync(byte[] bytes)
    {
        await output.WriteAsync(bytes);
        await output.WriteAsync(Newline);
        await output.FlushAsync();
    }

    private sealed class RpcError(int code, string message) : Exception(message)
    {
        internal int Code { get; } = code;
    }
}
