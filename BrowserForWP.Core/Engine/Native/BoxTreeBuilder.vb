' BrowserForWP — the box tree, which is where HTML stops being visible.
'
' This is a transliteration of build() in tools/proto/boxtree.mjs, and that
' prototype is its specification: change the prototype first, watch it pass, then
' port.
'
' The rule this stage exists to enforce is the anonymous block. Inside a block
' container, a run of inline and text boxes is gathered into one generated block
' box, and a real block box closes that run. Phase 2 then lays out only blocks and
' inline runs, and never has to mix the two -- which is the single largest
' simplification available to a layout engine, and it is paid for here.
'
' A child that produces no box at all -- display:none, or whitespace-only text --
' must NOT break the run: 'a<SCRIPT>b' is one run of two texts, not three runs.
' That is asserted by the prototype.

Namespace Engine.Native

    ''' <summary>Turns a styled element tree into a box tree.</summary>
    Public NotInheritable Class BoxTreeBuilder

        Private Sub New()
        End Sub

        ''' <summary>
        ''' The root font size for rem. It is the initial value, and the user-agent
        ''' sheet does not override it for html, so the two agree by construction.
        ''' </summary>
        Private Const RootFontSizePx As Double = 16

        ''' <summary>Style and box an element tree that has already been parsed.</summary>
        Public Shared Function Build(root As HtmlElement, pageSheet As Stylesheet) As BoxNode
            If root Is Nothing Then Return Nothing
            Return BuildNode(root, pageSheet, Nothing)
        End Function

        ''' <summary>
        ''' The engine entry point: HTML text plus page CSS in, box tree out. Phase 2
        ''' calls this and nothing else.
        ''' </summary>
        Public Shared Function BuildPage(html As String, pageCss As String) As BoxNode
            Dim root As HtmlElement = HtmlTreeBuilder.Build(HtmlTokenizer.Tokenize(html))
            Dim sheet As Stylesheet = CssParser.Parse(pageCss)
            Return Build(root, sheet)
        End Function

        Private Shared Function BuildNode(element As HtmlElement, pageSheet As Stylesheet,
                                          parentStyle As ComputedStyle) As BoxNode
            ' Style first: a hidden element is dropped whole, so its subtree is
            ' never walked and never resolved.
            Dim computed As ComputedStyle = StyleResolver.Resolve(element, pageSheet, parentStyle, RootFontSizePx)
            If computed Is Nothing OrElse computed.IsHidden Then Return Nothing

            If element.IsText Then
                If element.Text Is Nothing OrElse element.Text.Trim().Length = 0 Then Return Nothing

                Dim textBox As New BoxNode()
                textBox.Kind = BoxKind.Text
                textBox.TagName = "#text"
                textBox.Text = element.Text
                textBox.Style = computed
                Return textBox
            End If

            Dim box As New BoxNode()
            box.Kind = If(computed.IsBlock, BoxKind.Block, BoxKind.Inline)
            box.TagName = element.TagName
            box.Style = computed

            For Each childElement In element.Children
                Dim childBox As BoxNode = BuildNode(childElement, pageSheet, computed)
                If childBox Is Nothing Then Continue For

                If Not computed.IsBlock Then
                    ' Inside an inline container nothing is generated: the inline
                    ' run is already the natural nesting there.
                    childBox.Parent = box
                    box.Children.Add(childBox)
                    Continue For
                End If

                If childBox.Kind = BoxKind.Block Then
                    childBox.Parent = box
                    box.Children.Add(childBox)
                    Continue For
                End If

                ' Inline or text inside a block: extend the run if the last sibling
                ' is a generated block, otherwise start a new one.
                Dim lastBox As BoxNode = Nothing
                If box.Children.Count > 0 Then lastBox = box.Children(box.Children.Count - 1)

                If lastBox IsNot Nothing AndAlso lastBox.Anonymous Then
                    childBox.Parent = lastBox
                    lastBox.Children.Add(childBox)
                Else
                    Dim anonymousBox As New BoxNode()
                    anonymousBox.Kind = BoxKind.Block
                    anonymousBox.TagName = "#anonymous"
                    anonymousBox.Style = AnonymousBlockStyle(computed)
                    anonymousBox.Anonymous = True
                    anonymousBox.Parent = box
                    childBox.Parent = anonymousBox
                    anonymousBox.Children.Add(childBox)
                    box.Children.Add(anonymousBox)
                End If
            Next

            Return box
        End Function

        ''' <summary>
        ''' The style of a generated block: inherited properties from the containing
        ''' block, everything else at its initial value, and display:block. That is
        ''' what an anonymous box is -- and it is also what stops the generated box
        ''' from acquiring the container's margins and padding, which would double
        ''' the spacing around every paragraph of loose text.
        ''' </summary>
        Private Shared Function AnonymousBlockStyle(container As ComputedStyle) As ComputedStyle
            Dim generated As New ComputedStyle()
            generated.Display = "block"
            If container Is Nothing Then Return generated

            generated.Color = container.Color
            generated.FontSizePx = container.FontSizePx
            generated.FontWeight = container.FontWeight
            generated.FontStyle = container.FontStyle
            generated.FontFamily = container.FontFamily
            generated.LineHeightPx = container.LineHeightPx
            generated.TextAlign = container.TextAlign
            generated.ListStyleType = container.ListStyleType
            Return generated
        End Function

        ''' <summary>
        ''' Collect the text of every inline style element, because the tree builder
        ''' drops script and style bodies. Anything more would need a real head parser.
        '''
        ''' This lived as Private Shared inside MainPage until the native engine became
        ''' a second caller of it. It is pure string work, its output is this class's
        ''' input, and the page is the wrong place for it to live; tools/proto/boxtree.mjs
        ''' transliterates it and asserts the same cases.
        ''' </summary>
        Public Shared Function PageCss(html As String) As String
            If String.IsNullOrEmpty(html) Then Return String.Empty
            Dim collected As New System.Text.StringBuilder()
            Dim lowered As String = html.ToLowerInvariant()
            Dim searchFrom As Integer = 0
            While True
                Dim openAt As Integer = lowered.IndexOf("<style", searchFrom, StringComparison.Ordinal)
                If openAt < 0 Then Exit While
                Dim bodyStart As Integer = lowered.IndexOf(">"c, openAt)
                If bodyStart < 0 Then Exit While
                Dim closeAt As Integer = lowered.IndexOf("</style", bodyStart)
                If closeAt < 0 Then Exit While
                collected.Append(html.Substring(bodyStart + 1, closeAt - bodyStart - 1))
                collected.Append(vbLf)
                searchFrom = closeAt + 1
            End While
            Return collected.ToString()
        End Function

    End Class

End Namespace
