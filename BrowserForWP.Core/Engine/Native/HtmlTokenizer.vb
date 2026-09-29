' BrowserForWP — the HTML tokenizer, for a declared subset of HTML.
'
' This is a transliteration of tokenize() and parseStartTag() in
' tools/proto/htmlparse.mjs, and that prototype is its specification: change the
' prototype first, watch it pass, then port. The prototype exists because nothing
' in this environment can execute VB.
'
' What it is not: a conforming HTML5 tokenizer. There is no character-reference
' decoding beyond what the tree builder needs, no foreign content, no adoption
' agency algorithm. It handles the subset the engine declares, and anything
' outside that subset is either skipped deliberately or treated as text.

Imports System.Collections.Generic
Imports System.Text

Namespace Engine.Native

    ''' <summary>Turns HTML source into a flat token list. Never throws.</summary>
    Public NotInheritable Class HtmlTokenizer

        ''' <summary>
        ''' Elements whose content is data rather than markup, so the tokenizer
        ''' consumes their body verbatim up to their own end tag.
        ''' </summary>
        Private Shared ReadOnly RawTextElements As String() = {"script", "style"}

        ''' <summary>
        ''' Tokenize a document. Comments and declarations are dropped; a `&lt;`
        ''' followed by anything other than a letter, `/` or `!` is literal text,
        ''' which is what keeps `a &lt; b` readable in a paragraph.
        ''' </summary>
        Public Shared Function Tokenize(html As String) As IList(Of HtmlToken)
            Dim tokens As New List(Of HtmlToken)()
            If html Is Nothing Then Return tokens

            Dim buffer As New StringBuilder()
            Dim pos As Integer = 0

            While pos < html.Length
                Dim ch As Char = html(pos)
                If ch <> "<"c Then
                    buffer.Append(ch)
                    pos += 1
                    Continue While
                End If

                ' A comment contributes nothing, not even whitespace: inserting a
                ' space where a comment was would change the text it sits between.
                If StartsWithAt(html, pos, "<!--") Then
                    Dim commentEnd As Integer = html.IndexOf("-->", pos + 4, StringComparison.Ordinal)
                    If commentEnd < 0 Then
                        pos = html.Length
                    Else
                        pos = commentEnd + 3
                    End If
                    Continue While
                End If

                ' A declaration: <!DOCTYPE html> here, and no CDATA section is
                ' honoured because no foreign content is parsed.
                If StartsWithAt(html, pos, "<!") Then
                    Dim declarationEnd As Integer = html.IndexOf(">"c, pos)
                    If declarationEnd < 0 Then Exit While
                    pos = declarationEnd + 1
                    Continue While
                End If

                If StartsWithAt(html, pos, "</") Then
                    Dim endTagEnd As Integer = html.IndexOf(">"c, pos)
                    If endTagEnd < 0 Then Exit While
                    FlushText(tokens, buffer)
                    Dim endName As String = html.Substring(pos + 2, endTagEnd - (pos + 2)).Trim().ToLowerInvariant()
                    If endName.Length > 0 Then
                        Dim endToken As New HtmlToken()
                        endToken.Kind = HtmlTokenKind.EndTag
                        endToken.Name = endName
                        tokens.Add(endToken)
                    End If
                    pos = endTagEnd + 1
                    Continue While
                End If

                If pos + 1 < html.Length AndAlso IsNameStartChar(html(pos + 1)) Then
                    FlushText(tokens, buffer)

                    Dim scan As Integer = pos + 1
                    While scan < html.Length AndAlso html(scan) <> ">"c
                        scan += 1
                    End While
                    Dim inside As String = html.Substring(pos + 1, scan - (pos + 1))
                    If scan < html.Length Then
                        pos = scan + 1
                    Else
                        ' Unterminated tag at end of input: consume it and stop.
                        pos = html.Length
                    End If

                    Dim startToken As HtmlToken = ParseStartTag(inside)
                    If startToken Is Nothing Then Continue While
                    tokens.Add(startToken)

                    If Not startToken.SelfClosing AndAlso IsRawTextElement(startToken.Name) Then
                        ' A script or style body is data. Consumed as a single
                        ' token, so an unescaped `<` inside it cannot derail the
                        ' rest of the document -- and the tree builder then drops
                        ' it, so it never reaches the box tree as text.
                        Dim closeAt As Integer = IndexOfIgnoreCase(html, "</" & startToken.Name, pos)
                        Dim rawEnd As Integer = If(closeAt < 0, html.Length, closeAt)
                        Dim raw As String = html.Substring(pos, rawEnd - pos)
                        If raw.Length > 0 Then
                            Dim rawToken As New HtmlToken()
                            rawToken.Kind = HtmlTokenKind.Text
                            rawToken.Text = raw
                            tokens.Add(rawToken)
                        End If
                        pos = rawEnd
                    End If
                    Continue While
                End If

                buffer.Append(ch)
                pos += 1
            End While

            FlushText(tokens, buffer)
            Return tokens
        End Function

        ''' <summary>
        ''' Parse the inside of a start tag: the tag name, then attributes.
        ''' Attribute names are lowercased; values are kept exactly as written.
        ''' </summary>
        Private Shared Function ParseStartTag(inside As String) As HtmlToken
            If inside Is Nothing Then Return Nothing

            Dim text As String = inside
            Dim selfClosing As Boolean = False
            If text.EndsWith("/", StringComparison.Ordinal) Then
                selfClosing = True
                text = text.Substring(0, text.Length - 1)
            End If

            Dim at As Integer = 0
            While at < text.Length AndAlso Char.IsWhiteSpace(text(at))
                at += 1
            End While
            If at >= text.Length OrElse Not IsNameStartChar(text(at)) Then Return Nothing

            Dim nameStart As Integer = at
            at += 1
            While at < text.Length AndAlso IsTagNameChar(text(at))
                at += 1
            End While

            Dim token As New HtmlToken()
            token.Kind = HtmlTokenKind.StartTag
            token.Name = text.Substring(nameStart, at - nameStart).ToLowerInvariant()
            token.SelfClosing = selfClosing

            While at < text.Length
                While at < text.Length AndAlso Char.IsWhiteSpace(text(at))
                    at += 1
                End While
                If at >= text.Length Then Exit While

                If Not IsAttributeNameStartChar(text(at)) Then
                    ' Outside the attribute grammar. Advance one character rather
                    ' than stalling: a malformed tag must not become an infinite
                    ' loop, and the tokenizer has no error to report.
                    at += 1
                    Continue While
                End If

                Dim attributeStart As Integer = at
                at += 1
                While at < text.Length AndAlso IsAttributeNameChar(text(at))
                    at += 1
                End While
                Dim attributeName As String = text.Substring(attributeStart, at - attributeStart).ToLowerInvariant()

                Dim attributeValue As String = String.Empty
                Dim afterName As Integer = at
                While at < text.Length AndAlso Char.IsWhiteSpace(text(at))
                    at += 1
                End While

                If at < text.Length AndAlso text(at) = "="c Then
                    at += 1
                    While at < text.Length AndAlso Char.IsWhiteSpace(text(at))
                        at += 1
                    End While
                    If at < text.Length AndAlso (text(at) = """"c OrElse text(at) = "'"c) Then
                        Dim quote As Char = text(at)
                        at += 1
                        Dim valueStart As Integer = at
                        While at < text.Length AndAlso text(at) <> quote
                            at += 1
                        End While
                        attributeValue = text.Substring(valueStart, at - valueStart)
                        If at < text.Length Then at += 1
                    Else
                        Dim valueStart As Integer = at
                        While at < text.Length AndAlso Not Char.IsWhiteSpace(text(at)) AndAlso
                              text(at) <> """"c AndAlso text(at) <> "'"c AndAlso text(at) <> ">"c
                            at += 1
                        End While
                        attributeValue = text.Substring(valueStart, at - valueStart)
                    End If
                Else
                    ' Valueless attribute (`<input disabled>`). The lookahead for
                    ' `=` consumed whitespace that belongs to the next attribute,
                    ' so the scan rewinds to just after the name.
                    at = afterName
                End If

                token.Attributes.Add(New HtmlAttribute(attributeName, attributeValue))
            End While

            Return token
        End Function

        Private Shared Sub FlushText(tokens As List(Of HtmlToken), buffer As StringBuilder)
            If buffer.Length = 0 Then Return
            Dim textToken As New HtmlToken()
            textToken.Kind = HtmlTokenKind.Text
            textToken.Text = buffer.ToString()
            tokens.Add(textToken)
            buffer.Length = 0
        End Sub

        Private Shared Function IsNameStartChar(ch As Char) As Boolean
            Return (ch >= "a"c AndAlso ch <= "z"c) OrElse (ch >= "A"c AndAlso ch <= "Z"c)
        End Function

        Private Shared Function IsTagNameChar(ch As Char) As Boolean
            Return IsNameStartChar(ch) OrElse (ch >= "0"c AndAlso ch <= "9"c) OrElse ch = "-"c
        End Function

        Private Shared Function IsAttributeNameStartChar(ch As Char) As Boolean
            Return IsNameStartChar(ch) OrElse ch = "_"c OrElse ch = ":"c
        End Function

        Private Shared Function IsAttributeNameChar(ch As Char) As Boolean
            Return IsAttributeNameStartChar(ch) OrElse (ch >= "0"c AndAlso ch <= "9"c) OrElse ch = "."c OrElse ch = "-"c
        End Function

        Private Shared Function IsRawTextElement(name As String) As Boolean
            For Each candidate In RawTextElements
                If candidate = name Then Return True
            Next
            Return False
        End Function

        Private Shared Function StartsWithAt(text As String, at As Integer, value As String) As Boolean
            If at < 0 OrElse at + value.Length > text.Length Then Return False
            Return String.CompareOrdinal(text, at, value, 0, value.Length) = 0
        End Function

        Private Shared Function IndexOfIgnoreCase(haystack As String, needle As String, startPos As Integer) As Integer
            If startPos >= haystack.Length Then Return -1
            Return haystack.IndexOf(needle, startPos, StringComparison.OrdinalIgnoreCase)
        End Function

    End Class

End Namespace
