Imports System.Windows.Media

''' <summary>
''' 镜像版本页 —— 查看 / 安装 / 切换 dsh 版本，并选择 npm 镜像源。
'''
''' 数据来自 <see cref="DshRegistry"/>（npm 注册表）。这个接口不需要认证、
''' 也没有配额限制，所以**不做缓存**：点刷新就真的重新拉一次。
''' 这一点和插件社区页（GitHub 搜索接口，10 次/小时）刚好相反。
'''
''' 安装走 <see cref="DshInstaller.InstallDshVersion"/>（底层是 <c>pnpm add</c>），
''' 是个可能耗时十几分钟的长事务，必须放后台线程，并且期间禁用所有按钮。
''' </summary>
Public Class PageDownloadMirror
    Implements IRefreshable

#Region "状态"

    Private IsLoad As Boolean = False

    ''' <summary>联网 / 安装期间为 True，用于禁用按钮并显示加载卡片。</summary>
    Private IsBusy As Boolean = False

    ''' <summary>当前展示的版本列表。</summary>
    Private Versions As New List(Of DshRegistry.DshVersionInfo)

    ''' <summary>程序化改写镜像下拉框时置位。</summary>
    Private IsUpdatingMirror As Boolean = False

    ''' <summary>
    ''' 上次刷新进度明细的时间戳（用于**节流**）。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 为什么必须节流：复制运行时会有 39 万个文件，每个都回调一次 ——
    ''' 不做节流的话，UI 线程每秒要处理几千次 Dispatcher 调用，
    ''' 界面会卡到无法响应（而且那些文字根本来不及看）。
    ''' 实测：不节流时界面几乎冻结。
    ''' </remarks>
    Private LastProgressTick As Long = 0

    ''' <summary>进度明细的最小刷新间隔（毫秒）。</summary>
    Private Const ProgressThrottleMs As Integer = 120

#End Region

#Region "初始化"

    Private Sub PageDownloadMirror_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        If Not IsLoad Then
            IsLoad = True
            BuildMirrorCombo()
        End If
        RefreshChannelNote()
        RefreshInstalledInfo()
        RefreshSlots()
    End Sub

    ''' <summary>页面重入时刷新（切走再切回来不一定重新触发 Loaded）。</summary>
    Public Sub PageOnEnterHook() Handles Me.PageEnter
        RefreshChannelNote()
        RefreshInstalledInfo()
        RefreshSlots()
    End Sub

#End Region

#Region "镜像源"

    ''' <summary>填充镜像源下拉框。</summary>
    Private Sub BuildMirrorCombo()
        Try
            IsUpdatingMirror = True
            Try
                ComboMirror.Items.Clear()
                For Each mirror In DshRegistry.DshNpmMirrors
                    ComboMirror.Items.Add(New MyComboBoxItem With {
                        .Content = mirror.Name,
                        .Tag = mirror.Url
                    })
                Next

                Dim idx As Integer = DshRegistry.DshCurrentMirrorIndex
                If idx >= 0 AndAlso idx < ComboMirror.Items.Count Then
                    ComboMirror.SelectedItem = ComboMirror.Items(idx)
                End If
            Finally
                IsUpdatingMirror = False
            End Try
            RefreshMirrorNote()
        Catch ex As Exception
            Logger.Error(ex, "DSH：填充镜像源下拉框失败")
        End Try
    End Sub

    ''' <summary>
    ''' 用户切换了更新通道 —— 刷新提示文案，并让「安装最新版本」按钮的目标变清楚。
    ''' </summary>
    ''' <remarks>
    ''' 设置值的持久化由 XAML 上的 <c>SettingService.Key</c> 声明式绑定完成，
    ''' 这里只做展示层面的同步。挂 <c>Check</c> 事件是 PCL 的 <c>MyRadioBox</c> 约定
    ''' （见 <c>PageSetupUI.xaml.vb</c> 里 <c>RadioDshBrowser_Change</c> 的同类写法）。
    ''' </remarks>
    Private Sub RadioDshChannel_Change() Handles RadioDshChannelStable.Check, RadioDshChannelPreview.Check
        RefreshChannelNote()
    End Sub

    ''' <summary>把当前通道对应的说明写进提示文案。</summary>
    Private Sub RefreshChannelNote()
        Try
            If LabChannelNote Is Nothing Then Return
            Dim channel As ModDSH.DshChannel = ModDSH.DshCurrentChannel
            If channel = ModDSH.DshChannel.Preview Then
                LabChannelNote.Text =
                    "预览通道：跟版本号最大的版本，包含 alpha / beta / rc 等预发布版。" &
                    "想第一时间用上新功能选这个，但可能遇到上游的破坏性改动。"
            Else
                LabChannelNote.Text =
                    "稳定通道：只装 dist-tags.latest 指向的正式版本，不含预发布。" &
                    "下方列表里仍可以手动安装任意版本。"
            End If
        Catch ex As Exception
            Logger.Warn($"DSH：刷新更新通道说明失败（可忽略）：{ex.Message}")
        End Try
    End Sub

    Private Sub RefreshMirrorNote()
        Try
            LabMirrorNote.Text = DshRegistry.DshCurrentMirror.Note & vbCrLf &
                                 DshRegistry.DshCurrentMirror.Url
        Catch ex As Exception
            LabMirrorNote.Text = ""
        End Try
    End Sub

    ''' <summary>用户切换了镜像源。</summary>
    ''' <remarks>
    ''' ⚠️ 必须挂 <c>SelectionChanged</c>，**不能**挂 <c>TextChanged</c>。
    ''' <see cref="MyComboBox"/> 的 <c>TextChanged</c> 只在 <c>IsEditable</c> 为真时
    ''' 才从内部文本框转发出来；本页的下拉框是不可编辑的，
    ''' 挂 <c>TextChanged</c> 会**永远不触发** —— 表现就是"换了镜像源但没生效"。
    ''' </remarks>
    Private Sub ComboMirror_SelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboMirror.SelectionChanged
        If IsUpdatingMirror Then Return

        Try
            Dim item = TryCast(ComboMirror.SelectedItem, MyComboBoxItem)
            If item Is Nothing Then Return

            '按 URL 反查索引，避免依赖下拉框顺序
            Dim url As String = CStr(item.Tag)
            Dim mirrors = DshRegistry.DshNpmMirrors
            For i As Integer = 0 To mirrors.Count - 1
                If String.Equals(mirrors(i).Url, url, StringComparison.OrdinalIgnoreCase) Then
                    If DshRegistry.DshCurrentMirrorIndex = i Then Return
                    DshRegistry.DshCurrentMirrorIndex = i
                    RefreshMirrorNote()
                    Hint($"已切换到 {mirrors(i).Name}，重新安装一次即可生效。", HintType.Green)
                    Return
                End If
            Next
        Catch ex As Exception
            Logger.Error(ex, "DSH：切换镜像源失败")
        End Try
    End Sub

#End Region

