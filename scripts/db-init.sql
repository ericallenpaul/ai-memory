-- AIMemory Database Initialization Script
-- Version: 0.1
-- Date: 2026-03-02
-- Run as: psql -U postgres -h localhost -f scripts/db-init.sql

-- 1.1 Create Database (run separately if needed; psql cannot CREATE DATABASE inside a transaction)
-- Execute this line manually if database doesn't exist:
-- CREATE DATABASE aimemory ENCODING 'UTF8';

-- Connect to aimemory database
\c aimemory

-- 1.2 Enable Extensions
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS "pgcrypto";
CREATE EXTENSION IF NOT EXISTS "pg_trgm";
CREATE EXTENSION IF NOT EXISTS "unaccent";

-- 1.3 Create Core Tables

CREATE TABLE IF NOT EXISTS sessions (
    session_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    external_id    TEXT UNIQUE,
    title          TEXT NOT NULL,
    project        TEXT,
    repo           TEXT,
    branch         TEXT,
    tags           TEXT[] DEFAULT '{}',
    source         TEXT,
    is_archived    BOOLEAN DEFAULT FALSE,
    created_at     TIMESTAMPTZ DEFAULT NOW(),
    updated_at     TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS messages (
    message_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id       UUID NOT NULL REFERENCES sessions(session_id),
    external_id      TEXT,
    role             TEXT NOT NULL CHECK (role IN ('system','user','assistant','tool')),
    content          TEXT NOT NULL,
    provider         TEXT,
    model            TEXT,
    request_id       TEXT,
    token_in         INT,
    token_out        INT,
    cost_usd         NUMERIC(10,6),
    latency_ms       INT,
    content_tsvector TSVECTOR,
    created_at       TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS tool_calls (
    tool_call_id   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id     UUID NOT NULL REFERENCES sessions(session_id),
    external_id    TEXT,
    tool_name      TEXT NOT NULL,
    arguments_json JSONB,
    result_json    JSONB,
    created_at     TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS artifacts (
    artifact_id    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id     UUID NOT NULL REFERENCES sessions(session_id),
    external_id    TEXT,
    type           TEXT NOT NULL,
    path_or_url    TEXT,
    hash           TEXT,
    metadata_json  JSONB,
    created_at     TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS ingestion_log (
    idempotency_key TEXT PRIMARY KEY,
    event_type      TEXT NOT NULL,
    source          TEXT NOT NULL,
    source_path     TEXT NOT NULL,
    record_offset   BIGINT NOT NULL,
    status          TEXT DEFAULT 'ok',
    created_at      TIMESTAMPTZ DEFAULT NOW()
);

-- 1.4 Create Indexes

CREATE INDEX IF NOT EXISTS idx_sessions_project ON sessions(project);
CREATE INDEX IF NOT EXISTS idx_sessions_repo ON sessions(repo);
CREATE INDEX IF NOT EXISTS idx_sessions_created_at ON sessions(created_at);
CREATE INDEX IF NOT EXISTS idx_sessions_source ON sessions(source);
CREATE INDEX IF NOT EXISTS idx_sessions_external_id ON sessions(external_id);
CREATE INDEX IF NOT EXISTS idx_sessions_tags ON sessions USING GIN(tags);

CREATE INDEX IF NOT EXISTS idx_messages_session_id ON messages(session_id);
CREATE INDEX IF NOT EXISTS idx_messages_provider ON messages(provider);
CREATE INDEX IF NOT EXISTS idx_messages_model ON messages(model);
CREATE INDEX IF NOT EXISTS idx_messages_created_at ON messages(created_at);
CREATE INDEX IF NOT EXISTS idx_messages_fts ON messages USING GIN(content_tsvector);
CREATE INDEX IF NOT EXISTS idx_messages_external_id ON messages(external_id);

CREATE INDEX IF NOT EXISTS idx_tool_calls_session_id ON tool_calls(session_id);
CREATE INDEX IF NOT EXISTS idx_tool_calls_tool_name ON tool_calls(tool_name);

CREATE INDEX IF NOT EXISTS idx_artifacts_session_id ON artifacts(session_id);
CREATE INDEX IF NOT EXISTS idx_artifacts_type ON artifacts(type);

CREATE INDEX IF NOT EXISTS idx_ingestion_source ON ingestion_log(source);
CREATE INDEX IF NOT EXISTS idx_ingestion_source_path ON ingestion_log(source_path);

-- 1.5 Create FTS Trigger

CREATE OR REPLACE FUNCTION messages_tsvector_trigger() RETURNS trigger AS $$
BEGIN
    NEW.content_tsvector := to_tsvector('english', COALESCE(NEW.content, ''));
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_messages_tsvector ON messages;
CREATE TRIGGER trg_messages_tsvector
    BEFORE INSERT OR UPDATE ON messages
    FOR EACH ROW
    EXECUTE FUNCTION messages_tsvector_trigger();

-- 1.6 Create Application User (idempotent)

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'aimemory_app') THEN
        CREATE USER aimemory_app WITH PASSWORD 'OB_app_2026!secure';
    END IF;
END
$$;

GRANT CONNECT ON DATABASE aimemory TO aimemory_app;
GRANT USAGE ON SCHEMA public TO aimemory_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO aimemory_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO aimemory_app;
