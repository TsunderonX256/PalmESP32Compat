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
        WriteText(Path.Combine(outputDirectory, "src", "palm_traps.h"), RenderPalmTrapsHeader())
        WriteText(Path.Combine(outputDirectory, "src", "palm_traps.cpp"), RenderPalmTrapsCpp())
        WriteText(Path.Combine(outputDirectory, "src", "palm_display.h"), RenderPalmDisplayHeader())
        WriteText(Path.Combine(outputDirectory, "src", "palm_display.cpp"), RenderPalmDisplayCpp())
        CopyMusashiCore(request, outputDirectory)
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

    Private Shared Sub CopyMusashiCore(request As Esp32ProjectRequest, outputDirectory As String)
        Dim sourceDirectory = FindMusashiDirectory(request)
        If String.IsNullOrWhiteSpace(sourceDirectory) Then
            Throw New DirectoryNotFoundException("Musashi-master was not found beside the ROM source directory or the generator repository.")
        End If

        Dim targetDirectory = Path.Combine(outputDirectory, "src", "musashi")
        Directory.CreateDirectory(targetDirectory)

        Dim files = {
            "m68k.h",
            "m68kconf.h",
            "m68kcpu.c",
            "m68kcpu.h",
            "m68kfpu.c",
            "m68kmmu.h",
            "m68kops.c",
            "m68kops.h",
            "m68k_68000_strict_aliases.h"
        }

        For Each fileName In files
            Dim sourcePath = Path.Combine(sourceDirectory, fileName)
            Dim targetPath = Path.Combine(targetDirectory, fileName)
            If fileName.Equals("m68kconf.h", StringComparison.OrdinalIgnoreCase) Then
                Dim configText = File.ReadAllText(sourcePath)
                configText = configText.Replace("#define M68K_INSTRUCTION_HOOK     OPT_OFF", "#define M68K_INSTRUCTION_HOOK     OPT_ON")
                File.WriteAllText(targetPath, configText)
            Else
                File.Copy(sourcePath, targetPath, True)
            End If
        Next

        Dim softFloatTarget = Path.Combine(targetDirectory, "softfloat")
        Directory.CreateDirectory(softFloatTarget)
        Dim softFloatHeaders = {
            "mamesf.h",
            "milieu.h",
            "softfloat.h",
            "softfloat-macros.h",
            "softfloat-specialize.h"
        }

        For Each fileName In softFloatHeaders
            File.Copy(Path.Combine(sourceDirectory, "softfloat", fileName), Path.Combine(softFloatTarget, fileName), True)
        Next
    End Sub

    Private Shared Function FindMusashiDirectory(request As Esp32ProjectRequest) As String
        Dim candidates As New List(Of String)()
        If Not String.IsNullOrWhiteSpace(request.RomPath) Then
            Dim romDirectory = Path.GetDirectoryName(Path.GetFullPath(request.RomPath))
            candidates.Add(Path.Combine(romDirectory, "Musashi-master"))
        End If

        candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ESP PALM", "Musashi-master")))
        candidates.Add(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "ESP PALM", "Musashi-master")))

        For Each candidate In candidates
            If Directory.Exists(candidate) AndAlso File.Exists(Path.Combine(candidate, "m68kcpu.c")) AndAlso File.Exists(Path.Combine(candidate, "m68kops.c")) Then
                Return candidate
            End If
        Next

        Return ""
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
build_src_filter =
  +<*>
  -<musashi/m68kfpu.c>
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
    palmDisplayBacklightOff();
    Serial.println(""LCD/backlight disabled for runtime trap probes"");

    if (loadGeneratedPalmApps())
    {
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

struct PalmLoadedResource
{
    char type[5];
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
    uint16_t loadedResourceCount;
    uint16_t codeResourceCount;
    PalmLoadedResource* resources;
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

static bool loadAnyResource(File& file, const PalmResourceEntry& entry, PalmLoadedResource& loaded)
{
    if (entry.size == 0)
    {
        return false;
    }

    strncpy(loaded.type, entry.type, sizeof(loaded.type));
    loaded.type[4] = '\0';
    loaded.id = entry.id;
    loaded.size = entry.size;
    loaded.bytes = nullptr;
    loaded.checksum = 0;

    uint8_t* bytes = static_cast<uint8_t*>(malloc(entry.size));
    if (bytes == nullptr)
    {
        Serial.printf(""      %s #%u allocation failed: %u bytes\n"",
            entry.type,
            static_cast<unsigned>(entry.id),
            static_cast<unsigned>(entry.size));
        return false;
    }

    if (!file.seek(entry.offset, SeekSet))
    {
        Serial.printf(""      %s #%u seek failed\n"",
            entry.type,
            static_cast<unsigned>(entry.id));
        free(bytes);
        return false;
    }

    const size_t readCount = file.read(bytes, entry.size);
    if (readCount != entry.size)
    {
        Serial.printf(""      %s #%u short read: %u/%u bytes\n"",
            entry.type,
            static_cast<unsigned>(entry.id),
            static_cast<unsigned>(readCount),
            static_cast<unsigned>(entry.size));
        free(bytes);
        return false;
    }

    loaded.bytes = bytes;
    loaded.checksum = fnv1a32(bytes, entry.size);
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

    loadedApp.resources = static_cast<PalmLoadedResource*>(calloc(resourceCount, sizeof(PalmLoadedResource)));
    if (loadedApp.resources == nullptr)
    {
        Serial.printf(""  resource table allocation failed: %u entries\n"",
            static_cast<unsigned>(resourceCount));
        return false;
    }

    loadedApp.codeResources = static_cast<PalmLoadedCodeResource*>(calloc(codeCount, sizeof(PalmLoadedCodeResource)));
    if (loadedApp.codeResources == nullptr)
    {
        Serial.printf(""  code resource table allocation failed: %u entries\n"",
            static_cast<unsigned>(codeCount));
        return false;
    }

    uint16_t loadedResourceIndex = 0;
    uint16_t loadedCodeIndex = 0;
    for (uint16_t i = 0; i < resourceCount; ++i)
    {
        PalmResourceEntry entry;
        if (!readResourceEntry(file, i, resourceCount, fileSize, entry))
        {
            continue;
        }

        if (loadedResourceIndex < resourceCount && loadAnyResource(file, entry, loadedApp.resources[loadedResourceIndex]))
        {
            if (strcmp(entry.type, ""code"") != 0)
            {
                Serial.printf(""    resource %s #%u offset=%u size=%u\n"",
                    entry.type,
                    static_cast<unsigned>(entry.id),
                    static_cast<unsigned>(entry.offset),
                    static_cast<unsigned>(entry.size));
            }
            ++loadedResourceIndex;
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

    loadedApp.loadedResourceCount = loadedResourceIndex;
    loadedApp.codeResourceCount = loadedCodeIndex;
    Serial.printf(""  resident resources: %u, code resources: %u\n"",
        static_cast<unsigned>(loadedApp.loadedResourceCount),
        static_cast<unsigned>(loadedApp.codeResourceCount));
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
        Serial.printf(""  resident app %u: %s resources=%u codeResources=%u\n"",
            static_cast<unsigned>(appIndex),
            app.dbName,
            static_cast<unsigned>(app.loadedResourceCount),
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

    Private Shared Function RenderPalmTrapsHeader() As String
        Return "#pragma once

#include <stddef.h>
#include <stdint.h>

struct PalmLoadedApp;

struct PalmTrapFrame
{
    uint16_t selector;
    uint16_t opcode;
    uint32_t pc;
    uint32_t resumePc;
    uint32_t sp;
    uint32_t d[8];
    uint32_t a[8];
    uint32_t stackLongs[8];
};

struct PalmTrapResult
{
    bool handled;
    bool stop;
    uint32_t resumePc;
    uint32_t d0;
    uint32_t a0;
};

const char* palmTrapName(uint16_t selector);
void palmTrapSetAppContext(const PalmLoadedApp* app);
PalmTrapResult palmDispatchTrap(const PalmTrapFrame& frame);
"
    End Function

    Private Shared Function RenderPalmTrapsCpp() As String
        Return "#include ""palm_traps.h""

#include ""palm_display.h""
#include ""palm_prc_loader.h""

#include <Arduino.h>
#include <string.h>

extern ""C"" unsigned int m68k_read_memory_8(unsigned int address);
extern ""C"" unsigned int m68k_read_memory_16(unsigned int address);
extern ""C"" unsigned int m68k_read_memory_32(unsigned int address);
extern ""C"" void m68k_write_memory_8(unsigned int address, unsigned int value);
extern ""C"" void m68k_write_memory_16(unsigned int address, unsigned int value);
extern ""C"" void m68k_write_memory_32(unsigned int address, unsigned int value);

static constexpr uint32_t kTrapScratchBase = 0x00001020u;
static constexpr uint32_t kSysAppLaunchBlock = kTrapScratchBase;
static constexpr uint32_t kFakeMemoDatabaseId = 0x00020000u;
static constexpr uint32_t kFakeMemoDatabaseRef = 0x00020010u;
static constexpr uint32_t kFakeMemoRecordHandle = 0x00020020u;
static constexpr uint32_t kFakeResourceHandle = 0x00020030u;
static constexpr uint32_t kFakeMemoRecordPtr = 0x00001080u;
static constexpr uint32_t kFakeResourcePtr = 0x000010A0u;
static bool gFakeMemoDatabaseCreated = false;
static const PalmLoadedApp* gTrapApp = nullptr;
static const PalmLoadedResource* gLockedResource = nullptr;
static bool gUseSyntheticStringResource = false;
static bool gRuntimeUiProbeShown = false;
static uint32_t gRuntimeUiProbeTrapCount = 0;
static bool gMemoPadProbeShown = false;
static char gCapturedUiText[32] = """";

static bool looksWritablePointer(uint32_t address);

void palmTrapSetAppContext(const PalmLoadedApp* app)
{
    gTrapApp = app;
    gLockedResource = nullptr;
    gUseSyntheticStringResource = false;
    gRuntimeUiProbeShown = false;
    gRuntimeUiProbeTrapCount = 0;
    gMemoPadProbeShown = false;
    gCapturedUiText[0] = '\0';
}

static PalmTrapResult handledTrap(const PalmTrapFrame& frame, uint32_t d0 = 0, uint32_t a0 = 0)
{
    PalmTrapResult result{};
    result.handled = true;
    result.stop = false;
    result.resumePc = frame.resumePc;
    result.d0 = d0;
    result.a0 = a0;
    return result;
}

static PalmTrapResult stopTrap(const PalmTrapFrame& frame, uint32_t d0 = 0, uint32_t a0 = 0)
{
    PalmTrapResult result = handledTrap(frame, d0, a0);
    result.stop = true;
    return result;
}

static uint16_t stackWord(const PalmTrapFrame& frame, uint32_t offset)
{
    return static_cast<uint16_t>(m68k_read_memory_16(frame.sp + offset));
}

static uint32_t stackLong(const PalmTrapFrame& frame, uint32_t offset)
{
    return m68k_read_memory_32(frame.sp + offset);
}

static uint32_t resourceTypeToU32(const char type[5])
{
    return (static_cast<uint32_t>(static_cast<uint8_t>(type[0])) << 24) |
        (static_cast<uint32_t>(static_cast<uint8_t>(type[1])) << 16) |
        (static_cast<uint32_t>(static_cast<uint8_t>(type[2])) << 8) |
        static_cast<uint32_t>(static_cast<uint8_t>(type[3]));
}

static void resourceTypeToString(uint32_t type, char out[5])
{
    out[0] = static_cast<char>((type >> 24) & 0xffu);
    out[1] = static_cast<char>((type >> 16) & 0xffu);
    out[2] = static_cast<char>((type >> 8) & 0xffu);
    out[3] = static_cast<char>(type & 0xffu);
    out[4] = '\0';
}

static const PalmLoadedResource* findLoadedResource(uint32_t type, uint16_t id)
{
    if (gTrapApp == nullptr)
    {
        return nullptr;
    }

    for (uint16_t i = 0; i < gTrapApp->loadedResourceCount; ++i)
    {
        const PalmLoadedResource& resource = gTrapApp->resources[i];
        if (resourceTypeToU32(resource.type) == type && resource.id == id)
        {
            return &resource;
        }
    }

    return nullptr;
}

static void copyResourceToMemory(const PalmLoadedResource& resource, uint32_t address)
{
    const uint32_t copySize = resource.size > 256u ? 256u : resource.size;
    for (uint32_t i = 0; i < copySize; ++i)
    {
        m68k_write_memory_8(address + i, resource.bytes[i]);
    }
}

static bool readCString(uint32_t address, char* out, uint32_t maxBytes)
{
    if (!looksWritablePointer(address) || maxBytes == 0)
    {
        return false;
    }

    uint32_t count = 0;
    while (count + 1u < maxBytes)
    {
        const uint8_t value = static_cast<uint8_t>(m68k_read_memory_8(address + count) & 0xffu);
        if (value == 0)
        {
            break;
        }
        if (value < 32 || value > 126)
        {
            break;
        }

        out[count++] = static_cast<char>(value);
    }

    out[count] = '\0';
    return count > 0;
}

static void showMemoPadProbe(uint16_t selector, const char* capturedText)
{
    ++gRuntimeUiProbeTrapCount;
    if (capturedText != nullptr && capturedText[0] != '\0')
    {
        strncpy(gCapturedUiText, capturedText, sizeof(gCapturedUiText));
        gCapturedUiText[sizeof(gCapturedUiText) - 1] = '\0';
    }

    if (!gMemoPadProbeShown || (capturedText != nullptr && capturedText[0] != '\0'))
    {
        gMemoPadProbeShown = true;
        gRuntimeUiProbeShown = true;
        palmDisplayShowMemoPadProbe(selector, gRuntimeUiProbeTrapCount, gCapturedUiText);
    }
}

static bool looksWritablePointer(uint32_t address)
{
    return address >= 0x00001000u && address < 0x00001100u;
}

static void writeCString(uint32_t address, const char* text, uint32_t maxBytes)
{
    if (!looksWritablePointer(address) || maxBytes == 0)
    {
        return;
    }

    uint32_t i = 0;
    while (text[i] != '\0' && i + 1u < maxBytes)
    {
        m68k_write_memory_8(address + i, static_cast<unsigned int>(text[i]));
        ++i;
    }

    m68k_write_memory_8(address + i, 0);
}

static void writeWordIfPointer(uint32_t address, uint16_t value)
{
    if (looksWritablePointer(address))
    {
        m68k_write_memory_16(address, value);
    }
}

static void writeLongIfPointer(uint32_t address, uint32_t value)
{
    if (looksWritablePointer(address))
    {
        m68k_write_memory_32(address, value);
    }
}

const char* palmTrapName(uint16_t selector)
{
    switch (selector)
    {
        case 0xA012: return ""MemPtrFree"";
        case 0xA013: return ""MemPtrNew"";
        case 0xA07F: return ""DmCreateDatabaseFromImage"";
        case 0xA041: return ""DmCreateDatabase"";
        case 0xA042: return ""SelectorA042"";
        case 0xA04A: return ""SelectorA04A"";
        case 0xA04C: return ""SelectorA04C"";
        case 0xA046: return ""SelectorA046"";
        case 0xA047: return ""SelectorA047"";
        case 0xA059: return ""SelectorA059"";
        case 0xA05B: return ""UIProbeA05B"";
        case 0xA05F: return ""SelectorA05F"";
        case 0xA020: return ""MemHandleLock"";
        case 0xA021: return ""MemHandleUnlock"";
        case 0xA022: return ""MemHandleFree"";
        case 0xA035: return ""SelectorA035"";
        case 0xA036: return ""SelectorA036"";
        case 0xA071: return ""UIProbeA071"";
        case 0xA075: return ""DmFindDatabaseByTypeCreator"";
        case 0xA07E: return ""SelectorA07E"";
        case 0xA061: return ""SelectorA061"";
        case 0xA084: return ""SelectorA084"";
        case 0xA08F: return ""SysAppStartup"";
        case 0xA090: return ""SysAppExit"";
        case 0xA0A9: return ""UIProbeTextOrDrawA0A9"";
        case 0xA104: return ""UIProbeFormA104"";
        case 0xA11D: return ""UIProbeStringA11D"";
        case 0xA19B: return ""UIProbeA19B"";
        case 0xA1A0: return ""UIProbeA1A0"";
        case 0xA1BF: return ""UIProbeA1BF"";
        case 0xA2D3: return ""PrefGetAppPreferences"";
        case 0xA2FC: return ""SelectorA2FC"";
        case 0xA12F: return ""EvtWakeup"";
        case 0xA27B: return ""FtrGet"";
        case 0xA3AB: return ""UIBrightnessAdjust"";
        case 0xA3FF: return ""SelectorA3FF"";
        case 0xA9F0: return ""SysAppLaunch-or-launch-dispatch"";
        default: return ""unknown"";
    }
}

PalmTrapResult palmDispatchTrap(const PalmTrapFrame& frame)
{
    switch (frame.selector)
    {
        case 0xA041:
        {
            const uint16_t cardNo = stackWord(frame, 0);
            const uint32_t nameP = stackLong(frame, 2);
            const uint32_t creator = stackLong(frame, 6);
            const uint32_t type = stackLong(frame, 10);
            const uint16_t resDB = stackWord(frame, 14);
            const bool looksLikeMemoCreate =
                creator == 0x6D656D6Fu &&
                type == 0x44415441u;
            if (looksLikeMemoCreate)
            {
                gFakeMemoDatabaseCreated = true;
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s card=%u nameP=0x%08X creator=0x%08X type=0x%08X resDB=%u fakeDb=%s\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(cardNo),
                static_cast<unsigned>(nameP),
                static_cast<unsigned>(creator),
                static_cast<unsigned>(type),
                static_cast<unsigned>(resDB),
                gFakeMemoDatabaseCreated ? ""yes"" : ""no"");
            return handledTrap(frame, 0, 0);
        }

        case 0xA042:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s dbOrRef=0x%08X mode=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(frame.stackLongs[1]));
            return handledTrap(frame, 0, kFakeMemoDatabaseRef);

        case 0xA059:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s dbRef=0x%08X arg1=0x%08X arg2=0x%08X -> handle=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(frame.stackLongs[1]),
                static_cast<unsigned>(frame.stackLongs[2]),
                static_cast<unsigned>(kFakeMemoRecordHandle));
            writeCString(kFakeMemoRecordPtr, """", 16u);
            return handledTrap(frame, 0, kFakeMemoRecordHandle);

        case 0xA05F:
        {
            const uint32_t resourceType = frame.stackLongs[0];
            const uint16_t resourceId = stackWord(frame, 4);
            char resourceTypeName[5];
            resourceTypeToString(resourceType, resourceTypeName);
            gLockedResource = findLoadedResource(resourceType, resourceId);
            gUseSyntheticStringResource = gLockedResource == nullptr && resourceType == 0x74535452u;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s resource=%s #%u found=%s synthetic=%s -> handle=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                resourceTypeName,
                static_cast<unsigned>(resourceId),
                gLockedResource != nullptr ? ""yes"" : ""no"",
                gUseSyntheticStringResource ? ""yes"" : ""no"",
                static_cast<unsigned>(kFakeResourceHandle));
            return handledTrap(frame, 0, (gLockedResource != nullptr || gUseSyntheticStringResource) ? kFakeResourceHandle : 0u);
        }

        case 0xA020:
        {
            const uint32_t handle = frame.stackLongs[0];
            const uint32_t ptr = handle == kFakeResourceHandle ? kFakeResourcePtr : kFakeMemoRecordPtr;
            if (handle == kFakeResourceHandle && gLockedResource != nullptr)
            {
                copyResourceToMemory(*gLockedResource, kFakeResourcePtr);
            }
            else if (handle == kFakeResourceHandle && gUseSyntheticStringResource)
            {
                writeCString(kFakeResourcePtr, ""Memo"", 16u);
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s handle=0x%08X -> ptr=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(handle),
                static_cast<unsigned>(ptr));
            return handledTrap(frame, 0, ptr);
        }

        case 0xA021:
        case 0xA022:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s handle=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]));
            return handledTrap(frame, 0, 0);

        case 0xA061:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s handle=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]));
            return handledTrap(frame, 0, 0);

        case 0xA035:
        case 0xA036:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s args=0x%08X,0x%08X,0x%08X,0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(frame.stackLongs[1]),
                static_cast<unsigned>(frame.stackLongs[2]),
                static_cast<unsigned>(frame.stackLongs[3]));
            return handledTrap(frame, 0, 0);

        case 0xA07E:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s args=0x%08X,0x%08X,0x%08X -> handle=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(frame.stackLongs[1]),
                static_cast<unsigned>(frame.stackLongs[2]),
                static_cast<unsigned>(kFakeResourceHandle));
            writeCString(kFakeResourcePtr, ""Memo"", 16u);
            return handledTrap(frame, 0, kFakeResourceHandle);

        case 0xA2FC:
        case 0xA3FF:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s args=0x%08X,0x%08X,0x%08X,0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(frame.stackLongs[1]),
                static_cast<unsigned>(frame.stackLongs[2]),
                static_cast<unsigned>(frame.stackLongs[3]));
            return handledTrap(frame, 0, 0);

        case 0xA104:
        case 0xA05B:
        case 0xA071:
        case 0xA19B:
        case 0xA11D:
        case 0xA0A9:
        case 0xA1BF:
        case 0xA1A0:
        {
            char capturedText[32];
            capturedText[0] = '\0';
            if (frame.selector == 0xA11D)
            {
                readCString(frame.stackLongs[0], capturedText, sizeof(capturedText));
            }
            showMemoPadProbe(frame.selector, capturedText);
            Serial.printf(""  trap dispatch: UI probe selector=0x%04X name=%s stack[0..2]=0x%08X,0x%08X,0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(frame.stackLongs[1]),
                static_cast<unsigned>(frame.stackLongs[2]));
            return handledTrap(frame, 0, 0);
        }

        case 0xA2D3:
        {
            const uint32_t creator = stackLong(frame, 0);
            const uint16_t id = stackWord(frame, 4);
            const uint32_t prefsP = stackLong(frame, 6);
            const uint32_t prefsSizeP = stackLong(frame, 10);
            const uint16_t saved = stackWord(frame, 14);
            writeWordIfPointer(prefsSizeP, 0);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s creator=0x%08X id=%u prefsP=0x%08X sizeP=0x%08X saved=%u -> noPreferenceFound\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(creator),
                static_cast<unsigned>(id),
                static_cast<unsigned>(prefsP),
                static_cast<unsigned>(prefsSizeP),
                static_cast<unsigned>(saved));
            return handledTrap(frame, 0xFFFFFFFFu, 0);
        }

        case 0xA27B:
        {
            const uint32_t creator = stackLong(frame, 0);
            const uint16_t featureNum = stackWord(frame, 4);
            const uint32_t valueP = stackLong(frame, 6);
            writeLongIfPointer(valueP, 0);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s creator=0x%08X feature=%u valueP=0x%08X -> noSuchFeature\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(creator),
                static_cast<unsigned>(featureNum),
                static_cast<unsigned>(valueP));
            return handledTrap(frame, 1, 0);
        }

        case 0xA046:
        {
            const uint16_t cardNo = stackWord(frame, 0);
            const uint32_t dbID = stackLong(frame, 2);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s card=%u db=0x%08X outWords=0x%08X,0x%08X outLongs=0x%08X,0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(cardNo),
                static_cast<unsigned>(dbID),
                static_cast<unsigned>(stackLong(frame, 6)),
                static_cast<unsigned>(stackLong(frame, 10)),
                static_cast<unsigned>(stackLong(frame, 14)),
                static_cast<unsigned>(stackLong(frame, 18)));
            writeWordIfPointer(stackLong(frame, 6), 0);
            writeWordIfPointer(stackLong(frame, 10), 0);
            writeLongIfPointer(stackLong(frame, 14), 0);
            writeLongIfPointer(stackLong(frame, 18), 0);
            return handledTrap(frame, 0, 0);
        }

        case 0xA047:
        {
            const uint16_t cardNo = stackWord(frame, 0);
            const uint32_t dbID = stackLong(frame, 2);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s card=%u db=0x%08X sizeOut=0x%08X recordsOut=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(cardNo),
                static_cast<unsigned>(dbID),
                static_cast<unsigned>(stackLong(frame, 6)),
                static_cast<unsigned>(stackLong(frame, 10)));
            writeLongIfPointer(stackLong(frame, 6), 0);
            writeWordIfPointer(stackLong(frame, 10), 0);
            return handledTrap(frame, 0, 0);
        }

        case 0xA04A:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s db=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]));
            return handledTrap(frame, 0, 0);

        case 0xA04C:
        {
            const uint32_t dbID = stackLong(frame, 0);
            const uint32_t nameP = stackLong(frame, 4);
            const uint32_t attributesP = stackLong(frame, 8);
            const uint32_t versionP = stackLong(frame, 12);
            const uint32_t crDateP = stackLong(frame, 16);
            const uint32_t modDateP = stackLong(frame, 20);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s db=0x%08X nameP=0x%08X attrP=0x%08X verP=0x%08X dates=0x%08X,0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(dbID),
                static_cast<unsigned>(nameP),
                static_cast<unsigned>(attributesP),
                static_cast<unsigned>(versionP),
                static_cast<unsigned>(crDateP),
                static_cast<unsigned>(modDateP));
            writeCString(nameP, ""MemoDB"", 32u);
            writeWordIfPointer(attributesP, 0);
            writeWordIfPointer(versionP, 1);
            writeLongIfPointer(crDateP, 0);
            writeLongIfPointer(modDateP, 0);
            return handledTrap(frame, 0, 0);
        }

        case 0xA075:
        {
            const uint32_t requestedType = frame.stackLongs[0];
            const uint32_t requestedCreator = frame.stackLongs[1];
            const bool isMemoData = requestedType == 0x44415441u && requestedCreator == 0x6D656D6Fu;
            const uint32_t foundDb = (gFakeMemoDatabaseCreated && isMemoData) ? kFakeMemoDatabaseId : 0u;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s fakeDb=%s type=0x%08X creator=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                gFakeMemoDatabaseCreated ? ""yes"" : ""no"",
                static_cast<unsigned>(requestedType),
                static_cast<unsigned>(requestedCreator));
            return handledTrap(frame, 0, foundDb);
        }

        case 0xA08F:
            // Minimal SysAppStartup model: supply cmd/cmdPBP/launchFlags block
            // expected by the generated Palm startup glue.
            m68k_write_memory_16(kSysAppLaunchBlock + 0u, 0u);
            m68k_write_memory_32(kSysAppLaunchBlock + 2u, 0u);
            m68k_write_memory_16(kSysAppLaunchBlock + 6u, 0u);
            m68k_write_memory_32(frame.stackLongs[0], kSysAppLaunchBlock);
            m68k_write_memory_32(frame.stackLongs[1], 0u);
            m68k_write_memory_32(frame.stackLongs[2], 0u);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s launchBlock=0x%08X out[0..2]=0x%08X,0x%08X,0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(kSysAppLaunchBlock),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(frame.stackLongs[1]),
                static_cast<unsigned>(frame.stackLongs[2]));
            return handledTrap(frame, 0, 0);

        case 0xA090:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s args=0x%08X,0x%08X,0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(frame.stackLongs[1]),
                static_cast<unsigned>(frame.stackLongs[2]));
            return stopTrap(frame, 0, 0);

        default:
            Serial.printf(""  trap dispatch: stub selector=0x%04X name=%s stack[0..2]=0x%08X,0x%08X,0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(frame.stackLongs[1]),
                static_cast<unsigned>(frame.stackLongs[2]));
            return handledTrap(frame, 0, 0);
    }
}
"
    End Function

    Private Shared Function RenderPalm68KRuntimeCpp() As String
        Return "#include ""palm_68k_runtime.h""

