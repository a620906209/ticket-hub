import { describe, expect, it } from 'vitest'
import morandiCss from './morandi.css?raw'
import styleCss from '../style.css?raw'
import { parseCssBlocks, type CssBlock } from './cssBlocks'

type Rgb = [number, number, number]

const SEMANTIC_COLORS = ['primary', 'success', 'warning', 'danger', 'info'] as const

function findBlock(blocks: CssBlock[], selector: string): CssBlock {
  const block = blocks.find((candidate) => candidate.selector === selector)
  if (!block) throw new Error(`找不到 CSS 區塊：${selector}`)
  return block
}

// 缺少 token 或循環引用一律拋錯：若回傳空值，後續比較可能因 NaN 而假通過
function resolveToken(name: string, scopes: Map<string, string>[], visiting: Set<string> = new Set()): string {
  if (visiting.has(name)) throw new Error(`var() 循環引用：${[...visiting, name].join(' → ')}`)
  const raw = scopes.find((scope) => scope.has(name))?.get(name)
  if (raw === undefined) throw new Error(`找不到 token：${name}`)
  const reference = raw.match(/^var\(\s*(--[\w-]+)\s*\)$/)
  if (!reference) return raw.toLowerCase()
  return resolveToken(reference[1], scopes, new Set([...visiting, name]))
}

