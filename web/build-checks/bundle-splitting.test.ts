import { execFile } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { promisify } from 'node:util'
import { afterAll, describe, expect, it } from 'vitest'
import { parseCssBlocks } from '../src/styles/cssBlocks.ts'

// 驗證 web-bundle-splitting spec（BS-001～BS-013）。必須先以 `npm run test:bundle` 產生 dist/ 與 bundle 報告。

type ChunkReport = {
  fileName: string
  isEntry: boolean
  isDynamicEntry: boolean
  facadeModuleId: string | null
  moduleIds: string[]
  imports: string[]
  dynamicImports: string[]
  importedCss: string[]
  gzipBytes: number
}
type BundleReport = { entryFileName: string; chunks: ChunkReport[]; cssAssets: { fileName: string; gzipBytes: number }[] }

const execFileAsync = promisify(execFile)
const webDir = path.resolve(import.meta.dirname, '..')
const srcDir = path.resolve(webDir, 'src')
const distDir = path.resolve(webDir, 'dist')
const defaultReportPath = path.resolve(webDir, 'node_modules/.tmp/bundle-report.json')
const elementPlusEsDir = path.resolve(webDir, 'node_modules/element-plus/es')
const elementPlusComponentsDir = path.resolve(elementPlusEsDir, 'components')

const HOME_PAGE_MODULE = 'src/pages/buyer/EventListPage.vue'
const SCANNER_PAGE_MODULE = 'src/pages/admin/RedemptionScannerPage.vue'
const ADMIN_LAYOUT_MODULE = 'src/layouts/AdminLayout.vue'
const SEMANTIC_COLORS = ['primary', 'success', 'warning', 'danger', 'info'] as const

// 標籤 → Element Plus 含 index.mjs 的元件目錄。option、form-item、table-column 等目錄只有樣式，
// 元件 JS 在父元件目錄內（Element Plus 2.14.4 實測），所以對照到父目錄。
const TAG_TO_COMPONENT_DIR: Record<string, string> = {
  'el-alert': 'alert',
  'el-button': 'button',
  'el-checkbox': 'checkbox',
  'el-date-picker': 'date-picker',
  'el-dropdown': 'dropdown',
  'el-dropdown-item': 'dropdown',
  'el-dropdown-menu': 'dropdown',
  'el-empty': 'empty',
  'el-form': 'form',
  'el-form-item': 'form',
  'el-input': 'input',
  'el-input-number': 'input-number',
  'el-menu': 'menu',
  'el-menu-item': 'menu',
  'el-option': 'select',
  'el-select': 'select',
  'el-skeleton': 'skeleton',
  'el-switch': 'switch',
  'el-table': 'table',
  'el-table-column': 'table',
  'el-tag': 'tag',
  'v-loading': 'loading',
}
const NAMED_IMPORT_TO_COMPONENT_DIR: Record<string, string> = {
  ElMessage: 'message',
  ElMessageBox: 'message-box',
}
// 與使用元件無關的元件；任何一個進入允許集合，代表使用或依賴有變動，須更新 spec
const UNRELATED_COMPONENT_DIRS = ['carousel', 'tree', 'upload', 'color-picker', 'transfer', 'cascader']

// ---------- 報告讀取與 chunk 走訪 ----------

export function loadBundleReport(reportPath: string, distIndexPath: string): BundleReport {
  if (!fs.existsSync(reportPath)) {
    throw new Error(`找不到 bundle 報告 ${reportPath}，請先執行 npm run test:bundle`)
  }
  const report = JSON.parse(fs.readFileSync(reportPath, 'utf8')) as BundleReport
  const indexHtml = fs.existsSync(distIndexPath) ? fs.readFileSync(distIndexPath, 'utf8') : ''
  // 不比 mtime：generateBundle 在寫檔前執行，報告必然不晚於 dist/；entry 檔名含內容雜湊，足以判定同一次 build
  if (!report.entryFileName || !indexHtml.includes(report.entryFileName)) {
    throw new Error(`bundle 報告的 entry ${report.entryFileName} 未出現在 ${distIndexPath}，報告與 dist/ 不是同一次 build，請先執行 npm run test:bundle`)
  }
  return report
}

function normalizeModuleId(id: string): string {
  const withoutQuery = id.split('?')[0]
  const relative = path.isAbsolute(withoutQuery) ? path.relative(webDir, withoutQuery) : withoutQuery
  return relative.split(path.sep).join('/')
}