#Region "已安装版本"

    ''' <summary>刷新「已安装版本」那一行。</summary>
    Private Sub RefreshInstalledInfo()
        Try
            Dim installed As String = DshRegistry.DshInstalledVersion
            Dim channel As ModDSH.DshChannel = ModDSH.DshCurrentChannel
            If String.IsNullOrWhiteSpace(installed) Then
                LabInstalledVersion.Text = "未安装 dsh"
                LabInstalledVersion.Foreground = TryFindResource("ColorBrushGray1")
            Else
                '按当前通道算"该装哪个"，装对了才标绿 —— 只跟锁定版本比对是没意义的，
                '因为用户完全可以合法地选择预览通道装上更新的预发布版
                Dim target As DshRegistry.DshVersionInfo =
                    DshRegistry.ResolveVersionForChannel(Versions, channel)
                Dim expected As String = If(target?.Version, ModDSH.DshVersion)

                LabInstalledVersion.Text = $"已安装 dsh {installed}"
                If String.Equals(installed, expected, StringComparison.OrdinalIgnoreCase) Then
                    LabInstalledVersion.Foreground = New SolidColorBrush(Color.FromRgb(&H2E, &HA0, &H55))
                Else
                    LabInstalledVersion.Foreground = TryFindResource("ColorBrushGray1")
                End If
            End If

            Dim parts As New List(Of String)
            parts.Add($"当前更新通道：{ModDSH.DshChannelName(channel)}")
            parts.Add($"安装目录：{ModDSH.DshInstallDir}")
            If Versions.Count > 0 Then
                Dim newest = Versions.FirstOrDefault(Function(v) v.IsNewest)
                Dim stable = Versions.FirstOrDefault(Function(v) v.IsLatest)
                If newest IsNot Nothing Then
                    parts.Add($"镜像源最新：{newest.Version}（{newest.PublishedText}）{If(newest.IsPrerelease, " · 预发布", "")}")
                End If
                '「稳定版」与「最新」不是同一个版本时才单独提一句 —— 否则是冗余信息
                If stable IsNot Nothing AndAlso newest IsNot Nothing AndAlso
                   Not String.Equals(stable.Version, newest.Version, StringComparison.OrdinalIgnoreCase) Then
                    parts.Add($"镜像源稳定版：{stable.Version}（{stable.PublishedText}）")
                End If

                '选中的源不通时会自动换源，这里把实际用上的那个显示出来
                Dim used As String = DshRegistry.DshLastFetchMirrorName
                If Not String.IsNullOrWhiteSpace(used) AndAlso
                   Not String.Equals(used, DshRegistry.DshCurrentMirror.Name, StringComparison.Ordinal) Then
                    parts.Add($"⚠ 选中的「{DshRegistry.DshCurrentMirror.Name}」连接失败，本次数据来自「{used}」")
                End If
            End If
            LabVersionDetail.Text = String.Join(vbCrLf, parts)

            RefreshUpdateState(installed, channel)
        Catch ex As Exception
            Logger.Error(ex, "DSH：刷新已安装版本信息失败")
        End Try
    End Sub

    ''' <summary>
    ''' 刷新那行**显眼的更新状态** —— 明确告诉用户"该不该更新"。
    ''' </summary>
    ''' <param name="Installed">当前已安装的版本号（可能为空）。</param>
    ''' <param name="Channel">当前更新通道。</param>
    ''' <remarks>
    ''' ⭐ 为什么需要它（用户反馈）：
    ''' 原来「检查更新」只是把版本号塞进明细文字里（"镜像源最新：0.1.7-alpha.1"），
    ''' 用户点完看不出**重点** —— 不知道"这是不是比我的新""我要不要升"。
    ''' 点按钮的期待是"有没有新版"，而不是看两个版本号。
    '''
    ''' 所以这里用一行带底色的小卡片明确回答，并给出可操作的建议。
    ''' </remarks>
    Private Sub RefreshUpdateState(Installed As String, Channel As ModDSH.DshChannel)
        Try
            If PanUpdateState Is Nothing OrElse LabUpdateState Is Nothing Then Return

            ' 还没拉过版本列表 —— 不显示状态（避免误导）
            If Versions Is Nothing OrElse Versions.Count = 0 Then
                PanUpdateState.Visibility = Visibility.Collapsed
                Return
            End If

            Dim target As DshRegistry.DshVersionInfo = DshRegistry.ResolveVersionForChannel(Versions, Channel)
            If target Is Nothing Then
                PanUpdateState.Visibility = Visibility.Collapsed
                Return
            End If

            If String.IsNullOrWhiteSpace(Installed) Then
                ' 一个都没装
                ShowUpdateState(
                    $"未安装 dsh —— 建议安装 {target.Version}",
                    Color.FromRgb(&HD9, &HEC, &HFF), Color.FromRgb(&H1B, &H4F, &H8A))
                Return
            End If

            ' 比较：用 semver 解析而不是字符串比对（"0.1.10" > "0.1.9" 但字符串是反的）
            Dim cmp As Integer = DshRegistry.CompareSemver(target.Version, Installed)
            If cmp > 0 Then
                ' 有新版本
                Dim extra As String = If(target.IsPrerelease, "（预发布）", "")
                ShowUpdateState(
                    $"发现新版本：{target.Version}{extra} —— 当前是 {Installed}，建议更新",
                    Color.FromRgb(&HFF, &HF4, &HD6), Color.FromRgb(&H8A, &H5A, &H00))
            ElseIf cmp = 0 Then
                ShowUpdateState(
                    $"已是最新版本（{Installed}）",
                    Color.FromRgb(&HDC, &HF5, &HE4), Color.FromRgb(&H1B, &H6B, &H3A))
            Else
                ' 当前版本比通道目标还新（比如手动装了预览版，但通道是稳定）
                ShowUpdateState(
                    $"当前版本（{Installed}）比通道目标（{target.Version}）更新 —— 无需操作",
                    Color.FromRgb(&HDC, &HF5, &HE4), Color.FromRgb(&H1B, &H6B, &H3A))
            End If
        Catch ex As Exception
            Logger.Warn(ex, "DSH：刷新更新状态失败（可忽略）")
            Try
                PanUpdateState.Visibility = Visibility.Collapsed
            Catch
            End Try
        End Try
    End Sub

    ''' <summary>显示更新状态那一行。</summary>
    Private Sub ShowUpdateState(Text As String, BackColor As Color, ForeColor As Color)
        Try
            LabUpdateState.Text = Text
            LabUpdateState.Foreground = New SolidColorBrush(ForeColor)
            PanUpdateState.Background = New SolidColorBrush(BackColor)
            PanUpdateState.Visibility = Visibility.Visible
        Catch ex As Exception
            Logger.Warn(ex, "DSH：显示更新状态失败（可忽略）")
        End Try
    End Sub

#End Region

#Region "版本列表"

    ''' <summary>
    ''' 拉取（或复用）版本列表。
    ''' </summary>
    ''' <param name="ForceRefresh">True = 真的重新联网。</param>
    Public Sub Reload(Optional ForceRefresh As Boolean = True)
        If IsBusy Then
            Hint("正在处理中，请稍候……")
            Return
        End If

        If Not ForceRefresh AndAlso Versions.Count > 0 Then
            RenderVersions()
            Return
        End If

        SetBusy(True, "正在从镜像源获取版本信息……")

        '联网是阻塞操作，必须放后台线程
        RunInNewThread(
            Sub()
                Dim list As List(Of DshRegistry.DshVersionInfo) = Nothing
                Dim err As String = Nothing
                Try
                    list = DshRegistry.FetchVersions()
                Catch ex As Exception
                    err = ex.Message
                    Logger.Error(ex, "DSH：获取版本列表失败")
                End Try

                RunInUi(
                    Sub()
                        SetBusy(False)
                        If list Is Nothing Then
                            '错误信息可能有多行（逐个镜像源的失败原因），
                            'Hint 显示不下，放进卡片详情里，Hint 只做提醒
                            LabVersionDetail.Text = If(err, "获取失败，原因未知。")
                            Hint("获取版本列表失败，详情见上方「dsh 版本」卡片。", HintType.Red)
                            Return
                        End If
                        Versions = list
                        RenderVersions()
                        RefreshInstalledInfo()
                        Hint($"已获取 {list.Count} 个版本。", HintType.Green)
                    End Sub)
            End Sub, "DSH 获取版本列表")
    End Sub

    ''' <summary>把版本列表渲染到界面上。</summary>
    Private Sub RenderVersions()
        Try
            PanVersions.Children.Clear()

            If Versions.Count = 0 Then
                LabVersionsEmpty.Visibility = Visibility.Visible
                Return
            End If
            LabVersionsEmpty.Visibility = Visibility.Collapsed

            Dim installed As String = DshRegistry.DshInstalledVersion
            For Each v In Versions
                PanVersions.Children.Add(BuildVersionRow(v, installed))
            Next
        Catch ex As Exception
            Logger.Error(ex, "DSH：渲染版本列表失败")
        End Try
    End Sub

    ''' <summary>构建一行版本。</summary>
    Private Function BuildVersionRow(v As DshRegistry.DshVersionInfo,
                                     installed As String) As FrameworkElement
        Dim isInstalled As Boolean =
            Not String.IsNullOrWhiteSpace(installed) AndAlso
            String.Equals(installed, v.Version, StringComparison.OrdinalIgnoreCase)

        Dim row As New Border With {
            .BorderThickness = New Thickness(1),
            .CornerRadius = New CornerRadius(4),
            .Padding = New Thickness(13, 10, 13, 10),
            .Margin = New Thickness(0, 0, 0, 8)
        }
        row.SetResourceReference(Border.BorderBrushProperty,
                                 If(isInstalled, "ColorBrush3", "ColorBrushGray5"))
        If isInstalled Then row.SetResourceReference(Border.BackgroundProperty, "ColorBrush8")

        Dim grid As New Grid()
        grid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
        grid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

        ' ── 左：版本号 + 徽标 + 日期 ──
        Dim left As New StackPanel()

        Dim titleRow As New StackPanel With {.Orientation = Orientation.Horizontal}
        Dim verText As New TextBlock With {
            .FontSize = 14,
            .FontWeight = FontWeights.Bold,
            .Text = v.Version,
            .VerticalAlignment = VerticalAlignment.Center
        }
        verText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        titleRow.Children.Add(verText)

        '徽标顺序：已安装 → 最新（版本号最大）→ 稳定版（dist-tags.latest）→ 预发布
        '「最新」与「稳定版」是两个不同的概念，可能不是同一个版本，所以都要标出来
        If isInstalled Then titleRow.Children.Add(MakeBadge("已安装", &H13, &H70, &HF3))
        If v.IsNewest Then titleRow.Children.Add(MakeBadge("最新", &H2E, &HA0, &H55))
        If v.IsLatest AndAlso Not v.IsNewest Then titleRow.Children.Add(MakeBadge("稳定版", &H48, &H90, &HF5))
        If v.IsPrerelease Then titleRow.Children.Add(MakeBadge("预发布", &HF0, &HA8, &H20))

        left.Children.Add(titleRow)

        Dim dateText As New TextBlock With {
            .FontSize = 12,
            .Margin = New Thickness(0, 4, 0, 0),
            .Text = $"发布于 {v.PublishedText}"
        }
        dateText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
        left.Children.Add(dateText)

        Grid.SetColumn(left, 0)
        grid.Children.Add(left)

        ' ── 右：安装按钮 ──
        Dim right As New StackPanel With {
            .Orientation = Orientation.Horizontal,
            .VerticalAlignment = VerticalAlignment.Center,
            .Margin = New Thickness(12, 0, 0, 0)
        }

        If isInstalled Then
            Dim btnCurrent As New MyButton With {.Text = "当前版本", .Height = 28, .IsEnabled = False}
            btnCurrent.TextPadding = New Thickness(12, 0, 12, 0)
            right.Children.Add(btnCurrent)
        Else
            Dim btnInstall As New MyButton With {.Text = "安装此版本", .Height = 28}
            btnInstall.TextPadding = New Thickness(12, 0, 12, 0)
            Dim captured As DshRegistry.DshVersionInfo = v
            AddHandler btnInstall.Click, Sub(s, e) InstallVersion(captured)
            right.Children.Add(btnInstall)
        End If

        Grid.SetColumn(right, 1)
        grid.Children.Add(right)

        row.Child = grid
        Return row
    End Function

    ''' <summary>生成一个小徽标（版本行里的「最新」「已安装」之类）。</summary>
    Private Function MakeBadge(Text As String, R As Byte, G As Byte, B As Byte) As FrameworkElement
        Dim border As New Border With {
            .CornerRadius = New CornerRadius(3),
            .Padding = New Thickness(6, 1, 6, 1),
            .Margin = New Thickness(8, 0, 0, 0),
            .VerticalAlignment = VerticalAlignment.Center,
            .Background = New SolidColorBrush(Color.FromRgb(R, G, B))
        }
        border.Child = New TextBlock With {
            .Text = Text,
            .FontSize = 11,
            .Foreground = Brushes.White
        }
        Return border
    End Function

