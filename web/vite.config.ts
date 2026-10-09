import fs from 'node:fs'
import path from 'node:path'
import zlib from 'node:zlib'
import { defineConfig, type Plugin } from 'vite'
import { configDefaults } from 'vitest/config'
import vue from '@vitejs/plugin-vue'
import Components from 'unplugin-vue-components/vite'
import { ElementPlusResolver } from 'unplugin-vue-components/resolvers'

// 手機實機測試相機掃描（AC 6.4）需要 secure context。用 mkcert 產生的區網 IP 憑證
// （web/certs/，見 .gitignore，僅限個人機器，未 commit）在本機開發時啟用 HTTPS；
// 沒有憑證檔（例如其他開發者的機器）時自動退回原本的 HTTP，不影響一般開發流程。
const certDir = path.resolve(import.meta.dirname, 'certs')
const certFile = path.join(certDir, 'dev-cert.pem')
const keyFile = path.join(certDir, 'dev-key.pem')
const httpsConfig =
  fs.existsSync(certFile) && fs.existsSync(keyFile)
    ? { cert: fs.readFileSync(certFile), key: fs.readFileSync(keyFile) }
    : undefined

// bundle-splitting 的驗證（build-checks/）讀這份報告判定 chunk 組成。報告含模組路徑，
// 寫在 node_modules/.tmp 而不是 dist/，避免路徑隨靜態檔外流。
const bundleReportPath = process.env.BUNDLE_REPORT_PATH || path.resolve(import.meta.dirname, 'node_modules/.tmp/bundle-report.json')

function requireField<T>(value: T | undefined, description: string): T {
  // rolldown／Vite 升級後欄位可能消失；輸出不完整的報告會讓驗證測試誤判，寧可讓 build 失敗
  if (value === undefined) throw new Error(`bundle-report：chunk 缺少欄位 ${description}`)
  return value
}

function createBundleReportPlugin(): Plugin {
  return {
    name: 'bundle-report',
    apply: 'build',
    generateBundle(_options, bundle) {
      const chunks = []
      const cssAssets = []
      for (const output of Object.values(bundle)) {
        if (output.type === 'asset') {
          if (output.fileName.endsWith('.css')) {
            cssAssets.push({ fileName: output.fileName, gzipBytes: zlib.gzipSync(output.source).length })
          }
          continue
        }
        const viteMetadata = requireField(output.viteMetadata, `viteMetadata（${output.fileName}）`)
        chunks.push({
          fileName: output.fileName,
          isEntry: requireField(output.isEntry, 'isEntry'),
          isDynamicEntry: requireField(output.isDynamicEntry, 'isDynamicEntry'),
          facadeModuleId: requireField(output.facadeModuleId, 'facadeModuleId'),
          moduleIds: [...requireField(output.moduleIds, 'moduleIds')],
          imports: [...requireField(output.imports, 'imports')],
          dynamicImports: [...requireField(output.dynamicImports, 'dynamicImports')],
          importedCss: [...requireField(viteMetadata.importedCss, 'viteMetadata.importedCss')],
          gzipBytes: zlib.gzipSync(output.code).length,
        })
      }
      const entryChunks = chunks.filter((chunk) => chunk.isEntry)
      if (entryChunks.length !== 1) throw new Error(`bundle-report：entry chunk 應恰好一個，實際 ${entryChunks.length} 個`)
      fs.mkdirSync(path.dirname(bundleReportPath), { recursive: true })
      fs.writeFileSync(bundleReportPath, JSON.stringify({ entryFileName: entryChunks[0].fileName, chunks, cssAssets }, null, 2))
    },
  }
}

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    vue(),
    // Element Plus 元件與 v-loading 在編譯期按需 import。樣式維持 main.ts 全量引入（importStyle: false）：
    // 按需樣式會在延遲載入的 chunk 中後注入，蓋掉 morandi.css 的色票覆寫。dirs: [] 讓外掛只處理 Element Plus；
    // dts: false 不產生 components.d.ts，避免 vue-tsc 開始檢查所有 template（不在 bundle-splitting 範圍）。
    Components({ resolvers: [ElementPlusResolver({ importStyle: false })], dirs: [], dts: false }),
    ...(process.env.BUNDLE_REPORT === '1' ? [createBundleReportPlugin()] : []),
  ],
  server: {
    https: httpsConfig,
    proxy: {
      '/api': {
        target: 'http://api:8080',
        changeOrigin: true,
      },
    },
    // Docker Desktop on Windows 的 bind mount 常常不會把 host 端的檔案變更事件正確傳進容器，
    // chokidar 的原生 fs watch 因此會漏掉變更、HMR 沒反應（實測發現：改完檔案，容器內還是舊內容，
    // 要重啟容器才會生效）。改用 polling 換取可靠性。
    watch: {
      usePolling: true,
      interval: 300,
    },
  },
  test: {
    environment: 'jsdom',
    // build-checks/ 讀真實 build 產出，須先 build，由 test:bundle 以 vitest.bundle.config.ts 另外執行
    exclude: [...configDefaults.exclude, 'build-checks/**'],
    // element-plus 若以 Node 原生 ESM 載入，其 default import 的 async-validator 會拿到 CJS 物件而非 class，
    // el-form 的 rules 在測試中整個失效（validate() 恆通過）；交給 Vite 轉換才會正確處理 interop。
    server: { deps: { inline: ['element-plus'] } },
    // Vitest 預設把 CSS 模組（含 ?raw）換成空字串；色票對比度測試需要讀取原始內容
    css: { include: [/src\/styles\/morandi\.css/, /src\/style\.css/] },
  },
})
