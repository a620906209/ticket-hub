import { describe, expect, it } from 'vitest'
import indexHtml from '../index.html?raw'

describe('index.html', () => {
  // 螢幕閱讀器與瀏覽器翻譯依 lang 判斷內容語言；宣告錯誤會用英文語音唸繁體中文介面
  it('[BW-LANG-001] 根元素的 lang 宣告為 zh-Hant-TW', () => {
    const document = new DOMParser().parseFromString(indexHtml, 'text/html')

    expect(document.documentElement.lang).toBe('zh-Hant-TW')
  })
})
