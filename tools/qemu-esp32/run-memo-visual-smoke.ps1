param(
    [string]$QemuPath = "$(Split-Path -Parent $PSCommandPath)\install\qemu\bin\qemu-system-xtensa.exe",
    [string]$ImagePath = "$(Split-Path -Parent $PSCommandPath)\images\PalmOS4MemoPadSmoke-qemu-headless.bin",
    [string]$OutputDir = "$(Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath)))\out\qemu-visual-smoke",
    [int]$Port = 5555,
    [int]$ConnectWaitMs = 15000,
    [int]$TimeoutMs = 30000,
    [int]$Scale = 3,
    [int]$TapX = 145,
    [int]$TapY = 6,
    [switch]$NoBuild,
    [switch]$Psram
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent (Split-Path -Parent $root)
$qemu = $QemuPath
$toolProject = Join-Path $repoRoot "tools\vbnet\PalmEsp32RomTool\PalmEsp32RomTool.vbproj"
$toolExe = Join-Path $repoRoot "tools\vbnet\PalmEsp32RomTool\bin\Debug\net8.0\PalmEsp32RomTool.exe"

function Wait-ForTcpPort {
    param(
        [int]$PortNumber,
        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $listen = netstat -ano | Select-String -Pattern ":$PortNumber\s+.*LISTENING"
        if ($listen) {
            return
        }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)

    throw "QEMU did not open TCP serial port $PortNumber"
}

function Invoke-Tool {
    param([string[]]$Arguments)

    & $toolExe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "PalmEsp32RomTool failed with exit code $LASTEXITCODE"
    }
}

if (-not (Test-Path $qemu)) {
    throw "Missing QEMU executable: $qemu"
}
if (-not (Test-Path $ImagePath)) {
    throw "Missing flash image: $ImagePath. Run make-memo-flash-image.ps1 first."
}
if (-not (Test-Path $toolProject)) {
    throw "Missing VB.NET tool project: $toolProject"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

if (-not $NoBuild) {
    & dotnet build $toolProject
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }
}
if (-not (Test-Path $toolExe)) {
    throw "Missing built VB.NET tool: $toolExe"
}

$memoBmp = Join-Path $OutputDir "memo-list.bmp"
$popupBmp = Join-Path $OutputDir "category-popup.bmp"

Write-Host "Starting QEMU TCP visual smoke on 127.0.0.1:$Port"
$job = Start-Job -ScriptBlock {
    param($QemuPath, $FlashImagePath, $TcpPort, $UsePsram)

    $args = @(
        "-machine", "esp32s3",
        "-m", "8M",
        "-drive", "file=$FlashImagePath,if=mtd,format=raw",
        "-nographic",
        "-monitor", "none"
    )
    if ($UsePsram) {
        $args += @("-global", "driver=ssi_psram,property=is_octal,value=true")
    }
    $args += @(
        "-chardev", "socket,id=serial0,host=127.0.0.1,port=$TcpPort,server=on,wait=on",
        "-serial", "chardev:serial0"
    )

    & $QemuPath @args
} -ArgumentList $qemu, $ImagePath, $Port, [bool]$Psram

try {
    Wait-ForTcpPort -PortNumber $Port -TimeoutSeconds 10

    Write-Host "Capturing Memo list: $memoBmp"
    Invoke-Tool -Arguments @(
        "snapshot-tcp", "127.0.0.1", "$Port", $memoBmp,
        "--timeout-ms", "$TimeoutMs",
        "--scale", "$Scale",
        "--connect-wait-ms", "$ConnectWaitMs"
    )

    Write-Host "Restarting QEMU for tap snapshot"
    Stop-Job $job -ErrorAction SilentlyContinue
    Remove-Job $job -Force -ErrorAction SilentlyContinue

    $job = Start-Job -ScriptBlock {
        param($QemuPath, $FlashImagePath, $TcpPort, $UsePsram)

        $args = @(
            "-machine", "esp32s3",
            "-m", "8M",
            "-drive", "file=$FlashImagePath,if=mtd,format=raw",
            "-nographic",
            "-monitor", "none"
        )
        if ($UsePsram) {
            $args += @("-global", "driver=ssi_psram,property=is_octal,value=true")
        }
        $args += @(
            "-chardev", "socket,id=serial0,host=127.0.0.1,port=$TcpPort,server=on,wait=on",
            "-serial", "chardev:serial0"
        )

        & $QemuPath @args
    } -ArgumentList $qemu, $ImagePath, $Port, [bool]$Psram

    Wait-ForTcpPort -PortNumber $Port -TimeoutSeconds 10

    Write-Host "Capturing category popup after tap ($TapX,$TapY): $popupBmp"
    Invoke-Tool -Arguments @(
        "tap-snapshot-tcp", "127.0.0.1", "$Port", "$TapX", "$TapY", $popupBmp,
        "--timeout-ms", "$TimeoutMs",
        "--scale", "$Scale",
        "--connect-wait-ms", "$ConnectWaitMs"
    )

    Write-Host "Visual smoke passed"
    Write-Host "  $memoBmp"
    Write-Host "  $popupBmp"
} finally {
    Stop-Job $job -ErrorAction SilentlyContinue
    Remove-Job $job -Force -ErrorAction SilentlyContinue
}
