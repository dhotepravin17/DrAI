import type { ReferenceData } from './types'

let authToken: string | null = null

export function setApiToken(token: string | null) {
  authToken = token
}

export const AUTH_EXPIRED_EVENT = 'smartqueue:auth-expired'

export class ApiError extends Error {
  readonly status: number
  readonly detail: string | undefined
  readonly fieldErrors: Record<string, string[]>

  constructor(status: number, title: string, detail?: string, fieldErrors: Record<string, string[]> = {}) {
    super(title)
    this.status = status
    this.detail = detail
    this.fieldErrors = fieldErrors
  }
}

interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE'
  body?: unknown
  signal?: AbortSignal
  headers?: Record<string, string>
}

export function getApiToken() {
  return authToken
}

interface ProblemBody {
  title?: string
  detail?: string
  errors?: Record<string, string[]>
}

export async function api<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json', ...options.headers }
  if (options.body !== undefined) headers['Content-Type'] = 'application/json'
  if (authToken) headers.Authorization = `Bearer ${authToken}`

  const response = await fetch(`/api/v1${path}`, {
    method: options.method ?? 'GET',
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
    signal: options.signal,
  })

  if (response.status === 401 && authToken) {
    window.dispatchEvent(new Event(AUTH_EXPIRED_EVENT))
  }

  if (!response.ok) {
    let problem: ProblemBody = {}
    try {
      problem = (await response.json()) as ProblemBody
    } catch {
      // Non-JSON error bodies fall back to the status code.
    }
    const title = problem.title ?? (response.status === 429 ? 'Too many requests. Please wait and try again.' : `Request failed (${response.status}).`)
    throw new ApiError(response.status, title, problem.detail, problem.errors ?? {})
  }

  const text = await response.text()
  return (text ? JSON.parse(text) : undefined) as T
}

let referencePromise: Promise<ReferenceData> | null = null

export function loadReference(): Promise<ReferenceData> {
  referencePromise ??= api<ReferenceData>('/reference').catch((error: unknown) => {
    referencePromise = null
    throw error
  })
  return referencePromise
}

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.detail ? `${error.message} ${error.detail}` : error.message
  return 'Something went wrong. Check your connection and try again.'
}
