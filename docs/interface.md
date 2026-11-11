# 当前接口与运行边界

0.4.0 新增 sw_api_catalog（接口/枚举检索）、sw_api_object（对象分页/释放）和 sw_submit 的 api_batch。通用调用使用官方原始单位，直接操作当前对象，不继承专用零件副本动作的自动复制保证。格式和完整示例见 [api-automation.md](api-automation.md)，精确字段见 Schema。

## 模型通信

`Bridge/SwBridge.exe --attach-only` 运行一个 MCP stdio 服务，采用 UTF-8、每行一个 JSON-RPC 2.0 消息。标准输出只发协议，诊断走标准错误。MCP 初始化后发现六个工具；对外无监听端口、无网络服务。

| 工具 | 必要输入 | 主要输出 |
|---|---|---|
| `sw_status` | 空对象 `{}` | connection、session_id、SolidWorks 版本/进程、文档与 revision、实际 capabilities |
| `sw_inspect` | session_id、document_id、scopes | 文档状态、按 scope 读取的特征/尺寸/几何引用/组件检查 |
| `sw_submit` | schema_version="1.0"、UUID request_id、session_id、expected_documents、execution、output_dir、action | validate 的检查结果或 accepted 的 job_id/状态 |
| `sw_job` | session_id、job_id、operation="get" 或 "cancel" | 作业终态、步骤、checks、changes、artifacts、error |

精确字段、取值及必填规则以 [JSON Schema](../Bridge/schemas/bridge.schema.json) 为准。MCP 工具结果为 `content[].text` 中的 JSON；协议版本 2025-06-18 同时提供 `structuredContent`。业务结果信封含 schema_version、request_id、session_id、status、data、error，不能只凭 RPC 成功判断 CAD 成功。

模型调用示例：

```json
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"sw_status","arguments":{}}}
```

`sw_submit` 的 `execution` 为 `validate` 或 `execute`；写入需启动参数 `--allow-write`。同一 request_id 与相同输入关联作业；输入不同则冲突。session 和实体引用只在对应桥接会话中有效。原始长度/角度 API 通常用米/弧度；对外维度变更使用 `value_mm` 或 `value_deg`，具体转换由适配层处理。

| 已实现 action | 范围 | 当前验证依据 |
|---|---|---|
| `set_parameters` | 已保存、独立原生零件的新副本，修改命名尺寸并重建读回 | 见 validation.json，不能把编译当作 CAD 写入验证 |
| `export_document` | 从零件副本导出 parasolid / step / stl / png | 文件验证范围见作业 artifacts |
| `insert_component_with_mates` | 已有零件副本插入固定基准件的 Z 向孔，建立同轴和端面重合配合 | 本机已有装配体实测通过 |
| `activate_document` | 恢复目标活动文档及视图，不改几何；仍需写入模式 | 本机窗口恢复实测通过 |
| `assemble_demo` | 在当前活动空装配体中创建圆环底座和阶梯轴，固定底座并添加两种配合 | 见 validation.json；无 Motion Study、无任意装配配方 |

作业终态包括 succeeded、failed、cancelled、interrupted、outcome_unknown。发生失败可能已有部分模型变更；`changes` 与 `error.step` 用来定位，自动回滚不作保证。默认输出在 `Bridge/models/<request_id>/`；每个脚本客户端使用独立状态目录。多实例用 `--pid` 指定目标，模板可用 `--part-template` 指定绝对路径。

桥接使用专用 STA 和 Windows 消息循环；传输线程不直接并行操作 CAD。每个目标 CAD 进程的本桥接写入互斥；无法阻止第三方程序同时修改。`busy` 只描述桥接队列，不是 SolidWorks 全局忙闲证明。

## 离线 API 查询

`Discovery/sw-api-probe.exe` 是独立查询入口，直接启动即可，无脚本宿主。程序从本机注册表和现有进程路径定位安装，使用官方 DLL 的反射元数据返回方法及枚举。它不会启动 SolidWorks、不会创建 COM 对象或执行模型方法。

