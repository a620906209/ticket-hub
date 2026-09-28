import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it } from 'vitest'
import router from './index'
import { useAuthStore } from '../stores/auth'

// 建一個帶指定 payload 的假 JWT 字串（不需要合法簽章，decodeJwtPayload 只解碼不驗證），
// 供測試 authStore.organizerId 這個從 Access Token claim 解碼出來的計算屬性。
function fakeAccessToken(payload: Record<string, unknown>): string {
  const base64 = btoa(JSON.stringify(payload))
  return `header.${base64}.signature`
}

describe('router guard', () => {
  beforeEach(async () => {
    setActivePinia(createPinia())
    // 每個測試前導回中性路徑，避免「目的地跟目前路徑相同」時 vue-router 略過重新導覽、guard 沒有真的重跑。
    await router.push('/')
  })

  it('未登入進入需登入頁面導向登入頁', async () => {
    await router.push('/orders')

    expect(router.currentRoute.value.name).toBe('login')
    expect(router.currentRoute.value.query.redirect).toBe('/orders')
  })

  // AWU-GUARD-004：非 Admin 角色開啟審核頁面導向買家端首頁。
  it('一般會員開啟 /admin/organizers 導向買家端首頁', async () => {
    const authStore = useAuthStore()
    authStore.accessToken = 'access-token'
    authStore.member = { id: '1', email: 'a@example.com', displayName: 'A', role: 'Member', isActive: true }

    await router.push('/admin/organizers')

    expect(router.currentRoute.value.name).toBe('events')
  })

  // AWU-GUARD-005：Admin 角色不需要已切換 Organizer 即可進入審核頁面。
  it('Admin 登入後不需切換 Organizer 即可進入審核頁面', async () => {
    const authStore = useAuthStore()
    authStore.accessToken = 'access-token'
    authStore.member = { id: '1', email: 'a@example.com', displayName: 'A', role: 'Admin', isActive: true }

    await router.push('/admin/organizers')

    expect(router.currentRoute.value.name).toBe('admin-organizers')
  })

  // AWU-GUARD-002：已登入但 Access Token 未帶 OrganizerId claim（含單純 Admin 角色未切換）開啟活動或
  // 場館頁面，導向「選擇主辦方」頁面，不是買家端首頁——這是本次變更前後語意上的關鍵差異。
  it('已登入但未切換 Organizer 的使用者開啟場館頁面導向選擇主辦方頁面', async () => {
    const authStore = useAuthStore()
    authStore.accessToken = fakeAccessToken({ sub: '1' })
    authStore.member = { id: '1', email: 'a@example.com', displayName: 'A', role: 'Member', isActive: true }

    await router.push('/admin/venues')

    expect(router.currentRoute.value.name).toBe('my-organizers')
  })

  it('單純 Admin 角色但未切換 Organizer 開啟活動頁面導向選擇主辦方頁面', async () => {
    const authStore = useAuthStore()
    authStore.accessToken = fakeAccessToken({ sub: '1' })
    authStore.member = { id: '1', email: 'a@example.com', displayName: 'A', role: 'Admin', isActive: true }

    await router.push('/admin/events')

    expect(router.currentRoute.value.name).toBe('my-organizers')
  })

  // AWU-GUARD-003：已切換至一個 Organizer（Access Token 帶 OrganizerId claim）後可進入活動或場館頁面。
  it('已切換 Organizer 後可進入場館與活動頁面', async () => {
    const authStore = useAuthStore()
    authStore.accessToken = fakeAccessToken({ sub: '1', OrganizerId: 'org-1' })
    authStore.member = { id: '1', email: 'a@example.com', displayName: 'A', role: 'Member', isActive: true }

    await router.push('/admin/venues')
    expect(router.currentRoute.value.name).toBe('admin-venues')

    await router.push('/admin/events')
    expect(router.currentRoute.value.name).toBe('admin-events')
  })

  // AWU-GUARD-006：已切換 Organizer 但角色非 Admin 的使用者開啟訂單頁面，仍依既有規則導向買家端首頁
  // （訂單、核銷頁面本次不受影響，回歸測試）。
  it('已切換 Organizer 但非 Admin 的使用者開啟訂單頁面仍導向買家端首頁', async () => {
    const authStore = useAuthStore()
    authStore.accessToken = fakeAccessToken({ sub: '1', OrganizerId: 'org-1' })
    authStore.member = { id: '1', email: 'a@example.com', displayName: 'A', role: 'Member', isActive: true }

    await router.push('/admin/orders')

    expect(router.currentRoute.value.name).toBe('events')
  })

  it('未登入直接開啟後台路由導向登入頁，不顯示後台內容', async () => {
    await router.push('/admin/venues')

    expect(router.currentRoute.value.name).toBe('login')
  })

  // 對應 redemption-scanner-ui：/admin/redeem 沿用既有 /admin/* 共用守衛，不需另外的守衛邏輯
  it('未登入直接開啟核銷頁面導向登入頁', async () => {
    await router.push('/admin/redeem')

    expect(router.currentRoute.value.name).toBe('login')
    expect(router.currentRoute.value.query.redirect).toBe('/admin/redeem')
  })

  it('Admin 登入後可進入核銷頁面', async () => {
    const authStore = useAuthStore()
    authStore.accessToken = 'access-token'
    authStore.member = { id: '1', email: 'a@example.com', displayName: 'A', role: 'Admin', isActive: true }

    await router.push('/admin/redeem')

    expect(router.currentRoute.value.name).toBe('admin-redeem')
  })
})
