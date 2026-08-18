<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import type { FormInstance } from 'element-plus'
import { createSeatMap, createVenue, getSeatMapById, getVenueById, getVenues } from '../../api/admin'
import type { SeatDetail, SeatMapSummary, VenueSummary } from '../../types/apiResponses'
import { maxLengthRule, requiredRule } from '../../utils/validators'
import { toErrorMessage } from '../../utils/errors'

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

const selectedVenueId = ref('')
const selectedVenueSeatMaps = ref<SeatMapSummary[]>([])
const venueDetailLoading = ref(false)
const venueDetailError = ref('')
// 記錄目前這次 getVenueById 呼叫對應的場館，回應抵達時如果選定的場館已經變了（快速切換場館），
// 就捨棄這次回應、不套用結果，避免較晚抵達的舊回應覆蓋掉使用者較新的選擇（同樣的防護見
// EventListPage.vue 的 handleVenueChange）。
let venueDetailRequestVenueId = ''

async function loadSelectedVenueDetail(): Promise<void> {
  const venueId = selectedVenueId.value
  if (!venueId) return
  venueDetailRequestVenueId = venueId
  venueDetailLoading.value = true
  venueDetailError.value = ''
  try {
    const detail = await getVenueById(venueId)
    if (venueDetailRequestVenueId !== venueId) return
    selectedVenueSeatMaps.value = detail.seatMaps
  } catch (error) {
    if (venueDetailRequestVenueId !== venueId) return
    venueDetailError.value = toErrorMessage(error, '載入場館明細失敗')
  } finally {
    if (venueDetailRequestVenueId === venueId) {
      venueDetailLoading.value = false
    }
  }
}

function selectVenue(venueId: string): void {
  selectedVenueId.value = venueId
  selectedVenueSeatMaps.value = []
  loadSelectedVenueDetail()
}

interface SeatMapDetailState {
  loading: boolean
  error: string
  seats: SeatDetail[] | null
}

// 座位圖摘要只有 Id／座位數（decision 2），要看完整座位清單（分區代碼＋座位號碼）才呼叫
// getSeatMapById；用 seatMapId 當 key 快取，展開過的座位圖不重複打 API。
const seatMapDetails = reactive<Record<string, SeatMapDetailState>>({})

async function handleExpandSeatMap(seatMap: SeatMapSummary, expandedRows: SeatMapSummary[]): Promise<void> {
  const isExpanding = expandedRows.some((row) => row.id === seatMap.id)
  if (!isExpanding || seatMapDetails[seatMap.id]) return

  seatMapDetails[seatMap.id] = { loading: true, error: '', seats: null }
  try {
    const detail = await getSeatMapById(selectedVenueId.value, seatMap.id)
    seatMapDetails[seatMap.id] = { loading: false, error: '', seats: detail.seats }
  } catch (error) {
    seatMapDetails[seatMap.id] = { loading: false, error: toErrorMessage(error, '載入座位清單失敗'), seats: null }
  }
}

const venueFormRef = ref<FormInstance>()
const venueForm = reactive({ name: '' })
const venueRules = { name: [requiredRule('請輸入場館名稱'), maxLengthRule(200, '場館名稱長度不可超過 200 字')] }
const venueSubmitting = ref(false)
const venueError = ref('')

async function handleCreateVenue(): Promise<void> {
  venueError.value = ''
  const valid = await venueFormRef.value?.validate().catch(() => false)
  if (!valid) return

  venueSubmitting.value = true
  try {
    await createVenue(venueForm.name)
    ElMessage.success('場館建立成功')
    venueForm.name = ''
    venueFormRef.value?.resetFields()
    await loadVenues()
  } catch (error) {
    venueError.value = toErrorMessage(error, '建立場館失敗')
  } finally {
    venueSubmitting.value = false
  }
}

interface SeatRow {
  zoneCode: string
  seatNumber: string
}

interface SeatBatch {
  zoneCode: string
  start: number
  end: number
}

