Imports System.Threading.Tasks

''' <summary>
''' DSH 的环境诊断页。
'''
''' 职责单一：把 DshRuntime.DetectAll() 的结果可视化，并提供「重新装配」入口。
''' 本页不做任何自动修复 —— 装配是重操作，必须由用户显式点击触发。
''' </summary>
Public Class PageDSHEnv
    Implements IRefreshable

    Private Sub PageDSHEnv_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        RefreshEnv()
    End Sub

    ''' <summary>
    ''' 在后台线程跑一次探测，然后回 UI 线程填表。
    ''' 探测会读写文件系统，放 UI 线程上会有可感知的卡顿。
    ''' </summary>
    Private Async Sub RefreshEnv()
        LabOverall.Text = "正在探测……"
        LabNode.Text = ""
        LabPnpm.Text = ""
        LabDsh.Text = ""
        LabPaths.Text = $"工作目录：{ModDSH.DshHomeDir}{vbCrLf}" &
                        $"运行时目录：{ModDSH.DshRuntimeDir}{vbCrLf}" &
                        $"安装目录：{ModDSH.DshInstallDir}{vbCrLf}" &
                        $"数据根目录：{ModDSH.DshRoot}"

        Dim info As DshRuntime.DshRuntimeInfo = Nothing
        Dim err As String = Nothing
        Try
            info = Await Task.Run(Function() DshRuntime.DetectAll())
        Catch ex As Exception
            err = ex.Message
        End Try

        If err IsNot Nothing Then
            LabOverall.Text = "探测失败：" & err
            LabOverall.Foreground = New SolidColorBrush(Color.FromRgb(&HF0, &H60, &H50))
            Return
        End If

        '用主题色回填，避免上一轮的红色残留
        LabOverall.Foreground = TryFindResource("ColorBrushGray1")

        If info Is Nothing Then
            LabOverall.Text = "探测未返回结果。"
            Return
        End If

        If info.IsUsable Then
            LabOverall.Text = "✓ 运行时可用，可以直接启动 DSH。"
            LabOverall.Foreground = New SolidColorBrush(Color.FromRgb(&H2E, &HA0, &H55))
        Else
            LabOverall.Text = "✗ 运行时尚未就绪，首次启动时 PCL DSH 会自动完成装配。"
            LabOverall.Foreground = New SolidColorBrush(Color.FromRgb(&HE0, &H8A, &H20))
        End If

        LabNode.Text = $"Node：{If(info.NodeExe, "（未找到）")}" & vbCrLf &
                       $"      版本 {If(info.NodeVersion, "未知")}"
        LabPnpm.Text = $"pnpm：{If(info.PnpmExe, "（未找到，可选）")}"
        LabDsh.Text = $"dsh：{If(info.DshEntry, "（未安装）")}" & vbCrLf &
                      $"      版本 {If(info.DshVersion, "未知")}（锁定 {ModDSH.DshVersion}）"
    End Sub

    Private Sub BtnDetect_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnDetect.Click
        RefreshEnv()
    End Sub

    ''' <summary>
    ''' 强制重装运行时。
    ''' 二次确认后才动手 —— 这一步会重新下载几百 MB。
    ''' </summary>
    Private Async Sub BtnReinstall_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnReinstall.Click
        If MyMsgBox("重新装配会重新下载 Node、pnpm 与 dsh 本体（可能数百 MB），" & vbCrLf &
                    "并覆盖现有私有运行时目录。" & vbCrLf & vbCrLf &
                    "确定继续吗？", "重新装配运行时", "确定", "取消") <> 1 Then Return

        AniControlEnabled += 1
        BtnReinstall.IsEnabled = False
        LabOverall.Text = "正在重新装配……详细进度见「运行日志」页面。"
        LabOverall.Foreground = TryFindResource("ColorBrushGray1")

        Try
            Await Task.Run(Sub()
                               DshInstaller.EnsureRuntime(
                                   Sub(stage As DshInstaller.DshInstallStage, msg As String, pct As Double)
                                       Logger.Info($"DSH 装配：{msg}（{pct:P0}）")
                                   End Sub)
                           End Sub)
            Hint("运行时装配完成", HintType.Green)
        Catch ex As Exception
            Logger.Error(ex, "重新装配 DSH 运行时失败")
            Hint("装配失败：" & ex.Message, HintType.Red)
        Finally
            BtnReinstall.IsEnabled = True
            AniControlEnabled -= 1
            RefreshEnv()
        End Try
    End Sub

    Private Sub BtnOpenPrivate_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnOpenPrivate.Click
        Try
            If Not DshRuntime.DirExistsSafe(ModDSH.DshRuntimeDir) Then
                Hint("私有运行时目录尚未创建", HintType.Red)
                Return
            End If
            OpenExplorer(ModDSH.DshRuntimeDir)
        Catch ex As Exception
            Logger.Error(ex, "打开 DSH 运行时目录失败")
            Hint("无法打开目录：" & ex.Message, HintType.Red)
        End Try
    End Sub

    ''' <summary>由 FormMain / 自定义事件触发的刷新入口。</summary>
    Public Sub Refresh() Implements IRefreshable.Refresh
        RefreshEnv()
    End Sub
End Class
