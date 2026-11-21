using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalMessaging.Application;
using LocalMessaging.Contracts;
using Microsoft.EntityFrameworkCore;

namespace LocalMessaging.Infrastructure;

public sealed partial class MessagingStore
{
    /// <summary>
    /// users/channels: null=list, id=detail. sessions/messages: id=detail.
    /// channelSessions/sessionMessages: id=parent. mentions=id user, thread=id message.
    /// subscriptions: null=operational owner list, id=descriptor.
    /// </summary>
    public async Task<JsonNode> QueryAsync(string resource, Guid? id = null, PageQuery? query = null, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        query ??= new PageQuery();
        if (query.Limit < 1 || query.Limit > _limits.PageSize)
            throw new ApiException(400, "invalid_contract", "The page limit is outside the configured range.");
        await using var db = await CreateContextAsync(cancellationToken);
        var key = id?.ToString("D");
        if (resource == "subscriptions")
            return key is not null
                ? SubscriptionDto(await RequireSubscriptionAsync(db, key, cancellationToken), await HeadAsync(db, cancellationToken))
                : await SubscriptionListAsync(db, query, cancellationToken);
        if (key is not null)
        {
            switch (resource)
            {
                case "users": return UserDto(await RequireUserAsync(db, key, cancellationToken));
                case "channels": return ChannelDto(await RequireChannelAsync(db, key, cancellationToken));
                case "sessions": return SessionDto(await RequireSessionAsync(db, key, cancellationToken));
                case "messages":
                    var message = await RequireMessageAsync(db, key, cancellationToken);
                    return (await MessageDtosAsync(db, [message], await HeadAsync(db, cancellationToken), cancellationToken))[0];
            }
        }

        var prefix = query.HandlePrefix?.ToLowerInvariant() ?? "";
        if (prefix.Length > 32 || prefix.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            throw new ApiException(400, "invalid_contract", "handlePrefix must be at most 32 ASCII handle characters.");
        var view = query.View ?? "roots";
        if (resource == "sessionMessages" && view is not ("roots" or "flat"))
            throw new ApiException(400, "invalid_contract", "view must be roots or flat.");
        var scope = resource + "|" + key + "|" + (resource == "users" ? prefix : "") + "|" + (resource == "sessionMessages" ? view : "");
        var page = await PageStateAsync(db, scope, query, cancellationToken);
        switch (resource)
        {
            case "users" when key is null:
            {
                var rows = db.Users.AsNoTracking().Where(x => x.CreatedSequence <= page.Head);
                if (prefix.Length > 0) rows = rows.Where(x => x.Handle.StartsWith(prefix));
                if (page.Last is { } last) rows = rows.Where(x => x.CreatedSequence > last);
                var list = await rows.OrderBy(x => x.CreatedSequence).Take(query.Limit + 1).ToListAsync(cancellationToken);
                return Page(list, query.Limit, page, x => x.CreatedSequence, UserDto);
            }
            case "channels" when key is null:
            {
                var rows = db.Channels.AsNoTracking().Where(x => x.CreatedSequence <= page.Head);
                if (page.Last is { } last) rows = rows.Where(x => x.CreatedSequence > last);
                var list = await rows.OrderBy(x => x.CreatedSequence).Take(query.Limit + 1).ToListAsync(cancellationToken);
                return Page(list, query.Limit, page, x => x.CreatedSequence, ChannelDto);
            }
            case "channelSessions" when key is not null:
            {
                await RequireChannelAsync(db, key, cancellationToken);
                var rows = db.Sessions.AsNoTracking().Where(x => x.ChannelId == key && x.CreatedSequence <= page.Head);
                if (page.Last is { } last) rows = rows.Where(x => x.CreatedSequence > last);
                var list = await rows.OrderBy(x => x.CreatedSequence).Take(query.Limit + 1).ToListAsync(cancellationToken);
                return Page(list, query.Limit, page, x => x.CreatedSequence, SessionDto);
            }
            case "sessionMessages" when key is not null:
            {
                await RequireSessionAsync(db, key, cancellationToken);
                var rows = db.Messages.AsNoTracking().Where(x => x.SessionId == key && x.CreatedSequence <= page.Head);
                if (view == "roots") rows = rows.Where(x => x.Depth == 1);
                if (page.Last is { } last) rows = rows.Where(x => x.CreatedSequence < last);
                var list = await rows.OrderByDescending(x => x.CreatedSequence).Take(query.Limit + 1).ToListAsync(cancellationToken);
                return await MessagePageAsync(db, list, query.Limit, page, cancellationToken);
            }
            case "mentions" when key is not null:
            {
                await RequireUserAsync(db, key, cancellationToken);
                var rows = db.Messages.AsNoTracking().Where(m => m.CreatedSequence <= page.Head && db.MessageTargets.Any(t => t.MessageId == m.Id && t.UserId == key));
                if (page.Last is { } last) rows = rows.Where(x => x.CreatedSequence < last);
                var list = await rows.OrderByDescending(x => x.CreatedSequence).Take(query.Limit + 1).ToListAsync(cancellationToken);
                return await MessagePageAsync(db, list, query.Limit, page, cancellationToken);
            }
            case "acknowledgements" when key is not null:
                return await AcknowledgementsAsync(db, key, query.Limit, page, cancellationToken);
            case "channelParticipants" when key is not null:
                await RequireChannelAsync(db, key, cancellationToken);
                return await ParticipantsAsync(db, key, null, query.Limit, page, cancellationToken);
            case "sessionParticipants" when key is not null:
                var session = await RequireSessionAsync(db, key, cancellationToken);
                return await ParticipantsAsync(db, session.ChannelId, key, query.Limit, page, cancellationToken);
            case "thread" when key is not null:
                return await ThreadAsync(db, key, query.Limit, page, cancellationToken);
            default: throw new ApiException(400, "invalid_contract", "Invalid query resource or missing object ID.");
        }
    }

    public async Task<JsonObject> GetSubscriptionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        await using var db = await CreateContextAsync(cancellationToken);
        var row = await RequireSubscriptionAsync(db, id.ToString("D"), cancellationToken);
        return SubscriptionDto(row, await HeadAsync(db, cancellationToken));
    }

