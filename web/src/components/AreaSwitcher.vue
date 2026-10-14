<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '../stores/auth'

type Area = 'buyer' | 'organizer' | 'platform'

// 目前區域由版面指定而非從路由推導，路由改名時不會判斷錯（design.md 決策 2）
const props = defineProps<{ current: Area }>()

const router = useRouter()
const authStore = useAuthStore()

const areaLabels: Record<Area, string> = { buyer: '會員前台', organizer: '主辦方後台', platform: '平台管理' }
const areaPaths: Record<Area, string> = { buyer: '/', organizer: '/admin', platform: '/platform' }

// 只有會員前台頂列在 320px 擠不下完整名稱；另外兩區的導覽列放得下，不縮寫（web-area-separation 4.4）
const isAbbreviable = computed(() => props.current === 'buyer')

const areaItems = computed(() => {
  const areas: Area[] = authStore.isAdmin ? ['buyer', 'organizer', 'platform'] : ['buyer', 'organizer']
  return areas.map((area) => ({
    area,
    label: area === props.current ? `${areaLabels[area]}（目前）` : areaLabels[area],
    isCurrent: area === props.current,
  }))
})
</script>

<template>
  <el-dropdown class="area-switcher" trigger="click" popper-class="area-switcher-popper">
    <button
      type="button"
      class="area-switcher-trigger"
      :class="{ 'area-switcher-trigger--abbreviable': isAbbreviable }"
      :aria-label="isAbbreviable ? areaLabels[current] : undefined"
      :title="isAbbreviable ? areaLabels[current] : undefined"
    >
      <span class="area-switcher-swatch" :class="`area-switcher-swatch--${current}`" aria-hidden="true" />
      <span class="area-switcher-label">{{ areaLabels[current] }}</span>
      <span v-if="isAbbreviable" class="area-switcher-label-short" aria-hidden="true">前台</span>
    </button>
    <template #dropdown>
      <el-dropdown-menu>
        <el-dropdown-item class="area-switcher-account" disabled>登入帳號：{{ authStore.member?.email }}</el-dropdown-item>
        <el-dropdown-item
          v-for="item in areaItems"
          :key="item.area"
          :disabled="item.isCurrent"
          @click="router.push(areaPaths[item.area])"
        >
          {{ item.label }}
        </el-dropdown-item>
      </el-dropdown-menu>
    </template>
  </el-dropdown>
</template>

<style scoped>
.area-switcher-trigger {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-height: 32px;
  padding: 0 10px;
  border: 1px solid var(--el-border-color);
  border-radius: 4px;
  background: var(--color-bg-elevated);
  color: var(--color-text);
  font-size: 14px;
  white-space: nowrap;
  cursor: pointer;
}
.area-switcher-trigger:hover {
  border-color: var(--el-border-color-hover);
}
.area-switcher-swatch {
  width: 10px;
  height: 10px;
  border-radius: 2px;
  flex-shrink: 0;
}
/* 會員前台沒有強調色，以頁面底色加框表示「沒有色條的那一區」 */
.area-switcher-swatch--buyer {
  background: var(--color-bg);
  border: 1px solid var(--el-border-color);
}
.area-switcher-swatch--organizer {
  background: var(--color-area-organizer);
}
.area-switcher-swatch--platform {
  background: var(--color-area-platform);
}
.area-switcher-label-short {
  display: none;
}

@media (max-width: 480px) {
  .area-switcher-trigger--abbreviable .area-switcher-label {
    display: none;
  }
  .area-switcher-trigger--abbreviable .area-switcher-label-short {
    display: inline;
  }
}
</style>
