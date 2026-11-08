<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { getAdminOrders } from '../../api/admin'
import type { OrderSummary } from '../../types/apiResponses'
import OrderStatusTag from '../../components/OrderStatusTag.vue'
import { formatCurrency } from '../../utils/currency'
import { toErrorMessage } from '../../utils/errors'

const orders = ref<OrderSummary[]>([])
const loading = ref(false)
const errorMessage = ref('')

const currentPage = ref(1)
const loadedPage = ref(1)
const pageSize = ref(20)
const totalCount = ref(0)
let latestRequestSequence = 0

// 換頁、重新整理共用：以遞增序號丟棄過期回應；空頁且 totalCount > 0 時只重試一次改請求最後一頁；
// 失敗時頁碼退回上一個成功載入的頁，避免分頁元件與表格內容不一致（order-list-pagination design.md 決策 7）。
async function loadPage(page: number): Promise<void> {
  const requestSequence = ++latestRequestSequence
  loading.value = true
  errorMessage.value = ''
  try {
    let result = await getAdminOrders(page)
    if (requestSequence !== latestRequestSequence) {
      return
    }
    let resolvedPage = page
    if (result.items.length === 0 && result.totalCount > 0) {
      resolvedPage = Math.ceil(result.totalCount / result.pageSize)
      result = await getAdminOrders(resolvedPage)
      if (requestSequence !== latestRequestSequence) {
        return
      }
    }
    orders.value = result.items
    pageSize.value = result.pageSize
    totalCount.value = result.totalCount
    loadedPage.value = resolvedPage
    currentPage.value = resolvedPage
  } catch (error) {
    if (requestSequence !== latestRequestSequence) {
      return
    }
    errorMessage.value = toErrorMessage(error, '載入訂單列表失敗')
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

function reloadCurrentPage(): Promise<void> {
  return loadPage(currentPage.value)
}

onMounted(reloadCurrentPage)
</script>

<template>
  <div class="admin-order-list-page">
    <div class="header">
      <h1>訂單管理</h1>
      <el-button :loading="loading" @click="reloadCurrentPage">重新整理</el-button>
    </div>
    <el-alert v-if="errorMessage" :title="errorMessage" type="error" show-icon style="margin-bottom: 16px" />
    <el-table v-loading="loading" :data="orders" empty-text="目前沒有訂單">
      <!-- 短碼只供辨識、不保證唯一，明細連結仍用完整 Id（admin-order-list-enrichment design.md 決策 6） -->
      <el-table-column label="訂單編號" width="110">
        <template #default="{ row }">{{ row.id.slice(0, 8) }}</template>
      </el-table-column>
      <el-table-column prop="eventTitle" label="活動" min-width="160" />
      <el-table-column prop="buyerDisplayName" label="買家" min-width="120" />
      <el-table-column prop="ticketCount" label="張數" width="80" />
      <el-table-column label="總金額" width="120">
        <template #default="{ row }">{{ formatCurrency(row.totalAmount) }}</template>
      </el-table-column>
      <el-table-column label="狀態" width="120">
        <template #default="{ row }"><OrderStatusTag :status="row.status" /></template>
      </el-table-column>
      <el-table-column label="建立時間" min-width="180">
        <template #default="{ row }">{{ new Date(row.createdAtUtc).toLocaleString() }}</template>
      </el-table-column>
      <el-table-column label="持有到期時間" min-width="180">
        <template #default="{ row }">
          {{ row.status === 'Pending' ? new Date(row.heldUntilUtc).toLocaleString() : '—' }}
        </template>
      </el-table-column>
      <el-table-column label="操作" width="120">
        <template #default="{ row }">
          <router-link :to="`/admin/orders/${row.id}`">查看明細</router-link>
        </template>
      </el-table-column>
    </el-table>

    <el-pagination
      v-if="totalCount > pageSize"
      class="pagination"
      layout="total, prev, pager, next"
      :total="totalCount"
      :page-size="pageSize"
      :current-page="currentPage"
      @current-change="handlePageChange"
    />
  </div>
</template>

<style scoped>
.pagination {
  margin-top: 16px;
  justify-content: flex-end;
}

.header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
</style>
