# Harness 工具接口

应用启动时创建仅绑定 `127.0.0.1` 的 HTTP 服务，由系统分配端口。它是简单本机 HTTP API，不是 MCP Server。聊天页的“Harness 本机工具接口”显示地址和会话文件路径。

## 会话与认证

每次进程启动生成新的随机 Bearer token，并写入：

```text
%LOCALAPPDATA%\VisionWorkbench\harness-<进程号>.json
```

文件包含 `baseUrl`、`token` 和 `processId`。所有请求必须包含 `Authorization: Bearer <token>`。正常关闭删除会话文件；异常退出可能留下旧文件，旧地址不代表服务仍可用。文件只用于本机同一用户的集成，不要提交仓库或粘贴到对话中。

## HTTP 端点

| 方法 | 路径 | 内容 |
|---|---|---|
| GET | `/tools` | 工具名称、说明与 JSON 参数 schema |
| GET | `/state` | 当前工作区状态 |
| POST | `/tools/call` | 按名称执行注册工具，返回 JSON 结果 |

调用请求体：

```json
{
  "name": "SetToolParameter",
  "arguments": {
    "stepId": "实际步骤ID",
    "key": "min",
    "value": 120
  }
}
```

请求体上限 32 KB，读取正文超时 10 秒。认证错误返回 401，未知端点返回 404，过大正文返回 413；工具或参数错误返回 400，取消或读取超时返回 408。视觉执行以串行工具调用完成，不提供后台 job 轮询。

## 已开放工具

| 工具 | 参数 | 行为 |
|---|---|---|
| `ListAvailableTools` | 无 | 返回工具类型和默认参数范围 |
| `GetWorkspaceState` | 无 | 返回图像尺寸、步骤、参数、ROI、忙碌状态和最近运行编号 |
| `AddVisionTool` | `kind` | 添加目录中已有工具 |
| `SetToolParameter` | `stepId`, `key`, `value` | 修改数值参数并检查范围 |
| `SetRoi` | `stepId`, `x`, `y`, `width`, `height`, `enabled`, `shape` | 设置图像内的矩形或圆形 ROI；`shape` 为 `rectangle` 或 `circle`，圆形宽高相等 |
| `RunPipeline` | 可选 `throughStepId` | 等待整条流程或指定终点执行完成 |
| `GetRunResults` | 无 | 返回最近一次完成报告 |
| `GetDeviceStatus` | 无 | 读取相机、预览、TCP、串口、Modbus 实际状态 |
| `CaptureFrame` | 可选 `inspect` | 从用户已连接的相机采集新图；可同时检测，停止实时预览 |
| `SetCameraPreview` | `enabled` | 启停已连接相机的预览 |

工具参数名大小写按 schema 使用。运行期间拒绝流程修改；参数变化会使旧结果失效。接口不提供任意代码执行、任意文件访问、图像载入、模板训练或云模型密钥读取。

## PowerShell 示例

先启动应用并在界面点击“示例”。将界面显示的实际会话文件路径赋给变量。以下代码不输出会话 token，也不把它写进命令行参数：

```powershell
$sessionPath = Join-Path $env:LOCALAPPDATA 'VisionWorkbench\harness-实际进程号.json'
$session = Get-Content -LiteralPath $sessionPath -Raw | ConvertFrom-Json
$headers = @{ Authorization = 'Bearer ' + $session.token }
$baseUrl = $session.baseUrl

Invoke-RestMethod -Uri ($baseUrl + 'tools') -Headers $headers
$state = Invoke-RestMethod -Uri ($baseUrl + 'state') -Headers $headers
$stepId = ($state.Steps | Where-Object Kind -eq 'halcon.threshold' | Select-Object -First 1).Id

$body = @{ name = 'SetToolParameter'; arguments = @{ stepId = $stepId; key = 'min'; value = 120 } } | ConvertTo-Json -Depth 5
Invoke-RestMethod -Uri ($baseUrl + 'tools/call') -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body $body

$body = @{ name = 'RunPipeline'; arguments = @{} } | ConvertTo-Json
$report = Invoke-RestMethod -Uri ($baseUrl + 'tools/call') -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body $body
$report | Select-Object RunId, Success, Accepted, ElapsedMs

Remove-Variable session, headers
```

工作区和结果不自动携带图像字节与源文件路径，但项目名、参数、指标和错误消息属于实际数据。连接外部模型时，这些工具定义与结果可能被发送至所配置的模型服务。

设备连接、SDK路径、PLC地址和寄存器写入由用户在设备页面配置。AI不拥有连接或任意寄存器写入工具；相机未连接时采图明确失败，不复用旧图冒充新拍照。
