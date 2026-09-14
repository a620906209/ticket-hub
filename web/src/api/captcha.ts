import { request } from './httpClient'

export interface CaptchaChallenge {
  token: string
  imageBase64: string
}

export function getCaptcha(): Promise<CaptchaChallenge> {
  return request('/captcha', { skipAuth: true })
}
