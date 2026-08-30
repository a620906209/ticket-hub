<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { getEventSalesReport } from '../../api/admin'
import type { SalesReport } from '../../types/apiResponses'
import { toErrorMessage } from '../../utils/errors'

const route = useRoute()
const eventId = route.params.eventId as string

const report = ref<SalesReport | null>(null)
const loading = ref(false)
const errorMessage = ref('')

async function loadReport(): Promise<void> {
  loading.value = true
  errorMessage.value = ''
  try {
    report.value = await getEventSalesReport(eventId)
  } catch (error) {
    errorMessage.value = toErrorMessage(error, '載入銷售報表失敗')
  } finally {
    loading.value = false
  }
}

onMounted(loadReport)

// 總數為 0 不是錯誤（活動可能尚未開賣或尚未有任何已付款訂單），用獨立的旗標區分「載入失敗」
// 與「目前沒有銷售資料」，不誤判為錯誤畫面（見 spec.md「查詢單一活動的銷售彙總報表」Requirement）。
const hasNoSales = computed(() => report.value !== null && report.value.totalTicketsSold === 0)

// unclassifiedItemCount 直接取自後端回應，不用 totalRevenue 減 byTicketType 加總反推——
// 金額或張數的差額無法反推出實際筆數（見 spec.md「依票種明細排除無法歸類票種的已付款項目...」Requirement）。
const hasUnclassifiedItems = computed(() => (report.value?.unclassifiedItemCount ?? 0) > 0)
</script>

<template>
  <div class="admin-sales-report-page">
    <h1>銷售報表</h1>
    <el-alert v-if="errorMessage" :title="errorMessage" type="error" show-icon style="margin-bottom: 16px" />

    <template v-if="report">
      <h2>{{ report.eventTitle }}</h2>

      <el-alert
        v-if="hasUnclassifiedItems"
        :title="`含 ${report.unclassifiedItemCount} 筆無法歸類的項目（金額 NT$ ${report.unclassifiedRevenue}、${report.unclassifiedTicketsSold} 張，已計入下方總計但未列入依票種明細）`"
        type="warning"
        show-icon
        style="margin-bottom: 16px"
      />

      <div class="summary">
        <div class="summary-item">
          <span class="label">總營收</span>
          <span class="value">NT$ {{ report.totalRevenue }}</span>
        </div>
        <div class="summary-item">
          <span class="label">總售出張數</span>
          <span class="value">{{ report.totalTicketsSold }}</span>
        </div>
      </div>

      <p v-if="hasNoSales" class="no-sales-hint">尚無銷售</p>
      <el-table v-loading="loading" :data="report.byTicketType" empty-text="尚無票種">
        <el-table-column prop="zoneCode" label="票種名稱／分區代碼" />
        <el-table-column label="模式">
          <template #default="{ row }">{{ row.requiresSeat ? '座位制' : '計數制' }}</template>
        </el-table-column>
        <el-table-column prop="quantitySold" label="售出張數" />
        <el-table-column label="營收">
          <template #default="{ row }">NT$ {{ row.revenue }}</template>
        </el-table-column>
      </el-table>
    </template>
  </div>
</template>

<style scoped>
.admin-sales-report-page {
  max-width: 800px;
}
.summary {
  display: flex;
  gap: 32px;
  margin-bottom: 24px;
}
.summary-item {
  display: flex;
  flex-direction: column;
}
.summary-item .label {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.summary-item .value {
  font-size: 24px;
  font-weight: 600;
}
.no-sales-hint {
  color: var(--el-text-color-secondary);
}
</style>
