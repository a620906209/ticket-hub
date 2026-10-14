import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import router from '../router/index'
import { useAuthStore } from '../stores/auth'
import AreaSwitcher from './AreaSwitcher.vue'
import type { MemberProfile } from '../types/apiResponses'

type Area = 'buyer' | 'organizer' | 'platform'

let wrapper: VueWrapper | null = null

// isAuthenticated 需要 accessToken 與 member 兩者（比照 BuyerLayout.test.ts）
function signIn(role: MemberProfile['role'], email = 'user@example.com'): void {
  const authStore = useAuthStore()
  authStore.accessToken = 'access-token'
  authStore.member = { id: '1', email, displayName: 'A', role, isActive: true,
    hasRegisteredRealName: false, realName: null, nationalIdLast4Masked: null }
}

function findPoppers(): HTMLElement[] {
  return Array.from(document.body.querySelectorAll<HTMLElement>('.area-switcher-popper'))
}

// el-dropdown 的選單 Teleport 到 body 且以 setTimeout 開啟，flushPromises 等不到；
// 先斷言開啟前是隱藏的，否則「開啟後看得到」無法證明是點擊造成的
async function mountAndOpen(current: Area): Promise<HTMLElement> {
  wrapper = mount(AreaSwitcher, { props: { current }, global: { plugins: [ElementPlus, router] }, attachTo: document.body })
  expect(findPoppers()).toHaveLength(1)
  expect(findPoppers()[0].style.display).toBe('none')

  await wrapper.find('.area-switcher-trigger').trigger('click')
  await vi.waitFor(() => {
    expect(findPoppers()).toHaveLength(1)
    expect(findPoppers()[0].style.display).not.toBe('none')
  })
  return findPoppers()[0]
}

function readItems(popper: HTMLElement): HTMLElement[] {
  return Array.from(popper.querySelectorAll<HTMLElement>('.el-dropdown-menu__item'))
}

function readItemLabels(popper: HTMLElement): string[] {
  return readItems(popper).map((item) => item.textContent?.trim() ?? '')
}

function isItemDisabled(item: HTMLElement): boolean {
  return item.classList.contains('is-disabled') && item.getAttribute('aria-disabled') === 'true'
}

