<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { getMyOrganizers } from '../api/organizers'

const router = useRouter()
const authStore = useAuthStore()

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
</script>

<template>
  <div class="admin-layout">
    <div class="admin-nav">
      <el-menu mode="horizontal" router :ellipsis="false" class="admin-nav-menu">
        <el-menu-item index="/admin/venues">場館管理</el-menu-item>
        <el-menu-item index="/admin/events">活動管理</el-menu-item>
        <!-- 以下三頁路由守衛仍要求 Admin 角色（AWU-GUARD-004／006），非 Admin 的 Organizer 成員點了只會被導回買家首頁，因此不顯示。 -->
        <template v-if="authStore.isAdmin">
          <el-menu-item index="/admin/orders">訂單管理</el-menu-item>
          <el-menu-item index="/admin/redeem">票券核銷</el-menu-item>
          <el-menu-item index="/admin/organizers">主辦方審核</el-menu-item>
        </template>
      </el-menu>
      <router-link :to="{ name: 'my-organizers' }" class="organizer-indicator">
        {{ currentOrganizerName ?? '尚未切換主辦方' }}
      </router-link>
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
.organizer-indicator {
  margin-right: 12px;
  font-size: 14px;
  color: var(--el-text-color-secondary);
  text-decoration: none;
}
</style>
