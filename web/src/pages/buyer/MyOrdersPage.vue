<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { getMyOrders } from '../../api/orders'
import type { MyOrderSummary } from '../../types/apiResponses'
import OrderStatusTag from '../../components/OrderStatusTag.vue'
import { toErrorMessage } from '../../utils/errors'

const orders = ref<MyOrderSummary[]>([])
const loading = ref(false)
const errorMessage = ref('')

function isPending(order: MyOrderSummary): boolean {
  return order.status === 'Pending'
}

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
    let result = await getMyOrders(page)
    if (requestSequence !== latestRequestSequence) {
      return
    }
    let resolvedPage = page
    if (result.items.length === 0 && result.totalCount > 0) {
      resolvedPage = Math.ceil(result.totalCount / result.pageSize)
      result = await getMyOrders(resolvedPage)
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
  <div class="my-orders-page">
    <div class="header">
      <h1>我的訂單</h1>
      <el-button :loading="loading" @click="reloadCurrentPage">重新整理</el-button>
    </div>

    <el-alert v-if="errorMessage" :title="errorMessage" type="error" show-icon style="margin-bottom: 16px" />

    <el-table v-if="orders.length > 0" v-loading="loading" :data="orders">
      <el-table-column prop="eventTitle" label="活動" />
      <el-table-column label="狀態" width="120">
        <template #default="{ row }"><OrderStatusTag :status="row.status" /></template>
      </el-table-column>
      <el-table-column label="保留時間">
        <template #default="{ row }">
          <span v-if="isPending(row)">保留至 {{ new Date(row.heldUntilUtc).toLocaleString() }}</span>
        </template>
      </el-table-column>
      <el-table-column label="操作" width="120">
        <template #default="{ row }">
          <router-link :to="`/orders/${row.id}`">查看明細</router-link>
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

    <el-empty v-if="orders.length === 0 && !loading && !errorMessage" description="目前沒有訂單" />
  </div>
</template>

<style scoped>
.my-orders-page {
  max-width: 800px;
  margin: 64px auto;
  padding: 0 16px;
}

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