function getModules(chunk: ChunkReport): string[] {
  return chunk.moduleIds.map(normalizeModuleId)
}

function findChunkByFileName(report: BundleReport, fileName: string): ChunkReport {
  const chunk = report.chunks.find((candidate) => candidate.fileName === fileName)
  if (!chunk) throw new Error(`報告中找不到 chunk：${fileName}`)
  return chunk
}

function expectExactlyOne<T>(items: T[], description: string): T {
  if (items.length !== 1) throw new Error(`${description} 應恰好一個，實際 ${items.length} 個`)
  return items[0]
}

/** 從 startChunks 依序、沿 imports 陣列深度優先走訪（不含 dynamicImports），回傳去重後的走訪順序。 */
export function collectStaticClosure(report: BundleReport, startChunks: ChunkReport[]): ChunkReport[] {
  const visited = new Set<string>()
  const ordered: ChunkReport[] = []
  const visit = (chunk: ChunkReport) => {
    if (visited.has(chunk.fileName)) return
    visited.add(chunk.fileName)
    ordered.push(chunk)
    for (const imported of chunk.imports) visit(findChunkByFileName(report, imported))
  }
  startChunks.forEach(visit)
  return ordered
}

function getEntryChunk(report: BundleReport): ChunkReport {
  return expectExactlyOne(report.chunks.filter((chunk) => chunk.isEntry), 'entry chunk')
}

function getChunkContaining(report: BundleReport, moduleId: string): ChunkReport {
  return expectExactlyOne(report.chunks.filter((chunk) => getModules(chunk).includes(moduleId)), `含 ${moduleId} 的 chunk`)
}

function getFirstScreenChunks(report: BundleReport): ChunkReport[] {
  return collectStaticClosure(report, [getEntryChunk(report), getChunkContaining(report, HOME_PAGE_MODULE)])
}

/** 首屏 CSS 的來源順序：先 entry 的 importedCss，再依首屏走訪順序加入其餘 chunk 的，重複者只算第一次。 */
export function getFirstScreenCssOrder(report: BundleReport): string[] {
  const entry = getEntryChunk(report)
  const ordered = [...entry.importedCss]
  for (const chunk of getFirstScreenChunks(report)) {
    for (const css of chunk.importedCss) if (!ordered.includes(css)) ordered.push(css)
  }
  return ordered
}

let cachedReport: BundleReport | undefined
function getReport(): BundleReport {
  cachedReport ??= loadBundleReport(defaultReportPath, path.resolve(distDir, 'index.html'))
  return cachedReport
}

// ---------- 原始碼掃描與 Element Plus 元件目錄 ----------

function listFilesRecursively(dir: string): string[] {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const fullPath = path.join(dir, entry.name)
    return entry.isDirectory() ? listFilesRecursively(fullPath) : [fullPath]
  })
}

function listNonTestSourceFiles(extensions: string[]): string[] {
  return listFilesRecursively(srcDir).filter(
    (file) => extensions.includes(path.extname(file)) && !/\.test\.ts$/.test(file) && !/TestSupport\.ts$/.test(file),
  )
}

/** 掃描 .vue 中的 `<el-*>` 標籤與 `v-loading`，回傳出現過的名稱（未經對照）。 */
function scanTemplateUsages(): Set<string> {
  const usages = new Set<string>()
  for (const file of listNonTestSourceFiles(['.vue'])) {
    const source = fs.readFileSync(file, 'utf8')
    for (const match of source.matchAll(/<(el-[a-z]+(?:-[a-z]+)*)\b/g)) usages.add(match[1])
    if (/\bv-loading\b/.test(source)) usages.add('v-loading')
  }
  return usages
}

