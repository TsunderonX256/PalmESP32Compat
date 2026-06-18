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
    Public Property ExtractedFonts As List(Of ExtractedPalmFontResource) = New List(Of ExtractedPalmFontResource)()
End Class

Public NotInheritable Class ExportedPalmApplication
    Public Property Name As String = ""
    Public Property CreatorCode As String = ""
    Public Property TypeCode As String = ""
    Public Property SourceOffset As Integer
    Public Property Size As Integer
    Public Property RelativePath As String = ""
End Class

Public NotInheritable Class ExtractedPalmFontResource
    Public Property SourceName As String = ""
    Public Property ResourceId As UShort
    Public Property SourceOffset As Integer
    Public Property Bytes As Byte() = Array.Empty(Of Byte)()
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
        result.ExtractedFonts = ExtractPalmFontResources(rom, databases)

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
        WriteText(Path.Combine(outputDirectory, "src", "generated", "palm_font_resources.h"), RenderFontResourcesHeader())
        WriteText(Path.Combine(outputDirectory, "src", "generated", "palm_font_resources.cpp"), RenderFontResourcesCpp(result.ExtractedFonts))
        WriteText(Path.Combine(outputDirectory, "docs", "generated-from-rom.txt"), RenderGenerationNotes(request, rom.Length, databases.Count, result.ExportedApplications, result.ExtractedFonts))
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

    Private Shared Function ExtractPalmFontResources(rom As Byte(), databases As List(Of PalmDatabase)) As List(Of ExtractedPalmFontResource)
        Dim fonts As New List(Of ExtractedPalmFontResource)()
        Dim seen As New HashSet(Of String)(StringComparer.Ordinal)

        For Each db In databases.Where(Function(item) item.IsResourceDatabase)
            For Each resource In PalmRomScanner.ReadResourceData(rom, db)
                If Not resource.TypeCode.Equals("NFNT", StringComparison.Ordinal) Then
                    Continue For
                End If

                If resource.Length <= 0 OrElse resource.StartOffset < 0 OrElse resource.StartOffset + resource.Length > rom.Length Then
                    Continue For
                End If

                Dim key = $"{db.Name}:{resource.ResourceId}:{resource.StartOffset}:{resource.Length}"
                If Not seen.Add(key) Then
                    Continue For
                End If

                Dim bytes(resource.Length - 1) As Byte
                Buffer.BlockCopy(rom, resource.StartOffset, bytes, 0, resource.Length)
                fonts.Add(New ExtractedPalmFontResource With {
                    .SourceName = db.Name,
                    .ResourceId = resource.ResourceId,
                    .SourceOffset = resource.StartOffset,
                    .Bytes = bytes
                })
            Next
        Next

        Return fonts.
            OrderBy(Function(font) font.SourceName, StringComparer.OrdinalIgnoreCase).
            ThenBy(Function(font) font.ResourceId).
            ToList()
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

