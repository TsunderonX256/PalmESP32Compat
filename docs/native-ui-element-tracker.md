# Native Palm UI Element Tracker

This file tracks the native ESP32 reimplementation of Palm OS UI elements.
Use it to avoid rebuilding the same bridge twice.

Scope:

- Target UI is Palm OS 4-and-earlier 68K application UI.
- 68K app code still runs in the emulator.
- Palm OS UI Manager traps are handled by native ESP32 C/C++ shims.
- Native shims should prefer real ROM/PRC resources (`tFRM`, `NFNT`, `MBAR`,
  `Talt`, `tSTR`, bitmaps) over hardcoded Memo-specific layout.

Status meanings:

- `native`: drawn/handled by ESP32 native code with useful Palm-style behavior.
- `resource-backed`: native behavior uses decoded ROM/PRC resource data.
- `partial`: useful for Memo Pad, but important Palm semantics are missing.
- `memo-specific`: still contains Memo Pad assumptions that should be removed.
- `stub`: recognized but not functionally implemented yet.

## Current Visual Target

The current QEMU visual target is Palm OS 4 Memo Pad list form:

- Inverted `Memo List` title tab.
- Top-right category selector.
- Plain white memo list with ROM-font text.
- Palm-style scrollbar.
- Palm-style `New` button.
- Category popup with `All` and `Unfiled`.

QEMU visual smoke outputs:

```text
out/qemu-visual-smoke/memo-list.bmp
out/qemu-visual-smoke/category-popup.bmp
```

## Resource Bridge

| Resource | Status | Native owner | Notes |
| --- | --- | --- | --- |
| `tFRM` form resources | resource-backed, partial | `fakeFormDecodeFromResource`, Form Manager shim | Decodes object list, object type, id, bounds, style/group/font metadata, popup links, table rows/columns, and scrollbar metadata. Exact per-object binary layouts still need validation with more apps. |
| `NFNT` font resources | resource-backed, partial | generated font resources, `palm_display.cpp` font renderer | Native text rendering prefers valid ROM font data, then falls back to synthetic glyphs. Metrics still simplified. |
| `MBAR` menu resources | partial | Menu Manager shim | Menu resource discovery exists; active fake menu pointer/state exists. Real menu item drawing/command dispatch is not done. |
| `Talt` alert resources | partial | Dialog/Form Alert shim | Native OK modal exists, but full alert resource parsing/button layout is not complete. |
| `tSTR` strings | partial | Resource loader/string shims | Exported and discoverable; not yet a general UI text source for all managers. |
| Bitmap resources | partial | `WinDrawBitmap` native path | Uncompressed 1bpp resources can draw through locked resource handles. More bitmap encodings need support. |

## Native UI Elements

| Palm UI element | Status | Resource-backed? | Native owner | What works now | Remaining work |
| --- | --- | --- | --- | --- | --- |
| Form shell | partial | yes | `FrmInitForm`, `FrmDrawForm`, `renderMemoPadUiSurface` | Allocates fake `FormPtr`, decodes `tFRM`, publishes object bounds, draws Memo list form natively. | Make `FrmDrawForm` walk generic object classes directly instead of using Memo list-mode branches. |
| Title/tab | native, memo-specific | partly | `drawPalmTitleTab` | Palm OS 4-like inverted `Memo List` tab in QEMU visual smoke. | Derive exact title/tab style from decoded form/title resources where possible. |
| Category selector | native, resource-backed, partial | yes | `palmDisplayPalmUiSetCategoryBounds`, `drawPalmCategoryHeader` | Uses decoded trigger bounds and native tap handling; shows `All`. | Support full category list state, category DB metadata, and more selector styles. |
| Popup category list | native, partial | yes | `fakeFormLinkPopupMetadata`, `drawPalmCategoryPopupList` | Tapping category trigger opens native `All` / `Unfiled` popup using decoded hidden list bounds. | Use real list choices/count/categories and Palm selection semantics. |
| Memo list/table | native, partial | yes | Table/List Manager shim, `palmDisplayMemoListDraw` | Record rows draw in Palm-style list area; table callback path can render row text. | Remove synthetic row source, use real Data Manager/category/sort state, improve table column/row drawing. |
| Scrollbar | native, partial | yes | `Scl*` shim, `drawPalmScrollbar` | Native Palm-style scrollbar drawn at decoded/aliased list edge. | Use real scrollbar object values/ranges/page size for all forms, not just Memo list. |
| Push buttons | native, partial | yes | Control Manager shim, `drawPalmButton` | `New`, `Details`, dialog `OK`, edit `Done/Cancel` draw and handle taps. | Support all Palm control styles, disabled/selected states, groups, repeating controls, and exact OS 4 bevel metrics. |
| Field/edit box | partial | partly | Field Manager shim, edit-mode renderer | Text buffer, insert/delete, dirty state, and native edit screen smoke path exist. | Render real field object bounds/styles, caret, selection, scrolling, max chars, and database commit behavior. |
| Modal dialog/alert | partial | partly | `FrmAlert`, `FrmDoDialog`, `FrmCustomAlert`, `drawPalmModalFrame` | Native OK modal can appear and close from tap. | Parse real alert/dialog resources, multiple buttons, icons, default button, and exact layout. |
| Menus | stub/partial | partly | Menu Manager shim | Active fake menu state and safe pass-through behavior exist. | Draw real menu bar/menu items and dispatch menu commands. |
| Window frame/clip | native, partial | no | Window/Drawing shim | Fake windows, active/draw handles, display extent, clip rectangle, basic frame/drawing paths. | Full window stack, save/restore bits, clipping behavior, draw modes, and offscreen windows. |
| Text drawing | native, resource-backed, partial | yes | `WinDrawChars`, font renderer | ROM `NFNT` text can render into LCD surface with clipping. | Complete metrics, font selection, styles, gray/invert modes, and all character ranges. |
| Bitmap drawing | partial | yes | `WinDrawBitmap` | Basic uncompressed 1bpp Palm bitmap resources draw. | Support more bitmap versions, compression, density/depth, masks, and draw modes. |

