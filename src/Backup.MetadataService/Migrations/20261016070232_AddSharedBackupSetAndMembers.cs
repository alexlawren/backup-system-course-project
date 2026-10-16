using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backup.MetadataService.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedBackupSetAndMembers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActorClientId",
                table: "pending_file_operations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ActorClientId",
                table: "change_logs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccessMode",
                table: "backup_sets",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "PRIVATE");

            migrationBuilder.AddColumn<string>(
                name: "ShareCode",
                table: "backup_sets",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "backup_set_members",
                columns: table => new
                {
                    BackupSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "MEMBER"),
                    JoinedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_set_members", x => new { x.BackupSetId, x.ClientId });
                    table.ForeignKey(
                        name: "FK_backup_set_members_backup_sets_BackupSetId",
                        column: x => x.BackupSetId,
                        principalTable: "backup_sets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_backup_set_members_clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_change_logs_ActorClientId",
                table: "change_logs",
                column: "ActorClientId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_sets_ShareCode",
                table: "backup_sets",
                column: "ShareCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backup_set_members_ClientId",
                table: "backup_set_members",
                column: "ClientId");

            migrationBuilder.AddForeignKey(
                name: "FK_change_logs_clients_ActorClientId",
                table: "change_logs",
                column: "ActorClientId",
                principalTable: "clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Backfill OWNER membership for all existing backup sets
            migrationBuilder.Sql(
                "INSERT INTO backup_set_members (\"BackupSetId\", \"ClientId\", \"Role\", \"JoinedAtUtc\") " +
                "SELECT \"Id\", \"ClientId\", 'OWNER', \"CreatedAtUtc\" FROM backup_sets " +
                "ON CONFLICT (\"BackupSetId\", \"ClientId\") DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_change_logs_clients_ActorClientId",
                table: "change_logs");

            migrationBuilder.DropTable(
                name: "backup_set_members");

            migrationBuilder.DropIndex(
                name: "IX_change_logs_ActorClientId",
                table: "change_logs");

            migrationBuilder.DropIndex(
                name: "IX_backup_sets_ShareCode",
                table: "backup_sets");

            migrationBuilder.DropColumn(
                name: "ActorClientId",
                table: "pending_file_operations");

            migrationBuilder.DropColumn(
                name: "ActorClientId",
                table: "change_logs");

            migrationBuilder.DropColumn(
                name: "AccessMode",
                table: "backup_sets");

            migrationBuilder.DropColumn(
                name: "ShareCode",
                table: "backup_sets");
        }
    }
}