struct PalmLoadedResourceCatalogEntry
{
    char type[5];
    uint16_t id;
    uint32_t size;
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
    uint16_t catalogResourceCount;
    PalmLoadedResource* resources;
    PalmLoadedCodeResource* codeResources;
    PalmLoadedResourceCatalogEntry* catalogResources;
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

static uint32_t readNextResourceOffset(File& file, uint16_t index, uint16_t count, uint32_t currentOffset, uint32_t fileSize)
{
    uint32_t nextOffset = fileSize;
    for (uint16_t i = 0; i < count; ++i)
    {
        if (i == index)
        {
            continue;
        }

        const uint32_t candidate = readResourceOffsetAt(file, i);
        if (candidate > currentOffset && candidate < nextOffset)
        {
            nextOffset = candidate;
        }
    }
    return nextOffset;
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

    const uint32_t nextOffset = readNextResourceOffset(file, index, count, entry.offset, fileSize);

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

static uint16_t readU16BEFromBytes(const uint8_t* bytes, uint32_t offset)
{
    return static_cast<uint16_t>((static_cast<uint16_t>(bytes[offset]) << 8) | bytes[offset + 1u]);
}

static uint32_t readU32BEFromBytes(const uint8_t* bytes, uint32_t offset)
{
    return (static_cast<uint32_t>(bytes[offset]) << 24) |
        (static_cast<uint32_t>(bytes[offset + 1u]) << 16) |
        (static_cast<uint32_t>(bytes[offset + 2u]) << 8) |
        static_cast<uint32_t>(bytes[offset + 3u]);
}

static uint16_t countOverlayCatalogEntries(const PalmLoadedApp& loadedApp)
{
    uint16_t count = 0;
    for (uint16_t i = 0; i < loadedApp.loadedResourceCount; ++i)
    {
        const PalmLoadedResource& resource = loadedApp.resources[i];
        if (strcmp(resource.type, ""ovly"") != 0 || resource.bytes == nullptr || resource.size < 32u)
        {
            continue;
        }

        const uint16_t entryCount = readU16BEFromBytes(resource.bytes, 30u);
        if (entryCount <= 256u && 32u + static_cast<uint32_t>(entryCount) * 16u <= resource.size)
        {
            count = static_cast<uint16_t>(count + entryCount);
        }
    }

    return count;
}

static void loadOverlayCatalog(PalmLoadedApp& loadedApp)
{
    const uint16_t catalogCount = countOverlayCatalogEntries(loadedApp);
    if (catalogCount == 0)
    {
        return;
    }

    loadedApp.catalogResources = static_cast<PalmLoadedResourceCatalogEntry*>(calloc(catalogCount, sizeof(PalmLoadedResourceCatalogEntry)));
    if (loadedApp.catalogResources == nullptr)
    {
        Serial.printf(""  overlay catalog allocation failed: %u entries\n"", static_cast<unsigned>(catalogCount));
        return;
    }

    uint16_t catalogIndex = 0;
    for (uint16_t i = 0; i < loadedApp.loadedResourceCount; ++i)
    {
        const PalmLoadedResource& resource = loadedApp.resources[i];
        if (strcmp(resource.type, ""ovly"") != 0 || resource.bytes == nullptr || resource.size < 32u)
        {
            continue;
        }

        const uint16_t entryCount = readU16BEFromBytes(resource.bytes, 30u);
        if (entryCount > 256u || 32u + static_cast<uint32_t>(entryCount) * 16u > resource.size)
        {
            Serial.printf(""    overlay catalog #%u ignored: entries=%u size=%u\n"",
                static_cast<unsigned>(resource.id),
                static_cast<unsigned>(entryCount),
                static_cast<unsigned>(resource.size));
            continue;
        }

        for (uint16_t entryIndex = 0; entryIndex < entryCount && catalogIndex < catalogCount; ++entryIndex)
        {
            const uint32_t offset = 32u + static_cast<uint32_t>(entryIndex) * 16u;
            PalmLoadedResourceCatalogEntry& catalog = loadedApp.catalogResources[catalogIndex++];
            memcpy(catalog.type, resource.bytes + offset + 2u, 4u);
            catalog.type[4] = '\0';
            catalog.id = readU16BEFromBytes(resource.bytes, offset + 6u);
            catalog.size = readU32BEFromBytes(resource.bytes, offset + 8u);
            catalog.checksum = readU32BEFromBytes(resource.bytes, offset + 12u);
            Serial.printf(""    overlay catalog %s #%u size=%u checksum=0x%08X\n"",
                catalog.type,
                static_cast<unsigned>(catalog.id),
                static_cast<unsigned>(catalog.size),
                static_cast<unsigned>(catalog.checksum));
        }
    }

    loadedApp.catalogResourceCount = catalogIndex;
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
    loadOverlayCatalog(loadedApp);
    Serial.printf(""  resident resources: %u, code resources: %u, catalog resources: %u\n"",
        static_cast<unsigned>(loadedApp.loadedResourceCount),
        static_cast<unsigned>(loadedApp.codeResourceCount),
        static_cast<unsigned>(loadedApp.catalogResourceCount));
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
        Serial.printf(""  resident app %u: %s resources=%u codeResources=%u catalogResources=%u\n"",
            static_cast<unsigned>(appIndex),
            app.dbName,
            static_cast<unsigned>(app.loadedResourceCount),
            static_cast<unsigned>(app.codeResourceCount),
            static_cast<unsigned>(app.catalogResourceCount));
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
static constexpr uint32_t kFakeFieldTextHandle = 0x00020050u;
static constexpr uint32_t kFakeMemoRecordPtr = 0x00002000u;
static constexpr uint32_t kFakeResourcePtr = 0x00002100u;
static constexpr uint32_t kFakeAllocationPtr = 0x00002200u;
static constexpr uint32_t kFakeFieldTextPtr = 0x00002300u;
static constexpr uint32_t kFakeListTextPtr = 0x00002380u;
static constexpr uint32_t kTinyHeapBase = 0x00002400u;
static constexpr uint32_t kTinyHeapEnd = 0x00004000u;
static constexpr uint32_t kTinyHandleBase = 0x00030000u;
static constexpr uint8_t kTinyHandleCapacity = 24u;
static constexpr uint8_t kTinyFreeBlockCapacity = 16u;
static constexpr uint16_t kFakeMemoRecordCapacity = 12u;
static constexpr uint32_t kFakeFormHandleBase = 0x00040000u;
static constexpr uint32_t kFakeDisplayWindowHandle = 0x00050000u;
static constexpr uint32_t kFakeSavedBitsWindowHandle = 0x00050010u;
static constexpr uint8_t kFakeFormObjectCapacity = 8u;
static constexpr uint8_t kPalmUiRoleForm = 1u;
static constexpr uint8_t kPalmUiRoleTitle = 2u;
static constexpr uint8_t kPalmUiRoleMemoText = 3u;
static constexpr uint16_t kMemoListId = 1000u;
static constexpr uint16_t kMemoNewButtonId = 1001u;
static constexpr uint16_t kMemoDetailsButtonId = 1002u;
static constexpr uint16_t kMemoDoneButtonId = 1003u;
static constexpr uint16_t kMemoCancelButtonId = 1004u;
static constexpr uint16_t kModalOkButtonId = 9001u;
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
static uint16_t gFakeFieldMaxChars = 63u;
static uint16_t gFakeFieldInsPt = 0u;
static bool gFakeFieldDirty = false;
static bool gFakeFieldUsable = true;
static uint16_t gFakeMemoRecordScratchIndex = 0;
static uint32_t gFakeMemoRecordScratchSize = 64u;
static bool gFakeMemoRecordScratchDirty = false;
static uint8_t gUiGeometryLogCount = 0;
static uint8_t gScratchDumpLogCount = 0;
static uint8_t gDatabaseTraceLogCount = 0;
struct TinyPalmHandle
{
    bool used;
    uint32_t handle;
    uint32_t ptr;
    uint32_t size;
    uint32_t capacity;
    uint16_t lockCount;
    bool recordBacked;
    uint16_t recordIndex;
};
struct TinyPalmFreeBlock
{
    bool used;
    uint32_t ptr;
    uint32_t size;
};
struct FakePalmFormObject
{
    bool used;
    uint16_t objectId;
    uint16_t kind;
    uint32_t ptr;
    int16_t x;
    int16_t y;
    int16_t w;
    int16_t h;
    int16_t value;
    bool enabled;
    bool usable;
    bool visible;
    char label[16];
};
static TinyPalmHandle gTinyHandles[kTinyHandleCapacity] = {};
static TinyPalmFreeBlock gTinyFreeBlocks[kTinyFreeBlockCapacity] = {};
static uint32_t gTinyHeapNext = kTinyHeapBase;
static uint16_t gTinyHandleGeneration = 1;
static bool gFakeMemoRecordTableSeeded = false;
static uint16_t gFakeMemoRecordCount = 0;
static uint32_t gFakeMemoRecordHandles[kFakeMemoRecordCapacity] = {};
static uint32_t gFakeMemoRecordUniqueIds[kFakeMemoRecordCapacity] = {};
static bool gFakeMemoRecordDirty[kFakeMemoRecordCapacity] = {};
static uint32_t gFakeMemoRecordNextUniqueId = 1u;
static uint16_t gFakeActiveFormId = 0;
static bool gFakeActiveFormCataloged = false;
static uint32_t gFakeActiveFormCatalogSize = 0;
static uint32_t gFakeActiveFormCatalogChecksum = 0;
static uint32_t gFakeActiveFormHandle = 0;
static uint32_t gFakeActiveFormPtr = 0;
static uint16_t gFakeFormFocusIndex = 0xffffu;
static FakePalmFormObject gFakeFormObjects[kFakeFormObjectCapacity] = {};
static uint8_t gFakeFormObjectCount = 0;
static int16_t gFakeListTopItem = 0;
static int16_t gFakeListVisibleItems = 5;
static uint16_t gFakeActiveMenuResourceId = 0;
static uint32_t gFakeActiveMenuHandle = 0;
static uint32_t gFakeActiveMenuPtr = 0;
static bool gFakeMenuVisible = false;
static uint32_t gFakeDrawWindowHandle = kFakeDisplayWindowHandle;
static uint32_t gFakeActiveWindowHandle = kFakeDisplayWindowHandle;
static int16_t gFakeClipX = 0;
static int16_t gFakeClipY = 0;
static int16_t gFakeClipW = 160;
static int16_t gFakeClipH = 160;

static bool looksWritablePointer(uint32_t address);
static void writeCString(uint32_t address, const char* text, uint32_t maxBytes);
static void writeWordIfPointer(uint32_t address, uint16_t value);
static uint32_t clampMemoRecordSize(uint32_t size);
static void copyMemoryBytes(uint32_t destination, uint32_t source, uint32_t byteCount);
static void zeroMemory(uint32_t address, uint32_t byteCount);
static void captureMemoProbeText(const char* memoText);
static void publishFakeMemoRows(uint16_t selector, uint32_t trapCount);
static void showMemoPadProbe(uint16_t selector, const char* capturedTitle, const char* capturedMemo);
static uint32_t tinyHandleAlloc(uint32_t size, bool recordBacked = false, uint16_t recordIndex = 0);
static uint32_t tinyHandleLock(uint32_t handle);
static bool tinyHandleUnlock(uint32_t handle);
static uint32_t tinyHandleSize(uint32_t handle);
static uint32_t tinyHandleRecoverFromPtr(uint32_t ptr);
static uint32_t tinyPtrSize(uint32_t ptr);
static uint16_t tinyHandleLockCount(uint32_t handle);
static bool tinyHandleResize(uint32_t handle, uint32_t newSize);
static bool tinyHandleFree(uint32_t handle);
static bool tinyPtrFree(uint32_t ptr);
static uint32_t fixedHandleForPtr(uint32_t ptr);
static uint32_t fixedPtrSize(uint32_t ptr);
static uint32_t palmHandleRecoverFromPtr(uint32_t ptr);
static uint32_t palmPtrSize(uint32_t ptr);
static void seedFakeMemoRecordTable();
static uint32_t fakeMemoRecordHandle(uint16_t index);
static bool fakeMemoInsertRecord(uint16_t index, const char* text, uint32_t requestedSize, uint32_t* outHandle);
static bool fakeMemoRemoveRecord(uint16_t index);
static uint32_t fakeMemoResizeRecord(uint16_t index, uint32_t newSize);
static bool commitFakeMemoRecord(uint16_t index);
static bool commitFakeMemoRecordByPtr(uint32_t ptr);
static bool markFakeMemoRecordDirtyByPtr(uint32_t ptr);
static uint32_t fakeFormInit(uint16_t formId);
static void fakeFormDelete(uint32_t formP);
static uint32_t fakeFormEnsureActive(uint32_t formP);
static uint16_t fakeFormGetObjectIndex(uint32_t formP, uint16_t objectId);
static uint32_t fakeFormGetObjectPtr(uint32_t formP, uint16_t objectIndex);
static FakePalmFormObject* fakeFormObjectAtIndex(uint32_t formP, uint16_t objectIndex);
static uint16_t fakeFormGetObjectIndexFromPtr(uint32_t formP, uint32_t objectP);
static void fakeFormWriteRectangle(uint32_t rectP, const FakePalmFormObject& object);
static void fakeFormDraw(uint16_t selector, uint32_t formP);
static FakePalmFormObject* fakeFormObjectForPtr(uint32_t objectP);
static bool fakeControlIsControl(FakePalmFormObject* object);
static void fakeControlDraw(uint16_t selector, uint32_t controlP);
static void fakeControlSetLabel(uint32_t controlP, const char* label);
static uint32_t fakeControlGetLabelPtr(uint32_t controlP);
static void fakeControlHit(uint16_t selector, uint32_t controlP, const char* source);
static bool fakeControlHandleEvent(uint16_t selector, uint32_t controlP, uint32_t eventP);
static bool fakeListIsMemoList(uint32_t listP);
static int16_t fakeListClampSelection(int16_t itemNum);
static void fakeListDraw(uint16_t selector, uint32_t listP);
static uint32_t fakeMenuInit(uint16_t resourceId);
static uint32_t fakeMenuSetActive(uint32_t menuP);
static void fakeMenuDispose(uint32_t menuP);
static bool fakeMenuHandleEvent(uint32_t menuP, uint32_t eventP, uint32_t errorP);
static void fakeMenuDraw(uint32_t menuP);
static void fakeMenuErase(uint32_t menuP);
static void writePalmRectangle(uint32_t rectP, int16_t x, int16_t y, int16_t w, int16_t h);
static bool readPalmRectangle(uint32_t rectP, int16_t& x, int16_t& y, int16_t& w, int16_t& h);

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
    gFakeFieldMaxChars = 63u;
    gFakeFieldInsPt = 0u;
    gFakeFieldDirty = false;
    gFakeFieldUsable = true;
    gFakeMemoRecordScratchIndex = 0;
    gFakeMemoRecordScratchSize = 64u;
    gFakeMemoRecordScratchDirty = false;
    gUiGeometryLogCount = 0;
    gScratchDumpLogCount = 0;
    gDatabaseTraceLogCount = 0;
    memset(gTinyHandles, 0, sizeof(gTinyHandles));
    memset(gTinyFreeBlocks, 0, sizeof(gTinyFreeBlocks));
    gTinyHeapNext = kTinyHeapBase;
    memset(gFakeMemoRecordHandles, 0, sizeof(gFakeMemoRecordHandles));
    memset(gFakeMemoRecordUniqueIds, 0, sizeof(gFakeMemoRecordUniqueIds));
    memset(gFakeMemoRecordDirty, 0, sizeof(gFakeMemoRecordDirty));
    gFakeMemoRecordTableSeeded = false;
    gFakeMemoRecordCount = 0;
    gFakeMemoRecordNextUniqueId = 1u;
    gFakeActiveFormId = 0;
    gFakeActiveFormCataloged = false;
    gFakeActiveFormCatalogSize = 0;
    gFakeActiveFormCatalogChecksum = 0;
    gFakeActiveFormHandle = 0;
    gFakeActiveFormPtr = 0;
    gFakeFormFocusIndex = 0xffffu;
    memset(gFakeFormObjects, 0, sizeof(gFakeFormObjects));
    gFakeFormObjectCount = 0;
    gFakeListTopItem = 0;
    gFakeListVisibleItems = 5;
    gFakeActiveMenuResourceId = 0;
    gFakeActiveMenuHandle = 0;
    gFakeActiveMenuPtr = 0;
    gFakeMenuVisible = false;
    gFakeDrawWindowHandle = kFakeDisplayWindowHandle;
    gFakeActiveWindowHandle = kFakeDisplayWindowHandle;
    gFakeClipX = 0;
    gFakeClipY = 0;
    gFakeClipW = 160;
    gFakeClipH = 160;
    ++gTinyHandleGeneration;
    if (gTinyHandleGeneration == 0)
    {
        gTinyHandleGeneration = 1;
    }
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

static const PalmLoadedResourceCatalogEntry* findCatalogResource(uint32_t type, uint16_t id)
{
    if (gTrapApp == nullptr)
    {
        return nullptr;
    }

    for (uint16_t i = 0; i < gTrapApp->catalogResourceCount; ++i)
    {
        const PalmLoadedResourceCatalogEntry& resource = gTrapApp->catalogResources[i];
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

static uint32_t fakeMemoRecordRequestedSize(const char* text, uint32_t requestedSize)
{
    const uint32_t textSize = text == nullptr ? 1u : static_cast<uint32_t>(strlen(text)) + 1u;
    uint32_t size = requestedSize > textSize ? requestedSize : textSize;
    if (size < 64u)
    {
        size = 64u;
    }
    return size;
}

static bool writeFakeMemoRecordText(uint32_t handle, const char* text, uint32_t requestedSize)
{
    if (handle == 0)
    {
        return false;
    }

    const uint32_t wantedSize = fakeMemoRecordRequestedSize(text, requestedSize);
    if (tinyHandleSize(handle) < wantedSize && !tinyHandleResize(handle, wantedSize))
    {
        return false;
    }

    const uint32_t ptr = tinyHandleLock(handle);
    if (ptr == 0)
    {
        return false;
    }

    const uint32_t size = tinyHandleSize(handle);
    zeroMemory(ptr, size);
    writeCString(ptr, text == nullptr ? """" : text, size);
    tinyHandleUnlock(handle);
    return true;
}

static uint32_t allocateFakeMemoRecordHandle(const char* text, uint32_t requestedSize)
{
    const uint32_t size = fakeMemoRecordRequestedSize(text, requestedSize);
    const uint32_t handle = tinyHandleAlloc(size, true);
    if (handle == 0)
    {
        return 0;
    }

    if (!writeFakeMemoRecordText(handle, text, size))
    {
        tinyHandleFree(handle);
        return 0;
    }

    return handle;
}

static void seedFakeMemoRecordTable()
{
    if (gFakeMemoRecordTableSeeded)
    {
        return;
    }

    const uint16_t displayCount = palmDisplayMemoRecordCount();
    gFakeMemoRecordCount = displayCount > kFakeMemoRecordCapacity ? kFakeMemoRecordCapacity : displayCount;
    for (uint16_t i = 0; i < gFakeMemoRecordCount; ++i)
    {
        gFakeMemoRecordHandles[i] = allocateFakeMemoRecordHandle(palmDisplayMemoText(i), 64u);
        gFakeMemoRecordUniqueIds[i] = gFakeMemoRecordNextUniqueId++;
        gFakeMemoRecordDirty[i] = false;
    }

    gFakeMemoRecordTableSeeded = true;
}

static uint32_t fakeMemoRecordHandle(uint16_t index)
{
    seedFakeMemoRecordTable();
    if (index >= gFakeMemoRecordCount)
    {
        return 0;
    }

    if (gFakeMemoRecordHandles[index] == 0)
    {
        gFakeMemoRecordHandles[index] = allocateFakeMemoRecordHandle(palmDisplayMemoText(index), 64u);
        if (gFakeMemoRecordUniqueIds[index] == 0)
        {
            gFakeMemoRecordUniqueIds[index] = gFakeMemoRecordNextUniqueId++;
        }
    }

    return gFakeMemoRecordHandles[index];
}

static int16_t fakeMemoRecordIndexForHandle(uint32_t handle)
{
    seedFakeMemoRecordTable();
    for (uint16_t i = 0; i < gFakeMemoRecordCount; ++i)
    {
        if (gFakeMemoRecordHandles[i] == handle)
        {
            return static_cast<int16_t>(i);
        }
    }

    return -1;
}

static int16_t fakeMemoRecordIndexForPtr(uint32_t ptr)
{
    const uint32_t handle = tinyHandleRecoverFromPtr(ptr);
    return handle == 0 ? -1 : fakeMemoRecordIndexForHandle(handle);
}

static bool fakeMemoInsertRecord(uint16_t index, const char* text, uint32_t requestedSize, uint32_t* outHandle)
{
    seedFakeMemoRecordTable();
    if (gFakeMemoRecordCount >= kFakeMemoRecordCapacity)
    {
        return false;
    }

    if (index > gFakeMemoRecordCount)
    {
        index = gFakeMemoRecordCount;
    }

    const uint32_t handle = allocateFakeMemoRecordHandle(text, requestedSize);
    if (handle == 0)
    {
        return false;
    }

    if (!palmDisplayInsertMemoRecord(index, text == nullptr ? """" : text))
    {
        tinyHandleFree(handle);
        return false;
    }

    for (int i = static_cast<int>(gFakeMemoRecordCount); i > static_cast<int>(index); --i)
    {
        gFakeMemoRecordHandles[i] = gFakeMemoRecordHandles[i - 1];
        gFakeMemoRecordUniqueIds[i] = gFakeMemoRecordUniqueIds[i - 1];
        gFakeMemoRecordDirty[i] = gFakeMemoRecordDirty[i - 1];
    }

    gFakeMemoRecordHandles[index] = handle;
    gFakeMemoRecordUniqueIds[index] = gFakeMemoRecordNextUniqueId++;
    gFakeMemoRecordDirty[index] = false;
    ++gFakeMemoRecordCount;
    if (outHandle != nullptr)
    {
        *outHandle = handle;
    }
    return true;
}

static bool fakeMemoRemoveRecord(uint16_t index)
{
    seedFakeMemoRecordTable();
    if (index >= gFakeMemoRecordCount)
    {
        return false;
    }

    const uint32_t handle = gFakeMemoRecordHandles[index];
    if (handle != 0)
    {
        tinyHandleFree(handle);
    }

    for (uint16_t i = index; i + 1u < gFakeMemoRecordCount; ++i)
    {
        gFakeMemoRecordHandles[i] = gFakeMemoRecordHandles[i + 1u];
        gFakeMemoRecordUniqueIds[i] = gFakeMemoRecordUniqueIds[i + 1u];
        gFakeMemoRecordDirty[i] = gFakeMemoRecordDirty[i + 1u];
    }

    --gFakeMemoRecordCount;
    gFakeMemoRecordHandles[gFakeMemoRecordCount] = 0;
    gFakeMemoRecordUniqueIds[gFakeMemoRecordCount] = 0;
    gFakeMemoRecordDirty[gFakeMemoRecordCount] = false;
    return palmDisplayRemoveMemoRecord(index);
}

static uint32_t fakeMemoResizeRecord(uint16_t index, uint32_t newSize)
{
    const uint32_t handle = fakeMemoRecordHandle(index);
    if (handle == 0)
    {
        return 0;
    }

    if (!tinyHandleResize(handle, newSize))
    {
        return 0;
    }

    gFakeMemoRecordScratchIndex = index;
    gFakeMemoRecordScratchSize = newSize;
    gFakeMemoRecordDirty[index] = true;
    return handle;
}

static bool commitFakeMemoRecord(uint16_t index)
{
    const uint32_t handle = fakeMemoRecordHandle(index);
    if (handle == 0)
    {
        return false;
    }

    const uint32_t ptr = tinyHandleLock(handle);
    if (ptr == 0)
    {
        return false;
    }

    char text[64];
    text[0] = '\0';
    readCString(ptr, text, sizeof(text));
    tinyHandleUnlock(handle);

    const bool updated = palmDisplayUpdateMemoRecord(index, text);
    if (updated)
    {
        captureMemoProbeText(text);
        gFakeMemoRecordDirty[index] = false;
        if (gFakeMemoRecordScratchIndex == index)
        {
            gFakeMemoRecordScratchDirty = false;
        }
    }
    return updated;
}

static bool commitFakeMemoRecordByPtr(uint32_t ptr)
{
    const int16_t index = fakeMemoRecordIndexForPtr(ptr);
    return index >= 0 ? commitFakeMemoRecord(static_cast<uint16_t>(index)) : false;
}

static bool markFakeMemoRecordDirtyByPtr(uint32_t ptr)
{
    const int16_t index = fakeMemoRecordIndexForPtr(ptr);
    if (index < 0)
    {
        return false;
    }

    gFakeMemoRecordDirty[index] = true;
    gFakeMemoRecordScratchIndex = static_cast<uint16_t>(index);
    gFakeMemoRecordScratchDirty = true;
    return true;
}

static uint8_t fakeFormObjectKind(uint16_t objectId)
{
    if (objectId == kMemoListId)
    {
        return 2u;
    }
    if (objectId == kMemoNewButtonId || objectId == kMemoDetailsButtonId || objectId == kMemoDoneButtonId || objectId == kMemoCancelButtonId || objectId == kModalOkButtonId)
    {
        return 1u;
    }
    return 0u;
}

static const char* fakeControlDefaultLabel(uint16_t objectId)
{
    switch (objectId)
    {
        case kMemoNewButtonId: return ""New"";
        case kMemoDetailsButtonId: return ""Details"";
        case kMemoDoneButtonId: return ""Done"";
        case kMemoCancelButtonId: return ""Cancel"";
        case kModalOkButtonId: return ""OK"";
        default: return """";
    }
}

static bool fakeControlIsControl(FakePalmFormObject* object)
{
    return object != nullptr && object->kind == 1u;
}

static void fakeControlSyncMemory(FakePalmFormObject& object)
{
    writeWordIfPointer(object.ptr + 0u, object.objectId);
    writeWordIfPointer(object.ptr + 2u, object.kind);
    writeWordIfPointer(object.ptr + 4u, static_cast<uint16_t>(object.x));
    writeWordIfPointer(object.ptr + 6u, static_cast<uint16_t>(object.y));
    writeWordIfPointer(object.ptr + 8u, static_cast<uint16_t>(object.w));
    writeWordIfPointer(object.ptr + 10u, static_cast<uint16_t>(object.h));
    writeCString(object.ptr + 12u, object.label, 12u);
}

static void fakeFormObjectBounds(uint16_t objectId, int16_t& x, int16_t& y, int16_t& w, int16_t& h)
{
    x = 0;
    y = 0;
    w = 0;
    h = 0;
    switch (objectId)
    {
        case kMemoListId:
            x = 7; y = 24; w = 138; h = 110;
            break;
        case kMemoNewButtonId:
            x = 13; y = 142; w = 39; h = 13;
            break;
        case kMemoDetailsButtonId:
            x = 61; y = 142; w = 63; h = 13;
            break;
        case kMemoDoneButtonId:
            x = 13; y = 142; w = 39; h = 13;
            break;
        case kMemoCancelButtonId:
            x = 61; y = 142; w = 48; h = 13;
            break;
        case kModalOkButtonId:
            x = 62; y = 101; w = 36; h = 14;
            break;
        default:
            break;
    }
}

static void fakeFormResetObjects()
{
    memset(gFakeFormObjects, 0, sizeof(gFakeFormObjects));
    gFakeFormObjectCount = 0;
}

static uint16_t fakeFormAddObject(uint16_t objectId)
{
    for (uint8_t i = 0; i < gFakeFormObjectCount; ++i)
    {
        if (gFakeFormObjects[i].used && gFakeFormObjects[i].objectId == objectId)
        {
            return i;
        }
    }

    if (gFakeFormObjectCount >= kFakeFormObjectCapacity || gFakeActiveFormPtr == 0)
    {
        return 0xffffu;
    }

    const uint8_t index = gFakeFormObjectCount++;
    FakePalmFormObject& object = gFakeFormObjects[index];
    object.used = true;
    object.objectId = objectId;
    object.kind = fakeFormObjectKind(objectId);
    object.ptr = gFakeActiveFormPtr + 32u + static_cast<uint32_t>(index) * 24u;
    object.value = 0;
    object.enabled = true;
    object.usable = true;
    object.visible = true;
    strncpy(object.label, fakeControlDefaultLabel(objectId), sizeof(object.label));
    object.label[sizeof(object.label) - 1] = '\0';
    fakeFormObjectBounds(objectId, object.x, object.y, object.w, object.h);
    fakeControlSyncMemory(object);
    palmDisplayPalmUiSetObjectBounds(object.objectId, object.x, object.y, object.w, object.h);
    return index;
}

static uint32_t fakeFormInit(uint16_t formId)
{
    if (gFakeActiveFormHandle == 0)
    {
        gFakeActiveFormHandle = tinyHandleAlloc(256u);
        gFakeActiveFormPtr = tinyHandleLock(gFakeActiveFormHandle);
    }
    else if (gFakeActiveFormPtr == 0)
    {
        gFakeActiveFormPtr = tinyHandleLock(gFakeActiveFormHandle);
    }

    if (gFakeActiveFormPtr == 0)
    {
        return 0;
    }

    gFakeActiveFormId = formId;
    const PalmLoadedResourceCatalogEntry* formCatalog = findCatalogResource(0x7446524Du, formId);
    gFakeActiveFormCataloged = formCatalog != nullptr;
    gFakeActiveFormCatalogSize = formCatalog != nullptr ? formCatalog->size : 0u;
    gFakeActiveFormCatalogChecksum = formCatalog != nullptr ? formCatalog->checksum : 0u;
    gFakeFormFocusIndex = 0xffffu;
    zeroMemory(gFakeActiveFormPtr, tinyHandleSize(gFakeActiveFormHandle));
    writeWordIfPointer(gFakeActiveFormPtr + 0u, formId);
    writeWordIfPointer(gFakeActiveFormPtr + 2u, 160u);
    writeWordIfPointer(gFakeActiveFormPtr + 4u, 160u);
    fakeFormResetObjects();
    fakeFormAddObject(kMemoListId);
    fakeFormAddObject(kMemoNewButtonId);
    fakeFormAddObject(kMemoDetailsButtonId);
    fakeFormAddObject(kMemoDoneButtonId);
    fakeFormAddObject(kMemoCancelButtonId);
    gFakeFormFocusIndex = 0u;
    writeWordIfPointer(gFakeActiveFormPtr + 6u, gFakeFormObjectCount);
    Serial.printf(""  fake form init: tFRM #%u catalog=%s size=%u checksum=0x%08X objects=%u\n"",
        static_cast<unsigned>(formId),
        gFakeActiveFormCataloged ? ""yes"" : ""no"",
        static_cast<unsigned>(gFakeActiveFormCatalogSize),
        static_cast<unsigned>(gFakeActiveFormCatalogChecksum),
        static_cast<unsigned>(gFakeFormObjectCount));
    return gFakeActiveFormPtr;
}

static void fakeFormDelete(uint32_t formP)
{
    if (formP != 0 && formP == gFakeActiveFormPtr)
    {
        gFakeActiveFormId = 0;
        gFakeFormFocusIndex = 0xffffu;
        fakeFormResetObjects();
    }
}

static uint32_t fakeFormEnsureActive(uint32_t formP)
{
    if (formP != 0 && formP == gFakeActiveFormPtr)
    {
        return formP;
    }

    return fakeFormInit(gFakeActiveFormId != 0 ? gFakeActiveFormId : 1000u);
}

static uint16_t fakeFormGetObjectIndex(uint32_t formP, uint16_t objectId)
{
    fakeFormEnsureActive(formP);

    return fakeFormAddObject(objectId);
}

static FakePalmFormObject* fakeFormObjectAtIndex(uint32_t formP, uint16_t objectIndex)
{
    fakeFormEnsureActive(formP);
    if (objectIndex >= gFakeFormObjectCount)
    {
        return nullptr;
    }

    return gFakeFormObjects[objectIndex].used ? &gFakeFormObjects[objectIndex] : nullptr;
}

static uint32_t fakeFormGetObjectPtr(uint32_t formP, uint16_t objectIndex)
{
    FakePalmFormObject* object = fakeFormObjectAtIndex(formP, objectIndex);
    return object != nullptr ? object->ptr : 0u;
}

static uint16_t fakeFormGetObjectIndexFromPtr(uint32_t formP, uint32_t objectP)
{
    fakeFormEnsureActive(formP);
    for (uint8_t i = 0; i < gFakeFormObjectCount; ++i)
    {
        if (gFakeFormObjects[i].used && gFakeFormObjects[i].ptr == objectP)
        {
            return i;
        }
    }

    return 0xffffu;
}

static void fakeFormWriteRectangle(uint32_t rectP, const FakePalmFormObject& object)
{
    writeWordIfPointer(rectP + 0u, static_cast<uint16_t>(object.x));
    writeWordIfPointer(rectP + 2u, static_cast<uint16_t>(object.y));
    writeWordIfPointer(rectP + 4u, static_cast<uint16_t>(object.w));
    writeWordIfPointer(rectP + 6u, static_cast<uint16_t>(object.h));
}

static void fakeFormDraw(uint16_t selector, uint32_t formP)
{
    fakeFormEnsureActive(formP);

    showMemoPadProbe(selector, ""Memo Pad"", """");
    publishFakeMemoRows(selector, gRuntimeUiProbeTrapCount);
}

static FakePalmFormObject* fakeFormObjectForPtr(uint32_t objectP)
{
    for (uint8_t i = 0; i < gFakeFormObjectCount; ++i)
    {
        if (gFakeFormObjects[i].used && gFakeFormObjects[i].ptr == objectP)
        {
            return &gFakeFormObjects[i];
        }
    }

    return nullptr;
}

static void fakeControlDraw(uint16_t selector, uint32_t controlP)
{
    FakePalmFormObject* object = fakeFormObjectForPtr(controlP);
    if (!fakeControlIsControl(object) || !object->usable)
    {
        return;
    }

    object->visible = true;
    fakeControlSyncMemory(*object);
    ++gRuntimeUiProbeTrapCount;
    palmDisplayPalmUiDrawButton(selector, gRuntimeUiProbeTrapCount, object->objectId, object->label);
}

static void fakeControlSetLabel(uint32_t controlP, const char* label)
{
    FakePalmFormObject* object = fakeFormObjectForPtr(controlP);
    if (!fakeControlIsControl(object))
    {
        return;
    }

    strncpy(object->label, label != nullptr && label[0] != '\0' ? label : fakeControlDefaultLabel(object->objectId), sizeof(object->label));
    object->label[sizeof(object->label) - 1] = '\0';
    fakeControlSyncMemory(*object);
}

static uint32_t fakeControlGetLabelPtr(uint32_t controlP)
{
    FakePalmFormObject* object = fakeFormObjectForPtr(controlP);
    if (!fakeControlIsControl(object))
    {
        return 0;
    }

    fakeControlSyncMemory(*object);
    return object->ptr + 12u;
}

static void fakeControlHit(uint16_t selector, uint32_t controlP, const char* source)
{
    FakePalmFormObject* object = fakeFormObjectForPtr(controlP);
    if (!fakeControlIsControl(object) || !object->enabled || !object->usable)
    {
        return;
    }

    object->value = 1;
    fakeControlSyncMemory(*object);
    const int16_t x = static_cast<int16_t>(object->x + object->w / 2);
    const int16_t y = static_cast<int16_t>(object->y + object->h / 2);
    ++gRuntimeUiProbeTrapCount;
    palmDisplayPalmUiHandleTap(selector, gRuntimeUiProbeTrapCount, x, y, source != nullptr ? source : ""CtlHitControl"");
}

static bool fakeControlHandleEvent(uint16_t selector, uint32_t controlP, uint32_t eventP)
{
    FakePalmFormObject* object = fakeFormObjectForPtr(controlP);
    if (!fakeControlIsControl(object) || !looksWritablePointer(eventP))
    {
        return false;
    }

    const uint16_t eType = static_cast<uint16_t>(m68k_read_memory_16(eventP) & 0xffffu);
    const uint16_t dataId = looksWritablePointer(eventP + 8u) ? static_cast<uint16_t>(m68k_read_memory_16(eventP + 8u) & 0xffffu) : 0u;
    if (eType == 8u && dataId == object->objectId)
    {
        object->value = looksWritablePointer(eventP + 14u) && m68k_read_memory_8(eventP + 14u) != 0 ? 1 : 0;
        fakeControlHit(selector, controlP, ""CtlHandleEvent"");
        return true;
    }

    return false;
}

static bool fakeListIsMemoList(uint32_t listP)
{
    FakePalmFormObject* object = fakeFormObjectForPtr(listP);
    return object == nullptr ? listP == 0 : object->objectId == kMemoListId;
}

static int16_t fakeListClampSelection(int16_t itemNum)
{
    const uint16_t count = palmDisplayMemoRecordCount();
    if (itemNum < 0 || itemNum >= static_cast<int16_t>(count))
    {
        return -1;
    }

    return itemNum;
}

static void fakeListDraw(uint16_t selector, uint32_t listP)
{
    if (!fakeListIsMemoList(listP))
    {
        return;
    }

    ++gRuntimeUiProbeTrapCount;
    palmDisplayMemoListDraw(selector, gRuntimeUiProbeTrapCount);
}

static void fakeMenuSyncMemory()
{
    if (gFakeActiveMenuPtr == 0)
    {
        return;
    }

    writeWordIfPointer(gFakeActiveMenuPtr + 0u, gFakeActiveMenuResourceId);
    writeWordIfPointer(gFakeActiveMenuPtr + 2u, gFakeMenuVisible ? 1u : 0u);
    writeWordIfPointer(gFakeActiveMenuPtr + 4u, 0xffffu);
    writeWordIfPointer(gFakeActiveMenuPtr + 6u, 0xffffu);
    writeWordIfPointer(gFakeActiveMenuPtr + 8u, 0u);
}

static uint32_t fakeMenuInit(uint16_t resourceId)
{
    if (gFakeActiveMenuHandle == 0)
    {
        gFakeActiveMenuHandle = tinyHandleAlloc(64u);
        gFakeActiveMenuPtr = tinyHandleLock(gFakeActiveMenuHandle);
    }
    else if (gFakeActiveMenuPtr == 0)
    {
        gFakeActiveMenuPtr = tinyHandleLock(gFakeActiveMenuHandle);
    }

    if (gFakeActiveMenuPtr == 0)
    {
        return 0;
    }

    gFakeActiveMenuResourceId = resourceId;
    gFakeMenuVisible = false;
    zeroMemory(gFakeActiveMenuPtr, tinyHandleSize(gFakeActiveMenuHandle));
    fakeMenuSyncMemory();
    return gFakeActiveMenuPtr;
}

static uint32_t fakeMenuSetActive(uint32_t menuP)
{
    const uint32_t previous = gFakeActiveMenuPtr;
    if (menuP == 0)
    {
        gFakeMenuVisible = false;
        gFakeActiveMenuPtr = 0;
        return previous;
    }

    if (menuP != gFakeActiveMenuPtr)
    {
        if (gFakeActiveMenuHandle == 0)
        {
            fakeMenuInit(0);
        }
        gFakeActiveMenuPtr = menuP;
    }

    fakeMenuSyncMemory();
    return previous;
}

static void fakeMenuDispose(uint32_t menuP)
{
    if (menuP != 0 && menuP == gFakeActiveMenuPtr)
    {
        gFakeMenuVisible = false;
        gFakeActiveMenuResourceId = 0;
        if (gFakeActiveMenuHandle != 0)
        {
            tinyHandleFree(gFakeActiveMenuHandle);
        }
        gFakeActiveMenuHandle = 0;
        gFakeActiveMenuPtr = 0;
    }
}

static bool fakeMenuHandleEvent(uint32_t menuP, uint32_t eventP, uint32_t errorP)
{
    writeWordIfPointer(errorP, 0u);
    if (menuP == 0)
    {
        menuP = gFakeActiveMenuPtr;
    }
    if (menuP == 0 || !looksWritablePointer(eventP))
    {
        return false;
    }

    const uint16_t eType = static_cast<uint16_t>(m68k_read_memory_16(eventP) & 0xffffu);
    if (eType == 21u)
    {
        gFakeMenuVisible = true;
        fakeMenuSyncMemory();
    }
    return false;
}

static void fakeMenuDraw(uint32_t menuP)
{
    if (menuP == 0)
    {
        menuP = gFakeActiveMenuPtr;
    }
    if (menuP == 0)
    {
        return;
    }
    gFakeMenuVisible = true;
    fakeMenuSyncMemory();
}

static void fakeMenuErase(uint32_t menuP)
{
    if (menuP == 0)
    {
        menuP = gFakeActiveMenuPtr;
    }
    if (menuP == 0)
    {
        return;
    }
    gFakeMenuVisible = false;
    fakeMenuSyncMemory();
}

static void seedFakeMemoRecord(uint16_t index)
{
    seedFakeMemoRecordTable();
    fakeMemoRecordHandle(index);
}

static void prepareFakeMemoRecordScratch(uint16_t index, uint32_t size)
{
    seedFakeMemoRecordTable();
    fakeMemoResizeRecord(index, size);
}

static bool commitFakeMemoRecordScratch()
{
    return commitFakeMemoRecord(gFakeMemoRecordScratchIndex);
}

static void publishFakeMemoRows(uint16_t selector, uint32_t trapCount)
{
    const uint16_t recordCount = palmDisplayMemoRecordCount();
    palmDisplayPalmUiSetListCount(selector, trapCount, recordCount);
    for (uint16_t i = 0; i < recordCount; ++i)
    {
        palmDisplayPalmUiSetListRow(selector, trapCount, i, palmDisplayMemoText(i));
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
        (address >= 0x00002000u && address < kTinyHeapEnd);
}

static uint32_t alignTinyHeapSize(uint32_t size)
{
    if (size == 0)
    {
        size = 1;
    }

    return (size + 3u) & ~3u;
}

static TinyPalmHandle* tinyHandleFind(uint32_t handle)
{
    for (uint8_t i = 0; i < kTinyHandleCapacity; ++i)
    {
        if (gTinyHandles[i].used && gTinyHandles[i].handle == handle)
        {
            return &gTinyHandles[i];
        }
    }

    return nullptr;
}

static TinyPalmHandle* tinyHandleFindByPtr(uint32_t ptr)
{
    for (uint8_t i = 0; i < kTinyHandleCapacity; ++i)
    {
        TinyPalmHandle& entry = gTinyHandles[i];
        if (entry.used && ptr >= entry.ptr && ptr < entry.ptr + entry.capacity)
        {
            return &entry;
        }
    }

    return nullptr;
}

static void tinyCoalesceFreeBlocks()
{
    bool changed = true;
    while (changed)
    {
        changed = false;
        for (uint8_t i = 0; i < kTinyFreeBlockCapacity; ++i)
        {
            TinyPalmFreeBlock& a = gTinyFreeBlocks[i];
            if (!a.used)
            {
                continue;
            }
            for (uint8_t j = static_cast<uint8_t>(i + 1u); j < kTinyFreeBlockCapacity; ++j)
            {
                TinyPalmFreeBlock& b = gTinyFreeBlocks[j];
                if (!b.used)
                {
                    continue;
                }
                if (a.ptr + a.size == b.ptr)
                {
                    a.size += b.size;
                    b.used = false;
                    changed = true;
                }
                else if (b.ptr + b.size == a.ptr)
                {
                    a.ptr = b.ptr;
                    a.size += b.size;
                    b.used = false;
                    changed = true;
                }
            }
        }
    }
}

static void tinyTrimHeapTop()
{
    bool changed = true;
    while (changed)
    {
        changed = false;
        for (uint8_t i = 0; i < kTinyFreeBlockCapacity; ++i)
        {
            TinyPalmFreeBlock& block = gTinyFreeBlocks[i];
            if (block.used && block.ptr + block.size == gTinyHeapNext)
            {
                gTinyHeapNext = block.ptr;
                block.used = false;
                changed = true;
            }
        }
    }
}

static void tinyAddFreeBlock(uint32_t ptr, uint32_t size)
{
    if (ptr < kTinyHeapBase || size == 0 || ptr + size > kTinyHeapEnd || ptr + size < ptr)
    {
        return;
    }

    for (uint8_t i = 0; i < kTinyFreeBlockCapacity; ++i)
    {
        TinyPalmFreeBlock& block = gTinyFreeBlocks[i];
        if (!block.used)
        {
            block.used = true;
            block.ptr = ptr;
            block.size = size;
            tinyCoalesceFreeBlocks();
            tinyTrimHeapTop();
            return;
        }
    }
}

static uint32_t tinyClaimFreeBlock(uint32_t size)
{
    for (uint8_t i = 0; i < kTinyFreeBlockCapacity; ++i)
    {
        TinyPalmFreeBlock& block = gTinyFreeBlocks[i];
        if (!block.used || block.size < size)
        {
            continue;
        }

        const uint32_t ptr = block.ptr;
        block.ptr += size;
        block.size -= size;
        if (block.size == 0)
        {
            block.used = false;
        }
        return ptr;
    }

    return 0;
}

static bool tinyConsumeAdjacentFree(uint32_t ptr, uint32_t size)
{
    for (uint8_t i = 0; i < kTinyFreeBlockCapacity; ++i)
    {
        TinyPalmFreeBlock& block = gTinyFreeBlocks[i];
        if (!block.used || block.ptr != ptr || block.size < size)
        {
            continue;
        }

        block.ptr += size;
        block.size -= size;
        if (block.size == 0)
        {
            block.used = false;
        }
        return true;
    }

    return false;
}

static uint32_t tinyAllocBlock(uint32_t size)
{
    uint32_t ptr = tinyClaimFreeBlock(size);
    if (ptr != 0)
    {
        zeroMemory(ptr, size);
        return ptr;
    }

    if (gTinyHeapNext + size > kTinyHeapEnd)
    {
        return 0;
    }

    ptr = gTinyHeapNext;
    gTinyHeapNext += size;
    zeroMemory(ptr, size);
    return ptr;
}

static uint32_t tinyHandleAlloc(uint32_t size, bool recordBacked, uint16_t recordIndex)
{
    const uint32_t alignedSize = alignTinyHeapSize(size);
    for (uint8_t i = 0; i < kTinyHandleCapacity; ++i)
    {
        TinyPalmHandle& entry = gTinyHandles[i];
        if (entry.used)
        {
            continue;
        }

        const uint32_t ptr = tinyAllocBlock(alignedSize);
        if (ptr == 0)
        {
            return 0;
        }

        entry.used = true;
        entry.handle = kTinyHandleBase + (static_cast<uint32_t>(gTinyHandleGeneration) << 8) + i;
        entry.ptr = ptr;
        entry.size = alignedSize;
        entry.capacity = alignedSize;
        entry.lockCount = 0;
        entry.recordBacked = recordBacked;
        entry.recordIndex = recordIndex;
        return entry.handle;
    }

    return 0;
}

static uint32_t tinyHandleLock(uint32_t handle)
{
    TinyPalmHandle* entry = tinyHandleFind(handle);
    if (entry == nullptr)
    {
        return 0;
    }

    ++entry->lockCount;
    return entry->ptr;
}

static bool tinyHandleUnlock(uint32_t handle)
{
    TinyPalmHandle* entry = tinyHandleFind(handle);
    if (entry == nullptr)
    {
        return false;
    }

    if (entry->lockCount > 0)
    {
        --entry->lockCount;
    }
    return true;
}

static uint32_t tinyHandleSize(uint32_t handle)
{
    TinyPalmHandle* entry = tinyHandleFind(handle);
    return entry != nullptr ? entry->size : 0u;
}

static uint32_t tinyHandleRecoverFromPtr(uint32_t ptr)
{
    TinyPalmHandle* entry = tinyHandleFindByPtr(ptr);
    return entry != nullptr ? entry->handle : 0u;
}

static uint32_t tinyPtrSize(uint32_t ptr)
{
    TinyPalmHandle* entry = tinyHandleFindByPtr(ptr);
    if (entry == nullptr)
    {
        return 0;
    }

    return entry->ptr + entry->size > ptr ? entry->ptr + entry->size - ptr : 0u;
}

static uint16_t tinyHandleLockCount(uint32_t handle)
{
    TinyPalmHandle* entry = tinyHandleFind(handle);
    return entry != nullptr ? entry->lockCount : 0u;
}

static uint32_t fixedHandleForPtr(uint32_t ptr)
{
    if (ptr >= kFakeMemoRecordPtr && ptr < kFakeMemoRecordPtr + gFakeMemoRecordScratchSize)
    {
        return kFakeMemoRecordHandle;
    }
    if (ptr >= kFakeResourcePtr && ptr < kFakeResourcePtr + 256u)
    {
        return kFakeResourceHandle;
    }
    if (ptr >= kFakeAllocationPtr && ptr < kFakeAllocationPtr + 256u)
    {
        return kFakeAllocationHandle;
    }
    if (ptr >= kFakeFieldTextPtr && ptr < kFakeFieldTextPtr + 64u)
    {
        return kFakeFieldTextHandle;
    }
    if (ptr >= kFakeListTextPtr && ptr < kFakeListTextPtr + 64u)
    {
        return kFakeAllocationHandle;
    }

    return 0;
}

static uint32_t fixedPtrSize(uint32_t ptr)
{
    if (ptr >= kFakeMemoRecordPtr && ptr < kFakeMemoRecordPtr + gFakeMemoRecordScratchSize)
    {
        return kFakeMemoRecordPtr + gFakeMemoRecordScratchSize - ptr;
    }
    if (ptr >= kFakeResourcePtr && ptr < kFakeResourcePtr + 256u)
    {
        const uint32_t resourceSize = gLockedResource != nullptr ? gLockedResource->size : (gUseSyntheticStringResource ? 16u : 256u);
        const uint32_t cappedSize = resourceSize > 256u ? 256u : resourceSize;
        return cappedSize > ptr - kFakeResourcePtr ? cappedSize - (ptr - kFakeResourcePtr) : 0u;
    }
    if (ptr >= kFakeAllocationPtr && ptr < kFakeAllocationPtr + 256u)
    {
        return kFakeAllocationPtr + 256u - ptr;
    }
    if (ptr >= kFakeFieldTextPtr && ptr < kFakeFieldTextPtr + 64u)
    {
        return kFakeFieldTextPtr + 64u - ptr;
    }
    if (ptr >= kFakeListTextPtr && ptr < kFakeListTextPtr + 64u)
    {
        return kFakeListTextPtr + 64u - ptr;
    }

    return 0;
}

static uint32_t palmHandleRecoverFromPtr(uint32_t ptr)
{
    const uint32_t handle = tinyHandleRecoverFromPtr(ptr);
    return handle != 0 ? handle : fixedHandleForPtr(ptr);
}

static uint32_t palmPtrSize(uint32_t ptr)
{
    const uint32_t size = tinyPtrSize(ptr);
    return size != 0 ? size : fixedPtrSize(ptr);
}

static bool tinyHandleResize(uint32_t handle, uint32_t newSize)
{
    TinyPalmHandle* entry = tinyHandleFind(handle);
    if (entry == nullptr)
    {
        return false;
    }

    const uint32_t alignedSize = alignTinyHeapSize(newSize);
    if (alignedSize <= entry->capacity)
    {
        if (alignedSize < entry->capacity)
        {
            tinyAddFreeBlock(entry->ptr + alignedSize, entry->capacity - alignedSize);
            entry->capacity = alignedSize;
        }
        entry->size = alignedSize;
        return true;
    }

    const uint32_t growBy = alignedSize - entry->capacity;
    if (entry->ptr + entry->capacity == gTinyHeapNext && gTinyHeapNext + growBy <= kTinyHeapEnd)
    {
        zeroMemory(entry->ptr + entry->capacity, growBy);
        gTinyHeapNext += growBy;
        entry->size = alignedSize;
        entry->capacity = alignedSize;
        return true;
    }

    if (tinyConsumeAdjacentFree(entry->ptr + entry->capacity, growBy))
    {
        zeroMemory(entry->ptr + entry->capacity, growBy);
        entry->size = alignedSize;
        entry->capacity = alignedSize;
        return true;
    }

    const uint32_t oldPtr = entry->ptr;
    const uint32_t oldSize = entry->size;
    const uint32_t oldCapacity = entry->capacity;
    const uint32_t newPtr = tinyAllocBlock(alignedSize);
    if (newPtr == 0)
    {
        return false;
    }

    entry->ptr = newPtr;
    entry->size = alignedSize;
    entry->capacity = alignedSize;
    copyMemoryBytes(entry->ptr, oldPtr, oldSize < alignedSize ? oldSize : alignedSize);
    tinyAddFreeBlock(oldPtr, oldCapacity);
    return true;
}

static bool tinyHandleFree(uint32_t handle)
{
    TinyPalmHandle* entry = tinyHandleFind(handle);
    if (entry == nullptr)
    {
        return false;
    }

    tinyAddFreeBlock(entry->ptr, entry->capacity);
    entry->used = false;
    return true;
}

static bool tinyPtrFree(uint32_t ptr)
{
    TinyPalmHandle* entry = tinyHandleFindByPtr(ptr);
    if (entry == nullptr || entry->ptr != ptr)
    {
        return false;
    }

    return tinyHandleFree(entry->handle);
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

static uint16_t readWordIfPointer(uint32_t address, uint16_t fallback = 0)
{
    if (!looksWritablePointer(address))
    {
        return fallback;
    }

    return static_cast<uint16_t>(m68k_read_memory_16(address) & 0xffffu);
}

static uint32_t clampMemoRecordSize(uint32_t size)
{
    if (size == 0)
    {
        return 1u;
    }

    return size > 64u ? 64u : size;
}

static void copyMemoryBytes(uint32_t destination, uint32_t source, uint32_t byteCount)
{
    for (uint32_t i = 0; i < byteCount; ++i)
    {
        if (!looksWritablePointer(destination + i) || !looksWritablePointer(source + i))
        {
            break;
        }

        m68k_write_memory_8(destination + i, m68k_read_memory_8(source + i) & 0xffu);
    }
}

static void setMemoryBytes(uint32_t destination, uint32_t byteCount, uint8_t value)
{
    for (uint32_t i = 0; i < byteCount; ++i)
    {
        if (!looksWritablePointer(destination + i))
        {
            break;
        }

        m68k_write_memory_8(destination + i, value);
    }
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

static void writeByteIfPointer(uint32_t address, uint8_t value)
{
    if (looksWritablePointer(address))
    {
        m68k_write_memory_8(address, value);
    }
}

static void writePalmRectangle(uint32_t rectP, int16_t x, int16_t y, int16_t w, int16_t h)
{
    writeWordIfPointer(rectP + 0u, static_cast<uint16_t>(x));
    writeWordIfPointer(rectP + 2u, static_cast<uint16_t>(y));
    writeWordIfPointer(rectP + 4u, static_cast<uint16_t>(w));
    writeWordIfPointer(rectP + 6u, static_cast<uint16_t>(h));
}

static bool readPalmRectangle(uint32_t rectP, int16_t& x, int16_t& y, int16_t& w, int16_t& h)
{
    if (!looksWritablePointer(rectP))
    {
        x = 0;
        y = 0;
        w = 0;
        h = 0;
        return false;
    }

    x = static_cast<int16_t>(m68k_read_memory_16(rectP + 0u) & 0xffffu);
    y = static_cast<int16_t>(m68k_read_memory_16(rectP + 2u) & 0xffffu);
    w = static_cast<int16_t>(m68k_read_memory_16(rectP + 4u) & 0xffffu);
    h = static_cast<int16_t>(m68k_read_memory_16(rectP + 6u) & 0xffffu);
    return true;
}

static void writePalmEvent(uint32_t eventP, const PalmQueuedEvent& event)
{
    zeroMemory(eventP, 32u);
    writeWordIfPointer(eventP + 0u, event.eType);
    writeByteIfPointer(eventP + 2u, event.penDown ? 1u : 0u);
    writeByteIfPointer(eventP + 3u, event.tapCount);
    writeWordIfPointer(eventP + 4u, static_cast<uint16_t>(event.screenX));
    writeWordIfPointer(eventP + 6u, static_cast<uint16_t>(event.screenY));
    if (event.eType == 8u)
    {
        writeWordIfPointer(eventP + 8u, event.controlID);
        writeLongIfPointer(eventP + 10u, 0u);
        writeByteIfPointer(eventP + 14u, event.on ? 1u : 0u);
        writeByteIfPointer(eventP + 15u, 0u);
        writeWordIfPointer(eventP + 16u, 0u);
    }
    else if (event.eType == 13u)
    {
        writeWordIfPointer(eventP + 8u, event.listID);
        writeLongIfPointer(eventP + 10u, 0u);
        writeWordIfPointer(eventP + 14u, static_cast<uint16_t>(event.selection));
    }
}

static uint16_t fakeFieldTextLength()
{
    uint16_t length = 0;
    while (length < gFakeFieldMaxChars)
    {
        const uint8_t value = static_cast<uint8_t>(m68k_read_memory_8(kFakeFieldTextPtr + length) & 0xffu);
        if (value == 0)
        {
            break;
        }
        ++length;
    }
    return length;
}

static void fakeFieldMirrorToDisplay(uint16_t selector)
{
    char text[64];
    text[0] = '\0';
    readCString(kFakeFieldTextPtr, text, sizeof(text));
    palmDisplaySetEditText(text);
    if (text[0] != '\0')
    {
        captureMemoProbeText(text);
    }
    Serial.printf(""  field mirror selector=0x%04X text='%s' length=%u dirty=%s\n"",
        static_cast<unsigned>(selector),
        text,
        static_cast<unsigned>(fakeFieldTextLength()),
        gFakeFieldDirty ? ""yes"" : ""no"");
}

static void fakeFieldSetTextFromPointer(uint32_t sourceP, uint32_t maxBytes)
{
    if (sourceP == kFakeFieldTextPtr)
    {
        gFakeFieldInsPt = fakeFieldTextLength();
        gFakeFieldDirty = false;
        return;
    }

    zeroMemory(kFakeFieldTextPtr, 64u);
    if (sourceP != 0 && looksWritablePointer(sourceP))
    {
        const uint32_t limit = maxBytes == 0 || maxBytes > 63u ? 63u : maxBytes;
        copyMemoryBytes(kFakeFieldTextPtr, sourceP, limit);
        writeByteIfPointer(kFakeFieldTextPtr + limit, 0);
    }
    gFakeFieldInsPt = fakeFieldTextLength();
    gFakeFieldDirty = false;
}

static void fakeFieldInsertText(uint32_t sourceP, uint16_t insertLength)
{
    if (insertLength == 0 || sourceP == 0 || !looksWritablePointer(sourceP))
    {
        return;
    }

    const uint16_t currentLength = fakeFieldTextLength();
    if (gFakeFieldInsPt > currentLength)
    {
        gFakeFieldInsPt = currentLength;
    }

    const uint16_t available = gFakeFieldMaxChars > currentLength ? static_cast<uint16_t>(gFakeFieldMaxChars - currentLength) : 0u;
    const uint16_t count = insertLength > available ? available : insertLength;
    for (int i = static_cast<int>(currentLength); i >= static_cast<int>(gFakeFieldInsPt); --i)
    {
        const uint8_t value = static_cast<uint8_t>(m68k_read_memory_8(kFakeFieldTextPtr + i) & 0xffu);
        writeByteIfPointer(kFakeFieldTextPtr + i + count, value);
    }
    for (uint16_t i = 0; i < count; ++i)
    {
        writeByteIfPointer(kFakeFieldTextPtr + gFakeFieldInsPt + i, static_cast<uint8_t>(m68k_read_memory_8(sourceP + i) & 0xffu));
    }
    gFakeFieldInsPt = static_cast<uint16_t>(gFakeFieldInsPt + count);
    gFakeFieldDirty = true;
}

static void fakeFieldDeleteRange(uint16_t start, uint16_t end)
{
    const uint16_t currentLength = fakeFieldTextLength();
    if (start > currentLength)
    {
        start = currentLength;
    }
    if (end > currentLength)
    {
        end = currentLength;
    }
    if (end <= start)
    {
        return;
    }

    const uint16_t removed = static_cast<uint16_t>(end - start);
    for (uint16_t i = start; i <= currentLength; ++i)
    {
        const uint8_t value = static_cast<uint8_t>(m68k_read_memory_8(kFakeFieldTextPtr + i + removed) & 0xffu);
        writeByteIfPointer(kFakeFieldTextPtr + i, value);
        if (value == 0)
        {
            break;
        }
    }
    gFakeFieldInsPt = start;
    gFakeFieldDirty = true;
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
        case 0xA014: return ""MemPtrRecoverHandle"";
        case 0xA016: return ""MemPtrSize"";
        case 0xA01C: return ""MemPtrResize"";
        case 0xA01E: return ""MemHandleNew"";
        case 0xA01F: return ""MemHandleLockCount"";
        case 0xA02D: return ""MemHandleSize"";
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
        case 0xA051: return ""DmSetRecordInfo"";
        case 0xA052: return ""DmAttachRecord"";
        case 0xA053: return ""DmDetachRecord"";
        case 0xA054: return ""DmMoveRecord"";
        case 0xA055: return ""DmNewRecord"";
        case 0xA056: return ""DmRemoveRecord"";
        case 0xA057: return ""DmDeleteRecord"";
        case 0xA058: return ""DmArchiveRecord"";
        case 0xA059: return ""DmNewHandle"";
        case 0xA05B: return ""DmQueryRecord"";
        case 0xA05C: return ""DmGetRecord"";
        case 0xA05D: return ""DmResizeRecord"";
        case 0xA05E: return ""DmReleaseRecord"";
        case 0xA05F: return ""DmGetResource"";
        case 0xA060: return ""DmGet1Resource"";
        case 0xA020: return ""MemHandleToLocalID"";
        case 0xA021: return ""MemHandleLock"";
        case 0xA022: return ""MemHandleUnlock"";
        case 0xA02B: return ""MemHandleFree"";
        case 0xA02C: return ""MemHandleFlags"";
        case 0xA033: return ""MemHandleResize"";
        case 0xA035: return ""MemPtrUnlock"";
        case 0xA036: return ""SelectorA036"";
        case 0xA071: return ""DmNumRecordsInCategory"";
        case 0xA075: return ""DmOpenDatabaseByTypeCreator"";
        case 0xA076: return ""DmWrite"";
        case 0xA077: return ""DmStrCopy"";
        case 0xA079: return ""DmWriteCheck"";
        case 0xA07E: return ""DmSet"";
        case 0xA061: return ""DmReleaseResource"";
        case 0xA084: return ""ErrDisplayFileLineMsg"";
        case 0xA08F: return ""SysAppStartup"";
        case 0xA090: return ""SysAppExit"";
        case 0xA0A9: return ""SysHandleEvent"";
        case 0xA104: return ""CategoryGetName"";
        case 0xA10D: return ""CtlDrawControl"";
        case 0xA10E: return ""CtlEraseControl"";
        case 0xA10F: return ""CtlHideControl"";
        case 0xA110: return ""CtlShowControl"";
        case 0xA111: return ""CtlGetValue"";
        case 0xA112: return ""CtlSetValue"";
        case 0xA113: return ""CtlGetLabel"";
        case 0xA114: return ""CtlSetLabel"";
        case 0xA115: return ""CtlHandleEvent"";
        case 0xA116: return ""CtlHitControl"";
        case 0xA117: return ""CtlSetEnabled"";
        case 0xA118: return ""CtlSetUsable"";
        case 0xA119: return ""CtlEnabled"";
        case 0xA11D: return ""EvtGetEvent"";
        case 0xA135: return ""FldDrawField"";
        case 0xA137: return ""FldFreeMemory"";
        case 0xA139: return ""FldGetTextPtr"";
        case 0xA13A: return ""FldGetSelection"";
        case 0xA13B: return ""FldHandleEvent"";
        case 0xA13D: return ""FldRecalculateField"";
        case 0xA13F: return ""FldSetText"";
        case 0xA142: return ""FldSetSelection"";
        case 0xA143: return ""FldGrabFocus"";
        case 0xA144: return ""FldReleaseFocus"";
        case 0xA145: return ""FldGetInsPtPosition"";
        case 0xA146: return ""FldSetInsPtPosition"";
        case 0xA147: return ""FldSetScrollPosition"";
        case 0xA148: return ""FldGetScrollPosition"";
        case 0xA149: return ""FldGetTextHeight"";
        case 0xA14A: return ""FldGetTextAllocatedSize"";
        case 0xA14B: return ""FldGetTextLength"";
        case 0xA14C: return ""FldScrollField"";
        case 0xA14D: return ""FldScrollable"";
        case 0xA14E: return ""FldGetVisibleLines"";
        case 0xA153: return ""FldGetTextHandle"";
        case 0xA155: return ""FldDirty"";
        case 0xA157: return ""FldSetTextAllocatedSize"";
        case 0xA158: return ""FldSetTextHandle"";
        case 0xA159: return ""FldSetTextPtr"";
        case 0xA15A: return ""FldGetMaxChars"";
        case 0xA15B: return ""FldSetMaxChars"";
        case 0xA15C: return ""FldSetUsable"";
        case 0xA15D: return ""FldInsert"";
        case 0xA15E: return ""FldDelete"";
        case 0xA160: return ""FldSetDirty"";
        case 0xA162: return ""FldMakeFullyVisible"";
        case 0xA16F: return ""FrmInitForm"";
        case 0xA170: return ""FrmDeleteForm"";
        case 0xA171: return ""FrmDrawForm"";
        case 0xA172: return ""FrmEraseForm"";
        case 0xA173: return ""FrmGetActiveForm"";
        case 0xA174: return ""FrmSetActiveForm"";
        case 0xA175: return ""FrmGetActiveFormID"";
        case 0xA176: return ""FrmGetUserModifiedState"";
        case 0xA177: return ""FrmSetNotUserModified"";
        case 0xA178: return ""FrmGetFocus"";
        case 0xA179: return ""FrmSetFocus"";
        case 0xA17B: return ""FrmGetFormBounds"";
        case 0xA17D: return ""FrmGetFormId"";
        case 0xA17E: return ""FrmGetFormPtr"";
        case 0xA17F: return ""FrmGetNumberOfObjects"";
        case 0xA180: return ""FrmGetObjectIndex"";
        case 0xA181: return ""FrmGetObjectId"";
        case 0xA182: return ""FrmGetObjectType"";
        case 0xA183: return ""FrmGetObjectPtr"";
        case 0xA184: return ""FrmHideObject"";
        case 0xA185: return ""FrmShowObject"";
        case 0xA186: return ""FrmGetObjectPosition"";
        case 0xA187: return ""FrmSetObjectPosition"";
        case 0xA188: return ""FrmGetControlValue"";
        case 0xA189: return ""FrmSetControlValue"";
        case 0xA192: return ""FrmAlert"";
        case 0xA193: return ""FrmDoDialog"";
        case 0xA194: return ""FrmCustomAlert"";
        case 0xA199: return ""FrmGetObjectBounds"";
        case 0xA19B: return ""FrmGotoForm"";
        case 0xA19C: return ""FrmPopupForm"";
        case 0xA19E: return ""FrmReturnToForm"";
        case 0xA1A0: return ""FrmDispatchEvent"";
        case 0xA1B0: return ""LstSetDrawFunction"";
        case 0xA1B1: return ""LstDrawList"";
        case 0xA1B2: return ""LstEraseList"";
        case 0xA1B3: return ""LstGetSelection"";
        case 0xA1B4: return ""LstGetSelectionText"";
        case 0xA1B5: return ""LstHandleEvent"";
        case 0xA1B6: return ""LstSetHeight"";
        case 0xA1B7: return ""LstSetSelection"";
        case 0xA1B8: return ""LstSetListChoices"";
        case 0xA1B9: return ""LstMakeItemVisible"";
        case 0xA1BA: return ""LstGetNumberOfItems"";
        case 0xA1BB: return ""LstPopupList"";
        case 0xA1BC: return ""LstSetPosition"";
        case 0xA1BD: return ""MenuInit"";
        case 0xA1BE: return ""MenuDispose"";
        case 0xA1BF: return ""MenuHandleEvent"";
        case 0xA1C0: return ""MenuDrawMenu"";
        case 0xA1C1: return ""MenuEraseStatus"";
        case 0xA1C2: return ""MenuGetActiveMenu"";
        case 0xA1C3: return ""MenuSetActiveMenu"";
        case 0xA1FC: return ""WinSetActiveWindow"";
        case 0xA1FD: return ""WinSetDrawWindow"";
        case 0xA1FE: return ""WinGetDrawWindow"";
        case 0xA1FF: return ""WinGetActiveWindow"";
        case 0xA200: return ""WinGetDisplayWindow"";
        case 0xA201: return ""WinGetFirstWindow"";
        case 0xA202: return ""WinEnableWindow"";
        case 0xA203: return ""WinDisableWindow"";
        case 0xA204: return ""WinGetWindowFrameRect"";
        case 0xA205: return ""WinDrawWindowFrame"";
        case 0xA206: return ""WinEraseWindow"";
        case 0xA207: return ""WinSaveBits"";
        case 0xA208: return ""WinRestoreBits"";
        case 0xA20B: return ""WinGetDisplayExtent"";
        case 0xA20C: return ""WinGetWindowExtent"";
        case 0xA20D: return ""WinDisplayToWindowPt"";
        case 0xA20E: return ""WinWindowToDisplayPt"";
        case 0xA20F: return ""WinGetClip"";
        case 0xA210: return ""WinSetClip"";
        case 0xA211: return ""WinResetClip"";
        case 0xA212: return ""WinClipRectangle"";
        case 0xA213: return ""WinDrawLine"";
        case 0xA214: return ""WinDrawGrayLine"";
        case 0xA215: return ""WinEraseLine"";
        case 0xA216: return ""WinInvertLine"";
        case 0xA217: return ""WinFillLine"";
        case 0xA218: return ""WinDrawRectangle"";
        case 0xA219: return ""WinEraseRectangle"";
        case 0xA21A: return ""WinInvertRectangle"";
        case 0xA21B: return ""WinDrawRectangleFrame"";
        case 0xA21C: return ""WinDrawGrayRectangleFrame"";
        case 0xA21D: return ""WinEraseRectangleFrame"";
        case 0xA21E: return ""WinInvertRectangleFrame"";
        case 0xA21F: return ""WinGetFramesRectangle"";
        case 0xA226: return ""WinDrawBitmap"";
        case 0xA220: return ""WinDrawChars"";
        case 0xA221: return ""WinEraseChars"";
        case 0xA222: return ""WinInvertChars"";
        case 0xA227: return ""WinModal"";
        case 0xA228: return ""WinGetDrawWindowBounds"";
        case 0xA229: return ""WinFillRectangle"";
        case 0xA22A: return ""WinDrawInvertedChars"";
        case 0xA2CC: return ""EvtEventAvail"";
        case 0xA2B5: return ""LstSetTopItem"";
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

        case 0xA012:
        {
            const uint32_t ptr = frame.stackLongs[0];
            const bool freed = tinyPtrFree(ptr);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s ptr=0x%08X freed=%s\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(ptr),
                freed ? ""yes"" : ""fixed-or-unknown"");
            return handledTrap(frame, 0, 0);
        }

        case 0xA013:
        {
            const uint32_t size = frame.stackLongs[0];
            const uint32_t handle = tinyHandleAlloc(size);
            const uint32_t ptr = tinyHandleLock(handle);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s size=%u -> ptr=0x%08X handle=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(size),
                static_cast<unsigned>(ptr),
                static_cast<unsigned>(handle));
            return handledTrap(frame, ptr, ptr);
        }

        case 0xA014:
        {
            const uint32_t ptr = frame.stackLongs[0];
            const uint32_t handle = palmHandleRecoverFromPtr(ptr);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s ptr=0x%08X -> handle=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(ptr),
                static_cast<unsigned>(handle));
            return handledTrap(frame, handle, handle);
        }

        case 0xA016:
        {
            const uint32_t ptr = frame.stackLongs[0];
            const uint32_t size = palmPtrSize(ptr);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s ptr=0x%08X -> size=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(ptr),
                static_cast<unsigned>(size));
            return handledTrap(frame, size, 0);
        }

        case 0xA01E:
        {
            const uint32_t size = frame.stackLongs[0];
            const uint32_t handle = tinyHandleAlloc(size);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s size=%u -> handle=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(size),
                static_cast<unsigned>(handle));
            return handledTrap(frame, handle, handle);
        }

        case 0xA01F:
        {
            const uint32_t handle = frame.stackLongs[0];
            const uint16_t lockCount = tinyHandleLockCount(handle);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s handle=0x%08X -> lockCount=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(handle),
                static_cast<unsigned>(lockCount));
            return handledTrap(frame, lockCount, 0);
        }

        case 0xA059:
        {
            const uint32_t size = frame.stackLongs[1];
            const uint32_t handle = tinyHandleAlloc(size);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s dbRef=0x%08X sizeOrArg=0x%08X arg2=0x%08X -> handle=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(frame.stackLongs[1]),
                static_cast<unsigned>(frame.stackLongs[2]),
                static_cast<unsigned>(handle));
            return handledTrap(frame, handle, handle);
        }

        case 0xA05F:
        case 0xA060:
        {
            const uint32_t resourceType = frame.stackLongs[0];
            const uint16_t resourceId = stackWord(frame, 4);
            char resourceTypeName[5];
            resourceTypeToString(resourceType, resourceTypeName);
            gLockedResource = findLoadedResource(resourceType, resourceId);
            const PalmLoadedResourceCatalogEntry* catalogResource = findCatalogResource(resourceType, resourceId);
            gUseSyntheticStringResource = gLockedResource == nullptr && resourceType == 0x74535452u;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s resource=%s #%u found=%s catalog=%s synthetic=%s -> handle=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                resourceTypeName,
                static_cast<unsigned>(resourceId),
                gLockedResource != nullptr ? ""yes"" : ""no"",
                catalogResource != nullptr ? ""yes"" : ""no"",
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
            uint32_t ptr = tinyHandleLock(handle);
            if (ptr == 0 && handle == kFakeMemoRecordHandle)
            {
                ptr = kFakeMemoRecordPtr;
            }
            if (handle == kFakeResourceHandle)
            {
                ptr = kFakeResourcePtr;
            }
            else if (handle == kFakeAllocationHandle)
            {
                ptr = kFakeAllocationPtr;
            }
            else if (handle == kFakeFieldTextHandle)
            {
                ptr = kFakeFieldTextPtr;
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

        case 0xA02C:
        {
            const uint32_t handle = frame.stackLongs[0];
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s handle=0x%08X -> flags=0\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(handle));
            return handledTrap(frame, 0, 0);
        }

        case 0xA02D:
        {
            const uint32_t handle = frame.stackLongs[0];
            uint32_t size = tinyHandleSize(handle);
            if (size == 0)
            {
                if (handle == kFakeMemoRecordHandle)
                {
                    size = fixedPtrSize(kFakeMemoRecordPtr);
                }
                else if (handle == kFakeResourceHandle)
                {
                    size = fixedPtrSize(kFakeResourcePtr);
                }
                else if (handle == kFakeAllocationHandle)
                {
                    size = fixedPtrSize(kFakeAllocationPtr);
                }
                else if (handle == kFakeFieldTextHandle)
                {
                    size = fixedPtrSize(kFakeFieldTextPtr);
                }
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s handle=0x%08X -> size=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(handle),
                static_cast<unsigned>(size));
            return handledTrap(frame, size, 0);
        }

        case 0xA033:
        {
            const uint32_t handle = frame.stackLongs[0];
            const uint32_t newSize = frame.stackLongs[1];
            const bool resized = tinyHandleResize(handle, newSize);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s handle=0x%08X newSize=%u resized=%s\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(handle),
                static_cast<unsigned>(newSize),
                resized ? ""yes"" : ""no"");
            return handledTrap(frame, resized ? 0u : 1u, 0);
        }

        case 0xA01C:
        {
            const uint32_t ptr = frame.stackLongs[0];
            const uint32_t newSize = frame.stackLongs[1];
            const uint32_t handle = tinyHandleRecoverFromPtr(ptr);
            const bool resized = handle != 0 && tinyHandleResize(handle, newSize);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s ptr=0x%08X handle=0x%08X newSize=%u resized=%s\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(ptr),
                static_cast<unsigned>(handle),
                static_cast<unsigned>(newSize),
                resized ? ""yes"" : ""no"");
            return handledTrap(frame, resized ? 0u : 1u, 0);
        }

        case 0xA04F:
        {
            const uint16_t recordCount = palmDisplayMemoRecordCount();
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s dbRef=0x%08X -> records=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(recordCount));
            return handledTrap(frame, recordCount, 0);
        }

        case 0xA050:
        {
            const uint32_t dbRef = frame.stackLongs[0];
            const uint16_t index = stackWord(frame, 4);
            const uint32_t attrP = stackLong(frame, 6);
            const uint32_t uniqueIDP = stackLong(frame, 10);
            const uint32_t chunkIDP = stackLong(frame, 14);
            const uint16_t recordCount = palmDisplayMemoRecordCount();
            const uint16_t safeIndex = index < recordCount ? index : 0;
            const uint32_t handle = fakeMemoRecordHandle(safeIndex);
            const uint32_t uniqueId = safeIndex < gFakeMemoRecordCount ? gFakeMemoRecordUniqueIds[safeIndex] : static_cast<uint32_t>(safeIndex) + 1u;
            writeWordIfPointer(attrP, 0);
            writeLongIfPointer(uniqueIDP, uniqueId);
            writeLongIfPointer(chunkIDP, handle);
            captureMemoProbeText(palmDisplayMemoText(safeIndex));
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
                palmDisplayMemoText(safeIndex));
            logScratchBytes(""after A050"");
            return handledTrap(frame, 0, 0);
        }

