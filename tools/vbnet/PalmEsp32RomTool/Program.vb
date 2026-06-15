Imports System.Globalization
Imports System.IO
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

    Private Function IsHelp(value As String) As Boolean
        Return value = "-h" OrElse value = "--help" OrElse value = "/?"
    End Function

    Private Sub PrintUsage()
        Console.WriteLine("PalmEsp32RomTool - convert Palm OS 4-and-earlier ROM contents into an ESP32 compatibility project")
        Console.WriteLine()
        Console.WriteLine("Usage:")
        Console.WriteLine("  PalmEsp32RomTool inspect <rom-file> [--rom-base 0x10C00000]")
        Console.WriteLine("  PalmEsp32RomTool generate <rom-file> <output-dir> [--all] [--app-name ""Memo Pad""] [--creator memo] [--project-name PalmCompatGenerated] [--hardware-profile esp32-palm-m100] [--rom-base 0x10C00000]")
        Console.WriteLine()
        Console.WriteLine("Examples:")
        Console.WriteLine("  dotnet run --project tools/vbnet/PalmEsp32RomTool -- inspect Palm-m100-3.51-en.rom")
        Console.WriteLine("  dotnet run --project tools/vbnet/PalmEsp32RomTool -- generate Palm-m100-3.51-en.rom out/PalmCompat --all")
        Console.WriteLine("  dotnet run --project tools/vbnet/PalmEsp32RomTool -- generate Palm-m100-3.51-en.rom out/MemoPad --app-name ""Memo Pad""")
    End Sub
End Module

