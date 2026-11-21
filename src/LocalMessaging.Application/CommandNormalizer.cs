using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LocalMessaging.Contracts;

namespace LocalMessaging.Application;

/// <summary>Strict, deterministic v1 command parsing shared by persistence and contract tests.</summary>
public static partial class CommandNormalizer
{
    public const int MaximumJsonBytes = 64 * 1024;

    public static JsonObject Create(JsonElement input, ProductLimits? limits = null)
    {
        limits ??= new ProductLimits();
        var root = ReadRoot(input, limits.JsonBytes);
        Keys(root, "contractVersion", "requestId", "callerUserId", "dataObject", "subscribe");
        Common(root);
        var data = Object(root, "dataObject");
        var kind = RequiredString(data, "type");
        JsonObject normalized;
        switch (kind)
        {
            case "user":
                Keys(data, "type", "handle", "displayName");
                if (root["callerUserId"] is not null || root["subscribe"] is not null)
                    throw Invalid("User creation requires an absent caller and cannot create a subscription.");
                var handle = Name(data, "handle", 32).ToLowerInvariant();
                if (!HandlePattern().IsMatch(handle)) throw Invalid("Handle must match [a-z][a-z0-9_]{2,31}.");
                normalized = new JsonObject { ["type"] = kind, ["handle"] = handle, ["displayName"] = Name(data, "displayName", 80) };
                break;
            case "channel":
                Keys(data, "type", "name", "description", "initialSessionTitle");
                normalized = new JsonObject
                {
                    ["type"] = kind, ["name"] = Name(data, "name", 120),
                    ["description"] = OptionalText(data, "description", 1000, trim: true),
                    ["initialSessionTitle"] = OptionalName(data, "initialSessionTitle", 120) ?? "General"
                };
                break;
            case "session":
                Keys(data, "type", "channel", "title");
                normalized = new JsonObject { ["type"] = kind, ["channel"] = Selector(Object(data, "channel"), true), ["title"] = Name(data, "title", 120) };
                break;
            case "message":
                Keys(data, "type", "channel", "session", "content", "parentMessageId", "targetUserIds");
                var channel = Selector(Object(data, "channel"), true);
                var session = Selector(Object(data, "session"), false);
                if (Text(channel, "mode") == "new" && Text(session, "mode") != "new")
                    throw Invalid("A message in a new channel must select a new session.");
                var content = Object(data, "content");
                var contentType = RequiredString(content, "type");
                JsonObject normalizedContent;
                if (contentType == "text")
                {
                    Keys(content, "type", "text");
                    var text = ContentText(content, "text", limits.TextLength, required: true);
                    normalizedContent = new JsonObject { ["type"] = "text", ["text"] = text };
                }
                else if (contentType == "image")
                {
                    Keys(content, "type", "caption");
                    normalizedContent = new JsonObject { ["type"] = "image", ["caption"] = ContentText(content, "caption", limits.CaptionLength, required: false) };
                }
                else throw Invalid("content.type must be text or image.");
                var targets = IdArray(data, "targetUserIds", limits.TargetUsers);
                normalized = new JsonObject
                {
                    ["type"] = kind, ["channel"] = channel, ["session"] = session, ["content"] = normalizedContent,
                    ["parentMessageId"] = OptionalId(data, "parentMessageId"), ["targetUserIds"] = targets
                };
                break;
            case "acknowledgement":
                Keys(data, "type", "messageId");
                normalized = new JsonObject { ["type"] = kind, ["messageId"] = Id(data, "messageId") };
                break;
            case "participation":
                Keys(data, "type", "channelId", "sessionId", "joined");
                var channelId = OptionalId(data, "channelId");
                var sessionId = OptionalId(data, "sessionId");
                if ((channelId is null) == (sessionId is null)) throw Invalid("Select exactly one channelId or sessionId.");
                if (data["joined"] is null) throw Invalid("joined is required.");
                normalized = new JsonObject
                {
                    ["type"] = kind, ["channelId"] = channelId, ["sessionId"] = sessionId, ["joined"] = Boolean(data, "joined")
                };
                break;
            default: throw Invalid("Unknown dataObject.type.");
        }
        var caller = kind == "user" ? null : Id(root, "callerUserId");
        JsonObject? subscribe = null;
        if (root["subscribe"] is not null)
        {
            if (kind is "acknowledgement" or "participation") throw Invalid("Public profile actions cannot create a delivery subscription.");
            var option = Object(root, "subscribe");
            Keys(option, "scope", "start");
            var scope = RequiredString(option, "scope");
            if (scope is not ("catalog" or "resolvedChannel" or "resolvedSession"))
                throw Invalid("Invalid coupled subscription scope.");
            if (RequiredString(option, "start") != "beforeRequest")
                throw Invalid("A coupled subscription must use beforeRequest.");
            subscribe = new JsonObject { ["scope"] = scope, ["start"] = "beforeRequest" };
        }
        return new JsonObject
        {
            ["contractVersion"] = 1, ["requestId"] = Id(root, "requestId"), ["callerUserId"] = caller,
            ["dataObject"] = normalized, ["subscribe"] = subscribe
        };
    }

