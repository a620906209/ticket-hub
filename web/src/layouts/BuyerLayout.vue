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
          <el-dropdown class="member-dropdown">
            <span class="member-trigger" :title="authStore.member?.displayName">{{ authStore.member?.displayName }}</span>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item @click="router.push({ name: 'real-name' })">實名資料</el-dropdown-item>
                <el-dropdown-item @click="router.push({ name: 'my-organizers' })">我的主辦方</el-dropdown-item>
                <el-dropdown-item @click="handleLogout">登出</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
          <AreaSwitcher current="buyer" />
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
/* 與 body 同色、外觀不變，只是讓三個區域都有可量測背景色的版面根元素（web-area-separation 決策 3） */
.buyer-layout {
  min-height: 100svh;
  background: var(--color-bg);
}
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

/* 會員名稱長度不定，換行會把頂列撐高，一律單行並在空間不足時截斷（web-area-separation 8.1） */
.buyer-actions,
.member-dropdown,
.member-trigger {
  min-width: 0;
}
.member-trigger {
  display: block;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  /* el-dropdown 設 line-height: 1，加上 overflow: hidden 後 g、y、p 等字母的下伸部會被裁掉（web-area-separation 8.1） */
  line-height: normal;
}

/* 加了區域切換鈕後 390px 就會把品牌名稱與「活動」擠成兩行；361px 以上這些元素不換行，空間只從會員名稱扣。
   320px 不換行時會員名稱只剩「…」，所以 360px 以下改為限制會員名稱寬度、其餘允許換行（web-area-separation 8.1）。
   用範圍語法而非 min-width: 361px，縮放時視窗寬可能是 360.x，兩條規則都不套用 */
@media (width > 360px) {
  .brand,
  .nav-link {
    white-space: nowrap;
  }
}
@media (width <= 360px) {
  .member-trigger {
    max-width: 4em;
  }
}

/* 只縮寫切換鈕時「我的訂單」仍會擠壓，間距也要一起收（web-area-separation 4.4、8.1），斷點與 AreaSwitcher 的縮寫一致 */
@media (max-width: 480px) {
  .buyer-header {
    gap: 12px;
    padding: 0 16px;
  }
  .buyer-actions {
    gap: 8px;
  }
}
</style>
