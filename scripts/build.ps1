# Inspection / Desktop 构建入口。通过 build.bat 调用，兼容 Windows PowerShell 5.1。
# 默认：CPU + Debug + 增量构建；--publish 默认 Release、win-x64、依赖 .NET 8 桌面运行时。
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }

function Show-Usage {
    Write-Host @"
用法: build.bat [--cpu | --cuda] [--debug | --release] [--publish] [--clean] [--force]

  --cpu       CPU 执行提供器（默认）
  --cuda      CUDA 执行提供器，检查 CUDA 12.x / cuDNN 9.x 的 PATH
  --debug     Debug 配置（构建默认）
  --release   Release 配置（发布默认）
  --publish   发布 Desktop 到 artifacts/publish，目标 win-x64，非自包含
  --clean     先清理所选 EP / 配置；发布时同时清空对应发布目录
  --force     CUDA 运行库缺失时仍允许编译，不跳过构建与产物检查
  --help      显示帮助

示例:
  scripts\build.bat
  scripts\build.bat --cuda --release
  scripts\build.bat --cuda --publish
  scripts\build.bat --cpu --publish --clean
"@
}

function Fail([string]$Message) {
    Write-Host "`n[失败] $Message" -ForegroundColor Red
    exit 1
}

$Provider = 'Cpu'
$Configuration = $null
$Publish = $false
$Clean = $false
$Force = $false
$ProviderSpecified = $false
foreach ($argument in $args) {
    switch (([string]$argument).ToLowerInvariant()) {
        '--cpu' {
            if ($ProviderSpecified -and $Provider -ne 'Cpu') { Fail '--cpu 与 --cuda 不能同时使用。' }
            $Provider = 'Cpu'; $ProviderSpecified = $true
        }
        '--cuda' {
            if ($ProviderSpecified -and $Provider -ne 'Cuda') { Fail '--cpu 与 --cuda 不能同时使用。' }
            $Provider = 'Cuda'; $ProviderSpecified = $true
        }
        '--debug' {
            if ($Configuration -eq 'Release') { Fail '--debug 与 --release 不能同时使用。' }
            $Configuration = 'Debug'
        }
        '--release' {
            if ($Configuration -eq 'Debug') { Fail '--debug 与 --release 不能同时使用。' }
            $Configuration = 'Release'
        }
        '--publish' { $Publish = $true }
        '--clean' { $Clean = $true }
        '--force' { $Force = $true }
        '--help' { Show-Usage; exit 0 }
        default { Show-Usage; Fail "未知参数: $argument" }
    }
}
if (-not $Configuration) { $Configuration = if ($Publish) { 'Release' } else { 'Debug' } }

$Root = Split-Path -Parent $PSScriptRoot
$Solution = Join-Path $Root 'Inspection.sln'
$DesktopProject = Join-Path $Root 'src\Desktop\Desktop.csproj'
$Artifacts = Join-Path $Root 'artifacts'

function Assert-NotRunning([string]$Executable) {
    $running = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Executable)) -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and [string]::Equals($_.Path, $Executable, [StringComparison]::OrdinalIgnoreCase) })
    if ($running.Count -gt 0) { Fail "请先关闭 $Executable (PID: $($running.Id -join ', '))。" }
}

