<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import type { FormInstance, FormItemRule } from 'element-plus'
import { createEvent, getVenueById, getVenues } from '../../api/admin'
import type { SeatMapSummary, VenueSummary } from '../../types/apiResponses'
import { maxLengthRule, optionalPositiveIntegerRule, requiredRule } from '../../utils/validators'
import { toErrorMessage } from '../../utils/errors'

const router = useRouter()

const venues = ref<VenueSummary[]>([])
const venuesLoading = ref(false)
const venuesError = ref('')

async function loadVenues(): Promise<void> {
  venuesLoading.value = true
  venuesError.value = ''
  try {
    venues.value = await getVenues()
  } catch (error) {
    venuesError.value = toErrorMessage(error, '載入場館列表失敗')
  } finally {
    venuesLoading.value = false
  }
}

onMounted(loadVenues)

const seatMapOptions = ref<SeatMapSummary[]>([])
const seatMapOptionsLoading = ref(false)
const seatMapOptionsError = ref('')
// 記錄目前這次 getVenueById 呼叫對應的場館，回應抵達時如果選定的場館已經變了（快速切換場館），
// 就捨棄這次回應、不套用結果，避免較晚抵達的舊回應覆蓋掉使用者較新的選擇。
let seatMapOptionsRequestVenueId = ''

async function handleVenueChange(venueId: string): Promise<void> {
  eventForm.seatMapId = ''
  seatMapOptions.value = []
  seatMapOptionsError.value = ''
  if (!venueId) return

  seatMapOptionsRequestVenueId = venueId
  seatMapOptionsLoading.value = true
  try {
    const detail = await getVenueById(venueId)
    if (seatMapOptionsRequestVenueId !== venueId) return
    seatMapOptions.value = detail.seatMaps
  } catch (error) {
    if (seatMapOptionsRequestVenueId !== venueId) return
    seatMapOptionsError.value = toErrorMessage(error, '載入座位圖列表失敗')
  } finally {
    if (seatMapOptionsRequestVenueId === venueId) {
      seatMapOptionsLoading.value = false
    }
  }
}

const eventFormRef = ref<FormInstance>()
const eventForm = reactive<{
  title: string
  startAt: string
  venueId: string
  seatMapId: string
  description: string
  posterUrl: string
  maxTicketsPerOrder: number | undefined
  isRealNameRequired: boolean
  salesStartAt: Date | string | null
  salesEndAt: Date | string | null
}>({
  title: '',
  startAt: '',
  venueId: '',
  seatMapId: '',
  description: '',
  posterUrl: '',
  maxTicketsPerOrder: undefined,
  isRealNameRequired: false,
  salesStartAt: '',
  salesEndAt: '',
})

// el-date-picker 選了再清空時 v-model 為 null、初始為空字串，一律以 truthy 判斷有無值；
// 不可對 null 呼叫 new Date()，否則會變成 1970-01-01。
function toTimeMs(value: Date | string | null): number | null {
  return value ? new Date(value).getTime() : null
}

const salesEndAtRule: FormItemRule = {
  validator: (_rule, value: Date | string | null, callback) => {
    const salesEndMs = toTimeMs(value)
    const startMs = toTimeMs(eventForm.startAt)
    if (salesEndMs !== null && startMs !== null && salesEndMs > startMs) {
      callback(new Error('停售時間不可晚於活動開始時間'))
      return
    }
    callback()
  },
}
const salesStartAtRule: FormItemRule = {
  validator: (_rule, value: Date | string | null, callback) => {
    const salesStartMs = toTimeMs(value)
    const effectiveEndMs = toTimeMs(eventForm.salesEndAt) ?? toTimeMs(eventForm.startAt)
    if (salesStartMs !== null && effectiveEndMs !== null && salesStartMs >= effectiveEndMs) {
      callback(new Error('開賣時間須早於停售時間（未填停售時間時為活動開始時間）'))
      return
    }
    callback()
  },
}
const eventRules = {
  title: [requiredRule('請輸入活動名稱'), maxLengthRule(200, '活動名稱長度不可超過 200 字')],
  startAt: [requiredRule('請選擇開始時間')],
  venueId: [requiredRule('請選擇場館')],
  seatMapId: [requiredRule('請選擇座位圖')],
  description: [maxLengthRule(2000, '活動說明長度不可超過 2000 字')],
  posterUrl: [maxLengthRule(500, '海報網址長度不可超過 500 字')],
  maxTicketsPerOrder: [optionalPositiveIntegerRule('每筆訂單限購張數須為正整數')],
  salesStartAt: [salesStartAtRule],
  salesEndAt: [salesEndAtRule],
}

// 販售期間的驗證依賴其他欄位，依賴欄位變更時重新驗證，避免舊錯誤訊息殘留。
// validateField 驗證失敗時會 reject，錯誤已由 el-form-item 顯示，這裡不需再處理。
function revalidateFields(fields: string[]): void {
  void eventFormRef.value?.validateField(fields).catch(() => undefined)
}
watch(
  () => eventForm.startAt,
  () => revalidateFields(['salesStartAt', 'salesEndAt']),
)
watch(
  () => eventForm.salesEndAt,
  () => revalidateFields(['salesStartAt']),
)

