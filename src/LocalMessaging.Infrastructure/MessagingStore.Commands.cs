using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalMessaging.Application;
using LocalMessaging.Contracts;
using Microsoft.EntityFrameworkCore;

namespace LocalMessaging.Infrastructure;

public sealed partial class MessagingStore
{
    public Task<JsonObject> ExecuteAsync(JsonElement request, ValidatedImage? image = null, CancellationToken cancellationToken = default)
    {
        var normalized = CommandNormalizer.Create(request, _limits);
        var data = normalized["dataObject"]!.AsObject();
        var isImage = data["type"]!.GetValue<string>() == "message" && data["content"]!["type"]!.GetValue<string>() == "image";
        if (isImage && image is null) throw new ApiException(400, "invalid_contract", "Image messages require exactly one validated binary multipart part.");
        if (!isImage && image is not null) throw new ApiException(400, "invalid_contract", "Only image-message commands accept binary parts.");
        if (image is not null) CheckImage(image);
        return WriteAsync("createObject", normalized,
            (db, requestId, now, ct) => CreateObjectAsync(db, normalized, requestId, now, image, ct),
            image?.Sha256.ToLowerInvariant(), cancellationToken);
    }

    private async Task<WriteResult> CreateObjectAsync(MessagingDbContext db, JsonObject request, string requestId, long now, ValidatedImage? image, CancellationToken ct)
    {
        var data = request["dataObject"]!.AsObject();
        var kind = data["type"]!.GetValue<string>();
        var caller = request["callerUserId"]?.GetValue<string>();
        if (kind == "acknowledgement") return await AcknowledgeAsync(db, data, caller!, requestId, now, ct);
        if (kind == "participation") return await ChangeParticipationAsync(db, data, caller!, requestId, now, ct);
        var previousHead = await HeadAsync(db, ct);
        UserRow? user = null;
        ChannelRow? channel = null;
        SessionRow? session = null;
        MessageRow? message = null;
        ImageRow? asset = null;
        bool newChannel = false, newSession = false;
        string[] targets = [];
        var events = new List<EventRow>();

        if (kind == "user")
        {
            var handle = data["handle"]!.GetValue<string>();
            if (await db.Users.AnyAsync(x => x.Handle == handle, ct))
                throw new ApiException(409, "handle_already_exists", "That normalized handle is already used. Select the existing user or choose another handle.");
            user = new UserRow { Id = NewId(), Handle = handle, DisplayName = data["displayName"]!.GetValue<string>(), CreatedUtcMs = now };
            events.Add(Event("user", user.Id, user.Id, null, null, requestId, now));
        }
        else
        {
            if (kind == "channel")
            {
                channel = NewChannel(data, caller!, now);
                newChannel = true;
                session = NewSession(channel.Id, data["initialSessionTitle"]!.GetValue<string>(), caller!, now);
                newSession = true;
            }
            else
            {
                var channelSelector = data["channel"]!.AsObject();
                newChannel = channelSelector["mode"]!.GetValue<string>() == "new";
                channel = newChannel ? NewChannel(channelSelector, caller!, now)
                    : await RequireChannelAsync(db, channelSelector["id"]!.GetValue<string>(), ct);
                if (kind == "session")
                {
                    session = NewSession(channel.Id, data["title"]!.GetValue<string>(), caller!, now);
                    newSession = true;
                }
                else
                {
                    var selector = data["session"]!.AsObject();
                    newSession = selector["mode"]!.GetValue<string>() == "new";
                    session = newSession ? NewSession(channel.Id, selector["title"]!.GetValue<string>(), caller!, now)
                        : await RequireSessionAsync(db, selector["id"]!.GetValue<string>(), ct);
                    if (session.ChannelId != channel.Id)
                        throw new ApiException(422, "channel_session_mismatch", "The session belongs to a different channel.");
                    var content = data["content"]!.AsObject();
                    var text = content["type"]!.GetValue<string>() == "text" ? content["text"]?.GetValue<string>() : content["caption"]?.GetValue<string>();
                    targets = await ResolveTargetsAsync(db, data["targetUserIds"]!.AsArray(), text ?? "", ct);
                    MessageRow? parent = null;
                    if (data["parentMessageId"] is { } parentId)
                    {
                        parent = await RequireMessageAsync(db, parentId.GetValue<string>(), ct);
                        if (parent.SessionId != session.Id)
                            throw new ApiException(422, "parent_session_mismatch", "A reply parent must belong to the selected session.");
                        if (parent.Depth >= 3)
                            throw new ApiException(422, "thread_depth_exceeded", "A level-3 message cannot receive another reply.");
                    }
                    var messageId = NewId();
                    if (image is not null)
                        asset = new ImageRow
                        {
                            Id = NewId(), Bytes = image.Bytes.ToArray(), ContentType = image.ContentType, ByteLength = image.Bytes.Length,
                            Width = image.Width, Height = image.Height, Sha256 = image.Sha256.ToLowerInvariant(), CreatedUtcMs = now
                        };
                    message = new MessageRow
                    {
                        Id = messageId, SessionId = session.Id, AuthorUserId = caller!, Kind = content["type"]!.GetValue<string>(),
                        Text = text, ImageId = asset?.Id, ParentMessageId = parent?.Id,
                        ThreadRootMessageId = parent?.ThreadRootMessageId ?? messageId, Depth = (parent?.Depth ?? 0) + 1,
                        CreatedUtcMs = now
                    };
                }
            }
            if (newChannel) events.Add(Event("channel", channel.Id, caller!, channel.Id, null, requestId, now));
            if (newSession) events.Add(Event("session", session!.Id, caller!, channel.Id, session.Id, requestId, now));
            if (message is not null)
            {
                var entry = Event("message", message.Id, caller!, channel.Id, session!.Id, requestId, now);
                entry.TargetUserIdsJson = JsonSerializer.Serialize(targets);
                events.Add(entry);
            }
        }

        db.EventLog.AddRange(events);
        await db.SaveChangesAsync(ct); // Sequences are allocated before dependent immutable snapshots, inside this transaction.
        foreach (var e in events)
        {
            switch (e.ObjectType)
            {
                case "user": user!.CreatedSequence = e.Sequence; db.Users.Add(user); break;
                case "channel": channel!.CreatedSequence = e.Sequence; db.Channels.Add(channel); break;
                case "session": session!.CreatedSequence = e.Sequence; db.Sessions.Add(session); break;
                case "message": message!.CreatedSequence = e.Sequence; db.Messages.Add(message); break;
            }
        }
        if (asset is not null) db.ImageAssets.Add(asset);
        foreach (var target in targets) db.MessageTargets.Add(new MessageTargetRow { MessageId = message!.Id, UserId = target });
        await db.SaveChangesAsync(ct);

        var objects = new JsonObject();
        if (user is not null) objects["user"] = UserDto(user);
        if (channel is not null) objects["channel"] = ChannelDto(channel);
        if (session is not null) objects["session"] = SessionDto(session);
        if (message is not null) objects["message"] = MessageDto(message, channel!.Id, targets, asset is null ? null : ImageDto(asset));
        foreach (var e in events)
        {
            e.PayloadJson = objects[e.ObjectType]!.ToJsonString();
            if (Encoding.UTF8.GetByteCount(EventDto(e).ToJsonString()) > _limits.EventFrameBytes)
                throw new ApiException(413, "request_too_large", "The serialized event exceeds the configured stream frame limit.");
        }

        JsonObject? subscriptionReference = null;
        if (request["subscribe"] is { } subscribeNode)
        {
            var scope = subscribeNode["scope"]!.GetValue<string>();
            JsonObject filter = scope switch
            {
                "catalog" => EmptyFilter(catalog: true),
                "resolvedChannel" when channel is not null => EmptyFilter(channelId: channel.Id),
                "resolvedSession" when session is not null => EmptyFilter(sessionId: session.Id),
                _ => throw new ApiException(400, "invalid_contract", "This subscription scope cannot resolve a container in the request.")
            };
            var subscription = new SubscriptionRow
            {
                Id = NewId(), OwnerUserId = caller!, FilterJson = filter.ToJsonString(),
                StartSequence = previousHead, CreatedUtcMs = now
            };
            db.Subscriptions.Add(subscription);
            subscriptionReference = SubscriptionReference(subscription);
        }
        var first = events[0].Sequence;
        var last = events[^1].Sequence;
        var result = new JsonObject
        {
            ["dataObjects"] = objects,
            ["created"] = new JsonArray(events.Select(e => (JsonNode)new JsonObject { ["type"] = e.ObjectType, ["id"] = e.ObjectId }).ToArray()),
            ["eventRange"] = new JsonObject { ["firstSequence"] = first.ToString(), ["lastSequence"] = last.ToString() },
            ["afterRequestCursor"] = Cursor(last),
            ["subscription"] = subscriptionReference
        };
        return new WriteResult(result, first, last);
    }

