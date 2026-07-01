# ESP32-S3 QEMU Setup

This folder contains the local Espressif QEMU setup used for PalmESP32Compat
smoke testing.

Installed QEMU:

```text
install/qemu/bin/qemu-system-xtensa.exe
```

Downloaded release:

```text
qemu-xtensa-softmmu-esp_develop_9.2.2_20260417-x86_64-w64-mingw32.tar.xz
```

The archive SHA256 was checked against Espressif's release checksum file before
extraction.

## Build a Flash Image

```powershell
powershell -ExecutionPolicy Bypass -File .\make-memo-flash-image.ps1 `
  -BuildDir "C:\Users\doubl\Documents\PalmESP32Compat\out\PalmOS4MemoPadSmokeQemu\.pio\build\esp32-palm-m505-qemu" `
  -OutputImage ".\images\PalmOS4MemoPadSmoke-qemu-headless.bin"
```

This merges bootloader, partition table, boot app, firmware, and `spiffs.bin`
from the QEMU/headless PlatformIO output:

```text
C:\Users\doubl\Documents\PalmESP32Compat\out\PalmOS4MemoPadSmokeQemu\.pio\build\esp32-palm-m505-qemu
```

into:

```text
images\PalmOS4MemoPadSmoke-qemu-headless.bin
```

## Run QEMU

```powershell
powershell -ExecutionPolicy Bypass -File .\run-memo-qemu.ps1 -Seconds 6
```

The timed mode writes UART output to `qemu-run.out.log` through QEMU's serial
file backend. A passing smoke run should show:

```text
Hardware profile: esp32-palm-m505-qemu
SPIFFS mounted
Resident Palm apps: 1
LCD headless framebuffer initialized for QEMU
tFRM object table: count=7
FrmInitForm formId=1000
```

## TCP Command Channel

QEMU can expose the firmware UART as a local TCP socket. Start QEMU in one
terminal:

```powershell
powershell -ExecutionPolicy Bypass -File .\run-memo-qemu.ps1 -SerialTcpPort 5555
```

The TCP socket waits for a client before booting, so run the VB.NET tool from a
second terminal. `--connect-wait-ms` keeps the socket drained while ESP32 boots,
then sends the command:

```powershell
dotnet run --project ..\vbnet\PalmEsp32RomTool -- snapshot-tcp 127.0.0.1 5555 ..\..\out\qemu-lcd.bmp --connect-wait-ms 15000
dotnet run --project ..\vbnet\PalmEsp32RomTool -- tap-snapshot-tcp 127.0.0.1 5555 145 6 ..\..\out\qemu-category-popup.bmp --connect-wait-ms 15000
```

The first command saves the current 160x160 LCD framebuffer as a scaled BMP.
The second sends a Palm tap first, waits for `PALM_LCD_TAP_QUEUED`, then saves
the updated framebuffer.

## Automated Visual Smoke

Use the visual smoke helper to run both TCP checks in one command:

```powershell
powershell -ExecutionPolicy Bypass -File .\run-memo-visual-smoke.ps1
```

By default it builds the VB.NET tool, starts QEMU, captures `memo-list.bmp`,
restarts QEMU, taps the category selector, captures `category-popup.bmp`, and
stops QEMU. The images are written to:

```text
out/qemu-visual-smoke/
```

Useful options:

```powershell
powershell -ExecutionPolicy Bypass -File .\run-memo-visual-smoke.ps1 -NoBuild
powershell -ExecutionPolicy Bypass -File .\run-memo-visual-smoke.ps1 -Port 5556 -ConnectWaitMs 15000
powershell -ExecutionPolicy Bypass -File .\run-memo-visual-smoke.ps1 `
  -QemuPath "C:\Users\doubl\Documents\ESP PALM\tools\qemu-esp32\install\qemu\bin\qemu-system-xtensa.exe" `
  -ImagePath "C:\Users\doubl\Documents\ESP PALM\tools\qemu-esp32\images\PalmOS4MemoPadSmoke-qemu-headless.bin"
```

## Current Firmware Status

The generated `esp32-palm-m505-qemu` profile now boots under ESP32-S3 QEMU,
mounts SPIFFS, loads the Memo Pad PRC, starts the 68K runtime, decodes the Memo
Pad `tFRM #1000`, and reaches the headless native LCD/UI path. The headless
profile:

- skips real RGB panel and backlight setup
- avoids physical-board PSRAM assumptions
- keeps the in-memory framebuffer used by native LCD drawing
- keeps UART `lcdsnap`, `tap`, and `text` command handlers compiled in

Remaining QEMU work: compare generated BMP/PNG artifacts against expected
images and fail the visual smoke on meaningful pixel differences.
