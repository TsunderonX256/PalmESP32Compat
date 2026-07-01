Imports System.Globalization
Imports System.IO
Imports System.IO.Ports
Imports System.Text

Module Program
    Function Main(args As String()) As Integer
        Console.OutputEncoding = Encoding.UTF8

        Try
            If args.Length = 0 OrElse IsHelp(args(0)) Then
                PrintUsage()
                Return If(args.Length = 0, 2, 0)
            End If

            Select Case args(0).ToLowerInvariant()
                Case "inspect"
                    Return InspectRom(args)
                Case "generate"
                    Return GenerateProject(args)
                Case "snapshot"
                    Return CaptureSnapshot(args)
                Case "tap-snapshot"
                    Return CaptureTapSnapshot(args)
                Case "tap"
                    Return SendTap(args)
                Case "text"
                    Return SendText(args)
                Case "trap-map"
                    Return PrintTrapMapCommand(args)
                Case Else
                    Console.Error.WriteLine($"unknown command: {args(0)}")
                    PrintUsage()
                    Return 2
            End Select
        Catch ex As Exception
            Console.Error.WriteLine(ex.Message)
            Return 1
        End Try
    End Function

    Private Function CaptureSnapshot(args As String()) As Integer
        If args.Length < 2 Then
            PrintUsage()
            Return 2
        End If

        Dim portName = args(1)
        Dim outputPath = Path.GetFullPath(If(args.Length >= 3 AndAlso Not args(2).StartsWith("--", StringComparison.Ordinal), args(2), "palm-lcd-snapshot.bmp"))
        Dim baud = Integer.Parse(GetOptionValue(args, "--baud", "115200"), CultureInfo.InvariantCulture)
        Dim timeoutMs = Integer.Parse(GetOptionValue(args, "--timeout-ms", "10000"), CultureInfo.InvariantCulture)
        Dim scale = Integer.Parse(GetOptionValue(args, "--scale", "3"), CultureInfo.InvariantCulture)
        If scale < 1 Then
            Throw New ArgumentOutOfRangeException(NameOf(scale), "scale must be >= 1")
        End If

        Using port As New SerialPort(portName, baud, Parity.None, 8, StopBits.One)
            port.Encoding = Encoding.ASCII
            port.ReadTimeout = 250
            port.WriteTimeout = 1000
            port.DtrEnable = False
            port.RtsEnable = False
            port.Open()
            port.DiscardInBuffer()
            port.DiscardOutBuffer()
            CaptureSnapshotFromOpenPort(port, outputPath, timeoutMs, scale)
        End Using

        Return 0
    End Function

    Private Function CaptureTapSnapshot(args As String()) As Integer
        If args.Length < 4 Then
            PrintUsage()
            Return 2
        End If

        Dim portName = args(1)
        Dim x = Integer.Parse(args(2), CultureInfo.InvariantCulture)
        Dim y = Integer.Parse(args(3), CultureInfo.InvariantCulture)
        If x < 0 OrElse x >= 160 OrElse y < 0 OrElse y >= 160 Then
            Throw New ArgumentOutOfRangeException("tap-snapshot", "tap coordinates must be inside the Palm LCD area: 0 <= x,y < 160")
        End If

        Dim outputPath = Path.GetFullPath(If(args.Length >= 5 AndAlso Not args(4).StartsWith("--", StringComparison.Ordinal), args(4), "palm-lcd-tap-snapshot.bmp"))
        Dim baud = Integer.Parse(GetOptionValue(args, "--baud", "115200"), CultureInfo.InvariantCulture)
        Dim timeoutMs = Integer.Parse(GetOptionValue(args, "--timeout-ms", "10000"), CultureInfo.InvariantCulture)
        Dim scale = Integer.Parse(GetOptionValue(args, "--scale", "3"), CultureInfo.InvariantCulture)
        If scale < 1 Then
            Throw New ArgumentOutOfRangeException(NameOf(scale), "scale must be >= 1")
        End If

        Using port As New SerialPort(portName, baud, Parity.None, 8, StopBits.One)
            port.Encoding = Encoding.ASCII
            port.ReadTimeout = 250
            port.WriteTimeout = 1000
            port.DtrEnable = False
            port.RtsEnable = False
            port.Open()
            port.DiscardInBuffer()
            port.DiscardOutBuffer()
            port.Write($"tap {x} {y}" & vbLf)

            Dim line = ReadUntilTapResponse(port, timeoutMs)
            Console.WriteLine(line)
            CaptureSnapshotFromOpenPort(port, outputPath, timeoutMs, scale)
        End Using

        Return 0
    End Function

    Private Sub CaptureSnapshotFromOpenPort(port As SerialPort, outputPath As String, timeoutMs As Integer, scale As Integer)
        port.Write("lcdsnap" & vbLf)

        Dim header = ReadUntilSnapshotHeader(port, timeoutMs)
        Dim parts = header.Split(" "c, StringSplitOptions.RemoveEmptyEntries)
        If parts.Length < 5 OrElse Not parts(0).Equals("PALM_LCD_SNAPSHOT_BEGIN", StringComparison.Ordinal) Then
            Throw New InvalidDataException($"unexpected snapshot header: {header}")
        End If

        Dim width = Integer.Parse(parts(1), CultureInfo.InvariantCulture)
        Dim height = Integer.Parse(parts(2), CultureInfo.InvariantCulture)
        Dim format = parts(3)
        If width <> 160 OrElse height <> 160 OrElse Not format.Equals("RGB565BE", StringComparison.Ordinal) Then
            Throw New InvalidDataException($"unsupported snapshot format: {header}")
        End If

        Dim pixelBytes = ReadExact(port, width * height * 2, timeoutMs)
        Dim endLine = ReadLineSkippingEmpty(port, timeoutMs)
        If Not endLine.Equals("PALM_LCD_SNAPSHOT_END", StringComparison.Ordinal) Then
            Throw New InvalidDataException($"snapshot end marker missing: {endLine}")
        End If

        WriteScaledRgb565Bmp(outputPath, pixelBytes, width, height, scale)
        Console.WriteLine($"Snapshot saved: {outputPath}")
        Console.WriteLine($"Source: {width}x{height} {format}; output: {width * scale}x{height * scale} BMP")
    End Sub

    Private Function SendTap(args As String()) As Integer
        If args.Length < 4 Then
            PrintUsage()
            Return 2
        End If

        Dim portName = args(1)
        Dim x = Integer.Parse(args(2), CultureInfo.InvariantCulture)
        Dim y = Integer.Parse(args(3), CultureInfo.InvariantCulture)
        If x < 0 OrElse x >= 160 OrElse y < 0 OrElse y >= 160 Then
            Throw New ArgumentOutOfRangeException("tap", "tap coordinates must be inside the Palm LCD area: 0 <= x,y < 160")
        End If

        Dim baud = Integer.Parse(GetOptionValue(args, "--baud", "115200"), CultureInfo.InvariantCulture)
        Dim timeoutMs = Integer.Parse(GetOptionValue(args, "--timeout-ms", "5000"), CultureInfo.InvariantCulture)

        Using port As New SerialPort(portName, baud, Parity.None, 8, StopBits.One)
            port.Encoding = Encoding.ASCII
            port.ReadTimeout = 250
            port.WriteTimeout = 1000
            port.DtrEnable = False
            port.RtsEnable = False
            port.Open()
            port.DiscardInBuffer()
            port.DiscardOutBuffer()
            port.Write($"tap {x} {y}" & vbLf)

            Dim line = ReadUntilTapResponse(port, timeoutMs)
            Console.WriteLine(line)
        End Using

        Return 0
    End Function

    Private Function SendText(args As String()) As Integer
        If args.Length < 3 Then
            PrintUsage()
            Return 2
        End If

        Dim portName = args(1)
        Dim memoText = GetCommandText(args, 2)
        If String.IsNullOrWhiteSpace(memoText) Then
            Throw New ArgumentException("text command requires non-empty memo text")
        End If

        Dim baud = Integer.Parse(GetOptionValue(args, "--baud", "115200"), CultureInfo.InvariantCulture)
        Dim timeoutMs = Integer.Parse(GetOptionValue(args, "--timeout-ms", "5000"), CultureInfo.InvariantCulture)

        Using port As New SerialPort(portName, baud, Parity.None, 8, StopBits.One)
            port.Encoding = Encoding.ASCII
            port.ReadTimeout = 250
            port.WriteTimeout = 1000
            port.DtrEnable = False
            port.RtsEnable = False
            port.Open()
            port.DiscardInBuffer()
            port.DiscardOutBuffer()
            port.Write($"text {memoText}" & vbLf)

            Dim line = ReadUntilTextResponse(port, timeoutMs)
            Console.WriteLine(line)
        End Using

        Return 0
    End Function

    Private Function PrintTrapMapCommand(args As String()) As Integer
        Dim markdown = HasOption(args, "--markdown")

        If markdown Then
            Console.WriteLine("# Memo Pad Trap Coverage")
            Console.WriteLine()
            Console.WriteLine("Reference: https://palm.wiki/development/docs/601/PalmOSReference/ReferenceTOC.html")
            Console.WriteLine()
            Console.WriteLine("| Selector | Current name | Area | Status | Notes |")
            Console.WriteLine("| --- | --- | --- | --- | --- |")
            For Each row In MemoTrapRows()
                Console.WriteLine($"| `{row.Selector}` | `{row.Name}` | {row.Area} | {row.Status} | {row.Notes} |")
            Next
        Else
            Console.WriteLine("Memo Pad trap coverage")
            Console.WriteLine("Palm API reference: https://palm.wiki/development/docs/601/PalmOSReference/ReferenceTOC.html")
            Console.WriteLine()
            For Each row In MemoTrapRows()
                Console.WriteLine($"{row.Selector,-8} {row.Name,-30} {row.Area,-12} {row.Status}")
                Console.WriteLine($"         {row.Notes}")
            Next
        End If

        Return 0
    End Function

    Private Function InspectRom(args As String()) As Integer
        If args.Length < 2 Then
            PrintUsage()
            Return 2
        End If

        Dim romPath = Path.GetFullPath(args(1))
        Dim romBase = ParseOptionalUInt32(args, "--rom-base")
        Dim databases = PalmRomScanner.ReadDatabases(romPath, romBase)
        Dim apps = databases.Where(Function(db) db.IsApplication).OrderBy(Function(db) db.Name, StringComparer.OrdinalIgnoreCase).ToList()

        Console.WriteLine($"ROM: {romPath}")
        Console.WriteLine($"Size: {New FileInfo(romPath).Length:N0} bytes")
        Console.WriteLine($"Databases: {databases.Count}")
        Console.WriteLine($"Applications: {apps.Count}")
        Console.WriteLine()
        Console.WriteLine($"{"Offset",-10} {"Size",8} {"Creator",-7} {"Recs",5} {"Name"}")
        Console.WriteLine(New String("-"c, 70))

        For Each app In apps
            Console.WriteLine($"0x{app.StartOffset:X6} {app.Length,8} {app.CreatorCode,-7} {app.EntryCount,5} {app.Name}")
        Next

        Return 0
    End Function

    Private Function GenerateProject(args As String()) As Integer
        If args.Length < 3 Then
            PrintUsage()
            Return 2
        End If

        Dim romPath = Path.GetFullPath(args(1))
        Dim outputDirectory = Path.GetFullPath(args(2))
        Dim romBase = ParseOptionalUInt32(args, "--rom-base")
        Dim projectName = GetOptionValue(args, "--project-name", "PalmCompatGenerated")
        Dim hardwareProfile = GetOptionValue(args, "--hardware-profile", "esp32-palm-m100")
        Dim appName = GetOptionValue(args, "--app-name", "")
        Dim creator = GetOptionValue(args, "--creator", "")
        Dim exportAll = HasOption(args, "--all")

        Dim request As New Esp32ProjectRequest With {
            .RomPath = romPath,
            .OutputDirectory = outputDirectory,
            .RomBase = romBase,
            .ProjectName = projectName,
            .HardwareProfile = hardwareProfile,
            .AppNameFilter = appName,
            .CreatorFilter = creator,
            .ExportAllApplications = exportAll
        }

        Dim result = Esp32ProjectGenerator.Generate(request)

        Console.WriteLine($"Generated: {result.OutputDirectory}")
        Console.WriteLine($"Applications exported: {result.ExportedApplications.Count}")
        For Each app In result.ExportedApplications
            Console.WriteLine($"  {app.Name} ({app.CreatorCode}) -> {app.RelativePath}")
        Next

        Console.WriteLine()
        Console.WriteLine("Next step:")
        Console.WriteLine($"  Open {Path.Combine(result.OutputDirectory, "platformio.ini")} and wire runtime sources into src/.")

        Return 0
    End Function

    Private Function HasOption(args As String(), optionName As String) As Boolean
        Return args.Any(Function(arg) arg.Equals(optionName, StringComparison.OrdinalIgnoreCase))
    End Function

    Private Function GetOptionValue(args As String(), optionName As String, defaultValue As String) As String
        For i = 0 To args.Length - 2
            If args(i).Equals(optionName, StringComparison.OrdinalIgnoreCase) Then
                Return args(i + 1)
            End If
        Next

        Return defaultValue
    End Function

    Private Function GetCommandText(args As String(), startIndex As Integer) As String
        Dim words As New List(Of String)()
        Dim i = startIndex
        While i < args.Length
            If args(i).StartsWith("--", StringComparison.Ordinal) Then
                i += 2
                Continue While
            End If

            words.Add(args(i))
            i += 1
        End While

        Return String.Join(" ", words)
    End Function

    Private Function ParseOptionalUInt32(args As String(), optionName As String) As UInteger?
        Dim value = GetOptionValue(args, optionName, "")
        If String.IsNullOrWhiteSpace(value) Then
            Return Nothing
        End If

        If value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) Then
            Return UInteger.Parse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        End If

        Return UInteger.Parse(value, CultureInfo.InvariantCulture)
    End Function

    Private Iterator Function MemoTrapRows() As IEnumerable(Of TrapCoverageRow)
        Yield New TrapCoverageRow("0xA012", "MemChunkFree", "Memory", "tiny heap", "Frees dynamic pointer-backed blocks and returns success for legacy fixed scratch pointers.")
        Yield New TrapCoverageRow("0xA013", "MemPtrNew", "Memory", "tiny heap", "Allocates a pointer-backed block from the generated trap heap.")
        Yield New TrapCoverageRow("0xA014", "MemPtrRecoverHandle", "Memory", "tiny heap", "Recovers dynamic handles from pointers and maps legacy fixed scratch pointers.")
        Yield New TrapCoverageRow("0xA016", "MemPtrSize", "Memory", "tiny heap", "Returns dynamic pointer sizes, including interior pointer remaining bytes, with fixed scratch fallbacks.")
        Yield New TrapCoverageRow("0xA01C", "MemPtrResize", "Memory", "tiny heap", "Resizes dynamic pointer-backed blocks through their recovered handle.")
        Yield New TrapCoverageRow("0xA01E", "MemHandleNew", "Memory", "tiny heap", "Allocates a dynamic fake MemHandle with pointer, size, and lock count metadata.")
        Yield New TrapCoverageRow("0xA01F", "MemHandleLockCount", "Memory", "tiny heap", "Reports dynamic fake-handle lock counts.")
        Yield New TrapCoverageRow("0xA020", "MemHandleToLocalID", "Memory", "compat shim", "Currently returns the fake handle value as its local id.")
        Yield New TrapCoverageRow("0xA021", "MemHandleLock", "Memory", "tiny heap", "Maps dynamic handles and legacy fixed handles to trap heap pointers.")
        Yield New TrapCoverageRow("0xA022", "MemHandleUnlock", "Memory", "tiny heap", "Decrements lock counts for dynamic fake handles.")
        Yield New TrapCoverageRow("0xA02B", "MemHandleFree", "Memory", "tiny heap", "Frees dynamic fake handles and returns their backing blocks to the reusable free list.")
        Yield New TrapCoverageRow("0xA02C", "MemHandleFlags", "Memory", "compat shim", "Returns zero flags for dynamic and legacy fake handles.")
        Yield New TrapCoverageRow("0xA02D", "MemHandleSize", "Memory", "tiny heap", "Returns dynamic handle sizes or legacy fixed-handle sizes.")
        Yield New TrapCoverageRow("0xA033", "MemHandleResize", "Memory", "tiny heap", "Resizes dynamic fake handles, moving backing storage when needed.")
        Yield New TrapCoverageRow("0xA035", "MemPtrUnlock", "Memory", "tiny heap", "Recovers the dynamic handle from a pointer and decrements its lock count.")
        Yield New TrapCoverageRow("0xA041", "DmCreateDatabase", "Database", "working shim", "Creates the synthetic MemoDB when Memo Pad asks for DATA/memo.")
        Yield New TrapCoverageRow("0xA04A", "DmCloseDatabase", "Database", "stubbed", "Returns success for the synthetic MemoDB.")
        Yield New TrapCoverageRow("0xA049", "DmOpenDatabase", "Database", "forced create path", "Currently returns null so Memo Pad initializes the fake database.")
        Yield New TrapCoverageRow("0xA04C", "DmOpenDatabaseInfo", "Database", "partial shim", "Writes zeroed metadata for now.")
        Yield New TrapCoverageRow("0xA04F", "DmNumRecords", "Database", "working shim", "Returns the synthetic memo record count.")
        Yield New TrapCoverageRow("0xA050", "DmRecordInfo", "Database", "working shim", "Writes attributes, unique id, and the per-record dynamic chunk handle.")
        Yield New TrapCoverageRow("0xA051", "DmSetRecordInfo", "Database", "metadata shim", "Accepts record metadata updates; category/dirty persistence is still simplified.")
        Yield New TrapCoverageRow("0xA055", "DmNewRecord", "Database", "writable shim", "Inserts an empty synthetic memo record backed by a dynamic tiny-heap MemHandle.")
        Yield New TrapCoverageRow("0xA056", "DmRemoveRecord", "Database", "writable shim", "Removes a synthetic memo record and frees its dynamic backing handle.")
        Yield New TrapCoverageRow("0xA057", "DmDeleteRecord", "Database", "writable shim", "Removes a synthetic memo record and frees its dynamic backing handle; sync delete flags are not modeled yet.")
        Yield New TrapCoverageRow("0xA058", "DmArchiveRecord", "Database", "writable shim", "Removes a synthetic memo record and frees its dynamic backing handle; archive flags are not modeled yet.")
        Yield New TrapCoverageRow("0xA05B", "DmQueryRecord", "Database", "working shim", "Returns the requested memo record's dynamic tiny-heap handle.")
        Yield New TrapCoverageRow("0xA05C", "DmGetRecord", "Database", "working shim", "Returns the requested memo record's dynamic tiny-heap handle.")
        Yield New TrapCoverageRow("0xA05D", "DmResizeRecord", "Database", "writable shim", "Resizes the requested memo record's dynamic backing handle.")
        Yield New TrapCoverageRow("0xA05E", "DmReleaseRecord", "Database", "writable shim", "Commits a dirty dynamic memo record handle back into the native display store.")
        Yield New TrapCoverageRow("0xA05F", "DmGetResource", "Resource", "partial shim", "Loads exported resource bytes or synthetic tSTR text and reports overlay-catalog hits.")
        Yield New TrapCoverageRow("0xA060", "DmGet1Resource", "Resource", "partial shim", "Uses the same exported-resource and overlay-catalog lookup path as DmGetResource.")
        Yield New TrapCoverageRow("0xA061", "DmReleaseResource", "Resource", "stubbed", "Returns success for fake resource handles.")
        Yield New TrapCoverageRow("0xA071", "DmNumRecordsInCategory", "Database", "working shim", "Publishes list rows and returns the synthetic count.")
        Yield New TrapCoverageRow("0xA075", "DmOpenDatabaseByTypeCreator", "Database", "working shim", "Opens DATA/memo only after synthetic create.")
        Yield New TrapCoverageRow("0xA076", "DmWrite", "Database", "writable shim", "Copies bytes into a dynamic memo record pointer and commits the memo text.")
        Yield New TrapCoverageRow("0xA077", "DmStrCopy", "Database", "writable shim", "Copies a string into a dynamic memo record pointer and commits the memo text.")
        Yield New TrapCoverageRow("0xA079", "DmWriteCheck", "Database", "validation shim", "Allows writes to proceed while bounds checks are handled by the scratch buffer.")
        Yield New TrapCoverageRow("0xA08F", "SysAppStartup", "System", "working shim", "Builds a minimal launch block for startup glue.")
        Yield New TrapCoverageRow("0xA090", "SysAppExit", "System", "working shim", "Stops the probe run cleanly.")
        Yield New TrapCoverageRow("0xA0A9", "SysHandleEvent", "Events", "probe shim", "Returns false while the native LCD probe keeps the Memo Pad surface alive.")
        Yield New TrapCoverageRow("0xA104", "CategoryGetName", "Categories", "working shim", "Writes All or Unfiled into the caller's category buffer.")
        Yield New TrapCoverageRow("0xA10D", "CtlDrawControl", "Controls", "control shim", "Draws known Memo buttons from fake control object metadata.")
        Yield New TrapCoverageRow("0xA10E", "CtlEraseControl", "Controls", "control shim", "Marks known fake controls hidden and clears their pressed value.")
        Yield New TrapCoverageRow("0xA10F", "CtlHideControl", "Controls", "control shim", "Marks known fake controls hidden and clears their pressed value.")
        Yield New TrapCoverageRow("0xA110", "CtlShowControl", "Controls", "control shim", "Marks known fake controls visible and redraws them.")
        Yield New TrapCoverageRow("0xA111", "CtlGetValue", "Controls", "control shim", "Returns the tracked value for known fake controls.")
        Yield New TrapCoverageRow("0xA112", "CtlSetValue", "Controls", "control shim", "Updates the tracked value for known fake controls.")
        Yield New TrapCoverageRow("0xA113", "CtlGetLabel", "Controls", "control shim", "Returns a stable pointer to the fake control label text.")
        Yield New TrapCoverageRow("0xA114", "CtlSetLabel", "Controls", "control shim", "Copies a caller label into known fake control metadata and redraws.")
        Yield New TrapCoverageRow("0xA115", "CtlHandleEvent", "Controls", "control shim", "Consumes matching ctlSelect events and routes them through the native Memo tap path.")
        Yield New TrapCoverageRow("0xA116", "CtlHitControl", "Controls", "control shim", "Triggers the known fake control through the native Memo tap path.")
        Yield New TrapCoverageRow("0xA117", "CtlSetEnabled", "Controls", "control shim", "Tracks whether a fake control is interactable.")
        Yield New TrapCoverageRow("0xA118", "CtlSetUsable", "Controls", "control shim", "Tracks whether a fake control is usable/visible.")
        Yield New TrapCoverageRow("0xA119", "CtlEnabled", "Controls", "control shim", "Returns tracked enabled state for known fake controls.")
        Yield New TrapCoverageRow("0xA11D", "EvtGetEvent", "Events", "queue-backed shim", "Writes queued ctlSelect/lstSelect events from UART tap injection, otherwise writes a minimal nil event.")
        Yield New TrapCoverageRow("0xA135", "FldDrawField", "Fields", "text shim", "Mirrors the synthetic active field buffer into the native Memo edit surface.")
        Yield New TrapCoverageRow("0xA139", "FldGetTextPtr", "Fields", "text shim", "Returns the fake field text pointer at 0x2300.")
        Yield New TrapCoverageRow("0xA13F", "FldSetText", "Fields", "text shim", "Copies text from a fake handle into the active field buffer.")
        Yield New TrapCoverageRow("0xA14A", "FldGetTextAllocatedSize", "Fields", "text shim", "Reports the current 64-byte smoke field buffer size.")
        Yield New TrapCoverageRow("0xA14B", "FldGetTextLength", "Fields", "text shim", "Returns the active field text length.")
        Yield New TrapCoverageRow("0xA153", "FldGetTextHandle", "Fields", "text shim", "Returns the fake field text handle.")
        Yield New TrapCoverageRow("0xA155", "FldDirty", "Fields", "text shim", "Reports whether insert/delete/set-dirty touched the active field.")
        Yield New TrapCoverageRow("0xA158", "FldSetTextHandle", "Fields", "text shim", "Copies text from a fake MemHandle into the active field buffer.")
        Yield New TrapCoverageRow("0xA159", "FldSetTextPtr", "Fields", "text shim", "Copies text from a pointer into the active field buffer.")
        Yield New TrapCoverageRow("0xA15D", "FldInsert", "Fields", "text shim", "Inserts text at the synthetic insertion point and mirrors the edit surface.")
        Yield New TrapCoverageRow("0xA15E", "FldDelete", "Fields", "text shim", "Deletes a text range and mirrors the edit surface.")
        Yield New TrapCoverageRow("0xA160", "FldSetDirty", "Fields", "text shim", "Updates the active field dirty flag.")
        Yield New TrapCoverageRow("0xA16F", "FrmInitForm", "Forms", "catalog-aware form shim", "Allocates a native fake FormPtr, reports overlay-catalog tFRM matches, and seeds stable list/button object pointers.")
        Yield New TrapCoverageRow("0xA170", "FrmDeleteForm", "Forms", "form shim", "Clears the active fake form object table.")
        Yield New TrapCoverageRow("0xA171", "FrmDrawForm", "Forms", "bounds-driven form shim", "Draws the current native Memo surface from published form object bounds and memo rows.")
        Yield New TrapCoverageRow("0xA172", "FrmEraseForm", "Forms", "form shim", "Accepts form erase requests for the active fake form.")
        Yield New TrapCoverageRow("0xA173", "FrmGetActiveForm", "Forms", "form shim", "Returns the active native fake FormPtr.")
        Yield New TrapCoverageRow("0xA174", "FrmSetActiveForm", "Forms", "form shim", "Accepts an active fake form pointer.")
        Yield New TrapCoverageRow("0xA175", "FrmGetActiveFormID", "Forms", "form shim", "Returns the active fake form resource id.")
        Yield New TrapCoverageRow("0xA176", "FrmGetUserModifiedState", "Forms", "form shim", "Returns false until real dirty form state is modeled.")
        Yield New TrapCoverageRow("0xA177", "FrmSetNotUserModified", "Forms", "form shim", "Accepts clear-dirty requests for the fake form.")
        Yield New TrapCoverageRow("0xA178", "FrmGetFocus", "Forms", "form shim", "Returns the tracked fake form focus index.")
        Yield New TrapCoverageRow("0xA179", "FrmSetFocus", "Forms", "form shim", "Tracks the requested fake form focus index.")
        Yield New TrapCoverageRow("0xA17B", "FrmGetFormBounds", "Forms", "form shim", "Writes a 160x160 fake form bounds rectangle.")
        Yield New TrapCoverageRow("0xA17D", "FrmGetFormId", "Forms", "form shim", "Returns the active fake form id.")
        Yield New TrapCoverageRow("0xA17E", "FrmGetFormPtr", "Forms", "form shim", "Returns or initializes the fake form pointer for a form id.")
        Yield New TrapCoverageRow("0xA17F", "FrmGetNumberOfObjects", "Forms", "form shim", "Returns the seeded fake form object count.")
        Yield New TrapCoverageRow("0xA180", "FrmGetObjectIndex", "Forms", "form shim", "Returns stable object indexes for known or newly observed form object ids.")
        Yield New TrapCoverageRow("0xA181", "FrmGetObjectId", "Forms", "form shim", "Returns fake form object ids by index.")
        Yield New TrapCoverageRow("0xA182", "FrmGetObjectType", "Forms", "form shim", "Returns Palm-compatible fake form object types for controls and lists.")
        Yield New TrapCoverageRow("0xA183", "FrmGetObjectPtr", "Forms", "bounds-driven form shim", "Returns native fake object pointers with id, kind, and bounds metadata mirrored into the LCD geometry bridge.")
        Yield New TrapCoverageRow("0xA184", "FrmHideObject", "Forms", "form shim", "Marks a fake form object hidden.")
        Yield New TrapCoverageRow("0xA185", "FrmShowObject", "Forms", "form shim", "Marks a fake form object visible and redraws known controls.")
        Yield New TrapCoverageRow("0xA186", "FrmGetObjectPosition", "Forms", "bounds-driven form shim", "Writes tracked fake object x/y coordinates.")
        Yield New TrapCoverageRow("0xA187", "FrmSetObjectPosition", "Forms", "bounds-driven form shim", "Updates tracked fake object coordinates and republishes LCD hit/draw bounds.")
        Yield New TrapCoverageRow("0xA188", "FrmGetControlValue", "Forms", "form/control shim", "Returns tracked fake control value by form object index.")
        Yield New TrapCoverageRow("0xA189", "FrmSetControlValue", "Forms", "form/control shim", "Updates tracked fake control value by form object index.")
        Yield New TrapCoverageRow("0xA192", "FrmAlert", "Dialogs", "modal shim", "Shows a native OK modal and returns button 0.")
        Yield New TrapCoverageRow("0xA193", "FrmDoDialog", "Dialogs", "modal shim", "Shows a native OK modal and returns the synthetic OK control id.")
        Yield New TrapCoverageRow("0xA194", "FrmCustomAlert", "Dialogs", "modal shim", "Shows a native OK modal, using captured text when available.")
        Yield New TrapCoverageRow("0xA199", "FrmGetObjectBounds", "Forms", "bounds-driven form shim", "Writes tracked fake object bounds rectangles used by the native Memo UI renderer.")
        Yield New TrapCoverageRow("0xA19B", "FrmGotoForm", "Forms", "probe shim", "Used as a form/title signal for the current Memo Pad LCD surface.")
        Yield New TrapCoverageRow("0xA1A0", "FrmDispatchEvent", "Forms", "select-event shim", "Reads ctlSelect/lstSelect data and mirrors it into the native Memo UI probe.")
        Yield New TrapCoverageRow("0xA1B0", "LstSetDrawFunction", "Lists", "list shim", "Accepts custom draw callback registration for the synthetic Memo list.")
        Yield New TrapCoverageRow("0xA1B1", "LstDrawList", "Lists", "list shim", "Redraws the native Memo list from synthetic memo records.")
        Yield New TrapCoverageRow("0xA1B2", "LstEraseList", "Lists", "list shim", "Returns success for the synthetic Memo list erase path.")
        Yield New TrapCoverageRow("0xA1B3", "LstGetSelection", "Lists", "list shim", "Returns the current synthetic Memo list selection.")
        Yield New TrapCoverageRow("0xA1B4", "LstGetSelectionText", "Lists", "list shim", "Returns a stable pointer to the selected memo row text.")
        Yield New TrapCoverageRow("0xA1B5", "LstHandleEvent", "Lists", "list shim", "Consumes list-select events and updates native Memo list selection.")
        Yield New TrapCoverageRow("0xA1B6", "LstSetHeight", "Lists", "list shim", "Stores requested visible item count for the synthetic Memo list.")
        Yield New TrapCoverageRow("0xA1B7", "LstSetSelection", "Lists", "list shim", "Updates the synthetic Memo list selection and redraws.")
        Yield New TrapCoverageRow("0xA1B8", "LstSetListChoices", "Lists", "list shim", "Accepts caller-supplied choice arrays while the Memo list remains record-backed.")
        Yield New TrapCoverageRow("0xA1B9", "LstMakeItemVisible", "Lists", "list shim", "Adjusts the synthetic Memo list top row so the requested item is visible.")
        Yield New TrapCoverageRow("0xA1BA", "LstGetNumberOfItems", "Lists", "list shim", "Returns the synthetic memo record count.")
        Yield New TrapCoverageRow("0xA1BB", "LstPopupList", "Lists", "list shim", "Draws the list and returns the current selection.")
        Yield New TrapCoverageRow("0xA1BC", "LstSetPosition", "Lists", "list shim", "Updates synthetic list object bounds metadata.")
        Yield New TrapCoverageRow("0xA1BD", "MenuInit", "Menus", "menu shim", "Allocates and activates a fake MenuBarType pointer for the requested menu resource id.")
        Yield New TrapCoverageRow("0xA1BE", "MenuDispose", "Menus", "menu shim", "Disposes the active fake menu and clears menu visibility.")
        Yield New TrapCoverageRow("0xA1BF", "MenuHandleEvent", "Menus", "menu shim", "Writes a zero error code and returns false unless future real menu command handling is added.")
        Yield New TrapCoverageRow("0xA1C0", "MenuDrawMenu", "Menus", "menu shim", "Marks the fake active menu visible.")
        Yield New TrapCoverageRow("0xA1C1", "MenuEraseStatus", "Menus", "menu shim", "Marks the fake active menu hidden.")
        Yield New TrapCoverageRow("0xA1C2", "MenuGetActiveMenu", "Menus", "menu shim", "Returns the tracked fake active menu pointer.")
        Yield New TrapCoverageRow("0xA1C3", "MenuSetActiveMenu", "Menus", "menu shim", "Replaces the fake active menu pointer and returns the previous one.")
        Yield New TrapCoverageRow("0xA1FC", "WinSetActiveWindow", "Windows", "window shim", "Tracks a fake active window handle.")
        Yield New TrapCoverageRow("0xA1FD", "WinSetDrawWindow", "Windows", "window shim", "Tracks a fake draw window handle and returns the previous handle.")
        Yield New TrapCoverageRow("0xA1FE", "WinGetDrawWindow", "Windows", "window shim", "Returns the tracked fake draw window handle.")
        Yield New TrapCoverageRow("0xA1FF", "WinGetActiveWindow", "Windows", "window shim", "Returns the tracked fake active window handle.")
        Yield New TrapCoverageRow("0xA200", "WinGetDisplayWindow", "Windows", "window shim", "Returns a stable fake display window handle.")
        Yield New TrapCoverageRow("0xA201", "WinGetFirstWindow", "Windows", "window shim", "Returns the stable fake display window handle.")
        Yield New TrapCoverageRow("0xA202", "WinEnableWindow", "Windows", "window shim", "Accepts fake window enable requests.")
        Yield New TrapCoverageRow("0xA203", "WinDisableWindow", "Windows", "window shim", "Accepts fake window disable requests.")
        Yield New TrapCoverageRow("0xA204", "WinGetWindowFrameRect", "Windows", "window shim", "Writes a 160x160 fake window frame rectangle.")
        Yield New TrapCoverageRow("0xA205", "WinDrawWindowFrame", "Drawing", "drawing shim", "Draws a native 160x160 Palm LCD frame.")
        Yield New TrapCoverageRow("0xA206", "WinEraseWindow", "Drawing", "drawing shim", "Clears the native Palm LCD area.")
        Yield New TrapCoverageRow("0xA207", "WinSaveBits", "Windows", "window shim", "Returns a stable fake saved-bits handle and success error code.")
        Yield New TrapCoverageRow("0xA208", "WinRestoreBits", "Windows", "window shim", "Accepts restore-bits requests as a no-op.")
        Yield New TrapCoverageRow("0xA20B", "WinGetDisplayExtent", "Windows", "window shim", "Writes 160x160 display extent.")
        Yield New TrapCoverageRow("0xA20C", "WinGetWindowExtent", "Windows", "window shim", "Writes 160x160 window extent.")
        Yield New TrapCoverageRow("0xA20D", "WinDisplayToWindowPt", "Windows", "window shim", "Treats display/window coordinate conversion as identity.")
        Yield New TrapCoverageRow("0xA20E", "WinWindowToDisplayPt", "Windows", "window shim", "Treats window/display coordinate conversion as identity.")
        Yield New TrapCoverageRow("0xA20F", "WinGetClip", "Drawing", "drawing shim", "Returns the tracked fake clip rectangle.")
        Yield New TrapCoverageRow("0xA210", "WinSetClip", "Drawing", "drawing shim", "Tracks a fake clip rectangle.")
        Yield New TrapCoverageRow("0xA211", "WinResetClip", "Drawing", "drawing shim", "Resets the fake clip rectangle to 160x160.")
        Yield New TrapCoverageRow("0xA212", "WinClipRectangle", "Drawing", "drawing shim", "Intersects a rectangle with the tracked fake clip rectangle.")
        Yield New TrapCoverageRow("0xA213", "WinDrawLine", "Drawing", "drawing shim", "Draws a native monochrome line in the Palm LCD area.")
        Yield New TrapCoverageRow("0xA214", "WinDrawGrayLine", "Drawing", "drawing shim", "Draws a native gray line in the Palm LCD area.")
        Yield New TrapCoverageRow("0xA215", "WinEraseLine", "Drawing", "drawing shim", "Draws a white line in the Palm LCD area.")
        Yield New TrapCoverageRow("0xA216", "WinInvertLine", "Drawing", "drawing shim", "Draws a gray/invert placeholder line.")
        Yield New TrapCoverageRow("0xA217", "WinFillLine", "Drawing", "drawing shim", "Draws a native monochrome line in the Palm LCD area.")
        Yield New TrapCoverageRow("0xA218", "WinDrawRectangle", "Drawing", "drawing shim", "Fills a native monochrome rectangle in the Palm LCD area.")
        Yield New TrapCoverageRow("0xA219", "WinEraseRectangle", "Drawing", "drawing shim", "Fills a native white rectangle in the Palm LCD area.")
        Yield New TrapCoverageRow("0xA21A", "WinInvertRectangle", "Drawing", "drawing shim", "Fills a native gray/invert placeholder rectangle.")
        Yield New TrapCoverageRow("0xA21B", "WinDrawRectangleFrame", "Drawing", "drawing shim", "Draws a native monochrome rectangle frame.")
        Yield New TrapCoverageRow("0xA21C", "WinDrawGrayRectangleFrame", "Drawing", "drawing shim", "Draws a native gray rectangle frame.")
        Yield New TrapCoverageRow("0xA21D", "WinEraseRectangleFrame", "Drawing", "drawing shim", "Draws a native white rectangle frame.")
        Yield New TrapCoverageRow("0xA21E", "WinInvertRectangleFrame", "Drawing", "drawing shim", "Draws a native gray/invert placeholder frame.")
        Yield New TrapCoverageRow("0xA21F", "WinGetFramesRectangle", "Drawing", "drawing shim", "Copies the source rectangle into the caller's obscured rectangle.")
        Yield New TrapCoverageRow("0xA220", "WinDrawChars", "Drawing", "drawing shim", "Draws native monochrome text using the current generated bitmap font.")
        Yield New TrapCoverageRow("0xA221", "WinEraseChars", "Drawing", "drawing shim", "Erases the text bounds and redraws text in white.")
        Yield New TrapCoverageRow("0xA222", "WinInvertChars", "Drawing", "drawing shim", "Draws native gray/invert placeholder text.")
        Yield New TrapCoverageRow("0xA226", "WinDrawBitmap", "Drawing", "resource drawing shim", "Draws uncompressed 1bpp Palm bitmap resources from the currently locked resource handle.")
        Yield New TrapCoverageRow("0xA227", "WinModal", "Windows", "window shim", "Returns false for fake window modality.")
        Yield New TrapCoverageRow("0xA228", "WinGetDrawWindowBounds", "Windows", "window shim", "Writes a 160x160 draw-window bounds rectangle.")
        Yield New TrapCoverageRow("0xA229", "WinFillRectangle", "Drawing", "drawing shim", "Fills a native monochrome rectangle in the Palm LCD area.")
        Yield New TrapCoverageRow("0xA22A", "WinDrawInvertedChars", "Drawing", "drawing shim", "Draws native gray/invert placeholder text.")
        Yield New TrapCoverageRow("0xA2B5", "LstSetTopItem", "Lists", "list shim", "Updates the synthetic Memo list top row and redraws the visible window.")
        Yield New TrapCoverageRow("0xA2CC", "EvtEventAvail", "Events", "working shim", "Reports whether the UART-backed Palm event queue has pending events.")
        Yield New TrapCoverageRow("0xA2D3", "PrefGetAppPreferences", "Preferences", "working shim", "Returns noPreferenceFound and size 0.")
        Yield New TrapCoverageRow("0xA2FC", "CategoryInitialize", "Categories", "stubbed", "Returns success for now.")
        Yield New TrapCoverageRow("0xA27B", "FtrGet", "System", "working shim", "Returns noSuchFeature with zero value.")
    End Function

    Private Structure TrapCoverageRow
        Public Sub New(selector As String, name As String, area As String, status As String, notes As String)
            Me.Selector = selector
            Me.Name = name
            Me.Area = area
            Me.Status = status
            Me.Notes = notes
        End Sub

        Public ReadOnly Selector As String
        Public ReadOnly Name As String
        Public ReadOnly Area As String
        Public ReadOnly Status As String
        Public ReadOnly Notes As String
    End Structure

    Private Function IsHelp(value As String) As Boolean
        Return value = "-h" OrElse value = "--help" OrElse value = "/?"
    End Function

    Private Function ReadUntilSnapshotHeader(port As SerialPort, timeoutMs As Integer) As String
        Dim deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs)
        Do
            Dim line = ReadLineSkippingEmpty(port, Math.Max(1, CInt((deadline - DateTime.UtcNow).TotalMilliseconds)))
            If line.StartsWith("PALM_LCD_SNAPSHOT_BEGIN ", StringComparison.Ordinal) Then
                Return line
            End If
            If line.StartsWith("PALM_LCD_SNAPSHOT_ERROR ", StringComparison.Ordinal) Then
                Throw New IOException(line)
            End If
        Loop While DateTime.UtcNow < deadline

        Throw New TimeoutException("timed out waiting for PALM_LCD_SNAPSHOT_BEGIN")
    End Function

    Private Function ReadUntilTapResponse(port As SerialPort, timeoutMs As Integer) As String
        Dim deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs)
        Do
            Dim line = ReadLineSkippingEmpty(port, Math.Max(1, CInt((deadline - DateTime.UtcNow).TotalMilliseconds)))
            If line.StartsWith("PALM_LCD_TAP_QUEUED ", StringComparison.Ordinal) Then
                Return line
            End If
            If line.StartsWith("PALM_LCD_TAP_ERROR ", StringComparison.Ordinal) Then
                Throw New IOException(line)
            End If
        Loop While DateTime.UtcNow < deadline

        Throw New TimeoutException("timed out waiting for PALM_LCD_TAP response")
    End Function

    Private Function ReadUntilTextResponse(port As SerialPort, timeoutMs As Integer) As String
        Dim deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs)
        Do
            Dim line = ReadLineSkippingEmpty(port, Math.Max(1, CInt((deadline - DateTime.UtcNow).TotalMilliseconds)))
            If line.StartsWith("PALM_LCD_TEXT_SET ", StringComparison.Ordinal) Then
                Return line
            End If
            If line.StartsWith("PALM_LCD_TEXT_ERROR ", StringComparison.Ordinal) Then
                Throw New IOException(line)
            End If
        Loop While DateTime.UtcNow < deadline

        Throw New TimeoutException("timed out waiting for PALM_LCD_TEXT response")
    End Function

    Private Function ReadLineSkippingEmpty(port As SerialPort, timeoutMs As Integer) As String
        Dim deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs)
        Dim bytes As New List(Of Byte)()

        Do
            Try
                Dim value = port.ReadByte()
                If value < 0 Then
                    Continue Do
                End If

                If value = AscW(ControlChars.Lf) Then
                    Dim line = Encoding.ASCII.GetString(bytes.ToArray()).Trim(ControlChars.Cr, ControlChars.Lf)
                    bytes.Clear()
                    If line.Length > 0 Then
                        Return line
                    End If
                Else
                    bytes.Add(CByte(value))
                    If bytes.Count > 512 Then
                        bytes.RemoveAt(0)
                    End If
                End If
            Catch ex As TimeoutException
                If DateTime.UtcNow >= deadline Then
                    Throw
                End If
            End Try
        Loop While DateTime.UtcNow < deadline

        Throw New TimeoutException("timed out reading serial line")
    End Function

    Private Function ReadExact(port As SerialPort, byteCount As Integer, timeoutMs As Integer) As Byte()
        Dim buffer(byteCount - 1) As Byte
        Dim offset = 0
        Dim deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs)

        Do While offset < byteCount
            Try
                Dim read = port.Read(buffer, offset, byteCount - offset)
                If read > 0 Then
                    offset += read
                End If
            Catch ex As TimeoutException
                If DateTime.UtcNow >= deadline Then
                    Throw New TimeoutException($"timed out reading snapshot bytes: {offset}/{byteCount}")
                End If
            End Try
        Loop

        Return buffer
    End Function

    Private Sub WriteScaledRgb565Bmp(path As String, rgb565Be As Byte(), width As Integer, height As Integer, scale As Integer)
        Dim outputWidth = width * scale
        Dim outputHeight = height * scale
        Dim rowStride = ((outputWidth * 3 + 3) \ 4) * 4
        Dim pixelDataSize = rowStride * outputHeight
        Dim fileSize = 14 + 40 + pixelDataSize

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path))
        Using stream = File.Create(path)
            WriteAscii(stream, "BM")
            WriteUInt32LE(stream, CUInt(fileSize))
            WriteUInt16LE(stream, 0US)
            WriteUInt16LE(stream, 0US)
            WriteUInt32LE(stream, 54UI)

            WriteUInt32LE(stream, 40UI)
            WriteInt32LE(stream, outputWidth)
            WriteInt32LE(stream, outputHeight)
            WriteUInt16LE(stream, 1US)
            WriteUInt16LE(stream, 24US)
            WriteUInt32LE(stream, 0UI)
            WriteUInt32LE(stream, CUInt(pixelDataSize))
            WriteInt32LE(stream, 2835)
            WriteInt32LE(stream, 2835)
            WriteUInt32LE(stream, 0UI)
            WriteUInt32LE(stream, 0UI)

            Dim row(rowStride - 1) As Byte
            For outY = outputHeight - 1 To 0 Step -1
                Array.Clear(row, 0, row.Length)
                Dim srcY = outY \ scale
                For outX = 0 To outputWidth - 1
                    Dim srcX = outX \ scale
                    Dim sourceIndex = (srcY * width + srcX) * 2
                    Dim pixel = CUShort((CUInt(rgb565Be(sourceIndex)) << 8) Or rgb565Be(sourceIndex + 1))
                    Dim r = CByte(((pixel >> 11) And &H1F) * 255 \ 31)
                    Dim g = CByte(((pixel >> 5) And &H3F) * 255 \ 63)
                    Dim b = CByte((pixel And &H1F) * 255 \ 31)
                    Dim targetIndex = outX * 3
                    row(targetIndex) = b
                    row(targetIndex + 1) = g
                    row(targetIndex + 2) = r
                Next
                stream.Write(row, 0, row.Length)
            Next
        End Using
    End Sub

    Private Sub WriteAscii(stream As Stream, value As String)
        Dim bytes = Encoding.ASCII.GetBytes(value)
        stream.Write(bytes, 0, bytes.Length)
    End Sub

    Private Sub WriteUInt16LE(stream As Stream, value As UShort)
        stream.WriteByte(CByte(value And &HFFUS))
        stream.WriteByte(CByte((value >> 8) And &HFFUS))
    End Sub

    Private Sub WriteUInt32LE(stream As Stream, value As UInteger)
        stream.WriteByte(CByte(value And &HFFUI))
        stream.WriteByte(CByte((value >> 8) And &HFFUI))
        stream.WriteByte(CByte((value >> 16) And &HFFUI))
        stream.WriteByte(CByte((value >> 24) And &HFFUI))
    End Sub

    Private Sub WriteInt32LE(stream As Stream, value As Integer)
        WriteUInt32LE(stream, CUInt(value))
    End Sub

    Private Sub PrintUsage()
        Console.WriteLine("PalmEsp32RomTool - convert Palm OS 4-and-earlier ROM contents into an ESP32 compatibility project")
        Console.WriteLine()
        Console.WriteLine("Usage:")
        Console.WriteLine("  PalmEsp32RomTool inspect <rom-file> [--rom-base 0x10C00000]")
        Console.WriteLine("  PalmEsp32RomTool generate <rom-file> <output-dir> [--all] [--app-name ""Memo Pad""] [--creator memo] [--project-name PalmCompatGenerated] [--hardware-profile esp32-palm-m100] [--rom-base 0x10C00000]")
        Console.WriteLine("  PalmEsp32RomTool snapshot <port> [output.bmp] [--baud 115200] [--scale 3] [--timeout-ms 10000]")
        Console.WriteLine("  PalmEsp32RomTool tap-snapshot <port> <x> <y> [output.bmp] [--baud 115200] [--scale 3] [--timeout-ms 10000]")
        Console.WriteLine("  PalmEsp32RomTool tap <port> <x> <y> [--baud 115200] [--timeout-ms 5000]")
        Console.WriteLine("  PalmEsp32RomTool text <port> <memo text> [--baud 115200] [--timeout-ms 5000]")
        Console.WriteLine("  PalmEsp32RomTool trap-map [--markdown]")
        Console.WriteLine()
        Console.WriteLine("Examples:")
        Console.WriteLine("  dotnet run --project tools/vbnet/PalmEsp32RomTool -- inspect Palm-m100-3.51-en.rom")
        Console.WriteLine("  dotnet run --project tools/vbnet/PalmEsp32RomTool -- generate Palm-m100-3.51-en.rom out/PalmCompat --all")
        Console.WriteLine("  dotnet run --project tools/vbnet/PalmEsp32RomTool -- generate Palm-m100-3.51-en.rom out/MemoPad --app-name ""Memo Pad""")
        Console.WriteLine("  dotnet run --project tools/vbnet/PalmEsp32RomTool -- snapshot COM4 out/palm-lcd.bmp")
        Console.WriteLine("  dotnet run --project tools/vbnet/PalmEsp32RomTool -- tap-snapshot COM4 130 12 out/category-popup.bmp")
        Console.WriteLine("  dotnet run --project tools/vbnet/PalmEsp32RomTool -- tap COM4 24 92")
        Console.WriteLine("  dotnet run --project tools/vbnet/PalmEsp32RomTool -- text COM4 ""New memo from UART""")
        Console.WriteLine("  dotnet run --project tools/vbnet/PalmEsp32RomTool -- trap-map --markdown")
    End Sub
End Module
