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
  <div class="admin-layout">
    <div class="admin-nav">
      <el-menu mode="horizontal" router :ellipsis="false" class="admin-nav-menu">
        <el-menu-item index="/admin/venues">場館管理</el-menu-item>
        <el-menu-item index="/admin/events">活動管理</el-menu-item>
        <el-menu-item index="/admin/orders">訂單管理</el-menu-item>
      </el-menu>
      <el-button text @click="handleLogout">登出</el-button>
    </div>
    <div class="admin-content">
      <router-view />
    </div>
  </div>
</template>

<style scoped>
.admin-nav {
  display: flex;
  align-items: center;
  border-bottom: 1px solid var(--el-menu-border-color);
  padding-right: 16px;
}
.admin-nav-menu {
  flex-grow: 1;
  border-bottom: none;
}
.admin-content {
  max-width: 1080px;
  margin: 24px auto;
  padding: 0 16px;
}
</style>
