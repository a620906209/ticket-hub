import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import App from './App.vue'
import router from './router/index'
import { useAuthStore } from './stores/auth'

function fakeAccessToken(payload: Record<string, unknown>): string {
  return `header.${btoa(JSON.stringify(payload))}.signature`
}

// 401 換發失敗只清空 store、不自動導頁；App.vue 的 watcher 負責把停在受保護頁面的使用者導回登入頁。
// 後台頁面只標 requiresOrganizerContext（order-report-redemption-organizer-scoping 起含訂單、核銷、銷售報表），
// watcher 必須同樣認得，否則登入失效後使用者會停在失效頁面、後續 API 一直 401。
// 頁面改為延遲載入後，第一次導航到某頁要先轉換並載入該頁（含 Element Plus），整套測試並行時可能超過預設 5 秒
describe('App 登入失效時導回登入頁', { timeout: 30_000 }, () => {
  beforeEach(async () => {
    setActivePinia(createPinia())
    await router.push('/')
  })

  it.each([
    '/admin/venues',
    '/admin/orders',
    '/admin/orders/order-1',
    '/admin/redeem',
    '/admin/events/event-1/sales-report',
    '/admin/organizers',
  ])('停在 %s 時登入失效，導向登入頁並帶 redirect', async (path) => {
    const authStore = useAuthStore()
    authStore.accessToken = fakeAccessToken({ sub: '1', OrganizerId: 'org-1' })
    authStore.member = { id: '1', email: 'a@example.com', displayName: 'A', role: 'Admin', isActive: true, hasRegisteredRealName: false, realName: null, nationalIdLast4Masked: null }
    await router.push(path)
    expect(router.currentRoute.value.path).toBe(path)
    mount(App, { global: { plugins: [ElementPlus, router], stubs: { RouterView: true } } })

    authStore.accessToken = null
    await flushPromises()

    // 頁面改為延遲載入後，導向 login 須先載入 LoginPage chunk，flushPromises 不保證導航已完成
    await vi.waitFor(
      () => {
        expect(router.currentRoute.value.name).toBe('login')
        expect(router.currentRoute.value.query.redirect).toBe(path)
      },
      { timeout: 20_000 },
    )
  })
})