/** 掃描從 element-plus／element-plus/es 的具名值 import（略過 `import type` 與 type-only specifier）。 */
function scanNamedImports(): Set<string> {
  const names = new Set<string>()
  for (const file of listNonTestSourceFiles(['.vue', '.ts'])) {
    const source = fs.readFileSync(file, 'utf8')
    for (const match of source.matchAll(/import\s+(?!type\s)\{([^}]*)\}\s*from\s*['"]element-plus(?:\/es)?['"]/g)) {
      for (const specifier of match[1].split(',')) {
        const trimmed = specifier.trim()
        if (!trimmed || trimmed.startsWith('type ')) continue
        names.add(trimmed.split(/\s+as\s+/)[0].trim())
      }
    }
  }
  return names
}

function mapUsagesToDirs(usages: Iterable<string>, table: Record<string, string>, kind: string): Set<string> {
  const unknown = [...usages].filter((usage) => !(usage in table))
  if (unknown.length > 0) throw new Error(`${kind} 對照表缺少：${unknown.join(', ')}，請更新 build-checks 的對照表`)
  return new Set([...usages].map((usage) => table[usage]))
}

function getUsedComponentDirs(): Set<string> {
  return new Set([
    ...mapUsagesToDirs(scanTemplateUsages(), TAG_TO_COMPONENT_DIR, '標籤'),
    ...mapUsagesToDirs(scanNamedImports(), NAMED_IMPORT_TO_COMPONENT_DIR, '具名 import'),
  ])
}

function getComponentDirOfFile(file: string): string | undefined {
  const relative = path.relative(elementPlusComponentsDir, file)
  if (relative.startsWith('..') || path.isAbsolute(relative)) return undefined
  return relative.split(path.sep)[0]
}

/** 從使用目錄的 index.mjs 沿 element-plus/es 內的相對 .mjs import 遞迴走訪，回傳可到達的元件目錄。 */
function collectAllowedComponentDirs(usedDirs: Set<string>): Set<string> {
  const pending = [...usedDirs].map((dir) => path.join(elementPlusComponentsDir, dir, 'index.mjs'))
  const visited = new Set<string>()
  const allowed = new Set<string>()
  while (pending.length > 0) {
    const file = pending.pop()!
    if (visited.has(file)) continue
    visited.add(file)
    const componentDir = getComponentDirOfFile(file)
    if (componentDir) allowed.add(componentDir)
    const source = fs.readFileSync(file, 'utf8')
    for (const match of source.matchAll(/(?:from|import)\s*\(?\s*["'](\.\.?\/[^"']+\.mjs)["']/g)) {
      pending.push(path.resolve(path.dirname(file), match[1]))
    }
  }
  return allowed
}

function getBundledComponentDirs(report: BundleReport): Set<string> {
  const dirs = new Set<string>()
  for (const chunk of report.chunks) {
    for (const moduleId of getModules(chunk)) {
      const match = moduleId.match(/node_modules\/element-plus\/es\/components\/([^/]+)\//)
      if (match) dirs.add(match[1])
    }
  }
  return dirs
}

// ---------- CSS cascade（BS-007） ----------

type Specificity = [number, number, number]
type Compound = { types: string[]; classes: string[]; hasIdOrAttribute: boolean; hasPseudoElement: boolean }
type CssRule = { selector: string; specificity: Specificity; compound: Compound; properties: Map<string, string>; order: number }
export type CascadeTarget = { isRoot: boolean; classes: string[] }

function splitTopLevel(text: string, separator: (char: string) => boolean): string[] {
  const parts: string[] = []
  let depth = 0
  let current = ''
  for (const char of text) {
    if (char === '(' || char === '[') depth += 1
    if (char === ')' || char === ']') depth -= 1
    if (depth === 0 && separator(char)) {
      parts.push(current)
      current = ''
      continue
    }
    current += char
  }
  parts.push(current)
  return parts.map((part) => part.trim()).filter((part) => part.length > 0)
}

function addSpecificity(a: Specificity, b: Specificity): Specificity {
  return [a[0] + b[0], a[1] + b[1], a[2] + b[2]]
}

function compareSpecificity(a: Specificity, b: Specificity): number {
  return a[0] - b[0] || a[1] - b[1] || a[2] - b[2]
}

const LEGACY_PSEUDO_ELEMENTS = ['before', 'after', 'first-line', 'first-letter']

/** 解析單一複合選擇器（不含組合子），回傳 specificity 與組成。無法解析時 throw，不靜默略過。 */
function parseCompound(text: string): { specificity: Specificity; compound: Compound } {
  const compound: Compound = { types: [], classes: [], hasIdOrAttribute: false, hasPseudoElement: false }
  let specificity: Specificity = [0, 0, 0]
  let index = 0
  const readName = () => {
    const match = text.slice(index).match(/^-?(?:[_a-zA-Z]|\\.)(?:[\w-]|\\.)*/)
    if (!match) throw new Error(`無法解析選擇器：${text}`)
    index += match[0].length
    return match[0]
  }
  const readParenthesized = () => {
    let depth = 0
    const start = index
    do {
      if (text[index] === '(') depth += 1
      if (text[index] === ')') depth -= 1
      index += 1
    } while (depth > 0 && index < text.length)
    return text.slice(start + 1, index - 1)
  }
  while (index < text.length) {
    const char = text[index]
    if (char === '*') {
      index += 1
      compound.types.push('*')
    } else if (char === '.') {
      index += 1
      compound.classes.push(readName())
      specificity = addSpecificity(specificity, [0, 1, 0])
    } else if (char === '#') {
      index += 1
      readName()
      compound.hasIdOrAttribute = true
      specificity = addSpecificity(specificity, [1, 0, 0])
    } else if (char === '[') {
      index = text.indexOf(']', index) + 1
      if (index === 0) throw new Error(`無法解析選擇器：${text}`)
      compound.hasIdOrAttribute = true
      specificity = addSpecificity(specificity, [0, 1, 0])
    } else if (text.startsWith('::', index)) {
      index += 2
      readName()
      if (text[index] === '(') readParenthesized()
      compound.hasPseudoElement = true
      specificity = addSpecificity(specificity, [0, 0, 1])
    } else if (char === ':') {
      index += 1
      const name = readName().toLowerCase()
      const argument = text[index] === '(' ? readParenthesized() : undefined
      if (LEGACY_PSEUDO_ELEMENTS.includes(name)) {
        compound.hasPseudoElement = true
        specificity = addSpecificity(specificity, [0, 0, 1])
      } else if (name === 'where') {
        // :where() 權重為 0
      } else if (name === 'not' || name === 'is' || name === 'has' || name === 'matches') {
        const argumentSpecificities = splitTopLevel(argument ?? '', (c) => c === ',').map(computeComplexSpecificity)
        specificity = addSpecificity(specificity, argumentSpecificities.sort(compareSpecificity).at(-1) ?? [0, 0, 0])
      } else {
        specificity = addSpecificity(specificity, [0, 1, 0])
      }
    } else if (/[_a-zA-Z]/.test(char)) {
      compound.types.push(readName().toLowerCase())
      specificity = addSpecificity(specificity, [0, 0, 1])
    } else {
      throw new Error(`無法解析選擇器：${text}`)
    }
  }
  return { specificity, compound }
}

function splitCompounds(selector: string): string[] {
  return splitTopLevel(selector.replace(/\s*([>+~])\s*/g, ' '), (char) => /\s/.test(char))
}

function computeComplexSpecificity(selector: string): Specificity {
  return splitCompounds(selector)
    .map((compound) => parseCompound(compound).specificity)
    .reduce(addSpecificity, [0, 0, 0] as Specificity)
}

const KEYFRAME_SELECTOR = /^(?:from|to|[\d.]+%)(?:\s*,\s*(?:from|to|[\d.]+%))*$/

/** 依來源順序把多個 CSS asset 解析成規則；逗號清單拆成個別選擇器、各自計算 specificity。 */
export function parseCssRules(cssSources: string[]): CssRule[] {
  const rules: CssRule[] = []
  let order = 0
  for (const css of cssSources) {
    for (const block of parseCssBlocks(css)) {
      // regex 取出的 selector 可能帶著前一段的 at-rule 敘述（例如 `@charset "UTF-8";.a`），取最後一段
      const selectorList = block.selector.slice(block.selector.lastIndexOf(';') + 1).trim()
      order += 1
      if (selectorList.startsWith('@') || KEYFRAME_SELECTOR.test(selectorList)) continue
      for (const selector of splitTopLevel(selectorList, (char) => char === ',')) {
        const compounds = splitCompounds(selector)
        const rightmost = parseCompound(compounds.at(-1)!)
        rules.push({ selector, specificity: computeComplexSpecificity(selector), compound: rightmost.compound, properties: block.properties, order })
      }
    }
  }
  return rules
}

/** 保守判定：最右側複合選擇器的 class 都在目標內、type 只允許 html（根元素）或 *、無 id／屬性；pseudo-class 與祖先條件視為可能成立。 */
function isPossiblyApplicable(compound: Compound, target: CascadeTarget): boolean {
  if (compound.hasIdOrAttribute || compound.hasPseudoElement) return false
  if (!compound.classes.every((className) => target.classes.includes(className))) return false
  return compound.types.every((type) => type === '*' || (type === 'html' && target.isRoot))
}

/** 依 !important、specificity、來源順序決定 property 的勝出宣告；找不到可能套用的宣告時回傳 undefined。 */
export function resolveCascadedValue(rules: CssRule[], target: CascadeTarget, property: string): string | undefined {
  let winner: { value: string; important: boolean; specificity: Specificity; order: number } | undefined
  for (const rule of rules) {
    const raw = rule.properties.get(property)
    if (raw === undefined || !isPossiblyApplicable(rule.compound, target)) continue
    const important = /!\s*important\s*$/i.test(raw)
    const candidate = { value: raw.replace(/!\s*important\s*$/i, '').trim(), important, specificity: rule.specificity, order: rule.order }
    const comparison = !winner
      ? 1
      : Number(candidate.important) - Number(winner.important) ||
        compareSpecificity(candidate.specificity, winner.specificity) ||
        candidate.order - winner.order
    if (comparison > 0) winner = candidate
  }
  return winner?.value
}

function normalizeCssValue(value: string): string {
  const compact = value.replace(/\s+/g, '').toLowerCase()
  const shortHex = compact.match(/^#([0-9a-f])([0-9a-f])([0-9a-f])$/)
  return shortHex ? `#${shortHex[1]}${shortHex[1]}${shortHex[2]}${shortHex[2]}${shortHex[3]}${shortHex[3]}` : compact
}

function findMorandiBlock(selector: string): Map<string, string> {
  const morandiCss = fs.readFileSync(path.resolve(srcDir, 'styles/morandi.css'), 'utf8')
  const block = parseCssBlocks(morandiCss).find((candidate) => candidate.selector === selector)
  if (!block) throw new Error(`morandi.css 找不到區塊：${selector}`)
  return block.properties
}

// ---------- 測試 ----------

describe('bundle 報告讀取（BS-009）', () => {
  const temporaryDirs: string[] = []
  afterAll(() => temporaryDirs.forEach((dir) => fs.rmSync(dir, { recursive: true, force: true })))

  it('BS-009 報告不存在時失敗並提示 npm run test:bundle', () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'bs009-'))
    temporaryDirs.push(dir)
    fs.writeFileSync(path.join(dir, 'index.html'), '<script src="/assets/index-abc.js"></script>')
    expect(() => loadBundleReport(path.join(dir, 'missing.json'), path.join(dir, 'index.html'))).toThrow(/npm run test:bundle/)
  })

  it('BS-009 報告的 entry 檔名不在 dist/index.html 時失敗並提示 npm run test:bundle', () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'bs009-'))
    temporaryDirs.push(dir)
    fs.writeFileSync(path.join(dir, 'report.json'), JSON.stringify({ entryFileName: 'assets/index-OLD.js', chunks: [], cssAssets: [] }))
    fs.writeFileSync(path.join(dir, 'index.html'), '<script src="/assets/index-NEW.js"></script>')
    expect(() => loadBundleReport(path.join(dir, 'report.json'), path.join(dir, 'index.html'))).toThrow(/npm run test:bundle/)
  })
})

