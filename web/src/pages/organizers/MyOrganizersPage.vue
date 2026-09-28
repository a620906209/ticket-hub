<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { getMyOrganizers, switchOrganizerContext } from '../../api/organizers'
import { useAuthStore } from '../../stores/auth'
import type { OrganizerSummary } from '../../types/apiResponses'
import { toErrorMessage } from '../../utils/errors'

const router = useRouter()
const authStore = useAuthStore()

const organizers = ref<OrganizerSummary[]>([])
const loading = ref(false)
const error = ref('')
const switchingId = ref('')

const statusLabels: Record<string, string> = {
  Pending: '待審核',
  Approved: '已核准',
  Rejected: '已駁回',
  Suspended: '已停權',
}

function statusLabel(status: string): string {
  return statusLabels[status] ?? status
}

async function loadOrganizers(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    organizers.value = await getMyOrganizers()
  } catch (loadError) {
    error.value = toErrorMessage(loadError, '載入主辦方清單失敗')
  } finally {
    loading.value = false
  }
}

onMounted(loadOrganizers)

async function handleSwitch(organizer: OrganizerSummary): Promise<void> {
  const refreshToken = authStore.getCurrentRefreshToken()
  if (!refreshToken) return

  switchingId.value = organizer.id
  try {
    const result = await switchOrganizerContext(organizer.id, refreshToken)
    authStore.applySwitchedAccessToken(result.accessToken)
    await router.push('/admin')
  } catch (switchError) {
    ElMessage.error(toErrorMessage(switchError, '切換主辦方失敗'))
  } finally {
    switchingId.value = ''
  }
}
</script>

<template>
  <div class="my-organizers-page">
    <h1>我的主辦方</h1>
    <el-alert v-if="error" :title="error" type="error" show-icon style="margin-bottom: 16px" />

    <el-skeleton v-if="loading" :rows="3" animated />

    <template v-else>
      <div v-if="organizers.length === 0" class="empty-state">
        <p>尚未加入任何主辦方。</p>
        <router-link :to="{ name: 'organizer-apply' }">前往申請建立主辦方</router-link>
      </div>

      <el-table v-else :data="organizers">
        <el-table-column prop="name" label="名稱" />
        <el-table-column label="狀態">
          <template #default="{ row }">{{ statusLabel(row.status) }}</template>
        </el-table-column>
        <el-table-column label="操作">
          <template #default="{ row }">
            <el-button
              v-if="row.status === 'Approved'"
              type="primary"
              size="small"
              :loading="switchingId === row.id"
              @click="handleSwitch(row)"
            >
              切換
            </el-button>
          </template>
        </el-table-column>
      </el-table>
    </template>
  </div>
</template>

<style scoped>
.empty-state {
  color: var(--el-text-color-secondary);
}
</style>
