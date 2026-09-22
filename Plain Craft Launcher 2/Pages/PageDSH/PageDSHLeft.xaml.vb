Imports System.Windows.Media

''' <summary>
''' DSH 启动面板 —— PCL_DSH 的主页面左栏。
'''
''' 布局（自上而下）：
'''   状态徽标 → 我的实例（可点选）→ 端口 → 实例操作 → 模式 → 启动 → 版本/插件
'''
''' ⚠️ 本页**不持有加载器**。
''' 加载器机制在 <see cref="MyPageRight"/> 上，而本类继承 <see cref="MyPageLeft"/>，
''' 拿不到 <c>PageLoaderInit</c> / <c>PageLoaderRestart</c>。
''' 多实例改造后启动流程搬到了 <see cref="DshLauncher"/>（与页面解耦），
''' 本页只负责调用 + 订阅进度事件，不再需要和右栏互相持有引用。
'''
''' ⚠️ 本页**不能**写 <c>Handles Me.PageEnter</c> ——
''' <c>PageEnter</c> 事件定义在 <see cref="MyPageRight"/> 上。
''' 左栏在每次主页面切换时都会被重新挂载，<c>Loaded</c> 会重新触发，够用。
''' </summary>
Public Class PageDSHLeft
    Implements IRefreshable

#Region "类型"

    ''' <summary>模型来源。</summary>
    Public Enum DshMode
        ''' <summary>本地模型（尚未实现）。</summary>
        Local = 0
        ''' <summary>在线 API Key。</summary>
        Online = 1
    End Enum

#End Region

#Region "状态"

    Private IsLoad As Boolean = False

    ''' <summary>当前是否处于「启动中」视图。</summary>
    Private IsLaunchingView As Boolean = False

    ''' <summary>刷新重入闸（重建实例列表时会连带触发选中变更事件）。</summary>
    Private IsRefreshing As Boolean = False

    ''' <summary>程序化改写端口输入框时置位，避免把自己的写入当成用户输入。</summary>
    Private IsUpdatingPort As Boolean = False

    ''' <summary>右栏「在线配置」页。</summary>
    Private FrmOnline As PageDSHOnline = Nothing

    ''' <summary>当前的模型来源模式。</summary>
    Private CurrentMode As DshMode = DshMode.Online

#End Region

