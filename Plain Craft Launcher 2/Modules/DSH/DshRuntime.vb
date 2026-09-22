Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Text.RegularExpressions
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq

''' <summary>
''' dsh 运行时环境的探测与装配。
'''
''' 这是 POC-1（poc/01-bootstrap/check-env.js）的 VB.NET 移植版。
'''
''' 策略（对应决策 2：优先复用已有环境，否则回落私有运行时）：
'''   1. 先找用户已有的合格 Node / pnpm，能用就用，不重复下载
'''   2. 任一项不合格，则装配到 <see cref="ModDSH.DshRuntimeDir"/> 私有目录
'''   3. 全程不写系统 PATH、不改环境变量 —— 私有路径通过子进程环境变量注入
'''
''' POC 阶段验证过的关键结论（务必保留）：
'''   - 不能依赖 `where.exe` 查找命令（受限 PATH / 便携分发时会失败）
'''   - pnpm v12+ 是原生独立可执行文件，用 @pnpm/exe 平台包装配，
'''     而非官方 install.ps1（Windows Defender 会拦截）
'''   - dsh 依赖树含 117MB 的 LibreOffice 组件，全量安装很慢，必须给足超时
''' </summary>
Public Module DshRuntime

#Region "常量"

    ''' <summary>
    ''' pnpm 版本。v12+ 为原生独立二进制，装配后不再依赖 Node。
    ''' </summary>
    Public Const PnpmVersion As String = "12.5.1"

    ''' <summary>
    ''' 安装 Node 时使用的版本。
    ''' 必须满足 dsh 的 engines 约束 `^22.19.0 || >=24.0.0`。
    ''' </summary>
    Public Const NodeVersion As String = "22.22.2"

    ''' <summary>dsh 要求的最低 Node 版本（对应 ^22.19.0 的下界）。</summary>
    Private Const NodeMinMajor As Integer = 22
    Private Const NodeMinMinor As Integer = 19
    ''' <summary>另一条被接受的 Node 大版本线（>= 该值即可，对应 >=24.0.0）。</summary>
    Private Const NodeAltMajor As Integer = 24

    ''' <summary>npm 注册表地址，用于下载 @pnpm/exe 平台包。</summary>
    Private Const NpmRegistry As String = "https://registry.npmjs.org"

#End Region