输入选项：`--install-dir`、`--type`、`--member`、`--list-types`、`--output`。`--member` 依赖 `--type`，`--list-types` 与 `--type` 互斥；多安装版本时必须明确目标。输出为 UTF-8 JSON，例如：

```json
{"schema_version":"1.0","status":"ok","read_only":true,"model_connection_verified":false,"installations":[],"warnings":[]}
```

此例只展示信封，不是实际发现记录。真实 `installations` 包含 install_dir、libraries、product_version、local_help、sources；查询结果的 `api.methods` 包含 name、return_type、parameters（name/type/is_out/is_by_ref/is_optional），枚举使用 `enum_values`。

退出码 0 成功，1 未找到安装，2 参数或查询错误，3 输出失败。输出文件不覆盖已有文件，仅接受本地磁盘路径。签名、选择标记、调用上下文和单位语义仍需核对对应本机官方帮助。`bridge_loaded=unknown` 不应当成桥接正在运行。

## 已有零件插入与配合

`insert_component_with_mates` 输入 action：

```json
{"type":"insert_component_with_mates","document_id":"当前文档ID","base_component_id":"当前组件ID","hole_reference_id":"圆柱孔面ID","seat_reference_id":"承靠平面ID","source_part_path":"C:\\本机零件目录\\已配置的零件.SLDPRT","source_configuration":"实际存在的配置名"}
```

`source_configuration` 可省略；其余必填。外层仍使用 `sw_submit` 的 session、request_id、expected_documents、execution 和 output_dir。`execution=validate` 返回计划，`execute` 才复制文件、插入与配合。引用从 `sw_inspect` 的 `details.components[].component_id` 和 `faces[].reference_id` 获取；其中表面参数为 SolidWorks 原始 SI 值，bbox_mm 和 position_mm 为毫米，transform_data 为原始 16 项变换。

边界：固定顶层基准零件、局部 Z 向圆柱孔、孔上口的局部 Z 平面、单位比例变换。源文件须是本地 SLDPRT，已保存配置有唯一、原点中心的局部 Z 圆柱，半径不超过孔，且有唯一底端平面。Toolbox 母版方向/尺寸可能不满足条件；程序会在插入前拒绝。源文件复制到独立请求目录，去除复制件的 Toolbox 管理标记，原库文件保持完整；不自动改母版尺寸。

输出：新零件副本和新装配文件的路径、大小、SHA-256；检查记录包括原组件变换和固定状态、新组件数、两类配合、配合特征错误、原文件完整性及实测轴/孔直径。旋转自由度保留。装配文件可能仍引用既有背板的原路径，不是完整独立的 Pack and Go。

`activate_document` 的 action 只含 type 与 document_id，使用相同版本前置条件，用于切回既有文档；不改模型几何。文档导航是 `sw_submit` 内的一种动作。主插入动作无论成功或失败都会尝试切回目标装配体，恢复状态记录在 changes.restoration。

## 手动接入

手动配置示意（替换解压路径，默认只读；在现有 Codex MCP 配置中合并此区块）：

```toml
[mcp_servers.solidworks_local_01a1cb2e]
command = 'C:\实际解压目录\Bridge\SwBridge.exe'
args = ['--attach-only', '--state-root', 'C:\实际解压目录\Bridge\state\codex', '--output-root', 'C:\实际解压目录\Bridge\models']
```

## 窗口 PNG

在 `sw_inspect` 输入 scopes 中加入 `snapshot`；其余仍为当前 session_id 与 document_id。输出 `data.details.snapshot` 包含 path、format="png"、width_px、height_px、pid、document_id、revision、captured_at、sha256 及 visual_content_verified=false。图片写入配置输出根目录下的 inspections/唯一目录，不传 base64。成功生成图片不等于已经检查其视觉内容，Agent 应实际查看 PNG 并核对标题、组件树及模型。

仅捕获所附加 SolidWorks 的窗口；不截取整个桌面。目标文档须已激活、窗口未最小化；失败返回错误，不把空图当成功。捕获不改变模型几何。Windows 10/11 下按物理像素捕获并恢复线程 DPI 状态；不同显卡/远程桌面环境仍需查看图片确认。`activate_document` 可恢复目标窗口并调整为完整等轴测视图。
