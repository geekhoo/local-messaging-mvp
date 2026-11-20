using System.Net.Quic;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalMessaging.Client;

internal sealed class MessagingTools(string workspace) : IDisposable
{
    private const string Acknowledgements = "profileAcknowledgements";
    private const string Rosters = "participantRosters";
    private MessagingClient? client;
    private static readonly string[] PageFields = ["limit", "pageToken", "asOf"];
    private static readonly Tool[] Catalog =
    [
        new("status", "Read the local messaging host status.", true, [], []),
        Page("list_users", "List user profiles; optionally filter by handle prefix.", [], "handlePrefix"),
        new("connect_profile", "Find or create a public profile. Provide exactly one of profileId (user-selected UUID for any client) or claudeSessionId (existing Claude identity). Use the returned userId in subsequent writes.", false, [], ["profileId", "claudeSessionId", "requestId"]),
        Page("list_channels", "List public channels.", []),
        Page("list_sessions", "List sessions in a channel.", ["channelId"]),
        new("create_channel", "Create a public channel and its initial session (default title General). Requires user approval.", false, ["userId", "name"], ["initialSessionTitle", "description", "requestId"]),
        new("create_session", "Create a session in an existing channel. Requires user approval.", false, ["userId", "channelId", "title"], ["requestId"]),
        new("send_message", "Send text or one explicitly user-approved workspace PNG/JPEG. A caption is only valid with imagePath. Use parentMessageId for a reply. Requires user approval.", false, ["userId", "channelId", "sessionId"], ["text", "imagePath", "caption", "parentMessageId", "targetUserIds", "requestId"]),
        Page("list_messages", "Read session history (flat by default; use view=roots for root messages only). Image metadata only.", ["sessionId"], "view"),
        Page("get_thread", "Read a thread page plus the root and required ancestor context.", ["messageId"]),
        Page("list_acknowledgements", "List public profile acknowledgements for a message, not verified human reads.", ["messageId"]),
        new("acknowledge_message", "Record a public profile acknowledgement only after showing the message to the user and obtaining explicit user authorization. Returns a committed receipt.", false, ["userId", "messageId"], ["requestId"]),
        Page("list_participants", "List joined profiles for exactly one channel or session. A roster is not live presence or access control.", [], "channelId", "sessionId"),
        new("set_participation", "Join or leave the public roster for exactly one channel or session. Does not change delivery. Requires user approval.", false, ["userId", "joined"], ["channelId", "sessionId", "requestId"]),
        new("subscribe", "Save a delivery subscription to exactly one channel or session. Does not change public participation. Requires user approval.", false, ["userId"], ["channelId", "sessionId", "start", "requestId"]),
        new("unsubscribe", "Close a delivery subscription owned by userId. Does not change public participation. Requires user approval.", false, ["userId", "subscriptionId"], ["requestId"]),
        new("join", "Legacy alias for subscribe (delivery only, not public participation). Requires user approval.", false, ["userId"], ["channelId", "sessionId", "start", "requestId"]),
        new("leave", "Legacy alias for unsubscribe (delivery only, not public participation). Requires user approval.", false, ["userId", "subscriptionId"], ["requestId"]),
        Page("list_subscriptions", "List saved subscriptions belonging to userId.", ["userId"]),
        new("read_events", "Read a saved subscription owned by userId. Continue with nextCursor as after even if items is empty and hasMore is true. Does not acknowledge events.", true, ["userId", "subscriptionId"], ["after", "limit"]),
        new("get_receipt", "Look up a committed write by its request UUID.", true, ["requestId"], [])
    ];
    private static readonly Dictionary<string, Tool> ByName = Catalog.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
    internal static readonly object[] Definitions = Catalog.Select(BuildDefinition).ToArray();

    internal static bool Contains(string name) => ByName.ContainsKey(name);
    private static Tool Page(string name, string description, string[] required, params string[] optional) =>
        new(name, description, true, required, [.. PageFields, .. optional]);

