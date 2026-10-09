<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { getMyOrganizers } from '../api/organizers'

const router = useRouter()
const route = useRoute()
const authStore = useAuthStore()

// 水平選單與窄螢幕抽屜共用同一份清單，避免新增選單時只改到其中一處（design.md 決策 2）。
const menuItems = computed(() => {
  const items = [
    { index: '/admin/venues', label: '場館管理' },
    { index: '/admin/events', label: '活動管理' },
    { index: '/admin/orders', label: '訂單管理' },
    { index: '/admin/redeem', label: '票券核銷' },
  ]
  // 審核頁路由守衛仍要求 Admin 角色（AWU-GUARD-004），非 Admin 點了只會被導回買家首頁，因此不顯示。
  if (authStore.isAdmin) items.push({ index: '/admin/organizers', label: '主辦方審核' })
  return items
})

const isDrawerOpen = ref(false)
const menuButton = ref<{ $el: HTMLElement } | null>(null)

// Element Plus 只把焦點還給開啟前的 activeElement，觸控與 Safari 點擊不會讓按鈕取得焦點而落到 body；
// 掛在 @closed（after-leave）才會晚於 focus trap 的預設還原，不被覆蓋；不可改掛 @close，單元測試分辨不出這個差異（design.md 決策 4）。
function focusMenuButton(): void {
  menuButton.value?.$el.focus()
}

// 導覽列顯示目前切換所在的 Organizer 名稱；Access Token 的 OrganizerId claim 僅帶 GUID，
// 依既定慣例呼叫 GET /api/organizers/mine 比對出名稱顯示，不新增後端能力（見 tasks.md 5.3）。
const currentOrganizerName = ref<string | null>(null)

async function loadCurrentOrganizerName(): Promise<void> {
  currentOrganizerName.value = null
  const organizerId = authStore.organizerId
  if (!organizerId) return

  try {
    const organizers = await getMyOrganizers()
    currentOrganizerName.value = organizers.find((o) => o.id === organizerId)?.name ?? null
  } catch {
    currentOrganizerName.value = null
  }
}

onMounted(loadCurrentOrganizerName)
watch(() => authStore.organizerId, loadCurrentOrganizerName)

async function handleLogout(): Promise<void> {
  await authStore.logout()
  await router.push('/login')
}

// 先關抽屜：logout 會等待後端登出 API，等待期間遮罩、捲動鎖與 focus trap 不應繼續蓋著畫面（design.md 決策 4）。
async function handleDrawerLogout(): Promise<void> {
  isDrawerOpen.value = false
  await handleLogout()
}
</script>

<template>
  <div class="admin-layout">
    <div class="admin-nav">
      <el-button
        ref="menuButton"
        text
        class="admin-nav-menu-button"
        aria-label="開啟後台選單"
        :aria-expanded="isDrawerOpen ? 'true' : 'false'"
        @click="isDrawerOpen = true"
      >
        <svg aria-hidden="true" width="20" height="20" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
          <path d="M3 5h14M3 10h14M3 15h14" />
        </svg>
      </el-button>
      <el-menu mode="horizontal" router :ellipsis="false" class="admin-nav-menu">
        <el-menu-item v-for="item in menuItems" :key="item.index" :index="item.index">{{ item.label }}</el-menu-item>
      </el-menu>
      <router-link :to="{ name: 'my-organizers' }" class="organizer-indicator" :title="currentOrganizerName ?? undefined">
        {{ currentOrganizerName ?? '尚未切換主辦方' }}
      </router-link>
      <el-button text class="admin-nav-logout" @click="handleLogout">登出</el-button>
    </div>
    <el-drawer
      v-model="isDrawerOpen"
      class="admin-nav-drawer"
      direction="ltr"
      size="280px"
      title="後台選單"
      @closed="focusMenuButton"
    >
      <el-menu router class="admin-nav-drawer-menu" :default-active="route.path" @select="isDrawerOpen = false">
        <el-menu-item v-for="item in menuItems" :key="item.index" :index="item.index">{{ item.label }}</el-menu-item>
      </el-menu>
      <el-button text class="admin-nav-drawer-logout" @click="handleDrawerLogout">登出</el-button>
    </el-drawer>
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
.admin-nav-menu-button {
  display: none;
  min-width: 44px;
  min-height: 44px;
  margin-left: 8px;
}
.admin-content {
  max-width: 1080px;
  margin: 24px auto;
  padding: 0 16px;
}
.organizer-indicator {
  min-width: 0;
  max-width: 240px;
  margin-right: 12px;
  overflow: hidden;
  font-size: 14px;
  color: var(--el-text-color-secondary);
  text-decoration: none;
  white-space: nowrap;
  text-overflow: ellipsis;
}

@media (max-width: 720px) {
  .admin-nav-menu,
  .admin-nav-logout {
    display: none;
  }
  .admin-nav-menu-button {
    display: inline-flex;
  }
  .organizer-indicator {
    flex: 1 1 auto;
    max-width: none;
    margin: 0 0 0 8px;
  }
}
</style>

<!-- 抽屜 Teleport 到 body，不帶 data-v 屬性，scoped 樣式不會套用；選擇器一律以 .admin-nav-drawer 限定範圍（design.md 決策 4）。 -->
<style>
.admin-nav-drawer .admin-nav-drawer-menu {
  border-right: none;
}
.admin-nav-drawer .admin-nav-drawer-logout {
  margin-top: 16px;
  margin-left: 12px;
}
</style>
