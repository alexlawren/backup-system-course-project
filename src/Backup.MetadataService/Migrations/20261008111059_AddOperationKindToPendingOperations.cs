using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backup.MetadataService.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationKindToPendingOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OperationKind",
                table: "pending_file_operations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "NORMAL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OperationKind",
                table: "pending_file_operations");
        }
    }
}
