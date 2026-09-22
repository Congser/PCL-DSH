''' <summary>
''' DSH 实例的启动 / 停止编排。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 为什么需要这个模块
''' ═══════════════════════════════════════════════════════════════════════
''' 早期版本把启动流程放在 <see cref="PageDSHOnline"/> 的加载器里，原因是
''' PCL 的加载器机制（<c>PageLoaderInit</c> / <c>PageLoaderRestart</c>）只存在于
''' <see cref="MyPageRight"/> 上，而左栏继承的是 <see cref="MyPageLeft"/>，拿不到。
'''
''' 那个方案有两个后遗症：
'''   1. 左栏必须想办法拿到右栏实例才能点火（<c>BindConsole</c> 那套转发）
'''   2. 为了阻止页面打开时自动跑加载器，得用 <c>IsLaunchAllowed</c> 标志「空跑一次」
'''      —— 因为 <c>PageLoaderRestart</c> 首行是 <c>If Not PageLoaderAutoRun Then Return</c>，
'''      用 <c>AutoRun:=False</c> 关掉之后手动重试也会一起失效
'''
''' 多实例之后「谁负责启动」变得更模糊（左栏、日志页、实例列表都要能点启动），
''' 所以干脆把编排抽成一个**与页面无关**的模块：
'''   - 后台线程干活，进度通过 <see cref="LaunchProgress"/> 事件抛出来
'''   - 界面只负责订阅事件并更新自己的进度条
'''   - 任何页面都能调用，不再需要互相持有引用
'''
''' 代价：DSH 启动不再出现在「任务管理」里（那是 Loader 才有的能力）。
''' 但左栏本来就有完整的启动中视图（加载环 + 进度条 + 阶段文案），够用了。
''' </summary>
Public Module DshLauncher

#Region "状态"

    Private ReadOnly _SyncRoot As New Object()

    ''' <summary>正在启动中的实例 Id 集合（防止对同一实例重复点火）。</summary>
    Private ReadOnly _Starting As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

#End Region

#Region "事件"

    ''' <summary>
    ''' 启动进度。
    ''' </summary>
    ''' <param name="Instance">目标实例。</param>
    ''' <param name="Stage">当前阶段的可读文案。</param>
    ''' <param name="Percent">进度 0~1；负数表示「不确定进度」（转圈）。</param>
    ''' <remarks>已在 UI 线程触发。</remarks>
    Public Event LaunchProgress(Instance As DshInstance, Stage As String, Percent As Double)

    ''' <summary>
    ''' 启动流程结束（成功或失败）。
    ''' </summary>
    ''' <param name="Instance">目标实例。</param>
    ''' <param name="Success">是否成功。</param>
    ''' <param name="ReadyUrl">成功时的带 token 就绪 URL。</param>
    ''' <param name="Message">失败原因；成功时为 Nothing。</param>
    ''' <remarks>已在 UI 线程触发。</remarks>
    Public Event LaunchFinished(Instance As DshInstance,
                                Success As Boolean,
                                ReadyUrl As String,
                                Message As String)

#End Region

#Region "查询"

    ''' <summary>某个实例是否正在启动中。</summary>
    Public Function IsStarting(Instance As DshInstance) As Boolean
        If Instance Is Nothing Then Return False
        SyncLock _SyncRoot
            Return _Starting.Contains(Instance.Id)
        End SyncLock
    End Function

    ''' <summary>正在启动的实例数量。</summary>
    Public ReadOnly Property StartingCount As Integer
        Get
            SyncLock _SyncRoot
                Return _Starting.Count
            End SyncLock
        End Get
    End Property

#End Region