#End Region

#Region "安装"

    ''' <summary>安装指定版本。</summary>
    Private Sub InstallVersion(v As DshRegistry.DshVersionInfo)
        If IsBusy Then Return
        If v Is Nothing Then Return

        '正在跑服务的实例会锁住 node_modules，先提示用户
        Dim running As List(Of DshInstance) =
            ModDSH.DshInstances.Where(Function(x) x.HasLiveProcess).ToList()
        If running.Count > 0 Then
            If MyMsgBox(
                $"以下实例正在运行：{String.Join("、", running.Select(Function(x) x.DisplayName))}{vbCrLf}{vbCrLf}" &
                "切换 dsh 版本会改写安装目录，正在运行的服务可能出错。" & vbCrLf &
                "建议先停止这些实例。是否仍要继续？",
                "有实例正在运行", "继续安装", "取消", IsWarn:=True) = 2 Then
                Return
            End If
        End If

        If MyMsgBox($"确定要安装 dsh {v.Version} 吗？" & vbCrLf & vbCrLf &
                    $"数据源：{DshRegistry.DshCurrentMirror.Name}" & vbCrLf &
                    "首次安装需要下载约 480 个包，可能耗时十几分钟。",
                    "安装 dsh " & v.Version, "开始安装", "取消") = 2 Then
            Return
        End If

        DoInstall(v.Version)
    End Sub

    ''' <summary>执行安装（后台线程）。</summary>
    Private Sub DoInstall(Version As String)
        SetBusy(True, $"正在安装 dsh {Version}……")

        RunInNewThread(
            Sub()
                Dim err As String = Nothing
                Try
                    DshInstaller.InstallDshVersion(
                        Version,
                        DshRegistry.DshCurrentMirror.Url,
                        Sub(stage As DshInstaller.DshInstallStage, text As String, pct As Double)
                            '进度回调可能在任意线程，切回 UI 再改控件。
                            '用统一的 UpdateProgressUI（带节流）—— pnpm 的输出刷得很快
                            RunInUi(Sub() UpdateProgressUI(stage.ToString(), text, pct))
                        End Sub)
                Catch ex As Exception
                    err = ex.Message
                    Logger.Error(ex, $"DSH：安装 dsh {Version} 失败")
                End Try

                RunInUi(
                    Sub()
                        SetBusy(False)
                        RefreshInstalledInfo()
                        RefreshSlots()
                        If err Is Nothing Then
                            ' ⭐ 刻意**不自动切换** —— 装完只新增一个槽位，当前激活的不变。
                            ' 为什么：插件是按 dsh 的内部 API 写的，新版 dsh 可能改了 API
                            ' 导致插件报错。用户需要能"退回去"，所以必须由他显式决定何时切。
                            ' 自动切过去等于替他做了这个有风险的决定。
                            Hint($"dsh {Version} 已安装。它已加入下方运行时列表，",
                                 HintType.Green)
                            Hint("需要时请手动点「切换」—— 不会自动启用。",
                                 HintType.Green)
                        Else
                            Hint($"安装失败：{err}", HintType.Red)
                        End If
                    End Sub)
            End Sub, "DSH 安装版本")
    End Sub

#End Region

