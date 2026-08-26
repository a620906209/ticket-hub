<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import type { FormInstance } from 'element-plus'
import { createTicketType, getAdminEvents } from '../../api/admin'
import { getTicketTypes } from '../../api/events'
import type { AdminEventSummary, TicketType } from '../../types/apiResponses'
import { maxLengthRule, positiveNumberRule, requiredPositiveIntegerRule, requiredRule } from '../../utils/validators'
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
const ticketTypeForm = reactive({ eventId: '', zoneCode: '', price: 0, requiresSeat: true, availableQuantity: undefined as number | undefined })
const ticketTypeRules = {
  eventId: [requiredRule('請選擇活動')],
  zoneCode: [requiredRule('請輸入分區代碼'), maxLengthRule(50, '分區代碼長度不可超過 50 字')],
  price: [positiveNumberRule('票價須為大於 0 的數字')],
  // 此欄位在 requiresSeat 為 true（座位制）時 v-if 不會掛載，不會被 el-form 註冊進驗證範圍，
  // 不需要額外用 ticketTypeForm.requiresSeat 判斷是否跳過——見設計文件決策 4。
  availableQuantity: [requiredPositiveIntegerRule('可售總量須為大於 0 的整數')],
}
const ticketTypeSubmitting = ref(false)
const ticketTypeError = ref('')

// 切回座位制時清空可售總量，避免送出時殘留舊值（比照既有「切換場館清除座位圖」的處理慣例，見 tasks.md 2.4）。
function handleRequiresSeatChange(requiresSeat: boolean): void {
  if (requiresSeat) {
    ticketTypeForm.availableQuantity = undefined
  }
}

async function handleCreateTicketType(): Promise<void> {
  ticketTypeError.value = ''
  const valid = await ticketTypeFormRef.value?.validate().catch(() => false)
  if (!valid) return
  // 額外顯式檢查：el-form 的 rules 驗證對這個條件式必填欄位不夠可靠（實測發現連內建 required
  // 規則在特定情境下也不會擋下送出，見 tasks.md 2.3 討論），純計數票種的可售總量用明確判斷把關，
  // 不能只依賴 el-form.validate() 的結果。
  const availableQuantity = ticketTypeForm.availableQuantity
  const isValidAvailableQuantity =
    availableQuantity !== undefined && Number.isInteger(availableQuantity) && availableQuantity > 0
  if (!ticketTypeForm.requiresSeat && !isValidAvailableQuantity) {
    ticketTypeError.value = '可售總量須為大於 0 的整數'
    return
  }

  ticketTypeSubmitting.value = true
  try {
    await createTicketType(
      ticketTypeForm.eventId,
      ticketTypeForm.zoneCode,
      ticketTypeForm.price,
      ticketTypeForm.requiresSeat,
      ticketTypeForm.requiresSeat ? undefined : ticketTypeForm.availableQuantity,
    )
    ElMessage.success('票種建立成功')
    const createdForEventId = ticketTypeForm.eventId
    ticketTypeFormRef.value?.resetFields()
    await refreshTicketTypes(createdForEventId)
  } catch (error) {
    ticketTypeError.value = toErrorMessage(error, '建立票種失敗')
  } finally {
    ticketTypeSubmitting.value = false
  }
}

// 每個活動的票種清單各自快取，展開列時才查詢（見 admin-web-ui spec「Admin 可透過介面管理場館與座位圖」
// 對座位圖摘要展開的既定慣例），建立成功後強制刷新對應活動的快取，不依賴前端猜測結果。
const ticketTypesByEvent = reactive<Record<string, TicketType[]>>({})
const ticketTypesLoadingByEvent = reactive<Record<string, boolean>>({})

async function loadTicketTypesIfNeeded(eventId: string): Promise<void> {
  if (ticketTypesByEvent[eventId] || ticketTypesLoadingByEvent[eventId]) return
  await refreshTicketTypes(eventId)
}

async function refreshTicketTypes(eventId: string): Promise<void> {
  ticketTypesLoadingByEvent[eventId] = true
  try {
    ticketTypesByEvent[eventId] = await getTicketTypes(eventId)
  } catch (error) {
    ElMessage.error(toErrorMessage(error, '載入票種清單失敗'))
  } finally {
    ticketTypesLoadingByEvent[eventId] = false
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
    <el-table v-loading="loading" :data="events" empty-text="目前沒有活動" @expand-change="(row: AdminEventSummary) => loadTicketTypesIfNeeded(row.id)">
      <el-table-column type="expand">
        <template #default="{ row }">
          <el-table
            v-loading="ticketTypesLoadingByEvent[row.id]"
            :data="ticketTypesByEvent[row.id] ?? []"
            empty-text="尚無票種"
            size="small"
          >
            <el-table-column prop="zoneCode" label="票種名稱／分區代碼" />
            <el-table-column label="票價">
              <template #default="{ row: ticketType }">NT$ {{ ticketType.price }}</template>
            </el-table-column>
            <el-table-column label="模式">
              <template #default="{ row: ticketType }">{{ ticketType.requiresSeat ? '座位制' : '計數制' }}</template>
            </el-table-column>
            <el-table-column label="可售總量">
              <template #default="{ row: ticketType }">{{ ticketType.requiresSeat ? '—' : ticketType.availableQuantity }}</template>
            </el-table-column>
          </el-table>
        </template>
      </el-table-column>
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
      <el-form-item label="是否綁座位">
        <el-switch v-model="ticketTypeForm.requiresSeat" @change="handleRequiresSeatChange" />
      </el-form-item>
      <el-form-item :label="ticketTypeForm.requiresSeat ? '分區代碼' : '票種名稱'" prop="zoneCode">
        <el-input v-model="ticketTypeForm.zoneCode" maxlength="50" />
      </el-form-item>
      <el-form-item label="票價" prop="price">
        <span class="currency-prefix">NT$</span>
        <el-input-number v-model="ticketTypeForm.price" :min="0.01" :step="0.01" :precision="2" />
      </el-form-item>
      <el-form-item v-if="!ticketTypeForm.requiresSeat" label="可售總量" prop="availableQuantity">
        <el-input-number v-model="ticketTypeForm.availableQuantity" :min="1" :step="1" :precision="0" />
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
