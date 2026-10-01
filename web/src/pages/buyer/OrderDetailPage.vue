<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'
import { getTicketQrCodeBlob } from '../../api/orders'
import OrderStatusTag from '../../components/OrderStatusTag.vue'
import TicketStatusTag from '../../components/TicketStatusTag.vue'
import { usePendingOrderActions } from '../../composables/usePendingOrderActions'
import type { MyOrderItem } from '../../types/apiResponses'
import { toErrorMessage } from '../../utils/errors'

const {
  orderId,
  order,
  isInitialLoading,
  isRefreshing,
  hasRefreshFailed,
  loadErrorMessage,
  actionErrorMessage,
  activeAction,
  canAct,
  canShowHeldUntil,
  confirmPayment,
  cancelPendingOrder,
  refreshOrder,
} = usePendingOrderActions()

const qrErrorMessage = ref('')
const activeQrUrl = ref<string | null>(null)
const activeTicketId = ref<string | null>(null)
let qrRequestVersion = 0

// 後端保證分區與號碼同時為 null（純計數項目）或同時有值，這裡只需判斷其中一欄。
function formatSeatLabel(item: MyOrderItem): string {
  return item.seatZoneCode == null ? '—' : `${item.seatZoneCode}-${item.seatNumber}`
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

async function showQrCode(ticketId: string): Promise<void> {
  const requestVersion = ++qrRequestVersion
  revokeActiveQrUrl()
  qrErrorMessage.value = ''
  try {
    const blob = await getTicketQrCodeBlob(ticketId)
    if (requestVersion !== qrRequestVersion) {
      return
    }
    activeTicketId.value = ticketId
    activeQrUrl.value = URL.createObjectURL(blob)
  } catch (error) {
    if (requestVersion === qrRequestVersion) {
      qrErrorMessage.value = toErrorMessage(error, '載入 QR Code 失敗')
    }
  }
}

function discardQrCode(): void {
  qrRequestVersion += 1
  qrErrorMessage.value = ''
  revokeActiveQrUrl()
}

watch(orderId, discardQrCode)
onBeforeUnmount(discardQrCode)
</script>

<template>
  <div v-loading="isInitialLoading" class="order-detail-page">
    <h1>訂單明細</h1>
    <el-alert v-if="loadErrorMessage" :title="loadErrorMessage" type="error" show-icon class="page-alert" />

    <template v-if="order">
      <p>訂單 Id：{{ order.id }}</p>
      <p>活動：{{ order.eventTitle }}</p>
      <p>狀態：<OrderStatusTag :status="order.status" /></p>
      <p v-if="canShowHeldUntil">保留至 {{ new Date(order.heldUntilUtc).toLocaleString() }}</p>

      <el-alert v-if="actionErrorMessage" :title="actionErrorMessage" type="error" show-icon class="page-alert" />
      <el-alert v-if="qrErrorMessage" :title="qrErrorMessage" type="error" show-icon class="page-alert" />

      <div v-if="canAct" class="order-actions">
        <el-button
          type="primary"
          :loading="activeAction === 'confirm'"
          :disabled="activeAction !== null"
          @click="confirmPayment"
        >
          確認付款
        </el-button>
        <el-button
          :loading="activeAction === 'cancel'"
          :disabled="activeAction !== null"
          @click="cancelPendingOrder"
        >
          取消訂單
        </el-button>
      </div>
      <el-button v-if="hasRefreshFailed" :loading="isRefreshing" @click="refreshOrder">重新整理</el-button>

      <section v-for="item in order.items" :key="item.id" class="order-item">
        <h2>訂單項目</h2>
        <p>票種：{{ item.ticketTypeName ?? '—' }}</p>
        <p>座位：{{ formatSeatLabel(item) }}</p>
        <p>數量：{{ item.quantity }}</p>
        <template v-if="item.tickets.length === 0">
          <p>尚未出票</p>
        </template>
        <ul v-else class="ticket-list">
          <li v-for="ticket in item.tickets" :key="ticket.id">
            <span>票券狀態：<TicketStatusTag :status="ticket.status" /></span>
            <template v-if="canShowQrCode(ticket.status)">
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

    <template v-else-if="!isInitialLoading && loadErrorMessage">
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

.page-alert {
  margin-bottom: 16px;
}

.order-actions {
  margin-top: 16px;
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