#Region "运行时槽位"

    ''' <summary>重绘运行时槽位列表。</summary>
    ''' <remarks>
    ''' 这一块和上面的「可用版本」是两回事：
    '''   · 上面 = npm 上**可以装**的版本（远端列表）
    '''   · 这里 = 本机**已经有的**运行时（官方装的那份 + 导入的若干份）
    ''' 两者在界面上分开，是因为「装一个新版本」和「切换到已有的另一份」
    ''' 对用户来说是两个不同的动作，混在一张列表里会让人分不清
    ''' 「点这个按钮是下载还是切换」。
    ''' </remarks>
    Public Sub RefreshSlots()
        Try
            PanSlots.Children.Clear()

            Dim activeId As String = DshRuntimeSlot.DshSlotActiveId()
            Dim slots As List(Of DshRuntimeSlot.DshSlotInfo) = Nothing
            Try
                slots = DshRuntimeSlot.DshSlotList()
            Catch ex As Exception
                Logger.Error(ex, "DSH：枚举运行时槽位失败")
            End Try

            If slots Is Nothing OrElse slots.Count = 0 Then
                PanSlots.Children.Add(MakeSlotInfoText("还没有可用的运行时。先在上方安装一个官方版本，或者导入一个本地包。"))
                Return
            End If

            For Each s In slots
                PanSlots.Children.Add(BuildSlotRow(s, activeId))
            Next
        Catch ex As Exception
            Logger.Error(ex, "DSH：刷新运行时槽位列表失败")
        End Try
    End Sub

    Private Function MakeSlotInfoText(Text As String) As FrameworkElement
        Dim tb As New TextBlock With {
            .Text = Text,
            .FontSize = 12,
            .TextWrapping = TextWrapping.Wrap,
            .LineHeight = 19,
            .Margin = New Thickness(0, 2, 0, 2)
        }
        tb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
        Return tb
    End Function

    ''' <summary>构建一行运行时槽位。</summary>
    Private Function BuildSlotRow(s As DshRuntimeSlot.DshSlotInfo,
                                  activeId As String) As FrameworkElement
        Dim isActive As Boolean = String.Equals(s.Id, activeId, StringComparison.OrdinalIgnoreCase)

        Dim row As New Border With {
            .BorderThickness = New Thickness(1),
            .CornerRadius = New CornerRadius(4),
            .Padding = New Thickness(13, 10, 13, 10),
            .Margin = New Thickness(0, 0, 0, 8)
        }
        row.SetResourceReference(Border.BorderBrushProperty,
                                 If(isActive, "ColorBrush3", "ColorBrushGray5"))
        If isActive Then row.SetResourceReference(Border.BackgroundProperty, "ColorBrush8")

        Dim grid As New Grid()
        grid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
        grid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

        ' ── 左：名称 + 徽标 + 来路说明 ──
        Dim left As New StackPanel()

        Dim titleRow As New StackPanel With {.Orientation = Orientation.Horizontal}
        Dim nameText As New TextBlock With {
            .FontSize = 14,
            .FontWeight = FontWeights.Bold,
            .Text = s.DisplayName,
            .VerticalAlignment = VerticalAlignment.Center
        }
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        titleRow.Children.Add(nameText)

        If isActive Then titleRow.Children.Add(MakeBadge("使用中", &H13, &H70, &HF3))
        If s.IsImported Then titleRow.Children.Add(MakeBadge("本地导入", &H48, &H90, &HF5))
        If s.HasOwnNode Then titleRow.Children.Add(MakeBadge("自带 Node", &H2E, &HA0, &H55))
        If Not s.IsUsable Then titleRow.Children.Add(MakeBadge("不完整", &HDC, &H35, &H45))

        left.Children.Add(titleRow)

        Dim detail As New TextBlock With {
            .FontSize = 12,
            .Margin = New Thickness(0, 4, 0, 0),
            .TextWrapping = TextWrapping.Wrap,
            .Text = s.DetailText
        }
        detail.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
        left.Children.Add(detail)

        ' 导入的槽位把目录也写出来 —— 用户可能想自己去看看或备份
        If s.IsImported Then
            Dim pathText As New TextBlock With {
                .FontSize = 11.5,
                .Margin = New Thickness(0, 2, 0, 0),
                .TextWrapping = TextWrapping.Wrap,
                .Text = s.Dir
            }
            pathText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4")
            left.Children.Add(pathText)
        End If

        Grid.SetColumn(left, 0)
        grid.Children.Add(left)

        ' ── 右：切换 / 删除 ──
        Dim right As New StackPanel With {
            .Orientation = Orientation.Horizontal,
            .VerticalAlignment = VerticalAlignment.Center,
            .Margin = New Thickness(12, 0, 0, 0)
        }

        If isActive Then
            Dim btnCurrent As New MyButton With {.Text = "使用中", .Height = 28, .IsEnabled = False}
            btnCurrent.TextPadding = New Thickness(12, 0, 12, 0)
            right.Children.Add(btnCurrent)
        ElseIf Not s.IsUsable Then
            Dim btnBroken As New MyButton With {.Text = "不可用", .Height = 28, .IsEnabled = False}
            btnBroken.TextPadding = New Thickness(12, 0, 12, 0)
            right.Children.Add(btnBroken)
        Else
            Dim btnUse As New MyButton With {.Text = "切换到它", .Height = 28}
            btnUse.TextPadding = New Thickness(12, 0, 12, 0)
            Dim capturedId As String = s.Id
            AddHandler btnUse.Click, Sub(sender, e) SwitchToSlot(capturedId)
            right.Children.Add(btnUse)
        End If

        ' 可删的槽位：导入的 + **版本化**的（都不是 pnpm 管的）
        ' ⚠️ 旧单例（IsLegacyNpm）不给删 —— 它由 pnpm 管理，
        '    直接删目录会让 package.json 与磁盘不一致；
        '    用户真想清理那个目录，应该用「检查环境 → 一键修复」或手工删。
        If (s.IsImported OrElse s.IsVersioned) AndAlso Not isActive Then
            Dim btnDelete As New MyButton With {
                .Text = "删除",
                .Height = 28,
                .Margin = New Thickness(8, 0, 0, 0)
            }
            btnDelete.TextPadding = New Thickness(12, 0, 12, 0)
            Dim capturedId As String = s.Id
            Dim capturedName As String = s.DisplayName
            AddHandler btnDelete.Click, Sub(sender, e) DeleteSlot(capturedId, capturedName)
            right.Children.Add(btnDelete)
        End If

        Grid.SetColumn(right, 1)
        grid.Children.Add(right)

        row.Child = grid
        Return row
    End Function

    ''' <summary>切换到某个运行时槽位。</summary>
    ''' <remarks>
    ''' ⭐ 切换前会做**插件兼容性提醒**：
    ''' 插件是按 dsh 的内部 API 写的，换运行时版本可能让某些插件失效。
    ''' 不阻止用户切（他可能有自己的判断），但必须让他**知道会有什么后果**。
    ''' </remarks>
    Private Sub SwitchToSlot(SlotId As String)
        If IsBusy Then Return

        ' 正在跑的实例会锁住文件，而且换运行时对它来说等于换了个程序
        Dim running As List(Of DshInstance) =
            ModDSH.DshInstances.Where(Function(x) x.HasLiveProcess).ToList()

        Dim body As New StringBuilder()

        ' ── 插件兼容性提醒 ──
        Dim target As DshRuntimeSlot.DshSlotInfo = Nothing
        Try
            target = DshRuntimeSlot.DshSlotList().FirstOrDefault(
                Function(s) String.Equals(s.Id, SlotId, StringComparison.OrdinalIgnoreCase))
        Catch ex As Exception
            Logger.Warn(ex, "DSH：读取目标槽位信息失败")
        End Try

        Dim warn As String = BuildPluginCompatWarning(target)
        If Not String.IsNullOrWhiteSpace(warn) Then
            body.AppendLine(warn)
            body.AppendLine()
        End If

        If running.Count > 0 Then
            body.AppendLine($"以下实例正在运行：{String.Join("、", running.Select(Function(x) x.DisplayName))}")
            body.AppendLine()
            body.AppendLine("切换运行时不会自动重启它们。正在运行的服务仍使用切换前的运行时，")
            body.AppendLine("下次启动才会用新的。")
            body.AppendLine()
        End If

        body.AppendLine("是否切换？")

        If body.ToString().Trim() <> "是否切换？" Then
            If MyMsgBox(body.ToString(), "切换运行时",
                        "继续切换", "取消", IsWarn:=True) = 2 Then
                Return
            End If
        ElseIf running.Count > 0 Then
            If MyMsgBox(body.ToString(), "有实例正在运行",
                        "继续切换", "取消", IsWarn:=True) = 2 Then
                Return
            End If
        End If

        Dim err As String = DshRuntimeSlot.DshSlotSetActive(SlotId)
        If err IsNot Nothing Then
            Hint(err, HintType.Red)
            Return
        End If

        RefreshSlots()
        RefreshInstalledInfo()
        Hint($"已切换运行时：{DshRuntime.DetectDsh()}", HintType.Green)
    End Sub

    ''' <summary>
    ''' 生成插件兼容性提醒文案；没有风险时返回 Nothing。
    ''' </summary>
    ''' <remarks>
    ''' 判断依据（按可靠性排序）：
    ''' <list type="number">
    ''' <item><b>目标槽位不可用</b> —— 直接说清楚（虽然界面上已经禁用了按钮）</item>
    ''' <item><b>各实例的插件数量</b> —— 有插件就提醒"换版本后可能需要重装"。
    '''       这是**概率性提醒**：不是所有插件都会坏，但"有插件"是明确的信号</item>
    ''' <item><b>dsh 版本差异</b> —— 跨大版本时措辞更强</item>
    ''' </list>
    '''
    ''' ⚠️ 刻意不做"精确的兼容性判定" —— 插件生态里没有统一的兼容性元数据
    ''' （`peerDependencies` 覆盖率极低），硬判会产生大量误报。
    ''' 提醒 + 让用户自己决定，比假装能判断更诚实。
    ''' </remarks>
    Private Function BuildPluginCompatWarning(Target As DshRuntimeSlot.DshSlotInfo) As String
        Try
            If Target Is Nothing Then Return Nothing
            If Not Target.IsUsable Then
                Return "⚠ 这个运行时槽位不完整（缺少入口脚本），切换后无法启动。"
            End If

            ' 统计所有实例装了插件的情况
            Dim totalPlugins As Integer = 0
            Dim instancesWithPlugins As Integer = 0
            For Each inst In ModDSH.DshInstances
                Try
                    Dim n As Integer = DshPluginMarket.ListInstalledPlugins(inst).Count
                    If n > 0 Then
                        instancesWithPlugins += 1
                        totalPlugins += n
                    End If
                Catch ex As Exception
                    Logger.Warn(ex, $"DSH：统计实例「{inst.DisplayName}」插件数失败")
                End Try
            Next

            If totalPlugins = 0 Then Return Nothing

            Dim sb As New StringBuilder()
            sb.AppendLine("⚠ 切换运行时可能影响已装的插件。")
            sb.AppendLine()
            sb.AppendLine($"你目前共装了 {totalPlugins} 个插件（分布在 {instancesWithPlugins} 个实例里）。")
            sb.AppendLine($"目标运行时：{Target.DisplayName}")
            sb.AppendLine()
            sb.AppendLine("插件是按 dsh 的内部接口写的。如果新版本改动了这些接口，")
            sb.AppendLine("部分插件可能报错或失效 —— 这不是插件坏了，是版本不匹配。")
            sb.AppendLine()
            sb.AppendLine("建议：切换前先给实例拍一个快照。如果切完插件出问题，")
            sb.AppendLine("可以切回原来的运行时，或者用快照回滚。")
            Return sb.ToString()
        Catch ex As Exception
            Logger.Warn(ex, "DSH：生成插件兼容性提醒失败（跳过提醒）")
            Return Nothing
        End Try
    End Function

    ''' <summary>删除一个导入的运行时槽位。</summary>
    Private Sub DeleteSlot(SlotId As String, DisplayName As String)
        If IsBusy Then Return

        If MyMsgBox(
            $"确定要删除运行时「{DisplayName}」吗？" & vbCrLf & vbCrLf &
            "会把它的整个目录从磁盘上删掉（几百 MB），无法恢复。" & vbCrLf &
            "如果你的原始包还在别处，之后可以重新导入。",
            "删除运行时", "删除", "取消", IsWarn:=True) = 2 Then
            Return
        End If

        Dim err As String = DshRuntimeSlot.DshSlotDelete(SlotId)
        If err IsNot Nothing Then
            Hint(err, HintType.Red)
            Return
        End If

        RefreshSlots()
        Hint("运行时已删除。", HintType.Green)
    End Sub

    ''' <summary>
    ''' 从外部路径导入一个运行时（zip 或文件夹），走「探测 → 确认 → 导入」全流程。
    ''' </summary>
    ''' <remarks>
    ''' 公开出来是为了让拖拽入口（<c>FormMain.FileDrag</c>）能直接调用 ——
    ''' 这样拖进来和点按钮进来走的是同一条路径，不会出现两套行为。
    ''' </remarks>
    Public Sub ImportRuntimeFromPath(SourcePath As String)
        If IsBusy Then
            Hint("正在处理中，请稍候……")
            Return
        End If
        If String.IsNullOrWhiteSpace(SourcePath) Then Return

        Dim isZip As Boolean = SourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
        Dim isDir As Boolean = Directory.Exists(SourcePath)

        If Not isZip AndAlso Not isDir Then
            Hint("请拖入一个 .zip 压缩包，或者用「导入运行时…」选一个文件夹。", HintType.Red)
            Return
        End If

        ' ── 探测（不复制任何文件，很快）──
        Dim preview As DshRuntimeSlot.DshSlotPreview
        If isZip Then
            preview = DshRuntimeSlot.DshSlotPeekZip(SourcePath)
        Else
            preview = DshRuntimeSlot.DshSlotPreviewFolder(SourcePath)
        End If

        ' ⭐ 走不通就看看它是不是「导出环境」包 ——
        '   导出包的结构是 runtime\ + home\ + 清单，与纯运行时包不同。
        '   原来看不到 runtime\ 就直接拒收，导致"自己导出的包自己导不回来"。
        If Not preview.Ok Then
            ImportEnvPackageFromPath(SourcePath)
            Return
        End If

        ' ── 确认 ──
        Dim sizeText As String = "未知"
        Try
            If isZip Then
                sizeText = DshDoctor.FormatBytes(New FileInfo(SourcePath).Length) & "（压缩包）"
            Else
                sizeText = DshDoctor.FormatBytes(DshRuntimeSlot.DshSlotMeasureFolder(SourcePath)) & "（解压后）"
            End If
        Catch ex As Exception
            Logger.Warn(ex, "DSH：估算运行时包体积失败")
        End Try

        Dim body As New StringBuilder()
        body.AppendLine("检测到这是一个完整的 dsh 运行时。")
        body.AppendLine()
        body.AppendLine($"版本：{preview.Version}")
        body.AppendLine($"大小：{sizeText}")
        body.AppendLine($"来源：{SourcePath}")
        body.AppendLine()
        body.AppendLine("导入后会占用一份独立的空间（原目录/压缩包不会被改动），")
        body.AppendLine("然后你可以在运行时列表里随时切换使用。")
        body.AppendLine()
        body.AppendLine("会自动跳过打包工具、启动器 exe、说明文档等无关文件。")
        If Not String.IsNullOrWhiteSpace(preview.Warning) Then
            body.AppendLine()
            body.AppendLine("⚠ " & preview.Warning)
        End If

        If MyMsgBox(body.ToString(), $"导入 dsh {preview.Version}", "开始导入", "取消") = 2 Then
            Return
        End If

        RunSlotImport(preview)
    End Sub

    ''' <summary>后台执行导入，过程中把进度写到页面的加载卡片上。</summary>
    Private Sub RunSlotImport(Preview As DshRuntimeSlot.DshSlotPreview)
        SetBusy(True, "正在导入运行时……")

        Dim isZip As Boolean = (Preview.SourceKind = "zip")
        RunInNewThread(
            Sub()
                Dim result As DshRuntimeSlot.DshSlotImportResult = Nothing
                Dim err As String = Nothing
                Try
                    ' 进度回调来自后台线程，必须 marshal 回 UI
                    Dim onProgress As DshRuntimeSlot.DshSlotProgressHandler =
                        Sub(stage, message, pct)
                            RunInUi(
                                Sub()
                                    Try
                                        LabLoadHint.Text = $"{stage} · {message}"
                                    Catch
                                    End Try
                                End Sub)
                        End Sub

                    If isZip Then
                        result = DshRuntimeSlot.DshSlotImportFromZip(Preview.SourcePath, onProgress)
                    Else
                        result = DshRuntimeSlot.DshSlotImportFromFolder(Preview.SourcePath, onProgress)
                    End If
                Catch ex As Exception
                    err = ex.Message
                    Logger.Error(ex, "DSH：导入运行时失败")
                End Try

                RunInUi(
                    Sub()
                        SetBusy(False)
                        If err IsNot Nothing Then
                            Hint($"导入出错：{err}", HintType.Red)
                            Return
                        End If
                        If result Is Nothing Then
                            Hint("导入没有返回结果，请查看日志。", HintType.Red)
                            Return
                        End If

                        RefreshSlots()

                        If Not result.Success Then
                            MyMsgBox(result.Message, "导入失败", "知道了", IsWarn:=True)
                            Hint("导入失败，详见弹窗说明。", HintType.Red)
                            Return
                        End If

                        Dim extra As New StringBuilder()
                        extra.AppendLine($"已导入 {result.Slot.DisplayName}")
                        extra.AppendLine()
                        extra.AppendLine($"复制了 {result.FilesCopied} 个文件")
                        If result.SkippedBytes > 0 Then
                            extra.AppendLine($"跳过了 {DshDoctor.FormatBytes(result.SkippedBytes)} 的无关文件")
                        End If
                        extra.AppendLine($"存放位置：{result.Slot.Dir}")
                        extra.AppendLine()
                        extra.AppendLine("它现在出现在上方的运行时列表里，点「切换到它」即可使用。")

                        MyMsgBox(extra.ToString(), "导入完成")
                        Hint("运行时导入完成。", HintType.Green)
                    End Sub)
            End Sub, "DSH 导入运行时")
    End Sub

#End Region

#Region "导入「导出环境」包"

    ''' <summary>
    ''' 导入一个「导出环境」包（<c>runtime\</c> + <c>home\</c> + 清单）。
    ''' </summary>
    ''' <param name="SourcePath">zip 或文件夹。</param>
    ''' <remarks>
    ''' ⭐ 与「导入运行时」的区别：导出包里除了运行时**还可能有实例数据**
    ''' （插件 / 对话历史 / 用户配置），所以流程是：
    ''' <list type="number">
    ''' <item>解压 + 认结构（后台，因为要解压几百 MB）</item>
    ''' <item>让用户选**导到哪个实例**（数据要写进实例）</item>
    ''' <item>确认清单 + 覆盖警告</item>
    ''' <item>执行（运行时 → 新槽位；数据 → 写进目标实例）</item>
    ''' </list>
    ''' </remarks>
    Private Sub ImportEnvPackageFromPath(SourcePath As String)
        If IsBusy Then Return

        ' ── ① 解压 + 探测（后台，可能要几十秒）──
        SetBusy(True, "正在检查这个包……")
        Dim probe As DshEnvImport.DshEnvImportProbe = Nothing
        Dim probeErr As String = Nothing
        Try
            probe = DshEnvImport.DshBuildEnvImportProbe(
                SourcePath,
                Sub(stage, message, pct)
                    RunInUi(Sub() UpdateProgressUI(stage, message, pct))
                End Sub)
        Catch ex As Exception
            probeErr = ex.Message
            Logger.Error(ex, "DSH：探测导入包失败")
        End Try
        SetBusy(False)

        If probeErr IsNot Nothing Then
            Hint($"检查失败：{probeErr}", HintType.Red)
            Return
        End If

        If probe Is Nothing OrElse Not probe.Ok Then
            ' 到这里说明既不是运行时包，也不是导出包 —— 给出准确的解释
            Dim reason As String = If(probe?.FailReason, "无法识别的包。")
            MyMsgBox(
                "这个包不能导入。" & vbCrLf & vbCrLf & reason,
                "导入失败", "知道了", IsWarn:=True)
            Hint("导入未通过校验，详见弹窗说明。", HintType.Red)
            DshEnvImport.DshEnvImportCleanup(probe?.ExtractedRoot)
            Return
        End If

        Try
            ' ── ② 让用户选目标实例（只有含数据时才需要）──
            Dim target As DshInstance = Nothing
            If probe.HasHomeData Then
                target = PickInstanceForImport(probe)
                If target Is Nothing Then
                    ' 用户取消 —— 但如果包里还有运行时，问他要不要只导运行时
                    If Not probe.HasRuntime Then Return
                    If MyMsgBox(
                        "你没有选择目标实例，包里的实例数据（插件 / 对话历史 / 配置）将被忽略。" & vbCrLf & vbCrLf &
                        "只导入运行时吗？",
                        "跳过实例数据", "只导运行时", "取消", IsWarn:=True) = 2 Then
                        Return
                    End If
                End If
            End If

            ' ── ③ 确认清单 ──
            Dim body As New StringBuilder()
            body.AppendLine("检测到这是一个 PCL DSH 的「导出环境」包。")
            body.AppendLine()
            body.AppendLine("包含内容：")
            body.AppendLine(probe.Summary)
            body.AppendLine()
            If Not String.IsNullOrWhiteSpace(probe.SourceInstance) Then
                body.AppendLine($"导出自实例：{probe.SourceInstance}")
            End If
            If Not String.IsNullOrWhiteSpace(probe.DshVersion) Then
                body.AppendLine($"导出时的 dsh 版本：{probe.DshVersion}")
            End If
            body.AppendLine($"来源：{SourcePath}")
            body.AppendLine()

            If probe.HasRuntime Then
                body.AppendLine("· 运行时会装成一个**新的运行时槽位**，不影响现有的。")
            End If
            If probe.HasHomeData AndAlso target IsNot Nothing Then
                body.AppendLine($"· 实例数据会写入「{target.DisplayName}」。")
                body.AppendLine("  ⚠ 同名文件会被覆盖（同名会话记录、同名插件配置）。")
                body.AppendLine("  建议先给这个实例拍一个快照，万一不满意可以回滚。")
            End If

            If Not String.IsNullOrWhiteSpace(probe.Warning) Then
                body.AppendLine()
                body.AppendLine("⚠ " & probe.Warning)
            End If

            If MyMsgBox(body.ToString(), "导入环境", "开始导入", "取消") = 2 Then
                DshEnvImport.DshEnvImportCleanup(probe.ExtractedRoot)
                Return
            End If

            ' ── ④ 执行 ──
            RunEnvPackageImport(probe, target)
        Catch ex As Exception
            Logger.Error(ex, "DSH：导入环境包失败")
            Hint($"导入失败：{ex.Message}", HintType.Red)
            DshEnvImport.DshEnvImportCleanup(probe?.ExtractedRoot)
        End Try
    End Sub

    ''' <summary>让用户选一个目标实例（数据要写进它）。</summary>
    ''' <returns>选中的实例；取消返回 Nothing。</returns>
    Private Function PickInstanceForImport(Probe As DshEnvImport.DshEnvImportProbe) As DshInstance
        Dim insts As List(Of DshInstance) = ModDSH.DshInstances
        If insts Is Nothing OrElse insts.Count = 0 Then
            MyMsgBox(
                "这个包里含实例数据（插件 / 对话历史 / 配置），但当前还没有任何实例。" & vbCrLf & vbCrLf &
                "请先新建一个实例，再重新导入。",
                "没有实例", "知道了", IsWarn:=True)
            Return Nothing
        End If

        If insts.Count = 1 Then
            ' 只有一个实例就不问了，但要让用户知道数据会写进去
            Return insts(0)
        End If

        Dim options As New List(Of IMyRadio)
        For Each i In insts
            options.Add(New MyRadioBox With {
                .Text = $"{i.DisplayName}（{DshSessionCopy.DshSessionCount(i)} 个会话）"
            })
        Next
        Dim picked As Integer? = MyMsgBoxSelect(
            options,
            "这个包里含实例数据，要导入到哪个实例？",
            "确定", "取消")
        If picked Is Nothing Then Return Nothing
        If picked.Value < 0 OrElse picked.Value >= insts.Count Then Return Nothing
        Return insts(picked.Value)
    End Function

    ''' <summary>后台执行导出包导入。</summary>
    Private Sub RunEnvPackageImport(Probe As DshEnvImport.DshEnvImportProbe, Target As DshInstance)
        SetBusy(True, "正在导入……")

        RunInNewThread(
            Sub()
                Dim res As DshEnvImport.DshEnvImportResult = Nothing
                Dim err As String = Nothing
                Try
                    res = DshEnvImport.DshEnvImportExecute(
                        Probe, Target, ImportRuntime:=Probe.HasRuntime,
                        Progress:=Sub(stage, message, pct)
                                       RunInUi(Sub() UpdateProgressUI(stage, message, pct))
                                   End Sub)
                Catch ex As Exception
                    err = ex.Message
                    Logger.Error(ex, "DSH：导入环境包失败")
                Finally
                    ' 临时目录必须清理 —— 里面是解压出来的完整运行时（几百 MB）
                    DshEnvImport.DshEnvImportCleanup(Probe.ExtractedRoot)
                End Try

                RunInUi(
                    Sub()
                        Try
                            SetBusy(False)
                            RefreshSlots()
                            RefreshInstalledInfo()

                            If err IsNot Nothing Then
                                Hint($"导入失败：{err}", HintType.Red)
                                Return
                            End If
                            If res Is Nothing Then
                                Hint("导入没有返回结果，请查看日志。", HintType.Red)
                                Return
                            End If

                            MyMsgBox(res.Message, If(res.Success, "导入完成", "导入未完全成功"))
                            Hint(If(res.Success, "环境导入完成。", "导入部分失败，详见弹窗。"),
                                 If(res.Success, HintType.Green, HintType.Red))
                        Catch ex As Exception
                            Logger.Error(ex, "DSH：展示导入结果失败")
                        End Try
                    End Sub)
            End Sub, "DSH 导入环境")
    End Sub