    public async Task<JsonObject> ReadEventsAsync(Guid id, string? after = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        if (limit < 1 || limit > _limits.PageSize)
            throw new ApiException(400, "invalid_contract", "The event page limit is outside the configured range.");
        await using var db = await CreateContextAsync(cancellationToken);
        var subscription = await RequireSubscriptionAsync(db, id.ToString("D"), cancellationToken);
        if (subscription.State != "active") throw new ApiException(410, "subscription_closed", "The saved subscription is closed.");
        var head = await HeadAsync(db, cancellationToken);
        var position = after is null ? subscription.StartSequence : ValidateCursor(after, head, subscription.StartSequence);
        var filter = JsonNode.Parse(subscription.FilterJson)!.AsObject();
        var channels = filter["channelIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var sessions = filter["sessionIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var target = filter["targetUserId"]?.GetValue<string>();
        var catalog = filter["catalog"]!.GetValue<bool>();
        var allMessages = filter["allMessages"]!.GetValue<bool>();
        var allAcknowledgements = filter["allAcknowledgements"]?.GetValue<bool>() == true;
        // Header-only scan keeps broad/sparse filters bounded without materializing unrelated payloads.
        var headers = await db.EventLog.AsNoTracking()
            .Where(e => e.Sequence > position && e.Sequence <= head).OrderBy(e => e.Sequence)
            .Select(e => new EventHeader(e.Sequence, e.EventType, e.ChannelId, e.SessionId, e.TargetUserIdsJson))
            .Take(_limits.EventHeaders).ToListAsync(cancellationToken);
        var items = new JsonArray();
        var bytes = 0;
        var next = position;
        foreach (var header in headers)
        {
            var matches = catalog && header.EventType is ("user.created" or "channel.created" or "session.created")
                || allMessages && header.EventType == "message.created"
                || allAcknowledgements && header.EventType == "acknowledgement.created"
                || header.ChannelId is not null && channels.Contains(header.ChannelId)
                || header.SessionId is not null && sessions.Contains(header.SessionId)
                || target is not null && header.EventType == "message.created" && (JsonSerializer.Deserialize<string[]>(header.Targets) ?? []).Contains(target, StringComparer.Ordinal);
            if (matches)
            {
                if (items.Count >= limit) break;
                var row = await db.EventLog.AsNoTracking().SingleAsync(e => e.Sequence == header.Sequence, cancellationToken);
                var item = EventDto(row);
                var itemBytes = Encoding.UTF8.GetByteCount(item.ToJsonString());
                if (itemBytes > _limits.EventPayloadBytes && items.Count == 0)
                    throw new ApiException(413, "event_too_large", "A retained event exceeds the configured payload budget; restore a sufficient event limit.");
                if (bytes + itemBytes > _limits.EventPayloadBytes) break;
                items.Add(item);
                bytes += itemBytes;
            }
            next = header.Sequence; // Never advance past an excluded matching event.
            if (items.Count >= limit) break;
        }
        return new JsonObject
        {
            ["contractVersion"] = 1, ["subscriptionId"] = subscription.Id, ["items"] = items,
            ["nextCursor"] = Cursor(next), ["headCursor"] = Cursor(head), ["hasMore"] = next < head
        };
    }

    private async Task<JsonObject> ThreadAsync(MessagingDbContext db, string messageId, int limit, PageState page, CancellationToken ct)
    {
        var message = await RequireMessageAsync(db, messageId, ct);
        if (message.CreatedSequence > page.Head) throw new ApiException(404, "message_not_found", "The message is newer than the requested snapshot.");
        var root = await RequireMessageAsync(db, message.ThreadRootMessageId, ct);
        var rows = db.Messages.AsNoTracking().Where(x => x.ThreadRootMessageId == root.Id && x.Depth > 1 && x.CreatedSequence <= page.Head);
        if (page.Last is { } last) rows = rows.Where(x => x.CreatedSequence > last);
        var fetched = await rows.OrderBy(x => x.CreatedSequence).Take(limit + 1).ToListAsync(ct);
        var more = fetched.Count > limit;
        var selected = fetched.Take(limit).ToList();
        var selectedIds = selected.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var ancestorIds = selected.Where(x => x.ParentMessageId is not null && x.ParentMessageId != root.Id && !selectedIds.Contains(x.ParentMessageId))
            .Select(x => x.ParentMessageId!).Distinct().ToArray();
        var context = ancestorIds.Length == 0 ? [] : await db.Messages.AsNoTracking().Where(x => ancestorIds.Contains(x.Id) && x.CreatedSequence <= page.Head).OrderBy(x => x.CreatedSequence).ToListAsync(ct);
        var rootDto = (await MessageDtosAsync(db, [root], page.Head, ct))[0];
        var itemDtos = await MessageDtosAsync(db, selected, page.Head, ct);
        var contextDtos = await MessageDtosAsync(db, context, page.Head, ct);
        return new JsonObject
        {
            ["root"] = rootDto, ["items"] = new JsonArray(itemDtos.Select(x => (JsonNode)x).ToArray()),
            ["contextMessages"] = new JsonArray(contextDtos.Select(x => (JsonNode)x).ToArray()),
            ["snapshotCursor"] = Cursor(page.Head),
            ["nextPageToken"] = more ? PageToken(page, selected[^1].CreatedSequence) : null
        };
    }

    private async Task<JsonObject> MessagePageAsync(MessagingDbContext db, List<MessageRow> fetched, int limit, PageState page, CancellationToken ct)
    {
        var more = fetched.Count > limit;
        var selected = fetched.Take(limit).ToList();
        var dtos = await MessageDtosAsync(db, selected, page.Head, ct);
        return new JsonObject
        {
            ["items"] = new JsonArray(dtos.Select(x => (JsonNode)x).ToArray()), ["snapshotCursor"] = Cursor(page.Head),
            ["nextPageToken"] = more ? PageToken(page, selected[^1].CreatedSequence) : null
        };
    }

    private JsonObject Page<T>(List<T> fetched, int limit, PageState page, Func<T, long> sequence, Func<T, JsonObject> dto)
    {
        var more = fetched.Count > limit;
        var selected = fetched.Take(limit).ToList();
        return new JsonObject
        {
            ["items"] = new JsonArray(selected.Select(x => (JsonNode)dto(x)).ToArray()),
            ["snapshotCursor"] = Cursor(page.Head),
            ["nextPageToken"] = more ? PageToken(page, sequence(selected[^1])) : null
        };
    }

    private async Task<PageState> PageStateAsync(MessagingDbContext db, string scope, PageQuery query, CancellationToken ct)
    {
        var head = await HeadAsync(db, ct);
        if (query.PageToken is null)
            return new PageState(query.AsOf is null ? head : ValidateCursor(query.AsOf, head), null, scope);
        var token = DecodeToken(query.PageToken);
        try
        {
            if (token["scope"]!.GetValue<string>() != scope) throw BadToken();
            var h = ValidateCursor(token["snapshotCursor"]!.GetValue<string>(), head);
            var lastText = token["last"]!.GetValue<string>();
            if (!long.TryParse(lastText, NumberStyles.None, CultureInfo.InvariantCulture, out var last) || last < 0 || last > h) throw BadToken();
            if (query.AsOf is not null && ValidateCursor(query.AsOf, head) != h) throw BadToken();
            return new PageState(h, last, scope);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or FormatException) { throw BadToken(); }
    }

    private string PageToken(PageState page, long last) => EncodeToken(new JsonObject
    {
        ["scope"] = page.Scope, ["snapshotCursor"] = Cursor(page.Head), ["last"] = last.ToString(CultureInfo.InvariantCulture)
    });

    private async Task<JsonObject> SubscriptionListAsync(MessagingDbContext db, PageQuery query, CancellationToken ct)
    {
        if (query.OwnerUserId is null) throw new ApiException(400, "invalid_contract", "ownerUserId is required when listing subscriptions.");
        var owner = query.OwnerUserId.Value.ToString("D");
        await RequireUserAsync(db, owner, ct);
        if (query.AsOf is not null) throw new ApiException(400, "invalid_contract", "Subscription lists are operational queries and do not accept asOf.");
        var rows = db.Subscriptions.AsNoTracking().Where(x => x.OwnerUserId == owner);
        if (query.PageToken is not null)
        {
            var token = DecodeToken(query.PageToken);
            try
            {
                if (token["scope"]!.GetValue<string>() != "subscriptions|" + owner) throw BadToken();
                if (token["epoch"]!.GetValue<string>() != _epoch) throw new ApiException(409, "store_changed", "The page belongs to another store history.");
                var time = token["time"]!.GetValue<long>();
                var lastId = token["id"]!.GetValue<string>();
                if (time < 0 || !Guid.TryParseExact(lastId, "D", out _)) throw BadToken();
                rows = rows.Where(x => x.CreatedUtcMs > time || x.CreatedUtcMs == time && x.Id.CompareTo(lastId) > 0);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or FormatException) { throw BadToken(); }
        }
        var fetched = await rows.OrderBy(x => x.CreatedUtcMs).ThenBy(x => x.Id).Take(query.Limit + 1).ToListAsync(ct);
        var selected = fetched.Take(query.Limit).ToArray();
        var head = await HeadAsync(db, ct);
        return new JsonObject
        {
            ["items"] = new JsonArray(selected.Select(x => (JsonNode)SubscriptionDto(x, head)).ToArray()),
            ["nextPageToken"] = fetched.Count <= query.Limit ? null : EncodeToken(new JsonObject
            {
                ["scope"] = "subscriptions|" + owner, ["epoch"] = _epoch,
                ["time"] = selected[^1].CreatedUtcMs, ["id"] = selected[^1].Id
            })
        };
    }

    private async Task<List<JsonObject>> MessageDtosAsync(MessagingDbContext db, IReadOnlyCollection<MessageRow> messages, long head, CancellationToken ct)
    {
        if (messages.Count == 0) return [];
        var ids = messages.Select(x => x.Id).ToArray();
        var sessionIds = messages.Select(x => x.SessionId).Distinct().ToArray();
        var imageIds = messages.Where(x => x.ImageId is not null).Select(x => x.ImageId!).Distinct().ToArray();
        var targets = await db.MessageTargets.AsNoTracking().Where(t => ids.Contains(t.MessageId)).ToListAsync(ct);
        var channels = await db.Sessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id)).Select(s => new { s.Id, s.ChannelId }).ToDictionaryAsync(s => s.Id, s => s.ChannelId, ct);
        // Explicit metadata projection prevents BLOB loading in message/query/event DTOs.
        var images = await db.ImageAssets.AsNoTracking().Where(i => imageIds.Contains(i.Id))
            .Select(i => new ImageRow { Id = i.Id, ContentType = i.ContentType, ByteLength = i.ByteLength, Width = i.Width, Height = i.Height, Sha256 = i.Sha256 })
            .ToDictionaryAsync(i => i.Id, ct);
        var roots = messages.Where(m => m.Depth == 1).Select(m => m.Id).ToArray();
        var counts = await db.Messages.AsNoTracking().Where(m => roots.Contains(m.ThreadRootMessageId) && m.Depth > 1 && m.CreatedSequence <= head)
            .GroupBy(m => m.ThreadRootMessageId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var directCounts = await db.Messages.AsNoTracking().Where(m => m.ParentMessageId != null && ids.Contains(m.ParentMessageId) && m.CreatedSequence <= head)
            .GroupBy(m => m.ParentMessageId!).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var visibleAcks = db.Acknowledgements.AsNoTracking().Where(a => a.Sequence <= head);
        var summaries = await db.Messages.AsNoTracking().Where(m => ids.Contains(m.Id)).Select(m => new
        {
            m.Id,
            Count = visibleAcks.Count(a => a.MessageId == m.Id),
            LatestHandle = (from a in visibleAcks where a.MessageId == m.Id
                            join user in db.Users on a.UserId equals user.Id
                            orderby a.Sequence descending select user.Handle).FirstOrDefault()
        }).ToDictionaryAsync(m => m.Id, ct);
        var targetGroups = targets.GroupBy(t => t.MessageId).ToDictionary(g => g.Key, g => g.Select(x => x.UserId).Order(StringComparer.Ordinal).ToArray());
        return messages.Select(m =>
        {
            var dto = MessageDto(m, channels[m.SessionId], targetGroups.GetValueOrDefault(m.Id) ?? [],
                m.ImageId is null ? null : ImageDto(images[m.ImageId]), m.Depth == 1 ? counts.GetValueOrDefault(m.Id) : directCounts.GetValueOrDefault(m.Id));
            var summary = summaries[m.Id];
            dto["acknowledgementCount"] = summary.Count;
            dto["latestAcknowledgingHandle"] = summary.LatestHandle;
            return dto;
        }).ToList();
    }