    public Task<JsonObject> CreateSubscriptionAsync(JsonElement request, CancellationToken cancellationToken = default)
    {
        var normalized = CommandNormalizer.Subscription(request, _limits);
        return WriteAsync("createSubscription", normalized, async (db, requestId, now, ct) =>
        {
            var filter = normalized["filter"]!.AsObject();
            await ValidateFilterReferencesAsync(db, filter, ct);
            var head = await HeadAsync(db, ct);
            var start = normalized["start"]!.AsObject();
            var sequence = start["mode"]!.GetValue<string>() switch
            {
                "now" => head,
                "beginning" => 0,
                "after" => ValidateCursor(start["cursor"]!.GetValue<string>(), head),
                _ => throw new ApiException(400, "invalid_contract", "Invalid subscription start.")
            };
            var row = new SubscriptionRow
            {
                Id = NewId(), OwnerUserId = normalized["callerUserId"]!.GetValue<string>(), FilterJson = filter.ToJsonString(),
                StartSequence = sequence, CreatedUtcMs = now
            };
            db.Subscriptions.Add(row);
            return new WriteResult(new JsonObject { ["subscription"] = SubscriptionDto(row, head) });
        }, null, cancellationToken);
    }

    public Task<JsonObject> CloseSubscriptionAsync(Guid id, JsonElement request, CancellationToken cancellationToken = default)
    {
        var normalized = CommandNormalizer.Close(request, _limits);
        normalized["subscriptionId"] = id.ToString("D");
        return WriteAsync("closeSubscription", normalized, async (db, requestId, now, ct) =>
        {
            var subscription = await db.Subscriptions.SingleOrDefaultAsync(s => s.Id == id.ToString("D"), ct)
                ?? throw new ApiException(404, "subscription_not_found", "The subscription does not exist.");
            if (subscription.OwnerUserId != normalized["callerUserId"]!.GetValue<string>())
                throw new ApiException(403, "subscription_caller_mismatch", "Select the profile that owns this subscription.");
            subscription.State = "closed";
            subscription.ClosedUtcMs ??= now;
            return new WriteResult(new JsonObject
            {
                ["subscriptionId"] = subscription.Id, ["subscriptionState"] = "closed",
                ["closedAtUtc"] = CommandNormalizer.Utc(subscription.ClosedUtcMs.Value)
            }, ClosedSubscription: id);
        }, null, cancellationToken);
    }

