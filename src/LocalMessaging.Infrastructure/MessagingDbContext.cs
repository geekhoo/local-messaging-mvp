using Microsoft.EntityFrameworkCore;

namespace LocalMessaging.Infrastructure;

public sealed class MessagingDbContext(DbContextOptions<MessagingDbContext> options) : DbContext(options)
{
    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<ChannelRow> Channels => Set<ChannelRow>();
    public DbSet<SessionRow> Sessions => Set<SessionRow>();
    public DbSet<MessageRow> Messages => Set<MessageRow>();
    public DbSet<MessageTargetRow> MessageTargets => Set<MessageTargetRow>();
    public DbSet<ImageRow> ImageAssets => Set<ImageRow>();
    public DbSet<SubscriptionRow> Subscriptions => Set<SubscriptionRow>();
    public DbSet<ReceiptRow> RequestReceipts => Set<ReceiptRow>();
    public DbSet<AcknowledgementRow> Acknowledgements => Set<AcknowledgementRow>();
    public DbSet<ParticipationRow> Participation => Set<ParticipationRow>();
    public DbSet<EventRow> EventLog => Set<EventRow>();
    public DbSet<MetadataRow> AppMetadata => Set<MetadataRow>();

    protected override void OnModelCreating(ModelBuilder model) => ConfigureModel(model);

    public static void ConfigureModel(ModelBuilder model)
    {
        var user = model.Entity<UserRow>();
        user.ToTable("Users", t =>
        {
            t.HasCheckConstraint("CK_Users_Handle", "length(Handle) BETWEEN 3 AND 32 AND Handle = lower(Handle) AND Handle NOT GLOB '*[^a-z0-9_]*' AND substr(Handle,1,1) GLOB '[a-z]'");
            t.HasCheckConstraint("CK_Users_Name", "length(trim(DisplayName)) BETWEEN 1 AND 80");
            t.HasCheckConstraint("CK_Users_Sequence", "CreatedSequence > 0");
        });
        user.HasKey(x => x.Id);
        user.Property(x => x.Id).ValueGeneratedNever();
        user.Property(x => x.Handle).IsRequired();
        user.Property(x => x.DisplayName).IsRequired();
        user.HasIndex(x => x.Handle).IsUnique();
        user.HasIndex(x => x.CreatedSequence);

        var channel = model.Entity<ChannelRow>();
        channel.ToTable("Channels", t =>
        {
            t.HasCheckConstraint("CK_Channels_Name", "length(trim(Name)) BETWEEN 1 AND 120");
            t.HasCheckConstraint("CK_Channels_Description", "Description IS NULL OR length(Description) <= 1000");
            t.HasCheckConstraint("CK_Channels_Sequence", "CreatedSequence > 0");
        });
        channel.HasKey(x => x.Id);
        channel.Property(x => x.Id).ValueGeneratedNever();
        channel.Property(x => x.Name).IsRequired();
        channel.HasOne<UserRow>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        channel.HasIndex(x => x.CreatedSequence);

        var session = model.Entity<SessionRow>();
        session.ToTable("Sessions", t =>
        {
            t.HasCheckConstraint("CK_Sessions_Title", "length(trim(Title)) BETWEEN 1 AND 120");
            t.HasCheckConstraint("CK_Sessions_Sequence", "CreatedSequence > 0");
        });
        session.HasKey(x => x.Id);
        session.Property(x => x.Id).ValueGeneratedNever();
        session.Property(x => x.Title).IsRequired();
        session.HasOne<ChannelRow>().WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Restrict);
        session.HasOne<UserRow>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        session.HasIndex(x => new { x.ChannelId, x.CreatedSequence });

        var image = model.Entity<ImageRow>();
        image.ToTable("ImageAssets", t =>
        {
            t.HasCheckConstraint("CK_ImageAssets_ContentType", "ContentType IN ('image/png','image/jpeg')");
            t.HasCheckConstraint("CK_ImageAssets_Bounds", "ByteLength > 0 AND ByteLength <= 8388608 AND ByteLength = length(Bytes) AND Width > 0 AND Height > 0 AND Width <= 8192 AND Height <= 8192 AND CAST(Width AS INTEGER) * Height <= 20000000");
            t.HasCheckConstraint("CK_ImageAssets_Digest", "length(Sha256) = 64");
        });
        image.HasKey(x => x.Id);
        image.Property(x => x.Id).ValueGeneratedNever();
        image.Property(x => x.Bytes).IsRequired();
        image.Property(x => x.ContentType).IsRequired();
        image.Property(x => x.Sha256).IsRequired();

