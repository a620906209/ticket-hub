<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { getEvents } from '../../api/events'
import type { EventSummary } from '../../types/apiResponses'
import { toErrorMessage } from '../../utils/errors'

const events = ref<EventSummary[]>([])
const loading = ref(false)
const errorMessage = ref('')

async function loadEvents(): Promise<void> {
  loading.value = true
  errorMessage.value = ''
  try {
    events.value = await getEvents()
  } catch (error) {
    errorMessage.value = toErrorMessage(error, '載入活動列表失敗')
  } finally {
    loading.value = false
  }
}

onMounted(loadEvents)
</script>

<template>
  <div class="event-list-page">
    <h1>活動列表</h1>
    <el-alert v-if="errorMessage" :title="errorMessage" type="error" show-icon style="margin-bottom: 16px" />
    <el-empty v-else-if="!loading && events.length === 0" description="目前沒有活動" />
    <div v-else v-loading="loading" class="event-grid">
      <router-link v-for="event in events" :key="event.id" :to="`/events/${event.id}`" class="event-card">
        <h2>{{ event.title }}</h2>
        <p>{{ new Date(event.startAtUtc).toLocaleString() }}</p>
      </router-link>
    </div>
  </div>
</template>

<style scoped>
.event-list-page {
  max-width: 1080px;
  margin: 32px auto;
  padding: 0 16px;
}
.event-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(260px, 1fr));
  gap: 16px;
  margin-top: 24px;
}
.event-card {
  display: block;
  padding: 20px;
  border-radius: 8px;
  background: var(--color-bg-elevated);
  border: 1px solid var(--color-border);
  text-decoration: none;
  color: var(--color-text);
  transition:
    border-color 0.2s,
    box-shadow 0.2s;
}
.event-card:hover {
  border-color: var(--color-primary);
  box-shadow: 0 2px 8px rgb(0 0 0 / 6%);
}
.event-card h2 {
  margin: 0 0 8px;
  font-size: 18px;
}
.event-card p {
  margin: 0;
  color: var(--color-text-secondary);
  font-size: 14px;
}
</style>
