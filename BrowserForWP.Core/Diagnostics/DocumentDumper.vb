' BrowserForWP — renders a box tree as indented text.
'
' This is how the pipeline becomes inspectable without a layout engine: on the
' device it shows exactly what the engine understood, which is the difference
' between "the page looks wrong" and "the engine dropped the article tag".

Imports System.Text
Imports BrowserForWP.Core.Engine.Native

Namespace Diagnostics

    ''' <summary>Indented, human-readable dump of a box tree.</summary>
    Public NotInheritable Class DocumentDumper

        Private Sub New()
        End Sub

        Public Shared Function Dump(root As BoxNode) As String
            Dim builder As New StringBuilder()
            Append(builder, root, 0)
            Return builder.ToString().TrimEnd()
        End Function

        Private Shared Sub Append(builder As StringBuilder, node As BoxNode, depth As Integer)
            If node Is Nothing OrElse builder Is Nothing Then Return
            ' Indentation is capped so a deeply nested page cannot produce a
            ' kilometre-wide line on a 480px screen.
            Dim cappedDepth As Integer = Math.Min(depth, 12)
            builder.Append(New String(" "c, cappedDepth * 2))

            builder.Append(BoxKindLetter(node.Kind))
            builder.Append(" "c)
            If node.Kind = BoxKind.Text Then
                builder.Append("#text """)
                builder.Append(Truncate(node.Text, 48))
                builder.Append(""""c)
            Else
                builder.Append(node.TagName)
                If node.Anonymous Then builder.Append(" (anonymous)")
            End If
            builder.Append(vbLf)

            For Each childItem In node.Children
                Append(builder, childItem, depth + 1)
            Next
        End Sub

        Private Shared Function BoxKindLetter(kind As BoxKind) As String
            Select Case kind
                Case BoxKind.Block
                    Return "b"
                Case BoxKind.Inline
                    Return "i"
                Case Else
                    Return "t"
            End Select
        End Function

        Private Shared Function Truncate(value As String, maxLength As Integer) As String
            Dim safeValue As String = If(value, String.Empty)
            If safeValue.Length <= maxLength Then Return safeValue
            Return safeValue.Substring(0, maxLength) & "..."
        End Function
    End Class

End Namespace
