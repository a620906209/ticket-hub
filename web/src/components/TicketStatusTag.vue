<script setup lang="ts">
import { getTicketStatusLabel, getTicketStatusTagType } from '../utils/statusLabels'

defineProps<{ status: string }>()
</script>

<template>
  <el-tag :type="getTicketStatusTagType(status)" disable-transitions>{{ getTicketStatusLabel(status) }}</el-tag>
</template>
