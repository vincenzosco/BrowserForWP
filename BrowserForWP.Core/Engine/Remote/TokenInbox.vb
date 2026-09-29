' BrowserForWP — the rules behind the page this phone serves, so that a device
' token can be pasted from a computer instead of typed on a 2014 keyboard.
'
' WHY THE PHONE SERVES A PAGE AT ALL. A token is 43 characters of base64url, the
' server shows it once, and the place it has to reach is a text field on this
' handset. Reading it on the phone means reading it in the phone's own browser,
' and moving it from there into the app means typing it. The one device in the
' room that can paste comfortably is the computer, so the phone publishes a form
' for the minute a person is looking at it and the computer does the typing.
'
' WHAT THIS FILE IS NOT. It is a rule and not a thing: no socket, no HTML, no
' prose. It returns resource keys and the shell resolves them, and it decides --
' the decoding of a form, which of the phone's addresses to advertise, what a
' token looks like, which slot a word names, and whether a submission may be
' saved. The same split as RemoteServers, for the same reason: the parts worth
' testing are the parts that do not need a phone.
'
' AND THE TOKEN IS NEVER HANDED BACK. Masked() exists so that the reply can say
' which slot was written without putting the secret on the wire a second time:
' the page travels in the clear inside the local network, which is a thing the
' documentation says out loud rather than a thing this code pretends away.

Imports System
Imports System.Collections.Generic
Imports System.Text