## Manager Contract Checklist

Use this checklist when changing UI behavior:

- `FrmInitForm` should decode resource objects and create stable fake pointers.
- `FrmGetObject*` APIs should report the same object metadata that drawing uses.
- `FrmDrawForm` should ask native managers to draw object classes, not duplicate
  manager state in a separate Memo-only renderer.
- `Ctl*`, `Lst*`, `Tbl*`, `Fld*`, `Scl*`, `Menu*`, and `Win*` state should be
  the source of truth for hit testing and drawing.
- Native drawing should use ROM fonts/resources where available.
- QEMU visual smoke should pass after every UI-manager change.

## Implementation Plan To Milestone

Milestone goal:

Memo Pad should look and behave like Palm OS 4 Memo Pad while its Palm OS UI
Managers are reimplemented in ESP32 native code. The 68K app should request UI
work through normal Palm traps; native ESP32 Manager shims should own the UI
objects, state, rendering, input dispatch, and resource usage.

### Phase 1: Resource-Owned Form Model

Goal: `tFRM` decoding becomes the source of truth for the visible form.

Tasks:

- Decode every `tFRM` object class needed by Memo list and edit forms.
- Preserve object type, id, index, bounds, flags, style, group, font, popup
  links, table metadata, scrollbar metadata, and label/resource text.
- Keep fake object pointers stable and make `FrmGetObject*` return the same
  metadata used by drawing and hit testing.
- Remove any duplicated hardcoded bounds when a decoded resource bound exists.

Exit criteria:

- Memo list form can be recreated from decoded `tFRM` object data after
  `FrmInitForm`.
- QEMU visual smoke still shows Memo list and category popup correctly.
- This tracker marks each decoded object class as resource-backed or lists the
  exact missing layout fields.

### Phase 2: Generic `FrmDrawForm` Dispatch

Goal: `FrmDrawForm` walks decoded form objects and calls native Manager draw
shims instead of rendering a Memo-only surface directly.

Tasks:

- Add a generic native draw path for object classes:
  controls, lists, tables, fields, scrollbars, labels, bitmap objects, lines,
  frames, rectangles, gadgets/placeholders.
- Route each object class to its Manager owner:
  `Ctl*`, `Lst*`, `Tbl*`, `Fld*`, `Scl*`, `Win*`.
- Keep the current Memo renderer only as a temporary fallback, with comments
  showing which missing Manager behavior still needs it.
- Make hit testing use Manager object state instead of separate Memo branches.

Exit criteria:

- `FrmDrawForm` render order follows the decoded `tFRM` object order.
- Hiding/showing/moving objects affects native drawing without separate Memo
  layout edits.
- QEMU visual smoke passes.

### Phase 3: Control, List, Table, Scrollbar Fidelity

Goal: Memo list screen is owned by Palm-style Manager state.

Tasks:

- Make Control Manager draw all Memo controls from control object metadata:
  label, style, enabled, usable, selected/pressed, group.
