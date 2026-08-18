// 純前端本地狀態型別，不對應任何後端 API DTO，不與 api.generated.ts／apiResponses.ts 同名（見設計文件決策 4）。

export interface CreatedVenue {
  id: string
  name: string
}

export interface CreatedSeatMap {
  id: string
  venueId: string
  seatCount: number
}

export interface SelectedSeat {
  eventSeatId: string
  zoneCode: string
  seatNumber: string
  ticketTypeId: string
  price: number
}
