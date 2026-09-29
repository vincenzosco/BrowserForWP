' BrowserForWP — the cascade, inheritance and length resolution.
'
' This is a transliteration of parseLength, expandShorthand and
' winningDeclaration in tools/proto/csscascade.mjs, and that prototype is its
' specification: change the prototype first, watch it pass, then port.
'
' Resolve is called once per element, top-down, with the parent's already-computed
' style. That ordering is the contract: inheritance is a parameter, never a second
' pass. It is also why em and rem can be resolved here at all -- the parent font
' size is known at this moment and at no other.
'
' Three decisions worth stating, because each one is the difference between a
' wrong page and a collapsed one:
'
'   * The user-agent sheet is applied FIRST, so a page rule of equal specificity
'     still wins through source order. No `!important` is needed to let a page
'     override a default.
'   * Shorthands are expanded before the winner is chosen, so a longhand later in
'     the same rule overrides the shorthand it was written beside.
'   * An unparsable length leaves the inherited value alone instead of becoming
'     zero. A wrong colour is a wrong page; a margin that silently becomes 0
'     collapses the layout.

Imports System.Collections.Generic
Imports System.Globalization

Namespace Engine.Native

    ''' <summary>Turns matched declarations into used pixel values.</summary>
    Public NotInheritable Class StyleResolver

        Private Sub New()
        End Sub

        ''' <summary>The "auto" sentinel for a length. A real length is never negative.</summary>
        Private Const AutoLength As Double = -1

        ''' <summary>
        ''' Parsed once and kept: parsing the user-agent sheet per element would be
        ''' thousands of redundant parses for a page.
        ''' </summary>
        Private Shared _userAgentSheet As Stylesheet

        ''' <summary>
        ''' The used style for one element. parentStyle carries inheritance;
        ''' rootFontSizePx is the base for rem. Pass the already-computed parent, or
        ''' Nothing at the root.
        ''' </summary>
        Public Shared Function Resolve(element As HtmlElement,
                                       pageSheet As Stylesheet,
                                       parentStyle As ComputedStyle,
                                       rootFontSizePx As Double) As ComputedStyle
            Dim style As New ComputedStyle()

            ' Inherited properties only. Everything else starts from the defaults,
            ' which are the body defaults.
            If parentStyle IsNot Nothing Then
                style.Color = parentStyle.Color
                style.FontSizePx = parentStyle.FontSizePx
                style.FontWeight = parentStyle.FontWeight
                style.FontStyle = parentStyle.FontStyle
                style.FontFamily = parentStyle.FontFamily
                style.LineHeightPx = parentStyle.LineHeightPx
                style.TextAlign = parentStyle.TextAlign
                style.ListStyleType = parentStyle.ListStyleType
            End If

            If element Is Nothing Then Return style

            ' em is resolved against the inherited font size, which is what the
            ' prototype asserts (parseLength('2em', 20, 16) is 40). font-size is
            ' then applied first, so a unitless line-height sees this element's own
            ' resolved font size rather than the parent's.
            Dim emBasePx As Double = style.FontSizePx

            Dim cascade As New CascadeState()
            CollectWinners(element, UserAgentSheet(), cascade)
            CollectWinners(element, pageSheet, cascade)

            If cascade.Values.ContainsKey("font-size") Then
                Dim resolvedFontSize As Double = ResolveLength(cascade.Values("font-size"), emBasePx, rootFontSizePx)
                If Not Double.IsNaN(resolvedFontSize) AndAlso resolvedFontSize > 0 Then
                    style.FontSizePx = resolvedFontSize
                End If
            End If

            For Each propertyName In cascade.Values.Keys
                If propertyName = "font-size" Then Continue For
                ApplyDeclaration(style, propertyName, cascade.Values(propertyName), emBasePx, rootFontSizePx)
            Next

            Return style
        End Function

        ''' <summary>
        ''' One element's cascade state: the winning value per property, and the
        ''' specificity and source order that won it. Deliberately per call. As
        ''' Shared fields these would leak one element's winners into the next, and
        ''' the symptom -- later elements silently inheriting an earlier element's
        ''' declarations -- would look like a matching bug, not a state bug.
        ''' </summary>
        Private NotInheritable Class CascadeState

            Public ReadOnly Values As New Dictionary(Of String, String)()
            Public ReadOnly Specificity As New Dictionary(Of String, Integer)()
            Public ReadOnly Order As New Dictionary(Of String, Integer)()
            Public Property NextOrder As Integer

        End Class

        ''' <summary>
        ''' The winning value for each property. Iteration order is fixed -- the
        ''' user-agent sheet, then the page sheet, each in source order -- so a tie
        ''' on specificity is broken by "later wins", exactly as the prototype's
        ''' source order does.
        ''' </summary>
        Private Shared Sub CollectWinners(element As HtmlElement, sheet As Stylesheet, cascade As CascadeState)
            If sheet Is Nothing Then Return

            For Each rule In sheet.Rules
                For Each selector In rule.Selectors
                    If Not SelectorMatcher.Matches(selector, element) Then Continue For

                    Dim ruleSpecificity As Integer = selector.Specificity()
                    For Each declaration In rule.Declarations
                        Dim longhands As List(Of StyleDeclaration) = ExpandShorthand(declaration.Name, declaration.Value)
                        If longhands Is Nothing Then
                            longhands = New List(Of StyleDeclaration)()
                            longhands.Add(declaration)
                        End If

                        For Each longhand In longhands
                            Dim propertyName As String = longhand.Name

                            Dim shouldReplace As Boolean = False
                            If Not cascade.Specificity.ContainsKey(propertyName) Then
                                shouldReplace = True
                            ElseIf ruleSpecificity > cascade.Specificity(propertyName) Then
                                shouldReplace = True
                            ElseIf ruleSpecificity = cascade.Specificity(propertyName) AndAlso
                                   cascade.NextOrder > cascade.Order(propertyName) Then
                                shouldReplace = True
                            End If

                            If shouldReplace Then
                                cascade.Values(propertyName) = longhand.Value
                                cascade.Specificity(propertyName) = ruleSpecificity
                                cascade.Order(propertyName) = cascade.NextOrder
                            End If
                            cascade.NextOrder += 1
                        Next
                    Next
                Next
            Next
        End Sub

        ''' <summary>
        ''' Expand `margin`/`padding` into four longhands, and `border` into width,
        ''' style and colour. Returns Nothing when the name is not a shorthand this
        ''' engine expands, so the caller knows to use the declaration as written.
        ''' </summary>
        Private Shared Function ExpandShorthand(name As String, value As String) As List(Of StyleDeclaration)
            If name = "margin" OrElse name = "padding" Then
                Dim expanded As New List(Of StyleDeclaration)()
                Dim parts As String() = SplitOnWhitespace(value)
                If parts.Length = 0 Then Return expanded

                Dim topValue As String = parts(0)
                Dim rightValue As String = If(parts.Length > 1, parts(1), topValue)
                Dim bottomValue As String = If(parts.Length > 2, parts(2), topValue)
                Dim leftValue As String = If(parts.Length > 3, parts(3), rightValue)
                expanded.Add(New StyleDeclaration(name & "-top", topValue))
                expanded.Add(New StyleDeclaration(name & "-right", rightValue))
                expanded.Add(New StyleDeclaration(name & "-bottom", bottomValue))
                expanded.Add(New StyleDeclaration(name & "-left", leftValue))
                Return expanded
            End If

            If name = "border" Then
                Dim expanded As New List(Of StyleDeclaration)()
                For Each piece In SplitOnWhitespace(value)
                    ' Each piece classified by shape: a leading digit is a width, a
                    ' known keyword is the style, and anything else is the colour.
                    ' The order the pieces are written in does not matter.
                    If piece.Length > 0 AndAlso Char.IsDigit(piece(0)) Then
                        expanded.Add(New StyleDeclaration("border-width-all", piece))
                    ElseIf IsBorderStyleKeyword(piece) Then
                        expanded.Add(New StyleDeclaration("border-style-all", piece))
                    Else
                        expanded.Add(New StyleDeclaration("border-color-all", piece))
                    End If
                Next
                Return expanded
            End If

            Return Nothing
        End Function

        Private Shared Function IsBorderStyleKeyword(piece As String) As Boolean
            Dim lowered As String = piece.ToLowerInvariant()
            Return lowered = "none" OrElse lowered = "hidden" OrElse lowered = "solid" OrElse
                   lowered = "dashed" OrElse lowered = "dotted"
        End Function

        Private Shared Function SplitOnWhitespace(value As String) As String()
            If String.IsNullOrEmpty(value) Then Return New String() {}

            Dim parts As New List(Of String)()
            Dim index As Integer = 0
            While index < value.Length
                While index < value.Length AndAlso Char.IsWhiteSpace(value(index))
                    index += 1
                End While
                Dim tokenStart As Integer = index
                While index < value.Length AndAlso Not Char.IsWhiteSpace(value(index))
                    index += 1
                End While
                If index > tokenStart Then
                    parts.Add(value.Substring(tokenStart, index - tokenStart))
                End If
            End While
            Return parts.ToArray()
        End Function

        ''' <summary>
        ''' Resolve a CSS length to pixels. Returns NaN when the value is not a
        ''' length this engine understands -- which the caller must treat as "leave
        ''' the inherited value alone", never as zero.
        ''' </summary>
        Private Shared Function ResolveLength(value As String, parentFontPx As Double, rootFontPx As Double) As Double
            If value Is Nothing Then Return Double.NaN
            Dim text As String = value.Trim().ToLowerInvariant()
            If text.Length = 0 Then Return Double.NaN

            If text = "auto" Then Return AutoLength
            If text = "0" Then Return 0

            ' `rem` is tested before `em` because "rem" ends with "em".
            Dim numberText As String
            Dim scale As Double
            If text.EndsWith("rem", StringComparison.Ordinal) Then
                numberText = text.Substring(0, text.Length - 3)
                scale = rootFontPx
            ElseIf text.EndsWith("em", StringComparison.Ordinal) Then
                numberText = text.Substring(0, text.Length - 2)
                scale = parentFontPx
            ElseIf text.EndsWith("px", StringComparison.Ordinal) Then
                numberText = text.Substring(0, text.Length - 2)
                scale = 1
            ElseIf text.EndsWith("pt", StringComparison.Ordinal) Then
                numberText = text.Substring(0, text.Length - 2)
                scale = 96.0 / 72.0
            Else
                Return Double.NaN
            End If

            Dim parsedNumber As Double = 0
            If Not Double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, parsedNumber) Then
                Return Double.NaN
            End If
            Return parsedNumber * scale
        End Function

        ''' <summary>
        ''' Apply a resolved length, or leave the value alone. Written this way
        ''' rather than as twenty if-statements so that every length property is
        ''' guarded by the same rule.
        ''' </summary>
        Private Shared Sub SetLength(value As String, parentFontPx As Double, rootFontPx As Double, assign As Action(Of Double))
            Dim resolved As Double = ResolveLength(value, parentFontPx, rootFontPx)
            If Double.IsNaN(resolved) Then Return
            assign(resolved)
        End Sub

        Private Shared Sub ApplyDeclaration(style As ComputedStyle, name As String, value As String,
                                            emBasePx As Double, rootFontSizePx As Double)
            Select Case name
                Case "display"
                    style.Display = value.Trim().ToLowerInvariant()
                Case "color"
                    style.Color = value.Trim()
                Case "background-color"
                    style.BackgroundColor = value.Trim()
                Case "font-family"
                    style.FontFamily = value.Trim()
                Case "font-style"
                    style.FontStyle = value.Trim().ToLowerInvariant()
                Case "font-weight"
                    Dim weight As Integer = ResolveFontWeight(value)
                    If weight >= 0 Then style.FontWeight = weight
                Case "line-height"
                    Dim resolvedLineHeight As Double = ResolveLineHeight(value, style.FontSizePx)
                    If Not Double.IsNaN(resolvedLineHeight) Then style.LineHeightPx = resolvedLineHeight
                Case "text-align"
                    style.TextAlign = value.Trim().ToLowerInvariant()
                Case "text-decoration"
                    style.TextDecoration = value.Trim().ToLowerInvariant()
                Case "list-style-type"
                    style.ListStyleType = value.Trim().ToLowerInvariant()

                Case "margin-top"
                    SetLength(value, emBasePx, rootFontSizePx, Sub(v As Double) style.MarginTopPx = v)
                Case "margin-right"
                    SetLength(value, emBasePx, rootFontSizePx, Sub(v As Double) style.MarginRightPx = v)
                Case "margin-bottom"
                    SetLength(value, emBasePx, rootFontSizePx, Sub(v As Double) style.MarginBottomPx = v)
                Case "margin-left"
                    SetLength(value, emBasePx, rootFontSizePx, Sub(v As Double) style.MarginLeftPx = v)

                Case "padding-top"
                    SetLength(value, emBasePx, rootFontSizePx, Sub(v As Double) style.PaddingTopPx = v)
                Case "padding-right"
                    SetLength(value, emBasePx, rootFontSizePx, Sub(v As Double) style.PaddingRightPx = v)
                Case "padding-bottom"
                    SetLength(value, emBasePx, rootFontSizePx, Sub(v As Double) style.PaddingBottomPx = v)
                Case "padding-left"
                    SetLength(value, emBasePx, rootFontSizePx, Sub(v As Double) style.PaddingLeftPx = v)

                Case "border-width-all"
                    SetLength(value, emBasePx, rootFontSizePx, Sub(v As Double) style.BorderTopWidthPx = v)
                    SetLength(value, emBasePx, rootFontSizePx, Sub(v As Double) style.BorderRightWidthPx = v)
                    SetLength(value, emBasePx, rootFontSizePx, Sub(v As Double) style.BorderBottomWidthPx = v)
                    SetLength(value, emBasePx, rootFontSizePx, Sub(v As Double) style.BorderLeftWidthPx = v)
                Case "border-style-all"
                    Dim borderStyle As String = value.Trim().ToLowerInvariant()
                    style.BorderTopStyle = borderStyle
                    style.BorderRightStyle = borderStyle
                    style.BorderBottomStyle = borderStyle
                    style.BorderLeftStyle = borderStyle
                Case "border-color-all"
                    Dim borderColor As String = value.Trim()
                    style.BorderTopColor = borderColor
                    style.BorderRightColor = borderColor
                    style.BorderBottomColor = borderColor
                    style.BorderLeftColor = borderColor

                Case "width"
                    SetLength(value, emBasePx, rootFontSizePx, Sub(v As Double) style.WidthPx = v)
                Case "height"
                    SetLength(value, emBasePx, rootFontSizePx, Sub(v As Double) style.HeightPx = v)
                Case "max-width"
                    SetLength(value, emBasePx, rootFontSizePx, Sub(v As Double) style.MaxWidthPx = v)
            End Select
        End Sub

        ''' <summary>Weight as a number, or -1 for a value that is not one.</summary>
        Private Shared Function ResolveFontWeight(value As String) As Integer
            Dim text As String = value.Trim().ToLowerInvariant()
            If text = "bold" OrElse text = "bolder" Then Return 700
            If text = "lighter" Then Return 300
            Dim parsedWeight As Integer = 0
            If Integer.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, parsedWeight) Then
                Return parsedWeight
            End If
            Return -1
        End Function

        ''' <summary>
        ''' A unitless line-height is a multiplier of this element's own font size,
        ''' which is why font-size is applied before this runs. `normal` and an
        ''' unparsable value both yield the -1 sentinel.
        ''' </summary>
        Private Shared Function ResolveLineHeight(value As String, ownFontPx As Double) As Double
            Dim text As String = value.Trim().ToLowerInvariant()
            If text = "normal" OrElse text.Length = 0 Then Return -1

            Dim multiplier As Double = 0
            If Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, multiplier) Then
                If multiplier > 0 Then Return multiplier * ownFontPx
                Return -1
            End If

            Return ResolveLength(text, ownFontPx, ownFontPx)
        End Function

        Private Shared Function UserAgentSheet() As Stylesheet
            If _userAgentSheet Is Nothing Then
                _userAgentSheet = CssParser.Parse(UserAgentStylesheet.Css)
            End If
            Return _userAgentSheet
        End Function

    End Class

End Namespace
