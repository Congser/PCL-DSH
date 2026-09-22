Public Class PageOtherHelp
    Implements IRefreshable

#Region "初始化"

    ''' <summary>
    ''' PCL_DSH：帮助内容里**应当隐藏**的分类。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 这些是上游 PCL 自带的 **Minecraft 教程**（安装 Mod、安装光影、微软账号登录失败……），
    ''' 对 PCL_DSH 的用户毫无意义 —— 本产品已经把 MC 功能整体剥离，
    ''' 只做 DeepSeek Harness 的启动与管理。
    '''
    ''' 不删数据、只在这里过滤，理由有两条：
    ''' <list type="number">
    ''' <item><c>Resources\Help.zip</c> 是上游资源包，删条目会让后续跟上游合并时冲突不断</item>
    ''' <item>万一以后要恢复 MC 相关页，把这个数组清空即可，不用重新找资源</item>
    ''' </list>
    ''' 另外 <c>百科</c> 分类里全是 MC 百科的跳转，一并隐藏。
    ''' </remarks>
    Private ReadOnly DshHiddenHelpTypes As String() = {"Minecraft", "百科"}

    '滚动条
    Private Sub PageOther_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
    End Sub
    '初始化加载器信息
    Private Sub PageOther_Inited(sender As Object, e As EventArgs) Handles Me.Initialized
        PageLoaderInit(Load, PanLoad, PanBack, Nothing, HelpLoader, AddressOf HelpListLoad)
    End Sub

