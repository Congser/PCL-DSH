Imports System.Windows.Media

''' <summary>
''' 网络工具箱 —— 原来「百宝箱」的位置。
'''
''' 三块内容：
'''   1. <b>IP 优选 + hosts 加速</b>：多源 DNS 收集候选 IP → 并发 TCP 测速 →
'''      把最快的写进 hosts 的受管区块
'''   2. <b>镜像站</b>：反代可用性测速与跳转
'''   3. <b>网络诊断</b>：DNS → TCP → TLS → HTTP 逐项计时，指出卡在哪一环
'''
''' ⚠️ 写 hosts 是**系统级修改**，所以：
'''   - 必须提权（<see cref="DshNetTool.IsElevated"/> 为 False 时按钮会给出明确提示）
'''   - 只动受管区块，区块外一行都不碰
'''   - 首次修改前自动备份到 <see cref="DshNetTool.HostsBackupFile"/>
'''   - 有独立的一键还原
'''
''' ⚠️ 本类里那批 <c>Public Shared</c> 方法（Jrrp / RubbishClear / MemoryOptimize /
''' StartCustomDownload / GetRandomHint …）是原版百宝箱的占位实现，
''' **被 ModEvent / ModMain / Application / ModLaunch 多处调用**，
''' 不能删，只能保留。
''' </summary>
Public Class PageOtherTest
    Implements IRefreshable

#Region "状态"

    Private IsLoad As Boolean = False

    ''' <summary>测速 / 诊断期间为 True。</summary>
    Private IsBusy As Boolean = False

    ''' <summary>当前的域名勾选列表。</summary>
    Private Targets As List(Of DshNetTool.AccelTarget) = Nothing

    ''' <summary>当前的镜像列表。</summary>
    Private Mirrors As List(Of DshNetTool.MirrorEntry) = Nothing

    ''' <summary>域名 → 对应的复选框（点「开始优选」时读它们的勾选状态）。</summary>
    Private ReadOnly TargetChecks As New Dictionary(Of String, MyCheckBox)(StringComparer.OrdinalIgnoreCase)

    ''' <summary>最近一次优选结果（域名 → 结果）。</summary>
    Private ReadOnly Optimizations As New Dictionary(Of String, DshNetTool.HostOptimization)(StringComparer.OrdinalIgnoreCase)

    ''' <summary>程序化改写诊断目标下拉框时置位。</summary>
    Private IsUpdatingDiagCombo As Boolean = False

#End Region

#Region "生命周期"

    Private Sub PageOtherTest_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        If Not IsLoad Then
            IsLoad = True
            Targets = DshNetTool.LoadTargets()
            Mirrors = DshNetTool.LoadMirrors()

            BuildTargetList()
            BuildMirrorList()
            BuildDiagCombo()
        End If
        RefreshAdminStatus()
        RefreshHostsState()
    End Sub

    Public Sub PageOnEnterHook() Handles Me.PageEnter
        RefreshAdminStatus()
        RefreshHostsState()
    End Sub

#End Region

