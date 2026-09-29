' BrowserForWP — TLS probe runner (app layer, may reference Core and Net).
'
' This is the missing Task 15 Step 2 file: it connects Tls13Client,
' HttpClient13 and DohResolver to the Settings UI so "the app speaks TLS 1.3"
' describes the browser, not just the library. Layering is intact because this
' file lives in the app, which depends on all libraries.

Imports System.Threading.Tasks
Imports BrowserForWP.Core.Diagnostics
Imports BrowserForWP.Net.Dns
Imports BrowserForWP.Net.Http
Imports BrowserForWP.Net.Tls13

Namespace Diagnostics

    ''' <summary>Runs a live TLS 1.3 handshake plus one HTTPS GET.</summary>
    Public NotInheritable Class TlsProbeRunner

        Private Sub New()
        End Sub

        ''' <summary>Probe a host; never throws, the result carries the detail.</summary>
        Public Shared Async Function RunAsync(hostName As String, dohUrl As String, pinTable As PinStore) As Task(Of TlsProbeResult)
            Dim cleanHost As String = If(hostName, String.Empty).Trim().ToLowerInvariant()
            If String.IsNullOrEmpty(cleanHost) Then
                Return New TlsProbeResult(String.Empty, String.Empty, String.Empty, String.Empty, False, "host required")
            End If
            Dim dohEndpoint As String = If(String.IsNullOrEmpty(dohUrl), DohResolver.DefaultServerUrl, dohUrl)
            Try
                Using doh As New DohResolver(dohEndpoint)
                    Try
                        Await doh.ResolveAsync(cleanHost)
                    Catch ex As Exception
                        ' Resolution failure is informational; the handshake below
                        ' still attempts the OS path so the user gets a real error.
                    End Try
                End Using
                Using web As New HttpClient13(cleanHost, 443)
                    Dim response As HttpResponse = Await web.GetAsync("https://" & cleanHost & "/")
                    Dim session As TlsSessionInfo = web.SessionInfo
                    If session Is Nothing Then
                        Return New TlsProbeResult(cleanHost, String.Empty, String.Empty, String.Empty, False, "no session")
                    End If
                    Dim pinOk As Boolean = True
                    Dim pinNote As String = String.Empty
                    If pinTable IsNot Nothing AndAlso pinTable.Contains(cleanHost) Then
                        ' The pin comparison lives in CertificateValidator; the
                        ' runner deliberately does not carry a second copy that
                        ' could drift from it.
                        pinOk = CertificateValidator.VerifyPin(session.LeafCertificateDer, cleanHost, pinTable)
                        pinNote = If(pinOk, " pin-match", " pin-MISMATCH")
                    End If
                    Dim suiteHex As String = "0x" & session.CipherSuite.ToString("X4")
                    Dim certOk As Boolean = session.CertificateValid AndAlso pinOk
                    Dim detailText As String = "HTTP " & response.StatusCode & " chain=" & session.CertificateChainStatus & pinNote
                    Return New TlsProbeResult(cleanHost, "TLS1.3", suiteHex, If(session.Alpn, String.Empty), certOk, detailText, (Not pinOk))
                End Using
            Catch ex As Exception
                Return New TlsProbeResult(cleanHost, String.Empty, String.Empty, String.Empty, False, ex.Message)
            End Try
        End Function
    End Class

End Namespace
