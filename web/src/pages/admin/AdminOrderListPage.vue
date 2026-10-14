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

async function loadOrders(): Promise<void> {
  loading.value = true
  errorMessage.value = ''
  try {
    orders.value = await getAdminOrders()
  } catch (error) {
    errorMessage.value = toErrorMessage(error, '載入訂單列表失敗')
  } finally {
    loading.value = false
  }
}

onMounted(loadOrders)
</script>

<template>
  <div class="admin-order-list-page">
    <div class="header">
      <h1>訂單管理</h1>
      <el-button :loading="loading" @click="loadOrders">重新整理</el-button>
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
  </div>
</template>

<style scoped>
.header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
</style>
