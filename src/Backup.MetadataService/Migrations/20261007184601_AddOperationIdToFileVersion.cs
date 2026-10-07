using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backup.MetadataService.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationIdToFileVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OperationId",
                table: "file_versions",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.CreateIndex(
                name: "IX_file_versions_OperationId",
                table: "file_versions",
                column: "OperationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_file_versions_OperationId",
                table: "file_versions");

            migrationBuilder.DropColumn(
                name: "OperationId",
                table: "file_versions");
        }
    }
}