        case 0xA051:
        {
            const uint32_t dbRef = frame.stackLongs[0];
            const uint16_t index = stackWord(frame, 4);
            const uint32_t attrP = stackLong(frame, 6);
            const uint32_t uniqueIDP = stackLong(frame, 10);
            const uint16_t attr = readWordIfPointer(attrP, 0);
            const uint32_t uniqueId = looksWritablePointer(uniqueIDP) ? m68k_read_memory_32(uniqueIDP) : 0u;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s dbRef=0x%08X index=%u attrP=0x%08X attr=0x%04X uniqueP=0x%08X unique=0x%08X -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(dbRef),
                static_cast<unsigned>(index),
                static_cast<unsigned>(attrP),
                static_cast<unsigned>(attr),
                static_cast<unsigned>(uniqueIDP),
                static_cast<unsigned>(uniqueId));
            return handledTrap(frame, 0, 0);
        }

        case 0xA055:
        {
            const uint32_t dbRef = frame.stackLongs[0];
            const uint32_t atP = stackLong(frame, 4);
            const uint32_t size = stackLong(frame, 8);
            uint16_t index = palmDisplayMemoRecordCount();
            if (looksWritablePointer(atP))
            {
                const uint16_t requested = readWordIfPointer(atP, index);
                if (requested != 0xffffu && requested <= palmDisplayMemoRecordCount())
                {
                    index = requested;
                }
            }

            uint32_t handle = 0;
            const bool inserted = fakeMemoInsertRecord(index, """", size, &handle);
            if (inserted)
            {
                writeWordIfPointer(atP, index);
                gFakeMemoRecordScratchIndex = index;
                gFakeMemoRecordScratchSize = size;
            }

            Serial.printf(""  trap dispatch: selector=0x%04X name=%s dbRef=0x%08X atP=0x%08X index=%u size=%u inserted=%s -> handle=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(dbRef),
                static_cast<unsigned>(atP),
                static_cast<unsigned>(index),
                static_cast<unsigned>(size),
                inserted ? ""yes"" : ""no"",
                static_cast<unsigned>(handle));
            return handledTrap(frame, handle, handle);
        }

        case 0xA056:
        case 0xA057:
        case 0xA058:
        {
            const uint32_t dbRef = frame.stackLongs[0];
            const uint16_t index = stackWord(frame, 4);
            const bool removed = fakeMemoRemoveRecord(index);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s dbRef=0x%08X index=%u removed=%s -> %u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(dbRef),
                static_cast<unsigned>(index),
                removed ? ""yes"" : ""no"",
                removed ? 0u : 1u);
            return handledTrap(frame, removed ? 0u : 1u, 0);
        }

        case 0xA05B:
        case 0xA05C:
        {
            const uint32_t dbRef = frame.stackLongs[0];
            const uint16_t index = stackWord(frame, 4);
            const uint16_t recordCount = palmDisplayMemoRecordCount();
            const uint16_t safeIndex = index < recordCount ? index : 0;
            const uint32_t handle = fakeMemoRecordHandle(safeIndex);
            captureMemoProbeText(palmDisplayMemoText(safeIndex));
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s dbRef=0x%08X index=%u -> handle=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(dbRef),
                static_cast<unsigned>(safeIndex),
                static_cast<unsigned>(handle));
            return handledTrap(frame, handle, handle);
        }

        case 0xA05D:
        {
            const uint32_t dbRef = frame.stackLongs[0];
            const uint16_t index = stackWord(frame, 4);
            const uint32_t newSize = stackLong(frame, 6);
            const uint16_t recordCount = palmDisplayMemoRecordCount();
            const bool valid = index < recordCount;
            uint32_t handle = 0;
            if (valid)
            {
                handle = fakeMemoResizeRecord(index, newSize);
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s dbRef=0x%08X index=%u newSize=%u valid=%s -> handle=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(dbRef),
                static_cast<unsigned>(index),
                static_cast<unsigned>(newSize),
                valid ? ""yes"" : ""no"",
                static_cast<unsigned>(handle));
            return handledTrap(frame, handle, handle);
        }

        case 0xA05E:
        {
            const uint32_t dbRef = frame.stackLongs[0];
            const uint16_t index = stackWord(frame, 4);
            const uint16_t dirty = stackWord(frame, 6);
            const bool tableDirty = index < gFakeMemoRecordCount && gFakeMemoRecordDirty[index];
            const bool committed = dirty != 0 || tableDirty || gFakeMemoRecordScratchDirty ? commitFakeMemoRecord(index) : false;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s dbRef=0x%08X index=%u dirty=%u scratchDirty=%s committed=%s\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(dbRef),
                static_cast<unsigned>(index),
                static_cast<unsigned>(dirty),
                gFakeMemoRecordScratchDirty ? ""yes"" : ""no"",
                committed ? ""yes"" : ""no"");
            return handledTrap(frame, 0, 0);
        }

        case 0xA022:
        case 0xA02B:
        {
            const uint32_t handle = frame.stackLongs[0];
            const bool dynamicHandle = frame.selector == 0xA022 ? tinyHandleUnlock(handle) : tinyHandleFree(handle);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s handle=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(handle));
            if (dynamicHandle)
            {
                Serial.println(""    tiny handle table updated"");
            }
            return handledTrap(frame, 0, 0);
        }

        case 0xA061:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s handle=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]));
            return handledTrap(frame, 0, 0);

        case 0xA035:
        {
            const uint32_t ptr = frame.stackLongs[0];
            const uint32_t handle = tinyHandleRecoverFromPtr(ptr);
            const bool unlocked = handle != 0 && tinyHandleUnlock(handle);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s ptr=0x%08X handle=0x%08X unlocked=%s\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(ptr),
                static_cast<unsigned>(handle),
                unlocked ? ""yes"" : ""fixed-or-unknown"");
            return handledTrap(frame, 0, 0);
        }

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

        case 0xA079:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s recordP=0x%08X offset=%u bytes=%u -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(frame.stackLongs[1]),
                static_cast<unsigned>(frame.stackLongs[2]));
            return handledTrap(frame, 0, 0);

        case 0xA076:
        {
            const uint32_t recordP = frame.stackLongs[0];
            const uint32_t offset = frame.stackLongs[1];
            const uint32_t srcP = frame.stackLongs[2];
            const uint32_t bytes = frame.stackLongs[3];
            const uint32_t writableBytes = palmPtrSize(recordP + offset);
            const uint32_t safeBytes = bytes > writableBytes ? writableBytes : bytes;
            copyMemoryBytes(recordP + offset, srcP, safeBytes);
            if (markFakeMemoRecordDirtyByPtr(recordP))
            {
                const uint32_t recordBytes = palmPtrSize(recordP);
                if (offset + safeBytes < recordBytes)
                {
                    writeByteIfPointer(recordP + offset + safeBytes, 0);
                }
                commitFakeMemoRecordByPtr(recordP);
            }
            else if (recordP == kFakeMemoRecordPtr)
            {
                if (offset + safeBytes < 64u)
                {
                    writeByteIfPointer(recordP + offset + safeBytes, 0);
                }
                gFakeMemoRecordScratchDirty = true;
                commitFakeMemoRecordScratch();
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s recordP=0x%08X offset=%u srcP=0x%08X bytes=%u copied=%u -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(recordP),
                static_cast<unsigned>(offset),
                static_cast<unsigned>(srcP),
                static_cast<unsigned>(bytes),
                static_cast<unsigned>(safeBytes));
            return handledTrap(frame, 0, 0);
        }

        case 0xA077:
        {
            const uint32_t recordP = frame.stackLongs[0];
            const uint32_t offset = frame.stackLongs[1];
            const uint32_t srcP = frame.stackLongs[2];
            char text[64];
            text[0] = '\0';
            readCString(srcP, text, sizeof(text));
            writeCString(recordP + offset, text, palmPtrSize(recordP + offset));
            if (markFakeMemoRecordDirtyByPtr(recordP))
            {
                commitFakeMemoRecordByPtr(recordP);
            }
            else if (recordP == kFakeMemoRecordPtr)
            {
                gFakeMemoRecordScratchDirty = true;
                commitFakeMemoRecordScratch();
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s recordP=0x%08X offset=%u srcP=0x%08X text='%s' -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(recordP),
                static_cast<unsigned>(offset),
                static_cast<unsigned>(srcP),
                text);
            return handledTrap(frame, 0, 0);
        }

        case 0xA07E:
        {
            const uint32_t recordP = frame.stackLongs[0];
            const uint32_t offset = frame.stackLongs[1];
            const uint32_t bytes = frame.stackLongs[2];
            const uint8_t value = static_cast<uint8_t>(stackWord(frame, 12) & 0xffu);
            const uint32_t writableBytes = palmPtrSize(recordP + offset);
            const uint32_t safeBytes = bytes > writableBytes ? writableBytes : bytes;
            setMemoryBytes(recordP + offset, safeBytes, value);
            if (markFakeMemoRecordDirtyByPtr(recordP))
            {
                commitFakeMemoRecordByPtr(recordP);
            }
            else if (recordP == kFakeMemoRecordPtr)
            {
                gFakeMemoRecordScratchDirty = true;
                commitFakeMemoRecordScratch();
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s recordP=0x%08X offset=%u bytes=%u value=0x%02X set=%u -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(recordP),
                static_cast<unsigned>(offset),
                static_cast<unsigned>(bytes),
                static_cast<unsigned>(value),
                static_cast<unsigned>(safeBytes));
            return handledTrap(frame, 0, 0);
        }

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

        case 0xA10D:
        case 0xA110:
        {
            const uint32_t controlP = frame.stackLongs[0];
            fakeControlDraw(frame.selector, controlP);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s controlP=0x%08X -> drawn\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(controlP));
            return handledTrap(frame, 0, 0);
        }

        case 0xA10E:
        case 0xA10F:
        {
            const uint32_t controlP = frame.stackLongs[0];
            FakePalmFormObject* object = fakeFormObjectForPtr(controlP);
            if (fakeControlIsControl(object))
            {
                object->visible = false;
                object->value = 0;
                fakeControlSyncMemory(*object);
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s controlP=0x%08X -> hidden\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(controlP));
            return handledTrap(frame, 0, 0);
        }

        case 0xA111:
        {
            const uint32_t controlP = frame.stackLongs[0];
            FakePalmFormObject* object = fakeFormObjectForPtr(controlP);
            const int16_t value = fakeControlIsControl(object) ? object->value : 0;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s controlP=0x%08X -> value=%d\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(controlP),
                static_cast<int>(value));
            return handledTrap(frame, static_cast<uint32_t>(static_cast<uint16_t>(value)), 0);
        }

        case 0xA112:
        {
            const uint32_t controlP = frame.stackLongs[0];
            const int16_t newValue = static_cast<int16_t>(stackWord(frame, 4));
            FakePalmFormObject* object = fakeFormObjectForPtr(controlP);
            if (fakeControlIsControl(object))
            {
                object->value = newValue;
                fakeControlSyncMemory(*object);
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s controlP=0x%08X value=%d -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(controlP),
                static_cast<int>(newValue));
            return handledTrap(frame, 0, 0);
        }

        case 0xA113:
        {
            const uint32_t controlP = frame.stackLongs[0];
            const uint32_t labelP = fakeControlGetLabelPtr(controlP);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s controlP=0x%08X -> labelP=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(controlP),
                static_cast<unsigned>(labelP));
            return handledTrap(frame, labelP, labelP);
        }

        case 0xA114:
        {
            const uint32_t controlP = frame.stackLongs[0];
            const uint32_t labelP = frame.stackLongs[1];
            char label[16];
            label[0] = '\0';
            readCString(labelP, label, sizeof(label));
            fakeControlSetLabel(controlP, label);
            fakeControlDraw(frame.selector, controlP);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s controlP=0x%08X label='%s' -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(controlP),
                label);
            return handledTrap(frame, 0, 0);
        }

        case 0xA115:
        {
            const uint32_t controlP = frame.stackLongs[0];
            const uint32_t eventP = frame.stackLongs[1];
            const bool handled = fakeControlHandleEvent(frame.selector, controlP, eventP);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s controlP=0x%08X eventP=0x%08X -> %s\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(controlP),
                static_cast<unsigned>(eventP),
                handled ? ""true"" : ""false"");
            return handledTrap(frame, handled ? 1u : 0u, handled ? 1u : 0u);
        }

        case 0xA116:
        {
            const uint32_t controlP = frame.stackLongs[0];
            fakeControlHit(frame.selector, controlP, ""CtlHitControl"");
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s controlP=0x%08X -> hit\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(controlP));
            return handledTrap(frame, 0, 0);
        }

        case 0xA117:
        case 0xA118:
        {
            const uint32_t controlP = frame.stackLongs[0];
            const bool on = stackWord(frame, 4) != 0;
            FakePalmFormObject* object = fakeFormObjectForPtr(controlP);
            if (fakeControlIsControl(object))
            {
                if (frame.selector == 0xA117u)
                {
                    object->enabled = on;
                }
                else
                {
                    object->usable = on;
                    object->visible = on;
                }
                fakeControlSyncMemory(*object);
                if (on)
                {
                    fakeControlDraw(frame.selector, controlP);
                }
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s controlP=0x%08X on=%s -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(controlP),
                on ? ""true"" : ""false"");
            return handledTrap(frame, 0, 0);
        }

        case 0xA119:
        {
            const uint32_t controlP = frame.stackLongs[0];
            FakePalmFormObject* object = fakeFormObjectForPtr(controlP);
            const bool enabled = fakeControlIsControl(object) && object->enabled;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s controlP=0x%08X -> enabled=%s\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(controlP),
                enabled ? ""true"" : ""false"");
            return handledTrap(frame, enabled ? 1u : 0u, enabled ? 1u : 0u);
        }

        case 0xA173:
        {
            const uint32_t formP = fakeFormEnsureActive(gFakeActiveFormPtr);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s -> formP=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP));
            return handledTrap(frame, formP, formP);
        }

        case 0xA174:
        {
            const uint32_t formP = frame.stackLongs[0];
            if (formP != 0)
            {
                fakeFormEnsureActive(formP);
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP));
            return handledTrap(frame, 0, 0);
        }

        case 0xA175:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s -> formId=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(gFakeActiveFormId));
            return handledTrap(frame, gFakeActiveFormId, 0);

        case 0xA176:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X -> false\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]));
            return handledTrap(frame, 0, 0);

        case 0xA177:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]));
            return handledTrap(frame, 0, 0);

        case 0xA178:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X -> focus=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(gFakeFormFocusIndex));
            return handledTrap(frame, gFakeFormFocusIndex, 0);

        case 0xA179:
        {
            const uint32_t formP = frame.stackLongs[0];
            const uint16_t objectIndex = stackWord(frame, 4);
            fakeFormEnsureActive(formP);
            gFakeFormFocusIndex = objectIndex < gFakeFormObjectCount ? objectIndex : 0xffffu;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X index=%u -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP),
                static_cast<unsigned>(objectIndex));
            return handledTrap(frame, 0, 0);
        }

        case 0xA16F:
        {
            const uint16_t formId = stackWord(frame, 0);
            const uint32_t formP = fakeFormInit(formId);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formId=%u catalog=%s -> formP=0x%08X objects=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formId),
                gFakeActiveFormCataloged ? ""yes"" : ""no"",
                static_cast<unsigned>(formP),
                static_cast<unsigned>(gFakeFormObjectCount));
            return handledTrap(frame, formP, formP);
        }

        case 0xA170:
        {
            const uint32_t formP = frame.stackLongs[0];
            fakeFormDelete(formP);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP));
            return handledTrap(frame, 0, 0);
        }

        case 0xA171:
        {
            const uint32_t formP = frame.stackLongs[0];
            fakeFormDraw(frame.selector, formP);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X formId=%u objects=%u -> drawn\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP),
                static_cast<unsigned>(gFakeActiveFormId),
                static_cast<unsigned>(gFakeFormObjectCount));
            return handledTrap(frame, 0, 0);
        }

        case 0xA172:
        {
            const uint32_t formP = frame.stackLongs[0];
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP));
            return handledTrap(frame, 0, 0);
        }

        case 0xA17B:
        {
            const uint32_t formP = frame.stackLongs[0];
            const uint32_t rectP = frame.stackLongs[1];
            writeWordIfPointer(rectP + 0u, 0u);
            writeWordIfPointer(rectP + 2u, 0u);
            writeWordIfPointer(rectP + 4u, 160u);
            writeWordIfPointer(rectP + 6u, 160u);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X rectP=0x%08X -> 160x160\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP),
                static_cast<unsigned>(rectP));
            return handledTrap(frame, 0, 0);
        }

        case 0xA17D:
        {
            const uint32_t formP = frame.stackLongs[0];
            fakeFormEnsureActive(formP);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X -> formId=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP),
                static_cast<unsigned>(gFakeActiveFormId));
            return handledTrap(frame, gFakeActiveFormId, 0);
        }

        case 0xA17E:
        {
            const uint16_t formId = stackWord(frame, 0);
            const uint32_t formP = fakeFormInit(formId);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formId=%u -> formP=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formId),
                static_cast<unsigned>(formP));
            return handledTrap(frame, formP, formP);
        }

        case 0xA17F:
        {
            const uint32_t formP = frame.stackLongs[0];
            fakeFormEnsureActive(formP);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X -> objects=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP),
                static_cast<unsigned>(gFakeFormObjectCount));
            return handledTrap(frame, gFakeFormObjectCount, 0);
        }

        case 0xA180:
        {
            const uint32_t formP = frame.stackLongs[0];
            const uint16_t objectId = stackWord(frame, 4);
            const uint16_t objectIndex = fakeFormGetObjectIndex(formP, objectId);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X objectId=%u -> index=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP),
                static_cast<unsigned>(objectId),
                static_cast<unsigned>(objectIndex));
            return handledTrap(frame, objectIndex, 0);
        }

        case 0xA181:
        {
            const uint32_t formP = frame.stackLongs[0];
            const uint16_t objectIndex = stackWord(frame, 4);
            FakePalmFormObject* object = fakeFormObjectAtIndex(formP, objectIndex);
            const uint16_t objectId = object != nullptr ? object->objectId : 0u;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X index=%u -> id=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP),
                static_cast<unsigned>(objectIndex),
                static_cast<unsigned>(objectId));
            return handledTrap(frame, objectId, 0);
        }

        case 0xA182:
        {
            const uint32_t formP = frame.stackLongs[0];
            const uint16_t objectIndex = stackWord(frame, 4);
            FakePalmFormObject* object = fakeFormObjectAtIndex(formP, objectIndex);
            const uint16_t objectType = object != nullptr ? object->kind : 0xffffu;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X index=%u -> type=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP),
                static_cast<unsigned>(objectIndex),
                static_cast<unsigned>(objectType));
            return handledTrap(frame, objectType, 0);
        }

        case 0xA183:
        {
            const uint32_t formP = frame.stackLongs[0];
            const uint16_t objectIndex = stackWord(frame, 4);
            const uint32_t objectP = fakeFormGetObjectPtr(formP, objectIndex);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X index=%u -> objectP=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP),
                static_cast<unsigned>(objectIndex),
                static_cast<unsigned>(objectP));
            return handledTrap(frame, objectP, objectP);
        }

        case 0xA184:
        case 0xA185:
        {
            const uint32_t formP = frame.stackLongs[0];
            const uint16_t objectIndex = stackWord(frame, 4);
            FakePalmFormObject* object = fakeFormObjectAtIndex(formP, objectIndex);
            if (object != nullptr)
            {
                object->visible = frame.selector == 0xA185u;
                if (object->visible && fakeControlIsControl(object))
                {
                    fakeControlDraw(frame.selector, object->ptr);
                }
                else
                {
                    fakeControlSyncMemory(*object);
                }
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X index=%u -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP),
                static_cast<unsigned>(objectIndex));
            return handledTrap(frame, 0, 0);
        }

        case 0xA186:
        {
            const uint32_t formP = frame.stackLongs[0];
            const uint16_t objectIndex = stackWord(frame, 4);
            const uint32_t xP = stackLong(frame, 6);
            const uint32_t yP = stackLong(frame, 10);
            FakePalmFormObject* object = fakeFormObjectAtIndex(formP, objectIndex);
            if (object != nullptr)
            {
                writeWordIfPointer(xP, static_cast<uint16_t>(object->x));
                writeWordIfPointer(yP, static_cast<uint16_t>(object->y));
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X index=%u -> x=%d y=%d\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP),
                static_cast<unsigned>(objectIndex),
                object != nullptr ? static_cast<int>(object->x) : -1,
                object != nullptr ? static_cast<int>(object->y) : -1);
            return handledTrap(frame, 0, 0);
        }

        case 0xA187:
        {
            const uint32_t formP = frame.stackLongs[0];
            const uint16_t objectIndex = stackWord(frame, 4);
            const int16_t x = static_cast<int16_t>(stackWord(frame, 6));
            const int16_t y = static_cast<int16_t>(stackWord(frame, 8));
            FakePalmFormObject* object = fakeFormObjectAtIndex(formP, objectIndex);
            if (object != nullptr)
            {
                object->x = x;
                object->y = y;
                fakeControlSyncMemory(*object);
                palmDisplayPalmUiSetObjectBounds(object->objectId, object->x, object->y, object->w, object->h);
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X index=%u x=%d y=%d -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP),
                static_cast<unsigned>(objectIndex),
                static_cast<int>(x),
                static_cast<int>(y));
            return handledTrap(frame, 0, 0);
        }

        case 0xA188:
        {
            const uint32_t formP = frame.stackLongs[0];
            const uint16_t objectIndex = stackWord(frame, 4);
            FakePalmFormObject* object = fakeFormObjectAtIndex(formP, objectIndex);
            const int16_t value = fakeControlIsControl(object) ? object->value : 0;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X index=%u -> value=%d\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP),
                static_cast<unsigned>(objectIndex),
                static_cast<int>(value));
            return handledTrap(frame, static_cast<uint32_t>(static_cast<uint16_t>(value)), 0);
        }

        case 0xA189:
        {
            const uint32_t formP = frame.stackLongs[0];
            const uint16_t objectIndex = stackWord(frame, 4);
            const int16_t value = static_cast<int16_t>(stackWord(frame, 6));
            FakePalmFormObject* object = fakeFormObjectAtIndex(formP, objectIndex);
            if (fakeControlIsControl(object))
            {
                object->value = value;
                fakeControlSyncMemory(*object);
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X index=%u value=%d -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP),
                static_cast<unsigned>(objectIndex),
                static_cast<int>(value));
            return handledTrap(frame, 0, 0);
        }

        case 0xA199:
        {
            const uint32_t formP = frame.stackLongs[0];
            const uint16_t objectIndex = stackWord(frame, 4);
            const uint32_t rectP = stackLong(frame, 6);
            FakePalmFormObject* object = fakeFormObjectAtIndex(formP, objectIndex);
            if (object != nullptr)
            {
                fakeFormWriteRectangle(rectP, *object);
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X index=%u rectP=0x%08X -> %s\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP),
                static_cast<unsigned>(objectIndex),
                static_cast<unsigned>(rectP),
                object != nullptr ? ""ok"" : ""missing"");
            return handledTrap(frame, 0, 0);
        }

        case 0xA135:
        case 0xA13D:
        case 0xA162:
        {
            fakeFieldMirrorToDisplay(frame.selector);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s fieldP=0x%08X -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]));
            return handledTrap(frame, 0, 0);
        }

        case 0xA137:
        {
            zeroMemory(kFakeFieldTextPtr, 64u);
            gFakeFieldInsPt = 0;
            gFakeFieldDirty = false;
            fakeFieldMirrorToDisplay(frame.selector);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s fieldP=0x%08X -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]));
            return handledTrap(frame, 0, 0);
        }

        case 0xA139:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s fieldP=0x%08X -> textP=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(kFakeFieldTextPtr));
            return handledTrap(frame, kFakeFieldTextPtr, kFakeFieldTextPtr);

        case 0xA153:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s fieldP=0x%08X -> handle=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(kFakeFieldTextHandle));
            return handledTrap(frame, kFakeFieldTextHandle, kFakeFieldTextHandle);

        case 0xA13F:
        {
            const uint32_t fieldP = frame.stackLongs[0];
            const uint32_t textHandle = frame.stackLongs[1];
            const uint16_t offset = stackWord(frame, 8);
            const uint16_t size = stackWord(frame, 10);
            uint32_t textP = 0;
            if (textHandle == kFakeMemoRecordHandle)
            {
                textP = kFakeMemoRecordPtr + offset;
            }
            else if (textHandle == kFakeFieldTextHandle)
            {
                textP = kFakeFieldTextPtr + offset;
            }
            else if (textHandle == kFakeAllocationHandle)
            {
                textP = kFakeAllocationPtr + offset;
            }
            else
            {
                const uint32_t lockedP = tinyHandleLock(textHandle);
                if (lockedP != 0)
                {
                    textP = lockedP + offset;
                }
            }
            fakeFieldSetTextFromPointer(textP, size);
            fakeFieldMirrorToDisplay(frame.selector);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s fieldP=0x%08X textH=0x%08X offset=%u size=%u -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(fieldP),
                static_cast<unsigned>(textHandle),
                static_cast<unsigned>(offset),
                static_cast<unsigned>(size));
            return handledTrap(frame, 0, 0);
        }

        case 0xA158:
        {
            const uint32_t fieldP = frame.stackLongs[0];
            const uint32_t textHandle = frame.stackLongs[1];
            uint32_t textP = 0;
            if (textHandle == kFakeMemoRecordHandle)
            {
                textP = kFakeMemoRecordPtr;
            }
            else if (textHandle == kFakeFieldTextHandle)
            {
                textP = kFakeFieldTextPtr;
            }
            else if (textHandle == kFakeAllocationHandle)
            {
                textP = kFakeAllocationPtr;
            }
            else
            {
                textP = tinyHandleLock(textHandle);
            }
            fakeFieldSetTextFromPointer(textP, 63u);
            fakeFieldMirrorToDisplay(frame.selector);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s fieldP=0x%08X textH=0x%08X textP=0x%08X -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(fieldP),
                static_cast<unsigned>(textHandle),
                static_cast<unsigned>(textP));
            return handledTrap(frame, 0, 0);
        }

        case 0xA159:
        {
            const uint32_t fieldP = frame.stackLongs[0];
            const uint32_t textP = frame.stackLongs[1];
            fakeFieldSetTextFromPointer(textP, 63u);
            fakeFieldMirrorToDisplay(frame.selector);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s fieldP=0x%08X textP=0x%08X -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(fieldP),
                static_cast<unsigned>(textP));
            return handledTrap(frame, 0, 0);
        }

        case 0xA13A:
        {
            const uint32_t startP = frame.stackLongs[1];
            const uint32_t endP = frame.stackLongs[2];
            writeWordIfPointer(startP, gFakeFieldInsPt);
            writeWordIfPointer(endP, gFakeFieldInsPt);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s fieldP=0x%08X startP=0x%08X endP=0x%08X pos=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(startP),
                static_cast<unsigned>(endP),
                static_cast<unsigned>(gFakeFieldInsPt));
            return handledTrap(frame, 0, 0);
        }

        case 0xA142:
        {
            const uint16_t start = stackWord(frame, 4);
            const uint16_t end = stackWord(frame, 6);
            gFakeFieldInsPt = end > gFakeFieldMaxChars ? gFakeFieldMaxChars : end;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s fieldP=0x%08X start=%u end=%u -> pos=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(start),
                static_cast<unsigned>(end),
                static_cast<unsigned>(gFakeFieldInsPt));
            return handledTrap(frame, 0, 0);
        }

        case 0xA145:
            return handledTrap(frame, gFakeFieldInsPt, 0);

        case 0xA146:
        {
            const uint16_t position = stackWord(frame, 4);
            gFakeFieldInsPt = position > gFakeFieldMaxChars ? gFakeFieldMaxChars : position;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s fieldP=0x%08X position=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(gFakeFieldInsPt));
            return handledTrap(frame, 0, 0);
        }

        case 0xA14A:
        case 0xA157:
            if (frame.selector == 0xA157)
            {
                const uint16_t size = stackWord(frame, 4);
                gFakeFieldMaxChars = size > 0 && size < 64u ? static_cast<uint16_t>(size - 1u) : 63u;
            }
            return handledTrap(frame, 64u, 0);

        case 0xA14B:
            return handledTrap(frame, fakeFieldTextLength(), 0);

        case 0xA15A:
            return handledTrap(frame, gFakeFieldMaxChars, 0);

        case 0xA15B:
        {
            const uint16_t maxChars = stackWord(frame, 4);
            gFakeFieldMaxChars = maxChars > 63u ? 63u : maxChars;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s fieldP=0x%08X max=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(gFakeFieldMaxChars));
            return handledTrap(frame, 0, 0);
        }

        case 0xA15D:
        {
            const uint32_t charsP = frame.stackLongs[1];
            const uint16_t insertLen = stackWord(frame, 8);
            fakeFieldInsertText(charsP, insertLen);
            fakeFieldMirrorToDisplay(frame.selector);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s fieldP=0x%08X charsP=0x%08X len=%u -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(charsP),
                static_cast<unsigned>(insertLen));
            return handledTrap(frame, 0, 0);
        }

        case 0xA15E:
        {
            const uint16_t start = stackWord(frame, 4);
            const uint16_t end = stackWord(frame, 6);
            fakeFieldDeleteRange(start, end);
            fakeFieldMirrorToDisplay(frame.selector);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s fieldP=0x%08X start=%u end=%u -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(start),
                static_cast<unsigned>(end));
            return handledTrap(frame, 0, 0);
        }

        case 0xA155:
            return handledTrap(frame, gFakeFieldDirty ? 1u : 0u, 0);

        case 0xA160:
        {
            const uint16_t dirty = stackWord(frame, 4);
            gFakeFieldDirty = dirty != 0;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s fieldP=0x%08X dirty=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(dirty));
            return handledTrap(frame, 0, 0);
        }

        case 0xA143:
        case 0xA144:
        case 0xA147:
        case 0xA14C:
        case 0xA15C:
        {
            if (frame.selector == 0xA15C)
            {
                gFakeFieldUsable = stackWord(frame, 4) != 0;
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s fieldP=0x%08X usable=%s\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                gFakeFieldUsable ? ""yes"" : ""no"");
            return handledTrap(frame, 0, 0);
        }

        case 0xA13B:
        case 0xA148:
        case 0xA149:
        case 0xA14D:
        case 0xA14E:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s fieldP=0x%08X -> 0\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]));
            return handledTrap(frame, 0, 0);

        case 0xA192:
        {
            const uint16_t alertId = stackWord(frame, 0);
            showMemoPadProbe(frame.selector, ""Memo Pad"", """");
            palmDisplayPalmUiShowModal(frame.selector, gRuntimeUiProbeTrapCount, ""Alert"", ""Palm alert requested"", ""Tap OK"");
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s alertId=%u -> button=0\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(alertId));
            return handledTrap(frame, 0, 0);
        }

        case 0xA193:
        {
            const uint32_t formP = frame.stackLongs[0];
            showMemoPadProbe(frame.selector, ""Memo Pad"", """");
            palmDisplayPalmUiShowModal(frame.selector, gRuntimeUiProbeTrapCount, ""Dialog"", ""Form dialog requested"", ""Tap OK"");
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s formP=0x%08X -> button=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(formP),
                static_cast<unsigned>(kModalOkButtonId));
            return handledTrap(frame, kModalOkButtonId, kModalOkButtonId);
        }

        case 0xA194:
        {
            const uint16_t alertId = stackWord(frame, 0);
            char text[32];
            text[0] = '\0';
            readCandidateTextFromFrame(frame, text, sizeof(text));
            showMemoPadProbe(frame.selector, ""Memo Pad"", """");
            palmDisplayPalmUiShowModal(frame.selector, gRuntimeUiProbeTrapCount, ""Alert"", text[0] != '\0' ? text : ""Custom alert"", ""Tap OK"");
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s alertId=%u text='%s' -> button=0\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(alertId),
                text);
            return handledTrap(frame, 0, 0);
        }

        case 0xA19B:
        case 0xA0A9:
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
            if (frame.selector == 0xA11D || frame.selector == 0xA0A9 || frame.selector == 0xA1A0)
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

        case 0xA1A0:
        {
            const uint32_t eventP = frame.stackLongs[0];
            const uint16_t eType = looksWritablePointer(eventP) ? static_cast<uint16_t>(m68k_read_memory_16(eventP) & 0xffffu) : 0u;
            const int16_t x = looksWritablePointer(eventP + 4u) ? static_cast<int16_t>(m68k_read_memory_16(eventP + 4u) & 0xffffu) : 0;
            const int16_t y = looksWritablePointer(eventP + 6u) ? static_cast<int16_t>(m68k_read_memory_16(eventP + 6u) & 0xffffu) : 0;
            const uint16_t dataId = looksWritablePointer(eventP + 8u) ? static_cast<uint16_t>(m68k_read_memory_16(eventP + 8u) & 0xffffu) : 0u;
            const int16_t selection = looksWritablePointer(eventP + 14u) ? static_cast<int16_t>(m68k_read_memory_16(eventP + 14u) & 0xffffu) : -1;
            showMemoPadProbe(frame.selector, """", """");
            if (eType == 1u || eType == 2u)
            {
                palmDisplayPalmUiHandleTap(frame.selector, gRuntimeUiProbeTrapCount, x, y, ""FrmDispatchEvent"");
            }
            else if (eType == 8u)
            {
                const int16_t buttonX = dataId == kMemoNewButtonId || dataId == kMemoDoneButtonId ? 30 : 92;
                palmDisplayPalmUiHandleTap(frame.selector, gRuntimeUiProbeTrapCount, buttonX, 148, ""ctlSelectEvent"");
            }
            else if (eType == 13u && selection >= 0)
            {
                palmDisplayPalmUiHandleTap(frame.selector, gRuntimeUiProbeTrapCount, 24, static_cast<int16_t>(29 + selection * 18), ""lstSelectEvent"");
            }
            logUiGeometryProbe(frame);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s eventP=0x%08X eType=%u x=%d y=%d dataId=%u selection=%d -> false\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(eventP),
                static_cast<unsigned>(eType),
                static_cast<int>(x),
                static_cast<int>(y),
                static_cast<unsigned>(dataId),
                static_cast<int>(selection));
            return handledTrap(frame, 0, 0);
        }

        case 0xA1BD:
        {
            const uint16_t resourceId = stackWord(frame, 0);
            const uint32_t menuP = fakeMenuInit(resourceId);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s resourceId=%u -> menuP=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(resourceId),
                static_cast<unsigned>(menuP));
            return handledTrap(frame, menuP, menuP);
        }

        case 0xA1BE:
        {
            const uint32_t menuP = frame.stackLongs[0];
            fakeMenuDispose(menuP);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s menuP=0x%08X -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(menuP));
            return handledTrap(frame, 0, 0);
        }

        case 0xA1BF:
        {
            const uint32_t menuP = frame.stackLongs[0];
            const uint32_t eventP = frame.stackLongs[1];
            const uint32_t errorP = frame.stackLongs[2];
            const bool handled = fakeMenuHandleEvent(menuP, eventP, errorP);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s menuP=0x%08X eventP=0x%08X errorP=0x%08X -> %s\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(menuP),
                static_cast<unsigned>(eventP),
                static_cast<unsigned>(errorP),
                handled ? ""true"" : ""false"");
            return handledTrap(frame, handled ? 1u : 0u, handled ? 1u : 0u);
        }

        case 0xA1C0:
        {
            const uint32_t menuP = frame.stackLongs[0];
            fakeMenuDraw(menuP);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s menuP=0x%08X -> drawn\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(menuP));
            return handledTrap(frame, 0, 0);
        }

        case 0xA1C1:
        {
            const uint32_t menuP = frame.stackLongs[0];
            fakeMenuErase(menuP);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s menuP=0x%08X -> erased\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(menuP));
            return handledTrap(frame, 0, 0);
        }

        case 0xA1C2:
        {
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s -> menuP=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(gFakeActiveMenuPtr));
            return handledTrap(frame, gFakeActiveMenuPtr, gFakeActiveMenuPtr);
        }

        case 0xA1C3:
        {
            const uint32_t menuP = frame.stackLongs[0];
            const uint32_t previous = fakeMenuSetActive(menuP);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s menuP=0x%08X -> previous=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(menuP),
                static_cast<unsigned>(previous));
            return handledTrap(frame, previous, previous);
        }

        case 0xA1FC:
        {
            const uint32_t windowH = frame.stackLongs[0] != 0 ? frame.stackLongs[0] : kFakeDisplayWindowHandle;
            gFakeActiveWindowHandle = windowH;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s window=0x%08X -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(windowH));
            return handledTrap(frame, 0, 0);
        }

        case 0xA1FD:
        {
            const uint32_t previous = gFakeDrawWindowHandle;
            const uint32_t windowH = frame.stackLongs[0] != 0 ? frame.stackLongs[0] : kFakeDisplayWindowHandle;
            gFakeDrawWindowHandle = windowH;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s window=0x%08X -> previous=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(windowH),
                static_cast<unsigned>(previous));
            return handledTrap(frame, previous, previous);
        }

        case 0xA1FE:
            return handledTrap(frame, gFakeDrawWindowHandle, gFakeDrawWindowHandle);

        case 0xA1FF:
            return handledTrap(frame, gFakeActiveWindowHandle, gFakeActiveWindowHandle);

        case 0xA200:
        case 0xA201:
            return handledTrap(frame, kFakeDisplayWindowHandle, kFakeDisplayWindowHandle);

        case 0xA202:
        case 0xA203:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s window=0x%08X -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]));
            return handledTrap(frame, 0, 0);

        case 0xA204:
        {
            const uint32_t rectP = frame.stackLongs[1];
            writePalmRectangle(rectP, 0, 0, 160, 160);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s window=0x%08X rectP=0x%08X -> 160x160\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(rectP));
            return handledTrap(frame, 0, 0);
        }

        case 0xA205:
            palmDisplayWinDrawRectangleFrame(frame.selector, ++gRuntimeUiProbeTrapCount, 0, 0, 160, 160, 0u);
            return handledTrap(frame, 0, 0);

        case 0xA206:
            palmDisplayWinEraseWindow(frame.selector, ++gRuntimeUiProbeTrapCount);
            return handledTrap(frame, 0, 0);

        case 0xA207:
        {
            const uint32_t errorP = frame.stackLongs[1];
            writeWordIfPointer(errorP, 0u);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s rectP=0x%08X errorP=0x%08X -> bits=0x%08X\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(errorP),
                static_cast<unsigned>(kFakeSavedBitsWindowHandle));
            return handledTrap(frame, kFakeSavedBitsWindowHandle, kFakeSavedBitsWindowHandle);
        }

        case 0xA208:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s bits=0x%08X x=%d y=%d -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<int>(stackWord(frame, 4)),
                static_cast<int>(stackWord(frame, 6)));
            return handledTrap(frame, 0, 0);

        case 0xA20B:
        case 0xA20C:
        {
            const uint32_t xP = frame.stackLongs[0];
            const uint32_t yP = frame.stackLongs[1];
            writeWordIfPointer(xP, 160u);
            writeWordIfPointer(yP, 160u);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s xP=0x%08X yP=0x%08X -> 160x160\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(xP),
                static_cast<unsigned>(yP));
            return handledTrap(frame, 0, 0);
        }

        case 0xA20D:
        case 0xA20E:
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s xP=0x%08X yP=0x%08X -> identity\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(frame.stackLongs[0]),
                static_cast<unsigned>(frame.stackLongs[1]));
            return handledTrap(frame, 0, 0);

        case 0xA20F:
        {
            const uint32_t rectP = frame.stackLongs[0];
            writePalmRectangle(rectP, gFakeClipX, gFakeClipY, gFakeClipW, gFakeClipH);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s rectP=0x%08X -> clip %d,%d %dx%d\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(rectP),
                static_cast<int>(gFakeClipX),
                static_cast<int>(gFakeClipY),
                static_cast<int>(gFakeClipW),
                static_cast<int>(gFakeClipH));
            return handledTrap(frame, 0, 0);
        }

        case 0xA210:
        {
            int16_t x, y, w, h;
            if (readPalmRectangle(frame.stackLongs[0], x, y, w, h))
            {
                gFakeClipX = x;
                gFakeClipY = y;
                gFakeClipW = w;
                gFakeClipH = h;
            }
            return handledTrap(frame, 0, 0);
        }

        case 0xA211:
            gFakeClipX = 0;
            gFakeClipY = 0;
            gFakeClipW = 160;
            gFakeClipH = 160;
            return handledTrap(frame, 0, 0);

        case 0xA212:
        {
            int16_t x, y, w, h;
            if (readPalmRectangle(frame.stackLongs[0], x, y, w, h))
            {
                const int16_t x2 = static_cast<int16_t>(max(static_cast<int>(x), static_cast<int>(gFakeClipX)));
                const int16_t y2 = static_cast<int16_t>(max(static_cast<int>(y), static_cast<int>(gFakeClipY)));
                const int16_t r2 = static_cast<int16_t>(min(static_cast<int>(x + w), static_cast<int>(gFakeClipX + gFakeClipW)));
                const int16_t b2 = static_cast<int16_t>(min(static_cast<int>(y + h), static_cast<int>(gFakeClipY + gFakeClipH)));
                writePalmRectangle(frame.stackLongs[0], x2, y2, r2 > x2 ? static_cast<int16_t>(r2 - x2) : 0, b2 > y2 ? static_cast<int16_t>(b2 - y2) : 0);
            }
            return handledTrap(frame, 0, 0);
        }

        case 0xA213:
        case 0xA214:
        case 0xA215:
        case 0xA216:
        case 0xA217:
        {
            const int16_t x1 = static_cast<int16_t>(stackWord(frame, 0));
            const int16_t y1 = static_cast<int16_t>(stackWord(frame, 2));
            const int16_t x2 = static_cast<int16_t>(stackWord(frame, 4));
            const int16_t y2 = static_cast<int16_t>(stackWord(frame, 6));
            const uint8_t mode = frame.selector == 0xA215u ? 1u : (frame.selector == 0xA214u || frame.selector == 0xA216u ? 2u : 0u);
            palmDisplayWinDrawLine(frame.selector, ++gRuntimeUiProbeTrapCount, x1, y1, x2, y2, mode);
            return handledTrap(frame, 0, 0);
        }

        case 0xA218:
        case 0xA219:
        case 0xA21A:
        case 0xA229:
        {
            int16_t x, y, w, h;
            readPalmRectangle(frame.stackLongs[0], x, y, w, h);
            const uint8_t mode = frame.selector == 0xA219u ? 1u : (frame.selector == 0xA21Au ? 2u : 0u);
            palmDisplayWinDrawRectangle(frame.selector, ++gRuntimeUiProbeTrapCount, x, y, w, h, mode);
            return handledTrap(frame, 0, 0);
        }

        case 0xA21B:
        case 0xA21C:
        case 0xA21D:
        case 0xA21E:
        {
            int16_t x, y, w, h;
            readPalmRectangle(stackLong(frame, 2), x, y, w, h);
            const uint8_t mode = frame.selector == 0xA21Du ? 1u : (frame.selector == 0xA21Cu || frame.selector == 0xA21Eu ? 2u : 0u);
            palmDisplayWinDrawRectangleFrame(frame.selector, ++gRuntimeUiProbeTrapCount, x, y, w, h, mode);
            return handledTrap(frame, 0, 0);
        }

        case 0xA21F:
        {
            int16_t x, y, w, h;
            const uint32_t sourceRectP = stackLong(frame, 2);
            const uint32_t obscuredP = stackLong(frame, 6);
            readPalmRectangle(sourceRectP, x, y, w, h);
            writePalmRectangle(obscuredP, x, y, w, h);
            return handledTrap(frame, 0, 0);
        }

        case 0xA226:
        {
            const uint32_t bitmapP = frame.stackLongs[0];
            const int16_t x = static_cast<int16_t>(stackWord(frame, 4));
            const int16_t y = static_cast<int16_t>(stackWord(frame, 6));
            bool drawn = false;
            if (bitmapP >= kFakeResourcePtr && bitmapP < kFakeResourcePtr + 256u && gLockedResource != nullptr)
            {
                drawn = palmDisplayWinDrawBitmapResource(frame.selector, ++gRuntimeUiProbeTrapCount, gLockedResource->bytes, gLockedResource->size, x, y);
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s bitmapP=0x%08X x=%d y=%d resource=%s -> %s\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(bitmapP),
                static_cast<int>(x),
                static_cast<int>(y),
                gLockedResource != nullptr ? gLockedResource->type : ""none"",
                drawn ? ""drawn"" : ""unsupported"");
            return handledTrap(frame, 0, 0);
        }

        case 0xA220:
        case 0xA221:
        case 0xA222:
        case 0xA22A:
        {
            const uint32_t charsP = frame.stackLongs[0];
            const int16_t len = static_cast<int16_t>(stackWord(frame, 4));
            const int16_t x = static_cast<int16_t>(stackWord(frame, 6));
            const int16_t y = static_cast<int16_t>(stackWord(frame, 8));
            char text[64];
            text[0] = '\0';
            readCString(charsP, text, len >= 0 && len < static_cast<int16_t>(sizeof(text)) ? static_cast<uint32_t>(len) + 1u : sizeof(text));
            const uint8_t mode = frame.selector == 0xA221u ? 1u : (frame.selector == 0xA222u || frame.selector == 0xA22Au ? 2u : 0u);
            palmDisplayWinDrawChars(frame.selector, ++gRuntimeUiProbeTrapCount, text, len, x, y, mode);
            return handledTrap(frame, 0, 0);
        }

        case 0xA227:
            return handledTrap(frame, 0, 0);

        case 0xA228:
        {
            const uint32_t rectP = frame.stackLongs[0];
            writePalmRectangle(rectP, 0, 0, 160, 160);
            return handledTrap(frame, 0, 0);
        }

        case 0xA1B0:
        case 0xA1B8:
        {
            const uint32_t listP = frame.stackLongs[0];
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s listP=0x%08X arg1=0x%08X arg2=0x%08X -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(listP),
                static_cast<unsigned>(frame.stackLongs[1]),
                static_cast<unsigned>(frame.stackLongs[2]));
            return handledTrap(frame, 0, 0);
        }

        case 0xA1B1:
        {
            const uint32_t listP = frame.stackLongs[0];
            fakeListDraw(frame.selector, listP);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s listP=0x%08X -> drawn\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(listP));
            return handledTrap(frame, 0, 0);
        }

        case 0xA1B2:
        {
            const uint32_t listP = frame.stackLongs[0];
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s listP=0x%08X -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(listP));
            return handledTrap(frame, 0, 0);
        }

        case 0xA1B3:
        {
            const uint32_t listP = frame.stackLongs[0];
            const int16_t selection = fakeListIsMemoList(listP) ? palmDisplayMemoListSelection() : -1;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s listP=0x%08X -> selection=%d\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(listP),
                static_cast<int>(selection));
            return handledTrap(frame, selection < 0 ? 0xffffffffu : static_cast<uint32_t>(selection), 0);
        }

        case 0xA1B4:
        {
            const uint32_t listP = frame.stackLongs[0];
            const int16_t itemNum = static_cast<int16_t>(stackWord(frame, 4));
            const int16_t safeItem = fakeListClampSelection(itemNum);
            if (fakeListIsMemoList(listP) && safeItem >= 0)
            {
                writeCString(kFakeListTextPtr, palmDisplayMemoText(static_cast<uint16_t>(safeItem)), 64u);
            }
            else
            {
                writeCString(kFakeListTextPtr, """", 64u);
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s listP=0x%08X item=%d -> textP=0x%08X '%s'\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(listP),
                static_cast<int>(itemNum),
                static_cast<unsigned>(kFakeListTextPtr),
                safeItem >= 0 ? palmDisplayMemoText(static_cast<uint16_t>(safeItem)) : """");
            return handledTrap(frame, kFakeListTextPtr, kFakeListTextPtr);
        }

        case 0xA1B5:
        {
            const uint32_t listP = frame.stackLongs[0];
            const uint32_t eventP = frame.stackLongs[1];
            const uint16_t eType = looksWritablePointer(eventP) ? static_cast<uint16_t>(m68k_read_memory_16(eventP) & 0xffffu) : 0u;
            const int16_t selection = looksWritablePointer(eventP + 14u) ? static_cast<int16_t>(m68k_read_memory_16(eventP + 14u) & 0xffffu) : -1;
            bool handled = false;
            if (fakeListIsMemoList(listP) && eType == 13u && fakeListClampSelection(selection) >= 0)
            {
                palmDisplayMemoListSetSelection(selection);
                fakeListDraw(frame.selector, listP);
                handled = true;
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s listP=0x%08X eventP=0x%08X eType=%u selection=%d -> %s\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(listP),
                static_cast<unsigned>(eventP),
                static_cast<unsigned>(eType),
                static_cast<int>(selection),
                handled ? ""true"" : ""false"");
            return handledTrap(frame, handled ? 1u : 0u, handled ? 1u : 0u);
        }

        case 0xA1B6:
        {
            const uint32_t listP = frame.stackLongs[0];
            const int16_t visibleItems = static_cast<int16_t>(stackWord(frame, 4));
            if (fakeListIsMemoList(listP) && visibleItems > 0)
            {
                gFakeListVisibleItems = visibleItems;
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s listP=0x%08X visible=%d -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(listP),
                static_cast<int>(visibleItems));
            return handledTrap(frame, 0, 0);
        }

        case 0xA1B7:
        {
            const uint32_t listP = frame.stackLongs[0];
            const int16_t itemNum = static_cast<int16_t>(stackWord(frame, 4));
            const int16_t safeItem = fakeListClampSelection(itemNum);
            if (fakeListIsMemoList(listP))
            {
                palmDisplayMemoListSetSelection(safeItem);
                fakeListDraw(frame.selector, listP);
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s listP=0x%08X item=%d safe=%d -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(listP),
                static_cast<int>(itemNum),
                static_cast<int>(safeItem));
            return handledTrap(frame, 0, 0);
        }

        case 0xA1B9:
        case 0xA2B5:
        {
            const uint32_t listP = frame.stackLongs[0];
            const int16_t itemNum = static_cast<int16_t>(stackWord(frame, 4));
            if (fakeListIsMemoList(listP) && fakeListClampSelection(itemNum) >= 0)
            {
                if (frame.selector == 0xA1B9u)
                {
                    palmDisplayMemoListMakeItemVisible(itemNum);
                }
                else
                {
                    palmDisplayMemoListSetTopItem(itemNum);
                }
                gFakeListTopItem = palmDisplayMemoListTopItem();
                fakeListDraw(frame.selector, listP);
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s listP=0x%08X item=%d top=%d -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(listP),
                static_cast<int>(itemNum),
                static_cast<int>(gFakeListTopItem));
            return handledTrap(frame, 0, 0);
        }

        case 0xA1BA:
        {
            const uint32_t listP = frame.stackLongs[0];
            const uint16_t count = fakeListIsMemoList(listP) ? palmDisplayMemoRecordCount() : 0u;
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s listP=0x%08X -> items=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(listP),
                static_cast<unsigned>(count));
            return handledTrap(frame, count, 0);
        }

        case 0xA1BB:
        {
            const uint32_t listP = frame.stackLongs[0];
            const int16_t selection = fakeListIsMemoList(listP) ? palmDisplayMemoListSelection() : -1;
            fakeListDraw(frame.selector, listP);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s listP=0x%08X -> selection=%d\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(listP),
                static_cast<int>(selection));
            return handledTrap(frame, selection < 0 ? 0xffffffffu : static_cast<uint32_t>(selection), 0);
        }

        case 0xA1BC:
        {
            const uint32_t listP = frame.stackLongs[0];
            const int16_t x = static_cast<int16_t>(stackWord(frame, 4));
            const int16_t y = static_cast<int16_t>(stackWord(frame, 6));
            FakePalmFormObject* object = fakeFormObjectForPtr(listP);
            if (object != nullptr)
            {
                object->x = x;
                object->y = y;
                writeWordIfPointer(object->ptr + 4u, static_cast<uint16_t>(x));
                writeWordIfPointer(object->ptr + 6u, static_cast<uint16_t>(y));
                palmDisplayPalmUiSetObjectBounds(object->objectId, object->x, object->y, object->w, object->h);
            }
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s listP=0x%08X x=%d y=%d -> ok\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(listP),
                static_cast<int>(x),
                static_cast<int>(y));
            return handledTrap(frame, 0, 0);
        }

        case 0xA11D:
        {
            const uint32_t eventP = frame.stackLongs[0];
            const uint32_t timeout = frame.stackLongs[1];
            PalmQueuedEvent queuedEvent{};
            const bool hasQueuedEvent = palmDisplayDequeuePalmEvent(&queuedEvent);
            if (hasQueuedEvent)
            {
                writePalmEvent(eventP, queuedEvent);
            }
            else
            {
                zeroMemory(eventP, 32u);
            }
            showMemoPadProbe(frame.selector, """", gPendingMemoText);
            if (gPendingMemoText[0] != '\0')
            {
                palmDisplayPalmUiDrawText(frame.selector, gRuntimeUiProbeTrapCount, 12, 29, gPendingMemoText);
            }
            logUiGeometryProbe(frame);
            logScratchBytes(""at EvtGetEvent"");
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s eventP=0x%08X timeout=0x%08X -> eType=%u x=%d y=%d queued=%s\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(eventP),
                static_cast<unsigned>(timeout),
                static_cast<unsigned>(hasQueuedEvent ? queuedEvent.eType : 0u),
                static_cast<int>(hasQueuedEvent ? queuedEvent.screenX : 0),
                static_cast<int>(hasQueuedEvent ? queuedEvent.screenY : 0),
                hasQueuedEvent ? ""yes"" : ""no"");
            return handledTrap(frame, 0, 0);
        }

        case 0xA2CC:
        {
            const bool available = palmDisplayHasPalmEvent();
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s -> %s\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                available ? ""true"" : ""false"");
            return handledTrap(frame, available ? 1u : 0u, available ? 1u : 0u);
        }

        case 0xA071:
        {
            const uint32_t dbRef = frame.stackLongs[0];
            const uint16_t category = stackWord(frame, 4);
            const uint16_t recordCount = palmDisplayMemoRecordCount();
            publishFakeMemoRows(frame.selector, gRuntimeUiProbeTrapCount);
            Serial.printf(""  trap dispatch: selector=0x%04X name=%s dbRef=0x%08X category=%u -> records=%u\n"",
                static_cast<unsigned>(frame.selector),
                palmTrapName(frame.selector),
                static_cast<unsigned>(dbRef),
                static_cast<unsigned>(category),
                static_cast<unsigned>(recordCount));
            return handledTrap(frame, recordCount, 0);
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
static constexpr uint32_t kTrapHeapSize = 8192u;
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

#include <stdint.h>

#include ""palm_prc_loader.h""

struct PalmQueuedEvent
{
    uint16_t eType;
    bool penDown;
    uint8_t tapCount;
    int16_t screenX;
    int16_t screenY;
    uint16_t controlID;
    uint16_t listID;
    int16_t selection;
    bool on;
};

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
void palmDisplayPalmUiSetObjectBounds(uint16_t objectId, int16_t x, int16_t y, int16_t w, int16_t h);
void palmDisplayPalmUiDrawText(uint16_t selector, uint32_t trapCount, int16_t x, int16_t y, const char* text);
void palmDisplayPalmUiHandleTap(uint16_t selector, uint32_t trapCount, int16_t x, int16_t y, const char* source);
void palmDisplayPalmUiShowModal(uint16_t selector, uint32_t trapCount, const char* title, const char* line1, const char* line2);
void palmDisplayWinEraseWindow(uint16_t selector, uint32_t trapCount);
void palmDisplayWinDrawChars(uint16_t selector, uint32_t trapCount, const char* text, int16_t len, int16_t x, int16_t y, uint8_t mode);
void palmDisplayWinDrawLine(uint16_t selector, uint32_t trapCount, int16_t x1, int16_t y1, int16_t x2, int16_t y2, uint8_t mode);
void palmDisplayWinDrawRectangle(uint16_t selector, uint32_t trapCount, int16_t x, int16_t y, int16_t w, int16_t h, uint8_t mode);
void palmDisplayWinDrawRectangleFrame(uint16_t selector, uint32_t trapCount, int16_t x, int16_t y, int16_t w, int16_t h, uint8_t mode);
bool palmDisplayWinDrawBitmapResource(uint16_t selector, uint32_t trapCount, const uint8_t* bytes, uint32_t size, int16_t x, int16_t y);
uint16_t palmDisplayMemoRecordCount();
const char* palmDisplayMemoText(uint16_t index);
int16_t palmDisplayMemoListSelection();
void palmDisplayMemoListSetSelection(int16_t index);
int16_t palmDisplayMemoListTopItem();
void palmDisplayMemoListSetTopItem(int16_t index);
void palmDisplayMemoListMakeItemVisible(int16_t index);
void palmDisplayMemoListDraw(uint16_t selector, uint32_t trapCount);
bool palmDisplayInsertMemoRecord(uint16_t index, const char* text);
bool palmDisplayUpdateMemoRecord(uint16_t index, const char* text);
bool palmDisplayRemoveMemoRecord(uint16_t index);
bool palmDisplaySetEditText(const char* text);
bool palmDisplayHasPalmEvent();
bool palmDisplayDequeuePalmEvent(PalmQueuedEvent* event);
void palmDisplayPollSerialCommands();
"
    End Function

    Private Shared Function RenderPalmDisplayCpp() As String
        Return "#include ""palm_display.h""

#include ""generated/palm_font_resources.h""

#include <Arduino.h>
#include <esp_err.h>
#include <esp_heap_caps.h>
#include <esp_lcd_panel_ops.h>
#include <esp_lcd_panel_rgb.h>
#include <cstdio>
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
static int16_t gPalmUiSelectedRow = -1;
static int16_t gPalmUiTopRow = 0;
static uint16_t gPalmUiPressedControl = 0;
static uint8_t gPalmUiMode = 0;
static char gPalmEditText[64] = """";
static bool gPalmUiCategoryVisible = false;
static bool gPalmUiSurfaceStarted = false;
static bool gPalmModalVisible = false;
static uint16_t gPalmModalPressedControl = 0;
static char gPalmModalTitle[24] = """";
static char gPalmModalLine1[32] = """";
static char gPalmModalLine2[32] = """";
static char gPalmModalOkButton[12] = ""OK"";
static constexpr uint8_t kPalmEventQueueSize = 8;
static PalmQueuedEvent gPalmEventQueue[kPalmEventQueueSize] = {};
static uint8_t gPalmEventQueueHead = 0;
static uint8_t gPalmEventQueueTail = 0;
static constexpr uint16_t kMaxFakeMemoRecords = 12;
static char gFakeMemoRecords[kMaxFakeMemoRecords][64] = {};
static uint16_t gFakeMemoRecordCount = 0;
static bool gFakeMemoRecordsSeeded = false;
struct PalmDecodedFont
{
    bool valid;
    const PalmGeneratedFontResource* resource;
    const uint8_t* bytes;
    uint32_t size;
    uint16_t firstChar;
    uint16_t lastChar;
    uint16_t fRectHeight;
    uint16_t rowWords;
    uint16_t ascent;
    uint16_t descent;
    uint32_t bitmapOffset;
    uint32_t locTableOffset;
    uint32_t owTableOffset;
};
static PalmDecodedFont gPalmRealFont = {};
static bool gPalmRealFontProbeDone = false;

static constexpr uint8_t kPalmUiRoleForm = 1;
static constexpr uint8_t kPalmUiRoleTitle = 2;
static constexpr uint8_t kPalmUiRoleMemoText = 3;
static constexpr uint8_t kPalmUiModeList = 0;
static constexpr uint8_t kPalmUiModeEdit = 1;
static constexpr uint16_t kPalmNilEvent = 0;
static constexpr uint16_t kPalmPenDownEvent = 1;
static constexpr uint16_t kPalmPenUpEvent = 2;
static constexpr uint16_t kPalmCtlSelectEvent = 8;
static constexpr uint16_t kPalmLstSelectEvent = 13;
static constexpr uint16_t kMemoListId = 1000;
static constexpr uint16_t kMemoNewButtonId = 1001;
static constexpr uint16_t kMemoDetailsButtonId = 1002;
static constexpr uint16_t kMemoDoneButtonId = 1003;
static constexpr uint16_t kMemoCancelButtonId = 1004;
static constexpr uint16_t kModalOkButtonId = 9001;
static constexpr uint8_t kPalmUiObjectCapacity = 8;

struct PalmUiObjectBounds
{
    bool used;
    uint16_t objectId;
    int16_t x;
    int16_t y;
    int16_t w;
    int16_t h;
};

static PalmUiObjectBounds gPalmUiObjects[kPalmUiObjectCapacity] = {};

static void publishStoredMemoRowsToUi();
static void resetPalmUiSurface(const char* titleText);
static void presentPalmUiSurface(uint16_t selector, uint32_t trapCount);

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

static void palmUiDefaultObjectBounds(uint16_t objectId, int16_t& x, int16_t& y, int16_t& w, int16_t& h)
{
    switch (objectId)
    {
        case kMemoListId:
            x = 7; y = 24; w = 138; h = 110;
            break;
        case kMemoNewButtonId:
            x = 13; y = 142; w = 39; h = 13;
            break;
        case kMemoDetailsButtonId:
            x = 61; y = 142; w = 63; h = 13;
            break;
        case kMemoDoneButtonId:
            x = 13; y = 142; w = 39; h = 13;
            break;
        case kMemoCancelButtonId:
            x = 61; y = 142; w = 48; h = 13;
            break;
        case kModalOkButtonId:
            x = 62; y = 101; w = 36; h = 14;
            break;
        default:
            x = 0; y = 0; w = 0; h = 0;
            break;
    }
}

static PalmUiObjectBounds* palmUiFindObject(uint16_t objectId)
{
    for (uint8_t i = 0; i < kPalmUiObjectCapacity; ++i)
    {
        if (gPalmUiObjects[i].used && gPalmUiObjects[i].objectId == objectId)
        {
            return &gPalmUiObjects[i];
        }
    }

    return nullptr;
}

static PalmUiObjectBounds palmUiObjectBounds(uint16_t objectId)
{
    PalmUiObjectBounds bounds{};
    PalmUiObjectBounds* existing = palmUiFindObject(objectId);
    if (existing != nullptr)
    {
        return *existing;
    }

    bounds.used = true;
    bounds.objectId = objectId;
    palmUiDefaultObjectBounds(objectId, bounds.x, bounds.y, bounds.w, bounds.h);
    return bounds;
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

static uint16_t readPalmFontU16(const uint8_t* bytes, uint32_t offset)
{
    return static_cast<uint16_t>((static_cast<uint16_t>(bytes[offset]) << 8) | bytes[offset + 1u]);
}

static bool decodePalmGeneratedFont(const PalmGeneratedFontResource& resource, PalmDecodedFont& decoded)
{
    memset(&decoded, 0, sizeof(decoded));
    if (resource.bytes == nullptr || resource.size < 28u)
    {
        return false;
    }

    const uint16_t firstChar = readPalmFontU16(resource.bytes, 2u);
    const uint16_t lastChar = readPalmFontU16(resource.bytes, 4u);
    const uint16_t fRectHeight = readPalmFontU16(resource.bytes, 14u);
    const uint16_t owTLoc = readPalmFontU16(resource.bytes, 16u);
    const uint16_t ascent = readPalmFontU16(resource.bytes, 18u);
    const uint16_t descent = readPalmFontU16(resource.bytes, 20u);
    const uint16_t rowWords = readPalmFontU16(resource.bytes, 24u);
    if (firstChar > 0x7fu || lastChar < firstChar || lastChar > 0xffu || fRectHeight == 0u || fRectHeight > 32u || rowWords == 0u || rowWords > 256u)
    {
        return false;
    }

    const uint32_t charCount = static_cast<uint32_t>(lastChar) - firstChar + 1u;
    const uint32_t bitmapOffset = 26u;
    const uint32_t bitmapBytes = static_cast<uint32_t>(rowWords) * 2u * fRectHeight;
    const uint32_t locTableOffset = bitmapOffset + bitmapBytes;
    const uint32_t locTableBytes = (charCount + 1u) * 2u;
    const uint32_t owTableOffset = static_cast<uint32_t>(owTLoc) * 2u;
    const uint32_t owTableBytes = charCount * 2u;
    if (locTableOffset + locTableBytes > resource.size || owTableOffset < locTableOffset + locTableBytes || owTableOffset + owTableBytes > resource.size)
    {
        return false;
    }

    decoded.valid = true;
    decoded.resource = &resource;
    decoded.bytes = resource.bytes;
    decoded.size = resource.size;
    decoded.firstChar = firstChar;
    decoded.lastChar = lastChar;
    decoded.fRectHeight = fRectHeight;
    decoded.rowWords = rowWords;
    decoded.ascent = ascent;
    decoded.descent = descent;
    decoded.bitmapOffset = bitmapOffset;
    decoded.locTableOffset = locTableOffset;
    decoded.owTableOffset = owTableOffset;
    return true;
}

static int palmGeneratedFontScore(const PalmDecodedFont& decoded)
{
    int score = 0;
    if (decoded.firstChar <= 0x20u && decoded.lastChar >= 0x7eu)
    {
        score += 100;
    }
    if (decoded.fRectHeight >= 9u && decoded.fRectHeight <= 12u)
    {
        score += 40;
    }
    if (decoded.resource != nullptr && decoded.resource->sourceName != nullptr)
    {
        if (strstr(decoded.resource->sourceName, ""System"") != nullptr)
        {
            score += 80;
        }
        if (strstr(decoded.resource->sourceName, ""Latin"") != nullptr)
        {
            score += 30;
        }
    }
    return score;
}

static void selectPalmGeneratedFont()
{
    if (gPalmRealFontProbeDone)
    {
        return;
    }

    gPalmRealFontProbeDone = true;
    int bestScore = -1;
    for (size_t i = 0; i < kPalmGeneratedFontResourceCount; ++i)
    {
        PalmDecodedFont decoded{};
        if (decodePalmGeneratedFont(kPalmGeneratedFontResources[i], decoded))
        {
            const int score = palmGeneratedFontScore(decoded);
            if (!gPalmRealFont.valid || score > bestScore)
            {
                bestScore = score;
                gPalmRealFont = decoded;
            }
        }
    }

    if (gPalmRealFont.valid)
    {
        Serial.printf(""Palm NFNT font active: %s #%u size=%u chars=%u-%u height=%u rowWords=%u score=%d\n"",
            gPalmRealFont.resource->sourceName,
            static_cast<unsigned>(gPalmRealFont.resource->resourceId),
            static_cast<unsigned>(gPalmRealFont.size),
            static_cast<unsigned>(gPalmRealFont.firstChar),
            static_cast<unsigned>(gPalmRealFont.lastChar),
            static_cast<unsigned>(gPalmRealFont.fRectHeight),
            static_cast<unsigned>(gPalmRealFont.rowWords),
            bestScore);
    }
    else
    {
        Serial.printf(""Palm NFNT font fallback: generated resources=%u\n"", static_cast<unsigned>(kPalmGeneratedFontResourceCount));
    }
}

static uint8_t glyphRow(char ch, int row)
{
    switch (ch)
    {
        case 'A': return (const uint8_t[7]){0x0E,0x11,0x11,0x1F,0x11,0x11,0x11}[row];
        case 'B': return (const uint8_t[7]){0x1E,0x11,0x11,0x1E,0x11,0x11,0x1E}[row];
        case 'C': return (const uint8_t[7]){0x0F,0x10,0x10,0x10,0x10,0x10,0x0F}[row];
        case 'D': return (const uint8_t[7]){0x1E,0x11,0x11,0x11,0x11,0x11,0x1E}[row];
        case 'E': return (const uint8_t[7]){0x1F,0x10,0x10,0x1E,0x10,0x10,0x1F}[row];
        case 'F': return (const uint8_t[7]){0x1F,0x10,0x10,0x1E,0x10,0x10,0x10}[row];
        case 'G': return (const uint8_t[7]){0x0F,0x10,0x10,0x13,0x11,0x11,0x0F}[row];
        case 'H': return (const uint8_t[7]){0x11,0x11,0x11,0x1F,0x11,0x11,0x11}[row];
        case 'I': return (const uint8_t[7]){0x0E,0x04,0x04,0x04,0x04,0x04,0x0E}[row];
        case 'J': return (const uint8_t[7]){0x07,0x02,0x02,0x02,0x12,0x12,0x0C}[row];
        case 'K': return (const uint8_t[7]){0x11,0x12,0x14,0x18,0x14,0x12,0x11}[row];
        case 'L': return (const uint8_t[7]){0x10,0x10,0x10,0x10,0x10,0x10,0x1F}[row];
        case 'M': return (const uint8_t[7]){0x11,0x1B,0x15,0x15,0x11,0x11,0x11}[row];
        case 'N': return (const uint8_t[7]){0x11,0x19,0x15,0x13,0x11,0x11,0x11}[row];
        case 'O': return (const uint8_t[7]){0x0E,0x11,0x11,0x11,0x11,0x11,0x0E}[row];
        case 'P': return (const uint8_t[7]){0x1E,0x11,0x11,0x1E,0x10,0x10,0x10}[row];
        case 'Q': return (const uint8_t[7]){0x0E,0x11,0x11,0x11,0x15,0x12,0x0D}[row];
        case 'R': return (const uint8_t[7]){0x1E,0x11,0x11,0x1E,0x14,0x12,0x11}[row];
        case 'S': return (const uint8_t[7]){0x0F,0x10,0x10,0x0E,0x01,0x01,0x1E}[row];
        case 'T': return (const uint8_t[7]){0x1F,0x04,0x04,0x04,0x04,0x04,0x04}[row];
        case 'U': return (const uint8_t[7]){0x11,0x11,0x11,0x11,0x11,0x11,0x0E}[row];
        case 'V': return (const uint8_t[7]){0x11,0x11,0x11,0x11,0x11,0x0A,0x04}[row];
        case 'W': return (const uint8_t[7]){0x11,0x11,0x11,0x15,0x15,0x15,0x0A}[row];
        case 'X': return (const uint8_t[7]){0x11,0x11,0x0A,0x04,0x0A,0x11,0x11}[row];
        case 'Y': return (const uint8_t[7]){0x11,0x11,0x0A,0x04,0x04,0x04,0x04}[row];
        case 'Z': return (const uint8_t[7]){0x1F,0x01,0x02,0x04,0x08,0x10,0x1F}[row];
        case 'a': return (const uint8_t[7]){0x00,0x00,0x0E,0x01,0x0F,0x11,0x0F}[row];
        case 'b': return (const uint8_t[7]){0x10,0x10,0x1E,0x11,0x11,0x11,0x1E}[row];
        case 'c': return (const uint8_t[7]){0x00,0x00,0x0F,0x10,0x10,0x10,0x0F}[row];
        case 'd': return (const uint8_t[7]){0x01,0x01,0x0F,0x11,0x11,0x11,0x0F}[row];
        case 'e': return (const uint8_t[7]){0x00,0x00,0x0E,0x11,0x1F,0x10,0x0E}[row];
        case 'f': return (const uint8_t[7]){0x06,0x08,0x08,0x1E,0x08,0x08,0x08}[row];
        case 'g': return (const uint8_t[7]){0x00,0x00,0x0F,0x11,0x0F,0x01,0x0E}[row];
        case 'h': return (const uint8_t[7]){0x10,0x10,0x1E,0x11,0x11,0x11,0x11}[row];
        case 'i': return (const uint8_t[7]){0x04,0x00,0x0C,0x04,0x04,0x04,0x0E}[row];
        case 'j': return (const uint8_t[7]){0x02,0x00,0x06,0x02,0x02,0x12,0x0C}[row];
        case 'k': return (const uint8_t[7]){0x10,0x10,0x12,0x14,0x18,0x14,0x12}[row];
        case 'l': return (const uint8_t[7]){0x0C,0x04,0x04,0x04,0x04,0x04,0x0E}[row];
        case 'm': return (const uint8_t[7]){0x00,0x00,0x1A,0x15,0x15,0x15,0x15}[row];
        case 'n': return (const uint8_t[7]){0x00,0x00,0x1E,0x11,0x11,0x11,0x11}[row];
        case 'o': return (const uint8_t[7]){0x00,0x00,0x0E,0x11,0x11,0x11,0x0E}[row];
        case 'p': return (const uint8_t[7]){0x00,0x00,0x1E,0x11,0x1E,0x10,0x10}[row];
        case 'q': return (const uint8_t[7]){0x00,0x00,0x0F,0x11,0x0F,0x01,0x01}[row];
        case 'r': return (const uint8_t[7]){0x00,0x00,0x16,0x19,0x10,0x10,0x10}[row];
        case 's': return (const uint8_t[7]){0x00,0x00,0x0F,0x10,0x0E,0x01,0x1E}[row];
        case 't': return (const uint8_t[7]){0x04,0x04,0x1F,0x04,0x04,0x05,0x02}[row];
        case 'u': return (const uint8_t[7]){0x00,0x00,0x11,0x11,0x11,0x13,0x0D}[row];
        case 'v': return (const uint8_t[7]){0x00,0x00,0x11,0x11,0x11,0x0A,0x04}[row];
        case 'w': return (const uint8_t[7]){0x00,0x00,0x11,0x11,0x15,0x15,0x0A}[row];
        case 'x': return (const uint8_t[7]){0x00,0x00,0x11,0x0A,0x04,0x0A,0x11}[row];
        case 'y': return (const uint8_t[7]){0x00,0x00,0x11,0x11,0x0F,0x01,0x0E}[row];
        case 'z': return (const uint8_t[7]){0x00,0x00,0x1F,0x02,0x04,0x08,0x1F}[row];
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
        case '.': return (const uint8_t[7]){0x00,0x00,0x00,0x00,0x00,0x0C,0x0C}[row];
        case ',': return (const uint8_t[7]){0x00,0x00,0x00,0x00,0x00,0x0C,0x04}[row];
        case '-': return (const uint8_t[7]){0x00,0x00,0x00,0x1F,0x00,0x00,0x00}[row];
        case '_': return (const uint8_t[7]){0x00,0x00,0x00,0x00,0x00,0x00,0x1F}[row];
        case '/': return (const uint8_t[7]){0x01,0x01,0x02,0x04,0x08,0x10,0x10}[row];
        case '!': return (const uint8_t[7]){0x04,0x04,0x04,0x04,0x04,0x00,0x04}[row];
        case '?': return (const uint8_t[7]){0x0E,0x11,0x01,0x02,0x04,0x00,0x04}[row];
        case '(': return (const uint8_t[7]){0x02,0x04,0x08,0x08,0x08,0x04,0x02}[row];
        case ')': return (const uint8_t[7]){0x08,0x04,0x02,0x02,0x02,0x04,0x08}[row];
        case '\'': return (const uint8_t[7]){0x04,0x04,0x08,0x00,0x00,0x00,0x00}[row];
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

static void drawPalmRaisedFrame(int x, int y, int w, int h, uint16_t face, uint16_t ink, uint16_t mid)
{
    if (w <= 2 || h <= 2)
    {
        return;
    }

    fillPalmRect(x, y, w, h, face);
    drawPalmRectOutline(x, y, w, h, ink);
    fillPalmRect(x + 1, y + 1, w - 2, 1, 0xFFFFu);
    fillPalmRect(x + 1, y + 1, 1, h - 2, 0xFFFFu);
    fillPalmRect(x + 1, y + h - 2, w - 2, 1, mid);
    fillPalmRect(x + w - 2, y + 1, 1, h - 2, mid);
}

static void drawPalmInsetFrame(int x, int y, int w, int h, uint16_t face, uint16_t ink, uint16_t mid)
{
    if (w <= 2 || h <= 2)
    {
        return;
    }

    fillPalmRect(x, y, w, h, face);
    drawPalmRectOutline(x, y, w, h, ink);
    fillPalmRect(x + 1, y + 1, w - 2, 1, mid);
    fillPalmRect(x + 1, y + 1, 1, h - 2, mid);
    fillPalmRect(x + 1, y + h - 2, w - 2, 1, 0xFFFFu);
    fillPalmRect(x + w - 2, y + 1, 1, h - 2, 0xFFFFu);
}

static void drawPalmPopupArrow(int x, int y, uint16_t color)
{
    fillPalmRect(x, y, 5, 1, color);
    fillPalmRect(x + 1, y + 1, 3, 1, color);
    fillPalmRect(x + 2, y + 2, 1, 1, color);
}

static bool palmRealFontContains(char ch)
{
    if (!gPalmRealFont.valid)
    {
        return false;
    }

    const uint8_t code = static_cast<uint8_t>(ch);
    return code >= gPalmRealFont.firstChar && code <= gPalmRealFont.lastChar;
}

static int palmRealCharWidth(char ch)
{
    if (!palmRealFontContains(ch))
    {
        return 0;
    }

    const uint32_t index = static_cast<uint32_t>(static_cast<uint8_t>(ch)) - gPalmRealFont.firstChar;
    const uint16_t left = readPalmFontU16(gPalmRealFont.bytes, gPalmRealFont.locTableOffset + index * 2u);
    const uint16_t right = readPalmFontU16(gPalmRealFont.bytes, gPalmRealFont.locTableOffset + (index + 1u) * 2u);
    uint16_t width = right > left ? static_cast<uint16_t>(right - left) : 0u;
    const uint16_t ow = readPalmFontU16(gPalmRealFont.bytes, gPalmRealFont.owTableOffset + index * 2u);
    const uint16_t tableWidth = ow & 0x00ffu;
    if (tableWidth > 0u && tableWidth <= 24u)
    {
        width = tableWidth;
    }
    if (width == 0u || width > 24u)
    {
        width = 5u;
    }
    return static_cast<int>(width) + 1;
}

static bool drawPalmRealCharClipped(int x, int y, char ch, uint16_t color, int scale, int clipLeft, int clipRight)
{
    if (!palmRealFontContains(ch))
    {
        return false;
    }

    const uint32_t index = static_cast<uint32_t>(static_cast<uint8_t>(ch)) - gPalmRealFont.firstChar;
    const uint16_t left = readPalmFontU16(gPalmRealFont.bytes, gPalmRealFont.locTableOffset + index * 2u);
    const uint16_t right = readPalmFontU16(gPalmRealFont.bytes, gPalmRealFont.locTableOffset + (index + 1u) * 2u);
    if (right <= left || right - left > 32u)
    {
        return false;
    }

    const uint32_t rowBytes = static_cast<uint32_t>(gPalmRealFont.rowWords) * 2u;
    if ((static_cast<uint32_t>(right) + 7u) / 8u > rowBytes)
    {
        return false;
    }

    const int baselineAdjust = gPalmRealFont.fRectHeight > 9u ? -1 : 0;
    for (uint16_t gy = 0; gy < gPalmRealFont.fRectHeight && gy < 12u; ++gy)
    {
        const uint32_t rowOffset = gPalmRealFont.bitmapOffset + static_cast<uint32_t>(gy) * rowBytes;
        for (uint16_t gx = 0; gx < right - left; ++gx)
        {
            const uint32_t bitIndex = static_cast<uint32_t>(left) + gx;
            const uint32_t packedOffset = rowOffset + (bitIndex >> 3);
            if (packedOffset >= gPalmRealFont.bitmapOffset + rowBytes * gPalmRealFont.fRectHeight)
            {
                continue;
            }

            const uint8_t packed = gPalmRealFont.bytes[packedOffset];
            if ((packed & (0x80u >> (bitIndex & 7u))) != 0u)
            {
                const int px = x + static_cast<int>(gx) * scale;
                if (px >= clipLeft && px < clipRight)
                {
                    fillPalmRect(px, y + baselineAdjust + static_cast<int>(gy) * scale, scale, scale, color);
                }
            }
        }
    }
    return true;
}

static int palmCharAdvance(char ch)
{
    const int realWidth = palmRealCharWidth(ch);
    if (realWidth > 0)
    {
        return realWidth;
    }

    switch (ch)
    {
        case ' ':
            return 4;
        case 'i':
        case 'l':
        case 'I':
        case ':':
            return 3;
        case 'f':
        case 'j':
        case 'r':
        case 't':
            return 4;
        case 'm':
        case 'w':
        case 'M':
        case 'W':
            return 6;
        default:
            return 6;
    }
}

static int palmTextWidth(const char* text)
{
    if (text == nullptr)
    {
        return 0;
    }

    int width = 0;
    for (int i = 0; text[i] != '\0' && i < 28; ++i)
    {
        width += palmCharAdvance(text[i]);
    }
    return width;
}

static void drawPalmTextClipped(int x, int y, const char* text, uint16_t color, int scale, int maxWidth)
{
    if (text == nullptr || maxWidth <= 0)
    {
        return;
    }

    int cursor = x;
    const int clipRight = x + maxWidth;
    for (int i = 0; text[i] != '\0' && i < 40; ++i)
    {
        const char ch = text[i];
        const int advance = palmCharAdvance(ch) * scale;
        if (cursor >= clipRight)
        {
            break;
        }

        if (!drawPalmRealCharClipped(cursor, y, ch, color, scale, x, clipRight))
        {
            for (int gy = 0; gy < 7; ++gy)
            {
                const uint8_t bits = glyphRow(ch, gy);
                for (int gx = 0; gx < 5; ++gx)
                {
                    if ((bits & (1u << (4 - gx))) != 0)
                    {
                        const int px = cursor + gx * scale;
                        if (px >= x && px < clipRight)
                        {
                            fillPalmRect(px, y + gy * scale, scale, scale, color);
                        }
                    }
                }
            }
        }
        cursor += advance;
    }
}

static void drawPalmText(int x, int y, const char* text, uint16_t color, int scale)
{
    drawPalmTextClipped(x, y, text, color, scale, kPalmLcdW - x);
}

static void drawPalmButton(int x, int y, int w, int h, const char* text, bool pressed, uint16_t bg, uint16_t ink, uint16_t mid)
{
    const uint16_t face = pressed ? mid : bg;
    if (pressed)
    {
        drawPalmInsetFrame(x, y, w, h, face, ink, mid);
    }
    else
    {
        drawPalmRaisedFrame(x, y, w, h, face, ink, mid);
    }

    const int textW = palmTextWidth(text);
    const int textX = x + (w - textW) / 2 + (pressed ? 1 : 0);
    const int textY = y + (h <= 13 ? 3 : 4) + (pressed ? 1 : 0);
    drawPalmTextClipped(textX, textY, text, ink, 1, w - 5);
}

static void drawPalmPopupSelector(int x, int y, int w, int h, const char* text, uint16_t bg, uint16_t ink, uint16_t mid)
{
    drawPalmRaisedFrame(x, y, w, h, bg, ink, mid);
    drawPalmTextClipped(x + 4, y + 3, text, ink, 1, w - 15);
    drawPalmPopupArrow(x + w - 9, y + 5, ink);
}

static void drawPalmModalFrame(int x, int y, int w, int h, const char* title, uint16_t bg, uint16_t ink, uint16_t mid, uint16_t shadow)
{
    fillPalmRect(x + 2, y + 2, w, h, shadow);
    fillPalmRect(x, y, w, h, bg);
    drawPalmRectOutline(x, y, w, h, ink);
    drawPalmRectOutline(x + 1, y + 1, w - 2, h - 2, mid);
    fillPalmRect(x + 2, y + 2, w - 4, 14, ink);
    drawPalmTextClipped(x + 7, y + 5, title, bg, 1, w - 14);
}

static void drawPalmScrollArrow(int x, int y, bool down, uint16_t ink)
{
    if (down)
    {
        fillPalmRect(x, y, 7, 1, ink);
        fillPalmRect(x + 1, y + 1, 5, 1, ink);
        fillPalmRect(x + 2, y + 2, 3, 1, ink);
        fillPalmRect(x + 3, y + 3, 1, 1, ink);
    }
    else
    {
        fillPalmRect(x + 3, y, 1, 1, ink);
        fillPalmRect(x + 2, y + 1, 3, 1, ink);
        fillPalmRect(x + 1, y + 2, 5, 1, ink);
        fillPalmRect(x, y + 3, 7, 1, ink);
    }
}

static void drawPalmScrollbar(int x, int y, int h, uint16_t count, int16_t topRow, uint16_t bg, uint16_t ink, uint16_t mid)
{
    drawPalmInsetFrame(x, y, 9, h, bg, ink, mid);
    drawPalmRaisedFrame(x + 1, y + 1, 7, 10, bg, ink, mid);
    drawPalmRaisedFrame(x + 1, y + h - 11, 7, 10, bg, ink, mid);
    drawPalmScrollArrow(x + 1, y + 4, false, ink);
    drawPalmScrollArrow(x + 1, y + h - 8, true, ink);

    const int trackY = y + 12;
    const int trackH = h - 24;
    drawPalmInsetFrame(x + 2, trackY, 5, trackH, 0xFFFFu, mid, mid);

    const int visibleRows = 5;
    const int maxTop = count > visibleRows ? static_cast<int>(count) - visibleRows : 0;
    const int thumbTravel = trackH - 14;
    const int thumbY = trackY + 1 + (maxTop > 0 ? (thumbTravel * static_cast<int>(topRow)) / maxTop : 0);
    const int thumbH = count > visibleRows ? 13 : trackH - 2;
    drawPalmRaisedFrame(x + 2, thumbY, 5, thumbH, bg, ink, mid);
}

static void presentPalmRawSurface()
{
    ++gDisplayGeneration;
    if (gPanel != nullptr && gFrameBuffer != nullptr)
    {
        ESP_ERROR_CHECK(esp_lcd_panel_draw_bitmap(gPanel, 0, 0, kScreenW, kScreenH, gFrameBuffer));
        esp_lcd_panel_disp_on_off(gPanel, true);
    }
}

static uint16_t palmWinColor(uint8_t mode)
{
    switch (mode)
    {
        case 1u: return 0xFFFFu;
        case 2u: return 0x8410u;
        case 3u: return 0xA514u;
        default: return 0x0000u;
    }
}

static void drawPalmLine(int x1, int y1, int x2, int y2, uint16_t color)
{
    int dx = abs(x2 - x1);
    int sx = x1 < x2 ? 1 : -1;
    int dy = -abs(y2 - y1);
    int sy = y1 < y2 ? 1 : -1;
    int err = dx + dy;
    while (true)
    {
        drawPalmPixel(x1, y1, color);
        if (x1 == x2 && y1 == y2)
        {
            break;
        }
        const int e2 = 2 * err;
        if (e2 >= dy)
        {
            err += dy;
            x1 += sx;
        }
        if (e2 <= dx)
        {
            err += dx;
            y1 += sy;
        }
    }
}

void palmDisplayWinEraseWindow(uint16_t selector, uint32_t trapCount)
{
    if (!palmDisplayBegin())
    {
        return;
    }
    fillPalmRect(0, 0, kPalmLcdW, kPalmLcdH, 0xFFFFu);
    presentPalmRawSurface();
    Serial.printf(""LCD WinEraseWindow selector=0x%04X traps=%u generation=%u\n"",
        static_cast<unsigned>(selector),
        static_cast<unsigned>(trapCount),
        static_cast<unsigned>(gDisplayGeneration));
}

void palmDisplayWinDrawChars(uint16_t selector, uint32_t trapCount, const char* text, int16_t len, int16_t x, int16_t y, uint8_t mode)
{
    if (!palmDisplayBegin() || text == nullptr)
    {
        return;
    }
    char buffer[64];
    const int16_t count = len < 0 ? 0 : (len > static_cast<int16_t>(sizeof(buffer) - 1) ? static_cast<int16_t>(sizeof(buffer) - 1) : len);
    for (int16_t i = 0; i < count; ++i)
    {
        buffer[i] = text[i];
    }
    buffer[count] = '\0';
    if (mode == 1u)
    {
        fillPalmRect(x, y, count * 6, 8, 0xFFFFu);
    }
    drawPalmText(x, y, buffer, palmWinColor(mode), 1);
    presentPalmRawSurface();
    Serial.printf(""LCD WinDrawChars selector=0x%04X traps=%u x=%d y=%d mode=%u text='%s' generation=%u\n"",
        static_cast<unsigned>(selector),
        static_cast<unsigned>(trapCount),
        static_cast<int>(x),
        static_cast<int>(y),
        static_cast<unsigned>(mode),
        buffer,
        static_cast<unsigned>(gDisplayGeneration));
}

void palmDisplayWinDrawLine(uint16_t selector, uint32_t trapCount, int16_t x1, int16_t y1, int16_t x2, int16_t y2, uint8_t mode)
{
    if (!palmDisplayBegin())
    {
        return;
    }
    drawPalmLine(x1, y1, x2, y2, palmWinColor(mode));
    presentPalmRawSurface();
    Serial.printf(""LCD WinDrawLine selector=0x%04X traps=%u (%d,%d)-(%d,%d) mode=%u generation=%u\n"",
        static_cast<unsigned>(selector),
        static_cast<unsigned>(trapCount),
        static_cast<int>(x1),
        static_cast<int>(y1),
        static_cast<int>(x2),
        static_cast<int>(y2),
        static_cast<unsigned>(mode),
        static_cast<unsigned>(gDisplayGeneration));
}

void palmDisplayWinDrawRectangle(uint16_t selector, uint32_t trapCount, int16_t x, int16_t y, int16_t w, int16_t h, uint8_t mode)
{
    if (!palmDisplayBegin())
    {
        return;
    }
    fillPalmRect(x, y, w, h, palmWinColor(mode));
    presentPalmRawSurface();
    Serial.printf(""LCD WinDrawRectangle selector=0x%04X traps=%u x=%d y=%d w=%d h=%d mode=%u generation=%u\n"",
        static_cast<unsigned>(selector),
        static_cast<unsigned>(trapCount),
        static_cast<int>(x),
        static_cast<int>(y),
        static_cast<int>(w),
        static_cast<int>(h),
        static_cast<unsigned>(mode),
        static_cast<unsigned>(gDisplayGeneration));
}

void palmDisplayWinDrawRectangleFrame(uint16_t selector, uint32_t trapCount, int16_t x, int16_t y, int16_t w, int16_t h, uint8_t mode)
{
    if (!palmDisplayBegin())
    {
        return;
    }
    drawPalmRectOutline(x, y, w, h, palmWinColor(mode));
    presentPalmRawSurface();
    Serial.printf(""LCD WinDrawRectangleFrame selector=0x%04X traps=%u x=%d y=%d w=%d h=%d mode=%u generation=%u\n"",
        static_cast<unsigned>(selector),
        static_cast<unsigned>(trapCount),
        static_cast<int>(x),
        static_cast<int>(y),
        static_cast<int>(w),
        static_cast<int>(h),
        static_cast<unsigned>(mode),
        static_cast<unsigned>(gDisplayGeneration));
}

static uint16_t readBitmapU16BE(const uint8_t* bytes, uint32_t offset)
{
    return static_cast<uint16_t>((static_cast<uint16_t>(bytes[offset]) << 8) | bytes[offset + 1u]);
}

bool palmDisplayWinDrawBitmapResource(uint16_t selector, uint32_t trapCount, const uint8_t* bytes, uint32_t size, int16_t x, int16_t y)
{
    if (!palmDisplayBegin() || bytes == nullptr || size < 10u)
    {
        return false;
    }

    const uint16_t width = readBitmapU16BE(bytes, 0u);
    const uint16_t height = readBitmapU16BE(bytes, 2u);
    const uint16_t rowBytes = readBitmapU16BE(bytes, 4u);
    const uint16_t flags = readBitmapU16BE(bytes, 6u);
    const uint8_t pixelSize = bytes[8];
    const uint8_t version = bytes[9];
    const bool compressed = (flags & 0x8000u) != 0u;
    if (width == 0 || height == 0 || width > 160u || height > 160u || rowBytes == 0 || compressed || pixelSize != 1u)
    {
        Serial.printf(""LCD WinDrawBitmap unsupported selector=0x%04X traps=%u w=%u h=%u rowBytes=%u flags=0x%04X pixelSize=%u version=%u size=%u\n"",
            static_cast<unsigned>(selector),
            static_cast<unsigned>(trapCount),
            static_cast<unsigned>(width),
            static_cast<unsigned>(height),
            static_cast<unsigned>(rowBytes),
            static_cast<unsigned>(flags),
            static_cast<unsigned>(pixelSize),
            static_cast<unsigned>(version),
            static_cast<unsigned>(size));
        return false;
    }

    const uint32_t dataOffset = version == 0u ? 8u : 16u;
    if (dataOffset + static_cast<uint32_t>(rowBytes) * height > size)
    {
        return false;
    }

    for (uint16_t yy = 0; yy < height; ++yy)
    {
        const uint32_t rowOffset = dataOffset + static_cast<uint32_t>(yy) * rowBytes;
        for (uint16_t xx = 0; xx < width; ++xx)
        {
            const uint8_t packed = bytes[rowOffset + (xx >> 3)];
            const bool ink = (packed & (0x80u >> (xx & 7u))) != 0u;
            if (ink)
            {
                drawPalmPixel(x + static_cast<int16_t>(xx), y + static_cast<int16_t>(yy), 0x0000u);
            }
        }
    }

    presentPalmRawSurface();
    Serial.printf(""LCD WinDrawBitmap selector=0x%04X traps=%u x=%d y=%d w=%u h=%u rowBytes=%u generation=%u\n"",
        static_cast<unsigned>(selector),
        static_cast<unsigned>(trapCount),
        static_cast<int>(x),
        static_cast<int>(y),
        static_cast<unsigned>(width),
        static_cast<unsigned>(height),
        static_cast<unsigned>(rowBytes),
        static_cast<unsigned>(gDisplayGeneration));
    return true;
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

static void seedFakeMemoRecords()
{
    if (gFakeMemoRecordsSeeded)
    {
        return;
    }

    const char* defaults[] =
    {
        ""Hello from ESP32"",
        ""Second memo row"",
        ""Palm UI drawing"",
        ""Third memo row"",
        ""Note four"",
        ""Note five"",
        ""Note six""
    };

    const uint16_t defaultCount = static_cast<uint16_t>(sizeof(defaults) / sizeof(defaults[0]));
    gFakeMemoRecordCount = defaultCount > kMaxFakeMemoRecords ? kMaxFakeMemoRecords : defaultCount;
    for (uint16_t i = 0; i < gFakeMemoRecordCount; ++i)
    {
        copyPalmUiText(gFakeMemoRecords[i], sizeof(gFakeMemoRecords[i]), defaults[i]);
    }

    gFakeMemoRecordsSeeded = true;
}

static void publishStoredMemoRowsToUi()
{
    seedFakeMemoRecords();
    gPalmUiListCount = gFakeMemoRecordCount;
    const int16_t visibleRows = 5;
    int16_t maxTop = static_cast<int16_t>(gFakeMemoRecordCount) - visibleRows;
    if (maxTop < 0)
    {
        maxTop = 0;
    }
    if (gPalmUiTopRow < 0)
    {
        gPalmUiTopRow = 0;
    }
    if (gPalmUiTopRow > maxTop)
    {
        gPalmUiTopRow = maxTop;
    }

    for (uint16_t row = 0; row < visibleRows; ++row)
    {
        const uint16_t recordIndex = static_cast<uint16_t>(gPalmUiTopRow + static_cast<int16_t>(row));
        if (recordIndex < gFakeMemoRecordCount)
        {
            copyPalmUiText(gPalmUiRows[row], sizeof(gPalmUiRows[row]), gFakeMemoRecords[recordIndex]);
        }
        else
        {
            gPalmUiRows[row][0] = '\0';
        }
    }
}

static void storeMemoText(uint16_t index, const char* text)
{
    if (index >= kMaxFakeMemoRecords)
    {
        return;
    }

    gFakeMemoRecords[index][0] = '\0';
    if (text != nullptr && text[0] != '\0')
    {
        copyPalmUiText(gFakeMemoRecords[index], sizeof(gFakeMemoRecords[index]), text);
    }
}

bool palmDisplayInsertMemoRecord(uint16_t index, const char* text)
{
    seedFakeMemoRecords();
    if (gFakeMemoRecordCount >= kMaxFakeMemoRecords)
    {
        return false;
    }

    if (index > gFakeMemoRecordCount)
    {
        index = gFakeMemoRecordCount;
    }

    for (int i = static_cast<int>(gFakeMemoRecordCount); i > static_cast<int>(index); --i)
    {
        copyPalmUiText(gFakeMemoRecords[i], sizeof(gFakeMemoRecords[i]), gFakeMemoRecords[i - 1]);
    }

    storeMemoText(index, text);
    ++gFakeMemoRecordCount;
    publishStoredMemoRowsToUi();
    return true;
}

bool palmDisplayUpdateMemoRecord(uint16_t index, const char* text)
{
    seedFakeMemoRecords();
    if (index >= gFakeMemoRecordCount)
    {
        return false;
    }

    storeMemoText(index, text);
    publishStoredMemoRowsToUi();
    if (gPalmUiSelectedRow == static_cast<int16_t>(index))
    {
        copyPalmUiText(gPalmUiMemo, sizeof(gPalmUiMemo), gFakeMemoRecords[index]);
    }
    return true;
}

bool palmDisplayRemoveMemoRecord(uint16_t index)
{
    seedFakeMemoRecords();
    if (index >= gFakeMemoRecordCount)
    {
        return false;
    }

    for (uint16_t i = index; i + 1u < gFakeMemoRecordCount; ++i)
    {
        copyPalmUiText(gFakeMemoRecords[i], sizeof(gFakeMemoRecords[i]), gFakeMemoRecords[i + 1u]);
    }
    --gFakeMemoRecordCount;
    gFakeMemoRecords[gFakeMemoRecordCount][0] = '\0';
    publishStoredMemoRowsToUi();
    if (gPalmUiSelectedRow >= static_cast<int16_t>(gFakeMemoRecordCount))
    {
        gPalmUiSelectedRow = gFakeMemoRecordCount == 0 ? -1 : static_cast<int16_t>(gFakeMemoRecordCount - 1u);
    }
    palmDisplayMemoListMakeItemVisible(gPalmUiSelectedRow);
    return true;
}

static bool prependFakeMemoRecord(const char* text)
{
    return palmDisplayInsertMemoRecord(0, text);
}

uint16_t palmDisplayMemoRecordCount()
{
    seedFakeMemoRecords();
    return gFakeMemoRecordCount;
}

const char* palmDisplayMemoText(uint16_t index)
{
    seedFakeMemoRecords();
    if (index >= gFakeMemoRecordCount)
    {
        return """";
    }

    return gFakeMemoRecords[index];
}