describe('首屏 chunk 組成（BS-001～BS-003）', () => {
  it('BS-001 首屏 JS gzip 總量不超過 200 kB', () => {
    const firstScreen = getFirstScreenChunks(getReport())
    const totalGzipBytes = firstScreen.reduce((sum, chunk) => sum + chunk.gzipBytes, 0)
    expect(firstScreen.length).toBeGreaterThan(0)
    expect(totalGzipBytes, `首屏 chunk：${firstScreen.map((chunk) => `${chunk.fileName}=${chunk.gzipBytes}`).join(', ')}`).toBeLessThanOrEqual(200 * 1000)
  })

  it('BS-002 首屏不含後台、主辦單位與其他買家頁面', () => {
    const report = getReport()
    const firstScreen = getFirstScreenChunks(report)
    expect(firstScreen).toContain(getEntryChunk(report))
    expect(firstScreen).toContain(getChunkContaining(report, HOME_PAGE_MODULE))
    const forbidden = firstScreen.flatMap(getModules).filter(
      (moduleId) =>
        moduleId.startsWith('src/pages/admin/') ||
        moduleId === ADMIN_LAYOUT_MODULE ||
        moduleId.startsWith('src/pages/organizers/') ||
        (moduleId.startsWith('src/pages/buyer/') && moduleId.endsWith('.vue') && moduleId !== HOME_PAGE_MODULE),
    )
    expect([...new Set(forbidden)]).toEqual([])
  })

  it('BS-003 jsqr 只隨驗票頁載入', () => {
    const report = getReport()
    const jsqrChunks = report.chunks.filter((chunk) => getModules(chunk).some((moduleId) => moduleId.includes('node_modules/jsqr/')))
    expect(jsqrChunks.length).toBeGreaterThanOrEqual(1)
    const scannerClosure = collectStaticClosure(report, [getChunkContaining(report, SCANNER_PAGE_MODULE)])
    const firstScreen = getFirstScreenChunks(report)
    for (const chunk of jsqrChunks) {
      expect(firstScreen, `${chunk.fileName} 不得在首屏`).not.toContain(chunk)
      expect(scannerClosure, `${chunk.fileName} 須在驗票頁靜態閉包內`).toContain(chunk)
    }
  })
})

