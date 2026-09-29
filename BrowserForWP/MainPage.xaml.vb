' BrowserForWP — the browser shell.
'
' Hosts the engine, drives navigation, persists settings/history/favorites/pins,
' injects the compat bundle, and exposes TLS-probe + compatibility diagnostics.

Imports BrowserForWP.Core.Browser
Imports BrowserForWP.Core.Diagnostics
Imports BrowserForWP.Core.Engine
Imports BrowserForWP.Core.Engine.Native
Imports BrowserForWP.Core.Engine.Remote
' RemoteServers is in BrowserForWP.Core.Remote; see the note in Engine/RemoteEngine.vb.
Imports BrowserForWP.Core.Remote
Imports BrowserForWP.Core.Storage
Imports BrowserForWP.Localization
Imports BrowserForWP.Net.Tls13
Imports Windows.ApplicationModel.DataTransfer
Imports Windows.Phone.UI.Input
Imports Windows.Storage
Imports Windows.Storage.Pickers
Imports Windows.UI.Xaml
Imports Windows.UI.Xaml.Controls
Imports Windows.UI.Xaml.Input
Imports Windows.UI.Xaml.Navigation
Imports Windows.Web

Public NotInheritable Class MainPage
    Inherits Page

    ' The engine is mutable from this round on: Settings can hand the shell a
    ' different one, and UseEngine is the only thing that ever assigns it. The two
    ' candidates are built once and kept, because building one is also what attaches
    ' its events.
    Private _engine As IBrowserEngine
    Private _tridentEngine As TridentEngine
    Private _remoteEngine As BrowserForWP.Engine.RemoteEngine
    Private ReadOnly _session As New BrowserSession()
    Private ReadOnly _appSettings As New AppSettings()
    Private ReadOnly _historyStore As New HistoryStore()
    Private ReadOnly _favoritesStore As New FavoritesStore()
    Private ReadOnly _savedPages As New SavedPages()
    Private ReadOnly _sitePrefs As New SiteSettings()
    Private ReadOnly _speedDial As New SpeedDial()
    Private ReadOnly _pinTable As New PinStore()

    Private _navigationToken As Object
    Private _injectedToken As Object
    Private _settingsPopulated As Boolean = False

    ' The remote engine's sound. It lives here rather than in the engine because
    ' MediaElement is a piece of the shell's visual tree, and the engine's host is
    ' a picture: a control that draws nothing still has to be in the tree for the
    ' platform to play it.
    Private ReadOnly _audio As New MediaElement()
    Private _audioHosted As Boolean

    ''' <summary>True while pickers/lists are repopulated, so programmatic selection is ignored.</summary>
    Private _populatingLanguage As Boolean = False
    Private _refreshingTabs As Boolean = False
    Private _populatingEngine As Boolean = False

    Protected Overrides Sub OnNavigatedTo(e As NavigationEventArgs)
        MyBase.OnNavigatedTo(e)

        AddHandler HardwareButtons.BackPressed, AddressOf OnHardwareBackPressed
        AddHandler DataTransferManager.GetForCurrentView().DataRequested, AddressOf OnShareRequested

        ' Nothing engine-shaped is wired here any more. Which engine the shell gets
        ' depends on a persisted setting, so both the host control and the platform
        ' event handlers are attached by ApplyEngineChoice below, once
        ' LoadPersistedState has run.

        LoadPersistedState()

        ' THE ENGINE IS CHOSEN BEFORE THE STRINGS ARE APPLIED, and the order is not
        ' cosmetic. ApplyLocalizedStrings reads _engine.Capabilities to build the
        ' diagnostics line, and it used to run FIRST -- so on the very first launch
        ' the shell dereferenced Nothing before any engine existed. A
        ' NullReferenceException inside OnNavigatedTo is not caught by anything, so
        ' that ordering crashed the app on start: every feature in this repository
        ' was unreachable, including the ten rows in the hand-written verification
        ' table in docs/MAINTAINING.md, which all begin at a screen that would never
        ' have drawn. Nothing here needed the strings in place first.
        ApplyEngineChoice()
        ApplyLocalizedStrings()
        RefreshTabsList()
        RefreshHistoryList()
        RefreshFavoritesList()
        RefreshSavedList()
        RefreshSpeedDialList()

        _session.DesktopMode = _appSettings.DesktopMode
        Dim restoredTabs As List(Of String) = _appSettings.GetSessionTabs()
        If _appSettings.RestoreSession AndAlso restoredTabs.Count > 0 Then
            For Each restoredUrl In restoredTabs
                _session.NewTab()
                _session.ActiveTab.PushHistory(restoredUrl)
            Next
            _session.ActivateTab(_session.Tabs.Count - 1)
            _engine.Navigate(_session.ActiveTab.Url)
        Else
            _session.NewTab()
            _engine.Navigate(_appSettings.Homepage)
        End If
    End Sub

    Protected Overrides Sub OnNavigatedFrom(e As NavigationEventArgs)
        MyBase.OnNavigatedFrom(e)
        RemoveHandler HardwareButtons.BackPressed, AddressOf OnHardwareBackPressed
        RemoveHandler DataTransferManager.GetForCurrentView().DataRequested, AddressOf OnShareRequested
        ' Only the WebView has this event, and only if the WebView was ever built.
        If _tridentEngine IsNot Nothing Then
            RemoveHandler _tridentEngine.View.DOMContentLoaded, AddressOf OnDOMContentLoaded
        End If
        SavePersistedState()
    End Sub

    Private Sub LoadPersistedState()
        Try
            Dim localValues = ApplicationData.Current.LocalSettings.Values
            Dim hostMap As New Dictionary(Of String, String)()
            For Each pairItem In localValues
                If TypeOf pairItem.Value Is String Then
                    hostMap(pairItem.Key) = DirectCast(pairItem.Value, String)
                End If
            Next
            _appSettings.LoadFromMap(hostMap)
            If localValues.ContainsKey("history") Then
                Dim historyText As String = TryCast(localValues("history"), String)
                _historyStore.Parse(If(historyText, String.Empty))
            End If
            If localValues.ContainsKey("favorites") Then
                Dim favoritesText As String = TryCast(localValues("favorites"), String)
                _favoritesStore.Parse(If(favoritesText, String.Empty))
            End If
            If localValues.ContainsKey("pins") Then
                Dim pinsText As String = TryCast(localValues("pins"), String)
                _pinTable.Parse(If(pinsText, String.Empty))
            End If
            If localValues.ContainsKey("saved") Then
                Dim savedText As String = TryCast(localValues("saved"), String)
                _savedPages.Parse(If(savedText, String.Empty))
            End If
            If localValues.ContainsKey("sites") Then
                Dim sitesText As String = TryCast(localValues("sites"), String)
                _sitePrefs.Parse(If(sitesText, String.Empty))
            End If
            If localValues.ContainsKey("speeddial") Then
                Dim dialText As String = TryCast(localValues("speeddial"), String)
                _speedDial.Parse(If(dialText, String.Empty))
            End If
            If Not String.IsNullOrEmpty(_appSettings.LanguageOverride) Then
                Localizer.Override(_appSettings.LanguageOverride)
            End If
        Catch ex As Exception
        End Try
    End Sub

    Private Sub SavePersistedState()
        Try
            Dim localValues = ApplicationData.Current.LocalSettings.Values
            Dim hostMap As Dictionary(Of String, String) = _appSettings.SaveToMap()
            For Each pairItem In hostMap
                localValues(pairItem.Key) = pairItem.Value
            Next
            localValues("history") = _historyStore.Serialize()
            localValues("favorites") = _favoritesStore.Serialize()
            localValues("pins") = _pinTable.Serialize()
            localValues("saved") = _savedPages.Serialize()
            localValues("sites") = _sitePrefs.Serialize()
            localValues("speeddial") = _speedDial.Serialize()
        Catch ex As Exception
        End Try
    End Sub

    Private Sub ApplyLocalizedStrings()
        AddressBox.PlaceholderText = Localizer.Get("AddressPlaceholder")
        GoButton.Content = Localizer.Get("Go")
        BackButton.Content = Localizer.Get("Back")
        ForwardButton.Content = Localizer.Get("Forward")
        ReloadButton.Content = Localizer.Get("Reload")
        StopButton.Content = Localizer.Get("Stop")
        TabsButton.Content = Localizer.Get("NewTab") & " (" & _session.Tabs.Count & ")"
        FindButton.Content = Localizer.Get("Find")
        ReadingButton.Content = Localizer.Get("ReadingMode")
        ShareButton.Content = Localizer.Get("Share")
        SavePageButton.Content = Localizer.Get("SavePage")
        RetryButton.Content = Localizer.Get("Retry")
        FindNextButton.Content = Localizer.Get("Find")
        FindCloseButton.Content = Localizer.Get("DiagnosticsClose")
        SecurityDetailsButton.Content = Localizer.Get("SecurityDetails")
        SettingsButton.Content = Localizer.Get("Settings")

        ' The Settings overlay is titled "Settings". It used to read "Diagnostics",
        ' because these two heading keys were swapped with the ones below.
        SettingsTitle.Text = Localizer.Get("Settings")
        LanguageLabel.Text = Localizer.Get("LanguageLabel")
        DiagnosticsTitle.Text = Localizer.Get("DiagnosticsTitle")
        CompatProbeButton.Content = Localizer.Get("DiagnosticsCompatProbe")
        CloseSettingsButton.Content = Localizer.Get("DiagnosticsClose")
        DiagnosticsButton.Content = Localizer.Get("Diagnostics")
        DiagnosticsBackButton.Content = Localizer.Get("Back")
        DesktopToggle.Content = Localizer.Get("DesktopSite")
        PrivateModeToggle.Content = Localizer.Get("PrivateMode")
        NightModeToggle.Content = Localizer.Get("NightMode")
        BlockTrackersToggle.Content = Localizer.Get("BlockTrackers")
        RestoreSessionToggle.Content = Localizer.Get("RestoreSession")
        LiteRedirectsToggle.Content = Localizer.Get("LiteRedirects")
        HomepageLabel.Text = Localizer.Get("HomepageLabel")
        EngineLabel.Text = Localizer.Get("EngineLabel")
        TabsTitle.Text = Localizer.Get("TabsTitle")
        CloseTabButton.Content = Localizer.Get("CloseTab")
        HistoryTitle.Text = Localizer.Get("HistoryTitle")
        ClearHistoryButton.Content = Localizer.Get("ClearHistory")
        FavoritesTitle.Text = Localizer.Get("FavoritesTitle")
        AddFavoriteButton.Content = Localizer.Get("AddFavorite")
        RemoveFavoriteButton.Content = Localizer.Get("RemoveFavorite")
        TlsProbeHostLabel.Text = Localizer.Get("TlsProbeHostLabel")
        TlsProbeButton.Content = Localizer.Get("TlsProbeRun")
        DohServerLabel.Text = Localizer.Get("DohServerLabel")
        PinTitle.Text = Localizer.Get("PinTitle")
        PinAddButton.Content = Localizer.Get("PinAdd")
        PinRemoveButton.Content = Localizer.Get("PinRemove")
        IeModeButton.Content = Localizer.Get("IeModeCheck")
        RemoteKeysButton.Content = Localizer.Get("RemoteKeys")
        KeyTabButton.Content = Localizer.Get("KeyTab")
        KeyEnterButton.Content = Localizer.Get("KeyEnter")
        KeyEscapeButton.Content = Localizer.Get("KeyEscape")
        KeyBackspaceButton.Content = Localizer.Get("KeyBackspace")
        KeyLeftButton.Content = Localizer.Get("KeyLeft")
        KeyUpButton.Content = Localizer.Get("KeyUp")
        KeyDownButton.Content = Localizer.Get("KeyDown")
        KeyRightButton.Content = Localizer.Get("KeyRight")
        KeyBarCloseButton.Content = Localizer.Get("KeyBarClose")
        SavedPagesTitle.Text = Localizer.Get("SavedPages")
        DeleteSavedButton.Content = Localizer.Get("SavedDelete")
        SpeedDialTitle.Text = Localizer.Get("SpeedDial")
        SpeedDialAddButton.Content = Localizer.Get("SpeedDialAdd")
        SpeedDialRemoveButton.Content = Localizer.Get("SpeedDialRemove")
        BackupButton.Content = Localizer.Get("Backup")
        RestoreButton.Content = Localizer.Get("Restore")
        OfflineBackButton.Content = Localizer.Get("DiagnosticsClose")
        ImagesToggle.Content = Localizer.Get("Images")
        TextSizeLabel.Text = Localizer.Get("TextSize")
        UpdateSiteSection()

        DesktopToggle.IsChecked = _session.DesktopMode
        PrivateModeToggle.IsChecked = _session.PrivateMode
        NightModeToggle.IsChecked = _appSettings.NightMode
        BlockTrackersToggle.IsChecked = _appSettings.BlockTrackers
        RestoreSessionToggle.IsChecked = _appSettings.RestoreSession
        LiteRedirectsToggle.IsChecked = _appSettings.LiteRedirects
        HomepageBox.Text = _appSettings.Homepage
        DohServerBox.Text = _appSettings.DohUrl
        RemoteNoticeLabel.Text = Localizer.Get("RemoteNotice")
        RemoteServerLabel.Text = Localizer.Get("RemoteServerLabel")
        RemoteTokenLabel.Text = Localizer.Get("RemoteTokenLabel")
        RemoteBackupLabel.Text = Localizer.Get("RemoteBackupLabel")
        RemoteBackupTokenLabel.Text = Localizer.Get("RemoteBackupTokenLabel")
        RemoteEnabledToggle.Content = Localizer.Get("RemoteUseServer")
        RemoteServerBox.Text = _appSettings.RemotePrimaryUrl
        RemoteTokenBox.Text = _appSettings.RemotePrimaryToken
        RemoteBackupBox.Text = _appSettings.RemoteSecondaryUrl
        RemoteBackupTokenBox.Text = _appSettings.RemoteSecondaryToken
        RemoteEnabledToggle.IsChecked = _appSettings.RemoteEnabled

        Dim capabilities = _engine.Capabilities
        ' The layer state used to be hardcoded English ("compatibility layer
        ' active") and was prefixed with DiagnosticsProbe, which is the TLS probe
        ' button's own label ("Run TLS probe"). Both now come from the catalogue.
        Dim layerState As String = If(capabilities.NeedsPolyfillLayer,
                                      Localizer.Get("CompatLayerActive"),
                                      Localizer.Get("CompatLayerNative"))
        EngineText.Text = Localizer.Get("DiagnosticsEngineLabel") & ":" & vbCrLf &
                          capabilities.ToString() & vbCrLf &
                          Localizer.Get("CompatLayerLabel") & ": " & layerState
    End Sub

    Private Sub PopulateLanguagePicker()
        _populatingLanguage = True
        Try
            LanguagePicker.Items.Clear()
            LanguagePicker.Items.Add(Localizer.Get("LanguageAutomatic"))
            For Each supportedTag As String In LanguageCatalog.Supported
                LanguagePicker.Items.Add(LanguageCatalog.DisplayName(supportedTag))
            Next
            LanguagePicker.SelectedIndex = If(Localizer.IsOverridden, 1, 0)
        Finally
            _populatingLanguage = False
        End Try
    End Sub

    ''' <summary>
    ''' The three choices, in the order EngineIndexOf and EngineSettingFor agree on.
    ''' The picker is repopulated under a guard, exactly like the language picker, so
    ''' that setting SelectedIndex programmatically does not look like a user choice.
    ''' </summary>
    Private Sub PopulateEnginePicker()
        _populatingEngine = True
        Try
            EnginePicker.Items.Clear()
            EnginePicker.Items.Add(Localizer.Get("EngineAuto"))
            EnginePicker.Items.Add(Localizer.Get("EngineTrident"))
            EnginePicker.Items.Add(Localizer.Get("EngineRemote"))
            EnginePicker.SelectedIndex = EngineIndexOf(EngineChoice.Normalize(_appSettings.EngineSetting))
        Finally
            _populatingEngine = False
        End Try
    End Sub

    Private Shared Function EngineIndexOf(normalizedSetting As String) As Integer
        If normalizedSetting = EngineChoice.Remote Then Return 2
        If normalizedSetting = EngineChoice.Trident Then Return 1
        Return 0
    End Function

    Private Shared Function EngineSettingFor(pickedIndex As Integer) As String
        If pickedIndex = 2 Then Return EngineChoice.Remote
        If pickedIndex = 1 Then Return EngineChoice.Trident
        Return EngineChoice.Auto
    End Function

    Private Sub EnginePicker_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
        If _populatingEngine Then Return
        _appSettings.EngineSetting = EngineSettingFor(EnginePicker.SelectedIndex)
        SavePersistedState()
        ApplyEngineChoice()
        Dim tabUrl As String = _session.ActiveTab.Url
        If String.IsNullOrEmpty(tabUrl) Then
            tabUrl = _appSettings.Homepage
        End If
        _engine.Navigate(tabUrl)
    End Sub

    Private Sub RefreshTabsList()
        _refreshingTabs = True
        Try
            TabsList.Items.Clear()
            For i As Integer = 0 To _session.Tabs.Count - 1
                Dim tabUrl As String = _session.Tabs(i).Url
                If String.IsNullOrEmpty(tabUrl) Then
                    tabUrl = _appSettings.Homepage
                End If
                TabsList.Items.Add((i + 1) & ": " & tabUrl)
            Next
            If _session.ActiveIndex >= 0 AndAlso _session.ActiveIndex < TabsList.Items.Count Then
                TabsList.SelectedIndex = _session.ActiveIndex
            End If
            TabsButton.Content = Localizer.Get("NewTab") & " (" & _session.Tabs.Count & ")"
        Finally
            _refreshingTabs = False
        End Try
    End Sub

    Private Sub RefreshHistoryList()
        HistoryList.Items.Clear()
        Dim entries As IList(Of HistoryEntry) = _historyStore.List()
        For i As Integer = entries.Count - 1 To 0 Step -1
            HistoryList.Items.Add(entries(i).Url)
            If HistoryList.Items.Count >= 25 Then
                Exit For
            End If
        Next
    End Sub

    Private Sub RefreshFavoritesList()
        FavoritesList.Items.Clear()
        Dim entries As IList(Of FavoriteEntry) = _favoritesStore.List()
        For Each favEntry In entries
            FavoritesList.Items.Add(favEntry.Url)
        Next
    End Sub

    Private Sub RefreshSavedList()
        SavedPagesList.Items.Clear()
        Dim entries As IList(Of SavedPageEntry) = _savedPages.List()
        For i As Integer = entries.Count - 1 To 0 Step -1
            Dim shownTitle As String = entries(i).Title
            If String.IsNullOrEmpty(shownTitle) Then
                shownTitle = entries(i).Url
            End If
            SavedPagesList.Items.Add(shownTitle)
        Next
    End Sub

    Private Sub RefreshSpeedDialList()
        SpeedDialList.Items.Clear()
        Dim entries As IList(Of SpeedDialEntry) = _speedDial.List()
        For Each dialEntry In entries
            SpeedDialList.Items.Add(dialEntry.Title)
        Next
    End Sub

    Private Sub LanguagePicker_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
        If _populatingLanguage Then
            Return
        End If
        If LanguagePicker.SelectedIndex <= 0 Then
            Localizer.Override(Nothing)
            _appSettings.LanguageOverride = Nothing
        ElseIf LanguagePicker.SelectedIndex - 1 < LanguageCatalog.Supported.Length Then
            Dim pickedTag As String = LanguageCatalog.Supported(LanguagePicker.SelectedIndex - 1)
            Localizer.Override(pickedTag)
            _appSettings.LanguageOverride = pickedTag
        End If
        SavePersistedState()
        ApplyLocalizedStrings()
        PopulateLanguagePicker()
    End Sub

    Private Sub DesktopToggle_Checked(sender As Object, e As RoutedEventArgs)
        _session.DesktopMode = True
        _appSettings.DesktopMode = True
        SavePersistedState()
    End Sub

    Private Sub DesktopToggle_Unchecked(sender As Object, e As RoutedEventArgs)
        _session.DesktopMode = False
        _appSettings.DesktopMode = False
        SavePersistedState()
    End Sub

    Private Sub PrivateModeToggle_Checked(sender As Object, e As RoutedEventArgs)
        _session.PrivateMode = True
    End Sub

    Private Sub PrivateModeToggle_Unchecked(sender As Object, e As RoutedEventArgs)
        _session.PrivateMode = False
    End Sub

    Private Sub NightModeToggle_Checked(sender As Object, e As RoutedEventArgs)
        _appSettings.NightMode = True
        SavePersistedState()
        ApplyNightMode()
    End Sub

    Private Sub NightModeToggle_Unchecked(sender As Object, e As RoutedEventArgs)
        _appSettings.NightMode = False
        SavePersistedState()
        ApplyNightMode()
    End Sub

    Private Async Sub ApplyNightMode()
        Try
            Dim scripted As TridentEngine = ScriptedEngine
            If scripted Is Nothing Then
                Return
            End If
            Await ApplyPageTweaksAsync(scripted)
        Catch ex As Exception
        End Try
    End Sub

    Private Sub BlockTrackersToggle_Checked(sender As Object, e As RoutedEventArgs)
        _appSettings.BlockTrackers = True
        SavePersistedState()
    End Sub

    Private Sub BlockTrackersToggle_Unchecked(sender As Object, e As RoutedEventArgs)
        _appSettings.BlockTrackers = False
        SavePersistedState()
    End Sub

    Private Sub RestoreSessionToggle_Checked(sender As Object, e As RoutedEventArgs)
        _appSettings.RestoreSession = True
        SavePersistedState()
    End Sub

    Private Sub RestoreSessionToggle_Unchecked(sender As Object, e As RoutedEventArgs)
        _appSettings.RestoreSession = False
        SavePersistedState()
    End Sub

    Private Sub LiteRedirectsToggle_Checked(sender As Object, e As RoutedEventArgs)
        _appSettings.LiteRedirects = True
        SavePersistedState()
    End Sub

    Private Sub LiteRedirectsToggle_Unchecked(sender As Object, e As RoutedEventArgs)
        _appSettings.LiteRedirects = False
        SavePersistedState()
    End Sub

    Private Async Sub ApplyNightMode()
        ' TryCast, not DirectCast. Night mode is a Trident feature applied inside
        ' the page; the remote engine applies it on the SERVER through the SETTINGS
        ' message instead, so there is nothing for this method to do. With
        ' DirectCast the remote engine made this line throw and the empty Catch
        ' below swallow it -- the same silent nothing, arrived at by an exception.
        ' An exception used as control flow, inside a handler that discards it, is
        ' how a real failure later gets discarded too.
        Dim scripted As TridentEngine = TryCast(_engine, TridentEngine)
        If scripted Is Nothing Then Return
        Try
            Await scripted.SetNightModeAsync(_appSettings.NightMode)
        Catch ex As Exception
        End Try
    End Sub

    ''' <summary>Host of the active tab, or empty when there is nothing to tweak.</summary>
    Private Function CurrentHost() As String
        Dim tabUrl As String = _session.ActiveTab.Url
        Dim parsedUri As Uri = Nothing
        If Not Uri.TryCreate(If(tabUrl, String.Empty), UriKind.Absolute, parsedUri) Then
            Return String.Empty
        End If
        Return parsedUri.Host
    End Function

    Private Sub UpdateSiteSection()
        Dim hostName As String = CurrentHost()
        SiteHostText.Text = hostName
        Dim prefs = _sitePrefs.GetSetting(hostName)
        TextSizeLabel.Text = Localizer.Get("TextSize") & ": " & prefs.TextSizePct & "%"
        ImagesToggle.IsChecked = Not prefs.ImagesOff
    End Sub

    Private Sub TextSmallerButton_Click(sender As Object, e As RoutedEventArgs)
        AdjustSiteTextSize(-10)
    End Sub

    Private Sub TextLargerButton_Click(sender As Object, e As RoutedEventArgs)
        AdjustSiteTextSize(10)
    End Sub

    Private Sub AdjustSiteTextSize(stepValue As Integer)
        Dim hostName As String = CurrentHost()
        If String.IsNullOrEmpty(hostName) Then
            Return
        End If
        Dim prefs = _sitePrefs.GetSetting(hostName)
        _sitePrefs.SetSetting(hostName, prefs.TextSizePct + stepValue, prefs.ImagesOff)
        SavePersistedState()
        UpdateSiteSection()
        ApplySiteTextSize(hostName)
    End Sub

    Private Async Sub ApplySiteTextSize(hostName As String)
        Try
            Dim scripted As TridentEngine = ScriptedEngine
            If scripted Is Nothing Then
                Return
            End If
            Await scripted.SetTextSizeAsync(_sitePrefs.GetSetting(hostName).TextSizePct)
        Catch ex As Exception
        End Try
    End Sub

    Private Sub ImagesToggle_Checked(sender As Object, e As RoutedEventArgs)
        SetSiteImages(True)
    End Sub

    Private Sub ImagesToggle_Unchecked(sender As Object, e As RoutedEventArgs)
        SetSiteImages(False)
    End Sub

    Private Async Sub SetSiteImages(showImages As Boolean)
        Dim hostName As String = CurrentHost()
        If String.IsNullOrEmpty(hostName) Then
            Return
        End If
        Dim prefs = _sitePrefs.GetSetting(hostName)
        _sitePrefs.SetSetting(hostName, prefs.TextSizePct, Not showImages)
        SavePersistedState()
        Try
            Dim scripted As TridentEngine = ScriptedEngine
            If scripted Is Nothing Then
                Return
            End If
            Await scripted.SetImagesEnabledAsync(showImages)
        Catch ex As Exception
        End Try
    End Sub

    ''' <summary>Night mode plus this host's tweaks. Idempotent: safe on every load.</summary>
    Private Async Function ApplyPageTweaksAsync(scripted As TridentEngine) As Task
        ' The bool goes in, not a hardcoded True: unchecking night mode must
        ' remove the stylesheet, and ApplyNightMode above exists for the toggle.
        Await scripted.SetNightModeAsync(_appSettings.NightMode)
        Dim hostName As String = CurrentHost()
        If Not String.IsNullOrEmpty(hostName) Then
            Dim prefs = _sitePrefs.GetSetting(hostName)
            Await scripted.SetTextSizeAsync(prefs.TextSizePct)
            Await scripted.SetImagesEnabledAsync(Not prefs.ImagesOff)
        End If
    End Function

    Private Sub HomepageBox_LostFocus(sender As Object, e As RoutedEventArgs)
        Dim typedHome As String = HomepageBox.Text.Trim()
        If Not String.IsNullOrEmpty(typedHome) Then
            _appSettings.Homepage = typedHome
            SavePersistedState()
        End If
    End Sub

    Private Sub DohServerBox_LostFocus(sender As Object, e As RoutedEventArgs)
        Dim typedDoh As String = DohServerBox.Text.Trim()
        If Not String.IsNullOrEmpty(typedDoh) Then
            _appSettings.DohUrl = typedDoh
            SavePersistedState()
        End If
    End Sub

    ''' <summary>
    ''' The two server addresses, normalized on the way in.
    '''
    ''' Normalize, and not the raw text: it is the function that turns "what a
    ''' person typed" into "a web address or nothing", and a value it refuses must
    ''' land in the box as the empty string. An address this build cannot honour has
    ''' to look unconfigured, because the alternative is a browser that fails later,
    ''' somewhere else, with a message about a server the person believes they
    ''' entered correctly.
    ''' </summary>
    Private Sub RemoteServerBox_LostFocus(sender As Object, e As RoutedEventArgs)
        _appSettings.RemotePrimaryUrl = RemoteServers.Normalize(RemoteServerBox.Text)
        RemoteServerBox.Text = _appSettings.RemotePrimaryUrl
        SavePersistedState()
    End Sub

    Private Sub RemoteBackupBox_LostFocus(sender As Object, e As RoutedEventArgs)
        _appSettings.RemoteSecondaryUrl = RemoteServers.Normalize(RemoteBackupBox.Text)
        RemoteBackupBox.Text = _appSettings.RemoteSecondaryUrl
        SavePersistedState()
    End Sub

    Private Sub RemoteTokenBox_LostFocus(sender As Object, e As RoutedEventArgs)
        _appSettings.RemotePrimaryToken = RemoteTokenBox.Text.Trim()
        SavePersistedState()
    End Sub

    Private Sub RemoteBackupTokenBox_LostFocus(sender As Object, e As RoutedEventArgs)
        _appSettings.RemoteSecondaryToken = RemoteBackupTokenBox.Text.Trim()
        SavePersistedState()
    End Sub

    Private Sub RemoteEnabledToggle_Checked(sender As Object, e As RoutedEventArgs)
        _appSettings.RemoteEnabled = True
        SavePersistedState()
    End Sub

    Private Sub RemoteEnabledToggle_Unchecked(sender As Object, e As RoutedEventArgs)
        _appSettings.RemoteEnabled = False
        SavePersistedState()
        ' A connection that is open stays open until the person navigates again,
        ' but a page already being drawn on somebody's server is not something this
        ' handler can take back. The button is the promise; the socket is closed by
        ' the next navigation or by Stop.
    End Sub

    Private Sub AddressBox_KeyDown(sender As Object, e As KeyRoutedEventArgs)
        If e.Key <> Windows.System.VirtualKey.Enter Then Return
        NavigateFromAddressBar()
    End Sub

    Private Sub GoButton_Click(sender As Object, e As RoutedEventArgs)
        NavigateFromAddressBar()
    End Sub

    Private Sub NavigateFromAddressBar()
        HideError()
        Dim rawText As String = AddressBox.Text.Trim()
        Dim target = AddressNormalizer.Normalize(rawText)

        If Not target.IsValid Then
            AddressBox.Text = Localizer.Get("ErrorUnknownScheme")
            Return
        End If

        Dim destUrl As String = target.Url
        If target.IsSearch Then
            destUrl = _appSettings.SearchUrlFor(rawText)
        End If

        If _appSettings.LiteRedirects Then
            Dim liteUrl As String = LiteRedirects.RedirectUrl(destUrl)
            If Not String.IsNullOrEmpty(liteUrl) Then
                destUrl = liteUrl
            End If
        End If

        If IsBlockedTrackerUrl(destUrl) Then
            ShowBlockedTracker()
            Return
        End If

        _session.ActiveTab.PushHistory(destUrl)
        RefreshTabsList()
        _engine.Navigate(destUrl)
    End Sub

    Private Sub AddressBox_GotFocus(sender As Object, e As RoutedEventArgs)
        AddressBox.SelectAll()
    End Sub

    Private Sub AddressBox_LostFocus(sender As Object, e As RoutedEventArgs)
        AddressBox.Text = _session.ActiveTab.Url
    End Sub

    Private Sub BackButton_Click(sender As Object, e As RoutedEventArgs)
        If _session.ActiveTab.CanGoBack Then
            _session.ActiveTab.Back()
            _engine.GoBack()
            RefreshTabsList()
        End If
    End Sub

    Private Sub ForwardButton_Click(sender As Object, e As RoutedEventArgs)
        If _session.ActiveTab.CanGoForward Then
            _session.ActiveTab.Forward()
            _engine.GoForward()
            RefreshTabsList()
        End If
    End Sub

    Private Sub ReloadButton_Click(sender As Object, e As RoutedEventArgs)
        HideError()
        _engine.Reload()
    End Sub

    Private Sub StopButton_Click(sender As Object, e As RoutedEventArgs)
        _navigationToken = Nothing
        LoadProgress.Value = 0
        StatusText.Text = String.Empty
        _engine.Stop()
    End Sub

    Private Sub TabsButton_Click(sender As Object, e As RoutedEventArgs)
        _session.NewTab()
        RefreshTabsList()
        _engine.Navigate(_appSettings.Homepage)
    End Sub

    Private Sub TabsList_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
        If _refreshingTabs Then
            Return
        End If
        Dim picked As Integer = TabsList.SelectedIndex
        If picked < 0 OrElse picked >= _session.Tabs.Count Then
            Return
        End If
        _session.ActivateTab(picked)
        Dim tabUrl As String = _session.ActiveTab.Url
        If String.IsNullOrEmpty(tabUrl) Then
            tabUrl = _appSettings.Homepage
        End If
        _engine.Navigate(tabUrl)
        RefreshTabsList()
    End Sub

    Private Sub CloseTabButton_Click(sender As Object, e As RoutedEventArgs)
        _session.CloseActiveTab()
        RefreshTabsList()
        Dim tabUrl As String = _session.ActiveTab.Url
        If String.IsNullOrEmpty(tabUrl) Then
            tabUrl = _appSettings.Homepage
        End If
        _engine.Navigate(tabUrl)
    End Sub

    Private Sub HistoryList_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
        Dim picked As Integer = HistoryList.SelectedIndex
        If picked < 0 Then
            Return
        End If
        Dim pickedUrl As String = TryCast(HistoryList.SelectedItem, String)
        If String.IsNullOrEmpty(pickedUrl) Then
            Return
        End If
        SettingsOverlay.Visibility = Visibility.Collapsed
        _session.ActiveTab.PushHistory(pickedUrl)
        _engine.Navigate(pickedUrl)
        HistoryList.SelectedIndex = -1
    End Sub

    Private Sub ClearHistoryButton_Click(sender As Object, e As RoutedEventArgs)
        _historyStore.Clear()
        SavePersistedState()
        RefreshHistoryList()
    End Sub

    Private Sub FavoritesList_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
        Dim pickedUrl As String = TryCast(FavoritesList.SelectedItem, String)
        If String.IsNullOrEmpty(pickedUrl) Then
            Return
        End If
        SettingsOverlay.Visibility = Visibility.Collapsed
        _session.ActiveTab.PushHistory(pickedUrl)
        _engine.Navigate(pickedUrl)
        FavoritesList.SelectedIndex = -1
    End Sub

    Private Sub AddFavoriteButton_Click(sender As Object, e As RoutedEventArgs)
        Dim tabUrl As String = _session.ActiveTab.Url
        If String.IsNullOrEmpty(tabUrl) Then
            Return
        End If
        _favoritesStore.Add(tabUrl, tabUrl)
        SavePersistedState()
        RefreshFavoritesList()
    End Sub

    Private Sub RemoveFavoriteButton_Click(sender As Object, e As RoutedEventArgs)
        Dim pickedUrl As String = TryCast(FavoritesList.SelectedItem, String)
        If String.IsNullOrEmpty(pickedUrl) Then
            pickedUrl = _session.ActiveTab.Url
        End If
        _favoritesStore.Remove(pickedUrl)
        SavePersistedState()
        RefreshFavoritesList()
    End Sub

    Private Sub SecurityDetailsButton_Click(sender As Object, e As RoutedEventArgs)
        Dim tabUrl As String = _session.ActiveTab.Url
        Dim isHttps As Boolean = tabUrl IsNot Nothing AndAlso tabUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        Dim caps = _engine.Capabilities
        Dim detailText As String
        If isHttps Then
            detailText = Localizer.Get("SecuritySecure") & " " & Localizer.Get("SecurityWebViewCeiling") & " UA=" & _session.EffectiveUserAgent
        Else
            detailText = Localizer.Get("SecurityInsecure")
        End If
        ErrorText.Text = detailText & vbCrLf & caps.ToString()
        ErrorText.Visibility = Visibility.Visible
    End Sub

    ''' <summary>Tracker check for top-level navigations (subresources excluded by the OS).</summary>
    Private Function IsBlockedTrackerUrl(pageUrl As String) As Boolean
        If Not _appSettings.BlockTrackers Then
            Return False
        End If
        If String.IsNullOrEmpty(pageUrl) Then
            Return False
        End If
        Dim parsedUri As Uri = Nothing
        If Not Uri.TryCreate(pageUrl, UriKind.Absolute, parsedUri) Then
            Return False
        End If
        Return TrackerBlocklist.ShouldBlock(parsedUri.Host)
    End Function

    Private Sub ShowBlockedTracker()
        ErrorText.Text = Localizer.Get("BlockedTracker")
        ErrorText.Visibility = Visibility.Visible
    End Sub

    ''' <summary>Snapshot open tabs for restore; skipped entirely in private mode.</summary>
    Private Sub SaveSessionTabs()
        Try
            Dim openUrls As New List(Of String)()
            For Each openTab In _session.Tabs
                If Not String.IsNullOrEmpty(openTab.Url) Then
                    openUrls.Add(openTab.Url)
                End If
            Next
            _appSettings.SetSessionTabs(openUrls)
        Catch ex As Exception
        End Try
    End Sub

    Private Sub FindButton_Click(sender As Object, e As RoutedEventArgs)
        If FindBar.Visibility = Visibility.Visible Then
            FindBar.Visibility = Visibility.Collapsed
        Else
            FindBar.Visibility = Visibility.Visible
            FindBox.Focus(FocusState.Programmatic)
        End If
    End Sub

    Private Sub FindBox_KeyDown(sender As Object, e As KeyRoutedEventArgs)
        If e.Key <> Windows.System.VirtualKey.Enter Then Return
        DoFindNext()
    End Sub

    Private Sub FindNextButton_Click(sender As Object, e As RoutedEventArgs)
        DoFindNext()
    End Sub

    Private Sub FindCloseButton_Click(sender As Object, e As RoutedEventArgs)
        FindBar.Visibility = Visibility.Collapsed
        FindResult.Text = String.Empty
    End Sub

    Private Async Sub DoFindNext()
        Try
            Dim searchTerm As String = FindBox.Text
            If String.IsNullOrEmpty(searchTerm) Then
                Return
            End If
            ' TryCast: Find is a Trident feature. The remote engine has its own
            ' FIND message and a FindResult to answer with, which the remote screen
            ' wires up; until then this does nothing rather than throwing into the
            ' empty Catch below.
            Dim scripted As TridentEngine = TryCast(_engine, TridentEngine)
            If scripted Is Nothing Then Return
            Dim foundIt As Boolean = Await scripted.FindInPageAsync(searchTerm)
            If foundIt Then
                FindResult.Text = String.Empty
            Else
                FindResult.Text = Localizer.Get("FindNoMatch")
            End If
        Catch ex As Exception
        End Try
    End Sub

    Private Async Sub ReadingButton_Click(sender As Object, e As RoutedEventArgs)
        Try
            ' TryCast: reading mode rewrites the document, which is a Trident
            ' feature. For the remote engine the reader fallback is the server's
            ' business -- and it is an ALTERNATIVE to the engine fallback, not a
            ' step after it (see ApplyAutoReader).
            Dim scripted As TridentEngine = TryCast(_engine, TridentEngine)
            If scripted Is Nothing Then Return
            Dim entered As Boolean = Await scripted.EnterReadingModeAsync()
            If Not entered Then
                ErrorText.Text = Localizer.Get("ErrorPageFailed")
                ErrorText.Visibility = Visibility.Visible
            End If
        Catch ex As Exception
        End Try
    End Sub

    Private Sub ShareButton_Click(sender As Object, e As RoutedEventArgs)
        Try
            DataTransferManager.ShowShareUI()
        Catch ex As Exception
        End Try
    End Sub

    Private Async Sub SavePageButton_Click(sender As Object, e As RoutedEventArgs)
        Try
            Dim scripted As TridentEngine = ScriptedEngine
            If scripted Is Nothing Then
                Return
            End If
            Dim pageUrl As String = _session.ActiveTab.Url
            If String.IsNullOrEmpty(pageUrl) Then
                Return
            End If
            Dim rawText As String = Await scripted.ExtractArticleTextAsync()
            If String.IsNullOrEmpty(rawText) Then
                StatusText.Text = Localizer.Get("ErrorPageFailed")
                Return
            End If
            Dim breakPos As Integer = rawText.IndexOf(vbLf)
            Dim pageTitle As String
            Dim pageBody As String
            If breakPos < 0 Then
                pageTitle = pageUrl
                pageBody = rawText
            Else
                pageTitle = rawText.Substring(0, breakPos).Trim()
                pageBody = rawText.Substring(breakPos + 1)
                If String.IsNullOrEmpty(pageTitle) Then
                    pageTitle = pageUrl
                End If
            End If
            _savedPages.Add(pageUrl, pageTitle, pageBody)
            SavePersistedState()
            RefreshSavedList()
            StatusText.Text = Localizer.Get("SavedPages")
        Catch ex As Exception
        End Try
    End Sub

    Private Sub SavedPagesList_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
        Dim picked As Integer = SavedPagesList.SelectedIndex
        If picked < 0 Then
            Return
        End If
        Dim entries As IList(Of SavedPageEntry) = _savedPages.List()
        If picked >= entries.Count Then
            Return
        End If
        OfflineTitle.Text = entries(picked).Title
        OfflineBody.Text = entries(picked).Text
        SettingsOverlay.Visibility = Visibility.Collapsed
        OfflineOverlay.Visibility = Visibility.Visible
        SavedPagesList.SelectedIndex = -1
    End Sub

    Private Sub DeleteSavedButton_Click(sender As Object, e As RoutedEventArgs)
        Dim picked As Integer = SavedPagesList.SelectedIndex
        If picked < 0 Then
            Return
        End If
        _savedPages.RemoveAt(picked)
        SavePersistedState()
        RefreshSavedList()
    End Sub

    Private Sub OfflineBackButton_Click(sender As Object, e As RoutedEventArgs)
        OfflineOverlay.Visibility = Visibility.Collapsed
        SettingsOverlay.Visibility = Visibility.Visible
    End Sub

    Private Sub SpeedDialList_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
        Dim picked As Integer = SpeedDialList.SelectedIndex
        If picked < 0 Then
            Return
        End If
        Dim entries As IList(Of SpeedDialEntry) = _speedDial.List()
        If picked >= entries.Count Then
            Return
        End If
        Dim pickedUrl As String = entries(picked).Url
        SettingsOverlay.Visibility = Visibility.Collapsed
        _session.ActiveTab.PushHistory(pickedUrl)
        _engine.Navigate(pickedUrl)
        SpeedDialList.SelectedIndex = -1
    End Sub

    Private Sub SpeedDialAddButton_Click(sender As Object, e As RoutedEventArgs)
        Dim tabUrl As String = _session.ActiveTab.Url
        If String.IsNullOrEmpty(tabUrl) Then
            Return
        End If
        _speedDial.Add(tabUrl, tabUrl)
        SavePersistedState()
        RefreshSpeedDialList()
    End Sub

    Private Sub SpeedDialRemoveButton_Click(sender As Object, e As RoutedEventArgs)
        Dim picked As Integer = SpeedDialList.SelectedIndex
        If picked < 0 Then
            Return
        End If
        Dim entries As IList(Of SpeedDialEntry) = _speedDial.List()
        If picked >= entries.Count Then
            Return
        End If
        _speedDial.Remove(entries(picked).Url)
        SavePersistedState()
        RefreshSpeedDialList()
    End Sub

    Private Async Sub BackupButton_Click(sender As Object, e As RoutedEventArgs)
        Try
            Dim saver As New FileSavePicker()
            saver.SuggestedStartLocation = PickerLocationId.ComputerFolder
            saver.SuggestedFileName = "browserforwp-backup"
            Dim exts As New List(Of String)()
            exts.Add(".txt")
            saver.FileTypeChoices.Add("Text", exts)
            Dim pickedFile As StorageFile = Await saver.PickSaveFileAsync()
            If pickedFile Is Nothing Then
                Return
            End If
            Dim sections As New Dictionary(Of String, String)()
            sections("settings") = MapToLines(_appSettings.SaveToMap())
            sections("history") = _historyStore.Serialize()
            sections("favorites") = _favoritesStore.Serialize()
            sections("pins") = _pinTable.Serialize()
            sections("saved") = _savedPages.Serialize()
            sections("sites") = _sitePrefs.Serialize()
            sections("speeddial") = _speedDial.Serialize()
            Await FileIO.WriteTextAsync(pickedFile, BackupManager.BuildBackup(sections))
            BackupStatus.Text = Localizer.Get("BackupDone")
        Catch ex As Exception
            BackupStatus.Text = Localizer.Get("ErrorPageFailed")
        End Try
    End Sub

    Private Async Sub RestoreButton_Click(sender As Object, e As RoutedEventArgs)
        Try
            Dim opener As New FileOpenPicker()
            opener.SuggestedStartLocation = PickerLocationId.ComputerFolder
            opener.FileTypeFilter.Add(".txt")
            Dim pickedFile As StorageFile = Await opener.PickSingleFileAsync()
            If pickedFile Is Nothing Then
                Return
            End If
            Dim backupText As String = Await FileIO.ReadTextAsync(pickedFile)
            Dim sections As Dictionary(Of String, String) = Nothing
            If Not BackupManager.TryParseBackup(backupText, sections) Then
                BackupStatus.Text = Localizer.Get("ErrorPageFailed")
                Return
            End If
            If sections.ContainsKey("settings") Then
                _appSettings.LoadFromMap(ParseMapLines(sections("settings")))
            End If
            If sections.ContainsKey("history") Then
                _historyStore.Parse(sections("history"))
            End If
            If sections.ContainsKey("favorites") Then
                _favoritesStore.Parse(sections("favorites"))
            End If
            If sections.ContainsKey("pins") Then
                _pinTable.Parse(sections("pins"))
            End If
            If sections.ContainsKey("saved") Then
                _savedPages.Parse(sections("saved"))
            End If
            If sections.ContainsKey("sites") Then
                _sitePrefs.Parse(sections("sites"))
            End If
            If sections.ContainsKey("speeddial") Then
                _speedDial.Parse(sections("speeddial"))
            End If
            SavePersistedState()
            ApplyLocalizedStrings()
            PopulateLanguagePicker()
            PopulateEnginePicker()
            RefreshTabsList()
            RefreshHistoryList()
            RefreshFavoritesList()
            RefreshSavedList()
            RefreshSpeedDialList()
            BackupStatus.Text = Localizer.Get("BackupDone")
        Catch ex As Exception
            BackupStatus.Text = Localizer.Get("ErrorPageFailed")
        End Try
    End Sub

    ''' <summary>Settings map to lines. Newlines in values ride as "\n": the
    ''' section encoding is already base64, so this only has to survive one
    ''' split on vbLf, which a literal backslash-n never contains.</summary>
    Private Shared Function MapToLines(hostMap As Dictionary(Of String, String)) As String
        Dim lines As New List(Of String)()
        For Each pairItem In hostMap
            lines.Add(pairItem.Key & "=" & If(pairItem.Value, String.Empty).Replace(vbLf, "\n"))
        Next
        Return String.Join(vbLf, lines.ToArray())
    End Function

    Private Shared Function ParseMapLines(mapText As String) As Dictionary(Of String, String)
        Dim hostMap As New Dictionary(Of String, String)()
        If String.IsNullOrEmpty(mapText) Then
            Return hostMap
        End If
        Dim rawLines As String() = mapText.Split(New String() {vbLf}, StringSplitOptions.None)
        For Each rawLine In rawLines
            rawLine = If(rawLine, String.Empty).TrimEnd()
            If String.IsNullOrEmpty(rawLine) Then
                Continue For
            End If
            Dim eqPos As Integer = rawLine.IndexOf("="c)
            If eqPos <= 0 Then
                Continue For
            End If
            hostMap(rawLine.Substring(0, eqPos)) = rawLine.Substring(eqPos + 1).Replace("\n", vbLf)
        Next
        Return hostMap
    End Function

    Private Sub RetryButton_Click(sender As Object, e As RoutedEventArgs)
        HideError()
        Dim tabUrl As String = _session.ActiveTab.Url
        If String.IsNullOrEmpty(tabUrl) Then
            tabUrl = _appSettings.Homepage
        End If
        _engine.Navigate(tabUrl)
    End Sub

    Private Sub OnShareRequested(sender As DataTransferManager, e As DataRequestedEventArgs)
        Try
            Dim pageUrl As String = _session.ActiveTab.Url
            Dim sharedUri As Uri = Nothing
            If Not Uri.TryCreate(If(pageUrl, String.Empty), UriKind.Absolute, sharedUri) Then
                e.Request.FailWithDisplayText(Localizer.Get("ErrorPageFailed"))
                Return
            End If
            e.Request.Data.Properties.Title = If(String.IsNullOrEmpty(pageUrl), Localizer.Get("AppName"), pageUrl)
            ' SetWebLink, not SetUri: the OS deprecates DataPackage.SetUri with this
            ' exact advice, and a shared page URL is a web link by definition.
            e.Request.Data.SetWebLink(sharedUri)
        Catch ex As Exception
            e.Request.FailWithDisplayText(Localizer.Get("ErrorPageFailed"))
        End Try
    End Sub

    Private Sub OnHardwareBackPressed(sender As Object, e As BackPressedEventArgs)
        If _session.ActiveTab.CanGoBack Then
            _session.ActiveTab.Back()
            _engine.GoBack()
            RefreshTabsList()
            e.Handled = True
        End If
    End Sub

    Private Sub OnNavigationStarting(sender As WebView, e As WebViewNavigationStartingEventArgs)
        If e.Uri IsNot Nothing AndAlso _appSettings.LiteRedirects Then
            Dim liteUrl As String = LiteRedirects.RedirectUrl(e.Uri.ToString())
            If Not String.IsNullOrEmpty(liteUrl) AndAlso liteUrl <> e.Uri.ToString() Then
                e.Cancel = True
                _session.ActiveTab.ReplaceCurrent(liteUrl)
                AddressBox.Text = liteUrl
                _engine.Navigate(liteUrl)
                Return
            End If
        End If
        If e.Uri IsNot Nothing AndAlso IsBlockedTrackerUrl(e.Uri.ToString()) Then
            e.Cancel = True
            ShowBlockedTracker()
            Return
        End If
        _navigationToken = e.Uri
        LoadProgress.Value = 10
        StatusText.Text = Localizer.Get("Loading")
        HideError()
    End Sub

    Private Async Sub OnDOMContentLoaded(sender As WebView, e As WebViewDOMContentLoadedEventArgs)
        Dim scripted As TridentEngine = ScriptedEngine
        If scripted Is Nothing Then Return
        Try
            Await scripted.InjectPolyfillAsync()
            _injectedToken = _navigationToken
            Await ApplyPageTweaksAsync(scripted)
        Catch ex As Exception
        End Try
    End Sub

    Private Async Sub OnNavigationCompleted(sender As WebView, e As WebViewNavigationCompletedEventArgs)
        If _navigationToken Is Nothing Then Return
        _navigationToken = Nothing

        If Not e.IsSuccess Then
            ' A failed navigation must not be recorded as a visit or reported as
            ' 100% loaded. The OS status name is shown verbatim (it is a stable
            ' enum name, not a translated sentence) next to a localized reason.
            LoadProgress.Value = 0
            Dim failureReason As String
            Select Case e.WebErrorStatus
                Case WebErrorStatus.HostNameNotResolved, WebErrorStatus.CannotConnect, WebErrorStatus.ServerUnreachable, WebErrorStatus.Timeout, WebErrorStatus.ConnectionAborted
                    failureReason = Localizer.Get("ErrorNoConnection")
                Case Else
                    failureReason = Localizer.Get("ErrorNavigationFailed")
            End Select
            ErrorText.Text = failureReason & " (" & e.WebErrorStatus.ToString() & ")"
            ErrorText.Visibility = Visibility.Visible
            RetryButton.Visibility = Visibility.Visible
            StatusText.Text = String.Empty
            Return
        End If

        LoadProgress.Value = 100
        StatusText.Text = Localizer.Get("LoadComplete")
        Dim pageUrl As String = e.Uri.ToString()
        _session.ActiveTab.ReplaceCurrent(pageUrl)
        AddressBox.Text = _session.ActiveTab.Url
        If Not _session.PrivateMode Then
            _historyStore.Add(_session.ActiveTab.Url, String.Empty)
            SaveSessionTabs()
        End If
        SavePersistedState()
        RefreshTabsList()
        RefreshHistoryList()
        UpdateSecurityGlyph()
        Dim scripted As TridentEngine = ScriptedEngine
        If scripted Is Nothing Then Return

        Try
            ' One injection per navigation: DOMContentLoaded already ran it for
            ' this token on most pages, so running it again here doubles a ~27KB
            ' eval on every load for nothing. Late-loading documents miss that
            ' event, which is why the call stays as a fallback.
            If _injectedToken IsNot _navigationToken Then
                Await scripted.InjectPolyfillAsync()
                _injectedToken = _navigationToken
            End If
            Await ApplyPageTweaksAsync(scripted)
        Catch ex As Exception
        End Try

        Try
            Dim compatProbe As New CompatibilityProbe()
            Dim compatReport = Await compatProbe.RunAsync(_engine)

            ' A measurement that never ran must not move anything, which is the rule
            ' EngineChoice encodes and tools/proto/engine-choice.mjs refuses to let
            ' anyone forget. It also means this block does nothing at all when the
            ' remote engine is already the engine: ScriptedEngine returns Nothing for
            ' it -- the page's scripts run inside Chromium on the server, not here --
            ' so the caller returned above.
            If Not compatReport.CouldRun Then Return

            ' The engine fallback and the reader fallback are alternatives, not a
            ' sequence: when a measurement says Trident cannot cope, having a server
            ' draw the page is a better answer than injecting a reader, and doing both
            ' would fight over the same document. The reader stays reachable from the
            ' Reading button.
            If _appSettings.EngineSetting = EngineChoice.Auto AndAlso
               EngineChoice.Decide(EngineChoice.Auto, True, compatReport.MissingFeatures.Count) = EngineChoice.Remote Then
                UseEngine(EngineChoice.Remote)
                _engine.Navigate(_session.ActiveTab.Url)
                Return
            End If

            If compatReport.MissingFeatures.Count >= EngineChoice.AutomaticFallbackThreshold Then
                Dim entered As Boolean = Await scripted.EnterReadingModeAsync()
                If entered Then
                    ErrorText.Text = Localizer.Get("ReaderFallback")
                    ErrorText.Visibility = Visibility.Visible
                End If
            End If
        Catch ex As Exception
        End Try
    End Sub

    Private Sub HideError()
        ErrorText.Visibility = Visibility.Collapsed
        ErrorText.Text = String.Empty
        RetryButton.Visibility = Visibility.Collapsed
    End Sub

    Private Sub UpdateSecurityGlyph()
        Dim tabUrl As String = _session.ActiveTab.Url
        Dim isHttps As Boolean = tabUrl IsNot Nothing AndAlso tabUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)

        If isHttps Then
            SecurityGlyph.Text = If(_engine.Capabilities.SupportsTls13,
                                    Char.ConvertFromUtf32(&H1F512),
                                    Char.ConvertFromUtf32(&H1F513))
        Else
            SecurityGlyph.Text = ChrW(&H26A0)
        End If
    End Sub

    Private Sub SettingsButton_Click(sender As Object, e As RoutedEventArgs)
        ' Cold-start cost: the pickers are built on first open, not on launch.
        If Not _settingsPopulated Then
            PopulateLanguagePicker()
            PopulateEnginePicker()
            _settingsPopulated = True
        End If
        RefreshTabsList()
        RefreshHistoryList()
        RefreshFavoritesList()
        RefreshSavedList()
        RefreshSpeedDialList()
        UpdateSiteSection()
        SettingsOverlay.Visibility = Visibility.Visible
    End Sub

    Private Sub DiagnosticsButton_Click(sender As Object, e As RoutedEventArgs)
        SettingsOverlay.Visibility = Visibility.Collapsed
        DiagnosticsOverlay.Visibility = Visibility.Visible
    End Sub

    Private Sub DiagnosticsBackButton_Click(sender As Object, e As RoutedEventArgs)
        DiagnosticsOverlay.Visibility = Visibility.Collapsed
        SettingsOverlay.Visibility = Visibility.Visible
    End Sub

    Private Sub CloseSettingsButton_Click(sender As Object, e As RoutedEventArgs)
        SavePersistedState()
        ApplyLocalizedStrings()
        SettingsOverlay.Visibility = Visibility.Collapsed
    End Sub

    Private Async Sub CompatProbeButton_Click(sender As Object, e As RoutedEventArgs)
        CompatProbeButton.IsEnabled = False
        Try
            Dim probe As New CompatibilityProbe()
            Dim report = Await probe.RunAsync(_engine)

            If Not report.CouldRun Then
                ' Nothing was measured, so say that. Reporting "no missing web
                ' features" here was a claim the probe had not earned, and it is
                ' the shape of lie this project exists to avoid.
                CompatProbeResult.Text = Localizer.Get("ProbeNotRun")
            ElseIf report.IsFullyCompatible Then
                CompatProbeResult.Text = Localizer.Get("DiagnosticsNoMissingFeatures")
            Else
                CompatProbeResult.Text = String.Join(", ", report.MissingFeatures)
            End If
        Catch ex As Exception
            CompatProbeResult.Text = Localizer.Get("ErrorPageFailed")
        Finally
            CompatProbeButton.IsEnabled = True
        End Try
    End Sub

    Private Async Sub TlsProbeButton_Click(sender As Object, e As RoutedEventArgs)
        TlsProbeButton.IsEnabled = False
        Try
            Dim probeHost As String = TlsHostBox.Text.Trim()
            If String.IsNullOrEmpty(probeHost) Then
                Dim tabUrl As String = _session.ActiveTab.Url
                If Not String.IsNullOrEmpty(tabUrl) Then
                    Dim parsedUri As Uri = Nothing
                    If Uri.TryCreate(tabUrl, UriKind.Absolute, parsedUri) Then
                        probeHost = parsedUri.Host
                    End If
                End If
            End If
            If String.IsNullOrEmpty(probeHost) Then
                probeHost = "example.com"
            End If
            Dim runnerResult = Await BrowserForWP.Diagnostics.TlsProbeRunner.RunAsync(probeHost, _appSettings.DohUrl, _pinTable)
            ' SecurityTls13 exists for exactly this state, and only for this state:
            ' the app's own transport negotiated TLS 1.3. It must never be shown
            ' for WebView traffic, which rides Schannel and tops out at TLS 1.2.
            If runnerResult.IsTls13 Then
                TlsProbeResult.Text = Localizer.Get("SecurityTls13") & vbCrLf & runnerResult.ToString()
            Else
                TlsProbeResult.Text = runnerResult.ToString()
            End If
            ' Surface the localized mismatch sentence instead of leaving the user
            ' to spot the English "pin-MISMATCH" token inside the detail line.
            If runnerResult.PinMismatch Then
                PinStatus.Text = Localizer.Get("PinMismatch")
            End If
        Catch ex As Exception
            TlsProbeResult.Text = Localizer.Get("ErrorTlsHandshake") & " " & ex.Message
        Finally
            TlsProbeButton.IsEnabled = True
        End Try
    End Sub

    Private Sub PinAddButton_Click(sender As Object, e As RoutedEventArgs)
        Try
            Dim hostKey As String = PinHostBox.Text.Trim()
            Dim pinText As String = PinValueBox.Text.Trim()
            If String.IsNullOrEmpty(hostKey) OrElse String.IsNullOrEmpty(pinText) Then
                Return
            End If
            _pinTable.Add(hostKey, pinText)
            SavePersistedState()
            PinStatus.Text = Localizer.Get("PinStored")
        Catch ex As Exception
            PinStatus.Text = Localizer.Get("ErrorPageFailed")
        End Try
    End Sub

    Private Sub PinRemoveButton_Click(sender As Object, e As RoutedEventArgs)
        Dim hostKey As String = PinHostBox.Text.Trim()
        If String.IsNullOrEmpty(hostKey) Then
            Return
        End If
        _pinTable.Remove(hostKey)
        SavePersistedState()
        PinStatus.Text = Localizer.Get("PinStored")
    End Sub

    ''' <summary>
    ''' The engine's scripting-only features, or Nothing when it has no script host.
    '''
    ''' This is the one place in the shell that asks WHICH engine it has. Those
    ''' features live on TridentEngine rather than on IBrowserEngine, and the
    ''' capability flag is what keeps the question honest: everywhere else this page
    ''' branches on EngineCapabilities, which is the point of having a seam at all.
    ''' </summary>
    Private ReadOnly Property ScriptedEngine As TridentEngine
        Get
            ' TryCast, and the flag alone was never enough. `SupportsScripting` says
            ' the engine runs scripts; this property needs the stronger, narrower
            ' thing -- an engine whose script host is ON THIS DEVICE, so that we can
            ' inject into it.
            '
            ' RemoteEngine reports SupportsScripting = True and is right to: the
            ' page's scripts really do run, inside Chromium on the server, where
            ' this device can neither see nor invoke them. So the guard passed, the
            ' DirectCast threw InvalidCastException, and every caller had already
            ' been written for the OTHER contract -- two of them open with
            ' `If scripted Is Nothing Then Return`, and the comment further down
            ' states that this property returns Nothing for the remote engine.
            ' The documentation, the callers and the implementation disagreed, and
            ' the implementation was the one that was wrong.
            If _engine Is Nothing Then Return Nothing
            If Not _engine.Capabilities.SupportsScripting Then Return Nothing
            Return TryCast(_engine, TridentEngine)
        End Get
    End Property

    ''' <summary>
    ''' Resolve the setting to an engine and host it. Called with no measurement in
    ''' hand, which is exactly why an automatic choice lands on the system engine here:
    ''' the measurement, if there is one, arrives later, and that is what may move it.
    ''' </summary>
    Private Sub ApplyEngineChoice()
        UseEngine(EngineChoice.Decide(_appSettings.EngineSetting, False, 0))
    End Sub

    ''' <summary>
    ''' Build the chosen engine if it does not exist, attach its events once, put it in
    ''' ContentHost and say why it was chosen. This is the only assignment to _engine,
    ''' so no other code path can leave the shell pointed at a stale engine.
    ''' </summary>
    Private Sub UseEngine(chosen As String)
        If chosen = EngineChoice.Remote Then
            If _remoteEngine Is Nothing Then
                _remoteEngine = New BrowserForWP.Engine.RemoteEngine(_appSettings, _pinTable)
                AddHandler _remoteEngine.Navigated, AddressOf OnRemoteNavigated
                AddHandler _remoteEngine.Audio, AddressOf OnAudioMessage
            End If
            _engine = _remoteEngine
            HostAudioElement()
        Else
            If _tridentEngine Is Nothing Then
                _tridentEngine = New TridentEngine()
                AddHandler _tridentEngine.View.NavigationStarting, AddressOf OnNavigationStarting
                AddHandler _tridentEngine.View.NavigationCompleted, AddressOf OnNavigationCompleted
                AddHandler _tridentEngine.View.DOMContentLoaded, AddressOf OnDOMContentLoaded
                ' No NavigationFailed handler: that event is deprecated on Windows Phone
                ' 8.1 and carries no URI. Completion reports the same failure through
                ' IsSuccess / WebErrorStatus, so failures are handled there instead.
            End If
            _engine = _tridentEngine
        End If

        ContentHost.Child = DirectCast(_engine.Source, UIElement)

        ' Find, reading mode and night mode rewrite or inspect the DOCUMENT, and on
        ' this engine there is no document here to touch: the page is a picture of
        ' Chromium's own. The controls are disabled rather than left to do nothing,
        ' because a button that silently does nothing is the shape of lie this
        ' repository keeps finding. The capability flag alone cannot decide this --
        ' the remote engine reports SupportsScripting truthfully, and it is telling
        ' the truth about a machine on the other side of the network.
        Dim localScripting As Boolean = _engine.Capabilities.SupportsScripting AndAlso
                                        TypeOf _engine Is TridentEngine
        FindButton.IsEnabled = localScripting
        ReadingButton.IsEnabled = localScripting
        NightModeToggle.IsEnabled = localScripting

        ' And the keys bar belongs to the OTHER engine: it exists because the server
        ' is the only thing that can press Tab, Escape or an arrow on this page.
        Dim remoteEngine As Boolean = TypeOf _engine Is BrowserForWP.Engine.RemoteEngine
        RemoteKeysButton.IsEnabled = remoteEngine
        If Not remoteEngine Then RemoteKeysBar.Visibility = Visibility.Collapsed

        EngineStatusText.Text = Localizer.Get(EngineChoice.Explain(_appSettings.EngineSetting, False, 0))
    End Sub

    ''' <summary>
    ''' The keys bar: the keys a phone's soft keyboard has no way to send. Every
    ''' button goes through one helper, so the eight key names live in one list that
    ''' tools/proto/remote-input.mjs can read and check against what the server can
    ''' press.
    ''' </summary>
    Private Sub SendRemoteKey(keyName As String)
        Dim remote As BrowserForWP.Engine.RemoteEngine = TryCast(_engine, BrowserForWP.Engine.RemoteEngine)
        If remote Is Nothing Then Return
        remote.TypeKey(keyName)
    End Sub

    Private Sub RemoteKeysButton_Click(sender As Object, e As RoutedEventArgs)
        If RemoteKeysBar.Visibility = Visibility.Visible Then
            RemoteKeysBar.Visibility = Visibility.Collapsed
        Else
            RemoteKeysBar.Visibility = Visibility.Visible
        End If
    End Sub

    Private Sub KeyBarCloseButton_Click(sender As Object, e As RoutedEventArgs)
        RemoteKeysBar.Visibility = Visibility.Collapsed
    End Sub

    Private Sub KeyTabButton_Click(sender As Object, e As RoutedEventArgs)
        SendRemoteKey("Tab")
    End Sub

    Private Sub KeyEnterButton_Click(sender As Object, e As RoutedEventArgs)
        SendRemoteKey("Enter")
    End Sub

    Private Sub KeyEscapeButton_Click(sender As Object, e As RoutedEventArgs)
        SendRemoteKey("Escape")
    End Sub

    Private Sub KeyBackspaceButton_Click(sender As Object, e As RoutedEventArgs)
        SendRemoteKey("Backspace")
    End Sub

    Private Sub KeyLeftButton_Click(sender As Object, e As RoutedEventArgs)
        SendRemoteKey("ArrowLeft")
    End Sub

    Private Sub KeyUpButton_Click(sender As Object, e As RoutedEventArgs)
        SendRemoteKey("ArrowUp")
    End Sub

    Private Sub KeyDownButton_Click(sender As Object, e As RoutedEventArgs)
        SendRemoteKey("ArrowDown")
    End Sub

    Private Sub KeyRightButton_Click(sender As Object, e As RoutedEventArgs)
        SendRemoteKey("ArrowRight")
    End Sub

    ''' <summary>
    ''' Puts the MediaElement in the tree, once. Its parent is ContentHost's, which
    ''' is the Grid the page lives in: a Border holds exactly one child, and that
    ''' child is the engine.
    ''' </summary>
    Private Sub HostAudioElement()
        If _audioHosted Then Return
        Try
            Dim hostGrid As Panel = TryCast(ContentHost.Parent, Panel)
            If hostGrid Is Nothing Then Return
            ' A MediaElement with nothing to show must not take a tap meant for the
            ' page underneath it.
            _audio.IsHitTestVisible = False
            _audio.AutoPlay = True
            hostGrid.Children.Add(_audio)
            _audioHosted = True
        Catch
            ' A tree that will not take it means silent sound, not a broken page.
        End Try
    End Sub

    ''' <summary>
    ''' The server has a sound to play, and the shell plays it. The url is the
    ''' server's own, reached over Schannel: MediaElement cannot be fed by this
    ''' project's TLS stack, and it does not need to be.
    ''' </summary>
    Private Sub OnAudioMessage(playing As Boolean, url As String)
        Try
            If playing AndAlso Not String.IsNullOrEmpty(url) Then
                _audio.Source = New Uri(url)
                _audio.Play()
            Else
                _audio.Stop()
                _audio.Source = Nothing
            End If
        Catch
            ' A url the media pipeline will not take is a page without sound.
        End Try
    End Sub

    ''' <summary>
    ''' A render by the remote engine ended. It reports the same states the WebView's
    ''' completion handler reports, through the same helpers, so a page drawn by a
    ''' server leaves the shell exactly where the rest of the app expects it to be.
    ''' </summary>
    Private Sub OnRemoteNavigated(sender As Object, e As BrowserForWP.Engine.RemoteNavigationResult)
        If e Is Nothing Then Return

        LoadProgress.Value = If(e.IsSuccess, 100, 0)
        HideError()

        If Not e.IsSuccess Then
            ' The token is engine-level detail shown beside localized copy, the same
            ' shape the WebView path already uses for WebErrorStatus. It is a wart
            ' this repository records rather than one introduced here.
            StatusText.Text = String.Empty
            ErrorText.Text = Localizer.Get(e.StatusKey)
            If Not String.IsNullOrEmpty(e.Detail) Then
                ErrorText.Text = ErrorText.Text & " (" & e.Detail & ")"
            End If
            ErrorText.Visibility = Visibility.Visible
            RetryButton.Visibility = Visibility.Visible
            RefreshTabsList()
            Return
        End If

        StatusText.Text = Localizer.Get("LoadComplete") & "  " & Localizer.Get(e.StatusKey)
        _session.ActiveTab.ReplaceCurrent(e.Url)
        AddressBox.Text = _session.ActiveTab.Url
        If Not _session.PrivateMode Then
            _historyStore.Add(_session.ActiveTab.Url, String.Empty)
            SaveSessionTabs()
        End If
        SavePersistedState()
        RefreshTabsList()
        RefreshHistoryList()
        UpdateSecurityGlyph()
    End Sub

    ''' <summary>
    ''' Ask the hosted engine how it is configured. This is the instrument behind
    ''' docs/MAINTAINING.md's "IE-adaptation is closed": the four levers a
    ''' "re-configure Trident" plan needs are absent from the API surface, and this
    ''' is how a handset confirms that instead of taking our word for it. Record the
    ''' mode it reports in that section when someone runs it on a device.
    ''' </summary>
    Private Async Sub IeModeButton_Click(sender As Object, e As RoutedEventArgs)
        IeModeButton.IsEnabled = False
        Try
            Dim modeReport As IeModeReport = Await IeModeProbe.RunAsync(_engine)
            If modeReport Is Nothing Then
                IeModeResult.Text = Localizer.Get("ProbeNotRun")
                Return
            End If
            IeModeResult.Text = Localizer.Get("IeModeReport") & modeReport.DocumentMode.ToString() & vbCrLf &
                                modeReport.RawJson
        Catch ex As Exception
            IeModeResult.Text = Localizer.Get("ErrorPageFailed")
        Finally
            IeModeButton.IsEnabled = True
        End Try
    End Sub

End Class
