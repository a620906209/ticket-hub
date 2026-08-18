/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue()],
  server: {
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