function parseHex(value: string): Rgb {
  const match = value.match(/^#([0-9a-f]{3}|[0-9a-f]{6})$/i)
  if (!match) throw new Error(`不是十六進位色碼：${value}`)
  const digits = match[1].length === 3 ? [...match[1]].map((digit) => digit + digit).join('') : match[1]
  return [0, 2, 4].map((start) => parseInt(digits.slice(start, start + 2), 16)) as Rgb
}

function toLinear(channel: number): number {
  const srgb = channel / 255
  return srgb <= 0.04045 ? srgb / 12.92 : ((srgb + 0.055) / 1.055) ** 2.4
}

function getRelativeLuminance(hex: string): number {
  const [red, green, blue] = parseHex(hex).map(toLinear)
  return 0.2126 * red + 0.7152 * green + 0.0722 * blue
}

function getContrastRatio(foreground: string, background: string): number {
  const [lighter, darker] = [getRelativeLuminance(foreground), getRelativeLuminance(background)].sort((a, b) => b - a)
  return (lighter + 0.05) / (darker + 0.05)
}

function convertHexToOklch(hex: string): { lightness: number; chroma: number; hue: number } {
  const [red, green, blue] = parseHex(hex).map(toLinear)
  const long = Math.cbrt(0.4122214708 * red + 0.5363325363 * green + 0.0514459929 * blue)
  const medium = Math.cbrt(0.2119034982 * red + 0.6806995451 * green + 0.1073969566 * blue)
  const short = Math.cbrt(0.0883024619 * red + 0.2817188376 * green + 0.6299787005 * blue)
  const lightness = 0.2104542553 * long + 0.793617785 * medium - 0.0040720468 * short
  const a = 1.9779984951 * long - 2.428592205 * medium + 0.4505937099 * short
  const b = 0.0259040371 * long + 0.7827717662 * medium - 0.808675766 * short
  const hue = (Math.atan2(b, a) * 180) / Math.PI
  return { lightness, chroma: Math.hypot(a, b), hue: hue < 0 ? hue + 360 : hue }
}

function getHueDifference(first: number, second: number): number {
  const difference = Math.abs(first - second) % 360
  return difference > 180 ? 360 - difference : difference
}

const blocks = parseCssBlocks(morandiCss)
const rootScope = findBlock(blocks, ':root').properties
const resolveRootToken = (name: string) => resolveToken(name, [rootScope])
const resolveButtonToken = (selector: string, name: string) =>
  resolveToken(name, [findBlock(blocks, selector).properties, rootScope])

const white = resolveRootToken('--el-bg-color')
const pageBackgrounds = [resolveRootToken('--el-bg-color-page'), resolveRootToken('--color-bg')]
const whiteAndPageBackgrounds = [white, ...pageBackgrounds]

describe('對比度輔助函式', () => {
  it('以 WCAG 參考值核對對比度公式', () => {
    expect(getContrastRatio('#000000', '#ffffff')).toBeCloseTo(21, 2)
    expect(Math.abs(getContrastRatio('#767676', '#ffffff') - 4.54)).toBeLessThanOrEqual(0.01)
    expect(getContrastRatio('#8c9a9e', '#8c9a9e')).toBeCloseTo(1, 2)
  })

  // WCC-009 的彩度斷言是單向的（不得高於原色），公式若算得偏小也會通過，所以明度與彩度也要對參考值
  it('以參考值核對 OKLCH 明度、彩度與色相', () => {
    const red = convertHexToOklch('#ff0000')
    expect(Math.abs(red.hue - 29.2)).toBeLessThanOrEqual(0.5)
    expect(red.lightness).toBeCloseTo(0.628, 3)
    expect(red.chroma).toBeCloseTo(0.258, 3)

    const blue = convertHexToOklch('#0000ff')
    expect(Math.abs(blue.hue - 264.05)).toBeLessThanOrEqual(0.5)
    expect(blue.lightness).toBeCloseTo(0.452, 3)
    expect(blue.chroma).toBeCloseTo(0.313, 3)
  })

  it('var() 指向不存在的 token 時拋出錯誤', () => {
    expect(() => resolveToken('--a', [new Map([['--a', 'var(--missing)']])])).toThrow('找不到 token')
  })

  it('var() 循環引用時拋出錯誤', () => {
    const scope = new Map([
      ['--a', 'var(--b)'],
      ['--b', 'var(--a)'],
    ])
    expect(() => resolveToken('--a', [scope])).toThrow('循環引用')
  })
})

describe('morandi.css 色票對比度', () => {
  it.each(SEMANTIC_COLORS)('WCC-001 實心語意色按鈕三種狀態的白字（%s）', (color) => {
    const selector = `.el-button--${color}`
    const base = resolveRootToken(`--el-color-${color}`)
    const hoverBackground = resolveButtonToken(selector, '--el-button-hover-bg-color')
    const activeBackground = resolveButtonToken(selector, '--el-button-active-bg-color')

    for (const background of [base, hoverBackground, activeBackground]) {
      expect(getContrastRatio('#ffffff', background)).toBeGreaterThanOrEqual(4.5)
    }
    expect(getRelativeLuminance(hoverBackground)).toBeLessThan(getRelativeLuminance(base))
    expect(getRelativeLuminance(activeBackground)).toBeLessThan(getRelativeLuminance(hoverBackground))
    expect(resolveButtonToken(selector, '--el-button-hover-border-color')).toBe(hoverBackground)
    expect(resolveButtonToken(selector, '--el-button-active-border-color')).toBe(activeBackground)
  })

  it.each(SEMANTIC_COLORS)('WCC-002 狀態標籤與提示訊息文字（%s）', (color) => {
    const base = resolveRootToken(`--el-color-${color}`)
    const tagBackground = resolveRootToken(`--el-color-${color}-light-9`)

    expect(getContrastRatio(base, tagBackground)).toBeGreaterThanOrEqual(4.5)
  })

  it.each(['--color-primary', '--el-color-primary'])('WCC-003 連結與主色文字（%s）', (token) => {
    for (const background of whiteAndPageBackgrounds) {
      expect(getContrastRatio(resolveRootToken(token), background)).toBeGreaterThanOrEqual(4.5)
    }
  })

  it.each([
    '--el-text-color-primary',
    '--el-text-color-regular',
    '--el-text-color-secondary',
    '--color-text',
    '--color-text-secondary',
  ])('WCC-004 一般文字與次要文字（%s）', (token) => {
    for (const background of whiteAndPageBackgrounds) {
      expect(getContrastRatio(resolveRootToken(token), background)).toBeGreaterThanOrEqual(4.5)
    }
  })

  it('WCC-005 Placeholder 對白底', () => {
    expect(getContrastRatio(resolveRootToken('--el-text-color-placeholder'), white)).toBeGreaterThanOrEqual(4.5)
  })

  it.each(['', '-light-3', '-light-5', '-light-7', '-light-8', '-light-9', '-dark-2'])(
    'WCC-006 error 系列維持為 danger 的別名（%s）',
    (suffix) => {
      expect(resolveRootToken(`--el-color-error${suffix}`)).toBe(resolveRootToken(`--el-color-danger${suffix}`))
    },
  )

  it('WCC-007 輸入框邊框與 hover 邊框', () => {
    const border = resolveRootToken('--el-border-color')
    const hoverBorder = resolveRootToken('--el-border-color-hover')

    for (const background of whiteAndPageBackgrounds) {
      expect(getContrastRatio(border, background)).toBeGreaterThanOrEqual(3)
      expect(getContrastRatio(hoverBorder, background)).toBeGreaterThanOrEqual(3)
    }
    expect(getRelativeLuminance(hoverBorder)).toBeLessThan(getRelativeLuminance(border))
  })

  it.each(['.el-button', ...SEMANTIC_COLORS.map((color) => `.el-button--${color}`)])(
    'WCC-008 按鈕焦點框（%s）',
    (selector) => {
      const outline = resolveButtonToken(selector, '--el-button-outline-color')

      for (const background of whiteAndPageBackgrounds) {
        expect(getContrastRatio(outline, background)).toBeGreaterThanOrEqual(3)
      }
    },
  )

  // 兩者權重相同，後出現者勝出；.el-button 若排在後面，會把語意色按鈕的焦點框蓋成 primary
  it('WCC-008 .el-button 區塊排在所有語意色按鈕區塊之前', () => {
    const defaultButtonOffset = findBlock(blocks, '.el-button').offset
    for (const color of SEMANTIC_COLORS) {
      expect(defaultButtonOffset).toBeLessThan(findBlock(blocks, `.el-button--${color}`).offset)
    }
  })

  // 調整前的原色寫死在此：若從 CSS 讀取，原色被改掉時比較基準也會跟著跑掉
  const originalColors: Record<(typeof SEMANTIC_COLORS)[number], string> = {
    primary: '#8c9a9e',
    success: '#96a87f',
    warning: '#c9a66c',
    danger: '#b97c6d',
    info: '#a39c93',
  }

  it.each(SEMANTIC_COLORS)('WCC-009 調整後的語意色色相與彩度（%s）', (color) => {
    const original = convertHexToOklch(originalColors[color])
    const adjusted = convertHexToOklch(resolveRootToken(`--el-color-${color}`))

    expect(getHueDifference(adjusted.hue, original.hue)).toBeLessThanOrEqual(5)
    expect(adjusted.chroma).toBeLessThanOrEqual(original.chroma + 0.01)
  })

  it('WCC-010 根元素配色宣告為 light', () => {
    const styleRoot = findBlock(parseCssBlocks(styleCss), ':root').properties

    expect(styleRoot.get('color-scheme')).toBe('light')
  })

  it.each(['--el-color-primary', '--el-color-danger', '--el-text-color-regular'])(
    'WCC-011 text 按鈕文字對 hover 與 active 背景（%s）',
    (token) => {
      const hoverBackground = resolveRootToken('--el-fill-color-light')
      // morandi.css 未覆寫 --el-fill-color 時，text 按鈕 active 背景是 Element Plus 2.14.4 的預設值
      // （element-plus/theme-chalk/src/common/var.scss 的 fill-color base）
      const activeBackground = rootScope.has('--el-fill-color') ? resolveRootToken('--el-fill-color') : '#f0f2f5'

      for (const background of [hoverBackground, activeBackground]) {
        expect(getContrastRatio(resolveRootToken(token), background)).toBeGreaterThanOrEqual(4.5)
      }
    },
  )

  it('WCC-012 同義 token 同值、裝飾性邊框不變', () => {
    expect(resolveRootToken('--color-primary')).toBe(resolveRootToken('--el-color-primary'))
    expect(resolveRootToken('--color-text-secondary')).toBe(resolveRootToken('--el-text-color-secondary'))
    expect(resolveRootToken('--el-border-color-light')).toBe('#e5dfd5')
    expect(resolveRootToken('--el-border-color-lighter')).toBe('#ede8df')
  })
})
