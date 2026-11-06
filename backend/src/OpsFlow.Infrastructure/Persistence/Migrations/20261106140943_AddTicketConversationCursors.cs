using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpsFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketConversationCursors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChatReadBaselineSequence",
                table: "Tickets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Initialize existing tickets without marking historical messages unread.
            migrationBuilder.Sql("""
                UPDATE "Tickets"
                SET "ChatReadBaselineSequence" = "MessageSequence";
                """);

            migrationBuilder.CreateTable(
                name: "TicketConversationCursors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveredSequence = table.Column<int>(type: "integer", nullable: false),
                    ReadSequence = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketConversationCursors", x => x.Id);
                    table.CheckConstraint("CK_TicketConversationCursors_Delivered", "\"DeliveredSequence\" >= \"ReadSequence\"");
                    table.CheckConstraint("CK_TicketConversationCursors_Read", "\"ReadSequence\" >= 0");
                    table.ForeignKey(
                        name: "FK_TicketConversationCursors_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketConversationCursors_Tickets_TenantId_TicketId",
                        columns: x => new { x.TenantId, x.TicketId },
                        principalTable: "Tickets",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketConversationCursors_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketConversationCursors_TenantId_UserId",
                table: "TicketConversationCursors",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketConversationCursors_Ticket_User",
                table: "TicketConversationCursors",
                columns: new[] { "TenantId", "TicketId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketConversationCursors_UserId",
                table: "TicketConversationCursors",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketConversationCursors");

            migrationBuilder.DropColumn(
                name: "ChatReadBaselineSequence",
                table: "Tickets");
        }
    }
}
