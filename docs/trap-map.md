# Memo Pad Trap Coverage

Reference: https://palm.wiki/development/docs/601/PalmOSReference/ReferenceTOC.html

| Selector | Current name | Area | Status | Notes |
| --- | --- | --- | --- | --- |
| `0xA012` | `MemChunkFree` | Memory | tiny heap | Frees dynamic pointer-backed blocks and returns success for legacy fixed scratch pointers. |
| `0xA013` | `MemPtrNew` | Memory | tiny heap | Allocates a pointer-backed block from the generated trap heap. |
| `0xA014` | `MemPtrRecoverHandle` | Memory | tiny heap | Recovers dynamic handles from pointers and maps legacy fixed scratch pointers. |
| `0xA016` | `MemPtrSize` | Memory | tiny heap | Returns dynamic pointer sizes, including interior pointer remaining bytes, with fixed scratch fallbacks. |
| `0xA01C` | `MemPtrResize` | Memory | tiny heap | Resizes dynamic pointer-backed blocks through their recovered handle. |
| `0xA01E` | `MemHandleNew` | Memory | tiny heap | Allocates a dynamic fake MemHandle with pointer, size, and lock count metadata. |
| `0xA01F` | `MemHandleLockCount` | Memory | tiny heap | Reports dynamic fake-handle lock counts. |
| `0xA020` | `MemHandleToLocalID` | Memory | compat shim | Currently returns the fake handle value as its local id. |
| `0xA021` | `MemHandleLock` | Memory | tiny heap | Maps dynamic handles and legacy fixed handles to trap heap pointers. |
| `0xA022` | `MemHandleUnlock` | Memory | tiny heap | Decrements lock counts for dynamic fake handles. |
| `0xA02B` | `MemHandleFree` | Memory | tiny heap | Frees dynamic fake handles and returns their backing blocks to the reusable free list. |
| `0xA02C` | `MemHandleFlags` | Memory | compat shim | Returns zero flags for dynamic and legacy fake handles. |
| `0xA02D` | `MemHandleSize` | Memory | tiny heap | Returns dynamic handle sizes or legacy fixed-handle sizes. |
| `0xA033` | `MemHandleResize` | Memory | tiny heap | Resizes dynamic fake handles, moving backing storage when needed. |
| `0xA035` | `MemPtrUnlock` | Memory | tiny heap | Recovers the dynamic handle from a pointer and decrements its lock count. |
| `0xA041` | `DmCreateDatabase` | Database | working shim | Creates the synthetic MemoDB when Memo Pad asks for DATA/memo. |
| `0xA04A` | `DmCloseDatabase` | Database | stubbed | Returns success for the synthetic MemoDB. |
| `0xA049` | `DmOpenDatabase` | Database | forced create path | Currently returns null so Memo Pad initializes the fake database. |
| `0xA04C` | `DmOpenDatabaseInfo` | Database | partial shim | Writes zeroed metadata for now. |
| `0xA04F` | `DmNumRecords` | Database | working shim | Returns the synthetic memo record count. |
| `0xA050` | `DmRecordInfo` | Database | working shim | Writes attributes, unique id, and the per-record dynamic chunk handle. |
| `0xA051` | `DmSetRecordInfo` | Database | metadata shim | Accepts record metadata updates; category/dirty persistence is still simplified. |
| `0xA055` | `DmNewRecord` | Database | writable shim | Inserts an empty synthetic memo record backed by a dynamic tiny-heap MemHandle. |
| `0xA056` | `DmRemoveRecord` | Database | writable shim | Removes a synthetic memo record and frees its dynamic backing handle. |
| `0xA057` | `DmDeleteRecord` | Database | writable shim | Removes a synthetic memo record and frees its dynamic backing handle; sync delete flags are not modeled yet. |
| `0xA058` | `DmArchiveRecord` | Database | writable shim | Removes a synthetic memo record and frees its dynamic backing handle; archive flags are not modeled yet. |
| `0xA05B` | `DmQueryRecord` | Database | working shim | Returns the requested memo record's dynamic tiny-heap handle. |
| `0xA05C` | `DmGetRecord` | Database | working shim | Returns the requested memo record's dynamic tiny-heap handle. |
| `0xA05D` | `DmResizeRecord` | Database | writable shim | Resizes the requested memo record's dynamic backing handle. |
| `0xA05E` | `DmReleaseRecord` | Database | writable shim | Commits a dirty dynamic memo record handle back into the native display store. |
| `0xA05F` | `DmGetResource` | Resource | partial shim | Loads exported resource bytes or synthetic tSTR text and reports overlay-catalog hits. |
| `0xA060` | `DmGet1Resource` | Resource | partial shim | Uses the same exported-resource and overlay-catalog lookup path as DmGetResource. |
| `0xA061` | `DmReleaseResource` | Resource | stubbed | Returns success for fake resource handles. |
| `0xA071` | `DmNumRecordsInCategory` | Database | working shim | Publishes list rows and returns the synthetic count. |
| `0xA075` | `DmOpenDatabaseByTypeCreator` | Database | working shim | Opens DATA/memo only after synthetic create. |
| `0xA076` | `DmWrite` | Database | writable shim | Copies bytes into a dynamic memo record pointer and commits the memo text. |
| `0xA077` | `DmStrCopy` | Database | writable shim | Copies a string into a dynamic memo record pointer and commits the memo text. |
| `0xA079` | `DmWriteCheck` | Database | validation shim | Allows writes to proceed while bounds checks are handled by the scratch buffer. |
| `0xA08F` | `SysAppStartup` | System | working shim | Builds a minimal launch block for startup glue. |
| `0xA090` | `SysAppExit` | System | working shim | Stops the probe run cleanly. |
| `0xA0A9` | `SysHandleEvent` | Events | probe shim | Returns false while the native LCD probe keeps the Memo Pad surface alive. |
| `0xA104` | `CategoryGetName` | Categories | working shim | Writes All or Unfiled into the caller's category buffer. |
| `0xA10D` | `CtlDrawControl` | Controls | control shim | Draws known Memo buttons from fake control object metadata. |
| `0xA10E` | `CtlEraseControl` | Controls | control shim | Marks known fake controls hidden and clears their pressed value. |
| `0xA10F` | `CtlHideControl` | Controls | control shim | Marks known fake controls hidden and clears their pressed value. |
| `0xA110` | `CtlShowControl` | Controls | control shim | Marks known fake controls visible and redraws them. |
| `0xA111` | `CtlGetValue` | Controls | control shim | Returns the tracked value for known fake controls. |
| `0xA112` | `CtlSetValue` | Controls | control shim | Updates the tracked value for known fake controls. |
| `0xA113` | `CtlGetLabel` | Controls | control shim | Returns a stable pointer to the fake control label text. |
| `0xA114` | `CtlSetLabel` | Controls | control shim | Copies a caller label into known fake control metadata and redraws. |
| `0xA115` | `CtlHandleEvent` | Controls | control shim | Consumes matching ctlSelect events and routes them through the native Memo tap path. |
| `0xA116` | `CtlHitControl` | Controls | control shim | Triggers the known fake control through the native Memo tap path. |
| `0xA117` | `CtlSetEnabled` | Controls | control shim | Tracks whether a fake control is interactable. |
| `0xA118` | `CtlSetUsable` | Controls | control shim | Tracks whether a fake control is usable/visible. |
| `0xA119` | `CtlEnabled` | Controls | control shim | Returns tracked enabled state for known fake controls. |
| `0xA11D` | `EvtGetEvent` | Events | queue-backed shim | Writes queued ctlSelect/lstSelect events from UART tap injection, otherwise writes a minimal nil event. |
| `0xA135` | `FldDrawField` | Fields | text shim | Mirrors the synthetic active field buffer into the native Memo edit surface. |
| `0xA139` | `FldGetTextPtr` | Fields | text shim | Returns the fake field text pointer at 0x2300. |
| `0xA13F` | `FldSetText` | Fields | text shim | Copies text from a fake handle into the active field buffer. |
| `0xA14A` | `FldGetTextAllocatedSize` | Fields | text shim | Reports the current 64-byte smoke field buffer size. |
| `0xA14B` | `FldGetTextLength` | Fields | text shim | Returns the active field text length. |
| `0xA153` | `FldGetTextHandle` | Fields | text shim | Returns the fake field text handle. |
| `0xA155` | `FldDirty` | Fields | text shim | Reports whether insert/delete/set-dirty touched the active field. |
| `0xA158` | `FldSetTextHandle` | Fields | text shim | Copies text from a fake MemHandle into the active field buffer. |
| `0xA159` | `FldSetTextPtr` | Fields | text shim | Copies text from a pointer into the active field buffer. |
| `0xA15D` | `FldInsert` | Fields | text shim | Inserts text at the synthetic insertion point and mirrors the edit surface. |
| `0xA15E` | `FldDelete` | Fields | text shim | Deletes a text range and mirrors the edit surface. |
| `0xA160` | `FldSetDirty` | Fields | text shim | Updates the active field dirty flag. |
| `0xA16F` | `FrmInitForm` | Forms | catalog-aware form shim | Allocates a native fake FormPtr, reports overlay-catalog tFRM matches, and seeds stable list/button object pointers. |
| `0xA170` | `FrmDeleteForm` | Forms | form shim | Clears the active fake form object table. |
| `0xA171` | `FrmDrawForm` | Forms | bounds-driven form shim | Draws the current native Memo surface from published form object bounds and memo rows. |
| `0xA172` | `FrmEraseForm` | Forms | form shim | Accepts form erase requests for the active fake form. |
| `0xA173` | `FrmGetActiveForm` | Forms | form shim | Returns the active native fake FormPtr. |
| `0xA174` | `FrmSetActiveForm` | Forms | form shim | Accepts an active fake form pointer. |
| `0xA175` | `FrmGetActiveFormID` | Forms | form shim | Returns the active fake form resource id. |
| `0xA176` | `FrmGetUserModifiedState` | Forms | form shim | Returns false until real dirty form state is modeled. |
| `0xA177` | `FrmSetNotUserModified` | Forms | form shim | Accepts clear-dirty requests for the fake form. |
| `0xA178` | `FrmGetFocus` | Forms | form shim | Returns the tracked fake form focus index. |
| `0xA179` | `FrmSetFocus` | Forms | form shim | Tracks the requested fake form focus index. |
| `0xA17B` | `FrmGetFormBounds` | Forms | form shim | Writes a 160x160 fake form bounds rectangle. |
| `0xA17D` | `FrmGetFormId` | Forms | form shim | Returns the active fake form id. |
| `0xA17E` | `FrmGetFormPtr` | Forms | form shim | Returns or initializes the fake form pointer for a form id. |
| `0xA17F` | `FrmGetNumberOfObjects` | Forms | form shim | Returns the seeded fake form object count. |
| `0xA180` | `FrmGetObjectIndex` | Forms | form shim | Returns stable object indexes for known or newly observed form object ids. |
| `0xA181` | `FrmGetObjectId` | Forms | form shim | Returns fake form object ids by index. |
| `0xA182` | `FrmGetObjectType` | Forms | form shim | Returns Palm-compatible fake form object types for controls and lists. |
| `0xA183` | `FrmGetObjectPtr` | Forms | bounds-driven form shim | Returns native fake object pointers with id, kind, and bounds metadata mirrored into the LCD geometry bridge. |
| `0xA184` | `FrmHideObject` | Forms | form shim | Marks a fake form object hidden. |
| `0xA185` | `FrmShowObject` | Forms | form shim | Marks a fake form object visible and redraws known controls. |
| `0xA186` | `FrmGetObjectPosition` | Forms | form shim | Writes tracked fake object x/y coordinates. |
| `0xA187` | `FrmSetObjectPosition` | Forms | bounds-driven form shim | Updates tracked fake object coordinates and republishes LCD hit/draw bounds. |
| `0xA188` | `FrmGetControlValue` | Forms | form/control shim | Returns tracked fake control value by form object index. |
| `0xA189` | `FrmSetControlValue` | Forms | form/control shim | Updates tracked fake control value by form object index. |
| `0xA192` | `FrmAlert` | Dialogs | modal shim | Shows a native OK modal and returns button 0. |
| `0xA193` | `FrmDoDialog` | Dialogs | modal shim | Shows a native OK modal and returns the synthetic OK control id. |
| `0xA194` | `FrmCustomAlert` | Dialogs | modal shim | Shows a native OK modal, using captured text when available. |
| `0xA199` | `FrmGetObjectBounds` | Forms | bounds-driven form shim | Writes tracked fake object bounds rectangles used by the native Memo UI renderer. |
| `0xA19B` | `FrmGotoForm` | Forms | probe shim | Used as a form/title signal for the current Memo Pad LCD surface. |
| `0xA1A0` | `FrmDispatchEvent` | Forms | select-event shim | Reads ctlSelect/lstSelect data and mirrors it into the native Memo UI probe. |
| `0xA1B0` | `LstSetDrawFunction` | Lists | list shim | Accepts custom draw callback registration for the synthetic Memo list. |
| `0xA1B1` | `LstDrawList` | Lists | list shim | Redraws the native Memo list from synthetic memo records. |
| `0xA1B2` | `LstEraseList` | Lists | list shim | Returns success for the synthetic Memo list erase path. |
| `0xA1B3` | `LstGetSelection` | Lists | list shim | Returns the current synthetic Memo list selection. |
| `0xA1B4` | `LstGetSelectionText` | Lists | list shim | Returns a stable pointer to the selected memo row text. |
| `0xA1B5` | `LstHandleEvent` | Lists | list shim | Consumes list-select events and updates native Memo list selection. |
| `0xA1B6` | `LstSetHeight` | Lists | list shim | Stores requested visible item count for the synthetic Memo list. |
| `0xA1B7` | `LstSetSelection` | Lists | list shim | Updates the synthetic Memo list selection and redraws. |
| `0xA1B8` | `LstSetListChoices` | Lists | list shim | Accepts caller-supplied choice arrays while the Memo list remains record-backed. |
| `0xA1B9` | `LstMakeItemVisible` | Lists | list shim | Adjusts the synthetic Memo list top row so the requested item is visible. |
| `0xA1BA` | `LstGetNumberOfItems` | Lists | list shim | Returns the synthetic memo record count. |
| `0xA1BB` | `LstPopupList` | Lists | list shim | Draws the list and returns the current selection. |
| `0xA1BC` | `LstSetPosition` | Lists | list shim | Updates synthetic list object bounds metadata. |
| `0xA1BD` | `MenuInit` | Menus | menu shim | Allocates and activates a fake MenuBarType pointer for the requested menu resource id. |
| `0xA1BE` | `MenuDispose` | Menus | menu shim | Disposes the active fake menu and clears menu visibility. |
| `0xA1BF` | `MenuHandleEvent` | Menus | menu shim | Writes a zero error code and returns false unless future real menu command handling is added. |
| `0xA1C0` | `MenuDrawMenu` | Menus | menu shim | Marks the fake active menu visible. |
| `0xA1C1` | `MenuEraseStatus` | Menus | menu shim | Marks the fake active menu hidden. |
| `0xA1C2` | `MenuGetActiveMenu` | Menus | menu shim | Returns the tracked fake active menu pointer. |
| `0xA1C3` | `MenuSetActiveMenu` | Menus | menu shim | Replaces the fake active menu pointer and returns the previous one. |
| `0xA1FC` | `WinSetActiveWindow` | Windows | window shim | Tracks a fake active window handle. |
| `0xA1FD` | `WinSetDrawWindow` | Windows | window shim | Tracks a fake draw window handle and returns the previous handle. |
| `0xA1FE` | `WinGetDrawWindow` | Windows | window shim | Returns the tracked fake draw window handle. |
| `0xA1FF` | `WinGetActiveWindow` | Windows | window shim | Returns the tracked fake active window handle. |
| `0xA200` | `WinGetDisplayWindow` | Windows | window shim | Returns a stable fake display window handle. |
| `0xA201` | `WinGetFirstWindow` | Windows | window shim | Returns the stable fake display window handle. |
| `0xA202` | `WinEnableWindow` | Windows | window shim | Accepts fake window enable requests. |
| `0xA203` | `WinDisableWindow` | Windows | window shim | Accepts fake window disable requests. |
| `0xA204` | `WinGetWindowFrameRect` | Windows | window shim | Writes a 160x160 fake window frame rectangle. |
| `0xA205` | `WinDrawWindowFrame` | Drawing | drawing shim | Draws a native 160x160 Palm LCD frame. |
| `0xA206` | `WinEraseWindow` | Drawing | drawing shim | Clears the native Palm LCD area. |
| `0xA207` | `WinSaveBits` | Windows | window shim | Returns a stable fake saved-bits handle and success error code. |
| `0xA208` | `WinRestoreBits` | Windows | window shim | Accepts restore-bits requests as a no-op. |
| `0xA20B` | `WinGetDisplayExtent` | Windows | window shim | Writes 160x160 display extent. |
| `0xA20C` | `WinGetWindowExtent` | Windows | window shim | Writes 160x160 window extent. |
| `0xA20D` | `WinDisplayToWindowPt` | Windows | window shim | Treats display/window coordinate conversion as identity. |
| `0xA20E` | `WinWindowToDisplayPt` | Windows | window shim | Treats window/display coordinate conversion as identity. |
| `0xA20F` | `WinGetClip` | Drawing | drawing shim | Returns the tracked fake clip rectangle. |
| `0xA210` | `WinSetClip` | Drawing | drawing shim | Tracks a fake clip rectangle. |
| `0xA211` | `WinResetClip` | Drawing | drawing shim | Resets the fake clip rectangle to 160x160. |
| `0xA212` | `WinClipRectangle` | Drawing | drawing shim | Intersects a rectangle with the tracked fake clip rectangle. |
| `0xA213` | `WinDrawLine` | Drawing | drawing shim | Draws a native monochrome line in the Palm LCD area. |
| `0xA214` | `WinDrawGrayLine` | Drawing | drawing shim | Draws a native gray line in the Palm LCD area. |
| `0xA215` | `WinEraseLine` | Drawing | drawing shim | Draws a white line in the Palm LCD area. |
| `0xA216` | `WinInvertLine` | Drawing | drawing shim | Draws a gray/invert placeholder line. |
| `0xA217` | `WinFillLine` | Drawing | drawing shim | Draws a native monochrome line in the Palm LCD area. |
| `0xA218` | `WinDrawRectangle` | Drawing | drawing shim | Fills a native monochrome rectangle in the Palm LCD area. |
| `0xA219` | `WinEraseRectangle` | Drawing | drawing shim | Fills a native white rectangle in the Palm LCD area. |
| `0xA21A` | `WinInvertRectangle` | Drawing | drawing shim | Fills a native gray/invert placeholder rectangle. |
| `0xA21B` | `WinDrawRectangleFrame` | Drawing | drawing shim | Draws a native monochrome rectangle frame. |
| `0xA21C` | `WinDrawGrayRectangleFrame` | Drawing | drawing shim | Draws a native gray rectangle frame. |
| `0xA21D` | `WinEraseRectangleFrame` | Drawing | drawing shim | Draws a native white rectangle frame. |
| `0xA21E` | `WinInvertRectangleFrame` | Drawing | drawing shim | Draws a native gray/invert placeholder frame. |
| `0xA21F` | `WinGetFramesRectangle` | Drawing | drawing shim | Copies the source rectangle into the caller's obscured rectangle. |
| `0xA220` | `WinDrawChars` | Drawing | drawing shim | Draws native monochrome text using the current generated bitmap font. |
| `0xA221` | `WinEraseChars` | Drawing | drawing shim | Erases the text bounds and redraws text in white. |
| `0xA222` | `WinInvertChars` | Drawing | drawing shim | Draws native gray/invert placeholder text. |
| `0xA226` | `WinDrawBitmap` | Drawing | resource drawing shim | Draws uncompressed 1bpp Palm bitmap resources from the currently locked resource handle. |
| `0xA227` | `WinModal` | Windows | window shim | Returns false for fake window modality. |
| `0xA228` | `WinGetDrawWindowBounds` | Windows | window shim | Writes a 160x160 draw-window bounds rectangle. |
| `0xA229` | `WinFillRectangle` | Drawing | drawing shim | Fills a native monochrome rectangle in the Palm LCD area. |
| `0xA22A` | `WinDrawInvertedChars` | Drawing | drawing shim | Draws native gray/invert placeholder text. |
| `0xA2B5` | `LstSetTopItem` | Lists | list shim | Updates the synthetic Memo list top row and redraws the visible window. |
| `0xA2CC` | `EvtEventAvail` | Events | working shim | Reports whether the UART-backed Palm event queue has pending events. |
| `0xA2D3` | `PrefGetAppPreferences` | Preferences | working shim | Returns noPreferenceFound and size 0. |
| `0xA2FC` | `CategoryInitialize` | Categories | stubbed | Returns success for now. |
| `0xA27B` | `FtrGet` | System | working shim | Returns noSuchFeature with zero value. |
