' keyword-probe batch 2. Read batch1.vb first: it carries the full explanation,
' the run command, and the meaning of the controls and the sentinel.
'
' Same shape as batch 1, and the same two self-checks: the controls must come
' back clean and the sentinel -- `Dim next As Integer`, last -- must come back
' REFUSED. A sentinel missing from the log means vbc stopped early and the rest
' of this batch was never compiled.

Option Strict On

Public Class KeywordProbe

    Public Sub Candidates()
        Dim when As Integer
        Dim while As Integer
        Dim select As Integer
        Dim shared As Integer
        Dim partial As Integer
        Dim private As Integer
        Dim public As Integer
        Dim friend As Integer
        Dim protected As Integer
        Dim property As Integer
        Dim event As Integer
        Dim exit As Integer
        Dim loop As Integer
        Dim each As Integer
        Dim option As Integer
        Dim module As Integer
        Dim class As Integer
        Dim structure As Integer
        Dim interface As Integer
        Dim imports As Integer
        Dim inherits As Integer
        Dim implements As Integer
        Dim handles As Integer
        Dim return As Integer
        Dim continue As Integer
        Dim try As Integer
        Dim catch As Integer
        Dim finally As Integer
        Dim throw As Integer
        Dim call As Integer
        Dim typeof As Integer
        Dim with As Integer
        Dim xor As Integer
        Dim mod As Integer
        Dim like As Integer
        Dim alias As Integer
        Dim declare As Integer
        Dim delegate As Integer
        Dim enum As Integer
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
