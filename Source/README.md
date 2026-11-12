# 源码与维护

0.4.0：ApiCatalog.cs 从官方 Interop 反射接口/枚举及调用签名；ApiAutomation.cs 实现 API 批处理、参数和对象传输。check_api.py / bridge_rpc.py 使用真实协议和实际 SW COM 验证通用层，不使用模拟 CAD。

```powershell
python .\Source\tests\check_api.py --package 实际Bridge目录 --pid 目标进程号 --report 检查报告.json
```

不加 --pid 时只检查元数据、协议和只读拒绝；加 --pid 会通过写入桥接测试 MathUtility、属性和失败的文件打开（ref/out），不创建模型几何。

`tests/check_handoff.py` 接受 `--package`、`--pid`、`--assembly`（可动装配）、`--locked-assembly`（被角度锁定的回归装配）和 `--report`，以只读方式打开现有 CAD 文件，验证约束、配合抑制和实际只读状态。它不保存模型、不退出 SolidWorks；测试应使用独立实例。

`tests/check_setup.py` 使用 Python 3.11+，在临时目录里通过 Windows PowerShell 测试安装、重复安装、手动配置冲突、损坏区块、预览安装和 Git 检出校验，不改动当前用户配置：

```powershell
python .\Source\tests\check_setup.py --package '整包根目录' --report 'C:\开发目录\setup.json'
```

| 位置 | 职责 |
|---|---|
| `bridge/src/Program.cs` | MCP 握手、六工具及 JSON-RPC 路由 |
| `bridge/src/Core.cs`、`Jobs.cs` | 路径、序列化、STA 队列、前置条件、作业持久化与幂等 |
| `bridge/src/CadAdapter.cs` | 附加已打开的 SW、文档/几何读写、导出和窗口恢复 |
| `bridge/src/AssemblyAdjustment.cs` | 已有零件副本插入、配合与读回 |
| `bridge/src/AssemblyInspection.cs` | 不遍历面的轻量装配约束读回 |
| `bridge/src/AssemblyDemo.cs` | 仅限空装配体的演示动作 |
| `bridge/src/WindowSnapshot.cs` | 指定 SW 窗口的 PNG 捕获和 DPI 处理 |
| `bridge/schema/bridge.schema.json` | 唯一编辑的协议源；构建时复制为运行时 `Bridge/schemas/bridge.schema.json` |
| `bridge/SwBridge.exe.config` | .NET 运行时配置源；随构建复制 |
| `discovery/SwApiProbe.cs` | 离线读取本机安装、官方 Interop 元数据、帮助及 Toolbox 位置 |
| `tests/check_mcp.py` | 可选的只读连接自检；Python 仅是开发测试依赖 |
| `provenance.json` | 来源和当前发布二进制 SHA-256 |

在包根目录编译两个原生程序到全新目录，不覆盖正在使用的包：

```powershell
.\Source\Build-All.ps1 -Destination 'C:\开发目录\sw-build'
```

默认使用随包官方 Interop。需使用指定本机版本时增加 `-InteropDirectory '实际安装目录\api\redist'`。编译使用 Windows 自带 .NET Framework C# 编译器；不下载依赖。只重新生成二进制、运行配置和 Schema，不把历史验证结论自动赋给新构建。

可选只读开发测试：

```powershell
python .\Source\tests\check_mcp.py --package 'C:\开发目录\sw-build\Bridge' --report 'C:\开发目录\connection.json' --state-root 'C:\开发目录\test-state' --pid 目标进程号
```

源码与实际发布程序版本分别标注。当前整合版和桥接版本为 0.4.0；原有专用 CAD 动作保持原逻辑，新增通用 API 组合调用。不要凭编译通过认定写入通过；新的构建需独立验收。

发布时更新 `validation.json` 与来源哈希，执行 `.\Tools\Update-Manifest.ps1`、`.\Verify-Package.ps1`，再运行 `.\Tools\Export-Package.ps1 -ArchivePath 'C:\交付目录\sw-codex-integration-v0.4.0.zip'`。导出只包含清单文件，自动排除运行状态、模型、日志及生成的 MCP 配置；拒绝覆盖已有压缩包。二进制重建后必须重新记录哈希与验证结果，不能沿用旧结论。
