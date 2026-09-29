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
' Lite-first: defaults point at DuckDuckGo Lite, which Trident renders fast.
' Stored Google/Bing templates from earlier versions migrate to the default.

Imports System.Collections.Generic

Namespace Storage

    ''' <summary>All user-tunable browser settings with safe defaults.</summary>
    Public NotInheritable Class AppSettings

        Public Const DefaultHomepage As String = "https://lite.duckduckgo.com/lite/"
        Public Const DefaultSearchTemplate As String = "https://lite.duckduckgo.com/lite/?q={q}"
        Public Const DefaultDohUrl As String = "https://cloudflare-dns.com/dns-query"

        ''' <summary>
        ''' The hosted renderer this build ships pointed at: the server this
        ''' project's operator runs, where Chromium draws pages this phone cannot.
        '''
        ''' It is an address and not a credential. The device token is issued per
        ''' device by the server's own `bfwp-device add` and pasted in by hand, so an
        ''' install that has not been registered is pointed at a server it cannot
        ''' use yet -- which RemoteServers.Ready reports as "not configured" and
        ''' EngineChoice turns into the on-device engine, rather than a page sent
        ''' nowhere. See ARCHITECTURE.md Law 5 for what this default discloses.
        '''
        ''' No port: the render channel's own port is RemoteEngine.DefaultPort
        ''' (8443), and the port lives there so that a url which spells one out can
        ''' still override it.
        ''' </summary>
        Public Const DefaultHostedUrl As String = "https://34.132.106.149"

        ''' <summary>Maximum tabs kept across a session restore (speed + memory).</summary>
        Public Const MaxSessionTabs As Integer = 10

        Public Sub New()
            Homepage = DefaultHomepage
            SearchTemplate = DefaultSearchTemplate
            DohUrl = DefaultDohUrl
            DesktopMode = False
            LanguageOverride = Nothing
            NightMode = False
            BlockTrackers = True
            RestoreSession = False
            LiteRedirects = True
            EngineSetting = Engine.EngineChoice.Remote
            LastSessionTabs = String.Empty
            RemotePrimaryUrl = DefaultHostedUrl
            RemotePrimaryToken = String.Empty
            RemoteSecondaryUrl = String.Empty
            RemoteSecondaryToken = String.Empty
            RemoteEnabled = True
        End Sub

        Public Property Homepage As String
        Public Property SearchTemplate As String
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
        ''' Remote is the default: pages are drawn by the hosted server, because on
        ''' this platform that is the only engine that can draw a modern page at all.
        ''' What keeps that honest is EngineChoice.Decide, which falls back to the
        ''' on-device engine whenever the hosted one is not usable, and Auto for the
        ''' people who would rather let the measurement decide.
        ''' </summary>
        Public Property EngineSetting As String

        Public Property LastSessionTabs As String

        ''' <summary>
        ''' Where pages are rendered when the hosted engine is chosen. Points at
        ''' DefaultHostedUrl out of the box, and a person who runs their own server
        ''' replaces it here -- which is also how the operator of the default becomes
        ''' somebody they chose instead of somebody they did not.
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

        ''' <summary>
        ''' ON by default, because the hosted engine is the default engine here. It
        ''' is the disclosure's switch as much as the engine's: turning it off keeps
        ''' every page on the device. See ARCHITECTURE.md Law 5.
        ''' </summary>
        Public Property RemoteEnabled As Boolean

        ''' <summary>
        ''' These settings as Core's rules take them. ONE definition, used by the shell
        ''' (to ask whether the hosted engine is usable before choosing it) and by
        ''' RemoteEngine (to decide where to connect), so the two cannot come to
        ''' disagree about which server a person just configured.
        '''
        ''' Built on every call, never cached: the settings screen can change the
        ''' server between two navigations.
        ''' </summary>
        Public Function RemoteSettings() As Remote.RemoteServerSettings
            Dim snapshot As New Remote.RemoteServerSettings()
            snapshot.PrimaryUrl = RemotePrimaryUrl
            snapshot.PrimaryToken = RemotePrimaryToken
            snapshot.SecondaryUrl = RemoteSecondaryUrl
            snapshot.SecondaryToken = RemoteSecondaryToken
            snapshot.RemoteEnabled = RemoteEnabled
            Return snapshot
        End Function

        ''' <summary>Build a search URL from raw query text.</summary>
        Public Function SearchUrlFor(queryText As String) As String
            Dim safeQuery As String = If(queryText, String.Empty)
            Dim templateText As String = If(String.IsNullOrEmpty(SearchTemplate), DefaultSearchTemplate, SearchTemplate)
            Return templateText.Replace("{q}", Uri.EscapeDataString(safeQuery))
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

        ''' <summary>Store tabs for restore: first 10, each truncated to 300 chars.</summary>
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

        ''' <summary>Legacy heavy search engines migrate to the lite default.</summary>
        Public Shared Function MigrateSearchTemplate(storedTemplate As String) As String
            If String.IsNullOrEmpty(storedTemplate) Then
                Return DefaultSearchTemplate
            End If
            Dim loweredTemplate As String = storedTemplate.ToLowerInvariant()
            If loweredTemplate.Contains("google.") OrElse loweredTemplate.Contains("bing.") Then
                Return DefaultSearchTemplate
            End If
            Return storedTemplate
        End Function

        ''' <summary>Export to a plain string map for LocalSettings persistence.</summary>
        Public Function SaveToMap() As Dictionary(Of String, String)
            Dim hostMap As New Dictionary(Of String, String)()
            hostMap("homepage") = If(String.IsNullOrEmpty(Homepage), DefaultHomepage, Homepage)
            hostMap("searchTemplate") = If(String.IsNullOrEmpty(SearchTemplate), DefaultSearchTemplate, SearchTemplate)
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
            If sourceMap.TryGetValue("searchTemplate", foundValue) Then
                SearchTemplate = MigrateSearchTemplate(foundValue)
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
