# 结构与数据流

## 模块

| 模块 | 责任 |
|---|---|
| `MainWindow` / `Views` | WPF 交互、图像缩放与 ROI 绘制、参数输入、聊天显示 |
| `WorkbenchController` | 工作区状态、流程编辑、方案资源、单次与批量运行 |
| `Models` | 方案、步骤、参数、ROI、指标、运行报告及预览像素 |
| `ToolCatalog` | 工具类型和参数范围 |
| `PipelineRunner` | HALCON / OpenCV 顺序执行和结果判定 |
| `ImageBridge` / `HalconRuntime` | 图像内存转换、灰度归一化、HALCON 原生库路径 |
| `Acquisition` | IFrameSource 品牌适配、海康SDK、HALCON/USB/回放、串行生命周期 |
| `Communication` | TCP/串口 JSON 传输与 NModbus TCP/RTU |
| `DeviceRuntime` | 相机拍照检测、PLC握手、请求去重和任务关闭归属 |
| `AppBus` / `InspectionJournal` | CommunityToolkit 窗体消息、检测追溯及图像关联 |
| `AI` | 模型设置、聊天上下文、工具注册和本机 HTTP 接口 |
| `SmokeChecks` | 算法与应用集成检查、界面冒烟检查 |

第一版使用明确的普通类和方法。界面与 AI 共同调用控制器，没有额外的插件系统或工作流框架。

## 检测执行

```text
界面 / VisionTools
    → WorkbenchController.RunAsync
    → 创建方案快照、切换忙碌状态
    → 后台 PipelineRunner.Run
    → 从本次源帧内存快照读取 8-bit 灰度图、顺序执行启用步骤
    → RunReport + PreviewFrame
    → 回到 UI 线程更新指标、状态、预览
```

每次运行从源图像的托管像素快照开始，步骤参数使用运行前快照。滤波和阈值工具替换当前图像；区域分析、尺寸测量和模板匹配读取当前图像并输出指标。步骤发生错误时停止后续执行；正常执行但超出判定条件则记录 NG。只有所有执行步骤成功且通过条件，整条流程才为 OK。

图像核心对象使用 HALCON `HObject`，OpenCV 工具经 `Mat` 转换，界面只保留托管灰度像素和简单叠加框。HALCON、OpenCV 对象在使用完成后释放。

批量处理复用流程快照逐张检测，结果写入 CSV 与日志，不将批量图像替换为当前工程源图。流程参数、ROI 或启停状态修改后，旧报告和叠加失效，预览恢复源图。

取消为协作式：模型网络请求支持取消，视觉流程在步骤之间检查取消；已经进入的原生算子不保证即时中断。取消不撤销已经完成的流程编辑。

## 方案与资源

方案为 JSON，记录格式版本、名称、源图像和工具列表。保存时将源图像与模板复制到同名 `.assets` 目录，并在 JSON 中记录相对路径。打开时恢复为完整路径，按应用目录定义恢复参数范围并检查配置。

本机临时图像和模板位于 `%LOCALAPPDATA%\VisionWorkbench\Work`。AI 设置与 Harness 会话文件位于其上级目录。聊天历史仅在当前进程内存中保留，不写入方案。

## AI 执行

`ChatService` 通过 `Microsoft.Extensions.AI` 调用支持工具的 OpenAI 兼容服务，`FunctionInvokingChatClient` 处理最多 8 轮工具交互。工具注册在 `VisionTools`，执行通过 WPF Dispatcher 回到界面所属线程；工具串行执行，流程运行本身转入后台。

同一组 `VisionTools` 同时服务内置聊天和本机 `HarnessServer`。模型只能调用注册工具，不执行任意 C#、HALCON 脚本或 shell 命令。模型没有任意文件载入、文件保存和模板训练工具。额外的固定设备工具只查询设备状态，或对用户已连接相机启停预览、采图与检测；硬件配置和 PLC 寄存器不交给模型任意修改。

上下文保留工具调用与结果，裁剪时按用户轮次删除，避免留下孤立的工具结果。最近执行报告具有运行编号；参数修改后应重新运行，以实际报告判断结果。

## 算法语义

- 内部为 8-bit 单通道图像；OpenCV 灰度工具用于练习转换接口，输入在读入时已统一灰度。
- 矩形/圆形 ROI 使用原图外接框坐标，默认不启用；圆形宽高相等。模板创建后自动切换为全图搜索，可另设搜索 ROI。
- 区域分析先取当前图像灰度 1 至 255 的区域，再连接、筛选面积并判断数量。通常与阈值分割组合使用。
- 尺寸测量输出亮区域轴对齐外接矩形的像素宽高，不提供标定或亚像素边缘拟合。
- 模板为 ROI 裁剪图，每次运行创建并释放形状模型；匹配姿态由 HALCON 返回，叠加框不随角度旋转，只作位置示意。
- 形状匹配与 NCC 匹配共用模板资产管理；直线和圆测量模型保存 ROI 与卡尺参数，在运行时创建 HALCON Metrology 模型，应用后读取拟合结果并释放模型。
- 直线模型的长度是有效卡尺之间的拟合线段长度，不代表被测物体真实端点间距；圆模型返回拟合圆心、半径和直径。
- 当前预览为最终处理图像和最后执行步骤的叠加，不存储所有步骤的中间图。

## 验证范围

`--check` 检查算法结果、方案保存恢复、工具执行与 HTTP 接口等集成行为；`--ui-smoke` 创建实际 WPF 窗口并输出截图。具体通过项与失败信息以 `artifacts` 下生成的检查报告为准。模型协议可通过本机 stub 验证；真实云模型仍需要有效的服务配置，不把 stub 结果等同于云模型能力。

## 设备执行路径

```text
TCP / 串口 TRIGGER 或 PLC 触发边沿 / 设备窗 / CaptureFrame 工具
    → DeviceRuntime 拍照互斥（同一请求编号共享任务）
    → 停止预览 → CameraSession.SnapAsync
    → IFrameSource（海康SDK / HALCON / USB / 回放 / 模拟）
    → 独立托管灰度帧 → Controller.AcceptCapturedFrame → RunAsync
    → 真实报告 → 通信响应 / PLC寄存器 → InspectionJournal
```

连续预览通过 LiveFrameMessage 经过 AppBus 到主窗，Dispatcher 只显示最新待显示帧；拍照检测期间不让预览覆盖当前源帧。设置窗不持有另一个控制器，关闭设置窗仅退订。主窗关闭先取消设备总生命周期，再等待采集、通信与检测归还资源，最后关闭窗口。

设备配置独立于检测方案；采集图像保存方案时才写入方案资产。追溯默认写入站点 Records 目录，保存本次运行的原图与报告。没有把每个预览帧写到磁盘，也没有让第三方参考仓库的程序参与执行。
