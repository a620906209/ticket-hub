import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it } from 'vitest'
import router from './index'
import { useAuthStore } from '../stores/auth'

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

  it('一般會員進入 /admin/* 導向買家端首頁', async () => {
    const authStore = useAuthStore()
    authStore.accessToken = 'access-token'
    authStore.member = { id: '1', email: 'a@example.com', displayName: 'A', role: 'Member', isActive: true }

    await router.push('/admin/venues')

    expect(router.currentRoute.value.name).toBe('events')
  })

  it('Admin 登入後可進入後台', async () => {
    const authStore = useAuthStore()
    authStore.accessToken = 'access-token'
    authStore.member = { id: '1', email: 'a@example.com', displayName: 'A', role: 'Admin', isActive: true }

    await router.push('/admin/venues')

    expect(router.currentRoute.value.name).toBe('admin-venues')
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
