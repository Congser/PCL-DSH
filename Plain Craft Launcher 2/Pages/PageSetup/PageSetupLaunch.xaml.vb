Imports System.IO
Imports System.Linq

Public Class PageSetupLaunch

    Private Sub PageSetupLaunch_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        '重复加载部分
        PanBack.ScrollToHome()

        'Java 可能在版本设置被修改，所以总是重新加载（反正其他的 Refresh 也不太吃性能）
        AniControlEnabled += 1
        Refresh()
        AniControlEnabled -= 1

        '非重复加载部分
        Static Reloaded As Boolean = False
        If Reloaded Then Return
        Reloaded = True

        '磁盘占用统计比较重，只在首次进入时算一次
        RefreshDshDiskUsage()

        '首次运行引导卡片（决策 2-d）
        UpdateDshFirstRunCard()
    End Sub

    Public Sub Refresh()
        Try
            SettingService.RefreshSettings(Me)
            UpdateJavaList()
            '── PCL_DSH：DSH 设置卡 ──
            ' 必须放在 SettingService.RefreshSettings 之后：
            ' 那一句会把各个控件按设置项回填，之后我们才能覆盖 HintText 等展示性内容。
            RefreshDsh()
        Catch ex As NullReferenceException
            Logger.Error(ex, "启动设置项存在异常，已被自动重置", LogBehavior.Alert)
            Reset()
        Catch ex As Exception
            Logger.Error(ex, "重载启动设置时出错")
        End Try
    End Sub
    Public Sub Reset()
        Try
            SettingService.ResetSettings(Me)
            Settings.Set("LaunchArgumentIndieV2", Settings.GetDefault("LaunchArgumentIndieV2"))
            Configs.JavaList.Reset()
            Configs.JavaRemovedList.Reset()
            '── PCL_DSH：不再触发 Java 全盘扫描 ──
            ' 原版这里会 JavaListRefreshWorker.Start() 去扫盘找 Java。
            ' 本改版不启动 Minecraft，Java 列表没有消费者，扫了纯属浪费
            '（实测要遍历磁盘目录 + 起子进程跑 java -version）。
            ' JavaListRefreshWorker 本身保留（ModJava 源码仍在，只是不再主动触发）。
            Hint("已初始化启动设置！", HintType.Green)
        Catch ex As Exception
            Logger.Error(ex, "初始化启动设置失败", LogBehavior.Alert)
        End Try
        Refresh()
    End Sub

