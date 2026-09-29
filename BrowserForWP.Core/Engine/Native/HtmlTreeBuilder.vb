' BrowserForWP — the HTML tree builder, for a declared subset of HTML.
'
' This is a transliteration of tree() in tools/proto/htmlparse.mjs, and that
' prototype is its specification: change the prototype first, watch it pass, then
' port. It builds a DOM-shaped tree good enough to style and lay out.
'
' Two behaviours are not optional, because they were both wrong in the first
' version of the prototype and both were caught by running it rather than by
' reading it:
'
'   * An implicit <head> is created when head content appears before any <body>.
'     Without it, <title> landed in the body and the caller could not find a
'     head at all.
'   * Script and style bodies are dropped, never treated as document text. A
'     stray "<div>" inside a JavaScript string would otherwise be laid out as a
'     paragraph.

Imports System.Collections.Generic
Imports System.Text

Namespace Engine.Native

    ''' <summary>Builds a document tree from a token list. Never throws.</summary>
    Public NotInheritable Class HtmlTreeBuilder

        ''' <summary>Elements that never contain anything, so never go on the stack.</summary>
        Private Shared ReadOnly VoidElements As String() = {
            "area", "base", "br", "col", "embed", "hr", "img", "input",
            "link", "meta", "param", "source", "track", "wbr"}

        ''' <summary>Elements whose text content is not document text.</summary>
        Private Shared ReadOnly RawTextElements As String() = {"script", "style"}

        ''' <summary>
        ''' Elements that belong to the head when they appear before any body.
        ''' Order matters for nothing here, but membership does.
        ''' </summary>
        Private Shared ReadOnly HeadContentElements As String() = {
            "title", "meta", "link", "base", "style", "script", "noscript"}

        ''' <summary>
        ''' Build the tree and return its &lt;html&gt; root. The root always exists,
        ''' even for empty input, so every caller has something to walk.
        ''' </summary>
        Public Shared Function Build(tokens As IList(Of HtmlToken)) As HtmlElement
            Dim root As HtmlElement = MakeNode("html")
            If tokens Is Nothing Then Return root

            Dim head As HtmlElement = Nothing
            Dim body As HtmlElement = Nothing
            Dim inHead As Boolean = False

            Dim stack As New List(Of HtmlElement)()
            stack.Add(root)

            For Each tok In tokens
                Dim top As HtmlElement = stack(stack.Count - 1)

                If tok.Kind = HtmlTokenKind.Text Then
                    ' Text directly under <html> belongs to neither section and is
                    ' dropped, which is what the specification says to do with
                    ' inter-element whitespace there.
                    If top Is root Then Continue For
                    If Contains(RawTextElements, top.TagName) Then Continue For
                    If tok.Text.Trim().Length = 0 Then Continue For

                    Dim textNode As HtmlElement = MakeNode("#text")
                    textNode.Text = CollapseWhitespace(tok.Text)
                    textNode.Parent = top
                    top.Children.Add(textNode)
                    Continue For
                End If

                If tok.Kind = HtmlTokenKind.EndTag Then
                    ' Pop to the matching open element and discard everything
                    ' inside it. An end tag with no match is ignored: the subset
                    ' has no error recovery beyond this.
                    Dim depthIndex As Integer = stack.Count - 1
                    While depthIndex >= 1
                        If stack(depthIndex).TagName = tok.Name Then
                            stack.RemoveRange(depthIndex, stack.Count - depthIndex)
                            Exit While
                        End If
                        depthIndex -= 1
                    End While
                    Continue For
                End If

                ' A literal <html> adds nothing: the root already is one.
                If tok.Name = "html" Then Continue For

                If tok.Name = "head" Then
                    head = EnsureHead(root, head)
                    inHead = True
                    ResetStack(stack, root, head)
                    Continue For
                End If

                If tok.Name = "body" Then
                    body = EnsureBody(root, body)
                    inHead = False
                    ResetStack(stack, root, body)
                    Continue For
                End If

                If body Is Nothing AndAlso Contains(HeadContentElements, tok.Name) Then
                    head = EnsureHead(root, head)
                    Dim headChild As HtmlElement = MakeNode(tok.Name)
                    headChild.Parent = head
                    ApplyAttributes(headChild, tok)
                    head.Children.Add(headChild)
                    If Not IsVoidElement(tok.Name) AndAlso Not tok.SelfClosing Then
                        stack.Add(headChild)
                    End If
                    Continue For
                End If

                body = EnsureBody(root, body)

                ' Leaving the head section moved the insertion point: because the
                ' declared subset has no <html> children other than head and body,
                ' the only position that can hold real content is the body.
                If inHead OrElse top Is root Then
                    inHead = False
                    ResetStack(stack, root, body)
                End If

                Dim parent As HtmlElement = stack(stack.Count - 1)
                Dim element As HtmlElement = MakeNode(tok.Name)
                element.Parent = parent
                ApplyAttributes(element, tok)
                parent.Children.Add(element)
                If Not IsVoidElement(tok.Name) AndAlso Not tok.SelfClosing Then
                    stack.Add(element)
                End If
            Next

            Return root
        End Function

        Private Shared Function MakeNode(tagName As String) As HtmlElement
            Dim node As New HtmlElement()
            node.TagName = tagName
            Return node
        End Function

        ''' <summary>Insert the head at position 0, so it precedes the body.</summary>
        Private Shared Function EnsureHead(root As HtmlElement, head As HtmlElement) As HtmlElement
            If head IsNot Nothing Then Return head
            Dim created As HtmlElement = MakeNode("head")
            created.Parent = root
            root.Children.Insert(0, created)
            Return created
        End Function

        Private Shared Function EnsureBody(root As HtmlElement, body As HtmlElement) As HtmlElement
            If body IsNot Nothing Then Return body
            Dim created As HtmlElement = MakeNode("body")
            created.Parent = root
            root.Children.Add(created)
            Return created
        End Function

        Private Shared Sub ResetStack(stack As List(Of HtmlElement), root As HtmlElement, section As HtmlElement)
            stack.Clear()
            stack.Add(root)
            stack.Add(section)
        End Sub

        Private Shared Sub ApplyAttributes(element As HtmlElement, tok As HtmlToken)
            For Each attributeEntry In tok.Attributes
                element.Attributes(attributeEntry.Name) = attributeEntry.Value
            Next
        End Sub

        ''' <summary>Collapse every whitespace run to one space. Does not trim.</summary>
        Private Shared Function CollapseWhitespace(text As String) As String
            Dim builder As New StringBuilder()
            Dim inRun As Boolean = False
            For index As Integer = 0 To text.Length - 1
                If Char.IsWhiteSpace(text(index)) Then
                    If Not inRun Then
                        builder.Append(" "c)
                        inRun = True
                    End If
                Else
                    builder.Append(text(index))
                    inRun = False
                End If
            Next
            Return builder.ToString()
        End Function

        Private Shared Function IsVoidElement(name As String) As Boolean
            Return Contains(VoidElements, name)
        End Function

        Private Shared Function Contains(candidates As String(), value As String) As Boolean
            For Each candidate In candidates
                If candidate = value Then Return True
            Next
            Return False
        End Function

    End Class

End Namespace
