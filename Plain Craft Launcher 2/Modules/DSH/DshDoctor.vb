Imports System.IO
Imports System.Linq

''' <summary>
''' DSH 的环境体检、修复与迁移。
'''
''' 与 <see cref="DshRuntime"/>（只探测）、<see cref="DshInstaller"/>（只装配）的分工：
'''   - <b>DshDoctor</b> 回答「现在哪里不对、能不能一键修好」
'''   - 它把探测结果翻译成「结论 + 一条可执行建议」，供设置页的维护卡片直接展示
'''
''' 设计要点：
'''   - 体检**只读**，不改任何东西；修复是单独一步，由用户点按钮触发
'''   - 每个检查项都有 <see cref="DshCheckResult.Severity"/>，
'''     界面据此决定用绿色勾还是红色叉
'''   - 所有检查项都**不抛异常** —— 单项失败只是那项报红，不该让整份报告失败
''' </summary>
Public Module DshDoctor

#Region "数据模型"

    ''' <summary>体检项的严重程度。</summary>
    Public Enum DshCheckLevel
        ''' <summary>一切正常。</summary>
        Ok = 0
        ''' <summary>能用，但建议处理（例如版本偏旧）。</summary>
        Warning = 1
        ''' <summary>坏了，不修就跑不起来。</summary>
        [Error] = 2
        ''' <summary>纯信息，不参与判定。</summary>
        Info = 3
    End Enum

    ''' <summary>单个体检项。</summary>
    Public Class DshCheckResult
        ''' <summary>体检项标题，例如「Node.js 运行时」。</summary>
        Public Property Title As String = ""
        ''' <summary>严重程度。</summary>
        Public Property Level As DshCheckLevel = DshCheckLevel.Info
        ''' <summary>一句话结论，直接展示给用户。</summary>
        Public Property Message As String = ""
        ''' <summary>补充细节（路径、版本号等），可为空。</summary>
        Public Property Detail As String = ""

        ''' <summary>
        ''' 这个项目是否可以被 <see cref="AutoRepair"/> 自动修复。
        ''' </summary>
        ''' <remarks>
        ''' 标为 True 的项会在「一键修复」里被实际动手处理；
        ''' False 的项只会在报告里列出，需要用户自己决策（例如版本偏旧要不要升级）。
        ''' </remarks>
        Public Property Repairable As Boolean = False

        ''' <summary>给界面用的徽标文本。</summary>
        Public ReadOnly Property BadgeText As String
            Get
                Select Case Level
                    Case DshCheckLevel.Ok : Return "正常"
                    Case DshCheckLevel.Warning : Return "建议处理"
                    Case DshCheckLevel.Error : Return "异常"
                    Case Else : Return "信息"
                End Select
            End Get
        End Property
    End Class

    ''' <summary>一份完整体检报告。</summary>
    Public Class DshHealthReport
        ''' <summary>所有体检项。</summary>
        Public Property Items As New List(Of DshCheckResult)

        ''' <summary>报告生成时间。</summary>
        Public Property GeneratedAt As DateTime = DateTime.Now

        ''' <summary>是否存在阻断性问题。</summary>
        Public ReadOnly Property HasError As Boolean
            Get
                Return Items.Any(Function(i) i.Level = DshCheckLevel.Error)
            End Get
        End Property

        ''' <summary>存在多少条可自动修复的问题。</summary>
        Public ReadOnly Property RepairableCount As Integer
            Get
                Return Items.Where(Function(i) i.Repairable AndAlso i.Level <> DshCheckLevel.Ok).Count()
            End Get
        End Property

        ''' <summary>整体结论（一句话）。</summary>
        Public ReadOnly Property Summary As String
            Get
                If Items.Count = 0 Then Return "尚未体检。"
                Dim errCount = Items.Where(Function(i) i.Level = DshCheckLevel.Error).Count()
                Dim warnCount = Items.Where(Function(i) i.Level = DshCheckLevel.Warning).Count()
                If errCount > 0 Then Return $"发现 {errCount} 个问题需要处理。"
                If warnCount > 0 Then Return $"环境可用，有 {warnCount} 条建议。"
                Return "环境一切正常。"
            End Get
        End Property

        ''' <summary>渲染成可直接塞进 TextBlock 的多行文本。</summary>
        Public Function ToPlainText() As String
            Dim sb As New Text.StringBuilder()
            sb.AppendLine($"PCL DSH 环境体检　{GeneratedAt:yyyy-MM-dd HH:mm:ss}")
            sb.AppendLine(New String("-"c, 42))
            For Each item In Items
                Dim mark As String
                Select Case item.Level
                    Case DshCheckLevel.Ok : mark = "[正常]"
                    Case DshCheckLevel.Warning : mark = "[建议]"
                    Case DshCheckLevel.Error : mark = "[异常]"
                    Case Else : mark = "[信息]"
                End Select
                sb.AppendLine($"{mark} {item.Title}")
                sb.AppendLine($"       {item.Message}")
                If Not String.IsNullOrWhiteSpace(item.Detail) Then
                    For Each line In item.Detail.Split({vbCrLf, vbLf}, StringSplitOptions.RemoveEmptyEntries)
                        sb.AppendLine($"       {line}")
                    Next
                End If
            Next
            sb.AppendLine(New String("-"c, 42))
            sb.AppendLine(Summary)
            If RepairableCount > 0 Then
                sb.AppendLine($"其中 {RepairableCount} 项可以点击「一键修复」自动处理。")
            End If
            Return sb.ToString()
        End Function
    End Class

