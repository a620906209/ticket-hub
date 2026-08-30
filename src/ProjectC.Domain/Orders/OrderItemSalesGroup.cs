namespace ProjectC.Domain.Orders;

/// <summary>依 <see cref="TicketTypeId"/> 分組後的一列已付款銷售彙總，純資料投影，不是聚合根、不帶行為。
/// <see cref="TicketTypeId"/> 為 <see langword="null"/> 代表該分組對應到 <c>OrderItem.TicketTypeId IS NULL</c>
/// 的項目（僅可能存在於本能力上線前建立的舊資料）；這個分組是否算「無法歸類」由 Application 層依票種目錄
/// 判斷（見 sales-report design.md 決策 2、3），這個 record 本身只反映查詢結果的真實形狀。</summary>
public sealed record OrderItemSalesGroup(Guid? TicketTypeId, int ItemCount, int QuantitySold, decimal Revenue);
