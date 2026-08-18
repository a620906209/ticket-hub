<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { getEvents, getEventSeats, getTicketTypes } from '../../api/events'
import { placeOrder } from '../../api/orders'
import { useAuthStore } from '../../stores/auth'
import type { EventSeat, EventSummary, TicketType } from '../../types/apiResponses'
import type { SelectedSeat } from '../../types/ui'
import { computeHeldUntilUtc } from '../../utils/orderHold'
import { toErrorMessage } from '../../utils/errors'
import { formatCurrency } from '../../utils/currency'

const route = useRoute()
const router = useRouter()
const authStore = useAuthStore()
const eventId = route.params.id as string

const event = ref<EventSummary | null>(null)
const seats = ref<EventSeat[]>([])
const ticketTypes = ref<TicketType[]>([])
const selectedSeats = ref<SelectedSeat[]>([])
const loading = ref(false)
const submitting = ref(false)
const errorMessage = ref('')

const ticketTypeByZone = computed(() => {
  const map = new Map<string, TicketType>()
  for (const ticketType of ticketTypes.value) {
    map.set(ticketType.zoneCode, ticketType)
  }
  return map
})

// 座位數量可能到幾百個，用 Set 做 O(1) 查找，不要對每個座位都去掃一次 selectedSeats 陣列
// （原本用 .some() 掃陣列，500 個座位 x 每次選位都要重算，畫面會明顯卡頓，實測抓到才發現）。
const selectedSeatIds = computed(() => new Set(selectedSeats.value.map((s) => s.eventSeatId)))

function isSelected(seat: EventSeat): boolean {
  return selectedSeatIds.value.has(seat.eventSeatId)
}

// 依分區分組顯示，座位數量大時比一整片攤平的清單好瀏覽，也讓每個分區各自是較小的 v-for。
const seatsByZone = computed(() => {
  const map = new Map<string, EventSeat[]>()
  for (const seat of seats.value) {
    const list = map.get(seat.zoneCode)
    if (list) {
      list.push(seat)
    } else {
      map.set(seat.zoneCode, [seat])
    }
  }
  return [...map.entries()].sort(([a], [b]) => a.localeCompare(b))
})

// 每筆訂單限購張數（Admin 建立活動時設定，選填，null 代表不限制）；後端 OrderService.PlaceOrderAsync
// 也會擋一次（見 design.md），這裡只是提前給使用者清楚的提示，不是唯一的把關點。
const maxTicketsPerOrder = computed(() => event.value?.maxTicketsPerOrder ?? null)
const remainingCapacity = computed(() =>
  maxTicketsPerOrder.value === null ? Infinity : maxTicketsPerOrder.value - selectedSeats.value.length,
)

function buildSelection(seat: EventSeat): SelectedSeat | null {
  const ticketType = ticketTypeByZone.value.get(seat.zoneCode)
  if (!ticketType) {
    errorMessage.value = `分區 ${seat.zoneCode} 尚未設定票種，無法選購`
    return null
  }
  return {
    eventSeatId: seat.eventSeatId,
    zoneCode: seat.zoneCode,
    seatNumber: seat.seatNumber,
    ticketTypeId: ticketType.id,
    price: ticketType.price,
  }
}

function toggleSeat(seat: EventSeat): void {
  // 選位本身就是購票行為的第一步，未登入直接導去登入頁，登入後導回同一頁（見設計文件 buyer-web-ui spec）。
  if (!authStore.isAuthenticated) {
    router.push({ path: '/login', query: { redirect: route.fullPath } })
    return
  }
  if (seat.status !== 'Available') return

  if (isSelected(seat)) {
    selectedSeats.value = selectedSeats.value.filter((s) => s.eventSeatId !== seat.eventSeatId)
    return
  }

  if (remainingCapacity.value <= 0) {
    errorMessage.value = `這個活動每筆訂單最多購買 ${maxTicketsPerOrder.value} 張，請先取消已選的座位再選新的`
    return
  }

  const selection = buildSelection(seat)
  if (!selection) return

  selectedSeats.value = [...selectedSeats.value, selection]
}

const totalPrice = computed(() => selectedSeats.value.reduce((sum, s) => sum + s.price, 0))

