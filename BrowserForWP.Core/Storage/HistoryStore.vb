' BrowserForWP — persisted history (pure, no WinRT dependency).
'
' Bounded to 50 entries; newest last. Serialization is one entry per line:
' ticks + "|" + url + "|" + title (pipes in fields are stripped).

Imports System.Collections.Generic

Namespace Storage

    ''' <summary>One visited page.</summary>
    Public NotInheritable Class HistoryEntry

        Public Sub New(pageUrl As String, pageTitle As String, visitedTicks As Long)
            Me.Url = If(pageUrl, String.Empty)
            Me.Title = If(pageTitle, String.Empty)
            Me.VisitedTicks = visitedTicks
        End Sub

        Public ReadOnly Url As String
        Public ReadOnly Title As String
        Public ReadOnly VisitedTicks As Long
    End Class

    ''' <summary>Bounded in-memory history with string serialization.</summary>
    Public NotInheritable Class HistoryStore

        Public Const MaxEntries As Integer = 50

        Private ReadOnly _entries As New List(Of HistoryEntry)()

        Public ReadOnly Property Count As Integer
            Get
                Return _entries.Count
            End Get
        End Property

        Public Sub Add(pageUrl As String, pageTitle As String)
            If String.IsNullOrEmpty(pageUrl) Then
                Return
            End If
            _entries.Add(New HistoryEntry(pageUrl, If(pageTitle, String.Empty), DateTime.UtcNow.Ticks))
            While _entries.Count > MaxEntries
                _entries.RemoveAt(0)
            End While
        End Sub

        ''' <summary>
        ''' A snapshot copy. List(Of T).AsReadOnly() does not exist in the
        ''' ".NET for Windows Store apps" profile -- ReadOnlyCollection(Of T) is not
        ''' part of it -- so that call fails as BC30456 rather than degrading.
        ''' Copying is the profile-safe equivalent, and it is what the build proved.
        ''' </summary>
        Public Function List() As IList(Of HistoryEntry)
            Return New List(Of HistoryEntry)(_entries)
        End Function

        Public Sub Clear()
            _entries.Clear()
        End Sub

        Public Function Serialize() As String
            Dim lines As New List(Of String)()
            For Each entryItem In _entries
                Dim cleanUrl As String = entryItem.Url.Replace("|", "/")
                Dim cleanTitle As String = entryItem.Title.Replace("|", " ")
                lines.Add(entryItem.VisitedTicks.ToString() & "|" & cleanUrl & "|" & cleanTitle)
            Next
            Return String.Join(vbLf, lines.ToArray())
        End Function

        Public Sub Parse(savedText As String)
            _entries.Clear()
            If String.IsNullOrEmpty(savedText) Then
                Return
            End If
            Dim rawLines As String() = savedText.Split(New String() {vbLf}, StringSplitOptions.None)
            For Each rawLine In rawLines
                If String.IsNullOrEmpty(rawLine) Then
                    Continue For
                End If
                Dim pipePos As Integer = rawLine.IndexOf("|"c)
                If pipePos <= 0 Then
                    Continue For
                End If
                Dim ticksPart As String = rawLine.Substring(0, pipePos)
                Dim restPart As String = rawLine.Substring(pipePos + 1)
                Dim secondPipe As Integer = restPart.IndexOf("|"c)
                Dim urlPart As String
                Dim titlePart As String
                If secondPipe < 0 Then
                    urlPart = restPart
                    titlePart = String.Empty
                Else
                    urlPart = restPart.Substring(0, secondPipe)
                    titlePart = restPart.Substring(secondPipe + 1)
                End If
                Dim tickValue As Long = 0
                If Not Long.TryParse(ticksPart, tickValue) Then
                    Continue For
                End If
                If String.IsNullOrEmpty(urlPart) Then
                    Continue For
                End If
                _entries.Add(New HistoryEntry(urlPart, titlePart, tickValue))
            Next
            While _entries.Count > MaxEntries
                _entries.RemoveAt(0)
            End While
        End Sub
    End Class

End Namespace
