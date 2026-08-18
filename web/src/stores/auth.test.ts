import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuthStore } from './auth'
import { ApiError } from '../api/httpClient'
import * as authApi from '../api/auth'

vi.mock('../api/auth')

const REFRESH_TOKEN_STORAGE_KEY = 'ticketing.refreshToken'

const member = { id: 'm1', email: 'a@example.com', displayName: 'A', role: 'Member', isActive: true }
const adminMember = { ...member, role: 'Admin' }

describe('auth store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    localStorage.clear()
    vi.mocked(authApi.login).mockReset()
    vi.mocked(authApi.refresh).mockReset()
    vi.mocked(authApi.logout).mockReset()
    vi.mocked(authApi.getMyProfile).mockReset()
  })

  afterEach(() => {
    localStorage.clear()
  })

  it('登入成功寫入 accessToken 與 member', async () => {
    vi.mocked(authApi.login).mockResolvedValue({ accessToken: 'access-1', refreshToken: 'refresh-1' })
    vi.mocked(authApi.getMyProfile).mockResolvedValue(member)

    const store = useAuthStore()
    await store.login('a@example.com', 'password123')

    expect(store.accessToken).toBe('access-1')
    expect(store.member).toEqual(member)
    expect(store.isAuthenticated).toBe(true)
    expect(localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBe('refresh-1')
  })

  it('登出清空 accessToken／member／localStorage 的 refresh token', async () => {
    vi.mocked(authApi.login).mockResolvedValue({ accessToken: 'access-1', refreshToken: 'refresh-1' })
    vi.mocked(authApi.getMyProfile).mockResolvedValue(member)
    vi.mocked(authApi.logout).mockResolvedValue(undefined)

    const store = useAuthStore()
    await store.login('a@example.com', 'password123')
    await store.logout()

    expect(store.accessToken).toBeNull()
    expect(store.member).toBeNull()
    expect(store.isAuthenticated).toBe(false)
    expect(localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBeNull()
  })

  it('refreshSession 成功更新 access token', async () => {
    localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, 'old-refresh')
    vi.mocked(authApi.refresh).mockResolvedValue({ accessToken: 'access-2', refreshToken: 'refresh-2' })
    vi.mocked(authApi.getMyProfile).mockResolvedValue(member)

    const store = useAuthStore()
    const result = await store.refreshSession()

    expect(result).toBe(true)
    expect(store.accessToken).toBe('access-2')
    expect(localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBe('refresh-2')
  })

  it('refreshSession 失敗清空登入狀態', async () => {
    localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, 'expired-refresh')
    vi.mocked(authApi.refresh).mockRejectedValue(new ApiError(401, { status: 401, detail: 'invalid' }))

    const store = useAuthStore()
    const result = await store.refreshSession()

    expect(result).toBe(false)
    expect(store.isAuthenticated).toBe(false)
    expect(localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBeNull()
  })

  it('bootstrapAsync 沒有 refreshToken 時直接視為未登入', async () => {
    const store = useAuthStore()
    await store.bootstrapAsync()

    expect(store.isAuthenticated).toBe(false)
    expect(store.bootstrapError).toBe(false)
    expect(authApi.refresh).not.toHaveBeenCalled()
  })

  it('bootstrapAsync 有 refreshToken 且換發成功時寫入 member', async () => {
    localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, 'old-refresh')
    vi.mocked(authApi.refresh).mockResolvedValue({ accessToken: 'access-3', refreshToken: 'refresh-3' })
    vi.mocked(authApi.getMyProfile).mockResolvedValue(adminMember)

    const store = useAuthStore()
    await store.bootstrapAsync()

    expect(store.isAuthenticated).toBe(true)
    expect(store.isAdmin).toBe(true)
    expect(store.bootstrapError).toBe(false)
  })

  it('bootstrapAsync 遇到 401（refresh token 失效）清空 localStorage 且視為未登入', async () => {
    localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, 'expired-refresh')
    vi.mocked(authApi.refresh).mockRejectedValue(new ApiError(401, { status: 401, detail: 'invalid' }))

    const store = useAuthStore()
    await store.bootstrapAsync()

    expect(store.isAuthenticated).toBe(false)
    expect(store.bootstrapError).toBe(false)
    expect(localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBeNull()
  })

  it('bootstrapAsync 遇到網路錯誤時保留 localStorage 的 refreshToken 並標記 bootstrapError', async () => {
    localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, 'still-valid-refresh')
    vi.mocked(authApi.refresh).mockRejectedValue(new TypeError('Failed to fetch'))

    const store = useAuthStore()
    await expect(store.bootstrapAsync()).resolves.toBeUndefined()

    expect(store.isAuthenticated).toBe(false)
    expect(store.bootstrapError).toBe(true)
    expect(localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBe('still-valid-refresh')
  })
})
