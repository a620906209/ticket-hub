import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import * as authApi from '../api/auth'
import { ApiError, configureHttpClientAuth, configureHttpClientRefresh } from '../api/httpClient'
import type { MemberProfile } from '../types/apiResponses'

const REFRESH_TOKEN_STORAGE_KEY = 'ticketing.refreshToken'

function getStoredRefreshToken(): string | null {
  return localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)
}

function storeRefreshToken(token: string): void {
  localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, token)
}

function clearStoredRefreshToken(): void {
  localStorage.removeItem(REFRESH_TOKEN_STORAGE_KEY)
}

export const useAuthStore = defineStore('auth', () => {
  const accessToken = ref<string | null>(null)
  const member = ref<MemberProfile | null>(null)
  // bootstrap 遇到網路錯誤/5xx 等非預期錯誤時設為 true，代表「還不確定是否登入」而非「確定未登入」（見設計文件決策 5）。
  const bootstrapError = ref(false)

  const isAuthenticated = computed(() => accessToken.value !== null && member.value !== null)
  const isAdmin = computed(() => member.value?.role === 'Admin')

  // httpClient 不直接 import 這個 store（避免循環相依），改由 store 建立時把「怎麼拿目前 token」
  // 與「怎麼換發」注入進去；refreshSession 是下面的函式宣告，會被提升（hoisting），這裡先呼叫沒問題。
  configureHttpClientAuth(() => accessToken.value)
  configureHttpClientRefresh(refreshSession)

  function clearSession(): void {
    accessToken.value = null
    member.value = null
  }

  async function login(email: string, password: string): Promise<void> {
    const tokens = await authApi.login(email, password)
    accessToken.value = tokens.accessToken
    storeRefreshToken(tokens.refreshToken)
    member.value = await authApi.getMyProfile()
  }

  async function logout(): Promise<void> {
    const refreshToken = getStoredRefreshToken()
    clearSession()
    clearStoredRefreshToken()
    if (refreshToken) {
      try {
        await authApi.logout(refreshToken)
      } catch {
        // best-effort：本地登入狀態已經清空，後端登出呼叫失敗不阻擋導向登入頁（見設計文件決策 5）。
      }
    }
  }

  /**
   * 給 httpClient 401 攔截器（single-flight 換發）使用：任何失敗都視為登入失效，
   * 清空狀態並回傳 false。與 bootstrapAsync() 分開實作，因為 bootstrap 需要區分
   * 預期／非預期錯誤（見 bootstrapAsync 內的邏輯與設計文件決策 5），這裡不需要。
   */
  async function refreshSession(): Promise<boolean> {
    const refreshToken = getStoredRefreshToken()
    if (!refreshToken) {
      clearSession()
      return false
    }

    try {
      const tokens = await authApi.refresh(refreshToken)
      accessToken.value = tokens.accessToken
      storeRefreshToken(tokens.refreshToken)
      member.value = await authApi.getMyProfile()
      return true
    } catch {
      clearSession()
      clearStoredRefreshToken()
      return false
    }
  }

  /**
   * App 啟動時呼叫一次，決定畫面初始的登入狀態。區分「預期錯誤」（refresh token 確定失效）
   * 與「非預期錯誤」（網路問題，還不確定登入狀態）——見設計文件決策 5。不拋出例外、不導頁。
   */
  async function bootstrapAsync(): Promise<void> {
    bootstrapError.value = false
    const refreshToken = getStoredRefreshToken()
    if (!refreshToken) {
      clearSession()
      return
    }

    try {
      const tokens = await authApi.refresh(refreshToken)
      accessToken.value = tokens.accessToken
      storeRefreshToken(tokens.refreshToken)
      member.value = await authApi.getMyProfile()
    } catch (error) {
      if (error instanceof ApiError && (error.status === 401 || error.status === 404)) {
        // 預期錯誤：refresh token 已失效，或 /members/me 查不到會員資料，視為未登入。
        clearSession()
        clearStoredRefreshToken()
      } else {
        // 非預期錯誤（網路中斷、逾時、5xx）：保留 refresh token，可能只是這次請求失敗。
        clearSession()
        bootstrapError.value = true
      }
    }
  }

  return {
    accessToken,
    member,
    bootstrapError,
    isAuthenticated,
    isAdmin,
    login,
    logout,
    refreshSession,
    bootstrapAsync,
  }
})
