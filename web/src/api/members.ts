import { authorizedRequest } from './httpClient'
import type { MemberProfile } from '../types/apiResponses'

// 與 authApi.getMyProfile 不同：這裡給頁面使用，走 authorizedRequest 以享有 401 自動換發；
// authApi 版本供 auth store 還原登入狀態用，刻意不掛換發攔截（見 api/auth.ts 開頭說明）。
export function getMyProfile(): Promise<MemberProfile> {
  return authorizedRequest('/members/me')
}

export function registerRealName(realName: string, nationalIdLast4: string): Promise<MemberProfile> {
  return authorizedRequest('/members/me/real-name', {
    method: 'PUT',
    body: { realName, nationalIdLast4 },
  })
}
