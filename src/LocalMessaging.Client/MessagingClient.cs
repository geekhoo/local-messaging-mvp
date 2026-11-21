using System.Net;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalMessaging.Client;

public sealed record EndpointDescriptor(
    Guid InstanceId, int ProcessId, Guid StoreEpoch, string MessagingBaseUrl,
    string FrontendBaseUrl, string CertificateSha256, int ContractVersion, bool Ready)
{
    public static async Task<EndpointDescriptor> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var result = await JsonSerializer.DeserializeAsync<EndpointDescriptor>(stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken)
            ?? throw new InvalidDataException("Endpoint descriptor is empty.");
        if (!result.Ready || result.ContractVersion != 1) throw new InvalidDataException("Host descriptor is not ready or has an unsupported contract.");
        return result;
    }
}

public sealed record MessagingCapabilities(bool ProfileAcknowledgements, bool ParticipantRosters)
{
    public static MessagingCapabilities FromStatus(JsonObject status)
    {
        var capabilities = status["capabilities"] as JsonObject;
        bool Enabled(string key) => capabilities?[key] is JsonValue flag && flag.TryGetValue<bool>(out var enabled) && enabled;
        return new(Enabled("profileAcknowledgements"), Enabled("participantRosters"));
    }
}

public sealed class MessagingApiException : Exception
{
    public int Status { get; }
    public string Code { get; }
    public JsonObject? Details { get; }
    public TimeSpan? RetryAfter { get; }
    public bool RequiresResynchronization => Code is "store_changed" or "cursor_ahead_of_store" or "subscription_closed" or "subscription_not_found";
    public bool IsTemporary => Status is 429 or 503;
    public MessagingApiException(int status, string code, string message, JsonObject? details = null, TimeSpan? retryAfter = null)
        : base(message) => (Status, Code, Details, RetryAfter) = (status, code, details, retryAfter);
}

/// <summary>Owns a frozen command snapshot. Retrying never consults current UI/profile/draft state.</summary>
public sealed class FrozenCommand
{
    // This is an application/json body, never HTML. Preserve UTF-8 so a legal
    // 16,384-character non-ASCII message is not expanded beyond the 64 KiB envelope limit.
    private static readonly JsonSerializerOptions WireOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly byte[] json;
    private readonly byte[]? image;
    public Guid RequestId { get; }
    public Guid? CallerUserId { get; }
    public string Path { get; }
    public bool HasImage => image is not null;
    public string? ImageContentType { get; }

    private FrozenCommand(string path, JsonObject envelope, byte[]? bytes)
    {
        RequestId = Guid.Parse(envelope["requestId"]?.GetValue<string>() ?? throw new ArgumentException("Request ID is required."));
        CallerUserId = envelope["callerUserId"] is JsonValue caller ? Guid.Parse(caller.GetValue<string>()) : null;
        Path = path;
        json = JsonSerializer.SerializeToUtf8Bytes(envelope, WireOptions);
        image = bytes?.ToArray();
        ImageContentType = image is null ? null : image.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png" : "image/jpeg";
    }
    public static FrozenCommand Create(Guid? caller, JsonObject dataObject, byte[]? image = null, JsonObject? subscribe = null) =>
        new("requests", new JsonObject
        {
            ["contractVersion"] = 1, ["requestId"] = Guid.NewGuid().ToString(),
            ["callerUserId"] = caller?.ToString(), ["dataObject"] = dataObject.DeepClone(),
            ["subscribe"] = subscribe?.DeepClone()
        }, image);

    public static FrozenCommand CreateAcknowledgement(Guid caller, Guid messageId) => Create(caller, new JsonObject
    {
        ["type"] = "acknowledgement", ["messageId"] = messageId.ToString("D")
    });

    public static FrozenCommand CreateChannelParticipation(Guid caller, Guid channelId, bool joined) =>
        CreateParticipation(caller, "channelId", channelId, joined);

    public static FrozenCommand CreateSessionParticipation(Guid caller, Guid sessionId, bool joined) =>
        CreateParticipation(caller, "sessionId", sessionId, joined);

    private static FrozenCommand CreateParticipation(Guid caller, string scopeKey, Guid scopeId, bool joined) => Create(caller, new JsonObject
    {
        ["type"] = "participation", [scopeKey] = scopeId.ToString("D"), ["joined"] = joined
    });

    public static FrozenCommand Operation(string path, JsonObject envelope, byte[]? image = null) => new(path, (JsonObject)envelope.DeepClone(), image);
    public JsonObject GetEnvelope() => JsonNode.Parse(json)!.AsObject();

