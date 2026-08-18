<script setup lang="ts">
import { watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from './stores/auth'

const authStore = useAuthStore()
const route = useRoute()
const router = useRouter()

// 401 換發失敗（見 httpClient 的 authorizedRequest）只會清空 store 狀態，不會自動導頁——
// 使用者可能還停在一個已經失效的受保護頁面上。這裡統一補一層：登入狀態變成 false 時，
// 若目前頁面需要登入，主動導向登入頁（見 tasks.md 6.2 review 時發現的缺口）。
watch(
  () => authStore.isAuthenticated,
  (isAuthenticated) => {
    if (isAuthenticated) return
    const requiresAuth = route.meta.requiresAuth === true || route.matched.some((record) => record.meta.requiresAdmin)
    if (requiresAuth) {
      router.push({ name: 'login', query: { redirect: route.fullPath } })
    }
  },
)
</script>

<template>
  <el-alert
    v-if="authStore.bootstrapError"
    title="無法確認登入狀態，請檢查網路連線"
    type="error"
    show-icon
    :closable="false"
  >
    <el-button size="small" @click="authStore.bootstrapAsync()">重試</el-button>
  </el-alert>
  <router-view />
</template>
