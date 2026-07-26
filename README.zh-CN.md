# Tripo-Rhino

[English](./README.md) | 简体中文

Tripo-Rhino 是面向 Rhino 8 的独立社区适配器。AEC 用户可以在 per-document Eto
panel 中使用 text-to-model workflow，也可以通过可选 Grasshopper GHA 显式执行
text/本地图生 3D 并得到 Grasshopper mesh value；agentic client 可通过 MCP 使用
同一个 sidecar。经校验的 OBJ 可以导入精确 active Rhino document 成为 mesh/block，
也可以不 mutation document，仅 staging 给 Grasshopper。

它不是 Tripo 或 McNeel 官方产品。

```text
Rhino Eto / 可选 Grasshopper             MCP client
                   ↕ host-control           ↕ stdio
       Tripo.Rhino.Mcp sidecar / server ── Tripo v3 HTTPS API
                    ↕ authenticated protocol-v2 host bridge
                 Tripo.Rhino.rhp
                    ↕ Rhino UI thread + one undo record
                 exact active Rhino document
```

sidecar 是唯一解析、存储或使用 Tripo API key 的进程。plug-in 的 password dialog
只通过 authenticated local control channel 转发临时值并清空输入框；它不会把 key
写入 Rhino settings 或 `.3dm` document。

> **当前状态：**`.rhp` 与可选 `.gha` 以 Rhino 8 为目标，并可针对 pinned
> RhinoCommon/Grasshopper packages 编译。Eto text workflow、Grasshopper
> text/本地 PNG 或 JPEG workflow、credential dialog、sidecar launcher 与 bundled
> sidecar layout 已存在源码，并有 portable control/workflow/MCP/process tests。
> 真实 Rhino panel/GHA 加载、component 可见性与 menu 交互、macOS Keychain 与
> Windows Credential Manager 交互、Undo、scale/orientation、性能与视觉验收仍是
> open gates。目前没有 Yak package、安装器、签名、notarization 或自动更新机制。

GHA 的详细构建、安装、components、隐私与恢复说明见
[Grasshopper 指南](./src/Tripo.Rhino.Grasshopper/README.zh-CN.md)。

## 前置要求

- Rhino 8。
- 使用 .NET 8 SDK restore 和 build 项目。仓库选择 `8.0.100`，并允许在 .NET 8
  内按 `latestFeature` roll forward；restore 需要访问 NuGet。
- 运行 framework-dependent MCP server 需要 .NET 8 runtime；SDK 已包含该 runtime。
- 只有可选 MCP 路径需要支持 stdio server 的 MCP client。
- remote generation 与 conversion 需要 Tripo v3 API key。
- Rhino、panel sidecar 与任何 MCP server 必须以同一个操作系统用户运行。

宿主 plug-in 以 `net7.0` 为目标，并针对 RhinoCommon
`8.32.26160.13001` 编译。仓库尚未确定 Rhino 8 的最低 service release，也没有形成
完整验收过的 Windows/macOS runtime matrix。

## 构建

在仓库根目录运行：

```bash
dotnet restore src/Tripo.Rhino/Tripo.Rhino.csproj
dotnet restore src/Tripo.Rhino.Grasshopper/Tripo.Rhino.Grasshopper.csproj
dotnet restore src/Tripo.Rhino.Mcp/Tripo.Rhino.Mcp.csproj

dotnet build src/Tripo.Rhino/Tripo.Rhino.csproj \
  --configuration Release \
  --no-restore

dotnet build src/Tripo.Rhino.Mcp/Tripo.Rhino.Mcp.csproj \
  --configuration Release \
  --no-restore

dotnet build src/Tripo.Rhino.Grasshopper/Tripo.Rhino.Grasshopper.csproj \
  --configuration Release \
  --no-restore
```

输出目录：

```text
src/Tripo.Rhino/bin/Release/net7.0/
src/Tripo.Rhino.Grasshopper/bin/Release/net7.0/
src/Tripo.Rhino.Mcp/bin/Release/net8.0/
```

每个输出目录必须整体保留：

- 从同一次 build 部署 `Tripo.Rhino.rhp`、`Tripo.Bridge.dll`、
  `Tripo.HostUi.dll`、完整生成的 `sidecar/` 目录与其他宿主输出文件；
