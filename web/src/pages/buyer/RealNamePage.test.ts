import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import RealNamePage from './RealNamePage.vue'
import * as membersApi from '../../api/members'
import { ApiError } from '../../api/httpClient'
import type { MemberProfile } from '../../types/apiResponses'

vi.mock('../../api/members')

const pushMock = vi.fn()
const routeQuery: Record<string, string> = {}
vi.mock('vue-router', () => ({
  useRouter: () => ({ push: pushMock }),
  useRoute: () => ({ query: routeQuery }),
}))

const unregisteredProfile: MemberProfile = {
  id: 'member-1',
  email: 'buyer@example.com',
  displayName: '買家',
  role: 'Member',
  isActive: true,
  hasRegisteredRealName: false,
  realName: null,
  nationalIdLast4Masked: null,
}

function registeredProfile(realName = '王小明'): MemberProfile {
  return { ...unregisteredProfile, hasRegisteredRealName: true, realName, nationalIdLast4Masked: '**34' }
}

let wrapper: VueWrapper | null = null

async function mountPage(): Promise<VueWrapper> {
  // 掛到 document 上，document.activeElement 才會反映對話框的預設焦點。
  wrapper = mount(RealNamePage, { global: { plugins: [ElementPlus] }, attachTo: document.body })
  await flushPromises()
  return wrapper
}

async function fillAndSubmit(page: VueWrapper, realName = '王小明', last4 = '1234'): Promise<void> {
  await page.find('input[name="realName"]').setValue(realName)
  await page.find('input[name="nationalIdLast4"]').setValue(last4)
  await page.find('form').trigger('submit')
  await flushPromises()
}

async function confirmRegistration(page: VueWrapper): Promise<void> {
  await page.find('[data-testid="confirm-register"]').trigger('click')
  await flushPromises()
}