describe('路由延遲載入（BS-004）', () => {
  it('BS-004 19 個動態 import 的元件各自是獨立 dynamic entry chunk，且不在 entry 靜態閉包內', () => {
    const report = getReport()
    const routerDir = path.resolve(srcDir, 'router')
    const routerSource = fs.readFileSync(path.join(routerDir, 'index.ts'), 'utf8')
    const lazyModules = [...routerSource.matchAll(/import\(\s*['"]([^'"]+)['"]\s*\)/g)].map((match) =>
      normalizeModuleId(path.resolve(routerDir, match[1])),
    )
    expect(lazyModules).toHaveLength(19)
    expect(lazyModules).toContain(ADMIN_LAYOUT_MODULE)
    const entryClosure = collectStaticClosure(report, [getEntryChunk(report)])
    const chunkFileNames = new Set<string>()
    for (const moduleId of lazyModules) {
      const chunk = getChunkContaining(report, moduleId)
      expect(chunk.isDynamicEntry, `${moduleId} 的 chunk 應為 dynamic entry`).toBe(true)
      expect(chunk.facadeModuleId && normalizeModuleId(chunk.facadeModuleId), `${moduleId} 的 chunk facade`).toBe(moduleId)
      expect(entryClosure, `${moduleId} 不得在 entry 靜態閉包內`).not.toContain(chunk)
      chunkFileNames.add(chunk.fileName)
    }
    expect(chunkFileNames.size).toBe(19)
  })
})

