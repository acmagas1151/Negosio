import type { ApiErrorBody } from './types'

const BASE_URL = (import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5170').replace(/\/$/, '')

/** Error thrown for any non-2xx API response, carrying the server's error envelope. */
export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly traceId?: string
  readonly fieldErrors?: Record<string, string[]>

  constructor(status: number, body: Partial<ApiErrorBody> & { message: string }) {
    super(body.message)
    this.name = 'ApiError'
    this.status = status
    this.code = body.code ?? 'UNKNOWN'
    this.traceId = body.traceId
    this.fieldErrors = body.errors
  }

  get isValidation() {
    return this.status === 400 && !!this.fieldErrors
  }
}

let authToken: string | null = null

/** Set (or clear) the bearer token used for subsequent requests. */
export function setAuthToken(token: string | null) {
  authToken = token
}

type RequestOptions = {
  method?: string
  body?: unknown
  /** When false, a 401 will not trigger the global unauthorized handler. */
  signOutOn401?: boolean
}

let onUnauthorized: (() => void) | null = null

/** Registered by the auth layer so a 401 anywhere can clear local session state. */
export function setUnauthorizedHandler(handler: (() => void) | null) {
  onUnauthorized = handler
}

export async function apiRequest<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, signOutOn401 = true } = options

  const headers: Record<string, string> = { Accept: 'application/json' }
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  if (authToken) headers['Authorization'] = `Bearer ${authToken}`

  let response: Response
  try {
    response = await fetch(`${BASE_URL}${path}`, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  } catch {
    throw new ApiError(0, { code: 'NETWORK_ERROR', message: 'Unable to reach the server. Check your connection and try again.' })
  }

  if (response.status === 401 && signOutOn401) {
    onUnauthorized?.()
  }

  if (response.status === 204) {
    return undefined as T
  }

  const isJson = response.headers.get('content-type')?.includes('application/json')
  const payload = isJson ? await response.json().catch(() => null) : null

  if (!response.ok) {
    throw new ApiError(response.status, {
      code: payload?.code,
      message: payload?.message ?? `Request failed with status ${response.status}.`,
      traceId: payload?.traceId,
      errors: payload?.errors,
    })
  }

  return payload as T
}
