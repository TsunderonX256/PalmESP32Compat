# Architecture

PalmESP32Compat is a hybrid Palm runtime for ESP32.

## Execution Model

```text
Palm PRC code resource
        |
        v
68K emulator core
        |
        +-- normal 68K instructions execute in the emulator
        |
        +-- Palm OS trap instruction
                |
                v
        native ESP32 Palm API shim
```

The emulator owns CPU state and 68K instruction execution. The compatibility layer owns Palm OS services.

## Major Runtime Pieces

- `cpu68k`: emulator integration, memory callbacks, trap decoding, and register marshalling.
- `palm_api`: native Palm OS trap implementations.
- `palm_memory`: Palm-compatible memory manager for handles, chunks, heaps, and resources.
- `palm_ui`: event queue, windows, forms, controls, fonts, drawing primitives, and framebuffer ownership.
- `platform_esp32`: hardware adapters for TFT, touch/buttons, SD/SPIFFS, timers, and serial.
- `tools/vbnet`: offline analysis and conversion tools for ROM, PRC, PDB, and resources.

## Comparison Strategy

The original emulator remains the behavioral reference. This runtime should be tested with:

- trap call logs
- event traces
- framebuffer hashes or screenshots
- database/resource operation traces
- app launch and event loop milestones

