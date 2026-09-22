Imports System.Windows.Media

''' <summary>
''' 插件社区页 —— dsh 插件的发现与安装。
'''
''' ⭐ **数据源（决策 4-a / 4-b）**
''' 默认走 <c>awesome-dsh-plugin.com</c> 的静态索引（4000+ 插件、无配额、带分类）；
''' 可在设置里切回 GitHub <c>topic:dsh-plugin</c>（只一页 50 个、匿名配额 10 次/小时）。
''' 所有配额相关的提示文案都跟着数据源动态变化 —— CDN 源下套用限流警告是误导。
'''
''' ⭐ **加载策略：完全手动**（2026-09-21 用户决定，取代原「三重刷新」决策 3-a）
'''   ① 「刷新插件列表」按钮 —— 强制联网（<c>ForceRefresh:=True</c>）
'''   ② 「仅读缓存」按钮 —— 一个请求都不发，直接渲染上次的结果
'''   ③ 缓存过期判据（<see cref="DshPluginMarket.IsCacheFresh"/>）只在①②内部生效，
'''      决定「读到的东西够不够新、要不要真去联网」
''' 缓存**有效期按数据源动态决定**：
'''   · 索引站源（无配额）→ **30 秒**，接近实时
'''   · GitHub 源（10 次/小时）→ 30 分钟，避免烧光配额
''' 这就是为什么 TTL 不能写死成 30 秒 —— 在 GitHub 源下会瞬间 403。
'''
''' ⚠️ **进入页面 / 切回本页都不加载**（与「镜像版本」页一致）。
''' 原设计的「切回本页自动按新鲜度重拉」已去掉：索引站源 TTL 只有 30 秒，
''' 等于每次切页都真联网拉 4 MB，用户从别的页切过来就被卡一下。
''' 现在只有用户点①②才会动，视觉树里已渲染的列表会一直留着，不会白屏。
'''
''' ⭐ **大数据量渲染**
''' 索引站源一次给 4000+ 条。**绝不能**一次全塞进视觉树
''' （每个插件行是 Border+Grid+3 个 TextBlock，4000 行 = 2 万多个元素，
'''   首次渲染会卡住好几秒）。
''' 做法：
'''   · 内存里保留全量 <see cref="AllPlugins"/>
'''   · 界面上只保留最近一次筛选结果的前 <see cref="PageSize"/> 条
'''   · 底部「加载更多」每点一次多渲染 <see cref="PageSize"/> 条
''' </summary>
Public Class PageDownloadPlugin
    Implements IRefreshable

#Region "状态"

    Private IsLoad As Boolean = False

    ''' <summary>联网 / 安装期间为 True。</summary>
    Private IsBusy As Boolean = False

    ''' <summary>全量插件（不随筛选变化）。</summary>
    Private AllPlugins As New List(Of DshPluginMarket.DshPluginInfo)

    ''' <summary>当前筛选 + 排序后的结果。</summary>
    Private Filtered As New List(Of DshPluginMarket.DshPluginInfo)

    ''' <summary>当前已经渲染出来的条数（配合「加载更多」）。</summary>
    Private RenderedCount As Integer = 0

    ''' <summary>一页渲染多少条。</summary>
    ''' <remarks>
    ''' 60 是权衡：一屏大约能看 4~6 行，60 条够滚十几屏；
    ''' 而 60 个视觉子树构建约 20~40 ms，用户感觉不到卡顿。
    ''' </remarks>
    Private Const PageSize As Integer = 60

    ''' <summary>当前结果是否来自缓存。</summary>
    Private CurrentFromCache As Boolean = False

    ''' <summary>当前结果的完整元信息（用于状态行）。</summary>
    Private CurrentResult As DshPluginMarket.DshPluginSearchResult = Nothing

    ''' <summary>程序化改写实例下拉框时置位。</summary>
    Private IsUpdatingInstanceCombo As Boolean = False

    ''' <summary>程序化改写分类 / 排序下拉框时置位（防 SelectionChanged 递归）。</summary>
    Private IsUpdatingFilterCombos As Boolean = False

    ''' <summary>某个插件的安装请求正在处理中（key），用于防连点。</summary>
    Private InstallingSpec As String = Nothing

    ''' <summary>搜索框的防抖计时器。</summary>
    Private SearchDebounce As Windows.Threading.DispatcherTimer = Nothing

#End Region

#Region "初始化"

    Private Sub PageDownloadPlugin_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        If Not IsLoad Then
            IsLoad = True
            AddHandler ModDSH.DshInstancesChanged, AddressOf OnInstancesChanged
        End If

        '⚠️ 初始值必须在挂事件**之前**设好（决策备忘里 MyComboBox 的坑）：
        '   SelectionChanged 一旦挂上，程序化改 SelectedItem 也会触发。
        BuildSortCombo()
        BuildCategoryCombo(Nothing)

        BuildInstanceCombo()
        RefreshInstalledList()
        ShowEmptyHintIfNothingLoaded()
    End Sub

    ''' <summary>
    ''' 每次切到本页时执行。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ **这里刻意不加载插件列表**（用户 2026-09-21 的决定）。
    ''' 原设计是「三重刷新」的第 2 重 —— 切回本页自动按缓存新鲜度决定要不要重拉。
    ''' 但实际用起来的问题是：每次切页都会触发一次「正在读取插件列表…」的忙碌态，
    ''' 索引站源下 TTL 只有 30 秒，等于**几乎每次切页都在真联网拉 4 MB**，
    ''' 用户从别的页切过来就被卡一下，很烦。
    '''
    ''' 现在改成**完全手动**，和「镜像版本」页保持一致：
    '''   · 点「刷新插件列表」→ 强制联网重新获取
    '''   · 点「仅读缓存」    → 只读本地缓存，一个请求都不发
    '''   · 什么都不点        → 保持上次已渲染的列表（视觉树还在，不会白屏）
    ''' 首次进入（还没加载过）时显示引导文案，告诉用户该点哪里。
    ''' </remarks>
    Public Sub PageOnEnterHook() Handles Me.PageEnter
        BuildInstanceCombo()
        RefreshInstalledList()
        ShowEmptyHintIfNothingLoaded()
    End Sub

    ''' <summary>
    ''' 还没加载过任何列表时，显示「点击刷新插件列表获取」的引导文案。
    ''' </summary>
    ''' <remarks>
    ''' 只在**从没加载过**时动手：一旦有数据，视觉树里的列表还在，
    ''' 这时去改 <c>LabPluginsEmpty</c> 反而会盖住已经渲染好的内容。
    ''' （<c>RenderNextPage</c> 也会管这个控件的可见性，两边判断口径要一致。）
    ''' </remarks>
    Private Sub ShowEmptyHintIfNothingLoaded()
        If CurrentResult IsNot Nothing AndAlso AllPlugins.Count > 0 Then Return
        LabPluginsEmpty.Visibility = Visibility.Visible
    End Sub

    Private Sub OnInstancesChanged()
        BuildInstanceCombo()
        RefreshInstalledList()
    End Sub

#End Region

