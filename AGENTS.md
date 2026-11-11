# SolidWorks 操作要点

0.4.0 通用 API：先用 sw_api_catalog 查接口、准确参数、枚举和官方帮助，按 [docs/api-automation.md](docs/api-automation.md) 用 sw_submit 的 api_batch 组合调用。绑定全部打开文档的 revision，使用原始官方单位（通常 m/rad）。通用调用直接操作活动对象，不自动复制、保存或回滚；按用户授权显式安排副本、重建、另存和读回。对象引用可跨调用，大数组通过 sw_api_object 分页，使用后释放引用。元数据存在和调用返回均不等于 CAD 任务已经成功。原有专用装配动作按下列限制操作。

先核对 `package.json` 标识与 `validation.json`。模型链路仅为 MCP stdio → 本地桥接 → 官方 COM；离线查询使用 `Discovery/sw-api-probe.exe`。不修改另一会话的包或全局配置。

1. **先复用，再建模。** 优先现有零件和标准件。按检测到的 Toolbox 库位置检索，核对保存配置、实际尺寸及方向。PreviewCfg 母版不等于已选规格；Toolbox 自动生成尺寸配置尚未封装。
2. **明确目标。** 先 `sw_status`、再 `sw_inspect`，绑定本次 session、document、revision、组件和面引用。已有装配使用 `insert_component_with_mates`；`assemble_demo` 仅限空装配体。不靠窗口标题猜目标。
3. **保护原件。** 默认只读，获用户授权才启用写入。插入使用独立副本、独立文件名，避免改库、覆盖原件及打开重名冲突。当前支持固定顶层基准件、局部 Z 向孔/承靠面及符合要求的源零件，限制详见接口说明。
4. **切回装配体。** 临时打开零件后，无论成功或失败都恢复目标装配体并核对活动文档；恢复失败用 `activate_document`。独立零件窗口不是装配结果。
5. **读回再报完成。** 检查原组件位置/固定状态、新组件数、两类配合、特征错误及文件。报告实测轴/孔尺寸和间隙，不把同轴配合当成紧配合、干涉或运动仿真完成。
6. **PNG 辅助核对。** 需要直观回读时，给 `sw_inspect` 增加 `snapshot` scope，查看返回 PNG，核对窗口标题、组件树及模型。截图应对应目标文档；保留组件与配合的数值检查。窗口最小化、目标不活跃或图片内容异常时明确报告。
7. **失败先查现状。** 查作业及实际组件后再处理，不盲目重复插入；部分失败不保证回滚。

接入与完整调用顺序见 `docs/agent-usage.md`。精确格式与单位见 `docs/interface.md` 和 `Bridge/schemas/bridge.schema.json`。对外长度/角度用 mm/deg，原始表面参数与变换按 SI 标注。`busy_scope=bridge_queue_only` 只表示本桥接队列。
