<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { getMyOrderDetail, getTicketQrCodeBlob } from '../../api/orders'
import { ApiError } from '../../api/httpClient'
import type { MyOrderDetail } from '../../types/apiResponses'
import { toErrorMessage } from '../../utils/errors'

const route = useRoute()
const orderId = route.params.id as string

const order = ref<MyOrderDetail | null>(null)
const loading = ref(false)
const errorMessage = ref('')
const activeQrUrl = ref<string | null>(null)
const activeTicketId = ref<string | null>(null)
let qrRequestVersion = 0

function isPending(): boolean {
  return order.value?.status === 'Pending'
}

function canShowQrCode(status: string): boolean {
  return status === 'Issued' || status === 'Redeemed'
}

function revokeActiveQrUrl(): void {
  if (activeQrUrl.value) {
    URL.revokeObjectURL(activeQrUrl.value)
    activeQrUrl.value = null
  }
  activeTicketId.value = null
}

async function loadOrder(): Promise<void> {
  loading.value = true
  errorMessage.value = ''
  order.value = null
  try {
    order.value = await getMyOrderDetail(orderId)
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) {
      errorMessage.value = '找不到這筆訂單'
    } else if (error instanceof ApiError && error.status === 403) {
      errorMessage.value = '你沒有權限查看這筆訂單'
    } else {
      errorMessage.value = toErrorMessage(error, '載入訂單明細失敗')
    }
  } finally {
    loading.value = false
  }
}

async function showQrCode(ticketId: string): Promise<void> {
  const requestVersion = ++qrRequestVersion
  revokeActiveQrUrl()
  errorMessage.value = ''
  try {
    const blob = await getTicketQrCodeBlob(ticketId)
    if (requestVersion !== qrRequestVersion) {
      return
    }
    activeTicketId.value = ticketId
    activeQrUrl.value = URL.createObjectURL(blob)
  } catch (error) {
    if (requestVersion === qrRequestVersion) {
      errorMessage.value = toErrorMessage(error, '載入 QR Code 失敗')
    }
  }
}

onMounted(loadOrder)
onBeforeUnmount(() => {
  qrRequestVersion += 1
  revokeActiveQrUrl()
})
</script>

<template>
  <div class="order-detail-page">
    <h1>訂單明細</h1>
    <el-alert v-if="errorMessage" :title="errorMessage" type="error" show-icon style="margin-bottom: 16px" />

    <template v-if="order">
      <p>訂單 Id：{{ order.id }}</p>
      <p>狀態：{{ order.status }}</p>
      <p v-if="isPending()">保留至 {{ new Date(order.heldUntilUtc).toLocaleString() }}</p>

      <section v-for="item in order.items" :key="item.id" class="order-item">
        <h2>訂單項目</h2>
        <p>數量：{{ item.quantity }}</p>
        <template v-if="item.tickets.length === 0">
          <p>尚未出票</p>
        </template>
        <ul v-else class="ticket-list">
          <li v-for="ticket in item.tickets" :key="ticket.id">
            <template v-if="canShowQrCode(ticket.status)">
              <span>票券狀態：{{ ticket.status }}</span>
              <el-button text type="primary" @click="showQrCode(ticket.id)">查看 QR Code</el-button>
              <img
                v-if="activeTicketId === ticket.id && activeQrUrl"
                :src="activeQrUrl"
                alt="票券 QR Code"
                class="qr-code"
              />
            </template>
          </li>
        </ul>
      </section>
    </template>

    <template v-else-if="!loading && errorMessage">
      <router-link to="/orders">返回我的訂單</router-link>
    </template>
  </div>
</template>

<style scoped>
.order-detail-page {
  max-width: 800px;
  margin: 64px auto;
  padding: 0 16px;
}

.order-item {
  margin-top: 24px;
  padding-top: 16px;
  border-top: 1px solid var(--el-border-color-lighter);
}

.ticket-list {
  padding-left: 20px;
}

.qr-code {
  display: block;
  width: 240px;
  max-width: 100%;
  margin-top: 12px;
}
</style>
