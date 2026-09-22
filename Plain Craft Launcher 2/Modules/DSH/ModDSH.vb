Imports System.IO
Imports System.Text
Imports Newtonsoft.Json.Linq

''' <summary>
''' PCL_DSH 的 DSH 集成总入口。
'''
''' 职责边界：
'''   - 定义 DSH 相关的所有路径常量与配置项
'''   - 维护**实例集合**（创建 / 删除 / 查询 / 持久化）
'''   - 作为外壳 UI（Pages/PageDSH/*）与服务实现（DshRuntime / DshService）之间的唯一中介
'''
''' 设计原则：
'''   1. 本模块**不依赖任何 Minecraft 相关代码**，可独立编译
'''   2. 所有路径都落在 PCL 的 PathTemp 下，卸载 = 删目录
'''   3. 运行时（Node / pnpm / dsh 包）全局共享，数据（DSH_HOME）按实例隔离
'''   4. 不修改系统 PATH、不写注册表、不污染用户环境
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 单例 → 多实例的迁移说明
''' ═══════════════════════════════════════════════════════════════════════
''' 早期版本整个模块是**单例**的：状态（<c>DshCurrentState</c> / <c>DshReadyUrl</c> /
''' <c>DshActualPort</c>）直接挂在模块上，进程引用挂在 <see cref="DshService"/> 上。
'''
''' 现在这些全部下移到 <see cref="DshInstance"/> —— 一个实例一份。
''' 为了让还没改完的调用点不至于崩，模块底部保留了一组**兼容属性**，
''' 它们统一代理到「主实例」（列表里的第一个）。新代码请直接用实例。
''' </summary>
Public Module ModDSH

#Region "版本与配置"

    ''' <summary>
    ''' 锁定的 dsh 版本。alpha 阶段刻意锁定，避免上游破坏性变更影响用户。
    ''' 升级方式：手动改这里，并同步更新关于页的版本展示。
    ''' </summary>
    Public Const DshVersion As String = "0.1.6-alpha.2" 'PCL_DSH: 锁定版本

    ''' <summary>npm 包名。</summary>
    Public Const DshPackageName As String = "@deepseek-ai/dsh"

    ''' <summary>
    ''' 使用的 profile 名。
    ''' 必须是 dsh 内置模板名（web / headless / sdk / sdk-minimal / acp），
    ''' 因为只有内置模板才会被自动初始化；自建名字会报 "profile does not exist"。
    ''' 本项目采用「继承官方 web profile + 叠加 patch」策略。
    ''' </summary>
    Public Const DshProfileName As String = "web"

    ''' <summary>所有可选的内置 profile 模板名（新建实例时给用户选）。</summary>
    Public ReadOnly Property DshAvailableProfiles As String()
        Get
            Return {"web", "headless", "sdk", "sdk-minimal", "acp"}
        End Get
    End Property

    ''' <summary>
    ''' dsh Web UI 的默认端口。
    ''' 取官方 Electron 桌面壳使用的端口，便于对照排查。
    ''' </summary>
    Public Const DshDefaultPort As Integer = 19387

    ''' <summary>
    ''' 实例数量上限。
    ''' </summary>
    ''' <remarks>
    ''' 每个实例是一个独立的 node 进程，dsh 常驻内存不小；
    ''' 再加上每个实例第一次启动都要初始化一份 profile。
    ''' 4 个是「够用」与「不至于把用户机器拖垮」之间的取舍。
    ''' </remarks>
    Public Const DshMaxInstances As Integer = 4

    ''' <summary>等待 dsh 打印就绪 URL 的超时时间（毫秒）。首次启动需初始化 profile，给足时间。</summary>
    Public Const DshStartupTimeoutMs As Integer = 180000

    ''' <summary>
    ''' 停止服务时等待进程**优雅退出**的时间（毫秒）。
    '''
    ''' 实测 dsh 不响应 stdin EOF（<see cref="DshService.TryStopGracefully"/> 目前是空实现），
    ''' 所以这段等待**必然超时**，纯粹是白等。
    ''' 原值 8000ms 让关闭流程白白卡 8 秒（用户报障）。
    ''' 现在压到 800ms：给一个"万一将来上游实现了优雅退出"的机会，又不至于让用户等。
    ''' 超时后立刻 taskkill 进程树。
    ''' </summary>
    Public Const DshStopTimeoutMs As Integer = 800

    ''' <summary>
    ''' 退出时留给后台清理的时间上限（毫秒）。
    '''
    ''' 超过这个时间就直接退出，不再等 —— 残留的 node 进程会在下次启动时
    ''' 被端口探测发现并回收（见 <see cref="DshService"/> 的端口选择逻辑）。
    ''' </summary>
    Public Const DshShutdownWaitMs As Integer = 3000

#End Region

#Region "更新通道"

    ''' <summary>
    ''' dsh 的更新通道。决定「安装最新版本」按钮指向哪一类版本。
    ''' </summary>
    ''' <remarks>
    ''' 为什么需要这个开关：dsh 目前整条线都在预发布（0.1.x-alpha / -rc），
    ''' 但用户对「我要不要跟预发布」的偏好差异很大 ——
    ''' 追新的人想第一时间拿到 alpha，求稳的人只想动 rc/正式版。
    ''' 纯靠列表里手动挑徽标太隐晦，所以给一个显式的通道开关。
    ''' </remarks>
    Public Enum DshChannel
        ''' <summary>稳定通道：只跟 <c>dist-tags.latest</c>（npm 保证它不指向预发布）。</summary>
        Stable = 0
        ''' <summary>预览通道：跟版本号最大的那个，含 alpha / beta / rc。</summary>
        Preview = 1
    End Enum

    ''' <summary>当前更新通道。默认稳定（用户第 4 轮敲定：5-a 默认 stable）。</summary>
    Public ReadOnly Property DshCurrentChannel As DshChannel
        Get
            Try
                Return CType(Settings.Get(Of Integer)("DshUpdateChannel"), DshChannel)
            Catch
                Return DshChannel.Stable
            End Try
        End Get
    End Property

    ''' <summary>通道的可读名（给界面用）。</summary>
    Public Function DshChannelName(Channel As DshChannel) As String
        If Channel = DshChannel.Preview Then Return "预览"
        Return "稳定"
    End Function

