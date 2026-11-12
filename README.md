# Codex ↔ SOLIDWORKS 官方 API 桥接层

**0.4.0：按官方 Interop 元数据检索和调用接口。** Windows x64 / .NET Framework 4.8；CAD 操作需要本机已安装、获得许可且已打开的 SolidWorks。

Windows 智能应用控制可能拦截本包的未签名程序；遇到拦截时，可能需要关闭“智能应用控制”后运行。请先确认包的来源，具体设置见 [Microsoft 官方说明](https://support.microsoft.com/en-us/windows/security/threat-malware-protection/smart-app-control-frequently-asked-questions)。安装脚本不会更改 Windows 安全设置。

```text
Codex → MCP stdio → SwBridge.exe → 官方 COM → SolidWorks
       ├─ sw_api_catalog：接口、签名、属性、ref/out、枚举、帮助链接
       ├─ sw_submit / api_batch：按任务组合官方 API 调用
       └─ sw_api_object：数组分页和对象引用释放
```

Agent 先查官方接口，核对签名和帮助，再通过对象引用串联调用。原有状态、检查、零件副本修改、导出和装配动作继续保留。

Agent 入口集中在 AGENTS.md，MCP 启动信息会给出该文件的实际路径。装配可轻量读取 `constraints`、配合错误码和抑制状态；连接失败会列出运行中的 SW PID 与 COM 注册情况。`read_only` 同时反映 SW 的只读打开状态和文件属性。

## 接入

1. 整包解压到固定、可写目录，打开目标 SolidWorks。
2. 双击 `Install-CodexBridge.cmd`，安装可建模的 MCP 配置；脚本只更新自己管理的服务区块，保留其他设置并备份原配置。它不会启动 SolidWorks。需要只读或指定实例时，在包根目录运行：

   ```powershell
   .\Install-CodexBridge.ps1                                # 只读
   .\Install-CodexBridge.ps1 -AllowWrite -SolidWorksPid 目标进程号
   ```

   重载 Codex 的 MCP 连接。多个 SW 实例并存时指定 PID；移动解压目录后重新运行安装。已经手动配置过同名服务时，安装脚本会提示使用以下生成器更新已有区块：

   ```powershell
   .\New-CodexConfig.ps1 -OutputPath .\solidworks.mcp.toml -AllowWrite -SolidWorksPid 目标进程号
   ```

   生成器仅输出 TOML 区块；将它合并到 Codex MCP 配置并保留其他设置。不加 -AllowWrite 时默认只读，仍可查 API 元数据。MCP 配置格式见 [Codex 官方文档](https://learn.chatgpt.com/docs/extend/mcp?surface=cli)。
3. 让 Agent 读取 [AGENTS.md](AGENTS.md) 和 [通用调用说明](docs/api-automation.md)。仅复制 skill 不会建立 MCP 连接。

可以对使用此包的 Agent 说：

> 读取整合包中的 AGENTS.md。先检查 SolidWorks 会话，再用 sw_api_catalog 查找任务所需的官方接口和枚举，按官方帮助核对单位与上下文，通过 sw_submit 的 api_batch 组合调用，并读回结果。保护原模型，所有建模输出放到本次独立目录。

## 范围

- 随包接口库版本 34.0.0.5070（SW 2026），含 1,030 个接口定义、978 个枚举和 16,686 条方法签名；含接口别名、属性 getter/setter，不等于同等数量的独立功能。
- 支持实例方法、属性、跨调用对象引用、批内结果引用、基本类型及数组、枚举和 flags、ref/out、COM SAFEARRAY、数组分页及引用释放。
- 可通过启动参数 --api-library 本机Interop.dll 扩展元数据；配置生成器对应 -ApiLibrary 路径数组。附加模块仍需相应官方安装、依赖和许可，不能仅靠主 API DLL 获得 Simulation/PDM 等产品功能。
- 元数据存在不等于当前 SW 版本能执行。原生指针、回调、仅进程内 Add-in 方法不属于此 JSON/外部 COM 桥接的完整覆盖范围。检索会标出可识别的限制，其他方法仍需查官方帮助。
- api_batch 直接调用活动 COM 对象，使用官方原始单位（通常 m/rad），不自动复制模型、保存、回滚或限制 API 内部的文件路径。Agent 应按用户授权选择操作。原有专用副本动作的保护行为保持不变。

本机 SW 2025 的实际 COM 调用和协议测试见 [validation.json](validation.json)，不能推广为全部方法、附加模块或其他版本已经通过验收。

## 文件入口

| 入口 | 用途 |
|---|---|
| [AGENTS.md](AGENTS.md) | Agent 调用顺序和边界 |
| [docs/api-automation.md](docs/api-automation.md) | 通用接口格式与完整示例 |
| [docs/agent-usage.md](docs/agent-usage.md) | 原有装配流程和恢复处理 |
| [docs/interface.md](docs/interface.md) | 专用动作限制与协议 |
| [Source/README.md](Source/README.md) | C# 源码、构建和开发测试 |
| [validation.json](validation.json)、manifest.json | 验证范围与文件哈希 |

运行 .\Verify-Package.ps1 校验发布内容。运行状态、模型、报告和生成配置不进入压缩包。桥接本身无需网络、Python、Node 或模型密钥；Python 仅用于可选开发测试。桥接代码采用 [MIT](LICENSE)；官方 Interop 的版权和适用条款见 [第三方文件说明](THIRD_PARTY_NOTICES.md)。