#Region "初始化"

    Private Sub PageDSHLeft_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        '⚠️ 必须挂 Loaded，不能挂 Initialized ——
        ' 挂 Initialized 时字段还没赋值，界面会停在空白（本页早期版本踩过这个坑）。
        If Not IsLoad Then
            IsLoad = True

            AddHandler ModDSH.DshInstancesChanged, AddressOf OnInstancesChanged
            AddHandler ModDSH.DshInstanceStateChanged, AddressOf OnInstanceStateChanged
            AddHandler ModDSH.DshSelectedInstanceChanged, AddressOf OnSelectedInstanceChanged
            AddHandler DshLauncher.LaunchProgress, AddressOf OnLaunchProgress
            AddHandler DshLauncher.LaunchFinished, AddressOf OnLaunchFinished
        End If

        LoadModeFromSettings()
        RefreshAll()
    End Sub

    ''' <summary>从设置里恢复模型来源模式。</summary>
    Private Sub LoadModeFromSettings()
        Try
            CurrentMode = CType(Settings.Get(Of Integer)("DshModelMode"), DshMode)
        Catch ex As Exception
            Logger.Warn($"DSH：读取模型来源模式失败，回退到在线模式：{ex.Message}")
            CurrentMode = DshMode.Online
        End Try
    End Sub

#End Region

#Region "事件回调"

    Private Sub OnInstancesChanged()
        RefreshInstances()
        RefreshPortBox()
        RefreshLaunchButton()
    End Sub

    Private Sub OnInstanceStateChanged(Instance As DshInstance)
        '事件已在 UI 线程触发
        RefreshInstances()
        RefreshStateBadge()
        RefreshLaunchButton()

        '正在启动的实例状态变了 → 同步「启动中」视图里的状态行
        If IsLaunchingView AndAlso ModDSH.DshSelectedInstance Is Instance Then
            LabLaunchingState.Text = Instance.DisplayDetail
        End If
    End Sub

    Private Sub OnSelectedInstanceChanged(Instance As DshInstance)
        RefreshInstances()
        RefreshPortBox()
        RefreshStateBadge()
        RefreshLaunchButton()
    End Sub

    ''' <summary>启动进度（已在 UI 线程）。</summary>
    Private Sub OnLaunchProgress(Instance As DshInstance, Stage As String, Percent As Double)
        If ModDSH.DshSelectedInstance IsNot Instance Then Return
        Try
            LabLaunchingStage.Text = Stage
            LabLaunchingState.Text = Instance.DisplayDetail

            If Percent < 0 Then
                '不确定进度：整条保持"未完成"色，靠转圈表达
                LabLaunchingProgress.Text = "进行中…"
                ProgressLaunchingFinished.Width = New GridLength(0, GridUnitType.Star)
                ProgressLaunchingUnfinished.Width = New GridLength(100, GridUnitType.Star)
            Else
                Dim pct As Double = Math.Max(0, Math.Min(1, Percent))
                LabLaunchingProgress.Text = CInt(pct * 100) & "%"
                ProgressLaunchingFinished.Width = New GridLength(pct * 100, GridUnitType.Star)
                ProgressLaunchingUnfinished.Width = New GridLength((1 - pct) * 100, GridUnitType.Star)
            End If
        Catch ex As Exception
            Logger.Warn($"DSH：更新启动进度失败（可忽略）：{ex.Message}")
        End Try
    End Sub

    ''' <summary>启动流程结束（已在 UI 线程）。</summary>
    Private Sub OnLaunchFinished(Instance As DshInstance, Success As Boolean, ReadyUrl As String, Message As String)
        '只有「由本页发起」的启动才由本页收尾并弹提示。
        '从日志页发起的启动，那边自己会处理，否则会弹两条重复提示。
        If Not IsLaunchingView Then Return
        If ModDSH.DshSelectedInstance IsNot Instance Then Return

        HideLaunchingView()
        RefreshAll()

        If Success Then
            '左下角成功反馈
            Hint($"实例「{Instance.DisplayName}」启动成功！", HintType.Green)
            If DshBrowser.DshBrowserShouldAutoOpen Then
                DshBrowser.OpenInstance(Instance)
            Else
                Hint("DSH 已就绪，可点击「打开 DSH 界面」进入。")
            End If
        Else
            Hint($"启动失败：{If(String.IsNullOrWhiteSpace(Message), "未知原因", Message)}", HintType.Red)
        End If
    End Sub

#End Region

#Region "界面刷新"

    Private Sub RefreshAll()
        If IsRefreshing Then Return
        IsRefreshing = True
        Try
            RefreshInstances()
            RefreshPortBox()
            RefreshStateBadge()
            RefreshModeButtons()
            RefreshLaunchButton()
        Finally
            IsRefreshing = False
        End Try
    End Sub

    ''' <summary>重建实例列表。</summary>
    Private Sub RefreshInstances()
        Try
            PanInstances.Children.Clear()

            Dim all As List(Of DshInstance) = ModDSH.DshInstances
            Dim selected As DshInstance = ModDSH.DshSelectedInstance

            For Each inst In all
                PanInstances.Children.Add(BuildInstanceItem(inst, selected))
            Next

            LabInstanceCount.Text = $"{all.Count} / {ModDSH.DshMaxInstances}"

            '操作按钮的可用性
            BtnNewInstance.IsEnabled = all.Count < ModDSH.DshMaxInstances
            BtnRenameInstance.IsEnabled = selected IsNot Nothing
            '至少要留一个实例，否则主页会空掉
            BtnDeleteInstance.IsEnabled = all.Count > 1 AndAlso selected IsNot Nothing
        Catch ex As Exception
            Logger.Error(ex, "DSH：刷新实例列表失败")
        End Try
    End Sub

    ''' <summary>构建一个可点选的实例条目。</summary>
    Private Function BuildInstanceItem(inst As DshInstance, selected As DshInstance) As FrameworkElement
        Dim isSelected As Boolean =
            selected IsNot Nothing AndAlso
            String.Equals(selected.Id, inst.Id, StringComparison.OrdinalIgnoreCase)

        Dim row As New Border With {
            .BorderThickness = New Thickness(1),
            .CornerRadius = New CornerRadius(4),
            .Padding = New Thickness(11, 8, 11, 8),
            .Margin = New Thickness(0, 0, 0, 6),
            .Cursor = Cursors.Hand
        }
        row.SetResourceReference(Border.BorderBrushProperty,
                                 If(isSelected, "ColorBrush3", "ColorBrushGray5"))
        If isSelected Then row.SetResourceReference(Border.BackgroundProperty, "ColorBrush8")

        Dim grid As New Grid()
        grid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
        grid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})

        '状态圆点
        Dim dot As New Shapes.Ellipse With {
            .Width = 9, .Height = 9,
            .VerticalAlignment = VerticalAlignment.Center,
            .Margin = New Thickness(0, 0, 9, 0),
            .Fill = New SolidColorBrush(StateColor(inst))
        }
        Grid.SetColumn(dot, 0)
        grid.Children.Add(dot)

        '名字 + 端口/状态
        Dim texts As New StackPanel()
        Dim nameText As New TextBlock With {
            .FontSize = 13,
            .Text = inst.DisplayName,
            .TextTrimming = TextTrimming.CharacterEllipsis,
            .FontWeight = If(isSelected, FontWeights.Bold, FontWeights.Normal)
        }
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        texts.Children.Add(nameText)

        Dim detailText As New TextBlock With {
            .FontSize = 11.5,
            .Margin = New Thickness(0, 3, 0, 0),
            .Text = inst.DisplayDetail,
            .TextTrimming = TextTrimming.CharacterEllipsis
        }
        detailText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
        texts.Children.Add(detailText)

        Grid.SetColumn(texts, 1)
        grid.Children.Add(texts)

        row.Child = grid

        Dim captured As DshInstance = inst
        AddHandler row.MouseLeftButtonUp, Sub(s, e) SelectInstance(captured)
        Return row
    End Function

    ''' <summary>状态对应的指示色。</summary>
    Private Function StateColor(inst As DshInstance) As Color
        Select Case inst.State
            Case ModDSH.DshState.Running
                Return Color.FromRgb(&H2E, &HA0, &H55)
            Case ModDSH.DshState.Failed
                Return Color.FromRgb(&HDF, &H50, &H40)
            Case ModDSH.DshState.Starting, ModDSH.DshState.Stopping,
                 ModDSH.DshState.CheckingRuntime, ModDSH.DshState.Installing
                Return Color.FromRgb(&HF0, &HA8, &H20)
            Case Else
                Return Color.FromRgb(&HA6, &HA6, &HA6)
        End Select
    End Function

    ''' <summary>选中一个实例。</summary>
    Private Sub SelectInstance(inst As DshInstance)
        If inst Is Nothing Then Return
        If ModDSH.DshSelectedInstance Is inst Then Return
        '选中变更会触发 DshSelectedInstanceChanged → 自动刷新，这里不用手动刷
        ModDSH.DshSelectedInstance = inst
    End Sub

    ''' <summary>把当前实例的端口写进输入框。</summary>
    Private Sub RefreshPortBox()
        Try
            Dim inst As DshInstance = ModDSH.DshSelectedInstance
            IsUpdatingPort = True
            Try
                If inst Is Nothing Then
                    TextPort.Text = ""
                    TextPort.IsEnabled = False
                Else
                    TextPort.Text = inst.Port.ToString()
                    '服务在跑的时候改端口没有意义，禁掉避免误解
                    TextPort.IsEnabled = Not (inst.IsRunning OrElse inst.HasLiveProcess OrElse inst.IsBusy)
                End If
            Finally
                IsUpdatingPort = False
            End Try
        Catch ex As Exception
            Logger.Error(ex, "DSH：刷新端口输入框失败")
        End Try
    End Sub

    ''' <summary>刷新顶部状态徽标（跟随当前实例）。</summary>
    Private Sub RefreshStateBadge()
        Try
            Dim inst As DshInstance = ModDSH.DshSelectedInstance
            If inst Is Nothing Then
                LabStateBadge.Text = "无实例"
                SetBadgeLogo(LogoIdle)
                Return
            End If

            Select Case inst.State
                Case ModDSH.DshState.Running
                    LabStateBadge.Text = "运行中"
                    SetBadgeLogo(LogoCheck)
                Case ModDSH.DshState.Starting, ModDSH.DshState.CheckingRuntime, ModDSH.DshState.Installing
                    LabStateBadge.Text = "启动中"
                    SetBadgeLogo(LogoLoading)
                Case ModDSH.DshState.Stopping
                    LabStateBadge.Text = "停止中"
                    SetBadgeLogo(LogoLoading)
                Case ModDSH.DshState.Failed
                    LabStateBadge.Text = "启动失败"
                    SetBadgeLogo(LogoError)
                Case Else
                    LabStateBadge.Text = "未运行"
                    SetBadgeLogo(LogoIdle)
            End Select
        Catch ex As Exception
            Logger.Error(ex, "DSH：刷新状态徽标失败")
        End Try
    End Sub

    Private Sub SetBadgeLogo(Data As String)
        PathStateBadge.Data = Geometry.Parse(Data)
    End Sub

    ''' <summary>刷新模式切换按钮的高亮。</summary>
    Private Sub RefreshModeButtons()
        Dim isLocal As Boolean = (CurrentMode = DshMode.Local)
        BtnModeLocal.ColorType = If(isLocal, MyButton.ColorState.Highlight, MyButton.ColorState.Normal)
        BtnModeOnline.ColorType = If(isLocal, MyButton.ColorState.Normal, MyButton.ColorState.Highlight)
    End Sub

    ''' <summary>刷新主按钮文案与可用性。</summary>
    Private Sub RefreshLaunchButton()
        Try
            Dim inst As DshInstance = ModDSH.DshSelectedInstance
            If inst Is Nothing Then
                BtnLaunch.Text = "启动 DSH"
                BtnLaunch.IsEnabled = False
                LabVersion.Text = "请先新建一个实例"
                Return
            End If

            '本地模型分支还没实现 —— 直接拦住，别让用户点了才发现不行
            If CurrentMode = DshMode.Local Then
                BtnLaunch.Text = "启动 DSH"
                BtnLaunch.IsEnabled = False
                LabVersion.Text = "本地模型尚未实现，请切换到 API Key 模式"
                Return
            End If

            If DshLauncher.IsStarting(inst) OrElse inst.IsBusy Then
                BtnLaunch.Text = "启动中…"
                BtnLaunch.IsEnabled = False
                LabVersion.Text = "正在准备运行时环境"
                Return
            End If

            If inst.IsRunning OrElse inst.HasLiveProcess Then
                BtnLaunch.Text = "停止 DSH"
                BtnLaunch.IsEnabled = True
                LabVersion.Text = "DSH 正在运行，点击停止"
            Else
                BtnLaunch.Text = "启动 DSH"
                BtnLaunch.IsEnabled = True
                LabVersion.Text = If(DshCredentials.HasDeepSeekApiKey(inst.HomeDir),
                                     "点击启动 DeepSeek Harness",
                                     "尚未配置 API Key，可在右侧面板填入")
            End If
        Catch ex As Exception
            Logger.Error(ex, "DSH：刷新启动按钮失败")
        End Try
    End Sub

#End Region

#Region "按钮事件"

    ''' <summary>主按钮：启动 / 停止当前实例。</summary>
    Private Sub BtnLaunch_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnLaunch.Click
        Dim inst As DshInstance = ModDSH.DshSelectedInstance
        If inst Is Nothing Then
            Hint("请先新建一个实例。", HintType.Red)
            Return
        End If

        If CurrentMode = DshMode.Local Then
            Hint("本地模型尚未实现，请先切换到 API Key 模式。", HintType.Red)
            Return
        End If

        If inst.IsRunning OrElse inst.HasLiveProcess Then
            DshLauncher.StopAsync(inst)
        Else
            '先切到"启动中"视图，再点火 —— 顺序反了会闪一下常态视图
            LabLaunchingName.Text = inst.DisplayName
            LabLaunchingStage.Text = "准备启动"
            LabLaunchingState.Text = inst.DisplayDetail
            LabLaunchingProgress.Text = "—"
            ProgressLaunchingFinished.Width = New GridLength(0, GridUnitType.Star)
            ProgressLaunchingUnfinished.Width = New GridLength(100, GridUnitType.Star)
            ShowLaunchingView()
            DshLauncher.StartAsync(inst)
        End If
    End Sub

    ''' <summary>新建实例。</summary>
    Private Sub BtnNewInstance_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnNewInstance.Click
        Try
            If Not ModDSH.DshCanCreateInstance Then
                Hint($"最多只能有 {ModDSH.DshMaxInstances} 个实例。请先删除一个再新建。", HintType.Red)
                Return
            End If

            Dim suggestedPort As Integer = ModDSH.DshSuggestPort()
            Dim defaultName As String = "实例 " & (ModDSH.DshInstanceCount + 1)

            Dim input As String = MyMsgBoxInput(
                "新建实例",
                $"每个实例有独立的数据目录（配置、插件、会话、凭据），共享同一份 Node 与 dsh 运行时。{vbCrLf}" &
                $"端口默认 {suggestedPort}，之后可以在左栏直接修改。",
                defaultName,
                HintText:="实例名称")

            'Button2（取消）返回 Nothing
            If input Is Nothing Then Return
            If String.IsNullOrWhiteSpace(input) Then
                Hint("实例名不能为空。", HintType.Red)
                Return
            End If

            Dim created As DshInstance = ModDSH.DshCreateInstance(input.Trim(), suggestedPort)
            If created Is Nothing Then
                Hint($"最多只能有 {ModDSH.DshMaxInstances} 个实例。", HintType.Red)
                Return
            End If

            '把已有的凭据与接入配置也复制给新实例 —— 否则用户会莫名其妙用不了
            ApplyExistingConfigToNewInstance(created)

            ModDSH.DshSelectedInstance = created
            Hint($"已新建实例「{created.DisplayName}」（端口 {created.Port}）。", HintType.Green)
        Catch ex As Exception
            Logger.Error(ex, "DSH：新建实例失败")
            Hint($"新建失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    ''' <summary>
    ''' 把当前已有的凭据与接入配置复制给一个新实例。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 这里必须把**两种**凭据都复制（官方 + 自定义网关），
    ''' 而不只是官方那一把 —— 早期版本只同步了 <c>DEEPSEEK_API_KEY</c>，
    ''' 结果在自定义网关模式下新建的实例是个空目录，
    ''' 启动后直接报 MISSING_CREDENTIAL，用户完全不知道哪里错了。
    '''
    ''' 接入配置（settings.yaml 的 llm-deepseek 分节）也要一并写入，
    ''' 否则新实例会去连官方端点，而用户配的是自定义网关。
    '''
    ''' 失败不阻塞新建 —— 用户可以随后在右栏手动保存一次。
    ''' </remarks>
    Private Sub ApplyExistingConfigToNewInstance(inst As DshInstance)
        If inst Is Nothing Then Return

        '── 1. 凭据：**每一种接入方式**的密钥都复制一份 ──
        '不能只同步官方那一把 —— 新实例默认会继承主实例正在用的接入方式，
        '如果那把密钥没跟过来，启动后直接报 MISSING_CREDENTIAL。
        '顺带把其它 provider 的密钥也带上，这样新实例随时切换都不用重新输入。
        Try
            For Each provider In DshApiConfig.LoadProviders()
                Try
                    Dim value As String = DshCredentials.GetAnyKey(provider.KeyRef)
                    If String.IsNullOrWhiteSpace(value) Then Continue For
                    DshCredentials.SetRef(provider.KeyRef, value, inst.HomeDir)
                Catch ex As Exception
                    Logger.Warn($"DSH：向新实例同步 {provider.KeyRef} 失败（可忽略）：{ex.Message}")
                End Try
            Next
        Catch ex As Exception
            Logger.Warn($"DSH：读取接入方式列表失败（可忽略）：{ex.Message}")
        End Try

        '── 2. 接入方式：跟随主实例 ──
        '新实例默认用主实例正在用的那个，符合"我刚配好的环境"的直觉。
        '失败不阻塞新建 —— 用户可以随后在右栏手动选一次。
        Try
            Dim providers As List(Of DshApiConfig.DshProvider) = DshApiConfig.LoadProviders()
            Dim primary As DshInstance = ModDSH.DshPrimaryInstance
            Dim target As DshApiConfig.DshProvider = Nothing

            If primary IsNot Nothing Then target = DshApiConfig.GetInstanceProvider(primary, providers)
            If target Is Nothing Then target = DshApiConfig.GetProvider(providers, DshApiConfig.OfficialProviderId)

            Dim reason As String = Nothing
            If DshApiConfig.ApplyToInstance(inst, target, reason) Then
                Logger.Info($"DSH：新实例「{inst.DisplayName}」的接入方式 = {target.DisplayLabel}")
            Else
                Logger.Warn($"DSH：向新实例应用接入方式失败：{reason}")
            End If
        Catch ex As Exception
            Logger.Warn($"DSH：向新实例应用接入方式时出错（可忽略）：{ex.Message}")
        End Try
    End Sub

    ''' <summary>重命名实例。</summary>
    Private Sub BtnRenameInstance_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnRenameInstance.Click
        Try
            Dim inst As DshInstance = ModDSH.DshSelectedInstance
            If inst Is Nothing Then Return

            Dim input As String = MyMsgBoxInput("重命名实例", "输入新的实例名称。", inst.DisplayName, HintText:="实例名称")
            If input Is Nothing Then Return
            If String.IsNullOrWhiteSpace(input) Then
                Hint("实例名不能为空。", HintType.Red)
                Return
            End If

            If ModDSH.DshRenameInstance(inst.Id, input.Trim()) Then
                Hint($"已重命名为「{input.Trim()}」。", HintType.Green)
            End If
        Catch ex As Exception
            Logger.Error(ex, "DSH：重命名实例失败")
            Hint($"重命名失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    ''' <summary>删除实例。</summary>
    Private Sub BtnDeleteInstance_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnDeleteInstance.Click
        Try
            Dim inst As DshInstance = ModDSH.DshSelectedInstance
            If inst Is Nothing Then Return

            If ModDSH.DshInstanceCount <= 1 Then
                Hint("至少要保留一个实例。", HintType.Red)
                Return
            End If

            '⚠️ MyMsgBox 的返回值是 **1 / 2 / 3**（对应 Button1 / Button2 / Button3），
            '   不是 0 基的。这里曾经写成 Case 2 / Case 3，导致点「同时删除数据」返回 1
            '   落进了 Case Else 被当成"取消" —— 表现就是"点了没反应，也没提示"。
            Dim choice As Integer = MyMsgBox(
                $"确定要删除实例「{inst.DisplayName}」吗？{vbCrLf}{vbCrLf}" &
                $"它的数据目录是：{inst.HomeDir}{vbCrLf}{vbCrLf}" &
                "「同时删除数据」会连同该目录一起清空（配置、插件、会话、凭据都会丢失）；" &
                "「仅移出列表」则保留目录，方便以后手工找回。",
                "删除实例", "同时删除数据", "仅移出列表", "取消")

            Select Case choice
                Case 1
                    'Button1：删数据
                    If Not ModDSH.DshRemoveInstance(inst.Id, DeleteData:=True) Then
                        Hint($"删除实例「{inst.DisplayName}」失败，详情见日志。", HintType.Red)
                        Return
                    End If
                    Hint($"已删除实例「{inst.DisplayName}」及其数据目录。", HintType.Green)
                Case 2
                    'Button2：保留数据
                    If Not ModDSH.DshRemoveInstance(inst.Id, DeleteData:=False) Then
                        Hint($"移出实例「{inst.DisplayName}」失败，详情见日志。", HintType.Red)
                        Return
                    End If
                    Hint($"已把实例「{inst.DisplayName}」移出列表，数据目录保留。", HintType.Green)
                Case Else
                    'Button3：取消
                    Return
            End Select

            '选中项可能已经被删了，确保回落
            Dim remaining As List(Of DshInstance) = ModDSH.DshInstances
            If remaining.Count > 0 Then ModDSH.DshSelectedInstance = remaining(0)
        Catch ex As Exception
            Logger.Error(ex, "DSH：删除实例失败")
            Hint($"删除失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    ''' <summary>端口输入框变化 → 写回当前实例。</summary>
    Private Sub TextPort_TextChanged() Handles TextPort.ValidatedTextChanged
        '程序化改写不该被当成用户输入
        If IsUpdatingPort Then Return

        Try
            Dim inst As DshInstance = ModDSH.DshSelectedInstance
            If inst Is Nothing Then Return

            Dim raw As String = If(TextPort.Text, "").Trim()
            If raw.Length = 0 Then Return

            Dim value As Integer
            If Not Integer.TryParse(raw, value) Then
                Hint("端口必须是数字，填 0 表示自动选择。", HintType.Red)
                RefreshPortBox()
                Return
            End If
            If value < 0 OrElse value > 65535 Then
                Hint("端口范围是 0 ~ 65535。", HintType.Red)
                RefreshPortBox()
                Return
            End If
            If value = inst.Port Then Return

            If ModDSH.DshSetInstancePort(inst.Id, value) Then
                Hint(If(value = 0, "已改为自动选择端口。", $"端口已改为 {value}。"), HintType.Green)
            Else
                Hint("实例正在运行，无法修改端口。请先停止服务。", HintType.Red)
                RefreshPortBox()
            End If
        Catch ex As Exception
            Logger.Error(ex, "DSH：修改端口失败")
            Hint($"修改端口失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    ''' <summary>切到本地模型模式。</summary>
    Private Sub BtnModeLocal_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnModeLocal.Click
        CurrentMode = DshMode.Local
        Settings.Set("DshModelMode", CInt(DshMode.Local))
        RefreshModeButtons()
        RefreshLaunchButton()
        Hint("本地模型尚未实现，该选项暂时不可用。")
    End Sub

    ''' <summary>切到 API Key 模式。</summary>
    Private Sub BtnModeOnline_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnModeOnline.Click
        CurrentMode = DshMode.Online
        Settings.Set("DshModelMode", CInt(DshMode.Online))
        RefreshModeButtons()
        RefreshLaunchButton()
        Hint("已切换到 API Key 模式，可在右侧面板配置密钥。", HintType.Green)
    End Sub

    ''' <summary>版本选择按钮：跳到下载页的「镜像版本」。</summary>
    Private Sub BtnVersion_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnVersion.Click
        Try
            FrmMain.PageChange(New FormMain.PageStackData With {.Page = FormMain.PageType.Download},
                               FormMain.PageSubType.DownloadMirror)
        Catch ex As Exception
            Logger.Error(ex, "DSH：跳转镜像版本页失败")
            Hint($"无法打开镜像版本页：{ex.Message}", HintType.Red)
        End Try
    End Sub

    ''' <summary>插件管理按钮：跳到下载页的「插件社区」。</summary>
    Private Sub BtnPlugins_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnPlugins.Click
        Try
            FrmMain.PageChange(New FormMain.PageStackData With {.Page = FormMain.PageType.Download},
                               FormMain.PageSubType.DownloadPlugin)
        Catch ex As Exception
            Logger.Error(ex, "DSH：跳转插件社区页失败")
            Hint($"无法打开插件社区页：{ex.Message}", HintType.Red)
        End Try
    End Sub

    ''' <summary>
    ''' 取消按钮：仅退出启动中视图，不中断后台装配。
    ''' </summary>
    ''' <remarks>
    ''' 这里刻意**不**真的取消 —— 运行时装配（下载 Node/pnpm/dsh）
    ''' 是个长事务，中途打断会留下半成品目录。让它跑完更安全，
    ''' 用户只是回到常态视图而已，装配仍在后台继续。
    ''' </remarks>
    Private Sub BtnCancel_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnCancel.Click
        HideLaunchingView()
        Hint("已返回，DSH 装配仍在后台继续。")
    End Sub

#End Region

#Region "浏览器"

    ''' <summary>用配置的浏览器打开当前实例的 DSH 界面。</summary>
    Public Sub OpenDshInBrowser(Optional ReadyUrl As String = Nothing)
        Try
            If Not String.IsNullOrWhiteSpace(ReadyUrl) Then
                DshBrowser.OpenDsh(ReadyUrl)
                Return
            End If
            DshBrowser.OpenInstance(ModDSH.DshSelectedInstance)
        Catch ex As Exception
            Logger.Error(ex, "DSH：打开浏览器失败")
            Hint($"打开浏览器失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

#End Region

#Region "视图切换"

    ''' <summary>
    ''' 切到「启动中」视图。
    ''' </summary>
    ''' <remarks>
    ''' PCL_DSH：这里**刻意不做缩放/透明度动画**。
    '''
    ''' 早期版本让 <c>PanInput</c> 缩到 0.8 倍淡出、<c>PanLaunching</c> 从 0.8 倍淡入，
    ''' 想做出 PCL 原版启动页那种「切换感」。但那个做法有两个实际问题：
    '''   1. <c>AaScaleTransform</c> 的位移量是**增量**，且在 <c>AniStart</c> 构建那一刻
    '''      就求值了；若初值写在延迟的 <c>AaCode</c> 里，两者永远对不上，
    '''      结果是 <c>PanInput</c> 永久停在 0.8 倍缩放（用户报障：「点了启动左边栏变小了」）
    '''   2. 即使加了防御性复位，缩放本身仍会让左栏的**视觉宽度**在启动前后不一致，
    '''      而这一栏的宽度是用户日常对着看的，任何跳动都很刺眼
    '''
    ''' 现在改成**纯可见性切换**：没有中间态，就没有中间态可以残留。
    ''' 代价是少了动画，但换来的是「启动前后布局完全一致」——
    ''' 这正是用户明确要求的行为。
    ''' </remarks>
    Private Sub ShowLaunchingView()
        If IsLaunchingView Then Return
        IsLaunchingView = True
        Try
            PanInput.Visibility = Visibility.Collapsed
            PanLaunching.Visibility = Visibility.Visible
            PanLaunching.Opacity = 1
            LoadLaunching.State.LoadingState = MyLoading.MyLoadingState.Run
        Catch ex As Exception
            Logger.Error(ex, "DSH：切换到启动中视图失败")
        End Try
    End Sub

    ''' <summary>切回「常态」视图。</summary>
    ''' <remarks>与 <see cref="ShowLaunchingView"/> 对称，同样不做动画。</remarks>
    Private Sub HideLaunchingView()
        If Not IsLaunchingView Then Return
        IsLaunchingView = False
        Try
            PanLaunching.Visibility = Visibility.Collapsed
            PanInput.Visibility = Visibility.Visible
            PanInput.Opacity = 1
            LoadLaunching.State.LoadingState = MyLoading.MyLoadingState.Stop
        Catch ex As Exception
            Logger.Error(ex, "DSH：切换回常态视图失败")
        End Try
    End Sub

#End Region

#Region "页面导航"

    ''' <summary>当前页面 ID（本页只有一个子页面：在线配置）。</summary>
    Public ReadOnly Property PageID As FormMain.PageSubType
        Get
            Return FormMain.PageSubType.DshOnline
        End Get
    End Property

    ''' <summary>
    ''' 取得子页面实例（本页只有一个：在线配置）。
    ''' </summary>
    ''' <remarks>
    ''' 必须保留这组方法：<see cref="FormMain.PageChangeActual"/> 对
    ''' <c>PageType.Launch</c> 会调用 <c>FrmDshLeft.PageGet(SubType)</c>。
    ''' </remarks>
    Public Function PageGet(Optional SubType As FormMain.PageSubType = FormMain.PageSubType.Default) As FrameworkElement
        If FrmOnline Is Nothing Then FrmOnline = New PageDSHOnline
        Return FrmOnline
    End Function

    ''' <summary>切换到指定子页面（本页只有一个，等价于回到自己）。</summary>
    Public Sub PageChange(Optional SubType As FormMain.PageSubType = FormMain.PageSubType.Default)
        FrmMain.PageChange(New FormMain.PageStackData With {.Page = FormMain.PageType.Launch})
    End Sub

#End Region

#Region "刷新接口"

    ''' <summary>F5 / 外部触发的刷新。</summary>
    Public Sub Refresh() Implements IRefreshable.Refresh
        RefreshAll()
        Hint("已刷新 DSH 状态。")
    End Sub

#End Region

#Region "状态徽标图标"

    '内联的 SVG path 数据（与 PCL 其他页面保持一致的做法）

    ''' <summary>对勾：运行中。</summary>
    Private Const LogoCheck As String =
        "M743.308346 410.930535l-260.477761 260.477761c-15.864068 15.864068-41.963018 15.864068-57.827087 0l-144.823588-144.823588c-15.864068-15.864068-15.864068-41.963018 0-57.827087 8.187906-8.187906 18.422789-11.770115 29.169415-11.770115 10.234883 0 20.981509 4.093953 29.169416 11.770115l115.654173 115.654173L685.993003 352.591704c15.864068-15.864068 41.963018-15.864068 57.827087 0 15.352324 16.375812 15.352324 42.474763-0.511744 58.338831z"

    ''' <summary>圆环：未运行。</summary>
    Private Const LogoIdle As String =
        "M512 128q159 0 271.5 112.5T896 512q0 159-112.5 271.5T512 896q-159 0-271.5-112.5T128 512q0-159 112.5-271.5T512 128z m0 96q-120 0-204 84t-84 204q0 120 84 204t204 84q120 0 204-84t84-204q0-120-84-204t-204-84z"

    ''' <summary>十字：启动中 / 停止中。</summary>
    Private Const LogoLoading As String =
        "M512 128q22 0 37 15t15 37v154q0 22-15 37t-37 15-37-15-15-37V180q0-22 15-37t37-15z m0 640q22 0 37 15t15 37v110q0 22-15 37t-37 15-37-15-15-37V820q0-22 15-37t37-15z m384-256q0 22-15 37t-37 15H690q-22 0-37-15t-15-37 15-37 37-15h154q22 0 37 15t15 37zM384 512q0 22-15 37t-37 15H180q-22 0-37-15t-15-37 15-37 37-15h152q22 0 37 15t15 37z"

    ''' <summary>感叹号：启动失败。</summary>
    Private Const LogoError As String =
        "M512 928q-86 0-161.5-32.5T217 810 131.5 676.5 99 515t32.5-161.5T217 220 350.5 134.5 512 102t161.5 32.5T807 220t65.5 133.5T905 515t-32.5 161.5T807 810t-133.5 85.5T512 928z m0-74q70 0 131.5-26.5T750 754t73.5-106.5T850 515t-26.5-132.5T750 276t-106.5-73.5T512 176t-131.5 26.5T274 276t-73.5 106.5T174 515t26.5 132.5T274 754t106.5 73.5T512 854z M512 320q22 0 37 15t15 37v230q0 22-15 37t-37 15-37-15-15-37V372q0-22 15-37t37-15z m0 448q26 0 45 19t19 45-19 45-45 19-45-19-19-45 19-45 45-19z"

#End Region

End Class
