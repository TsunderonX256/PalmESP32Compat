Imports System.IO
Imports System.Text

Public NotInheritable Class Esp32ProjectRequest
    Public Property RomPath As String = ""
    Public Property OutputDirectory As String = ""
    Public Property RomBase As UInteger?
    Public Property ProjectName As String = "PalmCompatGenerated"
    Public Property HardwareProfile As String = "esp32-palm-m100"
    Public Property AppNameFilter As String = ""
    Public Property CreatorFilter As String = ""
    Public Property ExportAllApplications As Boolean
End Class

Public NotInheritable Class Esp32ProjectResult
    Public Property OutputDirectory As String = ""
    Public Property ExportedApplications As List(Of ExportedPalmApplication) = New List(Of ExportedPalmApplication)()
End Class

Public NotInheritable Class ExportedPalmApplication
    Public Property Name As String = ""
    Public Property CreatorCode As String = ""
    Public Property TypeCode As String = ""
    Public Property SourceOffset As Integer
    Public Property Size As Integer
    Public Property RelativePath As String = ""
End Class

Public NotInheritable Class Esp32ProjectGenerator
    Private Sub New()
    End Sub

    Public Shared Function Generate(request As Esp32ProjectRequest) As Esp32ProjectResult
        If Not File.Exists(request.RomPath) Then
            Throw New FileNotFoundException("ROM dump not found", request.RomPath)
        End If

        Dim rom = File.ReadAllBytes(request.RomPath)
        Dim databases = PalmRomScanner.FindDatabases(rom, request.RomBase)
        Dim selectedApps = SelectApplications(databases, request).ToList()

        If selectedApps.Count = 0 Then
            Throw New InvalidDataException("no matching applications found in ROM")
        End If

        Dim outputDirectory = request.OutputDirectory
        Directory.CreateDirectory(outputDirectory)
        Directory.CreateDirectory(Path.Combine(outputDirectory, "include"))
        Directory.CreateDirectory(Path.Combine(outputDirectory, "src"))
        Directory.CreateDirectory(Path.Combine(outputDirectory, "src", "generated"))
        Directory.CreateDirectory(Path.Combine(outputDirectory, "data", "apps"))
        Directory.CreateDirectory(Path.Combine(outputDirectory, "docs"))

        Dim result As New Esp32ProjectResult With {.OutputDirectory = outputDirectory}

        For Each app In selectedApps
            Dim fileName = PalmRomScanner.MakeSafeFileName($"{app.Name}-{app.CreatorCode}.prc")
            Dim relativePath = Path.Combine("apps", fileName).Replace("\"c, "/"c)
            Dim outputPath = Path.Combine(outputDirectory, "data", "apps", fileName)
            PalmRomScanner.ExportDatabase(rom, app, outputPath)

            result.ExportedApplications.Add(New ExportedPalmApplication With {
                .Name = app.Name,
                .CreatorCode = app.CreatorCode,
                .TypeCode = app.TypeCode,
                .SourceOffset = app.StartOffset,
                .Size = CInt(New FileInfo(outputPath).Length),
                .RelativePath = relativePath
            })
        Next

        WriteText(Path.Combine(outputDirectory, "platformio.ini"), RenderPlatformIo(request))
        WriteText(Path.Combine(outputDirectory, "include", "palm_compat_config.h"), RenderConfig(request))
        WriteText(Path.Combine(outputDirectory, "src", "main.cpp"), RenderMainCpp(request))
        WriteText(Path.Combine(outputDirectory, "src", "palm_prc_loader.h"), RenderPalmPrcLoaderHeader())
        WriteText(Path.Combine(outputDirectory, "src", "palm_prc_loader.cpp"), RenderPalmPrcLoaderCpp())
        WriteText(Path.Combine(outputDirectory, "src", "palm_68k_runtime.h"), RenderPalm68KRuntimeHeader())
        WriteText(Path.Combine(outputDirectory, "src", "palm_68k_runtime.cpp"), RenderPalm68KRuntimeCpp())
        WriteText(Path.Combine(outputDirectory, "src", "palm_display.h"), RenderPalmDisplayHeader())
        WriteText(Path.Combine(outputDirectory, "src", "palm_display.cpp"), RenderPalmDisplayCpp())
        WriteText(Path.Combine(outputDirectory, "src", "generated", "palm_rom_manifest.h"), RenderManifestHeader())
        WriteText(Path.Combine(outputDirectory, "src", "generated", "palm_rom_manifest.cpp"), RenderManifestCpp(request, result.ExportedApplications))
        WriteText(Path.Combine(outputDirectory, "docs", "generated-from-rom.txt"), RenderGenerationNotes(request, rom.Length, databases.Count, result.ExportedApplications))
        WriteText(Path.Combine(outputDirectory, "README.md"), RenderReadme(request, result.ExportedApplications))
        WriteText(Path.Combine(outputDirectory, "test.cmd"), RenderTestCmd())

        Return result
    End Function

    Private Shared Function SelectApplications(databases As List(Of PalmDatabase), request As Esp32ProjectRequest) As IEnumerable(Of PalmDatabase)
        Dim apps = databases.Where(Function(db) db.IsApplication)

        If Not String.IsNullOrWhiteSpace(request.AppNameFilter) Then
            apps = apps.Where(Function(db) db.Name.Equals(request.AppNameFilter, StringComparison.OrdinalIgnoreCase))
        End If

        If Not String.IsNullOrWhiteSpace(request.CreatorFilter) Then
            apps = apps.Where(Function(db) db.CreatorCode.Equals(request.CreatorFilter, StringComparison.OrdinalIgnoreCase))
        End If

        If Not request.ExportAllApplications AndAlso String.IsNullOrWhiteSpace(request.AppNameFilter) AndAlso String.IsNullOrWhiteSpace(request.CreatorFilter) Then
            apps = apps.Take(1)
        End If

        Return apps.OrderBy(Function(db) db.Name, StringComparer.OrdinalIgnoreCase)
    End Function

    Private Shared Function RenderPlatformIo(request As Esp32ProjectRequest) As String
        Dim board = ResolvePlatformIoBoard(request.HardwareProfile)
        Return $"; Generated by PalmEsp32RomTool
[env:{SanitizeIdentifier(request.HardwareProfile)}]
platform = espressif32
board = {board}
framework = arduino
monitor_speed = 115200
board_build.filesystem = spiffs
board_build.arduino.memory_type = qio_opi
build_flags =
  -DPALM_COMPAT_GENERATED_PROJECT=1
  -DPALM_COMPAT_HARDWARE_PROFILE=\""{EscapeCppString(request.HardwareProfile)}\""
  -DBOARD_HAS_PSRAM
"
    End Function

    Private Shared Function ResolvePlatformIoBoard(hardwareProfile As String) As String
        If hardwareProfile.IndexOf("esp32-palm", StringComparison.OrdinalIgnoreCase) >= 0 Then
            Return "esp32-s3-devkitc-1"
        End If

        Return "esp32dev"
    End Function

    Private Shared Function RenderConfig(request As Esp32ProjectRequest) As String
        Return $"#pragma once

#define PALM_COMPAT_PROJECT_NAME ""{EscapeCppString(request.ProjectName)}""
#define PALM_COMPAT_HARDWARE_PROFILE ""{EscapeCppString(request.HardwareProfile)}""
#define PALM_COMPAT_CPU_68K_EMULATED 1
#define PALM_COMPAT_NATIVE_TRAPS 1

// Runtime policy:
// - PRC code resources remain Motorola 68K.
// - ESP32 native code implements Palm OS traps and hardware bridges.
// - The generated manifest lists installable applications exported from the ROM.
"
    End Function

    Private Shared Function RenderMainCpp(request As Esp32ProjectRequest) As String
        Return "#include <Arduino.h>
#include ""palm_compat_config.h""
#include ""palm_prc_loader.h""
#include ""palm_68k_runtime.h""
#include ""palm_display.h""

void setup()
{
    Serial.begin(115200);
    delay(200);

    Serial.printf(""Palm compat project: %s\n"", PALM_COMPAT_PROJECT_NAME);
    Serial.printf(""Hardware profile: %s\n"", PALM_COMPAT_HARDWARE_PROFILE);

    printGeneratedPalmApps();
    if (loadGeneratedPalmApps())
    {
        palmDisplayBegin();
        palmDisplayShowLoaderSmoke(gLoadedApps, gLoadedAppCount);
        palm68kRunProbe(gLoadedApps, gLoadedAppCount);
    }
}

void loop()
{
    delay(16);
}
"
    End Function

    Private Shared Function RenderPalmPrcLoaderHeader() As String
        Return "#pragma once

#include <stddef.h>
#include <stdint.h>
#include ""generated/palm_rom_manifest.h""

struct PalmLoadedCodeResource
{
    uint16_t id;
    uint32_t size;
    uint8_t* bytes;
    uint32_t checksum;
};

struct PalmLoadedApp
{
    const PalmGeneratedApp* manifest;
    char dbName[33];
    char type[5];
    char creator[5];
    uint32_t fileSize;
    uint16_t resourceCount;
    uint16_t codeResourceCount;
    PalmLoadedCodeResource* codeResources;
};

extern PalmLoadedApp* gLoadedApps;
extern size_t gLoadedAppCount;

void printGeneratedPalmApps();
bool loadGeneratedPalmApps();
"
    End Function

    Private Shared Function RenderPalmPrcLoaderCpp() As String
        Return "#include ""palm_prc_loader.h""

#include <Arduino.h>
#include <FS.h>
#include <SPIFFS.h>
#include <stdlib.h>
#include <string.h>

struct PalmResourceEntry
{
    char type[5];
    uint16_t id;
    uint32_t offset;
    uint32_t size;
};

PalmLoadedApp* gLoadedApps = nullptr;
size_t gLoadedAppCount = 0;

static uint16_t readU16BE(File& file)
{
    uint8_t bytes[2];
    if (file.read(bytes, sizeof(bytes)) != sizeof(bytes))
    {
        return 0;
    }

    return static_cast<uint16_t>((bytes[0] << 8) | bytes[1]);
}

static uint32_t readU32BE(File& file)
{
    uint8_t bytes[4];
    if (file.read(bytes, sizeof(bytes)) != sizeof(bytes))
    {
        return 0;
    }

    return (static_cast<uint32_t>(bytes[0]) << 24) |
        (static_cast<uint32_t>(bytes[1]) << 16) |
        (static_cast<uint32_t>(bytes[2]) << 8) |
        static_cast<uint32_t>(bytes[3]);
}

static bool readFourCc(File& file, char out[5])
{
    if (file.read(reinterpret_cast<uint8_t*>(out), 4) != 4)
    {
        return false;
    }

    out[4] = '\0';
    return true;
}

static uint32_t readResourceOffsetAt(File& file, uint16_t index)
{
    const uint32_t entryOffset = 78u + static_cast<uint32_t>(index) * 10u + 6u;
    if (!file.seek(entryOffset, SeekSet))
    {
        return 0;
    }

    return readU32BE(file);
}

static bool readResourceEntry(File& file, uint16_t index, uint16_t count, uint32_t fileSize, PalmResourceEntry& entry)
{
    const uint32_t entryOffset = 78u + static_cast<uint32_t>(index) * 10u;
    if (!file.seek(entryOffset, SeekSet) || !readFourCc(file, entry.type))
    {
        return false;
    }

    entry.id = readU16BE(file);
    entry.offset = readU32BE(file);

    uint32_t nextOffset = fileSize;
    if (index + 1u < count)
    {
        nextOffset = readResourceOffsetAt(file, static_cast<uint16_t>(index + 1u));
    }

    if (entry.offset > fileSize || nextOffset < entry.offset || nextOffset > fileSize)
    {
        entry.size = 0;
        return false;
    }

    entry.size = nextOffset - entry.offset;
    return true;
}

static uint32_t fnv1a32(const uint8_t* data, uint32_t size)
{
    uint32_t hash = 2166136261u;
    for (uint32_t i = 0; i < size; ++i)
    {
        hash ^= data[i];
        hash *= 16777619u;
    }

    return hash;
}

static bool loadCodeResource(File& file, const PalmResourceEntry& entry, PalmLoadedCodeResource& loaded)
{
    if (entry.size == 0)
    {
        Serial.printf(""      code #%u has empty range\n"", static_cast<unsigned>(entry.id));
        return false;
    }

    loaded.id = entry.id;
    loaded.size = entry.size;
    loaded.bytes = nullptr;
    loaded.checksum = 0;

    uint8_t* bytes = static_cast<uint8_t*>(malloc(entry.size));
    if (bytes == nullptr)
    {
        Serial.printf(""      code #%u allocation failed: %u bytes\n"",
            static_cast<unsigned>(entry.id),
            static_cast<unsigned>(entry.size));
        return false;
    }

    if (!file.seek(entry.offset, SeekSet))
    {
        Serial.printf(""      code #%u seek failed\n"", static_cast<unsigned>(entry.id));
        free(bytes);
        return false;
    }

    const size_t readCount = file.read(bytes, entry.size);
    if (readCount != entry.size)
    {
        Serial.printf(""      code #%u short read: %u/%u bytes\n"",
            static_cast<unsigned>(entry.id),
            static_cast<unsigned>(readCount),
            static_cast<unsigned>(entry.size));
        free(bytes);
        return false;
    }

    loaded.bytes = bytes;
    loaded.checksum = fnv1a32(bytes, entry.size);

    Serial.printf(""      loaded code #%u bytes=%u fnv1a32=0x%08X first=%02X %02X %02X %02X\n"",
        static_cast<unsigned>(entry.id),
        static_cast<unsigned>(entry.size),
        static_cast<unsigned>(loaded.checksum),
        entry.size > 0 ? bytes[0] : 0,
        entry.size > 1 ? bytes[1] : 0,
        entry.size > 2 ? bytes[2] : 0,
        entry.size > 3 ? bytes[3] : 0);

    return true;
}

static bool loadPalmPrc(const PalmGeneratedApp& app, PalmLoadedApp& loadedApp)
{
    File file = SPIFFS.open(app.path, ""r"");
    if (!file)
    {
        Serial.printf(""  open failed: %s\n"", app.path);
        Serial.println(""  Upload filesystem first: platformio run -t uploadfs"");
        return false;
    }

    memset(&loadedApp, 0, sizeof(loadedApp));
    loadedApp.manifest = &app;

    const uint32_t fileSize = static_cast<uint32_t>(file.size());
    if (file.read(reinterpret_cast<uint8_t*>(loadedApp.dbName), 32) != 32)
    {
        Serial.println(""  short PRC header"");
        return false;
    }

    if (!file.seek(32, SeekSet))
    {
        Serial.println(""  seek failed"");
        return false;
    }

    const uint16_t attributes = readU16BE(file);
    file.seek(60, SeekSet);
    readFourCc(file, loadedApp.type);
    readFourCc(file, loadedApp.creator);
    file.seek(76, SeekSet);
    const uint16_t resourceCount = readU16BE(file);
    const bool isResourceDb = (attributes & 0x0001u) != 0;
    loadedApp.fileSize = fileSize;
    loadedApp.resourceCount = resourceCount;

    Serial.printf(""  PRC: %s type=%s creator=%s size=%u resources=%u\n"",
        loadedApp.dbName,
        loadedApp.type,
        loadedApp.creator,
        static_cast<unsigned>(fileSize),
        static_cast<unsigned>(resourceCount));

    if (!isResourceDb)
    {
        Serial.println(""  not a resource database; skipping code resource scan"");
        return false;
    }

    uint16_t codeCount = 0;
    for (uint16_t i = 0; i < resourceCount; ++i)
    {
        PalmResourceEntry entry;
        if (!readResourceEntry(file, i, resourceCount, fileSize, entry))
        {
            Serial.printf(""    resource %u: invalid range\n"", static_cast<unsigned>(i));
            continue;
        }

        if (strcmp(entry.type, ""code"") == 0)
        {
            ++codeCount;
        }
    }

    if (codeCount == 0)
    {
        Serial.println(""  no code resources found"");
        return false;
    }

    loadedApp.codeResources = static_cast<PalmLoadedCodeResource*>(calloc(codeCount, sizeof(PalmLoadedCodeResource)));
    if (loadedApp.codeResources == nullptr)
    {
        Serial.printf(""  code resource table allocation failed: %u entries\n"",
            static_cast<unsigned>(codeCount));
        return false;
    }

    uint16_t loadedCodeIndex = 0;
    for (uint16_t i = 0; i < resourceCount; ++i)
    {
        PalmResourceEntry entry;
        if (!readResourceEntry(file, i, resourceCount, fileSize, entry))
        {
            continue;
        }

        if (strcmp(entry.type, ""code"") == 0)
        {
            Serial.printf(""    code #%u offset=%u size=%u\n"",
                static_cast<unsigned>(entry.id),
                static_cast<unsigned>(entry.offset),
                static_cast<unsigned>(entry.size));
            if (loadedCodeIndex < codeCount && loadCodeResource(file, entry, loadedApp.codeResources[loadedCodeIndex]))
            {
                ++loadedCodeIndex;
            }
        }
    }

    loadedApp.codeResourceCount = loadedCodeIndex;
    Serial.printf(""  resident code resources: %u\n"", static_cast<unsigned>(loadedApp.codeResourceCount));
    return loadedApp.codeResourceCount > 0;
}

void printGeneratedPalmApps()
{
    Serial.printf(""Generated apps: %u\n"", static_cast<unsigned>(kPalmGeneratedAppCount));

    for (size_t i = 0; i < kPalmGeneratedAppCount; ++i)
    {
        const PalmGeneratedApp& app = kPalmGeneratedApps[i];
        Serial.printf(""  %s (%s), %u bytes, %s\n"",
            app.name,
            app.creator,
            static_cast<unsigned>(app.sizeBytes),
            app.path);
    }
}

bool loadGeneratedPalmApps()
{
    if (!SPIFFS.begin(true))
    {
        Serial.println(""SPIFFS mount failed"");
        return false;
    }

    Serial.println(""SPIFFS mounted"");
    gLoadedApps = static_cast<PalmLoadedApp*>(calloc(kPalmGeneratedAppCount, sizeof(PalmLoadedApp)));
    if (gLoadedApps == nullptr)
    {
        Serial.println(""Loaded app table allocation failed"");
        return false;
    }

    for (size_t i = 0; i < kPalmGeneratedAppCount; ++i)
    {
        if (loadPalmPrc(kPalmGeneratedApps[i], gLoadedApps[gLoadedAppCount]))
        {
            ++gLoadedAppCount;
        }
    }

    Serial.printf(""Resident Palm apps: %u\n"", static_cast<unsigned>(gLoadedAppCount));
    for (size_t appIndex = 0; appIndex < gLoadedAppCount; ++appIndex)
    {
        const PalmLoadedApp& app = gLoadedApps[appIndex];
        Serial.printf(""  resident app %u: %s codeResources=%u\n"",
            static_cast<unsigned>(appIndex),
            app.dbName,
            static_cast<unsigned>(app.codeResourceCount));
    }

    return gLoadedAppCount > 0;
}
"
    End Function

    Private Shared Function RenderPalm68KRuntimeHeader() As String
        Return "#pragma once

#include ""palm_prc_loader.h""

void palm68kRunProbe(const PalmLoadedApp* apps, size_t appCount);
"
    End Function

    Private Shared Function RenderPalm68KRuntimeCpp() As String
        Return "#include ""palm_68k_runtime.h""

#include <Arduino.h>

static const PalmLoadedCodeResource* findCodeResource(const PalmLoadedApp& app, uint16_t id)
{
    for (uint16_t i = 0; i < app.codeResourceCount; ++i)
    {
        if (app.codeResources[i].id == id)
        {
            return &app.codeResources[i];
        }
    }

    return nullptr;
}

static uint32_t readU32BEFromMemory(const uint8_t* bytes, uint32_t size, uint32_t offset)
{
    if (offset + 4u > size)
    {
        return 0;
    }

    return (static_cast<uint32_t>(bytes[offset]) << 24) |
        (static_cast<uint32_t>(bytes[offset + 1]) << 16) |
        (static_cast<uint32_t>(bytes[offset + 2]) << 8) |
        static_cast<uint32_t>(bytes[offset + 3]);
}

void palm68kRunProbe(const PalmLoadedApp* apps, size_t appCount)
{
    Serial.println(""68K runtime probe"");

    if (apps == nullptr || appCount == 0)
    {
        Serial.println(""  no resident apps"");
        return;
    }

    const PalmLoadedApp& app = apps[0];
    const PalmLoadedCodeResource* code0 = findCodeResource(app, 0);
    const PalmLoadedCodeResource* code1 = findCodeResource(app, 1);

    if (code0 != nullptr)
    {
        Serial.printf(""  code #0 size=%u checksum=0x%08X firstLong=0x%08X\n"",
            static_cast<unsigned>(code0->size),
            static_cast<unsigned>(code0->checksum),
            static_cast<unsigned>(readU32BEFromMemory(code0->bytes, code0->size, 0)));
    }
    else
    {
        Serial.println(""  code #0 missing"");
    }

    if (code1 != nullptr)
    {
        Serial.printf(""  code #1 size=%u checksum=0x%08X firstLong=0x%08X\n"",
            static_cast<unsigned>(code1->size),
            static_cast<unsigned>(code1->checksum),
            static_cast<unsigned>(readU32BEFromMemory(code1->bytes, code1->size, 0)));
    }
    else
    {
        Serial.println(""  code #1 missing"");
    }

    Serial.println(""  emulator integration point ready"");
}
"
    End Function

    Private Shared Function RenderPalmDisplayHeader() As String
        Return "#pragma once

#include ""palm_prc_loader.h""

bool palmDisplayBegin();
void palmDisplayShowLoaderSmoke(const PalmLoadedApp* apps, size_t appCount);
"
    End Function

    Private Shared Function RenderPalmDisplayCpp() As String
        Return "#include ""palm_display.h""

#include <Arduino.h>
#include <esp_err.h>
#include <esp_heap_caps.h>
#include <esp_lcd_panel_ops.h>
#include <esp_lcd_panel_rgb.h>

static constexpr int kScreenW = 480;
static constexpr int kScreenH = 272;
static constexpr int kPalmSurfaceW = 160;
static constexpr int kPalmSurfaceH = 220;
static constexpr int kPalmViewW = (kScreenH * kPalmSurfaceH) / kPalmSurfaceW;
static constexpr int kPalmViewH = kScreenH;
static constexpr int kPalmViewX = (kScreenW - kPalmViewW) / 2;
static constexpr int kPalmViewY = 0;
static constexpr int kPalmLcdW = 160;
static constexpr int kPalmLcdH = 160;
static constexpr int kPalmSilkscreenH = 60;
static constexpr int kPalmLcdViewX = kPalmViewX + (kPalmViewW * kPalmSilkscreenH) / kPalmSurfaceH;
static constexpr int kPalmLcdViewY = kPalmViewY;
static constexpr int kPalmLcdViewW = kPalmViewW - (kPalmViewW * kPalmSilkscreenH) / kPalmSurfaceH;
static constexpr int kPalmLcdViewH = kPalmViewH;

static constexpr int kTftBacklight = 2;
static constexpr int kIli6485Standby = 17;
static constexpr uint32_t kBacklightPwmHz = 5000;
static constexpr uint8_t kBacklightPwmBits = 8;
static constexpr uint8_t kBacklightDuty = 64;
static constexpr uint8_t kBacklightPwmChannel = 0;

static constexpr int32_t kRgbPclkHz = 8000000;
static constexpr bool kRgbPclkActiveNeg = false;
static constexpr int kRgbHsyncFrontPorch = 8;
static constexpr int kRgbHsyncPulseWidth = 4;
static constexpr int kRgbHsyncBackPorch = 43;
static constexpr int kRgbVsyncFrontPorch = 8;
static constexpr int kRgbVsyncPulseWidth = 4;
static constexpr int kRgbVsyncBackPorch = 12;
static constexpr bool kRgbPclkIdleHigh = true;
static esp_lcd_panel_handle_t gPanel = nullptr;
static const PalmLoadedApp* gDisplayApps = nullptr;
static size_t gDisplayAppCount = 0;
static uint32_t gDisplayGeneration = 0;
static volatile uint32_t gDisplayedFrames = 0;
static uint16_t* gFrameBuffer = nullptr;

static uint16_t rgb565(uint8_t r, uint8_t g, uint8_t b)
{
    return static_cast<uint16_t>(((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3));
}

static bool inRect(int x, int y, int rx, int ry, int rw, int rh)
{
    return x >= rx && x < rx + rw && y >= ry && y < ry + rh;
}

static uint16_t smokePixel(int x, int y)
{
    const uint16_t page = rgb565(38, 42, 40);
    const uint16_t shell = rgb565(88, 106, 76);
    const uint16_t shellDark = rgb565(42, 54, 38);
    const uint16_t lcdBg = rgb565(226, 230, 218);
    const uint16_t lcdInk = rgb565(50, 62, 48);
    const uint16_t accent = rgb565(80, 180, 130);
    const uint16_t warn = rgb565(200, 210, 90);

    if (x < kPalmViewX || x >= kPalmViewX + kPalmViewW)
    {
        return page;
    }

    if (inRect(x, y, kPalmLcdViewX, kPalmLcdViewY, kPalmLcdViewW, kPalmLcdViewH))
    {
        const int lx = x - kPalmLcdViewX;
        const int ly = y - kPalmLcdViewY;
        if (lx < 3 || ly < 3 || lx >= kPalmLcdViewW - 3 || ly >= kPalmLcdViewH - 3)
        {
            return lcdInk;
        }

        const int innerX = lx - 12;
        const int innerY = ly - 18;
        const int appBars = static_cast<int>(gDisplayAppCount > 0 ? gDisplayAppCount : 1);
        if (innerY >= 0 && innerY < 18 && innerX >= 0 && innerX < kPalmLcdViewW - 24)
        {
            return ((innerX / 10 + innerY / 6) & 1) ? accent : lcdInk;
        }

        for (int appIndex = 0; appIndex < appBars && appIndex < 4; ++appIndex)
        {
            const int rowY = 58 + appIndex * 24;
            if (innerY >= rowY && innerY < rowY + 10 && innerX >= 0)
            {
                const int codeCount = gDisplayApps != nullptr && appIndex < static_cast<int>(gDisplayAppCount)
                    ? gDisplayApps[appIndex].codeResourceCount
                    : 1;
                const int barW = 34 + codeCount * 22;
                if (innerX < barW)
                {
                    return accent;
                }
                if (innerX < barW + 6)
                {
                    return lcdInk;
                }
            }
        }

        if (((lx + ly + static_cast<int>(gDisplayGeneration & 15)) % 37) == 0)
        {
            return warn;
        }

        return lcdBg;
    }

    if (inRect(x, y, kPalmViewX, kPalmViewY, kPalmViewW, kPalmViewH))
    {
        const int sy = y - kPalmViewY;
        if (sy > kPalmViewH - 76)
        {
            const int sx = x - kPalmViewX;
            if (sx % (kPalmViewW / 4) < 3)
            {
                return shellDark;
            }
            return shell;
        }

        return shellDark;
    }

    return page;
}

static bool rgbFrameDoneCallback(esp_lcd_panel_handle_t panel,
                                 esp_lcd_rgb_panel_event_data_t* edata,
                                 void* userCtx)
{
    (void)panel;
    (void)edata;
    (void)userCtx;
    ++gDisplayedFrames;
    return false;
}

static void renderSmokeFrame()
{
    if (gFrameBuffer == nullptr)
    {
        return;
    }

    for (int y = 0; y < kScreenH; ++y)
    {
        uint16_t* row = gFrameBuffer + static_cast<uint32_t>(y) * kScreenW;
        for (int x = 0; x < kScreenW; ++x)
        {
            row[x] = smokePixel(x, y);
        }
    }
}

bool palmDisplayBegin()
{
    if (gPanel != nullptr)
    {
        return true;
    }

    digitalWrite(kIli6485Standby, HIGH);
    pinMode(kIli6485Standby, OUTPUT);

    if (gFrameBuffer == nullptr)
    {
        const size_t frameBytes = static_cast<size_t>(kScreenW) * kScreenH * sizeof(uint16_t);
        gFrameBuffer = static_cast<uint16_t*>(heap_caps_malloc(frameBytes, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
        if (gFrameBuffer == nullptr)
        {
            gFrameBuffer = static_cast<uint16_t*>(heap_caps_malloc(frameBytes, MALLOC_CAP_8BIT));
        }

        if (gFrameBuffer == nullptr)
        {
            Serial.printf(""LCD framebuffer allocation failed: %u bytes\n"", static_cast<unsigned>(frameBytes));
            return false;
        }

        Serial.printf(""LCD framebuffer allocated: %u bytes at %p\n"",
            static_cast<unsigned>(frameBytes),
            static_cast<void*>(gFrameBuffer));
    }

    esp_lcd_rgb_panel_config_t panelConfig = {};
    panelConfig.clk_src = LCD_CLK_SRC_XTAL;
    panelConfig.timings.pclk_hz = kRgbPclkHz;
    panelConfig.timings.h_res = kScreenW;
    panelConfig.timings.v_res = kScreenH;
    panelConfig.timings.hsync_pulse_width = kRgbHsyncPulseWidth;
    panelConfig.timings.hsync_back_porch = kRgbHsyncBackPorch;
    panelConfig.timings.hsync_front_porch = kRgbHsyncFrontPorch;
    panelConfig.timings.vsync_pulse_width = kRgbVsyncPulseWidth;
    panelConfig.timings.vsync_back_porch = kRgbVsyncBackPorch;
    panelConfig.timings.vsync_front_porch = kRgbVsyncFrontPorch;
    panelConfig.timings.flags.hsync_idle_low = 1;
    panelConfig.timings.flags.vsync_idle_low = 1;
    panelConfig.timings.flags.de_idle_high = 0;
    panelConfig.timings.flags.pclk_active_neg = kRgbPclkActiveNeg ? 1 : 0;
    panelConfig.timings.flags.pclk_idle_high = kRgbPclkIdleHigh ? 1 : 0;
    panelConfig.data_width = 16;
    panelConfig.sram_trans_align = 4;
    panelConfig.psram_trans_align = 64;
    panelConfig.hsync_gpio_num = 39;
    panelConfig.vsync_gpio_num = 41;
    panelConfig.de_gpio_num = 40;
    panelConfig.pclk_gpio_num = 42;
    panelConfig.disp_gpio_num = -1;
    panelConfig.data_gpio_nums[0] = 15;
    panelConfig.data_gpio_nums[1] = 16;
    panelConfig.data_gpio_nums[2] = 4;
    panelConfig.data_gpio_nums[3] = 45;
    panelConfig.data_gpio_nums[4] = 48;
    panelConfig.data_gpio_nums[5] = 47;
    panelConfig.data_gpio_nums[6] = 21;
    panelConfig.data_gpio_nums[7] = 14;
    panelConfig.data_gpio_nums[8] = 8;
    panelConfig.data_gpio_nums[9] = 3;
    panelConfig.data_gpio_nums[10] = 46;
    panelConfig.data_gpio_nums[11] = 9;
    panelConfig.data_gpio_nums[12] = 1;
    panelConfig.data_gpio_nums[13] = 5;
    panelConfig.data_gpio_nums[14] = 6;
    panelConfig.data_gpio_nums[15] = 7;
    panelConfig.on_frame_trans_done = rgbFrameDoneCallback;
    panelConfig.user_ctx = nullptr;
    panelConfig.flags.disp_active_low = 1;
    panelConfig.flags.relax_on_idle = 0;
    panelConfig.flags.fb_in_psram = 1;

    esp_err_t err = esp_lcd_new_rgb_panel(&panelConfig, &gPanel);
    if (err != ESP_OK)
    {
        Serial.printf(""LCD panel create failed: 0x%X\n"", static_cast<unsigned>(err));
        return false;
    }

    ESP_ERROR_CHECK(esp_lcd_panel_reset(gPanel));
    ESP_ERROR_CHECK(esp_lcd_panel_init(gPanel));

    ledcSetup(kBacklightPwmChannel, kBacklightPwmHz, kBacklightPwmBits);
    ledcAttachPin(kTftBacklight, kBacklightPwmChannel);
    ledcWrite(kBacklightPwmChannel, kBacklightDuty);

    Serial.println(""LCD smoke panel initialized"");
    return true;
}

void palmDisplayShowLoaderSmoke(const PalmLoadedApp* apps, size_t appCount)
{
    gDisplayApps = apps;
    gDisplayAppCount = appCount;
    ++gDisplayGeneration;
    renderSmokeFrame();

    if (gPanel != nullptr)
    {
        ESP_ERROR_CHECK(esp_lcd_panel_draw_bitmap(gPanel, 0, 0, kScreenW, kScreenH, gFrameBuffer));
        esp_lcd_panel_disp_on_off(gPanel, true);
    }

    Serial.printf(""LCD smoke display apps=%u generation=%u\n"",
        static_cast<unsigned>(gDisplayAppCount),
        static_cast<unsigned>(gDisplayGeneration));
}
"
    End Function

    Private Shared Function RenderManifestHeader() As String
        Return "#pragma once

#include <stddef.h>
#include <stdint.h>

struct PalmGeneratedApp
{
    const char* name;
    const char* creator;
    const char* type;
    const char* path;
    uint32_t sourceRomOffset;
    uint32_t sizeBytes;
};

extern const PalmGeneratedApp kPalmGeneratedApps[];
extern const size_t kPalmGeneratedAppCount;
"
    End Function

    Private Shared Function RenderManifestCpp(request As Esp32ProjectRequest, apps As List(Of ExportedPalmApplication)) As String
        Dim builder As New StringBuilder()
        builder.AppendLine("#include ""palm_rom_manifest.h""")
        builder.AppendLine()
        builder.AppendLine("const PalmGeneratedApp kPalmGeneratedApps[] =")
        builder.AppendLine("{")

        For Each app In apps
            builder.AppendLine("    {")
            builder.AppendLine($"        ""{EscapeCppString(app.Name)}"",")
            builder.AppendLine($"        ""{EscapeCppString(app.CreatorCode)}"",")
            builder.AppendLine($"        ""{EscapeCppString(app.TypeCode)}"",")
            builder.AppendLine($"        ""/{EscapeCppString(app.RelativePath)}"",")
            builder.AppendLine($"        0x{app.SourceOffset:X8}u,")
            builder.AppendLine($"        {app.Size}u")
            builder.AppendLine("    },")
        Next

        builder.AppendLine("};")
        builder.AppendLine()
        builder.AppendLine("const size_t kPalmGeneratedAppCount = sizeof(kPalmGeneratedApps) / sizeof(kPalmGeneratedApps[0]);")
        Return builder.ToString()
    End Function

    Private Shared Function RenderGenerationNotes(request As Esp32ProjectRequest, romSize As Integer, databaseCount As Integer, apps As List(Of ExportedPalmApplication)) As String
        Dim builder As New StringBuilder()
        builder.AppendLine($"ROM: {request.RomPath}")
        builder.AppendLine($"ROM size: {romSize} bytes")
        builder.AppendLine($"Project: {request.ProjectName}")
        builder.AppendLine($"Hardware profile: {request.HardwareProfile}")
        builder.AppendLine($"Databases detected: {databaseCount}")
        builder.AppendLine($"Applications exported: {apps.Count}")
        builder.AppendLine()

        For Each app In apps
            builder.AppendLine($"- {app.Name} ({app.CreatorCode}) offset=0x{app.SourceOffset:X8} size={app.Size} fsPath=/{app.RelativePath}")
        Next

        Return builder.ToString()
    End Function

    Private Shared Function RenderReadme(request As Esp32ProjectRequest, apps As List(Of ExportedPalmApplication)) As String
        Dim builder As New StringBuilder()
        builder.AppendLine($"# {request.ProjectName}")
        builder.AppendLine()
        builder.AppendLine("Generated ESP32 Palm compatibility project.")
        builder.AppendLine()
        builder.AppendLine("This project does not natively recompile 68K code yet. It packages extracted Palm applications and prepares an ESP32 runtime where 68K app code executes in an emulator while Palm OS traps are implemented in native C/C++.")
        builder.AppendLine()
        builder.AppendLine("## Exported Applications")
        builder.AppendLine()

        For Each app In apps
            builder.AppendLine($"- `{app.Name}` (`{app.CreatorCode}`): `/{app.RelativePath}`")
        Next

        builder.AppendLine()
        builder.AppendLine("## Test")
        builder.AppendLine()
        builder.AppendLine("```text")
        builder.AppendLine("test.cmd")
        builder.AppendLine("```")
        builder.AppendLine()
        builder.AppendLine("Or run the steps manually:")
        builder.AppendLine()
        builder.AppendLine("```text")
        builder.AppendLine("platformio run")
        builder.AppendLine("platformio run -t uploadfs")
        builder.AppendLine("platformio run -t upload")
        builder.AppendLine("platformio device monitor")
        builder.AppendLine("```")
        builder.AppendLine()
        builder.AppendLine("The first runtime test mounts SPIFFS, opens each generated PRC, parses its resource table, and prints `code` resources over serial.")
        builder.AppendLine()
        builder.AppendLine("## Runtime Work Still Needed")
        builder.AppendLine()
        builder.AppendLine("- Integrate the 68K emulator wrapper.")
        builder.AppendLine("- Implement the Palm memory/resource/database managers.")
        builder.AppendLine("- Implement native Palm OS trap dispatch.")
        builder.AppendLine("- Connect the selected ESP32 board display/input/storage profile.")
        Return builder.ToString()
    End Function

    Private Shared Function RenderTestCmd() As String
        Return "@echo off
setlocal
cd /d ""%~dp0""

set ""PIO=C:\Users\doubl\.platformio\penv\Scripts\platformio.exe""
if not exist ""%PIO%"" set ""PIO=platformio""

echo [1/4] Building firmware
""%PIO%"" run || exit /b %ERRORLEVEL%

echo [2/4] Building SPIFFS image
""%PIO%"" run -t buildfs || exit /b %ERRORLEVEL%

echo [3/4] Uploading SPIFFS image
""%PIO%"" run -t uploadfs || exit /b %ERRORLEVEL%

echo [4/4] Uploading firmware
""%PIO%"" run -t upload || exit /b %ERRORLEVEL%

echo Opening serial monitor. Press Ctrl+C to close it.
""%PIO%"" device monitor
"
    End Function

    Private Shared Sub WriteText(filePath As String, content As String)
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath))
        File.WriteAllText(filePath, content, New UTF8Encoding(False))
    End Sub

    Private Shared Function EscapeCppString(value As String) As String
        Return value.Replace("\"c, "/"c).Replace("""", "\""")
    End Function

    Private Shared Function SanitizeIdentifier(value As String) As String
        Dim chars = value.Select(Function(ch) If(Char.IsLetterOrDigit(ch) OrElse ch = "_"c OrElse ch = "-"c, ch, "_"c)).ToArray()
        Return New String(chars)
    End Function
End Class