        var message = model.Entity<MessageRow>();
        message.ToTable("Messages", t =>
        {
            t.HasCheckConstraint("CK_Messages_Depth", "Depth BETWEEN 1 AND 3");
            t.HasCheckConstraint("CK_Messages_Root", "(Depth = 1 AND ParentMessageId IS NULL AND ThreadRootMessageId = Id) OR (Depth > 1 AND ParentMessageId IS NOT NULL AND ThreadRootMessageId <> Id)");
            t.HasCheckConstraint("CK_Messages_Content", "(Kind = 'text' AND Text IS NOT NULL AND length(trim(Text)) > 0 AND length(Text) <= 16384 AND ImageId IS NULL) OR (Kind = 'image' AND ImageId IS NOT NULL AND (Text IS NULL OR length(Text) <= 4096))");
            t.HasCheckConstraint("CK_Messages_Sequence", "CreatedSequence > 0");
        });
        message.HasKey(x => x.Id);
        message.Property(x => x.Id).ValueGeneratedNever();
        message.Property(x => x.Kind).IsRequired();
        message.HasAlternateKey(x => new { x.Id, x.SessionId });
        message.HasOne<SessionRow>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        message.HasOne<UserRow>().WithMany().HasForeignKey(x => x.AuthorUserId).OnDelete(DeleteBehavior.Restrict);
        message.HasOne<ImageRow>().WithMany().HasForeignKey(x => x.ImageId).OnDelete(DeleteBehavior.Restrict);
        message.HasOne<MessageRow>().WithMany().HasForeignKey(x => new { x.ParentMessageId, x.SessionId }).HasPrincipalKey(x => new { x.Id, x.SessionId }).OnDelete(DeleteBehavior.Restrict);
        message.HasOne<MessageRow>().WithMany().HasForeignKey(x => new { x.ThreadRootMessageId, x.SessionId }).HasPrincipalKey(x => new { x.Id, x.SessionId }).OnDelete(DeleteBehavior.Restrict);
        message.HasIndex(x => x.ImageId).IsUnique().HasFilter("ImageId IS NOT NULL");
        message.HasIndex(x => new { x.SessionId, x.CreatedSequence });
        message.HasIndex(x => new { x.ThreadRootMessageId, x.CreatedSequence });
        message.HasIndex(x => x.ParentMessageId);

        var target = model.Entity<MessageTargetRow>();
        target.ToTable("MessageTargets");
        target.HasKey(x => new { x.MessageId, x.UserId });
        target.HasOne<MessageRow>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        target.HasOne<UserRow>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        target.HasIndex(x => new { x.UserId, x.MessageId });

        var subscription = model.Entity<SubscriptionRow>();
        subscription.ToTable("Subscriptions", t =>
        {
            t.HasCheckConstraint("CK_Subscriptions_Start", "StartSequence >= 0");
            t.HasCheckConstraint("CK_Subscriptions_State", "State IN ('active','closed')");
        });
        subscription.HasKey(x => x.Id);
        subscription.Property(x => x.Id).ValueGeneratedNever();
        subscription.Property(x => x.FilterJson).IsRequired();
        subscription.Property(x => x.State).IsRequired();
        subscription.HasOne<UserRow>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        subscription.HasIndex(x => new { x.OwnerUserId, x.State, x.CreatedUtcMs });

        var receipt = model.Entity<ReceiptRow>();
        receipt.ToTable("RequestReceipts");
        receipt.HasKey(x => x.RequestId);
        receipt.Property(x => x.RequestId).ValueGeneratedNever();
        receipt.Property(x => x.Operation).IsRequired();
        receipt.Property(x => x.RequestHash).IsRequired();
        receipt.Property(x => x.ResultJson).IsRequired();
        receipt.HasOne<UserRow>().WithMany().HasForeignKey(x => x.CallerUserId).OnDelete(DeleteBehavior.Restrict);

        var eventRow = model.Entity<EventRow>();
        eventRow.ToTable("EventLog");
        eventRow.HasKey(x => x.Sequence);
        eventRow.Property(x => x.Sequence).ValueGeneratedOnAdd().HasAnnotation("Sqlite:Autoincrement", true);
        eventRow.Property(x => x.EventType).IsRequired();
        eventRow.Property(x => x.RequestId).IsRequired();
        eventRow.Property(x => x.ObjectType).IsRequired();
        eventRow.Property(x => x.ObjectId).IsRequired();
        eventRow.Property(x => x.ActorUserId).IsRequired();
        eventRow.Property(x => x.PayloadJson).IsRequired();
        eventRow.Property(x => x.TargetUserIdsJson).IsRequired();
        eventRow.HasIndex(x => new { x.ChannelId, x.Sequence });
        eventRow.HasIndex(x => new { x.SessionId, x.Sequence });
        eventRow.HasIndex(x => new { x.EventType, x.Sequence });

