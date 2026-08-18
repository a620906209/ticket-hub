<script setup lang="ts">
import { reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import type { FormInstance } from 'element-plus'
import { createSeatMap, createVenue } from '../../api/admin'
import { useAdminVenueCacheStore } from '../../stores/adminVenueCache'
import { maxLengthRule, requiredRule } from '../../utils/validators'
import { toErrorMessage } from '../../utils/errors'

const cache = useAdminVenueCacheStore()

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
    const { id } = await createVenue(venueForm.name)
    cache.addVenue({ id, name: venueForm.name })
    ElMessage.success(`場館建立成功，Id：${id}`)
    venueForm.name = ''
    venueFormRef.value?.resetFields()
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
    const { id } = await createSeatMap(seatMapVenueId.value, seats)
    cache.addSeatMap({ id, venueId: seatMapVenueId.value, seatCount: seats.length })
    ElMessage.success(`座位圖建立成功，Id：${id}，共 ${seats.length} 個座位`)
    seatRows.value = [{ zoneCode: '', seatNumber: '' }]
    seatBatches.value = []
  } catch (error) {
    seatMapError.value = toErrorMessage(error, '建立座位圖失敗')
  } finally {
    seatMapSubmitting.value = false
  }
}
</script>

<template>
  <div class="venue-list-page">
    <h1>場館管理</h1>
    <p class="hint">
      後端目前沒有場館/座位圖查詢 API，以下清單只顯示本次瀏覽器分頁建立過的紀錄，重新整理頁面即消失。
    </p>

    <el-table :data="cache.venues" empty-text="尚未建立任何場館">
      <el-table-column prop="name" label="場館名稱" />
      <el-table-column prop="id" label="Id" />
    </el-table>

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
          <el-option v-for="venue in cache.venues" :key="venue.id" :label="venue.name" :value="venue.id" />
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
