using System.Text.Json.Serialization;
namespace LocalMessaging.Contracts;

public sealed record CreateRequest
{
    public required int ContractVersion { get; init; }
    public required Guid RequestId { get; init; }
    public Guid? CallerUserId { get; init; }
    public required CreateDataObject DataObject { get; init; }
    public CoupledSubscription? Subscribe { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CreateUser), "user")]
[JsonDerivedType(typeof(CreateChannel), "channel")]
[JsonDerivedType(typeof(CreateSession), "session")]
[JsonDerivedType(typeof(CreateMessage), "message")]
[JsonDerivedType(typeof(CreateAcknowledgement), "acknowledgement")]
[JsonDerivedType(typeof(CreateParticipation), "participation")]
public abstract record CreateDataObject;
public sealed record CreateUser : CreateDataObject
{
    public required string Handle { get; init; }
    public required string DisplayName { get; init; }
}
public sealed record CreateChannel : CreateDataObject
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? InitialSessionTitle { get; init; }
}
public sealed record CreateSession : CreateDataObject
{
    public required ChannelSelector Channel { get; init; }
    public required string Title { get; init; }
}
public sealed record CreateMessage : CreateDataObject
{
    public required ChannelSelector Channel { get; init; }
    public required SessionSelector Session { get; init; }
    public required MessageInput Content { get; init; }
    public Guid? ParentMessageId { get; init; }
    public Guid[]? TargetUserIds { get; init; }
}
public sealed record CreateAcknowledgement : CreateDataObject
{
    public required Guid MessageId { get; init; }
}
public sealed record CreateParticipation : CreateDataObject
{
    public Guid? ChannelId { get; init; }
    public Guid? SessionId { get; init; }
    public required bool Joined { get; init; }
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "mode")]
[JsonDerivedType(typeof(ExistingChannel), "existing")]
[JsonDerivedType(typeof(NewChannel), "new")]
public abstract record ChannelSelector;
public sealed record ExistingChannel : ChannelSelector { public required Guid Id { get; init; } }
public sealed record NewChannel : ChannelSelector
{
    public required string Name { get; init; }
    public string? Description { get; init; }
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "mode")]
[JsonDerivedType(typeof(ExistingSession), "existing")]
[JsonDerivedType(typeof(NewSession), "new")]
public abstract record SessionSelector;
public sealed record ExistingSession : SessionSelector { public required Guid Id { get; init; } }
public sealed record NewSession : SessionSelector { public required string Title { get; init; } }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextInput), "text")]
[JsonDerivedType(typeof(ImageInput), "image")]
public abstract record MessageInput;
public sealed record TextInput : MessageInput { public required string Text { get; init; } }
public sealed record ImageInput : MessageInput { public string? Caption { get; init; } }
public sealed record CoupledSubscription
{
    public required string Scope { get; init; }
    public required string Start { get; init; }
}

public sealed record CreateSubscriptionRequest
{
    public required int ContractVersion { get; init; }
    public required Guid RequestId { get; init; }
    public required Guid CallerUserId { get; init; }
    public required SubscriptionFilter Filter { get; init; }
    public required SubscriptionStart Start { get; init; }
}
public sealed record SubscriptionFilter
{
    public bool Catalog { get; init; }
    public bool AllMessages { get; init; }
    public bool? AllAcknowledgements { get; init; }
    public Guid[]? ChannelIds { get; init; }
    public Guid[]? SessionIds { get; init; }
    public Guid? TargetUserId { get; init; }
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "mode")]
[JsonDerivedType(typeof(StartNow), "now")]
[JsonDerivedType(typeof(StartBeginning), "beginning")]
[JsonDerivedType(typeof(StartAfter), "after")]
public abstract record SubscriptionStart;
public sealed record StartNow : SubscriptionStart;
public sealed record StartBeginning : SubscriptionStart;
public sealed record StartAfter : SubscriptionStart { public required string Cursor { get; init; } }
public sealed record CloseSubscriptionRequest
{
    public required int ContractVersion { get; init; }
    public required Guid RequestId { get; init; }
    public required Guid CallerUserId { get; init; }
}
