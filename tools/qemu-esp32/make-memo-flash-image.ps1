param(
    [string]$BuildDir = "C:\Users\doubl\Documents\PalmESP32Compat\out\PalmOS4MemoPadSmoke\.pio\build\esp32-palm-m505",
    [string]$OutputImage = "$(Split-Path -Parent $PSCommandPath)\images\PalmOS4MemoPadSmoke-esp32s3-flash.bin",
    [string]$FlashSize = "8MB",
    [string]$SpiffsOffset = "0x670000"
)

$ErrorActionPreference = "Stop"

$python = "C:\Users\doubl\.platformio\penv\Scripts\python.exe"
$esptool = "C:\Users\doubl\.platformio\packages\tool-esptoolpy\esptool.py"
$bootApp = "C:\Users\doubl\.platformio\packages\framework-arduinoespressif32\tools\partitions\boot_app0.bin"

$bootloader = Join-Path $BuildDir "bootloader.bin"
$partitions = Join-Path $BuildDir "partitions.bin"
$firmware = Join-Path $BuildDir "firmware.bin"
$spiffs = Join-Path $BuildDir "spiffs.bin"

foreach ($path in @($python, $esptool, $bootApp, $bootloader, $partitions, $firmware)) {
    if (-not (Test-Path $path)) {
        throw "Missing required file: $path"
    }
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputImage) | Out-Null

$mergeArgs = @(
    $esptool,
    "--chip", "esp32s3",
    "merge_bin",
    "--fill-flash-size", $FlashSize,
    "-o", $OutputImage,
    "0x0", $bootloader,
    "0x8000", $partitions,
    "0xe000", $bootApp,
    "0x10000", $firmware
)

if (Test-Path $spiffs) {
    $mergeArgs += @($SpiffsOffset, $spiffs)
}

& $python @mergeArgs

Get-Item $OutputImage
