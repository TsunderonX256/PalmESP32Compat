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
    Serial.println(""LCD starts off; Memo UI probe enables 25% backlight when it draws"");

    if (loadGeneratedPalmApps())
    {
        palm68kRunProbe(gLoadedApps, gLoadedAppCount);
    }
}

void loop()
{
    palmDisplayPollSerialCommands();
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
static constexpr uint32_t kFakeAllocationHandle = 0x00020040u;
static constexpr uint32_t kFakeMemoRecordPtr = 0x00002000u;
static constexpr uint32_t kFakeResourcePtr = 0x00002100u;
static constexpr uint32_t kFakeAllocationPtr = 0x00002200u;
static constexpr uint16_t kFakeMemoRecordCount = 7u;
static constexpr uint8_t kPalmUiRoleForm = 1u;
static constexpr uint8_t kPalmUiRoleTitle = 2u;
static constexpr uint8_t kPalmUiRoleMemoText = 3u;
static constexpr uint16_t kMemoNewButtonId = 1001u;
static constexpr uint16_t kMemoDetailsButtonId = 1002u;
static bool gFakeMemoDatabaseCreated = false;
static const PalmLoadedApp* gTrapApp = nullptr;
static const PalmLoadedResource* gLockedResource = nullptr;
static bool gUseSyntheticStringResource = false;
static bool gRuntimeUiProbeShown = false;
static uint32_t gRuntimeUiProbeTrapCount = 0;
static bool gMemoPadProbeShown = false;
static char gCapturedTitleText[32] = """";
static char gCapturedMemoText[32] = """";
static char gPendingMemoText[32] = """";
static uint8_t gUiGeometryLogCount = 0;
static uint8_t gScratchDumpLogCount = 0;
static uint8_t gDatabaseTraceLogCount = 0;

static bool looksWritablePointer(uint32_t address);
static void writeCString(uint32_t address, const char* text, uint32_t maxBytes);

void palmTrapSetAppContext(const PalmLoadedApp* app)
{
    gTrapApp = app;
    gLockedResource = nullptr;
    gUseSyntheticStringResource = false;
    gRuntimeUiProbeShown = false;
    gRuntimeUiProbeTrapCount = 0;
    gMemoPadProbeShown = false;
    gCapturedTitleText[0] = '\0';
    gCapturedMemoText[0] = '\0';
    gPendingMemoText[0] = '\0';
    gUiGeometryLogCount = 0;
    gScratchDumpLogCount = 0;
    gDatabaseTraceLogCount = 0;
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

static const char* fakeMemoText(uint16_t index)
{
    switch (index)
    {
        case 0: return ""Hello from ESP32"";
        case 1: return ""Second memo row"";
        case 2: return ""Palm UI drawing"";
        case 3: return ""Third memo row"";
        case 4: return ""Note four"";
        case 5: return ""Note five"";
        case 6: return ""Note six"";
        default: return """";
    }
}

static void seedFakeMemoRecord(uint16_t index)
{
    writeCString(kFakeMemoRecordPtr, fakeMemoText(index), 64u);
}

static void publishFakeMemoRows(uint16_t selector, uint32_t trapCount)
{
    palmDisplayPalmUiSetListCount(selector, trapCount, kFakeMemoRecordCount);
    for (uint16_t i = 0; i < kFakeMemoRecordCount; ++i)
    {
        palmDisplayPalmUiSetListRow(selector, trapCount, i, fakeMemoText(i));
    }
}

static void captureMemoProbeText(const char* memoText)
{
    if (memoText == nullptr || memoText[0] == '\0')
    {
        return;
    }

    strncpy(gPendingMemoText, memoText, sizeof(gPendingMemoText));
    gPendingMemoText[sizeof(gPendingMemoText) - 1] = '\0';
}

static bool readInlineStackText(const PalmTrapFrame& frame, char* out, uint32_t maxBytes)
{
    if (maxBytes == 0)
    {
        return false;
    }

    uint8_t bytes[16];
    for (uint32_t i = 0; i < sizeof(bytes); i += 2)
    {
        const uint16_t word = stackWord(frame, i);
        bytes[i] = static_cast<uint8_t>((word >> 8) & 0xffu);
        bytes[i + 1u] = static_cast<uint8_t>(word & 0xffu);
    }

    uint32_t bestStart = 0;
    uint32_t bestLen = 0;
    uint32_t runStart = 0;
    uint32_t runLen = 0;
    for (uint32_t i = 0; i < sizeof(bytes); ++i)
    {
        const bool printable = bytes[i] >= 32 && bytes[i] <= 126;
        if (printable)
        {
            if (runLen == 0)
            {
                runStart = i;
            }
            ++runLen;
            if (runLen > bestLen)
            {
                bestStart = runStart;
                bestLen = runLen;
            }
        }
        else
        {
            runLen = 0;
        }
    }

    if (bestLen < 3)
    {
        out[0] = '\0';
        return false;
    }

    const uint32_t copyLen = bestLen + 1u < maxBytes ? bestLen : maxBytes - 1u;
    for (uint32_t i = 0; i < copyLen; ++i)
    {
        out[i] = static_cast<char>(bytes[bestStart + i]);
    }
    out[copyLen] = '\0';
    return true;
}

static bool readCandidateTextFromFrame(const PalmTrapFrame& frame, char* out, uint32_t maxBytes)
{
    if (maxBytes == 0)
    {
        return false;
    }

    for (uint32_t i = 0; i < 8; ++i)
    {
        const uint32_t value = frame.stackLongs[i];
        if (readCString(value, out, maxBytes))
        {
            return true;
        }
        if (readCString(value >> 16, out, maxBytes))
        {
            return true;
        }
        if (readCString(value & 0xffffu, out, maxBytes))
        {
            return true;
        }
    }

    out[0] = '\0';
    return false;
}

static bool isScratchPointer(uint32_t address)
{
    return address >= 0x00001000u && address < 0x00001100u;
}