    private async Task<string[]> ResolveTargetsAsync(MessagingDbContext db, JsonArray explicitIds, string text, CancellationToken ct)
    {
        var targets = explicitIds.Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        if (targets.Count > 0 && await db.Users.CountAsync(x => targets.Contains(x.Id), ct) != targets.Count)
            throw new ApiException(422, "invalid_target_user", "Every explicit target must identify an existing user.");
        var handles = CommandNormalizer.Mentions(text);
        if (handles.Count > 0)
        {
            var users = await db.Users.AsNoTracking().Where(x => handles.Contains(x.Handle)).Select(x => new { x.Id, x.Handle }).ToListAsync(ct);
            var resolved = users.Select(u => u.Handle).ToHashSet(StringComparer.Ordinal);
            var unknown = handles.Where(h => !resolved.Contains(h)).ToArray();
            if (unknown.Length > 0)
                throw new ApiException(422, "unknown_mention", "One or more syntactically valid mentions do not resolve to an existing user.",
                    new Dictionary<string, string[]> { ["dataObject.content"] = unknown });
            targets.UnionWith(users.Select(u => u.Id));
        }
        if (targets.Count > _limits.TargetUsers) throw new ApiException(422, "invalid_target_user", "The canonical target set exceeds the configured limit.");
        return targets.Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task ValidateFilterReferencesAsync(MessagingDbContext db, JsonObject filter, CancellationToken ct)
    {
        var channels = filter["channelIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        var sessions = filter["sessionIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        if (channels.Length > 0 && await db.Channels.CountAsync(x => channels.Contains(x.Id), ct) != channels.Length)
            throw new ApiException(404, "channel_not_found", "A selected channel does not exist.");
        if (sessions.Length > 0 && await db.Sessions.CountAsync(x => sessions.Contains(x.Id), ct) != sessions.Length)
            throw new ApiException(404, "session_not_found", "A selected session does not exist.");
        if (filter["targetUserId"] is { } target) await RequireUserAsync(db, target.GetValue<string>(), ct);
    }

    private void CheckImage(ValidatedImage image)
    {
        if (image.Bytes.Length == 0 || image.Bytes.Length > _limits.ImageBytes)
            throw new ApiException(413, "image_too_large", "The image exceeds the configured byte limit or is empty.");
        if (image.ContentType is not ("image/png" or "image/jpeg"))
            throw new ApiException(415, "unsupported_image_type", "Only verified PNG and JPEG images are accepted.");
        if (image.Width <= 0 || image.Height <= 0 || image.Width > _limits.ImageSide || image.Height > _limits.ImageSide || (long)image.Width * image.Height > _limits.ImagePixels)
            throw new ApiException(422, "image_dimensions_exceeded", "The verified image dimensions exceed the configured bounds.");
        if (!string.Equals(image.Sha256, Convert.ToHexStringLower(SHA256.HashData(image.Bytes)), StringComparison.OrdinalIgnoreCase))
            throw new ApiException(422, "invalid_image", "The verified image digest does not match its bytes.");
    }

    private static ChannelRow NewChannel(JsonObject data, string caller, long now) => new()
    {
        Id = NewId(), Name = data["name"]!.GetValue<string>(), Description = data["description"]?.GetValue<string>(),
        OwnerUserId = caller, CreatedUtcMs = now
    };
    private static SessionRow NewSession(string channel, string title, string caller, long now) => new()
    {
        Id = NewId(), ChannelId = channel, Title = title, OwnerUserId = caller, CreatedUtcMs = now
    };
    private static string NewId() => Guid.NewGuid().ToString("D");
    private static EventRow Event(string type, string id, string actor, string? channel, string? session, string request, long now) => new()
    {
        EventType = type + ".created", ObjectType = type, ObjectId = id, ActorUserId = actor,
        ChannelId = channel, SessionId = session, RequestId = request, OccurredUtcMs = now
    };
    private static JsonObject EmptyFilter(bool catalog = false, string? channelId = null, string? sessionId = null) => new()
    {
        ["catalog"] = catalog, ["allMessages"] = false,
        ["channelIds"] = channelId is null ? new JsonArray() : new JsonArray(channelId),
        ["sessionIds"] = sessionId is null ? new JsonArray() : new JsonArray(sessionId), ["targetUserId"] = null
    };
}