- MCP assembly、`.deps.json`、`.runtimeconfig.json` 与 dependency files 必须放在
  一起，不能只部署 `Tripo.Rhino.Mcp.dll`；
- 只有在 matching 的完整 Rhino host output 已安装并能在启动时加载后，才安装
  `Tripo.Rhino.Grasshopper.gha`；GHA output 本身不是完整 sidecar deployment；
- `RhinoCommon` 由 Rhino 提供，因此有意不复制到本地；
- `.pdb` 是可选的调试符号。

把部署产物复制到稳定目录。若 `bin/` 可能被 `dotnet clean` 删除，不要直接从该目录
注册 plug-in。宿主 build 的 `sidecar/` 目录是 panel runtime；只有配置 MCP client
时才需要单独的 `src/Tripo.Rhino.Mcp/bin/Release/net8.0/` 输出。Bridge protocol v2 与
host-control protocol v3 都没有向后兼容 shim，因此所有组件必须来自同一个仓库
revision。

## 安装 Rhino plug-in

当前仓库只提供手动开发安装方式。

### Windows

1. 关闭 Rhino。
2. 把完整的 `src/Tripo.Rhino/bin/Release/net7.0/` 输出复制到稳定的本地 plug-in 目录。
3. 启动 Rhino 8 并运行 `PlugInManager`。
4. 选择安装/加载操作，并选中该目录中的 `Tripo.Rhino.rhp`。
5. 重启 Rhino，以实际执行 plug-in 的 startup load 路径。
6. 打开或新建一个 Rhino document。
7. 确认 Rhino command history 中出现：

   ```text
   [Tripo] Rhino bridge and Eto panel ready for PID <process-id>.
   ```

8. 运行 Rhino command `TripoPanel`，打开 per-document **Tripo** panel。

