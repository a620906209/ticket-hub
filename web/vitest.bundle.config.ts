import { defineConfig } from 'vitest/config'

// 只跑 build-checks/：這些測試讀 `BUNDLE_REPORT=1 vite build` 的產出，不需要 jsdom 與 Vue 外掛
export default defineConfig({
  test: {
    environment: 'node',
    include: ['build-checks/**/*.test.ts'],
  },
})