#include <Arduino.h>
#include <string.h>
#include ""palm_traps.h""

extern ""C"" {
#include ""musashi/m68k.h""
}

static constexpr uint32_t kProbeBase = 0x00001000u;
static constexpr uint32_t kProbeRamSize = 256u;
static constexpr uint32_t kPalmCodeBase = 0x00010000u;
static constexpr uint32_t kPalmEntryProbeOffset = 0x10u;
static uint8_t gProbeRam[kProbeRamSize];
static bool gMusashiInitialized = false;
static uint32_t gLastUnknownRead = 0;
static uint32_t gLastUnknownWrite = 0;
static const PalmLoadedCodeResource* gMappedCode = nullptr;
static bool gTrapHookEnabled = false;
static uint16_t gInstructionHookCount = 0;
static uint16_t gTrapHookCount = 0;

static bool probeAddressToOffset(uint32_t address, uint32_t width, uint32_t& offset)
{
    if (address < kProbeBase)
    {
        return false;
    }

    offset = address - kProbeBase;
    return offset + width <= kProbeRamSize;
}

static uint8_t probeRead8(uint32_t address)
{
    uint32_t offset = 0;
    if (gMappedCode != nullptr && address >= kPalmCodeBase)
    {
        offset = address - kPalmCodeBase;
        if (offset < gMappedCode->size)
        {
            return gMappedCode->bytes[offset];
        }
    }

    if (!probeAddressToOffset(address, 1, offset))
    {
        gLastUnknownRead = address;
        return 0;
    }

    return gProbeRam[offset];
}

