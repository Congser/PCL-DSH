''' <summary>
''' PCL_DSH：实例快照页。
''' </summary>
''' <remarks>
''' 界面刻意保持简单：一个实例选择器 + 两个创建按钮 + 一个列表。
''' 快照本身的能力都在 <see cref="DshSnapshot"/> 里，这一层只负责
''' 「选实例 / 显示 / 确认 / 报进度」。
'''
''' ⚠️ 两个容易踩的点：
''' <list type="number">
''' <item><c>MyComboBox</c> 非可编辑时 <c>TextChanged</c> **永远不触发**，
'''       必须挂 <c>SelectionChanged</c>（项目里踩过三次）。</item>
''' <item>挂 <c>SelectionChanged</c> 后，程序化设 <c>SelectedItem</c> 也会触发，
'''       所以刷新列表时要先摘事件再挂回来，或者用标志位挡。</item>
''' </list>
''' </remarks>
Public Class PageSnapshot

#Region "生命周期"

    ''' <summary>防止程序化设 SelectedItem 时递归触发刷新。</summary>
    Private IsUpdatingCombo As Boolean = False

    Private Sub PageSnapshot_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        Try
            PanBack.ScrollToHome()
            ReloadInstances()
        Catch ex As Exception
            Logger.Error(ex, "DSH：快照页加载失败")
        End Try
    End Sub

#End Region

#Region "实例选择"

    ''' <summary>重新填充实例下拉框。</summary>
    Private Sub ReloadInstances()
        Try
            IsUpdatingCombo = True
            Try
                Dim items As New List(Of String)
                For Each inst In ModDSH.DshInstances
                    items.Add(inst.DisplayName)
                Next
                ComboInstance.ItemsSource = items

                Dim sel As DshInstance = ModDSH.DshSelectedInstance
                If sel IsNot Nothing Then
                    Dim idx As Integer = ModDSH.DshInstances.IndexOf(sel)
                    If idx >= 0 AndAlso idx < items.Count Then ComboInstance.SelectedIndex = idx
                ElseIf items.Count > 0 Then
                    ComboInstance.SelectedIndex = 0
                End If
            Finally
                IsUpdatingCombo = False
            End Try

            RefreshNote()
            RenderSnapshots()
        Catch ex As Exception
            Logger.Error(ex, "DSH：刷新实例列表失败")
        End Try
    End Sub

    Private Sub ComboInstance_SelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboInstance.SelectionChanged
        If IsUpdatingCombo Then Return
        Try
            RefreshNote()
            RenderSnapshots()
        Catch ex As Exception
            Logger.Warn(ex, "DSH：切换实例失败（可忽略）")
        End Try
    End Sub

    ''' <summary>当前选中的实例。</summary>
    Private Function CurrentInstance() As DshInstance
        Try
            Dim idx As Integer = ComboInstance.SelectedIndex
            If idx < 0 OrElse idx >= ModDSH.DshInstances.Count Then Return Nothing
            Return ModDSH.DshInstances(idx)
        Catch ex As Exception
            Return Nothing
        End Try
    End Function

    Private Sub RefreshNote()
        Try
            If LabInstanceNote Is Nothing Then Return
            Dim inst As DshInstance = CurrentInstance()
            If inst Is Nothing Then
                LabInstanceNote.Text = "当前没有实例，请先在「我的实例」里新建一个。"
                Return
            End If
            LabInstanceNote.Text = $"数据目录：{inst.HomeDir}"
        Catch ex As Exception
            Logger.Warn(ex, "DSH：刷新快照说明失败（可忽略）")
        End Try
    End Sub

#End Region

#Region "渲染列表"

    Private Sub RenderSnapshots()
        Try
            PanSnapshots.Children.Clear()
            Dim inst As DshInstance = CurrentInstance()
            If inst Is Nothing Then
                LabSnapEmpty.Visibility = Visibility.Visible
                LabSnapUsage.Text = ""
                Return
            End If

            Dim list As List(Of DshSnapshot.DshSnapshotInfo) = DshSnapshot.DshSnapshotList(inst)
            LabSnapUsage.Text = $"快照占用：{DshDoctor.FormatBytes(DshSnapshot.DshSnapshotTotalSize())}"

            If list.Count = 0 Then
                LabSnapEmpty.Visibility = Visibility.Visible
                Return
            End If
            LabSnapEmpty.Visibility = Visibility.Collapsed

            For Each info In list
                PanSnapshots.Children.Add(BuildSnapshotRow(inst, info))
            Next
        Catch ex As Exception
            Logger.Error(ex, "DSH：渲染快照列表失败")
        End Try
    End Sub

    ''' <summary>构建一行快照。</summary>
    Private Function BuildSnapshotRow(inst As DshInstance,
                                      info As DshSnapshot.DshSnapshotInfo) As FrameworkElement
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

        Dim textPanel As New StackPanel With {.VerticalAlignment = VerticalAlignment.Center}
        Dim titleText As New TextBlock With {
            .Text = info.Name,
            .FontSize = 14,
            .FontWeight = FontWeights.Bold,
            .TextWrapping = TextWrapping.Wrap
        }
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        textPanel.Children.Add(titleText)

        Dim infoText As New TextBlock With {
            .Text = info.Describe & If(String.IsNullOrWhiteSpace(info.Note), "", "  ·  " & info.Note),
            .FontSize = 12,
            .Margin = New Thickness(0, 4, 0, 0),
            .TextWrapping = TextWrapping.Wrap
        }
        infoText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
        textPanel.Children.Add(infoText)

        Grid.SetColumn(textPanel, 0)
        grid.Children.Add(textPanel)

        Dim btnPanel As New StackPanel With {
            .Orientation = Orientation.Horizontal,
            .VerticalAlignment = VerticalAlignment.Center
        }

        Dim btnRestore As New MyButton With {
            .Text = "恢复",
            .Margin = New Thickness(0, 0, 8, 0)
        }
        AddHandler btnRestore.Click, Sub() OnRestoreClick(inst, info)
        btnPanel.Children.Add(btnRestore)

        Dim btnDelete As New MyButton With {.Text = "删除"}
        AddHandler btnDelete.Click, Sub() OnDeleteClick(inst, info)
        btnPanel.Children.Add(btnDelete)

        Grid.SetColumn(btnPanel, 1)
        grid.Children.Add(btnPanel)

        row.Child = grid
        Return row
    End Function