// 區域隨選購票：買家不用一個一個手動點座位，選分區＋張數，系統隨機從符合條件的 Available
// 座位中抽出補滿（受每筆訂單限購張數與可售座位數雙重限制），選完直接送出訂單，不用再手動
// 滾到最下面按「送出訂單」——這是刻意的一鍵購票路徑，跟下面手動點座位＋手動送出的路徑分開。
const ALL_ZONES = '__ALL__'
const quickPickZone = ref(ALL_ZONES)
const quickPickCount = ref(1)

const zoneOptions = computed(() => seatsByZone.value.map(([zoneCode]) => zoneCode))

function shuffle<T>(items: T[]): T[] {
  const result = [...items]
  for (let i = result.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1))
    ;[result[i], result[j]] = [result[j], result[i]]
  }
  return result
}

async function handleQuickPick(): Promise<void> {
  if (!authStore.isAuthenticated) {
    router.push({ path: '/login', query: { redirect: route.fullPath } })
    return
  }
  if (quickPickCount.value <= 0) return

  errorMessage.value = ''
  const candidates = seats.value.filter(
    (seat) =>
      seat.status === 'Available' &&
      !isSelected(seat) &&
      (quickPickZone.value === ALL_ZONES || seat.zoneCode === quickPickZone.value),
  )
  const takeCount = Math.min(quickPickCount.value, candidates.length, remainingCapacity.value)
  if (takeCount <= 0) {
    errorMessage.value =
      remainingCapacity.value <= 0
        ? `這個活動每筆訂單最多購買 ${maxTicketsPerOrder.value} 張，請先取消已選的座位再選新的`
        : quickPickZone.value === ALL_ZONES
          ? '目前沒有足夠的可售座位'
          : `${quickPickZone.value} 區沒有足夠的可售座位`
    return
  }

  const picked: SelectedSeat[] = []
  for (const seat of shuffle(candidates)) {
    if (picked.length >= takeCount) break
    const selection = buildSelection(seat)
    if (selection) picked.push(selection)
  }
  selectedSeats.value = [...selectedSeats.value, ...picked]
  await handleSubmit()
}

async function loadData(): Promise<void> {
  loading.value = true
  errorMessage.value = ''
  try {
    // 沒有「查單一活動」的 API，活動列表本來就要整包抓，直接從裡面找這筆。
    const [events, seatResult, ticketTypeResult] = await Promise.all([
      getEvents(),
      getEventSeats(eventId),
      getTicketTypes(eventId),
    ])
    event.value = events.find((e) => e.id === eventId) ?? null
    seats.value = seatResult
    ticketTypes.value = ticketTypeResult
  } catch (error) {
    errorMessage.value = toErrorMessage(error, '載入活動資訊失敗')
  } finally {
    loading.value = false
  }
}

async function handleSubmit(): Promise<void> {
  if (selectedSeats.value.length === 0) return

  submitting.value = true
  errorMessage.value = ''
  try {
    const { id } = await placeOrder(
      selectedSeats.value.map((s) => ({ eventSeatId: s.eventSeatId, ticketTypeId: s.ticketTypeId })),
    )
    const heldUntilUtc = computeHeldUntilUtc()
    await router.push({ path: `/order-result/${id}`, query: { heldUntilUtc } })
  } catch (error) {
    // 401（換發也失敗）已經由 App.vue 的全域 watcher 統一導去登入頁，這裡只處理座位被搶等業務錯誤。
    errorMessage.value = toErrorMessage(error, '下單失敗，可能有座位已被搶先鎖定或售出，請重新選擇')
    await loadData()
    selectedSeats.value = []
  } finally {
    submitting.value = false
  }
}

onMounted(loadData)
</script>