#Region "目标实例"

    Private Sub BuildInstanceCombo()
        Try
            IsUpdatingInstanceCombo = True
            Try
                Dim previousId As String = Nothing
                Dim prevItem = TryCast(ComboInstance.SelectedItem, MyComboBoxItem)
                If prevItem IsNot Nothing Then previousId = CStr(prevItem.Tag)

                ComboInstance.Items.Clear()
                For Each inst In ModDSH.DshInstances
                    ComboInstance.Items.Add(New MyComboBoxItem With {
                        .Content = inst.DisplayName,
                        .Tag = inst.Id
                    })
                Next

                '尽量保持之前的选择
                Dim restored As Boolean = False
                If Not String.IsNullOrWhiteSpace(previousId) Then
                    For Each obj In ComboInstance.Items
                        Dim ci = CType(obj, MyComboBoxItem)
                        If String.Equals(CStr(ci.Tag), previousId, StringComparison.Ordinal) Then
                            ComboInstance.SelectedItem = ci
                            restored = True
                            Exit For
                        End If
                    Next
                End If

                If Not restored AndAlso ComboInstance.Items.Count > 0 Then
                    '回落到「当前实例」
                    Dim selected As DshInstance = ModDSH.DshSelectedInstance
                    Dim targetIdx As Integer = 0
                    If selected IsNot Nothing Then
                        For i As Integer = 0 To ComboInstance.Items.Count - 1
                            Dim ci = CType(ComboInstance.Items(i), MyComboBoxItem)
                            If String.Equals(CStr(ci.Tag), selected.Id, StringComparison.Ordinal) Then
                                targetIdx = i
                                Exit For
                            End If
                        Next
                    End If
                    ComboInstance.SelectedItem = ComboInstance.Items(targetIdx)
                End If
            Finally
                IsUpdatingInstanceCombo = False
            End Try
            RefreshInstanceNote()
        Catch ex As Exception
            Logger.Error(ex, "DSH：填充插件目标实例下拉框失败")
        End Try
    End Sub

    Private Sub RefreshInstanceNote()
        Try
            Dim inst As DshInstance = GetTargetInstance()
            If inst Is Nothing Then
                LabInstanceNote.Text = "没有可用实例"
                Return
            End If
            LabInstanceNote.Text = $"profile：{If(inst.Profile, ModDSH.DshProfileName)}" & vbCrLf &
                                   $"插件目录：{DshPluginMarket.ProfileDirOf(inst)}"
        Catch ex As Exception
            LabInstanceNote.Text = ""
        End Try
    End Sub

    ''' <summary>取当前选中的目标实例。</summary>
    Private Function GetTargetInstance() As DshInstance
        Dim item = TryCast(ComboInstance.SelectedItem, MyComboBoxItem)
        If item IsNot Nothing Then
            Dim inst As DshInstance = ModDSH.DshGetInstance(CStr(item.Tag))
            If inst IsNot Nothing Then Return inst
        End If
        Return ModDSH.DshSelectedInstance
    End Function

#End Region

#Region "筛选与排序控件"

    ''' <summary>填充排序下拉框（只做一次）。</summary>
    Private Sub BuildSortCombo()
        Try
            IsUpdatingFilterCombos = True
            ComboSort.Items.Clear()
            ComboSort.Items.Add(New MyComboBoxItem With {.Content = "星标最多", .Tag = CInt(DshPluginMarket.DshPluginSort.Stars)})
            ComboSort.Items.Add(New MyComboBoxItem With {.Content = "最新收录", .Tag = CInt(DshPluginMarket.DshPluginSort.Newest)})
            ComboSort.Items.Add(New MyComboBoxItem With {.Content = "名称 A→Z", .Tag = CInt(DshPluginMarket.DshPluginSort.NameAsc)})
            ComboSort.Items.Add(New MyComboBoxItem With {.Content = "优先 npm 安装", .Tag = CInt(DshPluginMarket.DshPluginSort.NpmFirst)})
            ComboSort.SelectedItem = ComboSort.Items(1)
        Finally
            IsUpdatingFilterCombos = False
        End Try
        '⚠️ 事件在初值设好**之后**才挂 —— 否则设置初值就会触发一次空筛选
        AddHandler ComboSort.SelectionChanged, AddressOf ComboSort_SelectionChanged
    End Sub

    ''' <summary>填充分类下拉框。</summary>
    ''' <param name="categories">分类列表；Nothing 时只放一个「全部分类」。</param>
    ''' <param name="PreferKey">优先恢复选中的分类 key；Nothing 时按「保持当前选择 → 落到全部」处理。</param>
    ''' <remarks>
    ''' ⚠️ 这是**重建** Items，所以每次都要先摘掉事件再挂回来，
    ''' 否则程序化重建会连着触发好几次 SelectionChanged。
    ''' </remarks>
    Private Sub BuildCategoryCombo(categories As List(Of DshPluginMarket.DshPluginCategory),
                                   Optional PreferKey As String = Nothing)
        Try
            RemoveHandler ComboCategory.SelectionChanged, AddressOf ComboCategory_SelectionChanged

            Dim previous As String = PreferKey
            If String.IsNullOrWhiteSpace(previous) Then
                Dim prevItem = TryCast(ComboCategory.SelectedItem, MyComboBoxItem)
                If prevItem IsNot Nothing Then previous = CStr(prevItem.Tag)
            End If
            If String.IsNullOrWhiteSpace(previous) Then previous = "*"

            ComboCategory.Items.Clear()
            ComboCategory.Items.Add(New MyComboBoxItem With {
                .Content = $"全部分类（{AllPlugins.Count}）",
                .Tag = "*"
            })

            If categories IsNot Nothing Then
                For Each c In categories
                    ComboCategory.Items.Add(New MyComboBoxItem With {.Content = c.Display, .Tag = c.Key})
                Next
            End If

            '尽量恢复到之前选的那个分类（它可能在新列表里已经没了）
            Dim restored As Boolean = False
            For Each obj In ComboCategory.Items
                Dim ci = CType(obj, MyComboBoxItem)
                If String.Equals(CStr(ci.Tag), previous, StringComparison.Ordinal) Then
                    ComboCategory.SelectedItem = ci
                    restored = True
                    Exit For
                End If
            Next
            If Not restored AndAlso ComboCategory.Items.Count > 0 Then
                '之前选的分类在当前搜索下没有命中 → 落回「全部分类」，
                '否则会卡在一个 0 结果的分类上，看起来像列表坏了
                ComboCategory.SelectedItem = ComboCategory.Items(0)
            End If
        Catch ex As Exception
            Logger.Warn($"DSH：填充分类下拉框失败（可忽略）：{ex.Message}")
        Finally
            AddHandler ComboCategory.SelectionChanged, AddressOf ComboCategory_SelectionChanged
        End Try
    End Sub

    Private Sub ComboCategory_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
        If IsUpdatingFilterCombos Then Return
        ApplyFilter()
    End Sub

    Private Sub ComboSort_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
        If IsUpdatingFilterCombos Then Return
        ApplyFilter()
    End Sub

    ''' <summary>搜索框文本变化（带内置延迟）。</summary>
    Private Sub TextSearch_ValidatedTextChanged(sender As Object, e As RoutedEventArgs) Handles TextSearch.ValidatedTextChanged
        '再叠一层 250ms 防抖：MyTextBox 自己的延迟较短，
        '而这里每次筛选都要遍历 4000 条 + 重建视觉树，值得再等一拍。
        If SearchDebounce Is Nothing Then
            SearchDebounce = New Windows.Threading.DispatcherTimer With {
                .Interval = TimeSpan.FromMilliseconds(250)
            }
            AddHandler SearchDebounce.Tick,
                Sub()
                    SearchDebounce.Stop()
                    ApplyFilter()
                End Sub
        End If
        SearchDebounce.Stop()
        SearchDebounce.Start()
    End Sub

    ''' <summary>取当前选中的分类 key。</summary>
    Private Function GetSelectedCategory() As String
        Dim item = TryCast(ComboCategory.SelectedItem, MyComboBoxItem)
        If item Is Nothing Then Return "*"
        Return CStr(item.Tag)
    End Function

    ''' <summary>取当前排序方式。</summary>
    Private Function GetSelectedSort() As DshPluginMarket.DshPluginSort
        Dim item = TryCast(ComboSort.SelectedItem, MyComboBoxItem)
        If item Is Nothing Then Return DshPluginMarket.DshPluginSort.Stars
        Try
            Return CType(CInt(item.Tag), DshPluginMarket.DshPluginSort)
        Catch
            Return DshPluginMarket.DshPluginSort.Stars
        End Try
    End Function

