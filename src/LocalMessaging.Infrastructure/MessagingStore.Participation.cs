using System.Text;
using System.Text.Json.Nodes;
using LocalMessaging.Contracts;
using Microsoft.EntityFrameworkCore;

namespace LocalMessaging.Infrastructure;

public sealed partial class MessagingStore
{
    private async Task<WriteResult> AcknowledgeAsync(MessagingDbContext db, JsonObject data, string userId, string requestId, long now, CancellationToken ct)
    {
        var message = await RequireMessageAsync(db, data["messageId"]!.GetValue<string>(), ct);
        var user = await RequireUserAsync(db, userId, ct);
        var prior = await db.Acknowledgements.AsNoTracking().SingleOrDefaultAsync(x => x.MessageId == message.Id && x.UserId == userId, ct);
        if (prior is not null)
            return PublicActionReceipt("acknowledgement", AcknowledgementDto(prior, user), await HeadAsync(db, ct));

        var session = await RequireSessionAsync(db, message.SessionId, ct);
        var row = new AcknowledgementRow { Id = NewId(), MessageId = message.Id, UserId = userId, AcknowledgedUtcMs = now };
        var value = AcknowledgementDto(row, user);
        var entry = Event("acknowledgement", row.Id, userId, session.ChannelId, session.Id, requestId, now);
        row.Sequence = await AddPublicEventAsync(db, entry, value, ct);
        db.Acknowledgements.Add(row);
        return PublicActionReceipt("acknowledgement", value, row.Sequence, row.Sequence);
    }

    private async Task<WriteResult> ChangeParticipationAsync(MessagingDbContext db, JsonObject data, string userId, string requestId, long now, CancellationToken ct)
    {
        var sessionId = data["sessionId"]?.GetValue<string>();
        var channelId = sessionId is not null ? (await RequireSessionAsync(db, sessionId, ct)).ChannelId
            : (await RequireChannelAsync(db, data["channelId"]!.GetValue<string>(), ct)).Id;
        var joined = data["joined"]!.GetValue<bool>();
        var user = await RequireUserAsync(db, userId, ct);
        var head = await HeadAsync(db, ct);
        var prior = await db.Participation.AsNoTracking().Where(x => x.ChannelId == channelId && x.SessionId == sessionId && x.UserId == userId)
            .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
        var otherScopeJoined = await ActiveParticipation(db, channelId, head)
            .AnyAsync(x => x.UserId == userId && x.SessionId != sessionId, ct);
        if ((prior?.Joined ?? false) == joined)
            return PublicActionReceipt("participation", ParticipationDto(prior?.Id, channelId, sessionId, user,
                joined, joined || otherScopeJoined, prior?.ChangedUtcMs), head);

        var row = new ParticipationRow
        {
            Id = NewId(), ChannelId = channelId, SessionId = sessionId, UserId = userId, Joined = joined, ChangedUtcMs = now
        };
        var value = ParticipationDto(row.Id, channelId, sessionId, user, joined, joined || otherScopeJoined, now);
        var entry = Event("participation", row.Id, userId, channelId, sessionId, requestId, now);
        entry.EventType = "participation.changed";
        row.Sequence = await AddPublicEventAsync(db, entry, value, ct);
        db.Participation.Add(row);
        return PublicActionReceipt("participation", value, row.Sequence, row.Sequence);
    }

    private async Task<long> AddPublicEventAsync(MessagingDbContext db, EventRow entry, JsonObject value, CancellationToken ct)
    {
        entry.PayloadJson = value.ToJsonString();
        db.EventLog.Add(entry);
        await db.SaveChangesAsync(ct);
        if (Encoding.UTF8.GetByteCount(EventDto(entry).ToJsonString()) > _limits.EventFrameBytes)
            throw new ApiException(413, "request_too_large", "The serialized event exceeds the configured stream frame limit.");
        return entry.Sequence;
    }

    private WriteResult PublicActionReceipt(string kind, JsonObject value, long head, long? sequence = null) => new(new JsonObject
    {
        ["dataObjects"] = new JsonObject { [kind] = value },
        ["created"] = sequence is null ? new JsonArray() : new JsonArray(new JsonObject { ["type"] = kind, ["id"] = value["id"]!.DeepClone() }),
        ["eventRange"] = sequence is null ? null : new JsonObject { ["firstSequence"] = sequence.ToString(), ["lastSequence"] = sequence.ToString() },
        ["afterRequestCursor"] = Cursor(head),
        ["subscription"] = null
    }, sequence, sequence);

    private async Task<JsonObject> AcknowledgementsAsync(MessagingDbContext db, string messageId, int limit, PageState page, CancellationToken ct)
    {
        await RequireMessageAsync(db, messageId, ct);
        var after = page.Last ?? 0;
        var fetched = await (from row in db.Acknowledgements.AsNoTracking()
                             where row.MessageId == messageId && row.Sequence > after && row.Sequence <= page.Head
                             join user in db.Users.AsNoTracking() on row.UserId equals user.Id
                             orderby row.Sequence
                             select new { Row = row, User = user }).Take(limit + 1).ToListAsync(ct);
        return Page(fetched, limit, page, x => x.Row.Sequence, x => AcknowledgementDto(x.Row, x.User));
    }

    private static IQueryable<ParticipationRow> ActiveParticipation(MessagingDbContext db, string channelId, long head) =>
        db.Participation.AsNoTracking().Where(row => row.ChannelId == channelId && row.Joined && row.Sequence <= head
            && !db.Participation.Any(next => next.ChannelId == row.ChannelId && next.SessionId == row.SessionId
                && next.UserId == row.UserId && next.Sequence > row.Sequence && next.Sequence <= head));

    private async Task<JsonObject> ParticipantsAsync(MessagingDbContext db, string channelId, string? sessionId, int limit, PageState page, CancellationToken ct)
    {
        var active = ActiveParticipation(db, channelId, page.Head);
        if (sessionId is not null) active = active.Where(x => x.SessionId == sessionId);
        var after = page.Last ?? 0;
        var profiles = active.GroupBy(x => x.UserId).Select(group => new { UserId = group.Key, Sequence = group.Max(x => x.Sequence) });
        var fetched = await (from row in profiles
                             where row.Sequence > after
                             join user in db.Users.AsNoTracking() on row.UserId equals user.Id
                             orderby row.Sequence
                             select new { row.Sequence, User = user }).Take(limit + 1).ToListAsync(ct);
        return Page(fetched, limit, page, x => x.Sequence, x => new JsonObject { ["profile"] = PublicProfileDto(x.User) });
    }

    private static JsonObject PublicProfileDto(UserRow user) => new()
    {
        ["id"] = user.Id, ["handle"] = user.Handle, ["displayName"] = user.DisplayName
    };

    private static JsonObject AcknowledgementDto(AcknowledgementRow row, UserRow user) => new()
    {
        ["id"] = row.Id, ["messageId"] = row.MessageId, ["profile"] = PublicProfileDto(user), ["acknowledgedUtcMs"] = row.AcknowledgedUtcMs
    };

    private static JsonObject ParticipationDto(string? id, string channelId, string? sessionId, UserRow user, bool joined, bool channelJoined, long? now) => new()
    {
        ["id"] = id, ["channelId"] = channelId, ["sessionId"] = sessionId, ["profile"] = PublicProfileDto(user),
        ["joined"] = joined, ["channelJoined"] = channelJoined, ["changedUtcMs"] = now
    };
}
