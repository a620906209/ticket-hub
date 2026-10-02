<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRedemptionScanner, type ScanResultKind } from '../../composables/useRedemptionScanner'

type AlertType = 'success' | 'warning' | 'error' | 'info'

const scanner = useRedemptionScanner()
const { state, manualInputActive, scanResult, holderVerification, videoElement } = scanner

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

    <!-- 不加位移／縮放動畫，至多 150ms 淡入：入場高峰會出現數百次（design.md 決策 8 UI 細節） -->
    <Transition name="holder-panel">
      <section v-if="holderVerification" class="holder-panel" aria-label="持票人確認">
        <p v-if="holderVerification.phase === 'loading'" class="holder-status">查詢持票人資料中…</p>

        <template v-else-if="holderVerification.phase === 'lookup-failed'">
          <el-alert title="查詢持票人資料失敗，請重試" type="error" show-icon :closable="false" role="alert" />
          <div class="holder-actions">
            <el-button type="primary" size="large" @click="scanner.retryHolderLookup">重試</el-button>
            <el-button size="large" @click="scanner.abandonHolderVerification">放棄</el-button>
          </div>
        </template>

        <template v-else-if="holderVerification.holder">
          <p class="holder-hint">請比對證件後確認核銷（持票人即訂購會員）</p>
          <dl class="holder-info">
            <dt>姓名</dt>
            <dd data-testid="holder-real-name">{{ holderVerification.holder.holderRealName }}</dd>
            <dt>身分證末四碼</dt>
            <dd data-testid="holder-national-id-last4">{{ holderVerification.holder.holderNationalIdLast4 }}</dd>
          </dl>
          <div class="holder-actions">
            <el-button
              type="primary"
              size="large"
              :loading="holderVerification.phase === 'confirming'"
              :disabled="holderVerification.phase === 'confirming'"
              @click="scanner.confirmHolderVerification"
            >
              確認核銷
            </el-button>
            <el-button
              size="large"
              :disabled="holderVerification.phase === 'confirming'"
              @click="scanner.abandonHolderVerification"
            >
              放棄
            </el-button>
          </div>
        </template>
      </section>
    </Transition>

    <div
      v-if="!holderVerification && scanResult"
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

    <template v-else-if="!holderVerification">
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
        <div class="trust-label">操作人員信任操作，未驗證簽章</div>
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
.holder-panel {
  margin-bottom: 16px;
  padding: 16px;
  border: 1px solid var(--el-border-color);
  border-radius: 8px;
}
.holder-hint,
.holder-status {
  margin: 0 0 12px;
}
.holder-info {
  display: grid;
  grid-template-columns: max-content 1fr;
  align-items: baseline;
  gap: 8px 16px;
  margin: 0 0 16px;
}
.holder-info dd {
  margin: 0;
  font-size: 32px;
  font-weight: 700;
  word-break: break-all;
}
.holder-actions {
  display: flex;
  gap: 12px;
  margin-top: 12px;
}
.holder-actions .el-button {
  flex: 1;
  min-height: 48px;
}
.holder-panel-enter-active {
  transition: opacity 120ms ease-out;
}
.holder-panel-enter-from {
  opacity: 0;
}
</style>
