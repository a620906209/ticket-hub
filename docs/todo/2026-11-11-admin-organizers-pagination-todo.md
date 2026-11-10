# `GET /api/admin/organizers` 分頁觀察項目（2026-11-11）

## 背景

`docs/api-conventions.md` 第 3 節「列表分頁」把 `GET /api/admin/organizers`（全平台主辦方清單）判定為不做分頁，理由是規模可接受、列為觀察項目。原本沒有觸發條件與追蹤處，等於沒人會重看。

## 觸發條件

Organizer 總數超過 **200 筆**時，另開 change 套用 `list-pagination`（`PageRequest`／`PagedResultDto<T>`），並連同前端管理畫面一起改。

## 待辦

- [ ] 每次盤點專案進度時，以 DB 查詢 `Organizers` 筆數；達 200 筆即開分頁 change。
- [ ] 開 change 時：排序採 `CreatedAtUtc` 新到舊、`Id` 升冪；更新 `docs/api-conventions.md` 第 3 節與 `ApiConventionRuleTests` 的例外清單。
- [ ] 完成分頁後刪除本檔或移到 `docs/archive/`。