function Assert-File([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { Fail "缺少构建产物: $Path" }
}

function Test-PathDll([string]$Name) {
    foreach ($entry in ($env:Path -split ';')) {
        $directory = $entry.Trim().Trim('"')
        if ([string]::IsNullOrWhiteSpace($directory)) { continue }
        if (Test-Path -LiteralPath (Join-Path $directory $Name) -PathType Leaf) { return $true }
    }
    return $false
}

try {
    if (-not (Test-Path -LiteralPath $Solution -PathType Leaf) -or
        -not (Test-Path -LiteralPath $DesktopProject -PathType Leaf)) {
        Fail '未找到 Inspection.sln 或 src/Desktop/Desktop.csproj，请检查仓库结构。'
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail '未找到 dotnet，请安装 .NET 8 或更高版本 SDK。' }
    $sdkVersion = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or [int]($sdkVersion.Split('.')[0]) -lt 8) { Fail '需要 .NET 8 或更高版本 SDK。' }

    # 使用全局模式属性隔离发布缓存；SDK 编译类库引用时可能移除 RuntimeIdentifier。
    $mode = if ($Publish) { 'publish' } else { 'build' }
    $properties = @("-p:YoloEp=$Provider", "-p:Configuration=$Configuration", "-p:InspectionBuildMode=$mode")
    if ($Publish) { $properties += '-p:RuntimeIdentifier=win-x64' }
    $evaluation = & dotnet msbuild $DesktopProject -nologo @properties '-getProperty:TargetPath,TargetDir,PublishDir'
    if ($LASTEXITCODE -ne 0) { Fail '无法读取 Desktop 的构建路径。' }
    $paths = ($evaluation -join "`n" | ConvertFrom-Json).Properties
    $Output = if ($Publish) { [IO.Path]::GetFullPath($paths.PublishDir) } else { [IO.Path]::GetFullPath($paths.TargetDir) }
    $Executable = Join-Path $Output ([IO.Path]::GetFileNameWithoutExtension($paths.TargetPath) + '.exe')
    Assert-NotRunning $Executable
    # publish 也会更新它依赖的 build 输出。
    Assert-NotRunning (Join-Path $paths.TargetDir ([IO.Path]::GetFileName($Executable)))

    Write-Host "`nInspection / Desktop - $Provider / $Configuration"
    Write-Host "SDK:  $sdkVersion"
    Write-Host "仓库: $Root"
    Write-Host "输出: $Output"
    Write-Host $(if ($Publish) { '模式: 发布 (win-x64，依赖 .NET 8 桌面运行时)' } else { '模式: 增量构建' })

    if ($Provider -eq 'Cuda') {
        $missing = @()
        foreach ($dll in @('cudart64_12.dll', 'cublas64_12.dll', 'cublasLt64_12.dll', 'cudnn64_9.dll')) {
            if (Test-PathDll $dll) { Write-Host "[OK]   $dll" }
            else { Write-Host "[缺失] $dll" -ForegroundColor Yellow; $missing += $dll }
        }
        if ($missing.Count -gt 0) {
            if (-not $Force) { Fail '请将 CUDA 12.x / cuDNN 9.x 的 bin 目录加入 PATH；仅编译可使用 --force。' }
            Write-Host '[警告] 跳过本机构建依赖检查；运行目标仍需 NVIDIA 驱动、CUDA 12.x / cuDNN 9.x。' -ForegroundColor Yellow
        }
    }

    if ($Clean) {
        Write-Host "`n清理 $Provider / $Configuration ..."
        & dotnet clean $Solution @properties -v:minimal --nologo
        if ($LASTEXITCODE -ne 0) { Fail '清理失败。' }
        if (Test-Path -LiteralPath $Output) {
            # 删除前核实最终绝对路径，限定为 artifacts/bin 或 publish 下所选配置。
            $category = if ($Publish) { 'publish' } else { 'bin' }
            $allowedRoot = [IO.Path]::GetFullPath((Join-Path $Artifacts $category)) + [IO.Path]::DirectorySeparatorChar
            if (-not $Output.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
                Fail "拒绝清理产物根目录之外的路径: $Output"
            }
            Remove-Item -LiteralPath $Output -Recurse -Force
        }
    }

    $logDirectory = Join-Path $Artifacts 'logs'
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $verb = if ($Publish) { 'publish' } else { 'build' }
    $log = Join-Path $logDirectory ("$verb-$Provider-$Configuration-" + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.log')
    Write-Host "`n开始 $verb ... (日志: $log)"
    if ($Publish) {
        & dotnet publish $DesktopProject @properties --self-contained false -v:minimal --nologo 2>&1 | Tee-Object -FilePath $log
    }
    else {
        & dotnet build $Solution @properties -v:minimal --nologo 2>&1 | Tee-Object -FilePath $log
    }
    if ($LASTEXITCODE -ne 0) { Fail "构建失败，日志: $log" }

    foreach ($name in @('Desktop.exe', 'Desktop.dll', 'Desktop.deps.json', 'Desktop.runtimeconfig.json',
                         'Core.dll', 'Detection.dll', 'SOP.dll', 'Converters.dll', "YoloDotNet.ExecutionProvider.$Provider.dll")) {
        Assert-File (Join-Path $Output $name)
    }
    $otherProvider = if ($Provider -eq 'Cuda') { 'Cpu' } else { 'Cuda' }
    if (Test-Path -LiteralPath (Join-Path $Output "YoloDotNet.ExecutionProvider.$otherProvider.dll")) {
        Fail '输出混入另一执行提供器，请使用 --clean 重建。'
    }
    $nativeDirectory = if ($Publish) { $Output } else { Join-Path $Output 'runtimes\win-x64\native' }
    Assert-File (Join-Path $nativeDirectory 'onnxruntime.dll')
    if ($Provider -eq 'Cuda') { Assert-File (Join-Path $nativeDirectory 'onnxruntime_providers_cuda.dll') }
    if ($Publish) {
        Assert-File (Join-Path $Output 'configs\sop_config.json')
        if (-not (Test-Path -LiteralPath (Join-Path $Output 'configs\sop') -PathType Container)) { Fail '发布目录缺少 configs/sop 配方。' }
    }

    Write-Host "`n[成功] $Provider / $Configuration 产物检查通过。" -ForegroundColor Green
    Write-Host "程序: $Executable"
    Write-Host "日志: $log"
    if ($Publish) { Write-Host '可分发整个输出目录；目标机需 .NET 8 桌面运行时，检测模型按配方放入 models/ 或 yolo_models/。' }
}
catch { Fail $_.Exception.Message }
