# Native ESP32 Palm API Reference

This file tracks Palm OS APIs that currently have useful native ESP32 behavior
in the generated compatibility firmware. It is intentionally narrower than
`docs/trap-map.md`: entries here should mean "the app can depend on this path
for the current Memo Pad smoke target", not merely "the trap is recognized".

Status meanings:

- `native`: implemented as ESP32-side behavior with visible/runtime effect.
- `partial native`: useful native behavior exists, but important Palm semantics
  are still simplified.
- `shim`: enough compatibility behavior for Memo Pad startup/runtime, but not a
  full manager implementation yet.

## Verified Runtime Surface

| Palm area | Native ESP32 owner | Verified behavior |
| --- | --- | --- |
| 68K app launch | `palm_68k_runtime.cpp`, generated app manifest | Memo Pad PRC launches from the converted ROM output. |
| Trap dispatch | `palm_traps.cpp` generated from `Esp32ProjectGenerator.vb` | Palm OS traps are intercepted and routed to native C/C++ handlers. |
| LCD renderer | `palm_display.cpp` generated from `Esp32ProjectGenerator.vb` | 160x160 Palm LCD surface draws on the ESP32 panel and can be captured over UART with `lcdsnap`. |
| UART test input | `palm_display.cpp`, `PalmEsp32RomTool` | `tap x y`, `text ...`, `lcdsnap`, and one-session `tap-snapshot` checks exercise the native UI surface. |
| Memo list form | `fakeFormDecodeFromResource`, `renderMemoPadUiSurface` | Real ROM `tFRM #1000` geometry is decoded for title, category trigger, hidden category list, table, scrollbar, and New button. |
| Category popup | `fakeFormLinkPopupMetadata`, `drawPalmCategoryPopupList`, `palmDisplayPalmUiHandleTap` | Tapping the real `All` trigger draws native `All` / `Unfiled` popup using the decoded hidden list bounds, and updates selector text. |
| Memo table callback | Table Manager shim plus native draw overlay | Real Memo row draw callback renders record titles into the native table area. |

## Native Trap Table

### Memory Manager

| Trap | Palm function | Status | Native behavior |
| --- | --- | --- | --- |
| `0xA012` | `MemChunkFree` | partial native | Frees generated trap-heap pointer blocks; accepts legacy scratch pointers. |
| `0xA013` | `MemPtrNew` | native | Allocates pointer-backed blocks from the generated trap heap. |
| `0xA014` | `MemPtrRecoverHandle` | native | Recovers dynamic fake handles from pointers. |
| `0xA016` | `MemPtrSize` | native | Reports dynamic pointer size, including interior-pointer remaining bytes. |
| `0xA01C` | `MemPtrResize` | native | Resizes pointer-backed blocks through their recovered handles. |
| `0xA01E` | `MemHandleNew` | native | Allocates fake `MemHandle` records with pointer, size, and lock metadata. |
| `0xA01F` | `MemHandleLockCount` | native | Reports tracked lock counts. |
| `0xA021` | `MemHandleLock` | native | Maps fake handles to trap-heap pointers and increments lock count. |
| `0xA022` | `MemHandleUnlock` | native | Decrements tracked lock counts. |
| `0xA02B` | `MemHandleFree` | native | Frees dynamic handles and returns backing blocks to the reusable free list. |
| `0xA02D` | `MemHandleSize` | native | Reports dynamic handle size. |
| `0xA033` | `MemHandleResize` | native | Resizes dynamic handles, moving backing storage when needed. |
| `0xA035` | `MemPtrUnlock` | native | Recovers the handle for a pointer and unlocks it. |

### Database Manager

| Trap | Palm function | Status | Native behavior |
| --- | --- | --- | --- |
| `0xA041` | `DmCreateDatabase` | partial native | Creates the synthetic Memo database when Memo Pad asks for `DATA/memo`. |
| `0xA04A` | `DmCloseDatabase` | shim | Accepts close for the synthetic Memo database. |
| `0xA04F` | `DmNumRecords` | native | Returns current synthetic Memo record count. |
| `0xA050` | `DmRecordInfo` | native | Writes attributes, unique id, and dynamic backing handle. |
| `0xA051` | `DmSetRecordInfo` | partial native | Accepts record metadata updates; category/dirty persistence is simplified. |
| `0xA055` | `DmNewRecord` | native | Inserts an empty dynamic Memo record. |
| `0xA056` | `DmRemoveRecord` | native | Removes a Memo record and frees its dynamic backing handle. |
| `0xA057` | `DmDeleteRecord` | partial native | Removes a Memo record; sync delete flags are not modeled. |
| `0xA058` | `DmArchiveRecord` | partial native | Removes a Memo record; archive flags are not modeled. |
| `0xA05B` | `DmQueryRecord` | native | Returns the requested Memo record handle. |
| `0xA05C` | `DmGetRecord` | native | Returns the requested Memo record handle for editing. |
| `0xA05D` | `DmResizeRecord` | native | Resizes a Memo record backing handle. |
| `0xA05E` | `DmReleaseRecord` | partial native | Commits dirty dynamic Memo record text back to native display storage. |
| `0xA071` | `DmNumRecordsInCategory` | partial native | Returns synthetic Memo count and republishes visible list rows. |
| `0xA075` | `DmOpenDatabaseByTypeCreator` | partial native | Opens the synthetic `DATA/memo` database after create/init path. |
| `0xA076` | `DmWrite` | native | Writes bytes into a dynamic Memo record pointer and commits text. |
| `0xA077` | `DmStrCopy` | native | Copies a string into a dynamic Memo record pointer and commits text. |
| `0xA079` | `DmWriteCheck` | shim | Allows writes while bounds are enforced by native buffers. |

