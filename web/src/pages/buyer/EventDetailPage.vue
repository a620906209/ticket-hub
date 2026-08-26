<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { getEvents, getEventSeats, getTicketTypes } from '../../api/events'
import { placeOrder } from '../../api/orders'
import type { PlaceOrderSelection } from '../../api/orders'
import { ApiError } from '../../api/httpClient'
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

// 只納入座位制票種：純計數票種的 zoneCode 是自由顯示名稱，可能剛好跟某個座位分區同名，
// 若也放進這個 Map 可能覆寫掉真正的座位分區對照，導致座位選購組出「RequiresSeat = false
// 卻帶 EventSeatId」的無效項目、被後端拒絕。
const ticketTypeByZone = computed(() => {
  const map = new Map<string, TicketType>()
  for (const ticketType of ticketTypes.value) {
    if (!ticketType.requiresSeat) continue
    map.set(ticketType.zoneCode, ticketType)
  }
  return map
})

// 純計數票種（不綁座位）走獨立的「計數購票」區塊，座位網格與區域隨選只處理座位制票種。
const countTicketTypes = computed(() => ticketTypes.value.filter((t) => !t.requiresSeat))

// 每個純計數票種各自的購買數量輸入值；0 代表不購買，送出訂單時過濾掉（見 design.md 決策 1）。
const countQuantities = reactive<Record<string, number>>({})
const countTotal = computed(() => Object.values(countQuantities).reduce((sum, n) => sum + (n || 0), 0))

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
// 座位選購與計數購買共用同一份剩餘額度（design.md 決策 3），任一方消耗額度後另一方的可選/可輸入上限立即跟著變動。
const maxTicketsPerOrder = computed(() => event.value?.maxTicketsPerOrder ?? null)
const remainingCapacity = computed(() =>
  maxTicketsPerOrder.value === null ? Infinity : maxTicketsPerOrder.value - selectedSeats.value.length - countTotal.value,
)

// 限制型輸入（design.md 決策 6）：計算結果直接當作 el-input-number 的 max，元件本身擋掉超額輸入，
// 不需要另外顯示「超過上限」的錯誤訊息。availableQuantity 為 null 屬不應發生的資料異常（task 3.9），
// 直接回傳 0 讓輸入框鎖住，不讓 null 參與 Math.min 運算。
function countMaxFor(ticketType: TicketType): number {
  if (ticketType.availableQuantity === null) return 0
  const ownValue = countQuantities[ticketType.id] ?? 0
  if (remainingCapacity.value === Infinity) return ticketType.availableQuantity
  return Math.max(0, Math.min(ticketType.availableQuantity, remainingCapacity.value + ownValue))
}

// 未登入時調整計數數量比照既有選位規則，互動當下立即攔截導向登入頁，不套用該次變更
// （design.md 決策 7）；因為攔截發生在寫入 countQuantities 之前，沒有暫存值需要還原。
function handleCountChange(ticketType: TicketType, rawValue: number | undefined): void {
  if (!authStore.isAuthenticated) {
    router.push({ path: '/login', query: { redirect: route.fullPath } })
    return
  }
  countQuantities[ticketType.id] = rawValue ?? 0
}

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

const totalPrice = computed(
  () =>
    selectedSeats.value.reduce((sum, s) => sum + s.price, 0) +
    countTicketTypes.value.reduce((sum, t) => sum + (countQuantities[t.id] ?? 0) * t.price, 0),
)

// 區域隨選購票：買家不用一個一個手動點座位，選分區＋張數，系統隨機從符合條件的 Available
// 座位中抽出補滿（受每筆訂單限購張數與可售座位數雙重限制），選完直接送出訂單，不用再手動
// 滾到最下面按「送出訂單」——這是刻意的一鍵購票路徑，跟下面手動點座位＋手動送出的路徑分開。
const ALL_ZONES = '__ALL__'
const quickPickZone = ref(ALL_ZONES)
const quickPickCount = ref(1)

