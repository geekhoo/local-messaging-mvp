using System.Text.Json.Nodes;
namespace LocalMessaging.Contracts;

public sealed record PublicProfile(Guid Id, string Handle, string DisplayName);
public sealed record ProfileAcknowledgement(Guid Id, Guid MessageId, PublicProfile Profile, long AcknowledgedUtcMs);
public sealed record Participant(PublicProfile Profile);
public sealed record ParticipationChange(Guid? Id, Guid ChannelId, Guid? SessionId, PublicProfile Profile,
    bool Joined, bool ChannelJoined, long? ChangedUtcMs);
public sealed record UserDto(Guid Id, string Handle, string DisplayName, DateTime CreatedAtUtc);
public sealed record ChannelDto(Guid Id, string Name, string? Description, Guid OwnerUserId, DateTime CreatedAtUtc, SubscriptionFilter SuggestedFilter);
public sealed record SessionDto(Guid Id, Guid ChannelId, string Title, Guid OwnerUserId, DateTime CreatedAtUtc, SubscriptionFilter SuggestedFilter);
public sealed record ImageDescriptor(Guid Id, string ContentType, int ByteLength, int Width, int Height, string Sha256, string ContentPath);
public sealed record MessageContent(string Type, string? Text, string? Caption, ImageDescriptor? Image);
public sealed record MessageDto(Guid Id, Guid ChannelId, Guid SessionId, Guid AuthorUserId,
    MessageContent Content, Guid? ParentMessageId, Guid ThreadRootMessageId, int Depth,
    Guid[] TargetUserIds, DateTime CreatedAtUtc, string Sequence, int? ReplyCount = null,
    int? AcknowledgementCount = null, string? LatestAcknowledgingHandle = null);
public sealed record ObjectPage<T>(IReadOnlyList<T> Items, string SnapshotCursor, string? NextPageToken);
public sealed record ThreadPage(MessageDto Root, IReadOnlyList<MessageDto> Items, IReadOnlyList<MessageDto> ContextMessages, string SnapshotCursor, string? NextPageToken);
public sealed record EventPage(int ContractVersion, Guid SubscriptionId, JsonObject[] Items, string NextCursor, string HeadCursor, bool HasMore);
public sealed record SubscriptionDto(int ContractVersion, Guid Id, Guid OwnerUserId, string State,
    SubscriptionFilter Filter, string StartCursor, string HeadCursor, DateTime CreatedAtUtc,
    int ActiveConnectionCount, Dictionary<string,string> Links);
public sealed record EndpointDescriptor(Guid InstanceId, int ProcessId, Guid StoreEpoch,
    string MessagingBaseUrl, string FrontendBaseUrl, string CertificateSha256, int ContractVersion, bool Ready);
