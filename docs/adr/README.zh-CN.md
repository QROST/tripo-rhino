# 架构决策记录

[English](./README.md) | 简体中文

ADR 用于记录 `tripo-rhino` 中需要长期保持的设计决策，是 trust boundary、front
door 与 credential ownership 的变更控制入口。实现若要违背现有 ADR，必须先更新
该 ADR 或用新的 ADR supersede 它。

| ID | 标题 | 状态 |
| --- | --- | --- |
| [0001](./0001-dual-front-doors-and-sidecar-credentials.md) | 双入口（宿主 UI + MCP）与仅由 sidecar 持有 API credential | Accepted；§3.3 的 Grasshopper scope 已由 0002 部分 supersede |
| [0002](./0002-recoverable-grasshopper-components.md) | 在 Rhino sidecar 上提供显式、可恢复的 Grasshopper components | Accepted |

相关的非 ADR 设计约束：

- [`../ARCHITECTURE.md`](../ARCHITECTURE.md) — 当前 trust boundary 与阶段顺序
- [`../SECURITY.md`](../SECURITY.md) — credential、journal、bridge 与 artifact 规则
- [`../MATERIALS-DESIGN.md`](../MATERIALS-DESIGN.md) — OBJ baked-diffuse 材质与导入模式

各 ADR 的英文正文是规范版本；其中的中文摘要仅用于辅助阅读。
