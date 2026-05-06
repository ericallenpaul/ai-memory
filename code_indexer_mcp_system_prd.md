# Product Requirements Document (PRD)

## Product Name
Code Indexer + MCP Server (Local AI Code Intelligence Platform)

---

## 1. Overview

The goal is to build a cross-platform system that indexes source code repositories and exposes a structured, queryable interface for AI tools and humans to efficiently locate symbols, dependencies, and relationships within codebases.

The system minimizes token usage by replacing large context loading with precise, structured queries.

---

## 2. Objectives

### Primary Goals
- Reduce AI token consumption by avoiding large context loads
- Provide fast symbol lookup (functions, classes, SQL objects, etc.)
- Enable AI tools via MCP to query code structure
- Provide a UI for developers to explore indexed code
- Support large repositories efficiently

### Non-Goals (v1)
- No code editing or mutation
- No agent execution or shell access
- No cloud sync (local-first only)

---

## 3. High-Level Architecture

```
Repos → Indexer Service → SQLite → API → (React UI + MCP Server)
```

### Components

#### 1. Indexer Service (Core Engine)
- Cross-platform .NET Worker
- Runs as OS background service
- Handles indexing, scheduling, and updates

#### 2. SQLite Database
- Stores symbols, files, dependencies
- WAL mode enabled
- Read-heavy optimized

#### 3. API Layer (ASP.NET Core)
- Read-only endpoints
- Shared by UI and MCP server

#### 4. MCP Server
- Exposes structured tools for AI clients
- Calls API/service layer

#### 5. React + Electron UI
- Displays index data
- Controls indexing behavior

---

## 4. Functional Requirements

### 4.1 Repository Management
- Add/remove repositories
- Detect repo type (Git, folder)
- Store metadata (path, last indexed, size)

---

### 4.2 Indexing Engine

#### Supported Languages (v1)
- C# (Roslyn)
- JavaScript / TypeScript
- SQL (basic parsing)

#### Capabilities
- Extract symbols (functions, classes, methods)
- Extract references (callers, callees)
- Track dependencies
- File-level summaries

---

### 4.3 Change Detection

#### Strategy
1. Git-aware detection (primary)
2. Hash-based fallback
3. Optional file watcher (disabled by default)

#### Behavior
- Detect changed files via Git
- Re-index only modified files
- Schedule periodic full validation

---

### 4.4 Scheduling

- Incremental indexing: every 5–15 minutes
- Full reindex: daily/weekly
- Manual trigger via UI/API

---

### 4.5 Query Capabilities

#### Core Queries
- `find_symbol(name)`
- `find_file(path)`
- `find_references(symbol)`
- `get_callers(symbol)`
- `get_callees(symbol)`
- `get_related_files(symbol)`

#### Response Rules
- Return metadata first
- Return source only on demand

---

### 4.6 MCP Integration

Expose tools:

- find_symbol
- find_references
- get_callers
- get_callees
- find_file

All responses must be structured JSON.

---

### 4.7 UI Features

- Repository dashboard
- Index status (last run, duration, errors)
- Symbol search
- File browser
- Dependency graph view (future)
- Manual reindex controls

---

## 5. Non-Functional Requirements

### Performance
- Handle repos with 100k+ files
- Query latency < 100ms typical

### Reliability
- Survive restarts
- Resume indexing safely

### Storage
- Efficient SQLite usage
- WAL mode enabled

### Security
- Read-only operations
- No arbitrary code execution
- Localhost-only API by default

---

## 6. Cross-Platform Strategy

### Indexer Hosting

| OS | Mechanism |
|----|----------|
| Windows | Windows Service |
| Linux | systemd (user service) |
| macOS | launchd agent |

Indexer must auto-start on login/boot.

---

## 7. Data Model (High-Level)

### Tables

- Repositories
- Files
- Symbols
- References
- Dependencies

### Example Symbol

```
Symbol
- Id
- Name
- Type
- FilePath
- LineStart
- LineEnd
- Signature
```

---

## 8. API Endpoints

- GET /status
- GET /repos
- GET /symbols/search
- GET /files/{id}
- POST /reindex

---

## 9. Indexing Flow

```
Detect Changes → Parse Files → Extract Symbols → Store → Update Metadata
```

---

## 10. Risks & Mitigations

### Large Repos
- Mitigation: incremental indexing + Git detection

### File Watcher Overhead
- Mitigation: disable by default

### SQLite Locking
- Mitigation: WAL mode + write serialization

---

## 11. Future Enhancements

- Embeddings for semantic search
- Cross-repo linking
- Graph visualization
- Cloud sync
- AI-assisted summarization

---

## 12. Success Metrics

- Reduction in token usage
- Query latency
- Index completeness
- User adoption

---

## 13. MVP Scope

Include:
- Indexer service
- SQLite schema
- Basic API
- MCP server
- Minimal UI

Exclude:
- Graph visualization
- Advanced search
- Multi-user support

---

## 14. Deployment

- Packaged binaries per OS
- Installer registers background service
- Electron installs UI

---

## 15. Summary

This system provides a structured alternative to large-context AI workflows by enabling precise, low-cost code navigation through indexing and MCP-based querying.

