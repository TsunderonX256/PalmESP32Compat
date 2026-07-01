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

## Current Firmware Status

The generated `esp32-palm-m505-qemu` profile now boots under ESP32-S3 QEMU,
mounts SPIFFS, loads the Memo Pad PRC, starts the 68K runtime, decodes the Memo
Pad `tFRM #1000`, and reaches the headless native LCD/UI path. The headless
profile:

- skips real RGB panel and backlight setup
- avoids physical-board PSRAM assumptions
- keeps the in-memory framebuffer used by native LCD drawing
- keeps UART `lcdsnap`, `tap`, and `text` command handlers compiled in

Remaining QEMU work: expose an easy host-side way to send UART commands such as
`lcdsnap` and `tap` into the running emulator, then convert the framebuffer into
automatic BMP/PNG test artifacts.
