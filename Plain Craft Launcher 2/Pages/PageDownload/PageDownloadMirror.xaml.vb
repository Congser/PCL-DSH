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
        Catch ex As Exception
            Logger.Error(ex, "DSH：刷新已安装版本信息失败")
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
                            '进度回调可能在任意线程，切回 UI 再改控件
                            RunInUi(
                                Sub()
                                    Try
                                        LabLoadHint.Text = If(pct >= 0, $"{text}（{CInt(pct * 100)}%）", text)
                                    Catch ex As Exception
                                        Logger.Warn($"DSH：更新安装进度失败（可忽略）：{ex.Message}")
                                    End Try
                                End Sub)
                        End Sub)
                Catch ex As Exception
                    err = ex.Message
                    Logger.Error(ex, $"DSH：安装 dsh {Version} 失败")
                End Try

                RunInUi(
                    Sub()
                        SetBusy(False)
                        RefreshInstalledInfo()
                        If err Is Nothing Then
                            Hint($"dsh {Version} 安装完成。已运行的实例需要重启才会用上新版本。", HintType.Green)
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

        ' 官方槽位是 pnpm 管的，不给删 —— 删了会让 package.json 和磁盘不一致
        If s.IsImported AndAlso Not isActive Then
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
    Private Sub SwitchToSlot(SlotId As String)
        If IsBusy Then Return

        ' 正在跑的实例会锁住文件，而且换运行时对它来说等于换了个程序
        Dim running As List(Of DshInstance) =
            ModDSH.DshInstances.Where(Function(x) x.HasLiveProcess).ToList()
        If running.Count > 0 Then
            If MyMsgBox(
                $"以下实例正在运行：{String.Join("、", running.Select(Function(x) x.DisplayName))}{vbCrLf}{vbCrLf}" &
                "切换运行时不会自动重启它们。正在运行的服务仍使用切换前的运行时，" &
                "下次启动才会用新的。" & vbCrLf & vbCrLf &
                "是否继续切换？",
                "有实例正在运行", "继续切换", "取消", IsWarn:=True) = 2 Then
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

        If Not preview.Ok Then
            MyMsgBox(
                "这个包不能作为 dsh 运行时导入。" & vbCrLf & vbCrLf &
                preview.FailReason,
                "导入失败", "知道了", IsWarn:=True)
            Hint("导入未通过校验，详见弹窗说明。", HintType.Red)
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

#Region "界面辅助"

    ''' <summary>统一切换忙碌状态。</summary>
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

            BtnCheckUpdate.IsEnabled = Not Busy
            BtnInstallLatest.IsEnabled = Not Busy
            BtnOpenDshFolder.IsEnabled = Not Busy
            ComboMirror.IsEnabled = Not Busy
            BtnImportRuntime.IsEnabled = Not Busy
            BtnRefreshSlots.IsEnabled = Not Busy
        Catch ex As Exception
            Logger.Error(ex, "DSH：切换忙碌状态失败")
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

End Class