#End Region

#Region "插件列表"

    ''' <summary>加载插件列表。</summary>
    ''' <param name="ForceRefresh">True = 强制联网。</param>
    Public Sub Reload(Optional ForceRefresh As Boolean = True)
        LoadPlugins(ForceRefresh)
    End Sub

    Private Sub LoadPlugins(ForceRefresh As Boolean)
        If IsBusy Then
            Hint("正在处理中，请稍候……")
            Return
        End If

        If Not ForceRefresh Then
            '缓存够新就直接渲染，一个请求都不发
            Dim cached = DshPluginMarket.ReadCache()
            If DshPluginMarket.IsCacheFresh(cached) Then
                AbsorbResult(cached)
                Return
            End If
        End If

        Dim srcHint As String = If(DshPluginMarket.DshPluginUseCdnSource,
                                   "正在从插件索引站获取列表……",
                                   "正在从 GitHub 获取插件列表……")
        SetBusy(True, If(ForceRefresh, srcHint, "正在读取插件列表……"))

        RunInNewThread(
            Sub()
                Dim result As DshPluginMarket.DshPluginSearchResult = Nothing
                Dim err As String = Nothing
                Try
                    result = DshPluginMarket.FetchPlugins(ForceRefresh)
                Catch ex As Exception
                    err = ex.Message
                    Logger.Error(ex, "DSH：获取插件列表失败")
                End Try

                RunInUi(
                    Sub()
                        SetBusy(False)
                        If result Is Nothing Then
                            '失败时也尽量把过期缓存显示出来，总比空白页强
                            Dim stale = DshPluginMarket.ReadCache()
                            If stale IsNot Nothing AndAlso stale.Items.Count > 0 Then
                                AbsorbResult(stale)
                            Else
                                LabPluginsEmpty.Visibility = Visibility.Visible
                            End If
                            Hint("获取插件列表失败：" & If(err, "未知原因"), HintType.Red)
                            Return
                        End If

                        AbsorbResult(result)
                        Hint($"已获取 {result.Items.Count} 个插件。", HintType.Green)
                    End Sub)
            End Sub, "DSH 获取插件列表")
    End Sub

    ''' <summary>
    ''' 把一次查询结果吸收进页面状态并渲染。
    ''' </summary>
    ''' <remarks>
    ''' 集中处理「换数据源」的一个隐患：**索引站与 GitHub 源的数据结构不同**
    ''' （索引站有分类、无星标；GitHub 有星标、无分类）。
    ''' 切换源后分类表会整个变掉，所以这里无条件重建分类下拉框。
    ''' </remarks>
    Private Sub AbsorbResult(result As DshPluginMarket.DshPluginSearchResult)
        CurrentResult = result
        CurrentFromCache = result.FromCache
        AllPlugins = If(result.Items, New List(Of DshPluginMarket.DshPluginInfo))

        '分类下拉框跟着数据源重建（保留用户之前选的分类，如果它还在）
        BuildCategoryCombo(DshPluginMarket.BuildCategories(AllPlugins, result.Categories))
        '新数据到达 → 分类计数已经用全量算过了，把「上次算计数用的关键词」同步过来，
        '免得紧接着的 ApplyFilter 又白算一遍（或反过来漏算一遍）
        LastCategoryKeyword = If(TextSearch.Text, "").Trim()

        ApplyFilter()
        UpdateMarketStatus(result)
    End Sub

    ''' <summary>更新市场状态说明行。</summary>
    Private Sub UpdateMarketStatus(result As DshPluginMarket.DshPluginSearchResult)
        Try
            If result Is Nothing Then
                LabMarketStatus.Text = ""
                Return
            End If

            Dim parts As New List(Of String)
            If result.FromCache Then
                parts.Add($"当前显示缓存结果（{result.CacheAgeText}）· 缓存有效期 {DshPluginMarket.DshPluginCacheTtlText}")
            Else
                parts.Add($"数据来源：{result.Source} · 刚刚获取")
            End If

            '索引站源下的 total 是可信的；GitHub 源下刻意不显示（见 DshPluginSearchResult 的注释）
            If result.IsFullList Then
                Dim src As String = If(String.IsNullOrWhiteSpace(result.IndexUpdatedAt), "",
                                       $"（索引更新于 {result.IndexUpdatedAt}）")
                parts.Add($"全量 {result.Items.Count} 个插件{src}")
            Else
                parts.Add($"已获取 {result.Items.Count} 个")
                If result.MayHaveMore Then
                    parts.Add("GitHub 源只取一页，可能还有更多；切到索引站源可以看到全部。")
                End If
            End If

            '配额提示只在 GitHub 源下有意义 —— 索引站无配额，别吓唬用户
            If Not DshPluginMarket.DshPluginUseCdnSource Then
                parts.Add("GitHub 匿名搜索接口限额 10 次/小时，请尽量用缓存。")
            End If

            '当前筛选情况（只在真的筛掉了东西时才说）
            If Filtered.Count <> AllPlugins.Count Then
                parts.Add($"筛选后 {Filtered.Count} 个")
            End If

            LabMarketStatus.Text = String.Join(vbCrLf, parts)
        Catch ex As Exception
            Logger.Warn($"DSH：更新市场状态失败（可忽略）：{ex.Message}")
        End Try
    End Sub

    ''' <summary>按当前筛选条件重算列表并重新渲染。</summary>
    Private Sub ApplyFilter()
        Try
            Dim keyword As String = If(TextSearch.Text, "")

            Filtered = DshPluginMarket.FilterAndSort(
                AllPlugins,
                keyword,
                GetSelectedCategory(),
                GetSelectedSort())

            '分类下拉框里的计数要跟着搜索词走。
            '否则搜「memory」时下拉框还显示「UI 增强（698）」，
            '点进去却只有 2 条 —— 用户会以为筛选坏了。
            '⚠️ 只在搜索词变化时重建（`LastCategoryKeyword` 比对），
            '   避免每次切分类都重建一遍 24 个下拉项。
            If Not String.Equals(keyword.Trim(), LastCategoryKeyword, StringComparison.Ordinal) Then
                LastCategoryKeyword = keyword.Trim()
                RebuildCategoryCounts(keyword)
            End If

            '筛选变了就回到第一页
            PanPlugins.Children.Clear()
            RenderedCount = 0
            RenderNextPage()

            '状态行里的「筛选后 N 个」要跟着更新 —— 否则用户改完搜索词，
            '看到的还是「全量 4062 个」，会以为筛选没生效。
            UpdateMarketStatus(CurrentResult)
        Catch ex As Exception
            Logger.Error(ex, "DSH：筛选插件列表失败")
        End Try
    End Sub

    ''' <summary>上一次用来算分类计数的搜索词。</summary>
    Private LastCategoryKeyword As String = Nothing

    ''' <summary>
    ''' 按当前搜索词重算各分类的计数，并重建分类下拉框。
    ''' </summary>
    ''' <remarks>
    ''' 计数口径：**只看搜索词、不看已选分类**。
    ''' 也就是「搜 memory，各分类分别命中多少」——
    ''' 这正是用户在挑分类时想知道的。若把已选分类也套进去，
    ''' 其他分类会全变成 0，下拉框就没法切换了。
    ''' </remarks>
    Private Sub RebuildCategoryCounts(keyword As String)
        Try
            '保持用户当前的选择
            Dim selected As String = GetSelectedCategory()

            Dim scope As IEnumerable(Of DshPluginMarket.DshPluginInfo) = AllPlugins
            If Not String.IsNullOrWhiteSpace(keyword) Then
                '借 FilterAndSort 的关键词逻辑，但分类传 "*"（不按分类过滤）
                scope = DshPluginMarket.FilterAndSort(AllPlugins, keyword, "*", DshPluginMarket.DshPluginSort.Stars)
            End If

            BuildCategoryCombo(DshPluginMarket.BuildCategories(
                scope, If(CurrentResult IsNot Nothing, CurrentResult.Categories, Nothing)),
                selected)
        Catch ex As Exception
            Logger.Warn($"DSH：重算分类计数失败（可忽略）：{ex.Message}")
        End Try
    End Sub

    ''' <summary>再渲染一页（或者第一页）。</summary>
    Private Sub RenderNextPage()
        Try
            If Filtered.Count = 0 Then
                PanPlugins.Children.Clear()
                PanPlugins.Children.Add(MakeInfoText("没有匹配的插件。换个关键词，或者把分类切回「全部分类」。"))
                LabPluginsEmpty.Visibility = Visibility.Collapsed
                PanMore.Children.Clear()
                Return
            End If
            LabPluginsEmpty.Visibility = Visibility.Collapsed

            Dim installed As Dictionary(Of String, String) = GetInstalledMap()
            Dim [end] As Integer = Math.Min(RenderedCount + PageSize, Filtered.Count)
            For i As Integer = RenderedCount To [end] - 1
                PanPlugins.Children.Add(BuildPluginRow(Filtered(i), installed))
            Next
            RenderedCount = [end]

            BuildMoreButton()
        Catch ex As Exception
            Logger.Error(ex, "DSH：渲染插件列表失败")
        End Try
    End Sub

    ''' <summary>重建底部的「加载更多 / 已全部显示」区域。</summary>
    Private Sub BuildMoreButton()
        PanMore.Children.Clear()

        If RenderedCount >= Filtered.Count Then
            If Filtered.Count > PageSize Then
                Dim tb As New TextBlock With {
                    .FontSize = 11.5,
                    .Margin = New Thickness(0, 6, 0, 6),
                    .Text = $"已显示全部 {Filtered.Count} 个"
                }
                tb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4")
                PanMore.Children.Add(tb)
            End If
            Return
        End If

        Dim btn As New MyButton With {
            .Text = $"加载更多（已显示 {RenderedCount} / {Filtered.Count}）",
            .Height = 30,
            .Margin = New Thickness(0, 6, 0, 6)
        }
        btn.TextPadding = New Thickness(16, 0, 16, 0)
        AddHandler btn.Click,
            Sub(s, e)
                Try
                    RenderNextPage()
                Catch ex As Exception
                    Logger.Error(ex, "DSH：加载更多失败")
                End Try
            End Sub
        PanMore.Children.Add(btn)
    End Sub

    ''' <summary>构建一行插件。</summary>
    Private Function BuildPluginRow(p As DshPluginMarket.DshPluginInfo,
                                    installed As Dictionary(Of String, String)) As FrameworkElement
        '仓库名或 owner/repo 命中都算已安装；索引站源还多了个 npm 包名可以比对
        Dim installedKey As String = Nothing
        If Not String.IsNullOrWhiteSpace(p.FullName) AndAlso installed.ContainsKey(p.FullName) Then
            installedKey = p.FullName
        ElseIf installed.ContainsKey(p.RepoName) Then
            installedKey = p.RepoName
        ElseIf p.HasNpm AndAlso installed.ContainsKey(DshPluginMarket.NpmBaseName(p.NpmName)) Then
            installedKey = DshPluginMarket.NpmBaseName(p.NpmName)
        End If

        Dim row As New Border With {
            .BorderThickness = New Thickness(1),
            .CornerRadius = New CornerRadius(4),
            .Padding = New Thickness(13, 9, 13, 9),
            .Margin = New Thickness(0, 0, 0, 6)
        }
        row.SetResourceReference(Border.BorderBrushProperty,
                                 If(installedKey IsNot Nothing, "ColorBrush3", "ColorBrushGray5"))
        If installedKey IsNot Nothing Then
            row.SetResourceReference(Border.BackgroundProperty, "ColorBrush8")
        Else
            '⚠️ 必须显式给一个透明背景：Border 的 Background 为 Nothing 时，
            '   只有边框线本身能命中鼠标，中间的留白收不到 MouseEnter，
            '   下面"悬停展开简介"就会时灵时不灵。
            row.Background = Brushes.Transparent
        End If

        Dim grid As New Grid()
        grid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
        grid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

        ' ── 左：仓库名 + 简介 + 元信息 ──
        Dim left As New StackPanel()

        '标题行用 WrapPanel：索引站源下名称最长能到 40+ 字符，
        '横向 StackPanel 会被右边的按钮挤到溢出（没有裁剪，直接画到卡片外）。
        Dim titleRow As New WrapPanel With {.Orientation = Orientation.Horizontal}
        Dim nameText As New TextBlock With {
            .FontSize = 14,
            .FontWeight = FontWeights.Bold,
            .Text = p.Display,
            .VerticalAlignment = VerticalAlignment.Center,
            .TextTrimming = TextTrimming.CharacterEllipsis,
            .MaxWidth = 420
        }
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        '展示名可能是 "dsh-forge-studio#plugin-notes" 这种子包形式 —— 悬停时给出完整仓库地址
        nameText.ToolTip = If(p.Display <> p.FullName, p.Display & vbCrLf & vbCrLf & "仓库：" & p.FullName, p.FullName)
        titleRow.Children.Add(nameText)

        If installedKey IsNot Nothing Then
            titleRow.Children.Add(MakeBadge("已安装 " & If(installed(installedKey), ""), &H13, &H70, &HF3))
        End If
        If p.HasNpm Then
            '有 npm 包 = 安装成功率最高的一条路径，值得突出
            titleRow.Children.Add(MakeBadge("npm", &H2E, &HA8, &H5E))
        End If
        If Not String.IsNullOrWhiteSpace(p.Version) Then
            titleRow.Children.Add(MakeBadge("v" & p.Version, &H9A, &H9A, &H9A))
        End If
        left.Children.Add(titleRow)

        '简介默认只显示 2 行 —— 很多仓库的简介长得离谱，
        '不限行的话一屏只能看下一两个插件（这正是用户反馈"显示区域太小"的原因）。
        '鼠标移到整张卡片上时再展开看全文。
        Const DescLineHeight As Double = 18.0
        Const DescMaxLines As Integer = 2
        Dim descClamp As Double = DescLineHeight * DescMaxLines

        Dim descText As New TextBlock With {
            .FontSize = 12.5,
            .Margin = New Thickness(0, 4, 0, 0),
            .TextWrapping = TextWrapping.Wrap,
            .LineHeight = DescLineHeight,
            .Text = p.DescriptionText,
            .MaxHeight = descClamp,
            .TextTrimming = TextTrimming.CharacterEllipsis
        }
        descText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2")
        descText.ToolTip = p.DescriptionText
        left.Children.Add(descText)

        Dim metaParts As New List(Of String)
        If p.Stars > 0 Then metaParts.Add($"★ {p.Stars}")
        If Not String.IsNullOrWhiteSpace(p.Category) Then
            Dim zh As String = Nothing
            If CurrentResult IsNot Nothing Then CurrentResult.Categories.TryGetValue(p.Category, zh)
            metaParts.Add(If(String.IsNullOrWhiteSpace(zh), p.Category, zh))
        End If
        If Not String.IsNullOrWhiteSpace(p.Language) Then metaParts.Add(p.Language)
        If p.AddedAt IsNot Nothing Then
            metaParts.Add("收录于 " & CType(p.AddedAt, DateTime).ToString("yyyy-MM-dd"))
        ElseIf p.UpdatedAt IsNot Nothing Then
            metaParts.Add("更新于 " & p.UpdatedText)
        End If
        Dim metaText As New TextBlock With {
            .FontSize = 11.5,
            .Margin = New Thickness(0, 4, 0, 0),
            .Text = String.Join(" · ", metaParts)
        }
        metaText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4")
        left.Children.Add(metaText)

        Grid.SetColumn(left, 0)
        grid.Children.Add(left)

        ' ── 右：操作按钮 ──
        Dim right As New StackPanel With {
            .Orientation = Orientation.Horizontal,
            .VerticalAlignment = VerticalAlignment.Center,
            .Margin = New Thickness(12, 0, 0, 0)
        }

        Dim captured As DshPluginMarket.DshPluginInfo = p

        Dim btnRepo As New MyButton With {.Text = "仓库", .Height = 28, .Margin = New Thickness(0, 0, 8, 0)}
        btnRepo.TextPadding = New Thickness(12, 0, 12, 0)
        AddHandler btnRepo.Click, Sub(s, e) OpenRepo(captured)
        right.Children.Add(btnRepo)

        If installedKey IsNot Nothing Then
            Dim btnRemove As New MyButton With {.Text = "卸载", .Height = 28}
            btnRemove.TextPadding = New Thickness(12, 0, 12, 0)
            AddHandler btnRemove.Click, Sub(s, e) UninstallPluginByName(installedKey, GetTargetInstance())
            right.Children.Add(btnRemove)
        ElseIf String.Equals(InstallingSpec, p.Key, StringComparison.OrdinalIgnoreCase) Then
            Dim btnBusy As New MyButton With {.Text = "安装中…", .Height = 28, .IsEnabled = False}
            btnBusy.TextPadding = New Thickness(12, 0, 12, 0)
            right.Children.Add(btnBusy)
        Else
            Dim btnInstall As New MyButton With {.Text = "安装", .Height = 28}
            btnInstall.TextPadding = New Thickness(12, 0, 12, 0)
            AddHandler btnInstall.Click, Sub(s, e) InstallPlugin(captured)
            right.Children.Add(btnInstall)
        End If

        Grid.SetColumn(right, 1)
        grid.Children.Add(right)

        '鼠标移进整张卡片就展开简介全文，移出收回 2 行。
        '挂在 row（而不是 descText）上，这样鼠标落在卡片任何位置都能展开 ——
        '只挂文本的话，鼠标稍微偏一点就收回去，很难看清。
        AddHandler row.MouseEnter,
            Sub(s, e)
                Try
                    descText.MaxHeight = Double.PositiveInfinity
                Catch ex As Exception
                    '展开失败不影响功能，忽略
                End Try
            End Sub
        AddHandler row.MouseLeave,
            Sub(s, e)
                Try
                    descText.MaxHeight = descClamp
                Catch ex As Exception
                    '同上
                End Try
            End Sub

        row.Child = grid
        Return row
    End Function

    ''' <summary>生成一个小徽标。</summary>
    Private Function MakeBadge(Text As String, R As Byte, G As Byte, B As Byte) As FrameworkElement
        Dim border As New Border With {
            .CornerRadius = New CornerRadius(3),
            .Padding = New Thickness(6, 1, 6, 1),
            .Margin = New Thickness(8, 0, 0, 0),
            .VerticalAlignment = VerticalAlignment.Center,
            .Background = New SolidColorBrush(Color.FromRgb(R, G, B))
        }
        border.Child = New TextBlock With {
            .Text = Text.Trim(),
            .FontSize = 11,
            .Foreground = Brushes.White
        }
        Return border
    End Function

    ''' <summary>打开插件主页（索引站有详情页就优先用它）。</summary>
    Private Sub OpenRepo(p As DshPluginMarket.DshPluginInfo)
        If p Is Nothing Then Return
        Dim url As String = p.HtmlUrl
        '索引站源下 HtmlUrl 就是 GitHub 仓库地址 —— 那个更权威，优先；
        '没有仓库地址时退回索引站的详情页（总比打不开强）
        If String.IsNullOrWhiteSpace(url) Then url = p.PageUrl
        If String.IsNullOrWhiteSpace(url) Then Return

        Try
            OpenWebsite(url)
        Catch ex As Exception
            Logger.Error(ex, "DSH：打开插件仓库失败")
            ClipboardSet(url, False)
            Hint("无法打开浏览器，地址已复制到剪贴板。", HintType.Red)
        End Try
    End Sub

#End Region

#Region "已安装插件"

    ''' <summary>刷新「已安装」列表。</summary>
    Private Sub RefreshInstalledList()
        Try
            PanInstalled.Children.Clear()

            Dim inst As DshInstance = GetTargetInstance()
            If inst Is Nothing Then
                PanInstalled.Children.Add(MakeInfoText("没有可用实例。"))
                Return
            End If

            Dim installed As Dictionary(Of String, String) = DshPluginMarket.ListInstalledPlugins(inst)

            'profile 目录还没建出来是正常情况 —— 说明这个实例还没启动过
            If Not DshRuntime.FileExistsSafe(DshPluginMarket.ProfilePackageJsonOf(inst)) Then
                PanInstalled.Children.Add(MakeInfoText(
                    $"实例「{inst.DisplayName}」还没有初始化 profile 目录，因此没有已安装的插件。" & vbCrLf &
                    "先启动一次这个实例，profile 会自动初始化。"))
                Return
            End If

            If installed.Count = 0 Then
                PanInstalled.Children.Add(MakeInfoText($"实例「{inst.DisplayName}」当前没有安装任何插件。"))
                Return
            End If

            For Each kv In installed
                PanInstalled.Children.Add(BuildInstalledRow(kv.Key, kv.Value, inst))
            Next
        Catch ex As Exception
            Logger.Error(ex, "DSH：刷新已安装插件列表失败")
        End Try
    End Sub

    Private Function MakeInfoText(Text As String) As FrameworkElement
        Dim tb As New TextBlock With {
            .FontSize = 12.5,
            .TextWrapping = TextWrapping.Wrap,
            .LineHeight = 20,
            .Margin = New Thickness(0, 0, 0, 6),
            .Text = Text
        }
        tb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4")
        Return tb
    End Function

    Private Function BuildInstalledRow(Name As String, Version As String,
                                       inst As DshInstance) As FrameworkElement
        Dim row As New Border With {
            .BorderThickness = New Thickness(1),
            .CornerRadius = New CornerRadius(4),
            .Padding = New Thickness(13, 8, 13, 8),
            .Margin = New Thickness(0, 0, 0, 6)
        }
        row.SetResourceReference(Border.BorderBrushProperty, "ColorBrushGray5")

        Dim grid As New Grid()
        grid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
        grid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

        Dim nameText As New TextBlock With {
            .FontSize = 13,
            .Text = Name,
            .TextTrimming = TextTrimming.CharacterEllipsis
        }
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        Grid.SetColumn(nameText, 0)
        grid.Children.Add(nameText)

        Dim rightPanel As New StackPanel With {
            .Orientation = Orientation.Horizontal,
            .VerticalAlignment = VerticalAlignment.Center,
            .Margin = New Thickness(12, 0, 0, 0)
        }

        Dim verText As New TextBlock With {
            .FontSize = 12,
            .Text = Version,
            .VerticalAlignment = VerticalAlignment.Center,
            .Margin = New Thickness(0, 0, 10, 0)
        }
        verText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
        rightPanel.Children.Add(verText)

        Dim btnRemove As New MyButton With {.Text = "卸载", .Height = 26}
        btnRemove.TextPadding = New Thickness(10, 0, 10, 0)
        Dim capturedName As String = Name
        Dim capturedInst As DshInstance = inst
        AddHandler btnRemove.Click, Sub(s, e) UninstallPluginByName(capturedName, capturedInst)
        rightPanel.Children.Add(btnRemove)

        Grid.SetColumn(rightPanel, 1)
        grid.Children.Add(rightPanel)

        row.Child = grid
        Return row
    End Function

    ''' <summary>取「包名 → 版本」字典（用于判断某个插件装没装）。</summary>
    Private Function GetInstalledMap() As Dictionary(Of String, String)
        Try
            Return DshPluginMarket.ListInstalledPlugins(GetTargetInstance())
        Catch ex As Exception
            Logger.Warn($"DSH：读取已安装插件失败（可忽略）：{ex.Message}")
            Return New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        End Try
    End Function

#End Region

#Region "安装 / 卸载"

    ''' <summary>安装市场里的一个插件。</summary>
    Private Sub InstallPlugin(p As DshPluginMarket.DshPluginInfo)
        If IsBusy OrElse p Is Nothing Then Return

        Dim inst As DshInstance = GetTargetInstance()
        If inst Is Nothing Then
            Hint("没有可用实例，无法安装插件。", HintType.Red)
            Return
        End If

        '── 先解析安装计划，再让用户确认 ──
        '⚠️ 顺序很重要：把「这个仓库到底是不是 dsh 插件」放在确认**之前**，
        '   用户才有机会在看到警告后取消，而不是装完了才发现白装。
        '   而且这一步还要定下 spec —— 不能直接把 package.json 里的 name 当 npm 包名，
        '   市场里混着大量没发布到 npm 的普通仓库，那样做只会得到一个
        '   看不懂的 ERR_PNPM_...RESOLVE_LATEST 报错。
        SetBusy(True, $"正在解析 {p.FullName} 的包信息……")
        RunInNewThread(
            Sub()
                Dim result As DshPluginMarket.PluginInstallPlan = Nothing
                Dim err As String = Nothing
                Try
                    result = DshPluginMarket.PlanPluginInstall(p)
                Catch ex As Exception
                    err = ex.Message
                    Logger.Error(ex, $"DSH：解析插件 {p.FullName} 失败")
                End Try

                RunInUi(
                    Sub()
                        SetBusy(False)
                        If result Is Nothing OrElse String.IsNullOrWhiteSpace(result.Spec) Then
                            Hint($"无法解析「{p.FullName}」：{If(err, "没有可用的安装方式")}", HintType.Red)
                            Return
                        End If
                        ConfirmAndInstall(p, inst, result)
                    End Sub)
            End Sub, "DSH 解析插件")
    End Sub

    ''' <summary>弹出确认框，确认后真正安装。</summary>
    Private Sub ConfirmAndInstall(p As DshPluginMarket.DshPluginInfo,
                                  inst As DshInstance,
                                  plan As DshPluginMarket.PluginInstallPlan)
        Dim body As New StringBuilder()
        body.AppendLine($"把「{p.FullName}」安装到实例「{inst.DisplayName}」？")
        body.AppendLine()
        body.AppendLine($"安装方式：{plan.SourceText}")
        body.AppendLine($"安装标识：{plan.Spec}")
        body.AppendLine($"插件目录：{DshPluginMarket.ProfileDirOf(inst)}")

        If Not plan.IsDshPlugin Then
            body.AppendLine()
            body.AppendLine("⚠ 注意：这个仓库的 package.json 里没有 dsh 字段，")
            body.AppendLine("   它很可能不是 dsh 插件 —— GitHub 的 topic 搜索比较宽松，")
            body.AppendLine("   会把只是提到 dsh 的项目也收进来。")
            body.AppendLine("   装了不会让 dsh 出问题，但也不会带来新功能。")
        End If

        body.AppendLine()
        body.Append("安装完成后需要重启该实例的服务，插件才会被加载。")

        If MyMsgBox(body.ToString(), "安装插件", "开始安装", "取消",
                    IsWarn:=Not plan.IsDshPlugin) = 2 Then
            Return
        End If

        InstallingSpec = p.Key
        ApplyFilter()

        RunInNewThread(
            Sub()
                Dim err As String = Nothing
                Try
                    RunInUi(Sub() SetBusy(True, $"正在安装 {plan.Spec}……"))

                    DshPluginMarket.InstallPlugin(
                        inst, plan.Spec,
                        Sub(stage As String, pct As Double)
                            RunInUi(
                                Sub()
                                    Try
                                        LabLoadHint.Text = If(pct >= 0, $"{stage}（{CInt(pct * 100)}%）", stage)
                                    Catch ex As Exception
                                        Logger.Warn($"DSH：更新插件安装进度失败（可忽略）：{ex.Message}")
                                    End Try
                                End Sub)
                        End Sub)
                Catch ex As Exception
                    err = ex.Message
                    Logger.Error(ex, $"DSH：安装插件 {p.FullName} 失败")
                End Try

                RunInUi(
                    Sub()
                        SetBusy(False)
                        InstallingSpec = Nothing
                        RefreshInstalledList()
                        ApplyFilter()

                        If err IsNot Nothing Then
                            '把 pnpm 的原始报错翻译成人话，翻译不出来就显示原文
                            Dim friendly As String = DshPluginMarket.TranslatePnpmError(err)
                            Hint($"安装失败：{If(friendly, err)}", HintType.Red)
                            If friendly IsNot Nothing Then
                                MyMsgBox(
                                    $"安装「{p.FullName}」失败。" & vbCrLf & vbCrLf & friendly & vbCrLf & vbCrLf &
                                    "—— 原始输出 ——" & vbCrLf & err,
                                    "安装失败", "知道了", IsWarn:=True)
                            End If
                            Return
                        End If

                        Hint($"「{p.FullName}」安装完成。重启该实例的服务后生效。", HintType.Green)
                    End Sub)
            End Sub, "DSH 安装插件")
    End Sub

    ''' <summary>按包名卸载。</summary>
    Private Sub UninstallPluginByName(PackageName As String, inst As DshInstance)
        If IsBusy Then Return
        If inst Is Nothing OrElse String.IsNullOrWhiteSpace(PackageName) Then Return

        If MyMsgBox(
            $"从实例「{inst.DisplayName}」卸载「{PackageName}」？{vbCrLf}{vbCrLf}" &
            "卸载完成后需要重启该实例的服务才会生效。",
            "卸载插件", "卸载", "取消", IsWarn:=True) = 2 Then
            Return
        End If

        SetBusy(True, $"正在卸载 {PackageName}……")

        RunInNewThread(
            Sub()
                Dim err As String = Nothing
                Try
                    DshPluginMarket.UninstallPlugin(
                        inst, PackageName,
                        Sub(stage As String, pct As Double)
                            RunInUi(
                                Sub()
                                    Try
                                        LabLoadHint.Text = If(pct >= 0, $"{stage}（{CInt(pct * 100)}%）", stage)
                                    Catch ex As Exception
                                        Logger.Warn($"DSH：更新卸载进度失败（可忽略）：{ex.Message}")
                                    End Try
                                End Sub)
                        End Sub)
                Catch ex As Exception
                    err = ex.Message
                    Logger.Error(ex, $"DSH：卸载插件 {PackageName} 失败")
                End Try

                RunInUi(
                    Sub()
                        SetBusy(False)
                        RefreshInstalledList()
                        ApplyFilter()
                        If err Is Nothing Then
                            Hint($"「{PackageName}」已卸载。重启该实例的服务后生效。", HintType.Green)
                        Else
                            Hint($"卸载失败：{err}", HintType.Red)
                        End If
                    End Sub)
            End Sub, "DSH 卸载插件")
    End Sub

