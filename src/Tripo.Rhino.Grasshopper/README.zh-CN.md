# Tripo Grasshopper components

[English](./README.md) | 简体中文 | [Rhino 适配器](../../README.zh-CN.md)

`Tripo.Rhino.Grasshopper.gha` 是 tripo-rhino 面向 Rhino 8 Grasshopper 的可选
surface。用户可以在交互式 Grasshopper canvas 中显式创建 Tripo text-to-model 或
本地图生 3D task，再显式创建独立 OBJ conversion，并把经校验的结果输出为
Grasshopper `Mesh`。

它不是 Tripo、McNeel 或 Grasshopper 的官方产品。

GHA 不包含另一套 Tripo client 或 credential store。它复用 matching
`Tripo.Rhino.rhp` 已加载的 sidecar、API-key owner、paid-operation journal、
recovery records 与精确 Rhino document session。

Rhino panel 与 MCP document-import surface 还提供推荐的 direct
generation-GLB/PBR 路径。该路径有意不改变 GHA contract：**Tripo Task to Mesh**
仍使用显式 OBJ conversion，因为它必须在不 mutation Rhino document 的前提下发布
scalar Grasshopper `Mesh` value。

> **证据边界：**源码可针对 pinned Rhino 8 RhinoCommon 与 Grasshopper packages
> 编译。portable tests 覆盖 shared image staging、upload/generation recovery、OBJ
> staging 与 journal 行为。编译不能证明真实 Rhino/Grasshopper 已加载 GHA、解析
> 全部 assemblies、显示 components 或正确渲染输出。Windows/macOS 交互验收仍需
> 完成。

## Components

三个 components 都位于 **Tripo → Generate**：

| Component | Inputs | Outputs | 显式 menu actions |
| --- | --- | --- | --- |
| **Tripo Text Task** | `Prompt`、`Face Limit`、`With Materials` | task/status/progress/credits/operation/message | **Create text task…**、**Refresh task status** |
| **Tripo Image Task** | `Face Limit`、`With Materials` | task/status/progress/credits/operation/image SHA/message | **Choose image and create task…**、**Refresh task status** |
| **Tripo Task to Mesh** | `Source Task ID`、`Face Limit`、`With Materials` | `Mesh`、conversion task/status/progress/credits/operation/material names/message | **Create OBJ conversion…**、**Refresh conversion / load mesh** |

所有 input 都必须是 scalar。list 与 data-tree batching 会被拒绝，因为一个 component
只拥有一个可恢复 paid-operation identity。

Canvas recompute、`SolveInstance`、打开保存过的 `.gh` 与反序列化都不会创建或转换
模型。付费工作只能来自 component context menu 的 action，并需要在显示 durable
operation UUID 后明确确认费用。

## 要求

- Rhino 8 与 Grasshopper。
- 完整 matching `Tripo.Rhino.rhp` output 已安装，并在 Rhino 启动时加载；其
  `sidecar/` 必须保留在宿主 plug-in 旁。
- sidecar 需要 .NET 8 runtime；.NET 8 SDK 已包含该 runtime。
- Tripo v3 API key；通过 `TripoPanel` 配置，或由 sidecar 继承
  `TRIPO_API_KEY`。
- Rhino、GHA 与 sidecar 以同一操作系统用户运行。
- GHA、Rhino plug-in 与 sidecar 必须来自同一 repository revision。

付费 action 有意不支持 Grasshopper Player、headless 或 compiled-command 执行。

## 构建

在 repository root 运行：

```bash
dotnet restore src/Tripo.Rhino/Tripo.Rhino.csproj
dotnet restore src/Tripo.Rhino.Grasshopper/Tripo.Rhino.Grasshopper.csproj

dotnet build src/Tripo.Rhino/Tripo.Rhino.csproj \
  --configuration Release \
  --no-restore

dotnet build src/Tripo.Rhino.Grasshopper/Tripo.Rhino.Grasshopper.csproj \
  --configuration Release \
  --no-restore
```

