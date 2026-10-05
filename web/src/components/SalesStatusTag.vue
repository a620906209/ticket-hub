<script setup lang="ts">
import type { SalesStatus } from '../utils/salesWindow'

defineProps<{ status: SalesStatus }>()

// 狀態以文字表達，顏色只是輔助（event-sales-window-web-ui design.md 決策 5）。
const SALES_STATUS_DISPLAY: Record<SalesStatus, { label: string; tagType: 'success' | 'info' }> = {
  NotOpen: { label: '尚未開賣', tagType: 'info' },
  Open: { label: '販售中', tagType: 'success' },
  Closed: { label: '已停售', tagType: 'info' },
}
</script>

<template>
  <el-tag :type="SALES_STATUS_DISPLAY[status].tagType" disable-transitions>{{ SALES_STATUS_DISPLAY[status].label }}</el-tag>
</template>
