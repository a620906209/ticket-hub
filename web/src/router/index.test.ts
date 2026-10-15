import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it } from 'vitest'
import type { RouteLocationNormalized } from 'vue-router'
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

  // 2026-10-13 走查打錯網址時整頁空白，沒有任何導覽可以離開。
  it('push_WhenPathUnknown_RedirectsToEventList', async () => {
    // 先離開 events，否則導覽失敗留在原地也會通過斷言
    await router.push('/login')
    await router.push('/no-such-page/deep')

    expect(router.currentRoute.value.name).toBe('events')
  })

  // 換頁沒回頁首時，從長列表點進詳情會停在頁面中段；上一頁則要還原原本的位置。
  it('scrollBehavior_WithOrWithoutSavedPosition_RestoresSavedOrScrollsToTop', () => {
    const scrollBehavior = router.options.scrollBehavior!
    const route = router.currentRoute.value

    expect(scrollBehavior(route, route, null)).toEqual({ top: 0 })
    expect(scrollBehavior(route, route, { left: 0, top: 480 })).toEqual({ left: 0, top: 480 })
  })

  // [BW-NAV-STEP-001] 進出確認步驟只換 step query；若照常捲到頁首或還原 savedPosition，
  // 會與元件把焦點移到標題／「下一步」的捲動互相競爭（order-confirmation-page design.md 決策 6）。
  it('[BW-NAV-STEP-001] scrollBehavior_WhenOnlyStepQueryChanges_DoesNotScroll', () => {
    const scrollBehavior = router.options.scrollBehavior!
    // scrollBehavior 只讀 path、query、hash，resolve 的結果已含這些欄位
    const at = (path: string) => router.resolve(path) as RouteLocationNormalized
    const selection = at('/events/event-1?zone=A')
    const confirm = at('/events/event-1?zone=A&step=confirm')
    const saved = { left: 0, top: 480 }

    expect(scrollBehavior(confirm, selection, null)).toBe(false)
    expect(scrollBehavior(confirm, selection, saved)).toBe(false)
    expect(scrollBehavior(selection, confirm, null)).toBe(false)
    expect(scrollBehavior(selection, confirm, saved)).toBe(false)
    // 其他 query 的 key 順序不同仍視為只切換 step
    const reordered = at('/events/event-1?step=confirm&page=2&zone=A')
    expect(scrollBehavior(reordered, at('/events/event-1?zone=A&page=2'), null)).toBe(false)

    // 反例：路徑、其他 query 或 hash 不同時，仍是一般換頁
    const otherPath = at('/events/event-2?zone=A&step=confirm')
    const otherQuery = at('/events/event-1?zone=B&step=confirm')
    const otherHash = at('/events/event-1?zone=A&step=confirm#seats')
    for (const to of [otherPath, otherQuery, otherHash]) {
      expect(scrollBehavior(to, selection, null)).toEqual({ top: 0 })
      expect(scrollBehavior(to, selection, saved)).toEqual(saved)
    }
  })

  // 已寄出的通知或書籤可能還留著舊的結果頁網址，結果頁移除後要導到同一筆訂單的明細頁。
  it('[BW-RESULT-REDIRECT-001] push_OldOrderResultUrlWhenSignedIn_RedirectsToOrderDetail', async () => {
    signIn('Member')

    await router.push('/order-result/3f2504e0-4f89-11d3-9a0c-0305e82c3301')

    expect(router.currentRoute.value.name).toBe('order-detail')
    expect(router.currentRoute.value.params.id).toBe('3f2504e0-4f89-11d3-9a0c-0305e82c3301')
  })

  // 導向後的目的地仍經 requiresAuth 守衛；登入後要回到明細頁，而不是已不存在的結果頁。
  it('[BW-RESULT-REDIRECT-002] push_OldOrderResultUrlWhenSignedOut_RedirectsToLoginWithOrderDetailRedirect', async () => {
    await router.push('/order-result/order-1')

    expect(router.currentRoute.value.name).toBe('login')
    expect(router.currentRoute.value.query.redirect).toBe('/orders/order-1')
  })

  // 導向時不檢查 Id 格式、不查訂單；找不到或格式錯誤交給明細頁既有的提示處理。
  it.each(['00000000-0000-0000-0000-000000000000', 'not-a-guid'])(
    '[BW-RESULT-REDIRECT-003] push_OldOrderResultUrlWithUnknownOrNonGuidId_RedirectsToOrderDetailWithSameId：%s',
    async (orderId) => {
      signIn('Member')

      await router.push(`/order-result/${orderId}`)

      expect(router.currentRoute.value.name).toBe('order-detail')
      expect(router.currentRoute.value.params.id).toBe(orderId)
    },
  )

  it('未登入進入需登入頁面導向登入頁', async () => {
    await router.push('/orders')

    expect(router.currentRoute.value.name).toBe('login')
    expect(router.currentRoute.value.query.redirect).toBe('/orders')
  })

  // AWU-GUARD-004：非 Admin 角色不論是否已切換 Organizer，開啟審核頁面都導向買家端首頁。
  // 舊網址 redirect 到新路由後必須同樣經過守衛，否則書籤成為繞過 requiresAdmin 的入口；
  // 已切換 Organizer 的組合證明 requiresOrganizerContext 的放行沒有連帶放行審核頁。
  it.each([
    ['/platform/organizers', undefined],
    ['/platform/organizers', 'org-1'],
    ['/admin/organizers', undefined],
    ['/admin/organizers', 'org-1'],
  ] as [string, string | undefined][])('[AWU-GUARD-004] 一般會員開啟 %s（OrganizerId=%s）導向買家端首頁', async (path, organizerId) => {
    signIn('Member', organizerId)
    // 先離開 events，否則導覽失敗留在原地也會通過斷言
    await router.push('/login')

    await router.push(path)

    expect(router.currentRoute.value.name).toBe('events')
  })

  // AWU-GUARD-005：Admin 角色不需要已切換 Organizer 即可進入審核頁面。
  it('[AWU-GUARD-005] Admin 登入後不需切換 Organizer 即可進入審核頁面', async () => {
    signIn('Admin')

    await router.push('/platform/organizers')

    expect(router.currentRoute.value.name).toBe('platform-organizers')
  })

  // AWU-GUARD-008：舊網址與書籤仍要能到審核頁；/platform 根路徑沒有自己的內容。
  it.each(['/admin/organizers', '/platform'])('[AWU-GUARD-008] Admin 未切換 Organizer 開啟 %s 最終停在 /platform/organizers', async (path) => {
    signIn('Admin')

    await router.push(path)

    expect(router.currentRoute.value.path).toBe('/platform/organizers')
    expect(router.currentRoute.value.name).toBe('platform-organizers')
  })

  it('[AWU-GUARD-001] 未登入直接開啟平台管理審核頁導向登入頁', async () => {
    await router.push('/platform/organizers')

    expect(router.currentRoute.value.name).toBe('login')
    expect(router.currentRoute.value.query.redirect).toBe('/platform/organizers')
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