const seatMapVenueId = ref('')
// 單筆手動新增，給少量、非連號的座位用。
const seatRows = ref<SeatRow[]>([{ zoneCode: '', seatNumber: '' }])
// 批次產生，給大量連號座位用（例如一個分區 100 個座位）——不會把每個座位都渲染成一個輸入框，
// 只顯示「這一批」的摘要，送出時才展開成實際的座位陣列。
const seatBatches = ref<SeatBatch[]>([])
const batchDraft = reactive({ zoneCode: '', start: 1, end: 100 })
const seatMapSubmitting = ref(false)
const seatMapError = ref('')

function addSeatRow(): void {
  seatRows.value.push({ zoneCode: '', seatNumber: '' })
}

function removeSeatRow(index: number): void {
  if (seatRows.value.length <= 1) return
  seatRows.value.splice(index, 1)
}

function addSeatBatch(): void {
  if (!batchDraft.zoneCode || batchDraft.start > batchDraft.end) return
  seatBatches.value.push({ zoneCode: batchDraft.zoneCode, start: batchDraft.start, end: batchDraft.end })
  batchDraft.zoneCode = ''
}

function removeSeatBatch(index: number): void {
  seatBatches.value.splice(index, 1)
}

function expandBatches(): SeatRow[] {
  const rows: SeatRow[] = []
  for (const batch of seatBatches.value) {
    for (let n = batch.start; n <= batch.end; n++) {
      rows.push({ zoneCode: batch.zoneCode, seatNumber: String(n) })
    }
  }
  return rows
}

async function handleCreateSeatMap(): Promise<void> {
  seatMapError.value = ''
  if (!seatMapVenueId.value) {
    seatMapError.value = '請選擇場館'
    return
  }
  const manualSeats = seatRows.value.filter((row) => row.zoneCode && row.seatNumber)
  const seats = [...manualSeats, ...expandBatches()]
  if (seats.length === 0) {
    seatMapError.value = '請至少新增一個座位（手動新增或批次產生）'
    return
  }

  seatMapSubmitting.value = true
  try {
    await createSeatMap(seatMapVenueId.value, seats)
    ElMessage.success(`座位圖建立成功，共 ${seats.length} 個座位`)
    seatRows.value = [{ zoneCode: '', seatNumber: '' }]
    seatBatches.value = []
    if (selectedVenueId.value === seatMapVenueId.value) {
      await loadSelectedVenueDetail()
    }
  } catch (error) {
    seatMapError.value = toErrorMessage(error, '建立座位圖失敗')
  } finally {
    seatMapSubmitting.value = false
  }
}
</script>