static void probeWrite8(uint32_t address, uint8_t value)
{
    uint32_t offset = 0;
    if (!probeAddressToOffset(address, 1, offset))
    {
        gLastUnknownWrite = address;
        return;
    }

    gProbeRam[offset] = value;
}

extern ""C"" unsigned int m68k_read_memory_8(unsigned int address)
{
    return probeRead8(address);
}

extern ""C"" unsigned int m68k_read_memory_16(unsigned int address)
{
    return (static_cast<unsigned int>(probeRead8(address)) << 8) |
        static_cast<unsigned int>(probeRead8(address + 1u));
}

extern ""C"" unsigned int m68k_read_memory_32(unsigned int address)
{
    return (m68k_read_memory_16(address) << 16) | m68k_read_memory_16(address + 2u);
}

extern ""C"" unsigned int palm_read_instr_16(unsigned int address)
{
    return m68k_read_memory_16(address);
}

extern ""C"" unsigned int m68k_read_immediate_16(unsigned int address)
{
    return m68k_read_memory_16(address);
}

extern ""C"" unsigned int m68k_read_immediate_32(unsigned int address)
{
    return m68k_read_memory_32(address);
}

extern ""C"" unsigned int m68k_read_pcrelative_8(unsigned int address)
{
    return m68k_read_memory_8(address);
}

