' BrowserForWP — per-site certificate pins (pure, no WinRT here).
'
' A pin is the Base64 of SHA256 over the leaf SPKI bytes (PublicKeyBlob).
' Storage persistence lives in the app layer; this class holds the map and the
' comparison so it stays unit-testable. Node mirror: tools/proto/pinstore.mjs.

Imports System.Collections.Generic

Namespace Tls13

    ''' <summary>User-managed per-site pins with explicit reversible override.</summary>
    Public NotInheritable Class PinStore

        Private ReadOnly _pins As New Dictionary(Of String, String)()

        ''' <summary>Maximum pins kept (speed + memory).</summary>
        Public Const MaxPins As Integer = 25

        Public ReadOnly Property Count As Integer
            Get
                Return _pins.Count
            End Get
        End Property

        ''' <summary>Normalize a host: lowercase, no trailing dot, no port.</summary>
        Public Shared Function NormalizeHost(hostName As String) As String
            If String.IsNullOrEmpty(hostName) Then
                Return String.Empty
            End If
            Dim cleanHost As String = hostName.Trim().ToLowerInvariant()
            Dim colonPos As Integer = cleanHost.IndexOf(":"c)
            If colonPos >= 0 Then
                cleanHost = cleanHost.Substring(0, colonPos)
            End If
            While cleanHost.EndsWith(".")
                cleanHost = cleanHost.Substring(0, cleanHost.Length - 1)
            End While
            Return cleanHost
        End Function

        Public Sub Add(hostName As String, base64Pin As String)
            Dim cleanHost As String = NormalizeHost(hostName)
            If String.IsNullOrEmpty(cleanHost) Then
                Throw New ArgumentException("host required", "hostName")
            End If
            If String.IsNullOrEmpty(base64Pin) Then
                Throw New ArgumentException("pin required", "base64Pin")
            End If
            If Not _pins.ContainsKey(cleanHost) AndAlso _pins.Count >= MaxPins Then
                Throw New InvalidOperationException("pin store full")
            End If
            _pins(cleanHost) = base64Pin
        End Sub

        Public Function Remove(hostName As String) As Boolean
            Dim cleanHost As String = NormalizeHost(hostName)
            If String.IsNullOrEmpty(cleanHost) Then
                Return False
            End If
            Return _pins.Remove(cleanHost)
        End Function

        Public Function TryGet(hostName As String, ByRef base64Pin As String) As Boolean
            base64Pin = Nothing
            Dim cleanHost As String = NormalizeHost(hostName)
            If String.IsNullOrEmpty(cleanHost) Then
                Return False
            End If
            Return _pins.TryGetValue(cleanHost, base64Pin)
        End Function

        Public Function Contains(hostName As String) As Boolean
            Dim dummyPin As String = Nothing
            Return TryGet(hostName, dummyPin)
        End Function

        Public Sub Clear()
            _pins.Clear()
        End Sub

        ''' <summary>True when no pin is stored for the host, or the pin matches.</summary>
        Public Function Verify(hostName As String, presentedBase64Pin As String) As Boolean
            Dim expectedPin As String = Nothing
            If Not TryGet(hostName, expectedPin) Then
                Return True
            End If
            If String.IsNullOrEmpty(presentedBase64Pin) Then
                Return False
            End If
            Return String.Equals(expectedPin, presentedBase64Pin, StringComparison.Ordinal)
        End Function

        Public Function Serialize() As String
            Dim lines As New List(Of String)()
            For Each pairItem In _pins
                lines.Add(pairItem.Key & "|" & pairItem.Value)
            Next
            Return String.Join(vbLf, lines.ToArray())
        End Function

        Public Sub Parse(savedText As String)
            _pins.Clear()
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
                Dim hostPart As String = NormalizeHost(rawLine.Substring(0, pipePos))
                Dim pinPart As String = rawLine.Substring(pipePos + 1)
                If String.IsNullOrEmpty(hostPart) OrElse String.IsNullOrEmpty(pinPart) Then
                    Continue For
                End If
                If _pins.Count >= MaxPins Then
                    Exit For
                End If
                _pins(hostPart) = pinPart
            Next
        End Sub
    End Class

End Namespace
