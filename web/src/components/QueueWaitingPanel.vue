<script setup lang="ts">
defineProps<{
  waitingCount: number | null
}>()
</script>

<template>
  <div class="queue-waiting-panel">
    <h3>排隊中，請稍候</h3>
    <p v-if="waitingCount !== null">目前前方還有 {{ waitingCount }} 人等待</p>
    <p class="hint">系統會自動為您輪詢排隊狀態，輪到您時會自動開放選位與下單</p>
  </div>
</template>

<style scoped>
.queue-waiting-panel {
  text-align: center;
  padding: 48px 16px;
  background: var(--color-bg-elevated);
  border: 1px solid var(--color-border);
  border-radius: 8px;
}
.queue-waiting-panel h3 {
  margin: 0 0 8px;
}
.queue-waiting-panel p {
  margin: 4px 0;
  color: var(--color-text-secondary);
}
.hint {
  font-size: 13px;
}
</style>