describe('AreaSwitcher 區域切換選單', { timeout: 30_000 }, () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  // Teleport 的 popper 掛在 document.body，不 unmount 會殘留並讓下一支測試的命中數變成 2
  afterEach(() => {
    wrapper?.unmount()
    wrapper = null
    vi.restoreAllMocks()
  })

  it('[AWU-AREA-SWITCH-001] Admin 在主辦方後台展開選單，依序列出四項且帳號與目前區域停用', async () => {
    signIn('Admin', 'admin@example.com')

    const popper = await mountAndOpen('organizer')

    expect(wrapper!.find('.area-switcher').exists()).toBe(true)
    expect(wrapper!.find('.area-switcher .area-switcher-trigger').text()).toContain('主辦方後台')
    expect(readItemLabels(popper)).toEqual(['登入帳號：admin@example.com', '會員前台', '主辦方後台（目前）', '平台管理'])
    expect(readItems(popper).map(isItemDisabled)).toEqual([true, false, true, false])
    // 未停用的項目不得帶 aria-disabled="true"，否則輔助技術會把可點的項目讀成停用
    for (const item of readItems(popper).filter((_, index) => index === 1 || index === 3)) {
      expect(item.classList.contains('is-disabled')).toBe(false)
      expect(item.getAttribute('aria-disabled')).not.toBe('true')
    }
  })

  // 停用只看樣式不夠：點了仍跳頁會讓「（目前）」重新載入目前區域，帳號列也不該是入口（design.md 決策 2 第 1、5 點）
  it.each(['登入帳號：admin@example.com', '主辦方後台（目前）'])('[AWU-AREA-SWITCH-001] 點選停用的「%s」不導覽', async (label) => {
    signIn('Admin', 'admin@example.com')
    const push = vi.spyOn(router, 'push').mockResolvedValue(undefined)

    const popper = await mountAndOpen('organizer')
    const items = readItems(popper).filter((candidate) => candidate.textContent?.trim() === label)
    expect(items).toHaveLength(1)
    items[0].click()

    expect(push).not.toHaveBeenCalled()
  })

  // 只斷言「找不到平台管理」時，選單整個沒渲染也會通過，所以斷言完整清單
  it('[AWU-AREA-SWITCH-002] 非 Admin 在會員前台展開選單，恰為帳號、會員前台、主辦方後台三項', async () => {
    signIn('Member')

    const popper = await mountAndOpen('buyer')

    expect(readItemLabels(popper)).toEqual(['登入帳號：user@example.com', '會員前台（目前）', '主辦方後台'])
  })

  // 最終路徑（含守衛導向）由各版面測試以真實 router 驗證，這裡只驗證項目與目的地的對應
  it.each([
    ['平台管理', '/platform', 'organizer'],
    ['主辦方後台', '/admin', 'platform'],
    ['會員前台', '/', 'platform'],
  ] as [string, string, Area][])('[AWU-AREA-SWITCH-003] 點選「%s」前往 %s', async (label, path, current) => {
    signIn('Admin')
    const push = vi.spyOn(router, 'push').mockResolvedValue(undefined)

    const popper = await mountAndOpen(current)
    const item = readItems(popper).find((candidate) => candidate.textContent?.trim() === label)
    expect(item).toBeDefined()
    item!.click()

    expect(push).toHaveBeenCalledTimes(1)
    expect(push).toHaveBeenCalledWith(path)
  })

  it('[AWU-AREA-SWITCH-005] Email 含 HTML 標籤時以文字顯示，不產生元素', async () => {
    signIn('Member', '<b>x</b>@example.com')

    const popper = await mountAndOpen('buyer')

    const accountItems = popper.querySelectorAll('.area-switcher-account')
    expect(accountItems).toHaveLength(1)
    expect(accountItems[0].textContent).toContain('<b>x</b>@example.com')
    expect(popper.querySelectorAll('b')).toHaveLength(0)
  })

  // 窄螢幕何時顯示縮寫由 CSS media query 決定，jsdom 不套用，改由瀏覽器量測驗證（tasks.md 4.4）；
  // 這裡驗證縮寫元素存在與否，以及縮寫後仍有完整名稱可讀
  it('[AWU-AREA-SWITCH-006] 會員前台觸發按鈕帶完整名稱的 aria-label 與 title，並有「前台」縮寫', () => {
    signIn('Member')
    wrapper = mount(AreaSwitcher, { props: { current: 'buyer' }, global: { plugins: [ElementPlus, router] } })

    const trigger = wrapper.find('.area-switcher-trigger')
    expect(trigger.attributes('aria-label')).toBe('會員前台')
    expect(trigger.attributes('title')).toBe('會員前台')
    expect(trigger.find('.area-switcher-label').text()).toBe('會員前台')
    const shortLabels = trigger.findAll('.area-switcher-label-short')
    expect(shortLabels).toHaveLength(1)
    expect(shortLabels[0].text()).toBe('前台')
    // 有 aria-label 時縮寫不應再被讀一次
    expect(shortLabels[0].attributes('aria-hidden')).toBe('true')
  })

  it.each(['organizer', 'platform'] as Area[])('[AWU-AREA-SWITCH-006] %s 的觸發按鈕不縮寫', (current) => {
    signIn('Admin')
    wrapper = mount(AreaSwitcher, { props: { current }, global: { plugins: [ElementPlus, router] } })

    const trigger = wrapper.find('.area-switcher-trigger')
    expect(trigger.find('.area-switcher-label').exists()).toBe(true)
    expect(trigger.findAll('.area-switcher-label-short')).toHaveLength(0)
  })
})
