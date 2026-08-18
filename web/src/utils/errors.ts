import { ApiError } from '../api/httpClient'

export function toErrorMessage(error: unknown, fallback = '發生錯誤，請稍後再試'): string {
  if (error instanceof ApiError) {
    return error.message
  }
  return fallback
}