describe('Element Plus 按需引入（BS-006、BS-011）', () => {
  it('BS-006 使用集合與允許集合非空，使用目錄都有 index.mjs', () => {
    const usedDirs = getUsedComponentDirs()
    expect(usedDirs.size).toBeGreaterThan(0)
    for (const dir of usedDirs) {
      expect(fs.existsSync(path.join(elementPlusComponentsDir, dir, 'index.mjs')), `${dir}/index.mjs`).toBe(true)
    }
    const allowedDirs = collectAllowedComponentDirs(usedDirs)
    expect(allowedDirs.size).toBeGreaterThan(0)
    expect([...usedDirs].filter((dir) => !allowedDirs.has(dir))).toEqual([])
  })

  it('BS-006 bundle 中的元件目錄都屬於允許集合', () => {
    const allowedDirs = collectAllowedComponentDirs(getUsedComponentDirs())
    const bundledDirs = getBundledComponentDirs(getReport())
    expect(bundledDirs.size).toBeGreaterThan(0)
    expect([...bundledDirs].filter((dir) => !allowedDirs.has(dir)).sort()).toEqual([])
  })

  it('BS-006 與使用無關的 6 個元件既不在允許集合，也不在 bundle', () => {
    const allowedDirs = collectAllowedComponentDirs(getUsedComponentDirs())
    const bundledDirs = getBundledComponentDirs(getReport())
    expect(UNRELATED_COMPONENT_DIRS.filter((dir) => allowedDirs.has(dir)), '進入允許集合者須更新 spec').toEqual([])
    expect(UNRELATED_COMPONENT_DIRS.filter((dir) => bundledDirs.has(dir))).toEqual([])
  })

  it('BS-006 沒有 chunk 引入 theme-chalk 或元件個別樣式模組', () => {
    const styleModules = getReport().chunks.flatMap(getModules).filter(
      (moduleId) => moduleId.includes('element-plus/theme-chalk/') || /element-plus\/es\/components\/[^/]+\/style\//.test(moduleId),
    )
    expect(styleModules).toEqual([])
  })

  it('BS-006 main.ts 不以 app.use(ElementPlus) 全量註冊', () => {
    const mainSource = fs.readFileSync(path.resolve(srcDir, 'main.ts'), 'utf8')
    expect(mainSource).not.toMatch(/app\.use\(\s*ElementPlus\s*\)/)
  })

  it('BS-011 template 用到的元件與 v-loading 都進入 bundle', () => {
    const usedDirs = mapUsagesToDirs(scanTemplateUsages(), TAG_TO_COMPONENT_DIR, '標籤')
    expect(usedDirs.size).toBeGreaterThan(0)
    const bundledDirs = getBundledComponentDirs(getReport())
    expect([...usedDirs].filter((dir) => !bundledDirs.has(dir)).sort()).toEqual([])
  })
})

