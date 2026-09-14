import { ref } from 'vue'
import { getCaptcha } from '../api/captcha'

const LOAD_ERROR_MESSAGE = '系統暫時無法取得驗證碼，請稍後再試'

// 註冊頁、登入頁、加入排隊操作三處共用同一套「顯示圖片、輸入文字、送出時附帶 token」互動模式
// （captcha-verification design.md 決策 7）。初次載入與刷新失敗共用同一套處理邏輯，不特別區分
// 429／5xx 的文字，呼叫端一律顯示同一句提示，不拋出未捕捉例外中斷呼叫端。
export function useCaptcha() {
  const token = ref('')
  const imageBase64 = ref('')
  const loadError = ref('')

  async function refresh(): Promise<void> {
    try {
      const challenge = await getCaptcha()
      token.value = challenge.token
      imageBase64.value = challenge.imageBase64
      loadError.value = ''
    } catch {
      token.value = ''
      imageBase64.value = ''
      loadError.value = LOAD_ERROR_MESSAGE
    }
  }

  return { token, imageBase64, loadError, refresh }
}
