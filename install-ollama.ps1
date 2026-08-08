# ============================================================
#  Giantess LLM Mod - Ollama 一键配置助手
#  功能：安装/启动 Ollama -> 读取显存推荐模型 -> 测试接口
#        -> 把接口和推荐模型写入模组配置（不自动下载模型）
#  用法：把本文件与 install-ollama.bat 放到游戏根目录，
#        双击 install-ollama.bat 即可。
#  换模型：.\install-ollama.ps1 -ForceModel "qwen2.5:3b"
# ============================================================

param(
    [string]$ForceModel = ""
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
try { $OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

# ---------------- 可调参数 ----------------
$OllamaInstallerUrl = "https://ollama.com/download/OllamaSetup.exe"
$OllamaEndpoint     = "http://127.0.0.1:11434"
$ApiUrl             = "$OllamaEndpoint/v1/chat/completions"
# -----------------------------------------

function Write-Step([string]$msg)  { Write-Host ""; Write-Host "=== $msg ===" -ForegroundColor Cyan }
function Write-OK([string]$msg)    { Write-Host "[OK] $msg" -ForegroundColor Green }
function Write-Warn([string]$msg)  { Write-Host "[!] $msg" -ForegroundColor Yellow }
function Write-Fail([string]$msg)  { Write-Host "[X] $msg" -ForegroundColor Red }
function Find-GameRoot {
    $dir = Split-Path -Parent $PSCommandPath
    for ($i = 0; $i -lt 5 -and $dir; $i++) {
        if (Test-Path (Join-Path $dir 'GiantessSandbox.exe')) { return $dir }
        $dir = Split-Path -Parent $dir
    }
    foreach ($d in @(
        "$env:ProgramFiles(x86)\Steam\steamapps\common\Giantess Sandbox",
        "$env:ProgramFiles\Steam\steamapps\common\Giantess Sandbox",
        "D:\Games\GaintessSandBox"
    )) {
        if ($d -and (Test-Path (Join-Path $d 'GiantessSandbox.exe'))) { return $d }
    }
    return $null
}

function Select-GameRoot {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
        $dlg.Description = "请选择 Giantess Sandbox 游戏根目录（包含 GiantessSandbox.exe 的文件夹）"
        $dlg.ShowNewFolderButton = $false
        if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { return $dlg.SelectedPath }
    } catch { }
    return $null
}
function Get-OllamaExe {
    foreach ($p in @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe'),
        (Join-Path $env:ProgramFiles 'Ollama\ollama.exe'),
        'ollama.exe'
    )) {
        $cmd = Get-Command $p -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }
    return $null
}

function Test-OllamaServer([int]$TimeoutSec = 3) {
    try {
        $r = Invoke-WebRequest -Uri "$OllamaEndpoint/api/tags" -UseBasicParsing -TimeoutSec $TimeoutSec
        return ($r.StatusCode -eq 200)
    } catch { return $false }
}

