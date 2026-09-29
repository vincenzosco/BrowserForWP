' BrowserForWP — minimal DER reader for X.509.
'
' WHY THIS EXISTS
' ---------------
' Windows.Security.Cryptography.Certificates.Certificate can build and validate
' a chain against the system trust store, but it exposes no accessor for the
' certificate's Subject Alternative Name list, and none for the raw public key
' bytes in a form we can import. Both are mandatory for a TLS client:
'
'   * RFC 6125 hostname verification needs the dNSName entries from the SAN
'     extension (OID 2.5.29.17).
'   * RFC 8446 §4.4.3 CertificateVerify needs the server's public key, imported
'     into the platform provider to check the signature.
'
' So a small DER parser is unavoidable. It is deliberately minimal: it reads
' only the structures above and fails loudly on anything malformed. It is not a
' general ASN.1 library and must not be used as one.

Imports System.Collections.Generic
Imports System.Text

Namespace Tls13

    ''' <summary>
    ''' A cursor over DER-encoded ASN.1. Only definite-length, primitive and
    ''' constructed forms are supported, which is all X.509 uses in practice.
    ''' </summary>
    Public NotInheritable Class DerReader

        Public Const TagInteger As Byte = &H2
        Public Const TagBitString As Byte = &H3
        Public Const TagOctetString As Byte = &H4
        Public Const TagOid As Byte = &H6
        Public Const TagSequence As Byte = &H30
        Public Const TagContextConstructed As Byte = &HA0
        Public Const TagContextPrimitive As Byte = &H80

        ''' <summary>A parsed tag/length header pointing at its value.</summary>
        Public NotInheritable Class Element

            Public Sub New(tag As Byte, offset As Integer, length As Integer, raw As Byte())
                Me.Tag = tag
                Me.Offset = offset
                Me.Length = length
                Me.Raw = raw
            End Sub

            ''' <summary>Tag byte, context-class bits intact.</summary>
            Public ReadOnly Tag As Byte

            ''' <summary>Offset of the value within the enclosing raw buffer.</summary>
            Public ReadOnly Offset As Integer

            ''' <summary>Value length in bytes.</summary>
            Public ReadOnly Length As Integer

            Private ReadOnly Raw As Byte()

            ''' <summary>The element's value bytes, without the header.</summary>
            Public Function Value() As Byte()
                ' The local is `bytes`, not `value`: a local with the same name as the
                ' containing function is BC30290 "the local variable cannot have the
                ' same name as the function that contains it". VS is case-insensitive
                ' here too, so even a differently-cased `value` is rejected.
                Dim bytes(Length - 1) As Byte
                If Length > 0 Then Array.Copy(Raw, Offset, bytes, 0, Length)
                Return bytes
            End Function

            ''' <summary>The whole element, header included.</summary>
            Public Function FullBytes(headerLength As Integer) As Byte()
                Dim full(headerLength + Length - 1) As Byte
                Array.Copy(Raw, Offset - headerLength, full, 0, headerLength + Length)
                Return full
            End Function
        End Class

        Private ReadOnly _data As Byte()
        Private _position As Integer

        Public Sub New(data As Byte())
            _data = data
        End Sub

        Public ReadOnly Property HasMore As Boolean
            Get
                Return _position < _data.Length
            End Get
        End Property

        ''' <summary>Reads the next element header and advances past its value.</summary>
        Public Function ReadElement() As Element
            If _position >= _data.Length Then Throw New TlsProtocolException("DER: unexpected end of data")

            Dim tag = _data(_position)
            _position += 1

            Dim length = ReadLength()
            If _position + length > _data.Length Then
                Throw New TlsProtocolException("DER: element length runs past the end of the buffer")
            End If

            Dim valueOffset = _position
            _position += length
            Return New Element(tag, valueOffset, length, _data)
        End Function

        ''' <summary>Reads the next element and asserts its tag, raising otherwise.</summary>
        Public Function ReadExpected(expectedTag As Byte) As Element
            Dim element = ReadElement()
            If element.Tag <> expectedTag Then
                Throw New TlsProtocolException(
                    "DER: expected tag 0x" & expectedTag.ToString("X2") &
                    ", found 0x" & element.Tag.ToString("X2"))
            End If
            Return element
        End Function

        ''' <summary>A sub-reader over a constructed element's value.</summary>
        Public Function ReadNested(expectedTag As Byte) As DerReader
            Return New DerReader(ReadExpected(expectedTag).Value())
        End Function

        Private Function ReadLength() As Integer
            If _position >= _data.Length Then Throw New TlsProtocolException("DER: truncated length")

            Dim first = _data(_position)
            _position += 1

            If (first And &H80) = 0 Then Return CInt(first)

            Dim count = CInt(first And &H7F)
            If count = 0 Then Throw New TlsProtocolException("DER: indefinite lengths are not valid DER")
            If count > 4 Then Throw New TlsProtocolException("DER: unsupported length width")

            Dim length = 0
            For i As Integer = 1 To count
                If _position >= _data.Length Then Throw New TlsProtocolException("DER: truncated length")
                length = (length << 8) Or CInt(_data(_position))
                _position += 1
            Next
            Return length
        End Function
    End Class

    ''' <summary>
    ''' The subjectAltName list, in the two forms a TLS client matches against.
    '''
    ''' WHY THE ADDRESSES ARE BYTES AND NOT TEXT. An iPAddress entry is 4 raw bytes
    ''' (IPv4) or 16 (IPv6), and the host being matched arrives as text. Parsing the
    ''' host to bytes and comparing bytes means there is exactly ONE
    ''' representation in play, so no rule about how to spell an address can be
    ''' wrong -- no leading zeros, no upper or lower case, no "::" compression to
    ''' argue about. Formatting exists only for the diagnostic message, where a
    ''' human reads it and a wrong answer costs nothing.
    '''
    ''' A SAN with neither list is not "fine by default": both are empty and every
    ''' match fails, which is what the comments below rely on.
    ''' </summary>
    Public NotInheritable Class SubjectAltNames

        Public Sub New(dnsNames As IList(Of String), ipAddresses As IList(Of Byte()))
            Me.DnsNames = dnsNames
            Me.IpAddresses = ipAddresses
        End Sub

        ''' <summary>dNSName entries (RFC 6125 §4.2.1.6).</summary>
        Public ReadOnly DnsNames As IList(Of String)

        ''' <summary>iPAddress entries, raw: 4 bytes for IPv4, 16 for IPv6.</summary>
        Public ReadOnly IpAddresses As IList(Of Byte())

        ''' <summary>True when the extension listed nothing this client can match on.</summary>
        Public ReadOnly Property IsEmpty As Boolean
            Get
                Return (DnsNames Is Nothing OrElse DnsNames.Count = 0) AndAlso
                       (IpAddresses Is Nothing OrElse IpAddresses.Count = 0)
            End Get
        End Property
    End Class

    ''' <summary>The pieces of an X.509 certificate a TLS client actually needs.</summary>
    Public NotInheritable Class CertificateInfo

        Public Sub New(publicKeyAlgorithmOid As String, publicKeyBlob As Byte(),
                       names As SubjectAltNames)
            Me.PublicKeyAlgorithmOid = publicKeyAlgorithmOid
            Me.PublicKeyBlob = publicKeyBlob
            Me.SubjectAltNames = names
        End Sub

        ''' <summary>SPKI algorithm OID, e.g. 1.2.840.10045.2.1 for EC.</summary>
        Public ReadOnly PublicKeyAlgorithmOid As String

        ''' <summary>
        ''' The blob to hand to AsymmetricKeyAlgorithmProvider.ImportPublicKey:
        ''' for EC, the raw uncompressed point X||Y; for RSA, the PKCS#1
        ''' RSAPublicKey DER taken from the subjectPublicKey BIT STRING.
        ''' </summary>
        Public ReadOnly PublicKeyBlob As Byte()

        ''' <summary>What the SAN extension holds: names and addresses.</summary>
        Public ReadOnly SubjectAltNames As SubjectAltNames
    End Class

    ''' <summary>Extracts the fields above from a DER certificate.</summary>
    Public NotInheritable Class X509Reader

        Public Const OidEcPublicKey As String = "1.2.840.10045.2.1"
        Public Const OidRsaEncryption As String = "1.2.840.113549.1.1.1"
        Public Const OidSubjectAltName As String = "2.5.29.17"

        ' GeneralName choices are context-specific tags, so the tag byte is the
        ' choice number with the primitive bit set (RFC 5280 §4.2.1.6):
        '   dNSName   [2] IA5String -> 0x82
        '   iPAddress [7] OCTET STRING -> 0x87
        Public Const TagContextDnsName As Byte = &H82
        Public Const TagContextIpAddress As Byte = &H87

        Private Sub New()
        End Sub

        Public Shared Function Read(certificateDer As Byte()) As CertificateInfo
            If certificateDer Is Nothing OrElse certificateDer.Length = 0 Then
                Throw New TlsProtocolException("empty certificate")
            End If

            Dim certificate = New DerReader(certificateDer).ReadNested(DerReader.TagSequence)
            Dim tbs = certificate.ReadNested(DerReader.TagSequence)

            ' Collect the TBSCertificate children, then index them. Field order is
            ' fixed by RFC 5280 §4.1:
            '   [0] version, serialNumber, signature, issuer, validity, subject,
            '   subjectPublicKeyInfo, [1] issuerUniqueID, [2] subjectUniqueID,
            '   [3] extensions
            ' The version tag is optional and explicit, so every later index shifts
            ' by one depending on whether it is present.
            Dim children As New List(Of DerReader.Element)()
            While tbs.HasMore
                children.Add(tbs.ReadElement())
            End While

            Dim hasExplicitVersion = children.Count > 0 AndAlso
                                     children(0).Tag = DerReader.TagContextConstructed
            Dim spkiIndex = If(hasExplicitVersion, 6, 5)
            If children.Count <= spkiIndex Then
                Throw New TlsProtocolException("certificate is missing subjectPublicKeyInfo")
            End If
            Dim spki = New DerReader(children(spkiIndex).Value())

            ' The extensions block is the trailing [3] element, when present.
            Dim sanExtension As Byte() = Nothing
            If children.Count > 0 Then
                Dim last = children(children.Count - 1)
                If last.Tag = DerReader.TagContextConstructed Then
                    sanExtension = FindSubjectAltName(New DerReader(last.Value()))
                End If
            End If

            Dim spkiBody = New DerReader(spki.ReadExpected(DerReader.TagSequence).Value())
            Dim algorithm = New DerReader(spkiBody.ReadExpected(DerReader.TagSequence).Value())
            Dim algorithmOid = ReadOid(algorithm)

            Dim bitString = spkiBody.ReadExpected(DerReader.TagBitString).Value()
            If bitString.Length = 0 Then
                Throw New TlsProtocolException("empty subjectPublicKey BIT STRING")
            End If
            ' The first byte of a BIT STRING value is the count of unused bits.
            If bitString(0) <> 0 Then
                Throw New TlsProtocolException("subjectPublicKey has unused bits set")
            End If
            Dim keyBytes(bitString.Length - 2) As Byte
            Array.Copy(bitString, 1, keyBytes, 0, keyBytes.Length)

            Dim blob As Byte()
            If algorithmOid = OidEcPublicKey Then
                ' ImportPublicKey wants the raw point. The BIT STRING holds
                ' 04 || X || Y, so drop the 0x04 uncompressed-point marker.
                If keyBytes.Length <> 65 OrElse keyBytes(0) <> &H4 Then
                    Throw New TlsProtocolException(
                        "only uncompressed P-256 EC points are supported")
                End If
                blob = New Byte(63) {}
                Array.Copy(keyBytes, 1, blob, 0, 64)
            ElseIf algorithmOid = OidRsaEncryption Then
                blob = keyBytes            ' the PKCS#1 RSAPublicKey DER itself
            Else
                Throw New TlsProtocolException("unsupported public key algorithm " & algorithmOid)
            End If

            Return New CertificateInfo(algorithmOid, blob, ReadSubjectAltNames(sanExtension))
        End Function

        Private Shared Function FindSubjectAltName(extensions As DerReader) As Byte()
            Dim list = extensions.ReadNested(DerReader.TagSequence)
            While list.HasMore
                Dim extension = New DerReader(list.ReadExpected(DerReader.TagSequence).Value())
                Dim oid = ReadOid(extension)

                ' critical BOOLEAN is optional and defaults to FALSE.
                Dim nextElement = extension.ReadElement()
                Dim value As DerReader.Element
                If nextElement.Tag = DerReader.TagOctetString Then
                    value = nextElement
                Else
                    value = extension.ReadExpected(DerReader.TagOctetString)
                End If

                If oid = OidSubjectAltName Then Return value.Value()
            End While
            Return Nothing
        End Function

        ''' <summary>
        ''' GeneralNames inside a SAN extension value, split into the two kinds this
        ''' client matches against.
        '''
        ''' The address entries were MISSING here, and their absence was not a
        ''' visible failure: a certificate for an IP address carries the address in
        ''' an iPAddress entry and, under Let's Encrypt's `shortlived` profile, no
        ''' common name at all. Name matching therefore returned "no SAN to match
        ''' against" for a certificate that is correct in every respect, and refused
        ''' a chain that validated. Measured against the deployed server on
        ''' 2026-09-29, where the client would have rejected it.
        ''' </summary>
        Private Shared Function ReadSubjectAltNames(sanExtensionValue As Byte()) As SubjectAltNames
            Dim dnsNames As New List(Of String)()
            Dim ipAddresses As New List(Of Byte())
            If sanExtensionValue Is Nothing OrElse sanExtensionValue.Length = 0 Then
                ' No SAN at all; every match must fail rather than "pass by default".
                Return New SubjectAltNames(dnsNames, ipAddresses)
            End If

            Dim generalNames = New DerReader(sanExtensionValue).ReadNested(DerReader.TagSequence)
            While generalNames.HasMore
                Dim name = generalNames.ReadElement()
                If name.Tag = TagContextDnsName Then
                    dnsNames.Add(Encoding.UTF8.GetString(name.Value(), 0, name.Length))
                ElseIf name.Tag = TagContextIpAddress Then
                    ' 4 bytes or 16, and nothing else: an iPAddress of another
                    ' length is malformed, and keeping it would only invite a
                    ' comparison against bytes that are not an address.
                    If name.Length = 4 OrElse name.Length = 16 Then
                        ipAddresses.Add(name.Value())
                    End If
                End If
            End While
            Return New SubjectAltNames(dnsNames, ipAddresses)
        End Function

        ''' <summary>Decodes a dotted-decimal OID.</summary>
        Private Shared Function ReadOid(reader As DerReader) As String
            Dim bytes = reader.ReadExpected(DerReader.TagOid).Value()
            If bytes.Length = 0 Then Throw New TlsProtocolException("empty OID")

            Dim parts As New List(Of String)()
            Dim value As ULong = 0UL
            For i As Integer = 0 To bytes.Length - 1
                value = (value << 7) Or CULng(bytes(i) And &H7F)
                If (bytes(i) And &H80) = 0 Then
                    If parts.Count = 0 Then
                        ' First byte encodes two arcs: X*40 + Y.
                        parts.Add((value \ 40UL).ToString())
                        parts.Add((value Mod 40UL).ToString())
                    Else
                        parts.Add(value.ToString())
                    End If
                    value = 0UL
                End If
            Next
            Return String.Join(".", parts.ToArray())
        End Function
    End Class

End Namespace
