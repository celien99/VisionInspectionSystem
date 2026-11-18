# 脚本入口

所有仓库默认路径由脚本自身定位，支持从其他工作目录调用。

## Windows 构建与发布

使用 `build.bat` 调用兼容 Windows PowerShell 5.1 的 `build.ps1`：

```bat
scripts\build.bat                         :: CPU / Debug 增量构建
scripts\build.bat --cuda --release        :: CUDA / Release 构建
scripts\build.bat --cuda --publish        :: CUDA / Release / win-x64 发布
scripts\build.bat --cpu --publish --clean :: 清理后发布 CPU 版
scripts\build.bat --help
```

`--publish` 默认 Release，也可指定 `--debug`。`--clean` 清理所选 EP / 配置的构建文件；发布时还会清空对应发布目录（先备份其中的运行数据）。`--force` 仅允许 CUDA 运行库缺失时继续编译。CPU 构建无需 CUDA。SDK 需要 .NET 8 或更高版本，发布包依赖目标机的 .NET 8 桌面运行时。

| 内容 | 输出位置 |
|---|---|
| 构建产物 | `artifacts/bin/<项目>/<Cpu或Cuda>/<配置>/<目标框架>/` |
| 中间文件及 NuGet 缓存 | `artifacts/obj/<项目>/<Cpu或Cuda>/<build或publish>/` |
| Windows x64 发布包 | `artifacts/publish/Desktop/<Cpu或Cuda>/<配置>/win-x64/` |
| 构建与发布日志 | `artifacts/logs/` |

使用当前 `Inspection.sln` 和 `src/Desktop/Desktop.csproj`，执行文件名为 `Desktop.exe`。直接执行 `dotnet build` 或使用 Visual Studio 也遵循公共路径配置。CPU 与 CUDA 的缓存和产物隔离，切换 EP 无需先清理另一份构建。

发布会复制 `configs/` 及项目声明的可选模型；其他模型按配方放入发布目录的 `models/` 或 `yolo_models/`。CUDA 部署还需要 NVIDIA 驱动、CUDA 12.x、cuDNN 9.x。`artifacts/` 不纳入版本库。

## 辅助工具

| 脚本 | 用法与默认输出 | 依赖 |
|---|---|---|
| `train_sop_detector.py` | `python scripts/train_sop_detector.py --help`；训练结果在 `artifacts/training/sop-det/`，最终 ONNX 复制到 `yolo_models/`，相对路径基于仓库根目录 | Ultralytics CLI |
| `build_sop_manual.py` | `python scripts/build_sop_manual.py`；自动发现 `docs/SOP*.md`，输出到 `artifacts/docs/`；缺少文档源时明确报错 | python-docx，文档源文件 |
| `gen_modern_icon.py` | `python scripts/gen_modern_icon.py`；默认写入 `src/Desktop/Assets/AppIcon.ico`；`--png 256` 默认写入 `artifacts/assets/AppIcon.png`；`--out` 相对路径基于仓库根目录 | Pillow |
| `convert_wav_to_16bit.py` | `python scripts/convert_wav_to_16bit.py input.wav output.wav`；输入、输出使用调用者提供的路径 | Python 标准库 |