<template>
  <div class="venue-list-page">
    <div class="header">
      <h1>場館管理</h1>
      <el-button :loading="venuesLoading" @click="loadVenues">重新整理</el-button>
    </div>
    <el-alert v-if="venuesError" :title="venuesError" type="error" show-icon style="margin-bottom: 16px" />
    <el-table
      v-loading="venuesLoading"
      :data="venues"
      empty-text="尚未建立任何場館"
      highlight-current-row
      @row-click="(row: VenueSummary) => selectVenue(row.id)"
    >
      <el-table-column prop="name" label="場館名稱" />
      <el-table-column prop="id" label="Id" />
    </el-table>

    <div v-if="selectedVenueId" class="seat-map-summary">
      <h2>座位圖（{{ venues.find((v) => v.id === selectedVenueId)?.name }}）</h2>
      <el-alert v-if="venueDetailError" :title="venueDetailError" type="error" show-icon style="margin-bottom: 16px" />
      <el-table
        v-loading="venueDetailLoading"
        :data="selectedVenueSeatMaps"
        empty-text="這個場館還沒有任何座位圖"
        @expand-change="handleExpandSeatMap"
      >
        <el-table-column type="expand">
          <template #default="{ row }">
            <div v-if="seatMapDetails[row.id]?.loading">載入座位清單中…</div>
            <el-alert
              v-else-if="seatMapDetails[row.id]?.error"
              :title="seatMapDetails[row.id]?.error"
              type="error"
              show-icon
            />
            <div v-else-if="seatMapDetails[row.id]?.seats" class="seat-detail-list">
              <span v-for="seat in seatMapDetails[row.id]?.seats" :key="seat.id" class="seat-chip">
                {{ seat.zoneCode }}-{{ seat.seatNumber }}
              </span>
            </div>
          </template>
        </el-table-column>
        <el-table-column prop="id" label="座位圖 Id" />
        <el-table-column prop="seatCount" label="座位數" width="120" />
      </el-table>
    </div>

    <h2>建立場館</h2>
    <el-alert v-if="venueError" :title="venueError" type="error" show-icon style="margin-bottom: 16px" />
    <el-form ref="venueFormRef" :model="venueForm" :rules="venueRules" inline @submit.prevent="handleCreateVenue">
      <el-form-item label="場館名稱" prop="name">
        <el-input v-model="venueForm.name" maxlength="200" />
      </el-form-item>
      <el-form-item>
        <el-button type="primary" :loading="venueSubmitting" native-type="submit">建立</el-button>
      </el-form-item>
    </el-form>

    <h2>建立座位圖</h2>
    <el-alert v-if="seatMapError" :title="seatMapError" type="error" show-icon style="margin-bottom: 16px" />
    <el-form label-width="80px" @submit.prevent="handleCreateSeatMap">
      <el-form-item label="場館">
        <el-select v-model="seatMapVenueId" placeholder="請選擇場館">
          <el-option v-for="venue in venues" :key="venue.id" :label="venue.name" :value="venue.id" />
        </el-select>
      </el-form-item>
      <el-form-item label="批次產生">
        <div class="batch-draft">
          <el-input v-model="batchDraft.zoneCode" placeholder="分區代碼" style="width: 120px" maxlength="50" />
          <span>從</span>
          <el-input-number v-model="batchDraft.start" :min="1" :step="1" :precision="0" controls-position="right" style="width: 110px" />
          <span>到</span>
          <el-input-number v-model="batchDraft.end" :min="1" :step="1" :precision="0" controls-position="right" style="width: 110px" />
          <el-button @click="addSeatBatch">加入這批</el-button>
        </div>
        <p class="hint">
          例如分區代碼 A、從 1 到 100，會產生 A 區 100 個座位（A-1 ~ A-100），大量座位建議用這個，
          不要一個一個手動新增。
        </p>
        <div v-if="seatBatches.length" class="batch-list">
          <div v-for="(batch, index) in seatBatches" :key="index" class="batch-item">
            <span>{{ batch.zoneCode }} 區：{{ batch.start }} ~ {{ batch.end }}（{{ batch.end - batch.start + 1 }} 個）</span>
            <el-button text type="danger" @click="removeSeatBatch(index)">移除</el-button>
          </div>
        </div>
      </el-form-item>
      <el-form-item label="手動新增">
        <div v-for="(row, index) in seatRows" :key="index" class="seat-row">
          <el-input v-model="row.zoneCode" placeholder="分區代碼" style="width: 140px" maxlength="50" />
          <el-input v-model="row.seatNumber" placeholder="座位號碼" style="width: 140px" maxlength="50" />
          <el-button text type="danger" :disabled="seatRows.length <= 1" @click="removeSeatRow(index)">
            移除
          </el-button>
        </div>
        <el-button text @click="addSeatRow">+ 新增單一座位</el-button>
      </el-form-item>
      <el-form-item>
        <el-button type="primary" :loading="seatMapSubmitting" native-type="submit">建立座位圖</el-button>
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
.seat-map-summary {
  margin: 24px 0;
}
.seat-detail-list {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 8px 16px;
}
.seat-chip {
  padding: 2px 8px;
  border-radius: 4px;
  background: var(--el-fill-color-light);
  font-size: 12px;
}
.hint {
  color: var(--el-text-color-secondary);
}
.seat-row {
  display: flex;
  gap: 8px;
  margin-bottom: 8px;
  align-items: center;
}
.batch-draft {
  display: flex;
  gap: 8px;
  align-items: center;
}
.batch-list {
  margin-top: 8px;
}
.batch-item {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 4px 0;
}
</style>
