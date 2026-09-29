' BrowserForWP — persisted favorites/bookmarks (pure, no WinRT dependency).
'
' Serialization is one entry per line: url + "|" + title.

Imports System.Collections.Generic

Namespace Storage

    ''' <summary>Named bookmark.</summary>
    Public NotInheritable Class FavoriteEntry

        Public Sub New(pageUrl As String, pageTitle As String)
            Me.Url = If(pageUrl, String.Empty)
            Me.Title = If(pageTitle, String.Empty)
        End Sub

        Public ReadOnly Url As String
        Public ReadOnly Title As String
    End Class

    ''' <summary>Bookmark set keyed by URL.</summary>
    Public NotInheritable Class FavoritesStore

        ''' <summary>Maximum bookmarks kept (speed + memory).</summary>
        Public Const MaxEntries As Integer = 50

        Private ReadOnly _items As New Dictionary(Of String, String)()

        Public ReadOnly Property Count As Integer
            Get
                Return _items.Count
            End Get
        End Property

        Public Function Add(pageUrl As String, pageTitle As String) As Boolean
            If String.IsNullOrEmpty(pageUrl) Then
                Return False
            End If
            If _items.ContainsKey(pageUrl) Then
                Return False
            End If
            If _items.Count >= MaxEntries Then
                Return False
            End If
            _items(pageUrl) = If(pageTitle, pageUrl)
            Return True
        End Function

        Public Function Remove(pageUrl As String) As Boolean
            If String.IsNullOrEmpty(pageUrl) Then
                Return False
            End If
            Return _items.Remove(pageUrl)
        End Function

        Public Function Contains(pageUrl As String) As Boolean
            If String.IsNullOrEmpty(pageUrl) Then
                Return False
            End If
            Return _items.ContainsKey(pageUrl)
        End Function

        Public Function List() As IList(Of FavoriteEntry)
            Dim result As New List(Of FavoriteEntry)()
            For Each pairItem In _items
                result.Add(New FavoriteEntry(pairItem.Key, pairItem.Value))
            Next
            Return result
        End Function

        Public Sub Clear()
            _items.Clear()
        End Sub

        Public Function Serialize() As String
            Dim lines As New List(Of String)()
            For Each pairItem In _items
                Dim cleanUrl As String = pairItem.Key.Replace("|", "/")
                Dim cleanTitle As String = pairItem.Value.Replace("|", " ")
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
                _items(urlPart) = titlePart
            Next
        End Sub
    End Class

End Namespace
