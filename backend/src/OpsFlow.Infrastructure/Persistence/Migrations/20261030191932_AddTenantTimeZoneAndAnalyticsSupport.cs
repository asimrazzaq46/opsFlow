using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpsFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantTimeZoneAndAnalyticsSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReportingTimeZone",
                table: "TenantSettings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "UTC");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_UpdatedAtUtc_Number",
                table: "Tickets",
                columns: new[] { "TenantId", "UpdatedAtUtc", "Number" },
                descending: new[] { false, true, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_TenantId_UpdatedAtUtc_Number",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ReportingTimeZone",
                table: "TenantSettings");
        }
    }
}
