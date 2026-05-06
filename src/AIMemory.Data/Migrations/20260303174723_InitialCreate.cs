using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIMemory.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_users",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    username = table.Column<string>(type: "TEXT", nullable: false),
                    password_hash = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ingestion_log",
                columns: table => new
                {
                    idempotency_key = table.Column<string>(type: "TEXT", nullable: false),
                    event_type = table.Column<string>(type: "TEXT", nullable: false),
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    source_path = table.Column<string>(type: "TEXT", nullable: false),
                    record_offset = table.Column<long>(type: "INTEGER", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingestion_log", x => x.idempotency_key);
                });

            migrationBuilder.CreateTable(
                name: "message_embeddings",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    session_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    embedding = table.Column<byte[]>(type: "BLOB", nullable: false),
                    dimensions = table.Column<int>(type: "INTEGER", nullable: false),
                    model = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_embeddings", x => x.message_id);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    external_id = table.Column<string>(type: "TEXT", nullable: true),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    project = table.Column<string>(type: "TEXT", nullable: true),
                    repo = table.Column<string>(type: "TEXT", nullable: true),
                    branch = table.Column<string>(type: "TEXT", nullable: true),
                    tags = table.Column<string>(type: "TEXT", nullable: false),
                    source = table.Column<string>(type: "TEXT", nullable: true),
                    is_archived = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.session_id);
                });

            migrationBuilder.CreateTable(
                name: "artifacts",
                columns: table => new
                {
                    artifact_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    session_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    external_id = table.Column<string>(type: "TEXT", nullable: true),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    path_or_url = table.Column<string>(type: "TEXT", nullable: true),
                    hash = table.Column<string>(type: "TEXT", nullable: true),
                    metadata_json = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifacts", x => x.artifact_id);
                    table.ForeignKey(
                        name: "FK_artifacts_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    session_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    external_id = table.Column<string>(type: "TEXT", nullable: true),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    content = table.Column<string>(type: "TEXT", nullable: false),
                    provider = table.Column<string>(type: "TEXT", nullable: true),
                    model = table.Column<string>(type: "TEXT", nullable: true),
                    request_id = table.Column<string>(type: "TEXT", nullable: true),
                    token_in = table.Column<int>(type: "INTEGER", nullable: true),
                    token_out = table.Column<int>(type: "INTEGER", nullable: true),
                    cost_usd = table.Column<decimal>(type: "TEXT", precision: 10, scale: 6, nullable: true),
                    latency_ms = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => x.message_id);
                    table.ForeignKey(
                        name: "FK_messages_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tool_calls",
                columns: table => new
                {
                    tool_call_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    session_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    external_id = table.Column<string>(type: "TEXT", nullable: true),
                    tool_name = table.Column<string>(type: "TEXT", nullable: false),
                    arguments_json = table.Column<string>(type: "TEXT", nullable: true),
                    result_json = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_calls", x => x.tool_call_id);
                    table.ForeignKey(
                        name: "FK_tool_calls_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_users_username",
                table: "admin_users",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_artifacts_session_id",
                table: "artifacts",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "IX_artifacts_type",
                table: "artifacts",
                column: "type");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_log_source",
                table: "ingestion_log",
                column: "source");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_log_source_path",
                table: "ingestion_log",
                column: "source_path");

            migrationBuilder.CreateIndex(
                name: "IX_message_embeddings_session_id",
                table: "message_embeddings",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "IX_messages_created_at",
                table: "messages",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_messages_model",
                table: "messages",
                column: "model");

            migrationBuilder.CreateIndex(
                name: "IX_messages_provider",
                table: "messages",
                column: "provider");

            migrationBuilder.CreateIndex(
                name: "IX_messages_session_id",
                table: "messages",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_created_at",
                table: "sessions",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_external_id",
                table: "sessions",
                column: "external_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sessions_project",
                table: "sessions",
                column: "project");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_repo",
                table: "sessions",
                column: "repo");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_source",
                table: "sessions",
                column: "source");

            migrationBuilder.CreateIndex(
                name: "IX_tool_calls_session_id",
                table: "tool_calls",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "IX_tool_calls_tool_name",
                table: "tool_calls",
                column: "tool_name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_users");

            migrationBuilder.DropTable(
                name: "artifacts");

            migrationBuilder.DropTable(
                name: "ingestion_log");

            migrationBuilder.DropTable(
                name: "message_embeddings");

            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.DropTable(
                name: "tool_calls");

            migrationBuilder.DropTable(
                name: "sessions");
        }
    }
}
