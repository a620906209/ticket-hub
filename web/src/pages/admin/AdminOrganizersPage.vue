<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { approveOrganizer, getPendingOrganizers, rejectOrganizer } from '../../api/organizers'
import type { PendingOrganizer } from '../../types/apiResponses'
import { toErrorMessage } from '../../utils/errors'

const organizers = ref<PendingOrganizer[]>([])
const loading = ref(false)
const error = ref('')
const actingId = ref('')

async function loadPending(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    organizers.value = await getPendingOrganizers()
  } catch (loadError) {
    error.value = toErrorMessage(loadError, '載入待審核主辦方清單失敗')
  } finally {
    loading.value = false
  }
}

onMounted(loadPending)

async function handleApprove(organizer: PendingOrganizer): Promise<void> {
  actingId.value = organizer.id
  try {
    await approveOrganizer(organizer.id)
    ElMessage.success('已核准')
    await loadPending()
  } catch (actError) {
    ElMessage.error(toErrorMessage(actError, '核准失敗'))
  } finally {
    actingId.value = ''
  }
}

async function handleReject(organizer: PendingOrganizer): Promise<void> {
  actingId.value = organizer.id
  try {
    await rejectOrganizer(organizer.id)
    ElMessage.success('已駁回')
    await loadPending()
  } catch (actError) {
    ElMessage.error(toErrorMessage(actError, '駁回失敗'))
  } finally {
    actingId.value = ''
  }
}
</script>

<template>
  <div class="admin-organizers-page">
    <h1>主辦方審核</h1>
    <el-alert v-if="error" :title="error" type="error" show-icon style="margin-bottom: 16px" />

    <el-skeleton v-if="loading" :rows="3" animated />

    <template v-else>
      <p v-if="organizers.length === 0" class="empty-state">目前沒有待審核的主辦方申請。</p>

      <el-table v-else :data="organizers">
        <el-table-column prop="name" label="主辦方名稱" />
        <el-table-column prop="createdByDisplayName" label="申請人" />
        <el-table-column label="操作">
          <template #default="{ row }">
            <el-button type="primary" size="small" :loading="actingId === row.id" @click="handleApprove(row)">核准</el-button>
            <el-button type="danger" size="small" :loading="actingId === row.id" @click="handleReject(row)">駁回</el-button>
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
