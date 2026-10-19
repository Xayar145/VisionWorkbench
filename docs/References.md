# 复用与代码参考

项目按小型视觉工作站组织，采集、协议和消息分发优先使用成熟 SDK/库；上层相机生命周期、触发握手、方案、界面与追溯负责把它们串起来。

| 来源 | 使用方式 | 许可或边界 |
|---|---|---|
| [OpenVisionLab](https://github.com/Noah8218/OpenVisionLab) | 参考视觉工作台、算子/参数/ROI/流程布局；本项目简化为顺序流程 | Apache-2.0；未复制其应用源码或图片 |
| [vision-pilot](https://github.com/agentthink/vision-pilot/tree/2a409ac0f658e4bbfcdd7496a91b5650ca2f4045) | 已读取 ICamera、CameraAcquisitionService、ModbusInspectionPollingService、InspectionHistoryService，参考其采集/通信/追溯分层 | MIT；参考结构，未复制其手写 Modbus 编解码 |
| [WPFSamples](https://github.com/AFei19911012/WPFSamples) | HALCON/WPF 与通信案例检索参考 | 没有并入源码或二进制 |
| [NModbus](https://github.com/NModbus/NModbus) 3.0.83 / NModbus.Serial | 实际 NuGet 依赖，Modbus TCP/RTU 编解码和主站 I/O | MIT；许可见 `licenses/NModbus.txt` |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) 8.4.2 | 实际 NuGet 依赖，AppBus 通过弱引用 Messenger 分发类型化消息 | MIT；许可见 `licenses/CommunityToolkit.Mvvm.md` |
| [System.IO.Ports](https://learn.microsoft.com/dotnet/api/system.io.ports.serialport) 10.0.0 | 实际 NuGet 依赖，普通串口与 RTU 的串口底层 | .NET 开源组件许可 |
| 海康 MVS SDK | `MvCameraControl.Net.dll` 运行时适配实例 | 商业 SDK，由用户安装，未上传 DLL；未实机验证 |
| [HALCON 官方采集 API](https://www.mvtec.com/doc/halcon/2411/en/open_framegrabber.html) | 复用 Framegrabber 统一采集接口与 HALCON 运算 | 商业软件，由用户安装并授权 |
| OpenCvSharp | 现有图像转换、USB 采集与视频解码 | NuGet 依赖，遵循其自身及传递组件许可 |

参考仓库仅作为源码阅读材料保留在本机忽略的 artifacts 下，未将整个仓库混入应用。公开提交不包含厂商运行库、许可证、设备配置、聊天凭据或外部审阅原始记录。
