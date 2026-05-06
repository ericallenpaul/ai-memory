using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIMemory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeysAndMachineName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "machine_name",
                table: "sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "machine_name",
                table: "ingestion_log",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    api_key_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    key_hash = table.Column<string>(type: "TEXT", nullable: false),
                    key_prefix = table.Column<string>(type: "TEXT", nullable: false),
                    scopes = table.Column<string>(type: "TEXT", nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false),
                    last_used_at = table.Column<string>(type: "TEXT", nullable: true),
                    expires_at = table.Column<string>(type: "TEXT", nullable: true),
                    created_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.api_key_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sessions_machine_name",
                table: "sessions",
                column: "machine_name");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_is_active",
                table: "api_keys",
                column: "is_active");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_key_hash",
                table: "api_keys",
                column: "key_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropIndex(
                name: "IX_sessions_machine_name",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "machine_name",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "machine_name",
                table: "ingestion_log");
        }
    }
}
