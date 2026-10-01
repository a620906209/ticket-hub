<script setup lang="ts">
import OrderStatusTag from '../../components/OrderStatusTag.vue'
import { usePendingOrderActions } from '../../composables/usePendingOrderActions'

const {
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
</script>

<template>
  <div v-loading="isInitialLoading" class="order-result-page">
    <h1>訂單結果</h1>
    <el-alert v-if="loadErrorMessage" :title="loadErrorMessage" type="error" show-icon class="page-alert" />

    <template v-if="order">
      <p>訂單 Id：{{ order.id }}</p>
      <p>活動：{{ order.eventTitle }}</p>
      <p>狀態：<OrderStatusTag :status="order.status" /></p>
      <p v-if="canShowHeldUntil">保留至 {{ new Date(order.heldUntilUtc).toLocaleString() }}</p>

      <el-alert v-if="actionErrorMessage" :title="actionErrorMessage" type="error" show-icon class="page-alert" />

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
    </template>

    <template v-else-if="!isInitialLoading && loadErrorMessage">
      <router-link to="/orders">返回我的訂單</router-link>
    </template>
  </div>
</template>

<style scoped>
.order-result-page {
  max-width: 480px;
  margin: 64px auto;
  padding: 0 16px;
}

.page-alert {
  margin-bottom: 16px;
}

.order-actions {
  margin-top: 16px;
}
</style>
