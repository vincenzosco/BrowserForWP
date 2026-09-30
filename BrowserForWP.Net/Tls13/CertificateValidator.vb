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
'      The host may be a name or a literal ADDRESS, and those live in different
'      SAN entries: dNSName and iPAddress. Matching only the first means a
'      certificate issued for an address is refused while its chain validates,
'      which is what happened here until the deployed server was pointed at. See
'      MatchSubjectAltName.
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
            Dim match = MatchSubjectAltName(info, hostName)

            Return New CertificateValidationResult(trusted, match.IsMatch,
                                                   chainStatus, match.MatchedName)
        End Function

        ''' <summary>
        ''' Matches what we asked for against what the certificate is for, taking
        ''' the host as it is: a literal address is matched against the SAN's
        ''' iPAddress entries, anything else against its dNSName entries.
        '''
        ''' THESE ARE TWO CHECKS, NOT ONE, and conflating them is the defect this
        ''' function exists for. RFC 6125 covers names; an address in a certificate
        ''' is an iPAddress GeneralName (RFC 5280 §4.2.1.6), which no dNSName rule
        ''' can ever match. A certificate for an address therefore failed here with
        ''' "nothing to match against" while its chain validated perfectly, and it is
        ''' not an exotic mistake: OpenSSL's own hostname check has it, measured on
        ''' 2026-09-29 against the deployed server -- `-verify_hostname 1.2.3.4` refuses
        ''' a certificate whose SAN holds that address while `x509 -checkip` accepts
        ''' it.
        ''' </summary>
        Public Shared Function MatchSubjectAltName(info As CertificateInfo,
                                                  hostName As String) As HostnameMatchResult
            If String.IsNullOrEmpty(hostName) Then
                Throw New ArgumentException("host required", "hostName")
            End If
            If info Is Nothing OrElse info.SubjectAltNames Is Nothing Then
                ' No parsed SAN is not a pass. It is a failure to check, and a
                ' failure to check must never read as "fine".
                Return New HostnameMatchResult(False, Nothing)
            End If

            Dim addressBytes() As Byte = Nothing
            If Not TryParseIpLiteral(hostName, addressBytes) Then
                ' A name. The rules are unchanged, including that an empty SAN list
                ' fails rather than matching nothing.
                Return MatchHostname(info.SubjectAltNames.DnsNames, hostName)
            End If

            Dim addresses = info.SubjectAltNames.IpAddresses
            If addresses IsNot Nothing Then
                For Each entry In addresses
                    If entry IsNot Nothing AndAlso
                       entry.Length = addressBytes.Length AndAlso
                       SameBytes(entry, addressBytes) Then
                        Return New HostnameMatchResult(True, FormatIpLiteral(entry))
                    End If
                Next
            End If

            ' An address that is not in the SAN is a mismatch, and it must NOT fall
            ' back to the name rules: a dNSName is never an address, and a fallback
            ' would be a second chance for a certificate that did not earn the
            ' first one.
            Return New HostnameMatchResult(False, Nothing)
        End Function

        ''' <summary>
        ''' Parses a host that IS an address into its bytes. False for every host
        ''' that is a name -- which is not a failure but the branch the caller takes
        ''' to the name rules.
        ''' </summary>
        Public Shared Function TryParseIpLiteral(hostName As String, ByRef addressBytes As Byte()) As Boolean
            addressBytes = Nothing
            If String.IsNullOrEmpty(hostName) Then Return False

            Dim text As String = hostName.Trim()

            ' A url spells an IPv6 host inside brackets, and the brackets belong to
            ' the url rather than to the address.
            If text.Length >= 2 AndAlso text.Chars(0) = "["c AndAlso
               text.Chars(text.Length - 1) = "]"c Then
                text = text.Substring(1, text.Length - 2)
            End If

            If text.Length = 0 Then Return False

            ' A zone id is a local scope ("fe80::1%eth0") and a suffix is a network,
            ' not a host: an iPAddress entry has nowhere to put either, so neither
            ' can ever be confirmed by this check. Refused rather than half-matched.
            If text.IndexOf("%"c) >= 0 OrElse text.IndexOf("/"c) >= 0 Then Return False

            If text.IndexOf(":"c) >= 0 Then Return TryParseIpv6Literal(text, addressBytes)
            Return TryParseIpv4Literal(text, addressBytes)
        End Function

        ''' <summary>Dotted quad, and nothing a second parser could read differently.</summary>
        Private Shared Function TryParseIpv4Literal(text As String, ByRef addressBytes As Byte()) As Boolean
            Dim parts As String() = text.Split("."c)
            If parts.Length <> 4 Then Return False

            Dim bytes(3) As Byte
            For i As Integer = 0 To 3
                Dim part As String = parts(i)
                If part.Length = 0 OrElse part.Length > 3 Then Return False
                ' "010" is octal to some parsers and decimal to others. A spelling
                ' that means two things is refused rather than guessed.
                If part.Length > 1 AndAlso part.Chars(0) = "0"c Then Return False

                Dim value As Integer = 0
                For Each digit As Char In part
                    If digit < "0"c OrElse digit > "9"c Then Return False
                    value = value * 10 + (AscW(digit) - AscW("0"c))
                Next
                If value > 255 Then Return False
                bytes(i) = CByte(value)
            Next
            addressBytes = bytes
            Return True
        End Function

        ''' <summary>
        ''' IPv6 text to 16 bytes, including a "::" run and a trailing dotted quad.
        '''
        ''' Written out rather than delegated: there is no IPv6 parser here to
        ''' borrow from, and the "::" rule (RFC 4291 §2.2) is the part worth having
        ''' in exactly one place.
        ''' </summary>
        Private Shared Function TryParseIpv6Literal(text As String, ByRef addressBytes As Byte()) As Boolean
            Dim gapAt As Integer = text.IndexOf("::", StringComparison.Ordinal)
            If gapAt >= 0 AndAlso text.IndexOf("::", gapAt + 1, StringComparison.Ordinal) >= 0 Then
                ' Two runs of zeros would make it ambiguous which one the "::" is.
                Return False
            End If

            Dim headText As String = If(gapAt >= 0, text.Substring(0, gapAt), text)
            Dim tailText As String = If(gapAt >= 0, text.Substring(gapAt + 2), String.Empty)

            Dim headBytes() As Byte = Nothing
            Dim tailBytes() As Byte = Nothing
            If Not TryParseIpv6Groups(headText, headBytes) Then Return False
            If Not TryParseIpv6Groups(tailText, tailBytes) Then Return False

            Dim gapBytes As Integer = 16 - headBytes.Length - tailBytes.Length
            If gapAt >= 0 Then
                ' "::" stands for AT LEAST one group of zeros, so the two sides
                ' cannot already fill the address when the gap is written down.
                If gapBytes <= 0 Then Return False
            ElseIf gapBytes <> 0 Then
                Return False
            End If

            Dim bytes(15) As Byte
            Array.Copy(headBytes, 0, bytes, 0, headBytes.Length)
            Array.Copy(tailBytes, 0, bytes, 16 - tailBytes.Length, tailBytes.Length)
            addressBytes = bytes
            Return True
        End Function

        ''' <summary>
        ''' One side of an IPv6 address, as bytes. Empty text is the empty side of a
        ''' "::" and is valid; a malformed side returns False.
        ''' </summary>
        Private Shared Function TryParseIpv6Groups(text As String, ByRef groupBytes As Byte()) As Boolean
            Dim bytes As New List(Of Byte)()
            If text.Length > 0 Then
                Dim parts As String() = text.Split(":"c)
                For i As Integer = 0 To parts.Length - 1
                    Dim part As String = parts(i)
                    ' An empty part means "::" inside one side (RFC 4291 §2.2 writes
                    ' the gap between the sides, never inside one).
                    If part.Length = 0 Then Return False

                    If part.IndexOf("."c) >= 0 Then
                        ' A dotted quad is allowed only as the LAST part, where it
                        ' stands for two groups of bytes: "::ffff:192.0.2.1".
                        If i <> parts.Length - 1 Then Return False
                        Dim quad() As Byte = Nothing
                        If Not TryParseIpv4Literal(part, quad) Then Return False
                        For Each octet As Byte In quad
                            bytes.Add(octet)
                        Next
                    Else
                        If part.Length > 4 Then Return False
                        Dim value As Integer = 0
                        For Each hexChar As Char In part
                            Dim nibble As Integer = 0
                            If Not TryHexNibble(hexChar, nibble) Then Return False
                            value = (value << 4) Or nibble
                        Next
                        bytes.Add(CByte((value >> 8) And &HFF))
                        bytes.Add(CByte(value And &HFF))
                    End If
                Next
            End If
            groupBytes = bytes.ToArray()
            Return True
        End Function

        Private Shared Function TryHexNibble(hexChar As Char, ByRef value As Integer) As Boolean
            Select Case hexChar
                Case "0"c To "9"c
                    value = AscW(hexChar) - AscW("0"c)
                    Return True
                Case "a"c To "f"c
                    value = AscW(hexChar) - AscW("a"c) + 10
                    Return True
                Case "A"c To "F"c
                    value = AscW(hexChar) - AscW("A"c) + 10
                    Return True
                Case Else
                    value = 0
                    Return False
            End Select
        End Function

        Private Shared Function SameBytes(left As Byte(), right As Byte()) As Boolean
            If left.Length <> right.Length Then Return False
            For i As Integer = 0 To left.Length - 1
                If left(i) <> right(i) Then Return False
            Next
            Return True
        End Function

        ''' <summary>
        ''' An address for a diagnostic message. IPv4 as a dotted quad; IPv6 as
        ''' eight hex groups with no "::" compression -- longer to read, and never
        ''' silently something other than the bytes it came from. Formatting exists
        ''' only for this: matching compares bytes.
        ''' </summary>
        Public Shared Function FormatIpLiteral(address As Byte()) As String
            If address Is Nothing Then Return String.Empty

            If address.Length = 4 Then
                Return address(0).ToString() & "." & address(1).ToString() & "." &
                       address(2).ToString() & "." & address(3).ToString()
            End If

            If address.Length = 16 Then
                Dim groups As New List(Of String)()
                For i As Integer = 0 To 7
                    Dim group As Integer = (CInt(address(i * 2)) << 8) Or CInt(address(i * 2 + 1))
                    groups.Add(group.ToString("x4"))
                Next
                Return String.Join(":", groups.ToArray())
            End If

            Return String.Empty
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
            Catch
                ' A malformed signature makes Verify throw rather than return false.
                ' Both mean "not verified", and the caller treats False as a failed
                ' handshake rather than as a reason to show the platform's message.
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
            Catch
                ' An empty pin is the documented failure answer and it is FAIL-CLOSED:
                ' VerifyPin turns an empty presented pin into False, so a certificate
                ' that will not parse fails a pinned host rather than passing it. The
                ' reason is not carried because there is nothing safe to do with it.
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
