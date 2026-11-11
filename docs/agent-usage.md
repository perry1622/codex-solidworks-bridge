# Agent 使用说明

0.4.0 共六个工具，新增 sw_api_catalog、sw_api_object。通用官方 API 调用见 [api-automation.md](api-automation.md)，下文保留专用装配流程。

先读取包根目录 `AGENTS.md`、`package.json` 和 `validation.json`。MCP 名称为 `solidworks_local_01a1cb2e`，程序为 `Bridge/SwBridge.exe`。工具不可见时先完成 README 的接入步骤；本包不会自动安装到所有会话。`skills/solidworks-local-bridge/` 可按所在 Codex 环境的技能安装方式使用，也可直接读取本文件。

## 最短操作顺序

1. 调用 `sw_status({})`：要求 `status=ok`、`data.connection=connected`，取得 `session_id`、活动 `document_id`、`revision` 和实际 capabilities。多实例不猜测目标，使用 `--pid` 连接目标实例。
2. 调用 `sw_inspect`：读取目标组件、固定状态、几何面引用和配合。组件与面 ID 只能来自本次连接，不能从旧日志抄用。
3. 优先现有零件和标准件。按需运行 `Discovery/sw-api-probe.exe` 定位 Toolbox 和官方帮助；检查实际保存配置、尺寸及轴向。PreviewCfg 母版不代表已选规格，不能缩放冒充标准规格。
4. 已授权且 `write_enabled=true` 时，先提交 `execution=validate` 核对前置条件，再提交 `execute`，并保留 request_id、job_id。验证计划通过不代表 CAD 装配已成功；源零件的部分几何条件在执行打开副本后才检查。
5. 用 `sw_job` 查看终态；重新 `sw_status` 和 `sw_inspect` 核对组件、配合、文件以及原组件位置/固定状态。需要窗口回读时增加 `snapshot` 并实际查看 PNG。只有数值与视觉检查符合任务时才报告完成。

## 请求格式模板

以下是工具 arguments，须替换占位符；MCP 客户端负责 JSON-RPC 外层。

读取活动装配体：

```json
{
  "session_id": "本次状态返回的UUID",
  "document_id": "本次活动装配体ID",
  "scopes": ["summary", "geometry", "mates", "checks", "snapshot"]
}
```

`data.details.components[].component_id` 是基准组件引用，其 `faces[].reference_id` 用于选孔圆柱面和承靠平面。`data.details.snapshot.path` 是 PNG 文件路径，`visual_content_verified=false` 表示程序尚未替 Agent 看图；不要只凭生成文件就声称视觉验证通过。

插入已有零件并建立同轴、端面重合配合：

```json
{
  "schema_version": "1.0",
  "request_id": "新建UUID",
  "session_id": "本次状态返回的UUID",
  "expected_documents": [
    {"document_id": "目标装配体ID", "revision": "最新revision"}
  ],
  "execution": "validate",
  "output_dir": "C:\\实际解压目录\\Bridge\\models",
  "action": {
    "type": "insert_component_with_mates",
    "document_id": "目标装配体ID",
    "base_component_id": "固定基准组件ID",
    "hole_reference_id": "孔圆柱面ID",
    "seat_reference_id": "承靠平面ID",
    "source_part_path": "C:\\零件库\\已核规格的零件.SLDPRT",
    "source_configuration": "实际存在的配置名"
  }
}
```

确认前置条件后将 `execution` 改为 `execute`；保持目标状态新鲜。执行已提交后，同一 request_id 只可重放完全相同的输入；输入变化应使用新 UUID。接收 `accepted` 后取 `data.job_id` 查询：

```json
{"session_id":"本次会话UUID","job_id":"返回的作业UUID","operation":"get"}
```

查询调用成功不代表作业成功，应检查 `data.state`、`checks`、`changes`、`artifacts` 和 `error`。长度变更为 mm、角度为 deg；原始表面参数和变换使用 SI，见接口文档。

## 关键边界

- 当前插入仅支持固定顶层基准件的局部 Z 向孔和上口承靠平面；源件须满足唯一居中 Z 向圆柱和底端平面的条件。详见 [接口](interface.md)。`assemble_demo` 仅用于空装配体。
- 插入会修改当前装配体，再另存新文件；源零件使用独立副本和唯一文件名。另存装配可能仍引用原有零件，不是自动独立打包。
- 操作后必须切回目标装配体。主插入动作会在成功/失败时尝试恢复；若需要 `activate_document`，它也通过 `sw_submit` 提交，并需要写入模式及新鲜 revision。
- 超时或部分失败时先查作业和实际模型，禁止盲目重新插入。部分修改不保证自动回滚，`cancel` 也只在步骤边界生效。
- 重启桥接后重新获取 session、文档和面引用。保持原 state-root 才能保留既有作业记录；不要共用另一个正在运行的桥接的状态目录。

离线查询示例（查到方法存在不代表已验证建模可用）：

```powershell
.\Discovery\sw-api-probe.exe
.\Discovery\sw-api-probe.exe --type IAssemblyDoc --member AddMate5
```