function Wait-OllamaServer {
    for ($i = 0; $i -lt 60; $i++) {
        if (Test-OllamaServer) { return $true }
        Start-Sleep -Seconds 1
    }
    return $false
}
function Get-GpuVramGB {
    # 通过注册表读取真实显存（WMI 的 AdapterRAM 超过 4GB 会溢出不准）
    $maxVram = 0
    $idx = 0
    $keyPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
    foreach ($gpu in @(Get-WmiObject Win32_VideoController -ErrorAction SilentlyContinue)) {
        $vram = 0
        try {
            $subKey = Join-Path $keyPath ('{0:D4}' -f $idx)
            $qw = (Get-ItemProperty $subKey -Name 'HardwareInformation.qwMemorySize' -ErrorAction SilentlyContinue).'HardwareInformation.qwMemorySize'
            if ($qw) { $vram = [uint64]$qw }
        } catch { }
        if (-not $vram) {
            $adapterRam = [uint64]$gpu.AdapterRAM
            if ($adapterRam -gt 0 -and $adapterRam -lt 0xFFFFFF00) { $vram = $adapterRam * 1MB }
        }
        if ($vram -gt $maxVram) { $maxVram = $vram }
        $idx++
    }
    return [math]::Round($maxVram / 1GB, 1)
}
function Get-RecommendedModel([double]$vramGB, [double]$ramGB) {
    if ($ForceModel) { return @{ Model = $ForceModel; Reason = '（手动指定）' } }
    if ($vramGB -le 0) {
        if ($ramGB -ge 16) { return @{ Model = 'qwen2.5:7b';  Reason = '未检测到独显，按内存推荐（CPU 运行）' } }
        if ($ramGB -ge 8)  { return @{ Model = 'qwen2.5:3b';  Reason = '未检测到独显，按内存推荐（CPU 运行）' } }
        return @{ Model = 'qwen2.5:1.5b'; Reason = '未检测到独显，按内存推荐（CPU 运行）' }
    }
    if ($vramGB -ge 12) { return @{ Model = 'gemma3:12b'; Reason = "约 ${vramGB}GB 显存，推荐 gemma3:12b（也可 qwen2.5:14b）" } }
    if ($vramGB -ge 8)  { return @{ Model = 'qwen2.5:7b';  Reason = "约 ${vramGB}GB 显存，推荐 qwen2.5:7b（也可 gemma3:4b）" } }
    if ($vramGB -ge 6)  { return @{ Model = 'qwen2.5:7b';  Reason = "约 ${vramGB}GB 显存，推荐 qwen2.5:7b（保守可 qwen2.5:3b）" } }
    if ($vramGB -ge 4)  { return @{ Model = 'qwen2.5:3b';  Reason = "约 ${vramGB}GB 显存，推荐 qwen2.5:3b（也可 gemma3:1b）" } }
    return @{ Model = 'qwen2.5:1.5b'; Reason = "显存约 ${vramGB}GB，推荐 qwen2.5:1.5b" }
}
function Main {
    Write-Host ""
    Write-Host "==============================================" -ForegroundColor Magenta
    Write-Host "  Giantess LLM Mod - Ollama 一键配置助手" -ForegroundColor Magenta
    Write-Host "==============================================" -ForegroundColor Magenta

    # ---- 1. 定位游戏目录 ----
    Write-Step "第 1 步 / 6：定位游戏目录"
    $gameRoot = Find-GameRoot
    if (-not $gameRoot) {
        Write-Warn "未自动找到游戏，请在弹出窗口中选择游戏文件夹。"
        $gameRoot = Select-GameRoot
        if (-not $gameRoot) { Write-Fail "未选择游戏目录，安装取消。"; return 1 }
    }
    if (-not (Test-Path (Join-Path $gameRoot 'GiantessSandbox.exe'))) {
        Write-Fail "所选目录中没有 GiantessSandbox.exe，请确认选择的是游戏根目录。"
        return 1
    }
    Write-OK "游戏目录：$gameRoot"

    $cfgDir  = Join-Path $gameRoot 'BepInEx\config'
    $cfgFile = Join-Path $cfgDir 'com.giantess.llmmod.cfg'
    $modDll  = Join-Path $gameRoot 'BepInEx\plugins\GiantessLLMMod\GiantessLLMMod.dll'
    if (-not (Test-Path $modDll)) {
        Write-Warn "未找到模组 DLL（BepInEx\plugins\GiantessLLMMod\GiantessLLMMod.dll）。"
        Write-Warn "请先确认模组已安装，否则下面的配置不会生效。"
    }
    # ---- 2. 检查 / 安装 Ollama ----
    Write-Step "第 2 步 / 6：检查并安装 Ollama"
    $ollamaExe = Get-OllamaExe
    if (-not $ollamaExe) {
        Write-Warn "未检测到 Ollama，开始自动下载安装包（约 500MB，请耐心等待）..."
        $installer = Join-Path $env:TEMP 'OllamaSetup.exe'
        Write-Host "正在从官网下载：$OllamaInstallerUrl"
        if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
            & curl.exe -fL --retry 3 --connect-timeout 30 -o $installer $OllamaInstallerUrl
        } else {
            Invoke-WebRequest -Uri $OllamaInstallerUrl -OutFile $installer -UseBasicParsing
        }
        if (-not (Test-Path $installer) -or (Get-Item $installer).Length -lt 1MB) {
            Write-Fail "下载失败。请手动到 https://ollama.com/download 下载 OllamaSetup.exe 后重新运行本脚本。"
            return 1
        }
        Write-Host "正在静默安装，请稍候..."
        Start-Process -FilePath $installer -ArgumentList '/S' -Wait -WindowStyle Hidden
        $ollamaExe = Get-OllamaExe
        if (-not $ollamaExe) {
            Write-Fail "安装未成功。请手动运行 OllamaSetup.exe 完成安装后重试。"
            return 1
        }
    }
    Write-OK "Ollama 已就绪：$ollamaExe"
    # ---- 3. 启动服务 ----
    Write-Step "第 3 步 / 6：启动 Ollama 服务"
    if (-not (Test-OllamaServer)) {
        Write-Host "服务未运行，正在后台启动..."
        Start-Process -FilePath $ollamaExe -ArgumentList 'serve' -WindowStyle Hidden
        if (-not (Wait-OllamaServer)) {
            Write-Fail "Ollama 服务启动失败（$OllamaEndpoint 无响应）。"
            Write-Fail "请检查防火墙/杀毒软件是否拦截，然后重新运行本脚本。"
            return 1
        }
    }
    Write-OK "Ollama 服务已就绪：$OllamaEndpoint"
    # ---- 4. 读取显存，推荐模型 ----
    Write-Step "第 4 步 / 6：读取显存，推荐模型"
    $ramGB = [math]::Round((Get-WmiObject Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
    $vramGB = Get-GpuVramGB
    $rec = Get-RecommendedModel $vramGB $ramGB
    $model = $rec.Model
    Write-Host "  内存：约 ${ramGB}GB"
    Write-Host "  显存：约 ${vramGB}GB（未识别到独显会显示 0）"
    Write-Warn "推荐模型：$model"
    Write-Warn $rec.Reason
    Write-Warn "想用其它模型：运行脚本时加参数 -ForceModel 模型名"
    # ---- 5. 测试接口 ----
    Write-Step "第 5 步 / 6：测试模组接口"
    $installed = @()
    try {
        $tags = Invoke-RestMethod -Uri "$OllamaEndpoint/api/tags" -TimeoutSec 10
        $installed = @($tags.models | ForEach-Object { ($_.name -replace ':latest$', '') })
    } catch {
        Write-Fail "无法读取 Ollama 模型列表：$($_.Exception.Message)"
        return 1
    }
    $needModel = $model -replace ':latest$', ''
    $needDownload = -not ($installed -contains $needModel)
    if (-not $needDownload) {
        $testBody = @{
            model    = $model
            messages = @(@{ role = 'user'; content = '你好，请只回复四个字：连接成功' })
            stream   = $false
        } | ConvertTo-Json -Depth 5
        try {
            $resp = Invoke-RestMethod -Uri $ApiUrl -Method Post -ContentType 'application/json' -Body $testBody -TimeoutSec 180
            $reply = $resp.choices[0].message.content
            if (-not $reply) { $reply = '（未返回文本）' }
            Write-OK "接口测试成功！AI 回复：$reply"
        } catch {
            Write-Fail "接口测试失败：$($_.Exception.Message)"
            Write-Fail "请确认 Ollama 服务正常后重新运行本脚本。"
            return 1
        }
    } else {
        Write-Warn "模型 $model 还没下载，已跳过对话测试。"
    }
    # ---- 6. 写入模组配置 ----
    Write-Step "第 6 步 / 6：写入模组配置"
    if (-not (Test-Path $cfgDir)) { New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null }
    $isNew = -not (Test-Path $cfgFile)
    if ($isNew) {
        $cfg = @(
            "## Settings file was created by GiantessLLMMod Ollama installer",
            "## Plugin GUID: com.giantess.llmmod",
            "",
            "[LLM API]",
            "ApiBaseUrl = $ApiUrl",
            "ApiKey =",
            "Model = $model",
            ""
        ) -join "`r`n"
    } else {
        $cfg = [IO.File]::ReadAllText($cfgFile, [Text.Encoding]::UTF8)
        $cfg = [regex]::Replace($cfg, '(?m)^ApiBaseUrl\s*=.*$', "ApiBaseUrl = $ApiUrl")
        $cfg = [regex]::Replace($cfg, '(?m)^ApiKey\s*=.*$', 'ApiKey =')
        $cfg = [regex]::Replace($cfg, '(?m)^Model\s*=.*$', "Model = $model")
    }
    [IO.File]::WriteAllText($cfgFile, $cfg, [Text.UTF8Encoding]::new($false))
    if ($isNew) {
        Write-Warn "未找到旧配置，已新建配置文件；游戏下次启动会自动补全其余默认项。"
    }
    Write-OK "已写入模组配置：$cfgFile"
    Write-Host "  ApiBaseUrl = $ApiUrl"
    Write-Host "  ApiKey     = （留空，本地 Ollama 无需密钥）"
    Write-Host "  Model      = $model"
    # ---- 下载模型（可选，不会自动下载） ----
    if ($needDownload) {
        Write-Host ""
        Write-Warn "推荐模型 $model 还未下载，游戏里暂时无法使用 AI。"
        $ans = Read-Host "按 Y 现在下载（可能几 GB，请耐心等待），直接回车跳过"
        if ($ans -eq 'Y' -or $ans -eq 'y') {
            Write-Host "正在下载模型 $model ，请耐心等待..."
            & $ollamaExe pull $model
            if ($LASTEXITCODE -eq 0) { Write-OK "模型下载完成！" }
            else { Write-Warn "下载失败，可稍后手动执行：ollama run $model" }
        } else {
            Write-Warn "已跳过下载。之后想用时打开命令行输入：ollama run $model"
        }
    }

    # ---- 完成 ----
    Write-Host ""
    Write-Host "======================== 完成 ========================" -ForegroundColor Magenta
    Write-Host "  启动游戏后按 F8 打开模组窗口即可使用！" -ForegroundColor Green
    Write-Host "  聊天：Chat 页签打字；手动触发：按 F7" -ForegroundColor Green
    Write-Host "=====================================================" -ForegroundColor Magenta
    return 0
}

try {
    $code = Main
    if ($code -ne 0) { exit $code }
} catch {
    Write-Fail "脚本出错：$($_.Exception.Message)"
    Write-Fail $_.ScriptStackTrace
    exit 1
}