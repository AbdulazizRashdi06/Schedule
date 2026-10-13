/** Errors the server sends as { error: "…" } are meant to be shown as-is. */
export class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const init: RequestInit = { method, credentials: 'same-origin', headers: {} }
  if (body instanceof FormData) init.body = body
  else if (body !== undefined) {
    init.body = JSON.stringify(body)
    ;(init.headers as Record<string, string>)['Content-Type'] = 'application/json'
  }
  let res: Response
  try {
    res = await fetch('/api' + url, init)
  } catch {
    throw new ApiError(0, "Can't reach the server. Check your connection.")
  }
  if (res.status === 204) return undefined as T
  const text = await res.text()
  let data: unknown = undefined
  try {
    data = text ? JSON.parse(text) : undefined
  } catch {
    /* not JSON */
  }
  if (!res.ok) {
    const msg = (data as { error?: string } | undefined)?.error
    if (res.status === 401 && !msg) throw new ApiError(401, 'Please sign in again.')
    if (res.status === 403 && !msg) throw new ApiError(403, "You don't have access to that.")
    throw new ApiError(res.status, msg ?? 'Something went wrong. Try again.')
  }
  return data as T
}

export const api = {
  get: <T>(url: string) => request<T>('GET', url),
  post: <T>(url: string, body?: unknown) => request<T>('POST', url, body ?? {}),
  put: <T>(url: string, body?: unknown) => request<T>('PUT', url, body ?? {}),
  del: <T>(url: string) => request<T>('DELETE', url),
  form: <T>(url: string, form: FormData) => request<T>('POST', url, form),
}

export const errorText = (e: unknown) => (e instanceof Error ? e.message : 'Something went wrong.')

/** localStorage that never throws (private mode, blocked storage). */
export const store = {
  get(key: string): string | null {
    try {
      return localStorage.getItem(key)
    } catch {
      return null
    }
  },
  set(key: string, value: string) {
    try {
      localStorage.setItem(key, value)
    } catch {
      /* ignore */
    }
  },
}
