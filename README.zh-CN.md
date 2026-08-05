# Tripo-Rhino

[English](./README.md) | 简体中文

Tripo-Rhino 是面向 Rhino 8 的独立社区适配器。AEC 用户可以在 per-document Eto
panel 中使用 text-to-model workflow，也可以通过可选 Grasshopper GHA 显式执行
text/本地图生 3D 并得到 Grasshopper mesh value；agentic client 可通过 MCP 使用
同一个 sidecar。Rhino 推荐路径会把成功 generation 的 GLB 直接导入为原生 PBR
block，不需要第二个 conversion task；经校验的 OBJ 仍是显式 mesh/block 兼容路径，
也是 Grasshopper 的 stage-only 格式。

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
> text/本地 PNG 或 JPEG workflow、credential dialog、sidecar launcher、direct
> GLB/PBR import 与 bundled sidecar layout 已存在源码，并有 portable
> control/workflow/MCP/process tests。macOS 开发宿主已实际验证手动 package
> layout、Eto panel、Keychain-backed credential 保存/使用、text generation 与约
> 两秒一次的 generation progress 刷新。同一宿主还已在三个彼此独立的全新 Rhino
> process 中用一份真实 provider GLB 做 proof-stability 压力测试，随后又对实际安装
> 的 proof-v5/schema-3 binary 做 exact canary；每轮都验证了同 UUID 的只读
> `already_exists` replay。
> Windows CI 还会对隔离、合成的 Credential Manager target 执行
> write/read/delete canary。可选 GHA 加载、production-user Credential Manager、
> Windows 宿主加载、Undo、scale/orientation、性能与视觉/材质验收仍是彼此独立的
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
没有 Yak package，也没有 `.macrhi`。上述 package-folder layout 已在 macOS Rhino
8 开发宿主中实际使用，但它不是已签名或普遍支持的安装器。

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

native 持久化 identity 是：macOS generic-password item 的 service
`ai.qrost.TripoMCPs.TripoV3`，account 为当前 OS username；Windows Generic
Credential 的 target 为 `TripoMCPs/TripoV3/<username>`。Windows 不会把 key 写入
project 或临时文件。不要自行创建 temp key file：不需持久化时应取消勾选，仅存
sidecar session memory；需要持久化则使用当前用户 native store；MCP 使用 client
secret/environment 机制。`secrets/tripo-v3-api-key` 只是在不支持 native store 的
OS 上明确报告的 private fallback，绝不是 Windows 或 macOS 路径。

最安全的本地数据设置是 Rhino 与 MCP server 都不设置
`TRIPO_LOCAL_DATA_DIR`。双方会共同使用当前用户 local application-data 目录下的
`TripoMCP`。

若自定义该目录：

- 在启动 Rhino 之前，以及 MCP client 启动 server 之前设置完全相同的值；
- 使用稳定本地 filesystem 上的绝对、私有路径；
- 不要使用 NFS/SMB；
- 恢复期间不要移动或删除其中的 `bridges`、`controls`、`staging`、
  `host-import-snapshots`、`host-imports`、`image-transfers`、`operations`、
  `secrets` 或 `ui-recovery`。

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
   active account-bound recovery 中该 action 仍可用，但会强制 session-only：
   ambiguous paid UUID 必须恢复精确原 key；accepted task/import 必须使用同一
   Tripo account 的 key。workflow 已解决并显式 reset 后，只有 sidecar 能明确证明
   OS-stored key 存在且可删除时，**Remove saved key…** 才会启用。其默认选择 No
   的确认框会同时清除 session key 与 stored key。`TRIPO_API_KEY` 环境覆盖仍然
   有效，并会禁用 panel credential actions；需在 Rhino 外修改后重启 Rhino。
4. 输入 prompt、face limit 与材质选项，然后点击 **Generate**。panel 会先显示
   可选择复制的 durable operation UUID，再显示 credit confirmation；拒绝确认不会
   发送付费请求。Rhino 会在私有本地 `ui-settings/rhino-panel.json` preference
   file 中记住最后一次合法的 face limit、材质偏好与 object name；不会保存
   prompt、API key、task/operation ID、document path 或 import source。