    internal HttpContent CreateContent()
    {
        var request = new ByteArrayContent(json);
        request.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (image is null) return request;
        var multipart = new MultipartFormDataContent();
        multipart.Add(request, "request");
        var binary = new ByteArrayContent(image);
        binary.Headers.ContentType = new MediaTypeHeaderValue(ImageContentType!);
        multipart.Add(binary, "image", ImageContentType == "image/png" ? "image.png" : "image.jpg");
        return multipart;
    }
}

public sealed class MessagingClient : IDisposable
{
    public const string ClientHeader = "X-Local-Messaging-Client";
    private readonly HttpClient http;
    private readonly bool exactHttp3;
    private byte[]? trustedCertificatePin;
    private string? descriptorPath;
    public Uri BaseAddress { get; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(45);

    public MessagingClient(Uri apiBase, string? certificateSha256 = null, bool useHttp3 = false)
    {
        ValidateBase(apiBase);
        if (useHttp3 && (apiBase.Scheme != "https" || string.IsNullOrWhiteSpace(certificateSha256)))
            throw new ArgumentException("Exact HTTP/3 requires HTTPS and the descriptor's certificate pin.");
        if (certificateSha256 is not null && (certificateSha256.Length != 64 || !certificateSha256.All(Uri.IsHexDigit)))
            throw new ArgumentException("The certificate pin must be a SHA-256 hexadecimal value.");
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        if (useHttp3)
        {
            trustedCertificatePin = Convert.FromHexString(certificateSha256!);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
                    return false;
                using var candidate = new X509Certificate2(certificate);
                var now = DateTime.UtcNow;
                return candidate.NotBefore.ToUniversalTime() <= now && candidate.NotAfter.ToUniversalTime() >= now
                    && CryptographicOperations.FixedTimeEquals(SHA256.HashData(candidate.RawData), Volatile.Read(ref trustedCertificatePin)!);
            };
        }
        http = new HttpClient(handler) { BaseAddress = apiBase, Timeout = Timeout.InfiniteTimeSpan };
        BaseAddress = apiBase;
        exactHttp3 = useHttp3;
    }

    public static MessagingClient FromDescriptor(EndpointDescriptor descriptor, bool messaging = false) =>
        messaging ? new(new Uri(descriptor.MessagingBaseUrl), descriptor.CertificateSha256, true)
                  : new(new Uri(descriptor.FrontendBaseUrl));

    public static async Task<MessagingClient> FromDescriptorFileAsync(string path, bool messaging = false, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var descriptor = await EndpointDescriptor.ReadAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var client = FromDescriptor(descriptor, messaging);
        client.descriptorPath = fullPath;
        return client;
    }

