' BrowserForWP — how the native engine gets bytes, without knowing how.
'
' Core must not reference BrowserForWP.Net (layer discipline), so the engine
' depends on this interface and the app layer implements it over the TLS 1.3
' client. That is also what makes the engine testable: a fetcher returning fixed
' bytes exercises the whole pipeline with no network.

Imports System.Threading.Tasks

Namespace Engine.Native

    ''' <summary>One fetched document, or the reason there is not one.</summary>
    Public NotInheritable Class DocumentResponse

        Public Property StatusCode As Integer
        Public Property FinalUrl As String = String.Empty

        ''' <summary>Lowercased media type with parameters stripped, e.g. "text/html".</summary>
        Public Property ContentType As String = String.Empty

        ''' <summary>Lowercased charset from Content-Type, or empty when absent.</summary>
        Public Property Charset As String = String.Empty

        ''' <summary>Decoded body. Empty when ErrorMessage is set or the type is not a document.</summary>
        Public Property Text As String = String.Empty

        ''' <summary>Empty on success. A failed fetch is data, never an exception.</summary>
        '''
        ''' Named ErrorMessage and not Error: Error is a reserved keyword in VB (the
        ''' legacy Error statement), so `Public Property Error` does not compile
        ''' (BC30183). Escaping it as [Error] would compile but would put brackets at
        ''' every call site, which no other member in this repository needs.
        Public Property ErrorMessage As String = String.Empty

        Public ReadOnly Property IsHtml As Boolean
            Get
                Return ContentType = "text/html" OrElse ContentType = "application/xhtml+xml"
            End Get
        End Property

        ''' <summary>The charset to decode with. UTF-8 unless the server says otherwise.</summary>
        Public ReadOnly Property EffectiveCharset As String
            Get
                If String.IsNullOrEmpty(Charset) Then Return "utf-8"
                Return Charset
            End Get
        End Property
    End Class

    ''' <summary>The engine's only impure dependency.</summary>
    Public Interface IDocumentFetcher

        ''' <summary>
        ''' Fetch one URL. Implementations must not throw for an expected failure --
        ''' no network, bad host, TLS failure -- but return it in Error.
        ''' </summary>
        Function FetchAsync(url As String, dohUrl As String) As Task(Of DocumentResponse)

    End Interface

End Namespace