- Make Table Manager own row count, row ids, row heights, usable flags,
  columns, callbacks, selection, and scroll mapping.
- Make List Manager own popup list choices, selection, visible row count, and
  top item.
- Make Scrollbar Manager own value/min/max/page size and draw from that state.
- Replace hardcoded category popup choices with category/database metadata.

Exit criteria:

- Memo list rows, category selector, popup, scrollbar, and `New` button are
  all drawn from Manager state.
- Synthetic memo rows are only a database seed, not a UI renderer shortcut.
- QEMU visual smoke list/popup snapshots stay stable.

### Phase 4: Field/Edit Form

Goal: New/edit memo screen is rendered through Form + Field + Control Managers.

Tasks:

- Decode Memo edit form `tFRM` and field object metadata.
- Make Field Manager own text pointer/handle, insertion point, selection,
  scroll position, max chars, dirty state, usable/visible state, and redraw.
- Draw caret, selected text, field frame/background, and text clipping natively.
- Commit edited text through Data Manager record APIs.
- Draw `Done`, `Cancel`, and related edit controls through Control Manager.

Exit criteria:

- Tapping `New`, entering text, and tapping `Done` creates a real editable
  memo record through Palm-style APIs.
- Edit screen visual smoke snapshot exists and passes.

### Phase 5: Menus, Alerts, Windows, Drawing Completeness

Goal: common Palm OS UI infrastructure is reusable beyond Memo.

Tasks:

- Parse and draw `MBAR` menu bars/items; dispatch menu commands.
- Parse `Talt` alerts/dialog resources for layout, title, buttons, and text.
- Expand Window Manager: window stack, save/restore bits, offscreen windows,
  clipping, draw modes, inversion, gray patterns.
- Expand bitmap support for more Palm bitmap versions and compression.
- Improve ROM font metrics and all text measurement paths.

Exit criteria:

- Memo menu and alert/dialog paths no longer rely on placeholder UI.
- Native UI element table marks menus/dialogs/windows as partial/native with
  concrete verified behavior.

### Phase 6: Regression Harness

Goal: visual/API regressions are caught automatically.

Tasks:

- Add expected-image comparison for QEMU visual smoke BMPs.
- Add snapshots for Memo list, category popup, edit screen, dialog, and menu.
- Keep a small trap/event log for each smoke path.
- Fail the helper when meaningful pixel differences or command timeouts occur.

Exit criteria:

- One command can rebuild/run QEMU and verify all milestone UI states.
- Changes to UI Managers are checked against stable snapshots before commit.

## Tracker Workflow

Before changing UI code:

- Read this tracker section for the affected UI element.
- Check `docs/native-esp32-trap-reference.md` for the related trap status.
- Identify whether the change should remove a Memo-specific assumption or add
  a new resource-backed/native Manager behavior.

During implementation:

- Prefer resource data and Manager state over hardcoded Memo layout.
- Keep new fallbacks narrow and mark them as temporary in this tracker.
- Run QEMU visual smoke after any visible UI change.

After implementation:

- Update the relevant row in `Native UI Elements`.
- Update `Known Memo-Specific Assumptions To Remove` if one was removed or a
  new temporary shortcut was added.
- Add the exact verified behavior to the tracker or trap reference.
- Do not call a piece `native` unless it has useful ESP32-side behavior and a
  passing smoke path or equivalent test.

## Known Memo-Specific Assumptions To Remove

- `renderMemoPadUiSurface` still has list/edit mode branches specific to Memo.
- Synthetic memo rows still seed the visible list before real database/category
  persistence is complete.
- `kPalmMemoTableId`, `kPalmMemoNewButtonId`, and related aliases bridge Memo
  resource ids into generic-looking renderer ids.
- Category popup choices are still hardcoded to `All` and `Unfiled`.
- Edit form is still a native smoke path, not fully driven by decoded field
  resources and `Fld*` state.

## Next Best Steps

1. Make `FrmDrawForm` walk decoded `tFRM` objects and dispatch each object to
   its manager draw shim (`CtlDrawControl`, `TblDrawTable`, `FldDrawField`,
   `SclDrawScrollBar`, labels/bitmaps/frames).
2. Move list/table row drawing fully behind Table Manager state, with Data
   Manager/category records as the row source.
3. Make Field Manager own the edit form visual state: bounds, text, caret,
   selection, dirty flag, and commit path.
4. Replace hardcoded category popup choices with real category metadata.
5. Add pixel-diff comparison for QEMU visual smoke snapshots once the intended
   Palm OS 4 target images are stable.
