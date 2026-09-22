Imports System.Windows.Media

''' <summary>
''' DSH 运行日志页 —— 服务侧的唯一落脚点。
'''
''' 内容：
'''   1. <b>我的实例</b>：全部实例的总览（名字 / 端口 / 状态），运行中的可以直接打开界面
'''   2. <b>服务状态</b>：当前实例的状态、地址与三个操作按钮
'''   3. <b>界面打开方式</b>：当前浏览器配置的说明
'''   4. <b>服务输出</b>：当前实例进程的 stdout/stderr（内存缓冲，不读磁盘）
'''
''' 前三条里的后三条原本都在启动页右栏的 DSH 控制台页上
''' （那个页面现已改名为 <see cref="PageDSHOnline"/>，专门承载在线模式配置）。
''' 多实例改造时按需求把启动页右栏的内容整体搬到了这里。
'''
''' ⚠️ 这一页由顶部菜单栏的「日志」按钮进入，在 <see cref="FormMain.PageChangeActual"/> 里
''' 配的是一个空的 <see cref="MyPageLeft"/>（<c>New MyPageLeft</c>），所以本页独占整个右栏宽度。
''' </summary>
Public Class PageDSHLog
    Implements IRefreshable

    ''' <summary>AI 分析是否正在进行（防止连点起多个请求）。</summary>
    Private IsAiAnalyzing As Boolean = False

#Region "状态"

    Private IsLoad As Boolean = False

    ''' <summary>防止状态事件引起的刷新风暴（一次状态变更会连带触发多次 RefreshAll）。</summary>
    Private IsRefreshing As Boolean = False

#End Region