    public static JsonObject Subscription(JsonElement input, ProductLimits? limits = null)
    {
        limits ??= new ProductLimits();
        var root = ReadRoot(input, limits.JsonBytes);
        Keys(root, "contractVersion", "requestId", "callerUserId", "filter", "start");
        Common(root);
        var filter = NormalizeFilter(Object(root, "filter"), limits);
        var start = Object(root, "start");
        var mode = RequiredString(start, "mode");
        Keys(start, "mode", "cursor");
        if (mode is not ("now" or "beginning" or "after")) throw Invalid("Invalid subscription start mode.");
        if (mode != "after" && start["cursor"] is not null) throw Invalid("Only after start accepts cursor.");
        return new JsonObject
        {
            ["contractVersion"] = 1, ["requestId"] = Id(root, "requestId"), ["callerUserId"] = Id(root, "callerUserId"),
            ["filter"] = filter, ["start"] = new JsonObject { ["mode"] = mode, ["cursor"] = mode == "after" ? NormalizedCursor(RequiredString(start, "cursor")) : null }
        };
    }

    public static JsonObject Close(JsonElement input, ProductLimits? limits = null)
    {
        limits ??= new ProductLimits();
        var root = ReadRoot(input, limits.JsonBytes);
        Keys(root, "contractVersion", "requestId", "callerUserId");
        Common(root);
        return new JsonObject { ["contractVersion"] = 1, ["requestId"] = Id(root, "requestId"), ["callerUserId"] = Id(root, "callerUserId") };
    }

    public static JsonObject NormalizeFilter(JsonObject filter, ProductLimits? limits = null)
    {
        limits ??= new ProductLimits();
        Keys(filter, "catalog", "allMessages", "allAcknowledgements", "channelIds", "sessionIds", "targetUserId");
        var channels = IdArray(filter, "channelIds", limits.FilterIds);
        var sessions = IdArray(filter, "sessionIds", limits.FilterIds);
        if (channels.Count + sessions.Count > limits.FilterIds) throw Invalid("A filter exceeds the configured channel/session ID limit.");
        var normalized = new JsonObject
        {
            ["catalog"] = Boolean(filter, "catalog"), ["allMessages"] = Boolean(filter, "allMessages"),
            ["channelIds"] = channels, ["sessionIds"] = sessions, ["targetUserId"] = OptionalId(filter, "targetUserId")
        };
        if (filter["allAcknowledgements"] is not null) normalized["allAcknowledgements"] = Boolean(filter, "allAcknowledgements");
        if (!normalized["catalog"]!.GetValue<bool>() && !normalized["allMessages"]!.GetValue<bool>()
            && normalized["allAcknowledgements"]?.GetValue<bool>() != true
            && channels.Count == 0 && sessions.Count == 0 && normalized["targetUserId"] is null)
            throw Invalid("A subscription requires at least one nonempty filter branch.");
        return normalized;
    }