    private static JsonObject UserDto(UserRow row) => new()
    {
        ["type"] = "user", ["id"] = row.Id, ["handle"] = row.Handle, ["displayName"] = row.DisplayName,
        ["createdAtUtc"] = CommandNormalizer.Utc(row.CreatedUtcMs)
    };
    private static JsonObject ChannelDto(ChannelRow row) => new()
    {
        ["type"] = "channel", ["id"] = row.Id, ["name"] = row.Name, ["description"] = row.Description,
        ["ownerUserId"] = row.OwnerUserId, ["createdAtUtc"] = CommandNormalizer.Utc(row.CreatedUtcMs),
        ["suggestedFilter"] = EmptyFilter(channelId: row.Id)
    };
    private static JsonObject SessionDto(SessionRow row) => new()
    {
        ["type"] = "session", ["id"] = row.Id, ["channelId"] = row.ChannelId, ["title"] = row.Title,
        ["ownerUserId"] = row.OwnerUserId, ["createdAtUtc"] = CommandNormalizer.Utc(row.CreatedUtcMs),
        ["suggestedFilter"] = EmptyFilter(sessionId: row.Id)
    };
    private static JsonObject ImageDto(ImageRow row) => new()
    {
        ["id"] = row.Id, ["contentType"] = row.ContentType, ["byteLength"] = row.ByteLength,
        ["width"] = row.Width, ["height"] = row.Height, ["sha256"] = row.Sha256, ["contentPath"] = "images/" + row.Id + "/content"
    };
    private static JsonObject MessageDto(MessageRow row, string channel, IReadOnlyCollection<string> targets, JsonObject? image, int? replyCount = null)
    {
        var dto = new JsonObject
        {
            ["type"] = "message", ["id"] = row.Id, ["channelId"] = channel, ["sessionId"] = row.SessionId,
            ["authorUserId"] = row.AuthorUserId,
            ["content"] = row.Kind == "text" ? new JsonObject { ["type"] = "text", ["text"] = row.Text }
                : new JsonObject { ["type"] = "image", ["caption"] = row.Text, ["image"] = image },
            ["parentMessageId"] = row.ParentMessageId, ["threadRootMessageId"] = row.ThreadRootMessageId,
            ["depth"] = row.Depth, ["targetUserIds"] = new JsonArray(targets.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["createdAtUtc"] = CommandNormalizer.Utc(row.CreatedUtcMs), ["sequence"] = row.CreatedSequence.ToString(CultureInfo.InvariantCulture)
        };
        if (replyCount.HasValue) dto["replyCount"] = replyCount.Value;
        return dto;
    }
    private JsonObject SubscriptionDto(SubscriptionRow row, long head) => new()
    {
        ["contractVersion"] = 1, ["id"] = row.Id, ["ownerUserId"] = row.OwnerUserId, ["state"] = row.State,
        ["filter"] = JsonNode.Parse(row.FilterJson), ["startCursor"] = Cursor(row.StartSequence), ["headCursor"] = Cursor(head),
        ["createdAtUtc"] = CommandNormalizer.Utc(row.CreatedUtcMs),
        ["closedAtUtc"] = row.ClosedUtcMs is { } closed ? CommandNormalizer.Utc(closed) : null,
        ["activeConnectionCount"] = 0,
        ["links"] = new JsonObject
        {
            ["self"] = "subscriptions/" + row.Id, ["events"] = "subscriptions/" + row.Id + "/events",
            ["stream"] = "subscriptions/" + row.Id + "/stream", ["close"] = "subscriptions/" + row.Id + "/close"
        }
    };
    private JsonObject SubscriptionReference(SubscriptionRow row) => new()
    {
        ["id"] = row.Id, ["startCursor"] = Cursor(row.StartSequence), ["queryPath"] = "subscriptions/" + row.Id,
        ["eventsPath"] = "subscriptions/" + row.Id + "/events", ["streamPath"] = "subscriptions/" + row.Id + "/stream"
    };
    private JsonObject EventDto(EventRow row) => new()
    {
        ["contractVersion"] = row.Version, ["eventType"] = row.EventType,
        ["sequence"] = row.Sequence.ToString(CultureInfo.InvariantCulture), ["cursor"] = Cursor(row.Sequence),
        ["requestId"] = row.RequestId, ["occurredAtUtc"] = CommandNormalizer.Utc(row.OccurredUtcMs),
        ["actorUserId"] = row.ActorUserId, ["channelId"] = row.ChannelId, ["sessionId"] = row.SessionId,
        ["dataObject"] = JsonNode.Parse(row.PayloadJson)
    };

    private static JsonObject DecodeToken(string token)
    {
        if (token.Length > 4096) throw BadToken();
        try
        {
            var base64 = token.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(base64)))?.AsObject() ?? throw BadToken();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException) { throw BadToken(); }
    }
    private static string EncodeToken(JsonObject node) => Convert.ToBase64String(Encoding.UTF8.GetBytes(node.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static ApiException BadToken() => new(400, "invalid_page_token", "The continuation token is invalid or does not match this query.");
    private sealed record PageState(long Head, long? Last, string Scope);
    private sealed record EventHeader(long Sequence, string EventType, string? ChannelId, string? SessionId, string Targets);
}
