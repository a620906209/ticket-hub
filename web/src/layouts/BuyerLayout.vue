<script setup lang="ts">
import { useRouter } from 'vue-router'
import { useAuthStore } from '../stores/auth'

const router = useRouter()
const authStore = useAuthStore()

async function handleLogout(): Promise<void> {
  await authStore.logout()
  await router.push('/login')
}
</script>

<template>
  <div class="buyer-layout">
    <header class="buyer-header">
      <router-link to="/" class="brand">售票平台</router-link>
      <nav class="buyer-nav">
        <router-link to="/" class="nav-link">活動</router-link>
      </nav>
      <div class="buyer-actions">
        <template v-if="authStore.isAuthenticated">
          <router-link to="/orders" class="nav-link">我的訂單</router-link>
          <el-dropdown>
            <span class="member-trigger">{{ authStore.member?.displayName }}</span>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item @click="handleLogout">登出</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </template>
        <template v-else>
          <router-link to="/login" class="nav-link">登入</router-link>
          <router-link to="/register" class="nav-link">註冊</router-link>
        </template>
      </div>
    </header>
    <main class="buyer-content">
      <router-view />
    </main>
  </div>
</template>

<style scoped>
.buyer-header {
  display: flex;
  align-items: center;
  gap: 24px;
  padding: 0 24px;
  height: 56px;
  background: var(--color-bg-elevated);
  border-bottom: 1px solid var(--color-border);
}
.brand {
  font-weight: 600;
  font-size: 18px;
  color: var(--color-text);
  text-decoration: none;
}
.buyer-nav {
  display: flex;
  gap: 16px;
  flex-grow: 1;
}
.nav-link {
  color: var(--color-text-secondary);
  text-decoration: none;
  font-size: 14px;
}
.nav-link:hover {
  color: var(--color-primary);
}
.buyer-actions {
  display: flex;
  align-items: center;
  gap: 16px;
}
.member-trigger {
  cursor: pointer;
  color: var(--color-text);
  font-size: 14px;
}
.buyer-content {
  min-height: calc(100svh - 56px);
  background: var(--color-bg);
}
</style>