#Region "数据模型"

    ''' <summary>
    ''' 探测结果：一套可用于启动 dsh 的运行时环境。
    ''' </summary>
    Public Class DshRuntimeInfo
        ''' <summary>Node 可执行文件绝对路径。</summary>
        Public Property NodeExe As String = Nothing
        ''' <summary>pnpm 可执行文件绝对路径。可能为 Nothing（dsh 基础运行不需要）。</summary>
        Public Property PnpmExe As String = Nothing
        ''' <summary>dsh 入口脚本绝对路径（lib/bin.js）。</summary>
        Public Property DshEntry As String = Nothing
        ''' <summary>Node 版本号，形如 22.22.2。</summary>
        Public Property NodeVersion As String = Nothing
        ''' <summary>dsh 版本号。</summary>
        Public Property DshVersion As String = Nothing

        ''' <summary>
        ''' 环境是否完整到可以启动 dsh。
        ''' 注意：pnpm 不参与判定 —— dsh 的基础运行不需要它，只有插件管理才需要。
        ''' </summary>
        Public ReadOnly Property IsUsable As Boolean
            Get
                Return FileExistsSafe(NodeExe) AndAlso FileExistsSafe(DshEntry)
            End Get
        End Property

        ''' <summary>生成用于日志的可读摘要。</summary>
        Public Function Describe() As String
            Return $"node={If(NodeVersion, "?")} | dsh={If(DshVersion, "未安装")} | pnpm={If(PnpmExe IsNot Nothing, "已就绪", "缺失")}"
        End Function
    End Class

#End Region

#Region "通用小工具"

    ''' <summary>
    ''' 安全判空 + 判文件存在。路径为 Nothing/空 时返回 False，不抛异常。
    ''' </summary>
    ''' <remarks>
    ''' 抽成独立函数是为了避免到处写 `Not String.IsNullOrEmpty(x) AndAlso File.Exists(x)`
    ''' 这种容易漏条件的判断 —— POC 阶段因为这类漏判踩过坑。
    ''' </remarks>
    Public Function FileExistsSafe(path As String) As Boolean
        If String.IsNullOrWhiteSpace(path) Then Return False
        Try
            Return File.Exists(path)
        Catch
            Return False
        End Try
    End Function

    ''' <summary>安全判目录存在。</summary>
    Public Function DirExistsSafe(path As String) As Boolean
        If String.IsNullOrWhiteSpace(path) Then Return False
        Try
            Return Directory.Exists(path)
        Catch
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 子进程环境的初始化与修改（PCL_DSH 唯一允许的环境变量入口）。
    '''
    ''' ⚠️⚠️ 必须用 <see cref="ProcessStartInfo.Environment"/>，
    ''' **绝对不要碰** <see cref="ProcessStartInfo.EnvironmentVariables"/>。
    '''
    ''' 原因是 EnvironmentVariables 有个 .NET Framework 的经典缺陷：
    ''' 它是**大小写不敏感**的 <c>Hashtable</c>，且内容是**惰性**从当前进程环境物化的。
    ''' Windows 上进程环境里常常同时存在 <c>Path</c> 与 <c>PATH</c>；
    ''' 物化时两者会撞键 → <c>ArgumentException</c>「已添加项。字典中的关键字:"Path"所添加的关键字:"path"」。
    ''' 更糟的是：**光是遍历 <c>EnvironmentVariables.Keys</c> 就会触发物化并抛异常**，
    ''' 所以「先枚举再删」这种绕法根本走不通（实测在第一个 <c>For Each</c> 就炸）。
    '''
    ''' <c>Environment</c>（<c>IDictionary(Of String, String)</c>）更健壮：
    ''' 它大小写**敏感**，键名唯一化后不会撞键。
    ''' 但它同样是惰性初始化的，且会先物化一次 <c>EnvironmentVariables</c>，
    ''' 因此**首次使用前必须先 <see cref="PrimeEnvironment"/> 预热**，否则会继承损坏状态。
    ''' </summary>
    ''' <param name="Psi">目标进程启动信息。</param>
    ''' <param name="Name">变量名（大小写不敏感，内部会归一化）。</param>
    ''' <param name="Value">变量值。传 Nothing 表示删除该变量。</param>
    Public Sub SetEnvSafe(Psi As ProcessStartInfo,
                          Name As String,
                          Value As String)
        '必须先预热，否则在受污染的环境下会因内部字典为 Nothing 而失败
        PrimeEnvironment(Psi)
        Dim key As String = EnvKey(Name)
        If Value Is Nothing Then
            Psi.Environment.Remove(key)
        Else
            Psi.Environment(key) = Value
        End If
    End Sub

    ''' <summary>
    ''' 读取子进程环境变量。
    ''' 与 <see cref="SetEnvSafe"/> 配套，只用 <c>Environment</c>。
    ''' </summary>
    Public Function GetEnvSafe(Psi As ProcessStartInfo, Name As String) As String
        PrimeEnvironment(Psi)
        Dim value As String = Nothing
        'VB 的 TryGetValue 用普通 ByRef 参数，没有 C# 的 out 关键字
        If Psi.Environment.TryGetValue(EnvKey(Name), value) Then Return value
        Return Nothing
    End Function

    ''' <summary>
    ''' 记录哪些 <see cref="ProcessStartInfo"/> 已经灌过环境（幂等用）。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 用 <see cref="ConditionalWeakTable(Of TKey, TValue)"/> 而不是
    ''' <c>HashSet(Of ProcessStartInfo)</c>：
    ''' 后者持有**强引用**，而这里的 key 是每次启动/装插件时 new 出来的局部
    ''' <c>ProcessStartInfo</c> —— 用完就没人引用了，但 HashSet 会把它一直钉住，
    ''' 永久阻止 GC（虽然单个对象只有几百字节、操作也低频，但这是纯粹的浪费，
    ''' 而且没有上限）。
    ''' <c>ConditionalWeakTable</c> 是弱引用表：key 一旦没人引用就自动移除条目，
    ''' 语义完全一致而零泄漏。
    ''' </remarks>
    Private ReadOnly _SeededInstances As New Runtime.CompilerServices.ConditionalWeakTable(Of ProcessStartInfo, Object)

    ''' <summary>
    ''' 让 <see cref="ProcessStartInfo.Environment"/> 完成一次安全初始化。
    '''
    ''' ⚠️ 为什么需要这个「先踩一次雷」的怪招：
    ''' <c>Environment</c> 是 <c>EnvironmentVariables</c> 的**包装器**，且是惰性初始化的 ——
    ''' 首次访问时它会去物化 <c>EnvironmentVariables</c>。若当前进程环境里
    ''' <c>Path</c> / <c>path</c> / <c>PATH</c> 并存（从 bash 或 git-bash 继承时经常如此），
    ''' 那次物化就会抛 <c>ArgumentException</c>，并且会把 <c>Environment</c> 留在一个
    ''' **半初始化的损坏状态**（内部字典为 Nothing）——
    ''' 此后**任何**对 <c>Environment</c> 的读写都会以
    ''' 「无法对 Null 数组进行索引」或「不能对 Null 值表达式调用方法」继续失败。
    '''
    ''' 实测：先故意触发并吞掉这一次异常，内部状态就会被重置为可用，
    ''' 随后 <c>Environment</c> 能正常物化出可安全继承的那部分环境（实测 12 项），
    ''' 之后的读写全部正常。
    '''
    ''' 这是 .NET Framework 的历史包袱，无法从其公开 API 层面优雅规避，
    ''' 因此这里显式做一次「预热」，并把原因写清楚，避免后人误删。
    ''' </summary>
    Private Sub PrimeEnvironment(Psi As ProcessStartInfo)
        If _PrimedInstances.TryGetValue(Psi, Nothing) Then Return
        Try
            '只读一次 Count 就会触发惰性物化；
            '在污染环境下这里会抛，但副作用正是我们想要的「重置」
            Dim dummy As Integer = Psi.EnvironmentVariables.Count
        Catch ex As ArgumentException
            '预期内：大小写变体撞键。吞掉即可，内部状态已被重置。
            ' ⚠️ 措辞刻意写成"已完成初始化"而不是"存在重复变量" ——
            '    这在**每次从 bash / git-bash 启动 PCL** 时都会发生，
            '    是正常现象。原来写成「存在大小写重复变量」会让日志里每次都
            '    出现一条看着像错误的记录，反而掩盖真正的问题。
            '    （PCL 的 Logger 没有 Debug 级别，只能用 Info，所以靠措辞区分。）
            Logger.Info("DSH：子进程环境已完成初始化（含大小写变体归一）")
        Catch ex As Exception
            Logger.Warn(ex, "DSH：预热子进程环境时出现意外异常")
        End Try
        _PrimedInstances.Add(Psi, True)
    End Sub

    ''' <summary>
    ''' 已经预热过的实例，避免重复触发。
    ''' </summary>
    ''' <remarks>同样用弱引用表，理由见 <see cref="_SeededInstances"/> 的注释。</remarks>
    Private ReadOnly _PrimedInstances As New Runtime.CompilerServices.ConditionalWeakTable(Of ProcessStartInfo, Object)

    ''' <summary>
    ''' 把当前进程的环境变量灌入子进程环境。
    '''
    ''' 用 <see cref="Environment.GetEnvironmentVariables()"/> 逐个 <c>DictionaryEntry</c> 取，
    ''' 它返回的是**大小写不敏感**的字典但**不会**因为 <c>Path</c>/<c>PATH</c> 并存而抛异常
    ''' （这是它与 <c>ProcessStartInfo.EnvironmentVariables</c> 的关键区别）。
    ''' 灌入时统一用 <c>ToUpperInvariant()</c> 归一化键名，彻底消除大小写歧义。
    ''' </summary>
    ''' <param name="Psi">目标进程启动信息。幂等，重复调用不会重复灌入。</param>
    Public Sub SeedEnvironment(Psi As ProcessStartInfo)
        '必须先预热，否则下面的写入会因 Environment 处于损坏状态而全部失败
        PrimeEnvironment(Psi)
        If _SeededInstances.TryGetValue(Psi, Nothing) Then Return

        Dim count As Integer = 0
        For Each entry As System.Collections.DictionaryEntry In Environment.GetEnvironmentVariables()
            Dim key = CStr(entry.Key)
            If String.IsNullOrEmpty(key) Then Continue For
            Try
                Psi.Environment(EnvKey(key)) = CStr(entry.Value)
                count += 1
            Catch ex As Exception
                '单个变量灌不进去不该中断整个启动
                Logger.Warn(ex, $"DSH：灌入环境变量失败（{key}）")
            End Try
        Next
        _SeededInstances.Add(Psi, True)
        Logger.Info($"DSH：已灌入 {count} 个环境变量到子进程环境")
    End Sub

    ''' <summary>把键名归一化，避免 <c>Path</c> / <c>PATH</c> 两种写法不一致。</summary>
    Private Function EnvKey(Name As String) As String
        Return Name.ToUpperInvariant()
    End Function

    ''' <summary>
    ''' 把若干目录前置到子进程的 <c>PATH</c>。
    ''' </summary>
    ''' <param name="Psi">目标进程启动信息。调用前应已 <see cref="SeedEnvironment"/>。</param>
    ''' <param name="Dirs">要前置的目录，靠前者优先。</param>
    Public Sub PrependPath(Psi As ProcessStartInfo, ParamArray Dirs As String())
        Dim parts As New List(Of String)
        For Each d As String In Dirs
            If Not String.IsNullOrWhiteSpace(d) Then parts.Add(d.TrimEnd("\"c))
        Next

        ' 续上已灌入的父进程 PATH
        Dim inherited As String = GetEnvSafe(Psi, "PATH")
        If Not String.IsNullOrWhiteSpace(inherited) Then parts.Add(inherited)

        SetEnvSafe(Psi, "PATH", String.Join(";", parts.Distinct()))
    End Sub

#End Region

#Region "Node 探测"

    ''' <summary>
    ''' 解析 Node 版本字符串中的数字部分，形如 "v22.22.2" → {22, 22, 2}。
    ''' 解析失败返回 Nothing。
    ''' </summary>
    Public Function ParseNodeVersion(raw As String) As Integer()
        If String.IsNullOrWhiteSpace(raw) Then Return Nothing
        Dim m = Regex.Match(raw, "(\d+)\.(\d+)\.(\d+)")
        If Not m.Success Then Return Nothing
        Try
            Return {CInt(m.Groups(1).Value), CInt(m.Groups(2).Value), CInt(m.Groups(3).Value)}
        Catch
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' 判断 Node 版本是否满足 dsh 的 engines 约束：`^22.19.0 || >=24.0.0`。
    ''' </summary>
    ''' <remarks>
    ''' `^22.19.0` 在 semver 里表示 >=22.19.0 且 &lt;23.0.0。
    ''' 再叠加 >=24.0.0。中间的 23.x 不被接受。
    ''' </remarks>
    Public Function NodeVersionSatisfies(v As Integer()) As Boolean
        If v Is Nothing OrElse v.Length < 3 Then Return False
        If v(0) = NodeMinMajor Then Return v(1) >= NodeMinMinor
        Return v(0) >= NodeAltMajor
    End Function

    ''' <summary>
    ''' 探测可用的 Node。
    ''' </summary>
    ''' <param name="PreferPrivate">是否优先检查私有运行时目录。</param>
    ''' <returns>Node 可执行文件路径；不可用时返回 Nothing。</returns>
    ''' <remarks>
    ''' POC 教训：**不要依赖 `where.exe node`**。
    ''' 在受限 PATH 或便携分发场景下它可能找不到，但进程已在运行即证明 Node 可用。
    ''' 因此探测顺序是：私有目录 → 常见安装位置 → 环境变量提示的位置。
    ''' </remarks>
    Public Function DetectNode(Optional PreferPrivate As Boolean = True) As String
        ' 0. 当前激活槽位**自带的** Node —— 优先级最高
        '    导入的运行时是「那一套环境」的快照，它自带的 Node 才是与它配套的版本，
        '    用别的 Node 跑可能踩到 ABI / 内建模块的差异。
        '    另外这也让「导入的包」真正自给自足：不再依赖系统 PATH 上有没有 Node。
        If PreferPrivate Then
            Try
                Dim slot = DshRuntimeSlot.DshSlotActive()
                If slot IsNot Nothing AndAlso slot.HasOwnNode Then
                    Dim ownNode As String = slot.NodeExePath
                    Dim ownVer = GetNodeVersion(ownNode)
                    If NodeVersionSatisfies(ParseNodeVersion(ownVer)) Then
                        Logger.Info($"DSH：使用运行时槽位自带的 Node：{ownNode}（{ownVer}）")
                        Return ownNode
                    End If
                    Logger.Warn($"DSH：槽位自带 Node 版本不满足，回退到其他来源：" &
                                $"{ownNode}（{If(ownVer, "未知版本")}）")
                End If
            Catch ex As Exception
                Logger.Warn(ex, "DSH：读取槽位自带 Node 失败，回退到其他来源")
            End Try
        End If

        ' 1. 私有运行时
        If PreferPrivate Then
            Dim privateNode = Path.Combine(ModDSH.DshNodeDir, "node.exe")
            If FileExistsSafe(privateNode) Then
                Dim ver = GetNodeVersion(privateNode)
                If NodeVersionSatisfies(ParseNodeVersion(ver)) Then
                    Logger.Info($"DSH：使用私有 Node：{privateNode}（{ver}）")
                    Return privateNode
                End If
            End If
        End If

        ' 2. 常见系统安装位置
        Dim candidates As New List(Of String)
        For Each baseDir As String In {
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("LOCALAPPDATA")}
            If Not String.IsNullOrWhiteSpace(baseDir) Then
                candidates.Add(Path.Combine(baseDir, "nodejs", "node.exe"))
            End If
        Next
        Dim nvmHome = Environment.GetEnvironmentVariable("NVM_HOME")
        If Not String.IsNullOrWhiteSpace(nvmHome) Then
            candidates.Add(Path.Combine(nvmHome, "node.exe"))
        End If
        Dim nvmSymlink = Environment.GetEnvironmentVariable("NVM_SYMLINK")
        If Not String.IsNullOrWhiteSpace(nvmSymlink) Then
            candidates.Add(Path.Combine(nvmSymlink, "node.exe"))
        End If

        For Each c In candidates
            If Not FileExistsSafe(c) Then Continue For
            Dim ver = GetNodeVersion(c)
            If NodeVersionSatisfies(ParseNodeVersion(ver)) Then
                Logger.Info($"DSH：使用系统 Node：{c}（{ver}）")
                Return c
            End If
            Logger.Warn($"DSH：跳过版本不满足的 Node：{c}（{If(ver, "未知版本")}）")
        Next

        Logger.Info("DSH：未找到可用的 Node")
        Return Nothing
    End Function

    ''' <summary>读取指定 node.exe 的版本号。失败返回 Nothing。</summary>
    Private Function GetNodeVersion(nodeExe As String) As String
        Try
            Dim psi As New ProcessStartInfo(nodeExe, "-v") With {
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .CreateNoWindow = True
            }
            Using p = Process.Start(psi)
                Dim out = p.StandardOutput.ReadToEnd()
                p.WaitForExit(8000)
                Return out.Trim()
            End Using
        Catch ex As Exception
            Logger.Error(ex, $"DSH：读取 Node 版本失败：{nodeExe}")
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' 读取任意 node.exe 的版本号。文件不存在或读不出时返回 Nothing。
    ''' </summary>
    ''' <remarks>
    ''' 与私有的 <c>GetNodeVersion</c> 的区别：这个是**公开**的，供
    ''' <see cref="DshDoctor"/> 在体检时检查「系统上那个 Node 是不是太旧」用。
    ''' </remarks>
    Public Function ReadNodeVersionVerbose(nodeExe As String) As String
        If Not FileExistsSafe(nodeExe) Then Return Nothing
        Return GetNodeVersion(nodeExe)
    End Function

    ''' <summary>
    ''' 试着真的执行一个可执行文件，返回它是否成功启动。
    ''' </summary>
    ''' <param name="exePath">目标可执行文件。</param>
    ''' <param name="Args">命令行参数。</param>
    ''' <returns>进程能启动并正常退出（ExitCode = 0）时返回 True。</returns>
    ''' <remarks>
    ''' 为什么不能只判断文件存在：pnpm.exe 是原生二进制，缺 DLL、被杀软隔离、
    ''' 或复制过程被中断时，**文件还在但一执行就炸**。
    ''' 体检必须真跑一次才能发现这种「幽灵损坏」。
    ''' </remarks>
    Public Function ProbeExecutable(exePath As String, Args As String) As Boolean
        If Not FileExistsSafe(exePath) Then Return False
        Try
            Dim psi As New ProcessStartInfo(exePath, If(Args, "")) With {
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .RedirectStandardInput = True,
                .CreateNoWindow = True
            }

            ' ⚠️ 必须先把父进程环境灌进去，否则子进程连 SystemRoot / TEMP / PATH 都没有，
            '    node 在 Windows 上会直接启动失败 —— 表现就是「明明能跑的 dsh 被判成跑不起来」。
            '    这和 DshService 启动服务时是同一个坑（见那里的注释）。
            SeedEnvironment(psi)
            ' 工作目录设为 dsh 安装目录：与真实启动保持一致，模块解析走同一条路径。
            Try
                If Directory.Exists(ModDSH.DshInstallDir) Then psi.WorkingDirectory = ModDSH.DshInstallDir
            Catch
                ' 设不上就算了，不影响大多数情况
            End Try
            ' 保持干净 —— 探测不应受父进程环境影响
            SetEnvSafe(psi, "NO_COLOR", "1")

            Using p = Process.Start(psi)
                ' ⚠️ 不要「先 ReadToEnd(stdout) 再 ReadToEnd(stderr)」——
                ' 子进程往 stderr 写超过管道缓冲（~4KB）时，它会阻塞等我们读，
                ' 而我们正阻塞在 stdout 上等它关流 → 经典死锁，只能等 15 秒超时，
                ' 于是「明明能跑」的 dsh 被判成跑不起来。
                ' 正确做法：两路都异步读，再等退出。
                Dim outTask = p.StandardOutput.ReadToEndAsync()
                Dim errTask = p.StandardError.ReadToEndAsync()
                ' 关掉 stdin，免得子进程以为还有输入在等
                Try
                    p.StandardInput.Close()
                Catch
                    ' 已经关了就算了
                End Try

                If Not p.WaitForExit(15000) Then
                    Try
                        p.Kill()
                    Catch
                        ' 进程可能已经自己退了
                    End Try
                    Return False
                End If

                ' 等异步读收尾（进程已退出，流会立刻关，不会卡）
                Try
                    If Not Task.WaitAll({outTask, errTask}, 3000) Then Return p.ExitCode = 0
                Catch
                    ' 读取异常不影响退出码判定
                End Try
                Return p.ExitCode = 0
            End Using
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：探查可执行文件失败：{exePath}")
            Return False
        End Try
    End Function

#End Region

#Region "pnpm 探测"

    ''' <summary>
    ''' 探测可用的 pnpm。
    ''' </summary>
    ''' <returns>pnpm 可执行文件路径；不可用时返回 Nothing。</returns>
    ''' <remarks>
    ''' 同样不依赖 `where.exe`。
    ''' pnpm 对 dsh 的基础运行不是必需的（只有插件管理才需要），
    ''' 因此探测失败**不应**阻塞服务启动。
    ''' </remarks>
    Public Function DetectPnpm() As String
        ' 私有运行时优先
        Dim privatePnpm = Path.Combine(ModDSH.DshPnpmDir, "pnpm.exe")
        If FileExistsSafe(privatePnpm) Then Return privatePnpm

        ' 与 Node 同目录的情况（npm i -g pnpm 的典型布局）
        Dim nodeExe = DetectNode(False)
        If Not String.IsNullOrWhiteSpace(nodeExe) Then
            Dim nodeDir = Path.GetDirectoryName(nodeExe)
            For Each name In {"pnpm.exe", "pnpm.cmd"}
                Dim sibling = Path.Combine(nodeDir, name)
                If FileExistsSafe(sibling) Then Return sibling
            Next
        End If

        Return Nothing
    End Function

#End Region

#Region "dsh 探测"

    ''' <summary>dsh 入口脚本相对于安装目录的路径。</summary>
    Private ReadOnly DshEntryRelative As String =
        Path.Combine("node_modules", "@deepseek-ai", "dsh", "lib", "bin.js")

    ''' <summary>dsh package.json 相对于安装目录的路径。</summary>
    Private ReadOnly DshPackageRelative As String =
        Path.Combine("node_modules", "@deepseek-ai", "dsh", "package.json")

    ''' <summary>
    ''' 探测当前**激活槽位**的 dsh 入口脚本。
    ''' </summary>
    ''' <returns>lib/bin.js 的绝对路径；未安装时返回 Nothing。</returns>
    ''' <remarks>
    ''' ⭐ 这里刻意走 <see cref="DshRuntimeSlot.DshSlotActive"/> 而不是写死
    ''' <c>runtime\dsh\</c>：用户可以在多个运行时之间切换
    ''' （官方安装的 + 若干导入的本地包），切完入口就跟着变。
    ''' 槽位解析失败时 <c>DshSlotActive</c> 会回落到 npm 槽位，所以这里不会炸。
    ''' </remarks>
    Public Function DetectDsh() As String
        Try
            Dim slot = DshRuntimeSlot.DshSlotActive()
            If slot IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(slot.EntryRelative) Then
                Dim p As String = Path.Combine(slot.Dir, slot.EntryRelative)
                If FileExistsSafe(p) Then Return p
            End If
        Catch ex As Exception
            Logger.Warn(ex, "DSH：按槽位解析 dsh 入口失败，回退到官方目录")
        End Try

        ' 兜底：老的固定路径。保证「槽位清单损坏」时仍能启动
        Dim entry = Path.Combine(ModDSH.DshInstallDir, DshEntryRelative)
        Return If(FileExistsSafe(entry), entry, Nothing)
    End Function

    ''' <summary>读取当前激活槽位的 dsh 版本号。读取失败返回 Nothing。</summary>
    Public Function GetInstalledDshVersion() As String
        Try
            Dim slot = DshRuntimeSlot.DshSlotActive()
            If slot IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(slot.Version) Then
                Return slot.Version
            End If
        Catch ex As Exception
            Logger.Warn(ex, "DSH：按槽位读取 dsh 版本失败，回退到官方目录")
        End Try
        Return GetNpmSlotVersion()
    End Function

    ''' <summary>
    ''' 读取 **npm 槽位**（<c>runtime\dsh\</c>）的 dsh 版本号。
    ''' </summary>
    ''' <remarks>
    ''' 单独暴露出来，是因为槽位清单需要一个「官方槽位现在是什么版本」的答案，
    ''' 而这个答案必须**实时读磁盘** —— 用户随时可能重装成别的版本。
    ''' </remarks>
    Public Function GetNpmSlotVersion() As String
        Dim pkg = Path.Combine(ModDSH.DshInstallDir, DshPackageRelative)
        If Not FileExistsSafe(pkg) Then Return Nothing
        Try
            Dim obj = JObject.Parse(File.ReadAllText(pkg, Encoding.UTF8))
            Return obj("version")?.ToString()
        Catch ex As Exception
            Logger.Error(ex, "DSH：读取 dsh 版本失败")
            Return Nothing
        End Try
    End Function

#End Region

#Region "环境总探测"

    ''' <summary>
    ''' 一次性探测完整运行时环境。
    ''' </summary>
    Public Function DetectAll() As DshRuntimeInfo
        ModDSH.DshEnsureDirectories()
        Dim info As New DshRuntimeInfo With {
            .NodeExe = DetectNode(),
            .PnpmExe = DetectPnpm(),
            .DshEntry = DetectDsh()
        }
        If Not String.IsNullOrWhiteSpace(info.NodeExe) Then
            Dim v = ParseNodeVersion(GetNodeVersion(info.NodeExe))
            If v IsNot Nothing Then info.NodeVersion = $"{v(0)}.{v(1)}.{v(2)}"
        End If
        info.DshVersion = GetInstalledDshVersion()
        Logger.Info($"DSH：环境探测结果 → {info.Describe()}")
        Return info
    End Function

#End Region

End Module