int16_t palmDisplayMemoListSelection()
{
    seedFakeMemoRecords();
    return gPalmUiSelectedRow;
}

int16_t palmDisplayMemoListTopItem()
{
    seedFakeMemoRecords();
    publishStoredMemoRowsToUi();
    return gPalmUiTopRow;
}

void palmDisplayMemoListSetTopItem(int16_t index)
{
    seedFakeMemoRecords();
    gPalmUiTopRow = index;
    publishStoredMemoRowsToUi();
}

void palmDisplayMemoListMakeItemVisible(int16_t index)
{
    seedFakeMemoRecords();
    if (index < 0 || index >= static_cast<int16_t>(gFakeMemoRecordCount))
    {
        publishStoredMemoRowsToUi();
        return;
    }

    if (index < gPalmUiTopRow)
    {
        gPalmUiTopRow = index;
    }
    else if (index >= gPalmUiTopRow + 5)
    {
        gPalmUiTopRow = static_cast<int16_t>(index - 4);
    }
    publishStoredMemoRowsToUi();
}

void palmDisplayMemoListSetSelection(int16_t index)
{
    seedFakeMemoRecords();
    if (index >= 0 && index < static_cast<int16_t>(gFakeMemoRecordCount))
    {
        gPalmUiSelectedRow = index;
        palmDisplayMemoListMakeItemVisible(index);
        copyPalmUiText(gPalmUiMemo, sizeof(gPalmUiMemo), gFakeMemoRecords[index]);
    }
    else
    {
        gPalmUiSelectedRow = -1;
        gPalmUiMemo[0] = '\0';
    }
    publishStoredMemoRowsToUi();
}