    private static object BuildDefinition(Tool tool)
    {
        var schema = new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JsonObject(tool.Required.Concat(tool.Optional).Select(key =>
                KeyValuePair.Create<string, JsonNode?>(key, PropertySchema(key)))),
            ["required"] = new JsonArray(tool.Required.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray())
        };
        if (UsesScope(tool.Name) || tool.Name is "send_message" or "connect_profile")
        {
            var (first, second) = tool.Name switch
            {
                "connect_profile" => ("profileId", "claudeSessionId"),
                "send_message" => ("text", "imagePath"),
                _ => ("channelId", "sessionId")
            };
            schema["oneOf"] = new JsonArray(
                new JsonObject { ["required"] = new JsonArray(first) },
                new JsonObject { ["required"] = new JsonArray(second) });
            if (tool.Name == "send_message")
                schema["dependentRequired"] = new JsonObject { ["caption"] = new JsonArray("imagePath") };
        }
        return new { name = tool.Name, description = tool.Description, inputSchema = schema,
            annotations = new { readOnlyHint = tool.ReadOnly, destructiveHint = tool.Name is "leave" or "unsubscribe" or "set_participation", openWorldHint = false } };
    }

    private static JsonObject PropertySchema(string key) => key switch
    {
        "limit" => new() { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 10, ["default"] = 10 },
        "joined" => new() { ["type"] = "boolean", ["description"] = "True to join; false to leave the selected roster." },
        "targetUserIds" => new() { ["type"] = "array", ["maxItems"] = 20, ["items"] = UuidSchema() },
        "start" => new() { ["type"] = "string", ["enum"] = new JsonArray("now", "beginning"), ["default"] = "now" },
        "view" => new() { ["type"] = "string", ["enum"] = new JsonArray("flat", "roots"), ["default"] = "flat" },
        "imagePath" => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = MaximumLength(key),
            ["description"] = "User-approved path relative to the working directory or an absolute path inside it. Maximum 8 MiB." },
        "requestId" => UuidSchema("Optional on writes: supply a UUID and keep it and the exact command (including image bytes) for uncertain outcomes."),
        _ when key.EndsWith("Id", StringComparison.Ordinal) => UuidSchema(),
        _ => new() { ["type"] = "string", ["minLength"] = MinimumLength(key), ["maxLength"] = MaximumLength(key) }
    };

    private static JsonObject UuidSchema(string? description = null)
    {
        var schema = new JsonObject { ["type"] = "string", ["format"] = "uuid", ["minLength"] = 36, ["maxLength"] = 36 };
        if (description is not null) schema["description"] = description;
        return schema;
    }

    private static int MinimumLength(string key) => key is "caption" or "description" ? 0 : 1;
    private static int MaximumLength(string key) => key switch
    {
        "text" => 16384, "caption" => 4096, "imagePath" or "pageToken" => 4096,
        "description" => 1000, "after" or "asOf" => 128, "handlePrefix" => 32, _ => 120
    };

    internal async Task<(object Data, bool Error)> CallAsync(string name, JsonElement arguments)
    {
        var tool = ByName[name];
        string? requestId = null;
        var attemptedWrite = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = timeout.Token;
        try
        {
            var args = Validate(tool, arguments);
            string Arg(string key) => args[key]!.GetValue<string>();
            string? Opt(string key) => args[key]?.GetValue<string>();
            if (!tool.ReadOnly) requestId = Opt("requestId") ?? Guid.NewGuid().ToString("D");
            Task<JsonObject> Get(string route) => GetAsync(route, token);
            Task<JsonObject> Send(string route, JsonObject envelope, byte[]? image = null)
            {
                attemptedWrite = true;
                return client!.SendWithRecoveryAsync(FrozenCommand.Operation(route, envelope, image), cancellationToken: token);
            }
            Task<JsonObject> Create(string? caller, JsonObject data, byte[]? image = null) =>
                Send("requests", Envelope(requestId!, caller, "dataObject", data), image);
            string PageRoute(string route, params (string Key, string? Value)[] extras) => Query(route,
                [("limit", (args["limit"]?.GetValue<int>() ?? 10).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                 ("pageToken", Opt("pageToken")), ("asOf", Opt("asOf")), .. extras]);

            // Validate the complete command and image before accessing the transport.
            byte[]? bytes = null;
            if (name == "send_message" && Opt("imagePath") is { } imagePath)
                bytes = await WorkspaceImage.ReadAsync(workspace, imagePath, token);
            await EnsureClientAsync(token);
            var capability = name switch
            {
                "list_acknowledgements" or "acknowledge_message" => Acknowledgements,
                "list_participants" or "set_participation" => Rosters,
                _ => null
            };
            if (capability is not null) await RequireCapabilityAsync(capability, token);

            object data = name switch
            {
                "status" => await Get("status"),
                "list_users" => await Get(PageRoute("users", ("handlePrefix", Opt("handlePrefix")))),
                "connect_profile" => await ConnectProfileAsync(Opt("claudeSessionId") ?? Arg("profileId"), Opt("claudeSessionId") is not null, Create, token),
                "list_channels" => await Get(PageRoute("channels")),
                "list_sessions" => await Get(PageRoute($"channels/{Arg("channelId")}/sessions")),
                "create_channel" => await Create(Arg("userId"), new JsonObject
                {
                    ["type"] = "channel", ["name"] = Arg("name"), ["description"] = Opt("description"), ["initialSessionTitle"] = Opt("initialSessionTitle")
                }),
                "create_session" => await Create(Arg("userId"), new JsonObject
                {
                    ["type"] = "session", ["channel"] = Existing(Arg("channelId")), ["title"] = Arg("title")
                }),
                "send_message" => await Create(Arg("userId"), new JsonObject
                {
                    ["type"] = "message", ["channel"] = Existing(Arg("channelId")), ["session"] = Existing(Arg("sessionId")),
                    ["content"] = bytes is null ? new JsonObject { ["type"] = "text", ["text"] = Arg("text") }
                        : new JsonObject { ["type"] = "image", ["caption"] = Opt("caption") },
                    ["parentMessageId"] = Opt("parentMessageId"), ["targetUserIds"] = args["targetUserIds"]?.DeepClone() ?? new JsonArray()
                }, bytes),
                "list_messages" => await Get(PageRoute($"sessions/{Arg("sessionId")}/messages", ("view", Opt("view") ?? "flat"))),
                "get_thread" => await Get(PageRoute($"messages/{Arg("messageId")}/thread")),
                "list_acknowledgements" => await Get(PageRoute($"messages/{Arg("messageId")}/acknowledgements")),
                "acknowledge_message" => await Create(Arg("userId"), new JsonObject
                {
                    ["type"] = "acknowledgement", ["messageId"] = Arg("messageId")
                }),
                "list_participants" => await Get(PageRoute(Opt("channelId") is { } channel
                    ? $"channels/{channel}/participants" : $"sessions/{Arg("sessionId")}/participants")),
                "set_participation" => await Create(Arg("userId"), new JsonObject
                {
                    ["type"] = "participation", ["joined"] = args["joined"]!.GetValue<bool>(),
                    [args.ContainsKey("channelId") ? "channelId" : "sessionId"] = Opt("channelId") ?? Arg("sessionId")
                }),
                "subscribe" or "join" => await Send("subscriptions", JoinEnvelope(requestId!, Arg("userId"), Opt("channelId"), Opt("sessionId"), Opt("start") ?? "now")),
                "unsubscribe" or "leave" => await CloseAsync(Arg("userId"), Arg("subscriptionId"), requestId!, Send, token),
                "list_subscriptions" => await Get(PageRoute("subscriptions", ("ownerUserId", Arg("userId")))),
                "read_events" => await ReadEventsAsync(Arg("userId"), Arg("subscriptionId"), Opt("after"), args["limit"]?.GetValue<int>() ?? 10, token),
                "get_receipt" => await Get($"requests/{Arg("requestId")}"),
                _ => throw new ToolFault("unknown_tool", "Unknown tool.")
            };
            return (data, false);
        }
        catch (ToolFault ex) { return (Failure(ex.Code, ex.Message), true); }
        catch (MessagingApiException ex)
        {
            var error = Failure(ex.Code, ex.Message, attemptedWrite ? requestId : null);
            error["status"] = ex.Status;
            if (ex.Details?["traceId"] is JsonValue trace) error["traceId"] = trace.DeepClone();
            if (ex.Details?["errors"] is { } validation) error["errors"] = validation.DeepClone();
            if (ex.RetryAfter is { } retry) error["retryAfterSeconds"] = Math.Max(0, retry.TotalSeconds);
            if (attemptedWrite && (ex.IsTemporary || ex.Status >= 500)) AddRecovery(error);
            return (error, true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException
            or JsonException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException or QuicException)
        {
            // Re-read the descriptor on the next call, including when a controlled restart moves the endpoint.
            client?.Dispose();
            client = null;
            var error = Failure(ex is OperationCanceledException ? "timeout" : "host_unavailable",
                ex is OperationCanceledException ? "The operation did not complete before its deadline."
                    : "Could not access the local messaging host over exact HTTP/3. Check that it is running and its descriptor is ready.",
                attemptedWrite ? requestId : null);
            if (attemptedWrite) AddRecovery(error);
            return (error, true);
        }
        catch (InvalidOperationException)
        {
            var error = Failure("invalid_data", "Invalid JSON string or unexpected response from the messaging host.", attemptedWrite ? requestId : null);
            if (attemptedWrite) AddRecovery(error);
            return (error, true);
        }
    }

    private async Task EnsureClientAsync(CancellationToken token)
    {
        if (client is not null) return;
        var endpoint = Environment.GetEnvironmentVariable("LOCAL_MESSAGING_ENDPOINT");
        if (endpoint is not null && !Path.IsPathFullyQualified(endpoint))
            throw new ToolFault("invalid_endpoint", "LOCAL_MESSAGING_ENDPOINT must be an absolute path to the endpoint descriptor.");
        endpoint ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalMessaging", "endpoint.json");
        client = await MessagingClient.FromDescriptorFileAsync(endpoint, messaging: true, cancellationToken: token);
        client.RequestTimeout = TimeSpan.FromSeconds(7);
    }

    private async Task<JsonObject> GetAsync(string path, CancellationToken token) => await client!.GetAsync(path, token);

    private async Task RequireCapabilityAsync(string capability, CancellationToken token)
    {
        var status = await GetAsync("status", token);
        if (status["capabilities"] is not JsonObject capabilities || capabilities[capability] is not JsonValue flag
            || !flag.TryGetValue<bool>(out var supported) || !supported)
            throw new ToolFault("unsupported_capability", $"This host does not advertise {capability}. Upgrade the host to use this feature; delivery and history are separate.");
    }

    private async Task<object> ConnectProfileAsync(string identity, bool claude, Func<string?, JsonObject, byte[]?, Task<JsonObject>> create, CancellationToken token)
    {
        var displayName = (claude ? "Claude " : "Agent ") + identity;
        var handle = ProfileHandle(Guid.ParseExact(identity, "D"), claude ? "claude" : "agent_");
        async Task<JsonObject?> FindAsync()
        {
            var page = await GetAsync(Query("users", ("handlePrefix", handle), ("limit", "1")), token);
            var item = page["items"]?.AsArray().FirstOrDefault()?.AsObject();
            if (item?["handle"]?.GetValue<string>() != handle) return null;
            if (item["displayName"]?.GetValue<string>() != displayName)
                throw new ToolFault("profile_handle_conflict", "The generated handle belongs to a different profile.");
            return item;
        }
        static object Connected(JsonObject profile, JsonObject? receipt = null) =>
            new { userId = profile["id"], profile, created = receipt is not null && receipt["replayed"]?.GetValue<bool>() != true, receipt };

        if (await FindAsync() is { } existing) return Connected(existing);
        try
        {
            var receipt = await create(null, new JsonObject { ["type"] = "user", ["handle"] = handle, ["displayName"] = displayName }, null);
            return Connected(receipt["dataObjects"]?["user"]?.AsObject() ?? throw new InvalidOperationException(), receipt);
        }
        catch (MessagingApiException ex) when (ex.Code == "handle_already_exists")
        {
            if (await FindAsync() is { } raced) return Connected(raced);
            throw;
        }
    }

    private static string ProfileHandle(Guid id, string prefix)
    {
        // An unpadded base32 encoding fits all 128 UUID bits in a 32-character valid handle.
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var result = new System.Text.StringBuilder(prefix, 32);
        var buffer = 0;
        var bits = 0;
        foreach (var value in id.ToByteArray(bigEndian: true))
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5) { bits -= 5; result.Append(alphabet[(buffer >> bits) & 31]); }
            buffer &= (1 << bits) - 1;
        }
        if (bits > 0) result.Append(alphabet[buffer << (5 - bits)]);
        return result.ToString();
    }

    private async Task RequireOwnerAsync(string userId, string subscriptionId, CancellationToken token)
    {
        var subscription = await GetAsync($"subscriptions/{subscriptionId}", token);
        if (subscription["ownerUserId"]?.GetValue<string>() != userId)
            throw new ToolFault("subscription_not_owned", "The subscription does not belong to the supplied userId.");
    }

    private async Task<JsonObject> CloseAsync(string userId, string id, string requestId,
        Func<string, JsonObject, byte[]?, Task<JsonObject>> send, CancellationToken token)
    {
        await RequireOwnerAsync(userId, id, token);
        return await send($"subscriptions/{id}/close", Envelope(requestId, userId), null);
    }

    private async Task<JsonObject> ReadEventsAsync(string userId, string id, string? after, int limit, CancellationToken token)
    {
        await RequireOwnerAsync(userId, id, token);
        return await GetAsync(Query($"subscriptions/{id}/events", ("after", after),
            ("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture))), token);
    }

    private static JsonObject JoinEnvelope(string requestId, string userId, string? channel, string? session, string start)
    {
        var envelope = Envelope(requestId, userId, "filter", channel is not null
            ? new JsonObject { ["channelIds"] = new JsonArray(channel) }
            : new JsonObject { ["sessionIds"] = new JsonArray(session) });
        envelope["start"] = new JsonObject { ["mode"] = start };
        return envelope;
    }

    private static JsonObject Envelope(string requestId, string? userId, string? key = null, JsonObject? data = null)
    {
        var envelope = new JsonObject { ["contractVersion"] = 1, ["requestId"] = requestId, ["callerUserId"] = userId };
        if (key is not null) envelope[key] = data;
        return envelope;
    }

    private static JsonObject Validate(Tool tool, JsonElement arguments)
    {
        var result = new JsonObject();
        if (arguments.ValueKind != JsonValueKind.Undefined)
        {
            if (!McpServer.UniqueProperties(arguments)) throw Invalid("Expected an arguments object with unique keys.");
            foreach (var property in arguments.EnumerateObject())
            {
                var key = property.Name;
                var value = property.Value;
                if (!tool.Required.Contains(key) && !tool.Optional.Contains(key)) throw Invalid("Unexpected argument; consult tools/list.");
                if (key == "limit")
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var limit) || limit is < 1 or > 10)
                        throw Invalid("limit must be an integer from 1 to 10.");
                    result[key] = limit;
                }
                else if (key == "joined")
                {
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("joined must be a boolean.");
                    result[key] = value.GetBoolean();
                }
                else if (key == "targetUserIds")
                {
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 20) throw Invalid("targetUserIds must contain up to 20 UUIDs.");
                    result[key] = new JsonArray(value.EnumerateArray().Select(item => (JsonNode?)JsonValue.Create(Uuid(item, key))).ToArray());
                }
                else if (key.EndsWith("Id", StringComparison.Ordinal)) result[key] = Uuid(value, key);
                else
                {
                    if (value.ValueKind != JsonValueKind.String) throw Invalid($"{key} must be a string.");
                    var text = value.GetString()!;
                    if (text.Length < MinimumLength(key) || text.Length > MaximumLength(key) || text.Contains('\0'))
                        throw Invalid($"{key} must have {MinimumLength(key)} to {MaximumLength(key)} UTF-16 characters and no NUL.");
                    if (key == "start" && text is not ("now" or "beginning")) throw Invalid("start must be now or beginning.");
                    if (key == "view" && text is not ("flat" or "roots")) throw Invalid("view must be flat or roots.");
                    result[key] = text;
                }
            }
        }
        foreach (var key in tool.Required)
            if (!result.ContainsKey(key)) throw Invalid($"{key} is required.");
        if (tool.Name == "connect_profile" && result.ContainsKey("profileId") == result.ContainsKey("claudeSessionId"))
            throw Invalid("Specify exactly one of profileId and claudeSessionId.");
        if (UsesScope(tool.Name) && result.ContainsKey("channelId") == result.ContainsKey("sessionId"))
            throw Invalid("Specify exactly one of channelId and sessionId.");
        if (tool.Name == "send_message" && (result.ContainsKey("text") == result.ContainsKey("imagePath")
            || (result.ContainsKey("caption") && !result.ContainsKey("imagePath"))))
            throw Invalid("Specify text or imagePath, not both. caption requires imagePath.");
        return result;
    }

    private static bool UsesScope(string name) => name is "subscribe" or "join" or "list_participants" or "set_participation";

    private static string Uuid(JsonElement value, string key) => value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: 36 } text && Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty
            ? id.ToString("D") : throw Invalid($"{key} must be a non-nil hyphenated UUID.");

    private static string Query(string route, params (string Key, string? Value)[] fields)
    {
        var values = fields.Where(field => field.Value is not null).Select(field => field.Key + "=" + Uri.EscapeDataString(field.Value!));
        var query = string.Join('&', values);
        return query.Length == 0 ? route : route + "?" + query;
    }

    private static JsonObject Existing(string id) => new() { ["mode"] = "existing", ["id"] = id };
    private static JsonObject Failure(string code, string message, string? requestId = null)
    {
        var result = new JsonObject { ["code"] = code, ["message"] = message };
        if (requestId is not null) result["requestId"] = requestId;
        return result;
    }
    private static void AddRecovery(JsonObject failure) => failure["recovery"] =
        "Check get_receipt with this requestId. If uncommitted, retry only with this same requestId and exactly the same command and image bytes.";
    private static ToolFault Invalid(string message) => new("invalid_arguments", message);
    public void Dispose() => client?.Dispose();
    private sealed record Tool(string Name, string Description, bool ReadOnly, string[] Required, string[] Optional);
}

internal sealed class ToolFault(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}
