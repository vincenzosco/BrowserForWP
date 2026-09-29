' BrowserForWP — the CSS parser, for a declared subset of CSS.
'
' This is a transliteration of stripComments, parseSimple, parseSelector and
' parseCss in tools/proto/csscascade.mjs, and that prototype is its specification:
' change the prototype first, watch it pass, then port.
'
' What it is not: a conforming CSS parser. There is no nested at-rule handling,
' no `@media` evaluation, no shorthand expansion, no `!important`. At-rules are
' skipped whole rather than guessed at, which is the honest behaviour when the
' engine cannot honour their condition.
'
' The brace scan is deliberately flat: find `{`, find the next `}`, and treat
' everything between them as declarations. That is why `@media screen { p { ... } }`
' is skipped instead of mis-nested -- see the prototype's assertion for it.

Imports System.Collections.Generic
Imports System.Text

Namespace Engine.Native

    ''' <summary>Turns CSS source into a stylesheet. Never throws.</summary>
    Public NotInheritable Class CssParser

        ''' <summary>Parse a stylesheet. Unknown at-rules are skipped, never guessed at.</summary>
        Public Shared Function Parse(css As String) As Stylesheet
            Dim sheet As New Stylesheet()
            If css Is Nothing Then Return sheet

            Dim text As String = StripComments(css)
            Dim index As Integer = 0

            While index < text.Length
                Dim braceAt As Integer = text.IndexOf("{"c, index)
                If braceAt < 0 Then Exit While
                Dim closeAt As Integer = text.IndexOf("}"c, braceAt)
                If closeAt < 0 Then Exit While

                Dim selectorText As String = text.Substring(index, braceAt - index).Trim()
                Dim body As String = text.Substring(braceAt + 1, closeAt - (braceAt + 1))
                index = closeAt + 1

                If selectorText.Length = 0 Then Continue While
                If selectorText.StartsWith("@", StringComparison.Ordinal) Then Continue While

                Dim rule As New CssRule()
                For Each selectorPiece In selectorText.Split(","c)
                    Dim selector As CssSelector = ParseSelector(selectorPiece)
                    If selector.Parts.Count > 0 Then rule.Selectors.Add(selector)
                Next
                If rule.Selectors.Count = 0 Then Continue While

                For Each declarationPiece In body.Split(";"c)
                    Dim colonAt As Integer = declarationPiece.IndexOf(":"c)
                    ' colonAt = 0 means no property name; a declaration without a
                    ' name is not something to guess about.
                    If colonAt <= 0 Then Continue For
                    Dim declarationName As String = declarationPiece.Substring(0, colonAt).Trim().ToLowerInvariant()
                    Dim declarationValue As String = declarationPiece.Substring(colonAt + 1).Trim()
                    rule.Declarations.Add(New StyleDeclaration(declarationName, declarationValue))
                Next
                If rule.Declarations.Count = 0 Then Continue While

                sheet.Rules.Add(rule)
            End While

            Return sheet
        End Function

        ''' <summary>
        ''' Remove `/* ... */`. An unterminated comment drops the remainder of the
        ''' input rather than being reopened, because a truncated stylesheet has no
        ''' recoverable structure past that point.
        ''' </summary>
        Private Shared Function StripComments(css As String) As String
            Dim builder As New StringBuilder()
            Dim index As Integer = 0
            While index < css.Length
                If index + 1 < css.Length AndAlso css(index) = "/"c AndAlso css(index + 1) = "*"c Then
                    Dim commentEnd As Integer = css.IndexOf("*/", index + 2, StringComparison.Ordinal)
                    If commentEnd < 0 Then Exit While
                    index = commentEnd + 2
                    Continue While
                End If
                builder.Append(css(index))
                index += 1
            End While
            Return builder.ToString()
        End Function

        ''' <summary>
        ''' Split a compound selector text into type, class and id. The last of each
        ''' kind wins, matching the prototype's regex scan.
        ''' </summary>
        Private Shared Function ParseSimple(selectorText As String) As CssSimpleSelector
            Dim simple As New CssSimpleSelector()
            If selectorText Is Nothing Then Return simple

            Dim index As Integer = 0
            While index < selectorText.Length
                Dim prefix As Char = " "c
                If selectorText(index) = "."c OrElse selectorText(index) = "#"c Then
                    prefix = selectorText(index)
                    index += 1
                End If

                Dim nameStart As Integer = index
                While index < selectorText.Length AndAlso IsSelectorNameChar(selectorText(index))
                    index += 1
                End While

                If index > nameStart Then
                    Dim nameValue As String = selectorText.Substring(nameStart, index - nameStart).ToLowerInvariant()
                    If prefix = "."c Then
                        simple.ClassName = nameValue
                    ElseIf prefix = "#"c Then
                        simple.IdName = nameValue
                    Else
                        simple.TypeName = nameValue
                    End If
                ElseIf prefix = " "c Then
                    ' A character outside the grammar, `*` being the common one.
                    ' Advancing keeps the loop from stalling, and leaves the
                    ' universal selector contributing nothing to specificity.
                    index += 1
                End If
            End While

            Return simple
        End Function

        ''' <summary>
        ''' Split a selector into parts and combinators. The first part's combinator
        ''' is empty; every later part carries `>` or a single space.
        ''' </summary>
        Private Shared Function ParseSelector(selectorText As String) As CssSelector
            Dim selector As New CssSelector()
            If selectorText Is Nothing Then Return selector

            Dim text As String = selectorText.Trim()
            Dim index As Integer = 0
            Dim combinator As String = String.Empty

            While index < text.Length
                If Char.IsWhiteSpace(text(index)) Then
                    index += 1
                    Continue While
                End If
                If text(index) = ">"c Then
                    combinator = ">"
                    index += 1
                    Continue While
                End If

                Dim tokenStart As Integer = index
                While index < text.Length AndAlso Not Char.IsWhiteSpace(text(index)) AndAlso text(index) <> ">"c
                    index += 1
                End While

                Dim partItem As New CssSelectorPart()
                partItem.Combinator = combinator
                partItem.Simple = ParseSimple(text.Substring(tokenStart, index - tokenStart))
                selector.Parts.Add(partItem)

                ' Once a part has been emitted, the next one is related to it by a
                ' descendant combinator unless a `>` says otherwise -- and a `>` is
                ' consumed explicitly above, so it is set before this point.
                combinator = " "
            End While

            Return selector
        End Function

        Private Shared Function IsSelectorNameChar(ch As Char) As Boolean
            Return (ch >= "a"c AndAlso ch <= "z"c) OrElse
                   (ch >= "A"c AndAlso ch <= "Z"c) OrElse
                   (ch >= "0"c AndAlso ch <= "9"c) OrElse
                   ch = "_"c OrElse ch = "-"c
        End Function

    End Class

End Namespace