        var acknowledgement = model.Entity<AcknowledgementRow>();
        acknowledgement.ToTable("Acknowledgements");
        acknowledgement.HasKey(x => new { x.MessageId, x.UserId });
        acknowledgement.HasIndex(x => x.Id).IsUnique();
        acknowledgement.HasIndex(x => new { x.MessageId, x.Sequence });
        acknowledgement.HasOne<MessageRow>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        acknowledgement.HasOne<UserRow>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        acknowledgement.HasOne<EventRow>().WithMany().HasForeignKey(x => x.Sequence).OnDelete(DeleteBehavior.Restrict);

        var participation = model.Entity<ParticipationRow>();
        participation.ToTable("Participation");
        participation.HasKey(x => x.Sequence);
        participation.Property(x => x.Sequence).ValueGeneratedNever();
        participation.HasIndex(x => x.Id).IsUnique();
        participation.HasIndex(x => new { x.ChannelId, x.UserId, x.SessionId, x.Sequence });
        participation.HasIndex(x => new { x.SessionId, x.UserId, x.Sequence });
        session.HasAlternateKey(x => new { x.Id, x.ChannelId });
        participation.HasOne<ChannelRow>().WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Restrict);
        participation.HasOne<SessionRow>().WithMany().HasForeignKey(x => new { x.SessionId, x.ChannelId }).HasPrincipalKey(x => new { x.Id, x.ChannelId }).OnDelete(DeleteBehavior.Restrict);
        participation.HasOne<UserRow>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        participation.HasOne<EventRow>().WithMany().HasForeignKey(x => x.Sequence).OnDelete(DeleteBehavior.Restrict);

        var metadata = model.Entity<MetadataRow>();
        metadata.ToTable("AppMetadata", t => t.HasCheckConstraint("CK_AppMetadata_Singleton", "Id = 1"));
        metadata.HasKey(x => x.Id);
        metadata.Property(x => x.Id).ValueGeneratedNever();
        metadata.Property(x => x.StoreEpoch).IsRequired();
    }
}

public sealed class UserRow
{
    public string Id { get; set; } = "";
    public string Handle { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long CreatedUtcMs { get; set; }
    public long CreatedSequence { get; set; }
}
public sealed class ChannelRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string OwnerUserId { get; set; } = "";
    public long CreatedUtcMs { get; set; }
    public long CreatedSequence { get; set; }
}
public sealed class SessionRow
{
    public string Id { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string Title { get; set; } = "";
    public string OwnerUserId { get; set; } = "";
    public long CreatedUtcMs { get; set; }
    public long CreatedSequence { get; set; }
}
public sealed class MessageRow
{
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string AuthorUserId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Text { get; set; }
    public string? ImageId { get; set; }
    public string? ParentMessageId { get; set; }
    public string ThreadRootMessageId { get; set; } = "";
    public int Depth { get; set; }
    public long CreatedUtcMs { get; set; }
    public long CreatedSequence { get; set; }
}
public sealed class MessageTargetRow
{
    public string MessageId { get; set; } = "";
    public string UserId { get; set; } = "";
}
public sealed class ImageRow
{
    public string Id { get; set; } = "";
    public byte[] Bytes { get; set; } = [];
    public string ContentType { get; set; } = "";
    public long ByteLength { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Sha256 { get; set; } = "";
    public long CreatedUtcMs { get; set; }
}
public sealed class SubscriptionRow
{
    public string Id { get; set; } = "";
    public string OwnerUserId { get; set; } = "";
    public string FilterJson { get; set; } = "";
    public long StartSequence { get; set; }
    public string State { get; set; } = "active";
    public long CreatedUtcMs { get; set; }
    public long? ClosedUtcMs { get; set; }
}
public sealed class ReceiptRow
{
    public string RequestId { get; set; } = "";
    public string Operation { get; set; } = "";
    public string? CallerUserId { get; set; }
    public string RequestHash { get; set; } = "";
    public string ResultJson { get; set; } = "";
    public long? FirstSequence { get; set; }
    public long? LastSequence { get; set; }
    public long CommittedUtcMs { get; set; }
}
public sealed class AcknowledgementRow
{
    public string Id { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string UserId { get; set; } = "";
    public long AcknowledgedUtcMs { get; set; }
    public long Sequence { get; set; }
}
public sealed class ParticipationRow
{
    public string Id { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string? SessionId { get; set; }
    public string UserId { get; set; } = "";
    public bool Joined { get; set; }
    public long ChangedUtcMs { get; set; }
    public long Sequence { get; set; }
}
public sealed class EventRow
{
    public long Sequence { get; set; }
    public int Version { get; set; } = 1;
    public string EventType { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string ObjectType { get; set; } = "";
    public string ObjectId { get; set; } = "";
    public string ActorUserId { get; set; } = "";
    public string? ChannelId { get; set; }
    public string? SessionId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string TargetUserIdsJson { get; set; } = "[]";
    public long OccurredUtcMs { get; set; }
}
public sealed class MetadataRow
{
    public int Id { get; set; } = 1;
    public string StoreEpoch { get; set; } = "";
    public long InitializedUtcMs { get; set; }
}