function toOptionalIsoString(value: Date | string | null): string | undefined {
  return value ? new Date(value).toISOString() : undefined
}
const eventSubmitting = ref(false)
const eventError = ref('')

async function handleCreateEvent(): Promise<void> {
  eventError.value = ''
  const valid = await eventFormRef.value?.validate().catch(() => false)
  if (!valid) return

  eventSubmitting.value = true
  try {
    await createEvent({
      title: eventForm.title,
      startAtUtc: new Date(eventForm.startAt).toISOString(),
      venueId: eventForm.venueId,
      seatMapId: eventForm.seatMapId,
      description: eventForm.description || undefined,
      posterUrl: eventForm.posterUrl || undefined,
      maxTicketsPerOrder: eventForm.maxTicketsPerOrder,
      isRealNameRequired: eventForm.isRealNameRequired,
      salesStartAtUtc: toOptionalIsoString(eventForm.salesStartAt),
      salesEndAtUtc: toOptionalIsoString(eventForm.salesEndAt),
    })
    ElMessage.success('活動建立成功')
    await router.push({ name: 'admin-events' })
  } catch (error) {
    eventError.value = toErrorMessage(error, '建立活動失敗，請確認場館/座位圖是否存在')
  } finally {
    eventSubmitting.value = false
  }
}
</script>

<template>
  <div class="event-create-page">
    <div class="header">
      <h1>建立活動</h1>
      <router-link :to="{ name: 'admin-events' }">回活動列表</router-link>
    </div>
    <el-alert v-if="eventError" :title="eventError" type="error" show-icon style="margin-bottom: 16px" />
    <el-form ref="eventFormRef" :model="eventForm" :rules="eventRules" label-width="100px" @submit.prevent="handleCreateEvent">
      <el-form-item label="活動名稱" prop="title">
        <el-input v-model="eventForm.title" maxlength="200" />
      </el-form-item>
      <el-form-item label="開始時間" prop="startAt">
        <el-date-picker v-model="eventForm.startAt" type="datetime" placeholder="選擇日期時間" />
      </el-form-item>
      <el-form-item label="開賣時間" prop="salesStartAt">
        <el-date-picker v-model="eventForm.salesStartAt" type="datetime" placeholder="選填" />
        <span class="field-hint">留空代表建立後立即開賣；建立後不可變更</span>
      </el-form-item>
      <el-form-item label="停售時間" prop="salesEndAt">
        <el-date-picker v-model="eventForm.salesEndAt" type="datetime" placeholder="選填" />
        <span class="field-hint">留空代表活動開始時停售；建立後不可變更</span>
      </el-form-item>
      <el-form-item label="場館" prop="venueId">
        <el-alert v-if="venuesError" :title="venuesError" type="error" show-icon style="margin-bottom: 8px" />
        <el-select
          v-model="eventForm.venueId"
          placeholder="請選擇場館"
          :loading="venuesLoading"
          no-data-text="尚無可選項目"
          @change="handleVenueChange"
        >
          <el-option v-for="venue in venues" :key="venue.id" :label="venue.name" :value="venue.id" />
        </el-select>
      </el-form-item>
      <el-form-item label="座位圖" prop="seatMapId">
        <el-alert v-if="seatMapOptionsError" :title="seatMapOptionsError" type="error" show-icon style="margin-bottom: 8px" />
        <el-select
          v-model="eventForm.seatMapId"
          placeholder="請先選擇場館"
          :disabled="!eventForm.venueId"
          :loading="seatMapOptionsLoading"
          no-data-text="尚無可選項目"
        >
          <el-option
            v-for="seatMap in seatMapOptions"
            :key="seatMap.id"
            :label="`${seatMap.id.slice(0, 8)}…（${seatMap.seatCount} 個座位）`"
            :value="seatMap.id"
          />
        </el-select>
      </el-form-item>
      <el-form-item label="活動說明" prop="description">
        <el-input v-model="eventForm.description" type="textarea" :rows="3" maxlength="2000" placeholder="選填" />
      </el-form-item>
      <el-form-item label="海報網址" prop="posterUrl">
        <el-input v-model="eventForm.posterUrl" maxlength="500" placeholder="選填，貼圖片網址" />
      </el-form-item>
      <el-form-item label="每筆訂單限購" prop="maxTicketsPerOrder">
        <el-input-number v-model="eventForm.maxTicketsPerOrder" :min="1" :step="1" :precision="0" />
        <span class="field-hint">選填，留空代表不限制</span>
      </el-form-item>
      <el-form-item label="需實名" prop="isRealNameRequired">
        <el-checkbox v-model="eventForm.isRealNameRequired" name="isRealNameRequired">需實名</el-checkbox>
        <span class="field-hint">建立後不可變更；買家需先登記實名才能購票，入場時需核對證件</span>
      </el-form-item>
      <el-form-item>
        <el-button type="primary" :loading="eventSubmitting" native-type="submit">建立</el-button>
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
.field-hint {
  margin-left: 8px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}
</style>