#End Region

#Region "创建"

    Private Sub BtnSnapQuick_Click() Handles BtnSnapQuick.Click
        CreateSnapshot(Full:=False)
    End Sub

    Private Sub BtnSnapFull_Click() Handles BtnSnapFull.Click
        CreateSnapshot(Full:=True)
    End Sub

    Private Sub BtnSnapRefresh_Click() Handles BtnSnapRefresh.Click
        RenderSnapshots()
        Hint("快照列表已刷新。", HintType.Blue)
    End Sub

    Private Sub CreateSnapshot(Full As Boolean)
        Dim inst As DshInstance = CurrentInstance()
        If inst Is Nothing Then
            Hint("请先选择一个实例。", HintType.Red)
            Return
        End If

        If Full Then
            Dim warn As Integer = MyMsgBox(
                "完整快照会把插件依赖一起存下来。" & vbCrLf & vbCrLf &
                "· 体积通常几百 MB，创建需要几十秒到几分钟" & vbCrLf &
                "· 好处是完全离线也能恢复" & vbCrLf & vbCrLf &
                "如果你只是想留个还原点，用「创建快速快照」就够了。" & vbCrLf & vbCrLf &
                "继续吗？",
                "创建完整快照", "继续", "取消")
            If warn <> 1 Then Return
        End If

        SetBusy(True, If(Full, "正在创建完整快照……", "正在创建快速快照……"))
        RunInNewThread(
            Sub()
                Dim res As DshSnapshot.DshSnapshotResult = Nothing
                Try
                    res = DshSnapshot.DshSnapshotCreate(
                        inst, Nothing, Full,
                        Progress:=Sub(stage As String, message As String, pct As Double)
                                      RunInUi(Sub() LabLoadHint.Text = $"{stage}：{message}")
                                  End Sub)
                Catch ex As Exception
                    Logger.Error(ex, "DSH：创建快照失败")
                End Try
                RunInUi(
                    Sub()
                        SetBusy(False)
                        If res Is Nothing Then
                            Hint("创建快照时出错，详见日志。", HintType.Red)
                            Return
                        End If
                        If res.Success Then
                            Hint("快照创建完成。", HintType.Green)
                            MyMsgBox(res.Message, "快照创建完成")
                        Else
                            Hint(res.Message, HintType.Red)
                        End If
                        RenderSnapshots()
                    End Sub)
            End Sub, "DSH 创建快照")
    End Sub

#End Region

