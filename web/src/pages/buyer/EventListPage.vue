<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { getEvents } from '../../api/events'
import type { EventSummary } from '../../types/apiResponses'
import { toErrorMessage } from '../../utils/errors'
import { getSalesStatus } from '../../utils/salesWindow'
import SalesStatusTag from '../../components/SalesStatusTag.vue'
import { usePlacedOrderNoticeStore } from '../../stores/placedOrderNotice'
import { ORDER_HOLD_MINUTES } from '../../utils/orderHold'

const events = ref<EventSummary[]>([])
const loading = ref(false)
const errorMessage = ref('')
const currentPage = ref(1)
const loadedPage = ref(1)
const pageSize = ref(20)
const totalCount = ref(0)
// 首次載入失敗時沒有任何可顯示的內容，要與「換頁失敗但保留舊內容」區分（BW-LIST-PAGE-005／006）
const hasLoadedPage = ref(false)
let latestRequestSequence = 0
// 列表只在載入時計算一次販售狀態，不需每秒更新（停留跨越邊界時以詳情頁的即時判斷為準）
const loadedAtMs = ref(Date.now())
// setup 時就取出並清空，提示只顯示一次（design.md 決策 4）
const placedOrderId = ref(usePlacedOrderNoticeStore().consumePlacedOrder())

// 以遞增序號丟棄過期回應；空頁且 totalCount > 0 時只重試一次改請求最後一頁；失敗時頁碼退回上一個成功載入的頁，
// 避免分頁元件與卡片內容不一致（與後台列表同一套處理，public-event-list-pagination design.md）。
async function loadPage(page: number): Promise<void> {
  const requestSequence = ++latestRequestSequence
  loading.value = true
  errorMessage.value = ''
  try {
    let result = await getEvents(page)
    if (requestSequence !== latestRequestSequence) {
      return
    }
    let resolvedPage = page
    if (result.items.length === 0 && result.totalCount > 0) {
      resolvedPage = Math.ceil(result.totalCount / result.pageSize)
      result = await getEvents(resolvedPage)
      if (requestSequence !== latestRequestSequence) {
        return
      }
    }
    events.value = result.items
    pageSize.value = result.pageSize
    totalCount.value = result.totalCount
    loadedPage.value = resolvedPage
    currentPage.value = resolvedPage
    hasLoadedPage.value = true
    loadedAtMs.value = Date.now()
  } catch (error) {
    if (requestSequence !== latestRequestSequence) {
      return
    }
    errorMessage.value = toErrorMessage(error, '載入活動列表失敗')
    currentPage.value = loadedPage.value
  } finally {
    if (requestSequence === latestRequestSequence) {
      loading.value = false
    }
  }
}

function handlePageChange(page: number): Promise<void> {
  currentPage.value = page
  return loadPage(page)
}

onMounted(() => loadPage(currentPage.value))
</script>

<template>
  <div class="event-list-page">
    <h1>活動列表</h1>
    <el-alert
      v-if="placedOrderId"
      class="placed-order-notice"
      role="status"
      type="success"
      show-icon
      :title="`訂單已建立，已為你保留 ${ORDER_HOLD_MINUTES} 分鐘，請在期限內完成付款`"
      @close="placedOrderId = null"
    >
      <router-link :to="`/orders/${placedOrderId}`">前往付款</router-link>
    </el-alert>
    <el-alert v-if="errorMessage" :title="errorMessage" type="error" show-icon style="margin-bottom: 16px" />
    <el-empty v-if="hasLoadedPage && totalCount === 0" description="目前沒有活動" />
    <div v-else-if="hasLoadedPage || !errorMessage" v-loading="loading" class="event-grid">
      <router-link v-for="event in events" :key="event.id" :to="`/events/${event.id}`" class="event-card">
        <h2>{{ event.title }}</h2>
        <p>{{ new Date(event.startAtUtc).toLocaleString() }}</p>
        <SalesStatusTag class="sales-status" :status="getSalesStatus(event, loadedAtMs)" />
      </router-link>
    </div>
    <el-pagination
      v-if="hasLoadedPage && totalCount > pageSize"
      class="event-pagination"
      layout="total, prev, pager, next"
      :total="totalCount"
      :page-size="pageSize"
      :current-page="currentPage"
      @current-change="handlePageChange"
    />
  </div>
</template>

<style scoped>
.event-list-page {
  max-width: 1080px;
  margin: 32px auto;
  padding: 0 16px;
}
.placed-order-notice {
  margin-bottom: 16px;
}
.event-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(260px, 1fr));
  gap: 16px;
  margin-top: 24px;
}
.event-pagination {
  margin-top: 24px;
  justify-content: center;
}
.event-card {
  display: block;
  padding: 20px;
  border-radius: 8px;
  background: var(--color-bg-elevated);
  border: 1px solid var(--color-border);
  text-decoration: none;
  color: var(--color-text);
  transition:
    border-color 0.2s,
    box-shadow 0.2s;
}
.event-card:hover {
  border-color: var(--color-primary);
  box-shadow: 0 2px 8px rgb(0 0 0 / 6%);
}
.event-card h2 {
  margin: 0 0 8px;
  font-size: 18px;
}
.event-card p {
  margin: 0;
  color: var(--color-text-secondary);
  font-size: 14px;
}
.event-card .sales-status {
  margin-top: 8px;
}
</style>