### Resource Manager

| Trap | Palm function | Status | Native behavior |
| --- | --- | --- | --- |
| `0xA05F` | `DmGetResource` | partial native | Loads exported resources, including ROM form/font/bitmap bytes used by the native bridge. |
| `0xA060` | `DmGet1Resource` | partial native | Uses the same exported-resource lookup path as `DmGetResource`. |
| `0xA061` | `DmReleaseResource` | shim | Accepts release of fake resource handles. |

### Form Manager

| Trap | Palm function | Status | Native behavior |
| --- | --- | --- | --- |
| `0xA16F` | `FrmInitForm` | partial native | Allocates fake `FormPtr`, decodes known `tFRM` resources, seeds native object metadata, and links popup trigger/list companions. |
| `0xA170` | `FrmDeleteForm` | shim | Clears active fake form state. |
| `0xA171` | `FrmDrawForm` | native | Draws native Memo surface from current form/object bounds and rows. |
| `0xA172` | `FrmEraseForm` | shim | Accepts erase for active fake form. |
| `0xA173` | `FrmGetActiveForm` | native | Returns active fake `FormPtr`. |
| `0xA174` | `FrmSetActiveForm` | shim | Tracks active fake form pointer. |
| `0xA175` | `FrmGetActiveFormID` | native | Returns active fake form resource id. |
| `0xA178` | `FrmGetFocus` | native | Returns tracked fake form focus index. |
| `0xA179` | `FrmSetFocus` | native | Tracks requested focus index. |
| `0xA17B` | `FrmGetFormBounds` | native | Writes 160x160 form bounds. |
| `0xA17D` | `FrmGetFormId` | native | Returns active fake form id. |
| `0xA17E` | `FrmGetFormPtr` | partial native | Returns or initializes fake form pointer for a form id. |
| `0xA17F` | `FrmGetNumberOfObjects` | native | Returns decoded/seeded object count. |
| `0xA180` | `FrmGetObjectIndex` | native | Returns stable object indexes. |
| `0xA181` | `FrmGetObjectId` | native | Returns object id by index. |
| `0xA182` | `FrmGetObjectType` | native | Returns Palm-compatible object type values for known objects. |
| `0xA183` | `FrmGetObjectPtr` | native | Returns stable fake object pointers with id, kind, and bounds metadata. |
| `0xA184` | `FrmHideObject` | native | Marks fake form object hidden. |
| `0xA185` | `FrmShowObject` | native | Marks fake form object visible and redraws known controls. |
| `0xA186` | `FrmGetObjectPosition` | native | Writes tracked object x/y position. |
| `0xA187` | `FrmSetObjectPosition` | native | Updates object position and republishes LCD hit/draw bounds. |
| `0xA188` | `FrmGetControlValue` | native | Returns tracked control value through form object index. |
| `0xA189` | `FrmSetControlValue` | native | Updates tracked control value through form object index. |
| `0xA199` | `FrmGetObjectBounds` | native | Writes tracked object bounds used by LCD renderer/hit testing. |
| `0xA19B` | `FrmGotoForm` | partial native | Updates active form/title signal for the Memo LCD surface. |
| `0xA1A0` | `FrmDispatchEvent` | partial native | Reads select events and mirrors them into native Memo UI state. |

### Dialog/Form Alerts

| Trap | Palm function | Status | Native behavior |
| --- | --- | --- | --- |
| `0xA192` | `FrmAlert` | partial native | Shows native OK modal and returns button 0. |
| `0xA193` | `FrmDoDialog` | partial native | Shows native OK modal and returns synthetic OK control id. |
| `0xA194` | `FrmCustomAlert` | partial native | Shows native OK modal with captured text when available. |

### Control Manager

