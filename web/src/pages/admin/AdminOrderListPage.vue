<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { getAdminOrders } from '../../api/admin'
import type { OrderSummary } from '../../types/apiResponses'
import OrderStatusTag from '../../components/OrderStatusTag.vue'
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
      <el-table-column label="狀態" width="120">
        <template #default="{ row }"><OrderStatusTag :status="row.status" /></template>
      </el-table-column>
      <el-table-column label="持有到期時間">
        <template #default="{ row }">
          <span v-if="row.status === 'Pending'">{{ new Date(row.heldUntilUtc).toLocaleString() }}</span>
        </template>
      </el-table-column>
      <el-table-column prop="buyerDisplayName" label="買家" />
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
