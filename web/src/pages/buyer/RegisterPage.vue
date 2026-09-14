<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import type { FormInstance } from 'element-plus'
import { ApiError } from '../../api/httpClient'
import { register } from '../../api/auth'
import { useCaptcha } from '../../composables/useCaptcha'
import { emailRules, maxLengthRule, passwordRules, requiredRule } from '../../utils/validators'
import { toErrorMessage } from '../../utils/errors'

const router = useRouter()
const {
  token: captchaToken,
  imageBase64: captchaImageBase64,
  loadError: captchaLoadError,
  refresh: refreshCaptcha,
} = useCaptcha()

const formRef = ref<FormInstance>()
const form = reactive({ email: '', password: '', displayName: '', captchaAnswer: '' })
const rules = {
  email: emailRules,
  password: passwordRules,
  displayName: [requiredRule('請輸入顯示名稱'), maxLengthRule(100, '顯示名稱長度不可超過 100 字')],
  captchaAnswer: [requiredRule('請輸入驗證碼')],
}

const submitting = ref(false)
const errorMessage = ref('')

onMounted(() => {
  void refreshCaptcha()
})

async function handleSubmit(): Promise<void> {
  errorMessage.value = ''
  const valid = await formRef.value?.validate().catch(() => false)
  if (!valid) return

  submitting.value = true
  try {
    await register(form.email, form.password, form.displayName, captchaToken.value, form.captchaAnswer)
    ElMessage.success('註冊成功，請登入')
    await router.push('/login')
  } catch (error) {
    // 驗證碼錯誤：顯示提示並自動換發新的驗證碼（CAPTCHA-BW-002）。判斷依據 MUST 是後端回傳的
    // 可區分 title（"CaptchaInvalid"），不是泛用的 400 狀態碼——Email／密碼／顯示名稱欄位驗證失敗
    // 也會回傳 400，若只依狀態碼判斷會被誤判成驗證碼錯誤，導致誤清空驗證碼輸入並不必要地換發新圖。
    if (error instanceof ApiError && error.problem?.title === 'CaptchaInvalid') {
      errorMessage.value = toErrorMessage(error, '驗證碼錯誤，請重新輸入')
      form.captchaAnswer = ''
      void refreshCaptcha()
      return
    }
    errorMessage.value = toErrorMessage(error, '註冊失敗，請確認輸入內容')
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div class="register-page">
    <h1>註冊</h1>
    <el-alert v-if="errorMessage" :title="errorMessage" type="error" show-icon style="margin-bottom: 16px" />
    <el-form ref="formRef" :model="form" :rules="rules" label-width="90px" @submit.prevent="handleSubmit">
      <el-form-item label="Email" prop="email">
        <el-input v-model="form.email" type="email" autocomplete="username" />
      </el-form-item>
      <el-form-item label="顯示名稱" prop="displayName">
        <el-input v-model="form.displayName" maxlength="100" />
      </el-form-item>
      <el-form-item label="密碼" prop="password">
        <el-input v-model="form.password" type="password" autocomplete="new-password" show-password />
      </el-form-item>
      <el-form-item label="驗證碼">
        <div class="captcha-row">
          <img v-if="captchaImageBase64" :src="`data:image/png;base64,${captchaImageBase64}`" alt="驗證碼圖片" class="captcha-image" />
          <el-button type="default" @click="refreshCaptcha()">看不清楚？換一張</el-button>
        </div>
        <el-alert v-if="captchaLoadError" :title="captchaLoadError" type="warning" show-icon style="margin-top: 8px" />
      </el-form-item>
      <el-form-item label="驗證碼輸入" prop="captchaAnswer">
        <el-input v-model="form.captchaAnswer" maxlength="16" />
      </el-form-item>
      <el-form-item>
        <el-button type="primary" :loading="submitting" :disabled="!captchaToken" native-type="submit">註冊</el-button>
        <router-link to="/login">已經有帳號？登入</router-link>
      </el-form-item>
    </el-form>
  </div>
</template>

<style scoped>
.register-page {
  max-width: 360px;
  margin: 64px auto;
  padding: 0 16px;
}

.captcha-row {
  display: flex;
  align-items: center;
  gap: 12px;
}

.captcha-image {
  height: 48px;
}
</style>
