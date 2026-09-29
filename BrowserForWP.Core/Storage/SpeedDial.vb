' BrowserForWP — speed dial (pure, no WinRT dependency).
'
' Eight fixed slots for one-tap top sites. Order is insertion order; tapping a
' slot navigates to it. Duplicates and overflow are rejected, never replaced.

Imports System.Collections.Generic

Namespace Storage

    ''' <summary>One speed-dial slot.</summary>
    Public NotInheritable Class SpeedDialEntry

        Public Sub New(pageUrl As String, pageTitle As String)
            Me.Url = If(pageUrl, String.Empty)
            Me.Title = If(pageTitle, String.Empty)
        End Sub

        Public ReadOnly Url As String
        Public ReadOnly Title As String
    End Class

    ''' <summary>Bounded one-tap site list.</summary>
    Public NotInheritable Class SpeedDial

        Public Const MaxSlots As Integer = 8

        Private ReadOnly _items As New List(Of SpeedDialEntry)()

        Public ReadOnly Property Count As Integer
            Get
                Return _items.Count
            End Get
        End Property

        Public Function Add(pageUrl As String, pageTitle As String) As Boolean
            If String.IsNullOrEmpty(pageUrl) Then
                Return False
            End If
            For Each existing In _items
                If existing.Url = pageUrl Then
                    Return False
                End If
            Next
            If _items.Count >= MaxSlots Then
                Return False
            End If
            Dim shownTitle As String = If(pageTitle, pageUrl)
            If String.IsNullOrEmpty(shownTitle) Then
                shownTitle = pageUrl
            End If
            _items.Add(New SpeedDialEntry(pageUrl, shownTitle))
            Return True
        End Function

        Public Function Remove(pageUrl As String) As Boolean
            If String.IsNullOrEmpty(pageUrl) Then
                Return False
            End If
            For i As Integer = 0 To _items.Count - 1
                If _items(i).Url = pageUrl Then
                    _items.RemoveAt(i)
                    Return True
                End If
            Next
            Return False
        End Function

        Public Function List() As IList(Of SpeedDialEntry)
            Return New List(Of SpeedDialEntry)(_items)
        End Function

        Public Sub Clear()
            _items.Clear()
        End Sub

        Public Function Serialize() As String
            Dim lines As New List(Of String)()
            For Each entryItem In _items
                Dim cleanUrl As String = entryItem.Url.Replace("|", "/")
                Dim cleanTitle As String = entryItem.Title.Replace("|", " ")
                lines.Add(cleanUrl & "|" & cleanTitle)
            Next
            Return String.Join(vbLf, lines.ToArray())
        End Function

        Public Sub Parse(savedText As String)
            _items.Clear()
            If String.IsNullOrEmpty(savedText) Then
                Return
            End If
            Dim rawLines As String() = savedText.Split(New String() {vbLf}, StringSplitOptions.None)
            For Each rawLine In rawLines
                rawLine = If(rawLine, String.Empty).TrimEnd()
                If String.IsNullOrEmpty(rawLine) Then
                    Continue For
                End If
                Dim pipePos As Integer = rawLine.IndexOf("|"c)
                If pipePos <= 0 Then
                    Continue For
                End If
                Dim urlPart As String = rawLine.Substring(0, pipePos)
                Dim titlePart As String = rawLine.Substring(pipePos + 1)
                If String.IsNullOrEmpty(urlPart) Then
                    Continue For
                End If
                If _items.Count >= MaxSlots Then
                    Exit For
                End If
                _items.Add(New SpeedDialEntry(urlPart, titlePart))
            Next
        End Sub
    End Class

End Namespace
