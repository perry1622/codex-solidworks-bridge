# 专用装配动作

通用入口和验收约定统一在 [AGENTS.md](../AGENTS.md)；任意官方 API 组合见 [api-automation.md](api-automation.md)。这里仅说明已有的专用插入流程。

## 插入已有零件

`insert_component_with_mates` 使用固定顶层基准件的局部 Z 向孔和上口承靠平面，源件须满足唯一居中 Z 向圆柱和底端平面的条件。其他方向或结构使用通用 API。`assemble_demo` 仅限空装配体。

先用 `sw_inspect` 的 `geometry` 取得当前基准组件的 `component_id`，以及 `faces[].reference_id` 中的孔圆柱面和承靠平面。以下为 `sw_submit` 的 arguments，替换占位符：

```json
{
  "schema_version":"1.0",
  "request_id":"新UUID",
  "session_id":"本次session",
  "expected_documents":[{"document_id":"目标装配ID","revision":"最新revision"}],
  "execution":"validate",
  "output_dir":"C:\\本次输出目录",
  "action":{
    "type":"insert_component_with_mates",
    "document_id":"目标装配ID",
    "base_component_id":"当前固定基准组件ID",
    "hole_reference_id":"当前孔圆柱面ID",
    "seat_reference_id":"当前承靠平面ID",
    "source_part_path":"C:\\已核规格的零件.SLDPRT",
    "source_configuration":"实际保存配置名"
  }
}
```

validate 只检查计划与前置条件；部分几何条件需执行时打开副本后检查。通过后保持目标状态新鲜，改 `execution=execute` 提交。保留返回的 job_id，用 `sw_job` 查询终态及检查结果。

此动作使用唯一零件副本，修改当前装配后另存新文件。新装配仍可能引用原有组件，不能据此宣称已独立打包。成功或失败后都会尝试恢复目标装配；恢复状态见作业结果，需要时通过 `sw_submit/activate_document` 激活目标文档。

同一 request_id 仅可重放相同输入；改输入用新 UUID。超时先查既有作业与实际组件，避免重复插入。取消在步骤边界生效，部分执行不保证回滚。桥接重启后重新取得 session 和对象引用，既有作业记录依赖原 state-root。

专用动作的长度/角度使用 mm/deg；原始变换与表面参数使用 SI。完整字段和几何限制见 [interface.md](interface.md)。
