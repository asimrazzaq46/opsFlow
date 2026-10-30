using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpsFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Invitations_Role",
                table: "Invitations");

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                    table.UniqueConstraint("AK_Customers_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Customers_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Customers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SlaPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ResponseMinutes = table.Column<int>(type: "integer", nullable: false),
                    ResolutionMinutes = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaPolicies", x => x.Id);
                    table.UniqueConstraint("AK_SlaPolicies_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SlaPolicies_Priority", "\"Priority\" IN ('Low','Medium','High','Urgent')");
                    table.CheckConstraint("CK_SlaPolicies_Targets", "\"ResponseMinutes\" BETWEEN 1 AND 525600 AND \"ResolutionMinutes\" BETWEEN 1 AND 525600 AND \"ResolutionMinutes\" >= \"ResponseMinutes\"");
                    table.ForeignKey(
                        name: "FK_SlaPolicies_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantSettings",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketNumberPrefix = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantSettings", x => x.TenantId);
                    table.CheckConstraint("CK_TenantSettings_Prefix", "length(\"TicketNumberPrefix\") > 0");
                    table.ForeignKey(
                        name: "FK_TenantSettings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantTicketCounters",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastNumber = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantTicketCounters", x => x.TenantId);
                    table.CheckConstraint("CK_TenantTicketCounters_LastNumber", "\"LastNumber\" >= 0");
                    table.ForeignKey(
                        name: "FK_TenantTicketCounters_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketCategories", x => x.Id);
                    table.UniqueConstraint("AK_TicketCategories_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_TicketCategories_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Tickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ActivitySequence = table.Column<int>(type: "integer", nullable: false),
                    CycleCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tickets", x => x.Id);
                    table.UniqueConstraint("AK_Tickets_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_Tickets_Number", "\"Number\" > 0");
                    table.CheckConstraint("CK_Tickets_Priority", "\"Priority\" IN ('Low','Medium','High','Urgent')");
                    table.CheckConstraint("CK_Tickets_Status", "\"Status\" IN ('Open','Assigned','InProgress','OnHold','Resolved','Closed')");
                    table.ForeignKey(
                        name: "FK_Tickets_Customers_TenantId_CustomerId",
                        columns: x => new { x.TenantId, x.CustomerId },
                        principalTable: "Customers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_TicketCategories_TenantId_CategoryId",
                        columns: x => new { x.TenantId, x.CategoryId },
                        principalTable: "TicketCategories",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OldValue = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    NewValue = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RelatedId = table.Column<Guid>(type: "uuid", nullable: true),
                    CustomerVisible = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketActivities", x => x.Id);
                    table.CheckConstraint("CK_TicketActivities_Sequence", "\"Sequence\" > 0");
                    table.ForeignKey(
                        name: "FK_TicketActivities_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketActivities_Tickets_TenantId_TicketId",
                        columns: x => new { x.TenantId, x.TicketId },
                        principalTable: "Tickets",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketActivities_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedByMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UnassignedByMembershipId = table.Column<Guid>(type: "uuid", nullable: true),
                    UnassignedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketAssignments_Memberships_TenantId_AssignedByMembership~",
                        columns: x => new { x.TenantId, x.AssignedByMembershipId },
                        principalTable: "Memberships",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketAssignments_Memberships_TenantId_MembershipId",
                        columns: x => new { x.TenantId, x.MembershipId },
                        principalTable: "Memberships",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketAssignments_Memberships_TenantId_UnassignedByMembersh~",
                        columns: x => new { x.TenantId, x.UnassignedByMembershipId },
                        principalTable: "Memberships",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketAssignments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketAssignments_Tickets_TenantId_TicketId",
                        columns: x => new { x.TenantId, x.TicketId },
                        principalTable: "Tickets",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketSlaCycles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    CycleNumber = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SlaPolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResponseMinutes = table.Column<int>(type: "integer", nullable: false),
                    ResolutionMinutes = table.Column<int>(type: "integer", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResponseDueAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolutionDueAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FirstResponseAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketSlaCycles", x => x.Id);
                    table.CheckConstraint("CK_TicketSlaCycles_CycleNumber", "\"CycleNumber\" > 0");
                    table.CheckConstraint("CK_TicketSlaCycles_Reason", "\"Reason\" IN ('Initial','Reopened')");
                    table.ForeignKey(
                        name: "FK_TicketSlaCycles_SlaPolicies_TenantId_SlaPolicyId",
                        columns: x => new { x.TenantId, x.SlaPolicyId },
                        principalTable: "SlaPolicies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketSlaCycles_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketSlaCycles_Tickets_TenantId_TicketId",
                        columns: x => new { x.TenantId, x.TicketId },
                        principalTable: "Tickets",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Invitations_Role",
                table: "Invitations",
                sql: "\"Role\" IN ('TenantOwner','Manager','Agent','Customer')");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId_NormalizedEmail",
                table: "Customers",
                columns: new[] { "TenantId", "NormalizedEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId_UserId",
                table: "Customers",
                columns: new[] { "TenantId", "UserId" },
                unique: true,
                filter: "\"UserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_UserId",
                table: "Customers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SlaPolicies_TenantId_Priority",
                table: "SlaPolicies",
                columns: new[] { "TenantId", "Priority" },
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_TicketActivities_ActorUserId",
                table: "TicketActivities",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketActivities_TenantId_TicketId",
                table: "TicketActivities",
                columns: new[] { "TenantId", "TicketId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketActivities_TicketId_Sequence",
                table: "TicketActivities",
                columns: new[] { "TicketId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketAssignments_MembershipId",
                table: "TicketAssignments",
                column: "MembershipId",
                filter: "\"UnassignedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TicketAssignments_TenantId_AssignedByMembershipId",
                table: "TicketAssignments",
                columns: new[] { "TenantId", "AssignedByMembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketAssignments_TenantId_MembershipId",
                table: "TicketAssignments",
                columns: new[] { "TenantId", "MembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketAssignments_TenantId_TicketId",
                table: "TicketAssignments",
                columns: new[] { "TenantId", "TicketId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketAssignments_TenantId_UnassignedByMembershipId",
                table: "TicketAssignments",
                columns: new[] { "TenantId", "UnassignedByMembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketAssignments_TicketId_MembershipId",
                table: "TicketAssignments",
                columns: new[] { "TicketId", "MembershipId" },
                unique: true,
                filter: "\"UnassignedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TicketCategories_TenantId_NormalizedName",
                table: "TicketCategories",
                columns: new[] { "TenantId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CreatedByUserId",
                table: "Tickets",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_CategoryId",
                table: "Tickets",
                columns: new[] { "TenantId", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_CreatedAtUtc_Id",
                table: "Tickets",
                columns: new[] { "TenantId", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_CreatedByUserId",
                table: "Tickets",
                columns: new[] { "TenantId", "CreatedByUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_CustomerId",
                table: "Tickets",
                columns: new[] { "TenantId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_Number",
                table: "Tickets",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_Status",
                table: "Tickets",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketSlaCycles_TenantId_ResolutionDueAtUtc",
                table: "TicketSlaCycles",
                columns: new[] { "TenantId", "ResolutionDueAtUtc" },
                filter: "\"ResolvedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TicketSlaCycles_TenantId_SlaPolicyId",
                table: "TicketSlaCycles",
                columns: new[] { "TenantId", "SlaPolicyId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketSlaCycles_TenantId_TicketId",
                table: "TicketSlaCycles",
                columns: new[] { "TenantId", "TicketId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketSlaCycles_TicketId",
                table: "TicketSlaCycles",
                column: "TicketId",
                unique: true,
                filter: "\"ResolvedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TicketSlaCycles_TicketId_CycleNumber",
                table: "TicketSlaCycles",
                columns: new[] { "TicketId", "CycleNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantSettings");

            migrationBuilder.DropTable(
                name: "TenantTicketCounters");

            migrationBuilder.DropTable(
                name: "TicketActivities");

            migrationBuilder.DropTable(
                name: "TicketAssignments");

            migrationBuilder.DropTable(
                name: "TicketSlaCycles");

            migrationBuilder.DropTable(
                name: "SlaPolicies");

            migrationBuilder.DropTable(
                name: "Tickets");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropTable(
                name: "TicketCategories");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Invitations_Role",
                table: "Invitations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Invitations_Role",
                table: "Invitations",
                sql: "\"Role\" IN ('TenantOwner','Manager','Agent')");
        }
    }
}
