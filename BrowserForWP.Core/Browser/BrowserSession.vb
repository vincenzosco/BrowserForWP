' BrowserForWP — tab and session state.
'
' Deliberately free of any transport or engine dependency: this is the part of
' the browser that can be unit-tested with no device, no network and no XAML.

Imports System.Collections.Generic

Namespace Browser

    ''' <summary>
    ''' One tab's history. The index is a cursor into a list, which is what makes
    ''' back/forward cheap — pushing a new entry truncates the forward tail, exactly
    ''' as a browser must.
    ''' </summary>
    Public NotInheritable Class TabModel

        Private ReadOnly _history As New List(Of String)()
        Private _index As Integer = -1

        Public Property Title As String = String.Empty

        ''' <summary>The current entry, or an empty string for a fresh tab.</summary>
        Public ReadOnly Property Url As String
            Get
                If _index < 0 OrElse _index >= _history.Count Then Return String.Empty
                Return _history(_index)
            End Get
        End Property

        Public ReadOnly Property HistoryCount As Integer
            Get
                Return _history.Count
            End Get
        End Property

        ''' <summary>
        ''' Record a navigation. Any forward entries are discarded, because the
        ''' user's new destination replaces the branch they had abandoned.
        ''' </summary>
        Public Sub PushHistory(url As String)
            If String.IsNullOrEmpty(url) Then Return
            If _index >= 0 AndAlso _index < _history.Count AndAlso _history(_index) = url Then Return

            If _index < _history.Count - 1 Then
                _history.RemoveRange(_index + 1, _history.Count - _index - 1)
            End If

            _history.Add(url)
            _index = _history.Count - 1
        End Sub

        ''' <summary>Replace the current entry without adding one, for in-page changes.</summary>
        Public Sub ReplaceCurrent(url As String)
            If String.IsNullOrEmpty(url) Then Return
            If _index < 0 Then
                PushHistory(url)
                Return
            End If
            _history(_index) = url
        End Sub

        Public ReadOnly Property CanGoBack As Boolean
            Get
                Return _index > 0
            End Get
        End Property

        Public ReadOnly Property CanGoForward As Boolean
            Get
                Return _index >= 0 AndAlso _index < _history.Count - 1
            End Get
        End Property

        Public Function Back() As String
            If Not CanGoBack Then Return Url
            _index -= 1
            Return Url
        End Function

        Public Function Forward() As String
            If Not CanGoForward Then Return Url
            _index += 1
            Return Url
        End Function

        ''' <summary>Where back/forward currently point, for pre-fetching a UI hint.</summary>
        Public Function PeekBack() As String
            If Not CanGoBack Then Return Nothing
            Return _history(_index - 1)
        End Function

        Public Function PeekForward() As String
            If Not CanGoForward Then Return Nothing
            Return _history(_index + 1)
        End Function
    End Class

    ''' <summary>
    ''' Owns the tabs and guarantees the active tab is always a valid one.
    ''' </summary>
    Public NotInheritable Class BrowserSession

        Private ReadOnly _tabs As New List(Of TabModel)()

        ''' <summary>
        ''' Settings the user has toggled. Kept on the session rather than the tab
        ''' because they apply to every tab.
        ''' </summary>
        Public Property DesktopMode As Boolean = False

        ''' <summary>Private mode is session-only and never persisted.</summary>
        Public Property PrivateMode As Boolean = False

        ''' <summary>Effective UA for the session mode (see UserAgents).</summary>
        Public ReadOnly Property EffectiveUserAgent As String
            Get
                Return UserAgents.EffectiveUserAgent(DesktopMode)
            End Get
        End Property

        Public ReadOnly Property Tabs As IReadOnlyList(Of TabModel)
            Get
                Return _tabs
            End Get
        End Property

        Private _activeIndex As Integer = -1

        Public ReadOnly Property ActiveTab As TabModel
            Get
                If _tabs.Count = 0 Then NewTab()
                If _activeIndex < 0 OrElse _activeIndex >= _tabs.Count Then _activeIndex = _tabs.Count - 1
                Return _tabs(_activeIndex)
            End Get
        End Property

        Public ReadOnly Property ActiveIndex As Integer
            Get
                Return _activeIndex
            End Get
        End Property

        Public Function NewTab() As TabModel
            Dim tab As New TabModel()
            _tabs.Add(tab)
            _activeIndex = _tabs.Count - 1
            Return tab
        End Function

        ''' <summary>Select a tab by index, ignoring out-of-range requests.</summary>
        Public Sub ActivateTab(index As Integer)
            If index < 0 OrElse index >= _tabs.Count Then Return
            _activeIndex = index
        End Sub

        ''' <summary>
        ''' Close the active tab. A session always keeps at least one tab, which is
        ''' why this cannot leave the list empty; the active index is re-clamped so
        ''' it can never point past the end.
        ''' </summary>
        Public Sub CloseActiveTab()
            If _tabs.Count = 0 Then Return

            _tabs.RemoveAt(_activeIndex)

            If _tabs.Count = 0 Then
                NewTab()
                Return
            End If

            If _activeIndex >= _tabs.Count Then _activeIndex = _tabs.Count - 1
            If _activeIndex < 0 Then _activeIndex = 0
        End Sub
    End Class

End Namespace
