' BrowserForWP — generated-vectors smoke checks (same assembly as Vectors).
'
' Vectors.generated.vb has Friend visibility, so only code in this project can
' see it. These checks prove the file is consumed rather than orphaned.

Namespace CryptoTests

    ''' <summary>Asserts the generated vectors are present and well-formed.</summary>
    Public NotInheritable Class VectorsSmokeTests

        Private Sub New()
        End Sub

        Private Shared Sub Check(ok As Boolean, checkName As String)
            If Not ok Then
                Throw New InvalidOperationException("check failed: " & checkName)
            End If
        End Sub

        Public Shared Function RunAll() As Integer
            Dim vec As New Vectors()
            Check(vec.HkdfCase1Ikm IsNot Nothing AndAlso vec.HkdfCase1Ikm.Length > 0, "hkdf ikm present")
            Check(vec.HkdfCase1Prk.Length = 32, "hkdf prk 32 bytes")
            Check(vec.HkdfCase1L = 42, "hkdf L 42")
            Check(vec.HkdfCase1Okm.Length = vec.HkdfCase1L, "hkdf okm length")
            Check(vec.X25519AlicePriv.Length = 32, "x25519 priv 32")
            Check(vec.X25519AlicePub.Length = 32, "x25519 pub 32")
            Check(vec.X25519Shared.Length = 32, "x25519 shared 32")
            Check(vec.Tls13EarlySecret.Length = 32, "tls early secret")
            Check(vec.AesGcmKey128.Length = 16, "aes128 key 16")
            Return 9
        End Function
    End Class

End Namespace