<template>
  <div v-loading="loading" class="event-detail-page">
    <el-alert v-if="errorMessage" :title="errorMessage" type="error" show-icon style="margin-bottom: 16px" />

    <template v-if="event">
      <div class="layout">
        <aside class="info-column">
          <img v-if="event.posterUrl" :src="event.posterUrl" alt="" class="poster" />
          <h1>{{ event.title }}</h1>
          <p class="start-at">{{ new Date(event.startAtUtc).toLocaleString() }}</p>
          <p v-if="event.description" class="description">{{ event.description }}</p>
          <el-table :data="ticketTypes" size="small" empty-text="尚未設定票種">
            <el-table-column prop="zoneCode" label="分區" />
            <el-table-column label="票價">
              <template #default="{ row }">{{ formatCurrency(row.price) }}</template>
            </el-table-column>
          </el-table>
        </aside>

        <section class="purchase-column">
          <h2>選位購票</h2>

          <el-alert
            v-if="!authStore.isAuthenticated"
            type="info"
            :closable="false"
            title="請先登入才能選位購票"
            style="margin-bottom: 16px"
          />
          <p v-if="maxTicketsPerOrder !== null" class="limit-hint">每筆訂單限購 {{ maxTicketsPerOrder }} 張</p>

          <div class="quick-pick">
            <span class="quick-pick-label">區域隨選：</span>
            <el-select v-model="quickPickZone" style="width: 110px">
              <el-option label="全部區域" :value="ALL_ZONES" />
              <el-option v-for="zoneCode in zoneOptions" :key="zoneCode" :label="`${zoneCode} 區`" :value="zoneCode" />
            </el-select>
            <el-input-number
              v-model="quickPickCount"
              :min="1"
              :max="Number.isFinite(remainingCapacity) ? remainingCapacity : undefined"
              :step="1"
              :precision="0"
              style="width: 110px"
            />
            <el-button type="primary" :loading="submitting" @click="handleQuickPick">自動選位並送出訂單</el-button>
          </div>

          <div v-for="[zoneCode, zoneSeats] in seatsByZone" :key="zoneCode" class="zone-block">
            <h3>{{ zoneCode }} 區</h3>
            <div class="seat-grid">
              <button
                v-for="seat in zoneSeats"
                :key="seat.eventSeatId"
                type="button"
                class="seat-btn"
                :class="{ selected: isSelected(seat), sold: seat.status !== 'Available' }"
                :title="`${seat.zoneCode}-${seat.seatNumber}（${seat.status}）`"
                @click="toggleSeat(seat)"
              >
                {{ seat.seatNumber }}
              </button>
            </div>
          </div>

          <div class="summary">
            <p>已選 {{ selectedSeats.length }} 個座位，總金額 {{ formatCurrency(totalPrice) }}</p>
            <el-button
              type="primary"
              :disabled="selectedSeats.length === 0"
              :loading="submitting"
              @click="handleSubmit"
            >
              送出訂單
            </el-button>
          </div>
        </section>
      </div>
    </template>
    <el-empty v-else-if="!loading" description="找不到這個活動" />
  </div>
</template>

<style scoped>
.event-detail-page {
  max-width: 1080px;
  margin: 32px auto;
  padding: 0 16px;
}
.layout {
  display: grid;
  grid-template-columns: 320px 1fr;
  gap: 32px;
  align-items: start;
}
@media (max-width: 720px) {
  .layout {
    grid-template-columns: 1fr;
  }
}
.info-column h1 {
  margin: 12px 0 4px;
  font-size: 22px;
}
.poster {
  width: 100%;
  border-radius: 8px;
  display: block;
}
.start-at {
  color: var(--color-text-secondary);
  margin: 0 0 12px;
}
.description {
  white-space: pre-wrap;
  color: var(--color-text);
  margin: 0 0 16px;
}
.purchase-column h2 {
  margin-top: 0;
}
.limit-hint {
  color: var(--color-text-secondary);
  font-size: 13px;
  margin: 0 0 12px;
}
.quick-pick {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 20px;
  padding: 12px;
  background: var(--color-bg-elevated);
  border: 1px solid var(--color-border);
  border-radius: 6px;
}
.quick-pick-label {
  font-size: 13px;
  color: var(--color-text-secondary);
}
.zone-block {
  margin-bottom: 20px;
}
.zone-block h3 {
  margin: 0 0 8px;
  font-size: 14px;
  color: var(--color-text-secondary);
}
.seat-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  min-height: 32px;
}
.seat-btn {
  user-select: none;
  cursor: pointer;
  border: 1px solid var(--color-border);
  background: var(--color-bg-elevated);
  color: var(--color-text);
  border-radius: 4px;
  width: 36px;
  height: 28px;
  font-size: 12px;
  padding: 0;
}
.seat-btn:hover {
  border-color: var(--color-primary);
}
.seat-btn.selected {
  background: var(--el-color-primary);
  border-color: var(--el-color-primary);
  color: #fff;
}
.seat-btn.sold {
  cursor: not-allowed;
  background: var(--el-fill-color-light);
  color: var(--color-text-secondary);
  border-color: var(--color-border);
}
.summary {
  margin-top: 24px;
}
</style>