#End Region

#Region "路径"

    ''' <summary>
    ''' PCL_DSH 私有数据根目录，默认形如 <c>%TEMP%\PCL\DSH\</c>。
    ''' </summary>
    ''' <remarks>
    ''' ⭐ **这个属性是所有 DSH 路径的唯一真相来源。**
    ''' 它下面的每一个子路径（runtime / instances / 日志 / 清单）都由它推导而来，
    ''' 所以「一键迁移」只需要改这一个地方 —— 改完立刻全局生效，
    ''' 不存在"某个角落还指着旧目录"的可能。
    '''
    ''' 挂在 PathTemp 下而不是 LocalAppData，是为了：
    '''   - 与 PCL 既有行为一致（PCL 的缓存都在 PathTemp）
    '''   - 允许用户通过 SystemSystemCache 设置项统一搬迁缓存盘
    '''
    ''' 用户可以把数据迁到任意盘（见 <see cref="DshMigrate"/>）：迁移完成后
    ''' 会在设置 <c>DshDataRoot</c> 里记下新位置，本属性立刻返回新值。
    ''' </remarks>
    Public ReadOnly Property DshRoot As String
        Get
            Try
                Dim custom As String = Settings.Get(Of String)("DshDataRoot")
                If Not String.IsNullOrWhiteSpace(custom) Then
                    Dim p As String = custom.Trim()
                    If Not p.EndsWith("\"c) Then p &= "\"
                    Return p
                End If
            Catch
                ' 设置项还没建立（首次运行）—— 直接用默认值
            End Try
            Return PathTemp & "DSH\"
        End Get
    End Property

    ''' <summary>
    ''' 数据根目录的**出厂默认值**。
    ''' </summary>
    ''' <remarks>
    ''' 单独暴露出来，是为了让「迁移」界面能显示「默认位置是哪里」，
    ''' 也能让用户一键迁回默认位置。
    ''' </remarks>
    Public ReadOnly Property DshDefaultRoot As String
        Get
            Return PathTemp & "DSH\"
        End Get
    End Property

    ''' <summary>当前数据根目录是否已被用户改到非默认位置。</summary>
    Public ReadOnly Property DshIsRootCustomized As Boolean
        Get
            Return Not String.Equals(
                DshRoot.TrimEnd("\"c),
                DshDefaultRoot.TrimEnd("\"c),
                StringComparison.OrdinalIgnoreCase)
        End Get
    End Property

    ''' <summary>私有运行时目录：Node / pnpm / dsh 全部装在这里（**所有实例共享**）。</summary>
    Public ReadOnly Property DshRuntimeDir As String
        Get
            Return DshRoot & "runtime\"
        End Get
    End Property

    ''' <summary>私有 Node 运行时目录。</summary>
    Public ReadOnly Property DshNodeDir As String
        Get
            Return DshRuntimeDir & "node\"
        End Get
    End Property

    ''' <summary>私有 pnpm 目录（存放 pnpm.exe）。</summary>
    Public ReadOnly Property DshPnpmDir As String
        Get
            Return DshRuntimeDir & "pnpm\"
        End Get
    End Property

    ''' <summary>
    ''' dsh 安装目录（pnpm 的 node_modules 落点，**所有实例共享**）。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 这是**旧版遗留的单例目录**，`pnpm add` **不再**装到这里。
    '''
    ''' 为什么改：这个目录是唯一的，装新版本时 `pnpm add` 会在同一目录里
    ''' **就地覆盖**旧版本 —— 于是"插件不适配新版本"时用户**退不回去**，
    ''' 因为旧版本已经被删了。
    '''
    ''' 现在新装的版本进 <see cref="DshVersionsDir"/> 下各自独立的目录
    ''' （<c>runtime\versions\&lt;版本号&gt;\</c>），多版本可以并存、随时切换。
    '''
    ''' 这个旧目录**保留**，因为它可能还装着用户唯一的运行时 ——
    ''' 直接删掉会让老用户升级后打不开程序。它被当作一个普通槽位
    ''' （id = <c>npm</c>）继续参与枚举与切换，用户想清理时在界面上删即可。
    ''' </remarks>
    Public ReadOnly Property DshInstallDir As String
        Get
            Return DshRuntimeDir & "dsh\"
        End Get
    End Property

    ''' <summary>
    ''' 多版本运行时根目录（每个 npm 装的版本在其中占一个 <c>&lt;版本号&gt;\</c>）。
    ''' </summary>
    ''' <remarks>
    ''' ⭐ 这是「多版本并存」的落点：装 v1.2 和 v1.3 会分别进
    ''' <c>versions\1.2.0\</c> 与 <c>versions\1.3.0\</c>，互不覆盖。
    ''' 装完**不会自动切换** —— 当前用的是哪个由
    ''' <see cref="DshRuntimeSlot.DshSlotActive"/> 决定，用户显式点才切。
    ''' </remarks>
    Public ReadOnly Property DshVersionsDir As String
        Get
            Return DshRuntimeDir & "versions\"
        End Get
    End Property

    ''' <summary>导入的运行时槽位根目录（每个导入包在其中占一个 <c>imp_&lt;id&gt;\</c>）。</summary>
    Public ReadOnly Property DshImportedDir As String
        Get
            Return DshRuntimeDir & "imported\"
        End Get
    End Property

    ''' <summary>
    ''' 运行时槽位清单文件（记录导入的槽位与当前激活的槽位）。
    ''' </summary>
    ''' <remarks>
    ''' 刻意只记**导入**的槽位 —— npm 槽位是隐式存在、永远有的，
    ''' 而且它的版本号必须实时读磁盘（用户随时可能重装），
    ''' 写进清单只会多一份会和现实脱节的副本。详见 <see cref="DshRuntimeSlot"/>。
    ''' </remarks>
    Public ReadOnly Property DshSlotsFile As String
        Get
            Return DshRuntimeDir & "runtimes.json"
        End Get
    End Property

    ''' <summary>实例数据根目录。每个实例在其中占一个 <c>inst_&lt;id&gt;\</c> 子目录。</summary>
    Public ReadOnly Property DshInstancesDir As String
        Get
            Return DshRoot & "instances\"
        End Get
    End Property

    ''' <summary>实例清单文件（记录名字 / 端口 / profile）。</summary>
    Public ReadOnly Property DshInstancesFile As String
        Get
            Return DshRoot & "instances.json"
        End Get
    End Property

    ''' <summary>
    ''' **主实例**的 <c>DSH_HOME</c>。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 这是迁移期兼容属性。多实例改造后，每个实例有自己的 home
    ''' （见 <see cref="DshInstance.HomeDir"/>）。新代码请用实例上的属性。
    ''' </remarks>
    Public ReadOnly Property DshHomeDir As String
        Get
            Dim p As DshInstance = DshPrimaryInstance
            If p Is Nothing Then Return DshInstancesDir & "inst_default\"
            Return p.HomeDir
        End Get
    End Property

    ''' <summary>主实例的 patch 注入文件路径（兼容属性）。</summary>
    Public ReadOnly Property DshPatchFile As String
        Get
            Return DshHomeDir & "pcl-dsh.patch.yml"
        End Get
    End Property

    ''' <summary>
    ''' 浏览器配置目录（历史遗留，不再参与任何流程）。
    ''' </summary>
    ''' <remarks>
    ''' 原本这里是 WebView2 的 user data dir。改成系统浏览器承载界面后
    ''' 这个目录不再需要 —— 浏览器自己管自己的 profile。
    ''' </remarks>
    Public ReadOnly Property DshBrowserDataDir As String
        Get
            Return DshRoot & "browser\"
        End Get
    End Property

    ''' <summary>运行时环境信息缓存文件（记录探测到的 node / pnpm / dsh 绝对路径）。</summary>
    Public ReadOnly Property DshEnvCacheFile As String
        Get
            Return DshRoot & "env.json"
        End Get
    End Property

    ''' <summary>DSH 专属日志文件（服务进程 stdout/stderr 的落盘副本）。</summary>
    Public ReadOnly Property DshLogFile As String
        Get
            Return DshRoot & "dsh.log"
        End Get
    End Property

    ''' <summary>
    ''' 界面的承载方式。
    ''' </summary>
    Public Enum DshUiHost
        ''' <summary>交给系统默认浏览器打开。</summary>
        SystemBrowser = 0
        ''' <summary>用用户指定的浏览器可执行文件打开。</summary>
        CustomBrowser = 1
    End Enum

    ''' <summary>确保所有全局目录存在。</summary>
    Public Sub DshEnsureDirectories()
        ' 注意：DshInstallDir（旧单例目录）**不在这里预创建** ——
        ' 新装版本不再往那里装东西，无条件创建会让新用户也看到一个空槽位。
        ' 需要它的地方（旧槽位导入、体检）会自己确保存在。
        ' ⚠️ 变量名不叫 dir —— Dir 是 VB 内置函数（见项目约定里那个坑家族）
        For Each dirPath As String In {
            DshRoot, DshRuntimeDir, DshVersionsDir, DshImportedDir, DshInstancesDir}
            Try
                If Not Directory.Exists(dirPath) Then Directory.CreateDirectory(dirPath)
            Catch ex As Exception
                Logger.Error(ex, $"创建 DSH 目录失败：{dirPath}")
            End Try
        Next
    End Sub

    ''' <summary>确保某个实例的数据目录存在。</summary>
    Public Sub DshEnsureInstanceDirectory(Instance As DshInstance)
        If Instance Is Nothing Then Return
        Try
            If Not Directory.Exists(Instance.HomeDir) Then
                Directory.CreateDirectory(Instance.HomeDir)
            End If
        Catch ex As Exception
            Logger.Error(ex, $"创建实例目录失败：{Instance.HomeDir}")
        End Try
    End Sub

#End Region

#Region "状态枚举"

    ''' <summary>DSH 服务的运行状态。</summary>
    Public Enum DshState
        ''' <summary>未启动，且运行时环境可能不完整。</summary>
        Stopped = 0
        ''' <summary>正在检测运行时环境。</summary>
        CheckingRuntime = 1
        ''' <summary>正在安装缺失的运行时组件（Node / pnpm / dsh）。</summary>
        Installing = 2
        ''' <summary>正在启动 dsh 服务进程。</summary>
        Starting = 3
        ''' <summary>服务已就绪，Web UI 可用。</summary>
        Running = 4
        ''' <summary>正在停止服务。</summary>
        Stopping = 5
        ''' <summary>发生错误。</summary>
        Failed = 6
    End Enum

#End Region

#Region "实例集合"

    Private ReadOnly _Instances As New List(Of DshInstance)
    Private ReadOnly _InstancesSync As New Object()
    Private _InstancesLoaded As Boolean = False

    ''' <summary>
    ''' 当前全部实例（**快照副本**，改它不会影响内部状态）。
    ''' </summary>
    ''' <remarks>
    ''' 返回副本是刻意的：UI 层经常在遍历时删除项，
    ''' 直接暴露内部 List 会踩「集合已修改」的 InvalidOperationException。
    ''' </remarks>
    Public ReadOnly Property DshInstances As List(Of DshInstance)
        Get
            DshEnsureInstancesLoaded()
            SyncLock _InstancesSync
                Return New List(Of DshInstance)(_Instances)
            End SyncLock
        End Get
    End Property

    ''' <summary>实例数量。</summary>
    Public ReadOnly Property DshInstanceCount As Integer
        Get
            DshEnsureInstancesLoaded()
            SyncLock _InstancesSync
                Return _Instances.Count
            End SyncLock
        End Get
    End Property

    ''' <summary>还能不能再建实例。</summary>
    Public ReadOnly Property DshCanCreateInstance As Boolean
        Get
            Return DshInstanceCount < DshMaxInstances
        End Get
    End Property

    ''' <summary>
    ''' 主实例 —— 列表里的第一个。
    ''' </summary>
    ''' <remarks>
    ''' 迁移期的兼容锚点：模块底部的旧属性全部代理到它。
    ''' 列表为空时会自动补一个，保证永远非 Nothing。
    ''' </remarks>
    Public ReadOnly Property DshPrimaryInstance As DshInstance
        Get
            DshEnsureInstancesLoaded()
            SyncLock _InstancesSync
                If _Instances.Count = 0 Then
                    '理论上不会发生（EnsureInstancesLoaded 会补一个），
                    '但并发场景下仍要兜住，绝不返回 Nothing
                    Dim fallback As DshInstance = DshBuildInstance("默认实例", DshDefaultPort)
                    _Instances.Add(fallback)
                    Return fallback
                End If
                Return _Instances(0)
            End SyncLock
        End Get
    End Property

    ''' <summary>按 Id 取实例；找不到返回 Nothing。</summary>
    Public Function DshGetInstance(Id As String) As DshInstance
        If String.IsNullOrWhiteSpace(Id) Then Return Nothing
        DshEnsureInstancesLoaded()
        SyncLock _InstancesSync
            For Each inst In _Instances
                If String.Equals(inst.Id, Id, StringComparison.OrdinalIgnoreCase) Then Return inst
            Next
        End SyncLock
        Return Nothing
    End Function

    ''' <summary>
    ''' 生成一个新的实例 Id。
    ''' </summary>
    ''' <remarks>
    ''' 8 位十六进制，足够避免碰撞，又不会让目录名长得难读。
    ''' </remarks>
    Public Function DshNewInstanceId() As String
        Return Guid.NewGuid().ToString("N").Substring(0, 8)
    End Function

    ''' <summary>
    ''' 判断一个路径是否**严格位于**实例数据根目录之下。
    ''' </summary>
    ''' <remarks>
    ''' 用于给递归删除加一道保险。要点：
    ''' <list type="bullet">
    ''' <item>先 <c>GetFullPath</c> 规范化，把 <c>..</c> 和相对片段解开再比</item>
    ''' <item>要求「前缀 + 分隔符」都匹配 —— 只比前缀的话
    '''       <c>instances_backup\</c> 会被误判成在 <c>instances\</c> 之下</item>
    ''' <item>相等也要拒绝 —— 不允许把实例根目录本身删掉</item>
    ''' </list>
    ''' </remarks>
    Public Function DshIsInsideInstancesDir(CandidatePath As String) As Boolean
        If String.IsNullOrWhiteSpace(CandidatePath) Then Return False
        Try
            Dim root As String = Path.GetFullPath(DshInstancesDir).TrimEnd("\"c)
            Dim full As String = Path.GetFullPath(CandidatePath).TrimEnd("\"c)
            If String.Equals(root, full, StringComparison.OrdinalIgnoreCase) Then Return False
            Return full.StartsWith(root & "\", StringComparison.OrdinalIgnoreCase)
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：路径越界检查失败（按越界处理）：{CandidatePath}")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 判断一个实例 Id 是否安全（可以被拼进路径）。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 这不是「洁癖式校验」，是**防目录穿越**。
    ''' 实例 Id 会拼成 <c>instances\inst_&lt;Id&gt;\</c>，
    ''' 而这个目录在「删除实例（同时删除数据）」时会被整棵递归删除。
    ''' 一旦 Id 里混进 <c>..\</c> 或路径分隔符，删除就会越界到实例目录之外。
    '''
    ''' 合法格式就是 <see cref="DshNewInstanceId"/> 生成的形态：8 位十六进制。
    ''' 用**白名单**而不是「黑名单过滤非法字符」—— 后者永远会漏。
    ''' </remarks>
    Public Function DshIsSafeInstanceId(Id As String) As Boolean
        If String.IsNullOrWhiteSpace(Id) Then Return False
        Dim t As String = Id.Trim()
        If t.Length <> 8 Then Return False
        For Each ch As Char In t
            Dim isHex As Boolean = (ch >= "0"c AndAlso ch <= "9"c) OrElse
                                  (ch >= "a"c AndAlso ch <= "f"c) OrElse
                                  (ch >= "A"c AndAlso ch <= "F"c)
            If Not isHex Then Return False
        Next
        Return True
    End Function

    ''' <summary>
    ''' 判断一个 profile 名是否安全（可以被拼进路径）。
    ''' </summary>
    ''' <remarks>
    ''' 与 <see cref="DshIsSafeInstanceId"/> 同理：profile 名会拼成
    ''' <c>&lt;实例目录&gt;\profiles\&lt;name&gt;\</c>，
    ''' 而 <c>dsh plugin</c> 会在这个目录里跑 pnpm、建 <c>node_modules</c>。
    ''' 只放行字母数字和 <c>- _ .</c>，且不允许以点开头（避免 <c>.</c> / <c>..</c>）。
    ''' </remarks>
    Public Function DshIsSafeProfileName(Name As String) As Boolean
        If String.IsNullOrWhiteSpace(Name) Then Return False
        Dim t As String = Name.Trim()
        If t.Length = 0 OrElse t.Length > 64 Then Return False
        If t.StartsWith("."c) Then Return False
        For Each ch As Char In t
            Dim ok As Boolean = (ch >= "0"c AndAlso ch <= "9"c) OrElse
                                (ch >= "a"c AndAlso ch <= "z"c) OrElse
                                (ch >= "A"c AndAlso ch <= "Z"c) OrElse
                                ch = "-"c OrElse ch = "_"c OrElse ch = "."c
            If Not ok Then Return False
        Next
        Return True
    End Function

    ''' <summary>
    ''' 按现有实例占用情况挑一个建议端口。
    ''' </summary>
    ''' <remarks>
    ''' 从 <see cref="DshDefaultPort"/> 起顺延，跳过已被其它实例声明的端口。
    ''' 这只是「建议值」，真正的可用性在启动时由端口探测兜底。
    ''' </remarks>
    Public Function DshSuggestPort() As Integer
        Dim used As New HashSet(Of Integer)
        SyncLock _InstancesSync
            For Each i In _Instances
                If i.Port > 0 Then used.Add(i.Port)
            Next
        End SyncLock

        For offset As Integer = 0 To DshMaxInstances + 8
            Dim candidate As Integer = DshDefaultPort + offset
            If Not used.Contains(candidate) Then Return candidate
        Next
        Return DshDefaultPort
    End Function

    ''' <summary>构造一个实例对象（不入集合、不落盘）。</summary>
    Private Function DshBuildInstance(InstanceName As String, InstancePort As Integer) As DshInstance
        Return New DshInstance With {
            .Id = DshNewInstanceId(),
            .Name = If(String.IsNullOrWhiteSpace(InstanceName), "新实例", InstanceName.Trim()),
            .Port = InstancePort,
            .Profile = DshProfileName,
            .CreatedAt = DateTime.Now
        }
    End Function

    ''' <summary>
    ''' 新建一个实例并落盘。
    ''' </summary>
    ''' <returns>新实例；已达上限时返回 Nothing。</returns>
    Public Function DshCreateInstance(Optional InstanceName As String = Nothing,
                                      Optional InstancePort As Integer = 0) As DshInstance
        DshEnsureInstancesLoaded()

        Dim created As DshInstance = Nothing
        SyncLock _InstancesSync
            If _Instances.Count >= DshMaxInstances Then
                Logger.Warn($"DSH：实例数已达上限 {DshMaxInstances}，拒绝新建")
                Return Nothing
            End If

            Dim finalName As String = InstanceName
            If String.IsNullOrWhiteSpace(finalName) Then finalName = "实例 " & (_Instances.Count + 1)

            Dim finalPort As Integer = InstancePort
            If finalPort <= 0 Then finalPort = DshSuggestPort()

            created = DshBuildInstance(finalName, finalPort)
            _Instances.Add(created)
            Logger.Info($"DSH：新建实例 {created.Describe()}")
        End SyncLock

        DshEnsureInstanceDirectory(created)
        DshSaveInstances()
        RunInUi(Sub() RaiseEvent DshInstancesChanged())
        Return created
    End Function

    ''' <summary>
    ''' 删除一个实例（会先停掉它的服务）。
    ''' </summary>
    ''' <param name="Id">实例 Id。</param>
    ''' <param name="DeleteData">是否同时删除该实例的数据目录。</param>
    ''' <returns>成功返回 True。</returns>
    ''' <remarks>
    ''' 刻意**不允许删掉最后一个实例** —— 实例列表为空会让整个主页没有可展示的内容，
    ''' 用户会以为程序坏了。想「重置」的话删掉再建一个即可。
    ''' </remarks>
    Public Function DshRemoveInstance(Id As String, Optional DeleteData As Boolean = False) As Boolean
        DshEnsureInstancesLoaded()

        Dim target As DshInstance = DshGetInstance(Id)
        If target Is Nothing Then Return False

        SyncLock _InstancesSync
            If _Instances.Count <= 1 Then
                Logger.Warn("DSH：至少要保留一个实例，拒绝删除")
                Return False
            End If
            _Instances.Remove(target)
        End SyncLock

        '先停服务，再删数据 —— 顺序反了会留下正在写文件的孤儿进程
        Try
            DshService.StopService(target, Force:=True)
        Catch ex As Exception
            Logger.Error(ex, $"DSH：删除实例前停止服务失败：{target.DisplayName}")
        End Try

        If DeleteData Then
            Try
                ' ⚠️ 纵深防御：删除前再确认一次这个路径**确实**落在 instances 目录之下。
                '   第一道防线是加载时的 DshIsSafeInstanceId（把非法 Id 换成新的），
                '   这里是不信任任何上游的第二道 —— 递归删除是全项目最危险的操作，
                '   宁可多一次字符串比较。
                Dim home As String = target.HomeDir
                If Not DshIsInsideInstancesDir(home) Then
                    Logger.Error($"DSH：拒绝删除实例数据目录 —— 路径越界：{home}")
                ElseIf Directory.Exists(home) Then
                    ' ⭐ 用健壮删除：实例数据里的 attachments 是内容寻址存储，
                    '    文件带 ReadOnly 属性，而 Directory.Delete 遇到只读文件
                    '    会直接抛 UnauthorizedAccessException（不是权限问题，
                    '    所以"以管理员运行"也没用）。见 DeleteDirectoryRobust 的注释。
                    DshMigrate.DeleteDirectoryRobust(home)
                    If Directory.Exists(home) Then
                        Logger.Warn($"DSH：实例数据目录删除后仍存在（可能被占用）：{home}")
                    Else
                        Logger.Info($"DSH：已删除实例数据目录 {home}")
                    End If
                End If
            Catch ex As Exception
                '删不掉不算失败 —— 实例已经从列表里移除，残留目录下次可手工清理
                Logger.Error(ex, $"DSH：删除实例数据目录失败：{target.HomeDir}")
            End Try
        End If

        DshSaveInstances()
        RunInUi(Sub() RaiseEvent DshInstancesChanged())
        Logger.Info($"DSH：已删除实例 {target.DisplayName}")
        Return True
    End Function

    ''' <summary>重命名实例。</summary>
    Public Function DshRenameInstance(Id As String, NewName As String) As Boolean
        Dim inst As DshInstance = DshGetInstance(Id)
        If inst Is Nothing Then Return False
        If String.IsNullOrWhiteSpace(NewName) Then Return False

        inst.Name = NewName.Trim()
        DshSaveInstances()
        RunInUi(Sub() RaiseEvent DshInstancesChanged())
        Return True
    End Function

    ''' <summary>修改实例的监听端口（0 = 自动）。</summary>
    Public Function DshSetInstancePort(Id As String, Port As Integer) As Boolean
        Dim inst As DshInstance = DshGetInstance(Id)
        If inst Is Nothing Then Return False
        If Port < 0 OrElse Port > 65535 Then Return False
        '正在跑的时候改端口没有意义，会让人以为生效了
        If inst.IsRunning OrElse inst.HasLiveProcess Then Return False

        inst.Port = Port
        DshSaveInstances()
        RunInUi(Sub() RaiseEvent DshInstancesChanged())
        Return True
    End Function

    ''' <summary>
    ''' 确保实例清单已经加载（幂等）。
    ''' </summary>
    ''' <remarks>
    ''' 首次调用会：
    '''   1. 建目录
    '''   2. 读 instances.json；不存在 / 损坏 → 建一个默认实例
    '''   3. 校验并修补字段（端口越界、profile 为空、Id 重复）
    ''' </remarks>
    Public Sub DshEnsureInstancesLoaded()
        If _InstancesLoaded Then Return

        SyncLock _InstancesSync
            If _InstancesLoaded Then Return
            '先置位，避免下面的 DshEnsureDirectories 递归回来
            _InstancesLoaded = True

            DshEnsureDirectories()
            _Instances.Clear()

            Dim loaded As List(Of DshInstance) = DshReadInstancesFile()
            If loaded.Count = 0 Then
                '首次运行：建一个默认实例，端口用官方默认值
                loaded.Add(DshBuildInstance("默认实例", DshDefaultPort))
                Logger.Info("DSH：未找到实例清单，已创建默认实例")
            End If

            '校验 + 去重
            Dim seenIds As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each inst In loaded
                ' ⚠️ Id 是**外部数据**（instances.json 可能被用户手工编辑、被别的工具改写、
                '   或来自一个损坏/被篡改的文件）。它会被直接拼进路径：
                '       DshInstancesDir & "inst_" & Id & "\"
                '   而这个路径在「删除实例」时会被整棵递归删掉（Directory.Delete(True)）。
                '   如果 Id 里带了 ..\ 之类的东西，就可能删到实例目录之外的用户数据。
                '   所以这里必须强制成「8 位十六进制」的白名单格式 —— 不合规就换一个新的。
                If Not DshIsSafeInstanceId(inst.Id) OrElse seenIds.Contains(inst.Id) Then
                    inst.Id = DshNewInstanceId()
                End If
                seenIds.Add(inst.Id)

                If String.IsNullOrWhiteSpace(inst.Name) Then inst.Name = "实例 " & seenIds.Count
                If String.IsNullOrWhiteSpace(inst.Profile) Then inst.Profile = DshProfileName
                ' Profile 同样会拼进路径（profiles\<name>\），只允许安全字符
                If Not DshIsSafeProfileName(inst.Profile) Then
                    Logger.Warn($"DSH：实例 {inst.Id} 的 profile 名不合法（{inst.Profile}），已重置为默认值")
                    inst.Profile = DshProfileName
                End If
                If inst.Port < 0 OrElse inst.Port > 65535 Then inst.Port = 0
            Next

            '超上限的条目直接截断（用户手工编辑过 json 的情况）
            If loaded.Count > DshMaxInstances Then
                Logger.Warn($"DSH：实例清单有 {loaded.Count} 项，超过上限 {DshMaxInstances}，已截断")
                loaded = loaded.Take(DshMaxInstances).ToList()
            End If

            _Instances.AddRange(loaded)
            Logger.Info($"DSH：已加载 {_Instances.Count} 个实例")
        End SyncLock

        '首次运行时把清单写下去，免得用户看到「每次都是新的默认实例」
        DshSaveInstances()
    End Sub

    ''' <summary>从 instances.json 读取实例列表；任何异常都退化成空列表。</summary>
    Private Function DshReadInstancesFile() As List(Of DshInstance)
        Dim result As New List(Of DshInstance)
        Dim path As String = DshInstancesFile
        If Not DshRuntime.FileExistsSafe(path) Then Return result

        Try
            Dim root As JObject = JObject.Parse(File.ReadAllText(path, Encoding.UTF8))
            Dim arr = TryCast(root("instances"), JArray)
            If arr Is Nothing Then Return result

            For Each item In arr
                Dim obj = TryCast(item, JObject)
                If obj Is Nothing Then Continue For
                Try
                    result.Add(DshInstance.FromJson(obj))
                Catch ex As Exception
                    Logger.Warn($"DSH：跳过损坏的实例条目：{ex.Message}")
                End Try
            Next
        Catch ex As Exception
            '文件坏了不该让程序起不来 —— 退化成「重建默认实例」
            Logger.Error(ex, $"DSH：解析实例清单失败，将重建：{path}")
            result.Clear()
        End Try

        Return result
    End Function

    ''' <summary>把实例列表写回 instances.json（原子写入）。</summary>
    Public Sub DshSaveInstances()
        Try
            DshEnsureDirectories()

            Dim arr As New JArray()
            SyncLock _InstancesSync
                For Each inst In _Instances
                    arr.Add(inst.ToJson())
                Next
            End SyncLock

            Dim root As New JObject()
            root("version") = 1
            root("instances") = arr

            Dim path As String = DshInstancesFile
            Dim tmp As String = DshMigrate.DshAtomicTempPath(path)
            File.WriteAllText(tmp, root.ToString(Newtonsoft.Json.Formatting.Indented), New UTF8Encoding(False))

            If File.Exists(path) Then
                File.Replace(tmp, path, Nothing)
            Else
                File.Move(tmp, path)
            End If
        Catch ex As Exception
            Logger.Error(ex, "DSH：保存实例清单失败")
        End Try
    End Sub

    ''' <summary>实例列表发生变化（新增 / 删除 / 重命名），已在 UI 线程。</summary>
    Public Event DshInstancesChanged()

    ''' <summary>
    ''' 数据目录迁移之后，强制重新加载实例清单。
    ''' </summary>
    ''' <remarks>
    ''' ⭐ 为什么必须有这个函数（用户特别强调「迁移后 PCL 读取路径要同步更新」）：
    '''
    ''' <see cref="DshInstance.HomeDir"/> 是现算的（从 <c>Id</c> 推导），
    ''' 所以它天然跟着 <see cref="DshRoot"/> 走。但**内存里那些实例对象本身**
    ''' 是在旧目录时代构造的，它们的 <c>State</c> / <c>Process</c> / <c>ReadyUrl</c>
    ''' 等运行时字段属于旧位置那套。
    '''
    ''' 重新从新位置的 <c>instances.json</c> 读一遍，可以保证：
    '''   · 内存对象与磁盘清单完全一致
    '''   · 任何曾经被序列化进 json 的路径字段都改成了新位置的值
    '''   · 顺手把「迁移前那份 json 若持有绝对路径」的历史包袱清掉
    '''
    ''' 副作用：正在运行的实例状态会被重置 —— 所以调用前必须确认没有实例在跑
    '''（<see cref="DshMigrate.PreflightCheck"/> 已经拦了这一条）。
    ''' </remarks>
    Public Sub DshReloadInstancesAfterMigration()
        SyncLock _InstancesSync
            _InstancesLoaded = False
            _Instances.Clear()
        End SyncLock

        ' 重新从新位置加载
        DshEnsureInstancesLoaded()

        ' 把规范化后的清单写回**新位置**，确保新目录里的 instances.json 是最新的
        DshSaveInstances()

        Logger.Info($"DSH：迁移后已重载实例清单 —— 共 {DshInstanceCount} 个实例，数据目录 {DshRoot}")

        Try
            RunInUi(Sub() RaiseEvent DshInstancesChanged())
        Catch ex As Exception
            ' 迁移发生在后台线程时 RunInUi 可能拿不到窗口 —— 不致命
            Logger.Warn(ex, "DSH：迁移后广播实例变更失败（界面刷新会滞后，可忽略）")
        End Try
    End Sub

    ''' <summary>某个实例的状态发生变化，已在 UI 线程。</summary>
    Public Event DshInstanceStateChanged(Instance As DshInstance)

    ''' <summary>「当前实例」发生变化（用户在启动页选了另一个实例）。</summary>
    Public Event DshSelectedInstanceChanged(Instance As DshInstance)

#End Region

#Region "当前实例"

    Private _SelectedInstanceId As String = Nothing

    ''' <summary>
    ''' 用户当前聚焦的实例。
    ''' </summary>
    ''' <remarks>
    ''' 启动页左栏选谁，这里就是谁；日志页的「服务状态」卡片也跟着它走。
    ''' 之所以做成全局的而不是各页面各存一份，是因为「我现在在操作哪个实例」
    ''' 在用户心里本来就是**一个**概念 —— 分开存必然出现两页显示不一致。
    '''
    ''' 被选中的实例被删掉时会自动回落到主实例，不会悬空。
    ''' </remarks>
    Public Property DshSelectedInstance As DshInstance
        Get
            DshEnsureInstancesLoaded()
            If Not String.IsNullOrWhiteSpace(_SelectedInstanceId) Then
                Dim found As DshInstance = DshGetInstance(_SelectedInstanceId)
                If found IsNot Nothing Then Return found
            End If
            Return DshPrimaryInstance
        End Get
        Set(value As DshInstance)
            If value Is Nothing Then Return
            If String.Equals(_SelectedInstanceId, value.Id, StringComparison.OrdinalIgnoreCase) Then Return
            _SelectedInstanceId = value.Id
            Logger.Info($"DSH：当前实例切换为「{value.DisplayName}」")
            RunInUi(Sub() RaiseEvent DshSelectedInstanceChanged(value))
        End Set
    End Property

#End Region

#Region "状态（实例级）"

    ''' <summary>
    ''' 变更某个实例的状态，并触发相应事件。
    ''' </summary>
    ''' <remarks>
    ''' 这是**唯一**的状态写入口。所有状态变更都要走这里，好处是：
    '''   - 日志格式统一，排查时序问题时能一眼看出谁在什么时候改了状态
    '''   - 事件一定在 UI 线程发，UI 层不用自己 RunInUi
    '''   - 状态没变就不发事件，避免无谓的界面刷新
    ''' </remarks>
    Friend Sub DshSetState(Instance As DshInstance, NewState As DshState)
        If Instance Is Nothing Then Return
        If Instance.State = NewState Then Return

        Instance.SetStateRaw(NewState)
        Logger.Info($"DSH[{Instance.DisplayName}] 状态变更：{NewState}")

        RunInUi(
            Sub()
                RaiseEvent DshInstanceStateChanged(Instance)
                '兼容：主实例的状态变更同时发旧事件，让还没迁移的页面继续工作
                If Instance Is DshPrimaryInstance Then RaiseEvent DshStateChanged(NewState)
            End Sub)
    End Sub

#End Region

#Region "兼容层（代理到主实例）"

    '以下属性是「单例 → 多实例」迁移期的过渡产物。
    '新代码请直接使用 DshInstance 上的对应成员。

    ''' <summary>主实例的状态（兼容属性）。</summary>
    Public Property DshCurrentState As DshState
        Get
            Dim p As DshInstance = DshPrimaryInstance
            Return If(p Is Nothing, DshState.Stopped, p.State)
        End Get
        Friend Set(value As DshState)
            Dim p As DshInstance = DshPrimaryInstance
            If p IsNot Nothing Then DshSetState(p, value)
        End Set
    End Property

    ''' <summary>主实例是否已就绪（兼容属性）。</summary>
    Public ReadOnly Property DshIsRunning As Boolean
        Get
            Dim p As DshInstance = DshPrimaryInstance
            Return p IsNot Nothing AndAlso p.IsRunning
        End Get
    End Property

    ''' <summary>主实例的就绪 URL（兼容属性）。</summary>
    Public Property DshReadyUrl As String
        Get
            Dim p As DshInstance = DshPrimaryInstance
            Return If(p Is Nothing, Nothing, p.ReadyUrl)
        End Get
        Friend Set(value As String)
            Dim p As DshInstance = DshPrimaryInstance
            If p IsNot Nothing Then p.ReadyUrl = value
        End Set
    End Property

    ''' <summary>主实例的实际端口（兼容属性）。</summary>
    Public Property DshActualPort As Integer
        Get
            Dim p As DshInstance = DshPrimaryInstance
            Return If(p Is Nothing, 0, p.ActualPort)
        End Get
        Friend Set(value As Integer)
            Dim p As DshInstance = DshPrimaryInstance
            If p IsNot Nothing Then p.ActualPort = value
        End Set
    End Property

    ''' <summary>主实例状态变更（兼容事件，已在 UI 线程触发）。</summary>
    Public Event DshStateChanged(NewState As DshState)

#End Region

End Module
