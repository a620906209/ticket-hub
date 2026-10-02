<script setup lang="ts">
import { nextTick, onMounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import type { FormInstance } from 'element-plus'
import { getMyProfile, registerRealName } from '../../api/members'
import { ApiError } from '../../api/httpClient'
import type { MemberProfile } from '../../types/apiResponses'
import { requiredRule } from '../../utils/validators'
import { toErrorMessage } from '../../utils/errors'

const route = useRoute()
const router = useRouter()

const profile = ref<MemberProfile | null>(null)
const loading = ref(true)
const loadError = ref('')

const REAL_NAME_MAX_LENGTH = 50
// 與後端 RegisterRealNameRequestValidator 相同：控制字元、格式字元，以及歸類為字母／符號卻顯示為空白的字元。
const INVISIBLE_CHARACTER_PATTERN = /[\p{Cc}\p{Cf}\p{Zl}\p{Zp}\u115F\u1160\u2800\u3164\uFFA0]/u
// 黑名單列不完整（只由組合字元等組成的姓名同樣看不見），再要求至少一個字母類別的字元（RNV-FORMAT-010）。
const LETTER_PATTERN = /\p{L}/u
const formRef = ref<FormInstance>()
const form = reactive({ realName: '', nationalIdLast4: '' })
// 只做格式提示，最終以後端 RegisterRealNameRequestValidator 為準（buyer-web-ui spec「前端格式提示」）。
const rules = {
  // 長度不放進 rules：async-validator 的 max 以未去除前後空白的值計算，會誤擋後端接受的姓名；改由送出時明確檢查。
  realName: [requiredRule('請輸入真實姓名')],
  nationalIdLast4: [
    requiredRule('請輸入身分證末四碼'),
    { pattern: /^[0-9]{4}$/, message: '身分證末四碼必須為 4 位數字', trigger: 'blur' as const },
  ],
}

const isConfirmDialogOpen = ref(false)
// 對話框是自製遮罩，焦點仍可回到底下的輸入欄位；以開啟當下通過檢查的值為準顯示與送出，
// 避免對話框開著時改動輸入而繞過前端檢查。
const pendingRegistration = ref<{ realName: string; nationalIdLast4: string } | null>(null)
const returnButtonRef = ref<HTMLButtonElement>()
const submitting = ref(false)
const submitError = ref('')
const alreadyRegisteredNotice = ref(false)

async function loadProfile(): Promise<void> {
  loading.value = true
  loadError.value = ''
  try {
    profile.value = await getMyProfile()
  } catch (error) {
    loadError.value = toErrorMessage(error, '讀取個人資料失敗')
  } finally {
    loading.value = false
  }
}

// 只接受站內絕對路徑；`//host` 會被瀏覽器解讀成 protocol-relative 外部網址（real-name-verification BW-RN-PAGE-008）。
function readSafeRedirect(): string | null {
  const redirect = route.query.redirect
  if (typeof redirect !== 'string') return null
  return redirect.startsWith('/') && !redirect.startsWith('//') ? redirect : null
}

async function openConfirmDialog(): Promise<void> {
  if (submitting.value) return
  // 對話框開著時在輸入欄按 Enter 會重新送出表單：先關閉，重新檢查通過才以新值再開。
  isConfirmDialogOpen.value = false
  submitError.value = ''
  const valid = await formRef.value?.validate().catch(() => false)
  // 同 OrganizerApplyPage：validate() 對未輸入過的必填欄位不一定拒絕，再明確檢查一次，
  // 並以頁面提示補上，不依賴 el-form 是否渲染出欄位錯誤。
  if (!/^[0-9]{4}$/.test(form.nationalIdLast4)) {
    submitError.value = '身分證末四碼必須為 4 位數字'
    return
  }
  // requiredRule 不擋只有空白的輸入，validate() 會通過；不在這裡提示的話按送出完全沒有反應。
  if (!form.realName.trim()) {
    submitError.value = '請輸入真實姓名'
    return
  }
  // 以展開後的字元數計（擴充 B 區罕用字算 1 字），與後端 rune 計數一致；理由同上，不只依賴 validate()。
  if ([...form.realName.trim()].length > REAL_NAME_MAX_LENGTH) {
    submitError.value = `真實姓名不得超過 ${REAL_NAME_MAX_LENGTH} 字`
    return
  }
  if (INVISIBLE_CHARACTER_PATTERN.test(form.realName)) {
    submitError.value = '真實姓名不得包含控制字元或不可見的字元'
    return
  }
  if (!LETTER_PATTERN.test(form.realName)) {
    submitError.value = '真實姓名至少須包含一個文字'
    return
  }
  if (!valid) return

  // 後端儲存去除前後空白後的值；確認對話框與送出同樣使用去除後的值，看到的就是登記的。
  pendingRegistration.value = { realName: form.realName.trim(), nationalIdLast4: form.nationalIdLast4 }
  isConfirmDialogOpen.value = true
  // 登記不可逆：預設焦點放在「返回修改」，避免誤按 Enter 直接送出（design.md 決策 8 UI 細節）。
  await nextTick()
  returnButtonRef.value?.focus()
}

function closeConfirmDialog(): void {
  if (submitting.value) return
  isConfirmDialogOpen.value = false
}

async function confirmRegistration(): Promise<void> {
  const pending = pendingRegistration.value
  if (submitting.value || !pending) return
  submitting.value = true
  try {
    profile.value = await registerRealName(pending.realName, pending.nationalIdLast4)
    isConfirmDialogOpen.value = false
    ElMessage.success('實名登記成功')
    const redirect = readSafeRedirect()
    if (redirect) {
      await router.push(redirect)
    }
  } catch (error) {
    isConfirmDialogOpen.value = false
    if (error instanceof ApiError && error.status === 409) {
      // 例如另一個分頁已先登記：以伺服器現況為準重新查詢（BW-RN-PAGE-006）。
      alreadyRegisteredNotice.value = true
      await loadProfile()
      return
    }
    submitError.value = error instanceof ApiError && error.status === 400
      ? '實名資料格式不正確，請檢查後再送出'
      : toErrorMessage(error, '實名登記失敗')
  } finally {
    submitting.value = false
  }
}

onMounted(loadProfile)
</script>

<template>
  <div class="real-name-page">
    <h1>實名資料</h1>
    <p v-if="loading">載入中…</p>
    <el-alert v-else-if="loadError" :title="loadError" type="error" show-icon :closable="false" />
    <template v-else-if="profile?.hasRegisteredRealName">
      <el-alert
        v-if="alreadyRegisteredNotice"
        title="已登記過實名"
        type="info"
        show-icon
        :closable="false"
        class="page-alert"
      />
      <dl class="registered-info">
        <dt>真實姓名</dt>
        <dd data-testid="registered-real-name">{{ profile.realName }}</dd>
        <dt>身分證末四碼</dt>
        <dd data-testid="registered-national-id-last4">{{ profile.nationalIdLast4Masked }}</dd>
      </dl>
      <p class="hint">實名資料登記後無法自行修改。</p>
    </template>
    <template v-else>
      <el-alert type="warning" :closable="false" show-icon class="page-alert" title="登記後無法自行修改，請確認與證件相符" />
      <el-alert v-if="submitError" :title="submitError" type="error" show-icon class="page-alert" />
      <el-form ref="formRef" :model="form" :rules="rules" label-width="120px" @submit.prevent="openConfirmDialog">
        <el-form-item label="真實姓名" prop="realName">
          <!-- 不用原生 maxlength：它以 UTF-16 單位計數，會誤擋擴充 B 區罕用字姓名；長度由送出時以去除前後空白後的字元數明確檢查。 -->
          <el-input v-model="form.realName" name="realName" />
        </el-form-item>
        <el-form-item label="身分證末四碼" prop="nationalIdLast4">
          <el-input v-model="form.nationalIdLast4" name="nationalIdLast4" maxlength="4" inputmode="numeric" />
        </el-form-item>
        <el-form-item>
          <el-button type="primary" native-type="submit">送出</el-button>
        </el-form-item>
      </el-form>

      <div
        v-if="isConfirmDialogOpen"
        class="confirm-overlay"
        @click.self="closeConfirmDialog"
        @keydown.esc="closeConfirmDialog"
      >
        <div class="confirm-dialog" role="dialog" aria-modal="true" aria-labelledby="real-name-confirm-title">
          <h2 id="real-name-confirm-title">確認實名資料</h2>
          <p>登記後無法自行修改，請再次確認：</p>
          <dl class="confirm-values">
            <dt>真實姓名</dt>
            <dd data-testid="confirm-real-name">{{ pendingRegistration?.realName }}</dd>
            <dt>身分證末四碼</dt>
            <dd data-testid="confirm-national-id-last4">{{ pendingRegistration?.nationalIdLast4 }}</dd>
          </dl>
          <div class="confirm-actions">
            <button
              ref="returnButtonRef"
              type="button"
              class="el-button"
              data-testid="confirm-return"
              :disabled="submitting"
              @click="closeConfirmDialog"
            >
              返回修改
            </button>
            <button
              type="button"
              class="el-button el-button--primary"
              data-testid="confirm-register"
              :disabled="submitting"
              @click="confirmRegistration"
            >
              {{ submitting ? '登記中…' : '確認登記' }}
            </button>
          </div>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.real-name-page {
  max-width: 520px;
  padding: 24px;
}
.page-alert {
  margin-bottom: 16px;
}
.registered-info,
.confirm-values {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 8px 16px;
  margin: 0 0 16px;
}
.registered-info dd,
.confirm-values dd {
  margin: 0;
  font-weight: 600;
  word-break: break-all;
}
.hint {
  color: var(--color-text-secondary);
  font-size: 14px;
}
.confirm-overlay {
  position: fixed;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgb(0 0 0 / 45%);
  z-index: 2000;
}
.confirm-dialog {
  width: min(420px, calc(100vw - 32px));
  padding: 24px;
  border-radius: 8px;
  background: var(--color-bg-elevated);
}
.confirm-dialog h2 {
  margin-top: 0;
  font-size: 18px;
}
.confirm-actions {
  display: flex;
  justify-content: flex-end;
  gap: 12px;
}
</style>
