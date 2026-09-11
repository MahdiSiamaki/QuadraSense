/**
 * Typed API client.
 *
 * Thin on purpose: TanStack Query owns caching, retries and cancellation, so this
 * layer only needs to build a request, surface a useful error, and hand back JSON.
 */

const BASE = import.meta.env.VITE_API_BASE_URL ?? ''

/** RFC 9110 problem details, which is what the API returns for every failure. */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  errors?: Record<string, string[]>
  traceId?: string
}

/**
 * An API failure carrying everything needed to show the user something useful
 * and to find the request in the logs afterwards.
 */
export class ApiError extends Error {
  readonly status: number
  readonly problem: ProblemDetails | null
  /** Correlation ID from the response header. The one thing support needs. */
  readonly correlationId: string | null

  // Fields are declared and assigned explicitly rather than as constructor
  // parameter properties, which `erasableSyntaxOnly` disallows: that flag keeps
  // the source strippable to plain JS with no TypeScript-only runtime semantics.
  constructor(status: number, problem: ProblemDetails | null, correlationId: string | null) {
    super(problem?.title ?? `Request failed with status ${status}`)
    this.name = 'ApiError'
    this.status = status
    this.problem = problem
    this.correlationId = correlationId
  }

  /** Field-level validation messages, flattened for display next to inputs. */
  get fieldErrors(): Array<{ field: string; message: string }> {
    if (!this.problem?.errors) return []
    return Object.entries(this.problem.errors).flatMap(([field, messages]) =>
      messages.map((message) => ({ field, message })),
    )
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${BASE}${path}`, {
    ...init,
    headers: {
      Accept: 'application/json',
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...init?.headers,
    },
  })

  const correlationId = response.headers.get('X-Correlation-Id')

  if (!response.ok) {
    let problem: ProblemDetails | null = null
    try {
      problem = (await response.json()) as ProblemDetails
    } catch {
      // A non-JSON error body (a proxy error page, say) is not worth failing over.
    }
    throw new ApiError(response.status, problem, correlationId)
  }

  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

/**
 * Builds a query string, dropping empty values.
 *
 * Note what is *not* here: this is only ever used for filter dimensions and
 * paging. Subscriber identifiers go in a POST body, never a URL, so they cannot
 * reach an access log or browser history.
 */
function qs(params: Record<string, string | number | boolean | undefined | null>): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value === undefined || value === null || value === '') continue
    search.set(key, String(value))
  }
  const s = search.toString()
  return s ? `?${s}` : ''
}

export const api = {
  get: <T>(path: string, params?: Record<string, string | number | boolean | undefined | null>, signal?: AbortSignal) =>
    request<T>(`${path}${params ? qs(params) : ''}`, { signal }),

  post: <T>(path: string, body: unknown, signal?: AbortSignal) =>
    request<T>(path, { method: 'POST', body: JSON.stringify(body), signal }),
}