#End Region

#Region "界面辅助"

    Private Sub SetBusy(Busy As Boolean, Optional Hint As String = Nothing)
        IsBusy = Busy
        Try
            PanLoad.Visibility = Busy.ToVisibility
            If Busy Then
                LoadStart.State.LoadingState = MyLoading.MyLoadingState.Run
                If Hint IsNot Nothing Then LabLoadHint.Text = Hint
            Else
                LoadStart.State.LoadingState = MyLoading.MyLoadingState.Stop
            End If

            BtnReload.IsEnabled = Not Busy
            BtnUseCache.IsEnabled = Not Busy
            BtnImport.IsEnabled = Not Busy
            BtnOpenRepo.IsEnabled = Not Busy
            BtnRefreshInstalled.IsEnabled = Not Busy
            ComboInstance.IsEnabled = Not Busy
            ComboCategory.IsEnabled = Not Busy
            ComboSort.IsEnabled = Not Busy
            TextSearch.IsEnabled = Not Busy
        Catch ex As Exception
            Logger.Error(ex, "DSH：切换忙碌状态失败")
        End Try
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        'F5 刷新走缓存 —— 不该因为用户按了 F5 就烧掉一次匿名配额
        RefreshInstalledList()
        LoadPlugins(ForceRefresh:=False)
    End Sub