#Region "恢复 / 删除"

    Private Sub OnRestoreClick(inst As DshInstance, info As DshSnapshot.DshSnapshotInfo)
        Try
            Dim tierText As String = If(String.Equals(info.Tier, DshSnapshot.TierFull, StringComparison.OrdinalIgnoreCase),
                                        "完整", "快速")
            Dim warn As Integer = MyMsgBox(
                $"把实例「{inst.DisplayName}」恢复到快照「{info.Name}」？" & vbCrLf & vbCrLf &
                $"· 快照时间：{info.CreatedAt:yyyy-MM-dd HH:mm}（{tierText}档）" & vbCrLf &
                $"· 快照内容：{info.FileCount} 个文件、{DshDoctor.FormatBytes(info.TotalBytes)}" & vbCrLf & vbCrLf &
                "· 会**覆盖**实例里对应的数据（插件配置、密钥、会话、审批规则等）" & vbCrLf &
                "· 恢复前会自动给当前状态也拍一个快照，点错了还能退回来" & vbCrLf &
                "· 实例会被先停止" &
                If(String.Equals(info.Tier, DshSnapshot.TierQuick, StringComparison.OrdinalIgnoreCase),
                   vbCrLf & "· 快速快照不含依赖，恢复后会按清单自动重装（需要联网）", ""),
                "恢复快照", "开始恢复", "取消")
            If warn <> 1 Then Return

            SetBusy(True, "正在恢复快照……")
            RunInNewThread(
                Sub()
                    Dim res As DshSnapshot.DshSnapshotResult = Nothing
                    Try
                        res = DshSnapshot.DshSnapshotRestore(
                            inst, info.Id,
                            Sub(stage As String, message As String, pct As Double)
                                RunInUi(Sub() LabLoadHint.Text = $"{stage}：{message}")
                            End Sub)
                    Catch ex As Exception
                        Logger.Error(ex, "DSH：恢复快照失败")
                    End Try
                    RunInUi(
                        Sub()
                            SetBusy(False)
                            If res Is Nothing Then
                                Hint("恢复快照时出错，详见日志。", HintType.Red)
                                Return
                            End If
                            MyMsgBox(res.Message, If(res.Success, "恢复完成", "恢复未完全成功"))
                            Hint(If(res.Success, "已恢复到快照。", "恢复未完全成功，详见弹窗。"),
                                 If(res.Success, HintType.Green, HintType.Red))
                            ' 数据换了，实例状态与列表都要重来
                            ModDSH.DshReloadInstancesAfterMigration()
                            RenderSnapshots()
                        End Sub)
                End Sub, "DSH 恢复快照")
        Catch ex As Exception
            Logger.Error(ex, "DSH：恢复快照流程启动失败")
            Hint($"无法恢复：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub OnDeleteClick(inst As DshInstance, info As DshSnapshot.DshSnapshotInfo)
        Try
            Dim warn As Integer = MyMsgBox(
                $"删除快照「{info.Name}」？" & vbCrLf & vbCrLf &
                $"· 时间：{info.CreatedAt:yyyy-MM-dd HH:mm}" & vbCrLf &
                $"· 占用：{DshDoctor.FormatBytes(info.TotalBytes)}" & vbCrLf & vbCrLf &
                "删除后无法恢复。",
                "删除快照", "删除", "取消")
            If warn <> 1 Then Return

            Dim err As String = DshSnapshot.DshSnapshotDelete(inst, info.Id)
            If String.IsNullOrWhiteSpace(err) Then
                Hint("快照已删除。", HintType.Green)
            Else
                Hint(err, HintType.Red)
            End If
            RenderSnapshots()
        Catch ex As Exception
            Logger.Error(ex, "DSH：删除快照失败")
            Hint($"删除失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

#End Region

#Region "忙碌状态"

    Private Sub SetBusy(Busy As Boolean, Optional HintText As String = Nothing)
        Try
            PanLoad.Visibility = If(Busy, Visibility.Visible, Visibility.Collapsed)
            If Busy Then LabLoadHint.Text = HintText
            BtnSnapQuick.IsEnabled = Not Busy
            BtnSnapFull.IsEnabled = Not Busy
            BtnSnapRefresh.IsEnabled = Not Busy
            ComboInstance.IsEnabled = Not Busy
        Catch ex As Exception
            Logger.Warn(ex, "DSH：切换快照页忙碌状态失败（可忽略）")
        End Try
    End Sub

#End Region

End Class