#Region "初始化"

    Private Sub PageDSHLog_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        If Not IsLoad Then
            IsLoad = True
            AddHandler ModDSH.DshInstanceStateChanged, AddressOf OnInstanceStateChanged
            AddHandler ModDSH.DshInstancesChanged, AddressOf OnInstancesChanged
            AddHandler ModDSH.DshSelectedInstanceChanged, AddressOf OnSelectedInstanceChanged
        End If
        RefreshAll()
    End Sub

    ''' <summary>
    ''' 页面重新进入时刷新。
    ''' </summary>
    ''' <remarks>
    ''' <c>Loaded</c> 只在第一次挂载时可靠，切走再切回来不一定重新触发。
    ''' 本页继承 <see cref="MyPageRight"/>，有 <c>PageEnter</c> 事件可用。
    ''' </remarks>
    Public Sub PageOnEnterHook() Handles Me.PageEnter
        RefreshAll()
    End Sub

    Private Sub OnInstanceStateChanged(Instance As DshInstance)
        '事件已在 UI 线程触发（见 ModDSH.DshSetState）
        RefreshAll()
    End Sub

    Private Sub OnInstancesChanged()
        RefreshAll()
    End Sub

    Private Sub OnSelectedInstanceChanged(Instance As DshInstance)
        RefreshAll()
    End Sub

#End Region

#Region "刷新"

    Private Sub RefreshAll()
        '状态变更 → 事件 → RefreshAll 这条链路可能在同一帧里被触发多次，
        '加个闸避免重复重建控件（重建实例列表会闪）
        If IsRefreshing Then Return
        IsRefreshing = True
        Try
            RefreshInstanceOverview()
            RefreshState()
            RefreshBrowserInfo()
            RefreshOutput()
            RefreshKeyPaths()
        Finally
            IsRefreshing = False
        End Try
    End Sub

    ''' <summary>重建「我的实例」总览列表。</summary>
    Private Sub RefreshInstanceOverview()
        Try
            PanInstanceOverview.Children.Clear()

            Dim all As List(Of DshInstance) = ModDSH.DshInstances
            For Each inst In all
                PanInstanceOverview.Children.Add(BuildInstanceRow(inst))
            Next

            BtnStopAll.IsEnabled = all.Any(Function(x) x.HasLiveProcess OrElse x.State <> ModDSH.DshState.Stopped)
        Catch ex As Exception
            Logger.Error(ex, "DSH：刷新实例总览失败")
        End Try
    End Sub

    ''' <summary>
    ''' 构建一行实例总览。
    ''' </summary>
    ''' <remarks>
    ''' 用代码动态构建而不是 XAML 模板：实例数量是 0~4 的动态值，
    ''' 写死 4 套 XAML 再逐个显隐既啰嗦又容易漏改。
    ''' PCL 里本来就有动态构建控件的惯例（见 ModDownloadLib / ResourceVersion）。
    ''' </remarks>
    Private Function BuildInstanceRow(inst As DshInstance) As FrameworkElement
        Dim row As New Border With {
            .BorderThickness = New Thickness(1),
            .CornerRadius = New CornerRadius(4),
            .Padding = New Thickness(13, 10, 13, 10),
            .Margin = New Thickness(0, 0, 0, 8)
        }
        row.SetResourceReference(Border.BorderBrushProperty, "ColorBrushGray5")

        Dim grid As New Grid()
        grid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
        grid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

        ' ── 左：名字 + 端口/状态 ──
        Dim left As New StackPanel()
        Dim title As New TextBlock With {
            .FontSize = 13.5,
            .Text = inst.DisplayName,
            .TextTrimming = TextTrimming.CharacterEllipsis
        }
        title.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        left.Children.Add(title)

        Dim detail As New TextBlock With {
            .FontSize = 12,
            .Margin = New Thickness(0, 4, 0, 0),
            .Text = inst.DisplayDetail
        }
        detail.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
        left.Children.Add(detail)

        If inst.State = ModDSH.DshState.Failed AndAlso Not String.IsNullOrWhiteSpace(inst.LastError) Then
            Dim errText As New TextBlock With {
                .FontSize = 12,
                .Margin = New Thickness(0, 4, 0, 0),
                .TextWrapping = TextWrapping.Wrap,
                .Text = "失败原因：" & inst.LastError
            }
            errText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushRedLight")
            left.Children.Add(errText)
        End If

        Grid.SetColumn(left, 0)
        grid.Children.Add(left)

        ' ── 右：操作按钮 ──
        Dim right As New StackPanel With {
            .Orientation = Orientation.Horizontal,
            .VerticalAlignment = VerticalAlignment.Center,
            .Margin = New Thickness(12, 0, 0, 0)
        }

        '闭包捕获一份显式副本：虽然 VB 的 For Each 变量是每次迭代新建的，
        '但显式写出来更不容易在后续改动里踩坑
        Dim captured As DshInstance = inst

        If inst.IsRunning Then
            Dim btnOpen As New MyButton With {
                .Text = "打开界面", .Height = 28, .ColorType = MyButton.ColorState.Highlight
            }
            btnOpen.TextPadding = New Thickness(12, 0, 12, 0)
            AddHandler btnOpen.Click, Sub(s, e) DshBrowser.OpenInstance(captured)
            right.Children.Add(btnOpen)
        ElseIf inst.IsBusy OrElse DshLauncher.IsStarting(inst) Then
            Dim btnBusy As New MyButton With {.Text = "处理中…", .Height = 28, .IsEnabled = False}
            btnBusy.TextPadding = New Thickness(12, 0, 12, 0)
            right.Children.Add(btnBusy)
        Else
            Dim btnStart As New MyButton With {.Text = "启动", .Height = 28}
            btnStart.TextPadding = New Thickness(12, 0, 12, 0)
            AddHandler btnStart.Click, Sub(s, e) StartInstance(captured)
            right.Children.Add(btnStart)
        End If

        Grid.SetColumn(right, 1)
        grid.Children.Add(right)

        row.Child = grid
        Return row
    End Function

    ''' <summary>刷新状态卡（跟随「当前实例」）。</summary>
    Private Sub RefreshState()
        Try
            Dim inst As DshInstance = ModDSH.DshSelectedInstance
            If inst Is Nothing Then
                LabStateTitle.Text = "没有可用实例"
                LabStateDetail.Text = "请先在启动页新建一个实例。"
                BtnStartStop.IsEnabled = False
                BtnOpenUi.IsEnabled = False
                LabAddress.Visibility = Visibility.Collapsed
                Return
            End If

            '把当前实例的名字带进标题，多实例时才不会看混
            Dim prefix As String = inst.DisplayName

            Select Case inst.State
                Case ModDSH.DshState.Running
                    LabStateTitle.Text = $"{prefix} 正在运行"
                    LabStateTitle.Foreground = New SolidColorBrush(Color.FromRgb(&H2E, &HA0, &H55))
                    LabStateDetail.Text = "服务已就绪，点击「打开 DSH 界面」即可在浏览器中使用。"
                    BtnStartStop.Text = "停止 DSH"
                    BtnStartStop.IsEnabled = True
                    BtnOpenUi.IsEnabled = True
                Case ModDSH.DshState.Starting, ModDSH.DshState.CheckingRuntime, ModDSH.DshState.Installing
                    LabStateTitle.Text = $"{prefix} 正在启动"
                    LabStateTitle.Foreground = TryFindResource("ColorBrushGray1")
                    LabStateDetail.Text = "正在准备运行时并等待服务就绪，进度见启动页的启动中视图。"
                    BtnStartStop.Text = "停止 DSH"
                    BtnStartStop.IsEnabled = True
                    BtnOpenUi.IsEnabled = False
                Case ModDSH.DshState.Stopping
                    LabStateTitle.Text = $"{prefix} 正在停止"
                    LabStateTitle.Foreground = TryFindResource("ColorBrushGray1")
                    LabStateDetail.Text = "正在回收服务进程，请稍候。"
                    BtnStartStop.Text = "停止 DSH"
                    BtnStartStop.IsEnabled = False
                    BtnOpenUi.IsEnabled = False
                Case ModDSH.DshState.Failed
                    LabStateTitle.Text = $"{prefix} 启动失败"
                    LabStateTitle.Foreground = New SolidColorBrush(Color.FromRgb(&HDF, &H50, &H40))
                    LabStateDetail.Text = If(String.IsNullOrWhiteSpace(inst.LastError),
                        "启动过程中发生错误。可查看下方「服务输出」了解详情。",
                        "失败原因：" & inst.LastError)
                    BtnStartStop.Text = "重新启动"
                    BtnStartStop.IsEnabled = True
                    BtnOpenUi.IsEnabled = False
                Case Else
                    LabStateTitle.Text = $"{prefix} 未运行"
                    LabStateTitle.Foreground = TryFindResource("ColorBrushGray1")
                    LabStateDetail.Text = "点击「启动 DSH」按钮即可启动服务，启动完成后会自动为你打开浏览器。"
                    BtnStartStop.Text = "启动 DSH"
                    BtnStartStop.IsEnabled = True
                    BtnOpenUi.IsEnabled = False
            End Select

            '带 token 的原始 URL 不展示，避免用户复制走一个会过期的链接。
            Dim url As String = inst.DisplayUrl
            LabAddress.Text = If(inst.IsRunning AndAlso Not String.IsNullOrWhiteSpace(url),
                                 $"服务地址：{url}　（数据目录：{inst.HomeDir}）",
                                 $"数据目录：{inst.HomeDir}")
            LabAddress.Visibility = Visibility.Visible
        Catch ex As Exception
            Logger.Error(ex, "DSH：刷新服务状态失败")
        End Try
    End Sub

    ''' <summary>刷新界面承载方式的说明文案。</summary>
    Private Sub RefreshBrowserInfo()
        Try
            Dim desc As String = DshBrowser.DshBrowserDescribe()
            LabBrowser.Text = If(DshBrowser.DshBrowserShouldAutoOpen,
                $"当前使用：{desc}（服务就绪后自动打开）",
                $"当前使用：{desc}（不自动打开，需手动点击）")
        Catch ex As Exception
            Logger.Warn($"DSH：读取浏览器配置失败（可忽略）：{ex.Message}")
            LabBrowser.Text = "当前使用：系统默认浏览器"
        End Try
    End Sub

    ''' <summary>
    ''' 刷新服务输出（当前实例的内存缓冲，最近 300 行）。
    ''' </summary>
    Private Sub RefreshOutput()
        Try
            Dim inst As DshInstance = ModDSH.DshSelectedInstance
            LabLogPath.Text = $"工作目录：{ModDSH.DshRoot}"

            If inst Is Nothing Then
                LabOutput.Text = "（没有可用实例）"
                Return
            End If

            Dim text As String = inst.GetRecentOutput(200)
            LabOutput.Text = If(String.IsNullOrWhiteSpace(text) OrElse text = "（无输出）",
                "（暂无输出）" & vbCrLf & "启动 DSH 后，服务进程的标准输出与错误输出会实时显示在这里。",
                text)
        Catch ex As Exception
            Logger.Warn($"DSH：读取服务输出失败（可忽略）：{ex.Message}")
            LabOutput.Text = "（读取输出失败）"
        End Try
    End Sub

    ''' <summary>
    ''' 刷新「密钥与配置是怎么生效的」卡片里的路径行。
    ''' </summary>
    ''' <remarks>
    ''' 两个文件都要列出来：密钥在 .credentials.yaml，地址与协议在 settings.yaml。
    ''' 混在一起讲会让用户以为配置只有一个地方。
    ''' </remarks>
    Private Sub RefreshKeyPaths()
        Try
            Dim inst As DshInstance = ModDSH.DshSelectedInstance
            If inst Is Nothing Then
                LabKeyPath.Text = ""
                Return
            End If

            Dim providers As List(Of DshApiConfig.DshProvider) = DshApiConfig.LoadProviders()
            Dim provider As DshApiConfig.DshProvider = DshApiConfig.GetInstanceProvider(inst, providers)
            Dim keyRef As String = If(provider Is Nothing, DshApiConfig.KeyRefOfficial, provider.KeyRef)

            Dim lines As New List(Of String) From {
                $"当前接入方式：{If(provider Is Nothing, "DeepSeek 官方 API", provider.DisplayLabel)}",
                $"实际请求地址：{DshApiConfig.ResolveRequestUrl(provider)}",
                $"凭据文件：{inst.CredentialsFile}",
                $"凭据名：{keyRef}（{(If(DshCredentials.HasRef(keyRef, inst.HomeDir), "已配置", "未配置"))}）",
                $"设置文档：{DshSettings.SettingsFileFor(inst)}"
            }
            LabKeyPath.Text = String.Join(vbCrLf, lines)
        Catch ex As Exception
            Logger.Error(ex, "DSH：刷新配置路径失败")
        End Try
    End Sub

    ''' <summary>由 FormMain / 自定义事件触发的刷新入口。</summary>
    Public Sub Refresh() Implements IRefreshable.Refresh
        RefreshAll()
    End Sub

#End Region

#Region "按钮事件"

    ''' <summary>打开当前实例的 DSH 界面。</summary>
    Private Sub BtnOpenUi_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnOpenUi.Click
        Try
            Dim inst As DshInstance = ModDSH.DshSelectedInstance
            If inst Is Nothing Then
                Hint("没有可用实例。", HintType.Red)
                Return
            End If
            If Not inst.IsRunning Then
                Hint($"实例「{inst.DisplayName}」尚未运行，请先启动服务。", HintType.Red)
                Return
            End If
            DshBrowser.OpenInstance(inst)
        Catch ex As Exception
            Logger.Error(ex, "DSH：打开界面失败")
            Hint($"打开界面失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    ''' <summary>启动 / 停止当前实例。</summary>
    Private Sub BtnStartStop_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnStartStop.Click
        Dim inst As DshInstance = ModDSH.DshSelectedInstance
        If inst Is Nothing Then Return
        If inst.IsRunning OrElse inst.HasLiveProcess Then
            DshLauncher.StopAsync(inst)
        Else
            StartInstance(inst)
        End If
    End Sub

    ''' <summary>
    ''' 启动一个实例，并按配置决定是否自动打开浏览器。
    ''' </summary>
    ''' <remarks>
    ''' 用**一次性订阅**而不是在构造函数里长期订阅：长期订阅会让每次启动都弹一遍提示，
    ''' 包括用户在启动页点的那次。这里订阅后立刻摘掉，只处理本次。
    ''' </remarks>
    Private Sub StartInstance(inst As DshInstance)
        If inst Is Nothing Then Return
        ModDSH.DshSelectedInstance = inst

        Dim handler As DshLauncher.LaunchFinishedEventHandler = Nothing
        handler =
            Sub(target As DshInstance, success As Boolean, readyUrl As String, message As String)
                RemoveHandler DshLauncher.LaunchFinished, handler
                If target IsNot inst Then Return

                If success Then
                    Hint($"实例「{inst.DisplayName}」启动成功！", HintType.Green)
                    If DshBrowser.DshBrowserShouldAutoOpen Then
                        DshBrowser.OpenInstance(inst)
                    Else
                        Hint("DSH 已就绪，可点击「打开 DSH 界面」进入。")
                    End If
                Else
                    Hint($"启动失败：{If(String.IsNullOrWhiteSpace(message), "未知原因", message)}", HintType.Red)
                End If
            End Sub
        AddHandler DshLauncher.LaunchFinished, handler

        DshLauncher.StartAsync(inst)
    End Sub

    ''' <summary>刷新状态。</summary>
    Private Sub BtnRefresh_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnRefresh.Click
        RefreshAll()
        Hint("状态已刷新。", HintType.Green)
    End Sub

    ''' <summary>刷新实例总览。</summary>
    Private Sub BtnRefreshInstances_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnRefreshInstances.Click
        RefreshInstanceOverview()
        Hint("实例列表已刷新。", HintType.Green)
    End Sub

    ''' <summary>跳到启动页去管理实例。</summary>
    Private Sub BtnOpenInstances_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnOpenInstances.Click
        Try
            FrmMain.PageChange(New FormMain.PageStackData With {.Page = FormMain.PageType.Launch})
        Catch ex As Exception
            Logger.Error(ex, "DSH：跳转启动页失败")
            Hint($"无法打开启动页：{ex.Message}", HintType.Red)
        End Try
    End Sub

    ''' <summary>停止全部实例。</summary>
    Private Sub BtnStopAll_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnStopAll.Click
        Try
            Dim running As List(Of DshInstance) =
                ModDSH.DshInstances.Where(
                    Function(x) x.HasLiveProcess OrElse x.State <> ModDSH.DshState.Stopped).ToList()

            If running.Count = 0 Then
                Hint("当前没有正在运行的实例。")
                Return
            End If

            DshLauncher.StopAllAsync()
            Hint($"正在停止 {running.Count} 个实例……")
        Catch ex As Exception
            Logger.Error(ex, "DSH：停止全部实例失败")
            Hint($"停止失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    ''' <summary>只刷新输出。</summary>
    Private Sub BtnRefreshOutput_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnRefreshOutput.Click
        RefreshOutput()
    End Sub

    ''' <summary>
    ''' 「AI 分析日志」：把脱敏后的日志交给模型，展示诊断结果。
    ''' </summary>
    ''' <remarks>
    ''' 刻意**只分析、不动手**：结果只作为文本 + 建议展示，
    ''' 需要执行什么由用户自己点。理由见 <see cref="DshLogAi"/> 的注释。
    ''' </remarks>
    Private Sub BtnAnalyzeLog_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnAnalyzeLog.Click
        If IsAiAnalyzing Then Return
        Try
            Dim inst As DshInstance = ModDSH.DshSelectedInstance
            If inst Is Nothing AndAlso ModDSH.DshInstances.Count > 0 Then inst = ModDSH.DshInstances(0)

            IsAiAnalyzing = True
            BtnAnalyzeLog.IsEnabled = False
            CardAiResult.Visibility = Visibility.Visible
            LabAiMeta.Text = "正在准备……"
            LabAiSummary.Text = ""
            LabAiAnalysis.Text = ""
            PanAiSuggestions.Children.Clear()

            RunInNewThread(
                Sub()
                    Dim diag As DshLogAi.DshAiDiagnosis = Nothing
                    Try
                        diag = DshLogAi.DshAnalyzeLogs(
                            inst,
                            Sub(message As String)
                                RunInUi(Sub() LabAiMeta.Text = message)
                            End Sub)
                    Catch ex As Exception
                        Logger.Error(ex, "DSH：AI 分析日志失败")
                    End Try

                    RunInUi(
                        Sub()
                            IsAiAnalyzing = False
                            BtnAnalyzeLog.IsEnabled = True
                            RenderAiDiagnosis(diag)
                        End Sub)
                End Sub, "DSH AI 分析日志")
        Catch ex As Exception
            IsAiAnalyzing = False
            BtnAnalyzeLog.IsEnabled = True
            Logger.Error(ex, "DSH：启动 AI 分析失败")
            Hint($"无法启动分析：{ex.Message}", HintType.Red)
        End Try
    End Sub

    ''' <summary>把诊断结果铺到界面上。</summary>
    Private Sub RenderAiDiagnosis(diag As DshLogAi.DshAiDiagnosis)
        Try
            CardAiResult.Visibility = Visibility.Visible
            PanAiSuggestions.Children.Clear()

            If diag Is Nothing Then
                LabAiMeta.Text = "分析失败：没有拿到结果，详见日志。"
                LabAiSummary.Text = ""
                LabAiAnalysis.Text = ""
                Return
            End If

            If Not diag.Success Then
                LabAiMeta.Text = "分析未能完成。"
                LabAiSummary.Text = ""
                LabAiAnalysis.Text = diag.ErrorMessage
                Return
            End If

            Dim meta As String = $"由 {diag.ProviderName}（{diag.Model}）分析"
            If diag.RedactedCount > 0 Then meta &= $" · 已脱敏 {diag.RedactedCount} 处敏感信息"
            meta &= $" · 送出 {diag.SentChars} 字符"
            LabAiMeta.Text = meta

            LabAiSummary.Text = If(String.IsNullOrWhiteSpace(diag.Summary), "（模型没有给出结论）", diag.Summary)
            LabAiAnalysis.Text = diag.Analysis

            If diag.Suggestions.Count = 0 Then
                LabAiSuggestTitle.Visibility = Visibility.Collapsed
                Return
            End If
            LabAiSuggestTitle.Visibility = Visibility.Visible
            For i As Integer = 0 To diag.Suggestions.Count - 1
                PanAiSuggestions.Children.Add(BuildSuggestionRow(i + 1, diag.Suggestions(i)))
            Next
        Catch ex As Exception
            Logger.Error(ex, "DSH：渲染 AI 诊断结果失败")
        End Try
    End Sub

    ''' <summary>一条建议（序号 + 正文）。</summary>
    Private Function BuildSuggestionRow(Index As Integer, Text As String) As FrameworkElement
        Dim row As New Grid With {.Margin = New Thickness(0, 0, 0, 6)}
        row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
        row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})

        Dim num As New TextBlock With {
            .Text = Index & ".",
            .FontSize = 12.5,
            .Margin = New Thickness(0, 0, 8, 0),
            .VerticalAlignment = VerticalAlignment.Top
        }
        num.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
        Grid.SetColumn(num, 0)
        row.Children.Add(num)

        Dim body As New TextBlock With {
            .Text = Text,
            .FontSize = 12.5,
            .TextWrapping = TextWrapping.Wrap,
            .LineHeight = 20
        }
        body.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2")
        Grid.SetColumn(body, 1)
        row.Children.Add(body)

        Return row
    End Function

    ''' <summary>打开 DSH 数据目录。</summary>
    Private Sub BtnOpenFolder_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnOpenFolder.Click
        Try
            ModDSH.DshEnsureDirectories()
            OpenExplorer(ModDSH.DshRoot)
        Catch ex As Exception
            Logger.Error(ex, "打开 DSH 数据目录失败")
            Hint($"无法打开目录：{ex.Message}", HintType.Red)
        End Try
    End Sub

#End Region

End Class
