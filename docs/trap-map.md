# Palm OS Trap Map

Coverage tracker for the Palm OS 4-and-earlier native API reimplementation.

Primary API behavior references:
<https://palm.wiki/development/docs/601/PalmOSReference/ReferenceTOC.html>
<https://github.com/jichu4n/palm-os-sdk/tree/master/sdk-4>

The Palm reference is useful for API arguments, structures, events, return
values, and OS-version compatibility. The SDK 4 `CoreTraps.h` header gives the
raw `0xAxxx` selector names used below.

## Memo Pad Smoke Coverage

Current target: enough native ESP32 Palm API behavior for the Palm OS 4 Memo Pad
application to open, discover or create its database, draw a Palm-like list UI,
and expose a 160x160 LCD snapshot over UART.

| Selector | Current name | Area | Status | Notes |
| --- | --- | --- | --- | --- |
| `0xA012` | `MemChunkFree` | Memory | stubbed | Returns success for now; real heap ownership is still missing. |
| `0xA013` | `MemPtrNew` | Memory | named only | Seen in `CoreTraps.h`; not yet backed by the fake heap dispatcher. |
| `0xA020` | `MemHandleToLocalID` | Memory | compat shim | Currently returns the fake handle value as its local id. |
| `0xA021` | `MemHandleLock` | Memory | working shim | Maps fake record, resource, and allocation handles to trap heap pointers. |
| `0xA022` | `MemHandleUnlock` | Memory | stubbed | Returns success; lock counts are not tracked yet. |
| `0xA02B` | `MemHandleFree` | Memory | stubbed | Returns success for fake handles. |
| `0xA041` | `DmCreateDatabase` | Database | working shim | Creates the synthetic MemoDB when Memo Pad asks for `DATA`/`memo`. |
| `0xA049` | `DmOpenDatabase` | Database | forced create path | Currently returns null so Memo Pad initializes the fake database. |
| `0xA04A` | `DmCloseDatabase` | Database | stubbed | Returns success for the synthetic MemoDB. |
| `0xA04C` | `DmOpenDatabaseInfo` | Database | partial shim | Writes zeroed metadata for now. |
| `0xA04F` | `DmNumRecords` | Database | working shim | Returns the synthetic memo record count. |
| `0xA050` | `DmRecordInfo` | Database | working shim | Writes attributes, unique id, and fake chunk handle. |
| `0xA05B` | `DmQueryRecord` | Database | working shim | Seeds the trap heap with the requested fake memo text. |
| `0xA05C` | `DmGetRecord` | Database | working shim | Same fake record handle path as query. |
| `0xA05E` | `DmReleaseRecord` | Database | stubbed | Returns success; dirty writes are ignored. |
| `0xA05F` | `DmGetResource` | Resource | partial shim | Loads exported resource bytes or synthetic `tSTR` text. |
| `0xA061` | `DmReleaseResource` | Resource | stubbed | Returns success for fake resource handles. |
| `0xA071` | `DmNumRecordsInCategory` | Database | working shim | Publishes list rows and returns the synthetic count. |
| `0xA075` | `DmOpenDatabaseByTypeCreator` | Database | working shim | Opens `DATA`/`memo` only after synthetic create. |
| `0xA08F` | `SysAppStartup` | System | working shim | Builds a minimal launch block for startup glue. |
| `0xA090` | `SysAppExit` | System | working shim | Stops the probe run cleanly. |
| `0xA0A9` | `SysHandleEvent` | Events | probe shim | Returns false while the native LCD probe keeps the Memo Pad surface alive. |
| `0xA104` | `CategoryGetName` | Categories | working shim | Writes `All` or `Unfiled` into the caller's category buffer. |
| `0xA11D` | `EvtGetEvent` | Events | partial shim | Writes a minimal nil event and advances the native LCD probe. |
| `0xA19B` | `FrmGotoForm` | Forms | probe shim | Used as a form/title signal for the current Memo Pad LCD surface. |
| `0xA1A0` | `FrmDispatchEvent` | Forms | probe shim | Returns false for now; real form dispatch is still missing. |
| `0xA1BF` | `MenuHandleEvent` | Menus | probe shim | Returns false for now; menu behavior is not implemented. |
| `0xA2D3` | `PrefGetAppPreferences` | Preferences | working shim | Returns `noPreferenceFound` and size 0. |
| `0xA2FC` | `CategoryInitialize` | Categories | stubbed | Returns success for now. |
| `0xA27B` | `FtrGet` | System | working shim | Returns `noSuchFeature` with zero value. |

You can print this same live coverage summary from the VB.NET tool:

```text
dotnet run --project tools/vbnet/PalmEsp32RomTool -- trap-map --markdown
```

## Next High-Value Gaps

| Area | Needed behavior | Why it matters |
| --- | --- | --- |
| Events | Expand `EvtGetEvent`, add `EvtEventAvail`, and model an event queue. | Needed for real list/button interaction instead of a static Memo Pad draw. |
| Forms | Implement real form/control/list dispatch around `FrmDrawForm`, `FrmHandleEvent`, `CtlDrawControl`, and `LstDrawList`. | Lets Memo Pad drive the UI through normal Palm API traps. |
| Databases | Replace the forced-create fake database path with a small persistent PDB-style record store. | Needed for adding, editing, and scrolling real memos. |
| Memory | Replace fixed fake handles with a tiny Palm-style handle heap. | Needed once fields, forms, resources, and records allocate more than one object. |