describe('CSS cascade 判定（BS-007 的合成案例）', () => {
  const primaryButton: CascadeTarget = { isRoot: false, classes: ['el-button', 'el-button--primary'] }
  const root: CascadeTarget = { isRoot: true, classes: [] }

  it('權重較高的規則在目標含其 class 時勝出，不含時不納入', () => {
    const rules = parseCssRules(['.el-button--primary.x{--p:#111}.el-button--primary{--p:#222}'])
    expect(resolveCascadedValue(rules, { ...primaryButton, classes: [...primaryButton.classes, 'x'] }, '--p')).toBe('#111')
    expect(resolveCascadedValue(rules, primaryButton, '--p')).toBe('#222')
  })

  it('同權重時後出現者勝', () => {
    const rules = parseCssRules(['.el-button--primary{--p:#111}', '.el-button--primary{--p:#222}'])
    expect(resolveCascadedValue(rules, primaryButton, '--p')).toBe('#222')
  })

  it('!important 優先於權重與順序', () => {
    const rules = parseCssRules(['.el-button--primary{--p:#111!important}.el-button.el-button--primary{--p:#222}'])
    expect(resolveCascadedValue(rules, primaryButton, '--p')).toBe('#111')
  })

  it('type 選擇器只允許 html（僅根元素）或 *', () => {
    expect(resolveCascadedValue(parseCssRules(['button.el-button--primary{--p:#111}']), primaryButton, '--p')).toBeUndefined()
    expect(resolveCascadedValue(parseCssRules(['*{--p:#111}']), primaryButton, '--p')).toBe('#111')
    expect(resolveCascadedValue(parseCssRules(['html{--p:#111}']), root, '--p')).toBe('#111')
    expect(resolveCascadedValue(parseCssRules(['html{--p:#111}']), primaryButton, '--p')).toBeUndefined()
  })

  it('最右側含 id 或屬性選擇器時不納入', () => {
    expect(resolveCascadedValue(parseCssRules(['.el-button--primary#a{--p:#111}']), primaryButton, '--p')).toBeUndefined()
    expect(resolveCascadedValue(parseCssRules(['.el-button--primary[disabled]{--p:#111}']), primaryButton, '--p')).toBeUndefined()
  })

  it('pseudo-class 視為可能套用，並計入權重', () => {
    const rules = parseCssRules(['.el-button--primary:hover{--p:#111}.el-button--primary{--p:#222}'])
    expect(resolveCascadedValue(rules, primaryButton, '--p')).toBe('#111')
  })

  it('逗號清單拆開後各自判定與計算權重', () => {
    const rules = parseCssRules(['.other.el-button--primary.y,.el-button--primary{--p:#111}.el-button--primary{--p:#222}'])
    // 可套用的只有 `.el-button--primary`（0,1,0），同權重時後者勝
    expect(resolveCascadedValue(rules, primaryButton, '--p')).toBe('#222')
  })

  it('html.dark 因根元素沒有 class 而不納入', () => {
    const rules = parseCssRules([':root{--p:#111}html.dark{--p:#222}'])
    expect(resolveCascadedValue(rules, root, '--p')).toBe('#111')
  })

  it('@media 內的規則視為可能套用', () => {
    const rules = parseCssRules(['.el-button--primary{--p:#111}@media (min-width:1px){.el-button--primary{--p:#222}}'])
    expect(resolveCascadedValue(rules, primaryButton, '--p')).toBe('#222')
  })

  it('跨 asset 順序：entry 的 importedCss 在前，其後依首屏走訪順序，重複者只算第一次', () => {
    const chunk = (fileName: string, imports: string[], importedCss: string[], extra: Partial<ChunkReport> = {}): ChunkReport => ({
      fileName, isEntry: false, isDynamicEntry: false, facadeModuleId: null, moduleIds: [], imports, dynamicImports: [], importedCss, gzipBytes: 0, ...extra,
    })
    const report: BundleReport = {
      entryFileName: 'entry.js',
      cssAssets: [],
      chunks: [
        chunk('entry.js', ['shared.js'], ['entry.css'], { isEntry: true }),
        chunk('shared.js', [], ['shared.css', 'entry.css']),
        chunk('home.js', ['shared.js', 'home-dep.js'], ['home.css'], { moduleIds: [path.resolve(webDir, HOME_PAGE_MODULE)] }),
        chunk('home-dep.js', [], ['home-dep.css']),
      ],
    }
    expect(getFirstScreenCssOrder(report)).toEqual(['entry.css', 'shared.css', 'home.css', 'home-dep.css'])
  })
})

