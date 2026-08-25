<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { getMyOrders } from '../../api/orders'
import type { MyOrderSummary } from '../../types/apiResponses'
import { toErrorMessage } from '../../utils/errors'

const orders = ref<MyOrderSummary[]>([])
const loading = ref(false)
const errorMessage = ref('')

function isPending(order: MyOrderSummary): boolean {
  return order.status === 'Pending'
}

async function loadOrders(): Promise<void> {
  loading.value = true
  errorMessage.value = ''
  try {
    orders.value = await getMyOrders()
  } catch (error) {
    errorMessage.value = toErrorMessage(error, '載入訂單列表失敗')
  } finally {
    loading.value = false
  }
}

onMounted(loadOrders)
</script>

<template>
  <div class="my-orders-page">
    <div class="header">
      <h1>我的訂單</h1>
      <el-button :loading="loading" @click="loadOrders">重新整理</el-button>
    </div>

    <el-alert v-if="errorMessage" :title="errorMessage" type="error" show-icon style="margin-bottom: 16px" />

    <el-table v-if="orders.length > 0" v-loading="loading" :data="orders">
      <el-table-column prop="status" label="狀態" width="120" />
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

    <el-empty v-else-if="!loading && !errorMessage" description="目前沒有訂單" />
  </div>
</template>

<style scoped>
.my-orders-page {
  max-width: 800px;
  margin: 64px auto;
  padding: 0 16px;
}

.header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
</style>
