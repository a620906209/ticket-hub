<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import type { FormInstance } from 'element-plus'
import { register } from '../../api/auth'
import { emailRules, maxLengthRule, passwordRules, requiredRule } from '../../utils/validators'
import { toErrorMessage } from '../../utils/errors'

const router = useRouter()

const formRef = ref<FormInstance>()
const form = reactive({ email: '', password: '', displayName: '' })
const rules = {
  email: emailRules,
  password: passwordRules,
  displayName: [requiredRule('請輸入顯示名稱'), maxLengthRule(100, '顯示名稱長度不可超過 100 字')],
}

const submitting = ref(false)
const errorMessage = ref('')

async function handleSubmit(): Promise<void> {
  errorMessage.value = ''
  const valid = await formRef.value?.validate().catch(() => false)
  if (!valid) return

  submitting.value = true
  try {
    await register(form.email, form.password, form.displayName)
    ElMessage.success('註冊成功，請登入')
    await router.push('/login')
  } catch (error) {
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
      <el-form-item>
        <el-button type="primary" :loading="submitting" native-type="submit">註冊</el-button>
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
</style>