extern ""C"" unsigned int m68k_read_pcrelative_16(unsigned int address)
{
    return m68k_read_memory_16(address);
}

extern ""C"" unsigned int m68k_read_pcrelative_32(unsigned int address)
{
    return m68k_read_memory_32(address);
}

extern ""C"" void m68k_write_memory_8(unsigned int address, unsigned int value)
{
    probeWrite8(address, static_cast<uint8_t>(value & 0xffu));
}

extern ""C"" void m68k_write_memory_16(unsigned int address, unsigned int value)
{
    probeWrite8(address, static_cast<uint8_t>((value >> 8) & 0xffu));
    probeWrite8(address + 1u, static_cast<uint8_t>(value & 0xffu));
}

extern ""C"" void m68k_write_memory_32(unsigned int address, unsigned int value)
{
    m68k_write_memory_16(address, (value >> 16) & 0xffffu);
    m68k_write_memory_16(address + 2u, value & 0xffffu);
}

static void writeProbe16(uint32_t offset, uint16_t value)
{
    gProbeRam[offset] = static_cast<uint8_t>((value >> 8) & 0xffu);
    gProbeRam[offset + 1u] = static_cast<uint8_t>(value & 0xffu);
}

static const PalmLoadedCodeResource* findCodeResource(const PalmLoadedApp& app, uint16_t id);
static void palmInstructionHook(unsigned int pc);

