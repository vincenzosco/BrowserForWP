' BrowserForWP — server certificate verification.
'
' Two independent questions, both mandatory, neither substitutable for the other:
'
'   1. IS THE CHAIN TRUSTED?  Windows.Security.Cryptography.Certificates.Certificate.
'      BuildChainAsync builds the chain and CertificateChain.Validate checks it
'      against the system trust store — the same roots and revocation state the
'      rest of the OS uses. We do not ship our own root list.
'
'   2. IS THE CERTIFICATE FOR THE HOST WE ASKED FOR?  Chain validation does NOT
'      answer this. A certificate for attacker.example that chains to a trusted
'      root validates perfectly, and accepting it is the classic TLS
'      man-in-the-middle. RFC 6125 hostname matching is therefore done here, on
'      the SAN, explicitly.
'
' Before this file, the prototype verified that a live server's CertificateVerify
' signature checks out under the leaf's public key. That check proves the peer
' holds the private key; it says nothing about whether the certificate was
' issued to them for this name. All three checks are required.

Imports System.Collections.Generic
Imports Windows.Security.Cryptography.Certificates
Imports Windows.Security.Cryptography.Core
Imports BrowserForWP.Crypto

Namespace Tls13

    ''' <summary>The result of verifying a server's certificate chain.</summary>
    Public NotInheritable Class CertificateValidationResult

        Public Sub New(isChainTrusted As Boolean, isHostnameMatch As Boolean,
                       chainStatus As String, matchedName As String)
            Me.IsChainTrusted = isChainTrusted
            Me.IsHostnameMatch = isHostnameMatch
            Me.ChainStatus = chainStatus
            Me.MatchedName = matchedName
        End Sub

        Public ReadOnly IsChainTrusted As Boolean
        Public ReadOnly IsHostnameMatch As Boolean
        Public ReadOnly ChainStatus As String
        Public ReadOnly MatchedName As String

        ''' <summary>Both checks must pass; there is no "mostly fine".</summary>
        Public ReadOnly Property IsValid As Boolean
            Get
                Return IsChainTrusted AndAlso IsHostnameMatch
            End Get
        End Property
    End Class

    Public NotInheritable Class CertificateValidator

        Private Sub New()
        End Sub

        ''' <summary>
        ''' Validates a chain against the system trust store and the requested host.
        ''' </summary>
        ''' <param name="chainDer">DER certificates, leaf first.</param>
        ''' <param name="hostName">The name we asked for, i.e. the SNI value.</param>
        Public Shared Async Function ValidateAsync(chainDer As IList(Of Byte()),
                                                   hostName As String) As Task(Of CertificateValidationResult)
            If chainDer Is Nothing OrElse chainDer.Count = 0 Then
                Throw New TlsProtocolException("no certificate to validate")
            End If

            Dim leaf = New Certificate(WinRtCrypto.ToBuffer(chainDer(0)))

            Dim intermediates As New List(Of Certificate)()
            For i As Integer = 1 To chainDer.Count - 1
                ' BuildChainAsync takes the intermediates; the leaf is the instance
                ' the chain is built FROM.
                intermediates.Add(New Certificate(WinRtCrypto.ToBuffer(chainDer(i))))
            Next

            Dim chainStatus As String
            Dim trusted As Boolean
            Try
                Dim chain = Await leaf.BuildChainAsync(intermediates)

                ' Validate() reports the *first* problem it finds.
                Dim result = chain.Validate()
                trusted = (result = ChainValidationResult.Success)
                chainStatus = result.ToString()
            Catch ex As Exception
                ' A failure to even build a chain is a failure to validate. Fail
                ' closed: never treat an exception here as "trusted".
                trusted = False
                chainStatus = "chain build failed: " & ex.Message
            End Try

            ' CertificateInfo parsing is done on the leaf's DER directly, because
            ' the WinRT Certificate type exposes no SAN accessor.
            Dim info = X509Reader.Read(chainDer(0))
            Dim match = MatchHostname(info.DnsNames, hostName)

            Return New CertificateValidationResult(trusted, match.IsMatch,
                                                   chainStatus, match.MatchedName)
        End Function

        ''' <summary>Outcome of RFC 6125 hostname matching.</summary>
        Public NotInheritable Class HostnameMatchResult

            Public Sub New(isMatch As Boolean, matchedName As String)
                Me.IsMatch = isMatch
                Me.MatchedName = matchedName
            End Sub

            Public ReadOnly IsMatch As Boolean
            Public ReadOnly MatchedName As String
        End Class

        ''' <summary>
        ''' RFC 6125 §6.4 matching against the subjectAltName dNSName list.
        '''
        ''' Two rules that are easy to get wrong and both are security-relevant:
        '''   * A wildcard matches exactly ONE label. "*.example.com" matches
        '''     "a.example.com" and must NOT match "a.b.example.com".
        '''   * A wildcard never matches the bare domain. "*.example.com" must NOT
        '''     match "example.com".
        '''
        ''' The legacy commonName fallback is deliberately NOT implemented: RFC 6125
        ''' deprecates it and browsers have removed it, because CN matching was a
        ''' repeated source of bypasses.
        ''' </summary>
        Public Shared Function MatchHostname(dnsNames As IList(Of String),
                                            hostName As String) As HostnameMatchResult
            If String.IsNullOrEmpty(hostName) Then
                Throw New ArgumentException("host required", "hostName")
            End If
            If dnsNames Is Nothing OrElse dnsNames.Count = 0 Then
                ' No SAN means nothing to match against. Hostname verification
                ' fails; it does not "pass by default".
                Return New HostnameMatchResult(False, Nothing)
            End If

            Dim host = hostName.ToLowerInvariant()

            For Each name In dnsNames
                If name Is Nothing Then Continue For
                Dim pattern = name.Trim().ToLowerInvariant()
                If pattern.Length = 0 Then Continue For

                If pattern = host Then Return New HostnameMatchResult(True, pattern)

                If pattern.StartsWith("*.") Then
                    Dim suffix = pattern.Substring(2)
                    ' One label only: the host must have something before the
                    ' suffix, and nothing extra inside the suffix.
                    Dim firstDot = host.IndexOf("."c)
                    If firstDot > 0 AndAlso host.Substring(firstDot + 1) = suffix Then
                        Return New HostnameMatchResult(True, pattern)
                    End If
                End If
            Next

            Return New HostnameMatchResult(False, Nothing)
        End Function

        ''' <summary>
        ''' Verifies CertificateVerify (RFC 8446 §4.4.3). Proves the peer holds the
        ''' certificate's private key.
        '''
        ''' The signed content is fixed by the spec and includes 64 spaces and a
        ''' trailing zero byte. Omitting either produces a signature that never
        ''' verifies, with no clue as to why:
        '''
        '''     64 * 0x20 | "TLS 1.3, server CertificateVerify" | 0x00 | transcript
        ''' </summary>
        Public Shared Function VerifyCertificateVerify(info As CertificateVerifyInfo,
                                                       certificateDer As Byte(),
                                                       transcriptHash As Byte()) As Boolean
            Dim signedContent = BuildCertificateVerifyContent(transcriptHash)
            Dim certificateInfo = X509Reader.Read(certificateDer)

            Dim algorithmName As String
            Select Case CType(info.Scheme, SignatureScheme)
                Case SignatureScheme.EcdsaSecp256r1Sha256
                    algorithmName = AsymmetricAlgorithmNames.EcdsaP256Sha256
                Case SignatureScheme.RsaPssRsaeSha256
                    algorithmName = AsymmetricAlgorithmNames.RsaSignPssSha256
                Case SignatureScheme.RsaPssRsaeSha384
                    algorithmName = AsymmetricAlgorithmNames.RsaSignPssSha384
                Case Else
                    ' Not a silent skip: an unverifiable signature must never be
                    ' treated as verified.
                    Throw New TlsProtocolException(
                        "server chose unsupported signature scheme 0x" &
                        info.Scheme.ToString("X4"))
            End Select

            Dim provider = AsymmetricKeyAlgorithmProvider.OpenAlgorithm(algorithmName)
            Dim publicKey = provider.ImportPublicKey(WinRtCrypto.ToBuffer(certificateInfo.PublicKeyBlob))

            Try
                ' VerifySignature, not Verify: the WinRT type exposes VerifySignature,
                ' VerifySignatureWithHashInput and VerifySignatureWithHashInput, and
                ' there is no method named Verify. BC30456 on a method that does not
                ' exist is worth reading literally rather than assuming a signature
                ' mismatch.
                Return CryptographicEngine.VerifySignature(
                    publicKey,
                    WinRtCrypto.ToBuffer(signedContent),
                    WinRtCrypto.ToBuffer(info.Signature))
            Catch ex As Exception
                ' A malformed signature makes Verify throw rather than return false.
                ' Both mean "not verified".
                Return False
            End Try
        End Function

        ''' <summary>The exact byte string covered by CertificateVerify.</summary>
        Public Shared Function BuildCertificateVerifyContent(transcriptHash As Byte()) As Byte()
            Dim context = System.Text.Encoding.UTF8.GetBytes(TlsLimits.CertVerifyContext)
            Dim content(TlsLimits.CertVerifySpaces + context.Length) As Byte

            For i As Integer = 0 To TlsLimits.CertVerifySpaces - 1
                content(i) = &H20
            Next
            Array.Copy(context, 0, content, TlsLimits.CertVerifySpaces, context.Length)
            content(content.Length - 1) = 0        ' the trailing zero byte

            Dim full(content.Length + transcriptHash.Length - 1) As Byte
            Array.Copy(content, 0, full, 0, content.Length)
            Array.Copy(transcriptHash, 0, full, content.Length, transcriptHash.Length)
            Return full
        End Function

        ''' <summary>SPKI pin: Base64(SHA256(leaf PublicKeyBlob)). Empty on failure.</summary>
        Public Shared Function ComputeSpkiPinBase64(certificateDer As Byte()) As String
            Try
                If certificateDer Is Nothing OrElse certificateDer.Length = 0 Then
                    Return String.Empty
                End If
                Dim parsedInfo = X509Reader.Read(certificateDer)
                If parsedInfo Is Nothing OrElse parsedInfo.PublicKeyBlob Is Nothing Then
                    Return String.Empty
                End If
                Dim digest = WinRtCrypto.Sha256(parsedInfo.PublicKeyBlob)
                Return Convert.ToBase64String(digest)
            Catch ex As Exception
                Return String.Empty
            End Try
        End Function

        ''' <summary>Pin check against a PinStore; passes when no pin is stored.</summary>
        Public Shared Function VerifyPin(certificateDer As Byte(), hostName As String, pinTable As PinStore) As Boolean
            If pinTable Is Nothing Then
                Return True
            End If
            Dim expectedPin As String = Nothing
            If Not pinTable.TryGet(hostName, expectedPin) Then
                Return True
            End If
            Dim presentedPin As String = ComputeSpkiPinBase64(certificateDer)
            If String.IsNullOrEmpty(presentedPin) Then
                Return False
            End If
            Return pinTable.Verify(hostName, presentedPin)
        End Function
    End Class

End Namespace
