<script setup lang="ts">
import { getOrderStatusLabel, getOrderStatusTagType } from '../utils/statusLabels'

defineProps<{ status: string }>()
</script>

<template>
  <el-tag :type="getOrderStatusTagType(status)" disable-transitions>{{ getOrderStatusLabel(status) }}</el-tag>
</template>