#Region "游戏内存"

    ''' <summary>
    ''' 获取当前设置的 RAM 值。单位为 GB。
    ''' </summary>
    ''' <remarks>
    ''' PCL DSH 不启动 Minecraft，所以设置页里已经没有内存分配的界面了；
    ''' 但这段计算逻辑与「实例设置」页共用（<c>PageInstanceSetup</c> 会调它），
    ''' 而且 MC 启动代码本身仍然物理保留，所以函数留着。
    ''' </remarks>
    Public Shared Function GetRam(Instance As McInstance, UseVersionJavaSetup As Boolean) As Double

        '------------------------------------------
        ' 修改下方代码时需要一并修改 PageInstanceSetup
        '------------------------------------------

        Dim RamGive As Double
        If Settings.Get(Of Integer)("LaunchRamType") = 0 Then
            '自动配置
            Dim RamAvailable As Double = Math.Round(My.Computer.Info.AvailablePhysicalMemory / 1024 / 1024 / 1024 * 10) / 10
            '确定需求的内存值
            Dim RamMininum As Double '无论如何也需要保证的最低限度内存
            Dim RamTarget1 As Double '估计能勉强带动了的内存
            Dim RamTarget2 As Double '估计没啥问题了的内存
            Dim RamTarget3 As Double '放一百万个材质和 Mod 和光影需要的内存
            If Instance IsNot Nothing AndAlso Not Instance.IsLoaded Then Instance.Load()
            If Instance IsNot Nothing AndAlso Instance.Modable Then
                '可安装 Mod 的版本
                Dim ModDir = DirectoryUtils.GetInfo(Instance.PathIndie & "mods\")
                Dim ModCount As Integer = If(ModDir.Exists, ModDir.GetFiles.Count(Function(f) {".jar", ".zip", ".litemod"}.Contains(f.Extension.Lower)), 0)
                RamMininum = 0.5 + ModCount / 150
                RamTarget1 = 1.5 + ModCount / 90
                RamTarget2 = 2.7 + ModCount / 50
                RamTarget3 = 4.5 + ModCount / 25
            ElseIf Instance IsNot Nothing AndAlso Instance.Version.HasOptiFine Then
                'OptiFine 版本
                RamMininum = 0.5
                RamTarget1 = 1.5
                RamTarget2 = 3
                RamTarget3 = 5
            Else
                '普通版本
                RamMininum = 0.5
                RamTarget1 = 1.5
                RamTarget2 = 2.5
                RamTarget3 = 4
            End If
            Dim RamDelta As Double
            '预分配内存，阶段一，0 ~ T1，100%
            RamDelta = RamTarget1
            RamGive += Math.Min(RamAvailable, RamDelta)
            RamAvailable -= RamDelta
            If RamAvailable < 0.1 Then GoTo PreFin
            '预分配内存，阶段二，T1 ~ T2，70%
            RamDelta = RamTarget2 - RamTarget1
            RamGive += Math.Min(RamAvailable * 0.7, RamDelta)
            RamAvailable -= RamDelta / 0.7
            If RamAvailable < 0.1 Then GoTo PreFin
            '预分配内存，阶段三，T2 ~ T3，40%
            RamDelta = RamTarget3 - RamTarget2
            RamGive += Math.Min(RamAvailable * 0.4, RamDelta)
            RamAvailable -= RamDelta / 0.4
            If RamAvailable < 0.1 Then GoTo PreFin
            '预分配内存，阶段四，T3 ~ T3 * 2，15%
            RamDelta = RamTarget3
            RamGive += Math.Min(RamAvailable * 0.15, RamDelta)
            RamAvailable -= RamDelta / 0.15
            If RamAvailable < 0.1 Then GoTo PreFin
PreFin:
            '不低于最低值
            RamGive = Math.Round(Math.Max(RamGive, RamMininum), 1)
        Else
            '手动配置
            Dim Value = Settings.Get(Of Integer)("LaunchRamCustom")
            If Value <= 12 Then
                RamGive = Value * 0.1 + 0.3
            ElseIf Value <= 25 Then
                RamGive = (Value - 12) * 0.5 + 1.5
            ElseIf Value <= 33 Then
                RamGive = (Value - 25) * 1 + 8
            Else
                RamGive = (Value - 33) * 2 + 16
            End If
        End If
        Return RamGive
    End Function

#End Region

#Region "Java 列表（占位）"

    ''' <summary>
    ''' 刷新 Java 下拉框。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ **这个方法必须保留**：<c>ModJava.UpdateJavaLists</c> 里有
    ''' <c>FrmSetupLaunch?.UpdateJavaList()</c> 这一句，删掉它 MC 侧就编译不过。
    '''
    ''' PCL DSH 不启动 Minecraft，设置页里的 Java 选择界面已经整块删掉了，
    ''' 所以这里只留一个空实现。
    ''' </remarks>
    Public Sub UpdateJavaList()
        ' 设置页已无 Java 选择界面，无需刷新
    End Sub

#End Region

#Region "DSH 运行时"

    ' NOTE: all identifiers/comments here are ASCII on purpose.
    ' A previous attempt at putting decorative Chinese comments inside the
    ' Settings.vb entry list produced 98 bogus BC30035 syntax errors, so the
    ' project convention for new code is: keep the code ASCII, put the Chinese
    ' in XAML/TextBlock where it is safe.

    ''' <summary>Detected browsers, cached so the combo box does not rescan on every refresh.</summary>
    Private DshBrowserList As List(Of DshBrowser.BrowserInfo) = Nothing

    ''' <summary>Set to True while we programmatically fill controls, to avoid feedback loops.</summary>
    Private IsDshUiLoading As Boolean = False

    ''' <summary>Refresh the DSH card: environment probe + browser list + custom-browser visibility.</summary>
    ''' <remarks>
    ''' ⚠️ The runtime probe must NOT run synchronously here.
    ''' <see cref="DshRuntime.DetectAll"/> spawns `node -v` once per candidate
    ''' (private runtime, Program Files, Program Files (x86), LocalAppData,
    ''' NVM_HOME, NVM_SYMLINK) and each spawn costs tens of milliseconds.
    ''' Doing that on the UI thread made the settings page visibly stall on open
    ''' (reported by the user). It now runs on a background thread and back-fills
    ''' the hint text when it finishes; the rest of the card renders immediately.
    ''' </remarks>
    Private Sub RefreshDsh()
        If IsDshUiLoading Then Return
        IsDshUiLoading = True
        AniControlEnabled += 1
        Try
            ' File-existence probes only -- cheap enough to stay on the UI thread.
            RefreshDshBrowserList()
            UpdateDshBrowserVisibility()
            ' 插件市场数据源说明（决策 4-b）—— 纯字符串拼接，零成本
            RefreshDshPluginSourceNote()
            ' 从其他环境同步的说明（探测默认 .dsh + 当前实例）—— 也是纯文件探测
            RefreshDshSyncNote()
            ' Process spawns -- must be async.
            RefreshDshRuntimeProbeAsync()
        Catch ex As Exception
            Logger.Error(ex, "DSH: refresh settings card failed")
        Finally
            AniControlEnabled -= 1
            IsDshUiLoading = False
        End Try
    End Sub

    ''' <summary>
    ''' Generation counter for the async probe. A newer probe invalidates older ones.
    ''' </summary>
    ''' <remarks>
    ''' The settings page can be refreshed repeatedly (tab switches, Reset, F5).
    ''' Without this guard a slow earlier probe could land after a faster later one
    ''' and overwrite fresh data with stale data.
    ''' </remarks>
    Private DshProbeGeneration As Integer = 0

    ''' <summary>
    ''' Kick off the Node / pnpm / dsh probe on a background thread.
    ''' </summary>
    Private Sub RefreshDshRuntimeProbeAsync()
        ' Show a neutral placeholder instead of leaving the previous (possibly stale) value.
        TextDshNodePath.HintText = "probing..."
        TextDshPnpmPath.HintText = "probing..."
        TextDshInstallPath.HintText = "probing..."

        DshProbeGeneration += 1
        Dim generation As Integer = DshProbeGeneration

        RunInNewThread(
            Sub()
                Dim info As DshRuntime.DshRuntimeInfo = Nothing
                Try
                    info = DshRuntime.DetectAll()
                Catch ex As Exception
                    Logger.Warn($"DSH: runtime probe failed: {ex.Message}")
                End Try

                RunInUi(
                    Sub()
                        ' A newer probe started while we were working -- drop this result.
                        If generation <> DshProbeGeneration Then Return
                        ApplyDshRuntimeProbe(info)
                    End Sub)
            End Sub, "DSH Runtime Probe")
    End Sub

    ''' <summary>
    ''' Write a probe result into the hint texts of the three path boxes.
    ''' </summary>
    ''' <param name="info">Probe result; Nothing means the probe itself failed.</param>
    ''' <remarks>
    ''' The hints are purely informational -- the stored setting is whatever the
    ''' user typed, and an empty setting means "auto-detect at launch time".
    ''' </remarks>
    Private Sub ApplyDshRuntimeProbe(info As DshRuntime.DshRuntimeInfo)
        If info Is Nothing Then
            TextDshNodePath.HintText = "auto-detect (probe failed)"
            TextDshPnpmPath.HintText = "auto-detect (probe failed)"
            TextDshInstallPath.HintText = ModDSH.DshInstallDir
            Return
        End If

        TextDshNodePath.HintText = If(String.IsNullOrWhiteSpace(info.NodeExe),
            "not found - will be installed automatically",
            $"detected: {info.NodeExe}  [{If(info.NodeVersion, "unknown version")}]")

        TextDshPnpmPath.HintText = If(String.IsNullOrWhiteSpace(info.PnpmExe),
            "not found - will be installed automatically",
            $"detected: {info.PnpmExe}")

        ' NOTE: VB has no ternary operator -- use If(cond, a, b).
        If String.IsNullOrWhiteSpace(info.DshEntry) Then
            TextDshInstallPath.HintText = ModDSH.DshInstallDir
        Else
            TextDshInstallPath.HintText = $"installed: {info.DshEntry}  [{If(info.DshVersion, ModDSH.DshVersion)}]"
        End If
    End Sub

    ''' <summary>Populate the browser combo box from the standard install locations.</summary>
    Private Sub RefreshDshBrowserList()
        If DshBrowserList Is Nothing Then
            Try
                DshBrowserList = DshBrowser.DetectBrowsers()
            Catch ex As Exception
                Logger.Warn($"DSH: browser detection failed: {ex.Message}")
                DshBrowserList = New List(Of DshBrowser.BrowserInfo)
            End Try
        End If

        Dim Previous As String = ComboDshBrowser.Text
        ComboDshBrowser.Items.Clear()

        For Each item In DshBrowserList
            ComboDshBrowser.Items.Add(New MyComboBoxItem With {
                .Content = item.Name,
                .Tag = item.Path
            })
        Next

        ' Restore previous selection if still present.
        If Not String.IsNullOrWhiteSpace(Previous) Then
            For Each obj In ComboDshBrowser.Items
                Dim ci = CType(obj, MyComboBoxItem)
                If String.Equals(CStr(ci.Content), Previous, StringComparison.OrdinalIgnoreCase) Then
                    ComboDshBrowser.SelectedItem = ci
                    Return
                End If
            Next
        End If

        ' Otherwise select whatever matches the configured path.
        Dim configured As String = If(Settings.Get(Of String)("DshBrowserPath"), "").Trim()
        If Not String.IsNullOrWhiteSpace(configured) Then
            For Each obj In ComboDshBrowser.Items
                Dim ci = CType(obj, MyComboBoxItem)
                If String.Equals(CStr(ci.Tag), configured, StringComparison.OrdinalIgnoreCase) Then
                    ComboDshBrowser.SelectedItem = ci
                    Return
                End If
            Next
        End If
    End Sub

    ''' <summary>Show the custom-browser sub-panel only in "specify browser" mode.</summary>
    Private Sub UpdateDshBrowserVisibility()
        Dim IsCustom As Boolean = (Settings.Get(Of Integer)("DshBrowserType") = CInt(DshBrowser.DshBrowserKind.Custom))
        PanDshBrowserCustom.Visibility = IsCustom.ToVisibility
    End Sub

    Private Sub BtnDshNodeDetect_Click() Handles BtnDshNodeDetect.Click
        Settings.Set("DshNodePath", "")
        RefreshDsh()
        Hint("Node.js has been reset to auto-detect.", HintType.Green)
    End Sub

    Private Sub BtnDshNodeBrowse_Click() Handles BtnDshNodeBrowse.Click
        PickDshExecutable("选择 node.exe", "可执行文件|*.exe", "DshNodePath", TextDshNodePath)
    End Sub

    Private Sub BtnDshNodeClear_Click() Handles BtnDshNodeClear.Click
        Settings.Set("DshNodePath", "")
        Refresh()
    End Sub

    Private Sub BtnDshPnpmBrowse_Click() Handles BtnDshPnpmBrowse.Click
        PickDshExecutable("选择 pnpm 可执行文件", "可执行文件|*.exe;*.cmd;*.bat", "DshPnpmPath", TextDshPnpmPath)
    End Sub

    Private Sub BtnDshPnpmClear_Click() Handles BtnDshPnpmClear.Click
        Settings.Set("DshPnpmPath", "")
        Refresh()
    End Sub

    Private Sub BtnDshInstallBrowse_Click() Handles BtnDshInstallBrowse.Click
        Try
            Dim DefaultDir As String = Settings.Get(Of String)("DshInstallPath")
            If String.IsNullOrWhiteSpace(DefaultDir) Then DefaultDir = ModDSH.DshInstallDir
            Dim Selected As String = Dialogs.SelectFolder("选择 DSH 安装目录", False, DefaultDir).FirstOrDefault()
            If String.IsNullOrWhiteSpace(Selected) Then Return
            Settings.Set("DshInstallPath", Selected)
            Refresh()
        Catch ex As Exception
            Logger.Error(ex, "DSH: pick install folder failed")
            Hint($"Failed to open folder picker: {ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub BtnDshInstallOpen_Click() Handles BtnDshInstallOpen.Click
        Try
            Dim Target As String = Settings.Get(Of String)("DshInstallPath")
            If String.IsNullOrWhiteSpace(Target) Then Target = ModDSH.DshInstallDir
            If Not DshRuntime.DirExistsSafe(Target) Then
                Hint("The directory does not exist yet. Start DSH once to create it.", HintType.Red)
                Return
            End If
            OpenExplorer(Target)
        Catch ex As Exception
            Logger.Error(ex, "DSH: open install folder failed")
            Hint($"Cannot open directory: {ex.Message}", HintType.Red)
        End Try
    End Sub

    ''' <summary>Shared file picker for the Node / pnpm paths.</summary>
    Private Sub PickDshExecutable(Title As String, Filter As String, SettingKey As String, Box As MyTextBox)
        Try
            Dim Selected As String = Dialogs.SelectFile(Title, False, filter:=Filter).FirstOrDefault()
            If String.IsNullOrWhiteSpace(Selected) Then Return
            Settings.Set(SettingKey, Selected)
            Refresh()
            Hint($"{SettingKey} updated.", HintType.Green)
        Catch ex As Exception
            Logger.Error(ex, $"DSH: pick {SettingKey} failed")
            Hint($"Failed to open file picker: {ex.Message}", HintType.Red)
        End Try
    End Sub

    ''' <summary>Re-scan the standard browser install locations.</summary>
    Private Sub BtnDshBrowserRescan_Click() Handles BtnDshBrowserRescan.Click
        DshBrowserList = Nothing
        RefreshDshBrowserList()
        If DshBrowserList Is Nothing OrElse DshBrowserList.Count = 0 Then
            Hint("No browser was found in the standard locations. Please pick the executable manually.", HintType.Red)
        Else
            Hint($"{DshBrowserList.Count} browser(s) found.", HintType.Green)
        End If
    End Sub

    ''' <summary>Selecting a detected browser fills the path box.</summary>
    Private Sub ComboDshBrowser_SelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboDshBrowser.SelectionChanged
        If IsDshUiLoading Then Return
        If AniControlEnabled <> 0 Then Return
        Dim ci = TryCast(ComboDshBrowser.SelectedItem, MyComboBoxItem)
        If ci Is Nothing Then Return
        Dim Path As String = TryCast(ci.Tag, String)
        If String.IsNullOrWhiteSpace(Path) Then Return
        TextDshBrowserPath.Text = Path
        Settings.Set("DshBrowserPath", Path)
        Hint($"Browser set to {ci.Content}.", HintType.Green)
    End Sub

    ''' <summary>Switching between system / custom browser updates the sub-panel.</summary>
    Private Sub RadioDshBrowser_Change() Handles RadioDshBrowserSystem.Check, RadioDshBrowserCustom.Check
        If IsDshUiLoading Then Return
        If AniControlEnabled <> 0 Then Return
        UpdateDshBrowserVisibility()
    End Sub

    ''' <summary>
    ''' Clamp the port text box to a valid range; 0 means "pick a free port".
    ''' </summary>
    Private Sub TextDshPort_TextChanged() Handles TextDshPort.ValidatedTextChanged
        If IsDshUiLoading Then Return
        If TextDshPort.Text = "" Then Return
        Dim Value As Integer
        If Not Integer.TryParse(TextDshPort.Text, Value) Then
            TextDshPort.Text = "0"
            Return
        End If
        If Value < 0 Then Value = 0
        If Value > 65535 Then Value = 65535
        If Value.ToString() <> TextDshPort.Text Then TextDshPort.Text = Value.ToString()
    End Sub

#End Region

#Region "维护"

    ''' <summary>
    ''' 最近一次体检报告。Nothing 表示还没体检过。
    ''' </summary>
    ''' <remarks>
    ''' 缓存起来是为了让「一键修复」能按报告里标了 Repairable 的项逐条执行，
    ''' 而不是自己重新猜一遍需要修什么。UI 上的报告文本也直接由它渲染。
    ''' </remarks>
    Private DshLastReport As DshDoctor.DshHealthReport = Nothing

    ''' <summary>维护卡片上的按钮是否正在工作中（用于禁用重复点击）。</summary>
    Private IsDshMaintenanceBusy As Boolean = False

    ''' <summary>
    ''' 把维护卡片的相关按钮整体启用 / 禁用。
    ''' </summary>
    ''' <remarks>
    ''' 体检 / 修复 / 更新都会起子进程，重复点击会同时跑两份，
    ''' 既浪费又会让状态显示错乱。开始前先全部禁掉，结束后恢复。
    ''' </remarks>
    Private Sub SetDshMaintenanceBusy(Busy As Boolean)
        IsDshMaintenanceBusy = Busy
        For Each btn In {BtnDshCheckEnv, BtnDshRepair, BtnDshUpdateAll, BtnDshMigrate}
            Try
                btn.IsEnabled = Not Busy
            Catch ex As Exception
                Logger.Warn(ex, "DSH：切换维护按钮状态失败")
            End Try
        Next
    End Sub

    ''' <summary>打开 PCL DSH 的数据根目录。</summary>
    Private Sub BtnOpenDshRoot_Click() Handles BtnOpenDshRoot.Click
        Try
            OpenExplorer(ModDSH.DshRoot)
        Catch ex As Exception
            Logger.Error(ex, "打开 DSH 数据目录失败")
            Hint($"无法打开目录：{ex.Message}", HintType.Red)
        End Try
    End Sub

    ''' <summary>跳到下载页的「镜像版本」，那里可以查看并安装任意版本的 dsh。</summary>
    Private Sub BtnCheckDshUpdate_Click() Handles BtnCheckDshUpdate.Click
        Try
            FrmMain.PageChange(New FormMain.PageStackData With {.Page = FormMain.PageType.Download},
                               FormMain.PageSubType.DownloadMirror)
        Catch ex As Exception
            Logger.Error(ex, "跳转镜像版本页失败")
            Hint($"无法打开下载页：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub BtnRefreshDshUsage_Click() Handles BtnRefreshDshUsage.Click
        RefreshDshDiskUsage()
    End Sub

    ''' <summary>
    ''' 刷新「插件市场数据源」那一行的说明文字。
    ''' </summary>
    ''' <remarks>
    ''' 决策 4-b 的开关本身由 <c>SettingService</c> 自动绑定（<c>DshPluginUseCdn</c>），
    ''' 这里只负责把「当前用的是哪个源、缓存多久」写清楚 —— 否则用户勾完不知道生效没生效。
    ''' </remarks>
    Private Sub RefreshDshPluginSourceNote()
        Try
            If LabDshPluginSourceNote Is Nothing Then Return

            Dim isIndex As Boolean = DshPluginMarket.DshPluginUseCdnSource
            Dim name As String = If(isIndex, "插件索引站", "GitHub topic")
            Dim ttl As String = DshPluginMarket.DshPluginCacheTtlText
            Dim extra As String

            If isIndex Then
                extra = "索引站是静态文件，刷新没有次数限制；缓存只用来避免短时间内重复下载 4 MB 数据。"
            Else
                extra = "GitHub 匿名搜索接口每小时限 10 次，缓存 30 分钟是为了不把配额烧光。"
            End If

            LabDshPluginSourceNote.Text =
                $"当前：{name} · 缓存有效期 {ttl}" & vbCrLf & extra & vbCrLf &
                "要立刻生效：切到「下载 → 插件社区」点一次「刷新插件列表」。"
        Catch ex As Exception
            Logger.Warn($"DSH：刷新插件数据源说明失败（可忽略）：{ex.Message}")
        End Try
    End Sub

    ''' <summary>用户切换「插件市场数据源」后，立刻更新下面的说明文字。</summary>
    ''' <remarks>
    ''' ⚠️ 用 <c>Change</c> 事件（第二个参数 <c>user</c> 为 True 表示真·用户操作）。
    ''' 只响应 user=True 的那些，避免 RefreshSettings 回填时也走一遍。
    ''' </remarks>
    Private Sub CheckDshPluginUseCdn_Change(sender As Object, user As Boolean) Handles CheckDshPluginUseCdn.Change
        If Not user Then Return
        RefreshDshPluginSourceNote()
    End Sub

#Region "环境体检"

    ''' <summary>
    ''' 「检查环境」按钮：跑一份体检报告并展示。
    ''' </summary>
    ''' <remarks>
    ''' 体检要起若干子进程（node -v / pnpm --version）并遍历实例目录，
    ''' 实测几百毫秒 —— 必须放后台线程，否则设置页会卡住。
    ''' </remarks>
    Private Sub BtnDshCheckEnv_Click() Handles BtnDshCheckEnv.Click
        If IsDshMaintenanceBusy Then Return
        SetDshMaintenanceBusy(True)
        LabDshHealth.Text = "正在体检……"

        RunInNewThread(
            Sub()
                Dim report As DshDoctor.DshHealthReport = Nothing
                Dim errMsg As String = Nothing
                Try
                    report = DshDoctor.RunHealthCheck()
                Catch ex As Exception
                    errMsg = ex.Message
                    Logger.Error(ex, "DSH：体检失败")
                End Try

                RunInUi(
                    Sub()
                        Try
                            SetDshMaintenanceBusy(False)
                            If errMsg IsNot Nothing Then
                                LabDshHealth.Text = $"体检失败：{errMsg}"
                                Hint($"环境体检出错：{errMsg}", HintType.Red)
                                Return
                            End If
                            ApplyDshReport(report)
                        Catch ex As Exception
                            Logger.Error(ex, "DSH：展示体检结果失败")
                        End Try
                    End Sub)
            End Sub, "DSH 环境体检")
    End Sub

    ''' <summary>把一份体检报告渲染到维护卡片上。</summary>
    Private Sub ApplyDshReport(Report As DshDoctor.DshHealthReport)
        DshLastReport = Report
        If Report Is Nothing Then
            LabDshHealth.Text = "体检没有返回结果。"
            Return
        End If

        ' 只把「有问题的 + 关键结论」写进卡片，完整报告放日志。
        ' 卡片位置有限，全铺出来会很长，用户还得自己找重点。
        Dim sb As New Text.StringBuilder()
        For Each item In Report.Items
            If item.Level = DshDoctor.DshCheckLevel.Info Then Continue For
            Dim mark As String
            Select Case item.Level
                Case DshDoctor.DshCheckLevel.Ok : mark = "✓"
                Case DshDoctor.DshCheckLevel.Warning : mark = "!"
                Case DshDoctor.DshCheckLevel.Error : mark = "✕"
                Case Else : mark = "·"
            End Select
            sb.AppendLine($"{mark} {item.Title}：{item.Message}")
        Next
        sb.AppendLine()
        sb.AppendLine(Report.Summary)
        If Report.RepairableCount > 0 Then
            sb.AppendLine($"其中 {Report.RepairableCount} 项可以点「一键修复」自动处理。")
        End If
        LabDshHealth.Text = sb.ToString().TrimEnd()

        ' ⚠️ 维护卡片默认是折叠的（IsSwapped=True），不然设置页第一屏全是按钮。
        ' 但体检出问题还让用户自己去找，等于白体检 —— 有问题就自动展开。
        EnsureDshMaintenanceExpanded()

        ' 完整报告（含 Info 级别的信息）落到日志，方便排查时对照。
        Try
            Logger.Info("DSH：体检报告" & vbCrLf & Report.ToPlainText())
        Catch ex As Exception
            Logger.Warn(ex, "DSH：体检报告写入日志失败")
        End Try

        If Report.HasError Then
            Hint(Report.Summary & " 详情见「日志」页。", HintType.Red)
        ElseIf Report.RepairableCount > 0 Then
            Hint(Report.Summary, HintType.Blue)
        Else
            Hint(Report.Summary, HintType.Green)
        End If
    End Sub

    ''' <summary>确保维护卡片处于展开状态。已经展开时不做任何事（不打断用户手动折叠）。</summary>
    Private Sub EnsureDshMaintenanceExpanded()
        Try
            If CardDshMaintenance.IsSwapped Then
                CardDshMaintenance.IsSwapped = False
            End If
        Catch ex As Exception
            Logger.Warn(ex, "DSH：自动展开维护卡片失败（不影响体检结果本身）")
        End Try
    End Sub

#End Region

#Region "一键修复"

    Private Sub BtnDshRepair_Click() Handles BtnDshRepair.Click
        If IsDshMaintenanceBusy Then Return

        ' 没体检过就先体检 —— 修复动作完全依赖报告里标记的可修复项
        If DshLastReport Is Nothing Then
            Hint("请先点「检查环境」，让 PCL 知道该修什么。", HintType.Blue)
            Return
        End If

        Dim repairable = DshLastReport.RepairableCount
        If repairable = 0 Then
            Hint("体检没有发现需要修复的问题。", HintType.Green)
            Return
        End If

        ' 历史遗留目录是**不可恢复**的删除，必须单独确认
        Dim legacy = DshLastReport.Items.FirstOrDefault(
            Function(i) i.Title = "历史遗留数据" AndAlso i.Level <> DshDoctor.DshCheckLevel.Ok)
        If legacy IsNot Nothing Then
            Dim choice As Integer = MyMsgBox(
                "体检发现旧版本留下的数据目录，可以清理。" & vbCrLf & vbCrLf &
                legacy.Detail & vbCrLf & vbCrLf &
                "⚠️ 删除后无法恢复。如果你在里面还有想保留的会话记录，请先自行备份。" & vbCrLf & vbCrLf &
                "要一并删除吗？",
                "确认清理遗留数据", "同时删除", "保留不动")
            If choice <> 1 Then
                ' 用户选择保留 → 把它从待修列表里剔除，其余照修
                legacy.Repairable = False
                Hint("已保留旧数据目录，其余问题照常修复。", HintType.Blue)
            End If
        End If

        SetDshMaintenanceBusy(True)
        Dim progressControl As New DshMaintenanceProgress("正在修复环境")
        progressControl.Show()

        RunInNewThread(
            Sub()
                Dim outcomes As List(Of DshDoctor.DshRepairOutcome) = Nothing
                Dim errMsg As String = Nothing
                Try
                    outcomes = DshDoctor.AutoRepair(
                        DshLastReport,
                        Sub(stage, message, pct)
                            RunInUi(Sub() progressControl.Update(stage.ToString(), message, pct))
                        End Sub)
                Catch ex As Exception
                    errMsg = ex.Message
                    Logger.Error(ex, "DSH：一键修复失败")
                End Try

                RunInUi(
                    Sub()
                        Try
                            progressControl.Close()
                            SetDshMaintenanceBusy(False)

                            If errMsg IsNot Nothing Then
                                Hint($"修复过程出错：{errMsg}", HintType.Red)
                                Return
                            End If

                            ShowDshRepairOutcomes(outcomes)
                            ' 修完立刻重体检一次，让报告反映真实现状
                            BtnDshCheckEnv_Click()
                        Catch ex As Exception
                            Logger.Error(ex, "DSH：展示修复结果失败")
                        End Try
                    End Sub)
            End Sub, "DSH 一键修复")
    End Sub

    ''' <summary>把修复结果汇总成一个对话框。</summary>
    Private Sub ShowDshRepairOutcomes(Outcomes As List(Of DshDoctor.DshRepairOutcome))
        If Outcomes Is Nothing OrElse Outcomes.Count = 0 Then
            Hint("没有需要修复的项目。", HintType.Green)
            Return
        End If

        Dim okCount = Outcomes.Where(Function(o) o.Success).Count()
        Dim sb As New Text.StringBuilder()
        For Each o In Outcomes
            sb.AppendLine($"{If(o.Success, "✓", "✕")} {o.Title}")
            sb.AppendLine($"    {o.Message}")
        Next

        Logger.Info($"DSH：一键修复结果（成功 {okCount}/{Outcomes.Count}）" & vbCrLf & sb.ToString())

        If okCount = Outcomes.Count Then
            MyMsgBox($"全部修复完成（{okCount} 项）。" & vbCrLf & vbCrLf & sb.ToString(),
                     "修复完成")
            Hint($"已修复 {okCount} 项。", HintType.Green)
        Else
            MyMsgBox($"部分项目未能修复（成功 {okCount}/{Outcomes.Count}）。" & vbCrLf & vbCrLf &
                     sb.ToString() & vbCrLf &
                     "失败项的详细原因见「日志」页。",
                     "修复完成（部分失败）")
            Hint($"{Outcomes.Count - okCount} 项修复失败，详情见日志。", HintType.Red)
        End If
    End Sub

#End Region

#Region "一键更新"

    ''' <summary>
    ''' 「一键更新」：把 Node / pnpm / dsh 本体 / 已装插件都升到当前通道的最新版。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 这是本页里唯一会**改动用户已有环境**的操作，所以：
    '''   - 开始前弹一次确认，把要做的事说清楚
    '''   - Node / pnpm 只在**缺失或不合格**时才装（不会去动系统上那份）
    '''   - dsh 按当前更新通道解析目标版本
    '''   - 插件只在用户确认后才动，且需要实例处于停止状态
    ''' </remarks>
    Private Sub BtnDshUpdateAll_Click() Handles BtnDshUpdateAll.Click
        If IsDshMaintenanceBusy Then Return

        Dim channelName As String = ModDSH.DshChannelName(ModDSH.DshCurrentChannel)
        Dim choice As Integer = MyMsgBox(
            "将把运行时环境更新到当前更新通道的最新版本。" & vbCrLf & vbCrLf &
            $"· 更新通道：{channelName}（可在「下载 → 镜像版本」里切换）" & vbCrLf &
            "· Node.js / pnpm：缺失或不合格时才安装私有副本，**不会改动你系统上已有的那份**" & vbCrLf &
            "· dsh 本体：就地覆盖安装，**不会影响任何实例的配置、会话与插件**" & vbCrLf &
            "· 已装插件：仅在下方勾选时才更新" & vbCrLf & vbCrLf &
            "开始更新吗？",
            "一键更新", "开始", "取消")
        If choice <> 1 Then Return

        ' 问一下要不要顺带更新插件 —— 插件更新会改实例目录，不能默认替用户决定
        Dim updatePlugins As Boolean = False
        If ModDSH.DshInstanceCount > 0 Then
            Dim pluginChoice As Integer = MyMsgBox(
                "是否同时把已安装的插件也更新到最新版？" & vbCrLf & vbCrLf &
                "· 插件更新会修改实例的 profile 目录（不影响会话记录）" & vbCrLf &
                "· 需要实例处于停止状态；有实例在跑时会自动跳过插件更新",
                "插件更新", "一起更新", "跳过插件")
            updatePlugins = (pluginChoice = 1)
        End If

        SetDshMaintenanceBusy(True)
        Dim progressControl As New DshMaintenanceProgress("正在更新运行时")
        progressControl.Show()

        RunInNewThread(
            Sub()
                Dim summary As New List(Of String)
                Dim errMsg As String = Nothing
                Try
                    RunDshUpdateAll(progressControl, summary, updatePlugins)
                Catch ex As Exception
                    errMsg = ex.Message
                    Logger.Error(ex, "DSH：一键更新失败")
                End Try

                RunInUi(
                    Sub()
                        Try
                            progressControl.Close()
                            SetDshMaintenanceBusy(False)

                            If errMsg IsNot Nothing Then
                                Hint($"更新过程出错：{errMsg}", HintType.Red)
                                Return
                            End If

                            Dim body As String = String.Join(vbCrLf, summary)
                            Logger.Info("DSH：一键更新完成" & vbCrLf & body)
                            MyMsgBox(body, "更新完成")
                            RefreshDshDiskUsage()
                            BtnDshCheckEnv_Click()
                        Catch ex As Exception
                            Logger.Error(ex, "DSH：展示更新结果失败")
                        End Try
                    End Sub)
            End Sub, "DSH 一键更新")
    End Sub

    ''' <summary>一键更新的实际流程（后台线程）。</summary>
    Private Sub RunDshUpdateAll(Progress As DshMaintenanceProgress,
                                ByRef Summary As List(Of String),
                                UpdatePlugins As Boolean)
        ' ⚠️ 变量名不能叫 step —— 那是 VB 的 For...Step 关键字（BC30183）
        Dim stepNo As Integer = 0
        Const TotalSteps As Integer = 4

        ' ---- 1. Node ----
        stepNo += 1
        Report(Progress, stepNo - 1, TotalSteps, "检查 Node.js……")
        Try
            Dim nodeExe = DshRuntime.DetectNode()
            If String.IsNullOrWhiteSpace(nodeExe) Then
                Report(Progress, stepNo - 1, TotalSteps, "未找到 Node.js，正在装配……")
                Dim installed = DshInstaller.RepairInstallPrivateNode(
                    Sub(s, m, p) RunInUi(Sub() Progress.Update(s.ToString(), m, p)))
                If String.IsNullOrWhiteSpace(installed) Then
                    Summary.Add("✕ Node.js：装配失败")
                Else
                    Summary.Add($"✓ Node.js：已装配私有副本 {installed}")
                End If
            Else
                Dim ver = DshRuntime.ReadNodeVersionVerbose(nodeExe)
                ' 系统上那份合格就原样复用 —— 不去"更新"它，那可能影响用户的其它项目
                Summary.Add($"✓ Node.js：{If(ver, "未知版本")} 可用，无需更新（{nodeExe}）")
            End If
        Catch ex As Exception
            Logger.Error(ex, "DSH：更新 Node 失败")
            Summary.Add($"✕ Node.js：{ex.Message}")
        End Try

        ' ---- 2. pnpm ----
        stepNo += 1
        Report(Progress, stepNo - 1, TotalSteps, "检查 pnpm……")
        Try
            Dim pnpmExe = DshRuntime.DetectPnpm()
            If String.IsNullOrWhiteSpace(pnpmExe) OrElse Not DshRuntime.ProbeExecutable(pnpmExe, "--version") Then
                Report(Progress, stepNo - 1, TotalSteps, "pnpm 不可用，正在装配……")
                Dim installed = DshInstaller.RepairInstallPrivatePnpm(
                    Sub(s, m, p) RunInUi(Sub() Progress.Update(s.ToString(), m, p)))
                If String.IsNullOrWhiteSpace(installed) Then
                    Summary.Add("✕ pnpm：装配失败（不影响 dsh 启动）")
                Else
                    Summary.Add($"✓ pnpm：已装配私有副本 {installed}")
                End If
            Else
                Summary.Add($"✓ pnpm：可用，无需更新（{pnpmExe}）")
            End If
        Catch ex As Exception
            Logger.Error(ex, "DSH：更新 pnpm 失败")
            Summary.Add($"✕ pnpm：{ex.Message}")
        End Try

        ' ---- 3. dsh 本体 ----
        stepNo += 1
        Report(Progress, stepNo - 1, TotalSteps, "正在解析 dsh 目标版本……")
        Try
            Dim target As String = ModDSH.DshVersion
            Dim channelName As String = ModDSH.DshChannelName(ModDSH.DshCurrentChannel)
            Try
                Dim versions = DshRegistry.FetchVersions()
                If versions IsNot Nothing AndAlso versions.Count > 0 Then
                    Dim picked = DshRegistry.ResolveVersionForChannel(versions, ModDSH.DshCurrentChannel)
                    If picked IsNot Nothing Then target = picked.Version
                End If
            Catch ex As Exception
                Logger.Warn($"DSH：更新时获取版本列表失败，回退到 {ModDSH.DshVersion}")
                Summary.Add($"! 无法连接镜像源，改用锁定版本 {ModDSH.DshVersion}")
            End Try

            Dim current = DshRuntime.GetInstalledDshVersion()
            If String.Equals(current, target, StringComparison.OrdinalIgnoreCase) Then
                Summary.Add($"✓ dsh：{current} 已是{channelName}通道的最新版，无需更新")
            Else
                Report(Progress, stepNo - 1, TotalSteps,
                       $"正在安装 dsh {target}（当前 {If(current, "未安装")}）……")
                DshInstaller.InstallDshVersion(target, DshRegistry.DshCurrentMirrorUrl,
                    Sub(s, m, p) RunInUi(Sub() Progress.Update(s.ToString(), m, p)))
                Summary.Add($"✓ dsh：{If(current, "未安装")} → {target}")
            End If
        Catch ex As Exception
            Logger.Error(ex, "DSH：更新 dsh 本体失败")
            Summary.Add($"✕ dsh 本体：{ex.Message}")
        End Try

        ' ---- 4. 插件 ----
        stepNo += 1
        Report(Progress, stepNo - 1, TotalSteps, "检查已安装插件……")
        If Not UpdatePlugins Then
            Summary.Add("· 插件：按你的选择跳过")
        Else
            Try
                UpdateAllPlugins(Progress, Summary)
            Catch ex As Exception
                Logger.Error(ex, "DSH：更新插件失败")
                Summary.Add($"✕ 插件：{ex.Message}")
            End Try
        End If

        Report(Progress, TotalSteps, TotalSteps, "更新完成")
    End Sub

    ''' <summary>把每个停止状态的实例里的插件全部 add 一遍（就地升级）。</summary>
    Private Sub UpdateAllPlugins(Progress As DshMaintenanceProgress, ByRef Summary As List(Of String))
        ModDSH.DshEnsureInstancesLoaded()
        Dim instances = ModDSH.DshInstances
        If instances Is Nothing OrElse instances.Count = 0 Then
            Summary.Add("· 插件：没有实例")
            Return
        End If

        Dim totalPlugins As Integer = 0
        Dim donePlugins As Integer = 0
        Dim perInstance As New List(Of Tuple(Of DshInstance, List(Of String)))

        For Each inst In instances
            If inst.IsRunning OrElse inst.HasLiveProcess Then
                Summary.Add($"· 插件：{inst.Name} 正在运行，已跳过")
                Continue For
            End If
            Try
                Dim installed = DshPluginMarket.ListInstalledPlugins(inst)
                If installed Is Nothing OrElse installed.Count = 0 Then
                    Summary.Add($"· 插件：{inst.Name} 没有已安装的插件")
                    Continue For
                End If
                Dim specs = installed.Keys.ToList()
                perInstance.Add(Tuple.Create(inst, specs))
                totalPlugins += specs.Count
            Catch ex As Exception
                Logger.Error(ex, $"DSH：读取 {inst.Name} 的插件列表失败")
                Summary.Add($"✕ 插件：读取 {inst.Name} 的列表失败（{ex.Message}）")
            End Try
        Next

        If totalPlugins = 0 Then Return

        For Each pair In perInstance
            Dim inst = pair.Item1
            For Each spec In pair.Item2
                donePlugins += 1
                Report(Progress, donePlugins - 1, totalPlugins,
                       $"正在更新插件（{donePlugins}/{totalPlugins}）：{spec}")
                Try
                    ' pnpm add 同一包名 = 就地升级到最新，不需要先 remove
                    DshPluginMarket.InstallPlugin(inst, spec)
                Catch ex As Exception
                    Logger.Warn(ex, $"DSH：更新插件失败（{inst.Name} / {spec}）")
                    Summary.Add($"✕ 插件：{inst.Name} 的 {spec} 更新失败")
                End Try
            Next
            Summary.Add($"✓ 插件：{inst.Name} 的 {pair.Item2.Count} 个插件已更新")
        Next
    End Sub

#End Region

#Region "迁移数据目录"

    ''' <summary>
    ''' 「迁移数据目录」：把整个 DshRoot 搬到用户选的位置（支持跨盘）。
    ''' </summary>
    ''' <remarks>
    ''' ⭐ 用户特别强调过：**迁移后 PCL 读取路径必须同步更新**。
    ''' 这件事由 <see cref="DshMigrate.Migrate"/> 的收尾动作完成 ——
    ''' 它写设置 <c>DshDataRoot</c>，而 <see cref="ModDSH.DshRoot"/> 是
    ''' 所有 DSH 路径的唯一真相来源，所以改完立刻全局生效。
    ''' </remarks>
    Private Sub BtnDshMigrate_Click() Handles BtnDshMigrate.Click
        If IsDshMaintenanceBusy Then Return

        Try
            Dim currentRoot As String = ModDSH.DshRoot
            Dim startDir As String = If(ModDSH.DshIsRootCustomized,
                Path.GetDirectoryName(currentRoot.TrimEnd("\"c)),
                ModDSH.DshDefaultRoot)

            Dim selected As String = Dialogs.SelectFolder(
                "选择新的数据目录（会在这里新建一份完整的 PCL DSH 数据）", False, startDir).FirstOrDefault()
            If String.IsNullOrWhiteSpace(selected) Then Return

            ' 预检 —— 预检会说清楚为什么不行，不能等搬到一半才失败
            Dim reject As String = DshMigrate.PreflightCheck(selected)
            If reject IsNot Nothing Then
                MyMsgBox(reject, "无法迁移")
                Hint("迁移条件不满足，详见弹窗说明。", HintType.Red)
                Return
            End If

            ' 估算体积，让用户在确认框里看到量级
            Dim sizeText As String = "计算中"
            Try
                sizeText = DshDoctor.FormatBytes(DshMigrate.MeasureDirectorySize(currentRoot))
            Catch ex As Exception
                Logger.Warn(ex, "DSH：估算迁移体积失败")
            End Try

            Dim choice As Integer = MyMsgBox(
                "将把整个 PCL DSH 数据目录搬到新位置。" & vbCrLf & vbCrLf &
                $"从：{currentRoot}" & vbCrLf &
                $"到：{selected}" & vbCrLf & vbCrLf &
                $"数据量约 {sizeText}，请确保目标磁盘有足够空间。" & vbCrLf & vbCrLf &
                "· 复制完成后会校验文件数量，一致才删除原目录" & vbCrLf &
                "· 如果校验不通过，原目录会完整保留" & vbCrLf &
                "· 迁移完成后，PCL 会自动改用新位置（无需你手动改任何设置）" & vbCrLf & vbCrLf &
                "注意：迁移期间不能有实例正在运行。",
                "确认迁移数据目录", "开始迁移", "取消")
            If choice <> 1 Then Return

            RunDshMigration(selected)
        Catch ex As Exception
            Logger.Error(ex, "DSH：迁移流程启动失败")
            Hint($"无法启动迁移：{ex.Message}", HintType.Red)
        End Try
    End Sub

    ''' <summary>反斜杠字符（VB 里不能写字面量，见项目备忘）。</summary>
    Private ReadOnly BS_CHAR As Char = ChrW(92)

    ''' <summary>
    ''' 「恢复到默认位置」：把数据目录搬回出厂默认位置。
    ''' </summary>
    ''' <remarks>
    ''' 用户把数据搬到别的盘之后，经常想搬回来（临时盘满了、或者想换台机器继续用）。
    ''' 单独给一个按钮，省得他自己去找默认目录在哪。
    ''' </remarks>
    Private Sub BtnDshMigrateBack_Click() Handles BtnDshMigrateBack.Click
        If IsDshMaintenanceBusy Then Return
        Try
            Dim currentRoot As String = ModDSH.DshRoot
            Dim target As String = ModDSH.DshDefaultRoot

            If String.Equals(System.IO.Path.GetFullPath(currentRoot).TrimEnd(BS_CHAR),
                              System.IO.Path.GetFullPath(target).TrimEnd(BS_CHAR),
                              StringComparison.OrdinalIgnoreCase) Then
                Hint("数据目录已经在默认位置了，不需要迁移。", HintType.Blue)
                Return
            End If

            Dim reject As String = DshMigrate.PreflightCheck(target)
            If reject IsNot Nothing Then
                MyMsgBox(reject, "无法迁移")
                Hint("迁移条件不满足，详见弹窗说明。", HintType.Red)
                Return
            End If

            Dim sizeText As String = "计算中"
            Try
                sizeText = DshDoctor.FormatBytes(DshMigrate.MeasureDirectorySize(currentRoot))
            Catch ex As Exception
                Logger.Warn(ex, "DSH：估算迁移体积失败")
            End Try

            Dim choice As Integer = MyMsgBox(
                "将把整个 PCL DSH 数据目录搬回默认位置。" & vbCrLf & vbCrLf &
                $"从：{currentRoot}" & vbCrLf &
                $"到：{target}" & vbCrLf & vbCrLf &
                $"数据量约 {sizeText}，请确保目标磁盘有足够空间。" & vbCrLf & vbCrLf &
                "· 复制完成后会校验文件数量，一致才删除原目录" & vbCrLf &
                "· 如果校验不通过，原目录会完整保留" & vbCrLf &
                "· 迁移完成后，PCL 会自动改用新位置（无需你手动改任何设置）" & vbCrLf & vbCrLf &
                "注意：迁移期间不能有实例正在运行。",
                "恢复到默认位置", "开始迁移", "取消")
            If choice <> 1 Then Return

            RunDshMigration(target)
        Catch ex As Exception
            Logger.Error(ex, "DSH：恢复默认位置流程启动失败")
            Hint($"无法启动迁移：{ex.Message}", HintType.Red)
        End Try
    End Sub

    ''' <summary>
    ''' 执行迁移（带进度浮层与结果展示）。两个入口共用。
    ''' </summary>
    ''' <param name="TargetRoot">目标数据根目录（调用方已确认过）。</param>
    Private Sub RunDshMigration(TargetRoot As String)
        SetDshMaintenanceBusy(True)
        Dim progressControl As New DshMaintenanceProgress("正在迁移数据目录")
        progressControl.Show()

        RunInNewThread(
                Sub()
                    Dim result As DshMigrate.DshMigrateResult = Nothing
                    Dim errMsg As String = Nothing
                    Try
                        result = DshMigrate.Migrate(TargetRoot,
                            Sub(stage, message, pct)
                                RunInUi(Sub() progressControl.Update(stage, message, pct))
                            End Sub)
                    Catch ex As Exception
                        errMsg = ex.Message
                        Logger.Error(ex, "DSH：迁移数据目录失败")
                    End Try

                    RunInUi(
                        Sub()
                            Try
                                progressControl.Close()
                                SetDshMaintenanceBusy(False)

                                If errMsg IsNot Nothing Then
                                    Hint($"迁移出错：{errMsg}", HintType.Red)
                                    Return
                                End If

                                If result Is Nothing Then
                                    Hint("迁移没有返回结果，请查看日志。", HintType.Red)
                                    Return
                                End If

                                If result.Success Then
                                    Dim extra As String = ""
                                    If result.WasCrossVolume Then extra = vbCrLf & "（跨盘迁移，已使用复制模式）"
                                    MyMsgBox(
                                        result.Message & extra & vbCrLf & vbCrLf &
                                        $"新的数据目录：{ModDSH.DshRoot}" & vbCrLf & vbCrLf &
                                        "PCL 已经改用新位置，现在可以正常启动实例了。",
                                        "迁移完成")
                                    Hint("数据目录迁移完成。", HintType.Green)
                                    ' 路径全变了，缓存与显示都要重来
                                    DshBrowserList = Nothing
                                    Refresh()
                                    RefreshDshDiskUsage()
                                Else
                                    MyMsgBox(result.Message, "迁移未完成")
                                    Hint("迁移未完成，原数据未受影响。", HintType.Red)
                                End If
                            Catch ex As Exception
                                Logger.Error(ex, "DSH：展示迁移结果失败")
                            End Try
                        End Sub)
                End Sub, "DSH 迁移数据目录")
    End Sub

#End Region

#Region "从其他 dsh 环境同步"

    ''' <summary>刷新「从其他环境导入」下方的说明行。</summary>
    Private Sub RefreshDshSyncNote()
        Try
            If LabDshSyncNote Is Nothing Then Return
            Dim inst As DshInstance = ModDSH.DshSelectedInstance
            Dim detected As String = DshProfileSync.DshSyncDefaultSourceHome()

            Dim parts As New List(Of String)
            If detected IsNot Nothing Then
                parts.Add($"检测到本机已有环境：{detected}")
            Else
                parts.Add("没有在用户目录下检测到 .dsh 环境，可以手动选择别的目录。")
            End If
            If inst IsNot Nothing Then
                parts.Add($"目标实例：{inst.DisplayName}")
            Else
                parts.Add("当前没有实例，请先新建一个。")
            End If
            LabDshSyncNote.Text = String.Join(vbCrLf, parts)
        Catch ex As Exception
            Logger.Warn(ex, "DSH：刷新同步说明失败（可忽略）")
        End Try
    End Sub

    ''' <summary>
    ''' 从其他 dsh 环境把插件与 API Key 同步到当前实例。
    ''' </summary>
    ''' <remarks>
    ''' 流程刻意是「探测 → 展示清单 → 确认 → 执行」四步：
    ''' 同步会**真的改写**目标实例的凭据和插件（可能跑几分钟），
    ''' 所以必须先让用户看清楚要搬什么、搬到哪，再动手。
    ''' 探测阶段只读文件，不产生任何副作用。
    ''' </remarks>
    Private Sub BtnDshSyncFromEnv_Click() Handles BtnDshSyncFromEnv.Click
        If IsDshMaintenanceBusy Then Return

        Dim inst As DshInstance = ModDSH.DshSelectedInstance
        If inst Is Nothing Then
            Hint("请先新建一个实例，再同步配置。", HintType.Red)
            Return
        End If

        ' ── ① 选源环境 ──
        ' 先看用户主目录下有没有现成的 .dsh —— 那是绝大多数人的情况，
        ' 有的话直接问「用它吗」，省掉一次在文件对话框里翻盘符的操作。
        Dim detected As String = DshProfileSync.DshSyncDefaultSourceHome()
        Dim sourceHome As String = Nothing

        If detected IsNot Nothing Then
            Dim pick As Integer = MyMsgBox(
                "检测到本机已有一个 dsh 环境：" & vbCrLf & vbCrLf &
                detected & vbCrLf & vbCrLf &
                "要用它作为同步来源吗？",
                "选择同步来源", "就用它", "选择其他目录…")
            If pick = 1 Then
                sourceHome = detected
            ElseIf pick = 2 Then
                sourceHome = Dialogs.SelectFolder(
                    "选择要同步的 dsh 环境目录（里面应当有 profiles 文件夹）",
                    False, detected).FirstOrDefault()
            Else
                Return
            End If
        Else
            Dim startDir As String = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            sourceHome = Dialogs.SelectFolder(
                "选择要同步的 dsh 环境目录（里面应当有 profiles 文件夹）",
                False, startDir).FirstOrDefault()
        End If
        If String.IsNullOrWhiteSpace(sourceHome) Then Return

        If Not DshProfileSync.DshSyncLooksLikeHome(sourceHome) Then
            MyMsgBox(
                "这个目录看起来不是 dsh 环境。" & vbCrLf & vbCrLf &
                "dsh 环境目录里应当有 profiles\ 文件夹，或者 .credentials.yaml。" & vbCrLf & vbCrLf &
                $"你选的是：{sourceHome}",
                "不是 dsh 环境", "知道了", IsWarn:=True)
            Return
        End If

        ' ── ② 定 profile ──
        Dim profiles As List(Of String) = DshProfileSync.DshSyncListProfiles(sourceHome)
        If profiles.Count = 0 Then
            Hint("这个环境里没有找到任何 profile（profiles\ 下没有含 package.json 的子目录）。", HintType.Red)
            Return
        End If

        Dim profileName As String = profiles.FirstOrDefault(
            Function(p) String.Equals(p, ModDSH.DshProfileName, StringComparison.OrdinalIgnoreCase))
        If String.IsNullOrWhiteSpace(profileName) Then profileName = profiles(0)
        If profiles.Count > 1 Then
            Hint($"源环境里有 {profiles.Count} 个 profile（{String.Join("、", profiles)}），" &
                 $"本次同步「{profileName}」。", HintType.Blue)
        End If

        ' ── ③ 先探测（含使用数据），让用户选同步范围 ──
        Dim probe As DshProfileSync.DshSyncPlan = Nothing
        Try
            probe = DshProfileSync.DshSyncBuildPlan(sourceHome, profileName, inst, IncludeData:=True)
        Catch ex As Exception
            Logger.Error(ex, "DSH：构建同步计划失败")
            Hint($"读取源环境失败：{ex.Message}", HintType.Red)
            Return
        End Try

        If Not probe.HasAnything Then
            MyMsgBox(
                "这个环境里没有找到可以同步的内容。" & vbCrLf & vbCrLf &
                If(probe.Warning, "（没有可搬的 API Key，也没有可搬的插件）"),
                "没有可同步的内容", "知道了")
            Return
        End If

        ' 源环境里有使用数据 → 先问同步范围。
        ' 之所以要问：这两档的后果差别很大 ——
        ' 「仅配置」得到的是干净环境；「全量导入」会把旧会话一起带过来，
        ' 而旧会话里的工作区绝对路径在新机器上可能不存在。让用户自己选。
        Dim includeData As Boolean = False
        If probe.DataItems.Count > 0 Then
            Dim dataText As String = If(probe.DataBytes >= 0, DshDoctor.FormatBytes(probe.DataBytes), "未知大小")
            Dim scopeChoice As Integer = MyMsgBox(
                "源环境里除了插件和 API Key，还有使用数据。" & vbCrLf & vbCrLf &
                $"· 配置：{probe.Specs.Count} 个插件 + {probe.ApiKeys.Count} 个 API Key" & vbCrLf &
                $"· 使用数据：{probe.DataItems.Count} 项（{dataText}）" & vbCrLf &
                $"  {String.Join("、", probe.DataItems)}" & vbCrLf & vbCrLf &
                "【全量导入】连聊天记录、任务看板、审批白名单、外观偏好一起搬。" & vbCrLf &
                "   ⚠ 会话里存的是**工作区绝对路径**，换机器后可能失效，" & vbCrLf &
                "     旧会话有可能打不开。" & vbCrLf &
                "   ⚠ 会**覆盖**目标实例里同名的使用数据。" & vbCrLf & vbCrLf &
                "【仅配置】只搬插件和 API Key，新环境干净，不带旧数据。" & vbCrLf &
                "   （目标实例里已有的使用数据保持不动）",
                "选择同步范围", "全量导入", "仅配置", "取消")
            If scopeChoice <> 1 AndAlso scopeChoice <> 2 Then Return
            includeData = (scopeChoice = 1)
        End If

        ' 按用户选择重建计划
        Dim plan As DshProfileSync.DshSyncPlan = Nothing
        Try
            plan = DshProfileSync.DshSyncBuildPlan(sourceHome, profileName, inst, includeData)
        Catch ex As Exception
            Logger.Error(ex, "DSH：重建同步计划失败")
            Hint($"读取源环境失败：{ex.Message}", HintType.Red)
            Return
        End Try

        Dim body As New StringBuilder()
        body.AppendLine($"把「{sourceHome}」的配置同步到实例「{inst.DisplayName}」？")
        body.AppendLine()

        If plan.ApiKeys.Count > 0 Then
            body.AppendLine($"API Key：{plan.ApiKeys.Count} 个")
            For Each kv In plan.ApiKeys
                body.AppendLine($"  · {kv.Key} = {DshCredentials.MaskKey(kv.Value)}")
            Next
        Else
            body.AppendLine("API Key：源环境里没有配置，跳过。")
        End If
        body.AppendLine()

        If plan.Specs.Count > 0 Then
            body.AppendLine($"插件：{plan.Specs.Count} 个（会联网重新安装一遍）")
        End If
        If plan.LinkDeps.Count > 0 Then
            body.AppendLine($"本地插件：{plan.LinkDeps.Count} 个（会先把目录搬过来）")
            For Each ld In plan.LinkDeps
                body.AppendLine($"  · {ld.Name}")
            Next
        End If
        If plan.Specs.Count = 0 AndAlso plan.LinkDeps.Count = 0 Then
            body.AppendLine("插件：源环境里没有装插件，跳过。")
        End If

        body.AppendLine()
        body.AppendLine("说明：")
        body.AppendLine("· 插件是重新安装而不是复制目录，这样 pnpm 的锁文件才一致，")
        body.AppendLine("  以后你再装别的插件不会把同步过来的清掉。")
        body.AppendLine("· 会一并带上该 profile 的 cordis.patch.yml（插件的权限预设写在里面）。")
        If plan.DataItems.Count > 0 Then
            body.AppendLine($"· 使用数据：{String.Join("、", plan.DataItems)}")
            body.AppendLine("  （会覆盖目标实例里同名的数据）")
            body.AppendLine("· 搬运范围：配置 + 使用数据（不含日志与设备标识）。")
        Else
            body.AppendLine("· 只搬配置（插件 + 密钥 + profile 配置），不搬会话记录。")
        End If
        body.AppendLine("· 安装期间不能有实例正在运行，否则 pnpm 会改写正在使用的目录。")
        body.AppendLine()
        body.AppendLine("⚠ 注意：这里不搬 dsh 本体。")
        ' 先把「当前实例会用哪个版本」讲清楚 —— 这是用户最容易困惑的点：
        ' 他以为「导入环境」会连版本一起带过来，其实环境目录里只有数据。
        Dim localVer As String = Nothing
        Try
            localVer = DshRuntime.GetInstalledDshVersion()
        Catch ex As Exception
            Logger.Warn(ex, "DSH：读取当前 dsh 版本失败")
        End Try
        body.AppendLine($"   当前实例用的是 dsh {If(String.IsNullOrWhiteSpace(localVer), "未知版本", localVer)}，" &
                        "同步不会改变它。")
        If Not String.IsNullOrWhiteSpace(plan.SourceDshVersion) Then
            body.AppendLine($"   （源环境那边是 dsh {plan.SourceDshVersion}）")
        End If
        body.AppendLine("   dsh 环境目录（如 .dsh）里只有数据，没有程序本体 ——")
        body.AppendLine("   用什么版本由「运行时」决定。想换成别的版本，请到")
        body.AppendLine("   「下载 → 镜像版本 → 运行时（可切换）」：")
        body.AppendLine("     · 装指定版本 → 点「安装此版本」")
        body.AppendLine("     · 用你手上现成的那份 → 点「导入运行时…」选它的文件夹")
        If Not String.IsNullOrWhiteSpace(plan.Warning) Then
            body.AppendLine()
            body.AppendLine("⚠ " & plan.Warning)
        End If

        If MyMsgBox(body.ToString(), "从其他环境同步", "开始同步", "取消") = 2 Then Return

        ' ── ④ 执行 ──
        Dim running As List(Of DshInstance) =
            ModDSH.DshInstances.Where(Function(x) x.HasLiveProcess).ToList()
        If running.Count > 0 Then
            Hint($"有实例正在运行（{String.Join("、", running.Select(Function(x) x.DisplayName))}），" &
                 "请先停止后再同步。", HintType.Red)
            Return
        End If

        SetDshMaintenanceBusy(True)
        Dim progressControl As New DshMaintenanceProgress("正在同步环境配置")
        progressControl.Show()

        RunInNewThread(
            Sub()
                Dim result As DshProfileSync.DshSyncResult = Nothing
                Dim errMsg As String = Nothing
                Try
                    result = DshProfileSync.DshSyncExecute(
                        plan,
                        Sub(stage, message, pct)
                            RunInUi(Sub() progressControl.Update(stage, message, pct))
                        End Sub)
                Catch ex As Exception
                    errMsg = ex.Message
                    Logger.Error(ex, "DSH：同步环境配置失败")
                End Try

                RunInUi(
                    Sub()
                        Try
                            progressControl.Close()
                            SetDshMaintenanceBusy(False)

                            If errMsg IsNot Nothing Then
                                Hint($"同步出错：{errMsg}", HintType.Red)
                                Return
                            End If
                            If result Is Nothing Then
                                Hint("同步没有返回结果，请查看日志。", HintType.Red)
                                Return
                            End If

                            MyMsgBox(result.Message, If(result.Success, "同步完成", "同步未全部完成"))
                            Hint(If(result.Success, "环境配置同步完成。", "同步部分失败，详见弹窗。"),
                                 If(result.Success, HintType.Green, HintType.Red))

                            ' 插件/凭据都变了，相关显示要重来
                            Refresh()
                            RefreshDshSyncNote()
                        Catch ex As Exception
                            Logger.Error(ex, "DSH：展示同步结果失败")
                        End Try
                    End Sub)
            End Sub, "DSH 同步环境配置")
    End Sub

#End Region

#Region "首次运行引导"

    ''' <summary>
    ''' 按设置决定是否显示首次运行引导卡片。
    ''' </summary>
    ''' <remarks>
    ''' 刻意做成**非强制**（决策 2-d = A）：它只是页面上的一张卡片，
    ''' 用户随时可以「知道了，不再显示」把它永久关掉，或者直接无视。
    ''' 不做成弹窗阻塞，是因为 dsh 的运行时装配本来就可以在点启动时自动完成 ——
    ''' 引导只是让用户少走点弯路，不该变成一道门。
    ''' </remarks>
    Private Sub UpdateDshFirstRunCard()
        Try
            Dim done As Boolean = Settings.Get(Of Boolean)("DshFirstRunDone")
            CardDshFirstRun.Visibility = If(done, Visibility.Collapsed, Visibility.Visible)
        Catch ex As Exception
            Logger.Warn(ex, "DSH：读取首次运行标志失败，按未完成处理")
            CardDshFirstRun.Visibility = Visibility.Visible
        End Try
    End Sub

    ''' <summary>「带我做一遍」：直接跑体检，缺什么就装什么。</summary>
    Private Sub BtnDshGuideGo_Click() Handles BtnDshGuideGo.Click
        ' 关掉引导卡片（用户已经明确开始动手了，再留着是噪音）
        Try
            Settings.Set("DshFirstRunDone", True)
        Catch ex As Exception
            Logger.Warn(ex, "DSH：写入首次运行标志失败")
        End Try
        UpdateDshFirstRunCard()

        ' 直接体检；如果有可修复项，紧接着弹修复确认
        BtnDshCheckEnv_Click()
        Hint("正在检查环境……如果有问题，检查完点「一键修复」即可。", HintType.Blue)
    End Sub

    ''' <summary>「知道了，不再显示」：永久关闭引导卡片。</summary>
    Private Sub BtnDshGuideDismiss_Click() Handles BtnDshGuideDismiss.Click
        Try
            Settings.Set("DshFirstRunDone", True)
            UpdateDshFirstRunCard()
            Hint("已隐藏使用引导。需要时可以在「设置 → 启动设置」里重新检查环境。", HintType.Green)
        Catch ex As Exception
            Logger.Error(ex, "DSH：隐藏首次运行引导失败")
            Hint($"操作失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

#End Region

#Region "进度浮层"

    ''' <summary>
    ''' 维护操作的进度浮层。
    ''' </summary>
    ''' <remarks>
    ''' 用 PCL 自带的 <see cref="MyMsgBox"/> 系列风格不太合适 ——
    ''' 那是个模态对话框，而维护操作可能要跑十几分钟（pnpm 装 dsh 依赖树）。
    ''' 这里做成一个可关闭的独立小窗，用户可以关掉它继续干别的，
    ''' 后台任务不受影响（进度回调会检查窗口是否已关）。
    ''' </remarks>
    Private Class DshMaintenanceProgress
        Inherits Window

        Private TextStage As TextBlock
        Private TextMessage As TextBlock
        Private BarProgress As ProgressBar
        Private IsClosedFlag As Boolean = False

        Public Sub New(Title As String)
            Me.Title = Title
            Me.Width = 460
            Me.Height = 170
            Me.WindowStartupLocation = WindowStartupLocation.CenterScreen
            Me.ResizeMode = ResizeMode.NoResize
            Me.WindowStyle = WindowStyle.ToolWindow
            Me.ShowInTaskbar = True

            ' 跟随 PCL 的浅色主题，避免和主窗口观感割裂
            Me.Background = New SolidColorBrush(Color.FromRgb(&HF2, &HF2, &HF2))

            TextStage = New TextBlock With {
                .FontSize = 13,
                .FontWeight = FontWeights.Bold,
                .Foreground = New SolidColorBrush(Color.FromRgb(&H33, &H33, &H33)),
                .Margin = New Thickness(20, 18, 20, 0)
            }
            TextMessage = New TextBlock With {
                .FontSize = 12.5,
                .TextWrapping = TextWrapping.Wrap,
                .Foreground = New SolidColorBrush(Color.FromRgb(&H66, &H66, &H66)),
                .Margin = New Thickness(20, 8, 20, 0)
            }
            BarProgress = New ProgressBar With {
                .Height = 6,
                .Margin = New Thickness(20, 16, 20, 0),
                .Minimum = 0,
                .Maximum = 100,
                .Value = 0
            }

            Dim panel As New StackPanel()
            panel.Children.Add(TextStage)
            panel.Children.Add(TextMessage)
            panel.Children.Add(BarProgress)
            Me.Content = panel

            AddHandler Me.Closed, Sub() IsClosedFlag = True
        End Sub

        ''' <summary>更新进度显示。可能被后台线程调用，但内部已经 marshal 到 UI。</summary>
        Public Sub Update(Stage As String, Message As String, Progress As Double)
            If IsClosedFlag Then Return
            TextStage.Text = If(Stage, "")
            TextMessage.Text = If(Message, "")
            If Progress >= 0 Then
                ' 有些阶段报的是 0~1，有些阶段（如 InstallDshVersion）直接给百分比
                BarProgress.Value = Math.Max(0, Math.Min(100, Progress * 100))
                BarProgress.IsIndeterminate = False
            Else
                BarProgress.IsIndeterminate = True
            End If
        End Sub

        ''' <summary>安全关闭（已关就忽略）。</summary>
        Public Shadows Sub Close()
            If IsClosedFlag Then Return
            MyBase.Close()
        End Sub
    End Class

#End Region

#Region "小工具"

    ''' <summary>把进度上报给浮层；浮层已关时静默跳过。</summary>
    Private Sub Report(Progress As DshMaintenanceProgress,
                       Current As Integer, Total As Integer, Message As String)
        If Progress Is Nothing Then Return
        Dim pct As Double = If(Total > 0, Current / CDbl(Total), -1)
        RunInUi(Sub() Progress.Update(If(pct >= 0, $"{Current} / {Total}", ""), Message, pct))
    End Sub

#End Region

    ''' <summary>
    ''' 统计运行时与实例数据的磁盘占用。
    ''' </summary>
    ''' <remarks>
    ''' 放到后台线程 —— runtime 里有 Node、pnpm 和 dsh 的完整依赖树
    ''' （光 LibreOffice 就 117MB，文件数量上万），同步遍历会让设置页卡住好几秒。
    ''' </remarks>
    Private Sub RefreshDshDiskUsage()
        LabDshDiskUsage.Text = "正在统计磁盘占用……"

        RunInNewThread(
            Sub()
                Dim runtimeSize As Long = 0
                Dim instanceSize As Long = 0
                Dim err As String = Nothing
                Try
                    runtimeSize = MeasureDirectory(ModDSH.DshRoot & "runtime")
                    instanceSize = MeasureDirectory(ModDSH.DshRoot & "instances")
                Catch ex As Exception
                    err = ex.Message
                    Logger.Warn($"统计 DSH 磁盘占用失败：{ex.Message}")
                End Try

                RunInUi(
                    Sub()
                        Try
                            If LabDshDiskUsage Is Nothing Then Return
                            If err IsNot Nothing Then
                                LabDshDiskUsage.Text = $"统计失败：{err}"
                                Return
                            End If
                            LabDshDiskUsage.Text =
                                $"数据目录：{ModDSH.DshRoot}{vbCrLf}" &
                                $"运行时（Node / pnpm / dsh，所有实例共享）：{FormatSize(runtimeSize)}{vbCrLf}" &
                                $"实例数据（配置 / 会话 / 插件 / 凭据）：{FormatSize(instanceSize)}{vbCrLf}" &
                                $"合计：{FormatSize(runtimeSize + instanceSize)}"
                        Catch ex As Exception
                            Logger.Error(ex, "刷新 DSH 占用显示失败")
                        End Try
                    End Sub)
            End Sub, "DSH 统计磁盘占用")
    End Sub

    ''' <summary>递归统计目录字节数；目录不存在返回 0。</summary>
    Private Function MeasureDirectory(Path As String) As Long
        If String.IsNullOrWhiteSpace(Path) OrElse Not Directory.Exists(Path) Then Return 0L
        Dim total As Long = 0
        Try
            For Each filePath In Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)
                Try
                    total += New FileInfo(filePath).Length
                Catch
                    '单个文件读不到（被占用 / 权限不足）就跳过，不该让整次统计失败
                End Try
            Next
        Catch ex As Exception
            Logger.Warn($"遍历目录失败（{Path}）：{ex.Message}")
        End Try
        Return total
    End Function

    Private Function FormatSize(Bytes As Long) As String
        If Bytes < 1024L Then Return $"{Bytes} B"
        If Bytes < 1024L * 1024L Then Return $"{Bytes / 1024.0:0.0} KB"
        If Bytes < 1024L * 1024L * 1024L Then Return $"{Bytes / 1024.0 / 1024.0:0.0} MB"
        Return $"{Bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB"
    End Function

#End Region

End Class
