' BrowserForWP — selector matching, for the declared subset.
'
' This is a transliteration of matchesSimple and matches in
' tools/proto/csscascade.mjs, and that prototype is its specification: change the
' prototype first, watch it pass, then port.
'
' The selector is matched RIGHT TO LEFT, from the subject outwards, which is the
' direction that lets a short-circuit on the subject reject most candidates before
' any ancestor is touched. Two things in the climbing loop are load-bearing, and
' both are asserted by the prototype:
'
'   * With `>`, the part on the left must match the immediate parent, and the
'     climb stops there. Climbing instead would make `body > p` match a
'     grandchild, which is the difference between correct styling and styling that
'     is quietly wrong.
'   * With a descendant combinator, the climb consumes the part on the left, so
'     the loop must skip it. Forgetting the skip makes `#main p` demand a second,
'     unrelated match of `#main` higher up.

Namespace Engine.Native

    ''' <summary>Decides whether a parsed selector matches a parsed element.</summary>
    Public NotInheritable Class SelectorMatcher

        ''' <summary>Does this selector match this element? The rightmost part is the subject.</summary>
        Public Shared Function Matches(selector As CssSelector, element As HtmlElement) As Boolean
            If selector Is Nothing OrElse element Is Nothing Then Return False
            If element.IsText Then Return False

            Dim parts As List(Of CssSelectorPart) = selector.Parts
            If parts Is Nothing OrElse parts.Count = 0 Then Return False

            Dim current As HtmlElement = element
            Dim depthIndex As Integer = parts.Count - 1

            While depthIndex >= 0
                If Not MatchesSimple(parts(depthIndex).Simple, current) Then Return False
                If depthIndex = 0 Then Return True

                Dim wantImmediateParent As Boolean = parts(depthIndex).Combinator = ">"
                current = current.Parent

                If wantImmediateParent Then
                    If current Is Nothing Then Return False
                Else
                    Dim climbed As HtmlElement = current
                    While climbed IsNot Nothing AndAlso Not MatchesSimple(parts(depthIndex - 1).Simple, climbed)
                        climbed = climbed.Parent
                    End While
                    If climbed Is Nothing Then Return False
                    current = climbed
                    depthIndex -= 1
                End If

                depthIndex -= 1
            End While

            Return True
        End Function

        ''' <summary>Type, class and id only, as declared in NodeTypes.</summary>
        Public Shared Function MatchesSimple(simple As CssSimpleSelector, element As HtmlElement) As Boolean
            If simple Is Nothing OrElse element Is Nothing Then Return False
            If element.IsText Then Return False

            If Not String.IsNullOrEmpty(simple.TypeName) AndAlso simple.TypeName <> "*" Then
                If element.TagName <> simple.TypeName Then Return False
            End If

            If Not String.IsNullOrEmpty(simple.IdName) Then
                If element.Attribute("id") <> simple.IdName Then Return False
            End If

            If Not String.IsNullOrEmpty(simple.ClassName) Then
                If Not HasClass(element.Attribute("class"), simple.ClassName) Then Return False
            End If

            Return True
        End Function

        ''' <summary>
        ''' Is `className` one of the whitespace-separated tokens of a class
        ''' attribute? Written without String.Split and without ControlChars: the
        ''' latter is not in this profile (BC30451), so naming tab or form feed by
        ''' constant is not available. Char.IsWhiteSpace is, and it is the separator
        ''' rule the class attribute actually has.
        ''' </summary>
        Private Shared Function HasClass(classAttribute As String, className As String) As Boolean
            If String.IsNullOrEmpty(classAttribute) Then Return False

            Dim index As Integer = 0
            While index < classAttribute.Length
                While index < classAttribute.Length AndAlso Char.IsWhiteSpace(classAttribute(index))
                    index += 1
                End While

                Dim tokenStart As Integer = index
                While index < classAttribute.Length AndAlso Not Char.IsWhiteSpace(classAttribute(index))
                    index += 1
                End While

                If index > tokenStart Then
                    If classAttribute.Substring(tokenStart, index - tokenStart) = className Then Return True
                End If
            End While

            Return False
        End Function

    End Class

End Namespace