#Region "通用界面辅助"

    Private Sub SetBusy(Busy As Boolean, Optional Hint As String = Nothing)
        IsBusy = Busy
        Try
            PanLoad.Visibility = Busy.ToVisibility
            If Busy Then
                LoadStart.State.LoadingState = MyLoading.MyLoadingState.Run
                If Hint IsNot Nothing Then LoadHintText.Text = Hint
            Else
                LoadStart.State.LoadingState = MyLoading.MyLoadingState.Stop
            End If

            BtnOptimize.IsEnabled = Not Busy
            BtnApplyHosts.IsEnabled = Not Busy
            BtnRestoreHosts.IsEnabled = Not Busy
            BtnProbeMirrors.IsEnabled = Not Busy
            BtnDiagnose.IsEnabled = Not Busy
        Catch ex As Exception
            Logger.Error(ex, "DSH：切换工具箱忙碌状态失败")
        End Try
    End Sub

    ''' <summary>生成一行「标题 + 副标题 + 右侧按钮」的卡片式条目。</summary>
    Private Function BuildRow(Title As String, SubTitle As String,
                              BadgeText As String, BadgeOk As Boolean,
                              ParamArray Actions As (Text As String, Handler As Action)()) As FrameworkElement
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

        Dim left As New StackPanel()
        Dim titleText As New TextBlock With {
            .FontSize = 13.5, .Text = Title, .TextTrimming = TextTrimming.CharacterEllipsis
        }
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        left.Children.Add(titleText)

        If Not String.IsNullOrWhiteSpace(SubTitle) Then
            Dim subText As New TextBlock With {
                .FontSize = 11.5, .Margin = New Thickness(0, 4, 0, 0),
                .TextWrapping = TextWrapping.Wrap, .LineHeight = 17, .Text = SubTitle
            }
            subText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4")
            left.Children.Add(subText)
        End If
        Grid.SetColumn(left, 0)
        grid.Children.Add(left)

        Dim right As New StackPanel With {
            .Orientation = Orientation.Horizontal,
            .VerticalAlignment = VerticalAlignment.Center,
            .Margin = New Thickness(12, 0, 0, 0)
        }

        If Not String.IsNullOrWhiteSpace(BadgeText) Then
            Dim badge As New TextBlock With {
                .FontSize = 12.5,
                .Text = BadgeText,
                .VerticalAlignment = VerticalAlignment.Center,
                .Margin = New Thickness(0, 0, 12, 0)
            }
            badge.Foreground = If(BadgeOk,
                                  New SolidColorBrush(Color.FromRgb(&H2E, &HA0, &H55)),
                                  New SolidColorBrush(Color.FromRgb(&HDF, &H50, &H40)))
            right.Children.Add(badge)
        End If

        For Each action In Actions
            Dim btn As New MyButton With {.Text = action.Text, .Height = 28, .Margin = New Thickness(0, 0, 8, 0)}
            btn.TextPadding = New Thickness(12, 0, 12, 0)
            Dim captured As Action = action.Handler
            AddHandler btn.Click, Sub(s, e) captured()
            right.Children.Add(btn)
        Next

        Grid.SetColumn(right, 1)
        grid.Children.Add(right)

        row.Child = grid
        Return row
    End Function

    Private Function MakeInfoText(Text As String) As FrameworkElement
        Dim tb As New TextBlock With {
            .FontSize = 12.5, .TextWrapping = TextWrapping.Wrap, .LineHeight = 20,
            .Margin = New Thickness(0, 0, 0, 6), .Text = Text
        }
        tb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4")
        Return tb
    End Function

#End Region

#Region "卡片 1：IP 优选与 hosts"

    Private Sub RefreshAdminStatus()
        Try
            If DshNetTool.IsElevated Then
                LabAdminStatus.Text = "✓ 当前已以管理员身份运行，可以直接写入 hosts。"
                LabAdminStatus.Foreground = New SolidColorBrush(Color.FromRgb(&H2E, &HA0, &H55))
            Else
                LabAdminStatus.Text = "⚠ 当前不是管理员身份。「开始优选」可以正常用（只读不改系统），" &
                                      "但「应用到 hosts / 还原 hosts」需要管理员权限。" & vbCrLf &
                                      "需要时请关掉 PCL，右键选择「以管理员身份运行」再试。"
                LabAdminStatus.Foreground = New SolidColorBrush(Color.FromRgb(&HF0, &HA8, &H20))
            End If
        Catch ex As Exception
            Logger.Error(ex, "DSH：刷新管理员状态失败")
        End Try
    End Sub

    Private Sub RefreshHostsState()
        Try
            Dim block As Dictionary(Of String, String) = DshNetTool.ReadHostsBlock()
            Dim sb As New StringBuilder()
            sb.AppendLine($"hosts 文件：{DshNetTool.HostsFile}")
            sb.AppendLine($"备份文件：{If(DshRuntime.FileExistsSafe(DshNetTool.HostsBackupFile), DshNetTool.HostsBackupFile, "（尚未创建，首次修改时自动生成）")}")

            If block.Count = 0 Then
                sb.AppendLine("受管区块：不存在（当前没有由 PCL DSH 写入的加速条目）")
            Else
                sb.AppendLine($"受管区块：{block.Count} 条")
                For Each kv In block
                    sb.AppendLine($"  {kv.Key} → {kv.Value}")
                Next
            End If

            LabHostsState.Text = sb.ToString().TrimEnd()
            BtnRestoreHosts.IsEnabled = block.Count > 0 AndAlso Not IsBusy
        Catch ex As Exception
            Logger.Error(ex, "DSH：刷新 hosts 状态失败")
        End Try
    End Sub

    ''' <summary>重建域名勾选列表。</summary>
    Private Sub BuildTargetList()
        Try
            PanTargets.Children.Clear()
            TargetChecks.Clear()

            For Each target In Targets
                Dim cb As New MyCheckBox With {
                    .Text = $"{target.Host}　（{target.Note}）",
                    .Checked = target.Enabled,
                    .Margin = New Thickness(0, 0, 0, 6)
                }
                TargetChecks(target.Host) = cb
                PanTargets.Children.Add(cb)
            Next
        Catch ex As Exception
            Logger.Error(ex, "DSH：构建加速目标列表失败")
        End Try
    End Sub

    ''' <summary>把界面上的勾选状态同步回 Targets。</summary>
    Private Sub SyncTargetChecks()
        For Each kv In TargetChecks
            Dim target = Targets.FirstOrDefault(Function(t) String.Equals(t.Host, kv.Key, StringComparison.OrdinalIgnoreCase))
            If target IsNot Nothing Then target.Enabled = kv.Value.Checked
        Next
    End Sub

    Private Sub BtnOptimize_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnOptimize.Click
        If IsBusy Then Return

        SyncTargetChecks()
        DshNetTool.SaveConfig(Mirrors, Targets)

        Dim selected As List(Of DshNetTool.AccelTarget) = Targets.Where(Function(t) t.Enabled).ToList()
        If selected.Count = 0 Then
            Hint("请至少勾选一个要加速的域名。", HintType.Red)
            Return
        End If

        SetBusy(True, "正在解析候选 IP 并测速……")
        Optimizations.Clear()

        RunInNewThread(
            Sub()
                Dim results As New List(Of DshNetTool.HostOptimization)
                For Each target In selected
                    Try
                        RunInUi(Sub() LoadHintText.Text = $"正在测速：{target.Host}……")
                        results.Add(DshNetTool.OptimizeHost(target))
                    Catch ex As Exception
                        Logger.Error(ex, $"DSH：优选 {target.Host} 失败")
                        results.Add(New DshNetTool.HostOptimization With {
                            .Host = target.Host, .Port = target.Port, .Detail = "优选失败：" & ex.Message
                        })
                    End Try
                Next

                RunInUi(
                    Sub()
                        SetBusy(False)
                        For Each r In results
                            Optimizations(r.Host) = r
                        Next
                        RenderOptimizeResult()
                        RefreshHostsState()

                        Dim okCount As Integer = results.Where(Function(r) r.BestIp IsNot Nothing).Count()
                        If okCount = 0 Then
                            Hint("所有域名都没测出可用 IP。可能是网络受限，可以试试镜像站或代理。", HintType.Red)
                        Else
                            Hint($"优选完成：{okCount}/{results.Count} 个域名找到可用 IP。", HintType.Green)
                        End If
                    End Sub)
            End Sub, "DSH 网络优选")
    End Sub

    Private Sub RenderOptimizeResult()
        Try
            PanOptimizeResult.Children.Clear()

            If Optimizations.Count = 0 Then
                PanOptimizeResult.Children.Add(MakeInfoText("还没有结果。点「开始优选」开始测速（不会改动系统）。"))
                Return
            End If

            For Each target In Targets
                Dim opt As DshNetTool.HostOptimization = Nothing
                If Not Optimizations.TryGetValue(target.Host, opt) Then Continue For

                Dim subtitle As New StringBuilder()
                If opt.BestIp Is Nothing Then
                    If opt.SuccessCount = 0 Then
                        subtitle.AppendLine($"没有可用 IP（候选 {opt.Probes.Count} 个全部握手失败）")
                    Else
                        subtitle.AppendLine($"{opt.SuccessCount} 个候选能握手，但都没有通过 TLS 证书校验 —— 不建议写入 hosts")
                    End If
                Else
                    subtitle.AppendLine($"最优：{opt.BestIp}　{opt.BestLatency} ms　" &
                                        $"（{opt.SuccessCount}/{opt.Probes.Count} 个候选可握手，{opt.Verified.Count} 个通过 TLS 校验）")
                    '把前三个已校验的候选列出来，方便判断稳定性
                    Dim top As List(Of String) =
                        opt.Verified.Take(3).Select(Function(p) $"{p.Ip}（{p.LatencyMs} ms）").ToList()
                    If top.Count > 0 Then subtitle.AppendLine("可选：" & String.Join("　", top))
                End If
                If Not String.IsNullOrWhiteSpace(opt.Detail) Then subtitle.Append(opt.Detail)

                PanOptimizeResult.Children.Add(
                    BuildRow(target.Host, subtitle.ToString().TrimEnd(),
                             If(opt.BestIp Is Nothing, "不可用", $"{opt.BestLatency} ms"),
                             opt.BestIp IsNot Nothing))
            Next
        Catch ex As Exception
            Logger.Error(ex, "DSH：渲染优选结果失败")
        End Try
    End Sub

    Private Sub BtnApplyHosts_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnApplyHosts.Click
        If IsBusy Then Return

        If Optimizations.Count = 0 Then
            Hint("请先点「开始优选」拿到结果，再写入 hosts。", HintType.Red)
            Return
        End If

        Dim entries As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        For Each target In Targets
            Dim opt As DshNetTool.HostOptimization = Nothing
            If Not Optimizations.TryGetValue(target.Host, opt) Then Continue For
            If opt.BestIp IsNot Nothing Then entries(target.Host) = opt.BestIp
        Next

        If entries.Count = 0 Then
            Hint("没有可写入的优选结果。", HintType.Red)
            Return
        End If

        Dim preview As String = String.Join(vbCrLf, entries.Select(Function(kv) $"  {kv.Key} → {kv.Value}"))
        If MyMsgBox(
            "将把以下条目写入 hosts 的受管区块：" & vbCrLf & vbCrLf & preview & vbCrLf & vbCrLf &
            $"文件位置：{DshNetTool.HostsFile}" & vbCrLf &
            "只会替换 PCL DSH 自己的受管区块，你自己加的条目与注释都不会动。" & vbCrLf &
            "首次修改会自动备份原文件。需要管理员权限，会弹一次 UAC。" & vbCrLf & vbCrLf &
            "注意：优选出来的 IP 可能会随时间失效，届时重新点一次「开始优选」即可。",
            "写入 hosts", "写入", "取消", IsWarn:=True) = 2 Then
            Return
        End If

        Dim reason As String = Nothing
        If DshNetTool.ApplyHostsBlock(entries, reason) Then
            Hint($"已写入 {entries.Count} 条加速条目。可能需要刷新 DNS 缓存或重启浏览器才生效。", HintType.Green)
            RefreshHostsState()
        Else
            Hint($"写入失败：{reason}", HintType.Red)
        End If
    End Sub

    Private Sub BtnRestoreHosts_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnRestoreHosts.Click
        If IsBusy Then Return

        If MyMsgBox(
            "将删除 hosts 里由 PCL DSH 写入的受管区块。" & vbCrLf & vbCrLf &
            "你自己添加的条目、注释以及其它内容都**不会**被改动。" & vbCrLf &
            "需要管理员权限，会弹一次 UAC。",
            "还原 hosts", "还原", "取消") = 2 Then
            Return
        End If

        Dim reason As String = Nothing
        If DshNetTool.RemoveHostsBlock(reason) Then
            Hint("hosts 受管区块已移除。", HintType.Green)
            RefreshHostsState()
        Else
            Hint($"还原失败：{reason}", HintType.Red)
        End If
    End Sub

    Private Sub BtnOpenHosts_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnOpenHosts.Click
        Try
            OpenExplorer(IO.Path.GetDirectoryName(DshNetTool.HostsFile))
        Catch ex As Exception
            Logger.Error(ex, "DSH：打开 hosts 目录失败")
            Hint($"无法打开目录：{ex.Message}", HintType.Red)
        End Try
    End Sub

#End Region

#Region "卡片 2：镜像站"

    Private Sub BuildMirrorList()
        Try
            PanMirrors.Children.Clear()

            For Each mirror In Mirrors
                Dim captured As DshNetTool.MirrorEntry = mirror

                Dim subtitle As String
                If String.IsNullOrWhiteSpace(mirror.Note) Then
                    subtitle = If(mirror.IsDirect, "不走任何代理", mirror.Prefix)
                Else
                    subtitle = mirror.Note
                End If
                If Not mirror.Enabled Then subtitle &= "（已禁用）"

                Dim badge As String = If(mirror.LatencyMs >= 0, $"{mirror.LatencyMs} ms", "")
                If Not mirror.Enabled Then badge = "—"

                PanMirrors.Children.Add(
                    BuildRow(mirror.Name, subtitle, badge, mirror.ProbeOk,
                             ("测速", Sub() ProbeOneMirror(captured)),
                             ("打开", Sub() OpenViaMirror(captured))))
            Next
        Catch ex As Exception
            Logger.Error(ex, "DSH：构建镜像列表失败")
        End Try
    End Sub

    Private Sub ProbeOneMirror(mirror As DshNetTool.MirrorEntry)
        If IsBusy OrElse mirror Is Nothing Then Return

        SetBusy(True, $"正在测速：{mirror.Name}……")
        RunInNewThread(
            Sub()
                Dim r As DshNetTool.MirrorProbeResult = DshNetTool.ProbeMirror(mirror)
                RunInUi(
                    Sub()
                        SetBusy(False)
                        mirror.ProbeOk = r.Ok
                        mirror.LatencyMs = r.LatencyMs
                        BuildMirrorList()
                        Hint($"{mirror.Name}：{If(r.Ok, $"可用，{r.LatencyMs} ms", $"不可用（{r.Detail}）")}",
                             If(r.Ok, HintType.Green, HintType.Red))
                    End Sub)
            End Sub, "DSH 镜像测速")
    End Sub

    Private Sub BtnProbeMirrors_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnProbeMirrors.Click
        If IsBusy Then Return

        Dim enabled As List(Of DshNetTool.MirrorEntry) = Mirrors.Where(Function(m) m.Enabled).ToList()
        If enabled.Count = 0 Then
            Hint("没有启用的镜像。", HintType.Red)
            Return
        End If

        SetBusy(True, $"正在测速 {enabled.Count} 个镜像……")
        RunInNewThread(
            Sub()
                Dim results As New List(Of DshNetTool.MirrorProbeResult)
                For Each m In enabled
                    Try
                        RunInUi(Sub() LoadHintText.Text = $"正在测速：{m.Name}……")
                        results.Add(DshNetTool.ProbeMirror(m))
                    Catch ex As Exception
                        Logger.Error(ex, $"DSH：镜像测速失败：{m.Name}")
                        results.Add(New DshNetTool.MirrorProbeResult With {.Mirror = m, .Ok = False, .Detail = ex.Message})
                    End Try
                Next

                RunInUi(
                    Sub()
                        SetBusy(False)
                        For Each r In results
                            r.Mirror.ProbeOk = r.Ok
                            r.Mirror.LatencyMs = r.LatencyMs
                        Next
                        BuildMirrorList()

                        Dim best As DshNetTool.MirrorProbeResult =
                            results.Where(Function(r) r.Ok).OrderBy(Function(r) r.LatencyMs).FirstOrDefault()
                        If best Is Nothing Then
                            Hint("所有镜像都不可用。可能是网络受限，试试直连或换网络。", HintType.Red)
                        Else
                            Hint($"最快镜像：{best.Mirror.Name}（{best.LatencyMs} ms）。", HintType.Green)
                        End If
                    End Sub)
            End Sub, "DSH 镜像批量测速")
    End Sub

    Private Sub OpenViaMirror(mirror As DshNetTool.MirrorEntry)
        If mirror Is Nothing Then Return
        Try
            Dim raw As String = If(TextGithubUrl.Text, "").Trim()
            If raw.Length = 0 Then raw = "https://github.com/deepseek-ai/deepseek-harness"
            If Not raw.StartsWith("http", StringComparison.OrdinalIgnoreCase) Then raw = "https://" & raw

            Dim url As String = DshNetTool.ToMirrorUrl(raw, mirror)
            If String.Equals(url, raw, StringComparison.OrdinalIgnoreCase) AndAlso Not mirror.IsDirect Then
                Hint($"「{mirror.Name}」处理不了这个地址（它只支持 raw 文件），已按原地址打开。")
            End If
            OpenWebsite(url)
        Catch ex As Exception
            Logger.Error(ex, "DSH：用镜像打开网页失败")
            Hint($"无法打开：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub BtnResetMirrors_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnResetMirrors.Click
        If MyMsgBox("把镜像列表恢复成内置默认值？你自己加的镜像会丢失。", "恢复默认镜像", "恢复", "取消") = 2 Then
            Return
        End If
        Mirrors = DshNetTool.DefaultMirrors
        BuildMirrorList()
        DshNetTool.SaveConfig(Mirrors, Targets)
        Hint("镜像列表已恢复默认。", HintType.Green)
    End Sub

#End Region

#Region "卡片 3：网络诊断"

    Private Sub BuildDiagCombo()
        Try
            IsUpdatingDiagCombo = True
            Try
                ComboDiagTarget.Items.Clear()
                For Each target In DshNetTool.DefaultTargets
                    ComboDiagTarget.Items.Add(New MyComboBoxItem With {
                        .Content = $"{target.Host}（{target.Note}）",
                        .Tag = $"https://{target.Host}/"
                    })
                Next
                ComboDiagTarget.Items.Add(New MyComboBoxItem With {
                    .Content = "GitHub 插件市场 API",
                    .Tag = "https://api.github.com/search/repositories?q=topic:dsh-plugin&per_page=1"
                })
                If ComboDiagTarget.Items.Count > 0 Then ComboDiagTarget.SelectedItem = ComboDiagTarget.Items(0)
            Finally
                IsUpdatingDiagCombo = False
            End Try
        Catch ex As Exception
            Logger.Error(ex, "DSH：构建诊断目标下拉框失败")
        End Try
    End Sub

    Private Sub ComboDiagTarget_TextChanged(sender As Object, e As TextChangedEventArgs) Handles ComboDiagTarget.TextChanged
        If IsUpdatingDiagCombo Then Return
        '选完目标就把诊断结果清掉，免得旧结果配新目标看串
        PanDiag.Children.Clear()
    End Sub

    Private Sub BtnDiagnose_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnDiagnose.Click
        If IsBusy Then Return

        Dim item = TryCast(ComboDiagTarget.SelectedItem, MyComboBoxItem)
        Dim url As String = If(item Is Nothing, Nothing, CStr(item.Tag))
        If String.IsNullOrWhiteSpace(url) Then
            Hint("请先选择一个诊断目标。", HintType.Red)
            Return
        End If

        SetBusy(True, "正在诊断……")
        PanDiag.Children.Clear()

        RunInNewThread(
            Sub()
                '⚠️ 循环变量不能叫 step —— Step 是 VB 的关键字（For ... To ... Step n），
                '   写 For Each step In ... 会被解析成语法错误（BC30201 应为表达式）
                Dim diagSteps As List(Of DshNetTool.DiagStep) = Nothing
                Dim err As String = Nothing
                Try
                    diagSteps = DshNetTool.Diagnose(url)
                Catch ex As Exception
                    err = ex.Message
                    Logger.Error(ex, "DSH：网络诊断失败")
                End Try

                RunInUi(
                    Sub()
                        SetBusy(False)
                        PanDiag.Children.Clear()

                        If diagSteps Is Nothing Then
                            PanDiag.Children.Add(MakeInfoText("诊断失败：" & If(err, "未知原因")))
                            Return
                        End If

                        For Each diagStep In diagSteps
                            PanDiag.Children.Add(
                                BuildRow(diagStep.Name, diagStep.Detail, diagStep.LatencyText, diagStep.Ok))
                        Next

                        Dim failed As Integer = diagSteps.Where(Function(s) Not s.Ok).Count()
                        If failed = 0 Then
                            Hint("全部通过。链路正常。", HintType.Green)
                        Else
                            Hint($"有 {failed} 步未通过，详见下方结果。", HintType.Red)
                        End If
                    End Sub)
            End Sub, "DSH 网络诊断")
    End Sub

#End Region

#Region "刷新接口"

    Public Sub Refresh() Implements IRefreshable.Refresh
        RefreshAdminStatus()
        RefreshHostsState()
        Hint("已刷新网络工具箱状态。")
    End Sub

#End Region

#Region "原百宝箱的占位实现（被多处调用，不能删）"

    Public Shared Sub StartCustomDownload(Url As String, FileName As String, Optional Folder As String = Nothing)
        Hint("为便于维护，开源内容中不包含百宝箱功能……")
    End Sub
    Public Shared Sub Jrrp()
        Hint("为便于维护，开源内容中不包含百宝箱功能……")
    End Sub
    Public Shared Sub RubbishClear()
        Hint("为便于维护，开源内容中不包含百宝箱功能……")
    End Sub
    Public Shared Sub MemoryOptimize(ShowHint As Boolean)
        If ShowHint Then Hint("为便于维护，开源内容中不包含百宝箱功能……")
    End Sub
    Public Shared Sub MemoryOptimizeInternal(ShowHint As Boolean)
        If ShowHint Then Hint("为便于维护，开源内容中不包含百宝箱功能……")
    End Sub
    Public Shared Function GetRandomCave() As String
        Return "为便于维护，开源内容中不包含百宝箱功能……"
    End Function
    Public Shared Function GetRandomHint() As String
        Return "为便于维护，开源内容中不包含百宝箱功能……"
    End Function
    Public Shared Function GetRandomPresetHint() As String
        Return "为便于维护，开源内容中不包含百宝箱功能……"
    End Function

#End Region

End Class