| Trap | Palm function | Status | Native behavior |
| --- | --- | --- | --- |
| `0xA10D` | `CtlDrawControl` | native | Draws known Memo controls using native Palm-style button/popup renderer. |
| `0xA10E` | `CtlEraseControl` | native | Hides known fake controls and clears pressed state. |
| `0xA10F` | `CtlHideControl` | native | Marks controls hidden and clears pressed state. |
| `0xA110` | `CtlShowControl` | native | Marks controls visible and redraws. |
| `0xA111` | `CtlGetValue` | native | Returns tracked control value. |
| `0xA112` | `CtlSetValue` | native | Updates tracked control value. |
| `0xA113` | `CtlGetLabel` | native | Returns stable pointer to control label text. |
| `0xA114` | `CtlSetLabel` | native | Copies label into native control metadata and redraws. |
| `0xA115` | `CtlHandleEvent` | partial native | Consumes matching control-select events and routes through native Memo tap path. |
| `0xA116` | `CtlHitControl` | partial native | Triggers known fake controls through native Memo tap path. |
| `0xA117` | `CtlSetEnabled` | native | Tracks enabled state. |
| `0xA118` | `CtlSetUsable` | native | Tracks usable/visible state. |
| `0xA119` | `CtlEnabled` | native | Returns tracked enabled state. |

### List and Table Managers

| Trap | Palm function | Status | Native behavior |
| --- | --- | --- | --- |
| `0xA1B0` | `LstSetDrawFunction` | shim | Accepts custom draw callback registration. |
| `0xA1B1` | `LstDrawList` | partial native | Redraws native list from synthetic Memo records. |
| `0xA1B3` | `LstGetSelection` | native | Returns current synthetic selection. |
| `0xA1B4` | `LstGetSelectionText` | native | Returns stable pointer to selected row text. |
| `0xA1B5` | `LstHandleEvent` | partial native | Consumes list-select events and updates native selection. |
| `0xA1B6` | `LstSetHeight` | native | Stores visible item count. |
| `0xA1B7` | `LstSetSelection` | native | Updates selection and redraws. |
| `0xA1B8` | `LstSetListChoices` | partial native | Accepts caller choice arrays while Memo remains record-backed. |
| `0xA1B9` | `LstMakeItemVisible` | native | Updates top row so selected item is visible. |
| `0xA1BA` | `LstGetNumberOfItems` | native | Returns synthetic Memo record count. |
| `0xA1BB` | `LstPopupList` | partial native | Draws popup/list surface and returns current selection. |
| `0xA1BC` | `LstSetPosition` | native | Updates list object bounds metadata. |
| `0xA2B5` | `LstSetTopItem` | native | Updates synthetic list top row and redraws visible rows. |
| `various Tbl*` | `TblGetRowID`, `TblSetRowID`, row usable/height APIs, draw callback APIs | partial native | Supports Memo table population, scroll-end behavior, row ids, usability, and draw callback rows. |

### Field Manager

| Trap | Palm function | Status | Native behavior |
| --- | --- | --- | --- |
| `0xA135` | `FldDrawField` | partial native | Mirrors active field buffer into native Memo edit surface. |
| `0xA139` | `FldGetTextPtr` | native | Returns fake field text pointer. |
| `0xA13F` | `FldSetText` | native | Copies text from fake handle into active field buffer. |
| `0xA14A` | `FldGetTextAllocatedSize` | native | Reports current field buffer size. |
| `0xA14B` | `FldGetTextLength` | native | Returns active field text length. |
| `0xA153` | `FldGetTextHandle` | native | Returns fake field text handle. |
| `0xA155` | `FldDirty` | native | Reports active field dirty state. |
| `0xA158` | `FldSetTextHandle` | native | Copies text from fake `MemHandle`. |
| `0xA159` | `FldSetTextPtr` | native | Copies text from caller pointer. |
| `0xA15D` | `FldInsert` | native | Inserts text at synthetic insertion point and redraws edit surface. |
| `0xA15E` | `FldDelete` | native | Deletes text range and redraws edit surface. |
| `0xA160` | `FldSetDirty` | native | Updates dirty flag. |

### Window, Drawing, and Font Managers