Namespace Remote

    ''' <summary>
    ''' What a submitted form amounted to, once the rules have looked at it.
    '''
    ''' ReasonKey is a resource key and never a sentence: Core has no business
    ''' holding user-facing prose, and the page the reply is drawn on is one more
    ''' place that rule has to hold.
    ''' </summary>
    Public NotInheritable Class TokenVerdict

        ' Backing fields rather than auto-implemented read-only properties: those are
        ' VB 14, and this project compiles with VB 12. The guest build says so, in
        ' BC30126 followed by a BC30634 for every line after it.
        Private ReadOnly _ok As Boolean
        Private ReadOnly _reasonKey As String
        Private ReadOnly _slot As String
        Private ReadOnly _address As String
        Private ReadOnly _token As String

        Public Sub New(ok As Boolean, reasonKey As String, slot As String,
                       address As String, token As String)
            _ok = ok
            _reasonKey = If(reasonKey, String.Empty)
            _slot = If(slot, String.Empty)
            _address = If(address, String.Empty)
            _token = If(token, String.Empty)
        End Sub

        ''' <summary>True when the shell may save this.</summary>
        Public ReadOnly Property Ok As Boolean
            Get
                Return _ok
            End Get
        End Property

        ''' <summary>Which slot the token belongs in: RemoteServers.Primary or Secondary.</summary>
        Public ReadOnly Property Slot As String
            Get
                Return _slot
            End Get
        End Property

        ''' <summary>The server address that will be stored, normalized.</summary>
        Public ReadOnly Property Address As String
            Get
                Return _address
            End Get
        End Property

        ''' <summary>The token, trimmed, as it will be stored. Empty when it was refused.</summary>
        Public ReadOnly Property Token As String
            Get
                Return _token
            End Get
        End Property

        ''' <summary>The resource key saying what happened.</summary>
        Public ReadOnly Property ReasonKey As String
            Get
                Return _reasonKey
            End Get
        End Property

    End Class

    Public NotInheritable Class TokenInbox

        ''' <summary>What the form's fields are called, in one place.</summary>
        Public Const FieldToken As String = "token"
        Public Const FieldSlot As String = "slot"
        Public Const FieldAddress As String = "address"
        Public Const FieldCode As String = "code"

        ''' <summary>
        ''' The port the listener asks for first. A fixed port is a URL a person can
        ''' type without being told a number that changes every time; the port after
        ''' it is tried when this one is taken (see PortsToTry), and the screen shows
        ''' the port that actually bound.
        ''' </summary>
        Public Const DefaultPort As Integer = 8777

        ''' <summary>How many ports to try before giving up. Five is four more than needed.</summary>
        Public Const PortsToTry As Integer = 5

        ''' <summary>A form is small, and a body larger than this is not one.</summary>
        Public Const MaxBodyBytes As Integer = 8192

        ''' <summary>
        ''' The shape of a device token, and it is deliberately wider than the
        ''' server's 43 characters: this refuses what cannot be a token at all, and
        ''' leaves "is this token real" to the server, which is the only thing that
        ''' can answer it. A phone that refuses a token the server just issued would
        ''' be a phone whose rules were wrong.
        ''' </summary>
        Public Const MinTokenLength As Integer = 16
        Public Const MaxTokenLength As Integer = 200

        ''' <summary>Four digits, typed on a computer keyboard once.</summary>
        Public Const CodeLength As Integer = 4

        ''' <summary>
        ''' Wrong codes before the listener closes itself. The code is four digits, so
        ''' the count is what makes guessing it pointless rather than merely slow.
        ''' </summary>
        Public Const MaxCodeFailures As Integer = 5

        ' The reason keys, as constants: the shell resolves them, the referee reads
        ' them, and a literal typed in two places is a literal that can disagree with
        ' itself.
        Public Const ReasonCode As String = "TokenInboxReasonCode"
        Public Const ReasonToken As String = "TokenInboxReasonToken"
        Public Const ReasonSlot As String = "TokenInboxReasonSlot"
        Public Const ReasonAddress As String = "TokenInboxReasonAddress"
        Public Const ReasonOk As String = "TokenInboxReasonOk"

        ''' <summary>Private ranges first, in the order a home network usually has them.</summary>
        Private Shared ReadOnly PreferredPrefixes As String() = {"192.168.", "10.", "172."}

        ''' <summary>A rule, not a thing: there is nothing here to hold.</summary>
        Private Sub New()
        End Sub

        ''' <summary>
        ''' Decode one application/x-www-form-urlencoded body.
        '''
        ''' Written out rather than delegated, for the reason RemoteServers writes
        ''' out its host check: what a decoder does with a malformed escape differs
        ''' between implementations, and the contract here is that NOTHING throws. A
        ''' form parser that raises on a stray percent sign is a parser that can be
        ''' made to fail by anyone who can reach the listener.
        '''
        ''' A repeated field keeps the LAST value, which is what a browser would have
        ''' sent and what a hand-written body means when it sends a field twice.
        ''' </summary>
        Public Shared Function ParseForm(body As String) As Dictionary(Of String, String)
            Dim result As New Dictionary(Of String, String)()
            If body Is Nothing Then Return result

            For Each pairText As String In body.Split("&"c)
                If pairText.Length = 0 Then Continue For
                Dim equalsAt As Integer = pairText.IndexOf("="c)
                Dim rawName As String = If(equalsAt < 0, pairText, pairText.Substring(0, equalsAt))
                Dim rawValue As String = If(equalsAt < 0, String.Empty, pairText.Substring(equalsAt + 1))
                result(Decode(rawName)) = Decode(rawValue)
            Next
            Return result
        End Function

        ''' <summary>The value of a field, or the empty string. Never Nothing.</summary>
        Public Shared Function Field(fields As Dictionary(Of String, String), name As String) As String
            If fields Is Nothing OrElse name Is Nothing Then Return String.Empty
            Dim found As String = Nothing
            If fields.TryGetValue(name, found) Then Return If(found, String.Empty)
            Return String.Empty
        End Function

        ''' <summary>
        ''' Which of the phone's own addresses a computer on the same network can
        ''' reach, or the empty string when there is none.
        '''
        ''' The candidates come from the platform's list of the machine's own names,
        ''' which also hands back the loopback address and -- on a handset that has
        ''' just lost its network -- a link-local one. Neither is an address a
        ''' computer can open, and showing one is worse than showing nothing: the
        ''' token would be typed into a URL that cannot work, and nothing would say
        ''' why.
        '''
        ''' Private ranges come first, and that is not a security claim: it is what
        ''' "the same network" looks like on a home router. An address outside them is
        ''' still returned when it is the only candidate, because a network that hands
        ''' out public addresses is unusual rather than impossible.
        ''' </summary>
        Public Shared Function ChooseAddress(candidates As IEnumerable(Of String)) As String
            If candidates Is Nothing Then Return String.Empty

            Dim usable As New List(Of String)()
            For Each raw As String In candidates
                Dim text As String = If(raw, String.Empty).Trim()
                If Not IsUsableIpv4(text) Then Continue For
                usable.Add(text)
            Next

            For Each preferred As String In PreferredPrefixes
                For Each address As String In usable
                    If address.StartsWith(preferred, StringComparison.Ordinal) Then Return address
                Next
            Next
            If usable.Count > 0 Then Return usable(0)
            Return String.Empty
        End Function

        ''' <summary>
        ''' Four dot-separated octets, and not an address that means something other
        ''' than a computer: loopback, link-local, or the unspecified address. IPv6 is
        ''' refused here rather than formatted: the URL is one a person types, and a
        ''' bracketed literal is not that.
        ''' </summary>
        Private Shared Function IsUsableIpv4(text As String) As Boolean
            If text.Length = 0 Then Return False
            If text.IndexOf(":"c) >= 0 Then Return False

            Dim parts As String() = text.Split("."c)
            If parts.Length <> 4 Then Return False
            For Each part As String In parts
                If part.Length = 0 OrElse part.Length > 3 Then Return False
                For Each digit As Char In part
                    If Not Char.IsDigit(digit) Then Return False
                Next
                Dim value As Integer = 0
                If Not Integer.TryParse(part, value) Then Return False
                If value > 255 Then Return False
            Next

            If text.StartsWith("127.", StringComparison.Ordinal) Then Return False
            If text.StartsWith("169.254.", StringComparison.Ordinal) Then Return False
            If text = "0.0.0.0" Then Return False
            Return True
        End Function

        ''' <summary>
        ''' Whether this could be a device token: long enough, short enough, and made
        ''' of the characters a token is made of.
        '''
        ''' It answers "could", not "is". The server decides whether a token is real,
        ''' one round trip later, and its answer is the one that matters.
        ''' </summary>
        Public Shared Function LooksLikeAToken(token As String) As Boolean
            Dim text As String = If(token, String.Empty).Trim()
            If text.Length < MinTokenLength OrElse text.Length > MaxTokenLength Then Return False

            For Each ch As Char In text
                If Char.IsLetterOrDigit(ch) Then Continue For
                If ch = "-"c OrElse ch = "_"c Then Continue For
                Return False
            Next
            Return True
        End Function

        ''' <summary>
        ''' The token as the reply may show it: the last four characters and nothing
        ''' else. The page is served in the clear inside the network, so the secret
        ''' goes over it once, from the computer, and never comes back.
        ''' </summary>
        Public Shared Function Masked(token As String) As String
            Dim text As String = If(token, String.Empty).Trim()
            If text.Length = 0 Then Return String.Empty
            If text.Length <= 4 Then Return "****"
            Return "****" & text.Substring(text.Length - 4)
        End Function

        ''' <summary>
        ''' Which of the two servers the form meant. The form sends NUMBERS ("1", "2")
        ''' and the words are accepted for a hand-written body, which is the only
        ''' reader of a form that also accepts words.
        '''
        ''' An unrecognised value returns empty and is refused, rather than falling
        ''' back to the primary: a submission that named a slot it cannot have meant
        ''' is not a submission to guess about.
        ''' </summary>
        Public Shared Function SlotFor(text As String) As String
            Dim value As String = If(text, String.Empty).Trim().ToLowerInvariant()
            Select Case value
                Case "", "1", "primary", "server1"
                    Return RemoteServers.Primary
                Case "2", "secondary", "backup", "server2"
                    Return RemoteServers.Secondary
                Case Else
                    Return String.Empty
            End Select
        End Function

        ''' <summary>
        ''' Whether the code the computer typed is the one on the phone's screen.
        '''
        ''' The whole string is walked even after a difference is found, so the work
        ''' does not reveal WHERE the first wrong digit is. That is shape and not a
        ''' timing defence -- four digits is not a secret worth a timing argument --
        ''' and MaxCodeFailures is the defence that matters.
        ''' </summary>
        Public Shared Function CodesMatch(presented As String, expected As String) As Boolean
            Dim given As String = If(presented, String.Empty).Trim()
            Dim wanted As String = If(expected, String.Empty).Trim()

            If wanted.Length <> CodeLength Then Return False
            If given.Length <> wanted.Length Then Return False

            Dim same As Boolean = True
            For i As Integer = 0 To wanted.Length - 1
                If Char.ToUpperInvariant(given.Chars(i)) <> Char.ToUpperInvariant(wanted.Chars(i)) Then
                    same = False
                End If
            Next
            Return same
        End Function

        ''' <summary>
        ''' Whether a submitted form may be saved, and what to save.
        '''
        ''' THE ORDER OF THE REFUSALS IS PART OF THE ANSWER: the code first, because
        ''' it is the only thing standing between a stranger on the same network and
        ''' this phone's token; then the token, because a form with no token in it is
        ''' a form that was not filled in; then the slot; then the address.
        '''
        ''' The address falls back to what the settings already hold -- this slot
        ''' first, then the other one -- because a person who has this phone working
        ''' already told it where the server is, and asking again is asking for a
        ''' second chance to get it wrong. When there is no address anywhere, the
        ''' submission is refused rather than saved without one: a token with nowhere
        ''' to go is a token that fails at the next navigation, somewhere else.
        ''' </summary>
        Public Shared Function Review(fields As Dictionary(Of String, String),
                                      code As String,
                                      primaryUrl As String,
                                      secondaryUrl As String) As TokenVerdict
            If fields Is Nothing Then fields = New Dictionary(Of String, String)()

            If Not CodesMatch(Field(fields, FieldCode), code) Then
                Return New TokenVerdict(False, ReasonCode, RemoteServers.Primary, String.Empty, String.Empty)
            End If

            Dim token As String = Field(fields, FieldToken).Trim()
            If Not LooksLikeAToken(token) Then
                Return New TokenVerdict(False, ReasonToken, RemoteServers.Primary, String.Empty, String.Empty)
            End If

            Dim slot As String = SlotFor(Field(fields, FieldSlot))
            If slot.Length = 0 Then
                Return New TokenVerdict(False, ReasonSlot, RemoteServers.Primary, String.Empty, String.Empty)
            End If

            ' An typed address that Normalize refuses is refused here too: storing the
            ' raw text would leave the settings holding something the engine cannot
            ' use, which is the failure Normalize exists to prevent.
            Dim typed As String = Field(fields, FieldAddress).Trim()
            Dim address As String = RemoteServers.Normalize(typed)
            If typed.Length > 0 AndAlso address.Length = 0 Then
                Return New TokenVerdict(False, ReasonAddress, slot, String.Empty, String.Empty)
            End If

            If address.Length = 0 Then
                address = RemoteServers.Normalize(If(slot = RemoteServers.Primary, primaryUrl, secondaryUrl))
            End If
            If address.Length = 0 Then
                address = RemoteServers.Normalize(If(slot = RemoteServers.Primary, secondaryUrl, primaryUrl))
            End If
            If address.Length = 0 Then
                Return New TokenVerdict(False, ReasonAddress, slot, String.Empty, String.Empty)
            End If

            Return New TokenVerdict(True, ReasonOk, slot, address, token)
        End Function

        ''' <summary>
        ''' One component of an urlencoded body. '+' is a space, %XX is a byte, and
        ''' anything malformed stays as it was typed instead of throwing.
        ''' </summary>
        Private Shared Function Decode(text As String) As String
            If text Is Nothing Then Return String.Empty
            If text.IndexOf("%"c) < 0 AndAlso text.IndexOf("+"c) < 0 Then Return text

            Dim bytes As New List(Of Byte)()
            Dim at As Integer = 0
            While at < text.Length
                Dim ch As Char = text.Chars(at)
                If ch = "+"c Then
                    bytes.Add(CByte(32))
                    at += 1
                    Continue While
                End If

                ' Two hex digits, and there have to BE two: at + 2 must be a real index.
                If ch = "%"c AndAlso at + 2 < text.Length Then
                    Dim high As Integer = HexDigit(text.Chars(at + 1))
                    Dim low As Integer = HexDigit(text.Chars(at + 2))
                    If high >= 0 AndAlso low >= 0 Then
                        bytes.Add(CByte(high * 16 + low))
                        at += 3
                        Continue While
                    End If
                End If

                ' Everything else is a character, and it becomes its own bytes: the
                ' input is UTF-8 text, so the escape a browser sent for a letter with
                ' an accent and the letter itself have to end up as the same bytes.
                Dim asBytes As Byte() = Encoding.UTF8.GetBytes(ch.ToString())
                For Each one As Byte In asBytes
                    bytes.Add(one)
                Next
                at += 1
            End While

            Return Encoding.UTF8.GetString(bytes.ToArray(), 0, bytes.Count)
        End Function

        ''' <summary>The value of one hex digit, or -1 when it is not one.</summary>
        Private Shared Function HexDigit(ch As Char) As Integer
            If ch >= "0"c AndAlso ch <= "9"c Then Return AscW(ch) - AscW("0"c)
            If ch >= "a"c AndAlso ch <= "f"c Then Return AscW(ch) - AscW("a"c) + 10
            If ch >= "A"c AndAlso ch <= "F"c Then Return AscW(ch) - AscW("A"c) + 10
            Return -1
        End Function

    End Class

End Namespace
