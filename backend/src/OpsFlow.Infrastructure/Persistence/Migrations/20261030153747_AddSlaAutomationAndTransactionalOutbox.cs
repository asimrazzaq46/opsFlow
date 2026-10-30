using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpsFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSlaAutomationAndTransactionalOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ResolutionBreachedAtUtc",
                table: "TicketSlaCycles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolutionWarnedAtUtc",
                table: "TicketSlaCycles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResponseBreachedAtUtc",
                table: "TicketSlaCycles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResponseWarnedAtUtc",
                table: "TicketSlaCycles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    EventVersion = table.Column<int>(type: "integer", nullable: false),
                    AggregateId = table.Column<Guid>(type: "uuid", nullable: true),
                    Payload = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    DedupKey = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RequiredChannels = table.Column<int>(type: "integer", nullable: false),
                    CompletedChannels = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClaimedUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutboxMessages_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketSlaCycles_ResolutionMonitor",
                table: "TicketSlaCycles",
                column: "ResolutionDueAtUtc",
                filter: "\"ResolutionBreachedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TicketSlaCycles_ResponseMonitor",
                table: "TicketSlaCycles",
                column: "ResponseDueAtUtc",
                filter: "\"ResponseBreachedAtUtc\" IS NULL AND \"ResponseDueAtUtc\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_CreatedAtUtc",
                table: "OutboxMessages",
                column: "CreatedAtUtc",
                filter: "\"Status\" = 'DeadLetter'");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_NextAttemptAtUtc_ClaimedUntilUtc",
                table: "OutboxMessages",
                columns: new[] { "NextAttemptAtUtc", "ClaimedUntilUtc" },
                filter: "\"Status\" IN ('Pending','RetryScheduled','Claimed')");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessedAtUtc",
                table: "OutboxMessages",
                column: "ProcessedAtUtc",
                filter: "\"Status\" = 'Completed'");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_TenantId_DedupKey",
                table: "OutboxMessages",
                columns: new[] { "TenantId", "DedupKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_TicketSlaCycles_ResolutionMonitor",
                table: "TicketSlaCycles");

            migrationBuilder.DropIndex(
                name: "IX_TicketSlaCycles_ResponseMonitor",
                table: "TicketSlaCycles");

            migrationBuilder.DropColumn(
                name: "ResolutionBreachedAtUtc",
                table: "TicketSlaCycles");

            migrationBuilder.DropColumn(
                name: "ResolutionWarnedAtUtc",
                table: "TicketSlaCycles");

            migrationBuilder.DropColumn(
                name: "ResponseBreachedAtUtc",
                table: "TicketSlaCycles");

            migrationBuilder.DropColumn(
                name: "ResponseWarnedAtUtc",
                table: "TicketSlaCycles");
        }
    }
}
