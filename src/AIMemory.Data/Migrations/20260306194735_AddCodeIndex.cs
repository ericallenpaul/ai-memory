using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIMemory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCodeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "code_repositories",
                columns: table => new
                {
                    repository_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    source_type = table.Column<string>(type: "TEXT", nullable: false),
                    source_path = table.Column<string>(type: "TEXT", nullable: false),
                    default_branch = table.Column<string>(type: "TEXT", nullable: true),
                    file_count = table.Column<int>(type: "INTEGER", nullable: false),
                    symbol_count = table.Column<int>(type: "INTEGER", nullable: false),
                    indexed_at = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_repositories", x => x.repository_id);
                });

            migrationBuilder.CreateTable(
                name: "code_files",
                columns: table => new
                {
                    file_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    repository_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    file_path = table.Column<string>(type: "TEXT", nullable: false),
                    language = table.Column<string>(type: "TEXT", nullable: false),
                    file_size = table.Column<long>(type: "INTEGER", nullable: false),
                    content_hash = table.Column<string>(type: "TEXT", nullable: false),
                    indexed_at = table.Column<string>(type: "TEXT", nullable: false),
                    CodeRepositoryRepositoryId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_files", x => x.file_id);
                    table.ForeignKey(
                        name: "FK_code_files_code_repositories_CodeRepositoryRepositoryId",
                        column: x => x.CodeRepositoryRepositoryId,
                        principalTable: "code_repositories",
                        principalColumn: "repository_id");
                });

            migrationBuilder.CreateTable(
                name: "code_symbols",
                columns: table => new
                {
                    symbol_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    file_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    repository_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    symbol_key = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    qualified_name = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    signature = table.Column<string>(type: "TEXT", nullable: true),
                    summary = table.Column<string>(type: "TEXT", nullable: true),
                    start_line = table.Column<int>(type: "INTEGER", nullable: false),
                    end_line = table.Column<int>(type: "INTEGER", nullable: false),
                    start_byte = table.Column<long>(type: "INTEGER", nullable: false),
                    end_byte = table.Column<long>(type: "INTEGER", nullable: false),
                    parent_symbol_key = table.Column<string>(type: "TEXT", nullable: true),
                    indexed_at = table.Column<string>(type: "TEXT", nullable: false),
                    CodeFileFileId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_symbols", x => x.symbol_id);
                    table.ForeignKey(
                        name: "FK_code_symbols_code_files_CodeFileFileId",
                        column: x => x.CodeFileFileId,
                        principalTable: "code_files",
                        principalColumn: "file_id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_code_files_CodeRepositoryRepositoryId",
                table: "code_files",
                column: "CodeRepositoryRepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_code_files_language",
                table: "code_files",
                column: "language");

            migrationBuilder.CreateIndex(
                name: "IX_code_files_repository_id",
                table: "code_files",
                column: "repository_id");

            migrationBuilder.CreateIndex(
                name: "IX_code_files_repository_id_file_path",
                table: "code_files",
                columns: new[] { "repository_id", "file_path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_code_repositories_name",
                table: "code_repositories",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_code_repositories_source_type",
                table: "code_repositories",
                column: "source_type");

            migrationBuilder.CreateIndex(
                name: "IX_code_symbols_CodeFileFileId",
                table: "code_symbols",
                column: "CodeFileFileId");

            migrationBuilder.CreateIndex(
                name: "IX_code_symbols_file_id",
                table: "code_symbols",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "IX_code_symbols_kind",
                table: "code_symbols",
                column: "kind");

            migrationBuilder.CreateIndex(
                name: "IX_code_symbols_name",
                table: "code_symbols",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "IX_code_symbols_qualified_name",
                table: "code_symbols",
                column: "qualified_name");

            migrationBuilder.CreateIndex(
                name: "IX_code_symbols_repository_id",
                table: "code_symbols",
                column: "repository_id");

            migrationBuilder.CreateIndex(
                name: "IX_code_symbols_symbol_key",
                table: "code_symbols",
                column: "symbol_key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "code_symbols");

            migrationBuilder.DropTable(
                name: "code_files");

            migrationBuilder.DropTable(
                name: "code_repositories");
        }
    }
}
