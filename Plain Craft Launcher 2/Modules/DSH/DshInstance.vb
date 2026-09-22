Imports System.Diagnostics
Imports System.IO
Imports System.Text

''' <summary>
''' 一个 DSH 实例。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 什么算「一个实例」
''' ═══════════════════════════════════════════════════════════════════════
''' 一个实例 = 一个**独立的 dsh 数据空间** + 一个**独立的监听端口** + 一个 dsh 进程。
'''
''' 具体来说，每个实例拥有自己的 <c>DSH_HOME</c>，因此：
'''   - 自己的 profile 配置（<c>profiles/</c>）
'''   - 自己的插件集（<c>storages/</c>）
'''   - 自己的会话历史
'''   - 自己的凭据文件（<c>.credentials.yaml</c>）
'''
''' 这样「实例」才有意义 —— 否则多开只是同一个数据空间开几个浏览器标签页。
''' 典型用法：一个实例跑工作项目（装了一批插件），另一个跑个人项目（插件干净）。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 什么**不**是每实例一份
''' ═══════════════════════════════════════════════════════════════════════
''' Node 运行时、pnpm、dsh 包本体（<c>node_modules</c>）都是**全局共享**的。
''' 理由：dsh 的依赖树含 117MB 的 LibreOffice 组件，
''' 每实例复制一份既浪费磁盘又让「新建实例」变成几分钟的等待。
''' 共享的是「程序」，隔离的是「数据」—— 这也是绝大多数工具的做法。
''' </summary>
Public Class DshInstance

#Region "持久化字段"

    ''' <summary>
    ''' 稳定标识。用于目录名，创建后不再改变。
    ''' </summary>
    ''' <remarks>
    ''' 用短 GUID 而不是序号：序号会在删除实例后产生「空洞」，
    ''' 或者被复用导致新实例读到旧实例的残留数据。
    ''' </remarks>
    Public Property Id As String = ""

    ''' <summary>用户可见的实例名。可重复、可随时改。</summary>
    Public Property Name As String = ""

    ''' <summary>
    ''' 监听端口。0 表示启动时自动挑一个空闲端口。
    ''' </summary>
    Public Property Port As Integer = 0

    ''' <summary>dsh profile 名。必须是内置模板名（web / headless / sdk / sdk-minimal / acp）。</summary>
    Public Property Profile As String = ModDSH.DshProfileName

    ''' <summary>创建时间（用于列表排序，也是排查问题时的线索）。</summary>
    Public Property CreatedAt As DateTime = DateTime.Now

#End Region

