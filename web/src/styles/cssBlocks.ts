export type CssBlock = { selector: string; properties: Map<string, string>; offset: number }

/**
 * 以 regex 取出不含巢狀大括號的 `selector { declarations }` 區塊。
 * `@media` 等條件規則內的區塊也會被取出（外層包裝被略過），同一區塊內重複的 property 以最後一次為準。
 */
export function parseCssBlocks(css: string): CssBlock[] {
  const withoutComments = css.replace(/\/\*[\s\S]*?\*\//g, (comment) => ' '.repeat(comment.length))
  const blocks: CssBlock[] = []
  for (const match of withoutComments.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const properties = new Map<string, string>()
    for (const declaration of match[2].split(';')) {
      const separatorIndex = declaration.indexOf(':')
      if (separatorIndex < 0) continue
      properties.set(declaration.slice(0, separatorIndex).trim(), declaration.slice(separatorIndex + 1).trim())
    }
    blocks.push({ selector: match[1].trim(), properties, offset: match.index })
  }
  return blocks
}
