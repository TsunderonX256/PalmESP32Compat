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
  native-esp32-trap-reference.md
                   Native ESP32 trap/function reference for verified shims
```

## VB.NET Converter

The first tool lives at `tools/vbnet/PalmEsp32RomTool`.

```text
dotnet run --project tools/vbnet/PalmEsp32RomTool -- inspect <rom-file>
dotnet run --project tools/vbnet/PalmEsp32RomTool -- generate <rom-file> <output-dir> --all
dotnet run --project tools/vbnet/PalmEsp32RomTool -- trap-map --markdown
dotnet run --project tools/vbnet/PalmEsp32RomTool -- snapshot COM4 out/palm-lcd.bmp
dotnet run --project tools/vbnet/PalmEsp32RomTool -- tap COM4 24 92
dotnet run --project tools/vbnet/PalmEsp32RomTool -- tap COM4 92 148
dotnet run --project tools/vbnet/PalmEsp32RomTool -- text COM4 "New memo from UART"
```

The generated ESP32 project currently contains:

- a PlatformIO project skeleton
- exported installable PRC files from the ROM
- generated C++ app manifest
- placeholder runtime entry points for 68K emulation and native Palm trap dispatch

The Memo Pad smoke path now includes a native ESP32 Palm API shim for the fake
Memo database, Palm-like UI drawing, 25% default backlight when the LCD is in
use, a `lcdsnap` UART command that the VB.NET `snapshot` command saves as a
BMP, a `tap x y` UART command that queues Palm pen events and updates the
native Memo UI probe, and a `text ...` UART command for filling the current
native edit field. Tapping `Details` opens a first native Palm-style modal
dialog; tapping its `OK` button closes it. Tapping `New`, sending text, and
tapping `Done` prepends a new synthetic memo record for the current runtime.
The Data Manager shim now also has an initial writable record path for
`DmNewRecord`, `DmResizeRecord`, `DmWrite`, `DmStrCopy`, and dirty
`DmReleaseRecord`. The Field Manager shim has a first active-field text buffer
for `FldSetText*`, `FldGetText*`, `FldInsert`, `FldDelete`, and dirty state.
The memory shim now includes a small handle table over an 8 KB generated trap
heap for `MemHandle*`, `MemPtr*`, and `DmNewHandle` smoke paths. Freed blocks
are coalesced and reused, pointer-to-handle recovery is supported, and legacy
fixed scratch handles still bridge early Memo Pad database, resource, and field
buffers. Synthetic Memo records now keep per-record dynamic backing handles, so
`DmQueryRecord`, `DmGetRecord`, `DmResizeRecord`, `DmWrite`, `DmStrCopy`, and
dirty `DmReleaseRecord` exercise the same heap path the Palm app expects.
The Form Manager shim now allocates native fake `FormPtr` values and stable
object pointers for `FrmInitForm`, `FrmDrawForm`, `FrmGetObjectIndex`, and
`FrmGetObjectPtr`, plus active-form, object count/id/type, focus, position, and
bounds APIs, which is the first bridge toward replacing the hardcoded Memo
mirror with Palm-style form/control/list handling.
The Control Manager shim now tracks fake Memo button state for `CtlDrawControl`,
`CtlGetValue`, `CtlSetValue`, label get/set, hit handling, and enabled/usable
state, so button drawing and taps can flow through Palm-style control pointers.
The List Manager shim now handles the Memo list object for `LstDrawList`,
`LstGetSelection`, `LstSetSelection`, `LstGetSelectionText`, `LstHandleEvent`,
`LstGetNumberOfItems`, scroll-window tracking, and related setup calls.
The Menu Manager shim now tracks a fake active `MenuBarType` for `MenuInit`,
`MenuDispose`, `MenuGetActiveMenu`, `MenuSetActiveMenu`, `MenuDrawMenu`,
`MenuEraseStatus`, and safe pass-through `MenuHandleEvent` behavior.
The Window/Drawing shim now provides stable fake window handles, display/window
extent and clip rectangle APIs, save/restore-bits placeholders, and native
monochrome drawing for basic lines, rectangles, frames, character output, and
uncompressed 1bpp Palm bitmap resources passed through `WinDrawBitmap`. The
generated PRC loader also reads ROM overlay catalogs, so hidden `tFRM`, `MBAR`,
`Talt`, and `tSTR` resources are reported separately from directly loadable
resources while the UI-resource extraction path is expanded. The native Memo UI
font renderer now has broader Palm-style glyph coverage plus clipped text
drawing for titles, list rows, category selectors, buttons, fields, and dialogs.
Generated firmware also exports direct `NFNT` resources from the ROM and prefers
valid System/Latin Palm fonts for native text rendering before falling back to
the built-in synthetic glyphs. Native chrome drawing now uses reusable
Palm-style raised/inset frames for buttons, popup selectors, scrollbars, edit
fields, list panes, and modal dialogs. The form bridge is now overlay-catalog
aware: `FrmInitForm` reports cataloged `tFRM` matches and publishes fake form
object bounds into the native renderer, so list/button drawing and tap hit areas
flow through Form Manager geometry instead of separate hardcoded rectangles.

Palm OS 4 smoke-test notes are tracked in `docs/os4-test-results.md`, and the
current selector/API coverage is tracked in `docs/trap-map.md`.
