<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import type { FormInstance } from 'element-plus'
import { ApiError } from '../../api/httpClient'
import { useAuthStore } from '../../stores/auth'
import { emailRules, requiredRule } from '../../utils/validators'
import { toErrorMessage } from '../../utils/errors'

const route = useRoute()
const router = useRouter()
const authStore = useAuthStore()

const formRef = ref<FormInstance>()
const form = reactive({ email: '', password: '' })
// 登入只驗證必填，不套用註冊用的密碼強度規則（後端 LoginRequestValidator 也只要求 NotEmpty，
// 套強度規則會誤擋輸入正確密碼但格式規則後來才調整過的既有帳號）。
const rules = { email: emailRules, password: [requiredRule('請輸入密碼')] }

const submitting = ref(false)
const errorMessage = ref('')

async function handleSubmit(): Promise<void> {
  errorMessage.value = ''
  const valid = await formRef.value?.validate().catch(() => false)
  if (!valid) return

  submitting.value = true
  try {
    await authStore.login(form.email, form.password)

    const redirect = typeof route.query.redirect === 'string' ? route.query.redirect : null
    if (redirect) {
      await router.push(redirect)
    } else if (authStore.isAdmin) {
      await router.push('/admin')
    } else {
      await router.push('/')
    }
  } catch (error) {
    // 登入請求頻率限制：顯示友善提示，不直接顯示後端原始 ProblemDetails.title 字串
    // （login-rate-limiting design.md 決策 5）。
    if (error instanceof ApiError && error.status === 429) {
      errorMessage.value = '登入嘗試過於頻繁，請稍後再試'
      return
    }
    errorMessage.value = toErrorMessage(error, '登入失敗，請確認帳號密碼是否正確')
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div class="login-page">
    <h1>登入</h1>
    <el-alert v-if="errorMessage" :title="errorMessage" type="error" show-icon style="margin-bottom: 16px" />
    <el-form ref="formRef" :model="form" :rules="rules" label-width="80px" @submit.prevent="handleSubmit">
      <el-form-item label="Email" prop="email">
        <el-input v-model="form.email" type="email" autocomplete="username" />
      </el-form-item>
      <el-form-item label="密碼" prop="password">
        <el-input v-model="form.password" type="password" autocomplete="current-password" show-password />
      </el-form-item>
      <el-form-item>
        <el-button type="primary" :loading="submitting" native-type="submit">登入</el-button>
        <router-link to="/register">還沒有帳號？註冊</router-link>
      </el-form-item>
    </el-form>
  </div>
</template>

<style scoped>
.login-page {
  max-width: 360px;
  margin: 64px auto;
  padding: 0 16px;
}
</style>
