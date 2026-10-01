/**
 * Typed API client.
 *
 * Thin on purpose: TanStack Query owns caching, retries and cancellation, so this
 * layer only needs to build a request, surface a useful error, and hand back JSON.
 */

const BASE = import.meta.env.VITE_API_BASE_URL ?? ''

/**
 * Absolute URL for an API path, for the places that cannot go through `request`.
 *
 * An `<img src>` is the case that needed this: the browser fetches it, not the client, so it does
 * not pick up the base the way every `api.*` call does. A relative path works in production, where
 * the SPA and the API share an origin, and silently 404s in development, where they do not.
 *
 * The session cookie still travels: :5173 and :5202 are different origins but the SAME SITE, and
 * SameSite=Strict is about the site.
 */
export function apiUrl(path: string): string {
  return `${BASE}${path}`
}

/**
 * Name of the CSRF cookie, which the server sets on sign-in and the SPA echoes back.
 *
 * The `__Host-` prefix is browser-enforced and requires a Secure cookie, so the server drops it
 * on a plain-HTTP development origin - a browser would otherwise reject the cookie silently.
 * Both spellings are looked for here, rather than the client needing to know which environment
 * it is in.
 */
const CSRF_COOKIE_NAMES = ['__Host-sqm_csrf', 'sqm_csrf']
const CSRF_HEADER = 'X-CSRF-Token'

const UNSAFE_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE'])

function readCsrfToken(): string | null {
  for (const name of CSRF_COOKIE_NAMES) {
    const match = document.cookie.match(new RegExp(`(?:^|; )${name}=([^;]*)`))
    if (match?.[1]) return decodeURIComponent(match[1])
  }
  return null
}

/**
 * The CSRF header for a state-changing request, or null when there is no token to echo.
 *
 * Exported for the one request that cannot go through `request()`: the import upload uses
 * XMLHttpRequest for progress events, and therefore has to assemble by hand what every other
 * call gets for free. That upload spent a while returning 401 because it did exactly that and
 * forgot both halves.
 */
export function csrfHeader(): { name: string; value: string } | null {
  const value = readCsrfToken()
  return value ? { name: CSRF_HEADER, value } : null
}

/** Tells the auth layer the session is gone. For callers outside `request()`. */
export function notifyUnauthenticated(): void {
  onUnauthenticated?.()
}

/**
 * Called when the server says the session is gone.
 *
 * Set once by the auth layer. The client cannot import the router without a cycle, and a module
 * that reaches into navigation from inside a fetch wrapper is the kind of hidden coupling that
 * makes a 401 impossible to trace.
 */
let onUnauthenticated: (() => void) | null = null

/** Registers what to do when a request comes back 401. */
export function setUnauthenticatedHandler(handler: () => void): void {
  onUnauthenticated = handler
}

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
  const response = await send(path, init)
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

/** Sends a request and returns the response once it is known to be a success; throws ApiError otherwise. */
async function send(path: string, init?: RequestInit, accept = 'application/json'): Promise<Response> {
  const method = (init?.method ?? 'GET').toUpperCase()

  // Double-submit CSRF: the server sets a token in a cookie the page CAN read, and every
  // state-changing request echoes it in a header. A cross-site page can make the browser SEND
  // the session cookie but cannot READ this one, so it cannot produce the header.
  const csrf = UNSAFE_METHODS.has(method) ? readCsrfToken() : null

  const response = await fetch(`${BASE}${path}`, {
    ...init,
    // The session lives in an HttpOnly cookie, which fetch does not send cross-origin without
    // this. In development the SPA is on :5173 and the API on :5202, so it is always cross-origin.
    credentials: 'include',
    headers: {
      Accept: accept,
      // FormData sets its own Content-Type, and it must: the multipart boundary is
      // generated by the browser and a hand-written header would omit it, leaving the
      // server unable to find where one part ends and the next begins.
      ...(init?.body && !(init.body instanceof FormData)
        ? { 'Content-Type': 'application/json' }
        : {}),
      ...(csrf ? { [CSRF_HEADER]: csrf } : {}),
      ...init?.headers,
    },
  })

  const correlationId = response.headers.get('X-Correlation-Id')

  if (response.status === 401) {
    // The session expired, was revoked, or the account was deactivated. The app cannot recover
    // by retrying, so it hands control to the auth layer, which shows the login page.
    onUnauthenticated?.()
  }

  if (!response.ok) {
    let problem: ProblemDetails | null = null
    try {
      problem = (await response.json()) as ProblemDetails
    } catch {
      // A non-JSON error body (a proxy error page, say) is not worth failing over.
    }
    throw new ApiError(response.status, problem, correlationId)
  }

  return response
}

/** A file the server sent: its bytes, and its name when the browser is allowed to read it. */
export interface DownloadedFile {
  blob: Blob
  /**
   * From Content-Disposition. Null across origins - in development the SPA and the API are on
   * different ports, and the header is not exposed to scripts there - so callers keep a fallback.
   */
  filename: string | null
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

  /**
   * Sends a multipart body, keeping the CSRF header and the session cookie.
   *
   * Separate from `put` because that one stringifies its body, which would turn a FormData into
   * the literal text "[object FormData]" and upload nothing. Uses fetch rather than the
   * XMLHttpRequest the import upload needs: that one exists for progress events on files that
   * reach a gigabyte, and a device photograph is capped at 512 KB.
   */
  upload: <T>(path: string, form: FormData, method: 'POST' | 'PUT' = 'PUT', signal?: AbortSignal) =>
    request<T>(path, { method, body: form, signal }),

  put: <T>(path: string, body: unknown, signal?: AbortSignal) =>
    request<T>(path, { method: 'PUT', body: JSON.stringify(body), signal }),

  delete: <T>(path: string, signal?: AbortSignal) =>
    request<T>(path, { method: 'DELETE', signal }),

  /**
   * POSTs a JSON body and returns the file the server answers with.
   *
   * A POST, not a link: what is exported is described by a query that can hold identifiers, and
   * a download URL would carry them into access logs and history.
   */
  download: async (path: string, body: unknown, signal?: AbortSignal): Promise<DownloadedFile> => {
    const response = await send(
      path,
      { method: 'POST', body: JSON.stringify(body), signal },
      'text/csv, application/problem+json',
    )
    const disposition = response.headers.get('Content-Disposition') ?? ''
    const match = /filename\*=UTF-8''([^;]+)|filename="?([^";]+)"?/i.exec(disposition)
    const filename = match ? decodeURIComponent(match[1] ?? match[2] ?? '') || null : null
    return { blob: await response.blob(), filename }
  },
}

/** Hands a file to the browser's own download, then lets go of it. */
export function saveFile(file: DownloadedFile, fallbackName: string): void {
  const url = URL.createObjectURL(file.blob)
  const link = document.createElement('a')
  link.href = url
  link.download = file.filename ?? fallbackName
  document.body.appendChild(link)
  link.click()
  link.remove()
  // Revoked on the next tick: revoking synchronously can cancel the download in some browsers.
  setTimeout(() => URL.revokeObjectURL(url), 0)
}
