import { request } from './httpClient'
import type { AuthTokens, MemberProfile } from '../types/apiResponses'

// 登入／註冊／換發不透過會觸發 401 自動換發攔截的 request 版本呼叫（見設計文件決策 5），
// 直接用 httpClient 的基礎 request()，避免「換發本身失敗」被誤判成「需要再換發一次」。

export function register(
  email: string,
  password: string,
  displayName: string,
  captchaToken: string,
  captchaAnswer: string,
): Promise<{ id: string }> {
  return request('/auth/register', {
    method: 'POST',
    skipAuth: true,
    body: { email, password, displayName, captchaToken, captchaAnswer },
  })
}

export function login(email: string, password: string, captchaToken: string, captchaAnswer: string): Promise<AuthTokens> {
  return request('/auth/login', {
    method: 'POST',
    skipAuth: true,
    body: { email, password, captchaToken, captchaAnswer },
  })
}

export function refresh(refreshToken: string): Promise<AuthTokens> {
  return request('/auth/refresh', {
    method: 'POST',
    skipAuth: true,
    body: { refreshToken },
  })
}

// 後端 logout 端點需要 [Authorize]；呼叫端已先清空 store，所以由呼叫端帶入清空前的 access token。
export function logout(refreshToken: string, accessToken: string | null): Promise<void> {
  return request('/auth/logout', {
    method: 'POST',
    accessToken: accessToken ?? undefined,
    body: { refreshToken },
  })
}

export function getMyProfile(): Promise<MemberProfile> {
  return request('/members/me')
}
