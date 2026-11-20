using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalMessaging.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppMetadata",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    StoreEpoch = table.Column<string>(type: "TEXT", nullable: false),
                    InitializedUtcMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppMetadata", x => x.Id);
                    table.CheckConstraint("CK_AppMetadata_Singleton", "Id = 1");
                });

            migrationBuilder.CreateTable(
                name: "EventLog",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    ObjectType = table.Column<string>(type: "TEXT", nullable: false),
                    ObjectId = table.Column<string>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<string>(type: "TEXT", nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    TargetUserIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredUtcMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventLog", x => x.Sequence);
                });

            migrationBuilder.CreateTable(
                name: "ImageAssets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Bytes = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false),
                    ByteLength = table.Column<long>(type: "INTEGER", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtcMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageAssets", x => x.Id);
                    table.CheckConstraint("CK_ImageAssets_Bounds", "ByteLength > 0 AND ByteLength <= 8388608 AND ByteLength = length(Bytes) AND Width > 0 AND Height > 0 AND Width <= 8192 AND Height <= 8192 AND CAST(Width AS INTEGER) * Height <= 20000000");
                    table.CheckConstraint("CK_ImageAssets_ContentType", "ContentType IN ('image/png','image/jpeg')");
                    table.CheckConstraint("CK_ImageAssets_Digest", "length(Sha256) = 64");
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Handle = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtcMs = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedSequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.CheckConstraint("CK_Users_Handle", "length(Handle) BETWEEN 3 AND 32 AND Handle = lower(Handle) AND Handle NOT GLOB '*[^a-z0-9_]*' AND substr(Handle,1,1) GLOB '[a-z]'");
                    table.CheckConstraint("CK_Users_Name", "length(trim(DisplayName)) BETWEEN 1 AND 80");
                    table.CheckConstraint("CK_Users_Sequence", "CreatedSequence > 0");
                });

            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    OwnerUserId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtcMs = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedSequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                    table.CheckConstraint("CK_Channels_Description", "Description IS NULL OR length(Description) <= 1000");
                    table.CheckConstraint("CK_Channels_Name", "length(trim(Name)) BETWEEN 1 AND 120");
                    table.CheckConstraint("CK_Channels_Sequence", "CreatedSequence > 0");
                    table.ForeignKey(
                        name: "FK_Channels_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RequestReceipts",
                columns: table => new
                {
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    Operation = table.Column<string>(type: "TEXT", nullable: false),
                    CallerUserId = table.Column<string>(type: "TEXT", nullable: true),
                    RequestHash = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSequence = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSequence = table.Column<long>(type: "INTEGER", nullable: true),
                    CommittedUtcMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestReceipts", x => x.RequestId);
                    table.ForeignKey(
                        name: "FK_RequestReceipts_Users_CallerUserId",
                        column: x => x.CallerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Subscriptions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", nullable: false),
                    FilterJson = table.Column<string>(type: "TEXT", nullable: false),
                    StartSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtcMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ClosedUtcMs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions", x => x.Id);
                    table.CheckConstraint("CK_Subscriptions_Start", "StartSequence >= 0");
                    table.CheckConstraint("CK_Subscriptions_State", "State IN ('active','closed')");
                    table.ForeignKey(
                        name: "FK_Subscriptions_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtcMs = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedSequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.CheckConstraint("CK_Sessions_Sequence", "CreatedSequence > 0");
                    table.CheckConstraint("CK_Sessions_Title", "length(trim(Title)) BETWEEN 1 AND 120");
                    table.ForeignKey(
                        name: "FK_Sessions_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Sessions_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorUserId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: true),
                    ImageId = table.Column<string>(type: "TEXT", nullable: true),
                    ParentMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    ThreadRootMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    Depth = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtcMs = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedSequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                    table.UniqueConstraint("AK_Messages_Id_SessionId", x => new { x.Id, x.SessionId });
                    table.CheckConstraint("CK_Messages_Content", "(Kind = 'text' AND Text IS NOT NULL AND length(trim(Text)) > 0 AND length(Text) <= 16384 AND ImageId IS NULL) OR (Kind = 'image' AND ImageId IS NOT NULL AND (Text IS NULL OR length(Text) <= 4096))");
                    table.CheckConstraint("CK_Messages_Depth", "Depth BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_Messages_Root", "(Depth = 1 AND ParentMessageId IS NULL AND ThreadRootMessageId = Id) OR (Depth > 1 AND ParentMessageId IS NOT NULL AND ThreadRootMessageId <> Id)");
                    table.CheckConstraint("CK_Messages_Sequence", "CreatedSequence > 0");
                    table.ForeignKey(
                        name: "FK_Messages_ImageAssets_ImageId",
                        column: x => x.ImageId,
                        principalTable: "ImageAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Messages_Messages_ParentMessageId_SessionId",
                        columns: x => new { x.ParentMessageId, x.SessionId },
                        principalTable: "Messages",
                        principalColumns: new[] { "Id", "SessionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Messages_Messages_ThreadRootMessageId_SessionId",
                        columns: x => new { x.ThreadRootMessageId, x.SessionId },
                        principalTable: "Messages",
                        principalColumns: new[] { "Id", "SessionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Messages_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Messages_Users_AuthorUserId",
                        column: x => x.AuthorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MessageTargets",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageTargets", x => new { x.MessageId, x.UserId });
                    table.ForeignKey(
                        name: "FK_MessageTargets_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessageTargets_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Channels_CreatedSequence",
                table: "Channels",
                column: "CreatedSequence");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_OwnerUserId",
                table: "Channels",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_EventLog_ChannelId_Sequence",
                table: "EventLog",
                columns: new[] { "ChannelId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_EventLog_EventType_Sequence",
                table: "EventLog",
                columns: new[] { "EventType", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_EventLog_SessionId_Sequence",
                table: "EventLog",
                columns: new[] { "SessionId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_AuthorUserId",
                table: "Messages",
                column: "AuthorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ImageId",
                table: "Messages",
                column: "ImageId",
                unique: true,
                filter: "ImageId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ParentMessageId",
                table: "Messages",
                column: "ParentMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ParentMessageId_SessionId",
                table: "Messages",
                columns: new[] { "ParentMessageId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_SessionId_CreatedSequence",
                table: "Messages",
                columns: new[] { "SessionId", "CreatedSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ThreadRootMessageId_CreatedSequence",
                table: "Messages",
                columns: new[] { "ThreadRootMessageId", "CreatedSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ThreadRootMessageId_SessionId",
                table: "Messages",
                columns: new[] { "ThreadRootMessageId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageTargets_UserId_MessageId",
                table: "MessageTargets",
                columns: new[] { "UserId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_RequestReceipts_CallerUserId",
                table: "RequestReceipts",
                column: "CallerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_ChannelId_CreatedSequence",
                table: "Sessions",
                columns: new[] { "ChannelId", "CreatedSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_OwnerUserId",
                table: "Sessions",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_OwnerUserId_State_CreatedUtcMs",
                table: "Subscriptions",
                columns: new[] { "OwnerUserId", "State", "CreatedUtcMs" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_CreatedSequence",
                table: "Users",
                column: "CreatedSequence");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Handle",
                table: "Users",
                column: "Handle",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppMetadata");

            migrationBuilder.DropTable(
                name: "EventLog");

            migrationBuilder.DropTable(
                name: "MessageTargets");

            migrationBuilder.DropTable(
                name: "RequestReceipts");

            migrationBuilder.DropTable(
                name: "Subscriptions");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "ImageAssets");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "Channels");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
