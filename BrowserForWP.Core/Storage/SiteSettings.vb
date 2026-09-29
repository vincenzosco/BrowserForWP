' BrowserForWP — per-site display settings (pure, no WinRT dependency).
'
' Keyed by normalized host. Text size is a percentage of the page default;
' ImagesOff hides images on that host. Both apply to the current document only
' after navigation: the OS exposes no subresource filter, so there is nothing
' to preload for the next page.

Imports System.Collections.Generic

Namespace Storage

    ''' <summary>Display tweaks for one host.</summary>
    Public NotInheritable Class SiteSetting

        Public Sub New(hostName As String, textSizePct As Integer, imagesOff As Boolean)
            Me.Host = If(hostName, String.Empty)
            Me.TextSizePct = textSizePct
            Me.ImagesOff = imagesOff
        End Sub

        Public ReadOnly Host As String
        Public ReadOnly TextSizePct As Integer
        Public ReadOnly ImagesOff As Boolean
    End Class

    ''' <summary>Host-keyed display settings, bounded.</summary>
    Public NotInheritable Class SiteSettings

        Public Const MaxSites As Integer = 50
        Public Const MinTextSize As Integer = 50
        Public Const MaxTextSize As Integer = 200
        Public Const DefaultTextSize As Integer = 100

        Private ReadOnly _items As New Dictionary(Of String, SiteSetting)()

        Public ReadOnly Property Count As Integer
            Get
                Return _items.Count
            End Get
        End Property

        Public Shared Function NormalizeHost(hostName As String) As String
            If String.IsNullOrEmpty(hostName) Then
                Return String.Empty
            End If
            Return hostName.Trim().ToLowerInvariant()
        End Function

        Public Shared Function ClampTextSize(pct As Integer) As Integer
            If pct < MinTextSize Then Return MinTextSize
            If pct > MaxTextSize Then Return MaxTextSize
            Return pct
        End Function

        Public Function GetSetting(hostName As String) As SiteSetting
            Dim cleanHost As String = NormalizeHost(hostName)
            Dim found As SiteSetting = Nothing
            If Not String.IsNullOrEmpty(cleanHost) AndAlso _items.TryGetValue(cleanHost, found) Then
                Return found
            End If
            Return New SiteSetting(cleanHost, DefaultTextSize, False)
        End Function

        Public Sub SetSetting(hostName As String, textSizePct As Integer, imagesOff As Boolean)
            Dim cleanHost As String = NormalizeHost(hostName)
            If String.IsNullOrEmpty(cleanHost) Then
                Return
            End If
            If Not _items.ContainsKey(cleanHost) AndAlso _items.Count >= MaxSites Then
                Return
            End If
            _items(cleanHost) = New SiteSetting(cleanHost, ClampTextSize(textSizePct), imagesOff)
        End Sub

        Public Function Remove(hostName As String) As Boolean
            Dim cleanHost As String = NormalizeHost(hostName)
            If String.IsNullOrEmpty(cleanHost) Then
                Return False
            End If
            Return _items.Remove(cleanHost)
        End Function

        Public Sub Clear()
            _items.Clear()
        End Sub

        Public Function Serialize() As String
            Dim lines As New List(Of String)()
            For Each pairItem In _items
                lines.Add(pairItem.Key & "|" & pairItem.Value.TextSizePct.ToString() & "|" & If(pairItem.Value.ImagesOff, "1", "0"))
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
                Dim parts As String() = rawLine.Split("|"c)
                If parts.Length < 3 Then
                    Continue For
                End If
                Dim hostPart As String = NormalizeHost(parts(0))
                Dim pctValue As Integer = DefaultTextSize
                If Not Integer.TryParse(parts(1), pctValue) Then
                    Continue For
                End If
                If String.IsNullOrEmpty(hostPart) Then
                    Continue For
                End If
                If _items.Count >= MaxSites Then
                    Exit For
                End If
                _items(hostPart) = New SiteSetting(hostPart, ClampTextSize(pctValue), parts(2) = "1")
            Next
        End Sub
    End Class

End Namespace
