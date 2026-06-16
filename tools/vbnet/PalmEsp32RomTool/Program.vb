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
        Yield New TrapCoverageRow("0xA012", "MemChunkFree", "Memory", "stubbed", "Returns success for now; real heap ownership is still missing.")
        Yield New TrapCoverageRow("0xA013", "MemPtrNew", "Memory", "named only", "Seen in CoreTraps.h; not yet backed by the fake heap dispatcher.")
        Yield New TrapCoverageRow("0xA020", "MemHandleToLocalID", "Memory", "compat shim", "Currently returns the fake handle value as its local id.")
        Yield New TrapCoverageRow("0xA021", "MemHandleLock", "Memory", "working shim", "Maps fake record/resource/allocation handles to trap heap pointers.")
        Yield New TrapCoverageRow("0xA022", "MemHandleUnlock", "Memory", "stubbed", "Returns success; lock counts are not tracked yet.")
        Yield New TrapCoverageRow("0xA02B", "MemHandleFree", "Memory", "stubbed", "Returns success for fake handles.")
        Yield New TrapCoverageRow("0xA041", "DmCreateDatabase", "Database", "working shim", "Creates the synthetic MemoDB when Memo Pad asks for DATA/memo.")
        Yield New TrapCoverageRow("0xA04A", "DmCloseDatabase", "Database", "stubbed", "Returns success for the synthetic MemoDB.")
        Yield New TrapCoverageRow("0xA049", "DmOpenDatabase", "Database", "forced create path", "Currently returns null so Memo Pad initializes the fake database.")
        Yield New TrapCoverageRow("0xA04C", "DmOpenDatabaseInfo", "Database", "partial shim", "Writes zeroed metadata for now.")
        Yield New TrapCoverageRow("0xA04F", "DmNumRecords", "Database", "working shim", "Returns the synthetic memo record count.")
        Yield New TrapCoverageRow("0xA050", "DmRecordInfo", "Database", "working shim", "Writes attributes, unique id, and fake chunk handle.")
        Yield New TrapCoverageRow("0xA05B", "DmQueryRecord", "Database", "working shim", "Seeds the trap heap with the requested fake memo text.")
        Yield New TrapCoverageRow("0xA05C", "DmGetRecord", "Database", "working shim", "Same fake record handle path as query.")
        Yield New TrapCoverageRow("0xA05E", "DmReleaseRecord", "Database", "stubbed", "Returns success; dirty writes are ignored.")
        Yield New TrapCoverageRow("0xA05F", "DmGetResource", "Resource", "partial shim", "Loads exported resource bytes or synthetic tSTR text.")
        Yield New TrapCoverageRow("0xA061", "DmReleaseResource", "Resource", "stubbed", "Returns success for fake resource handles.")
        Yield New TrapCoverageRow("0xA071", "DmNumRecordsInCategory", "Database", "working shim", "Publishes list rows and returns the synthetic count.")
        Yield New TrapCoverageRow("0xA075", "DmOpenDatabaseByTypeCreator", "Database", "working shim", "Opens DATA/memo only after synthetic create.")
        Yield New TrapCoverageRow("0xA08F", "SysAppStartup", "System", "working shim", "Builds a minimal launch block for startup glue.")
        Yield New TrapCoverageRow("0xA090", "SysAppExit", "System", "working shim", "Stops the probe run cleanly.")
        Yield New TrapCoverageRow("0xA0A9", "SysHandleEvent", "Events", "probe shim", "Returns false while the native LCD probe keeps the Memo Pad surface alive.")
        Yield New TrapCoverageRow("0xA104", "CategoryGetName", "Categories", "working shim", "Writes All or Unfiled into the caller's category buffer.")
        Yield New TrapCoverageRow("0xA11D", "EvtGetEvent", "Events", "partial shim", "Writes a minimal nil event and advances the native LCD probe.")
        Yield New TrapCoverageRow("0xA19B", "FrmGotoForm", "Forms", "probe shim", "Used as a form/title signal for the current Memo Pad LCD surface.")
        Yield New TrapCoverageRow("0xA1A0", "FrmDispatchEvent", "Forms", "probe shim", "Returns false for now; real form dispatch is still missing.")
        Yield New TrapCoverageRow("0xA1BF", "MenuHandleEvent", "Menus", "probe shim", "Returns false for now; menu behavior is not implemented.")
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
        Console.WriteLine("  PalmEsp32RomTool trap-map [--markdown]")
        Console.WriteLine()
        Console.WriteLine("Examples:")
        Console.WriteLine("  dotnet run --project tools/vbnet/PalmEsp32RomTool -- inspect Palm-m100-3.51-en.rom")
        Console.WriteLine("  dotnet run --project tools/vbnet/PalmEsp32RomTool -- generate Palm-m100-3.51-en.rom out/PalmCompat --all")
        Console.WriteLine("  dotnet run --project tools/vbnet/PalmEsp32RomTool -- generate Palm-m100-3.51-en.rom out/MemoPad --app-name ""Memo Pad""")
        Console.WriteLine("  dotnet run --project tools/vbnet/PalmEsp32RomTool -- snapshot COM4 out/palm-lcd.bmp")
        Console.WriteLine("  dotnet run --project tools/vbnet/PalmEsp32RomTool -- trap-map --markdown")
    End Sub
End Module