重要输出：

```text
src/Tripo.Rhino/bin/Release/net7.0/
src/Tripo.Rhino.Grasshopper/bin/Release/net7.0/Tripo.Rhino.Grasshopper.gha
```

GHA output directory 还包含 project-reference artifacts；它不是 standalone
plug-in package，也不能替代完整 Rhino host output。

## 安装

1. 关闭 Rhino。
2. 按照 [Rhino adapter 安装指南](../../README.zh-CN.md#安装-rhino-plug-in) 安装完整
   Release Rhino host output，其中必须包含 `Tripo.Rhino.rhp`、
   `Tripo.Bridge.dll`、`Tripo.HostUi.dll` 与完整 `sidecar/`。
3. 启动一次 Rhino，并确认 command history 出现：

   ```text
   [Tripo] Rhino bridge and Eto panel ready for PID <process-id>.
   ```

4. 打开 Grasshopper，使用 **File → Special Folders → Components Folder**
   （不同 Rhino service release 的文字可能不同）打开当前 GHA assembly directory。
5. 再次关闭 Rhino，把同一 revision 的 `Tripo.Rhino.Grasshopper.gha` 复制到该目录。
6. 重启 Rhino 与 Grasshopper。不要直接从可能被 `dotnet clean` 删除的 `bin/`
   目录注册 GHA。
7. 确认 **Tripo → Generate** 下能看到全部三个 components。

打包后的 `.gha` 已包含在 Yak 包 `tripo-rhino` 中。下列步骤仍是手动开发安装。没有安装器、签名、notarization，也没有超出 Yak 包版本之外的自动更新。
McNeel 在
[Grasshopper Folders API](https://developer.rhino3d.com/api/grasshopper/html/T_Grasshopper_Folders.htm)
说明 assembly folders，并在
[Yak guide](https://developer.rhino3d.com/en/guides/yak/creating-a-grasshopper-plugin-package/)
说明未来 `.gha` packaging。

如果没有先安装并加载 matching Rhino host plug-in，只复制 `.gha` 不受支持，并应
fail closed。

## 配置 API key

1. 启动 Rhino 并打开目标 `.3dm`。
2. 运行 `TripoPanel`，或右键任一 Tripo component，选择
   **Open Tripo panel / API key…**。
3. 在 **API key…** 设置 session-only key，或写入 macOS Keychain / Windows
   Credential Manager。

GHA 不接收也不序列化 key。sidecar environment 中的 `TRIPO_API_KEY` 优先于 session
与 stored key。轮换 effective key 前先核对所有未完成的付费 UUID。

## 文生 Mesh 工作流

1. 打开目标 Rhino document 与关联该 document 的 Grasshopper definition。
2. 放置 **Tripo Text Task**，提供且只提供一个 prompt、face limit
   （500–200000）与 material choice。
3. 右键选择 **Create text task…**。
4. 核对显示的 UUID 与费用 warning；选择 **No** 不会发送付费请求。
5. 手动使用 **Refresh task status**，直到 task 为 `success` 或 terminal state；
   不会自动 polling。
6. 把 `Task ID` 接到 **Tripo Task to Mesh**，并设置 OBJ conversion 所需的 face
   limit/material intent。
7. 右键 mesh component，选择 **Create OBJ conversion…**；只有确实需要时才接受
   独立 conversion 费用。
8. 手动使用 **Refresh conversion / load mesh**。conversion 成功后，component
   会 staging、校验、project 并发布 GH mesh。

Generation 与 conversion 使用不同 UUID；响应丢失时应保留两者。

## 图生 Mesh 工作流

1. 放置 **Tripo Image Task**，设置一个 face limit/material choice。
2. 右键选择 **Choose image and create task…**。
3. 选择一张 1–20,000,000 bytes 的本地 PNG 或 JPEG。
4. 核对 UUID 与费用 warning 后再确认。
5. 手动 refresh 到 `success`。
6. 把 `Task ID` 接到 **Tripo Task to Mesh**，再执行文生流程的步骤 7–8。

源文件 path 与 filename 不会跨越 sidecar protocol，也不会保存进 `.gh`。选择后，
private snapshot 会写到：

```text
<TRIPO_LOCAL_DATA_DIR>/image-transfers/
```

component-owned private `.gh` state 可能保存 opaque transfer UUID、SHA-256、byte
length、media type、operation UUID/fingerprint、durable task IDs，以及 bounded
status/progress/credits；不会保存 source path、image bytes、Tripo `file_token`、
API key 或 transient UI error text。

普通 Grasshopper inputs 与 upstream data 遵循 Grasshopper 自身 serialization
规则，因此 Text component 的 prompt 或 persistent default 可能出现在整份 `.gh`
中；应把 definition 视为可能包含敏感信息的 model file。

Image operation 尚未解决时，不要删除 `image-transfers/`、`operations/` 或
`ui-recovery/`。upload token 已持久化后，同一 UUID 可继续 generation 而不会重新
upload。ambiguous upload 或 generation 会记录为 `outcome_unknown`，且绝不会自动
重发。

## Mesh 行为

- 输出是 associated Rhino document units 下的一个 Grasshopper `Mesh` value。
- Tripo 以 meters 表示的 Y-up/right-handed geometry 会转换为 Rhino
  Z-up/right-handed，并按 document units 缩放。
- 不创建 Rhino object、layer、block、material 或 Undo record。Bake 是独立的普通
  Grasshopper 用户动作。
- `With Materials=true` 会在存在时保留 validated UV，并返回 material names；不会
  自动为 GH mesh 绑定 Rhino document material、texture 或 PBR graph。
- operation 准备后若改变 source task、face limit、material flag、prompt 或 image
  identity，保存结果会标为 stale；mismatched inputs 不会得到旧 mesh，旧 UUID 也
  不会被复用。
- 删除 component 会停止 UI/mesh publication。已 admitted 的本地 sidecar wait 会继续
  到 durable task-ID 或 ambiguity safety checkpoint，远端 task 也可能继续；删除既不
  表示本地取消，也不表示远端取消。

## 恢复

- 只有在原 inputs 与 UUID 均未变化时，才使用 **Retry same … operation…**；
  原始本地 journal 也必须仍存在；它会 replay durable task ID，或 fail closed 而不
  创建 replacement operation。
- 使用 output `Operation ID` 与 `tripo_operation_status`/Tripo history 核对丢失响应。
- 不要为 `outcome_unknown` 创建 replacement UUID。
- conflicting recovery hint 会阻止新的 canvas paid action。打开 `TripoPanel`，核对
  **Review recovery…**；panel 会自动执行只读本地检查、展示证据与风险，并且只有
  完成人工 reconciliation 后才允许显式勾选确认。
- 打开已保存 definition 只恢复本地 IDs/status text；必须显式 refresh，加载本身
  不调用 Tripo。

## 当前限制

- 每个 input 只接受一个 scalar item；没有 list/data-tree fan-out。
- 只支持本地 PNG/JPEG；没有 WebP、public URL、clipboard 或 multiview input。
- 没有 one-call generation/conversion workflow，也不自动 polling。
- 没有 Grasshopper Player、headless、compiled-command 或 unattended paid mode。
- 不自动绑定 GH/Rhino materials。
- 没有安装器，也没有超出 Yak 包版本之外的自动更新。
- 真实 Rhino/Grasshopper Windows/macOS 加载、UI、scale 与视觉验收仍是 open gates。

规范 safety 与 evidence boundary 见
[ADR-0002](../../docs/adr/0002-recoverable-grasshopper-components.md)、
[Architecture](../../docs/ARCHITECTURE.md)、
[Security](../../docs/SECURITY.md) 与
[Testing](../../docs/TESTING.md)。