void palmDisplayMemoListDraw(uint16_t selector, uint32_t trapCount)
{
    seedFakeMemoRecords();
    if (!palmDisplayBegin())
    {
        return;
    }

    if (!gPalmUiSurfaceStarted)
    {
        resetPalmUiSurface(""Memo Pad"");
    }
    publishStoredMemoRowsToUi();
    presentPalmUiSurface(selector, trapCount);
}

static void openDetailsModal()
{
    gPalmModalVisible = true;
    gPalmModalPressedControl = 0;
    gPalmUiPressedControl = 0;
    copyPalmUiText(gPalmModalTitle, sizeof(gPalmModalTitle), ""Details"");
    if (gPalmUiSelectedRow >= 0 && gPalmUiSelectedRow < static_cast<int16_t>(gFakeMemoRecordCount))
    {
        copyPalmUiText(gPalmModalLine1, sizeof(gPalmModalLine1), gFakeMemoRecords[gPalmUiSelectedRow]);
    }
    else
    {
        copyPalmUiText(gPalmModalLine1, sizeof(gPalmModalLine1), ""Memo details"");
    }
    copyPalmUiText(gPalmModalLine2, sizeof(gPalmModalLine2), ""Category: Unfiled"");
    copyPalmUiText(gPalmModalOkButton, sizeof(gPalmModalOkButton), ""OK"");
}

