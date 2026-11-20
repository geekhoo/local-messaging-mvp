using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalMessaging.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PublicProfileActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Sessions_Id_ChannelId",
                table: "Sessions",
                columns: new[] { "Id", "ChannelId" });

            migrationBuilder.CreateTable(
                name: "Acknowledgements",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Id = table.Column<string>(type: "TEXT", nullable: true),
                    AcknowledgedUtcMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Acknowledgements", x => new { x.MessageId, x.UserId });
                    table.ForeignKey(
                        name: "FK_Acknowledgements_EventLog_Sequence",
                        column: x => x.Sequence,
                        principalTable: "EventLog",
                        principalColumn: "Sequence",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Acknowledgements_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Acknowledgements_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Participation",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Id = table.Column<string>(type: "TEXT", nullable: true),
                    ChannelId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Joined = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChangedUtcMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Participation", x => x.Sequence);
                    table.ForeignKey(
                        name: "FK_Participation_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Participation_EventLog_Sequence",
                        column: x => x.Sequence,
                        principalTable: "EventLog",
                        principalColumn: "Sequence",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Participation_Sessions_SessionId_ChannelId",
                        columns: x => new { x.SessionId, x.ChannelId },
                        principalTable: "Sessions",
                        principalColumns: new[] { "Id", "ChannelId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Participation_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Acknowledgements_Id",
                table: "Acknowledgements",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Acknowledgements_MessageId_Sequence",
                table: "Acknowledgements",
                columns: new[] { "MessageId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Acknowledgements_Sequence",
                table: "Acknowledgements",
                column: "Sequence");

            migrationBuilder.CreateIndex(
                name: "IX_Acknowledgements_UserId",
                table: "Acknowledgements",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Participation_ChannelId_UserId_SessionId_Sequence",
                table: "Participation",
                columns: new[] { "ChannelId", "UserId", "SessionId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Participation_Id",
                table: "Participation",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Participation_SessionId_ChannelId",
                table: "Participation",
                columns: new[] { "SessionId", "ChannelId" });

            migrationBuilder.CreateIndex(
                name: "IX_Participation_SessionId_UserId_Sequence",
                table: "Participation",
                columns: new[] { "SessionId", "UserId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Participation_UserId",
                table: "Participation",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Acknowledgements");

            migrationBuilder.DropTable(
                name: "Participation");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Sessions_Id_ChannelId",
                table: "Sessions");
        }
    }
}
