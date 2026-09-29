' BrowserForWP — offline saved pages (pure, no WinRT dependency).
'
' A saved page is the article text extracted on-device, not the live URL: it
' renders with no network at all. Text is truncated and flattened to one line
' so serialization stays one entry per line like the other stores.

Imports System.Collections.Generic

Namespace Storage

    ''' <summary>One offline article.</summary>
    Public NotInheritable Class SavedPageEntry

        Public Sub New(pageUrl As String, pageTitle As String, pageText As String, savedTicks As Long)
            Me.Url = If(pageUrl, String.Empty)
            Me.Title = If(pageTitle, String.Empty)
            Me.Text = If(pageText, String.Empty)
            Me.SavedTicks = savedTicks
        End Sub

        Public ReadOnly Url As String
        Public ReadOnly Title As String
        Public ReadOnly Text As String
        Public ReadOnly SavedTicks As Long
    End Class

    ''' <summary>Bounded offline article store.</summary>
    Public NotInheritable Class SavedPages

        Public Const MaxEntries As Integer = 20
        Public Const MaxTextChars As Integer = 8000

        Private ReadOnly _entries As New List(Of SavedPageEntry)()

        Public ReadOnly Property Count As Integer
            Get
                Return _entries.Count
            End Get
        End Property

        Public Sub Add(pageUrl As String, pageTitle As String, pageText As String)
            If String.IsNullOrEmpty(pageUrl) Then
                Return
            End If
            Dim flatText As String = Flatten(If(pageText, String.Empty))
            If flatText.Length > MaxTextChars Then
                flatText = flatText.Substring(0, MaxTextChars)
            End If
            _entries.Add(New SavedPageEntry(pageUrl, Flatten(If(pageTitle, pageUrl)), flatText, DateTime.UtcNow.Ticks))
            While _entries.Count > MaxEntries
                _entries.RemoveAt(0)
            End While
        End Sub

        Public Function List() As IList(Of SavedPageEntry)
            Return New List(Of SavedPageEntry)(_entries)
        End Function

        Public Sub RemoveAt(index As Integer)
            If index < 0 OrElse index >= _entries.Count Then
                Return
            End If
            _entries.RemoveAt(index)
        End Sub

        Public Sub Clear()
            _entries.Clear()
        End Sub

        Private Shared Function Flatten(rawText As String) As String
            Dim flat As String = rawText.Replace(vbCr, " ").Replace(vbLf, " ").Replace("|", " ")
            While flat.Contains("  ")
                flat = flat.Replace("  ", " ")
            End While
            Return flat.Trim()
        End Function

        Public Function Serialize() As String
            Dim lines As New List(Of String)()
            For Each entryItem In _entries
                lines.Add(entryItem.SavedTicks.ToString() & "|" & entryItem.Url & "|" & entryItem.Title & "|" & entryItem.Text)
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
                rawLine = If(rawLine, String.Empty).TrimEnd()
                If String.IsNullOrEmpty(rawLine) Then
                    Continue For
                End If
                Dim firstPipe As Integer = rawLine.IndexOf("|"c)
                If firstPipe <= 0 Then
                    Continue For
                End If
                Dim ticksPart As String = rawLine.Substring(0, firstPipe)
                Dim rest As String = rawLine.Substring(firstPipe + 1)
                Dim secondPipe As Integer = rest.IndexOf("|"c)
                If secondPipe <= 0 Then
                    Continue For
                End If
                Dim urlPart As String = rest.Substring(0, secondPipe)
                Dim rest2 As String = rest.Substring(secondPipe + 1)
                Dim thirdPipe As Integer = rest2.IndexOf("|"c)
                Dim titlePart As String
                Dim textPart As String
                If thirdPipe < 0 Then
                    titlePart = rest2
                    textPart = String.Empty
                Else
                    titlePart = rest2.Substring(0, thirdPipe)
                    textPart = rest2.Substring(thirdPipe + 1)
                End If
                Dim tickValue As Long = 0
                If Not Long.TryParse(ticksPart, tickValue) Then
                    Continue For
                End If
                If String.IsNullOrEmpty(urlPart) Then
                    Continue For
                End If
                _entries.Add(New SavedPageEntry(urlPart, titlePart, textPart, tickValue))
            Next
            While _entries.Count > MaxEntries
                _entries.RemoveAt(0)
            End While
        End Sub
    End Class

End Namespace