static bool isMemoRowDrawFrame(const PalmTrapFrame& frame)
{
    if (frame.selector != 0xA11D || gPendingMemoText[0] == '\0')
    {
        return false;
    }

    const uint32_t rowStateP = frame.stackLongs[0];
    const uint32_t packedMeta = frame.stackLongs[2];
    const uint32_t rowMetaP = (packedMeta >> 16) & 0xffffu;
    return isScratchPointer(rowStateP) &&
        frame.stackLongs[1] == 0xffffffffu &&
        isScratchPointer(rowMetaP) &&
        rowMetaP >= rowStateP;
}

static void showMemoPadProbe(uint16_t selector, const char* capturedTitle, const char* capturedMemo)
{
    ++gRuntimeUiProbeTrapCount;
    if (!gMemoPadProbeShown)
    {
        gMemoPadProbeShown = true;
        gRuntimeUiProbeShown = true;
        palmDisplayPalmUiBeginForm(selector, gRuntimeUiProbeTrapCount,
            capturedTitle != nullptr && capturedTitle[0] != '\0' ? capturedTitle : ""Memo Pad"");
        palmDisplayPalmUiSetCategory(selector, gRuntimeUiProbeTrapCount, ""All"");
        palmDisplayPalmUiDrawButton(selector, gRuntimeUiProbeTrapCount, kMemoNewButtonId, ""New"");
        palmDisplayPalmUiDrawButton(selector, gRuntimeUiProbeTrapCount, kMemoDetailsButtonId, ""Details"");
    }

    if (capturedTitle != nullptr && capturedTitle[0] != '\0' && strcmp(capturedTitle, gCapturedTitleText) != 0)
    {
        strncpy(gCapturedTitleText, capturedTitle, sizeof(gCapturedTitleText));
        gCapturedTitleText[sizeof(gCapturedTitleText) - 1] = '\0';
        palmDisplayPalmUiDrawText(selector, gRuntimeUiProbeTrapCount, 9, 8, gCapturedTitleText);
    }
    if (capturedMemo != nullptr && capturedMemo[0] != '\0' && strcmp(capturedMemo, gCapturedMemoText) != 0)
    {
        strncpy(gCapturedMemoText, capturedMemo, sizeof(gCapturedMemoText));
        gCapturedMemoText[sizeof(gCapturedMemoText) - 1] = '\0';
        palmDisplayPalmUiDrawText(selector, gRuntimeUiProbeTrapCount, 12, 29, gCapturedMemoText);
    }
}

static void logUiGeometryProbe(const PalmTrapFrame& frame)
{
    if (gUiGeometryLogCount >= 12)
    {
        return;
    }

    ++gUiGeometryLogCount;
    Serial.printf(""  UI geometry selector=0x%04X words=[%u,%u,%u,%u,%u,%u,%u,%u] signed=[%d,%d,%d,%d] ptrs=0x%04X,0x%04X,0x%04X\n"",
        static_cast<unsigned>(frame.selector),
        static_cast<unsigned>(stackWord(frame, 0)),
        static_cast<unsigned>(stackWord(frame, 2)),
        static_cast<unsigned>(stackWord(frame, 4)),
        static_cast<unsigned>(stackWord(frame, 6)),
        static_cast<unsigned>(stackWord(frame, 8)),
        static_cast<unsigned>(stackWord(frame, 10)),
        static_cast<unsigned>(stackWord(frame, 12)),
        static_cast<unsigned>(stackWord(frame, 14)),
        static_cast<int16_t>(stackWord(frame, 0)),
        static_cast<int16_t>(stackWord(frame, 2)),
        static_cast<int16_t>(stackWord(frame, 4)),
        static_cast<int16_t>(stackWord(frame, 6)),
        static_cast<unsigned>(frame.stackLongs[0] & 0xffffu),
        static_cast<unsigned>((frame.stackLongs[1] >> 16) & 0xffffu),
        static_cast<unsigned>(frame.stackLongs[1] & 0xffffu));
}