McNeel 的 Windows 指南确认 `.rhp` 可通过 `PlugInManager` 加载：
[Registering Plugins (Windows)](https://developer.rhino3d.com/guides/rhinocommon/registering-plugins-windows/)。
精确菜单标签与下载文件的安全提示可能随 Rhino build 改变。

### macOS

Rhino for Mac 不使用 Windows Plug-in Manager 流程。手动、按版本安装开发 build 的
步骤是：

1. 退出 Rhino。
2. 把完整的 `src/Tripo.Rhino/bin/Release/net7.0/` 输出复制到一个稳定目录，再把这个
   “包含输出的目录”重命名为 `Tripo.Rhino.rhp`；不要重命名目录内部的
   `Tripo.Rhino.rhp` assembly。
3. 把生成的 package directory 放到：

   ```text
   ~/Library/Application Support/McNeel/Rhinoceros/8.0/MacPlugIns/Tripo.Rhino.rhp/
   ```

   package directory 内必须包含同一次 build 的 `Tripo.Rhino.rhp` assembly、
   `Tripo.Bridge.dll`、`Tripo.HostUi.dll` 与完整 `sidecar/`。
4. 重启 Rhino，打开一个 document，检查上文的 ready 消息，并运行
   `TripoPanel`。

McNeel 文档说明了 `.rhp` package-folder 约定，以及 Rhino 8 按版本使用的
`MacPlugIns` 位置：
[Plugin Installers (Mac)](https://developer.rhino3d.com/guides/rhinocommon/plugin-installers-mac/)。
McNeel 目前说明 `.macrhi` 已不再积极开发，并建议使用 Package Manager。本仓库既
没有 Yak package，也没有 `.macrhi`；该手动布局也尚未在真实宿主 canary 中验收。

## 安装可选 Grasshopper components

先安装并验证完整 `.rhp`/`sidecar/`。然后在 Grasshopper 中打开
**File → Special Folders → Components Folder**，关闭 Rhino，把同一 revision 的
`src/Tripo.Rhino.Grasshopper/bin/Release/net7.0/Tripo.Rhino.Grasshopper.gha` 复制到该
assembly directory。重启 Rhino/Grasshopper，并确认 **Tripo → Generate** 下包含
**Tripo Text Task**、**Tripo Image Task** 与 **Tripo Task to Mesh**。

GHA output 不是完整部署。它复用 startup-loaded `.rhp` 中的 sidecar manager、
credential owner、document-session registry、journal 与 recovery store；不支持只
复制 `.gha`。完整步骤见
[Grasshopper 部署与使用指南](./src/Tripo.Rhino.Grasshopper/README.zh-CN.md)。

## 配置可选 MCP server

最可移植的调用方式是使用 `dotnet` host 与 MCP assembly：

```text
dotnet /absolute/path/to/tripo-rhino/src/Tripo.Rhino.Mcp/bin/Release/net8.0/Tripo.Rhino.Mcp.dll
```

使用绝对路径。若 GUI MCP client 的 `PATH` 受限，把 `command` 设为 `dotnet`
executable 的绝对路径。

下面是使用 `mcpServers`、`command`、`args` 与 `env` 的 client 常见配置结构。请按
实际 client schema 与 secret 机制调整：

```json
{
  "mcpServers": {
    "tripo-rhino": {
      "command": "dotnet",
      "args": [
        "/absolute/path/to/tripo-rhino/src/Tripo.Rhino.Mcp/bin/Release/net8.0/Tripo.Rhino.Mcp.dll"
      ],
      "env": {
        "TRIPO_API_KEY": "REPLACE_USING_YOUR_CLIENT_SECRET_MECHANISM"
      }
    }
  }
}
```

Windows JSON 中的反斜线必须转义：

```json
{
  "mcpServers": {
    "tripo-rhino": {
      "command": "dotnet",
      "args": [
        "C:\\absolute\\path\\to\\tripo-rhino\\src\\Tripo.Rhino.Mcp\\bin\\Release\\net8.0\\Tripo.Rhino.Mcp.dll"
      ],
      "env": {
        "TRIPO_API_KEY": "REPLACE_USING_YOUR_CLIENT_SECRET_MECHANISM"
      }
    }
  }
}
```

若 Windows 的 `Tripo.Rhino.Mcp.exe` 或 macOS 的 `Tripo.Rhino.Mcp` apphost
是为相同 OS 与 architecture 构建的，也可以直接使用。`dotnet` 加 `.dll` 的方式不会
假设 apphost 的可移植性。

### 环境变量

| 变量 | 设置位置 | 要求 |
| --- | --- | --- |
| `TRIPO_API_KEY` | sidecar / MCP server | 可选的环境 key；存在时覆盖 session key 与 stored key。panel 不需要该变量也能设置 key。 |
| `TRIPO_MODEL` | sidecar / MCP server | 可选的 text-generation model identifier。默认是 `v3.1-20260211`；覆盖值必须匹配 `[A-Za-z0-9._-]{1,64}`，会由 text-task receipt 返回，并属于 text-task 付费请求 identity。panel 路径需在 Rhino 启动前设置。 |
| `TRIPO_HOST_PID` | 仅 MCP server | 同时存在多个 live Rhino bridge 时必需，且必须是正整数。 |
| `TRIPO_LOCAL_DATA_DIR` | Rhino 与 sidecar / MCP server | 可选的绝对、私有、稳定本地路径；所有参与进程必须解析到完全相同的值。 |
| `TRIPO_SIDECAR_PATH` | 仅 Rhino process | 可选的 matching `Tripo.Rhino.Mcp.dll` 或 native apphost 绝对路径，仅供开发覆盖。正常部署使用安装目录内复制的 `sidecar/`；需在 Rhino 启动前设置。 |

panel 路径请使用 **API key…** dialog：保持 **Save in this user's OS credential
store** 勾选，会写入 macOS Keychain 或 Windows Credential Manager；取消勾选则只
保存在 sidecar process memory。UI 只报告 `environment`、`session`、`store` 或
`none`，绝不回显 key。只有不支持上述 desktop API 的平台，persistence 才使用并
明确报告 private-file fallback。MCP 路径最好使用 client credential store 或继承的
进程环境。普通 `env` 对象可能以明文保存 key；`${NAME}` 插值由 client 决定，不能
假设一定支持。本仓库不会加载 `.env` 文件。替换 effective key 会改变
paid-operation identity，并可能让未完成 panel 或 MCP operation 的 same-UUID
recovery fail closed；轮换 key 前必须先核对并解决每个未完成的付费 UUID。

最安全的本地数据设置是 Rhino 与 MCP server 都不设置
`TRIPO_LOCAL_DATA_DIR`。双方会共同使用当前用户 local application-data 目录下的
`TripoMCP`。

若自定义该目录：

- 在启动 Rhino 之前，以及 MCP client 启动 server 之前设置完全相同的值；
- 使用稳定本地 filesystem 上的绝对、私有路径；
- 不要使用 NFS/SMB；
- 恢复期间不要移动或删除其中的 `bridges`、`controls`、`staging`、
  `image-transfers`、`operations`、`secrets` 或 `ui-recovery`。

只在 MCP client 中设置该变量会让 server 与 Rhino 使用不同的 discovery/staging
roots，无法建立正确的 bridge 连接。

`image-transfers` 可能暂存由 Grasshopper 或 MCP 选择的 PNG/JPEG 私有 snapshot，
直到 file-token 或 upload-ambiguity checkpoint 持久化。它不是 import allowlist；
恢复期间必须与 journal 一起保留。

## 使用 Rhino panel

1. 启动 Rhino，打开目标 document，并运行 `TripoPanel`。
2. per-document panel 会自动连接精确 active document；也可以点击
   **Connect / Refresh** 显式刷新。若 workflow 已有 state，不同 document session
   会被拒绝，不会静默继承旧 task。
3. 若没有可用 key，在 **API key…** 粘贴 key，并选择 persistent 或 session-only
   storage。可在 [Tripo Platform](https://platform.tripo3d.ai/api-keys) 创建 key。
4. 输入 prompt、face limit 与材质选项，然后点击 **Generate**。panel 会先显示
   可选择复制的 durable operation UUID，再显示 credit confirmation；拒绝确认不会
   发送付费请求。
5. 点击 **Refresh generation**，直到 task 为 `success`。
6. 点击 **Convert to OBJ**。该阶段生成另一个 UUID，并需要第二次独立费用确认；
   刷新 conversion 直到成功。
7. 选择 object name、`native`/`mesh`/`instance` 与是否应用 baked diffuse
   materials，然后点击 **Import into Rhino**。

panel 不自动轮询，也不声称能取消远端 task。响应丢失后，该阶段必须先执行
**Refresh**；只有 paid-operation journal 表明 creation 可以继续时，重试才会启用，
button 会明确标为 **Retry same UUID**。一旦取得 durable task 或 import receipt，
对应阶段 action 就会禁用，不会伪装成新请求。任意 dispatch 未决时，
**New workflow** 会禁用；付费 dispatch 未决时，API-key mutation 也会禁用。只要
Rhino 保留该 panel instance，隐藏或关闭 tab 都不会取消 workflow。durable
`request_rejected` 不属于未决状态：generation 被拒会清除 generation 与 downstream
stage；conversion 被拒只清除 conversion/import，并保留成功 generation。修正
credential 后，为被拒阶段准备新 UUID。

dispatch 前，shared state layer 会在
`<TRIPO_LOCAL_DATA_DIR>/ui-recovery/rhino/<recovery-id>.json`
（或默认 local-data root）原子写入 private recovery hint。hint 只包含 UUID、已知的
durable task ID 与 import retry 所需的最少参数；不包含 prompt、API key、
Authorization header、URL 或任意 path。

这个具有独立 identity 的 hint 会在 import 成功后继续保留，直到 live workflow 被
显式 reset。关闭 document、销毁 inspector、退出 Rhino 或崩溃都不会取消远端 task。
下次打开 panel 时会显示 stale recovery ID，并阻止新 workflow。
另一个 Rhino process 拥有的 hint 会保守地阻塞，因为不能跨 process 猜测 panel
session 是否仍存活；recovery 必须在 owner process 中完成，或在确认它退出后再进行。
API-key change 还会拒绝 Rhino 或 Revit 中任何未决且已记录的 UI paid hint、无法
确认已退出的 foreign-owner record 或无效 recovery storage。一个 root-global UI
intent lease 会串行化跨 panel 的 credential-recovery scan、key-mutation request
与 paid dispatch call。另一个 private sidecar execution lease 会持有实际 key
mutation，以及每个 UI 或 standalone MCP paid workflow 从 credential-derived
fingerprint 直到 durable task、明确 `request_rejected` 或 ambiguous-outcome journal
checkpoint 的全过程；即使 UI pipe 断开也不会提前释放。同一时间只允许一个 key
mutation 或 paid create/convert；发生竞争时，必须等当前 operation checkpoint 后，
以同一 UUID 重试。
**Review recovery…** 会自动且只读地查询本地 `operation_status`，不会重发付费
call 或 import。对话框会区分 durable task、same-UUID recovery、ambiguous outcome
与缺失本地证据，并在归档本地提示前要求用户显式勾选确认。import 必须回到原始
document 核对；本地证据缺失或 ambiguous 时，还必须检查 Tripo task 与 billing
history。对话框会把 recovery 文件与完整的本地 journal receipt（或明确的
unavailable 结果）一起绑定进用户看到的 snapshot；归档前会连续重新查询并比较。
归档重查期间还会持有 paid UI/MCP 与 key mutation 共用的 execution lease。
集合或 status 变化时会拒绝归档，仍在进行中的 operation 也会继续阻塞。若当前
panel 还持有 workflow state，**Reload and review all work…** 会把已派发 ID
保留为 recovery evidence，只清除从未发送的 setup，再统一审阅。手工修复无效
文件后可直接点击 **Refresh recovery status**。无效、超限、未知 schema、Unix
上非 private 或 symlink hint 会继续阻塞，等待人工检查。paid-operation journal
才是 authority，hint 不是。Eto panel 仍然只支持 text-to-3D；本地 image
controls 目前由可选 Grasshopper components 与 MCP tools 提供。

## 使用 Grasshopper components

1. 打开目标 Rhino document 与关联的交互式 Grasshopper definition。付费 action 会
   拒绝 headless、Player 与 compiled-command context。
2. 通过 `TripoPanel` 或 component menu 的 **Open Tripo panel / API key…** 配置
   sidecar key。
3. 放置 **Tripo Text Task** 或 **Tripo Image Task**。右键选择显式 create action，
   核对 durable UUID 与费用 warning，确认后才发送请求。Image 模式只接受一张
   1–20,000,000 bytes 的本地 PNG/JPEG。
4. 手动 refresh generation status，直到 `success`。
5. 把 task ID 接到 **Tripo Task to Mesh**，右键选择
   **Create OBJ conversion…**，单独确认可能的 conversion 费用；成功后再手动
   refresh/load。
6. 在 definition 中继续使用输出的 Grasshopper `Mesh`，需要时可通过普通
   Grasshopper UI bake。

Canvas recompute 与打开 `.gh` 都不会 dispatch 付费工作。Mesh 会从 meters 缩放到
关联 Rhino document units，不创建 Rhino object 或 Undo record。
`With Materials=true` 会在存在时保留经校验的 UV 与 material names，但不会自动绑定
Rhino/PBR materials。完整说明见
[GHA 指南](./src/Tripo.Rhino.Grasshopper/README.zh-CN.md)。

## 启动并验证 MCP

可选 MCP 路径的推荐顺序：

1. 安装 plug-in。
2. 启动 Rhino 并打开目标 document。
3. 等待 bridge-ready 消息并记下 PID。
4. 启动或重启 MCP client，让它启动 `Tripo.Rhino.Mcp`。
5. 确认 client 列出了下文八个 tools。
6. 调用 `tripo_host_context`。

成功的 context receipt 证明 MCP server 已经连接到 Rhino。它会返回宿主版本、
process ID、document title、document units、capabilities 与临时
`documentSessionId`。

项目没有 HTTP endpoint 或独立 `--health` 命令。直接运行 MCP server 时，它会等待
stdio handshake。

若只有一个 Rhino bridge 存活，server 会自动选择它。多个 live Rhino instances 会
fail closed 为 `host_ambiguous`；把 `TRIPO_HOST_PID` 设为目标 Rhino 进程打印的
PID，然后重启 MCP server。

## MCP 工具

MCP 入口通过以下八个 tools 暴露同一套 shared workflow：

| 工具 | 主要参数 | 作用 |
| --- | --- | --- |
| `tripo_host_context` | 无 | 读取已连接 Rhino 进程与精确 active-document session；不调用 Tripo API。 |
| `tripo_task_status` | `taskId` | 查询一个已有 Tripo task。 |
| `tripo_operation_status` | `operationId` | 读取一条持久化的本地 paid-operation record；不调用 Tripo 或 Rhino。 |
| `tripo_create_text_task` | `prompt`、`faceLimit`、`withMaterials`、`documentSessionId`、`operationId`、`confirmExternalCost` | 创建一个 text-to-model task。`withMaterials=true` 请求 textured PBR generation（`texture`/`pbr`）；`false` 保持 geometry-only。可能消耗 credits。 |
| `tripo_stage_local_image` | `localImagePath` | 校验并私有 snapshot 一张本地 PNG/JPEG，返回 opaque descriptor；不调用 Tripo。 |
| `tripo_create_image_task` | `transferId`、`sha256`、`byteLength`、`mediaType`、`faceLimit`、`withMaterials`、`documentSessionId`、`operationId`、`confirmExternalCost` | 上传一张 staged image，并以独立 upload/generation durable checkpoints 创建 image-to-model task。四个 descriptor fields 必须原样复制自 `tripo_stage_local_image`。可能消耗 credits。 |
| `tripo_create_obj_conversion` | `sourceTaskId`、`faceLimit`、`withMaterials`、`documentSessionId`、`operationId`、`confirmExternalCost` | 创建一个 OBJ conversion。`withMaterials=true` 请求带 baked-diffuse MTL 与 image textures 的 OBJ bundle（`bake=true`）；`false` 只转换几何。可能消耗 credits。 |
| `tripo_import_obj_task` | `conversionTaskId`、`name`、`documentSessionId`、`operationId`、`importMode`（默认 `native`）、`applyMaterials`（默认 `false`） | 下载、校验并将成功的 OBJ conversion 导入为一个 Rhino mesh 或 block instance。 |

输入边界：

- `prompt`：1–1024 个字符；
- `faceLimit`：500–200000；
- 导入对象 `name`：1–128 个字符；
- task ID 必须以 `task_` 开头；
- `documentSessionId` 必须是 `tripo_host_context` 返回的精确 UUID；
- 每个 `operationId` 都是 caller-generated UUID；
- `importMode` 可以是 `native`、`mesh` 或 `instance`；本 build 会以
  `import_mode_unsupported` 拒绝 `family`，而 `native` 解析为 `instance`；
- `applyMaterials=true` 时，若 converted bundle 没有 MTL 会 fail closed；在
  `mesh` 模式下，OBJ 使用多个 `usemtl` material slots 时也会拒绝。

只有在用户明确接受可能的外部费用后，`confirmExternalCost=true` 才有效。

## 典型工作流

1. 调用 `tripo_host_context` 并保留其精确 `documentSessionId`。
2. 选择一种 generation 分支：
   - text：生成 UUID A；在用户明确确认费用后调用 `tripo_create_text_task`；
   - 本地图像：先调用 `tripo_stage_local_image`，再生成 UUID A；明确确认费用后，
     把 descriptor 返回的 `transferId`、`sha256`、`byteLength` 与 `mediaType`
     分别传入四个同名参数，再调用 `tripo_create_image_task`。
3. 用 `tripo_task_status` 轮询返回的 task ID，直到 `success` 或 terminal failure。
   遇到 `failed`、`cancelled`、`banned` 或 `expired` 时停止。
4. 生成 UUID B；再次取得明确费用确认后调用
   `tripo_create_obj_conversion`。
5. 轮询 conversion task，直到 `success` 或 terminal failure。
6. 生成 UUID C，调用 `tripo_import_obj_task`，并按需选择
   `importMode` 与 `applyMaterials`。
7. 检查 receipt 与创建出的 Rhino mesh 或 block instance。已提交的 import 应可由
   一次 Rhino Undo 撤销。

若要导入材质，两个付费创建阶段都使用 `withMaterials=true`，导入阶段使用
`applyMaterials=true`；geometry-only 工作流则把三个 flags 都保持为 `false`。

text creation、OBJ conversion 与 host import 必须使用三个不同的 caller-owned
UUID。工作流执行期间不要切换或关闭 active document；在付费操作前、下载/导入前以及
Rhino UI-thread mutation 内部都会重新核对 document session。

付费阶段响应丢失时，先使用 `tripo_operation_status` 检查对应的本地记录。只有
journal 表明 creation 可以继续时，才以原 UUID、完全相同的显式参数、API key 与
document session 重试；text-task 重试还必须保持相同的 effective model。

若 operation 为 `outcome_unknown`，不要自动重发或创建替代 UUID。保留 journal，并
人工核对 Tripo task 或 billing history。

若 operation 为 `request_rejected`，provider 已明确拒绝请求且没有创建 task。修正
credential 并准备新 UUID；不要重试被拒 UUID。

Image creation 会分别 checkpoint upload 与 generation。持久化的 `file_token` 允许
继续 generation 而不再次 upload。若 upload 或 generation 结果不明确，journal 会
记录具体 stage 并拒绝自动重发；人工核对前需保留 `image-transfers/` 与 journal。

导入恢复有意采用不同规则。复用 import UUID、conversion task 与 artifact
content、name、解析后的 mode 和 materials flag。Rhino 重启后，应重新打开同一个目标
document，调用
`tripo_host_context`，并传入新的 `documentSessionId`；host fingerprint 排除了该
临时 session ID，但 active-session check 仍会 fail closed。要让
`already_exists` 跨应用重启生效，原 import 后必须保存 `.3dm`；若未保存的改动已经
丢失，持久化文档中没有可回放对象，重试会再次 commit 该 import。

## Rhino 导入行为

- Converted OBJ（以及存在时的 MTL 与 PNG/JPEG textures）会成为
  content-addressed bundle。每个 entry 都按 manifest 校验 SHA-256 和字节数，然后
  在 mutation 前完成解析与几何校验。bundle 最多保留 32 个 entries，每个最多
  128 MiB，总计最多 256 MiB。
- 首版把 `auto_size=true` 的输出视为 meters。
- Y-up、right-handed 输入会转换为 Rhino Z-up，并按 active document 的 unit
  system 缩放。
- 两种导入模式：`mesh` 创建一个 Rhino mesh object；`instance` 创建一个 block
  definition（每个 material slot 一个 sub-mesh）与一个 `InstanceObject`。
  `importMode=native` 解析为 `instance`。`AddMesh`/`AddInstanceObject`、
  object attributes、undo record 与 redraw 都在 Rhino UI thread 上运行。
- `applyMaterials=true` 通过 `TextureCoordinates` 与 Rhino render `Material`
  应用 baked-diffuse color，以及存在时的 diffuse texture。`mesh` 模式不会把多个
  OBJ `usemtl` slots 静默压到单个 mesh；multi-slot bundle 必须使用 `instance`。
  Texture validation 会以文末列出的 typed errors fail closed。
- import UUID 与 canonical import-identity fingerprint 会写入 object attributes；
  fingerprint 有意排除临时 `documentSessionId`。`mesh` 模式写在 mesh object 上；
  `instance` 模式写在 `InstanceObject` 与 block definition 内每个 geometry member
  上。若崩溃留下已创建但没有引用的 block definition，会先验证 member fingerprint，
  再补建缺失 instance。
- 对已 committed 的相同 import 重试会返回现有对象，不会创建第二个对象。崩溃恢复
  可能补建缺失的 instance，但不会复制已经验证的 block definition。
- 使用相同 import UUID 但改变参数（包括不同 `importMode` 或
  `applyMaterials`）会返回 idempotency conflict；这里比较的是解析后的
  `importMode`。
- Rhino 必须足够空闲，以便建立一条独立 undo record。

## 导入回执

宿主 receipt 会报告 `createdId`（Rhino mesh 或 `InstanceObject` GUID）、
`transactionStatus`（`committed` 或 `already_exists`）、解析后的 `importMode`、
几何计数，以及 prepared `materialCount`/`textureCount`。Rhino 的
`savedFamilyPath` 始终为 `null`。这些字段是 mutation/idempotency 路径的证据，不是
视觉渲染验收。

## 故障排查

### `host_unavailable`

检查 Rhino 是否正在运行、plug-in 是否已加载、是否出现 bridge-ready 消息、两个进程
是否使用同一 OS account、`TRIPO_HOST_PID` 是否正确，以及
`TRIPO_LOCAL_DATA_DIR` 是否在两端都未设置或完全相同。还应从同一个 revision 替换
完整 plug-in 与 MCP 输出并重启两个进程；host-control 版本混合部署通常会被 discovery
忽略，并表现为 `host_unavailable`。

### `host_ambiguous`

有多个 Rhino bridge 存活。把 `TRIPO_HOST_PID` 设为目标 Rhino PID，然后重启 MCP
server。

### API key 错误

在 MCP server 环境中设置真实 key。只提供 key 本身：不要添加 `Bearer`、空白、
control characters 或字面 quote characters。JSON 配置仍需要用双引号包围字符串，
但这些 delimiters 不属于 key。`tripo_host_context` 与本地 operation-status 读取
不需要 key；Tripo API tools 需要。

### `document_unavailable` 或 document-session 错误

打开目标 Rhino document 并重新调用 `tripo_host_context`。文档被切换、关闭或重开
后，paid-operation identity 不会迁移到新 session。已发送或响应丢失的付费阶段应先
调用 `tripo_operation_status`。除非 journal 报告明确 `request_rejected`，否则保留
原 UUID 与 identity；若被明确拒绝，应修正 credential 并准备新 UUID。
只有重新打开同一个目标 document，并保持原 import UUID、conversion task 与
content、name、解析后的 mode 和 materials flag 时，import retry 才能使用新
session。

### `host_busy` 或 `undo_unavailable`

等待当前 Rhino command 或 undo activity 结束，然后用同一个 import UUID 与相同参数
重试。

### MCP 进程无法启动

确认已经安装 .NET 8 runtime，command 与 assembly path 都是绝对路径，完整 MCP 输出
目录仍在，并且 client 能解析配置的 `dotnet` executable。

### `outcome_unknown`

远端付费请求可能已经成功。查询 `tripo_operation_status`、保留 journal，并检查
Tripo task/billing history；不要自动发送另一个付费请求。进程被终止后可能留下可读的
`dispatching` record；acquire 同一个 operation 会把它改写为
`outcome_unknown`，但不会重发。

### 材质或 bundle 错误

在以 `applyMaterials=true` 导入前，OBJ conversion 必须使用
`withMaterials=true`。MTL 引用的 texture entry 不在 bundle 时返回
`mtl_invalid`；staged file 缺失时返回 `artifact_missing`；字节数或 SHA-256
不匹配时返回 `artifact_hash_mismatch`；Rhino bitmap binding 失败时返回
`mtl_invalid`。`mesh` 模式下多个 OBJ `usemtl` slots 需要改用 `instance`。

## 当前限制

- 每次 import 创建一个 Rhino mesh（`mesh`）或一个 block instance
  （`instance`）；没有 placement controls，且 mesh 模式最多承载一个 material slot。
- 当前宿主 panel 只支持 text-to-3D。图像选择、upload 与 image-to-3D creation
  已由可选 GHA 与 MCP 提供；panel image mode、WebP 与 public URL input 尚未实现。
- text-generation 默认 model 是 `v3.1-20260211`；`TRIPO_MODEL` 可以选择另一个
  通过语法校验的 identifier，改变它也会改变 text-task paid-operation identity。
- 材质只支持 baked diffuse（OBJ `Kd`/`d`/`Tr` color/alpha 加每个 slot 一张
  `map_Kd` texture）：没有真正的 PBR channels，也没有 native GLB import。
  Text generation 关闭 quad output；OBJ conversion 关闭 quad output 与 animation。
- GHA 只支持 scalar、interactive workflow；没有 Grasshopper Player、headless、
  automatic polling、automatic material binding 或 one-call paid workflow。
- 没有 Yak package、安装器、签名、notarization 或自动更新。
- Production HTTP connections 有意不使用 system proxies。
- Windows/macOS 上尚未完成真实宿主验收。

详细信任与验收边界见 [Architecture](./docs/ARCHITECTURE.md)、
[Materials design](./docs/MATERIALS-DESIGN.md)、
[Security](./docs/SECURITY.md) 和
[Testing and evidence](./docs/TESTING.md)。

仓库来源与参考项目取舍见 [Migration](./docs/MIGRATION.md) 和
[Blender reference](./docs/BLENDER-REFERENCE.md)；候选包流程见
[`packaging/`](./packaging/README.md)。

## 许可证

尚未选择发布许可证。在许可证落定前，请不要把当前源码视为已授权再分发。Blender
参考项目只用于学习仓库与产品结构，没有复制其源码。
