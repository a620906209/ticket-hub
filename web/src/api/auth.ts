import { request } from './httpClient'
import type { AuthTokens, MemberProfile } from '../types/apiResponses'

// 登入／註冊／換發不透過會觸發 401 自動換發攔截的 request 版本呼叫（見設計文件決策 5），
// 直接用 httpClient 的基礎 request()，避免「換發本身失敗」被誤判成「需要再換發一次」。

export function register(email: string, password: string, displayName: string): Promise<{ id: string }> {
  return request('/auth/register', {
    method: 'POST',
    skipAuth: true,
    body: { email, password, displayName },
  })
}

export function login(email: string, password: string): Promise<AuthTokens> {
  return request('/auth/login', {
    method: 'POST',
    skipAuth: true,
    body: { email, password },
  })
}

export function refresh(refreshToken: string): Promise<AuthTokens> {
  return request('/auth/refresh', {
    method: 'POST',
    skipAuth: true,
    body: { refreshToken },
  })
}

export function logout(refreshToken: string): Promise<void> {
  return request('/auth/logout', {
    method: 'POST',
    body: { refreshToken },
  })
}

export function getMyProfile(): Promise<MemberProfile> {
  return request('/members/me')
}