static void closeModal()
{
    gPalmModalVisible = false;
    gPalmModalPressedControl = 0;
    gPalmUiPressedControl = 0;
}

struct PalmTapTarget
{
    uint16_t controlID;
    int16_t row;
};

static PalmTapTarget classifyPalmTap(int16_t x, int16_t y)
{
    PalmTapTarget target{};
    target.controlID = 0;
    target.row = -1;

    if (gPalmUiMode == kPalmUiModeEdit)
    {
        const PalmUiObjectBounds doneButton = palmUiObjectBounds(kMemoDoneButtonId);
        const PalmUiObjectBounds cancelButton = palmUiObjectBounds(kMemoCancelButtonId);
        if (inRect(x, y, doneButton.x, doneButton.y, doneButton.w, doneButton.h))
        {
            target.controlID = kMemoDoneButtonId;
        }
        else if (inRect(x, y, cancelButton.x, cancelButton.y, cancelButton.w, cancelButton.h))
        {
            target.controlID = kMemoCancelButtonId;
        }
        return target;
    }

    const PalmUiObjectBounds newButton = palmUiObjectBounds(kMemoNewButtonId);
    const PalmUiObjectBounds detailsButton = palmUiObjectBounds(kMemoDetailsButtonId);
    const PalmUiObjectBounds listBounds = palmUiObjectBounds(kMemoListId);
    if (inRect(x, y, newButton.x, newButton.y, newButton.w, newButton.h))
    {
        target.controlID = kMemoNewButtonId;
    }
    else if (inRect(x, y, detailsButton.x, detailsButton.y, detailsButton.w, detailsButton.h))
    {
        target.controlID = kMemoDetailsButtonId;
    }
    else if (inRect(x, y, listBounds.x, listBounds.y, listBounds.w, listBounds.h))
    {
        const int16_t row = static_cast<int16_t>((y - listBounds.y) / 18);
        const int16_t recordIndex = static_cast<int16_t>(gPalmUiTopRow + row);
        if (row >= 0 && row < 5 && recordIndex < static_cast<int16_t>(gPalmUiListCount) && gPalmUiRows[row][0] != '\0')
        {
            target.row = recordIndex;
        }
    }

    return target;
}