#End Region

#Region "界面辅助"

    ''' <summary>统一切换忙碌状态。</summary>
    Private Sub SetBusy(Busy As Boolean, Optional Hint As String = Nothing)
        IsBusy = Busy
        Try
            PanLoad.Visibility = Busy.ToVisibility
            If Busy Then
                LoadStart.State.LoadingState = MyLoading.MyLoadingState.Run
                If Hint IsNot Nothing Then LabLoadHint.Text = Hint
                ' 明细行在忙碌开始时清空 —— 否则上一次的残留会显示出来
                LabLoadDetail.Text = ""
                LabLoadDetail.Visibility = Visibility.Collapsed
            Else
                LoadStart.State.LoadingState = MyLoading.MyLoadingState.Stop
                LabLoadDetail.Visibility = Visibility.Collapsed
            End If

            BtnCheckUpdate.IsEnabled = Not Busy
            BtnInstallLatest.IsEnabled = Not Busy
            BtnOpenDshFolder.IsEnabled = Not Busy
            ComboMirror.IsEnabled = Not Busy
            BtnImportRuntime.IsEnabled = Not Busy
            ' ⚠️ 导出按钮也必须禁用 —— 它跑的是长事务（复制几十万文件），
            '   期间再点一次会起第二个导出，两个一起写同一个临时目录会互相踩
            BtnExportEnv.IsEnabled = Not Busy
            BtnRefreshSlots.IsEnabled = Not Busy
        Catch ex As Exception
            Logger.Error(ex, "DSH：切换忙碌状态失败")
        End Try
    End Sub

    ''' <summary>
    ''' 刷新进度显示（阶段 + 明细两行，带节流）。
    ''' </summary>
    ''' <param name="Stage">阶段名（显示在主文案行）。</param>
    ''' <param name="Message">明细文字（显示在第二行，可能含文件名与计数）。</param>
    ''' <param name="Percent">0~1；负数表示"进行中但不确定百分比"。</param>
    ''' <remarks>
    ''' 主文案行显示**阶段名 + 百分比**（稳定、低频变化），
    ''' 明细行显示**具体在做什么**（高频变化，可能很长所以用省略号）。
    '''
    ''' ⚠️ 节流是必需的（见 <see cref="ProgressThrottleMs"/> 的注释）——
    ''' 复制几十万个文件时，每个文件都会回调一次，不节流会冻结界面。
    ''' 但**阶段名变化时必须立刻刷新**（否则用户会觉得切换很迟钝）。
    ''' </remarks>
    Private Sub UpdateProgressUI(Stage As String, Message As String, Percent As Double)
        Try
            ' 阶段名变化 → 立刻刷新，不节流
            Dim stageChanged As Boolean = (LabLoadHint.Text <> Stage)
            Dim now As Long = Environment.TickCount
            Dim elapsed As Long = now - LastProgressTick
            ' TickCount 会回绕（约 49 天），负值也要视为"该刷新了"
            If Not stageChanged AndAlso elapsed >= 0 AndAlso elapsed < ProgressThrottleMs Then Return
            LastProgressTick = now

            If Not String.IsNullOrWhiteSpace(Stage) Then
                LabLoadHint.Text = If(Percent >= 0,
                                      $"{Stage}（{CInt(Percent * 100)}%）",
                                      Stage)
            End If

            ' 明细行：只在有内容时显示
            If String.IsNullOrWhiteSpace(Message) OrElse Message = Stage Then
                LabLoadDetail.Visibility = Visibility.Collapsed
            Else
                LabLoadDetail.Text = Message
                LabLoadDetail.Visibility = Visibility.Visible
            End If
        Catch ex As Exception
            Logger.Warn($"DSH：更新进度显示失败（可忽略）：{ex.Message}")
        End Try
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        Reload(ForceRefresh:=True)
    End Sub

#End Region

#Region "按钮事件"

    ''' <summary>检查更新。</summary>
    Private Sub BtnCheckUpdate_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnCheckUpdate.Click
        Reload(ForceRefresh:=True)
    End Sub

    ''' <summary>
    ''' 安装**当前更新通道**指向的那个版本。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 早期版本刻意写死"装版本号最大的"（即永远跟预发布），
    ''' 理由是当时 dsh 整条线只有预发布，用 <c>dist-tags.latest</c>
    ''' 会把用户从 0.1.6-alpha.2 **降级**到 0.1.5-rc.2。
    '''
    ''' 现在改成由用户显式选通道（见 <see cref="ModDSH.DshChannel"/>）：
    '''   · 稳定通道 → 优先 latest，且**显式排除预发布**；没有稳定版时如实回落
    '''   · 预览通道 → 版本号最大的（含预发布）
    ''' 挑选逻辑收在 <see cref="DshRegistry.ResolveVersionForChannel"/> 里，
    ''' 这里只负责取结果 + 给用户一句解释。
    ''' </remarks>
    Private Sub BtnInstallLatest_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnInstallLatest.Click
        If IsBusy Then Return

        '列表还没拉过就先拉一次，免得用户还要自己点一下「检查更新」
        If Versions.Count = 0 Then
            Hint("正在获取版本列表，请稍后再点一次。")
            Reload(ForceRefresh:=True)
            Return
        End If

        Dim channel As ModDSH.DshChannel = ModDSH.DshCurrentChannel
        Dim target As DshRegistry.DshVersionInfo = DshRegistry.ResolveVersionForChannel(Versions, channel)
        If target Is Nothing Then target = Versions(0)

        Dim installed As String = DshRegistry.DshInstalledVersion
        If String.Equals(installed, target.Version, StringComparison.OrdinalIgnoreCase) Then
            Hint($"已经是最新版本（{target.Version}）。")
            Return
        End If

        '整个包只有预发布版本时，稳定通道会回落到预发布 —— 必须告诉用户，
        '否则他会以为"选了稳定却装了 alpha"
        If channel = ModDSH.DshChannel.Stable AndAlso target.IsPrerelease Then
            Hint($"镜像源目前没有稳定版，{target.Version} 是唯一的预发布版本。")
        End If

        InstallVersion(target)
    End Sub

    ''' <summary>打开 dsh 安装目录。</summary>
    Private Sub BtnOpenDshFolder_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnOpenDshFolder.Click
        Try
            ModDSH.DshEnsureDirectories()
            OpenExplorer(ModDSH.DshInstallDir)
        Catch ex As Exception
            Logger.Error(ex, "DSH：打开安装目录失败")
            Hint($"无法打开目录：{ex.Message}", HintType.Red)
        End Try
    End Sub

    ''' <summary>手动刷新运行时槽位列表。</summary>
    ''' <remarks>
    ''' 槽位列表是本地扫描（读清单 + 查文件在不在），不联网，所以随时刷都没成本。
    ''' 提供这个按钮主要是给「用户自己在资源管理器里删了某个导入目录」这种情况兜底 ——
    ''' 刷新后不可用的槽位就会从列表里消失。
    ''' </remarks>
    Private Sub BtnRefreshSlots_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnRefreshSlots.Click
        If IsBusy Then Return
        RefreshSlots()
        Hint("运行时列表已刷新。", HintType.Green)
    End Sub

    ''' <summary>导入运行时：先让用户选 zip 还是文件夹。</summary>
    Private Sub BtnImportRuntime_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnImportRuntime.Click
        If IsBusy Then Return

        Dim choice As Integer = MyMsgBox(
            "导入一个已经打包好的 dsh 运行时。" & vbCrLf & vbCrLf &
            "· 选压缩包 —— 适合别人发给你、或者你自己备份的 zip" & vbCrLf &
            "· 选文件夹 —— 本机已经有一份现成的目录时最省事（不用先压缩）" & vbCrLf & vbCrLf &
            "导入后它会出现在运行时列表里，可以随时切换使用。",
            "导入运行时", "选压缩包…", "选文件夹…", "取消")

        If choice = 1 Then
            ' 选压缩包
            Dim picked As String = Dialogs.SelectFile(
                "选择 dsh 运行时压缩包",
                False,
                filter:={({"zip"}, "压缩包")}).FirstOrDefault()
            If String.IsNullOrWhiteSpace(picked) Then Return
            ImportRuntimeFromPath(picked)
        ElseIf choice = 2 Then
            ' 选文件夹
            Dim picked As String = Dialogs.SelectFolder(
                "选择 dsh 运行时所在的文件夹（里面应当有 node_modules\@deepseek-ai\dsh）",
                False).FirstOrDefault()
            If String.IsNullOrWhiteSpace(picked) Then Return
            ImportRuntimeFromPath(picked)
        End If
    End Sub

#End Region

#Region "导出环境"

    ''' <summary>
    ''' 导出某个实例的环境。
    ''' </summary>
    ''' <remarks>
    ''' 流程与导入**完全对称**：选实例 → 探测（只读）→ 勾选 → 确认 → 执行。
    ''' 与导入的两处刻意差异（都是安全考虑）：
    ''' <list type="bullet">
    ''' <item><b>默认不勾选「用户配置」</b> —— 导出是把数据交到别人手里，
    '''       默认值必须偏保守（导入侧默认全选是求省事）</item>
    ''' <item><b>默认剔除密钥值</b> —— 「分享配置」通常不等于「分享密钥」</item>
    ''' </list>
    ''' </remarks>
    Private Sub BtnExportEnv_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnExportEnv.Click
        If IsBusy Then Return

        ' ── ① 选实例 ──
        Dim insts As List(Of DshInstance) = ModDSH.DshInstances
        If insts.Count = 0 Then
            Hint("还没有实例可以导出。请先新建一个实例。", HintType.Red)
            Return
        End If

        Dim inst As DshInstance = Nothing
        If insts.Count = 1 Then
            inst = insts(0)
        Else
            ' ⚠️ 用 MyMsgBoxSelect（**单选**对话框）而不是 MyMsgBox ——
            '   后者只有按钮，实例多于两个时根本选不全。
            '   MyMsgBoxSelect 接受 IMyRadio 集合，正是这个场景该用的。
            Dim options As New List(Of IMyRadio)
            For Each i In insts
                options.Add(New MyRadioBox With {
                    .Text = $"{i.DisplayName}（{DshSessionCopy.DshSessionCount(i)} 个会话）"
                })
            Next
            Dim picked As Integer? = MyMsgBoxSelect(options, "选择要导出的实例", "确定", "取消")
            If picked Is Nothing Then Return
            If picked.Value < 0 OrElse picked.Value >= insts.Count Then Return
            inst = insts(picked.Value)
        End If

        If inst Is Nothing Then Return

        ' ── ② 探测（只读）──
        Dim probe As DshEnvExport.DshExportProbe = Nothing
        Try
            probe = DshEnvExport.DshBuildExportProbe(inst)
        Catch ex As Exception
            Logger.Error(ex, "DSH：探测导出内容失败")
            Hint($"探测失败：{ex.Message}", HintType.Red)
            Return
        End Try

        If probe.Parts.All(Function(p) Not p.HasContent) Then
            MyMsgBox($"实例「{inst.DisplayName}」里没有可导出的内容。",
                     "没有内容", "知道了")
            Return
        End If

        ' ── ③ 勾选要导出的内容 ──
        Dim pickedParts As List(Of DshEnvExport.DshExportPart) = Nothing
        Dim stripSecrets As Boolean = True
        If Not ShowExportPicker(probe, pickedParts, stripSecrets) Then Return
        If pickedParts Is Nothing OrElse pickedParts.Count = 0 Then
            Hint("没有勾选任何内容。", HintType.Red)
            Return
        End If

        ' ── ④ 选保存位置 ──
        ' ⚠️ 用 Dialogs.SaveFile（**保存**对话框），不是 SelectFile（**打开**对话框）——
        '   后者是"选一个已存在的文件"，用在导出场景下按钮会显示"打开"。
        '
        ' 签名（反射确认）：
        '   SaveFile(title, defaultFileName, defaultDirectory, filter) → String
        '
        ' ⚠️ 三个踩过的坑：
        '   ① 返回**完整绝对路径**，不要自作聪明去"补全" ——
        '      我原来加了段"相对路径就拼到桌面"的逻辑，结果把用户选的目录覆盖成了桌面。
        '   ② 要传 defaultDirectory（第 3 参），否则对话框每次从系统默认位置开始，
        '      用户上次选的目录不会被记住。
        '   ③ 返回值是 String（不是集合），不需要 .FirstOrDefault()。
        Dim suggested As String = $"{inst.DisplayName}_环境_{DateTime.Now:yyyyMMdd_HHmm}.zip"
        Dim lastDir As String = Nothing
        Try
            lastDir = Settings.Get(Of String)("DshExportLastDir")
        Catch ex As Exception
            ' 没存过就留空，让对话框用系统默认
        End Try
        If Not String.IsNullOrWhiteSpace(lastDir) AndAlso Not Directory.Exists(lastDir) Then
            lastDir = Nothing   ' 上次的目录已不存在（U 盘拔了等）
        End If

        Dim outPath As String = Dialogs.SaveFile(
            "选择导出位置",
            suggested,
            lastDir,
            {("zip", "压缩包")})
        If String.IsNullOrWhiteSpace(outPath) Then Return

        ' 记住这次选的目录，下次从它开始（与 PCL 原生导出的做法一致）
        Try
            Dim chosenDir As String = Path.GetDirectoryName(outPath)
            If Not String.IsNullOrWhiteSpace(chosenDir) AndAlso Directory.Exists(chosenDir) Then
                Settings.Set("DshExportLastDir", chosenDir)
            End If
        Catch ex As Exception
            Logger.Warn(ex, "DSH：记住导出目录失败（可忽略）")
        End Try

        If Not outPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) Then outPath &= ".zip"
        Logger.Info($"DSH：导出目标路径 = {outPath}")

        ' ── ⑤ 含密钥时二次确认 ──
        If Not stripSecrets AndAlso pickedParts.Contains(DshEnvExport.DshExportPart.Config) Then
            Dim secretCount As Integer = 0
            Try
                secretCount = DshCredentials.ReadRefs(inst.HomeDir).Count
            Catch ex As Exception
                Logger.Warn(ex, "DSH：统计待导出密钥数失败")
            End Try
            If secretCount > 0 Then
                If MyMsgBox(
                    $"这个包会包含 {secretCount} 个 API Key（明文）。" & vbCrLf & vbCrLf &
                    "发给别人就等于把密钥给了他 —— 他可以用你的额度。" & vbCrLf & vbCrLf &
                    "如果你只是想分享配置，建议改用「剔除密钥」选项：",
                    "确认包含密钥", "仍要包含", "改成剔除密钥", IsWarn:=True) = 2 Then
                    stripSecrets = True
                End If
            End If
        End If

        ' ── ⑥ 执行 ──
        SetBusy(True, "正在导出环境……")
        Dim finalParts As List(Of DshEnvExport.DshExportPart) = pickedParts
        Dim finalStrip As Boolean = stripSecrets
        Dim finalPath As String = outPath
        RunInNewThread(
            Sub()
                Dim res As DshEnvExport.DshExportResult = Nothing
                Dim err As String = Nothing
                Try
                    res = DshEnvExport.DshEnvExportToZip(
                        inst, finalParts, finalPath, finalStrip,
                        Sub(stage, message, pct)
                            RunInUi(Sub() UpdateProgressUI(stage, message, pct))
                        End Sub)
                Catch ex As Exception
                    err = ex.Message
                    Logger.Error(ex, "DSH：导出环境失败")
                End Try

                RunInUi(
                    Sub()
                        Try
                            SetBusy(False)
                            If err IsNot Nothing Then
                                Hint($"导出失败：{err}", HintType.Red)
                                Return
                            End If
                            If res Is Nothing Then
                                Hint("导出没有返回结果，请查看日志。", HintType.Red)
                                Return
                            End If
                            MyMsgBox(res.Message, If(res.Success, "导出完成", "导出未完全成功"))
                            Hint(If(res.Success, "环境已导出。", "导出部分失败，详见弹窗。"),
                                 If(res.Success, HintType.Green, HintType.Red))
                        Catch ex As Exception
                            Logger.Error(ex, "DSH：展示导出结果失败")
                        End Try
                    End Sub)
            End Sub, "DSH 导出环境")
    End Sub

    ''' <summary>
    ''' 弹出「勾选要导出的内容」对话框（真正的多选）。
    ''' </summary>
    ''' <returns>用户确认返回 True；取消返回 False。</returns>
    ''' <remarks>
    ''' ⚠️ 用的是**导入导出共用**的 <see cref="DshPartPicker"/> ——
    ''' 用户明确要求两边"可选项、交互方式保持一致"，
    ''' 各写一个迟早会漂移成"导入能选、导出不能选"。
    '''
    ''' 与导入的默认值差异（安全考虑）：
    ''' <b>用户配置默认不勾</b> —— 导出是把数据交到别人手里，默认值必须偏保守。
    ''' 勾上它时对话框会实时显示"含 N 个密钥"的警告。
    ''' </remarks>
    Private Function ShowExportPicker(Probe As DshEnvExport.DshExportProbe,
                                      ByRef PickedParts As List(Of DshEnvExport.DshExportPart),
                                      ByRef StripSecrets As Boolean) As Boolean
        ' 密钥数量（用于警告文案）
        Dim secretCount As Integer = 0
        Try
            Dim inst = ModDSH.DshInstances.FirstOrDefault(
                Function(x) String.Equals(x.HomeDir, Probe.InstanceHome, StringComparison.OrdinalIgnoreCase))
            If inst IsNot Nothing Then
                secretCount = DshCredentials.ReadRefs(inst.HomeDir).Count
            End If
        Catch ex As Exception
            Logger.Warn(ex, "DSH：统计待导出密钥数失败")
        End Try

        Dim items As New List(Of DshPartPicker.DshPickItem)()
        For Each p In Probe.Parts
            Dim key As String = p.Part.ToString()
            Dim detail As String = ""
            If p.ItemCount > 0 Then detail = $"{p.ItemCount} 项"
            If p.Bytes > 0 Then detail &= If(detail.Length > 0, " · ", "") & DshDoctor.FormatBytes(p.Bytes)

            Dim warn As String = Nothing
            If p.Part = DshExportPart.Config AndAlso secretCount > 0 Then
                warn = $"这个包会包含 {secretCount} 个 API Key（明文）。" & vbCrLf &
                       "   发给别人等于把密钥给了他 —— 他可以用你的额度。"
            End If

            items.Add(New DshPartPicker.DshPickItem With {
                .Key = key,
                .Title = p.Name,
                .Description = p.Description,
                .Detail = detail,
                .DefaultChecked = p.DefaultChecked,
                .Enabled = p.HasContent,
                .WarnOnCheck = warn
            })
        Next

        Dim intro As String =
            $"从「{Probe.InstanceName}」导出哪些内容？" & vbCrLf &
            "勾选后导出的包可以直接用「导入运行时…」导入到别的机器或实例。"

        Dim staticWarn As String =
            "运行时默认不勾选 —— 它有几十万个文件（几 GB），复制要十几分钟。" & vbCrLf &
            "   运行时是通用的，别人自己装配即可；真正需要搬的是插件、对话和配置（约几百 MB）。" & vbCrLf &
            "API Key 也默认不勾选 —— 避免误把密钥发给别人。"

        Dim dlg As New DshPartPicker("导出环境", intro, items,
                                     ConfirmText:="导出", Warning:=staticWarn)
        Dim ok As Boolean = (dlg.ShowDialog() = True)
        If Not ok Then Return False

        Dim picked As List(Of String) = dlg.PickedKeys()
        If picked.Count = 0 Then
            Hint("没有勾选任何内容。", HintType.Red)
            Return False
        End If

        ' 勾了配置 → 询问是否连密钥一起带
        StripSecrets = True
        If picked.Contains(DshExportPart.Config.ToString()) AndAlso secretCount > 0 Then
            Dim choice As Integer = MyMsgBox(
                $"你勾选了「用户配置」，其中包含 {secretCount} 个 API Key。" & vbCrLf & vbCrLf &
                "· 「剔除密钥」—— 保留配置结构（接入方式、BaseUrl、模型名），" & vbCrLf &
                "  但把密钥值留空。别人导入后填自己的 Key 就能用。" & vbCrLf &
                "· 「连密钥一起」—— 密钥明文打包。只在自己备份、或对方是你信任的人时选。",
                "是否包含密钥", "剔除密钥", "连密钥一起", IsWarn:=True)
            If choice = 3 Then Return False
            StripSecrets = (choice <> 2)
        End If

        PickedParts = New List(Of DshEnvExport.DshExportPart)()
        For Each s In picked
            Dim parsed As DshEnvExport.DshExportPart
            If [Enum].TryParse(s, parsed) Then PickedParts.Add(parsed)
        Next
        Return True
    End Function

#End Region

End Class