static void ensureMusashiInitialized()
{
    if (!gMusashiInitialized)
    {
        m68k_init();
        m68k_set_cpu_type(M68K_CPU_TYPE_68000);
        m68k_set_instr_hook_callback(palmInstructionHook);
        gMusashiInitialized = true;
    }
}

static void runSyntheticMusashiProbe()
{
    memset(gProbeRam, 0, sizeof(gProbeRam));
    gMappedCode = nullptr;
    gLastUnknownRead = 0;
    gLastUnknownWrite = 0;

    // moveq #7,d0; addq.l #1,d0; nop; nop
    writeProbe16(0, 0x7007u);
    writeProbe16(2, 0x5280u);
    writeProbe16(4, 0x4E71u);
    writeProbe16(6, 0x4E71u);

    ensureMusashiInitialized();

    m68k_set_reg(M68K_REG_PC, kProbeBase);
    m68k_set_reg(M68K_REG_SR, 0x2700);
    m68k_set_reg(M68K_REG_SP, kProbeBase + kProbeRamSize - 4u);
    m68k_set_reg(M68K_REG_D0, 0);

    const int usedCycles = m68k_execute(32);
    const uint32_t d0 = m68k_get_reg(nullptr, M68K_REG_D0);
    const uint32_t pc = m68k_get_reg(nullptr, M68K_REG_PC);

    Serial.printf(""  synthetic Musashi probe: cycles=%d D0=0x%08X PC=0x%08X\n"",
        usedCycles,
        static_cast<unsigned>(d0),
        static_cast<unsigned>(pc));

    if (gLastUnknownRead != 0 || gLastUnknownWrite != 0)
    {
        Serial.printf(""  synthetic probe unmapped read=0x%08X write=0x%08X\n"",
            static_cast<unsigned>(gLastUnknownRead),
            static_cast<unsigned>(gLastUnknownWrite));
    }
}

