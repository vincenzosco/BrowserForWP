' BrowserForWP — X25519 (RFC 7748).
'
' WinRT 8.1 exposes no X25519 primitive, and group x25519 (0x001d) is mandatory
' for a TLS 1.3 key_share, so the curve is implemented here.
'
' ── How this was verified ───────────────────────────────────────────────────
' The WP8.1 SDK is Windows-only, so this file cannot be executed during
' development on macOS or Linux. Instead, tools/proto/w25519.mjs is a line-for-
' line prototype of this file that DOES run, and it reproduces:
'
'   - RFC 7748 §5.2 raw scalar multiplication (both published vectors)
'   - RFC 7748 §6.1 public keys and the shared secret, from both sides
'   - RFC 8448 §3 client/server ephemeral keys and the ECDHE shared secret
'   - 66 randomised cross-checks of add/sub/mul/sq/invert against BigInt
'   - encode/decode round-trips
'
' Run it with: node tools/proto/w25519.mjs
' If it fails, do NOT hand-edit this file to compensate — fix the prototype.
'
' ── Representation ─────────────────────────────────────────────────────────
' 16 limbs of 2^16 held in Int64. A power-of-two radix is required, not merely
' convenient: it is the only one where limb weights add exactly,
' w(i+j) = w(i) + w(j), so a product a(i)*b(j) lands in slot i+j with no
' correction factor. The radix-2^25.5 representation used elsewhere does NOT
' have that property (there w(1)+w(1) = 52 while w(2) = 51), and reconstructing
' its alternating half-limb bookkeeping from memory produced wrong-but-plausible
' output when this file was first attempted.
'
' With 2^16 limbs there are exactly two fold factors, both derivable from
' p = 2^255 - 19:
'
'   2^255 = 19 (mod p)   -> the top bit of limb 15 folds into limb 0 with 19
'   2^256 = 38 (mod p)   -> a product landing above limb 15 folds down one
'                           limb with 38
'
' ── Known limitation ───────────────────────────────────────────────────────
' The carry chain is branch-dependent and the ladder's conditional swap is a
' real branch, so this implementation is NOT constant-time. That is a
' side-channel consideration for an attacker able to measure the handset's
' timing. Documented rather than left implicit; see docs/ARCHITECTURE.md.

