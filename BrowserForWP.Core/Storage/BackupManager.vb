' BrowserForWP — backup file encode/decode (pure, no WinRT dependency).
'
' The file pickers and the read/write live in the app layer; this class owns
' the format so it round-trips off-device. Layout: a header line, then one
' "[section]base64" line per section. Base64 carries embedded newlines
' (session tabs) without an escaping scheme to get wrong.

Imports System.Collections.Generic
Imports System.Text

Namespace Storage

    ''' <summary>Backup file format, versioned by its header line.</summary>
    Public NotInheritable Class BackupManager

        Public Const HeaderLine As String = "BROWSERFORWP-BACKUP-1"

        Public Shared ReadOnly SectionNames As String() = {
            "settings", "history", "favorites", "pins", "saved", "sites", "speeddial"
        }

        Private Sub New()
        End Sub

        Public Shared Function BuildBackup(sections As IDictionary(Of String, String)) As String
            Dim lines As New List(Of String)()
            lines.Add(HeaderLine)
            For Each sectionName In SectionNames
                Dim payload As String = String.Empty
                If sections IsNot Nothing AndAlso sections.ContainsKey(sectionName) Then
                    payload = If(sections(sectionName), String.Empty)
                End If
                lines.Add("[" & sectionName & "]" & ToBase64(payload))
            Next
            Return String.Join(vbLf, lines.ToArray())
        End Function

        Public Shared Function TryParseBackup(backupText As String, ByRef sections As Dictionary(Of String, String)) As Boolean
            sections = New Dictionary(Of String, String)()
            If String.IsNullOrEmpty(backupText) Then
                Return False
            End If
            Dim rawLines As String() = backupText.Split(New String() {vbLf}, StringSplitOptions.None)
            If rawLines.Length = 0 OrElse rawLines(0).TrimEnd() <> HeaderLine Then
                Return False
            End If
            For i As Integer = 1 To rawLines.Length - 1
                Dim cleanLine As String = If(rawLines(i), String.Empty).TrimEnd()
                If String.IsNullOrEmpty(cleanLine) Then
                    Continue For
                End If
                If Not cleanLine.StartsWith("[") Then
                    Return False
                End If
                Dim closePos As Integer = cleanLine.IndexOf("]"c)
                If closePos <= 1 Then
                    Return False
                End If
                Dim sectionName As String = cleanLine.Substring(1, closePos - 1)
                Dim known As Boolean = False
                For Each candidate In SectionNames
                    If candidate = sectionName Then
                        known = True
                        Exit For
                    End If
                Next
                If Not known Then
                    Return False
                End If
                Dim decoded As String = FromBase64(cleanLine.Substring(closePos + 1))
                If decoded Is Nothing Then
                    Return False
                End If
                sections(sectionName) = decoded
            Next
            Return True
        End Function

        Private Shared Function ToBase64(plainText As String) As String
            Dim bytes As Byte() = Encoding.UTF8.GetBytes(If(plainText, String.Empty))
            Return Convert.ToBase64String(bytes)
        End Function

        Private Shared Function FromBase64(encoded As String) As String
            Try
                If String.IsNullOrEmpty(encoded) Then
                    Return String.Empty
                End If
                Dim bytes As Byte() = Convert.FromBase64String(encoded)
                Return Encoding.UTF8.GetString(bytes, 0, bytes.Length)
            Catch ex As Exception
                Return Nothing
            End Try
        End Function
    End Class

End Namespace
