export interface ProblemDetails {
  status?: number
  title?: string
  detail?: string
  [key: string]: unknown
}

export class ApiError extends Error {
  readonly status: number
  readonly problem: ProblemDetails | null

  constructor(status: number, problem: ProblemDetails | null) {
    super(problem?.detail ?? problem?.title ?? `API 請求失敗（狀態碼 ${status}）`)
    this.name = 'ApiError'
    this.status = status
    this.problem = problem
  }
}

type AccessTokenGetter = () => string | null

// 用注入的方式取得目前的 access token，避免 httpClient 直接 import Pinia auth store
// 造成 stores/auth.ts → api/auth.ts → httpClient.ts → stores/auth.ts 的循環相依。
let getAccessToken: AccessTokenGetter = () => null

export function configureHttpClientAuth(getter: AccessTokenGetter): void {
  getAccessToken = getter
}

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE'
  body?: unknown
  signal?: AbortSignal
  /** 略過 Authorization Header（登入/註冊等不需要既有 token 的請求） */
  skipAuth?: boolean
  /** 內部旗標：這個請求是不是「401 換發後的重試」，呼叫端不要自己設定，見 authorizedRequest() */
  _retriedAfterRefresh?: boolean
}

async function parseProblemDetails(response: Response): Promise<ProblemDetails | null> {
  const contentType = response.headers.get('content-type') ?? ''
  if (!contentType.includes('json')) {
    return null
  }
  try {
    return (await response.json()) as ProblemDetails
  } catch {
    return null
  }
}

export async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' }

  if (!options.skipAuth) {
    const token = getAccessToken()
    if (token) {
      headers.Authorization = `Bearer ${token}`
    }
  }

  let body: string | undefined
  if (options.body !== undefined) {
    headers['Content-Type'] = 'application/json'
    body = JSON.stringify(options.body)
  }

  const response = await fetch(`/api${path}`, {
    method: options.method ?? 'GET',
    headers,
    body,
    signal: options.signal,
  })

  if (!response.ok) {
    throw new ApiError(response.status, await parseProblemDetails(response))
  }

  if (response.status === 204) {
    return undefined as T
  }

  const contentType = response.headers.get('content-type') ?? ''
  if (!contentType.includes('json')) {
    return undefined as T
  }
  return (await response.json()) as T
}

type RefreshHandler = () => Promise<boolean>

// 同樣用注入的方式取得「怎麼換發」，避免 httpClient 直接 import Pinia auth store（見上方 configureHttpClientAuth）。
let refreshSession: RefreshHandler = () => Promise.resolve(false)

export function configureHttpClientRefresh(handler: RefreshHandler): void {
  refreshSession = handler
}

// single-flight：同一時間只允許一個換發請求在進行中，後到的呼叫共用同一個 Promise（見設計文件決策 5）。
let inFlightRefresh: Promise<boolean> | null = null

function refreshOnce(): Promise<boolean> {
  if (!inFlightRefresh) {
    inFlightRefresh = refreshSession().finally(() => {
      inFlightRefresh = null
    })
  }
  return inFlightRefresh
}

/**
 * 一般業務 API 呼叫要用這個，不要直接用 request()。收到 401 時嘗試換發一次並重放原請求；
 * 換發本身呼叫的是 refreshSession()（內部用 authApi.refresh，走 request() 不掛這個攔截器，
 * 不會遞迴）。每個請求最多重試一次，重試後仍 401 就直接把錯誤丟出去，不再觸發第二次換發
 * （見設計文件決策 5 的防遞迴與重試邊界）。
 */
export async function authorizedRequest<T>(path: string, options: RequestOptions = {}): Promise<T> {
  try {
    return await request<T>(path, options)
  } catch (error) {
    if (error instanceof ApiError && error.status === 401 && !options._retriedAfterRefresh) {
      const refreshed = await refreshOnce()
      if (refreshed) {
        return authorizedRequest<T>(path, { ...options, _retriedAfterRefresh: true })
      }
    }
    throw error
  }
}