5. generation 在 `queued` 或 `running` 时会约每两秒自动刷新；也可点击
   **Refresh generation** 立即刷新，直到 task 为 `success`。
6. 保持 **Direct GLB (recommended)**，输入 block name，再点击
   **Import GLB (recommended)**。该路径下载 generation GLB 并保留 Rhino-native
   PBR；不会创建 conversion task，也不会消耗第二次 conversion credits。
7. 只有 direct GLB 不可用，或明确需要 OBJ/GH mesh 时才选择 **OBJ
   compatibility**：点击 **Convert to OBJ**、确认独立的可能费用、刷新至成功，再选择
   `native`/`mesh`/`instance` 与 baked-diffuse material 选项后导入。

每个新建 panel 都会从 **Direct GLB (recommended)** 开始，即使上一个 panel
曾使用 OBJ compatibility。compatibility 路由只在当前 session 生效，不会在重启后
意外成为 conversion charge。

panel 仅对已有 durable task ID 的 generation status 自动进行 single-flight
只读轮询；不会自动发送付费请求，也不声称能取消远端 task。响应丢失后，该阶段必须
先执行 **Refresh**；只有 paid-operation journal 表明 creation 可以继续时，重试才会启用，
button 会明确标为 **Retry same UUID**。一旦取得 durable task 或 import receipt，
对应阶段 action 就会禁用，不会伪装成新请求。任意 dispatch 未决时，
**New workflow** 会禁用；account-bound workflow 显式 reset 前，stored-key
replacement 与 clear 都会禁用。当前 effective key 缺失或被拒时，只能以
session-only key 恢复当前 workflow。只要 Rhino 保留该 panel instance，隐藏或关闭
tab 都不会取消 workflow。durable
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
API-key change 还会拒绝 Rhino 或 Revit 中任何尚未 reset 的
generation/conversion workflow、未确认 import、无法确认已退出的 foreign-owner
record 或无效 recovery storage。只有 host、recovery ID、process identity、启动
时间与 owned path 全部匹配时，才可仅排除当前 panel 自己的 hint。一个 root-global
UI intent lease 会串行化跨 panel 的 credential-recovery scan、key-mutation request
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
5. 确认 client 列出了下文九个 tools。
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

MCP 入口通过以下九个 tools 暴露同一套 shared workflow：

| 工具 | 主要参数 | 作用 |
| --- | --- | --- |
| `tripo_host_context` | 无 | 读取已连接 Rhino 进程与精确 active-document session；不调用 Tripo API。 |
| `tripo_task_status` | `taskId` | 查询一个已有 Tripo task。 |
| `tripo_operation_status` | `operationId` | 读取一条持久化的本地 paid-operation record；不调用 Tripo 或 Rhino。 |
| `tripo_create_text_task` | `prompt`、`faceLimit`、`withMaterials`、`documentSessionId`、`operationId`、`confirmExternalCost` | 创建一个 text-to-model task。`withMaterials=true` 请求 textured PBR generation（`texture`/`pbr`）；`false` 保持 geometry-only。可能消耗 credits。 |
| `tripo_stage_local_image` | `localImagePath` | 校验并私有 snapshot 一张本地 PNG/JPEG，返回 opaque descriptor；不调用 Tripo。 |
| `tripo_create_image_task` | `transferId`、`sha256`、`byteLength`、`mediaType`、`faceLimit`、`withMaterials`、`documentSessionId`、`operationId`、`confirmExternalCost` | 上传一张 staged image，并以独立 upload/generation durable checkpoints 创建 image-to-model task。四个 descriptor fields 必须原样复制自 `tripo_stage_local_image`。可能消耗 credits。 |
| `tripo_import_generation_glb` | `generationTaskId`、`name`、`documentSessionId`、`operationId`、`applyMaterials`（必须为 `true`） | Rhino 推荐路径：下载并原生导入成功 generation 的 GLB，创建一个 PBR block；不创建 conversion task，也没有额外 Tripo 费用。 |
| `tripo_create_obj_conversion` | `sourceTaskId`、`faceLimit`、`withMaterials`、`documentSessionId`、`operationId`、`confirmExternalCost` | 创建一个 OBJ conversion。`withMaterials=true` 请求带 baked-diffuse MTL 与 image textures 的 OBJ bundle（`bake=true`）；`false` 只转换几何。可能消耗 credits。 |
| `tripo_import_obj_task` | `conversionTaskId`、`name`、`documentSessionId`、`operationId`、`importMode`（默认 `native`）、`applyMaterials`（默认 `false`） | 下载、校验并将成功的 OBJ conversion 导入为一个 Rhino mesh 或 block instance。 |

