<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import type { FormInstance } from 'element-plus'
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
}>({
  title: '',
  startAt: '',
  venueId: '',
  seatMapId: '',
  description: '',
  posterUrl: '',
  maxTicketsPerOrder: undefined,
})
const eventRules = {
  title: [requiredRule('請輸入活動名稱'), maxLengthRule(200, '活動名稱長度不可超過 200 字')],
  startAt: [requiredRule('請選擇開始時間')],
  venueId: [requiredRule('請選擇場館')],
  seatMapId: [requiredRule('請選擇座位圖')],
  description: [maxLengthRule(2000, '活動說明長度不可超過 2000 字')],
  posterUrl: [maxLengthRule(500, '海報網址長度不可超過 500 字')],
  maxTicketsPerOrder: [optionalPositiveIntegerRule('每筆訂單限購張數須為正整數')],
}
const eventSubmitting = ref(false)
const eventError = ref('')

async function handleCreateEvent(): Promise<void> {
  eventError.value = ''
  const valid = await eventFormRef.value?.validate().catch(() => false)
  if (!valid) return

  eventSubmitting.value = true
  try {
    await createEvent(
      eventForm.title,
      new Date(eventForm.startAt).toISOString(),
      eventForm.venueId,
      eventForm.seatMapId,
      eventForm.description || undefined,
      eventForm.posterUrl || undefined,
      eventForm.maxTicketsPerOrder,
    )
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