describe('首屏 CSS 色票覆寫（BS-007）', () => {
  it('BS-007 morandi 覆寫在首屏 CSS 的 cascade 中勝出', () => {
    const cssFileNames = getFirstScreenCssOrder(getReport())
    expect(cssFileNames.length).toBeGreaterThan(0)
    const cssSources = cssFileNames.map((fileName) => fs.readFileSync(path.resolve(distDir, fileName), 'utf8'))
    cssSources.forEach((source, index) => expect(source.trim().length, cssFileNames[index]).toBeGreaterThan(0))
    const rules = parseCssRules(cssSources)

    const targets: { description: string; target: CascadeTarget; expected: Map<string, string> }[] = [
      { description: '根元素', target: { isRoot: true, classes: [] }, expected: findMorandiBlock(':root') },
      { description: '.el-button', target: { isRoot: false, classes: ['el-button'] }, expected: findMorandiBlock('.el-button') },
      ...SEMANTIC_COLORS.map((color) => ({
        description: `.el-button--${color}`,
        target: { isRoot: false, classes: ['el-button', `el-button--${color}`] },
        expected: findMorandiBlock(`.el-button--${color}`),
      })),
    ]
    const mismatches: string[] = []
    for (const { description, target, expected } of targets) {
      expect(expected.size, `${description} 的待比對 property`).toBeGreaterThan(0)
      for (const [property, morandiValue] of expected) {
        const winning = resolveCascadedValue(rules, target, property)
        if (winning === undefined) throw new Error(`${description} 的 ${property} 找不到可能套用的宣告`)
        if (normalizeCssValue(winning) !== normalizeCssValue(morandiValue)) {
          mismatches.push(`${description} ${property}: 勝出 ${winning}，morandi ${morandiValue}`)
        }
      }
    }
    expect(mismatches).toEqual([])
  })
})

describe('報告不外洩（BS-008）', () => {
  it('BS-008 dist/ 不含 bundle 報告', () => {
    getReport()
    const files = listFilesRecursively(distDir)
    expect(files.map((file) => path.relative(distDir, file).split(path.sep).join('/'))).toContain('index.html')
    expect(files.filter((file) => path.basename(file).includes('bundle-report'))).toEqual([])
    expect(files.filter((file) => fs.readFileSync(file).includes('bundle-report.json'))).toEqual([])
  })
})

describe('報告外掛只在 BUNDLE_REPORT=1 時啟用（BS-013）', () => {
  const temporaryDir = fs.mkdtempSync(path.join(os.tmpdir(), 'bs013-'))
  afterAll(() => fs.rmSync(temporaryDir, { recursive: true, force: true }))

  async function runViteBuild(bundleReport: string | undefined, outDir: string, reportPath: string): Promise<void> {
    const env: NodeJS.ProcessEnv = { ...process.env, BUNDLE_REPORT_PATH: reportPath }
    delete env.BUNDLE_REPORT
    if (bundleReport !== undefined) env.BUNDLE_REPORT = bundleReport
    try {
      await execFileAsync(path.resolve(webDir, 'node_modules/.bin/vite'), ['build', '--outDir', outDir, '--emptyOutDir'], { cwd: webDir, env })
    } catch (error) {
      const stderr = (error as { stderr?: string }).stderr ?? ''
      throw new Error(`vite build（BUNDLE_REPORT=${bundleReport ?? '未設定'}）失敗：${stderr}`, { cause: error })
    }
  }

  it('BS-013 未設定或為 0 時不產生報告，為 1 時產生且對應同一次 build', { timeout: 120_000 }, async () => {
    const reportPath = path.join(temporaryDir, 'report', 'bundle-report.json')
    const outDir = path.join(temporaryDir, 'dist')
    expect(fs.existsSync(reportPath)).toBe(false)

    await runViteBuild(undefined, outDir, reportPath)
    expect(fs.existsSync(reportPath), '未設定 BUNDLE_REPORT').toBe(false)

    await runViteBuild('0', outDir, reportPath)
    expect(fs.existsSync(reportPath), 'BUNDLE_REPORT=0').toBe(false)

    await runViteBuild('1', outDir, reportPath)
    expect(() => loadBundleReport(reportPath, path.join(outDir, 'index.html'))).not.toThrow()
  })
})
