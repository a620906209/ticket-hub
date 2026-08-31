import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import AdminLayout from './AdminLayout.vue'
import router from '../router/index'
import { useAuthStore } from '../stores/auth'

function mountLayout() {
  return mount(AdminLayout, {
    global: { plugins: [ElementPlus, router] },
  })
}

describe('AdminLayout', () => {
  beforeEach(async () => {
    setActivePinia(createPinia())
    const authStore = useAuthStore()
    authStore.accessToken = 'access-token'
    authStore.member = { id: '1', email: 'admin@example.com', displayName: 'Admin', role: 'Admin', isActive: true }
    await router.push('/admin/venues')
  })

  // 對應 AC: ADMIN-REDEEM-NAV-ENTRY
  it('導覽選單渲染出「票券核銷」項目，且連結指向 /admin/redeem', async () => {
    const wrapper = mountLayout()

    const menuItem = wrapper.findAll('.el-menu-item').find((item) => item.text() === '票券核銷')
    expect(menuItem).toBeDefined()

    await menuItem!.trigger('click')
    await flushPromises()

    expect(router.currentRoute.value.path).toBe('/admin/redeem')
  })
})
