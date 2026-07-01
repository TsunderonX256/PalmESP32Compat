param(
    [string]$ImagePath = "$(Split-Path -Parent $PSCommandPath)\images\PalmOS4MemoPadSmoke-qemu-headless.bin",
    [int]$Seconds = 0,
    [switch]$Graphical,
    [switch]$Psram
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSCommandPath
$qemu = Join-Path $root "install\qemu\bin\qemu-system-xtensa.exe"

if (-not (Test-Path $qemu)) {
    throw "Missing QEMU executable: $qemu"
}
if (-not (Test-Path $ImagePath)) {
    throw "Missing flash image: $ImagePath. Run make-memo-flash-image.ps1 first."
}

$driveArg = "file=`"$ImagePath`",if=mtd,format=raw"
$args = @(
    "-machine", "esp32s3",
    "-m", "8M",
    "-drive", $driveArg
)

if ($Psram) {
    $args += @("-global", "driver=ssi_psram,property=is_octal,value=true")
}

if ($Seconds -gt 0) {
    $out = Join-Path $root "qemu-run.out.log"
    $err = Join-Path $root "qemu-run.err.log"
    Remove-Item -LiteralPath $out, $err -ErrorAction SilentlyContinue

    if ($Graphical) {
        $args += @("-display", "sdl", "-serial", "file:qemu-run.out.log")
    } else {
        $args += @("-nographic", "-monitor", "none", "-serial", "file:qemu-run.out.log")
    }

    $process = Start-Process -FilePath $qemu -ArgumentList $args -WorkingDirectory $root -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds $Seconds
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }

    Write-Host "--- stdout ---"
    Get-Content -Path $out -ErrorAction SilentlyContinue
    Write-Host "--- stderr ---"
    Get-Content -Path $err -ErrorAction SilentlyContinue
} else {
    if ($Graphical) {
        $args += @("-display", "sdl", "-serial", "stdio")
    } else {
        $args += @("-nographic", "-serial", "stdio")
    }

    & $qemu @args
}
