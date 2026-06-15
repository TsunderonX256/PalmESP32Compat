Public NotInheritable Class PalmDatabase
    Public Property StartOffset As Integer
    Public Property BoundaryOffset As Integer
    Public Property Length As Integer
    Public Property Name As String = ""
    Public Property Attributes As UShort
    Public Property Version As UShort
    Public Property TypeCode As String = ""
    Public Property CreatorCode As String = ""
    Public Property EntryCount As UShort
    Public Property EntryFileOffsets As List(Of Integer) = New List(Of Integer)()
    Public Property KnownFileOffsets As List(Of Integer) = New List(Of Integer)()
    Public Property OffsetMode As String = ""

    Public ReadOnly Property IsResourceDatabase As Boolean
        Get
            Return (Attributes And PalmDatabaseConstants.ResourceDatabaseAttribute) <> 0
        End Get
    End Property

    Public ReadOnly Property IsApplication As Boolean
        Get
            Return IsResourceDatabase AndAlso TypeCode = "appl"
        End Get
    End Property
End Class

Public Module PalmDatabaseConstants
    Public Const DatabaseHeaderSize As Integer = 78
    Public Const ResourceEntrySize As Integer = 10
    Public Const RecordEntrySize As Integer = 8
    Public Const ResourceDatabaseAttribute As UShort = &H1US
    Public Const ReadOnlyDatabaseAttribute As UShort = &H2US
    Public Const OpenDatabaseAttribute As UShort = &H8000US
End Module

