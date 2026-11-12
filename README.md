# 视觉检测系统 (Vision Inspection System)

基于 [YoloDotNet](https://github.com/Nick-DiDonato/YoloDotNet) 的产线 **SOP 作业合规检测**系统，配置驱动、开箱可换配方。

## 项目简介

系统以 YOLO 系列模型（检测 / 姿态）+ MediaPipe 手部关键点为核心推理引擎，按 YAML 配方描述作业步骤，由状态机判定每一步是否按序、按时、按要求完成，违规即报警（界面横幅 + ng.wav + Modbus 输出）。UI 基于 WPF + MVVM，支持多相机、配方热切换、黄金数据录制与运行看板。

> 本项目只做 SOP 检测；缺陷检测 / 级联检测等模块已移除（历史版本遗留的空壳）。

## 核心架构

```
VisionInspectionSystem/
├── src/
│   ├── Core/                       # 核心类库（接口、模型、基础服务）
│   │   └── VisionInspection.Core/
│   │       ├── Interfaces/         # IDetectionModule 等模块契约
│   │       ├── Models/             # ROI / CameraConfiguration / ModuleResult / User
│   │       ├── Services/           # 相机、ROI、模型、用户管理
│   │       └── ViewModels/         # MVVM 基础（ViewModelBase、ROI 编辑器）
│   ├── Modules/
│   │   ├── VisionInspection.Modules.SOP/       # SOP 合规检测（状态机 + 条件/违规判定）
│   │   └── VisionInspection.Modules.Detection/ # 推理层（唯一依赖 YoloDotNet 的工程）
│   └── UI/
│       └── VisionInspection.UI/    # WPF 主界面（MVVM）
├── configs/                        # 配置（唯一来源）
│   ├── sop_config.json             # SOP 模块配置
│   └── sop/                        # 配方（产品/工位 SOP）+ regions/ 区域坐标
├── models/                         # ONNX 模型（不纳入仓库）
├── logs/                           # 运行日志
└── rk3588/                         # RK3588（ARM64 Linux + NPU）移植实现
```

## 功能特性

- ✅ **配方驱动**：`configs/sop/*.yaml` 描述步骤、条件、超时与违规规则；界面切配方即生效，无需改代码（上次使用的配方记在 `configs/last_recipe.json`）。
- ✅ **SOP 合规检测**：`SOPYamlConfig` → `SOPWorkflow` → `SOPStateMachine`；12 类步骤条件、8 类违规（跳步 / 错序 / 超时 / 禁物 / 漏放 / 越界等）。
- ✅ **手部动作判定**：MediaPipe 两阶段（palm 192 → landmark 224）或 YOLOv8-pose + landmark 两种后端，支持左右手、握持、进入/离开区域、稳定 N 帧等。
- ✅ **区域与 ROI**：`ROIManager` / `RegionConfigLoader` 管理矩形、圆形 ROI 与配方区域；坐标按实际画面尺寸缩放。
- ✅ **相机管理**：`CameraManager` 支持 Web / ONVIF / 海康 / USB 多路接入与热插拔。
- ✅ **推理层隔离**：`Modules.Detection` 是唯一依赖 YoloDotNet 的工程，SOP 引擎与 UI 只使用 `DetectedObject`；换推理后端只需改该工程。
- ✅ **可追溯**：黄金数据集旁路录制（帧 + 判定快照）、运行记录（每周期 jsonl + 每步 csv）、数据看板、可选 Modbus 联锁。
- ✅ **MVVM 架构**：`ViewModelBase` 等基础类型实现 UI 与业务逻辑解耦，便于维护与测试。
- ✅ **用户与权限**：`UserManager` / `User` 提供基础用户管理。

## 快速开始

> 需要 .NET 8 SDK（WPF 只能在 Windows 上运行）。**不再需要** YoloDotNet 的源码仓库：推理库走 NuGet。

```bat
:: 还原 + 编译（默认 CPU 版，任何 Windows 机器可直接跑）
dotnet restore
dotnet build

:: 运行
dotnet run --project src/UI/VisionInspection.UI
```

### CPU / CUDA 二选一

YoloDotNet v4 的执行提供程序是独立包，**一个工程只能引用一个**。仓库根 `Directory.Build.props` 用
`YoloEp` 属性切换，默认 CPU：

```bat
dotnet build                          :: CPU（默认，开箱即用）
dotnet build -p:YoloEp=Cuda           :: GPU，需自行安装 CUDA Toolkit 12.x + cuDNN 9.x
```

CPU 构建下界面的「GPU/CPU」开关不会生效（日志会说明），检测按 CPU 运行。

### 目录约定（全部可在运行时解析，无需改代码）

| 位置 | 用途 | 说明 |
|---|---|---|
| `configs/` | 系统/SOP/相机/通信配置 | 开发期自动向上找到仓库根；发布时随 exe 输出到同目录 |
| `configs/sop_config.json` | SOP 模块配置（模型、阈值、手部后端、GPU） | 首次运行即存在；改这里，不要再往代码里写死 |
| `models/`、`yolo_models/` | ONNX 模型 | 不放也能编译；缺模型时对应功能降级并打日志 |
| `logs/` | 运行日志 | 程序自动创建 |
| `configs/last_recipe.json` | 记住上次使用的配方 | 运行时生成，不进版本库 |

- 环境变量 `VISION_HOME`：把程序根目录指到别处（部署到非程序目录时用）。
- 环境变量 `VISION_FFMPEG_DIR`：指定 ffmpeg 所在目录（视频推理/抽帧用），否则查找程序目录与 PATH。

## 配置说明

- `configs/sop_config.json`：SOP 模块配置（模型路径、阈值、手部后端、GPU）。
- `configs/sop/*.yaml`：SOP 配方；界面「SOP → 产品配方」下拉框选择，选择结果记在 `configs/last_recipe.json`。
- 模型文件不纳入版本库（详见 `.gitignore`），需自行放置：

| 用途 | 位置 | 说明 |
|---|---|---|
| 物体检测 | `yolo_models/*.onnx` | 配方 `model.path` 指向它（如 `yolo_models/yolov8s.onnx`） |
| MediaPipe 手掌 | `models/palm_detection_full_Nx3x192x192_post.onnx` | 手部检测第一阶段 |
| MediaPipe 关键点 | `models/hand_landmark_sparse_Nx3x224x224.onnx` | 第二阶段 21 点 |
| YOLO-pose（可选） | `models/yolov8s-pose.onnx` | `HandDetectionBackend=YoloPose` 时用 |

> 配方写 `yolo_models/x.onnx` 而文件放在 `models/x.onnx` 也能找到（按文件名兜底）。

### 排障第一步：看 `logs/startup.log`

程序启动会写一行环境自检：

```
Root=... | configs=<实际使用的配置目录> | sop=<配方目录> | recipes=<找到几个> | models=[...] | VISION_HOME=-
```

- `recipes=0` → 界面「产品配方」会是空的（以前会回退显示"检测车辆/车牌识别"示例，已删除）
- `models=[无 models/ 目录]` → 没有任何模型，检测必然 0 目标
- `configs` 指向 `bin\...\configs`（旧构建残留）→ 删掉 `bin`、`obj` 重新构建，或设 `VISION_HOME` 指向仓库根

## 模型文件放置说明

### 1. MediaPipe 手部模型（SOP 手部检测必需）

SOP 手部姿态识别使用 MediaPipe 两阶段 ONNX 模型，共 **2 个文件**，放在**项目根目录 `models/`**：

```
models/
├── palm_detection_full_Nx3x192x192_post.onnx   # 第一阶段：手掌检测（192×192）
└── hand_landmark_sparse_Nx3x224x224.onnx       # 第二阶段：21 关键点（224×224）
```

- 构建时由 `VisionInspection.UI.csproj` 自动复制到输出目录 `bin\Debug\net8.0-windows\models\`，**clean 重建后会自动恢复，无需手动放置**。
- 若这两个文件缺失，SOP 手部检测不会初始化（日志出现 `Backend=MediaPipe 但模型文件缺失，手部检测未初始化`），手部动作类步骤（hand_in_region 等）将无法判定。

### 2. YOLO 检测模型（SOP / 实时检测）

YOLO 系列模型（检测 / 分割 / 姿态 / OBB）统一放在**项目根目录 `yolo_models/`**：

```
yolo_models/
├── yolo26n.onnx          # 示例：SOP 目标检测
├── yolov8s.onnx
└── ...
```

- SOP 配方（YAML）中的 `model.path` 建议写相对路径，如 `yolo_models/yolo26n.onnx`，运行时按"当前目录 → 程序集目录 → 向上逐层查找 `yolo_models/`"解析，换机器无需修改。
- 配置窗口 / 生成向导的"模型"下拉会自动扫描 `yolo_models/` 下的 `.onnx` 文件。

### 3. 注意事项

- 手部模型**不要**只放在输出目录 `bin\...\models\`——clean 重建会清除该目录；请放项目根 `models\`，由 csproj 自动复制。
- 手动放入的 YOLO 模型文件若不在 `yolo_models/` 下，配置下拉中不会出现；运行时也找不到。
- 模型文件体积较大，默认被 `.gitignore` 排除，不纳入版本库，请在各部署环境单独放置。

## CUDA GPU 加速（可选）

系统通过 YoloDotNet 的 CUDA 执行提供器调用 ONNX Runtime GPU（`Microsoft.ML.OnnxRuntime.Gpu 1.23.2`）进行推理加速；**未安装 CUDA 时自动回退 CPU，功能不受影响**，仅推理速度较慢。

### 推荐版本

| 组件 | 版本 | 说明 |
| --- | --- | --- |
| NVIDIA 显卡驱动 | ≥ 525.60 | 支持 CUDA 12.x 即可 |
| CUDA Toolkit | **12.x**（推荐 12.4+） | 与 ONNX Runtime 1.23 GPU 构建匹配 |
| cuDNN | **9.x**（推荐 9.1+） | ONNX Runtime 1.23 需要 cuDNN 9（不再支持 cuDNN 8） |

> 注意：本项目 ONNX Runtime GPU 为 **1.23.2**，对应 **CUDA 12.x + cuDNN 9.x**。请勿安装 CUDA 11.x 或 cuDNN 8.x（运行时加载 GPU 会话会失败并回退 CPU）。

### 安装步骤

1. **安装 CUDA Toolkit 12.x**
   - 下载：[NVIDIA CUDA Toolkit](https://developer.nvidia.com/cuda-downloads)，选 12.4 或 12.6 版本
   - 默认安装即可（自动写入 `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.x`）

2. **安装 cuDNN 9.x**
   - 下载：[NVIDIA cuDNN](https://developer.nvidia.com/cudnn)（需注册 NVIDIA 账号），选与 CUDA 12 对应的 cuDNN 9.x
   - 解压后把 `bin / include / lib` 三个目录下的文件**复制到 CUDA 安装目录同名文件夹**，或把 cuDNN 的 `bin` 目录加入系统 `PATH`

3. **验证**
   - 打开系统命令提示符执行 `nvidia-smi`，确认驱动支持 CUDA 12.x
   - 运行程序后，主界面工具栏 ⚡ 按钮显示 **GPU** 即为 GPU 推理生效（显示 CPU 表示回退到 CPU，点按钮可切换）

### 故障排查

- 状态栏/日志出现 CUDA 不可用提示时，程序已自动回退 CPU（`GpuSwitchFailed` 事件触发界面告警），按上述步骤检查驱动、CUDA、cuDNN 版本是否匹配
- 多个 CUDA 版本共存时，确保系统 `PATH` 与 `CUDA_PATH` 环境变量指向所需版本
- 若 GPU 显存不足（OOM），可关闭部分相机的 GPU 推理或改用 CPU

## 最近更新

### 2026-10 SOP 数据化、相机稳定性加固与 RK3588 移植

> 本批更新聚焦三大方向：**作业数据可追溯**、**相机长期运行稳定性**、**边缘端（RK3588）移植**。

#### SOP 数据记录与可视化看板
- 新增 SOP 检测数据记录：按天记录每个作业周期的 OK/NG 与各步骤耗时，结构化落盘。
- 新增 SOP 检测数据看板：按用户名 / 产品配方 / 日期维度统计违规与耗时，辅助产线合规分析。
- 违规即时反馈：检测违规时顶部弹出违规横幅，该步骤正常完成后自动消失；违规瞬间自动保存截图。
- 逐步骤检测缩略图：仅检测成功的一帧刷新，避免实时画面干扰。

#### 相机稳定性全面加固（修复长期运行的 AV 崩溃与掉帧）
- 切换相机 `AccessViolationException` 彻底加固：MSMF 优先 + DSHOW 会话隔离 + 不强制释放正在读取的图。
- 帧率修复：修复 UVC / ONVIF-RTSP 相机成功取帧后被 `Sleep` 误扣帧率的问题，USB(UVC) 采集帧率恢复正常。
- 连接体验：首次连接不再误报"相机已断开"；录制前校验相机连接，修复 ONVIF 取流 / 鉴权 / 重连。
- 录制修复：录制视频红蓝通道互换（bgr24 → rgb24）；稳定帧门控回归（步骤卡死 / 跳工序不报警）已修复。

#### 数据采集（录像 → 智能抽帧 → 自动预标注）
- 新增数据采集功能：录制作业视频后按场景阈值与清晰度智能抽帧，自动生成预标注素材，缩短标注准备周期。

#### SOP 复位与 Modbus 联锁
- SOP 复位改为立即复位；集成 Modbus 通信（NModbus）用于防错联锁，UI 同步改善。

#### 手部检测参数界面
- 补全 MediaPipe 手部检测参数设置界面；修复参数在 SOP 检测时不生效的根因，并接入真实时序平滑逻辑。

#### 边缘端移植：RK3588（P1 阶段）
- 在 `rk3588/` 建立独立 C++ 工程骨架，手部路线定为 YOLOv8-pose（找手腕）+ MediaPipe Landmark 出 21 关键点。
- SOP 引擎移植 + 黄金回放对拍：12 类条件逐帧布尔一致率 93%+、手部条件 98.7%+（尚未达 100%）。
- 移植 `ViolationDetector`（8 类违规 + 跳步探测 + 3 秒冷却）。
- 新增黄金数据集旁路录制器：零侵入录制"标准答案"帧序列，供板端 C++ 引擎逐帧对拍验收。
- 配套 RK3588 平台 SOP 检测系统移植方案文档（架构 / 三核 NPU / 开源借鉴分级）。

#### 设备性能测试
- 新增设备性能测试：对 MediaPipe / YOLO 推理做 CPU+GPU 帧率基准，量化边缘 / 桌面端推理性能。

### 多相机实时检测（最多 4 路）

- `CameraManager` 槽位化：每路相机独立连接/采集/推理，支持主相机 + `cam_2`~`cam_4`
- 主界面 1 / 2 / 4 路宫格布局（1 路单画面、2 路左右、4 路 2×2），每路独立叠加检测框
- 相机配置窗口支持多选连接、槽位持久化（`camera_config.json`），运行中新增相机自动补建推理通道
- 新增 `CameraPreviewControl`（次相机预览，检测框由推理结果叠加）与 `CameraFrameConverter`（帧转 SKBitmap）

### SOP 跨相机协同与步骤级相机/模型

- **步骤级指定相机**：每个步骤可选 `cam_2`~`cam_4` 画面检测（YAML `step.camera`），状态机按步骤相机取检测结果
- **步骤级指定模型**：每个步骤/相机可配不同 YOLO 模型（YAML `step.model`），运行时多模型缓存按需懒加载（`sop.model` 为全局默认）
- 生成向导与流程编辑窗口新增"检测相机 / 检测模型"下拉（自动扫描 `yolo_models/`），并支持随时修改
- **跨相机协同规则**：`sop.cross_camera` 节点定义"相机 A 区域 1 出现 X 且 相机 B 区域 2 出现 Y → 报警"，由 `CrossCameraEvaluator` 实时评估并播放报警音

### 多相机区域标定

- 区域标定窗口按相机分页签，YAML 区域按 `region.camera`（或步骤/跨相机规则推断）自动归属到对应相机画面
- 每路相机独立标定/拖动/改坐标，保存写回 `camera` 字段（主相机缺省不写）

### 手部检测（MediaPipe）修复

- 手部检测跟随当前步骤相机（不再固定主相机），手部骨架绘制到产生手部结果的相机画面
- 修复非阻塞缓存模式下"检测进行中返回空结果"导致多相机高负载时手部检测失效的问题（改为等待本次检测完成）
- 检测器增加文件日志（`sop_module_debug.log` 的 `[MediaPipe]` 前缀），便于排查

### 其他修复

- SOP 状态机：起始步骤取最小 StepId（配方步骤不固定从 1 开始），状态机严格按步骤相机取检测、禁止跨相机 fallback
- WebCameraService 改用 DirectShow 后端，修复 UVC 相机（如 DJI Pocket 3）采集/释放并发导致的 `AccessViolationException`
- 关闭程序时 `CameraViews` 在 UI 线程清理，修复跨线程修改集合异常
- 配置窗口：模型路径改下拉选择（可手填），"可检测类别"保存条件修复；相机配置窗口打开时展示已连接相机

## 开发计划

| 状态 | 内容 |
| --- | --- |
| ✅ 已完成 | 项目基础结构 |
| ✅ 已完成 | 核心类库（接口、ROI、相机 / 模型 / 用户服务、MVVM 基础） |
| ✅ 已完成 | SOP 合规检测模块（YOLO 姿态 + MediaPipe 手部 + 状态机 + 违规检测） |
| ✅ 已完成 | 通用 YOLO 检测服务、WPF UI 框架 |
| ⏳ 待规划 | PLC 通信、更多模型 / 算法接入 |

## 许可证

本项目基于 [MIT 许可证](./LICENSE) 开源。
