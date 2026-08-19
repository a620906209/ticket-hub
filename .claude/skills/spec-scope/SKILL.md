---
name: spec-scope
description: 專案初期唯一一次的宏觀需求盤點,產出 docs/project-scope.md
---

執行前檢查 docs/project-scope.md 是否已存在:
- 已存在 → 告知使用者此指令僅執行一次,若要修改請直接編輯該檔案,詢問是否仍要重跑
- 不存在 → 依序詢問以下 8 大類問題(每類 4-6 題),
  逐類等待使用者回答後才進下一類,不要一次列完 40 題轟炸使用者

1. 商業目標與使用者
2. 功能地圖與優先級(MoSCoW)
3. 資料與實體
4. 外部整合
5. 非功能需求(要求具體數字,如 QPS、SLA)
6. 技術限制
7. 明確排除項目
8. 里程碑

全部回答完後,整理成 docs/project-scope.md,格式為純 Markdown
(不使用 OpenSpec 的 Requirement/Scenario 語法)。