#Region "运行时状态（不持久化）"

    Private _State As ModDSH.DshState = ModDSH.DshState.Stopped

    ''' <summary>
    ''' 当前状态。
    ''' </summary>
    ''' <remarks>
    ''' 这是**唯一**的状态真相来源 —— <see cref="ModDSH.DshCurrentState"/> 只是
    ''' 代理到「主实例」的兼容属性。所有变更都经 <see cref="ModDSH.DshSetState"/>，
    ''' 由它负责记日志与在 UI 线程发事件。
    ''' </remarks>
    Public Property State As ModDSH.DshState
        Get
            Return _State
        End Get
        Friend Set(value As ModDSH.DshState)
            ModDSH.DshSetState(Me, value)
        End Set
    End Property

    ''' <summary>
    ''' 内部状态字段的直接读写口，仅供 <see cref="ModDSH.DshSetState"/> 使用。
    ''' </summary>
    ''' <remarks>
    ''' 需要这个「后门」是因为 State 的 setter 会把写入转发给 DshSetState，
    ''' 而 DshSetState 又要写回 _State —— 不绕开就会无限递归。
    ''' </remarks>
    Friend Sub SetStateRaw(value As ModDSH.DshState)
        _State = value
    End Sub

    ''' <summary>当前运行的 dsh 进程。未启动时为 Nothing。</summary>
    Public Property Process As Process = Nothing

    ''' <summary>带认证 token 的就绪 URL。未就绪时为 Nothing。</summary>
    Public Property ReadyUrl As String = Nothing

    ''' <summary>实际监听的端口。启动后才有意义。</summary>
    Public Property ActualPort As Integer = 0

    ''' <summary>最近一次失败的原因（用于列表里直接显示，不用翻日志）。</summary>
    Public Property LastError As String = Nothing

    ''' <summary>保护进程引用与启动/停止的互斥（每实例一把）。</summary>
    Friend ReadOnly SyncRoot As New Object()

    ''' <summary>等待就绪 URL 的信号量（每实例一个）。</summary>
    Friend Property ReadySignal As ManualResetEventSlim = Nothing

    Private ReadOnly _OutputLines As New List(Of String)
    Private Const MaxOutputLines As Integer = 300

#End Region

#Region "派生属性"

    ''' <summary>实例的数据目录（即该实例的 <c>DSH_HOME</c>）。</summary>
    Public ReadOnly Property HomeDir As String
        Get
            Return ModDSH.DshInstancesDir & "inst_" & Id & "\"
        End Get
    End Property

    ''' <summary>该实例的凭据文件路径。</summary>
    Public ReadOnly Property CredentialsFile As String
        Get
            Return HomeDir & ".credentials.yaml"
        End Get
    End Property

    ''' <summary>
    ''' 该实例的 home 级 patch 注入文件路径。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 文件名必须是 <c>cordis.patch.yml</c> —— 这是 dsh 官方约定的
    ''' home 层 patch 文件名（见 app-boot 的 <c>readProfilePatches</c>）。
    ''' 早期版本用 <c>pcl-dsh.patch.yml</c>，那个名字**根本不会被读取**。
    ''' </remarks>
    Public ReadOnly Property HomePatchFile As String
        Get
            Return HomeDir & "cordis.patch.yml"
        End Get
    End Property

    ''' <summary>
    ''' 历史遗留的 patch 文件名（<c>pcl-dsh.patch.yml</c>）。
    ''' </summary>
    ''' <remarks>
    ''' 保留只为能识别并清理旧版本留下的无用文件，**不要再用它写配置**。
    ''' </remarks>
    <Obsolete("这个名字不会被 dsh 读取，请改用 HomePatchFile。")>
    Public ReadOnly Property PatchFile As String
        Get
            Return HomeDir & "pcl-dsh.patch.yml"
        End Get
    End Property

    ''' <summary>服务是否已就绪、可直接访问 Web UI。</summary>
    Public ReadOnly Property IsRunning As Boolean
        Get
            Return _State = ModDSH.DshState.Running
        End Get
    End Property

    ''' <summary>是否正在启动或停止（此时按钮应禁用）。</summary>
    Public ReadOnly Property IsBusy As Boolean
        Get
            Return _State = ModDSH.DshState.Starting OrElse
                   _State = ModDSH.DshState.Stopping OrElse
                   _State = ModDSH.DshState.CheckingRuntime OrElse
                   _State = ModDSH.DshState.Installing
        End Get
    End Property

    ''' <summary>是否有活着的进程（不一定已就绪）。</summary>
    Public ReadOnly Property HasLiveProcess As Boolean
        Get
            Try
                Return Process IsNot Nothing AndAlso Not Process.HasExited
            Catch
                Return False
            End Try
        End Get
    End Property

    ''' <summary>列表里显示的标题。名字为空时回退到 Id。</summary>
    Public ReadOnly Property DisplayName As String
        Get
            If Not String.IsNullOrWhiteSpace(Name) Then Return Name.Trim()
            If Not String.IsNullOrWhiteSpace(Id) Then Return "实例 " & Id
            Return "未命名实例"
        End Get
    End Property

    ''' <summary>列表里显示的副标题（端口 + 状态）。</summary>
    Public ReadOnly Property DisplayDetail As String
        Get
            Dim portText As String =
                If(ActualPort > 0, $"端口 {ActualPort}",
                   If(Port > 0, $"端口 {Port}（未启动）", "端口 自动"))

            Dim stateText As String
            Select Case _State
                Case ModDSH.DshState.Running
                    stateText = "运行中"
                Case ModDSH.DshState.Starting
                    stateText = "启动中"
                Case ModDSH.DshState.Stopping
                    stateText = "停止中"
                Case ModDSH.DshState.CheckingRuntime
                    stateText = "检测环境"
                Case ModDSH.DshState.Installing
                    stateText = "装配运行时"
                Case ModDSH.DshState.Failed
                    stateText = "启动失败"
                Case Else
                    stateText = "未运行"
            End Select

            Return $"{portText} · {stateText}"
        End Get
    End Property

    ''' <summary>服务地址（不含 token）。未就绪时返回 Nothing。</summary>
    Public ReadOnly Property DisplayUrl As String
        Get
            If ActualPort <= 0 Then Return Nothing
            Return $"http://127.0.0.1:{ActualPort}/"
        End Get
    End Property

