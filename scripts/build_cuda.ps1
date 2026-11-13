# ============================================================
#  VisionInspectionSystem - CUDA(GPU)构建脚本
#
#  用法:
#    scripts\build_cuda.bat               Debug 构建
#    scripts\build_cuda.bat --release     Release 构建
#    scripts\build_cuda.bat --force       依赖检查不通过也继续构建
#    scripts\build_cuda.bat --no-clean    跳过构建前清理
#
#  前提: .NET 8 SDK + NVIDIA 驱动 + CUDA 12.x + cuDNN 9.x
#        (CUDA / cuDNN 的 bin 目录必须在 PATH 中)
#
#  入口请用 build_cuda.bat(双击或命令行),它会以 Bypass 策略调用本脚本。
# ============================================================

$ErrorActionPreference = 'Stop'

# 统一使用 UTF-8 输出，保证中文在控制台/重定向(CI 日志)下都不乱码
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
}
catch { }

function Show-Usage {
    Write-Host ""
    Write-Host "用法: build_cuda.bat [--release] [--force] [--no-clean]"
    Write-Host ""
    Write-Host "  --release   使用 Release 配置构建(默认 Debug)"
    Write-Host "  --force     依赖检查不通过也继续构建"
    Write-Host "  --no-clean  跳过构建前清理"
    Write-Host ""
}

$Release = $false
$Force = $false
$NoClean = $false
$ShowHelp = $false

foreach ($arg in $args) {
    $name = ([string]$arg).TrimStart('-', '/').ToLowerInvariant()
    switch ($name) {
        'release'  { $Release = $true }
        'force'    { $Force = $true }
        'no-clean' { $NoClean = $true }
        'help'     { $ShowHelp = $true }
        'h'        { $ShowHelp = $true }
        default {
            Write-Host "[错误] 未知参数: $arg"
            Show-Usage
            exit 1
        }
    }
}

if ($ShowHelp) {
    Show-Usage
    exit 0
}

$Configuration = if ($Release) { 'Release' } else { 'Debug' }
$Root = Split-Path -Parent $PSScriptRoot
$Sln = Join-Path $Root 'VisionInspectionSystem.sln'
$Out = Join-Path $Root ("src\UI\VisionInspection.UI\bin\" + $Configuration + "\net8.0-windows")

function Fail([string]$message) {
    Write-Host ""
    Write-Host "[失败] $message" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "============================================================"
Write-Host " VisionInspectionSystem - CUDA 构建"
Write-Host " 仓库: $Root"
Write-Host " 配置: $Configuration"
Write-Host "============================================================"
Write-Host ""

# ---------- 1. 检查 dotnet SDK ----------
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Fail "未找到 dotnet 命令。请先安装 .NET 8 SDK 并加入 PATH。"
}
$dotnetVersion = (& dotnet --version) | Select-Object -First 1
Write-Host "[OK]   .NET SDK $dotnetVersion"

# ---------- 2. 检查程序是否正在运行 ----------
$running = Get-Process -Name 'VisionInspection.UI' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host ""
    Write-Host "[错误] 检测到 VisionInspection.UI.exe 正在运行(PID: $($running.Id -join ', '))。" -ForegroundColor Red
    Write-Host "       请先关闭程序，否则输出文件被锁定，构建会失败。"
    exit 1
}
Write-Host "[OK]   程序未在运行"

# ---------- 3. 检查 CUDA / cuDNN 运行库 ----------
function Test-PathDll([string]$name) {
    foreach ($dir in ($env:Path -split ';')) {
        if ([string]::IsNullOrWhiteSpace($dir)) { continue }
        $candidate = Join-Path $dir $name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $true }
    }
    return $false
}

$dependencies = @(
    @{ Dll = 'cudart64_12.dll';   Label = 'CUDA 12.x 运行时' },
    @{ Dll = 'cublas64_12.dll';   Label = 'CUDA 12.x cuBLAS' },
    @{ Dll = 'cublasLt64_12.dll'; Label = 'CUDA 12.x cuBLASLt' },
    @{ Dll = 'cudnn64_9.dll';     Label = 'cuDNN 9.x' }
)

$missing = @()
foreach ($dep in $dependencies) {
    if (Test-PathDll $dep.Dll) {
        Write-Host "[OK]   $($dep.Dll) ($($dep.Label))"
    }
    else {
        Write-Host "[缺失] $($dep.Dll) ($($dep.Label))" -ForegroundColor Yellow
        $missing += $dep.Dll
    }
}

if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host "[提示] 缺少 CUDA 构建所需的运行库。产物仍能编译，但运行时会因"
    Write-Host '       加载 CUDA 执行提供器失败而回退为 "CUDA 不可用"。'
    Write-Host "       安装:"
    Write-Host "         CUDA Toolkit 12.x  https://developer.nvidia.com/cuda-downloads"
    Write-Host "         cuDNN 9.x          https://developer.nvidia.com/cudnn"
    Write-Host "       安装后把 CUDA 的 bin 目录和 cuDNN 的 bin 目录加入系统 PATH，"
    Write-Host "       并重新打开终端 / 重启资源管理器，确保新进程能读到。"
    if (-not $Force) {
        Fail "依赖检查未通过(如确认要强行构建，请加 --force)。"
    }
    Write-Host "[警告] 已指定 --force，继续构建(运行时很可能仍不可用)。" -ForegroundColor Yellow
}

# ---------- 4. 清理 + 构建 ----------
if (-not $NoClean) {
    Write-Host ""
    Write-Host "[1/2] 清理旧产物..."
    & dotnet clean $Sln -p:YoloEp=Cuda -c $Configuration -v:minimal
    if ($LASTEXITCODE -ne 0) {
        Fail "清理失败，可能有文件被占用。请关闭程序或相关进程后重试。"
    }
}
else {
    Write-Host ""
    Write-Host "[1/2] 跳过清理(--no-clean)"
}

Write-Host "[2/2] 构建 CUDA 版本..."
& dotnet build $Sln -p:YoloEp=Cuda -c $Configuration -v:minimal
if ($LASTEXITCODE -ne 0) {
    Fail "构建失败。请检查上面的错误信息。"
}

# ---------- 5. 校验产物 ----------
Write-Host ""
Write-Host "校验构建产物..."
$badOutput = $false
$artifacts = @(
    (Join-Path $Out 'YoloDotNet.ExecutionProvider.Cuda.dll'),
    (Join-Path $Out 'runtimes\win-x64\native\onnxruntime_providers_cuda.dll')
)
foreach ($file in $artifacts) {
    if (Test-Path $file) {
        Write-Host "[OK]   $file"
    }
    else {
        Write-Host "[缺失] $file" -ForegroundColor Yellow
        $badOutput = $true
    }
}

if (Test-Path (Join-Path $Out 'YoloDotNet.ExecutionProvider.Cpu.dll')) {
    Write-Host "[警告] 输出目录仍有 CPU EP DLL(旧产物)。建议关闭程序后删除 bin 再重建。" -ForegroundColor Yellow
}

if ($badOutput) {
    Fail "构建产物不是完整的 CUDA 版本，请勿分发。查看上方缺失项。"
}

Write-Host ""
Write-Host "============================================================"
Write-Host " [成功] CUDA 版构建完成"
Write-Host " 可执行文件: $Out\VisionInspection.UI.exe"
Write-Host ""
Write-Host " 提示: 直接运行上面的 exe 即可使用 GPU。"
Write-Host "       不要在 Visual Studio 中按 F5 或执行不带参数的 dotnet build，"
Write-Host "       那会重新编译成 CPU 版并覆盖当前产物。"
Write-Host "============================================================"
exit 0