#Region "启动"

    ''' <summary>
    ''' 异步启动某个实例。立即返回，进度通过事件抛出。
    ''' </summary>
    ''' <remarks>
    ''' 幂等：对同一个实例重复调用只会启动一次（第二次会提示并返回）。
    ''' </remarks>
    Public Sub StartAsync(Instance As DshInstance)
        If Instance Is Nothing Then Return

        SyncLock _SyncRoot
            If _Starting.Contains(Instance.Id) Then
                Hint($"实例「{Instance.DisplayName}」正在启动中，请稍候……")
                Return
            End If
            If Instance.HasLiveProcess Then
                '进程还活着但没就绪（比如上一次启动超时了）——先收干净再启，
                '否则新进程会因端口被自己占着而起不来
                Hint($"实例「{Instance.DisplayName}」的进程仍在运行，正在先回收……")
            End If
            _Starting.Add(Instance.Id)
        End SyncLock

        RunInNewThread(
            Sub()
                Dim ok As Boolean = False
                Dim url As String = Nothing
                Dim message As String = Nothing

                Try
                    ModDSH.DshEnsureInstanceDirectory(Instance)

                    ' ── 0. 上一次的残留进程 ──
                    If Instance.HasLiveProcess Then
                        Report(Instance, "回收上一次的进程", -1)
                        DshService.StopService(Instance, Force:=True)
                    End If

                    ' ── 1. 装配运行时（Node / pnpm / dsh 包，全局共享） ──
                    Instance.State = ModDSH.DshState.CheckingRuntime
                    Report(Instance, "检测运行环境", -1)

                    Dim runtime = DshInstaller.EnsureRuntime(
                        Sub(stage As DshInstaller.DshInstallStage, text As String, pct As Double)
                            '运行时装配期间切到 Installing 状态，让界面能显示"正在下载"之类的提示
                            If Instance.State <> ModDSH.DshState.Installing AndAlso pct >= 0 Then
                                Instance.State = ModDSH.DshState.Installing
                            End If
                            Report(Instance, text, pct)
                        End Sub)

                    ' ── 2. 已有服务在跑就直接复用 ──
                    If Instance.IsRunning AndAlso Not String.IsNullOrWhiteSpace(Instance.ReadyUrl) Then
                        Logger.Info($"DSH：实例「{Instance.DisplayName}」已在运行，直接复用")
                        Report(Instance, "服务已在运行", 1)
                        ok = True
                        url = Instance.ReadyUrl
                    Else
                        ' ── 3. 启动服务并等待就绪信号 ──
                        Instance.State = ModDSH.DshState.Starting
                        Report(Instance, "启动 DSH 服务", -1)
                        url = DshService.StartService(Instance, runtime)
                        Report(Instance, "服务已就绪", 1)
                        ok = True
                    End If

                Catch ex As Exception
                    message = ex.Message
                    Logger.Error(ex, $"DSH：实例「{Instance.DisplayName}」启动失败")
                    Instance.State = ModDSH.DshState.Failed
                    If String.IsNullOrWhiteSpace(Instance.LastError) Then Instance.LastError = ex.Message
                Finally
                    SyncLock _SyncRoot
                        _Starting.Remove(Instance.Id)
                    End SyncLock
                    Dim finalOk As Boolean = ok
                    Dim finalUrl As String = url
                    Dim finalMsg As String = message
                    RunInUi(Sub() RaiseEvent LaunchFinished(Instance, finalOk, finalUrl, finalMsg))
                End Try
            End Sub, "DSH 启动 " & Instance.DisplayName)
    End Sub

    ''' <summary>抛出一次进度（自动切到 UI 线程）。</summary>
    Private Sub Report(Instance As DshInstance, Stage As String, Percent As Double)
        RunInUi(Sub() RaiseEvent LaunchProgress(Instance, Stage, Percent))
    End Sub

#End Region

#Region "停止"

    ''' <summary>
    ''' 异步停止某个实例。立即返回。
    ''' </summary>
    ''' <remarks>
    ''' 不抛 <see cref="LaunchFinished"/> —— 停止是「状态变更」而不是「启动流程」，
    ''' 界面订阅 <see cref="ModDSH.DshInstanceStateChanged"/> 即可。
    ''' </remarks>
    Public Sub StopAsync(Instance As DshInstance)
        If Instance Is Nothing Then Return

        '正在启动的时候点停止：先把它从"启动中"里摘出来，
        '否则启动线程跑完后会覆盖掉停止结果
        SyncLock _SyncRoot
            _Starting.Remove(Instance.Id)
        End SyncLock

        RunInNewThread(
            Sub()
                Try
                    DshService.StopService(Instance, Force:=True)
                    RunInUi(Sub() Hint($"实例「{Instance.DisplayName}」已停止。", HintType.Green))
                Catch ex As Exception
                    Logger.Error(ex, $"DSH：停止实例「{Instance.DisplayName}」失败")
                    RunInUi(Sub() Hint($"停止失败：{ex.Message}", HintType.Red))
                End Try
            End Sub, "DSH 停止 " & Instance.DisplayName)
    End Sub

    ''' <summary>异步停止全部实例。</summary>
    Public Sub StopAllAsync()
        For Each inst In ModDSH.DshInstances
            If inst.HasLiveProcess OrElse inst.State <> ModDSH.DshState.Stopped Then
                StopAsync(inst)
            End If
        Next
    End Sub

#End Region

End Module