    public async Task RefreshDescriptorAsync(CancellationToken cancellationToken = default)
    {
        if (descriptorPath is null || !exactHttp3) return;
        var descriptor = await EndpointDescriptor.ReadAsync(descriptorPath, cancellationToken).ConfigureAwait(false);
        if (new Uri(descriptor.MessagingBaseUrl) != BaseAddress)
            throw new InvalidDataException("The local endpoint moved. Create a new client from the updated descriptor.");
        if (descriptor.CertificateSha256.Length != 64 || !descriptor.CertificateSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The updated descriptor has an invalid certificate fingerprint.");
        Interlocked.Exchange(ref trustedCertificatePin, Convert.FromHexString(descriptor.CertificateSha256));
    }

    private static void ValidateBase(Uri address)
    {
        if (!address.IsAbsoluteUri) throw new ArgumentException("The API base must be absolute.", nameof(address));
        var local = string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(address.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
        if (!address.IsAbsoluteUri || !local || address.Scheme is not ("http" or "https") || !address.AbsolutePath.EndsWith('/') || !string.IsNullOrEmpty(address.UserInfo))
            throw new ArgumentException("The API base must be an absolute loopback HTTP(S) URL ending with '/'.");
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out _) || path.StartsWith('/') || path.Contains('\\') || path.Split('?')[0].Split('/').Any(s => s == ".."))
            throw new ArgumentException("API paths must be relative to the configured local base.", nameof(path));
        var request = new HttpRequestMessage(method, path)
        {
            Version = exactHttp3 ? HttpVersion.Version30 : HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.TryAddWithoutValidation(ClientHeader, "1");
        return request;
    }

    private async Task<HttpResponseMessage> SendHeadersAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException) when (exactHttp3 && descriptorPath is not null)
        {
            // A controlled certificate renewal atomically updates this trusted local descriptor.
            // The next frozen-command retry/reconnect constructs a new HTTP request with the new pin.
            await RefreshDescriptorAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
        if (exactHttp3 && response.Version != HttpVersion.Version30)
        {
            response.Dispose();
            throw new HttpRequestException("The messaging listener did not negotiate exact HTTP/3.");
        }
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                var bytes = await ReadBoundedAsync(response.Content, 64 * 1024, cancellationToken).ConfigureAwait(false);
                JsonObject? body = null;
                try { body = JsonNode.Parse(bytes)?.AsObject(); } catch (JsonException) { }
                TimeSpan? retry = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is DateTimeOffset at ? at - DateTimeOffset.UtcNow : null);
                throw new MessagingApiException((int)response.StatusCode, body?["code"]?.GetValue<string>() ?? "http_error",
                    body?["detail"]?.GetValue<string>() ?? body?["title"]?.GetValue<string>() ?? $"HTTP {(int)response.StatusCode}",
                    body, retry);
            }
            finally { response.Dispose(); }
        }
        return response;
    }

    public async Task<JsonObject> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = Request(HttpMethod.Get, path);
        using var response = await SendHeadersAsync(request, timeout.Token).ConfigureAwait(false);
        return await ReadObjectAsync(response.Content, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Reads current host support. Flags absent on older hosts are false.</summary>
    public async Task<MessagingCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        MessagingCapabilities.FromStatus(await GetAsync("status", cancellationToken).ConfigureAwait(false));

    public Task<JsonObject> GetAcknowledgementsAsync(Guid messageId, int? limit = null, string? asOf = null,
        string? pageToken = null, CancellationToken cancellationToken = default) =>
        GetSnapshotPageAsync($"messages/{messageId:D}/acknowledgements", limit, asOf, pageToken, cancellationToken);

    public Task<JsonObject> GetChannelParticipantsAsync(Guid channelId, int? limit = null, string? asOf = null,
        string? pageToken = null, CancellationToken cancellationToken = default) =>
        GetSnapshotPageAsync($"channels/{channelId:D}/participants", limit, asOf, pageToken, cancellationToken);

    public Task<JsonObject> GetSessionParticipantsAsync(Guid sessionId, int? limit = null, string? asOf = null,
        string? pageToken = null, CancellationToken cancellationToken = default) =>
        GetSnapshotPageAsync($"sessions/{sessionId:D}/participants", limit, asOf, pageToken, cancellationToken);

    private Task<JsonObject> GetSnapshotPageAsync(string path, int? limit, string? asOf, string? pageToken, CancellationToken cancellationToken)
    {
        var query = new List<string>(3);
        if (limit is int size) query.Add("limit=" + size.ToString(CultureInfo.InvariantCulture));
        if (asOf is not null) query.Add("asOf=" + Uri.EscapeDataString(asOf));
        if (pageToken is not null) query.Add("pageToken=" + Uri.EscapeDataString(pageToken));
        return GetAsync(query.Count == 0 ? path : path + "?" + string.Join("&", query), cancellationToken);
    }

    public Task<JsonObject> AcknowledgeAsync(Guid caller, Guid messageId, CancellationToken cancellationToken = default) =>
        SendWithRecoveryAsync(FrozenCommand.CreateAcknowledgement(caller, messageId), cancellationToken: cancellationToken);

    public Task<JsonObject> SetChannelParticipationAsync(Guid caller, Guid channelId, bool joined, CancellationToken cancellationToken = default) =>
        SendWithRecoveryAsync(FrozenCommand.CreateChannelParticipation(caller, channelId, joined), cancellationToken: cancellationToken);

    public Task<JsonObject> SetSessionParticipationAsync(Guid caller, Guid sessionId, bool joined, CancellationToken cancellationToken = default) =>
        SendWithRecoveryAsync(FrozenCommand.CreateSessionParticipation(caller, sessionId, joined), cancellationToken: cancellationToken);

    public async Task<JsonObject> SendAsync(FrozenCommand command, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = Request(HttpMethod.Post, command.Path);
        request.Content = command.CreateContent();
        using var response = await SendHeadersAsync(request, timeout.Token).ConfigureAwait(false);
        var receipt = await ReadObjectAsync(response.Content, timeout.Token).ConfigureAwait(false);
        if (receipt["contractVersion"] is not JsonValue version || !version.TryGetValue<int>(out var major) || major != 1
            || receipt["state"]?.GetValue<string>() != "committed"
            || !Guid.TryParse(receipt["requestId"]?.GetValue<string>(), out var requestId) || requestId != command.RequestId
            || !Guid.TryParse(receipt["storeEpoch"]?.GetValue<string>(), out _))
            throw new MessagingApiException(502, "invalid_receipt", "The response did not confirm this request. Preserve its request ID and retry the same command.");
        return receipt;
    }

    public async Task<JsonObject> SendWithRecoveryAsync(FrozenCommand command, int maximumAttempts = 3, CancellationToken cancellationToken = default)
    {
        if (maximumAttempts is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        for (var attempt = 0; ; attempt++)
        {
            try { return await SendAsync(command, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (attempt + 1 < maximumAttempts && !cancellationToken.IsCancellationRequested
                && (exception is HttpRequestException or OperationCanceledException || exception is MessagingApiException { IsTemporary: true }))
            {
                // The exact same FrozenCommand owns all retry content and caller identity.
                var retry = (exception as MessagingApiException)?.RetryAfter;
                var delay = retry ?? TimeSpan.FromMilliseconds(Math.Min(15000, 500 * Math.Pow(2, attempt)) + Random.Shared.Next(100));
                await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<byte[]> GetImageAsync(string contentPath, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = Request(HttpMethod.Get, contentPath);
        using var response = await SendHeadersAsync(request, timeout.Token).ConfigureAwait(false);
        var mime = response.Content.Headers.ContentType?.MediaType;
        if (mime is not ("image/png" or "image/jpeg")) throw new InvalidDataException("Unexpected image content type.");
        return await ReadBoundedAsync(response.Content, 8 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<SseFrame> ReadStreamAsync(string subscriptionId, string? after,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(subscriptionId, out _)) throw new ArgumentException("Invalid subscription ID.", nameof(subscriptionId));
        var path = $"subscriptions/{subscriptionId}/stream" + (after is null ? "" : "?after=" + Uri.EscapeDataString(after));
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(StreamIdleTimeout);
        using var request = Request(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await SendHeadersAsync(request, idle.Token).ConfigureAwait(false);
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The subscription did not return an SSE stream.");
        await using var stream = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
        var parser = new SseParser();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            if (count == 0) break;
            idle.CancelAfter(StreamIdleTimeout); // Any bytes count, including comments and split frames.
            foreach (var frame in parser.Feed(buffer.AsSpan(0, count))) yield return frame;
        }
        parser.Complete();
    }

    public async Task<JsonObject> CreateSubscriptionAsync(Guid caller, string after, CancellationToken cancellationToken = default, bool includeAcknowledgements = false)
    {
        var filter = new JsonObject { ["catalog"] = true, ["allMessages"] = true, ["channelIds"] = new JsonArray(), ["sessionIds"] = new JsonArray(), ["targetUserId"] = null };
        if (includeAcknowledgements) filter["allAcknowledgements"] = true;
        var command = FrozenCommand.Operation("subscriptions", new JsonObject
        {
            ["contractVersion"] = 1, ["requestId"] = Guid.NewGuid().ToString(), ["callerUserId"] = caller.ToString(),
            ["filter"] = filter,
            ["start"] = new JsonObject { ["mode"] = "after", ["cursor"] = after }
        });
        var receipt = await SendWithRecoveryAsync(command, cancellationToken: cancellationToken).ConfigureAwait(false);
        return receipt["subscription"]?.AsObject() ?? throw new InvalidDataException("Missing subscription descriptor.");
    }

    public Task<JsonObject> CloseSubscriptionAsync(Guid owner, string id, CancellationToken cancellationToken = default) =>
        SendWithRecoveryAsync(FrozenCommand.Operation($"subscriptions/{id}/close", new JsonObject
        {
            ["contractVersion"] = 1, ["requestId"] = Guid.NewGuid().ToString(), ["callerUserId"] = owner.ToString()
        }), cancellationToken: cancellationToken);

    private static async Task<JsonObject> ReadObjectAsync(HttpContent content, CancellationToken token)
    {
        var result = JsonNode.Parse(await ReadBoundedAsync(content, 32 * 1024 * 1024, token).ConfigureAwait(false))?.AsObject()
            ?? throw new InvalidDataException("Expected a JSON object.");
        if (result["contractVersion"] is JsonValue v && v.TryGetValue<int>(out var version) && version != 1)
            throw new MessagingApiException(400, "unsupported_contract_version", "This client needs an update for the server contract.");
        return result;
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken token)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("Response exceeds the client byte limit.");
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > limit) throw new InvalidDataException("Response exceeds the client byte limit.");
            output.Write(buffer, 0, count);
        }
    }
    public void Dispose() => http.Dispose();
}

/// <summary>One run loop owns one consumer cursor, independently of every other client.</summary>
public sealed class SubscriptionConsumer(MessagingClient client, string subscriptionId, string startCursor)
{
    private int running;
    public string Cursor { get; private set; } = ValidateStartingCursor(startCursor);
    public string SubscriptionId { get; } = subscriptionId;

    public async Task RunAsync(Func<JsonObject, CancellationToken, Task> applyEvent,
        Action<string>? connectionState = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("A consumer already has a reconnect loop.");
        var attempt = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan? delay = null;
                try
                {
                    connectionState?.Invoke(attempt == 0 ? "Connecting" : "Reconnecting");
                    await foreach (var frame in client.ReadStreamAsync(SubscriptionId, Cursor, cancellationToken).ConfigureAwait(false))
                    {
                        connectionState?.Invoke("Connected");
                        if (frame.Event is "stream.closed" or "subscription_closed")
                            throw new MessagingApiException(410, "subscription_closed", "The saved subscription was closed.");
                        if (frame.Event is "store_changed" or "cursor_ahead_of_store")
                            throw new MessagingApiException(409, frame.Event, "The store/cursor changed; reload a fresh snapshot.");
                        if (frame.Event is "database_busy" or "storage_unavailable" or "host_not_ready")
                            throw new MessagingApiException(503, frame.Event, "The host temporarily interrupted this stream.");
                        if (frame.Event == "server.stopping") break;
                        var body = JsonNode.Parse(frame.Data)?.AsObject() ?? throw new InvalidDataException("Invalid SSE data.");
                        if (body["contractVersion"]?.GetValue<int>() != 1)
                            throw new MessagingApiException(400, "unsupported_contract_version", "Update required for the stream contract.");
                        if (!frame.HasExplicitId || !TryCursor(frame.Id, out var epoch, out var sequence)
                            || body["cursor"]?.GetValue<string>() != frame.Id)
                            throw InvalidStream("The frame ID and body cursor must be the same valid durable cursor.");
                        _ = TryCursor(Cursor, out var currentEpoch, out var currentSequence);
                        if (epoch != currentEpoch)
                            throw new MessagingApiException(409, "store_changed", "The stream changed store epoch. Reload a fresh snapshot.");
                        if (frame.Event != "stream.checkpoint"
                            && (body["eventType"]?.GetValue<string>() != frame.Event
                                || !long.TryParse(body["sequence"]?.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out var bodySequence)
                                || bodySequence != sequence))
                            throw InvalidStream("The event type and sequence do not agree with the SSE envelope.");
                        // Replays are at-least-once. Duplicate or older durable positions must never
                        // apply twice or rewind this independently owned consumer cursor.
                        if (sequence <= currentSequence) continue;
                        if (frame.Event != "stream.checkpoint") await applyEvent(body, cancellationToken).ConfigureAwait(false);
                        // Apply is awaited before the cursor can advance. Unknown optional events may be ignored by apply.
                        Cursor = frame.Id!;
                        attempt = 0;
                    }
                }
                catch (MessagingApiException ex) when (ex.IsTemporary) { delay = ex.RetryAfter; }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                catch (HttpRequestException) { }
                catch (IOException) { }
                if (cancellationToken.IsCancellationRequested) break;
                connectionState?.Invoke("Reconnecting");
                delay ??= TimeSpan.FromMilliseconds(Math.Min(15000, 500 * Math.Pow(2, Math.Min(attempt++, 5))) + Random.Shared.Next(100));
                await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref running, 0); }
    }

    private static string ValidateStartingCursor(string cursor) => TryCursor(cursor, out _, out _) ? cursor
        : throw new ArgumentException("A consumer requires an epoch-qualified, nonnegative 64-bit cursor.", nameof(cursor));

    private static bool TryCursor(string? cursor, out Guid epoch, out long sequence)
    {
        epoch = default; sequence = 0;
        if (cursor is null) return false;
        var separator = cursor.IndexOf(':');
        return separator == 36 && Guid.TryParseExact(cursor.AsSpan(0, separator), "D", out epoch)
            && long.TryParse(cursor.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out sequence) && sequence >= 0;
    }

    private static MessagingApiException InvalidStream(string detail) => new(502, "invalid_stream", detail);
}