输入边界：

- `prompt`：1–1024 个字符；
- `faceLimit`：500–200000；
- 导入对象 `name`：1–128 个字符；
- task ID 必须原样使用 Tripo 返回值：接受当前 v3 的 `task_...`，也接受
  legacy-compatible response 中的 canonical lowercase UUID；
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
4. Rhino 推荐路径：生成 UUID B，调用 `tripo_import_generation_glb`，并保持
   `applyMaterials=true`。检查 receipt 与创建出的 PBR block instance；已提交的
   import 应可由一次 Rhino Undo 撤销。
5. OBJ 兼容路径：生成 UUID B，再次取得明确费用确认后调用
   `tripo_create_obj_conversion`；轮询至 `success`，再生成 UUID C 并调用
   `tripo_import_obj_task`，按需选择 mode 与 baked-diffuse material policy。

推荐的 direct 路径中，generation 使用 `withMaterials=true`，direct import 保持其
必需的 `applyMaterials=true`。带材质的 OBJ fallback 中，两个付费创建阶段都使用
`withMaterials=true`，import 使用 `applyMaterials=true`。geometry-only 仅通过
OBJ fallback 提供，并把该路径的三个 flags 都保持为 `false`。

generation 与 direct GLB import 使用两个不同的 caller-owned UUID；OBJ fallback
使用三个：generation、conversion 与 host import。工作流执行期间不要切换或关闭
active document；在付费操作前、下载/导入前以及 Rhino UI-thread mutation 内部都会
重新核对 document session。

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

导入恢复有意采用不同规则。必须复用精确 import UUID、source task/artifact
content、name、解析后的 mode 与 materials flag。Direct GLB 还使用 flushed
host-import journal：`prepared`、`outcome_unknown`、corrupt 或 incomplete 状态都
绝不授权再次执行 native import；`committed` replay 只读核对 exact root GUID、
block members、计数、geometry digest 与 PBR-content digest。Rhino 重启后，应
重新打开同一个已保存的目标 document，
调用 `tripo_host_context` 并传入新的 `documentSessionId`。journal 与 document
不一致时必须人工检查，不能重发 paid 或 native request。

## Rhino 导入行为

- **Direct GLB（推荐）：**sidecar 按 signed-URL policy 下载成功 generation 的 GLB，
  校验 content-addressed manifest、container、bounded glTF arrays/buffer
  references 与 embedded PNG/JPEG dimensions，再只把 verified bytes 交给 Rhino。
  host 从这些 bytes 建立 private random fixed snapshot，先在 headless Rhino
  document preflight，再把同一 hash 导入 active document。
- native GLB 会保留 Rhino render/PBR materials 与 embedded textures，并包装进
  deterministic `Tripo_<operationId>` block，只建立一个带 identity 的 root
  `InstanceObject`。write-through host journal 会在 native import 前与 commit 后
  flush；任意 ambiguous native outcome 返回 `mutation_state_uncertain`、禁用 UI
  retry，并要求人工核对 document/journal。
- Direct GLB 仅接受 embedded PNG/JPEG，并在 native parser 前限制 64 MiB GLB、
  4 MiB JSON、arrays、accessors、buffer ranges、总计 64 MiB decoded accessor
  data、单边 4096 pixels、单图 16 Mi pixels 与所有图片合计 32 Mi pixels。Rhino
  native parser 仍在 Rhino process 内运行；headless preflight 只隔离 target
  document，不是 process crash isolation。
