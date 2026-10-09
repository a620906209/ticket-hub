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

type Role = 'Member' | 'Admin'

function signIn(role: Role, organizerId?: string): void {
  const authStore = useAuthStore()
  authStore.accessToken = fakeAccessToken(organizerId ? { sub: '1', OrganizerId: organizerId } : { sub: '1' })
  authStore.member = { id: '1', email: 'a@example.com', displayName: 'A', role, isActive: true, hasRegisteredRealName: false, realName: null, nationalIdLast4Masked: null }
}

// 頁面改為延遲載入後，第一次導航到某頁要先轉換並載入該頁（含 Element Plus），整套測試並行時可能超過預設 5 秒
describe('router guard', { timeout: 30_000 }, () => {
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

  // AWU-GUARD-004：非 Admin 角色不論是否已切換 Organizer，開啟審核頁面都導向買家端首頁。
  it('[AWU-GUARD-004] 一般會員開啟 /admin/organizers 導向買家端首頁', async () => {
    signIn('Member')

    await router.push('/admin/organizers')

    expect(router.currentRoute.value.name).toBe('events')
  })

  // 本次讓「已切換的非 Admin」可進入其他後台頁，必須證明審核頁沒有跟著被放行。
  it('[AWU-GUARD-004] 已切換 Organizer 的一般會員開啟 /admin/organizers 仍導向買家端首頁', async () => {
    signIn('Member', 'org-1')

    await router.push('/admin/organizers')

    expect(router.currentRoute.value.name).toBe('events')
  })

  // AWU-GUARD-005：Admin 角色不需要已切換 Organizer 即可進入審核頁面。
  it('[AWU-GUARD-005] Admin 登入後不需切換 Organizer 即可進入審核頁面', async () => {
    signIn('Admin')

    await router.push('/admin/organizers')

    expect(router.currentRoute.value.name).toBe('admin-organizers')
  })

  // AWU-GUARD-002：已登入但 Access Token 未帶 OrganizerId claim（含單純 Admin 角色未切換）開啟一般後台頁面，
  // 導向「選擇主辦方」頁面，不是買家端首頁。
  it('[AWU-GUARD-002] 已登入但未切換 Organizer 的使用者開啟場館頁面導向選擇主辦方頁面', async () => {
    signIn('Member')

    await router.push('/admin/venues')

    expect(router.currentRoute.value.name).toBe('my-organizers')
  })

  it('[AWU-GUARD-002] 單純 Admin 角色但未切換 Organizer 開啟活動頁面導向選擇主辦方頁面', async () => {
    signIn('Admin')

    await router.push('/admin/events')

    expect(router.currentRoute.value.name).toBe('my-organizers')
  })

  // 訂單、核銷、銷售報表本次起也要求已切換 Organizer；同時驗 Admin，證明 Admin 角色不再能繞過切換
  // （本次變更前這三頁是 Admin 直接放行）。
  it.each([
    ['Member', '/admin/orders'],
    ['Admin', '/admin/orders'],
    ['Member', '/admin/orders/order-1'],
    ['Admin', '/admin/orders/order-1'],
    ['Member', '/admin/redeem'],
    ['Admin', '/admin/redeem'],
    ['Member', '/admin/events/event-1/sales-report'],
    ['Admin', '/admin/events/event-1/sales-report'],
  ] as [Role, string][])('[AWU-GUARD-002] %s 未切換 Organizer 開啟 %s 導向選擇主辦方頁面', async (role, path) => {
    signIn(role)

    await router.push(path)

    expect(router.currentRoute.value.name).toBe('my-organizers')
  })

  // AWU-GUARD-003：已切換至一個 Organizer（Access Token 帶 OrganizerId claim）後可進入活動或場館頁面。
  it('[AWU-GUARD-003] 已切換 Organizer 後可進入場館與活動頁面', async () => {
    signIn('Member', 'org-1')

    await router.push('/admin/venues')
    expect(router.currentRoute.value.name).toBe('admin-venues')

    await router.push('/admin/events')
    expect(router.currentRoute.value.name).toBe('admin-events')
  })

  // AWU-GUARD-007：非 Admin 的 Organizer 成員切換後可進入訂單、核銷與銷售報表頁面（本次變更前會被導回買家端首頁）。
  it('[AWU-GUARD-007] 已切換 Organizer、角色非 Admin 的使用者可進入訂單、核銷與銷售報表頁面', async () => {
    signIn('Member', 'org-1')

    await router.push('/admin/orders')
    expect(router.currentRoute.value.name).toBe('admin-orders')

    await router.push('/admin/orders/order-1')
    expect(router.currentRoute.value.name).toBe('admin-order-detail')

    await router.push('/admin/redeem')
    expect(router.currentRoute.value.name).toBe('admin-redeem')

    await router.push('/admin/events/event-1/sales-report')
    expect(router.currentRoute.value.name).toBe('admin-sales-report')
  })

  it('[AWU-GUARD-003] Admin 切換 Organizer 後可進入核銷頁面', async () => {
    signIn('Admin', 'org-1')

    await router.push('/admin/redeem')

    expect(router.currentRoute.value.name).toBe('admin-redeem')
  })

  it('[AWU-GUARD-001] 未登入直接開啟後台路由導向登入頁，不顯示後台內容', async () => {
    await router.push('/admin/venues')

    expect(router.currentRoute.value.name).toBe('login')
  })

  // 對應 redemption-scanner-ui：/admin/redeem 沿用既有 /admin/* 共用守衛，不需另外的守衛邏輯
  it('[AWU-GUARD-001] 未登入直接開啟核銷頁面導向登入頁', async () => {
    await router.push('/admin/redeem')

    expect(router.currentRoute.value.name).toBe('login')
    expect(router.currentRoute.value.query.redirect).toBe('/admin/redeem')
  })
})