#End Region

#Region "按钮事件"

    ''' <summary>强制联网刷新插件列表。</summary>
    Private Sub BtnReload_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnReload.Click
        If IsBusy Then Return

        ' 确认文案要跟着数据源走：索引站无配额，套用 GitHub 的限流警告是误导
        Dim tip As String
        If DshPluginMarket.DshPluginUseCdnSource Then
            tip = "刷新会从插件索引站重新获取一次完整列表（约 4 MB）。" & vbCrLf & vbCrLf &
                  "该数据源是静态文件，没有请求次数限制，可以放心刷新。"
        Else
            tip = "刷新会向 GitHub 发起一次真实请求。" & vbCrLf & vbCrLf &
                  "GitHub 的匿名搜索接口限额为每小时 10 次，缓存有效期 " &
                  $"{DshPluginMarket.DshPluginCacheTtlText}。" & vbCrLf &
                  "如果只是想看看上次的结果，用「仅读缓存」就够了。"
        End If

        If MyMsgBox(tip, "刷新插件列表", "继续刷新", "取消") = 2 Then
            Return
        End If

        LoadPlugins(ForceRefresh:=True)
    End Sub

    ''' <summary>只读缓存。</summary>
    Private Sub BtnUseCache_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnUseCache.Click
        If IsBusy Then Return

        Dim cached = DshPluginMarket.ReadCache()
        If cached Is Nothing OrElse cached.Items.Count = 0 Then
            Hint("本地还没有缓存。请先点一次「刷新插件列表」。", HintType.Red)
            Return
        End If

        AbsorbResult(cached)
        Hint($"已显示缓存结果（{cached.CacheAgeText}，共 {cached.Items.Count} 项）。", HintType.Green)
    End Sub

    ''' <summary>在浏览器里打开插件市场的来源页（跟着数据源走）。</summary>
    Private Sub BtnOpenRepo_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnOpenRepo.Click
        Try
            Dim url As String = If(DshPluginMarket.DshPluginUseCdnSource,
                                   DshPluginMarket.DshPluginIndexSite,
                                   $"https://github.com/topics/{DshPluginMarket.DshPluginTopic}")
            OpenWebsite(url)
        Catch ex As Exception
            Logger.Error(ex, "DSH：打开插件市场页面失败")
            Hint($"无法打开浏览器：{ex.Message}", HintType.Red)
        End Try
    End Sub

    ''' <summary>刷新已安装列表。</summary>
    Private Sub BtnRefreshInstalled_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnRefreshInstalled.Click
        RefreshInstalledList()
        Hint("已安装插件列表已刷新。", HintType.Green)
    End Sub

