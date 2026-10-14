<script setup lang="ts">
import { useRouter } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import AreaSwitcher from '../components/AreaSwitcher.vue'

const router = useRouter()
const authStore = useAuthStore()

async function handleLogout(): Promise<void> {
  await authStore.logout()
  await router.push('/login')
}
</script>

<!-- 只有一個選單項目，320 CSS px 也放得下，不比照 AdminLayout 做抽屜（web-area-separation 決策 4） -->
<template>
  <div class="platform-layout">
    <div class="platform-nav">
      <el-menu mode="horizontal" router :ellipsis="false" class="platform-nav-menu">
        <el-menu-item index="/platform/organizers">主辦方審核</el-menu-item>
      </el-menu>
      <el-button text class="platform-nav-logout" @click="handleLogout">登出</el-button>
      <AreaSwitcher current="platform" class="platform-nav-area-switcher" />
    </div>
    <div class="platform-content">
      <router-view />
    </div>
  </div>
</template>

<style scoped>
/* 區域底色與導覽列色條（web-area-separation 決策 3），與主辦方後台同一套結構、不同顏色 */
.platform-layout {
  min-height: 100svh;
  background: var(--color-bg-platform);
}
.platform-nav {
  display: flex;
  align-items: center;
  background: var(--color-bg-elevated);
  border-top: 4px solid var(--color-area-platform);
  border-bottom: 1px solid var(--el-menu-border-color);
  padding: 0 16px;
}
.platform-nav-area-switcher {
  flex-shrink: 0;
  margin-left: 8px;
}
.platform-nav-menu {
  flex-grow: 1;
  min-width: 0;
  border-bottom: none;
}
.platform-nav-logout {
  flex-shrink: 0;
}
.platform-content {
  max-width: 1080px;
  margin: 24px auto;
  padding: 0 16px;
}

@media (max-width: 720px) {
  .platform-nav {
    padding-left: 8px;
  }
}
</style>