static bool enqueuePalmEvent(const PalmQueuedEvent& event)
{
    const uint8_t nextTail = static_cast<uint8_t>((gPalmEventQueueTail + 1u) % kPalmEventQueueSize);
    if (nextTail == gPalmEventQueueHead)
    {
        return false;
    }

    gPalmEventQueue[gPalmEventQueueTail] = event;
    gPalmEventQueueTail = nextTail;
    return true;
}

static bool queuePalmTap(int16_t x, int16_t y)
{
    PalmQueuedEvent event{};
    event.penDown = false;
    event.tapCount = 1;
    event.screenX = x;
    event.screenY = y;

    const PalmUiObjectBounds okButton = palmUiObjectBounds(kModalOkButtonId);
    if (gPalmModalVisible && inRect(x, y, okButton.x, okButton.y, okButton.w, okButton.h))
    {
        event.eType = kPalmCtlSelectEvent;
        event.controlID = kModalOkButtonId;
        event.on = true;
        return enqueuePalmEvent(event);
    }

    const PalmTapTarget target = classifyPalmTap(x, y);
    if (target.controlID != 0)
    {
        event.eType = kPalmCtlSelectEvent;
        event.controlID = target.controlID;
        event.on = true;
    }
    else if (target.row >= 0)
    {
        event.eType = kPalmLstSelectEvent;
        event.listID = kMemoListId;
        event.selection = target.row;
    }
    else
    {
        event.eType = kPalmPenUpEvent;
    }

    return enqueuePalmEvent(event);
}

