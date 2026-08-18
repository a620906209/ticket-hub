<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import type { FormInstance } from 'element-plus'
import { createTicketType, getAdminEvents } from '../../api/admin'
import type { AdminEventSummary } from '../../types/apiResponses'
import { maxLengthRule, positiveNumberRule, requiredRule } from '../../utils/validators'
import { toErrorMessage } from '../../utils/errors'

const events = ref<AdminEventSummary[]>([])
const loading = ref(false)
const listError = ref('')

async function loadEvents(): Promise<void> {
  loading.value = true
  listError.value = ''
  try {
    events.value = await getAdminEvents()
  } catch (error) {
    listError.value = toErrorMessage(error, '載入活動列表失敗')
  } finally {
    loading.value = false
  }
}

onMounted(loadEvents)

// 活動還沒有座位理論上不會發生（建立活動當下就會依座位圖產生 EventSeat），仍防呆處理（見設計文件決策 6）。
function totalSeatCount(row: AdminEventSummary): number {
  return row.availableSeatCount + row.heldSeatCount + row.soldSeatCount
}

const ticketTypeFormRef = ref<FormInstance>()
const ticketTypeForm = reactive({ eventId: '', zoneCode: '', price: 0 })
const ticketTypeRules = {
  eventId: [requiredRule('請選擇活動')],
  zoneCode: [requiredRule('請輸入分區代碼'), maxLengthRule(50, '分區代碼長度不可超過 50 字')],
  price: [positiveNumberRule('票價須為大於 0 的數字')],
}
const ticketTypeSubmitting = ref(false)
const ticketTypeError = ref('')

async function handleCreateTicketType(): Promise<void> {
  ticketTypeError.value = ''
  const valid = await ticketTypeFormRef.value?.validate().catch(() => false)
  if (!valid) return

  ticketTypeSubmitting.value = true
  try {
    await createTicketType(ticketTypeForm.eventId, ticketTypeForm.zoneCode, ticketTypeForm.price)
    ElMessage.success('票種建立成功')
    ticketTypeFormRef.value?.resetFields()
  } catch (error) {
    ticketTypeError.value = toErrorMessage(error, '建立票種失敗')
  } finally {
    ticketTypeSubmitting.value = false
  }
}
</script>

<template>
  <div class="admin-event-list-page">
    <div class="header">
      <h1>活動管理</h1>
      <router-link :to="{ name: 'admin-event-create' }">
        <el-button type="primary">建立活動</el-button>
      </router-link>
    </div>
    <p class="hint">活動列表的場館／座位圖欄位顯示原始 Id，無法顯示名稱。</p>

    <el-alert v-if="listError" :title="listError" type="error" show-icon style="margin-bottom: 16px" />
    <el-table v-loading="loading" :data="events" empty-text="目前沒有活動">
      <el-table-column prop="title" label="活動名稱" />
      <el-table-column label="開始時間">
        <template #default="{ row }">{{ new Date(row.startAtUtc).toLocaleString() }}</template>
      </el-table-column>
      <el-table-column prop="venueId" label="場館 Id" />
      <el-table-column prop="seatMapId" label="座位圖 Id" />
      <el-table-column label="建立者">
        <template #default="{ row }">{{ row.createdByDisplayName ?? '—' }}</template>
      </el-table-column>
      <el-table-column label="建立時間">
        <template #default="{ row }">{{ row.createdAtUtc ? new Date(row.createdAtUtc).toLocaleString() : '—' }}</template>
      </el-table-column>
      <el-table-column label="售票狀況" width="180">
        <template #default="{ row }">
          <div v-if="totalSeatCount(row) === 0" class="seat-status-empty">尚無座位資料</div>
          <div v-else class="seat-status-bar">
            <div
              v-if="row.availableSeatCount > 0"
              class="seat-status-segment available"
              :style="{ flex: row.availableSeatCount }"
              :title="`可售 ${row.availableSeatCount}`"
            />
            <div
              v-if="row.heldSeatCount > 0"
              class="seat-status-segment held"
              :style="{ flex: row.heldSeatCount }"
              :title="`保留中 ${row.heldSeatCount}`"
            />
            <div
              v-if="row.soldSeatCount > 0"
              class="seat-status-segment sold"
              :style="{ flex: row.soldSeatCount }"
              :title="`已售出 ${row.soldSeatCount}`"
            />
          </div>
        </template>
      </el-table-column>
    </el-table>

    <h2>建立票種</h2>
    <el-alert v-if="ticketTypeError" :title="ticketTypeError" type="error" show-icon style="margin-bottom: 16px" />
    <el-form
      ref="ticketTypeFormRef"
      :model="ticketTypeForm"
      :rules="ticketTypeRules"
      label-width="100px"
      @submit.prevent="handleCreateTicketType"
    >
      <el-form-item label="活動" prop="eventId">
        <el-select v-model="ticketTypeForm.eventId" placeholder="請選擇活動">
          <el-option v-for="event in events" :key="event.id" :label="event.title" :value="event.id" />
        </el-select>
      </el-form-item>
      <el-form-item label="分區代碼" prop="zoneCode">
        <el-input v-model="ticketTypeForm.zoneCode" maxlength="50" />
      </el-form-item>
      <el-form-item label="票價" prop="price">
        <span class="currency-prefix">NT$</span>
        <el-input-number v-model="ticketTypeForm.price" :min="0.01" :step="0.01" :precision="2" />
      </el-form-item>
      <el-form-item>
        <el-button type="primary" :loading="ticketTypeSubmitting" native-type="submit">建立</el-button>
      </el-form-item>
    </el-form>
  </div>
</template>

<style scoped>
.header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
.hint {
  color: var(--el-text-color-secondary);
}
.currency-prefix {
  margin-right: 8px;
  color: var(--el-text-color-secondary);
}
.seat-status-bar {
  display: flex;
  height: 16px;
  width: 100%;
  overflow: hidden;
  border-radius: 4px;
  background-color: var(--el-fill-color-light);
}
.seat-status-segment.available {
  background-color: var(--el-color-success);
}
.seat-status-segment.held {
  background-color: var(--el-color-warning);
}
.seat-status-segment.sold {
  background-color: var(--el-color-danger);
}
.seat-status-empty {
  font-size: 12px;
  color: var(--el-text-color-secondary);
}
</style>
