import { describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import LoginPage from './LoginPage.vue'
import { ApiError } from '../../api/httpClient'

const pushMock = vi.fn()
vi.mock('vue-router', () => ({
  useRoute: () => ({ query: {} }),
  useRouter: () => ({ push: pushMock }),
}))

const loginMock = vi.fn()
vi.mock('../../stores/auth', () => ({
  useAuthStore: () => ({
    login: loginMock,
    get isAdmin() {
      return false
    },
  }),
}))

function mountPage() {
  return mount(LoginPage, {
    global: { plugins: [ElementPlus], stubs: { RouterLink: true } },
  })
}

async function fillAndSubmit(wrapper: ReturnType<typeof mount>) {
  await wrapper.find('input[type="email"]').setValue('user@example.com')
  await wrapper.find('input[type="password"]').setValue('Password123')
  await wrapper.find('form').trigger('submit')
  await flushPromises()
}

// buyer-web-ui spec LRL-009：登入因請求頻率限制被拒絕時，顯示友善提示訊息，不直接顯示後端原始
// ProblemDetails.title 字串（login-rate-limiting design.md 決策 5）。
describe('LoginPage', () => {
  it('LRL-009：登入因請求頻率限制被拒絕（429）時，顯示友善提示，不顯示後端原始 title 字串', async () => {
    loginMock.mockRejectedValue(new ApiError(429, { status: 429, title: 'TooManyRequests' }))
    const wrapper = mountPage()

    await fillAndSubmit(wrapper)

    expect(wrapper.text()).toContain('登入嘗試過於頻繁，請稍後再試')
    expect(wrapper.text()).not.toContain('TooManyRequests')
  })

  it('登入失敗（非 429）沿用既有一般錯誤處理，不套用 429 的友善提示', async () => {
    loginMock.mockRejectedValue(new ApiError(401, { status: 401, title: 'Unauthorized' }))
    const wrapper = mountPage()

    await fillAndSubmit(wrapper)

    expect(wrapper.text()).not.toContain('登入嘗試過於頻繁，請稍後再試')
  })
})