#End Region

#Region "手动导入（决策 4-d / 4-e）"

    ''' <summary>
    ''' 「手动导入」入口 —— 给不在市场里的插件一条路。
    ''' </summary>
    ''' <remarks>
    ''' 三条路径（决策 4-d）：
    '''   ① GitHub 链接 / owner/repo
    '''   ② 本地文件夹（含 tarball）
    '''   ③ 从剪贴板自动识别
    ''' 导入前一律先探测它是不是 dsh 插件；不是就走「警告式确认框」（决策 4-e），
    ''' 用户执意要装就继续装 —— 与市场里的非 dsh 插件一个待遇。
    ''' </remarks>
    Private Sub BtnImport_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnImport.Click
        If IsBusy Then Return

        Dim inst As DshInstance = GetTargetInstance()
        If inst Is Nothing Then
            Hint("没有可用实例，无法导入插件。", HintType.Red)
            Return
        End If

        '先给个说明 + 快捷入口，再让用户输入
        Dim choice As Integer = MyMsgBox(
            "可以直接粘贴下面任意一种：" & vbCrLf & vbCrLf &
            "  · GitHub 链接    https://github.com/作者/仓库" & vbCrLf &
            "  · 简写            作者/仓库" & vbCrLf &
            "  · npm 包名      @scope/pkg 或 pkg@1.2.3" & vbCrLf &
            "  · 本地文件夹    D:\我的插件  或  \\网络路径\插件" & vbCrLf & vbCrLf &
            "程序会自动判断类型，并在安装前检查它是不是 dsh 插件。",
            "手动导入插件", "从剪贴板识别", "手动输入", "取消")
        If choice = 3 Then Return
        If choice <> 1 AndAlso choice <> 2 Then Return

        Dim spec As String = Nothing

        '── ① 从剪贴板识别 ──
        If choice = 1 Then
            Dim clip As String = Nothing
            Try
                If System.Windows.Clipboard.ContainsText() Then clip = System.Windows.Clipboard.GetText()
            Catch ex As Exception
                Logger.Warn($"DSH：读取剪贴板失败（可忽略）：{ex.Message}")
            End Try

            If String.IsNullOrWhiteSpace(clip) Then
                Hint("剪贴板里没有文本。", HintType.Red)
                Return
            End If

            spec = DshPluginMarket.DetectImportSpecFromClipboard(clip)
            If spec Is Nothing Then
                Hint("无法从剪贴板内容里识别出插件标识。", HintType.Red)
                MyMsgBox(
                    "剪贴板里的内容无法识别为插件标识。" & vbCrLf & vbCrLf &
                    "支持 GitHub 链接、owner/repo、npm 包名、本地路径。" & vbCrLf & vbCrLf &
                    "—— 剪贴板原文（前 300 字）——" & vbCrLf &
                    If(clip.Length > 300, clip.Substring(0, 300) & "……", clip),
                    "无法识别", "知道了", IsWarn:=True)
                Return
            End If
            ConfirmAndImport(inst, spec, True)
            Return
        End If

        '── ② 手动输入 ──
        Dim input As String = MyMsgBoxInput(
            "导入插件",
            "粘贴 GitHub 链接、npm 包名，或本地文件夹路径：",
            "",
            HintText:="例如 https://github.com/作者/仓库 或 @scope/pkg")
        '取消时返回 Nothing
        If input Is Nothing Then Return
        spec = input.Trim()
        If spec = "" Then Return
        ConfirmAndImport(inst, spec, False)
    End Sub

    ''' <summary>解析 spec → 弹确认 → 安装。</summary>
    Private Sub ConfirmAndImport(inst As DshInstance, Spec As String, FromClipboard As Boolean)
        SetBusy(True, $"正在解析 {Spec}……")
        RunInNewThread(
            Sub()
                Dim plan As DshPluginMarket.PluginInstallPlan = Nothing
                Dim err As String = Nothing
                Try
                    plan = DshPluginMarket.PlanManualImport(Spec)
                Catch ex As Exception
                    err = ex.Message
                    Logger.Error(ex, $"DSH：解析导入项 {Spec} 失败")
                End Try

                RunInUi(
                    Sub()
                        SetBusy(False)
                        If plan Is Nothing OrElse String.IsNullOrWhiteSpace(plan.Spec) Then
                            Dim why As String = If(plan IsNot Nothing AndAlso plan.Warning IsNot Nothing,
                                                   plan.Warning, If(err, "无法识别这个标识"))
                            Hint($"无法导入：{why}", HintType.Red)
                            MyMsgBox($"{why}{vbCrLf}{vbCrLf}输入的标识：{Spec}", "导入失败", "知道了", IsWarn:=True)
                            Return
                        End If

                        Dim body As New StringBuilder()
                        body.AppendLine($"把这一项安装到实例「{inst.DisplayName}」？")
                        body.AppendLine()
                        body.AppendLine($"来源：{plan.SourceText}")
                        If FromClipboard Then body.AppendLine("（识别自剪贴板）")
                        body.AppendLine($"安装标识：{plan.Spec}")
                        If Not String.IsNullOrWhiteSpace(plan.PackageName) Then
                            body.AppendLine($"包名：{plan.PackageName}")
                        End If
                        body.AppendLine($"插件目录：{DshPluginMarket.ProfileDirOf(inst)}")

                        '非 dsh 插件 → 警告式确认（决策 4-e）
                        If Not plan.IsDshPlugin Then
                            body.AppendLine()
                            body.AppendLine("⚠ 注意：无法确认它是 dsh 插件。")
                            If plan.Warning IsNot Nothing Then
                                body.AppendLine("   " & plan.Warning)
                            End If
                            body.AppendLine("   装了不会让 dsh 出问题，但可能不会带来新功能。")
                        End If

                        body.AppendLine()
                        body.Append("安装完成后需要重启该实例的服务，插件才会被加载。")

                        If MyMsgBox(body.ToString(), "导入插件", "开始安装", "取消",
                                    IsWarn:=Not plan.IsDshPlugin) = 2 Then
                            Return
                        End If

                        RunImportInstall(inst, plan)
                    End Sub)
            End Sub, "DSH 解析导入项")
    End Sub

    ''' <summary>真正执行导入安装。</summary>
    Private Sub RunImportInstall(inst As DshInstance, plan As DshPluginMarket.PluginInstallPlan)
        SetBusy(True, $"正在安装 {plan.Spec}……")

        RunInNewThread(
            Sub()
                Dim err As String = Nothing
                Try
                    DshPluginMarket.InstallPlugin(
                        inst, plan.Spec,
                        Sub(stage As String, pct As Double)
                            RunInUi(
                                Sub()
                                    Try
                                        LabLoadHint.Text = If(pct >= 0, $"{stage}（{CInt(pct * 100)}%）", stage)
                                    Catch ex As Exception
                                        Logger.Warn($"DSH：更新导入进度失败（可忽略）：{ex.Message}")
                                    End Try
                                End Sub)
                        End Sub)
                Catch ex As Exception
                    err = ex.Message
                    Logger.Error(ex, $"DSH：导入安装 {plan.Spec} 失败")
                End Try

                RunInUi(
                    Sub()
                        SetBusy(False)
                        RefreshInstalledList()
                        ApplyFilter()

                        If err IsNot Nothing Then
                            Dim friendly As String = DshPluginMarket.TranslatePnpmError(err)
                            Hint($"导入失败：{If(friendly, err)}", HintType.Red)
                            If friendly IsNot Nothing Then
                                MyMsgBox(
                                    $"安装「{plan.Spec}」失败。" & vbCrLf & vbCrLf & friendly & vbCrLf & vbCrLf &
                                    "—— 原始输出 ——" & vbCrLf & err,
                                    "导入失败", "知道了", IsWarn:=True)
                            End If
                            Return
                        End If

                        Hint($"「{plan.Spec}」导入完成。重启该实例的服务后生效。", HintType.Green)
                    End Sub)
            End Sub, "DSH 导入安装")
    End Sub

#End Region

End Class