static void runPalmCodeMapProbe(const PalmLoadedApp& app)
{
    const PalmLoadedCodeResource* code1 = findCodeResource(app, 1);
    if (code1 == nullptr || code1->bytes == nullptr || code1->size < 2)
    {
        Serial.println(""  Palm code map probe skipped: code #1 unavailable"");
        return;
    }

    memset(gProbeRam, 0, sizeof(gProbeRam));
    gMappedCode = code1;
    gLastUnknownRead = 0;
    gLastUnknownWrite = 0;

    ensureMusashiInitialized();
    m68k_set_reg(M68K_REG_PC, kPalmCodeBase);
    m68k_set_reg(M68K_REG_SR, 0x2700);
    m68k_set_reg(M68K_REG_SP, kProbeBase + kProbeRamSize - 4u);
    m68k_set_reg(M68K_REG_D0, 0);
    m68k_set_reg(M68K_REG_A5, kProbeBase);

    const uint16_t firstWord = static_cast<uint16_t>((static_cast<uint16_t>(code1->bytes[0]) << 8) | code1->bytes[1]);
    const int usedCycles = m68k_execute(24);
    const uint32_t pc = m68k_get_reg(nullptr, M68K_REG_PC);
    const uint32_t d0 = m68k_get_reg(nullptr, M68K_REG_D0);

    Serial.printf(""  Palm code #1 map probe: base=0x%08X firstWord=0x%04X cycles=%d PC=0x%08X D0=0x%08X\n"",
        static_cast<unsigned>(kPalmCodeBase),
        static_cast<unsigned>(firstWord),
        usedCycles,
        static_cast<unsigned>(pc),
        static_cast<unsigned>(d0));

    if (gLastUnknownRead != 0 || gLastUnknownWrite != 0)
    {
        Serial.printf(""  Palm code map unmapped read=0x%08X write=0x%08X\n"",
            static_cast<unsigned>(gLastUnknownRead),
            static_cast<unsigned>(gLastUnknownWrite));
    }

    gMappedCode = nullptr;
}

static void palmInstructionHook(unsigned int pc)
{
    if (!gTrapHookEnabled)
    {
        return;
    }

    const uint16_t opcode = static_cast<uint16_t>(m68k_read_memory_16(pc));
    if (gInstructionHookCount < 12)
    {
        Serial.printf(""  Palm hook pc[%u]=0x%08X opcode=0x%04X\n"",
            static_cast<unsigned>(gInstructionHookCount),
            static_cast<unsigned>(pc),
            static_cast<unsigned>(opcode));
    }
    ++gInstructionHookCount;

    uint16_t trap = 0;
    uint32_t resumePc = pc + 2u;
    if (opcode == 0x4E4Fu)
    {
        trap = static_cast<uint16_t>(m68k_read_memory_16(pc + 2u));
        resumePc = pc + 4u;
    }
    else if ((opcode & 0xF000u) == 0xA000u)
    {
        trap = opcode;
    }

    if ((trap & 0xF000u) != 0xA000u)
    {
        return;
    }

    if (gTrapHookCount < 8)
    {
        Serial.printf(""  Palm trap hook: pc=0x%08X op=0x%04X selector=0x%04X name=%s sp=0x%08X d0=0x%08X a0=0x%08X\n"",
            static_cast<unsigned>(pc),
            static_cast<unsigned>(opcode),
            static_cast<unsigned>(trap),
            palmTrapName(trap),
            static_cast<unsigned>(m68k_get_reg(nullptr, M68K_REG_SP)),
            static_cast<unsigned>(m68k_get_reg(nullptr, M68K_REG_D0)),
            static_cast<unsigned>(m68k_get_reg(nullptr, M68K_REG_A0)));
    }

    ++gTrapHookCount;

    PalmTrapFrame frame{};
    frame.selector = trap;
    frame.opcode = opcode;
    frame.pc = pc;
    frame.resumePc = resumePc;
    frame.sp = m68k_get_reg(nullptr, M68K_REG_SP);
    for (int i = 0; i < 8; ++i)
    {
        frame.d[i] = m68k_get_reg(nullptr, static_cast<m68k_register_t>(M68K_REG_D0 + i));
        frame.a[i] = m68k_get_reg(nullptr, static_cast<m68k_register_t>(M68K_REG_A0 + i));
    }
    for (int i = 0; i < 8; ++i)
    {
        frame.stackLongs[i] = m68k_read_memory_32(frame.sp + static_cast<uint32_t>(i) * 4u);
    }

    const PalmTrapResult result = palmDispatchTrap(frame);
    if (result.handled)
    {
        m68k_set_reg(M68K_REG_D0, result.d0);
        m68k_set_reg(M68K_REG_A0, result.a0);
        m68k_set_reg(M68K_REG_PC, result.resumePc);
        if (result.stop)
        {
            m68k_end_timeslice();
        }
    }
}

static void runPalmTrapVisibilityProbe(const PalmLoadedApp& app)
{
    const PalmLoadedCodeResource* code1 = findCodeResource(app, 1);
    if (code1 == nullptr || code1->bytes == nullptr || code1->size <= kPalmEntryProbeOffset)
    {
        Serial.println(""  Palm trap probe skipped: code #1 unavailable"");
        return;
    }

    memset(gProbeRam, 0, sizeof(gProbeRam));
    gMappedCode = code1;
    gLastUnknownRead = 0;
    gLastUnknownWrite = 0;

    ensureMusashiInitialized();
    m68k_set_reg(M68K_REG_PC, kPalmCodeBase + kPalmEntryProbeOffset);
    m68k_set_reg(M68K_REG_SR, 0x2700);
    m68k_set_reg(M68K_REG_SP, kProbeBase + kProbeRamSize - 4u);
    m68k_set_reg(M68K_REG_D0, 0);
    m68k_set_reg(M68K_REG_A0, 0);
    m68k_set_reg(M68K_REG_A5, kProbeBase + 0x80u);
    m68k_set_reg(M68K_REG_A6, 0);
    gTrapHookEnabled = true;
    gInstructionHookCount = 0;
    gTrapHookCount = 0;
    palmTrapSetAppContext(&app);

    const int usedCycles = m68k_execute(5000);
    gTrapHookEnabled = false;
    palmTrapSetAppContext(nullptr);

    if (gTrapHookCount > 0)
    {
        Serial.printf(""  Palm trap probe: observed %u trap(s), cycles=%d finalPC=0x%08X\n"",
            static_cast<unsigned>(gTrapHookCount),
            usedCycles,
            static_cast<unsigned>(m68k_get_reg(nullptr, M68K_REG_PC)));
    }
    else
    {
        Serial.printf(""  Palm trap probe: no trap observed, hookInstructions=%u cycles=%d pc=0x%08X lastRead=0x%08X lastWrite=0x%08X\n"",
            static_cast<unsigned>(gInstructionHookCount),
            usedCycles,
            static_cast<unsigned>(m68k_get_reg(nullptr, M68K_REG_PC)),
            static_cast<unsigned>(gLastUnknownRead),
            static_cast<unsigned>(gLastUnknownWrite));
    }

    gMappedCode = nullptr;
}

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

static uint16_t readU16BEFromMemory(const uint8_t* bytes, uint32_t size, uint32_t offset)
{
    if (offset + 2u > size)
    {
        return 0;
    }

    return static_cast<uint16_t>((static_cast<uint16_t>(bytes[offset]) << 8) |
        static_cast<uint16_t>(bytes[offset + 1]));
}

static uint32_t findFirstTrapOffset(const uint8_t* bytes, uint32_t size, uint32_t maxBytes)
{
    const uint32_t scanBytes = size < maxBytes ? size : maxBytes;
    for (uint32_t offset = 0; offset + 1u < scanBytes; offset += 2u)
    {
        const uint16_t opcode = readU16BEFromMemory(bytes, size, offset);
        if ((opcode & 0xF000u) == 0xA000u)
        {
            return offset;
        }
        if (opcode == 0x4E4Fu && offset + 3u < scanBytes)
        {
            const uint16_t selector = readU16BEFromMemory(bytes, size, offset + 2u);
            if ((selector & 0xF000u) == 0xA000u)
            {
                return offset;
            }
        }
    }

    return 0xFFFFFFFFu;
}

static void printCode0Summary(const PalmLoadedCodeResource& code0)
{
    const uint32_t aboveA5Bytes = readU32BEFromMemory(code0.bytes, code0.size, 0);
    const uint32_t belowA5Bytes = readU32BEFromMemory(code0.bytes, code0.size, 4);
    const uint32_t jumpTableBytes = readU32BEFromMemory(code0.bytes, code0.size, 8);
    const uint32_t code0Bytes = readU32BEFromMemory(code0.bytes, code0.size, 12);
    const uint16_t firstOpcode = readU16BEFromMemory(code0.bytes, code0.size, 16);
    const uint32_t trapOffset = findFirstTrapOffset(code0.bytes, code0.size, code0.size);

    Serial.printf(""  code #0 startup: aboveA5=%u belowA5=%u jumpTableBytes=%u code0Bytes=%u firstOpcode=0x%04X\n"",
        static_cast<unsigned>(aboveA5Bytes),
        static_cast<unsigned>(belowA5Bytes),
        static_cast<unsigned>(jumpTableBytes),
        static_cast<unsigned>(code0Bytes),
        static_cast<unsigned>(firstOpcode));

    if (trapOffset != 0xFFFFFFFFu)
    {
        const uint16_t trap = readU16BEFromMemory(code0.bytes, code0.size, trapOffset);
        Serial.printf(""  code #0 first trap: offset=0x%04X trap=0x%04X name=%s\n"",
            static_cast<unsigned>(trapOffset),
            static_cast<unsigned>(trap),
            palmTrapName(trap));
    }
}

static void printCode1Summary(const PalmLoadedCodeResource& code1)
{
    const uint32_t trapOffset = findFirstTrapOffset(code1.bytes, code1.size, 160u);
    if (trapOffset != 0xFFFFFFFFu)
    {
        const uint16_t opcode = readU16BEFromMemory(code1.bytes, code1.size, trapOffset);
        const uint16_t trap = opcode == 0x4E4Fu ? readU16BEFromMemory(code1.bytes, code1.size, trapOffset + 2u) : opcode;
        Serial.printf(""  code #1 first near trap: offset=0x%04X op=0x%04X selector=0x%04X name=%s\n"",
            static_cast<unsigned>(trapOffset),
            static_cast<unsigned>(opcode),
            static_cast<unsigned>(trap),
            palmTrapName(trap));
    }
}

void palm68kRunProbe(const PalmLoadedApp* apps, size_t appCount)
{
    Serial.println(""68K runtime probe"");
    runSyntheticMusashiProbe();

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
        printCode0Summary(*code0);
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
        printCode1Summary(*code1);
    }
    else
    {
        Serial.println(""  code #1 missing"");
    }

    runPalmCodeMapProbe(app);
    runPalmTrapVisibilityProbe(app);

    Serial.println(""  emulator integration point ready"");
}
"
    End Function

    Private Shared Function RenderPalmDisplayHeader() As String
        Return "#pragma once

#include ""palm_prc_loader.h""

bool palmDisplayBegin();
void palmDisplayBacklightOff();
void palmDisplayShowLoaderSmoke(const PalmLoadedApp* apps, size_t appCount);
void palmDisplayShowRuntimeUiProbe(uint16_t selector, uint32_t trapCount);
void palmDisplayShowMemoPadProbe(uint16_t selector, uint32_t trapCount, const char* capturedText);
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

void palmDisplayBacklightOff()
{
    pinMode(kTftBacklight, OUTPUT);
    digitalWrite(kTftBacklight, LOW);
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

static void renderRuntimeUiProbeFrame(uint16_t selector, uint32_t trapCount)
{
    if (gFrameBuffer == nullptr)
    {
        return;
    }

    const uint16_t page = rgb565(36, 38, 34);
    const uint16_t shell = rgb565(72, 86, 66);
    const uint16_t lcdBg = rgb565(224, 232, 213);
    const uint16_t lcdInk = rgb565(38, 52, 42);
    const uint16_t lcdMid = rgb565(118, 146, 104);
    const uint16_t accent = rgb565(56, 160, 118);
    const uint16_t alert = rgb565(190, 210, 96);

    for (int y = 0; y < kScreenH; ++y)
    {
        uint16_t* row = gFrameBuffer + static_cast<uint32_t>(y) * kScreenW;
        for (int x = 0; x < kScreenW; ++x)
        {
            uint16_t pixel = page;
            if (inRect(x, y, kPalmViewX, kPalmViewY, kPalmViewW, kPalmViewH))
            {
                pixel = shell;
            }

            if (inRect(x, y, kPalmLcdViewX, kPalmLcdViewY, kPalmLcdViewW, kPalmLcdViewH))
            {
                const int lx = x - kPalmLcdViewX;
                const int ly = y - kPalmLcdViewY;
                const int palmX = (lx * kPalmLcdW) / kPalmLcdViewW;
                const int palmY = (ly * kPalmLcdH) / kPalmLcdViewH;
                const bool border = palmX < 2 || palmY < 2 || palmX >= kPalmLcdW - 2 || palmY >= kPalmLcdH - 2;
                const bool title = palmY >= 8 && palmY < 22 && palmX >= 8 && palmX < 152;
                const bool rowLine = palmY >= 36 && palmY < 140 && ((palmY - 36) % 18) < 2;
                const bool checker = ((palmX / 10) ^ (palmY / 10) ^ static_cast<int>(trapCount & 1u)) & 1;
                const bool selectorStripe = palmY >= 144 && palmY < 154 && palmX < static_cast<int>((selector & 0xFFu) % 150u);

                pixel = checker ? lcdBg : rgb565(214, 224, 205);
                if (title)
                {
                    pixel = accent;
                }
                if (rowLine)
                {
                    pixel = lcdMid;
                }
                if (selectorStripe)
                {
                    pixel = alert;
                }
                if ((palmX == 18 || palmX == 19 || palmX == 20) && palmY >= 48 && palmY < 132)
                {
                    pixel = lcdInk;
                }
                if (border)
                {
                    pixel = lcdInk;
                }
            }

            row[x] = pixel;
        }
    }
}

static uint8_t glyphRow(char ch, int row)
{
    switch (ch)
    {
        case 'A': return (const uint8_t[7]){0x0E,0x11,0x11,0x1F,0x11,0x11,0x11}[row];
        case 'D': return (const uint8_t[7]){0x1E,0x11,0x11,0x11,0x11,0x11,0x1E}[row];
        case 'I': return (const uint8_t[7]){0x0E,0x04,0x04,0x04,0x04,0x04,0x0E}[row];
        case 'M': return (const uint8_t[7]){0x11,0x1B,0x15,0x15,0x11,0x11,0x11}[row];
        case 'N': return (const uint8_t[7]){0x11,0x19,0x15,0x13,0x11,0x11,0x11}[row];
        case 'P': return (const uint8_t[7]){0x1E,0x11,0x11,0x1E,0x10,0x10,0x10}[row];
        case 'R': return (const uint8_t[7]){0x1E,0x11,0x11,0x1E,0x14,0x12,0x11}[row];
        case 'T': return (const uint8_t[7]){0x1F,0x04,0x04,0x04,0x04,0x04,0x04}[row];
        case 'U': return (const uint8_t[7]){0x11,0x11,0x11,0x11,0x11,0x11,0x0E}[row];
        case 'a': return (const uint8_t[7]){0x00,0x00,0x0E,0x01,0x0F,0x11,0x0F}[row];
        case 'd': return (const uint8_t[7]){0x01,0x01,0x0F,0x11,0x11,0x11,0x0F}[row];
        case 'e': return (const uint8_t[7]){0x00,0x00,0x0E,0x11,0x1F,0x10,0x0E}[row];
        case 'i': return (const uint8_t[7]){0x04,0x00,0x0C,0x04,0x04,0x04,0x0E}[row];
        case 'l': return (const uint8_t[7]){0x0C,0x04,0x04,0x04,0x04,0x04,0x0E}[row];
        case 'm': return (const uint8_t[7]){0x00,0x00,0x1A,0x15,0x15,0x15,0x15}[row];
        case 'n': return (const uint8_t[7]){0x00,0x00,0x1E,0x11,0x11,0x11,0x11}[row];
        case 'o': return (const uint8_t[7]){0x00,0x00,0x0E,0x11,0x11,0x11,0x0E}[row];
        case 'p': return (const uint8_t[7]){0x00,0x00,0x1E,0x11,0x1E,0x10,0x10}[row];
        case 'r': return (const uint8_t[7]){0x00,0x00,0x16,0x19,0x10,0x10,0x10}[row];
        case 't': return (const uint8_t[7]){0x04,0x04,0x1F,0x04,0x04,0x05,0x02}[row];
        case 'u': return (const uint8_t[7]){0x00,0x00,0x11,0x11,0x11,0x13,0x0D}[row];
        case 'w': return (const uint8_t[7]){0x00,0x00,0x11,0x11,0x15,0x15,0x0A}[row];
        case '0': return (const uint8_t[7]){0x0E,0x11,0x13,0x15,0x19,0x11,0x0E}[row];
        case '1': return (const uint8_t[7]){0x04,0x0C,0x04,0x04,0x04,0x04,0x0E}[row];
        case '2': return (const uint8_t[7]){0x0E,0x11,0x01,0x02,0x04,0x08,0x1F}[row];
        case '3': return (const uint8_t[7]){0x1E,0x01,0x01,0x0E,0x01,0x01,0x1E}[row];
        case '4': return (const uint8_t[7]){0x02,0x06,0x0A,0x12,0x1F,0x02,0x02}[row];
        case '5': return (const uint8_t[7]){0x1F,0x10,0x10,0x1E,0x01,0x01,0x1E}[row];
        case '6': return (const uint8_t[7]){0x06,0x08,0x10,0x1E,0x11,0x11,0x0E}[row];
        case '7': return (const uint8_t[7]){0x1F,0x01,0x02,0x04,0x08,0x08,0x08}[row];
        case '8': return (const uint8_t[7]){0x0E,0x11,0x11,0x0E,0x11,0x11,0x0E}[row];
        case '9': return (const uint8_t[7]){0x0E,0x11,0x11,0x0F,0x01,0x02,0x0C}[row];
        case ':': return (const uint8_t[7]){0x00,0x04,0x04,0x00,0x04,0x04,0x00}[row];
        case '#': return (const uint8_t[7]){0x0A,0x0A,0x1F,0x0A,0x1F,0x0A,0x0A}[row];
        case ' ': return 0x00;
        default: return row == 6 ? 0x1F : 0x11;
    }
}

static void drawPalmPixel(int palmX, int palmY, uint16_t color)
{
    if (gFrameBuffer == nullptr || palmX < 0 || palmY < 0 || palmX >= kPalmLcdW || palmY >= kPalmLcdH)
    {
        return;
    }

    const int x0 = kPalmLcdViewX + (palmX * kPalmLcdViewW) / kPalmLcdW;
    const int x1 = kPalmLcdViewX + ((palmX + 1) * kPalmLcdViewW) / kPalmLcdW;
    const int y0 = kPalmLcdViewY + (palmY * kPalmLcdViewH) / kPalmLcdH;
    const int y1 = kPalmLcdViewY + ((palmY + 1) * kPalmLcdViewH) / kPalmLcdH;

    for (int y = y0; y < y1; ++y)
    {
        uint16_t* row = gFrameBuffer + static_cast<uint32_t>(y) * kScreenW;
        for (int x = x0; x < x1; ++x)
        {
            row[x] = color;
        }
    }
}

static void fillPalmRect(int x, int y, int w, int h, uint16_t color)
{
    for (int yy = y; yy < y + h; ++yy)
    {
        for (int xx = x; xx < x + w; ++xx)
        {
            drawPalmPixel(xx, yy, color);
        }
    }
}

static void drawPalmText(int x, int y, const char* text, uint16_t color, int scale)
{
    int cursor = x;
    for (int i = 0; text[i] != '\0' && i < 28; ++i)
    {
        const char ch = text[i];
        for (int gy = 0; gy < 7; ++gy)
        {
            const uint8_t bits = glyphRow(ch, gy);
            for (int gx = 0; gx < 5; ++gx)
            {
                if ((bits & (1u << (4 - gx))) != 0)
                {
                    fillPalmRect(cursor + gx * scale, y + gy * scale, scale, scale, color);
                }
            }
        }
        cursor += 6 * scale;
    }
}

static void renderMemoPadProbeFrame(uint16_t selector, uint32_t trapCount, const char* capturedText)
{
    renderRuntimeUiProbeFrame(selector, trapCount);

    // Keep the probe monochrome for now. Black/white/gray survive RGB/BGR
    // swaps and make the Palm UI shape readable while color order is unknown.
    const uint16_t lcdBg = 0xFFFFu;
    const uint16_t lcdInk = 0x0000u;
    const uint16_t title = 0xC618u;
    const uint16_t titleDark = 0x0000u;
    const uint16_t line = 0x8410u;

    fillPalmRect(3, 3, 154, 154, lcdBg);
    fillPalmRect(3, 3, 154, 18, title);
    fillPalmRect(3, 20, 154, 1, lcdInk);
    drawPalmText(9, 7, ""Memo Pad"", lcdInk, 1);
    drawPalmText(112, 7, ""All"", lcdInk, 1);

    for (int y = 34; y < 139; y += 17)
    {
        fillPalmRect(10, y, 138, 1, line);
    }

    drawPalmText(13, 30, ""New Memo"", lcdInk, 1);
    drawPalmText(13, 48, ""Memo runtime"", lcdInk, 1);
    drawPalmText(13, 66, capturedText != nullptr && capturedText[0] != '\0' ? capturedText : ""Trap UI"", lcdInk, 1);
    drawPalmText(13, 145, ""Trap #"", titleDark, 1);

    char trapDigits[4];
    trapDigits[0] = static_cast<char>('0' + ((trapCount / 100u) % 10u));
    trapDigits[1] = static_cast<char>('0' + ((trapCount / 10u) % 10u));
    trapDigits[2] = static_cast<char>('0' + (trapCount % 10u));
    trapDigits[3] = '\0';
    drawPalmText(55, 145, trapDigits, titleDark, 1);
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

void palmDisplayShowRuntimeUiProbe(uint16_t selector, uint32_t trapCount)
{
    if (!palmDisplayBegin())
    {
        return;
    }

    ++gDisplayGeneration;
    renderRuntimeUiProbeFrame(selector, trapCount);

    if (gPanel != nullptr)
    {
        ESP_ERROR_CHECK(esp_lcd_panel_draw_bitmap(gPanel, 0, 0, kScreenW, kScreenH, gFrameBuffer));
        esp_lcd_panel_disp_on_off(gPanel, true);
    }

    Serial.printf(""LCD runtime UI probe selector=0x%04X traps=%u generation=%u\n"",
        static_cast<unsigned>(selector),
        static_cast<unsigned>(trapCount),
        static_cast<unsigned>(gDisplayGeneration));
}

void palmDisplayShowMemoPadProbe(uint16_t selector, uint32_t trapCount, const char* capturedText)
{
    if (!palmDisplayBegin())
    {
        return;
    }

    ++gDisplayGeneration;
    renderMemoPadProbeFrame(selector, trapCount, capturedText);

    if (gPanel != nullptr)
    {
        ESP_ERROR_CHECK(esp_lcd_panel_draw_bitmap(gPanel, 0, 0, kScreenW, kScreenH, gFrameBuffer));
        esp_lcd_panel_disp_on_off(gPanel, true);
    }

    Serial.printf(""LCD Memo Pad probe selector=0x%04X traps=%u text='%s' generation=%u\n"",
        static_cast<unsigned>(selector),
        static_cast<unsigned>(trapCount),
        capturedText != nullptr ? capturedText : """",
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
