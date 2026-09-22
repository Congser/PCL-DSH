Imports System.Diagnostics
Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
'WMI：用来读别的进程的 CommandLine（Process 类拿不到，.NET Framework 也没有
'Process.CommandLine 属性）。只用于在回收孤儿进程时辨认「这是不是我们拉起来的 dsh」。
Imports System.Management

''' <summary>
''' dsh 服务的进程级生命周期管理（**按实例**）。
'''
''' 这是 POC-2 / POC-3 核心逻辑的 VB.NET 移植，多实例改造后所有状态都挂在
''' <see cref="DshInstance"/> 上，本模块只提供「对某个实例做某个动作」的无状态方法。
'''
''' 关键机制（已在 POC 阶段实测验证）：
'''   1. 启动命令：`node [dsh]/lib/bin.js --profile web --no-open --port [N]`
'''      - `--no-open` 必须带，否则 dsh 会自己弹出系统浏览器
'''      - `--profile` 必须用内置模板名（web / headless / sdk / sdk-minimal / acp）
'''   2. 就绪信号：dsh 启动完成后会把**带认证 token 的完整 URL** 打印到 stdout，
'''      形如 `dsh web: http://127.0.0.1:19387/?token=...`
'''      源码注释原文：*"supervisors RPC as soon as they observe the line"*
'''      —— 这就是官方设计给监督进程的就绪信号，直接抓它即可，不要自己拼 URL
'''   3. 认证机制：该 URL 首次访问会下发 cookie（HTTP 303 → /），之后凭 cookie 访问；
'''      不带 token 且无 cookie 的请求一律 401
'''
''' 线程模型：
'''   - 启动/停止是**阻塞操作**，调用方应放在后台线程（PCL 的 Loader 或 Task.Run）
'''   - 状态变更经 <see cref="ModDSH.DshSetState"/> 自动切回 UI 线程
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 多实例改造要点
''' ═══════════════════════════════════════════════════════════════════════
'''   - 进程引用、就绪信号、输出缓冲、同步锁**全部**下移到 <see cref="DshInstance"/>
'''   - 事件回调是**普通方法**（不是 lambda），这样能正常 RemoveHandler。
'''     为了在回调里找回「这是哪个实例的进程」，用 sender 反查
'''     （见 <see cref="OwnerOf"/>）—— 实例数 ≤ 4，线性查找的开销可以忽略。
'''   - <c>DSH_HOME</c> 用实例自己的目录，这是实例之间唯一的隔离点
''' </summary>
Public Module DshService

#Region "常量"

    ''' <summary>
    ''' dsh 打印的就绪信号正则。
    ''' 例：`dsh web: http://127.0.0.1:19387/?token=xxx`
    ''' </summary>
    Private ReadOnly ReadyUrlPattern As New Regex("dsh web:\s*(https?://\S+)", RegexOptions.Compiled)

    ''' <summary>启动命令行的特征串，用来在一堆 node 进程里认出「这是 PCL 拉起来的 dsh」。</summary>
    Private Const DshCommandMarker As String = "dsh\lib\bin.js"

#End Region

