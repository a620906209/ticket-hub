---
name: ui-router
description: Route frontend and UI tasks to the appropriate design skill based on whether the task is a new interface, redesign, visual polish, animation, or animation review.
---

# UI Skill Routing

當任務涉及前端 UI、UX、版面、樣式、元件設計或動畫時，先判斷任務類型，再選擇主要 UI skill。不要在尚未判斷情境前同時套用所有 UI skill。

## 路由規則

- 新增 Landing Page、Portfolio 或行銷頁面：使用 `taste-skill`（frontmatter name 為 `design-taste-frontend`）
- 改造既有網站或 App：使用 `redesign-skill`
- 使用者明確要求極簡、編輯風格、暖色單色系或 Bento Grid：使用 `minimalist-skill`
- UI 細節、元件質感、互動回饋或 UI 程式碼審查：使用 `emil-design-eng`
- 從零新增動畫或轉場：使用 `animate`
- 審查既有動畫：使用 `review-animations`

## 使用原則

1. 一次選擇一個主要設計 skill，避免互相衝突的視覺規則同時生效。
2. 只有在需要互動細節或元件質感審查時，才額外使用 `emil-design-eng`。
3. `minimalist-skill` 是明確的視覺風格，不是所有前端任務的預設規範。
4. `redesign-skill` 用於既有介面，不應取代從零建立新頁面的設計判斷。
5. 實作前先說明選用的主要 skill 與設計方向；若需求足夠明確，不要為了形式額外提問。
6. 保留既有前端框架與樣式系統，除非使用者明確要求技術遷移。
7. UI 實作完成前，檢查可及性、響應式行為、loading／empty／error 狀態與 `prefers-reduced-motion` 支援。
8. 不論選用哪個視覺 skill，都遵守專案既有的 Vue、XSS 防護與 API service 分層規範。