Namespace Crypto

    ''' <summary>X25519 Diffie-Hellman over Curve25519 (RFC 7748).</summary>
    Public NotInheritable Class X25519

        Public Const KeySize As Integer = 32

        Private Sub New()
        End Sub

        ''' <summary>
        ''' A uniformly random, clamped scalar. Clamping happens here so no caller
        ''' can forget it; ScalarMult clamps again as a second guard.
        ''' </summary>
        Public Shared Function GeneratePrivateKey() As Byte()
            Dim k = WinRtCrypto.RandomBytes(KeySize)
            Clamp(k)
            Return k
        End Function

        ''' <summary>Scalar multiplication of the base point (u = 9).</summary>
        Public Shared Function PublicFromPrivate(privateKey As Byte()) As Byte()
            If privateKey Is Nothing OrElse privateKey.Length <> KeySize Then
                Throw New ArgumentException("private key must be 32 bytes", "privateKey")
            End If
            Dim baseU(KeySize - 1) As Byte
            baseU(0) = 9
            Return ScalarMult(privateKey, baseU)
        End Function

        ''' <summary>
        ''' RFC 7748 §6.1 shared secret. The all-zero output a low-order peer public
        ''' key produces is rejected rather than passed on: contributory behaviour
        ''' failure must be loud.
        ''' </summary>
        Public Shared Function Agreement(privateKey As Byte(), peerPublic As Byte()) As Byte()
            If privateKey Is Nothing OrElse privateKey.Length <> KeySize Then
                Throw New ArgumentException("private key must be 32 bytes", "privateKey")
            End If
            If peerPublic Is Nothing OrElse peerPublic.Length <> KeySize Then
                Throw New ArgumentException("peer public key must be 32 bytes", "peerPublic")
            End If

            ' The variable must not be named `shared`: Shared is a VB keyword, and
            ' `Dim shared = ...` is a hard syntax error (BC30203 "identifier
            ' expected"), not a shadowing warning.
            Dim sharedSecret = ScalarMult(privateKey, peerPublic)
            For Each b In sharedSecret
                If b <> 0 Then Return sharedSecret
            Next
            Throw New InvalidOperationException("X25519 produced a degenerate shared secret (low-order public key)")
        End Function

        ''' <summary>RFC 7748 §5 scalar decoding/clamping.</summary>
        Private Shared Sub Clamp(k As Byte())
            k(0) = CByte(k(0) And &HF8)     ' clear the three low bits
            k(31) = CByte(k(31) And &H7F)   ' clear the top bit
            k(31) = CByte(k(31) Or &H40)    ' set the second-highest bit
        End Sub

        ''' <summary>
        ''' The Montgomery ladder (RFC 7748 §5). Bits are processed from 254 down to
        ''' 0; the conditional swap is driven by the scalar bit.
        ''' </summary>
        Public Shared Function ScalarMult(scalar As Byte(), u As Byte()) As Byte()
            If scalar Is Nothing OrElse scalar.Length <> KeySize Then
                Throw New ArgumentException("scalar must be 32 bytes", "scalar")
            End If
            If u Is Nothing OrElse u.Length <> KeySize Then
                Throw New ArgumentException("u-coordinate must be 32 bytes", "u")
            End If

            Dim k = CType(scalar.Clone(), Byte())
            Clamp(k)

            Dim x1 = Field.Decode(u)
            Dim x2 = Field.One()
            Dim z2 = Field.Zero()
            Dim x3 = CType(x1.Clone(), Long())
            Dim z3 = Field.One()
            Dim swap As Integer = 0

            For t As Integer = 254 To 0 Step -1
                Dim kt = (CInt(k(t >> 3)) >> (t And 7)) And 1
                swap = swap Xor kt
                If swap <> 0 Then
                    Dim tmp = x2 : x2 = x3 : x3 = tmp
                    tmp = z2 : z2 = z3 : z3 = tmp
                End If
                swap = kt

                ' RFC 7748 §5 ladder step. Note z2 uses (AA + a24*E), not
                ' (BB + a24*E): the latter is a one-token error that still yields
                ' plausible-looking output, and it is why the prototype asserts
                ' these vectors rather than trusting the transcription.
                Dim a = Field.Add(x2, z2)
                Dim aa = Field.Mul(a, a)
                Dim b = Field.Subtract(x2, z2)
                Dim bb = Field.Mul(b, b)
                Dim e = Field.Subtract(aa, bb)
                Dim c = Field.Add(x3, z3)
                Dim d = Field.Subtract(x3, z3)
                Dim da = Field.Mul(d, a)
                Dim cb = Field.Mul(c, b)
                Dim daPlus = Field.Add(da, cb)
                Dim daMinus = Field.Subtract(da, cb)
                x3 = Field.Mul(daPlus, daPlus)
                z3 = Field.Mul(x1, Field.Mul(daMinus, daMinus))
                x2 = Field.Mul(aa, bb)
                z2 = Field.Mul(e, Field.Add(aa, Field.MulSmall(e, 121665L)))
            Next

            If swap <> 0 Then
                Dim tmp = x2 : x2 = x3 : x3 = tmp
                tmp = z2 : z2 = z3 : z3 = tmp
            End If

            Dim notInvertible = True
            For Each limb In z2
                If limb <> 0L Then notInvertible = False
            Next
            If notInvertible Then
                Throw New InvalidOperationException("X25519 ladder produced a non-invertible coordinate")
            End If

            Return Field.Encode(Field.Mul(x2, Field.Invert(z2)))
        End Function

        ''' <summary>
        ''' Field arithmetic modulo p = 2^255 - 19 in 16 limbs of 2^16.
        ''' Friend rather than public: this is an implementation detail of ScalarMult.
        ''' </summary>
        Friend NotInheritable Class Field

            Friend Const Limbs As Integer = 16
            Friend Const Mask16 As Long = &HFFFFL

            ''' <summary>
            ''' Offset added before a subtraction so no limb can go negative. Any
            ''' multiple of p is congruent to zero, so the value is unchanged. 4 is the
            ''' smallest power of two whose limbs all exceed the largest possible
            ''' reduced limb; the prototype derives it rather than assuming it.
            ''' </summary>
            Private Const SubOffsetMultiple As Long = 4L

            Private Sub New()
            End Sub

            ''' <summary>p as 16 limbs: FFED, FFFF x14, 7FFF.</summary>
            Private Shared ReadOnly PLimbs As Long() = BuildP()
            Private Shared ReadOnly SubOffset As Long() = BuildSubOffset()

            Private Shared Function BuildP() As Long()
                Dim p(Limbs - 1) As Long
                For i As Integer = 1 To Limbs - 2
                    p(i) = Mask16
                Next
                p(0) = &HFFEDL
                p(Limbs - 1) = &H7FFFL
                Return p
            End Function

            Private Shared Function BuildSubOffset() As Long()
                Dim offset(Limbs - 1) As Long
                For i As Integer = 0 To Limbs - 1
                    offset(i) = SubOffsetMultiple * PLimbs(i)
                Next
                Return offset
            End Function

            Friend Shared Function Zero() As Long()
                Return New Long(Limbs - 1) {}
            End Function

            Friend Shared Function One() As Long()
                Dim h(Limbs - 1) As Long
                h(0) = 1L
                Return h
            End Function

            ''' <summary>RFC 7748 §5 decode: little-endian, top bit of byte 31 masked.</summary>
            Friend Shared Function Decode(bytes As Byte()) As Long()
                Dim masked(31) As Byte
                Array.Copy(bytes, masked, 32)
                masked(31) = CByte(masked(31) And &H7F)

                Dim h(Limbs - 1) As Long
                For i As Integer = 0 To Limbs - 1
                    h(i) = CLng(masked(2 * i)) Or (CLng(masked(2 * i + 1)) << 8)
                Next
                Return h
            End Function

            ''' <summary>
            ''' Normalise into range. Carries propagate up; limb 15's bit 15 has weight
            ''' 2^255, which is congruent to 19, so its overflow folds into limb 0.
            ''' </summary>
            Friend Shared Sub Carry(t As Long())
                For pass As Integer = 1 To 3
                    Dim stable = True
                    For i As Integer = 0 To Limbs - 2
                        ' Named `shifted` and not `carry`: this method IS Carry, and a local
                        ' called carry hides it -- harmless here only because nothing in
                        ' this method calls it. tools/check-vb.mjs reports the pattern.
                        Dim shifted = t(i) >> 16
                        If shifted <> 0L Then stable = False
                        t(i) = t(i) And Mask16
                        t(i + 1) += shifted
                    Next

                    Dim overflow = t(Limbs - 1) >> 15
                    If overflow <> 0L Then stable = False
                    t(Limbs - 1) = t(Limbs - 1) And &H7FFFL
                    t(0) += 19L * overflow

                    If stable Then Exit For
                Next

                ' A leftover here means a carry bug, not a valid operand.
                For i As Integer = 0 To Limbs - 1
                    If t(i) < 0L OrElse t(i) > Mask16 Then
                        Throw New InvalidOperationException("field carry left limb " & i & " out of range")
                    End If
                Next
            End Sub

            ''' <summary>
            ''' Schoolbook multiply with Int64 accumulation. Bounds, checked by the
            ''' prototype against Int64: the raw accumulator peaks near 2^36, and after
            ''' folding at 2^41, both far inside Int64's 2^63.
            ''' </summary>
            Friend Shared Function Mul(a As Long(), b As Long()) As Long()
                Dim t(2 * Limbs - 2) As Long
                For i As Integer = 0 To Limbs - 1
                    If a(i) = 0L Then Continue For
                    For j As Integer = 0 To Limbs - 1
                        t(i + j) += a(i) * b(j)
                    Next
                Next

                ' Limb k >= 16 has weight 2^(16k); folding it down one limb divides
                ' by 2^256, which is congruent to 38 (mod p).
                For k As Integer = Limbs To t.Length - 1
                    t(k - Limbs) += 38L * t(k)
                    t(k) = 0L
                Next

                Dim h(Limbs - 1) As Long
                Array.Copy(t, h, Limbs)
                Carry(h)
                Return h
            End Function

            Friend Shared Function Add(a As Long(), b As Long()) As Long()
                Dim h(Limbs - 1) As Long
                For i As Integer = 0 To Limbs - 1
                    h(i) = a(i) + b(i)
                Next
                Carry(h)
                Return h
            End Function

            ''' <summary>
            ''' Subtraction. Deliberately named Subtract and not Sub: Sub is a VB
            ''' keyword, and a method named Sub is reported as BC30183 "invalid
            ''' keyword as identifier".
            ''' </summary>
            Friend Shared Function Subtract(a As Long(), b As Long()) As Long()
                Dim h(Limbs - 1) As Long
                For i As Integer = 0 To Limbs - 1
                    h(i) = a(i) + SubOffset(i) - b(i)
                Next
                Carry(h)
                Return h
            End Function

            Friend Shared Function MulSmall(a As Long(), k As Long) As Long()
                Dim h(Limbs - 1) As Long
                For i As Integer = 0 To Limbs - 1
                    h(i) = a(i) * k
                Next
                Carry(h)
                Return h
            End Function

            ''' <summary>
            ''' Inversion by the standard addition chain for a^(p-2) = a^(2^255 - 21).
            ''' A wrong link cannot produce the RFC 7748 public keys, which is what makes
            ''' the prototype's vectors meaningful here.
            ''' </summary>
            Friend Shared Function Invert(a As Long()) As Long()
                Dim z2 = Mul(a, a)
                ' a^9 = a^8 * a. Writing this as a^2 * (a^2)^4 yields a^10 instead and
                ' silently breaks every inversion; the prototype caught exactly that.
                Dim z9 = Mul(Mul(z2, z2), a)
                Dim z11 = Mul(z9, z2)

                Dim t = Mul(z11, z11)
                t = Mul(t, z9)
                Dim z2_5_0 = t                                   ' a^(2^5 - 1)

                t = z2_5_0
                For i As Integer = 1 To 5
                    t = Mul(t, t)
                Next
                Dim z2_10_0 = Mul(t, z2_5_0)                     ' a^(2^10 - 1)

                t = z2_10_0
                For i As Integer = 1 To 10
                    t = Mul(t, t)
                Next
                Dim z2_20_0 = Mul(t, z2_10_0)                    ' a^(2^20 - 1)

                t = z2_20_0
                For i As Integer = 1 To 20
                    t = Mul(t, t)
                Next
                Dim z2_40_0 = Mul(t, z2_20_0)                    ' a^(2^40 - 1)

                t = z2_40_0
                For i As Integer = 1 To 10
                    t = Mul(t, t)
                Next
                Dim z2_50_0 = Mul(t, z2_10_0)                    ' a^(2^50 - 1)

                t = z2_50_0
                For i As Integer = 1 To 50
                    t = Mul(t, t)
                Next
                Dim z2_100_0 = Mul(t, z2_50_0)                   ' a^(2^100 - 1)

                t = z2_100_0
                For i As Integer = 1 To 100
                    t = Mul(t, t)
                Next
                Dim z2_200_0 = Mul(t, z2_100_0)                  ' a^(2^200 - 1)

                t = z2_200_0
                For i As Integer = 1 To 50
                    t = Mul(t, t)
                Next
                Dim z2_250_0 = Mul(t, z2_50_0)                   ' a^(2^250 - 1)

                t = z2_250_0
                For i As Integer = 1 To 5
                    t = Mul(t, t)
                Next
                Return Mul(t, z11)                               ' a^(2^255 - 21)
            End Function

            ''' <summary>
            ''' Canonical 32-byte little-endian encoding. Requires a full reduction
            ''' mod p, so the value is conditionally reduced until it drops below p.
            ''' </summary>
            Friend Shared Function Encode(input As Long()) As Byte()
                Dim t = CType(input.Clone(), Long())
                Carry(t)

                Dim guard As Integer = 0
                While Not LessThanP(t) AndAlso guard < 4
                    t = SubtractP(t)
                    guard += 1
                End While

                Dim out(31) As Byte
                For i As Integer = 0 To Limbs - 1
                    out(2 * i) = CByte(t(i) And &HFFL)
                    out(2 * i + 1) = CByte((t(i) >> 8) And &HFFL)
                Next
                Return out
            End Function

            Private Shared Function LessThanP(t As Long()) As Boolean
                For i As Integer = Limbs - 1 To 0 Step -1
                    If t(i) < PLimbs(i) Then Return True
                    If t(i) > PLimbs(i) Then Return False
                Next
                Return False   ' equal to p is not less than p
            End Function

            Private Shared Function SubtractP(t As Long()) As Long()
                Dim r(Limbs - 1) As Long
                Dim borrow As Long = 0L
                For i As Integer = 0 To Limbs - 1
                    Dim v = t(i) - PLimbs(i) - borrow
                    If v < 0L Then
                        r(i) = v + 65536L
                        borrow = 1L
                    Else
                        r(i) = v
                        borrow = 0L
                    End If
                Next
                Return r
            End Function
        End Class
    End Class

End Namespace
