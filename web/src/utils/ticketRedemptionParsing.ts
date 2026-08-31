// 掃描內容與手動輸入的解析（design.md 決策 5）：僅為避免明顯無效內容浪費一次 API 呼叫的
// 前端檢查，不構成安全邊界；後端 ITicketSigningService.TryVerify 才是唯一可信的驗證依據。

const GUID_PATTERN = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/

function isValidGuidFormat(value: string): boolean {
  return GUID_PATTERN.test(value)
}

export type QrContentParseResult =
  | { recognized: true; ticketId: string; signature: string }
  | { recognized: false }

// 恰好一個 `.` 分隔符、前段為合法 GUID、後段（簽章）非空，三者皆符合才回傳可辨識結果。
export function parseTicketIdFromQrContent(content: string): QrContentParseResult {
  const parts = content.split('.')
  if (parts.length !== 2) {
    return { recognized: false }
  }

  const [ticketId, signature] = parts
  if (!isValidGuidFormat(ticketId) || signature.length === 0) {
    return { recognized: false }
  }

  return { recognized: true, ticketId, signature }
}

export type ManualInputParseResult = { valid: true; ticketId: string } | { valid: false }

// 只接受單一合法 GUID（trim 前後空白），不接受 `.` 分隔符或附加內容。
export function parseTicketIdFromManualInput(value: string): ManualInputParseResult {
  const trimmed = value.trim()
  if (!isValidGuidFormat(trimmed)) {
    return { valid: false }
  }

  return { valid: true, ticketId: trimmed }
}
