# 通用官方 API 调用

先用 sw_api_catalog 查官方接口和枚举，用官方帮助核对语义，再用 sw_submit 的 api_batch 执行。全部格式以 Bridge/schemas/bridge.schema.json 为准。

## 检索

```json
{"operation":"summary"}
{"operation":"search","query":"FeatureExtrusion","type_name":"IFeatureManager"}
{"operation":"describe","type_name":"ISldWorks","member":"OpenDoc6"}
{"operation":"describe","type_name":"swDocumentTypes_e"}
{"operation":"types","query":"GearMate","offset":0,"limit":40}
```

上述为 sw_api_catalog 参数。limit 最大 100，offset 分页；方法返回参数类型、ref/out/optional、限制和官方帮助链接。接口/方法存在不等于当前 SW 版本、许可或上下文支持。元数据不是完整教程；DLL 内没有所有参数的文字含义和单位，须查实际版本的官方帮助或本机 CHM。

## 调用示例

先 sw_status 取得新鲜 session、全部打开文档的 ID/revision、active_document_id。多实例用 --pid 指定。下面示例只使用 MathUtility，不修改模型；替换占位符和路径。若有打开文档，必须把它们全部写入 expected_documents。

```json
{
  "schema_version":"1.0",
  "request_id":"替换为新UUID",
  "session_id":"替换为本次UUID",
  "expected_documents":[],
  "execution":"validate",
  "output_dir":"F:\\实际目录\\Bridge\\models",
  "action":{
    "type":"api_batch",
    "expected_active_document_id":null,
    "steps":[
      {"id":"math","target":"application","interface":"ISldWorks","member":"GetMathUtility","arguments":[]},
      {"id":"point","target":{"$ref":"math"},"interface":"IMathUtility","member":"CreatePoint","arguments":[{"$array":"double","items":[0.01,0.02,0.03]}]},
      {"id":"coordinates","target":{"$ref":"point"},"interface":"IMathPoint","member":"ArrayData","operation":"get","arguments":[]}
    ]
  }
}
```

validate 成功后改 execute，通过 sw_job 查询终态和 api_results。validate 不执行方法，不能预知后续对象或几何条件。执行前先核对全部步骤的签名、字面值及引用顺序，再逐步调用。

## 参数和对象

| 格式 | 用途 |
|---|---|
| target: application | 已附加的 ISldWorks 根对象 |
| {"$document":"doc-…"} | 本次状态返回的文档对象 |
| {"$ref":"id"} | 同一批次前一步的原始 return_value |
| {"$ref":"id","path":["return_value",0]} | 返回数组的第一个对象 |
| {"$ref":"id","path":["out_parameters","Errors"]} | 前一步的 ref/out 返回字段 |
| {"$object":"api-…"} | 之前执行返回的对象 ID |
| {"$enum":"swDocumentTypes_e.swDocPART"} | 官方枚举值 |
| {"$enum":["Enum.A","Enum.B"]} | 同一枚举的 flags 按位 OR |
| {"$array":"double","items":[1,2,3]} | 强类型数组 |
| {"$array":"dispatch","items":[{"$ref":"a"},{"$ref":"b"}]} | 需要 VT_DISPATCH 时的 COM 对象数组 |
| {"$missing":true} | 仅用于确实标记 optional 的参数 |

operation 默认 call；get/set 使用属性名，不加 get_/set_。索引属性把索引放在 arguments，set 的值放最后。也可用 call 和实际 get_/set_ 方法名。

所有参数都要提供：out 填 null，ref 填初始值；out_parameters 返回更新值。重载用 parameter_types 指定完整 .NET 类型数组，复制 catalog 结果，包括 System.Int32& 等标记。数组还支持 int/long/short/byte/bool/string/object。

对象引用仅在当前桥接 session 有效，并记录打开文档 revision。文档变化后重新取得对象，同批次用 $ref 跨建模步骤传递。重启不恢复对象引用。最多保留 4096 个引用，action 可加 release_objects:["api-…"]。

sw_api_object 参数如下；operation=release 释放，operation=read 加 offset/limit 分页读取大数组。超过 2048 项的返回数组用 object_id 表示；步骤可加 return_mode:"reference" 强制把较小数组也返回为引用。

```json
{"session_id":"本次UUID","object_id":"api-…","operation":"read","offset":0,"limit":100}
```

释放只删除桥接引用，不关闭用户文档，不强制 ReleaseComObject。

## 运动模块与上下文

`capabilities.native_motion_study=false` 表示没有专用运动算例动作；通用调用仍可通过 `--api-library` 加载本机 `SolidWorks.Interop.swmotionstudy.dll` 后访问官方运动接口。用 catalog 查询实际类型与签名，按本机安装和许可验证可用性。

运动算例与模型状态分开验收。`IMotionStudy.SetTime` 设置时间条，不据此推断组件 `Transform2` 已代表该动画帧；动画结果对象是否适用于该算例类型也需验证。计算模型几何时先切回实际模型页：本机中文界面已验证 `IMotionStudyManager.ActivateMotionStudy("模型")`，其他语言按实际名称确认。

只读装配检查可使用 `scopes:["summary","constraints","mates","checks"]`。`details.component_constraints` 返回固定状态、原生约束状态、抑制状态和模型变换；最多 100 个组件，超出范围会有未检查提示。`mates` 的 `error_code/is_warning/suppressed` 是当前配合读回，不等于运动或干涉验收。完整验收要求统一见 [AGENTS.md](../AGENTS.md)。

## 执行边界

任意 API 执行需要 --allow-write，不能仅凭方法名或 getter 保证无副作用。元数据检索不需要 SW 或写入模式。

api_batch 直接操作当前 COM 对象，使用官方原始单位（通常 m/rad）。复制、另存、重建、导出和恢复文档由调用方显式安排；output_dir 不约束官方 API 参数内的路径，不自动回滚。每批 1–128 步，输入数组最多 16384 项，嵌套最多 16 层，请求最多 1 MiB。

结果 changes.model_state=unknown、source_preserved=null；api_call_completed=true 只说明调用返回，cad_result_verified=false 仍需 Agent 检查任务效果、官方返回码、几何/特征、配合和文件。失败可能已经执行前几步，先查 completed_steps、api_results、error 和实际模型。同一 request_id 的相同输入关联同一个作业，不重复执行；改变输入使用新 UUID。取消只在步骤边界生效。

原生指针、回调和仅进程内方法需要额外实现。桥接拒绝可识别的指针/回调/原始缓冲区签名；transport_supported=true 只是可尝试传输，不能保证方法可用。启动参数 --api-library 可加载明确指定的其他官方 Interop，配置生成器对应 -ApiLibrary；附加模块仍需完整依赖和许可。

官方参考：[对象访问](https://help.solidworks.com/2026/english/api/sldworksapiprogguide/Overview/Accessing_Objects.htm)、[进程内方法](https://help.solidworks.com/2026/english/api/sldworksapiprogguide/Overview/In-process_Methods.htm)。