    public static string Hash(string operation, JsonObject normalized, string? imageDigest = null)
    {
        var hashInput = new JsonObject { ["operation"] = operation, ["request"] = normalized.DeepClone(), ["imageSha256"] = imageDigest };
        var bytes = Encoding.UTF8.GetBytes(Canonical(hashInput));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static string Canonical(JsonNode? node) => Sort(node)?.ToJsonString() ?? "null";

    public static IReadOnlyList<string> Mentions(string text)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        const string delimiters = " \t\n\r([{<'\"";
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '@' || (i > 0 && !delimiters.Contains(text[i - 1], StringComparison.Ordinal))) continue;
            var end = i + 1;
            while (end < text.Length && IsHandleCharacter(text[end])) end++;
            var token = text[(i + 1)..end].ToLowerInvariant();
            if (HandlePattern().IsMatch(token)) found.Add(token);
            i = end - 1;
        }
        return found.ToArray();
    }

    public static (Guid Epoch, long Sequence) ParseCursor(string cursor)
    {
        if (cursor.Length > 80) throw new ApiException(400, "invalid_cursor", "The cursor is malformed.");
        var separator = cursor.IndexOf(':');
        if (separator != 36 || !Guid.TryParseExact(cursor[..separator], "D", out var epoch) || epoch == Guid.Empty)
            throw new ApiException(400, "invalid_cursor", "The cursor must contain a store UUID and decimal sequence.");
        var sequenceText = cursor[(separator + 1)..];
        if (sequenceText.Length == 0 || sequenceText.Any(c => c < '0' || c > '9') ||
            !long.TryParse(sequenceText, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
            throw new ApiException(400, "invalid_cursor", "The cursor sequence must be a nonnegative Int64.");
        return (epoch, sequence);
    }

    private static string NormalizedCursor(string value)
    {
        var parsed = ParseCursor(value);
        return Cursor(parsed.Epoch.ToString("D"), parsed.Sequence);
    }

    public static string Cursor(string epoch, long sequence) => epoch + ":" + sequence.ToString(CultureInfo.InvariantCulture);
    public static string Utc(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static JsonObject ReadRoot(JsonElement input, int maximumBytes = MaximumJsonBytes)
    {
        if (Encoding.UTF8.GetByteCount(input.GetRawText()) > maximumBytes)
            throw new ApiException(413, "request_too_large", "The JSON envelope exceeds 64 KiB.");
        if (input.ValueKind != JsonValueKind.Object) throw Invalid("The request must be an object.");
        RejectDuplicates(input);
        return JsonNode.Parse(input.GetRawText())!.AsObject();
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw Invalid("Duplicate JSON property: " + property.Name);
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    private static void Common(JsonObject root)
    {
        if (root["contractVersion"] is not JsonValue v || !v.TryGetValue<int>(out var version)) throw Invalid("contractVersion must be integer 1.");
        if (version != 1) throw new ApiException(400, "unsupported_contract_version", "Only contract version 1 is supported.");
        _ = Id(root, "requestId");
    }

    private static JsonObject Selector(JsonObject input, bool channel)
    {
        var mode = RequiredString(input, "mode");
        if (mode == "existing")
        {
            Keys(input, "mode", "id");
            return new JsonObject { ["mode"] = mode, ["id"] = Id(input, "id") };
        }
        if (mode != "new") throw Invalid("Selectors must use mode new or existing.");
        if (channel)
        {
            Keys(input, "mode", "name", "description");
            return new JsonObject { ["mode"] = mode, ["name"] = Name(input, "name", 120), ["description"] = OptionalText(input, "description", 1000, trim: true) };
        }
        Keys(input, "mode", "title");
        return new JsonObject { ["mode"] = mode, ["title"] = Name(input, "title", 120) };
    }

    public static string Text(JsonObject input, string key) => RequiredString(input, key);
    public static JsonObject Object(JsonObject input, string key) => input[key] as JsonObject ?? throw Invalid(key + " must be an object.");

    private static void Keys(JsonObject input, params string[] allowed)
    {
        foreach (var key in input.Select(p => p.Key))
            if (!allowed.Contains(key, StringComparer.Ordinal)) throw Invalid("Unknown property: " + key);
    }

    private static string RequiredString(JsonObject input, string key)
    {
        if (input[key] is JsonValue v && v.TryGetValue<string>(out var value) && value is not null) return value;
        throw Invalid(key + " must be a string.");
    }

    private static string Name(JsonObject input, string key, int maximum)
    {
        var value = RequiredString(input, key).Trim();
        if (value.Length == 0 || value.Length > maximum) throw Invalid($"{key} must contain 1–{maximum} UTF-16 code units.");
        Controls(value);
        return value;
    }

    private static string? OptionalName(JsonObject input, string key, int maximum) =>
        input[key] is null || (input[key] is JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s))
            ? null : Name(input, key, maximum);

    private static string? OptionalText(JsonObject input, string key, int maximum, bool trim, bool allowBareReturn = false)
    {
        if (input[key] is null) return null;
        var value = RequiredString(input, key).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (allowBareReturn) value = value.Replace('\r', '\n');
        if (trim) value = value.Trim();
        if (value.Length > maximum) throw Invalid(key + " exceeds its UTF-16 limit.");
        Controls(value);
        return value.Length == 0 ? null : value;
    }

    private static string? ContentText(JsonObject input, string key, int maximum, bool required)
    {
        var value = OptionalText(input, key, maximum, trim: false, allowBareReturn: true);
        if (required && string.IsNullOrWhiteSpace(value)) throw Invalid(key + " must not be empty or whitespace.");
        return value;
    }

    private static void Controls(string value)
    {
        if (value.Any(c => char.IsControl(c) && c is not ('\t' or '\n')))
            throw Invalid("Control characters other than tab and newline are not allowed.");
    }

    private static string Id(JsonObject input, string key)
    {
        var value = RequiredString(input, key);
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty) throw Invalid(key + " must be a nonempty UUID.");
        return id.ToString("D");
    }

    private static string? OptionalId(JsonObject input, string key) => input[key] is null ? null : Id(input, key);

    private static JsonArray IdArray(JsonObject input, string key, int maximum)
    {
        if (input[key] is null) return new JsonArray();
        if (input[key] is not JsonArray items || items.Count > 1000) throw Invalid(key + " must be a bounded UUID array.");
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is not JsonValue v || !v.TryGetValue<string>(out var text) || !Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty)
                throw Invalid(key + " must contain UUIDs.");
            ids.Add(id.ToString("D"));
        }
        if (ids.Count > maximum) throw Invalid(key + " has too many distinct IDs.");
        return new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
    }

    private static bool Boolean(JsonObject input, string key)
    {
        if (input[key] is null) return false;
        if (input[key] is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        throw Invalid(key + " must be boolean.");
    }

    private static bool IsHandleCharacter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Sort).ToArray()),
        _ => node?.DeepClone()
    };

    private static ApiException Invalid(string detail) => new(400, "invalid_contract", detail);
    [GeneratedRegex("^[a-z][a-z0-9_]{2,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex HandlePattern();
}