static bool looksWritablePointer(uint32_t address)
{
    return (address >= 0x00001000u && address < 0x00001100u) ||
        (address >= 0x00002000u && address < 0x00002400u);
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

static void zeroMemory(uint32_t address, uint32_t byteCount)
{
    if (!looksWritablePointer(address))
    {
        return;
    }

    for (uint32_t i = 0; i < byteCount; ++i)
    {
        if (!looksWritablePointer(address + i))
        {
            break;
        }
        m68k_write_memory_8(address + i, 0);
    }
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

static void logScratchBytes(const char* label)
{
    if (gScratchDumpLogCount >= 8)
    {
        return;
    }

    ++gScratchDumpLogCount;
    Serial.printf(""  scratch %s 0x1070:"", label);
    for (uint32_t i = 0; i < 32; ++i)
    {
        Serial.printf("" %02X"", static_cast<unsigned>(m68k_read_memory_8(0x00001070u + i) & 0xffu));
    }
    Serial.printf("" | words: 1076=%04X 1086=%04X 108A=%04X 108C=%04X\n"",
        static_cast<unsigned>(m68k_read_memory_16(0x00001076u) & 0xffffu),
        static_cast<unsigned>(m68k_read_memory_16(0x00001086u) & 0xffffu),
        static_cast<unsigned>(m68k_read_memory_16(0x0000108Au) & 0xffffu),
        static_cast<unsigned>(m68k_read_memory_16(0x0000108Cu) & 0xffffu));
}

static void logDatabaseTrapWords(const PalmTrapFrame& frame)
{
    if (gDatabaseTraceLogCount >= 8)
    {
        return;
    }

    ++gDatabaseTraceLogCount;
    Serial.printf(""  DB words selector=0x%04X words=[%04X,%04X,%04X,%04X,%04X,%04X,%04X,%04X] longs=[0x%08X,0x%08X,0x%08X,0x%08X]\n"",
        static_cast<unsigned>(frame.selector),
        static_cast<unsigned>(stackWord(frame, 0)),
        static_cast<unsigned>(stackWord(frame, 2)),
        static_cast<unsigned>(stackWord(frame, 4)),
        static_cast<unsigned>(stackWord(frame, 6)),
        static_cast<unsigned>(stackWord(frame, 8)),
        static_cast<unsigned>(stackWord(frame, 10)),
        static_cast<unsigned>(stackWord(frame, 12)),
        static_cast<unsigned>(stackWord(frame, 14)),
        static_cast<unsigned>(frame.stackLongs[0]),
        static_cast<unsigned>(frame.stackLongs[1]),
        static_cast<unsigned>(frame.stackLongs[2]),
        static_cast<unsigned>(frame.stackLongs[3]));

    for (uint32_t i = 0; i < 4; ++i)
    {
        const uint32_t candidate = frame.stackLongs[i];
        if (!isScratchPointer(candidate) && !isScratchPointer(candidate >> 16) && !isScratchPointer(candidate & 0xffffu))
        {
            continue;
        }

        Serial.printf(""    ptr candidates[%u]=0x%08X hi=0x%04X lo=0x%04X\n"",
            static_cast<unsigned>(i),
            static_cast<unsigned>(candidate),
            static_cast<unsigned>((candidate >> 16) & 0xffffu),
            static_cast<unsigned>(candidate & 0xffffu));
    }
}

const char* palmTrapName(uint16_t selector)
{
    switch (selector)
    {
        case 0xA012: return ""MemChunkFree"";
        case 0xA013: return ""MemPtrNew"";
        case 0xA01E: return ""MemHandleNew"";
        case 0xA07F: return ""DmCreateDatabaseFromImage"";
        case 0xA041: return ""DmCreateDatabase"";
        case 0xA042: return ""DmDeleteDatabase"";
        case 0xA04A: return ""DmCloseDatabase"";
        case 0xA049: return ""DmOpenDatabase"";
        case 0xA04C: return ""DmOpenDatabaseInfo"";
        case 0xA046: return ""SelectorA046"";
        case 0xA047: return ""SelectorA047"";
        case 0xA04F: return ""DmNumRecords"";
        case 0xA050: return ""DmRecordInfo"";
        case 0xA059: return ""DmNewHandle"";
        case 0xA05B: return ""DmQueryRecord"";
        case 0xA05C: return ""DmGetRecord"";
        case 0xA05E: return ""DmReleaseRecord"";
        case 0xA05F: return ""SelectorA05F"";
        case 0xA020: return ""MemHandleToLocalID"";
        case 0xA021: return ""MemHandleLock"";
        case 0xA022: return ""MemHandleUnlock"";
        case 0xA02B: return ""MemHandleFree"";
        case 0xA035: return ""SelectorA035"";
        case 0xA036: return ""SelectorA036"";
        case 0xA071: return ""DmNumRecordsInCategory"";
        case 0xA075: return ""DmOpenDatabaseByTypeCreator"";
        case 0xA07E: return ""DmSet"";
        case 0xA061: return ""DmReleaseResource"";
        case 0xA084: return ""ErrDisplayFileLineMsg"";
        case 0xA08F: return ""SysAppStartup"";
        case 0xA090: return ""SysAppExit"";
        case 0xA0A9: return ""SysHandleEvent"";
        case 0xA104: return ""CategoryGetName"";
        case 0xA11D: return ""EvtGetEvent"";
        case 0xA19B: return ""FrmGotoForm"";
        case 0xA1A0: return ""FrmDispatchEvent"";
        case 0xA1BF: return ""MenuHandleEvent"";
        case 0xA2D3: return ""PrefGetAppPreferences"";
        case 0xA2FC: return ""CategoryInitialize"";
        case 0xA12F: return ""EvtWakeup"";
        case 0xA27B: return ""FtrGet"";
        case 0xA3AB: return ""UIBrightnessAdjust"";
        case 0xA3FF: return ""ExgRegisterDatatype"";
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
            return handledTrap(frame, kFakeMemoDatabaseRef, kFakeMemoDatabaseRef);

        case 0xA059:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s dbRef=0x%08X sizeOrArg=0x%08X arg2=0x%08X -> handle=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(frame.stackLongs[1]),
                static_cast<unsigned>(frame.stackLongs[2]),
                static_cast<unsigned>(kFakeAllocationHandle));
            zeroMemory(kFakeAllocationPtr, 256u);
            return handledTrap(frame, kFakeAllocationHandle, kFakeAllocationHandle);

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
            {
                const uint32_t handle = (gLockedResource != nullptr || gUseSyntheticStringResource) ? kFakeResourceHandle : 0u;
                return handledTrap(frame, handle, handle);
            }
        }

        case 0xA020:
        {
            const uint32_t handle = frame.stackLongs[0];
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s handle=0x%08X -> localID=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(handle),
                static_cast<unsigned>(handle));
            return handledTrap(frame, handle, handle);
        }

        case 0xA021:
        {
            const uint32_t handle = frame.stackLongs[0];
            uint32_t ptr = kFakeMemoRecordPtr;
            if (handle == kFakeResourceHandle)
            {
                ptr = kFakeResourcePtr;
            }
            else if (handle == kFakeAllocationHandle)
            {
                ptr = kFakeAllocationPtr;
            }
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
            return handledTrap(frame, ptr, ptr);
        }

        case 0xA04F:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s dbRef=0x%08X -> records=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(kFakeMemoRecordCount));
            return handledTrap(frame, kFakeMemoRecordCount, 0);

        case 0xA050:
        {
            const uint32_t dbRef = frame.stackLongs[0];
            const uint16_t index = stackWord(frame, 4);
            const uint32_t attrP = stackLong(frame, 6);
            const uint32_t uniqueIDP = stackLong(frame, 10);
            const uint32_t chunkIDP = stackLong(frame, 14);
            const uint16_t safeIndex = index < kFakeMemoRecordCount ? index : 0;
            writeWordIfPointer(attrP, 0);
            writeLongIfPointer(uniqueIDP, static_cast<uint32_t>(safeIndex) + 1u);
            writeLongIfPointer(chunkIDP, kFakeMemoRecordHandle);
            captureMemoProbeText(fakeMemoText(safeIndex));
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s dbRef=0x%08X index=%u attrP=0x%08X uniqueP=0x%08X chunkP=0x%08X rawWords=[%04X,%04X,%04X,%04X,%04X,%04X,%04X,%04X] probeMemo='%s' -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(dbRef),
                static_cast<unsigned>(index),
                static_cast<unsigned>(attrP),
                static_cast<unsigned>(uniqueIDP),
                static_cast<unsigned>(chunkIDP),
                static_cast<unsigned>(stackWord(frame, 0)),
                static_cast<unsigned>(stackWord(frame, 2)),
                static_cast<unsigned>(stackWord(frame, 4)),
                static_cast<unsigned>(stackWord(frame, 6)),
                static_cast<unsigned>(stackWord(frame, 8)),
                static_cast<unsigned>(stackWord(frame, 10)),
                static_cast<unsigned>(stackWord(frame, 12)),
                static_cast<unsigned>(stackWord(frame, 14)),
                fakeMemoText(safeIndex));
            logScratchBytes(""after A050"");
            return handledTrap(frame, 0, 0);
        }

        case 0xA05B:
        case 0xA05C:
        {
            const uint32_t dbRef = frame.stackLongs[0];
            const uint16_t index = stackWord(frame, 4);
            const uint16_t safeIndex = index < kFakeMemoRecordCount ? index : 0;
            seedFakeMemoRecord(safeIndex);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s dbRef=0x%08X index=%u -> handle=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(dbRef),
                static_cast<unsigned>(safeIndex),
                static_cast<unsigned>(kFakeMemoRecordHandle));
            return handledTrap(frame, kFakeMemoRecordHandle, kFakeMemoRecordHandle);
        }

        case 0xA05E:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s dbRef=0x%08X indexOrHandle=0x%08X dirty=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(frame.stackLongs[1]),
                static_cast<unsigned>(frame.stackLongs[2]));
            return handledTrap(frame, 0, 0);

        case 0xA022:
        case 0xA02B:
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
            logDatabaseTrapWords(frame);
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
            return handledTrap(frame, kFakeResourceHandle, kFakeResourceHandle);

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
        {
            const uint32_t dbRef = frame.stackLongs[0];
            const uint16_t categoryIndex = stackWord(frame, 4);
            const uint32_t nameP = stackLong(frame, 6);
            writeCString(nameP, categoryIndex == 0 ? ""All"" : ""Unfiled"", 16u);
            showMemoPadProbe(frame.selector, ""Memo Pad"", """");
            palmDisplayPalmUiSetCategory(frame.selector, gRuntimeUiProbeTrapCount, categoryIndex == 0 ? ""All"" : ""Unfiled"");
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s dbRef=0x%08X category=%u nameP=0x%08X -> '%s'\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(dbRef),
                static_cast<unsigned>(categoryIndex),
                static_cast<unsigned>(nameP),
                categoryIndex == 0 ? ""All"" : ""Unfiled"");
            return handledTrap(frame, 0, 0);
        }

        case 0xA19B:
        case 0xA0A9:
        case 0xA1BF:
        case 0xA1A0:
        {
            char capturedTitle[32];
            char capturedMemo[32];
            capturedTitle[0] = '\0';
            capturedMemo[0] = '\0';
            if (frame.selector == 0xA19B)
            {
                readCandidateTextFromFrame(frame, capturedTitle, sizeof(capturedTitle));
                if (capturedTitle[0] == '\0')
                {
                    readInlineStackText(frame, capturedTitle, sizeof(capturedTitle));
                }
            }
            else if (frame.selector == 0xA11D)
            {
                readCString(frame.stackLongs[0], capturedMemo, sizeof(capturedMemo));
                if (capturedMemo[0] == '\0' && isMemoRowDrawFrame(frame))
                {
                    strncpy(capturedMemo, gPendingMemoText, sizeof(capturedMemo));
                    capturedMemo[sizeof(capturedMemo) - 1] = '\0';
                }
            }
            if (frame.selector != 0xA19B && capturedMemo[0] == '\0')
            {
                readCandidateTextFromFrame(frame, capturedMemo, sizeof(capturedMemo));
            }
            showMemoPadProbe(frame.selector, capturedTitle, capturedMemo);
            if (frame.selector == 0xA11D || frame.selector == 0xA0A9 || frame.selector == 0xA1BF || frame.selector == 0xA1A0)
            {
                logUiGeometryProbe(frame);
                if (frame.selector == 0xA11D)
                {
                    logScratchBytes(""at A11D"");
                }
            }
            Serial.printf(""  trap dispatch: UI probe selector=0x%04X name=%s title='%s' memo='%s' stack[0..2]=0x%08X,0x%08X,0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                capturedTitle,
                capturedMemo,
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(frame.stackLongs[1]),
                static_cast<unsigned>(frame.stackLongs[2]));
            return handledTrap(frame, 0, 0);
        }

        case 0xA11D:
        {
            const uint32_t eventP = frame.stackLongs[0];
            const uint32_t timeout = frame.stackLongs[1];
            zeroMemory(eventP, 24u);
            showMemoPadProbe(frame.selector, """", gPendingMemoText);
            if (gPendingMemoText[0] != '\0')
            {
                palmDisplayPalmUiDrawText(frame.selector, gRuntimeUiProbeTrapCount, 12, 29, gPendingMemoText);
            }
            logUiGeometryProbe(frame);
            logScratchBytes(""at EvtGetEvent"");
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s eventP=0x%08X timeout=0x%08X -> nilEvent\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(eventP),
                static_cast<unsigned>(timeout));
            return handledTrap(frame, 0, 0);
        }

        case 0xA071:
        {
            const uint32_t dbRef = frame.stackLongs[0];
            const uint16_t category = stackWord(frame, 4);
            publishFakeMemoRows(frame.selector, gRuntimeUiProbeTrapCount);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s dbRef=0x%08X category=%u -> records=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(dbRef),
                static_cast<unsigned>(category),
                static_cast<unsigned>(kFakeMemoRecordCount));
            return handledTrap(frame, kFakeMemoRecordCount, 0);
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

        case 0xA049:
        {
            const uint32_t dbID = frame.stackLongs[0];
            const uint16_t mode = stackWord(frame, 4);
            const uint32_t openedRef = 0u;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s db=0x%08X mode=0x%04X -> ref=0x%08X (force create path)\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(dbID),
                static_cast<unsigned>(mode),
                static_cast<unsigned>(openedRef));
            return handledTrap(frame, openedRef, openedRef);
        }

        case 0xA04C:
        {
            const uint32_t dbRef = frame.stackLongs[0];
            const uint32_t dbIDP = stackLong(frame, 4);
            const uint32_t openCountP = stackLong(frame, 8);
            const uint32_t modeP = stackLong(frame, 12);
            const uint32_t cardNoP = stackLong(frame, 16);
            const uint32_t resDBP = stackLong(frame, 20);
            writeLongIfPointer(dbIDP, kFakeMemoDatabaseId);
            writeWordIfPointer(openCountP, 1);
            writeWordIfPointer(modeP, 0);
            writeWordIfPointer(cardNoP, 0);
            if (looksWritablePointer(resDBP))
            {
                m68k_write_memory_8(resDBP, 0);
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s dbRef=0x%08X dbIDP=0x%08X openCountP=0x%08X modeP=0x%08X cardNoP=0x%08X resDBP=0x%08X -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(dbRef),
                static_cast<unsigned>(dbIDP),
                static_cast<unsigned>(openCountP),
                static_cast<unsigned>(modeP),
                static_cast<unsigned>(cardNoP),
                static_cast<unsigned>(resDBP));
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
            return handledTrap(frame, foundDb, foundDb);
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
static constexpr uint32_t kTrapHeapBase = 0x00002000u;
static constexpr uint32_t kTrapHeapSize = 1024u;
static constexpr uint32_t kPalmCodeBase = 0x00010000u;
static constexpr uint32_t kPalmEntryProbeOffset = 0x10u;
static uint8_t gProbeRam[kProbeRamSize];
static uint8_t gTrapHeap[kTrapHeapSize];
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

static bool trapHeapAddressToOffset(uint32_t address, uint32_t width, uint32_t& offset)
{
    if (address < kTrapHeapBase)
    {
        return false;
    }

    offset = address - kTrapHeapBase;
    return offset + width <= kTrapHeapSize;
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

    if (trapHeapAddressToOffset(address, 1, offset))
    {
        return gTrapHeap[offset];
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
    if (trapHeapAddressToOffset(address, 1, offset))
    {
        gTrapHeap[offset] = value;
        return;
    }

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
    memset(gTrapHeap, 0, sizeof(gTrapHeap));
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
    memset(gTrapHeap, 0, sizeof(gTrapHeap));
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
    memset(gTrapHeap, 0, sizeof(gTrapHeap));
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
void palmDisplayShowMemoPadProbe(uint16_t selector, uint32_t trapCount, const char* titleText, const char* memoText);
void palmDisplayApplyPalmUiTrap(uint16_t selector, uint32_t trapCount, uint8_t role, const char* text);
void palmDisplayPalmUiBeginForm(uint16_t selector, uint32_t trapCount, const char* titleText);
void palmDisplayPalmUiSetCategory(uint16_t selector, uint32_t trapCount, const char* categoryText);
void palmDisplayPalmUiSetListCount(uint16_t selector, uint32_t trapCount, uint16_t recordCount);
void palmDisplayPalmUiSetListRow(uint16_t selector, uint32_t trapCount, uint16_t rowIndex, const char* text);
void palmDisplayPalmUiDrawButton(uint16_t selector, uint32_t trapCount, uint16_t controlId, const char* labelText);
void palmDisplayPalmUiDrawText(uint16_t selector, uint32_t trapCount, int16_t x, int16_t y, const char* text);
void palmDisplayPollSerialCommands();
"
    End Function

    Private Shared Function RenderPalmDisplayCpp() As String
        Return "#include ""palm_display.h""

#include <Arduino.h>
#include <esp_err.h>
#include <esp_heap_caps.h>
#include <esp_lcd_panel_ops.h>
#include <esp_lcd_panel_rgb.h>
#include <cstring>

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
static char gPalmUiTitle[32] = ""Memo Pad"";
static char gPalmUiCategory[16] = ""All"";
static char gPalmUiMemo[32] = """";
static char gPalmUiRows[5][32] = {};
static char gPalmUiNewButton[16] = """";
static char gPalmUiDetailsButton[16] = """";
static uint16_t gPalmUiListCount = 0;
static bool gPalmUiCategoryVisible = false;
static bool gPalmUiSurfaceStarted = false;

static constexpr uint8_t kPalmUiRoleForm = 1;
static constexpr uint8_t kPalmUiRoleTitle = 2;
static constexpr uint8_t kPalmUiRoleMemoText = 3;

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
        case 'E': return (const uint8_t[7]){0x1F,0x10,0x10,0x1E,0x10,0x10,0x1F}[row];
        case 'H': return (const uint8_t[7]){0x11,0x11,0x11,0x1F,0x11,0x11,0x11}[row];
        case 'I': return (const uint8_t[7]){0x0E,0x04,0x04,0x04,0x04,0x04,0x0E}[row];
        case 'M': return (const uint8_t[7]){0x11,0x1B,0x15,0x15,0x11,0x11,0x11}[row];
        case 'N': return (const uint8_t[7]){0x11,0x19,0x15,0x13,0x11,0x11,0x11}[row];
        case 'P': return (const uint8_t[7]){0x1E,0x11,0x11,0x1E,0x10,0x10,0x10}[row];
        case 'R': return (const uint8_t[7]){0x1E,0x11,0x11,0x1E,0x14,0x12,0x11}[row];
        case 'S': return (const uint8_t[7]){0x0F,0x10,0x10,0x0E,0x01,0x01,0x1E}[row];
        case 'T': return (const uint8_t[7]){0x1F,0x04,0x04,0x04,0x04,0x04,0x04}[row];
        case 'U': return (const uint8_t[7]){0x11,0x11,0x11,0x11,0x11,0x11,0x0E}[row];
        case 'a': return (const uint8_t[7]){0x00,0x00,0x0E,0x01,0x0F,0x11,0x0F}[row];
        case 'c': return (const uint8_t[7]){0x00,0x00,0x0F,0x10,0x10,0x10,0x0F}[row];
        case 'd': return (const uint8_t[7]){0x01,0x01,0x0F,0x11,0x11,0x11,0x0F}[row];
        case 'e': return (const uint8_t[7]){0x00,0x00,0x0E,0x11,0x1F,0x10,0x0E}[row];
        case 'f': return (const uint8_t[7]){0x06,0x08,0x08,0x1E,0x08,0x08,0x08}[row];
        case 'g': return (const uint8_t[7]){0x00,0x00,0x0F,0x11,0x0F,0x01,0x0E}[row];
        case 'h': return (const uint8_t[7]){0x10,0x10,0x1E,0x11,0x11,0x11,0x11}[row];
        case 'i': return (const uint8_t[7]){0x04,0x00,0x0C,0x04,0x04,0x04,0x0E}[row];
        case 'l': return (const uint8_t[7]){0x0C,0x04,0x04,0x04,0x04,0x04,0x0E}[row];
        case 'm': return (const uint8_t[7]){0x00,0x00,0x1A,0x15,0x15,0x15,0x15}[row];
        case 'n': return (const uint8_t[7]){0x00,0x00,0x1E,0x11,0x11,0x11,0x11}[row];
        case 'o': return (const uint8_t[7]){0x00,0x00,0x0E,0x11,0x11,0x11,0x0E}[row];
        case 'p': return (const uint8_t[7]){0x00,0x00,0x1E,0x11,0x1E,0x10,0x10}[row];
        case 'r': return (const uint8_t[7]){0x00,0x00,0x16,0x19,0x10,0x10,0x10}[row];
        case 's': return (const uint8_t[7]){0x00,0x00,0x0F,0x10,0x0E,0x01,0x1E}[row];
        case 't': return (const uint8_t[7]){0x04,0x04,0x1F,0x04,0x04,0x05,0x02}[row];
        case 'u': return (const uint8_t[7]){0x00,0x00,0x11,0x11,0x11,0x13,0x0D}[row];
        case 'v': return (const uint8_t[7]){0x00,0x00,0x11,0x11,0x11,0x0A,0x04}[row];
        case 'w': return (const uint8_t[7]){0x00,0x00,0x11,0x11,0x15,0x15,0x0A}[row];
        case 'x': return (const uint8_t[7]){0x00,0x00,0x11,0x0A,0x04,0x0A,0x11}[row];
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

static void drawPalmRectOutline(int x, int y, int w, int h, uint16_t color)
{
    fillPalmRect(x, y, w, 1, color);
    fillPalmRect(x, y + h - 1, w, 1, color);
    fillPalmRect(x, y, 1, h, color);
    fillPalmRect(x + w - 1, y, 1, h, color);
}

static void drawPalmPopupArrow(int x, int y, uint16_t color)
{
    fillPalmRect(x, y, 5, 1, color);
    fillPalmRect(x + 1, y + 1, 3, 1, color);
    fillPalmRect(x + 2, y + 2, 1, 1, color);
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

static void drawPalmButton(int x, int y, int w, int h, const char* text, uint16_t bg, uint16_t ink)
{
    fillPalmRect(x, y, w, h, bg);
    drawPalmRectOutline(x, y, w, h, ink);
    fillPalmRect(x + 1, y + 1, w - 2, 1, 0xFFFFu);
    fillPalmRect(x + 1, y + 1, 1, h - 2, 0xFFFFu);
    const int textW = static_cast<int>(strlen(text)) * 6 - 1;
    drawPalmText(x + (w - textW) / 2, y + 3, text, ink, 1);
}

static void copyPalmUiText(char* destination, size_t destinationSize, const char* source)
{
    if (destinationSize == 0 || source == nullptr || source[0] == '\0')
    {
        return;
    }

    strncpy(destination, source, destinationSize);
    destination[destinationSize - 1] = '\0';
}

static void resetPalmUiSurface(const char* titleText)
{
    copyPalmUiText(gPalmUiTitle, sizeof(gPalmUiTitle), titleText != nullptr && titleText[0] != '\0' ? titleText : ""Memo Pad"");
    strncpy(gPalmUiCategory, ""All"", sizeof(gPalmUiCategory));
    gPalmUiCategory[sizeof(gPalmUiCategory) - 1] = '\0';
    gPalmUiMemo[0] = '\0';
    gPalmUiNewButton[0] = '\0';
    gPalmUiDetailsButton[0] = '\0';
    for (size_t i = 0; i < sizeof(gPalmUiRows) / sizeof(gPalmUiRows[0]); ++i)
    {
        gPalmUiRows[i][0] = '\0';
    }
    gPalmUiListCount = 0;
    gPalmUiCategoryVisible = false;
    gPalmUiSurfaceStarted = true;
}

static void renderMemoPadUiSurface(uint16_t selector, uint32_t trapCount);

static void presentPalmUiSurface(uint16_t selector, uint32_t trapCount)
{
    ++gDisplayGeneration;
    renderMemoPadUiSurface(selector, trapCount);

    if (gPanel != nullptr)
    {
        ESP_ERROR_CHECK(esp_lcd_panel_draw_bitmap(gPanel, 0, 0, kScreenW, kScreenH, gFrameBuffer));
        esp_lcd_panel_disp_on_off(gPanel, true);
    }
}

static void renderMemoPadUiSurface(uint16_t selector, uint32_t trapCount)
{
    renderRuntimeUiProbeFrame(selector, trapCount);

    // Keep the probe monochrome for now. Black/white/gray survive RGB/BGR
    // swaps and make the Palm UI shape readable while color order is unknown.
    const uint16_t lcdBg = 0xFFFFu;
    const uint16_t lcdInk = 0x0000u;
    const uint16_t chrome = 0x0000u;
    const uint16_t chromeLight = 0xE71Cu;
    const uint16_t line = 0x8410u;
    const uint16_t shadow = 0xA514u;

    fillPalmRect(3, 3, 154, 154, lcdBg);
    drawPalmRectOutline(3, 3, 154, 154, lcdInk);

    fillPalmRect(4, 4, 152, 17, chrome);
    fillPalmRect(4, 21, 152, 1, lcdInk);
    drawPalmText(9, 8, gPalmUiTitle, lcdBg, 1);

    if (gPalmUiCategoryVisible)
    {
        fillPalmRect(108, 6, 43, 12, lcdBg);
        drawPalmRectOutline(108, 6, 43, 12, lcdBg);
        drawPalmText(113, 9, gPalmUiCategory, lcdInk, 1);
        drawPalmPopupArrow(143, 11, lcdInk);
    }

    fillPalmRect(147, 24, 7, 111, chromeLight);
    drawPalmRectOutline(147, 24, 7, 111, lcdInk);
    const int visibleRows = 5;
    const int trackY = 27;
    const int trackH = 104;
    const int thumbH = gPalmUiListCount > visibleRows ? 34 : trackH - 6;
    fillPalmRect(149, trackY, 3, thumbH, gPalmUiListCount > visibleRows ? shadow : line);

    for (int y = 42; y < 133; y += 18)
    {
        fillPalmRect(8, y, 136, 1, line);
    }

    if (gPalmUiListCount == 0 && gPalmUiRows[0][0] == '\0' && gPalmUiMemo[0] == '\0')
    {
        drawPalmText(12, 29, ""No Memos"", line, 1);
    }

    for (int row = 0; row < visibleRows; ++row)
    {
        if (gPalmUiRows[row][0] != '\0')
        {
            drawPalmText(12, 29 + row * 18, gPalmUiRows[row], lcdInk, 1);
        }
    }

    if (gPalmUiRows[0][0] == '\0' && gPalmUiMemo[0] != '\0')
    {
        drawPalmText(12, 29, gPalmUiMemo, lcdInk, 1);
    }

    fillPalmRect(4, 136, 152, 1, lcdInk);
    if (gPalmUiNewButton[0] != '\0')
    {
        drawPalmButton(13, 142, 39, 13, gPalmUiNewButton, chromeLight, lcdInk);
    }
    if (gPalmUiDetailsButton[0] != '\0')
    {
        drawPalmButton(61, 142, 63, 13, gPalmUiDetailsButton, chromeLight, lcdInk);
    }
}

static void renderMemoPadProbeFrame(uint16_t selector, uint32_t trapCount, const char* titleText, const char* memoText)
{
    if (!gPalmUiSurfaceStarted)
    {
        resetPalmUiSurface(titleText);
    }

    copyPalmUiText(gPalmUiTitle, sizeof(gPalmUiTitle), titleText);
    copyPalmUiText(gPalmUiMemo, sizeof(gPalmUiMemo), memoText);
    renderMemoPadUiSurface(selector, trapCount);
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

void palmDisplayShowMemoPadProbe(uint16_t selector, uint32_t trapCount, const char* titleText, const char* memoText)
{
    if (!palmDisplayBegin())
    {
        return;
    }

    renderMemoPadProbeFrame(selector, trapCount, titleText, memoText);
    presentPalmUiSurface(selector, trapCount);

    Serial.printf(""LCD Memo Pad probe selector=0x%04X traps=%u title='%s' memo='%s' generation=%u\n"",
        static_cast<unsigned>(selector),
        static_cast<unsigned>(trapCount),
        titleText != nullptr ? titleText : """",
        memoText != nullptr ? memoText : """",
        static_cast<unsigned>(gDisplayGeneration));
}

void palmDisplayApplyPalmUiTrap(uint16_t selector, uint32_t trapCount, uint8_t role, const char* text)
{
    if (!palmDisplayBegin())
    {
        return;
    }

    if (!gPalmUiSurfaceStarted || role == kPalmUiRoleForm)
    {
        resetPalmUiSurface(text);
    }
    else if (role == kPalmUiRoleTitle)
    {
        copyPalmUiText(gPalmUiTitle, sizeof(gPalmUiTitle), text);
    }
    else if (role == kPalmUiRoleMemoText)
    {
        copyPalmUiText(gPalmUiMemo, sizeof(gPalmUiMemo), text);
    }

    presentPalmUiSurface(selector, trapCount);

    Serial.printf(""LCD Palm UI trap selector=0x%04X traps=%u role=%u title='%s' memo='%s' generation=%u\n"",
        static_cast<unsigned>(selector),
        static_cast<unsigned>(trapCount),
        static_cast<unsigned>(role),
        gPalmUiTitle,
        gPalmUiMemo,
        static_cast<unsigned>(gDisplayGeneration));
}

void palmDisplayPalmUiBeginForm(uint16_t selector, uint32_t trapCount, const char* titleText)
{
    if (!palmDisplayBegin())
    {
        return;
    }

    resetPalmUiSurface(titleText);
    presentPalmUiSurface(selector, trapCount);
    Serial.printf(""LCD Palm form selector=0x%04X traps=%u title='%s' generation=%u\n"",
        static_cast<unsigned>(selector),
        static_cast<unsigned>(trapCount),
        gPalmUiTitle,
        static_cast<unsigned>(gDisplayGeneration));
}

void palmDisplayPalmUiSetCategory(uint16_t selector, uint32_t trapCount, const char* categoryText)
{
    if (!palmDisplayBegin())
    {
        return;
    }

    if (!gPalmUiSurfaceStarted)
    {
        resetPalmUiSurface(""Memo Pad"");
    }

    copyPalmUiText(gPalmUiCategory, sizeof(gPalmUiCategory), categoryText);
    gPalmUiCategoryVisible = true;
    presentPalmUiSurface(selector, trapCount);
    Serial.printf(""LCD Palm category selector=0x%04X traps=%u text='%s' generation=%u\n"",
        static_cast<unsigned>(selector),
        static_cast<unsigned>(trapCount),
        gPalmUiCategory,
        static_cast<unsigned>(gDisplayGeneration));
}

void palmDisplayPalmUiSetListCount(uint16_t selector, uint32_t trapCount, uint16_t recordCount)
{
    if (!palmDisplayBegin())
    {
        return;
    }

    if (!gPalmUiSurfaceStarted)
    {
        resetPalmUiSurface(""Memo Pad"");
    }

    gPalmUiListCount = recordCount;
    presentPalmUiSurface(selector, trapCount);
    Serial.printf(""LCD Palm list selector=0x%04X traps=%u records=%u generation=%u\n"",
        static_cast<unsigned>(selector),
        static_cast<unsigned>(trapCount),
        static_cast<unsigned>(gPalmUiListCount),
        static_cast<unsigned>(gDisplayGeneration));
}

void palmDisplayPalmUiSetListRow(uint16_t selector, uint32_t trapCount, uint16_t rowIndex, const char* text)
{
    if (!palmDisplayBegin())
    {
        return;
    }

    if (!gPalmUiSurfaceStarted)
    {
        resetPalmUiSurface(""Memo Pad"");
    }

    if (rowIndex < sizeof(gPalmUiRows) / sizeof(gPalmUiRows[0]))
    {
        copyPalmUiText(gPalmUiRows[rowIndex], sizeof(gPalmUiRows[rowIndex]), text);
        if (rowIndex == 0)
        {
            copyPalmUiText(gPalmUiMemo, sizeof(gPalmUiMemo), text);
        }
    }

    presentPalmUiSurface(selector, trapCount);
    Serial.printf(""LCD Palm list row selector=0x%04X traps=%u row=%u text='%s' generation=%u\n"",
        static_cast<unsigned>(selector),
        static_cast<unsigned>(trapCount),
        static_cast<unsigned>(rowIndex),
        text != nullptr ? text : """",
        static_cast<unsigned>(gDisplayGeneration));
}

void palmDisplayPalmUiDrawText(uint16_t selector, uint32_t trapCount, int16_t x, int16_t y, const char* text)
{
    if (!palmDisplayBegin())
    {
        return;
    }

    if (!gPalmUiSurfaceStarted)
    {
        resetPalmUiSurface(""Memo Pad"");
    }

    if (y < 22)
    {
        copyPalmUiText(gPalmUiTitle, sizeof(gPalmUiTitle), text);
    }
    else
    {
        copyPalmUiText(gPalmUiMemo, sizeof(gPalmUiMemo), text);
    }

    presentPalmUiSurface(selector, trapCount);
    Serial.printf(""LCD Palm text selector=0x%04X traps=%u x=%d y=%d text='%s' generation=%u\n"",
        static_cast<unsigned>(selector),
        static_cast<unsigned>(trapCount),
        static_cast<int>(x),
        static_cast<int>(y),
        text != nullptr ? text : """",
        static_cast<unsigned>(gDisplayGeneration));
}

void palmDisplayPalmUiDrawButton(uint16_t selector, uint32_t trapCount, uint16_t controlId, const char* labelText)
{
    if (!palmDisplayBegin())
    {
        return;
    }

    if (!gPalmUiSurfaceStarted)
    {
        resetPalmUiSurface(""Memo Pad"");
    }

    if (controlId == 1001u)
    {
        copyPalmUiText(gPalmUiNewButton, sizeof(gPalmUiNewButton), labelText);
    }
    else if (controlId == 1002u)
    {
        copyPalmUiText(gPalmUiDetailsButton, sizeof(gPalmUiDetailsButton), labelText);
    }

    presentPalmUiSurface(selector, trapCount);
    Serial.printf(""LCD Palm button selector=0x%04X traps=%u id=%u label='%s' generation=%u\n"",
        static_cast<unsigned>(selector),
        static_cast<unsigned>(trapCount),
        static_cast<unsigned>(controlId),
        labelText != nullptr ? labelText : """",
        static_cast<unsigned>(gDisplayGeneration));
}

static uint16_t readPalmPixelRgb565(int palmX, int palmY)
{
    if (gFrameBuffer == nullptr || palmX < 0 || palmY < 0 || palmX >= kPalmLcdW || palmY >= kPalmLcdH)
    {
        return 0;
    }

    const int screenX = kPalmLcdViewX + (palmX * kPalmLcdViewW) / kPalmLcdW;
    const int screenY = kPalmLcdViewY + (palmY * kPalmLcdViewH) / kPalmLcdH;
    return gFrameBuffer[static_cast<uint32_t>(screenY) * kScreenW + screenX];
}

static void writePalmLcdSnapshotSerial()
{
    if (!palmDisplayBegin())
    {
        Serial.println(""PALM_LCD_SNAPSHOT_ERROR display_not_ready"");
        return;
    }

    Serial.printf(""PALM_LCD_SNAPSHOT_BEGIN %d %d RGB565BE %u\n"",
        kPalmLcdW,
        kPalmLcdH,
        static_cast<unsigned>(gDisplayGeneration));

    uint8_t bytes[64];
    size_t used = 0;
    for (int y = 0; y < kPalmLcdH; ++y)
    {
        for (int x = 0; x < kPalmLcdW; ++x)
        {
            const uint16_t pixel = readPalmPixelRgb565(x, y);
            bytes[used++] = static_cast<uint8_t>((pixel >> 8) & 0xffu);
            bytes[used++] = static_cast<uint8_t>(pixel & 0xffu);
            if (used == sizeof(bytes))
            {
                Serial.write(bytes, used);
                used = 0;
            }
        }
    }

    if (used > 0)
    {
        Serial.write(bytes, used);
    }

    Serial.print(""\nPALM_LCD_SNAPSHOT_END\n"");
}

void palmDisplayPollSerialCommands()
{
    static char command[24];
    static uint8_t length = 0;

    while (Serial.available() > 0)
    {
        const char ch = static_cast<char>(Serial.read());
        if (ch == '\r')
        {
            continue;
        }

        if (ch == '\n')
        {
            command[length] = '\0';
            if (strcmp(command, ""lcdsnap"") == 0 || strcmp(command, ""LCDSNAP"") == 0)
            {
                writePalmLcdSnapshotSerial();
            }
            else if (length > 0)
            {
                Serial.printf(""PALM_LCD_UNKNOWN_COMMAND %s\n"", command);
            }
            length = 0;
            continue;
        }

        if (length + 1u < sizeof(command))
        {
            command[length++] = ch;
        }
    }
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
