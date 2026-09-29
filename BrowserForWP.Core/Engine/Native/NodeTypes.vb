' BrowserForWP — the value types the native pipeline stages agree on.
'
' Types only, no logic: every stage converts one of these into the next, so this
' file is the interface contract between the prototype and the VB. The names here
' are matched by tools/proto/htmlparse.mjs, csscascade.mjs and boxtree.mjs --
' change one, change both.

Imports System.Collections.Generic

Namespace Engine.Native

    Public Enum HtmlTokenKind
        Text
        StartTag
        EndTag
    End Enum

    ''' <summary>One attribute, name lowercased.</summary>
    Public NotInheritable Class HtmlAttribute

        Public Sub New(name As String, value As String)
            Me.Name = name
            Me.Value = value
        End Sub

        Public ReadOnly Name As String
        Public ReadOnly Value As String
    End Class

    Public NotInheritable Class HtmlToken

        Public Property Kind As HtmlTokenKind
        Public Property Name As String = String.Empty
        Public Property Text As String = String.Empty
        Public Property Attributes As New List(Of HtmlAttribute)()
        Public Property SelfClosing As Boolean

    End Class

    ''' <summary>
    ''' One node of the document tree. A text node has TagName "#text" and its
    ''' content in Text; an element has attributes and children.
    ''' </summary>
    Public NotInheritable Class HtmlElement

        Public Property TagName As String = String.Empty
        Public Property Attributes As New Dictionary(Of String, String)()
        Public Property Children As New List(Of HtmlElement)()
        Public Property Text As String = String.Empty
        Public Property Parent As HtmlElement

        Public ReadOnly Property IsText As Boolean
            Get
                Return TagName = "#text"
            End Get
        End Property

        ''' <summary>Attribute value, or an empty string. Never Nothing.</summary>
        Public Function Attribute(name As String) As String
            If String.IsNullOrEmpty(name) Then Return String.Empty
            Dim foundValue As String = Nothing
            If Attributes.TryGetValue(name.ToLowerInvariant(), foundValue) Then
                Return If(foundValue, String.Empty)
            End If
            Return String.Empty
        End Function

        Public Overrides Function ToString() As String
            If IsText Then Return "#text"
            Return TagName
        End Function
    End Class

    ''' <summary>
    ''' One compound selector: at most one type, one class and one id. Attribute
    ''' selectors and pseudo-classes are deliberately absent -- the engine reports
    ''' them as unsupported instead of matching them wrongly.
    ''' </summary>
    Public NotInheritable Class CssSimpleSelector

        Public Property TypeName As String = String.Empty
        Public Property ClassName As String = String.Empty
        Public Property IdName As String = String.Empty

        ''' <summary>id counts 100, class 10, type 1. The universal selector adds nothing.</summary>
        Public Function Specificity() As Integer
            Dim total As Integer = 0
            If Not String.IsNullOrEmpty(IdName) Then total += 100
            If Not String.IsNullOrEmpty(ClassName) Then total += 10
            If Not String.IsNullOrEmpty(TypeName) AndAlso TypeName <> "*" Then total += 1
            Return total
        End Function

        Public ReadOnly Property IsEmpty As Boolean
            Get
                Return String.IsNullOrEmpty(TypeName) AndAlso String.IsNullOrEmpty(ClassName) AndAlso String.IsNullOrEmpty(IdName)
            End Get
        End Property
    End Class

    ''' <summary>A simple selector plus how it relates to the part on its right.</summary>
    Public NotInheritable Class CssSelectorPart

        Public Property Combinator As String = String.Empty
        Public Property Simple As CssSimpleSelector

    End Class

    Public NotInheritable Class CssSelector

        Public Property Parts As New List(Of CssSelectorPart)()

        Public Function Specificity() As Integer
            Dim total As Integer = 0
            For Each partItem In Parts
                If partItem.Simple IsNot Nothing Then total += partItem.Simple.Specificity()
            Next
            Return total
        End Function

    End Class

    Public NotInheritable Class StyleDeclaration

        Public Sub New(name As String, value As String)
            Me.Name = name
            Me.Value = value
        End Sub

        Public ReadOnly Name As String
        Public ReadOnly Value As String

    End Class

    Public NotInheritable Class CssRule

        Public Property Selectors As New List(Of CssSelector)()
        Public Property Declarations As New List(Of StyleDeclaration)()

    End Class

    Public NotInheritable Class Stylesheet

        Public Property Rules As New List(Of CssRule)()

    End Class

    ''' <summary>
    ''' Used values for one box. Lengths are already resolved to pixels -- em/rem
    ''' need the parent font size, which is only available while walking the tree,
    ''' so resolution happens in the resolver and never in the parser.
    ''' </summary>
    Public NotInheritable Class ComputedStyle

        Public Property Display As String = "inline"
        Public Property Color As String = "#000000"
        Public Property BackgroundColor As String = "transparent"
        Public Property FontSizePx As Double = 16
        Public Property FontFamily As String = "'Segoe UI'"
        Public Property FontWeight As Integer = 400
        Public Property FontStyle As String = "normal"
        Public Property LineHeightPx As Double = -1
        Public Property TextAlign As String = "left"
        Public Property TextDecoration As String = "none"
        Public Property ListStyleType As String = "disc"
        Public Property MarginTopPx As Double
        Public Property MarginRightPx As Double
        Public Property MarginBottomPx As Double
        Public Property MarginLeftPx As Double
        Public Property PaddingTopPx As Double
        Public Property PaddingRightPx As Double
        Public Property PaddingBottomPx As Double
        Public Property PaddingLeftPx As Double
        Public Property BorderTopWidthPx As Double
        Public Property BorderRightWidthPx As Double
        Public Property BorderBottomWidthPx As Double
        Public Property BorderLeftWidthPx As Double
        Public Property BorderTopStyle As String = "none"
        Public Property BorderRightStyle As String = "none"
        Public Property BorderBottomStyle As String = "none"
        Public Property BorderLeftStyle As String = "none"
        Public Property BorderTopColor As String = "currentcolor"
        Public Property BorderRightColor As String = "currentcolor"
        Public Property BorderBottomColor As String = "currentcolor"
        Public Property BorderLeftColor As String = "currentcolor"
        Public Property WidthPx As Double = -1
        Public Property HeightPx As Double = -1
        Public Property MaxWidthPx As Double = -1

        Public ReadOnly Property IsBlock As Boolean
            Get
                Return Display = "block"
            End Get
        End Property

        Public ReadOnly Property IsHidden As Boolean
            Get
                Return Display = "none"
            End Get
        End Property
    End Class

    Public Enum BoxKind
        Block
        Inline
        Text
    End Enum

    ''' <summary>
    ''' One node of the box tree: a block box, an inline box, or a text run.
    ''' Phase 2's layout consumes only this -- it never sees HTML again.
    ''' </summary>
    Public NotInheritable Class BoxNode

        Public Property Kind As BoxKind
        Public Property TagName As String = String.Empty
        Public Property Text As String = String.Empty
        Public Property Style As ComputedStyle
        Public Property Children As New List(Of BoxNode)()
        Public Property Parent As BoxNode

        ''' <summary>True for a block generated to hold a stray inline run.</summary>
        Public Property Anonymous As Boolean

        Public Function DescendantCount() As Integer
            Dim total As Integer = Children.Count
            For Each childItem In Children
                total += childItem.DescendantCount()
            Next
            Return total
        End Function
    End Class

End Namespace
