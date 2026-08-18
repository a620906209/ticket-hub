import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { CreatedSeatMap, CreatedVenue } from '../types/ui'

// 後端沒有場館/座位圖查詢 API（見設計文件 Non-Goals），這裡只存本次瀏覽器分頁 session 內
// 建立過的紀錄，純記憶體狀態、不落地 localStorage，重新整理即消失。
export const useAdminVenueCacheStore = defineStore('adminVenueCache', () => {
  const venues = ref<CreatedVenue[]>([])
  const seatMaps = ref<CreatedSeatMap[]>([])

  function addVenue(venue: CreatedVenue): void {
    venues.value = [venue, ...venues.value]
  }

  function addSeatMap(seatMap: CreatedSeatMap): void {
    seatMaps.value = [seatMap, ...seatMaps.value]
  }

  return { venues, seatMaps, addVenue, addSeatMap }
})
