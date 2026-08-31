<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRedemptionScanner, type ScanResultKind } from '../../composables/useRedemptionScanner'

type AlertType = 'success' | 'warning' | 'error' | 'info'

const scanner = useRedemptionScanner()
const { state, manualInputActive, scanResult, videoElement } = scanner

const manualTicketId = ref('')
const manualFormatError = ref('')
const manualSubmitting = ref(false)
const resultBannerRef = ref<HTMLElement | null>(null)

const FALLBACK_STATES = ['unsupported', 'permission-denied', 'camera-unavailable', 'error'] as const
const isFallbackState = computed(() => (FALLBACK_STATES as readonly string[]).includes(state.value))
const showManualForm = computed(() => isFallbackState.value || manualInputActive.value)
const canRetryCamera = computed(() =>
  state.value === 'permission-denied' || state.value === 'camera-unavailable' || state.value === 'error',
)

const RESULT_TEXT: Record<ScanResultKind, string> = {
  success: '核銷成功',
  'already-redeemed': '此票券已核銷過',
  'not-found': '查無此票券',
  'invalid-signature': '簽章驗證失敗',
  unrecognized: '無法辨識的票券內容',
  'system-error': '系統發生錯誤，請重試',
}

// 只有成功／已核銷過屬於可預期的業務結果（role="status" polite）；其餘四種需要操作者留意，
// 用 role="alert" assertive 並搶佔焦點（決策 3）。
const ASSERTIVE_RESULTS: ScanResultKind[] = ['not-found', 'invalid-signature', 'unrecognized', 'system-error']

const resultBannerType = computed<AlertType>(() => {
  switch (scanResult.value) {
    case 'success':
      return 'success'
    case 'already-redeemed':
      return 'warning'
    default:
      return 'error'
  }
})
const resultBannerIsAssertive = computed(() => ASSERTIVE_RESULTS.includes(scanResult.value as ScanResultKind))

watch(scanResult, async (kind) => {
  if (kind && ASSERTIVE_RESULTS.includes(kind)) {
    await nextTick()
    resultBannerRef.value?.focus()
  }
})

const cameraStatusText: Record<string, string> = {
  unsupported: '此瀏覽器不支援相機掃描',
  'permission-denied': '相機權限被拒絕',
  'camera-unavailable': '找不到可用相機',
  error: '相機初始化發生錯誤',
}

async function handleManualSubmit(): Promise<void> {
  manualFormatError.value = ''
  manualSubmitting.value = true
  try {
    const result = await scanner.submitManualRedemption(manualTicketId.value)
    if (!result.formatValid) {
      manualFormatError.value = 'Ticket ID 格式不正確'
      return
    }
    manualTicketId.value = ''
  } finally {
    manualSubmitting.value = false
  }
}

onMounted(scanner.mount)
onUnmounted(scanner.unmount)
</script>

<template>
  <div class="redemption-scanner-page">
    <h1>票券核銷</h1>

    <div
      v-if="scanResult"
      ref="resultBannerRef"
      class="result-banner"
      :role="resultBannerIsAssertive ? 'alert' : 'status'"
      :aria-live="resultBannerIsAssertive ? 'assertive' : 'polite'"
      tabindex="-1"
    >
      <el-alert :title="RESULT_TEXT[scanResult]" :type="resultBannerType" show-icon :closable="false" />
      <el-button v-if="resultBannerIsAssertive" size="small" style="margin-top: 8px" @click="scanner.dismissResult">
        立即繼續掃描
      </el-button>
    </div>

    <template v-else>
      <div v-if="state === 'initializing'" class="camera-status">初始化相機中…</div>

      <template v-if="!showManualForm">
        <div class="trust-label">已驗證簽章</div>
        <video
          :ref="(el) => (videoElement = el as HTMLVideoElement | null)"
          class="camera-preview"
          autoplay
          muted
          playsinline
        ></video>
        <el-button v-if="state === 'scanning'" @click="scanner.switchToManualInput">改用手動輸入</el-button>
      </template>

      <template v-else>
        <div class="trust-label">Admin 信任操作，未驗證簽章</div>
        <p v-if="isFallbackState" class="camera-status">{{ cameraStatusText[state] }}</p>
        <el-form :model="{ manualTicketId }" @submit.prevent="handleManualSubmit">
          <el-form-item label="Ticket ID" :error="manualFormatError">
            <el-input v-model="manualTicketId" placeholder="貼上或輸入 Ticket ID" />
          </el-form-item>
          <el-button type="primary" :loading="manualSubmitting" native-type="submit">送出核銷</el-button>
          <el-button v-if="canRetryCamera" @click="scanner.retryCamera">重新嘗試相機</el-button>
          <el-button v-if="!isFallbackState" @click="scanner.cancelManualInput">改用相機掃描</el-button>
        </el-form>
      </template>
    </template>
  </div>
</template>

<style scoped>
.redemption-scanner-page {
  max-width: 480px;
}
.camera-preview {
  width: 100%;
  max-width: 480px;
  background: #000;
  border-radius: 8px;
}
.camera-status,
.trust-label {
  color: var(--el-text-color-secondary);
  font-size: 13px;
  margin-bottom: 8px;
}
.result-banner {
  margin-bottom: 16px;
}
</style>
