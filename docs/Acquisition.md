# 相机采集与实时预览

从主窗口“设备 → 设备与通信设置”进入。设备配置存于 `%LOCALAPPDATA%\VisionWorkbench\devices.json`，与算法方案分开保存；程序启动不会自动连接相机或 PLC。

## 已有采集源

| 驱动类型 | 设备栏 | 实现与当前验证 |
|---|---|---|
| 模拟相机 | 留空 | 生成移动的三个几何零件，连续采集与完整检测链路已验证 |
| 海康 MVS SDK | 枚举得到的序列号；留空取首台 | 对接 `MvCamCtrl.NET.MyCamera` 旧版兼容 API；代码已接入，无 SDK/物理相机，待实机验证 |
| HALCON 通用工业相机 | 接口枚举得到的设备标识 | `open_framegrabber` / `grab_image` / `close_framegrabber`；File 接口实际验证，GigE/GenICam 待设备 |
| USB / OpenCV | 非负序号，例如 `0` | `VideoCapture` DirectShow；已编译，无 USB 实机验证 |
| 文件回放 | 图像、图像目录或视频路径 | 目录按文件名排序、到尾循环；PNG 和 AVI 实际读取验证 |

HALCON 接口可填写 `GigEVision2`、`GenICamTL`、`DirectShow` 或 `File`。接口 DLL、驱动、GenTL Producer 和许可由本机安装提供。此开发机仅安装 HALCON File 采集接口，不能据此声称工业相机已联调。

## 海康实例

1. 从[海康机器人官方中心](https://www.hikrobotics.com/en/machinevision/service/download/?module=0)安装 x64 MVS 与对应驱动，先用 MVS 客户端确认设备能采图。
2. 选择“海康机器人 MVS SDK”，填写 SDK 的 `MvCameraControl.Net.dll` 完整路径，点击“枚举相机”，选择目标序列号。
3. 连接、单次拍照，再开始实时预览。曝光单位为微秒；空白表示不修改设备默认值。
4. 载入或搭建检测流程后点击“拍照检测”。这会停止预览、重新采图、运行检测并保留结果。

本适配器使用 `MvCamCtrl.NET.MyCamera`，通过运行时加载避免公开仓库依赖厂商 DLL。新 SDK 的 `MvCameraControl` API 与该类型不同，不能任意替换 DLL；可使用 MVS 提供的兼容 SDK，或增加独立适配器。

实际调用为设备枚举、Create/Open、TriggerMode=On、TriggerSource=Software、设置曝光/增益、StartGrabbing；每帧发送 `TriggerSoftware` 后调用 `GetImageForBGR`，转换并复制为应用自己的灰度字节数组。释放依次 Stop/Close/Destroy。当前实例采用软件触发，未实现相机外触发线的配置。

SDK 原生运行库需可被 Windows 正常加载。不会下载厂商 DLL、切换为模拟图来掩盖连接失败，仓库不附带 SDK、驱动或许可证。取帧超时为 1500ms，海康连接和真实型号仍需现场确认。

## 生命周期与扩展

`IFrameSource` 只定义 Open、Grab、ApplyParameters、Dispose。`FrameSources.Register("品牌标识", () => new Adapter())` 可注册其他品牌，不必改控制器、通信或算法层；设备驱动栏可填写注册标识。当前是一台活动相机，不是多相机同步采集系统。

`CameraSession` 统一串行设备访问，后台连续取帧；主窗口通过 AppBus 接收帧并合并为最新一帧显示。停止预览后才能单拍；设备运行服务会代为停止预览。预览只采图，不循环执行检测；每次触发检测会重新采图。F5 或 AI 运行当前流程时停止预览，保留当前图像上的结果。

灰度像素在进入应用时复制，算法直接读取内存快照。只有保存方案、训练模板或启用追溯存图时才落盘。单帧接口返回的缓冲区在后续采集时不得重用或改写。

HALCON 与海康的原生采集需等待调用返回或超时，取消不等于立即中断原生函数。OpenCV DirectShow 的 Read 退出时长取决于驱动，尚未验证 USB 拔线恢复。设备断线后释放句柄、停止预览并显示错误；重新连接由用户显式操作。

## 数据总线

`Infrastructure/AppBus` 封装 [CommunityToolkit WeakReferenceMessenger](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/messenger)。同一实例在主窗和设备窗共享；`LiveFrameMessage` 传图像，`DeviceStatusMessage` 传设备消息，`InspectionCompletedMessage` 传追溯结果。Subscribe 返回可释放订阅，窗口关闭时退订；消息分发在发布线程，WPF 控件更新回到 Dispatcher。

## 追溯

设备页可设置记录目录、NG 存图或全部存图。默认记录在 `%LOCALAPPDATA%\VisionWorkbench\Records`，JSONL 保留运行报告、触发来源、请求编号与图像关联。记录窗口显示最近 200 条，支持详细指标、CSV 导出和打开已保存图像；打开记录图像会停止实时预览。

图像按日期保存，当前不自动清理历史数据。保存失败会记录明确错误，不会伪造存档成功，也不会修改已生成的算法判定。批量检测继续使用自己的 CSV 导出，不写入站点逐次触发记录。