#Region "端口与孤儿进程"

    ''' <summary>
    ''' 判断某个 TCP 端口是否已被占用（仅看 127.0.0.1 的回环监听）。
    ''' </summary>
    ''' <remarks>
    ''' 刻意用 <c>IPGlobalProperties.GetActiveTcpListeners</c> 而不是 PCL 既有的
    ''' <see cref="ModNet.FindFreePort"/>：后者是「让操作系统随便给一个空端口」，
    ''' 解决不了我们真正的问题 —— 我们**想用特定端口**，得先知道它是不是被占了。
    '''
    ''' 只看回环地址：dsh 默认监听 127.0.0.1，用户机器上别的服务占了同一个端口号
    ''' 但绑在 0.0.0.0 或局域网网卡时，两者其实不冲突，不该误判。
    ''' </remarks>
    Public Function IsPortListening(Port As Integer) As Boolean
        If Port <= 0 Then Return False
        Try
            Dim props = Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            For Each ep In props.GetActiveTcpListeners()
                If ep.Port <> Port Then Continue For
                If Net.IPAddress.IsLoopback(ep.Address) OrElse ep.Address.Equals(Net.IPAddress.Any) Then
                    Return True
                End If
            Next
            Return False
        Catch ex As Exception
            Logger.Error(ex, $"DSH：探测端口 {Port} 占用状态失败")
            '探测失败时保守地当作"被占用"，让调用方走回退分支，而不是硬闯
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 找出所有「PCL 拉起来的、但我们不认识的」dsh node 进程并结束掉。
    ''' </summary>
    ''' <returns>回收掉的进程数。</returns>
    ''' <remarks>
    ''' ═══════════════════════════════════════════════════════════════════════
    ''' 为什么需要这个
    ''' ═══════════════════════════════════════════════════════════════════════
    ''' dsh 是**独立进程**：PCL 被强制结束（任务管理器、崩溃、调试器停止）时，
    ''' 子进程**不会**跟着走。于是下一次启动就会出现
    ''' `EADDRINUSE: address already in use 127.0.0.1:19387` ——
    ''' 用户看到的是「点启动报一堆红字」，但其实是上一轮的尸体还占着端口。
    '''
    ''' PCL 正常退出时靠 <see cref="StopAll"/> 回收；这里是**异常退出的兜底**。
    '''
    ''' 判定方式（三重条件，避免误杀）：
    '''   1. 进程名是 node
    '''   2. 命令行含 dsh 入口路径特征（<see cref="DshCommandMarker"/>）
    '''   3. 进程 Id 不在当前实例登记的活进程里
    ''' 第 3 条是关键 —— 否则会把用户**正在用**的实例一起杀掉。
    ''' </remarks>
    Public Function ReclaimOrphanProcesses() As Integer
        Dim killed As Integer = 0
        Try
            '先收集"合法"的进程 Id（当前实例正在用的，一个都不能动）
            Dim known As New HashSet(Of Integer)
            For Each inst In ModDSH.DshInstances
                Dim p As Process = inst.Process
                If p Is Nothing Then Continue For
                Try
                    If Not p.HasExited Then known.Add(p.Id)
                Catch
                    '进程已退出，忽略
                End Try
            Next

            For Each item In Process.GetProcessesByName("node")
                Try
                    If known.Contains(item.Id) Then Continue For

                    Dim cmd As String = GetCommandLine(item.Id)
                    If String.IsNullOrWhiteSpace(cmd) Then Continue For
                    If Not cmd.Contains(DshCommandMarker) Then Continue For
                    '只认 PCL 自己管的运行时，用户自己全局装的 dsh 不属于我们管。
                    '⚠️ 判据必须用**当前实际的数据根目录**（ModDSH.DshRuntimeDir），
                    '   不能写死 "PCL\DSH\runtime" —— 用户可以把数据目录迁到任意盘
                    '   （实测迁到了 E:\test\），写死的话：
                    '     ① 迁移后残留的孤儿进程永远回收不掉（端口一直被占）
                    '     ② 而且会去匹配一个根本不存在的路径，这个兜底等于失效
                    Dim runtimeDir As String = ModDSH.DshRuntimeDir
                    If String.IsNullOrWhiteSpace(runtimeDir) Then Continue For
                    '⚠️ .NET Framework 的 String.Contains 没有 StringComparison 重载
                    '   （那是 .NET Core 2.1+ 才有的），只能先 ToLowerInvariant 再比
                    If Not cmd.ToLowerInvariant().Contains(runtimeDir.TrimEnd("\"c).ToLowerInvariant()) Then
                        Continue For
                    End If

                    Logger.Warn($"DSH：回收孤儿 dsh 进程 PID={item.Id}")
                    KillProcessTree(item)
                    killed += 1
                Catch ex As Exception
                    Logger.Error(ex, $"DSH：回收进程 {item.Id} 失败")
                Finally
                    item.Dispose()
                End Try
            Next
        Catch ex As Exception
            Logger.Error(ex, "DSH：扫描孤儿 dsh 进程失败")
        End Try

        If killed > 0 Then Logger.Info($"DSH：已回收 {killed} 个孤儿 dsh 进程")
        Return killed
    End Function

    ''' <summary>
    ''' 读取指定进程的完整命令行。
    ''' </summary>
    ''' <remarks>
    ''' 走 WMI。<c>Process.MainModule</c> 拿不到命令行，
    ''' 而 .NET Framework 的 <c>Process</c> 没有 <c>CommandLine</c> 属性
    ''' （那是 .NET 5+ 才有的）。
    ''' 注意：读别的进程命令行需要权限，失败就返回空串（调用方按"不认识"处理，即不杀）。
    ''' </remarks>
    Private Function GetCommandLine(Pid As Integer) As String
        Try
            Using searcher As New Management.ManagementObjectSearcher(
                    $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {Pid}")
                For Each obj As Management.ManagementBaseObject In searcher.Get()
                    Return If(obj("CommandLine")?.ToString(), "")
                Next
            End Using
        Catch ex As Exception
            '权限不足或进程已消失 —— 静默返回空
            Logger.Warn($"DSH：读取进程 {Pid} 命令行失败：{ex.Message}")
        End Try
        Return ""
    End Function

    ''' <summary>
    ''' 为实例决定最终要用的端口，必要时清理占用者。
    ''' </summary>
    ''' <param name="Instance">目标实例。</param>
    ''' <returns>可以安全监听的端口。</returns>
    ''' <remarks>
    ''' ═══════════════════════════════════════════════════════════════════════
    ''' 为什么要做「预检」而不是「撞了再说」
    ''' ═══════════════════════════════════════════════════════════════════════
    ''' 原实现直接在命令行里塞 <c>Instance.Port</c>，端口被占时由 dsh 抛
    ''' <c>EADDRINUSE</c>。那个报错是 **node 的十六进制堆栈**，普通用户完全看不懂，
    ''' 只会觉得"这软件坏了"（用户就是这样报障的）。
    '''
    ''' 现在的策略，按优先级：
    '''   1. 先回收一轮孤儿进程（它们就是我们自己上次留下的）
    '''   2. 回收后端口空了 → 用配置的端口
    '''   3. 仍被占（是别人的服务）→ 顺着往上找第一个空闲端口，**照常启动**，
    '''      并在日志里说明换了端口
    '''   4. 实例配置的是 0（自动）→ 直接用系统给的随机空闲端口
    '''
    ''' 「换端口继续跑」比「报错让用户自己解决」好得多：端口号对用户没有语义，
    ''' 界面上显示的永远是实际端口，用户感知不到差异。
    ''' </remarks>
    Private Function ResolvePortForStart(Instance As DshInstance) As Integer
        '配置为 0 → 自动
        If Instance.Port <= 0 Then
            Dim auto_ As Integer = FindFreePortOrThrow()
            Logger.Info($"DSH[{Instance.DisplayName}]：配置为自动端口，选用 {auto_}")
            Return auto_
        End If

        '① 先回收孤儿 —— 端口被自己上次留下的进程占着是最常见的情况
        If IsPortListening(Instance.Port) Then
            Dim reclaimed As Integer = ReclaimOrphanProcesses()
            If reclaimed > 0 Then
                '给操作系统一点时间释放 socket（Close 到内核真正回收有个短窗口）
                Thread.Sleep(200)
            End If
        End If

        '② 回收后空了 → 用配置端口
        If Not IsPortListening(Instance.Port) Then Return Instance.Port

        '③ 还被别人占着 → 顺着往上找一个空闲的
        Logger.Warn($"DSH[{Instance.DisplayName}]：端口 {Instance.Port} 已被其它程序占用，将改用空闲端口")
        For offset As Integer = 1 To 32
            Dim candidate As Integer = Instance.Port + offset
            If candidate > 65535 Then Exit For
            If Not IsPortListening(candidate) Then
                Logger.Info($"DSH[{Instance.DisplayName}]：改用空闲端口 {candidate}")
                Return candidate
            End If
        Next

        '④ 实在找不到 → 交给系统随机分配
        Dim fallback As Integer = FindFreePortOrThrow()
        Logger.Warn($"DSH[{Instance.DisplayName}]：附近端口均被占用，回退到系统分配端口 {fallback}")
        Return fallback
    End Function

#End Region

#Region "启动"

    ''' <summary>
    ''' 启动某个实例的 dsh 服务并等待其就绪。
    ''' </summary>
    ''' <param name="Instance">目标实例。端口取 <see cref="DshInstance.Port"/>，0 表示自动。</param>
    ''' <param name="Runtime">已探测好的运行时环境。必须 IsUsable = True。</param>
    ''' <returns>带认证 token 的就绪 URL。</returns>
    ''' <exception cref="InvalidOperationException">环境不可用、端口占用或启动超时。</exception>
    ''' <remarks>阻塞调用。请在后台线程执行。</remarks>
    Public Function StartService(Instance As DshInstance,
                                 Runtime As DshRuntime.DshRuntimeInfo) As String
        If Instance Is Nothing Then Throw New ArgumentNullException(NameOf(Instance))

        SyncLock Instance.SyncRoot
            If Instance.HasLiveProcess Then
                Throw New InvalidOperationException($"实例「{Instance.DisplayName}」的服务已在运行，请先停止。")
            End If
            If Runtime Is Nothing OrElse Not Runtime.IsUsable Then
                Throw New InvalidOperationException(
                    $"dsh 运行时环境不完整，无法启动。{vbCrLf}" &
                    $"当前状态：{If(Runtime?.Describe(), "未探测")}")
            End If

            ModDSH.DshEnsureInstanceDirectory(Instance)
            Instance.ResetOutput()

            ' 端口选择：先回收孤儿进程，再确认端口可用；被别的程序占了就顺延。
            ' ⚠️ 这里**不能**直接相信 Instance.Port —— 见 ResolvePortForStart 的说明。
            Dim actualPort As Integer = ResolvePortForStart(Instance)

            ' 准备注入给 dsh 的 patch 文件（继承官方 profile 后再叠加我们的配置）
            EnsurePatchFile(Instance)

            ' ⭐ 启动前自愈：修掉 profile patch 里的「空数组占位符」
            ' 见 SanitizeProfilePatch 的注释 —— 不修的话 dsh 会直接解析失败起不来，
            ' 而且报错是英文 YAML 异常，用户完全看不懂。
            SanitizeProfilePatch(Instance)

            ' ⭐ 启动前自愈：补上缺失的本地插件链接（link: / file: 依赖）
            ' 见 RepairLocalPluginLinks 的注释 —— 缺了它 dsh 会报
            ' "cannot resolve profile bundle <名字>" 然后直接退出。
            RepairLocalPluginLinks(Instance)

            ' ── 组装启动参数 ──
            ' 顺序对齐官方 Electron 桌面壳：--profile <name> --no-open --port <N>
            Dim profileName As String = If(String.IsNullOrWhiteSpace(Instance.Profile),
                                           ModDSH.DshProfileName, Instance.Profile)
            Dim args As String =
                $"""{Runtime.DshEntry}"" --profile {profileName} --no-open --port {actualPort}"

            Logger.Info($"DSH[{Instance.DisplayName}]：启动服务 → port={actualPort}, profile={profileName}")
            Logger.Info($"DSH[{Instance.DisplayName}]：命令：{Runtime.NodeExe} {args}")

            ' 工作目录设为安装目录，避免 dsh 在 PCL 目录里散落文件
            Dim psi As New ProcessStartInfo(Runtime.NodeExe, args) With {
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True,
                .StandardOutputEncoding = Encoding.UTF8,
                .StandardErrorEncoding = Encoding.UTF8,
                .WorkingDirectory = ModDSH.DshInstallDir
            }
            ' ⚠️ 环境变量一律走 DshRuntime 的封装。
            '   ProcessStartInfo.Environment 不继承父进程环境，必须先 SeedEnvironment，
            '   否则 node 连 SystemRoot / TEMP / USERPROFILE 都没有，直接启动失败。
            '   也绝不能用 EnvironmentVariables：它惰性物化时会因 Path/PATH 撞键抛异常。
            DshRuntime.SeedEnvironment(psi)

            ' DSH_HOME：**按实例隔离**的数据目录。这是多实例的核心。
            DshRuntime.SetEnvSafe(psi, "DSH_HOME", Instance.HomeDir)
            ' NO_COLOR：去掉 ANSI 色码，避免污染 stdout 正则匹配
            DshRuntime.SetEnvSafe(psi, "NO_COLOR", "1")
            ' PATH 前置私有 pnpm 目录，供 dsh 内部的插件管理调用
            If Not String.IsNullOrWhiteSpace(Runtime.PnpmExe) Then
                DshRuntime.PrependPath(psi, Path.GetDirectoryName(Runtime.PnpmExe))
            End If

            Instance.ReadySignal = New ManualResetEventSlim(False)

            Dim proc As New Process With {.StartInfo = psi, .EnableRaisingEvents = True}
            AddHandler proc.OutputDataReceived, AddressOf OnOutputData
            AddHandler proc.ErrorDataReceived, AddressOf OnErrorData
            AddHandler proc.Exited, AddressOf OnProcessExited

            Instance.State = ModDSH.DshState.Starting
            Instance.Process = proc

            Try
                proc.Start()
                proc.BeginOutputReadLine()
                proc.BeginErrorReadLine()
            Catch ex As Exception
                Instance.Process = Nothing
                Instance.LastError = ex.Message
                Instance.State = ModDSH.DshState.Failed
                Throw New InvalidOperationException($"启动 dsh 进程失败：{ex.Message}", ex)
            End Try

            ' 等待就绪信号
            If Instance.ReadySignal.Wait(ModDSH.DshStartupTimeoutMs) Then
                Instance.ActualPort = actualPort
                '把实际使用的端口写回实例配置。
                '否则用户看到的永远是「端口 19387（未启动）」，而实际跑在 19388 上，
                '一旦真出了连接问题，两边对不上会很难排查。
                If Instance.Port <> actualPort Then
                    Logger.Info($"DSH[{Instance.DisplayName}]：实际端口 {actualPort}，" &
                                $"已写回实例配置（原配置 {Instance.Port}）")
                    Instance.Port = actualPort
                    ModDSH.DshSaveInstances()
                End If
                Instance.State = ModDSH.DshState.Running
                Logger.Info($"DSH[{Instance.DisplayName}]：服务就绪，端口 {actualPort}")
                Return Instance.ReadyUrl
            End If

            ' 超时：区分「进程已死」与「进程活着但没打印 URL」
            If proc.HasExited Then
                Dim reason As String =
                    $"dsh 进程已退出（代码 {proc.ExitCode}），未能就绪。{vbCrLf}{vbCrLf}" &
                    "最近的输出：" & vbCrLf & Instance.GetRecentOutput()
                Instance.LastError = $"进程已退出（代码 {proc.ExitCode}）"
                Instance.State = ModDSH.DshState.Failed
                ' ⭐ 必须清理：进程虽已退出，但事件处理器还挂着、Process 对象还没释放。
                '    不清理的话下次启动时 Instance.Process 会指向一个已死的旧对象。
                CleanupProcess(Instance, proc, KillIfAlive:=False)
                Throw New InvalidOperationException(reason)
            End If

            Dim timeoutReason As String =
                $"等待 {ModDSH.DshStartupTimeoutMs \ 1000} 秒未收到 dsh 就绪信号。{vbCrLf}{vbCrLf}" &
                "最近的输出：" & vbCrLf & Instance.GetRecentOutput()
            Instance.LastError = $"等待就绪超时（{ModDSH.DshStartupTimeoutMs \ 1000} 秒）"
            Instance.State = ModDSH.DshState.Failed
            ' ⭐⭐ 关键修复：超时这一刻**进程往往还活着**（只是没打印就绪 URL）。
            '     原来这里直接 Throw，什么都不清理 —— 结果是：
            '     · dsh 进程继续在后台跑，还占着端口
            '     · Instance.Process 仍指着它，事件处理器还挂着
            '     · 用户看到"启动失败"，再点启动就撞端口冲突；
            '       更糟的是两个 dsh 可能同时写同一份数据
            '     所以必须先强杀 + 清理，再抛异常。
            Logger.Warn($"DSH[{Instance.DisplayName}]：等待就绪超时，正在清理可能仍在运行的进程")
            CleanupProcess(Instance, proc, KillIfAlive:=True)
            Throw New InvalidOperationException(timeoutReason)
        End SyncLock
    End Function

    ''' <summary>
    ''' 找空闲端口，失败则抛异常。
    ''' </summary>
    ''' <remarks>复用 PCL 既有的 <see cref="ModNet.FindFreePort"/>，避免重复造轮子。</remarks>
    Private Function FindFreePortOrThrow() As Integer
        Try
            Dim p As Integer = ModNet.FindFreePort()
            If p > 0 Then
                Logger.Info($"DSH：自动选择端口 {p}")
                Return p
            End If
        Catch ex As Exception
            Logger.Error(ex, "DSH：查找空闲端口失败")
        End Try
        ' 兜底：用默认端口，让 dsh 自己报占用错误，比静默失败更容易排查
        Logger.Warn($"DSH：无法自动选端口，回退默认端口 {ModDSH.DshDefaultPort}")
        Return ModDSH.DshDefaultPort
    End Function

    ''' <summary>
    ''' 写入实例的注入式 patch 文件（幂等）。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 文件名必须是 <c>$DSH_HOME/cordis.patch.yml</c>。
    ''' 从 app-boot 的 <c>readProfilePatches</c> 可以看到叠加顺序：
    ''' <code>
    ''' ① 各 bundle 的 patch
    ''' ② profile 的 cordis.patch.yml（$DSH_HOME/profiles/&lt;name&gt;/cordis.patch.yml）
    ''' ③ home 级的 cordis.patch.yml（$DSH_HOME/cordis.patch.yml）  ← 我们这一层
    ''' ④ --patch 命令行 overlay
    ''' </code>
    ''' 早期版本写的是 <c>pcl-dsh.patch.yml</c> —— **dsh 根本不读那个文件名**，
    ''' 文件被创建了但从未生效。这里改成官方约定的 home 级文件名。
    '''
    ''' 目前内容保持空 patch（<c>[]</c>）：模型接入配置走
    ''' <c>settings.yaml</c> 的 <c>llm-deepseek</c> 分节（能热重载，粒度也更细）。
    ''' 留着这个文件是为了给将来需要"替换整个 config"的场景留一个正确的注入点。
    ''' </remarks>
    Private Sub EnsurePatchFile(Instance As DshInstance)
        Try
            ModDSH.DshEnsureInstanceDirectory(Instance)
            Dim path As String = Instance.HomePatchFile
            If File.Exists(path) Then Return

            Dim content As String =
                "# PCL_DSH 注入配置" & vbCrLf &
                "#" & vbCrLf &
                "# 本文件由 PCL DSH 改版自动生成，请勿手动删除。" & vbCrLf &
                "# 位于 home 层（$DSH_HOME/cordis.patch.yml），优先级高于各 bundle 与 profile，" & vbCrLf &
                "# 但低于 --patch 命令行覆盖。" & vbCrLf &
                "#" & vbCrLf &
                "# 当前为空 patch：继承官方 profile 的全部默认行为。" & vbCrLf &
                "# 模型接入方式（官方 / 自定义网关）不在这里配置 ——" & vbCrLf &
                "# 那部分写在 settings.yaml 的 llm-deepseek 分节里，可以热重载。" & vbCrLf &
                "[]" & vbCrLf
            File.WriteAllText(path, content, New UTF8Encoding(False))
            Logger.Info($"DSH[{Instance.DisplayName}]：已生成注入配置：{path}")
        Catch ex As Exception
            ' patch 写入失败不应阻塞启动 —— 空 patch 等价于官方默认行为
            Logger.Error(ex, "DSH：写入注入配置失败（不影响启动）")
        End Try
    End Sub

    ''' <summary>
    ''' 启动前自愈：修掉 profile 级 patch 文件里的「空数组占位符」。
    ''' </summary>
    ''' <remarks>
    ''' ⭐ 背景（真实报障，代价很大）：
    '''
    ''' dsh 新建 profile 时用模板生成 <c>profiles\&lt;name&gt;\cordis.patch.yml</c>，
    ''' 模板里带一行 <c>[]</c>（空数组占位符）。而 <c>dsh-approval-gate</c> 插件的
    ''' 「一键初始化」是**纯文本追加**，会把 <c>- id: permission</c> 块拼到**文件末尾**：
    ''' <code>
    ''' []
    ''' # ── 自动审批模式 ──
    ''' - id: permission      ← 追加在这里
    ''' </code>
    ''' YAML 里 <c>[]</c> 是完整的空数组**文档**，后面不能再直接跟 <c>- </c>。
    ''' 于是 dsh 报 <c>YAMLException: end of the stream or a document separator is expected</c>
    ''' 并且**服务完全起不来**。用户看到的是英文 YAML 报错，无从下手。
    '''
    ''' 责任不完全在 PCL（插件写文件时没处理占位符），但**用户不该为此买单** ——
    ''' 所以每次启动前检查一遍、顺手修掉，让这个坑自愈。
    ''' 清理逻辑复用 <see cref="DshProfileSync.DshSanitizePatchYaml"/>（只删独立成行的 <c>[]</c>）。
    ''' </remarks>
    Private Sub SanitizeProfilePatch(Instance As DshInstance)
        Try
            If Instance Is Nothing Then Return
            Dim profileName As String = If(String.IsNullOrWhiteSpace(Instance.Profile),
                                           ModDSH.DshProfileName, Instance.Profile)
            ' ⚠️ 变量名不能叫 path —— 会遮蔽 System.IO.Path（VB 大小写不敏感），
            '   下一行的 Path.Combine 就会报「Combine 不是 String 的成员」。
            '   这是项目里反复踩过的坑（file / dir / path 三个都是这个家族）。
            Dim patchPath As String = Path.Combine(Instance.HomeDir, "profiles", profileName, "cordis.patch.yml")
            If Not File.Exists(patchPath) Then Return

            Dim raw As String = File.ReadAllText(patchPath, Encoding.UTF8)
            Dim cleaned As String = DshProfileSync.DshSanitizePatchYaml(raw)
            If String.Equals(raw, cleaned, StringComparison.Ordinal) Then Return

            ' 备份一次再改 —— 这个文件里有用户/插件写的配置，出问题要能还原
            Try
                Dim backup As String = patchPath & ".before-pcl-fix"
                If Not File.Exists(backup) Then File.Copy(patchPath, backup, False)
            Catch ex As Exception
                Logger.Warn(ex, "DSH：备份 patch 文件失败（继续修复）")
            End Try

            File.WriteAllText(patchPath, cleaned, New UTF8Encoding(False))
            Logger.Info($"DSH[{Instance.DisplayName}]：已修复 profile patch 的空数组占位符 → {patchPath}")
        Catch ex As Exception
            ' 修复失败不阻塞启动 —— 让 dsh 自己报错，至少用户能看到原始信息
            Logger.Error(ex, "DSH：修复 profile patch 失败（不影响启动流程）")
        End Try
    End Sub

    ''' <summary>
    ''' 启动前自愈：补上缺失的本地插件链接（<c>link:</c> / <c>file:</c> 依赖）。
    ''' </summary>
    ''' <remarks>
    ''' ⭐ 为什么需要（真实报障）：
    ''' 用户装了个本地插件（<c>link:...\local-plugins\gal-view</c>），
    ''' 某次 dsh 启动直接失败：
    ''' <code>
    ''' Error: dsh: cannot resolve profile bundle "gal-view" from the dsh
    ''' installation or ...\profiles\web; run 'dsh plugin --profile web install'
    ''' </code>
    ''' dsh 解析 bundle 时会去找 <c>profiles\&lt;name&gt;\node_modules\&lt;包名&gt;\package.json</c>。
    ''' 而 <c>link:</c> 依赖在 node_modules 里是一个**符号链接**，由 pnpm 在安装时创建。
    ''' 只要 <c>node_modules</c> 被重建过（装插件 / 从其他环境同步 / 一键修复），
    ''' 这些链接就会先被删掉再重建 —— 中间任何环节出岔子（pnpm 被中断、
    ''' 目录被占用、上一次安装失败），链接就**永久缺失**，
    ''' 之后每次启动 dsh 都失败，而且报错是英文的 node 堆栈，用户完全无从下手。
    '''
    ''' 这里在启动前检查一遍：清单里声明了 <c>link:</c> 依赖、
    ''' 但 node_modules 下没有对应条目（或条目是断链）→ 直接补一个 junction 上去。
    ''' 比"让用户去跑 pnpm install"快得多，也不依赖网络。
    '''
    ''' 刻意**不**调用 pnpm：这条路径必须在离线时也能救活环境。
    ''' </remarks>
    Private Sub RepairLocalPluginLinks(Instance As DshInstance)
        Try
            If Instance Is Nothing Then Return
            Dim profileName As String = If(String.IsNullOrWhiteSpace(Instance.Profile),
                                           ModDSH.DshProfileName, Instance.Profile)
            Dim profileDir As String = Path.Combine(Instance.HomeDir, "profiles", profileName)
            Dim pkgPath As String = Path.Combine(profileDir, "package.json")
            If Not File.Exists(pkgPath) Then Return

            Dim pkg As Newtonsoft.Json.Linq.JObject = Nothing
            Try
                pkg = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(pkgPath, Encoding.UTF8))
            Catch ex As Exception
                Logger.Warn(ex, "DSH：解析 profile 的 package.json 失败（跳过本地插件自愈）")
                Return
            End Try

            Dim deps = TryCast(pkg("dependencies"), Newtonsoft.Json.Linq.JObject)
            If deps Is Nothing Then Return

            Dim nodeModules As String = Path.Combine(profileDir, "node_modules")
            Dim repaired As Integer = 0

            For Each prop As Newtonsoft.Json.Linq.JProperty In deps.Properties()
                Dim depName As String = prop.Name
                Dim spec As String = If(prop.Value?.ToString(), "")
                ' 只处理本地路径依赖
                If Not (spec.StartsWith("link:", StringComparison.OrdinalIgnoreCase) OrElse
                        spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) Then Continue For

                Dim rawPath As String = spec.Substring(5)
                Dim target As String = Nothing
                Try
                    If Path.IsPathRooted(rawPath) Then
                        target = Path.GetFullPath(rawPath)
                    Else
                        ' pnpm 写的相对路径是**相对 profile 目录**的
                        target = Path.GetFullPath(Path.Combine(profileDir, rawPath))
                    End If
                Catch ex As Exception
                    Logger.Warn(ex, $"DSH：解析本地插件路径失败：{depName} → {rawPath}")
                    Continue For
                End Try

                If Not Directory.Exists(target) Then
                    ' 目标本身没了 —— 这个补不了，交给上层报错（用户至少能看到包名）
                    Logger.Warn($"DSH：本地插件 {depName} 的目标目录不存在，无法自愈：{target}")
                    Continue For
                End If

                ' 已存在且是有效链接 / 有效目录 → 不动
                Dim linkPath As String = Path.Combine(nodeModules, depName)
                Dim ok As Boolean = False
                Try
                    ok = Directory.Exists(linkPath) AndAlso File.Exists(Path.Combine(linkPath, "package.json"))
                Catch
                    ok = False
                End Try
                If ok Then Continue For

                ' 断链或缺失 → 重建
                If Not Directory.Exists(nodeModules) Then Directory.CreateDirectory(nodeModules)
                ' 作用域包（@scope/name）要先建好 @scope 目录
                Dim parent As String = Path.GetDirectoryName(linkPath)
                If Not String.IsNullOrWhiteSpace(parent) AndAlso Not Directory.Exists(parent) Then
                    Try
                        Directory.CreateDirectory(parent)
                    Catch ex As Exception
                        Logger.Warn(ex, $"DSH：创建 {parent} 失败")
                    End Try
                End If

                If DshMigrate.DshCreateJunction(linkPath, target) Then
                    repaired += 1
                    Logger.Info($"DSH[{Instance.DisplayName}]：已自愈本地插件链接 {depName} → {target}")
                Else
                    Logger.Warn($"DSH：本地插件 {depName} 的链接重建失败：{linkPath}")
                End If
            Next

            If repaired > 0 Then
                Logger.Info($"DSH[{Instance.DisplayName}]：共自愈 {repaired} 个本地插件链接")
            End If
        Catch ex As Exception
            ' 自愈失败不阻塞启动 —— 让 dsh 自己报原始错误，至少信息更完整
            Logger.Error(ex, "DSH：本地插件链接自愈失败（不影响启动流程）")
        End Try
    End Sub

#End Region

#Region "输出处理"

    ''' <summary>
    ''' 从事件 sender 反查是哪个实例的进程。
    ''' </summary>
    ''' <remarks>
    ''' 实例数上限 4，线性查找的开销可以忽略。
    ''' 用反查而不是 lambda 闭包，是为了让事件处理器仍是**普通方法** ——
    ''' lambda 每次求值都是新委托，<c>RemoveHandler</c> 就永远摘不掉。
    ''' </remarks>
    Private Function OwnerOf(sender As Object) As DshInstance
        Dim proc = TryCast(sender, Process)
        If proc Is Nothing Then Return Nothing
        For Each inst In ModDSH.DshInstances
            If inst.Process Is proc Then Return inst
        Next
        Return Nothing
    End Function

    Private Sub OnOutputData(sender As Object, e As DataReceivedEventArgs)
        If e.Data Is Nothing Then Return
        Dim inst As DshInstance = OwnerOf(sender)
        If inst Is Nothing Then Return
        inst.AppendOutput("[out] " & e.Data)
        TryCaptureReadyUrl(inst, e.Data)
    End Sub

    Private Sub OnErrorData(sender As Object, e As DataReceivedEventArgs)
        If e.Data Is Nothing Then Return
        Dim inst As DshInstance = OwnerOf(sender)
        If inst Is Nothing Then Return
        inst.AppendOutput("[err] " & e.Data)
        ' 就绪 URL 理论上走 stdout，但不同版本可能落在 stderr，两边都抓更稳
        TryCaptureReadyUrl(inst, e.Data)
    End Sub

    ''' <summary>
    ''' 尝试从一行输出中提取就绪 URL。每个实例只认第一次抓到的。
    ''' </summary>
    Private Sub TryCaptureReadyUrl(Instance As DshInstance, line As String)
        If Instance.ReadyUrl IsNot Nothing Then Return
        Dim m As Match = ReadyUrlPattern.Match(line)
        If Not m.Success Then Return

        Instance.ReadyUrl = m.Groups(1).Value
        Logger.Info($"DSH[{Instance.DisplayName}]：已捕获就绪 URL")
        ' 只释放信号，不做任何 UI 操作 —— 保持本模块与 UI 解耦
        Instance.ReadySignal?.Set()
    End Sub

    ''' <summary>取某个实例最近的 dsh 输出，用于错误提示与日志页。</summary>
    Public Function GetRecentOutput(Instance As DshInstance,
                                    Optional MaxLines As Integer = 40) As String
        If Instance Is Nothing Then Return "（无输出）"
        Return Instance.GetRecentOutput(MaxLines)
    End Function

    Private Sub OnProcessExited(sender As Object, e As EventArgs)
        Dim inst As DshInstance = OwnerOf(sender)
        If inst Is Nothing Then Return

        ' 进程意外退出（不是我们主动停的）
        If inst.State = ModDSH.DshState.Stopping Then Return

        Logger.Warn($"DSH[{inst.DisplayName}]：服务进程已退出")
        inst.ReadyUrl = Nothing
        inst.ActualPort = 0
        If inst.LastError Is Nothing Then inst.LastError = "服务进程意外退出"
        inst.State = ModDSH.DshState.Stopped
    End Sub

#End Region

#Region "停止"

    ''' <summary>
    ''' 彻底清理一个实例的进程资源（杀进程树 + 摘事件 + Dispose + 复位状态）。
    ''' </summary>
    ''' <param name="Instance">目标实例。</param>
    ''' <param name="Proc">要清理的进程对象；为 Nothing 时从实例取。</param>
    ''' <param name="KillIfAlive">进程还活着时是否强杀。</param>
    ''' <remarks>
    ''' ⭐ 抽出来是因为**启动失败路径也要用它**（真实缺陷）：
    ''' 原来只有 <see cref="StopService"/> 会清理进程，而
    ''' 「等就绪信号超时」（<c>DshStartupTimeoutMs</c> = 3 分钟）
    ''' 那条路径抛异常时**什么都不清理** ——
    ''' 于是 dsh 进程还在后台跑、还占着端口、<c>Instance.Process</c> 还指着它，
    ''' 而状态已经变成 <c>Failed</c>。用户看到"启动失败"，再点一次启动
    ''' 就会撞端口冲突；更糟的是两个 dsh 可能同时写同一份数据。
    '''
    ''' 幂等：进程已退出 / 已经清理过，再调也不会出错。
    ''' </remarks>
    Private Sub CleanupProcess(Instance As DshInstance, Proc As Process, KillIfAlive As Boolean)
        If Instance Is Nothing Then Return
        Dim target As Process = If(Proc, Instance.Process)

        If target IsNot Nothing Then
            If KillIfAlive Then
                Try
                    If Not target.HasExited Then KillProcessTree(target)
                Catch
                    ' 已退出
                End Try
            End If
            Try
                RemoveHandler target.OutputDataReceived, AddressOf OnOutputData
                RemoveHandler target.ErrorDataReceived, AddressOf OnErrorData
                RemoveHandler target.Exited, AddressOf OnProcessExited
                target.Dispose()
            Catch
                ' 忽略释放异常
            End Try
        End If

        Instance.Process = Nothing
        Instance.ReadyUrl = Nothing
        Instance.ActualPort = 0
        ' 释放就绪信号 —— 否则超时路径留下的 ManualResetEventSlim 会一直占着
        Try
            Dim sig As Threading.ManualResetEventSlim = Instance.ReadySignal
            If sig IsNot Nothing Then
                sig.Dispose()
                Instance.ReadySignal = Nothing
            End If
        Catch
            ' 已释放
        End Try
    End Sub

    ''' <summary>
    ''' 停止某个实例的 dsh 服务。
    ''' </summary>
    ''' <param name="Instance">目标实例。</param>
    ''' <param name="Force">为 True 时若优雅退出超时则强杀。</param>
    ''' <remarks>
    ''' dsh 有 node-pty 等原生子进程，强行 Kill 可能留下孤儿子进程，
    ''' 因此**优先尝试优雅退出**，超时后才强杀整棵进程树。
    ''' </remarks>
    Public Sub StopService(Instance As DshInstance, Optional Force As Boolean = True)
        If Instance Is Nothing Then Return

        SyncLock Instance.SyncRoot
            Dim proc As Process = Instance.Process
            If proc Is Nothing Then
                Instance.State = ModDSH.DshState.Stopped
                Return
            End If

            Instance.State = ModDSH.DshState.Stopping
            Try
                If Not proc.HasExited Then
                    Logger.Info($"DSH[{Instance.DisplayName}]：正在停止服务…")
                    TryStopGracefully(proc)

                    If Not proc.WaitForExit(ModDSH.DshStopTimeoutMs) Then
                        If Force Then
                            Logger.Warn($"DSH[{Instance.DisplayName}]：优雅退出超时，强制结束进程树")
                            KillProcessTree(proc)
                        Else
                            Logger.Warn($"DSH[{Instance.DisplayName}]：优雅退出超时，进程仍在运行")
                        End If
                    End If
                End If
            Catch ex As Exception
                Logger.Error(ex, $"DSH[{Instance.DisplayName}]：停止服务时发生异常")
            Finally
                ' 统一走 CleanupProcess（杀进程树 + 摘事件 + Dispose + 复位状态 + 释放就绪信号）
                CleanupProcess(Instance, proc, KillIfAlive:=True)
                Instance.State = ModDSH.DshState.Stopped
                Logger.Info($"DSH[{Instance.DisplayName}]：服务已停止")
            End Try
        End SyncLock
    End Sub

    ''' <summary>
    ''' 停止**全部**实例的服务。
    ''' </summary>
    ''' <remarks>
    ''' 退出程序时用。单个实例失败不应中断其余实例的回收，
    ''' 所以每个都单独 try 住 —— 否则一个卡住的 taskkill 会让其它 node 进程变成孤儿。
    ''' </remarks>
    Public Sub StopAll()
        For Each inst In ModDSH.DshInstances
            Try
                StopService(inst, Force:=True)
            Catch ex As Exception
                Logger.Error(ex, $"DSH：停止实例失败：{inst.DisplayName}")
            End Try
        Next
    End Sub

    ''' <summary>
    ''' 尝试让 dsh 优雅退出。
    ''' </summary>
    ''' <remarks>
    ''' 优先通过 stdin 发送结束信号。
    ''' 注意：这里刻意**不**用 CloseMainWindow —— dsh 是控制台进程，没有窗口消息循环，
    ''' 给它发 WM_CLOSE 不会有任何效果（POC 阶段确认过其启动参数不含窗口消息泵）。
    ''' 由于当前未重定向 stdin，退化为等待其自然退出；若超时由调用方强杀。
    ''' </remarks>
    Private Sub TryStopGracefully(proc As Process)
        ' 当前实现：不重定向 stdin，因此无法发 EOF。
        ' 保留这个独立方法是为了将来接入 dsh 的优雅关闭协议
        ' （例如通过 HTTP 端点 POST /shutdown，若上游提供）。
        ' 现阶段直接等待，由调用方的超时逻辑兜底。
    End Sub

    ''' <summary>
    ''' 强制结束进程及其整棵子进程树。
    ''' </summary>
    ''' <remarks>
    ''' dsh 会派生 node-pty 等子进程，只杀父进程会留下孤儿。
    ''' 用 taskkill /T 递归结束整棵树。
    ''' </remarks>
    Private Sub KillProcessTree(proc As Process)
        Try
            Dim psi As New ProcessStartInfo("taskkill",
                $"/PID {proc.Id} /T /F") With {
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True
            }
            Using k = Process.Start(psi)
                k.WaitForExit(5000)
            End Using
        Catch ex As Exception
            Logger.Error(ex, "DSH：结束进程树失败，尝试直接 Kill")
            Try
                proc.Kill()
            Catch
                ' 已退出
            End Try
        End Try
    End Sub

#End Region

#Region "查询"

    ''' <summary>重启某个实例的服务。</summary>
    Public Function RestartService(Instance As DshInstance,
                                   Runtime As DshRuntime.DshRuntimeInfo) As String
        StopService(Instance)
        Return StartService(Instance, Runtime)
    End Function

    ''' <summary>
    ''' 用原始 HTTP 请求探测一个 URL 的状态码。
    ''' </summary>
    ''' <returns>HTTP 状态码；请求失败返回 -1。</returns>
    ''' <remarks>
    ''' **不自动跟随重定向** —— dsh 的认证流程本身包含一次 303 重定向，
    ''' 需要看到原始状态码才能区分「认证通过」与「认证失败」。
    ''' 303 = 带 token 通过认证；401 = 被拒绝。
    ''' </remarks>
    Public Function ProbeUrl(url As String) As Integer
        Try
            Using handler As New HttpClientHandler With {.AllowAutoRedirect = False}
                Using client As New HttpClient(handler) With {.Timeout = TimeSpan.FromSeconds(15)}
                    Using resp = client.GetAsync(url).GetAwaiter().GetResult()
                        Return CInt(resp.StatusCode)
                    End Using
                End Using
            End Using
        Catch ex As Exception
            Logger.Error(ex, $"DSH：探测 URL 失败：{url}")
            Return -1
        End Try
    End Function

#End Region

End Module