bool palmDisplayHasPalmEvent()
{
    return gPalmEventQueueHead != gPalmEventQueueTail;
}

bool palmDisplayDequeuePalmEvent(PalmQueuedEvent* event)
{
    if (event == nullptr || gPalmEventQueueHead == gPalmEventQueueTail)
    {
        return false;
    }

    *event = gPalmEventQueue[gPalmEventQueueHead];
    gPalmEventQueueHead = static_cast<uint8_t>((gPalmEventQueueHead + 1u) % kPalmEventQueueSize);
    return true;
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
    gPalmUiSelectedRow = -1;
    gPalmUiTopRow = 0;
    gPalmUiPressedControl = 0;
    gPalmUiMode = kPalmUiModeList;
    gPalmEditText[0] = '\0';
    gPalmUiCategoryVisible = false;
    gPalmModalVisible = false;
    gPalmModalPressedControl = 0;
    gPalmModalTitle[0] = '\0';
    gPalmModalLine1[0] = '\0';
    gPalmModalLine2[0] = '\0';
    copyPalmUiText(gPalmModalOkButton, sizeof(gPalmModalOkButton), ""OK"");
    publishStoredMemoRowsToUi();
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

void palmDisplayPalmUiSetObjectBounds(uint16_t objectId, int16_t x, int16_t y, int16_t w, int16_t h)
{
    if (w <= 0 || h <= 0)
    {
        return;
    }

    PalmUiObjectBounds* object = palmUiFindObject(objectId);
    if (object == nullptr)
    {
        for (uint8_t i = 0; i < kPalmUiObjectCapacity; ++i)
        {
            if (!gPalmUiObjects[i].used)
            {
                object = &gPalmUiObjects[i];
                object->used = true;
                object->objectId = objectId;
                break;
            }
        }
    }

    if (object == nullptr)
    {
        return;
    }

    object->x = x;
    object->y = y;
    object->w = w;
    object->h = h;
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
    drawPalmTextClipped(9, 8, gPalmUiTitle, lcdBg, 1, gPalmUiCategoryVisible ? 92 : 138);

    if (gPalmUiCategoryVisible)
    {
        drawPalmPopupSelector(105, 6, 48, 12, gPalmUiCategory, lcdBg, lcdInk, line);
    }

    if (gPalmUiMode == kPalmUiModeEdit)
    {
        const PalmUiObjectBounds doneButton = palmUiObjectBounds(kMemoDoneButtonId);
        const PalmUiObjectBounds cancelButton = palmUiObjectBounds(kMemoCancelButtonId);
        drawPalmTextClipped(10, 29, ""New Memo"", lcdInk, 1, 120);
        drawPalmInsetFrame(8, 42, 144, 86, lcdBg, lcdInk, line);
        drawPalmTextClipped(12, 49, gPalmEditText[0] != '\0' ? gPalmEditText : ""_"", lcdInk, 1, 136);
        fillPalmRect(4, 135, 152, 1, line);
        fillPalmRect(4, 136, 152, 1, lcdInk);
        drawPalmButton(doneButton.x, doneButton.y, doneButton.w, doneButton.h, ""Done"", gPalmUiPressedControl == kMemoDoneButtonId, chromeLight, lcdInk, line);
        drawPalmButton(cancelButton.x, cancelButton.y, cancelButton.w, cancelButton.h, ""Cancel"", gPalmUiPressedControl == kMemoCancelButtonId, chromeLight, lcdInk, line);
        if (gPalmModalVisible)
        {
            const PalmUiObjectBounds okButton = palmUiObjectBounds(kModalOkButtonId);
            drawPalmModalFrame(18, 38, 124, 83, gPalmModalTitle, lcdBg, lcdInk, line, shadow);
            drawPalmTextClipped(27, 62, gPalmModalLine1, lcdInk, 1, 104);
            drawPalmTextClipped(27, 78, gPalmModalLine2, lcdInk, 1, 104);
            drawPalmButton(okButton.x, okButton.y, okButton.w, okButton.h, gPalmModalOkButton, gPalmModalPressedControl == kModalOkButtonId, chromeLight, lcdInk, line);
        }
        return;
    }

    const int visibleRows = 5;
    const PalmUiObjectBounds listBounds = palmUiObjectBounds(kMemoListId);
    drawPalmInsetFrame(listBounds.x, listBounds.y, listBounds.w, listBounds.h, lcdBg, lcdInk, line);
    drawPalmScrollbar(listBounds.x + listBounds.w + 1, listBounds.y, listBounds.h + 1, gPalmUiListCount, gPalmUiTopRow, chromeLight, lcdInk, line);

    for (int y = listBounds.y + 18; y < listBounds.y + listBounds.h - 1; y += 18)
    {
        fillPalmRect(listBounds.x + 2, y, listBounds.w - 4, 1, line);
    }

    if (gPalmUiListCount == 0 && gPalmUiRows[0][0] == '\0' && gPalmUiMemo[0] == '\0')
    {
        drawPalmTextClipped(listBounds.x + 5, listBounds.y + 5, ""No Memos"", line, 1, listBounds.w - 10);
    }

    for (int row = 0; row < visibleRows; ++row)
    {
        if (gPalmUiRows[row][0] != '\0')
        {
            const int16_t recordIndex = static_cast<int16_t>(gPalmUiTopRow + row);
            const bool selected = gPalmUiSelectedRow == recordIndex;
            if (selected)
            {
                fillPalmRect(listBounds.x + 2, listBounds.y + 3 + row * 18, listBounds.w - 4, 13, lcdInk);
            }
            drawPalmTextClipped(listBounds.x + 5, listBounds.y + 5 + row * 18, gPalmUiRows[row], selected ? lcdBg : lcdInk, 1, listBounds.w - 10);
        }
    }

    if (gPalmUiRows[0][0] == '\0' && gPalmUiMemo[0] != '\0')
    {
        drawPalmTextClipped(listBounds.x + 5, listBounds.y + 5, gPalmUiMemo, lcdInk, 1, listBounds.w - 10);
    }

    fillPalmRect(4, 135, 152, 1, line);
    fillPalmRect(4, 136, 152, 1, lcdInk);
    if (gPalmUiNewButton[0] != '\0')
    {
        const PalmUiObjectBounds button = palmUiObjectBounds(kMemoNewButtonId);
        drawPalmButton(button.x, button.y, button.w, button.h, gPalmUiNewButton, gPalmUiPressedControl == kMemoNewButtonId, chromeLight, lcdInk, line);
    }
    if (gPalmUiDetailsButton[0] != '\0')
    {
        const PalmUiObjectBounds button = palmUiObjectBounds(kMemoDetailsButtonId);
        drawPalmButton(button.x, button.y, button.w, button.h, gPalmUiDetailsButton, gPalmUiPressedControl == kMemoDetailsButtonId, chromeLight, lcdInk, line);
    }

    if (gPalmModalVisible)
    {
        const PalmUiObjectBounds okButton = palmUiObjectBounds(kModalOkButtonId);
        drawPalmModalFrame(18, 38, 124, 83, gPalmModalTitle, lcdBg, lcdInk, line, shadow);
        drawPalmTextClipped(27, 62, gPalmModalLine1, lcdInk, 1, 104);
        drawPalmTextClipped(27, 78, gPalmModalLine2, lcdInk, 1, 104);
        drawPalmButton(okButton.x, okButton.y, okButton.w, okButton.h, gPalmModalOkButton, gPalmModalPressedControl == kModalOkButtonId, chromeLight, lcdInk, line);
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
        selectPalmGeneratedFont();
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

    selectPalmGeneratedFont();

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

bool palmDisplaySetEditText(const char* text)
{
    if (!palmDisplayBegin())
    {
        return false;
    }

    if (!gPalmUiSurfaceStarted)
    {
        resetPalmUiSurface(""Memo Pad"");
    }

    if (gPalmUiMode != kPalmUiModeEdit)
    {
        gPalmUiMode = kPalmUiModeEdit;
        gPalmUiPressedControl = 0;
        gPalmUiSelectedRow = -1;
    }

    gPalmEditText[0] = '\0';
    copyPalmUiText(gPalmEditText, sizeof(gPalmEditText), text != nullptr ? text : """");
    presentPalmUiSurface(0xFFFFu, gDisplayGeneration);
    Serial.printf(""LCD Palm edit text length=%u generation=%u\n"",
        static_cast<unsigned>(strlen(gPalmEditText)),
        static_cast<unsigned>(gDisplayGeneration));
    return true;
}

void palmDisplayPalmUiHandleTap(uint16_t selector, uint32_t trapCount, int16_t x, int16_t y, const char* source)
{
    if (!palmDisplayBegin())
    {
        return;
    }

    if (!gPalmUiSurfaceStarted)
    {
        resetPalmUiSurface(""Memo Pad"");
    }

    if (gPalmModalVisible)
    {
        bool okPressed = false;
        const PalmUiObjectBounds okButton = palmUiObjectBounds(kModalOkButtonId);
        if (inRect(x, y, okButton.x, okButton.y, okButton.w, okButton.h))
        {
            gPalmModalPressedControl = kModalOkButtonId;
            okPressed = true;
            closeModal();
        }
        else
        {
            gPalmModalPressedControl = 0;
        }

        presentPalmUiSurface(selector, trapCount);
        Serial.printf(""LCD Palm modal tap selector=0x%04X traps=%u x=%d y=%d ok=%s source=%s generation=%u\n"",
            static_cast<unsigned>(selector),
            static_cast<unsigned>(trapCount),
            static_cast<int>(x),
            static_cast<int>(y),
            okPressed ? ""yes"" : ""no"",
            source != nullptr ? source : ""unknown"",
            static_cast<unsigned>(gDisplayGeneration));
        return;
    }

    const PalmTapTarget target = classifyPalmTap(x, y);

    gPalmUiPressedControl = target.controlID;
    if (gPalmUiMode == kPalmUiModeEdit)
    {
        if (target.controlID == kMemoDoneButtonId)
        {
            prependFakeMemoRecord(gPalmEditText);
            gPalmUiMode = kPalmUiModeList;
            gPalmEditText[0] = '\0';
            palmDisplayMemoListSetSelection(0);
            gPalmUiPressedControl = 0;
        }
        else if (target.controlID == kMemoCancelButtonId)
        {
            gPalmUiMode = kPalmUiModeList;
            gPalmEditText[0] = '\0';
            gPalmUiSelectedRow = -1;
            gPalmUiPressedControl = 0;
            publishStoredMemoRowsToUi();
        }
    }
    else if (target.row >= 0)
    {
        palmDisplayMemoListSetSelection(target.row);
    }
    else if (target.controlID != 0)
    {
        gPalmUiSelectedRow = -1;
        if (target.controlID == kMemoNewButtonId)
        {
            gPalmUiMode = kPalmUiModeEdit;
            gPalmEditText[0] = '\0';
        }
        else if (target.controlID == kMemoDetailsButtonId)
        {
            openDetailsModal();
        }
    }

    presentPalmUiSurface(selector, trapCount);
    Serial.printf(""LCD Palm tap selector=0x%04X traps=%u x=%d y=%d row=%d control=%u source=%s generation=%u\n"",
        static_cast<unsigned>(selector),
        static_cast<unsigned>(trapCount),
        static_cast<int>(x),
        static_cast<int>(y),
        static_cast<int>(gPalmUiSelectedRow),
        static_cast<unsigned>(gPalmUiPressedControl),
        source != nullptr ? source : ""unknown"",
        static_cast<unsigned>(gDisplayGeneration));
}

void palmDisplayPalmUiShowModal(uint16_t selector, uint32_t trapCount, const char* title, const char* line1, const char* line2)
{
    if (!palmDisplayBegin())
    {
        return;
    }

    if (!gPalmUiSurfaceStarted)
    {
        resetPalmUiSurface(""Memo Pad"");
    }

    gPalmModalVisible = true;
    gPalmModalPressedControl = 0;
    copyPalmUiText(gPalmModalTitle, sizeof(gPalmModalTitle), title != nullptr && title[0] != '\0' ? title : ""Alert"");
    copyPalmUiText(gPalmModalLine1, sizeof(gPalmModalLine1), line1 != nullptr && line1[0] != '\0' ? line1 : ""Palm OS dialog"");
    copyPalmUiText(gPalmModalLine2, sizeof(gPalmModalLine2), line2 != nullptr && line2[0] != '\0' ? line2 : ""OK to continue"");
    copyPalmUiText(gPalmModalOkButton, sizeof(gPalmModalOkButton), ""OK"");
    presentPalmUiSurface(selector, trapCount);
    Serial.printf(""LCD Palm modal selector=0x%04X traps=%u title='%s' line1='%s' line2='%s' generation=%u\n"",
        static_cast<unsigned>(selector),
        static_cast<unsigned>(trapCount),
        gPalmModalTitle,
        gPalmModalLine1,
        gPalmModalLine2,
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
    static char command[96];
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
            else
            {
                int tapX = 0;
                int tapY = 0;
                if (strncmp(command, ""text "", 5) == 0 || strncmp(command, ""TEXT "", 5) == 0)
                {
                    if (palmDisplaySetEditText(command + 5))
                    {
                        Serial.printf(""PALM_LCD_TEXT_SET %u\n"", static_cast<unsigned>(strlen(gPalmEditText)));
                    }
                    else
                    {
                        Serial.println(""PALM_LCD_TEXT_ERROR display_not_ready"");
                    }
                }
                else if (sscanf(command, ""tap %d %d"", &tapX, &tapY) == 2 ||
                    sscanf(command, ""TAP %d %d"", &tapX, &tapY) == 2)
                {
                    if (tapX < 0 || tapX >= kPalmLcdW || tapY < 0 || tapY >= kPalmLcdH)
                    {
                        Serial.printf(""PALM_LCD_TAP_ERROR out_of_range %d %d\n"", tapX, tapY);
                    }
                    else if (!queuePalmTap(static_cast<int16_t>(tapX), static_cast<int16_t>(tapY)))
                    {
                        Serial.println(""PALM_LCD_TAP_ERROR queue_full"");
                    }
                    else
                    {
                        palmDisplayPalmUiHandleTap(0xFFFFu, gDisplayGeneration, static_cast<int16_t>(tapX), static_cast<int16_t>(tapY), ""serial"");
                        Serial.printf(""PALM_LCD_TAP_QUEUED %d %d\n"", tapX, tapY);
                    }
                }
                else if (length > 0)
                {
                    Serial.printf(""PALM_LCD_UNKNOWN_COMMAND %s\n"", command);
                }
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

    Private Shared Function RenderFontResourcesHeader() As String
        Return "#pragma once

#include <stddef.h>
#include <stdint.h>

struct PalmGeneratedFontResource
{
    const char* sourceName;
    uint16_t resourceId;
    uint32_t sourceOffset;
    const uint8_t* bytes;
    uint32_t size;
};

extern const PalmGeneratedFontResource kPalmGeneratedFontResources[];
extern const size_t kPalmGeneratedFontResourceCount;
"
    End Function

    Private Shared Function RenderFontResourcesCpp(fonts As List(Of ExtractedPalmFontResource)) As String
        Dim builder As New StringBuilder()
        builder.AppendLine("#include ""palm_font_resources.h""")
        builder.AppendLine()
        builder.AppendLine("#include <Arduino.h>")
        builder.AppendLine()

        For index = 0 To fonts.Count - 1
            Dim font = fonts(index)
            builder.AppendLine($"static const uint8_t kPalmFontResource{index}[] PROGMEM = {{")
            For offset = 0 To font.Bytes.Length - 1 Step 12
                Dim count = Math.Min(12, font.Bytes.Length - offset)
                Dim values = Enumerable.Range(offset, count).Select(Function(i) $"0x{font.Bytes(i):X2}")
                builder.AppendLine($"    {String.Join(", ", values)},")
            Next
            builder.AppendLine("};")
            builder.AppendLine()
        Next

        builder.AppendLine("const PalmGeneratedFontResource kPalmGeneratedFontResources[] = {")
        If fonts.Count = 0 Then
            builder.AppendLine("    {nullptr, 0u, 0u, nullptr, 0u},")
        Else
            For index = 0 To fonts.Count - 1
                Dim font = fonts(index)
                builder.AppendLine($"    {{""{EscapeCppString(font.SourceName)}"", {font.ResourceId}u, 0x{font.SourceOffset:X}u, kPalmFontResource{index}, {font.Bytes.Length}u}},")
            Next
        End If
        builder.AppendLine("};")
        builder.AppendLine()
        builder.AppendLine($"const size_t kPalmGeneratedFontResourceCount = {fonts.Count}u;")
        Return builder.ToString()
    End Function

    Private Shared Function RenderGenerationNotes(request As Esp32ProjectRequest, romSize As Integer, databaseCount As Integer, apps As List(Of ExportedPalmApplication), fonts As List(Of ExtractedPalmFontResource)) As String
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

        builder.AppendLine()
        builder.AppendLine($"Direct NFNT font resources extracted: {fonts.Count}")
        For Each font In fonts
            builder.AppendLine($"- {font.SourceName} NFNT #{font.ResourceId} offset=0x{font.SourceOffset:X8} size={font.Bytes.Length}")
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
