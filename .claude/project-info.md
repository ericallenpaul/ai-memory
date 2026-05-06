# OpenBrain - Project Information

## Project Overview
OpenBrain is a personal LLM work ledger and memory system that tracks prompts, sessions, tool calls, artifacts, and decisions across AI providers (Claude, Codex, OpenAI, local models). It provides durable storage, structured retrieval (search + embeddings), MCP access for agentic workflows, and cost/token telemetry.

It follows Eric's structured **C#/.NET architecture** with centralized logging, retry logic, and environment-based configuration.

## Essential Commands

### Development
```bash
# Build the entire solution
dotnet build src/OpenBrain.sln

# Run the API
dotnet run --project src/OpenBrain.Api

# Run the Ingestor (once)
dotnet run --project src/OpenBrain.Ingestor -- --once

# Run the Ingestor (service mode)
dotnet run --project src/OpenBrain.Ingestor

# Run the MCP server
dotnet run --project src/OpenBrain.Mcp

# Run tests
dotnet test

# Database init (from repo root)
# Requires: C:\Program Files\PostgreSQL\18\bin\psql
psql -U postgres -h localhost -f scripts/db-init.sql
```

### PostgreSQL Access
```bash
# psql path: C:\Program Files\PostgreSQL\18\bin\psql
# Connection: host=localhost user=openbrain_app dbname=openbrain
# Admin user: postgres (password in env/secrets)
```

## Directory Structure
```
AIMemory/
├── spec.md                          # OpenBrain API specification
├── ingester-spec.md                 # Ingestor specification
├── src/
│   ├── OpenBrain.sln                # Solution file
│   ├── OpenBrain.Models/            # Shared models, DTOs, event types
│   ├── OpenBrain.Data/              # EF Core DbContext, migrations, repositories
│   ├── OpenBrain.Api/               # .NET Minimal API (HTTP endpoints)
│   ├── OpenBrain.Ingestor/          # Windows background service + CLI
│   └── OpenBrain.Mcp/               # MCP Server (thin wrapper over API)
├── tests/
│   ├── OpenBrain.Tests.Unit/        # Unit tests
│   └── OpenBrain.Tests.Integration/ # Integration tests (requires DB)
├── scripts/
│   ├── db-init.sql                  # Database + extension + table setup
│   └── db-seed.sql                  # Optional seed data
├── docker/
│   └── Dockerfile                   # API container build
├── logs/                            # NLog output (7-day rolling retention)
└── .claude/                         # RIPER workflow configuration
```

## Technology Stack
- **Language:** C# (.NET 10)
- **Database:** PostgreSQL 18 (local, `C:\Program Files\PostgreSQL\18\bin`)
- **ORM:** Entity Framework Core (Npgsql provider)
- **Search:** PostgreSQL Full-Text Search (tsvector/tsquery) + pg_trgm
- **Logging:** NLog (7-day retention)
- **Retry Logic:** Polly (5 attempts, exponential backoff)
- **Containerization:** Docker (future)
- **Configuration:** Environment variables + User Secrets (dev)
- **Authentication:** API Key (X-API-Key header) — JWT/Cloudflare Access later
- **MCP:** Model Context Protocol server for agentic tool access
- **Extensions:** uuid-ossp, pgcrypto, pg_trgm, unaccent
- **Future:** pgvector for embedding search, Cloudflare Tunnel for internet access

## Key Concepts
- **Session** — A logical container for a conversation/workflow (project-scoped, time-bound)
- **Message** — An immutable interaction unit (system/user/assistant/tool role)
- **ToolCall** — Structured record of agent tool invocation
- **Artifact** — External/derived output (files, scripts, diffs, commits)
- **Ingestor** — Background service that captures CLI tool activity into OpenBrain

## API Endpoints
| Method | Path | Description |
|--------|------|-------------|
| POST | /sessions | Create a session |
| GET | /sessions/{id} | Get session + messages |
| POST | /sessions/{id}/messages | Append a message |
| POST | /sessions/{id}/toolcalls | Append a tool call |
| POST | /sessions/{id}/artifacts | Append an artifact |
| GET | /search | Full-text search |
| POST | /ingest/batch | Batch ingest (for Ingestor) |
| GET | /health | Health check (no auth) |

## Ingestor Sources
| Source | Path | Format |
|--------|------|--------|
| Claude Code | `~/.claude/projects/*/uuid.jsonl` | JSONL (structured messages) |
| Codex CLI | `~/.codex/history.jsonl` | JSONL (session_id, ts, text) |
| Codex CLI logs | `~/.codex/log/codex-tui.log` | Text (ToolCall entries) |

## RIPER Workflow

This project uses the RIPER development process for structured, context-efficient development.

### Available Commands
- `/riper:strict` - Enable strict RIPER protocol enforcement
- `/riper:research` - Research mode for information gathering
- `/riper:innovate` - Innovation mode for brainstorming (optional)
- `/riper:plan` - Planning mode for specifications
- `/riper:execute` - Execution mode for implementation
- `/riper:execute <substep>` - Execute a specific substep from the plan
- `/riper:review` - Review mode for validation
- `/memory:save` - Save context to memory bank
- `/memory:recall` - Retrieve from memory bank
- `/memory:list` - List all memories

### Workflow Phases
1. **Research & Innovate** - Understand and explore the codebase and requirements
2. **Plan** - Create detailed technical specifications saved to memory bank
3. **Execute** - Implement exactly what was specified in the approved plan
4. **Review** - Validate implementation against the plan

### Using the Workflow
1. Start with `/riper:strict` to enable strict mode enforcement
2. Use `/riper:research` to investigate the codebase
3. Optionally use `/riper:innovate` to brainstorm approaches
4. Create a plan with `/riper:plan`
5. Execute with `/riper:execute` (or `/riper:execute 1.2` for specific steps)
6. Validate with `/riper:review`

## Memory Bank Policy

### CRITICAL: Repository-Level Memory Bank
- Memory-bank location: Use `git rev-parse --show-toplevel` to find root, then `[ROOT]/.claude/memory-bank/`
- NEVER create memory-banks in subdirectories or packages
- All memories are branch-aware and date-organized
- Memories persist across sessions and can be shared with team

### Memory Bank Structure
```
.claude/memory-bank/
├── [branch-name]/
│   ├── plans/      # Technical specifications
│   ├── reviews/    # Code review reports
│   └── sessions/   # Session context
```

## Development Guidelines

- Follow **Eric's standard architecture**:
  - `Data` → `Models` → `Console/API`
  - Implement interfaces first (e.g., `ISessionRepository` before concrete implementations)
  - Use **Dependency Injection** for all services
  - Include **NLog** + **Polly** in every service method
- Prefer `Response.Headers.Append` over `.Add`
- All logs should go to `/logs` with daily rolling file targets
- Keep `appsettings.json` minimal—use environment overrides
- Limit log retention to **7 days**
- Avoid storing secrets in files — use Visual Studio User Secrets for local development
- Store everything as immutable events
- Keep raw data immutable; add derived data (summaries, embeddings) separately
- Structured JSON outputs only for MCP tools
- Any graphs included in md files should be created with SVG