#End Region

    ''' <summary>
    ''' 将帮助列表对象实例化为主页 UI。
    ''' </summary>
    Private Sub HelpListLoad(Loader As LoaderTask(Of Integer, List(Of HelpEntry)))
        Try

            '初始化
            PanList.Children.Clear()
            PanBack.ScrollToHome()
            Dim HelpItems = Loader.Output
            '获取全部分类
            Dim Types As New List(Of String)
            For Each Item As HelpEntry In HelpItems
                If BuildType = BuildTypes.Release AndAlso Not Item.ShowInPublic Then Continue For
                If BuildType <> BuildTypes.Release AndAlso Not Item.ShowInSnapshot Then Continue For
                'PCL_DSH：跳过 MC 相关的教程分类（见 DshHiddenHelpTypes 的说明）
                If Item.Types IsNot Nothing AndAlso
                   Item.Types.Any(Function(t) DshHiddenHelpTypes.Contains(t, StringComparer.OrdinalIgnoreCase)) Then
                    Continue For
                End If
                For Each Type In Item.Types
                    If Not Types.Contains(Type) Then Types.Add(Type)
                Next
            Next
            '将指南页面置顶
            If Types.Contains("指南") Then
                Types.Remove("指南")
                Types.Insert(0, "指南")
            End If
            '转化为 UI
            For Each Type As String In Types
                '确认所属该分类的项目
                Dim TypeItems As New List(Of HelpEntry)
                For Each Item In HelpItems
                    If BuildType = BuildTypes.Release AndAlso Not Item.ShowInPublic Then Continue For
                    If BuildType <> BuildTypes.Release AndAlso Not Item.ShowInSnapshot Then Continue For
                    'PCL_DSH：单个条目也可能带 MC 分类（跨分类的条目），一并跳过
                    If Item.Types IsNot Nothing AndAlso
                       Item.Types.Any(Function(t) DshHiddenHelpTypes.Contains(t, StringComparer.OrdinalIgnoreCase)) Then
                        Continue For
                    End If
                    If Item.Types.Contains(Type) Then TypeItems.Add(Item)
                Next
                'PCL_DSH：过滤后可能整类都空了，别渲染一张空卡片
                If TypeItems.Count = 0 Then Continue For
                '增加卡片
                Dim NewCard As New MyCard With {.Title = Type, .Margin = New Thickness(0, 0, 0, 15), .SwapType = 11}
                Dim NewStack As New StackPanel With {.Margin = New Thickness(20, MyCard.SwapedHeight, 18, 0), .VerticalAlignment = VerticalAlignment.Top, .RenderTransform = New TranslateTransform(0, 0), .Tag = TypeItems}
                NewCard.Children.Add(NewStack)
                NewCard.SwapControl = NewStack
                If Type = "指南" Then
                    MyCard.StackInstall(NewStack, 11, "指南")
                Else
                    NewCard.IsSwapped = True
                End If
                PanList.Children.Add(NewCard)
            Next

        Catch ex As Exception
            Logger.Error(ex, "加载帮助列表 UI 失败")
        End Try
    End Sub

    ''' <summary>
    ''' 帮助项目的点击事件。
    ''' </summary>
    Public Shared Sub OnItemClick(Entry As HelpEntry)
        Try
            If Entry.IsEvent Then
                CustomEvent.Raise(Entry.EventType, Entry.EventData)
            Else
                EnterHelpPage(Entry)
            End If
        Catch ex As Exception
            Logger.Error(ex, "处理帮助项目点击时发生意外错误")
        End Try
    End Sub
    Public Shared Sub EnterHelpPage(Location As String)
        RunInThread(
        Sub()
            If Not HelpLoader.State = LoadState.Finished Then HelpLoader.WaitForExit(GetUuid)
            Dim Entry As New HelpEntry(Location)
            RunInUi(
            Sub()
                Dim FrmHelpDetail As New PageOtherHelpDetail
                If FrmHelpDetail.Init(Entry) Then
                    FrmMain.PageChange(New FormMain.PageStackData With {.Page = FormMain.PageType.HelpDetail, .Additional = {Entry, FrmHelpDetail}})
                Else
                    Logger.Warn("已取消进入帮助项目，这一般是由于 xaml 初始化失败，且用户在弹窗中手动放弃")
                End If
            End Sub)
        End Sub)
    End Sub
    Public Shared Sub EnterHelpPage(Entry As HelpEntry)
        RunInThread(
        Sub()
            If Not HelpLoader.State = LoadState.Finished Then HelpLoader.WaitForExit(GetUuid)
            RunInUi(
            Sub()
                Dim FrmHelpDetail As New PageOtherHelpDetail
                If FrmHelpDetail.Init(Entry) Then
                    FrmMain.PageChange(New FormMain.PageStackData With {.Page = FormMain.PageType.HelpDetail, .Additional = {Entry, FrmHelpDetail}})
                Else
                    Logger.Warn("已取消进入帮助项目，这一般是由于 xaml 初始化失败，且用户在弹窗中手动放弃")
                End If
            End Sub)
        End Sub)
    End Sub

    ''' <summary>
    ''' 搜索帮助。
    ''' </summary>
    Public Sub SearchRun() Handles SearchBox.TextChanged
        If String.IsNullOrWhiteSpace(SearchBox.Text) Then
            '隐藏
            AniStart({
                 AaOpacity(PanSearch, -PanSearch.Opacity, 100),
                 AaCode(
                 Sub()
                     PanSearch.Height = 0
                     PanSearch.Visibility = Visibility.Collapsed
                     PanList.Visibility = Visibility.Visible
                 End Sub,, True),
                 AaOpacity(PanList, 1 - PanList.Opacity, 150, 30)
            }, "FrmOtherHelp Search Switch")
        Else
            '构造请求
            Dim QueryList As New List(Of SearchEntry(Of HelpEntry))
            For Each Entry As HelpEntry In HelpLoader.Output
                If Not Entry.ShowInSearch OrElse (BuildType = BuildTypes.Release AndAlso Not Entry.ShowInPublic) Then Continue For
                If Not Entry.ShowInSearch OrElse (BuildType <> BuildTypes.Release AndAlso Not Entry.ShowInSnapshot) Then Continue For
                'PCL_DSH：搜索也不能搜出 MC 教程 —— 列表里已经不显示它们了，
                '搜索却还能命中，会让人以为界面坏了
                If Entry.Types IsNot Nothing AndAlso
                   Entry.Types.Any(Function(t) DshHiddenHelpTypes.Contains(t, StringComparer.OrdinalIgnoreCase)) Then
                    Continue For
                End If
                QueryList.Add(New SearchEntry(Of HelpEntry) With {
                    .Item = Entry,
                    .SearchSource = New List(Of SearchSource) From {
                        New SearchSource(Entry.Title, 1),
                        New SearchSource(Entry.Desc, 0.5),
                        New SearchSource(Entry.Search, 1.5)
                    }
                })
                'New SearchSource(If(Entry.IsEvent, If(Entry.EventData, ""), Entry.XamlContent), 0.2)
            Next
            '进行搜索，构造列表
            Dim SearchResult = Search(QueryList, SearchBox.Text, MaxBlurCount:=5, MinBlurSimilarity:=0.08)
            PanSearchList.Children.Clear()
            If Not SearchResult.Any() Then
                PanSearch.Title = "无搜索结果"
                PanSearchList.Visibility = Visibility.Collapsed
            Else
                PanSearch.Title = "搜索结果"
                For Each Result In SearchResult
                    Dim Item = Result.Item.ToListItem
                    If ModeDebug Then Item.Info = If(Result.AbsoluteRight, "完全匹配，", "") & "相似度：" & Math.Round(Result.Similarity, 3) & "，" & Item.Info
                    PanSearchList.Children.Add(Item)
                Next
                PanSearchList.Visibility = Visibility.Visible
            End If
            '显示
            AniStart({
                 AaOpacity(PanList, -PanList.Opacity, 100),
                 AaCode(
                 Sub()
                     PanList.Visibility = Visibility.Collapsed
                     PanSearch.Visibility = Visibility.Visible
                     PanSearch.TriggerForceResize()
                 End Sub,, True),
                 AaOpacity(PanSearch, 1 - PanSearch.Opacity, 150, 30)
            }, "FrmOtherHelp Search Switch")
        End If
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        PageOtherLeft.RefreshHelp()
    End Sub
End Class
