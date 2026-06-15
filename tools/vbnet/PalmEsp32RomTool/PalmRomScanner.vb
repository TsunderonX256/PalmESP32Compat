Imports System.IO
Imports System.Text

Public NotInheritable Class PalmRomScanner
    Private Sub New()
    End Sub

    Public Shared Function ReadDatabases(romPath As String, romBase As UInteger?) As List(Of PalmDatabase)
        If Not File.Exists(romPath) Then
            Throw New FileNotFoundException("ROM dump not found", romPath)
        End If

        Return FindDatabases(File.ReadAllBytes(romPath), romBase)
    End Function

    Public Shared Function FindDatabases(rom As Byte(), romBase As UInteger?) As List(Of PalmDatabase)
        Dim databases As New List(Of PalmDatabase)()
        Dim effectiveRomBase = If(romBase, GuessRomBase(rom))

        For offset = 0 To rom.Length - PalmDatabaseConstants.DatabaseHeaderSize
            Dim candidate = TryReadDatabase(rom, offset, effectiveRomBase)
            If candidate IsNot Nothing Then
                databases.Add(candidate)
            End If
        Next

        databases = databases.OrderBy(Function(db) db.StartOffset).ToList()

        Dim knownFileOffsets = databases.
            SelectMany(Function(db) db.EntryFileOffsets).
            Distinct().
            OrderBy(Function(offset) offset).
            ToList()

        For index = 0 To databases.Count - 1
            Dim db = databases(index)
            db.BoundaryOffset = If(index + 1 < databases.Count, databases(index + 1).StartOffset, rom.Length)
            db.KnownFileOffsets = knownFileOffsets
            db.Length = EstimateExportLength(rom.Length, db)
        Next

        Return databases
    End Function

    Public Shared Sub ExportDatabase(rom As Byte(), db As PalmDatabase, outputPath As String)
        Dim entrySize = If(db.IsResourceDatabase, PalmDatabaseConstants.ResourceEntrySize, PalmDatabaseConstants.RecordEntrySize)
        Dim headerLength = PalmDatabaseConstants.DatabaseHeaderSize + CInt(db.EntryCount) * entrySize
        Dim entryRanges = GetEntryRanges(rom.Length, db)
        Dim outputLength = headerLength + entryRanges.Sum(Function(range) range.Length)
        Dim output(outputLength - 1) As Byte

        Buffer.BlockCopy(rom, db.StartOffset, output, 0, headerLength)
        SanitizeInstallHeader(output)

        Dim writeOffset = headerLength
        For i = 0 To entryRanges.Count - 1
            Dim entryOffset = PalmDatabaseConstants.DatabaseHeaderSize + i * entrySize + If(db.IsResourceDatabase, 6, 0)
            WriteUInt32BE(output, entryOffset, CUInt(writeOffset))

            Dim range = entryRanges(i)
            Buffer.BlockCopy(rom, range.Start, output, writeOffset, range.Length)
            writeOffset += range.Length
        Next

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath))
        File.WriteAllBytes(outputPath, output)
    End Sub

    Public Shared Function MakeSafeFileName(value As String) As String
        Dim invalid = Path.GetInvalidFileNameChars()
        Dim chars = value.Select(Function(ch) If(invalid.Contains(ch), "_"c, ch)).ToArray()
        Return New String(chars)
    End Function

    Private Shared Function TryReadDatabase(rom As Byte(), startOffset As Integer, romBase As UInteger?) As PalmDatabase
        Dim name = ReadNullTerminatedAscii(rom, startOffset, 32)
        If String.IsNullOrWhiteSpace(name) OrElse Not IsMostlyPrintable(name) Then
            Return Nothing
        End If

        Dim attributes = ReadUInt16BE(rom, startOffset + 32)
        Dim version = ReadUInt16BE(rom, startOffset + 34)
        Dim typeCode = ReadAscii(rom, startOffset + 60, 4)
        Dim creatorCode = ReadAscii(rom, startOffset + 64, 4)
        Dim nextRecordListId = ReadUInt32BE(rom, startOffset + 72)
        Dim entryCount = ReadUInt16BE(rom, startOffset + 76)
        Dim isResourceDb = (attributes And PalmDatabaseConstants.ResourceDatabaseAttribute) <> 0
        Dim entrySize = If(isResourceDb, PalmDatabaseConstants.ResourceEntrySize, PalmDatabaseConstants.RecordEntrySize)
        Dim entriesEnd = PalmDatabaseConstants.DatabaseHeaderSize + CInt(entryCount) * entrySize

        If entryCount = 0US OrElse entryCount > 4096US Then
            Return Nothing
        End If

        If startOffset + entriesEnd > rom.Length OrElse nextRecordListId <> 0UI Then
            Return Nothing
        End If

        If Not IsFourCc(typeCode) OrElse Not IsFourCc(creatorCode) Then
            Return Nothing
        End If

        Dim rawOffsets As New List(Of UInteger)()
        For i = 0 To CInt(entryCount) - 1
            Dim entryOffset = startOffset + PalmDatabaseConstants.DatabaseHeaderSize + i * entrySize
            Dim rawDataOffset = If(isResourceDb, ReadUInt32BE(rom, entryOffset + 6), ReadUInt32BE(rom, entryOffset))
            rawOffsets.Add(rawDataOffset)
        Next

        Dim normalized = NormalizeOffsets(rawOffsets, startOffset, entriesEnd, rom.Length, romBase)
        If normalized Is Nothing Then
            Return Nothing
        End If

        Dim entryFileOffsets = normalized.Select(Function(item) item.FileOffset).ToList()
        Dim entryListEnd = startOffset + entriesEnd
        If entryFileOffsets.Any(Function(fileOffset) fileOffset >= startOffset AndAlso fileOffset < entryListEnd) Then
            Return Nothing
        End If

        Return New PalmDatabase With {
            .StartOffset = startOffset,
            .Name = name,
            .Attributes = attributes,
            .Version = version,
            .TypeCode = typeCode,
            .CreatorCode = creatorCode,
            .EntryCount = entryCount,
            .EntryFileOffsets = entryFileOffsets,
            .OffsetMode = normalized(0).Mode
        }
    End Function

    Private Shared Function NormalizeOffsets(rawOffsets As List(Of UInteger), startOffset As Integer, entriesEnd As Integer, romLength As Integer, romBase As UInteger?) As List(Of NormalizedOffset)
        Dim relative = TryNormalizeOffsets(rawOffsets, Function(raw) CLng(startOffset) + raw, romLength, "relative")
        If relative IsNot Nothing Then
            Return relative
        End If

        Dim fileAbsolute = TryNormalizeOffsets(rawOffsets, Function(raw) CLng(raw), romLength, "file-absolute")
        If fileAbsolute IsNot Nothing Then
            Return fileAbsolute
        End If

        If romBase.HasValue Then
            Dim baseValue = CLng(romBase.Value)
            Dim explicitBase = TryNormalizeOffsets(rawOffsets, Function(raw) CLng(raw) - baseValue, romLength, "rom-base")
            If explicitBase IsNot Nothing Then
                Return explicitBase
            End If
        End If

        Dim inferredRomBase = InferRomBase(rawOffsets, romLength)
        If inferredRomBase.HasValue Then
            Dim baseValue = CLng(inferredRomBase.Value)
            Dim inferred = TryNormalizeOffsets(rawOffsets, Function(raw) CLng(raw) - baseValue, romLength, $"rom-base:0x{inferredRomBase.Value:X8}")
            If inferred IsNot Nothing Then
                Return inferred
            End If
        End If

        Return Nothing
    End Function

    Private Shared Function TryNormalizeOffsets(rawOffsets As List(Of UInteger), toFileOffset As Func(Of UInteger, Long), romLength As Integer, mode As String) As List(Of NormalizedOffset)
        Dim normalized As New List(Of NormalizedOffset)()

        For Each raw In rawOffsets
            Dim fileOffset = toFileOffset(raw)
            If fileOffset < 0 OrElse fileOffset >= romLength Then
                Return Nothing
            End If

            normalized.Add(New NormalizedOffset(CInt(fileOffset), mode))
        Next

        Return normalized
    End Function

    Private Shared Function InferRomBase(rawOffsets As List(Of UInteger), romLength As Integer) As UInteger?
        Dim minimumRaw = rawOffsets.Min()
        Dim candidate = minimumRaw And &HFFF00000UI

        If candidate = 0UI Then
            Return Nothing
        End If

        If rawOffsets.All(Function(raw) raw >= candidate AndAlso CLng(raw) - candidate < romLength) Then
            Return candidate
        End If

        Return Nothing
    End Function

    Private Shared Function GuessRomBase(rom As Byte()) As UInteger?
        If rom.Length < 92 Then
            Return Nothing
        End If

        Dim ramList = ReadUInt32BE(rom, 88)
        If ramList < &H200UI Then
            Return Nothing
        End If

        Dim guessedBase = ramList - &H200UI
        If guessedBase = 0UI Then
            Return Nothing
        End If

        Return guessedBase
    End Function

    Private Shared Function EstimateExportLength(romLength As Integer, db As PalmDatabase) As Integer
        Dim entrySize = If(db.IsResourceDatabase, PalmDatabaseConstants.ResourceEntrySize, PalmDatabaseConstants.RecordEntrySize)
        Dim headerLength = PalmDatabaseConstants.DatabaseHeaderSize + CInt(db.EntryCount) * entrySize
        Return headerLength + GetEntryRanges(romLength, db).Sum(Function(range) range.Length)
    End Function

    Private Shared Function GetEntryRanges(romLength As Integer, db As PalmDatabase) As List(Of EntryRange)
        Dim sortedOffsets = db.EntryFileOffsets.Distinct().OrderBy(Function(offset) offset).ToList()
        Dim headerStart = db.StartOffset
        Dim rangesByStart As New Dictionary(Of Integer, Integer)()

        For Each current In sortedOffsets
            Dim nextKnownOffset = db.KnownFileOffsets.FirstOrDefault(Function(offset) offset > current)
            Dim nextOffset As Integer

            If nextKnownOffset > 0 Then
                nextOffset = nextKnownOffset
            ElseIf current < headerStart Then
                nextOffset = headerStart
            Else
                nextOffset = If(db.BoundaryOffset > current, db.BoundaryOffset, romLength)
            End If

            If nextOffset <= current Then
                Throw New InvalidDataException($"invalid resource range for {db.Name}")
            End If

            rangesByStart(current) = nextOffset - current
        Next

        Return db.EntryFileOffsets.Select(Function(offset) New EntryRange(offset, rangesByStart(offset))).ToList()
    End Function

    Private Shared Sub SanitizeInstallHeader(output As Byte())
        Dim attributes = ReadUInt16BE(output, 32)
        attributes = CUShort(attributes And Not PalmDatabaseConstants.ReadOnlyDatabaseAttribute)
        attributes = CUShort(attributes And Not PalmDatabaseConstants.OpenDatabaseAttribute)
        WriteUInt16BE(output, 32, attributes)
        WriteUInt32BE(output, 72, 0UI)
    End Sub

    Private Shared Function ReadUInt16BE(data As Byte(), offset As Integer) As UShort
        Return CUShort((CUInt(data(offset)) << 8) Or data(offset + 1))
    End Function

    Private Shared Function ReadUInt32BE(data As Byte(), offset As Integer) As UInteger
        Return (CUInt(data(offset)) << 24) Or (CUInt(data(offset + 1)) << 16) Or (CUInt(data(offset + 2)) << 8) Or data(offset + 3)
    End Function

    Private Shared Sub WriteUInt16BE(data As Byte(), offset As Integer, value As UShort)
        data(offset) = CByte((value >> 8) And &HFFUS)
        data(offset + 1) = CByte(value And &HFFUS)
    End Sub

    Private Shared Sub WriteUInt32BE(data As Byte(), offset As Integer, value As UInteger)
        data(offset) = CByte((value >> 24) And &HFFUI)
        data(offset + 1) = CByte((value >> 16) And &HFFUI)
        data(offset + 2) = CByte((value >> 8) And &HFFUI)
        data(offset + 3) = CByte(value And &HFFUI)
    End Sub

    Private Shared Function ReadAscii(data As Byte(), offset As Integer, count As Integer) As String
        Return Encoding.ASCII.GetString(data, offset, count)
    End Function

    Private Shared Function ReadNullTerminatedAscii(data As Byte(), offset As Integer, maximumLength As Integer) As String
        Dim length = 0
        While length < maximumLength AndAlso data(offset + length) <> 0
            length += 1
        End While

        Return Encoding.ASCII.GetString(data, offset, length).Trim()
    End Function

    Private Shared Function IsFourCc(value As String) As Boolean
        Return value.Length = 4 AndAlso value.All(Function(ch) ch >= " "c AndAlso ch <= "~"c)
    End Function

    Private Shared Function IsMostlyPrintable(value As String) As Boolean
        Dim printable = value.Count(Function(ch) ch >= " "c AndAlso ch <= "~"c)
        Return printable = value.Length
    End Function

    Private NotInheritable Class NormalizedOffset
        Public Sub New(fileOffset As Integer, mode As String)
            Me.FileOffset = fileOffset
            Me.Mode = mode
        End Sub

        Public ReadOnly Property FileOffset As Integer
        Public ReadOnly Property Mode As String
    End Class

    Private NotInheritable Class EntryRange
        Public Sub New(start As Integer, length As Integer)
            Me.Start = start
            Me.Length = length
        End Sub

        Public ReadOnly Property Start As Integer
        Public ReadOnly Property Length As Integer
    End Class
End Class

