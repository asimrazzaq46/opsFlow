using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpsFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TicketTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AssigneeMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedByMembershipId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketTasks", x => x.Id);
                    table.CheckConstraint("CK_TicketTasks_SortOrder", "\"SortOrder\" > 0");
                    table.CheckConstraint("CK_TicketTasks_Status", "\"Status\" IN ('Todo','InProgress','Done')");
                    table.CheckConstraint("CK_TicketTasks_Title", "length(trim(\"Title\")) > 0");
                    table.ForeignKey(
                        name: "FK_TicketTasks_Memberships_TenantId_AssigneeMembershipId",
                        columns: x => new { x.TenantId, x.AssigneeMembershipId },
                        principalTable: "Memberships",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketTasks_Memberships_TenantId_CreatedByMembershipId",
                        columns: x => new { x.TenantId, x.CreatedByMembershipId },
                        principalTable: "Memberships",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketTasks_Memberships_TenantId_UpdatedByMembershipId",
                        columns: x => new { x.TenantId, x.UpdatedByMembershipId },
                        principalTable: "Memberships",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketTasks_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketTasks_Tickets_TenantId_TicketId",
                        columns: x => new { x.TenantId, x.TicketId },
                        principalTable: "Tickets",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketTasks_TenantId_AssigneeMembershipId_Status",
                table: "TicketTasks",
                columns: new[] { "TenantId", "AssigneeMembershipId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketTasks_TenantId_CreatedByMembershipId",
                table: "TicketTasks",
                columns: new[] { "TenantId", "CreatedByMembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketTasks_TenantId_TicketId_SortOrder",
                table: "TicketTasks",
                columns: new[] { "TenantId", "TicketId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketTasks_TenantId_UpdatedByMembershipId",
                table: "TicketTasks",
                columns: new[] { "TenantId", "UpdatedByMembershipId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketTasks");
        }
    }
}
