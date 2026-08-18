<script setup lang="ts">
import { ref } from 'vue'
import { useRoute } from 'vue-router'
import { cancelOrder, confirmOrder } from '../../api/orders'
import { toErrorMessage } from '../../utils/errors'

const route = useRoute()
const orderId = route.params.id as string
const heldUntilUtc = typeof route.query.heldUntilUtc === 'string' ? route.query.heldUntilUtc : null

type LocalStatus = 'pending' | 'confirmed' | 'cancelled'

const status = ref<LocalStatus>('pending')
const submitting = ref(false)
const errorMessage = ref('')

async function handleConfirm(): Promise<void> {
  submitting.value = true
  errorMessage.value = ''
  try {
    await confirmOrder(orderId)
    status.value = 'confirmed'
  } catch (error) {
    errorMessage.value = toErrorMessage(error, '確認訂單失敗')
  } finally {
    submitting.value = false
  }
}

async function handleCancel(): Promise<void> {
  submitting.value = true
  errorMessage.value = ''
  try {
    await cancelOrder(orderId)
    status.value = 'cancelled'
  } catch (error) {
    errorMessage.value = toErrorMessage(error, '取消訂單失敗')
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div class="order-result-page">
    <h1>訂單結果</h1>
    <p>訂單 Id：{{ orderId }}</p>
    <p v-if="heldUntilUtc">持有到期時間：{{ new Date(heldUntilUtc).toLocaleString() }}</p>

    <el-alert v-if="errorMessage" :title="errorMessage" type="error" show-icon style="margin-bottom: 16px" />

    <el-alert v-if="status === 'confirmed'" title="已確認" type="success" show-icon />
    <el-alert v-else-if="status === 'cancelled'" title="已取消" type="info" show-icon />
    <div v-else>
      <el-button type="primary" :loading="submitting" @click="handleConfirm">確認訂單</el-button>
      <el-button :loading="submitting" @click="handleCancel">取消訂單</el-button>
    </div>
  </div>
</template>

<style scoped>
.order-result-page {
  max-width: 480px;
  margin: 64px auto;
  padding: 0 16px;
}
</style>