#End Region

#Region "输出缓冲"

    ''' <summary>追加一行服务输出（同时写 PCL 日志）。</summary>
    Friend Sub AppendOutput(line As String)
        SyncLock _OutputLines
            _OutputLines.Add(line)
            If _OutputLines.Count > MaxOutputLines Then
                _OutputLines.RemoveRange(0, _OutputLines.Count - MaxOutputLines)
            End If
        End SyncLock
        Logger.Info($"DSH[{DisplayName}] {line}")
    End Sub

    ''' <summary>取最近的输出。</summary>
    Public Function GetRecentOutput(Optional MaxLines As Integer = 40) As String
        SyncLock _OutputLines
            If _OutputLines.Count = 0 Then Return "（无输出）"
            Dim take As Integer = Math.Min(MaxLines, _OutputLines.Count)
            Return String.Join(Environment.NewLine, _OutputLines.Skip(_OutputLines.Count - take))
        End SyncLock
    End Function

    ''' <summary>清空输出缓冲与就绪信息（启动前调用）。</summary>
    Friend Sub ResetOutput()
        SyncLock _OutputLines
            _OutputLines.Clear()
        End SyncLock
        ReadyUrl = Nothing
        ActualPort = 0
        LastError = Nothing
    End Sub

#End Region

#Region "描述"

    ''' <summary>用于日志的一行摘要。</summary>
    Public Function Describe() As String
        Return $"{DisplayName}(id={Id}, profile={Profile}, {DisplayDetail})"
    End Function

    ''' <summary>用于序列化到 instances.json 的对象。</summary>
    ''' <remarks>
    ''' 刻意不用 <c>New JObject From {{...}}</c> 的集合初始化写法 ——
    ''' VB 会把 <c>{"id", Id}</c> 推成 <c>Object()</c>，
    ''' 再往 <c>Add(String, JToken)</c> 上绑定时要做一次窄化转换，
    ''' 在 Option Strict 下是编译错误。逐项赋值最省事也最清楚。
    ''' </remarks>
    Friend Function ToJson() As Newtonsoft.Json.Linq.JObject
        Dim o As New Newtonsoft.Json.Linq.JObject()
        o("id") = Id
        o("name") = Name
        o("port") = Port
        o("profile") = Profile
        o("createdAt") = CreatedAt.ToString("o")
        Return o
    End Function

    ''' <summary>从 instances.json 的一个条目还原。</summary>
    Friend Shared Function FromJson(o As Newtonsoft.Json.Linq.JObject) As DshInstance
        Dim inst As New DshInstance With {
            .Id = If(o("id")?.ToString(), ""),
            .Name = If(o("name")?.ToString(), ""),
            .Port = If(o("port") Is Nothing, 0, CInt(o("port"))),
            .Profile = If(o("profile")?.ToString(), ModDSH.DshProfileName)
        }
        Dim created As String = o("createdAt")?.ToString()
        Dim dt As DateTime
        If Not String.IsNullOrWhiteSpace(created) AndAlso
           DateTime.TryParse(created, Globalization.CultureInfo.InvariantCulture,
                             Globalization.DateTimeStyles.RoundtripKind, dt) Then
            inst.CreatedAt = dt
        End If

        '兜底：没有 id 的条目（手工编辑过 json）也要能用
        If String.IsNullOrWhiteSpace(inst.Id) Then inst.Id = ModDSH.DshNewInstanceId()
        If String.IsNullOrWhiteSpace(inst.Profile) Then inst.Profile = ModDSH.DshProfileName

        Return inst
    End Function

#End Region

End Class
