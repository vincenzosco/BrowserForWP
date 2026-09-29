' ═══════════════════════════════════════════════════════════════════════════
'  keyword-probe — which VB keywords the VS2013 compiler refuses as a NAME
'
'  WHY THIS EXISTS. tools/check-vb.mjs group 17 flags VB keywords used as
'  identifiers, because `Dim next As UInteger = _outSequence + 1UI` cost a guest
'  build: BC30201 on that line and eleven BC30451 "is not declared" on the lines
'  after it, every one of them naming something that plainly is declared.
'
'  The check's list was first written from the language reference. THE REFERENCE
'  IS NOT THE COMPILER: `Out` is in the reference's reserved list, and
'  `Dim out(31) As Byte` compiles -- it is in BrowserForWP.Crypto/X25519.vb, and
'  that project builds in all six configurations. A check that GATES A BUILD and
'  cries wolf gets ignored, so the list is MEASURED instead of quoted.
'
'  WHY THREE FILES. vbc 12 is the pre-Roslyn compiler, and it GIVES UP after
'  about a hundred errors -- silently. The first version of this probe was one
'  file with 117 candidates, and it reported exactly one error per candidate up
'  to line 146 and then nothing at all, which reads as "the last sixteen words
'  are legal". They were never compiled. Hence: batches small enough to stay
'  under the cap, and a SENTINEL at the end of every batch.
'
'  HOW TO RUN IT (from the macOS host):
'
'    prlctl exec "66a2f493-162c-4b3f-ba40-0a26020cc818" cmd /c ^
'        "C:\Mac\Home\Documents\BrowserForWP\tools\keyword-probe.cmd"
'
'  HOW TO READ IT. Every candidate is `Dim <word> As Integer` on its own line, so
'  a reported line number IS the candidate. No error means the word is LEGAL as a
'  name and must not be in the check's list; an error (BC30183, "keyword not
'  valid as an identifier") means it is illegal and must be in it.
'
'  The controls must come back clean, and the SENTINEL must come back REFUSED.
'  A control in the error list, or a sentinel missing from it, voids the batch:
'  the first means the harness is broken, the second means the compiler stopped
'  early and the "no error" answers below it are not answers.
'
'  Measured 2026-09-29 with vbc 12.0.40629.0. The result is the list in
'  tools/check-vb.mjs, group 17.
' ═══════════════════════════════════════════════════════════════════════════

Option Strict On

Public Class KeywordProbe

    Public Sub Candidates()
        Dim next As Integer
        Dim error As Integer
        Dim date As Integer
        Dim step As Integer
        Dim to As Integer
        Dim in As Integer
        Dim of As Integer
        Dim on As Integer
        Dim not As Integer
        Dim then As Integer
        Dim is As Integer
        Dim as As Integer
        Dim me As Integer
        Dim new As Integer
        Dim single As Integer
        Dim static As Integer
        Dim string As Integer
        Dim object As Integer
        Dim char As Integer
        Dim decimal As Integer
        Dim boolean As Integer
        Dim byte As Integer
        Dim short As Integer
        Dim long As Integer
        Dim integer As Integer
        Dim double As Integer
        Dim set As Integer
        Dim get As Integer
        Dim if As Integer
        Dim for As Integer
        Dim do As Integer
        Dim or As Integer
        Dim and As Integer
        Dim using As Integer
        Dim rem As Integer
        Dim lib As Integer
        Dim let As Integer
        Dim stop As Integer
        Dim resume As Integer
    End Sub

    ' Known-legal names. If one of these is refused, the harness is broken and
    ' the batch says nothing about any candidate.
    Public Sub Controls()
        Dim out As Integer
        Dim value As Integer
        Dim name As Integer
        Dim type As Integer
        Dim count As Integer
        Dim time As Integer
    End Sub

    ' Declared LAST, and illegal. Its line number must appear in the error list.
    ' If it does not, the compilation stopped before reaching it and every
    ' "no error" above it means "not compiled" rather than "legal".
    Public Sub Sentinel()
        Dim next As Integer
    End Sub

End Class
