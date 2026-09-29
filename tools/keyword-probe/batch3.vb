' keyword-probe batch 3. Read batch1.vb first: it carries the full explanation,
' the run command, and the meaning of the controls and the sentinel.
'
' THESE ARE THE WORDS THE TRUNCATED FIRST RUN NEVER COMPILED. The one-file
' version stopped reporting errors at line 146 -- vbc 12 gives up after about a
' hundred -- which made `mustinherit` through `custom` look legal when they had
' simply never been read. `out` is here as a candidate too, next to its control
' copy, because it is the word that started this: the language reference lists it
' as reserved and the compiler accepts it.

Option Strict On

Public Class KeywordProbe

    Public Sub Candidates()
        Dim erase As Integer
        Dim goto As Integer
        Dim gosub As Integer
        Dim overloads As Integer
        Dim overridable As Integer
        Dim overrides As Integer
        Dim paramarray As Integer
        Dim raiseevent As Integer
        Dim readonly As Integer
        Dim redim As Integer
        Dim shadows As Integer
        Dim synclock As Integer
        Dim true As Integer
        Dim false As Integer
        Dim nothing As Integer
        Dim wend As Integer
        Dim writeonly As Integer
        Dim widening As Integer
        Dim narrowing As Integer
        Dim byval As Integer
        Dim byref As Integer
        Dim optional As Integer
        Dim default As Integer
        Dim mustinherit As Integer
        Dim mustoverride As Integer
        Dim notinheritable As Integer
        Dim notoverridable As Integer
        Dim directcast As Integer
        Dim trycast As Integer
        Dim gettype As Integer
        Dim addressof As Integer
        Dim else As Integer
        Dim elseif As Integer
        Dim end As Integer
        Dim endif As Integer
        Dim async As Integer
        Dim await As Integer
        Dim custom As Integer
        Dim out As Integer
    End Sub

    Public Sub Controls()
        Dim out As Integer
        Dim value As Integer
        Dim name As Integer
        Dim type As Integer
        Dim count As Integer
        Dim time As Integer
    End Sub

    Public Sub Sentinel()
        Dim next As Integer
    End Sub

End Class
