const BASE = ''

async function request<T>(path: string, options?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    credentials: 'include',
    headers: { 'Content-Type': 'application/json', ...options?.headers },
    ...options
  })
  if (!res.ok) {
    const body = await res.json().catch(() => ({ error: res.statusText }))
    throw new ApiError(res.status, body.error ?? res.statusText)
  }
  return res.json()
}

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message)
  }
}

// Setup
export const getSetupStatus = () => request<{ needsSetup: boolean }>('/api/setup/status')
export const postSetupInit = (data: {
  username: string; password: string; databaseProvider: string;
  connectionString?: string; port?: number
}) => request<{ message: string }>('/api/setup/init', { method: 'POST', body: JSON.stringify(data) })

// Auth
export const postLogin = (data: { username: string; password: string }) =>
  request<{ username: string }>('/api/auth/login', { method: 'POST', body: JSON.stringify(data) })
export const postLogout = () => request<{ message: string }>('/api/auth/logout', { method: 'POST' })
export const getMe = () => request<{ username: string; id: string }>('/api/auth/me')

// Stats
export interface StatsResponse {
  totalSessions: number; totalMessages: number; totalTokensIn: number; totalTokensOut: number;
  totalCostUsd: number; sessionsToday: number;
  dailyStats: { date: string; sessions: number; messages: number }[]
}
export const getStats = () => request<StatsResponse>('/api/stats')

// Sessions
export interface SessionSummary {
  sessionId: string; externalId?: string; title: string; project?: string; repo?: string;
  branch?: string; tags: string[]; source?: string; isArchived: boolean;
  createdAt: string; updatedAt: string; messageCount: number
}
export interface MessageResponse {
  messageId: string; sessionId: string; role: string; content: string;
  provider?: string; model?: string; tokenIn?: number; tokenOut?: number;
  costUsd?: number; latencyMs?: number; createdAt: string
}
export interface ToolCallResponse {
  toolCallId: string; sessionId: string; toolName: string;
  argumentsJson?: unknown; resultJson?: unknown; createdAt: string
}
export interface ArtifactResponse {
  artifactId: string; sessionId: string; type: string;
  pathOrUrl?: string; hash?: string; metadataJson?: unknown; createdAt: string
}
export interface SessionDetail extends SessionSummary {
  messages?: MessageResponse[]; toolCalls?: ToolCallResponse[]; artifacts?: ArtifactResponse[]
}
export const getSessions = (params?: Record<string, string>) => {
  const qs = params ? '?' + new URLSearchParams(params).toString() : ''
  return request<SessionSummary[]>(`/api/sessions${qs}`)
}
export const getSession = (id: string) => request<SessionDetail>(`/api/sessions/${id}`)

// Search
export interface SearchResult {
  sessionId: string; sessionTitle?: string; project?: string;
  snippet: string; role?: string; rank: number; createdAt: string
}
export const searchMessages = (params: Record<string, string>) =>
  request<SearchResult[]>(`/api/search?${new URLSearchParams(params)}`)

// Ingestion Logs
export interface IngestionLogEntry {
  idempotencyKey: string; eventType: string; source: string;
  sourcePath: string; recordOffset: number; status: string; createdAt: string
}
export const getIngestionLog = (params?: Record<string, string>) => {
  const qs = params ? '?' + new URLSearchParams(params).toString() : ''
  return request<IngestionLogEntry[]>(`/api/ingestion-log${qs}`)
}

// API Keys
export interface ApiKeyResponse {
  apiKeyId: string; name: string; keyPrefix: string; scopes: string[];
  isActive: boolean; createdAt: string; lastUsedAt?: string;
  expiresAt?: string; createdBy?: string
}
export interface CreateApiKeyResponse {
  apiKeyId: string; name: string; rawKey: string; keyPrefix: string; scopes: string[]
}
export const getApiKeys = () => request<ApiKeyResponse[]>('/api/keys')
export const createApiKey = (data: { name: string; scopes: string[] }) =>
  request<CreateApiKeyResponse>('/api/keys', { method: 'POST', body: JSON.stringify(data) })
export const updateApiKey = (id: string, data: { name?: string; scopes?: string[]; isActive?: boolean }) =>
  request<ApiKeyResponse>(`/api/keys/${id}`, { method: 'PATCH', body: JSON.stringify(data) })
export const deleteApiKey = (id: string) =>
  request<{ message: string }>(`/api/keys/${id}`, { method: 'DELETE' })
