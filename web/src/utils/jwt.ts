// 純粹解碼 JWT payload 供前端讀取非機敏 claim（例如 OrganizerId）使用，不驗證簽章——
// 簽章驗證是後端的責任，前端只是拿claim 值來決定要不要呼叫 GET /api/organizers/mine 比對名稱
// （見 organizer-management design.md tasks.md 5.3）。

export function decodeJwtPayload<T>(token: string): T | null {
  const parts = token.split('.')
  if (parts.length !== 3) {
    return null
  }

  try {
    const base64 = parts[1].replace(/-/g, '+').replace(/_/g, '/')
    const json = decodeURIComponent(
      atob(base64)
        .split('')
        .map((c) => '%' + c.charCodeAt(0).toString(16).padStart(2, '0'))
        .join(''),
    )
    return JSON.parse(json) as T
  } catch {
    return null
  }
}
