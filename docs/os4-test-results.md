# Palm OS 4 ROM Test Results

Verified with `PalmEsp32RomTool` on 2026-06-15.

## Inspect

| ROM | Size | Databases | Applications | Result |
| --- | ---: | ---: | ---: | --- |
| `Palm-IIIc-4.1-en.rom` | 1,736,704 | 87 | 14 | pass |
| `Palm-m505-4.1-en.rom` | 4,194,304 | 124 | 19 | pass |

## Generate

Generated a smoke-test ESP32 project from `Palm-m505-4.1-en.rom`:

```text
dotnet run --project tools/vbnet/PalmEsp32RomTool -- generate Palm-m505-4.1-en.rom out/PalmOS4MemoPadSmoke --app-name "Memo Pad" --project-name PalmOS4MemoPadCompat --hardware-profile esp32-palm-m505
```

Result:

```text
Applications exported: 1
Memo Pad (memo) -> data/apps/Memo Pad-memo.prc
Exported PRC size: 25,036 bytes
Source ROM offset: 0x001B9012
```

