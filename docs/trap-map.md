# Palm OS Trap Map

Coverage tracker for Palm OS 4-and-earlier native API reimplementation.

## Priority 0: Bootstrapping Apps

| Area | Traps | Status |
| --- | --- | --- |
| System | `SysAppLaunch`, `SysHandleEvent` | planned |
| Events | `EvtGetEvent`, `EvtAddEventToQueue`, `EvtEventAvail` | planned |
| Forms | `FrmInitForm`, `FrmDrawForm`, `FrmHandleEvent`, `FrmGetActiveForm` | planned |
| Windows | `WinDrawChars`, `WinEraseWindow`, `WinSetDrawWindow` | planned |
| Memory | `MemHandleNew`, `MemHandleLock`, `MemHandleUnlock`, `MemPtrFree` | planned |
| Resources | `DmGetResource`, `DmReleaseResource` | planned |

## Priority 1: Useful UI

| Area | Traps | Status |
| --- | --- | --- |
| Controls | `CtlDrawControl`, `CtlHandleEvent`, `CtlSetValue` | planned |
| Fields | `FldDrawField`, `FldHandleEvent`, `FldSetTextHandle` | planned |
| Menus | `MenuHandleEvent`, `MenuDrawMenu` | planned |
| Fonts | `FntSetFont`, `FntCharsWidth`, `FntLineHeight` | planned |

## Priority 2: Storage and Databases

| Area | Traps | Status |
| --- | --- | --- |
| Databases | `DmOpenDatabase`, `DmCloseDatabase`, `DmNumRecords` | planned |
| Records | `DmGetRecord`, `DmReleaseRecord`, `DmWrite` | planned |
| Preferences | `PrefGetAppPreferences`, `PrefSetAppPreferences` | planned |

