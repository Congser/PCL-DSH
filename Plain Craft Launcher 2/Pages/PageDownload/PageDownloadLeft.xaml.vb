''' <summary>
''' 下载页左栏 —— PCL_DSH 里已整体替换为 dsh 自己的两个入口。
'''
''' 原版是六个 Minecraft 社区资源入口（原版游戏 / Mod / 整合包 / 数据包 /
''' 资源包 / 光影包）。剥离 MC 功能后它们全部下线，换成：
'''   - <b>镜像版本</b>（<see cref="FormMain.PageSubType.DownloadMirror"/>）—— npm 注册表
'''   - <b>插件社区</b>（<see cref="FormMain.PageSubType.DownloadPlugin"/>）—— GitHub topic
'''
''' ⚠️ 原版那几个子页面的**类还在**，因为 MC 侧还有几处跳转指向它们
''' （「安装新版本」「去下载 Mod」等）。<see cref="PageGet"/> 里保留了兼容分支，
''' 按需创建，免得那些跳转变成空引用崩溃。它们不再出现在左栏，用户正常点不到。
''' </summary>
Public Class PageDownloadLeft
    Implements IRefreshable

#Region "页面切换"

    ''' <summary>当前页面的编号。</summary>
    Public PageID As FormMain.PageSubType = FormMain.PageSubType.DownloadMirror

    ''' <summary>勾选事件改变页面。</summary>
    Private Sub PageCheck(sender As FrameworkElement, e As RouteEventArgs) Handles ItemMirror.Check, ItemPlugin.Check
        '尚未初始化控件属性时，sender.Tag 为 Nothing，会导致切换到页面 0
        '若使用 IsLoaded，则会导致模拟点击不被执行（模拟点击切换页面时，控件的 IsLoaded 为 False）
        If sender.Tag IsNot Nothing Then PageChange(Val(sender.Tag))
    End Sub

    Public Function PageGet(Optional ID As FormMain.PageSubType = -1)
        If ID = -1 Then ID = PageID
        Select Case ID
            Case FormMain.PageSubType.DownloadMirror
                If FrmDownloadMirror Is Nothing Then FrmDownloadMirror = New PageDownloadMirror
                Return FrmDownloadMirror
            Case FormMain.PageSubType.DownloadPlugin
                If FrmDownloadPlugin Is Nothing Then FrmDownloadPlugin = New PageDownloadPlugin
                Return FrmDownloadPlugin
            Case Else
                Return LegacyPageGet(ID)
        End Select
    End Function

    ''' <summary>
    ''' 兼容分支：原版的 Minecraft 社区资源子页面。
    ''' </summary>
    ''' <remarks>
    ''' PCL_DSH 已经把它们的入口从左栏移除，但 MC 侧仍有几处代码会跳过来。
    ''' 这些页面类保留着按需创建，避免那些跳转直接空引用崩溃。
    ''' 它们不再出现在左栏，正常操作路径下用户碰不到。
    ''' </remarks>
    Private Function LegacyPageGet(ID As FormMain.PageSubType) As FrameworkElement
        Select Case ID
            Case FormMain.PageSubType.DownloadMod
                If FrmDownloadMod Is Nothing Then FrmDownloadMod = New PageDownloadMod
                Return FrmDownloadMod
            Case FormMain.PageSubType.DownloadPack
                If FrmDownloadPack Is Nothing Then FrmDownloadPack = New PageDownloadPack
                Return FrmDownloadPack
            Case FormMain.PageSubType.DownloadResourcePack
                If FrmDownloadResourcePack Is Nothing Then FrmDownloadResourcePack = New PageDownloadResourcePack
                Return FrmDownloadResourcePack
            Case FormMain.PageSubType.DownloadShader
                If FrmDownloadShader Is Nothing Then FrmDownloadShader = New PageDownloadShader
                Return FrmDownloadShader
            Case FormMain.PageSubType.DownloadDataPack
                If FrmDownloadDataPack Is Nothing Then FrmDownloadDataPack = New PageDownloadDataPack
                Return FrmDownloadDataPack
            Case Else
                '未知种类：不要抛异常（左栏已经改过一轮，枚举与下标不再一一对应），
                '退回镜像版本页并记一条日志，比让用户看到崩溃好得多
                Logger.Warn($"下载页收到未知的子页面种类 {ID}，已回退到镜像版本页")
                If FrmDownloadMirror Is Nothing Then FrmDownloadMirror = New PageDownloadMirror
                Return FrmDownloadMirror
        End Select
    End Function

    ''' <summary>
    ''' 切换现有页面。
    ''' </summary>
    Public Sub PageChange(ID As FormMain.PageSubType)
        If PageID = ID Then Return
        AniControlEnabled += 1
        Try
            PageChangeRun(PageGet(ID))
            PageID = ID
        Catch ex As Exception
            Logger.Error(ex, $"切换分页面失败（ID {ID}）")
        Finally
            AniControlEnabled -= 1
        End Try
    End Sub

    Private Shared Sub PageChangeRun(Target As MyPageRight)
        AniStop("FrmMain PageChangeRight") '停止主页面的右页面切换动画，防止它与本动画一起触发多次 PageOnEnter
        If Target.Parent IsNot Nothing Then Target.SetValue(ContentPresenter.ContentProperty, Nothing)
        FrmMain.PageRight = Target
        CType(FrmMain.PanMainRight.Child, MyPageRight).PageOnExit()
        AniStart({
            AaCode(
            Sub()
                CType(FrmMain.PanMainRight.Child, MyPageRight).PageOnForceExit()
                FrmMain.PanMainRight.Child = FrmMain.PageRight
                FrmMain.PageRight.Opacity = 0
            End Sub, 130),
            AaCode(
            Sub()
                '延迟触发页面通用动画，以使得在 Loaded 事件中加载的控件得以处理
                FrmMain.PageRight.Opacity = 1
                FrmMain.PageRight.PageOnEnter()
            End Sub, 30, True)
        }, "PageLeft PageChange")
    End Sub

#End Region

#Region "刷新"

    '强制刷新（由边栏的刷新按钮匿名调用，Tag 就是页面编号）
    Public Sub Refresh_Click(sender As Object, e As EventArgs)
        Refresh(Val(sender.Tag))
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        Refresh(FrmMain.PageCurrentSub)
    End Sub

    Public Sub Refresh(SubType As FormMain.PageSubType)
        Select Case SubType
            Case FormMain.PageSubType.DownloadMirror
                If FrmDownloadMirror IsNot Nothing Then FrmDownloadMirror.Reload(ForceRefresh:=True)
                ItemMirror.Checked = True
            Case FormMain.PageSubType.DownloadPlugin
                If FrmDownloadPlugin IsNot Nothing Then FrmDownloadPlugin.Reload(ForceRefresh:=True)
                ItemPlugin.Checked = True
            Case Else
                '原版社区资源页面的刷新逻辑（Dl*ListLoader）已随入口一起下线。
                '真被跳到那些页面时，让页面自己的加载器去处理。
                Logger.Info($"下载页刷新：子页面 {SubType} 已下线，跳过")
        End Select
        Hint("正在刷新……", Log:=False)
    End Sub

#End Region

End Class
