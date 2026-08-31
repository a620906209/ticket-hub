/// <reference types="vitest/config" />
import fs from 'node:fs'
import path from 'node:path'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

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

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue()],
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
  },
})
