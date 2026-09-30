<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { getAdminOrderById } from '../../api/admin'
import type { OrderDetail } from '../../types/apiResponses'
import { toErrorMessage } from '../../utils/errors'
import { formatCurrency } from '../../utils/currency'
import OrderStatusTag from '../../components/OrderStatusTag.vue'

const route = useRoute()
const orderId = route.params.id as string

const order = ref<OrderDetail | null>(null)
const loading = ref(false)
const errorMessage = ref('')

async function loadOrder(): Promise<void> {
  loading.value = true
  errorMessage.value = ''
  try {
    order.value = await getAdminOrderById(orderId)
  } catch (error) {
    errorMessage.value = toErrorMessage(error, '載入訂單明細失敗')
  } finally {
    loading.value = false
  }
}

onMounted(loadOrder)
</script>

<template>
  <div class="admin-order-detail-page">
    <h1>訂單明細</h1>
    <el-alert v-if="errorMessage" :title="errorMessage" type="error" show-icon style="margin-bottom: 16px" />

    <template v-if="order">
      <p>訂單 Id：{{ order.id }}</p>
      <p>狀態：<OrderStatusTag :status="order.status" /></p>
      <p v-if="order.status === 'Pending'">持有到期時間：{{ new Date(order.heldUntilUtc).toLocaleString() }}</p>
      <el-table v-loading="loading" :data="order.items">
        <el-table-column label="座位 Id">
          <template #default="{ row }">{{ row.eventSeatId ?? '—（計數票）' }}</template>
        </el-table-column>
        <el-table-column prop="quantity" label="數量" width="80" />
        <el-table-column label="單價">
          <template #default="{ row }">{{ formatCurrency(row.unitPrice) }}</template>
        </el-table-column>
      </el-table>
    </template>
  </div>
</template>

<style scoped>
.admin-order-detail-page {
  max-width: 800px;
}
</style>