// 只列出「有座位制票種對應」的分區：座位是依座位圖建立的，票種是 Admin 另外設定的，
// 兩者不保證同步——某分區可能還沒有對應的 RequiresSeat = true 票種。若把這種分區也
// 放進選項/候選池，buildSelection() 會回傳 null，抽到這種座位就會讓實際下單張數少於
// 買家要求的張數，重新引入「要求數量與實際下單數量不一致」的問題。
const zoneOptions = computed(() =>
  seatsByZone.value.filter(([zoneCode]) => ticketTypeByZone.value.has(zoneCode)).map(([zoneCode]) => zoneCode),
)

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
      ticketTypeByZone.value.has(seat.zoneCode) &&
      (quickPickZone.value === ALL_ZONES || seat.zoneCode === quickPickZone.value),
  )
  // 張數不足時 MUST 顯示錯誤、不加入任何座位、不呼叫下單 API——不能用 Math.min() 靜默縮減成
  // 實際可抽的數量直接送出，那樣送出的訂單張數會跟買家要求的不一致（spec Scenario「區域隨選
  // 張數超過可售座位或限購剩餘額度」）。
  if (quickPickCount.value > remainingCapacity.value) {
    errorMessage.value = `這個活動每筆訂單最多購買 ${maxTicketsPerOrder.value} 張，請先取消已選的座位再選新的`
    return
  }
  if (quickPickCount.value > candidates.length) {
    errorMessage.value =
      quickPickZone.value === ALL_ZONES ? '目前沒有足夠的可售座位' : `${quickPickZone.value} 區沒有足夠的可售座位`
    return
  }

  const picked: SelectedSeat[] = []
  for (const seat of shuffle(candidates)) {
    if (picked.length >= quickPickCount.value) break
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

function buildCountSelections(): PlaceOrderSelection[] {
  return countTicketTypes.value
    .filter((t) => (countQuantities[t.id] ?? 0) > 0)
    .map((t) => ({ eventSeatId: null, ticketTypeId: t.id, quantity: countQuantities[t.id] }))
}

function clearSelections(): void {
  selectedSeats.value = []
  for (const ticketTypeId of Object.keys(countQuantities)) {
    countQuantities[ticketTypeId] = 0
  }
}

async function handleSubmit(): Promise<void> {
  const countSelections = buildCountSelections()
  if (selectedSeats.value.length === 0 && countSelections.length === 0) return

  // 送出前重新加總，不只依賴各輸入元件的 max 屬性把關（design.md 決策 3 風險緩解）。
  const totalCount = selectedSeats.value.length + countSelections.reduce((sum, s) => sum + (s.quantity ?? 0), 0)
  if (maxTicketsPerOrder.value !== null && totalCount > maxTicketsPerOrder.value) {
    errorMessage.value = `這個活動每筆訂單最多購買 ${maxTicketsPerOrder.value} 張，請調整已選座位或購買數量`
    return
  }

  submitting.value = true
  errorMessage.value = ''
  try {
    const { id } = await placeOrder([
      ...selectedSeats.value.map((s) => ({ eventSeatId: s.eventSeatId, ticketTypeId: s.ticketTypeId })),
      ...countSelections,
    ])
    const heldUntilUtc = computeHeldUntilUtc()
    await router.push({ path: `/order-result/${id}`, query: { heldUntilUtc } })
  } catch (error) {
    // 401（換發也失敗）不能指望 App.vue 的全域 watcher 導頁——那個 watcher 只在目前路由
    // 的 meta 標示 requiresAuth/requiresAdmin 時才會導向登入頁，而活動詳情頁是公開頁
    // （未登入也能瀏覽選位，見 buyer-web-ui spec），route meta 沒有這個標記，watcher 不會
    // 處理這裡的登入失效。必須在這個元件自己攔截 401，直接導向登入頁，不落入下面「下單
    // 失敗」的一般錯誤處理，否則會顯示誤導訊息、還把使用者的選購狀態清空。
    if (error instanceof ApiError && error.status === 401) {
      router.push({ path: '/login', query: { redirect: route.fullPath } })
      return
    }
    // 其餘失敗（座位被搶、計數庫存於送出當下已變動、後端其他驗證失敗）一律清空並刷新，
    // 不依錯誤類型分流（design.md 決策 8）。
    // 注意：loadData() 一開始會清空 errorMessage，所以錯誤訊息必須在 loadData() 之後才設定，
    // 否則會被立刻清掉、畫面上完全看不到（這是既有程式碼原本就有的問題，這次順便修正）。
    // 這也代表：若 loadData() 本身也失敗，它內部設定的錯誤訊息會被這裡的下單失敗訊息蓋掉——
    // 這是刻意的（下單失敗才是使用者當下最需要知道的事），不是遺漏。
    const message = toErrorMessage(error, '下單失敗，可能有座位已被搶先鎖定或售出，請重新選擇')
    await loadData()
    errorMessage.value = message
    clearSelections()
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
            title="請先登入才能選位或購買計數票種"
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

          <div v-if="countTicketTypes.length > 0" class="count-purchase-section">
            <h2>計數購票</h2>
            <div v-for="ticketType in countTicketTypes" :key="ticketType.id" class="count-ticket-row">
              <span class="count-ticket-name">{{ ticketType.zoneCode }}</span>
              <span class="count-ticket-price">{{ formatCurrency(ticketType.price) }}</span>
              <span class="count-ticket-quantity">
                <template v-if="ticketType.availableQuantity === null">資料異常</template>
                <template v-else-if="ticketType.availableQuantity === 0">已售完</template>
                <template v-else>可售 {{ ticketType.availableQuantity }}</template>
              </span>
              <el-input-number
                :model-value="countQuantities[ticketType.id] ?? 0"
                :min="0"
                :max="countMaxFor(ticketType)"
                :step="1"
                :precision="0"
                :disabled="ticketType.availableQuantity === null"
                style="width: 110px"
                @change="(value: number | undefined) => handleCountChange(ticketType, value)"
              />
            </div>
          </div>

          <div class="summary">
            <p>已選 {{ selectedSeats.length }} 個座位、{{ countTotal }} 張計數票券，總金額 {{ formatCurrency(totalPrice) }}</p>
            <el-button
              type="primary"
              :disabled="selectedSeats.length === 0 && countTotal === 0"
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
.count-purchase-section {
  margin-top: 20px;
}
.count-ticket-row {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 8px 0;
  border-bottom: 1px solid var(--color-border);
}
.count-ticket-name {
  flex: 1;
}
.count-ticket-price,
.count-ticket-quantity {
  color: var(--color-text-secondary);
  font-size: 13px;
  white-space: nowrap;
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