describe('RealNamePage 實名資料頁', () => {
  beforeEach(() => {
    pushMock.mockReset()
    for (const key of Object.keys(routeQuery)) delete routeQuery[key]
    vi.mocked(membersApi.getMyProfile).mockReset().mockResolvedValue(unregisteredProfile)
    vi.mocked(membersApi.registerRealName).mockReset().mockResolvedValue(registeredProfile())
  })

  afterEach(() => {
    wrapper?.unmount()
    wrapper = null
    document.body.innerHTML = ''
  })

  it('[BW-RN-PAGE-001] 未登記時顯示姓名、末四碼輸入欄位與不可修改提示', async () => {
    const page = await mountPage()

    expect(page.find('input[name="realName"]').exists()).toBe(true)
    expect(page.find('input[name="nationalIdLast4"]').exists()).toBe(true)
    expect(page.text()).toContain('登記後無法自行修改')
  })

  it('[BW-RN-PAGE-002] 送出後先顯示確認對話框列出輸入值，焦點在返回修改，尚未呼叫登記 API', async () => {
    const page = await mountPage()

    await fillAndSubmit(page)

    const dialog = page.find('[role="dialog"]')
    expect(dialog.exists()).toBe(true)
    expect(dialog.text()).toContain('王小明')
    expect(dialog.text()).toContain('1234')
    expect(document.activeElement).toBe(page.find('[data-testid="confirm-return"]').element)
    expect(membersApi.registerRealName).toHaveBeenCalledTimes(0)
  })

  it('[BW-RN-PAGE-003] 選返回修改不呼叫登記 API，輸入保留', async () => {
    const page = await mountPage()
    await fillAndSubmit(page)

    await page.find('[data-testid="confirm-return"]').trigger('click')

    expect(page.find('[role="dialog"]').exists()).toBe(false)
    expect(membersApi.registerRealName).toHaveBeenCalledTimes(0)
    expect((page.find('input[name="realName"]').element as HTMLInputElement).value).toBe('王小明')
    expect((page.find('input[name="nationalIdLast4"]').element as HTMLInputElement).value).toBe('1234')
  })

  it('[BW-RN-PAGE-003] 以 Esc 關閉對話框不呼叫登記 API，輸入保留', async () => {
    const page = await mountPage()
    await fillAndSubmit(page)

    await page.find('[data-testid="confirm-return"]').trigger('keydown', { key: 'Escape' })

    expect(page.find('[role="dialog"]').exists()).toBe(false)
    expect(membersApi.registerRealName).toHaveBeenCalledTimes(0)
    expect((page.find('input[name="realName"]').element as HTMLInputElement).value).toBe('王小明')
    expect((page.find('input[name="nationalIdLast4"]').element as HTMLInputElement).value).toBe('1234')
  })

  it('末四碼不是 4 位數字時不開啟確認對話框，也不呼叫登記 API', async () => {
    const page = await mountPage()

    await fillAndSubmit(page, '王小明', '12a4')

    expect(page.find('[role="dialog"]').exists()).toBe(false)
    expect(page.text()).toContain('身分證末四碼必須為 4 位數字')
    expect(membersApi.registerRealName).toHaveBeenCalledTimes(0)
  })

  // RNV-FORMAT-002：與後端同樣以 Unicode 字元計數，擴充 B 區罕用字（UTF-16 佔兩個單位）50 字仍可送出。
  it.each([
    [50, true],
    [51, false],
  ])('姓名為 %i 個擴充 B 區罕用字時，確認對話框開啟與否為 %s', async (characterCount, isDialogOpen) => {
    const page = await mountPage()
    expect(page.find('input[name="realName"]').attributes('maxlength')).toBeUndefined()

    await fillAndSubmit(page, '\u{20000}'.repeat(characterCount))

    expect(page.find('[role="dialog"]').exists()).toBe(isDialogOpen)
    expect(page.text().includes('真實姓名不得超過 50 字')).toBe(!isDialogOpen)
    expect(membersApi.registerRealName).toHaveBeenCalledTimes(0)
  })

  // RNV-FORMAT-003：長度以去除前後空白後計算，50 字姓名前後多打空白仍可送出。
  it('姓名 50 字且前後有空白時仍開啟確認對話框', async () => {
    const page = await mountPage()

    await fillAndSubmit(page, `  ${'王'.repeat(50)}  `)

    expect(page.find('[role="dialog"]').exists()).toBe(true)
  })

  it.each([
    ['半形空白', '   '],
    ['全形空白 U+3000', '\u3000\u3000'],
  ])('[BW-RN-PAGE-011] 姓名只有%s時顯示必填提示，不開啟確認對話框', async (_label, realName) => {
    const page = await mountPage()

    await fillAndSubmit(page, realName)

    expect(page.find('[role="dialog"]').exists()).toBe(false)
    expect(page.text()).toContain('請輸入真實姓名')
    expect(membersApi.registerRealName).toHaveBeenCalledTimes(0)
  })

  // RNV-FORMAT-003：後端儲存去除前後空白後的值，確認對話框與送出值須與之一致，看到的就是登記的。
  it('[BW-RN-PAGE-012] 姓名前後有全形空白時，確認對話框顯示與送出的都是去除後的值', async () => {
    const page = await mountPage()

    await fillAndSubmit(page, '\u3000王小明\u3000')
    expect(page.find('[data-testid="confirm-real-name"]').element.textContent).toBe('王小明')
    await confirmRegistration(page)

    expect(membersApi.registerRealName).toHaveBeenCalledWith('王小明', '1234')
  })

  // 確認對話框是自製遮罩，焦點仍可回到底下的輸入欄位：對話框開著時改動輸入不得繞過送出前的檢查。
  it('[BW-RN-PAGE-012] 對話框開啟後改動輸入欄位，確認登記仍送出開啟當下通過檢查的值', async () => {
    const page = await mountPage()
    await fillAndSubmit(page)

    await page.find('input[name="realName"]').setValue('')
    await page.find('input[name="nationalIdLast4"]').setValue('12')
    await confirmRegistration(page)

    expect(membersApi.registerRealName).toHaveBeenCalledWith('王小明', '1234')
  })

  it('[BW-RN-PAGE-011] 對話框開著時把姓名改成空白再送出表單，關閉對話框並顯示必填提示', async () => {
    const page = await mountPage()
    await fillAndSubmit(page)

    await page.find('input[name="realName"]').setValue('   ')
    await page.find('form').trigger('submit')
    await flushPromises()

    expect(page.find('[role="dialog"]').exists()).toBe(false)
    expect(page.text()).toContain('請輸入真實姓名')
    expect(membersApi.registerRealName).toHaveBeenCalledTimes(0)
  })

  // RNV-FORMAT-007／RNV-FORMAT-009：看不見的字元會讓核銷面板的姓名無從比對證件，送出前就攔下。
  it.each([
    ['零寬空格 U+200B', '王\u200B小明'],
    ['方向覆寫 U+202E', '王\u202E小明'],
    ['分行符號 U+2028', '王\u2028小明'],
    ['分段符號 U+2029', '王\u2029小明'],
    ['韓文填充字 U+3164', '\u3164'],
    ['空白點字 U+2800', '王\u2800小明'],
    ['半形韓文填充字 U+FFA0', '\uFFA0'],
    ['韓文首填充字 U+115F', '\u115F'],
    ['韓文中填充字 U+1160', '王\u1160小明'],
  ])('姓名含%s時不開啟確認對話框，也不呼叫登記 API', async (_label, realName) => {
    const page = await mountPage()

    await fillAndSubmit(page, realName)

    expect(page.find('[role="dialog"]').exists()).toBe(false)
    expect(page.text()).toContain('真實姓名不得包含控制字元或不可見的字元')
    expect(membersApi.registerRealName).toHaveBeenCalledTimes(0)
  })

  // RNV-FORMAT-010：只由組合字元或數字組成的姓名沒有可比對的文字。
  it.each([
    ['組合字元 U+034F', '\u034F'],
    ['異體字選擇符 U+FE0F', '\uFE0F\uFE0F'],
    ['純數字', '123'],
    ['蒙古文自由變體選擇符 U+180B', '\u180B'],
    ['標點', '．'],
  ])('姓名只有%s時不開啟確認對話框，也不呼叫登記 API', async (_label, realName) => {
    const page = await mountPage()

    await fillAndSubmit(page, realName)

    expect(page.find('[role="dialog"]').exists()).toBe(false)
    expect(page.text()).toContain('真實姓名至少須包含一個文字')
    expect(membersApi.registerRealName).toHaveBeenCalledTimes(0)
  })

  it('[BW-RN-PAGE-004] 確認登記成功後顯示成功提示並改為唯讀遮蔽顯示', async () => {
    const page = await mountPage()
    await fillAndSubmit(page)

    await confirmRegistration(page)

    expect(membersApi.registerRealName).toHaveBeenCalledWith('王小明', '1234')
    expect(document.body.textContent).toContain('實名登記成功')
    expect(page.find('[data-testid="registered-real-name"]').text()).toBe('王小明')
    expect(page.find('[data-testid="registered-national-id-last4"]').text()).toBe('**34')
    expect(page.find('input').exists()).toBe(false)
    expect(pushMock).not.toHaveBeenCalled()
  })

  it('[BW-RN-PAGE-005] 已登記時唯讀顯示，沒有輸入欄位或修改按鈕', async () => {
    vi.mocked(membersApi.getMyProfile).mockResolvedValue(registeredProfile())
    const page = await mountPage()

    expect(page.find('[data-testid="registered-real-name"]').text()).toBe('王小明')
    expect(page.find('[data-testid="registered-national-id-last4"]').text()).toBe('**34')
    expect(page.find('input').exists()).toBe(false)
    expect(page.find('button').exists()).toBe(false)
  })

  it('[BW-RN-PAGE-006] 登記回 409 時重新查詢個人資料，顯示已登記狀態與已登記過實名', async () => {
    vi.mocked(membersApi.registerRealName).mockRejectedValue(new ApiError(409, { status: 409, title: 'Conflict' }))
    const page = await mountPage()
    await fillAndSubmit(page)
    vi.mocked(membersApi.getMyProfile).mockResolvedValue(registeredProfile('另一分頁登記的姓名'))

    await confirmRegistration(page)

    expect(membersApi.getMyProfile).toHaveBeenCalledTimes(2)
    expect(page.text()).toContain('已登記過實名')
    expect(page.find('[data-testid="registered-real-name"]').text()).toBe('另一分頁登記的姓名')
    expect(page.find('input').exists()).toBe(false)
  })

  it('登記回 400 時顯示格式錯誤並保留輸入內容', async () => {
    vi.mocked(membersApi.registerRealName).mockRejectedValue(new ApiError(400, { status: 400, title: 'Validation' }))
    const page = await mountPage()
    await fillAndSubmit(page)

    await confirmRegistration(page)

    expect(page.text()).toContain('實名資料格式不正確')
    expect((page.find('input[name="realName"]').element as HTMLInputElement).value).toBe('王小明')
    expect((page.find('input[name="nationalIdLast4"]').element as HTMLInputElement).value).toBe('1234')
  })

  it('[BW-RN-PAGE-007] redirect 為站內路徑時登記成功後導回該路徑', async () => {
    routeQuery.redirect = '/events/event-1'
    const page = await mountPage()
    await fillAndSubmit(page)

    await confirmRegistration(page)

    expect(pushMock).toHaveBeenCalledWith('/events/event-1')
  })

  it('[BW-RN-PAGE-008] redirect 為 protocol-relative 網址時登記成功後不導向，留在本頁顯示已登記', async () => {
    routeQuery.redirect = '//evil.example.com'
    const page = await mountPage()
    await fillAndSubmit(page)

    await confirmRegistration(page)

    expect(pushMock).not.toHaveBeenCalled()
    expect(page.find('[data-testid="registered-real-name"]').text()).toBe('王小明')
  })

  it('[BW-RN-PAGE-010] 姓名含 HTML 標籤時以文字顯示，不產生 img 元素', async () => {
    const maliciousName = '<img src=x onerror=alert(1)>'
    vi.mocked(membersApi.getMyProfile).mockResolvedValue(registeredProfile(maliciousName))
    const page = await mountPage()

    expect(page.find('[data-testid="registered-real-name"]').text()).toBe(maliciousName)
    expect(page.find('img').exists()).toBe(false)
  })
})