| Trap | Palm function | Status | Native behavior |
| --- | --- | --- | --- |
| `0xA1FC` | `WinSetActiveWindow` | shim | Tracks fake active window handle. |
| `0xA1FD` | `WinSetDrawWindow` | shim | Tracks fake draw window handle. |
| `0xA1FE` | `WinGetDrawWindow` | shim | Returns fake draw window handle. |
| `0xA1FF` | `WinGetActiveWindow` | shim | Returns fake active window handle. |
| `0xA200` | `WinGetDisplayWindow` | shim | Returns stable fake display window handle. |
| `0xA204` | `WinGetWindowFrameRect` | native | Writes 160x160 frame rectangle. |
| `0xA205` | `WinDrawWindowFrame` | native | Draws native 160x160 Palm LCD frame. |
| `0xA206` | `WinEraseWindow` | native | Clears native Palm LCD area. |
| `0xA20B` | `WinGetDisplayExtent` | native | Writes 160x160 extent. |
| `0xA20C` | `WinGetWindowExtent` | native | Writes 160x160 extent. |
| `0xA20F` | `WinGetClip` | native | Returns tracked clip rectangle. |
| `0xA210` | `WinSetClip` | native | Tracks clip rectangle. |
| `0xA211` | `WinResetClip` | native | Resets clip to full 160x160 surface. |
| `0xA212` | `WinClipRectangle` | native | Intersects caller rectangle with clip rectangle. |
| `0xA213` | `WinDrawLine` | native | Draws monochrome native line. |
| `0xA214` | `WinDrawGrayLine` | native | Draws gray native line. |
| `0xA215` | `WinEraseLine` | native | Draws white line. |
| `0xA218` | `WinDrawRectangle` | native | Fills monochrome rectangle. |
| `0xA219` | `WinEraseRectangle` | native | Fills white rectangle. |
| `0xA21B` | `WinDrawRectangleFrame` | native | Draws rectangle frame. |
| `0xA21C` | `WinDrawGrayRectangleFrame` | native | Draws gray rectangle frame. |
| `0xA21D` | `WinEraseRectangleFrame` | native | Draws white rectangle frame. |
| `0xA220` | `WinDrawChars` | native | Draws text using generated Palm bitmap font resources. |
| `0xA221` | `WinEraseChars` | native | Erases text bounds and redraws white text. |
| `0xA226` | `WinDrawBitmap` | partial native | Draws uncompressed 1bpp Palm bitmap resources from locked resource handles. |
| `0xA229` | `WinFillRectangle` | native | Fills monochrome rectangle. |
| `Fnt*` | `FntCharsWidth`, `FntWidthToOffset`, font select/metrics paths | partial native | Uses generated Palm font data for Memo text measurement and clipping. |

### Events, Categories, Menus, Preferences, System

| Trap | Palm function | Status | Native behavior |
| --- | --- | --- | --- |
| `0xA08F` | `SysAppStartup` | shim | Builds minimal launch block for startup glue. |
| `0xA090` | `SysAppExit` | shim | Stops probe run cleanly. |
| `0xA0A9` | `SysHandleEvent` | shim | Returns false while native LCD probe keeps surface alive. |
| `0xA104` | `CategoryGetName` | partial native | Writes `All` or `Unfiled` into caller buffer. |
| `0xA11D` | `EvtGetEvent` | partial native | Dequeues UART/native tap events, otherwise writes nil event. |
| `0xA1BD` | `MenuInit` | shim | Allocates and activates fake menu pointer. |
| `0xA1BE` | `MenuDispose` | shim | Clears active fake menu. |
| `0xA1BF` | `MenuHandleEvent` | shim | Returns false until real command handling is added. |
| `0xA1C0` | `MenuDrawMenu` | shim | Marks fake active menu visible. |
| `0xA1C1` | `MenuEraseStatus` | shim | Marks fake menu hidden. |
| `0xA1C2` | `MenuGetActiveMenu` | shim | Returns fake active menu pointer. |
| `0xA1C3` | `MenuSetActiveMenu` | shim | Replaces fake active menu pointer and returns previous. |
| `0xA2CC` | `EvtEventAvail` | partial native | Reports whether UART/native event queue has pending events. |
| `0xA2D3` | `PrefGetAppPreferences` | shim | Returns `noPreferenceFound` and size 0. |
| `0xA2FC` | `CategoryInitialize` | shim | Returns success for current category smoke path. |
| `0xA27B` | `FtrGet` | shim | Returns `noSuchFeature` with zero value. |

## Current Gaps Before "Real Palm UI" Confidence

| Area | Gap |
| --- | --- |
| Form resources | `tFRM` decoding handles Memo's current form and now preserves popup trigger/list companions, but broader Palm form object classes still need decoding and validation against more apps. |
| Category Manager | Native popup works visually and the resource trigger/list link is tracked, but full Palm category dialogs, category persistence, and database category filtering are simplified. |
| Table Manager | Memo draw callbacks work, but the table API is still focused on Memo list behavior rather than a general table widget implementation. |
| Window Manager | Drawing is useful for Memo, but window stack, offscreen windows, clipping edge cases, and save/restore bits are simplified. |
| Database Manager | Editable records use native dynamic handles, but full Palm database metadata, sorting, categories, sync flags, and persistence are not complete. |
| Event Manager | UART tap/event path works for smoke testing, but complete pen-down/pen-move/pen-up sequences and system event dispatch are still simplified. |