- portable semantic proof 必须在 headless preflight、active import 与完成后的
  block definition 之间一致。它覆盖精确 mesh data 与 UV、持久 mapping
  definitions、transforms、选定 material source 与 effective
  front/back/plugin/subobject bindings、严格 allowlist 内的内建 PBR/basic
  materials 与 bitmap textures、canonical persistent RDK fields、递归
  child-slot/on/amount state、legacy-material fallback values，以及每个可读
  referenced texture file 的 SHA-256。projection、wrap、mapping、
  linear-workflow 与 normal-map 语义由这些持久 fields 与 child-slot 语义覆盖，
  不依赖派生的 `RenderTexture` runtime getters。portable proof 排除
  document-owned render hash、派生的 cached texture coordinates/getters，以及
  精确 allowlist 内不影响语义的 editor/preview fields。随后把完成 definition
  的 document proof 持久化，只读 replay 必须与它精确一致。custom/procedural
  render content、parent-inherited 或 non-object plugin material source，以及
  unsafe/unreadable texture references 都会 fail closed。journal schema 3 会
  记录 PBR proof version 5 并强制要求 durable proof；schema 2、较旧 proof
  version 或不完整记录会明确要求人工检查，不能授权 replay。
- fixed GLB snapshot 通常会在 import lease 结束时删除。后续 import 只会
  best-effort、有界清理严格命名且超过 24 小时的 snapshot，并且必须能确认记录的
  owner PID 已不存活。每次最多检查 256 项、mutation 16 项；symlink/reparse
  content 会被拒绝，并使用当前进程拥有的 quarantine/tombstone 名称，不做递归
  删除。
- **OBJ 兼容路径：**converted OBJ（以及存在时的 MTL 与 PNG/JPEG textures）会成为
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
- OBJ import UUID 与 canonical import-identity fingerprint 会写入 object attributes；
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

### `mutation_state_uncertain`

不要再次点击或脚本调用 import。保留 `host-imports/`；若 Rhino 允许，先另存当前
`.3dm` 副本，再人工核对对应 block/root 与本地 journal。即使 best-effort Undo
看似成功，native importer 也可能已经开始；只有完整验证后的 `committed` replay
可以返回 `already_exists`。

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
- OBJ 兼容路径的材质上限是 baked diffuse（OBJ `Kd`/`d`/`Tr` color/alpha 加每个
  slot 一张 `map_Kd` texture）；direct GLB 会保留 Rhino-native PBR channels 与
  embedded textures。Text generation 关闭 quad output；OBJ conversion 关闭 quad
  output 与 animation。
- GHA 只支持 scalar、interactive workflow；没有 Grasshopper Player、headless、
  automatic polling、automatic material binding 或 one-call paid workflow。
- 没有 Yak package、安装器、签名、notarization 或自动更新。
- Production HTTP connections 有意不使用 system proxies。
- macOS 真实宿主已部分验收 panel loading、Keychain-backed credential、
  generation、status polling、direct GLB import 与即时同 UUID replay。
  Direct GLB/PBR 视觉效果、one-step Undo、save/reopen replay、可选 GHA 与
  Windows 真实宿主行为仍待验收。

详细信任与验收边界见 [Architecture](./docs/ARCHITECTURE.md)、
[Materials design](./docs/MATERIALS-DESIGN.md)、
[Security](./docs/SECURITY.md) 和
[Testing and evidence](./docs/TESTING.md)。

仓库来源与参考项目取舍见 [Migration](./docs/MIGRATION.md) 和
[Blender reference](./docs/BLENDER-REFERENCE.md)；候选包流程见
[`packaging/`](./packaging/README.md)。

## 许可证

本仓库采用 Apache License, Version 2.0（`Apache-2.0`）。详见
[`LICENSE`](./LICENSE) 与 [`NOTICE`](./NOTICE)。

本产品与 Tripo、McNeel 均无隶属或背书关系。用户需自备 Tripo API key（BYOK），
API 使用仍受 Tripo 服务条款约束。Blender 参考项目只用于学习仓库与产品结构，
没有复制其源码。
