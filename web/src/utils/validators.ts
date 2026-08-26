// 集中管理的表單驗證規則（Element Plus rules 格式），對應後端 FluentValidation 規則，
// 提前給使用者清楚的錯誤訊息；後端仍是最終驗證邊界，這裡的規則不取代後端驗證（見設計文件 Security 段落）。

export const emailRules = [
  { required: true, message: '請輸入 Email', trigger: 'blur' },
  { type: 'email' as const, message: 'Email 格式不正確', trigger: 'blur' },
]

// 對應後端 PasswordValidationRules.MustBeStrongPassword()：至少 8 碼、須含英文字母與數字。
export const passwordRules = [
  { required: true, message: '請輸入密碼', trigger: 'blur' },
  { min: 8, message: '密碼長度至少須為 8 碼', trigger: 'blur' },
  { pattern: /[A-Za-z]/, message: '密碼須包含英文字母', trigger: 'blur' },
  { pattern: /[0-9]/, message: '密碼須包含數字', trigger: 'blur' },
]

export function requiredRule(message: string) {
  return { required: true, message, trigger: 'blur' as const }
}

export function maxLengthRule(max: number, message: string) {
  return { max, message, trigger: 'blur' as const }
}

export function positiveNumberRule(message: string) {
  return {
    validator: (_rule: unknown, value: number, callback: (error?: Error) => void) => {
      if (value === null || value === undefined || Number.isNaN(value) || value <= 0) {
        callback(new Error(message))
        return
      }
      callback()
    },
    trigger: 'blur' as const,
  }
}

// 給必填的正整數欄位用（例如純計數票種的可售總量）：未填、非整數或非正數皆視為驗證失敗。
// 必須帶 required: true——async-validator 對純 validator 規則，值為 undefined/空時預設不會呼叫
// validator（除非明確標示 required），這是實測過的既有 gotcha，不能只靠 validator 內判斷 undefined。
export function requiredPositiveIntegerRule(message: string) {
  return {
    required: true,
    validator: (_rule: unknown, value: number | undefined | null, callback: (error?: Error) => void) => {
      if (value === undefined || value === null || !Number.isInteger(value) || value <= 0) {
        callback(new Error(message))
        return
      }
      callback()
    },
    trigger: 'blur' as const,
  }
}

// 給選填的正整數欄位用（例如每筆訂單限購張數）：沒填視為合法（代表不限制），有填才檢查是不是正整數。
export function optionalPositiveIntegerRule(message: string) {
  return {
    validator: (_rule: unknown, value: number | undefined | null, callback: (error?: Error) => void) => {
      if (value === undefined || value === null) {
        callback()
        return
      }
      if (!Number.isInteger(value) || value <= 0) {
        callback(new Error(message))
        return
      }
      callback()
    },
    trigger: 'blur' as const,
  }
}
