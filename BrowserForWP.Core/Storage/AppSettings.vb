' BrowserForWP — persisted browser settings (pure data holder, no WinRT here).
'
' Persistence lives in the app layer (ApplicationData LocalSettings); this class
' only holds values, defaults and map import/export so it stays unit-testable
' off-device. The Node mirror is tools/proto/useragents.mjs.
'
' Properties are auto-implemented (single line, no Get/Set block): the static
' checker only recognises a parameterless Set, while real VB setters always
' carry a parameter, so an explicit Set block desynchronises its stack.
' Empty-guards live in LoadFromMap and at the call sites instead.
'
' Lite-first: search is the DuckDuckGo Lite endpoint, which Trident renders
' fast. There is exactly one engine and no picker: the full-site template was
' removed as a dead option rather than kept as a choice nobody can use well.

Imports System.Collections.Generic

Namespace Storage

    ''' <summary>All user-tunable browser settings with safe defaults.</summary>
    Public NotInheritable Class AppSettings

        Public Const DefaultHomepage As String = "https://lite.duckduckgo.com/lite/"
        Public Const DefaultSearchTemplate As String = "https://lite.duckduckgo.com/lite/?q={q}"
        Public Const DefaultDohUrl As String = "https://cloudflare-dns.com/dns-query"

        ''' <summary>Maximum tabs kept across a session restore (speed + memory).</summary>
        Public Const MaxSessionTabs As Integer = 6

        Public Sub New()
            Homepage = DefaultHomepage
            DohUrl = DefaultDohUrl
            DesktopMode = False
            LanguageOverride = Nothing
            NightMode = False
            BlockTrackers = True
            RestoreSession = False
            LiteRedirects = True
            EngineSetting = Engine.EngineChoice.Auto
            LastSessionTabs = String.Empty
            RemotePrimaryUrl = String.Empty
            RemotePrimaryToken = String.Empty
            RemoteSecondaryUrl = String.Empty
            RemoteSecondaryToken = String.Empty
            RemoteEnabled = False
        End Sub

        Public Property Homepage As String
        Public Property DohUrl As String
        Public Property DesktopMode As Boolean
        Public Property LanguageOverride As String
        Public Property NightMode As Boolean
        Public Property BlockTrackers As Boolean
        Public Property RestoreSession As Boolean
        Public Property LiteRedirects As Boolean

        ''' <summary>
        ''' Which engine renders, as one of Engine.EngineChoice's three keywords.
        '''
        ''' Named EngineSetting rather than EngineChoice on purpose: VB is
        ''' case-insensitive, so a property called EngineChoice would shadow the type
        ''' of the same name inside this class and every use of EngineChoice.Auto
        ''' below it would become a reference to a String.
        '''
        ''' Auto is the default, which means "the system engine unless a measurement
        ''' says otherwise": upgrading this app must not change what a user sees
        ''' without being asked.
        ''' </summary>
        Public Property EngineSetting As String

        Public Property LastSessionTabs As String

        ''' <summary>
        ''' Where pages are rendered when the remote engine is chosen. EMPTY by
        ''' default: this build bakes in no server at all, so nothing a user reads
        ''' leaves their device until they configure one and turn it on.
        ''' </summary>
        Public Property RemotePrimaryUrl As String

        ''' <summary>The secret the server issued for THIS device, pasted once.</summary>
        Public Property RemotePrimaryToken As String

        ''' <summary>
        ''' The fallback, tried only when the primary cannot be reached. Somebody
        ''' who points this at their own server is using the app without the
        ''' primary's author being able to go away.
        ''' </summary>
        Public Property RemoteSecondaryUrl As String

        ''' <summary>Falls back to the primary's token when empty.</summary>
        Public Property RemoteSecondaryToken As String

        ''' <summary>Off until a person turns it on. See ARCHITECTURE.md Law 5.</summary>
        Public Property RemoteEnabled As Boolean

        ''' <summary>Build a search URL from raw query text (single lite engine).</summary>
        Public Function SearchUrlFor(queryText As String) As String
            Dim safeQuery As String = If(queryText, String.Empty)
            Return DefaultSearchTemplate.Replace("{q}", Uri.EscapeDataString(safeQuery))
        End Function

        ''' <summary>Tabs saved for restore, http(s) only, capped.</summary>
        Public Function GetSessionTabs() As List(Of String)
            Dim result As New List(Of String)()
            If String.IsNullOrEmpty(LastSessionTabs) Then
                Return result
            End If
            Dim rawLines As String() = LastSessionTabs.Split(New String() {vbLf}, StringSplitOptions.None)
            For Each rawLine In rawLines
                Dim cleanLine As String = rawLine.Trim()
                If cleanLine.StartsWith("http", StringComparison.OrdinalIgnoreCase) Then
                    result.Add(cleanLine)
                End If
                If result.Count >= MaxSessionTabs Then
                    Exit For
                End If
            Next
            Return result
        End Function

        ''' <summary>Store tabs for restore: first 6, each truncated to 300 chars.</summary>
        Public Sub SetSessionTabs(pageUrls As IList(Of String))
            Dim kept As New List(Of String)()
            If pageUrls IsNot Nothing Then
                For Each pageUrl In pageUrls
                    If String.IsNullOrEmpty(pageUrl) Then
                        Continue For
                    End If
                    Dim cleanUrl As String = pageUrl.Trim()
                    If cleanUrl.Length > 300 Then
                        cleanUrl = cleanUrl.Substring(0, 300)
                    End If
                    kept.Add(cleanUrl)
                    If kept.Count >= MaxSessionTabs Then
                        Exit For
                    End If
                Next
            End If
            LastSessionTabs = String.Join(vbLf, kept.ToArray())
        End Sub

        ''' <summary>Export to a plain string map for LocalSettings persistence.</summary>
        Public Function SaveToMap() As Dictionary(Of String, String)
            Dim hostMap As New Dictionary(Of String, String)()
            hostMap("homepage") = If(String.IsNullOrEmpty(Homepage), DefaultHomepage, Homepage)
            hostMap("dohUrl") = If(String.IsNullOrEmpty(DohUrl), DefaultDohUrl, DohUrl)
            hostMap("desktopMode") = If(DesktopMode, "1", "0")
            hostMap("languageOverride") = If(LanguageOverride, String.Empty)
            hostMap("nightMode") = If(NightMode, "1", "0")
            hostMap("blockTrackers") = If(BlockTrackers, "1", "0")
            hostMap("restoreSession") = If(RestoreSession, "1", "0")
            hostMap("liteRedirects") = If(LiteRedirects, "1", "0")
            hostMap("engineSetting") = If(String.IsNullOrEmpty(EngineSetting), Engine.EngineChoice.Auto, EngineSetting)
            hostMap("lastSessionTabs") = If(LastSessionTabs, String.Empty)
            hostMap("remotePrimaryUrl") = If(RemotePrimaryUrl, String.Empty)
            hostMap("remotePrimaryToken") = If(RemotePrimaryToken, String.Empty)
            hostMap("remoteSecondaryUrl") = If(RemoteSecondaryUrl, String.Empty)
            hostMap("remoteSecondaryToken") = If(RemoteSecondaryToken, String.Empty)
            hostMap("remoteEnabled") = If(RemoteEnabled, "1", "0")
            Return hostMap
        End Function

        ''' <summary>Import from a plain string map; unknown keys are ignored.</summary>
        Public Sub LoadFromMap(sourceMap As IDictionary(Of String, String))
            If sourceMap Is Nothing Then
                Return
            End If
            Dim foundValue As String = Nothing
            If sourceMap.TryGetValue("homepage", foundValue) Then
                If String.IsNullOrEmpty(foundValue) Then
                    Homepage = DefaultHomepage
                Else
                    Homepage = foundValue
                End If
            End If
            If sourceMap.TryGetValue("dohUrl", foundValue) Then
                If String.IsNullOrEmpty(foundValue) Then
                    DohUrl = DefaultDohUrl
                Else
                    DohUrl = foundValue
                End If
            End If
            If sourceMap.TryGetValue("desktopMode", foundValue) Then
                DesktopMode = (foundValue = "1")
            End If
            If sourceMap.TryGetValue("languageOverride", foundValue) Then
                If String.IsNullOrEmpty(foundValue) Then
                    LanguageOverride = Nothing
                Else
                    LanguageOverride = foundValue
                End If
            End If
            If sourceMap.TryGetValue("nightMode", foundValue) Then
                NightMode = (foundValue = "1")
            End If
            If sourceMap.TryGetValue("blockTrackers", foundValue) Then
                If String.IsNullOrEmpty(foundValue) Then
                    BlockTrackers = True
                Else
                    BlockTrackers = (foundValue = "1")
                End If
            End If
            If sourceMap.TryGetValue("restoreSession", foundValue) Then
                RestoreSession = (foundValue = "1")
            End If
            If sourceMap.TryGetValue("liteRedirects", foundValue) Then
                If String.IsNullOrEmpty(foundValue) Then
                    LiteRedirects = True
                Else
                    LiteRedirects = (foundValue = "1")
                End If
            End If
            If sourceMap.TryGetValue("engineSetting", foundValue) Then
                ' Normalize, not a raw copy: a stored value this version does not
                ' recognise becomes Auto instead of leaving the shell with an engine
                ' keyword nothing understands.
                EngineSetting = Engine.EngineChoice.Normalize(foundValue)
            End If
            If sourceMap.TryGetValue("lastSessionTabs", foundValue) Then
                LastSessionTabs = If(foundValue, String.Empty)
            End If
            ' The urls go through Normalize for the same reason EngineSetting goes
            ' through EngineChoice.Normalize: a stored value this version cannot
            ' honour has to become "not configured" rather than a broken attempt
            ' that fails later and somewhere else.
            If sourceMap.TryGetValue("remotePrimaryUrl", foundValue) Then
                RemotePrimaryUrl = Remote.RemoteServers.Normalize(foundValue)
            End If
            If sourceMap.TryGetValue("remotePrimaryToken", foundValue) Then
                RemotePrimaryToken = If(foundValue, String.Empty)
            End If
            If sourceMap.TryGetValue("remoteSecondaryUrl", foundValue) Then
                RemoteSecondaryUrl = Remote.RemoteServers.Normalize(foundValue)
            End If
            If sourceMap.TryGetValue("remoteSecondaryToken", foundValue) Then
                RemoteSecondaryToken = If(foundValue, String.Empty)
            End If
            If sourceMap.TryGetValue("remoteEnabled", foundValue) Then
                RemoteEnabled = (foundValue = "1")
            End If
        End Sub
    End Class

End Namespace
