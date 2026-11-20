using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalMessaging.Client;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private const string Help = """
        LocalMessaging.ConsoleClient [--endpoint absolute-file] [--frontend] COMMAND [OPTIONS]
        Default transport: exact HTTP/3 to M using the descriptor's certificate pin. No protocol fallback.

        status
        get      --path relative-api-path
        user     --handle handle --name display-name
        channel  --user UUID --name channel-name [--description text] [--initial-session title]
        session  --user UUID --channel UUID --title title
        text     --user UUID --channel UUID --session UUID --text text [--parent UUID] [--targets UUID,UUID]
        image    --user UUID --channel UUID --session UUID --image absolute-file [--caption text] [--parent UUID] [--targets UUID,UUID]
        send     --file absolute-envelope-json [--image absolute-file]
        watch    --user UUID [--channel UUID] [--session UUID] [--seconds 1..3600]

        Create commands accept --request-id UUID and --save-request absolute-new-file.
        Channel/session/text/image accept --subscribe catalog|resolvedChannel|resolvedSession.
        Retrying a command requires its original request ID, caller, normalized fields, and image bytes.
        Watch prints snapshot/event JSON lines with an independent cursor. Ctrl+C closes only its own subscription.
        --frontend explicitly selects the separate HTTP/1.1 frontend API; it is not an HTTP/3 qualification.
        """;

    public static async Task<int> Main(string[] arguments)
    {
        if (arguments.Length == 0 || arguments.Contains("--help", StringComparer.Ordinal)) { Console.WriteLine(Help); return 0; }
        using var canceled = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; canceled.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var (command, options, frontend) = Parse(arguments);
            var endpoint = options.GetValueOrDefault("--endpoint", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalMessaging", "endpoint.json"));
            RequireAbsolute(endpoint, "--endpoint");
            using var api = await MessagingClient.FromDescriptorFileAsync(endpoint, messaging: !frontend, canceled.Token);
            Console.Error.WriteLine(frontend ? "Transport: explicit F / HTTP/1.1" : "Transport: M / exact HTTP/3 with descriptor certificate pin");
            if (command == "status") { Print(await api.GetAsync("status", canceled.Token)); return 0; }
            if (command == "get") { Print(await api.GetAsync(Required(options, "--path"), canceled.Token)); return 0; }
            if (command == "watch") { await WatchAsync(api, options, canceled.Token); return 0; }
            JsonObject envelope;
            byte[]? image = null;
            if (command == "send")
            {
                var path = Required(options, "--file"); RequireAbsolute(path, "--file");
                envelope = await ReadEnvelopeAsync(path, canceled.Token);
            }
            else
            {
                var caller = command == "user" ? null : User(options).ToString();
                var data = MakeData(command, options);
                var requestId = options.TryGetValue("--request-id", out var given) ? Id(given, "--request-id") : Guid.NewGuid();
                envelope = new JsonObject
                {
                    ["contractVersion"] = 1, ["requestId"] = requestId.ToString(), ["callerUserId"] = caller,
                    ["dataObject"] = data, ["subscribe"] = options.TryGetValue("--subscribe", out var scope)
                        ? new JsonObject { ["scope"] = scope, ["start"] = "beforeRequest" } : null
                };
            }
            if (options.TryGetValue("--image", out var imagePath))
            {
                RequireAbsolute(imagePath, "--image");
                image = await ImageFileReader.ReadAsync(imagePath, canceled.Token);
            }
            if (command == "image" && image is null) throw new ArgumentException("image requires --image absolute-file.");
            var frozen = FrozenCommand.Operation("requests", envelope, image);
            if (options.TryGetValue("--save-request", out var savePath))
            {
                RequireAbsolute(savePath, "--save-request");
                await using var output = new FileStream(savePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await JsonSerializer.SerializeAsync(output, frozen.GetEnvelope(), JsonOptions, canceled.Token);
                await output.FlushAsync(canceled.Token);
            }
            Console.Error.WriteLine("Frozen request: " + frozen.GetEnvelope().ToJsonString(JsonOptions));
            var receipt = await api.SendWithRecoveryAsync(frozen, cancellationToken: canceled.Token);
            Print(receipt);
            return 0;
        }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { return 0; }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.Message, code = (error as MessagingApiException)?.Code }));
            return error is ArgumentException ? 2 : 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static JsonObject MakeData(string command, Dictionary<string, string> options)
    {
        if (command == "user") return new JsonObject { ["type"] = "user", ["handle"] = Required(options, "--handle"), ["displayName"] = Required(options, "--name") };
        if (command == "channel") return new JsonObject
        {
            ["type"] = "channel", ["name"] = Required(options, "--name"), ["description"] = options.GetValueOrDefault("--description"),
            ["initialSessionTitle"] = options.GetValueOrDefault("--initial-session", "General")
        };
        var channel = new JsonObject { ["mode"] = "existing", ["id"] = Id(Required(options, "--channel"), "--channel").ToString() };
        if (command == "session") return new JsonObject { ["type"] = "session", ["channel"] = channel, ["title"] = Required(options, "--title") };
        if (command is not ("text" or "image")) throw new ArgumentException("Unknown command. Use --help.");
        var targets = options.GetValueOrDefault("--targets", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Id(value, "--targets").ToString()).Distinct(StringComparer.Ordinal).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray();
        return new JsonObject
        {
            ["type"] = "message", ["channel"] = channel,
            ["session"] = new JsonObject { ["mode"] = "existing", ["id"] = Id(Required(options, "--session"), "--session").ToString() },
            ["content"] = command == "text" ? new JsonObject { ["type"] = "text", ["text"] = Required(options, "--text") }
                : new JsonObject { ["type"] = "image", ["caption"] = options.GetValueOrDefault("--caption") },
            ["parentMessageId"] = options.TryGetValue("--parent", out var parent) ? Id(parent, "--parent").ToString() : null,
            ["targetUserIds"] = new JsonArray(targets)
        };
    }

    private static async Task WatchAsync(MessagingClient api, Dictionary<string, string> options, CancellationToken cancellationToken)
    {
        var caller = User(options);
        string? channel = options.TryGetValue("--channel", out var channelValue) ? Id(channelValue, "--channel").ToString() : null;
        string? session = options.TryGetValue("--session", out var sessionValue) ? Id(sessionValue, "--session").ToString() : null;
        using var duration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.TryGetValue("--seconds", out var seconds))
        {
            if (!int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 1 or > 3600)
                throw new ArgumentException("--seconds must be 1..3600.");
            duration.CancelAfter(TimeSpan.FromSeconds(value));
        }
        var token = duration.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                string? subscriptionId = null;
                try
                {
                    await api.GetAsync("users/" + caller, token); // Revalidate the selected profile after a restore.
                    var status = await api.GetAsync("status", token);
                    var snapshot = status["headCursor"]!.GetValue<string>();
                    Print(new JsonObject { ["kind"] = "snapshot.begin", ["cursor"] = snapshot, ["storeEpoch"] = status["storeEpoch"]!.GetValue<string>() });
                    await SnapshotAsync(api, "users", "user", snapshot, token);
                    await SnapshotAsync(api, "channels", "channel", snapshot, token);
                    if (channel is not null) await SnapshotAsync(api, "channels/" + channel + "/sessions", "session", snapshot, token);
                    if (session is not null) await SnapshotAsync(api, "sessions/" + session + "/messages?view=flat", "message", snapshot, token);
                    var command = FrozenCommand.Operation("subscriptions", new JsonObject
                    {
                        ["contractVersion"] = 1, ["requestId"] = Guid.NewGuid().ToString(), ["callerUserId"] = caller.ToString(),
                        ["filter"] = new JsonObject
                        {
                            ["catalog"] = true, ["allMessages"] = channel is null && session is null,
                            ["channelIds"] = channel is null ? new JsonArray() : new JsonArray(channel),
                            ["sessionIds"] = session is null ? new JsonArray() : new JsonArray(session), ["targetUserId"] = null
                        },
                        ["start"] = new JsonObject { ["mode"] = "after", ["cursor"] = snapshot }
                    });
                    var receipt = await api.SendWithRecoveryAsync(command, cancellationToken: token);
                    var subscription = receipt["subscription"]!.AsObject();
                    subscriptionId = subscription["id"]!.GetValue<string>();
                    Print(new JsonObject { ["kind"] = "snapshot.end", ["cursor"] = snapshot, ["subscriptionId"] = subscriptionId });
                    var consumer = new SubscriptionConsumer(api, subscriptionId, subscription["startCursor"]!.GetValue<string>());
                    await consumer.RunAsync((evt, _) =>
                    {
                        Print(new JsonObject { ["kind"] = "event", ["event"] = evt.DeepClone() });
                        return Task.CompletedTask; // Console application completes before the durable cursor advances.
                    }, state => Console.Error.WriteLine("Stream: " + state), token);
                }
                catch (MessagingApiException error) when (error.RequiresResynchronization)
                {
                    Print(new JsonObject { ["kind"] = "resynchronizing", ["reason"] = error.Code });
                }
                finally
                {
                    if (subscriptionId is not null)
                    {
                        using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        try { await api.CloseSubscriptionAsync(caller, subscriptionId, closeTimeout.Token); }
                        catch (Exception error) when (error is HttpRequestException or MessagingApiException or OperationCanceledException)
                        { Console.Error.WriteLine("Stream stopped; saved subscription could not be closed: " + error.Message); }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static async Task SnapshotAsync(MessagingClient api, string path, string kind, string cursor, CancellationToken token)
    {
        string? next = null;
        do
        {
            var page = await api.GetAsync(path + (path.Contains('?') ? "&" : "?") + "limit=50&asOf=" + Uri.EscapeDataString(cursor)
                + (next is null ? "" : "&pageToken=" + Uri.EscapeDataString(next)), token);
            foreach (var item in page["items"]!.AsArray())
                Print(new JsonObject { ["kind"] = "snapshot." + kind, ["dataObject"] = item!.DeepClone() });
            next = page["nextPageToken"]?.GetValue<string>();
        } while (next is not null);
    }

    private static (string Command, Dictionary<string, string> Options, bool Frontend) Parse(string[] arguments)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        string? command = null; bool frontend = false;
        var allowed = new HashSet<string>(["--endpoint", "--path", "--handle", "--name", "--user", "--channel", "--session", "--description",
            "--initial-session", "--title", "--text", "--image", "--caption", "--parent", "--targets", "--request-id", "--save-request", "--subscribe", "--file", "--seconds"], StringComparer.Ordinal);
        for (int i = 0; i < arguments.Length; i++)
        {
            var argument = arguments[i];
            if (argument == "--frontend") { frontend = true; continue; }
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (!allowed.Contains(argument) || i + 1 >= arguments.Length || !options.TryAdd(argument, arguments[++i]))
                    throw new ArgumentException("Unknown, duplicated or incomplete option: " + argument);
            }
            else if (command is null) command = argument;
            else throw new ArgumentException("Unexpected argument: " + argument);
        }
        if (command is not ("status" or "get" or "user" or "channel" or "session" or "text" or "image" or "send" or "watch"))
            throw new ArgumentException("Choose a command listed by --help.");
        return (command, options, frontend);
    }

    private static async Task<JsonObject> ReadEnvelopeAsync(string path, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        using var copy = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await file.ReadAsync(buffer, token);
            if (count == 0) break;
            if (copy.Length + count > 64 * 1024) throw new ArgumentException("The request file must be at most 64 KiB.");
            copy.Write(buffer, 0, count);
        }
        return JsonNode.Parse(copy.ToArray())?.AsObject() ?? throw new InvalidDataException("Expected a JSON object envelope.");
    }
    private static string Required(Dictionary<string, string> options, string key) => options.TryGetValue(key, out var value) ? value : throw new ArgumentException("Missing " + key + ".");
    private static Guid User(Dictionary<string, string> options) => Id(Required(options, "--user"), "--user");
    private static Guid Id(string text, string field) => Guid.TryParseExact(text, "D", out var value) && value != Guid.Empty ? value : throw new ArgumentException(field + " must be a nonempty UUID.");
    private static void RequireAbsolute(string path, string field) { if (!Path.IsPathFullyQualified(path)) throw new ArgumentException(field + " must be an absolute path."); }
    private static void Print(JsonObject value) => Console.WriteLine(value.ToJsonString(JsonOptions));
}
