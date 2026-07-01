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

## Real Palm OS UI Milestone

This milestone tracks what is still needed before ROM-defined Palm OS UI
elements are reliably owned by native ESP32 code instead of Memo-specific
shortcuts.

- Complete exact `tFRM` object decoding for each Palm form object class. The
  bridge now preserves standard object types beyond Memo's core controls, but
  exact per-class resource layouts still need validation against more apps.
- Make the native object model match Palm managers more closely for forms,
  controls, lists, tables, fields, scrollbars, menus, and windows. Fake object
  records now preserve type/index/flags/style metadata, but full pointer/state
  lifetimes expected by 68K app code still need broader manager behavior.
- Make `FrmDispatchEvent`, `FrmHandleEvent`, object handlers, and form callback
  return paths behave more like Palm OS so app handlers drive UI behavior.
- Improve Palm-style drawing accuracy for control frames, popup/list borders,
  scrollbars, clipping, inversion, selected/disabled states, modal frames, and
  text alignment.
- Use ROM/PRC resources more fully, including fonts, bitmaps, forms, menus,
  alerts, and strings where available.
- Move field and text editing toward real `Fld*` manager behavior, including
  insertion, deletion, selection, caret state, dirty state, and database commit.
- Expand database and category behavior for record categories, sorting, unique
  ids, dirty/archive/delete flags, and persistence.
- Expand the QEMU/headless ESP32 test rig from boot/log smoke tests into
  host-driven framebuffer snapshots and tap/text interaction tests.

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
  native-ui-element-tracker.md
                   Native Palm UI element/resource implementation tracker
```

## VB.NET Converter

The first tool lives at `tools/vbnet/PalmEsp32RomTool`.

```text
dotnet run --project tools/vbnet/PalmEsp32RomTool -- inspect <rom-file>
dotnet run --project tools/vbnet/PalmEsp32RomTool -- generate <rom-file> <output-dir> --all
dotnet run --project tools/vbnet/PalmEsp32RomTool -- generate <rom-file> out/PalmOS4MemoPadSmokeQemu --app-name "Memo Pad" --hardware-profile esp32-palm-m505-qemu
dotnet run --project tools/vbnet/PalmEsp32RomTool -- trap-map --markdown
dotnet run --project tools/vbnet/PalmEsp32RomTool -- snapshot COM4 out/palm-lcd.bmp
dotnet run --project tools/vbnet/PalmEsp32RomTool -- tap-snapshot COM4 145 6 out/category-popup.bmp
dotnet run --project tools/vbnet/PalmEsp32RomTool -- snapshot-tcp 127.0.0.1 5555 out/qemu-lcd.bmp --connect-wait-ms 15000
dotnet run --project tools/vbnet/PalmEsp32RomTool -- tap-snapshot-tcp 127.0.0.1 5555 145 6 out/qemu-category-popup.bmp --connect-wait-ms 15000
dotnet run --project tools/vbnet/PalmEsp32RomTool -- tap COM4 24 92
dotnet run --project tools/vbnet/PalmEsp32RomTool -- tap COM4 92 148
dotnet run --project tools/vbnet/PalmEsp32RomTool -- text COM4 "New memo from UART"
```

The generated ESP32 project currently contains:

- a PlatformIO project skeleton
- exported installable PRC files from the ROM
- generated C++ app manifest
- placeholder runtime entry points for 68K emulation and native Palm trap dispatch

For emulator-first testing, generate with `--hardware-profile
esp32-palm-m505-qemu`, build with PlatformIO, run `platformio run -t buildfs`,
merge the firmware and SPIFFS images with `tools/qemu-esp32`, then run the
timed QEMU smoke. The current headless profile skips real RGB panel/backlight
setup, avoids physical-board PSRAM assumptions, and reaches Memo Pad's
headless native LCD/UI path under ESP32-S3 QEMU.

The Memo Pad smoke path now includes a native ESP32 Palm API shim for the fake
Memo database, Palm-like UI drawing, 25% default backlight when the LCD is in
use, a `lcdsnap` UART command that the VB.NET `snapshot` and `snapshot-tcp`
commands save as a BMP, a `tap x y` UART command that queues Palm pen events
and updates the native Memo UI probe, `tap-snapshot` and `tap-snapshot-tcp`
commands for one-session visual checks on boards and QEMU, and a `text ...`
UART command for filling the current native edit field. Tapping `Details` opens a first native
Palm-style modal dialog; tapping its `OK` button closes it. Tapping `New`,
sending text, and tapping `Done` prepends a new synthetic memo record for the
current runtime.
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