#End Region

#Region "体检"

    ''' <summary>
    ''' 执行一次完整体检。**只读，不改动任何文件。**
    ''' </summary>
    ''' <remarks>
    ''' 会起若干子进程（`node -v`、`pnpm -v`）来验活，耗时可达数百毫秒，
    ''' **必须放在后台线程调用**。
    ''' </remarks>
    Public Function RunHealthCheck() As DshHealthReport
        Dim report As New DshHealthReport()

        report.Items.Add(CheckNode())
        report.Items.Add(CheckPnpm())
        report.Items.Add(CheckDshPackage())
        report.Items.Add(CheckInstances())
        report.Items.Add(CheckPorts())
        report.Items.Add(CheckLegacyHome())
        report.Items.Add(CheckDiskLayout())

        Logger.Info($"DSH：体检完成 —— {report.Summary}")
        Return report
    End Function

    ''' <summary>检查 Node.js 是否可用且版本达标。</summary>
    Private Function CheckNode() As DshCheckResult
        Dim r As New DshCheckResult With {.Title = "Node.js 运行时"}
        Try
            Dim nodeExe = DshRuntime.DetectNode()
            If String.IsNullOrWhiteSpace(nodeExe) Then
                r.Level = DshCheckLevel.Error
                r.Message = "没有找到可用的 Node.js。"
                r.Detail = "PCL DSH 需要一个 Node.js 运行时来拉起 dsh 服务。" & vbCrLf &
                           "点击「一键修复」会自动下载一份私有副本（不会改动系统环境）。"
                r.Repairable = True
                Return r
            End If

            Dim ver = DshRuntime.ReadNodeVersionVerbose(nodeExe)
            Dim parsed = DshRuntime.ParseNodeVersion(ver)
            If parsed Is Nothing Then
                r.Level = DshCheckLevel.Warning
                r.Message = "找到了 Node.js 但读不出它的版本号。"
                r.Detail = $"路径：{nodeExe}" & vbCrLf & "可能是文件损坏，或执行被安全软件拦截。"
                r.Repairable = True
                Return r
            End If

            Dim verText As String = $"{parsed(0)}.{parsed(1)}.{parsed(2)}"
            If Not DshRuntime.NodeVersionSatisfies(parsed) Then
                r.Level = DshCheckLevel.Error
                r.Message = $"Node.js {verText} 版本过低，dsh 需要 22.19 以上（或 24 以上）。"
                r.Detail = $"路径：{nodeExe}" & vbCrLf &
                           "点击「一键修复」会装配一份达标的私有副本。" & vbCrLf &
                           "（不会卸载或覆盖你系统上原有的 Node.js）"
                r.Repairable = True
                Return r
            End If

            r.Level = DshCheckLevel.Ok
            r.Message = $"Node.js {verText} 可用。"
            r.Detail = nodeExe
            If nodeExe.StartsWith(ModDSH.DshNodeDir, StringComparison.OrdinalIgnoreCase) Then
                r.Detail = nodeExe & vbCrLf & "（PCL DSH 私有副本）"
            Else
                r.Detail = nodeExe & vbCrLf & "（复用系统安装的 Node.js）"
            End If
            Return r
        Catch ex As Exception
            Logger.Error(ex, "DSH：体检 Node 失败")
            r.Level = DshCheckLevel.Warning
            r.Message = $"检测 Node.js 时出错：{ex.Message}"
            Return r
        End Try
    End Function

    ''' <summary>检查 pnpm。它只影响插件管理，缺失不算致命。</summary>
    Private Function CheckPnpm() As DshCheckResult
        Dim r As New DshCheckResult With {.Title = "pnpm 包管理器"}
        Try
            Dim pnpmExe = DshRuntime.DetectPnpm()
            If String.IsNullOrWhiteSpace(pnpmExe) Then
                r.Level = DshCheckLevel.Warning
                r.Message = "没有找到 pnpm，插件安装 / 更新会不可用。"
                r.Detail = "dsh 服务本身的启动不依赖 pnpm，但装插件要用。" & vbCrLf &
                           "点击「一键修复」可以自动装配一份私有副本。"
                r.Repairable = True
                Return r
            End If

            ' 真跑一次，防止「文件在但执行不了」（缺 DLL / 被杀软隔离）
            Dim ok As Boolean = DshRuntime.ProbeExecutable(pnpmExe, "--version")
            If Not ok Then
                r.Level = DshCheckLevel.Warning
                r.Message = "pnpm 文件存在，但执行失败。"
                r.Detail = $"路径：{pnpmExe}" & vbCrLf &
                           "常见原因：文件被安全软件隔离，或复制过程中损坏。" & vbCrLf &
                           "点击「一键修复」会重新装配一份。"
                r.Repairable = True
                Return r
            End If

            r.Level = DshCheckLevel.Ok
            r.Message = "pnpm 可用。"
            r.Detail = pnpmExe
            Return r
        Catch ex As Exception
            Logger.Error(ex, "DSH：体检 pnpm 失败")
            r.Level = DshCheckLevel.Warning
            r.Message = $"检测 pnpm 时出错：{ex.Message}"
            Return r
        End Try
    End Function

    ''' <summary>检查 dsh 本体是否完整。</summary>
    Private Function CheckDshPackage() As DshCheckResult
        Dim r As New DshCheckResult With {.Title = "dsh 本体"}
        Try
            Dim entry = DshRuntime.DetectDsh()
            If String.IsNullOrWhiteSpace(entry) Then
                r.Level = DshCheckLevel.Error
                r.Message = "dsh 尚未安装。"
                r.Detail = "点击「一键修复」会从镜像源安装最新版本。" & vbCrLf &
                           "也可以在「下载 → 镜像版本」里挑选具体版本。"
                r.Repairable = True
                Return r
            End If

            Dim ver = DshRuntime.GetInstalledDshVersion()

            ' 入口在 ≠ 依赖树完整。node_modules 半装是 pnpm 被中断的典型残留。
            '
            ' ⚠️ 不要用「node_modules 顶层目录数」做判据 —— pnpm 用的是**虚拟存储**布局：
            '     node_modules/ 下只会看到 @deepseek-ai 一个实体目录，真正的依赖树
            '     全在 node_modules/.pnpm/ 里，@deepseek-ai/dsh 是指向它的符号链接。
            '     实测一个**完全健康**的安装顶层只有 1 个目录，用 `< 10` 判断会误报成
            '     「依赖树残缺」，进而怂恿用户去重装一个本来好好的 dsh。
            '
            ' 正确判据按「成本从低到高」排：
            '   ① package.json 在不在
            '   ② .pnpm 虚拟存储有没有内容（pnpm 安装的硬证据）
            '   ③ 入口文件本身能不能跑起来（最终裁决）
            Dim nmDir = Path.Combine(ModDSH.DshInstallDir, "node_modules")
            Dim pkgJson = Path.Combine(nmDir, "@deepseek-ai", "dsh", "package.json")

            If Not File.Exists(pkgJson) Then
                r.Level = DshCheckLevel.Error
                r.Message = "dsh 安装不完整：缺少 package.json。"
                r.Detail = $"安装目录：{ModDSH.DshInstallDir}"
                r.Repairable = True
                Return r
            End If

            ' ② pnpm 虚拟存储：.pnpm 里有包才算装全了。
            '    注意 .pnpm 本身以点开头，Directory.GetDirectories 默认**会**返回它，
            '    所以这里要显式按名字找，不要靠数量。
            Dim storeDir = Path.Combine(nmDir, ".pnpm")
            Dim storeCount As Integer = 0
            Try
                If Directory.Exists(storeDir) Then
                    storeCount = Directory.GetDirectories(storeDir).Length
                End If
            Catch
                ' 数不出来不影响结论 —— 下面还有「能不能跑」这一关兜底
            End Try

            If storeCount = 0 Then
                r.Level = DshCheckLevel.Error
                r.Message = "dsh 依赖树疑似残缺（pnpm 存储目录是空的）。"
                r.Detail = $"安装目录：{ModDSH.DshInstallDir}" & vbCrLf &
                           "通常是上一次安装被中断或网络中断导致的。" & vbCrLf &
                           "点击「一键修复」会就地重新安装一遍。"
                r.Repairable = True
                Return r
            End If

            ' ③ 最终裁决：真的把 dsh 拉起来问版本。
            '    文件都在、但被安全软件隔离 / 架构不匹配的情况只有这一关能发现。
            Dim nodeExe = DshRuntime.DetectNode()
            If Not String.IsNullOrWhiteSpace(nodeExe) Then
                If Not DshRuntime.ProbeExecutable(nodeExe, ChrW(34) & entry & ChrW(34) & " --version") Then
                    Logger.Warn($"DSH：体检发现 dsh 入口无法执行 —— node={nodeExe}，entry={entry}")
                    r.Level = DshCheckLevel.Error
                    r.Message = "dsh 装了但跑不起来（入口无法执行）。"
                    r.Detail = $"入口：{entry}" & vbCrLf &
                               $"Node：{nodeExe}" & vbCrLf &
                               "可能是安装被中断、文件损坏，或被安全软件拦截。" & vbCrLf &
                               "点击「一键修复」会重新安装 dsh 本体。"
                    r.Repairable = True
                    Return r
                End If
            End If

            r.Level = DshCheckLevel.Ok
            r.Message = $"dsh {If(ver, "（版本未知）")} 已安装，依赖树完整（{storeCount} 个包）。"
            r.Detail = $"入口：{entry}"
            Return r
        Catch ex As Exception
            Logger.Error(ex, "DSH：体检 dsh 本体失败")
            r.Level = DshCheckLevel.Warning
            r.Message = $"检测 dsh 本体时出错：{ex.Message}"
            Return r
        End Try
    End Function

    ''' <summary>检查每个实例的数据目录是否完整。</summary>
    Private Function CheckInstances() As DshCheckResult
        Dim r As New DshCheckResult With {.Title = "实例数据"}
        Try
            ModDSH.DshEnsureInstancesLoaded()
            Dim list = ModDSH.DshInstances
            If list Is Nothing OrElse list.Count = 0 Then
                r.Level = DshCheckLevel.Warning
                r.Message = "还没有任何实例。"
                r.Detail = "在「启动」页新建一个实例即可开始使用。"
                Return r
            End If

            Dim broken As New List(Of String)
            Dim healthy As Integer = 0
            For Each inst In list
                ' 目录不存在，或存在但 home 里空着 → 视为未就绪
                If Not Directory.Exists(inst.HomeDir) Then
                    broken.Add($"{inst.Name}：数据目录不存在")
                    Continue For
                End If
                If Directory.GetFileSystemEntries(inst.HomeDir).Length = 0 Then
                    broken.Add($"{inst.Name}：数据目录为空（尚未初始化）")
                    Continue For
                End If
                healthy += 1
            Next

            If broken.Count = 0 Then
                r.Level = DshCheckLevel.Ok
                r.Message = $"{healthy} 个实例的数据都完好。"
                Return r
            End If

            If broken.Count >= list.Count Then
                r.Level = DshCheckLevel.Warning
            Else
                r.Level = DshCheckLevel.Warning
            End If
            r.Message = $"{broken.Count} 个实例的数据目录不完整。"
            r.Detail = String.Join(vbCrLf, broken) & vbCrLf &
                       "这不影响其他实例；点「一键修复」会把这些目录补齐。" & vbCrLf &
                       "⚠️ 只会新建缺失的目录，**不会删除**任何已有数据。"
            r.Repairable = True
            Return r
        Catch ex As Exception
            Logger.Error(ex, "DSH：体检实例失败")
            r.Level = DshCheckLevel.Warning
            r.Message = $"检测实例数据时出错：{ex.Message}"
            Return r
        End Try
    End Function

    ''' <summary>检查各实例的端口占用情况。</summary>
    Private Function CheckPorts() As DshCheckResult
        Dim r As New DshCheckResult With {.Title = "端口占用"}
        Try
            ModDSH.DshEnsureInstancesLoaded()
            Dim list = ModDSH.DshInstances
            If list Is Nothing OrElse list.Count = 0 Then
                r.Level = DshCheckLevel.Info
                r.Message = "没有实例，无需检查。"
                Return r
            End If

            Dim conflicts As New List(Of String)
            Dim running As Integer = 0
            For Each inst In list
                ' 已在跑的实例，端口被自己占着是正常的
                If inst.IsRunning OrElse inst.HasLiveProcess Then
                    running += 1
                    Continue For
                End If
                If inst.Port > 0 AndAlso DshService.IsPortListening(inst.Port) Then
                    conflicts.Add($"{inst.Name}：{inst.Port} 被其他程序占用")
                End If
            Next

            If conflicts.Count = 0 Then
                r.Level = DshCheckLevel.Ok
                r.Message = If(running > 0,
                    $"端口没有冲突（{running} 个实例正在运行）。",
                    "端口没有冲突。")
                Return r
            End If

            ' 端口被占不致命 —— 启动时会自动顺延 +1..+32
            r.Level = DshCheckLevel.Warning
            r.Message = $"{conflicts.Count} 个实例的固定端口被占用。"
            r.Detail = String.Join(vbCrLf, conflicts) & vbCrLf &
                       "启动时 PCL DSH 会自动顺延到邻近的空闲端口，仍可正常使用。" & vbCrLf &
                       "如果希望改用别的固定端口，请在实例设置里修改。"
            Return r
        Catch ex As Exception
            Logger.Error(ex, "DSH：体检端口失败")
            r.Level = DshCheckLevel.Warning
            r.Message = $"检测端口时出错：{ex.Message}"
            Return r
        End Try
    End Function

    ''' <summary>
    ''' 检查是否存在旧版单实例时代的 <c>home\</c> 遗留目录（决策 2-b-3）。
    ''' </summary>
    ''' <remarks>
    ''' 多实例改造前，只有一份 <c>$DSH_HOME = DshRoot\home\</c>。
    ''' 改造后每个实例各有自己的目录，那份旧的**没有任何消费者**了，
    ''' 但它可能带着用户早期的会话数据，所以**只提示、不自动删**。
    ''' </remarks>
    Private Function CheckLegacyHome() As DshCheckResult
        Dim r As New DshCheckResult With {.Title = "历史遗留数据"}
        Try
            Dim legacy = Path.Combine(ModDSH.DshRoot, "home")
            If Not Directory.Exists(legacy) Then
                r.Level = DshCheckLevel.Ok
                r.Message = "没有发现旧版遗留目录。"
                Return r
            End If

            Dim sizeText As String = "未知大小"
            Try
                ' ⚠️ 不用 Directory.GetFiles(..., AllDirectories)：
                '    那个 API 遇到任何一层没权限就整体抛异常（体积会显示成"未知"），
                '    而且会跟随符号链接导致虚高。
                '    这里复用全项目统一的逐层遍历实现（CountFiles 明确不跟随链接）。
                Dim fileCount As Integer = DshMigrate.CountFiles(legacy)
                Dim total As Long = DshMigrate.MeasureDirectorySize(legacy)
                sizeText = $"{fileCount} 个文件，共 {FormatBytes(total)}"
            Catch ex As Exception
                Logger.Warn(ex, "DSH：统计遗留目录大小失败")
            End Try

            r.Level = DshCheckLevel.Warning
            r.Message = "发现旧版本留下的数据目录，可以清理。"
            r.Detail = $"{legacy}" & vbCrLf &
                       $"{sizeText}" & vbCrLf &
                       "这是多实例改造之前的单实例数据目录，当前版本已不再读取它。" & vbCrLf &
                       "⚠️ 如果你在里面还有想保留的会话记录，请先自行备份；" & vbCrLf &
                       "确认不需要后，点「一键修复」即可删除（**不可恢复**）。"
            r.Repairable = True
            Return r
        Catch ex As Exception
            Logger.Error(ex, "DSH：体检遗留目录失败")
            r.Level = DshCheckLevel.Warning
            r.Message = $"检测遗留目录时出错：{ex.Message}"
            Return r
        End Try
    End Function

    ''' <summary>报告数据目录的位置与磁盘余量。</summary>
    Private Function CheckDiskLayout() As DshCheckResult
        Dim r As New DshCheckResult With {.Title = "数据存放位置"}
        Try
            r.Level = DshCheckLevel.Info
            r.Message = $"当前数据根目录：{ModDSH.DshRoot}"
            Dim lines As New List(Of String)
            lines.Add($"运行时（Node / pnpm / dsh，所有实例共享）：{ModDSH.DshRuntimeDir}")
            lines.Add($"实例数据（配置 / 会话 / 插件 / 凭据）：{ModDSH.DshInstancesDir}")

            ' 试着报一下所在盘的剩余空间 —— 迁移决策要靠它
            Try
                Dim rootPath = Path.GetPathRoot(Path.GetFullPath(ModDSH.DshRoot))
                If Not String.IsNullOrWhiteSpace(rootPath) Then
                    Dim di As New DriveInfo(rootPath)
                    If di.IsReady Then
                        lines.Add($"所在磁盘 {rootPath} 剩余可用空间：{FormatBytes(di.AvailableFreeSpace)}")
                    End If
                End If
            Catch ex As Exception
                Logger.Warn(ex, "DSH：读取磁盘剩余空间失败")
            End Try

            lines.Add("如果想把数据换到别的盘，用下方的「迁移数据目录」。")
            r.Detail = String.Join(vbCrLf, lines)
            Return r
        Catch ex As Exception
            Logger.Error(ex, "DSH：体检数据目录失败")
            r.Level = DshCheckLevel.Warning
            r.Message = $"检测数据目录时出错：{ex.Message}"
            Return r
        End Try
    End Function

#End Region

#Region "修复"

    ''' <summary>单项修复的结果。</summary>
    Public Class DshRepairOutcome
        ''' <summary>对应体检项的标题。</summary>
        Public Property Title As String = ""
        ''' <summary>是否成功。</summary>
        Public Property Success As Boolean = False
        ''' <summary>结果描述。</summary>
        Public Property Message As String = ""
    End Class

    ''' <summary>
    ''' 按体检报告里的可修复项逐条执行修复。
    ''' </summary>
    ''' <param name="Report">由 <see cref="RunHealthCheck"/> 生成的报告。</param>
    ''' <param name="Progress">可选的进度回调（阶段, 描述, 0-1 进度）。</param>
    ''' <returns>每一条的执行结果。</returns>
    ''' <remarks>
    ''' 阻塞调用，**必须放在后台线程**。
    ''' 逐条独立执行：某一条失败不会中断后面的条目。
    ''' </remarks>
    Public Function AutoRepair(Report As DshHealthReport,
                               Optional Progress As DshInstaller.DshInstallProgressHandler = Nothing) As List(Of DshRepairOutcome)
        Dim outcomes As New List(Of DshRepairOutcome)
        If Report Is Nothing Then Return outcomes

        Dim targets = Report.Items.Where(Function(i) i.Repairable AndAlso i.Level <> DshCheckLevel.Ok).ToList()
        If targets.Count = 0 Then
            Logger.Info("DSH：体检没有发现可自动修复的问题")
            Return outcomes
        End If

        Logger.Info($"DSH：开始一键修复，共 {targets.Count} 项")
        Dim index As Integer = 0
        For Each item In targets
            index += 1
            Dim stageText = $"修复中（{index}/{targets.Count}）：{item.Title}"
            Report_(Progress, DshInstaller.DshInstallStage.Preparing, stageText, (index - 1) / targets.Count)

            Dim outcome As New DshRepairOutcome With {.Title = item.Title}
            Try
                Select Case item.Title
                    Case "Node.js 运行时"
                        RepairNode(Progress, outcome)
                    Case "pnpm 包管理器"
                        RepairPnpm(Progress, outcome)
                    Case "dsh 本体"
                        RepairDsh(Progress, outcome)
                    Case "实例数据"
                        RepairInstances(outcome)
                    Case "历史遗留数据"
                        RepairLegacyHome(outcome)
                    Case Else
                        outcome.Success = False
                        outcome.Message = "这一项没有对应的自动修复动作，请手动处理。"
                End Select
            Catch ex As Exception
                Logger.Error(ex, $"DSH：修复「{item.Title}」失败")
                outcome.Success = False
                outcome.Message = $"修复失败：{ex.Message}"
            End Try

            outcomes.Add(outcome)
        Next

        Report_(Progress, DshInstaller.DshInstallStage.Done, "修复完成", 1)
        Dim okCount = outcomes.Where(Function(o) o.Success).Count()
        Logger.Info($"DSH：一键修复结束 —— 成功 {okCount}/{outcomes.Count}")
        Return outcomes
    End Function

    ''' <summary>修复 Node：装配一份达标的私有副本。</summary>
    ''' <remarks>
    ''' 刻意**不去动系统上的 Node.js** —— 用户机器上可能还有其他项目依赖特定版本。
    ''' 私有副本落地在 <see cref="ModDSH.DshNodeDir"/>，只被 PCL_DSH 的子进程看到。
    ''' </remarks>
    Private Sub RepairNode(Progress As DshInstaller.DshInstallProgressHandler, ByRef Outcome As DshRepairOutcome)
        ' 先把版本不达标的私有副本清掉，否则 InstallPrivateNode 的解压会因为目标存在而失败
        Try
            If Directory.Exists(ModDSH.DshNodeDir) Then
                Dim existing = Path.Combine(ModDSH.DshNodeDir, "node.exe")
                Dim ver = DshRuntime.ReadNodeVersionVerbose(existing)
                If Not DshRuntime.NodeVersionSatisfies(DshRuntime.ParseNodeVersion(ver)) Then
                    Logger.Info($"DSH：清理版本不达标的私有 Node（{If(ver, "未知版本")}）")
                    DshMigrate.DeleteDirectoryRobust(ModDSH.DshNodeDir)
                End If
            End If
        Catch ex As Exception
            Logger.Warn(ex, "DSH：清理旧私有 Node 失败，继续尝试重新装配")
        End Try

        Dim nodeExe = DshInstaller.RepairInstallPrivateNode(Progress)
        If String.IsNullOrWhiteSpace(nodeExe) Then
            Outcome.Success = False
            Outcome.Message = "未能装配 Node.js，请检查网络后重试。"
            Return
        End If
        Outcome.Success = True
        Outcome.Message = $"已装配私有 Node.js → {nodeExe}"
    End Sub

    ''' <summary>修复 pnpm：重新装配私有副本。</summary>
    Private Sub RepairPnpm(Progress As DshInstaller.DshInstallProgressHandler, ByRef Outcome As DshRepairOutcome)
        ' 文件在但执行失败 → 先删干净再装，否则会出现"看起来还在、其实还是坏的"
        Try
            If Directory.Exists(ModDSH.DshPnpmDir) Then
                DshMigrate.DeleteDirectoryRobust(ModDSH.DshPnpmDir)
                Logger.Info("DSH：已清除损坏的私有 pnpm 目录")
            End If
        Catch ex As Exception
            Logger.Warn(ex, "DSH：清理旧私有 pnpm 失败，继续尝试重新装配")
        End Try

        Dim pnpmExe = DshInstaller.RepairInstallPrivatePnpm(Progress)
        If String.IsNullOrWhiteSpace(pnpmExe) Then
            Outcome.Success = False
            Outcome.Message = "未能装配 pnpm，插件管理仍不可用（不影响 dsh 启动）。"
            Return
        End If
        Outcome.Success = True
        Outcome.Message = $"已装配私有 pnpm → {pnpmExe}"
    End Sub

    ''' <summary>修复 dsh 本体：就地重装。</summary>
    Private Sub RepairDsh(Progress As DshInstaller.DshInstallProgressHandler, ByRef Outcome As DshRepairOutcome)
        ' 依赖树残缺时 pnpm 可能因为半装状态而拒绝干活，先把 node_modules 清掉
        Dim nmDir = Path.Combine(ModDSH.DshInstallDir, "node_modules")
        Try
            If Directory.Exists(nmDir) Then
                DshMigrate.DeleteDirectoryRobust(nmDir)
                Logger.Info("DSH：已清除残缺的 node_modules，将重新安装")
            End If
        Catch ex As Exception
            Logger.Warn(ex, "DSH：清理 node_modules 失败，继续尝试重装（pnpm 可能会自己修）")
        End Try

        ' 装「当前通道」指向的版本；通道解析要看网络，失败就退回锁定版本
        Dim target As String = ModDSH.DshVersion
        Try
            Dim versions = DshRegistry.FetchVersions()
            If versions IsNot Nothing AndAlso versions.Count > 0 Then
                Dim picked = DshRegistry.ResolveVersionForChannel(versions, ModDSH.DshCurrentChannel)
                If picked IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(picked.Version) Then
                    target = picked.Version
                End If
            End If
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：修复时获取版本列表失败，回退到锁定版本 {ModDSH.DshVersion}")
        End Try

        Dim mirror As String = Nothing
        Try
            mirror = DshRegistry.DshCurrentMirrorUrl()
        Catch ex As Exception
            Logger.Warn(ex, "DSH：读取镜像源设置失败，使用默认源")
        End Try

        DshInstaller.InstallDshVersion(target, mirror, Progress)

        Dim entry = DshRuntime.DetectDsh()
        If String.IsNullOrWhiteSpace(entry) Then
            Outcome.Success = False
            Outcome.Message = "重装后仍未找到 dsh 入口，请查看日志。"
            Return
        End If
        Outcome.Success = True
        Outcome.Message = $"已重装 dsh {target} → {entry}"
    End Sub

    ''' <summary>补齐缺失的实例数据目录。**绝不删除任何东西。**</summary>
    Private Sub RepairInstances(ByRef Outcome As DshRepairOutcome)
        ModDSH.DshEnsureInstancesLoaded()
        Dim list = ModDSH.DshInstances
        If list Is Nothing OrElse list.Count = 0 Then
            Outcome.Success = False
            Outcome.Message = "没有实例可修复。"
            Return
        End If

        Dim fixedCount As Integer = 0
        For Each inst In list
            Try
                If Not Directory.Exists(inst.HomeDir) Then
                    Directory.CreateDirectory(inst.HomeDir)
                    fixedCount += 1
                    Logger.Info($"DSH：已补齐实例目录 → {inst.HomeDir}")
                End If
                ' 顺带把 .credentials.yaml 骨架补上（多实例改造后新增实例必须有一份）
                DshCredentials.EnsureCredentialsFile(inst)
            Catch ex As Exception
                Logger.Error(ex, $"DSH：补齐实例数据失败：{inst.Name}")
            End Try
        Next

        Outcome.Success = True
        If fixedCount > 0 Then
            Outcome.Message = $"已为 {fixedCount} 个实例补齐数据目录与凭据文件。"
        Else
            Outcome.Message = "实例目录已存在，已刷新凭据文件。"
        End If
    End Sub

    ''' <summary>删除旧版遗留的 <c>home\</c> 目录。</summary>
    ''' <remarks>
    ''' ⚠️ 这是**不可恢复**的删除操作。只有在用户看过体检报告里那段
    ''' 「请先自行备份」的提示并点了「一键修复」之后，才会走到这里。
    ''' </remarks>
    Private Sub RepairLegacyHome(ByRef Outcome As DshRepairOutcome)
        Dim legacy = Path.Combine(ModDSH.DshRoot, "home")
        If Not Directory.Exists(legacy) Then
            Outcome.Success = True
            Outcome.Message = "遗留目录已不存在。"
            Return
        End If
        Try
            DshMigrate.DeleteDirectoryRobust(legacy)
            Logger.Info($"DSH：已删除旧版遗留目录 → {legacy}")
            Outcome.Success = True
            Outcome.Message = $"已删除旧版遗留目录：{legacy}"
        Catch ex As Exception
            Logger.Error(ex, "DSH：删除遗留目录失败")
            Outcome.Success = False
            Outcome.Message = $"删除失败（可能有文件被占用）：{ex.Message}"
        End Try
    End Sub

    ''' <summary>把进度回调包一层，避免进度回调自己抛异常把修复流程带崩。</summary>
    Private Sub Report_(Progress As DshInstaller.DshInstallProgressHandler,
                        Stage As DshInstaller.DshInstallStage,
                        Message As String,
                        Value As Double)
        If Progress Is Nothing Then Return
        Try
            Progress(Stage, Message, Value)
        Catch ex As Exception
            Logger.Warn(ex, "DSH：修复进度回调抛异常，已忽略")
        End Try
    End Sub

#End Region

#Region "小工具"

    ''' <summary>把字节数格式化成人类可读的字符串。</summary>
    Public Function FormatBytes(Bytes As Long) As String
        If Bytes < 1024L Then Return $"{Bytes} B"
        If Bytes < 1024L * 1024L Then Return $"{Bytes / 1024.0:0.0} KB"
        If Bytes < 1024L * 1024L * 1024L Then Return $"{Bytes / 1024.0 / 1024.0:0.0} MB"
        Return $"{Bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB"
    End Function

#End Region

End Module
