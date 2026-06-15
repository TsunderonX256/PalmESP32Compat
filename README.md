# PalmESP32Compat

Standalone experiment for running Palm OS 4-and-earlier 68K applications on ESP32 using a hybrid runtime:

- Original Palm application code executes inside a Motorola 68K emulator.
- Palm OS traps are intercepted and implemented as native ESP32 C/C++ services.
- Display, input, storage, timers, and serial I/O are bridged to ESP32 hardware.

This repo is intentionally separate from the full-ROM emulator so behavior can be compared side by side.

## Initial Goals

- Load PRC/PDB/resource databases from storage.
- Execute 68K application code through an emulator core.
- Dispatch Palm OS trap calls to native compatibility functions.
- Implement enough memory, event, UI, and database APIs to run simple Palm OS 3.x/4.x apps.
- Build comparison tests against the original emulator using shared event traces and framebuffer output.

## Repository Layout

```text
src/
  cpu68k/          68K emulator wrapper and trap interception boundary
  palm_api/        Native implementations of Palm OS traps
  palm_memory/     Palm-style heaps, chunks, handles, resources
  palm_ui/         Windows, forms, controls, fonts, drawing, events
  platform_esp32/  ESP32 display, touch/buttons, storage, timers
tools/
  vbnet/           Standalone VB.NET ROM/PRC/PDB analysis and generator tools
tests/
  trap_tests/      Focused behavior tests for Palm OS trap implementations
  fixtures/        Small PRC/resource fixtures and event traces
docs/
  architecture.md  Runtime design notes
  trap-map.md      Palm OS trap coverage tracker
```

## VB.NET Converter

The first tool lives at `tools/vbnet/PalmEsp32RomTool`.

```text
dotnet run --project tools/vbnet/PalmEsp32RomTool -- inspect <rom-file>
dotnet run --project tools/vbnet/PalmEsp32RomTool -- generate <rom-file> <output-dir> --all
```

The generated ESP32 project currently contains:

- a PlatformIO project skeleton
- exported installable PRC files from the ROM
- generated C++ app manifest
- placeholder runtime entry points for 68K emulation and native Palm trap dispatch
