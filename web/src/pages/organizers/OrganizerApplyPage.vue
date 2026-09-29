<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import type { FormInstance } from 'element-plus'
import { applyForOrganizer, switchOrganizerContext } from '../../api/organizers'
import { useAuthStore } from '../../stores/auth'
import { maxLengthRule, requiredRule } from '../../utils/validators'
import { toErrorMessage } from '../../utils/errors'

const router = useRouter()
const authStore = useAuthStore()

const formRef = ref<FormInstance>()
const form = reactive({ name: '' })
const rules = {
  name: [requiredRule('請輸入主辦方名稱'), maxLengthRule(100, '主辦方名稱長度不可超過 100 字')],
}
const submitting = ref(false)
const error = ref('')

// 申請成功後自動嘗試切換：因新申請預設為 Pending，這次切換預期會被拒絕（ORG-APPLY-005）。
// 不論這次自動切換成功、被預期拒絕、或因網路逾時等非預期原因失敗，介面一律顯示「申請已送出，
// 待平台審核」並導向「我的主辦方」清單頁，不顯示任何錯誤訊息（見 AWU-APPLY-002／AWU-APPLY-004）。
async function tryAutoSwitch(organizerId: string): Promise<void> {
  const refreshToken = authStore.getCurrentRefreshToken()
  if (!refreshToken) return

  try {
    const result = await switchOrganizerContext(organizerId, refreshToken)
    authStore.applySwitchedAccessToken(result.accessToken)
  } catch {
    // 預期或非預期失敗皆忽略，見上方說明。
  }
}

async function handleSubmit(): Promise<void> {
  error.value = ''
  const valid = await formRef.value?.validate().catch(() => false)
  // 額外明確檢查（而非只依賴 el-form 的 validate() 結果）：已實測發現這個 Element Plus 版本的
  // validate() 對「完全未輸入過、維持初始空字串」的必填欄位未如預期拒絕（jsdom/單元測試環境下可重現），
  // 這裡加一層防禦，確保空白名稱不會被送到後端。
  if (!valid || !form.name.trim()) return

  submitting.value = true
  try {
    const { id } = await applyForOrganizer(form.name)
    await tryAutoSwitch(id)
    ElMessage.success('申請已送出，待平台審核')
    await router.push({ name: 'my-organizers' })
  } catch (submitError) {
    error.value = toErrorMessage(submitError, '申請建立主辦方失敗')
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div class="organizer-apply-page">
    <h1>申請建立主辦方</h1>
    <el-alert v-if="error" :title="error" type="error" show-icon style="margin-bottom: 16px" />
    <el-form ref="formRef" :model="form" :rules="rules" label-width="100px" @submit.prevent="handleSubmit">
      <el-form-item label="主辦方名稱" prop="name">
        <el-input v-model="form.name" maxlength="100" />
      </el-form-item>
      <el-form-item>
        <el-button type="primary" :loading="submitting" native-type="submit">送出申請</el-button>
      </el-form-item>
    </el-form>
  </div>
</template>

<style scoped>
.organizer-apply-page {
  max-width: 480px;
}
</style>
