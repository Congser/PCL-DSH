Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Net.Http
Imports System.Text
Imports System.Text.RegularExpressions

''' <summary>
''' dsh 运行时组件的装配（下载与安装）。
'''
''' 这是 POC-1（poc/01-bootstrap/check-env.js）安装逻辑的 VB.NET 移植版。
'''
''' 只负责「把缺的东西装到私有目录」，不做探测（探测见 <see cref="DshRuntime"/>）、
''' 不做启动（见 <see cref="DshService"/>）。
'''
''' POC 阶段验证过的关键结论（务必保留）：
'''   - pnpm v12+ 是原生独立可执行文件，用 **`@pnpm/exe` 平台包**装配，
'''     绝不用官方 `get.pnpm.io/install.ps1`（Windows Defender 会拦截）
'''   - 全程不写系统 PATH、不改注册表、不污染用户环境
'''   - dsh 依赖树含 117MB 的 LibreOffice 组件，全量安装耗时可达 17 分钟以上，
'''     **超时必须给足**，否则会被中断并回滚
'''   - 解压 tar.gz 用 .NET 内置能力，**不调用系统 tar.exe**（受限 PATH 下会失败）
''' </summary>
Public Module DshInstaller

#Region "常量"

    ''' <summary>npm 注册表地址。</summary>
    Private Const NpmRegistry As String = "https://registry.npmjs.org"

    ''' <summary>Node.js 官方发行版下载地址前缀。</summary>
    Private Const NodeDistBase As String = "https://nodejs.org/dist"

    ''' <summary>下载大文件的超时时间（毫秒）。10 分钟。</summary>
    Private Const DownloadTimeoutMs As Integer = 600000

    ''' <summary>
    ''' dsh 安装的超时时间（毫秒）。**给足 40 分钟**。
    ''' POC 实测最慢一次 17m34s（480 个包），预留一倍余量。
    ''' </summary>
    Private Const InstallTimeoutMs As Integer = 2400000

#End Region

#Region "安装进度上报"

    ''' <summary>
    ''' 安装阶段。
    ''' </summary>
    Public Enum DshInstallStage
        ''' <summary>准备中。</summary>
        Preparing = 0
        ''' <summary>正在下载。</summary>
        Downloading = 1
        ''' <summary>正在解压。</summary>
        Extracting = 2
        ''' <summary>正在安装依赖。</summary>
        InstallingDeps = 3
        ''' <summary>完成。</summary>
        Done = 4
    End Enum

    ''' <summary>
    ''' 安装进度回调。
    ''' </summary>
    ''' <param name="Stage">当前阶段。</param>
    ''' <param name="Message">人类可读的描述。</param>
    ''' <param name="Progress">0-1 之间的进度；未知时为 -1。</param>
    ''' <remarks>
    ''' 刻意不在此模块里直接碰 UI —— 由调用方（PageDSH）决定如何 marshal 到 UI 线程。
    ''' </remarks>
    Public Delegate Sub DshInstallProgressHandler(Stage As DshInstallStage, Message As String, Progress As Double)

#End Region

#Region "对外主入口"

    ''' <summary>
    ''' 确保运行时环境完整；缺失的组件会被自动装配到私有目录。
    ''' </summary>
    ''' <param name="Progress">可选的进度回调。</param>
    ''' <returns>装配完成后的运行时信息。</returns>
    ''' <remarks>
    ''' 幂等：已就绪的组件会被跳过。
    ''' 这是「一键配置」的实现体 —— 用户无需手动装 Node / pnpm。
    ''' </remarks>
    Public Function EnsureRuntime(Optional Progress As DshInstallProgressHandler = Nothing) As DshRuntime.DshRuntimeInfo
        ModDSH.DshEnsureDirectories()

        ' ---- Node ----
        Dim info = DshRuntime.DetectAll()
        If String.IsNullOrWhiteSpace(info.NodeExe) Then
            Report(Progress, DshInstallStage.Preparing, "未找到可用的 Node.js，准备装配私有运行时…", -1)
            info.NodeExe = InstallPrivateNode(Progress)
            If String.IsNullOrWhiteSpace(info.NodeExe) Then
                Throw New InvalidOperationException("Node.js 装配失败：未能获得可用的 node.exe")
            End If
        Else
            Report(Progress, DshInstallStage.Preparing, $"复用已有 Node.js：{info.NodeVersion}", -1)
        End If

        ' ---- pnpm（非必需，失败不阻塞）----
        If String.IsNullOrWhiteSpace(info.PnpmExe) Then
            Try
                Report(Progress, DshInstallStage.Preparing, "未找到 pnpm，准备装配私有副本…", -1)
                info.PnpmExe = InstallPrivatePnpm(Progress)
            Catch ex As Exception
                ' pnpm 只影响插件管理，不影响 dsh 基础运行 —— 不应阻塞
                Logger.Error(ex, "DSH：pnpm 装配失败，已跳过（不影响服务启动）")
                Report(Progress, DshInstallStage.Preparing, $"pnpm 装配失败，已跳过：{ex.Message}", -1)
            End Try
        End If

        ' ---- dsh ----
        If String.IsNullOrWhiteSpace(info.DshEntry) Then
            InstallDsh(info, Progress)
            info.DshEntry = DshRuntime.DetectDsh()
            info.DshVersion = DshRuntime.GetInstalledDshVersion()
        Else
            Report(Progress, DshInstallStage.Preparing, $"复用已安装的 dsh：{If(info.DshVersion, "未知版本")}", -1)
        End If

        If String.IsNullOrWhiteSpace(info.DshEntry) Then
            Throw New InvalidOperationException("dsh 安装失败：未找到入口脚本 lib/bin.js")
        End If

        Report(Progress, DshInstallStage.Done, "运行时环境已就绪", 1)
        Logger.Info($"DSH：运行时装配完成 → {info.Describe()}")
        Return info
    End Function

#End Region

#Region "一键修复（供 DshDoctor 调用）"

    ''' <summary>
    ''' 强制装配一份私有 Node.js，返回 node.exe 路径；失败返回 Nothing。
    ''' </summary>
    ''' <remarks>
    ''' 与 <see cref="EnsureRuntime"/> 里的装配不同，这里**不做探测** ——
    ''' 调用方（<see cref="DshDoctor"/>）已经判定过现有 Node 不合格，
    ''' 再次探测只会拿到同一个不合格的结果。
    '''
    ''' 注意：会先删除已存在的私有 Node 目录（否则解压会因目标存在而失败），
    ''' 但**不会碰**系统上安装的 Node.js。
    ''' </remarks>
    Public Function RepairInstallPrivateNode(Optional Progress As DshInstallProgressHandler = Nothing) As String
        ModDSH.DshEnsureDirectories()
        Try
            If Directory.Exists(ModDSH.DshNodeDir) Then DshMigrate.DeleteDirectoryRobust(ModDSH.DshNodeDir)
        Catch ex As Exception
            Logger.Warn(ex, "DSH：清除旧私有 Node 目录失败，解压步骤可能会失败")
        End Try
        Try
            Return InstallPrivateNode(Progress)
        Catch ex As Exception
            Logger.Error(ex, "DSH：装配私有 Node 失败")
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' 强制装配一份私有 pnpm，返回 pnpm.exe 路径；失败返回 Nothing。
    ''' </summary>
    ''' <remarks>
    ''' 同样不做探测。会先清除已存在的私有 pnpm 目录 ——
    ''' 「文件在但执行不了」这种情况必须先删干净再装，否则复制的文件可能还是坏的。
    ''' </remarks>
    Public Function RepairInstallPrivatePnpm(Optional Progress As DshInstallProgressHandler = Nothing) As String
        ModDSH.DshEnsureDirectories()
        Try
            If Directory.Exists(ModDSH.DshPnpmDir) Then DshMigrate.DeleteDirectoryRobust(ModDSH.DshPnpmDir)
        Catch ex As Exception
            Logger.Warn(ex, "DSH：清除旧私有 pnpm 目录失败，解压步骤可能会失败")
        End Try
        Try
            Return InstallPrivatePnpm(Progress)
        Catch ex As Exception
            Logger.Error(ex, "DSH：装配私有 pnpm 失败")
            Return Nothing
        End Try
    End Function

#End Region

#Region "Node 装配"

    ''' <summary>
    ''' 下载并解压 Node.js 到私有运行时目录。
    ''' </summary>
    ''' <returns>私有 node.exe 的路径；失败返回 Nothing。</returns>
    Private Function InstallPrivateNode(Progress As DshInstallProgressHandler) As String
        Dim arch As String = If(Environment.Is64BitOperatingSystem, "x64", "x86")
        Dim fileName As String = $"node-v{DshRuntime.NodeVersion}-win-{arch}.zip"
        Dim url As String = $"{NodeDistBase}/v{DshRuntime.NodeVersion}/{fileName}"
        Dim zipPath As String = Path.Combine(ModDSH.DshRuntimeDir, fileName)

        Report(Progress, DshInstallStage.Downloading, $"正在下载 Node.js {DshRuntime.NodeVersion}…", 0)
        Logger.Info($"DSH：下载 Node.js → {url}")
        DownloadFile(url, zipPath, Progress, "Node.js")

        Report(Progress, DshInstallStage.Extracting, "正在解压 Node.js…", -1)
        Dim extractDir As String = Path.Combine(ModDSH.DshRuntimeDir, "node-temp")
        If Directory.Exists(extractDir) Then DshMigrate.DeleteDirectoryRobust(extractDir)
        Directory.CreateDirectory(extractDir)
        ExpandZipSafe(zipPath, extractDir)

        ' 官方 zip 内部有一层 `node-vX.Y.Z-win-x64\` 目录，需要把内容提上来
        Dim inner = Directory.GetDirectories(extractDir).FirstOrDefault()
        Dim sourceDir As String = If(inner, extractDir)
        If Directory.Exists(ModDSH.DshNodeDir) Then DshMigrate.DeleteDirectoryRobust(ModDSH.DshNodeDir)
        Directory.CreateDirectory(ModDSH.DshNodeDir)
        CopyDirectory(sourceDir, ModDSH.DshNodeDir)

        Try
            DshMigrate.DeleteDirectoryRobust(extractDir)
            File.Delete(zipPath)
        Catch ex As Exception
            Logger.Warn($"DSH：清理 Node 安装临时文件失败（可忽略）：{ex.Message}")
        End Try

        Dim nodeExe = Path.Combine(ModDSH.DshNodeDir, "node.exe")
        If Not File.Exists(nodeExe) Then
            Logger.Error($"DSH：Node 解压后未找到 node.exe：{nodeExe}")
            Return Nothing
        End If
        Logger.Info($"DSH：Node.js 装配完成 → {nodeExe}")
        Return nodeExe
    End Function

#End Region

#Region "pnpm 装配"

    ''' <summary>
    ''' 下载 `@pnpm/exe` 平台包并解压出 pnpm.exe。
    ''' </summary>
    ''' <returns>pnpm.exe 的路径。</returns>
    ''' <remarks>
    ''' 走 npm 注册表拿 tarball，而不是官方 install.ps1 —— 后者会被 Defender 拦截。
    ''' pnpm v12+ 是原生二进制，**不需要 Node 参与**，解压即用。
    ''' </remarks>
    Private Function InstallPrivatePnpm(Progress As DshInstallProgressHandler) As String
        Dim archTag As String =
            If(Environment.Is64BitOperatingSystem, "win32-x64", "win32-ia32")
        Dim pkgDirName As String = $"exe.{archTag}"

        ' 1. 取平台包的实际版本与 tarball 地址
        Dim metaUrl As String = $"{NpmRegistry}/@pnpm%2F{pkgDirName}/{DshRuntime.PnpmVersion}"
        Report(Progress, DshInstallStage.Preparing, $"正在查询 pnpm {DshRuntime.PnpmVersion} 平台包…", -1)
        Dim metaJson As String = HttpGetString(metaUrl)
        Dim tarball As String
        Try
            tarball = Newtonsoft.Json.Linq.JObject.Parse(metaJson)("dist")("tarball")?.ToString()
        Catch ex As Exception
            Throw New InvalidOperationException($"解析 pnpm 平台包元数据失败：{ex.Message}", ex)
        End Try
        If String.IsNullOrWhiteSpace(tarball) Then
            Throw New InvalidOperationException("pnpm 平台包元数据中缺少 dist.tarball")
        End If
        Logger.Info($"DSH：pnpm tarball → {tarball}")

        ' 2. 下载
        Dim tgzPath As String = Path.Combine(ModDSH.DshRuntimeDir, $"pnpm-{pkgDirName}.tgz")
        Report(Progress, DshInstallStage.Downloading, "正在下载 pnpm…", 0)
        DownloadFile(tarball, tgzPath, Progress, "pnpm")

        ' 3. 解压（tgz → tar → 文件）
        Report(Progress, DshInstallStage.Extracting, "正在解压 pnpm…", -1)
        Dim outDir As String = Path.Combine(ModDSH.DshRuntimeDir, "pnpm-temp")
        If Directory.Exists(outDir) Then DshMigrate.DeleteDirectoryRobust(outDir)
        Directory.CreateDirectory(outDir)
        ExtractTarGzSafe(tgzPath, outDir)

        ' npm tarball 内部结构是 package/xxx
        Dim pkgRoot = Path.Combine(outDir, "package")
        Dim sourceRoot As String = If(Directory.Exists(pkgRoot), pkgRoot, outDir)

        If Directory.Exists(ModDSH.DshPnpmDir) Then DshMigrate.DeleteDirectoryRobust(ModDSH.DshPnpmDir)
        Directory.CreateDirectory(ModDSH.DshPnpmDir)
        CopyDirectory(sourceRoot, ModDSH.DshPnpmDir)

        Try
            DshMigrate.DeleteDirectoryRobust(outDir)
            File.Delete(tgzPath)
        Catch ex As Exception
            Logger.Warn($"DSH：清理 pnpm 安装临时文件失败（可忽略）：{ex.Message}")
        End Try

        Dim pnpmExe = Path.Combine(ModDSH.DshPnpmDir, "pnpm.exe")
        If Not File.Exists(pnpmExe) Then
            Throw New InvalidOperationException($"pnpm 解压后未找到 pnpm.exe：{pnpmExe}")
        End If
        Logger.Info($"DSH：pnpm 装配完成 → {pnpmExe}")
        Return pnpmExe
    End Function

#End Region

#Region "dsh 安装"

    ''' <summary>
    ''' 需要允许执行构建脚本的依赖。
    '''
    ''' ⚠️ 背景：pnpm v10+ 默认**阻止**依赖的 install/postinstall 脚本（供应链安全策略），
    ''' 并在事务末尾以 <c>ERR_PNPM_IGNORED_BUILDS</c> **直接失败** —— 不是警告。
    ''' dsh 的依赖树里有 5 个包必须跑构建脚本，缺一不可：
    ''' <list type="bullet">
    '''   <item><c>node-pty</c> —— 原生 node 扩展，dsh 靠它做子进程终端</item>
    '''   <item><c>koffi</c> —— 原生 FFI 绑定</item>
    '''   <item><c>@deepseek-ai/dsh-subprocess-local</c> —— dsh 自身的子进程组件</item>
    '''   <item><c>@google/genai</c> / <c>protobufjs</c> —— 代码生成</item>
    ''' </list>
    ''' </summary>
    Private ReadOnly AllowedBuilds As String() = {
        "@deepseek-ai/dsh-subprocess-local",
        "@google/genai",
        "koffi",
        "node-pty",
        "protobufjs"
    }

    ''' <summary>
    ''' 在 pnpm 安装前写入 <c>pnpm-workspace.yaml</c> 的 <c>allowBuilds</c> 许可清单。
    '''
    ''' 为什么写文件而不是加 <c>--allow-build</c> 命令行参数：
    ''' <list type="number">
    '''   <item>
    '''     pnpm 12 的 <c>--allow-build</c> **只接受重复传参**，不支持逗号分隔
    '''     （实测 <c>--allow-build=a,b</c> 会被当成一个名为 <c>"a,b"</c> 的包名写进配置）。
    '''     拼长命令行既易错，也难排查。
    '''   </item>
    '''   <item>
    '''     pnpm 自己就是这么持久化这个决定的（<c>pnpm approve-builds</c> 写的就是这个文件），
    '''     我们只是替用户把这个交互步骤自动化掉。
    '''   </item>
    ''' </list>
    '''
    ''' 注意：允许构建脚本 = 允许这些包在安装时执行任意代码。
    ''' 这里刻意**逐个列举**而不是用 <c>--config.strict-dep-builds=false</c> 之类的全局开关，
    ''' 以便把攻击面限制在 dsh 实际需要的范围内。
    ''' </summary>
    Private Sub EnsureAllowBuilds()
        Dim target As String = Path.Combine(ModDSH.DshInstallDir, "pnpm-workspace.yaml")

        ' pnpm 模板里给的是占位符（"set this to true or false"），必须覆写为 true。
        ' 若文件已存在但内容含占位符，同样要覆写。
        Dim needWrite As Boolean = True
        If FileExistsSafe(target) Then
            Try
                Dim existing As String = File.ReadAllText(target)
                If Not existing.Contains("set this to true or false") Then
                    '已经是有效配置，但可能缺少我们需要的项 —— 逐项检查
                    needWrite = AllowedBuilds.Any(Function(p) Not existing.Contains($"'{p}'") AndAlso Not existing.Contains($"{p}:"))
                End If
            Catch ex As Exception
                Logger.Warn(ex, "读取 pnpm-workspace.yaml 失败，将直接覆写")
                needWrite = True
            End Try
        End If

        If Not needWrite Then
            Logger.Info("DSH：pnpm allowBuilds 已就绪，跳过写入")
            Return
        End If

        Dim sb As New StringBuilder()
        sb.AppendLine("# 由 PCL_DSH 自动生成：放行 dsh 原生依赖的构建脚本。")
        sb.AppendLine("# 手工编辑后请勿删除本段，否则 pnpm 会以 ERR_PNPM_IGNORED_BUILDS 拒绝安装。")
        sb.AppendLine("allowBuilds:")
        For Each pkg As String In AllowedBuilds
            sb.AppendLine($"  '{pkg}': true")
        Next

        Try
            If Not Directory.Exists(ModDSH.DshInstallDir) Then
                Directory.CreateDirectory(ModDSH.DshInstallDir)
            End If
            File.WriteAllText(target, sb.ToString(), New UTF8Encoding(False))
            Logger.Info($"DSH：已写入 pnpm allowBuilds（{AllowedBuilds.Length} 项）→ {target}")
        Catch ex As Exception
            '写不进去不直接失败 —— 让 pnpm 去报它自己的错，信息更准确
            Logger.Warn(ex, $"写入 pnpm-workspace.yaml 失败：{target}")
        End Try
    End Sub

    ''' <summary>
    ''' 安装（或切换到）指定版本的 dsh。
    ''' </summary>
    ''' <param name="Version">目标版本，例如 <c>0.1.6-alpha.2</c>。传 Nothing 用锁定版本。</param>
    ''' <param name="RegistryUrl">npm 注册表地址。传 Nothing 用 pnpm 默认（官方源）。</param>
    ''' <param name="Progress">进度回调。</param>
    ''' <remarks>
    ''' 阻塞调用，请放在后台线程。
    ''' pnpm 会就地覆盖 <c>node_modules</c>，所以「切换版本」= 再 add 一次，
    ''' 不需要先卸载 —— 这也是 pnpm 的常规用法。
    ''' </remarks>
    Public Sub InstallDshVersion(Version As String,
                                 Optional RegistryUrl As String = Nothing,
                                 Optional Progress As DshInstallProgressHandler = Nothing)
        ModDSH.DshEnsureDirectories()

        ' dsh 需要 pnpm 才能装
        Dim pnpmExe As String = DshRuntime.DetectPnpm()
        If String.IsNullOrWhiteSpace(pnpmExe) Then
            Report(Progress, DshInstallStage.Preparing, "未找到 pnpm，准备装配私有副本…", -1)
            pnpmExe = InstallPrivatePnpm(Progress)
        End If
        If String.IsNullOrWhiteSpace(pnpmExe) Then
            Throw New InvalidOperationException("缺少 pnpm，无法安装 dsh。")
        End If

        ' node 也顺手确保一下（pnpm 需要它来跑 node 脚本）
        Dim nodeExe As String = DshRuntime.DetectNode()
        If String.IsNullOrWhiteSpace(nodeExe) Then
            Report(Progress, DshInstallStage.Preparing, "未找到可用的 Node.js，准备装配私有运行时…", -1)
            nodeExe = InstallPrivateNode(Progress)
        End If

        Dim runtime As New DshRuntime.DshRuntimeInfo With {
            .NodeExe = nodeExe,
            .PnpmExe = pnpmExe
        }

        InstallDsh(runtime, Progress, Version, RegistryUrl)
        Report(Progress, DshInstallStage.Done, "dsh 安装完成", 1)
    End Sub

    ''' <summary>
    ''' 用 pnpm 把指定版本的 dsh 安装到私有目录。
    ''' </summary>
    ''' <remarks>
    ''' 关键点：
    '''   - 用 `pnpm add` 而非 `pnpm install`，并显式固定版本号
    '''   - 必须先写 <c>pnpm-workspace.yaml</c> 的 <c>allowBuilds</c> 放行原生依赖的构建脚本，
    '''     否则 pnpm v10+ 会以 `ERR_PNPM_IGNORED_BUILDS` **直接失败**（详见 <see cref="EnsureAllowBuilds"/>）
    '''   - 工作目录 = <see cref="ModDSH.DshInstallDir"/>
    '''   - PATH 前置 pnpm 所在目录，因为 pnpm 可能会去调 node
    '''   - **超时必须给足** —— POC 实测 17m34s
    ''' </remarks>
    Private Sub InstallDsh(Runtime As DshRuntime.DshRuntimeInfo,
                           Progress As DshInstallProgressHandler,
                           Optional Version As String = Nothing,
                           Optional RegistryUrl As String = Nothing)
        If String.IsNullOrWhiteSpace(Runtime.PnpmExe) Then
            Throw New InvalidOperationException(
                "缺少 pnpm，无法安装 dsh。请检查网络后重试。")
        End If

        Dim targetVersion As String =
            If(String.IsNullOrWhiteSpace(Version), ModDSH.DshVersion, Version.Trim())
        Dim spec As String = $"{ModDSH.DshPackageName}@{targetVersion}"
        Report(Progress, DshInstallStage.InstallingDeps,
               $"正在安装 {spec}（首次安装需下载约 480 个包，可能耗时十几分钟，请耐心等待）…", -1)
        Logger.Info($"DSH：开始安装 {spec}，超时上限 {InstallTimeoutMs / 60000} 分钟")

        ' 放行原生依赖的构建脚本（必须在 pnpm add 之前写好）
        EnsureAllowBuilds()

        ' pnpm 的调用方式取决于它是 .exe 还是 .cmd
        Dim pnpmExe = Runtime.PnpmExe
        Dim isCmdShell As Boolean = pnpmExe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)

        Dim psi As New ProcessStartInfo() With {
            .FileName = If(isCmdShell, "cmd.exe", pnpmExe),
            .WorkingDirectory = ModDSH.DshInstallDir,
            .UseShellExecute = False,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .CreateNoWindow = True,
            .StandardOutputEncoding = Encoding.UTF8,
            .StandardErrorEncoding = Encoding.UTF8
        }
        If isCmdShell Then
            psi.Arguments = $"/c """"{pnpmExe}"" add {spec}"""
        Else
            psi.Arguments = $"add {spec}"
        End If

        ' ⚠️ 环境变量一律走 DshRuntime 的封装：
        '   ProcessStartInfo.Environment 不继承父进程环境，必须先 SeedEnvironment 灌入，
        '   否则 node/pnpm 连 SystemRoot、TEMP、USERPROFILE 都没有，直接跑不起来。
        '   详见 DshRuntime.SetEnvSafe 的注释。
        DshRuntime.SeedEnvironment(psi)

        ' 让 pnpm 能找到 node：把 node 与 pnpm 目录前置到 PATH
        DshRuntime.PrependPath(psi,
                               Path.GetDirectoryName(Runtime.NodeExe),
                               Path.GetDirectoryName(pnpmExe))

        ' 保持输出无色，便于解析
        DshRuntime.SetEnvSafe(psi, "NO_COLOR", "1")
        ' 告诉 pnpm 把包装在安装目录，避免它往上找 workspace 根
        DshRuntime.SetEnvSafe(psi, "NPM_CONFIG_GLOBAL", "false")
        ' 镜像源：用户可以在「镜像版本」页里换源
        If Not String.IsNullOrWhiteSpace(RegistryUrl) Then
            DshRuntime.SetEnvSafe(psi, "NPM_CONFIG_REGISTRY", RegistryUrl.TrimEnd("/"c))
            Logger.Info($"DSH：本次安装使用 npm 源 {RegistryUrl}")
        End If

        Dim lastLines As New List(Of String)
        Using p As New Process()
            p.StartInfo = psi
            AddHandler p.OutputDataReceived,
                Sub(sender, e)
                    If e.Data Is Nothing Then Return
                    TrackLine(lastLines, e.Data)
                End Sub
            AddHandler p.ErrorDataReceived,
                Sub(sender, e)
                    If e.Data Is Nothing Then Return
                    TrackLine(lastLines, e.Data)
                    Logger.Warn($"DSH[pnpm] {e.Data}")
                End Sub

            p.Start()
            p.BeginOutputReadLine()
            p.BeginErrorReadLine()

            If Not p.WaitForExit(InstallTimeoutMs) Then
                Try
                    p.Kill()
                Catch
                End Try
                Throw New TimeoutException(
                    $"dsh 安装超时（超过 {InstallTimeoutMs \ 60000} 分钟）。最后输出：" & vbCrLf &
                    String.Join(vbCrLf, TakeLast(lastLines, 15)))
            End If

            If p.ExitCode <> 0 Then
                Throw New InvalidOperationException(
                    $"pnpm add 失败，退出码 {p.ExitCode}。最后输出：" & vbCrLf &
                    String.Join(vbCrLf, TakeLast(lastLines, 15)))
            End If
        End Using

        Logger.Info("DSH：dsh 安装完成")
    End Sub

    ''' <summary>把一行输出追加进环形缓冲（上限 200 行），用于失败时回溯。</summary>
    Private Sub TrackLine(Buffer As List(Of String), Line As String)
        Buffer.Add(Line)
        If Buffer.Count > 200 Then Buffer.RemoveAt(0)
    End Sub

    ''' <summary>
    ''' 取列表末尾 N 项。
    ''' </summary>
    ''' <remarks>
    ''' .NET Framework 4.8 的 LINQ **没有** `TakeLast`（那是 .NET Core 2.0+ 才加的），
    ''' 因此这里自己实现，避免编译失败。
    ''' </remarks>
    Private Function TakeLast(Source As List(Of String), Count As Integer) As List(Of String)
        If Source Is Nothing OrElse Source.Count = 0 Then Return New List(Of String)
        If Count >= Source.Count Then Return New List(Of String)(Source)
        Return Source.GetRange(Source.Count - Count, Count)
    End Function

#End Region

#Region "下载与解压"

    ''' <summary>
    ''' 下载文件到指定路径，并把进度回调出去。
    ''' </summary>
    Public Sub DownloadFile(Url As String, LocalPath As String, Progress As DshInstallProgressHandler,
                            Optional DisplayName As String = Nothing)
        Dim dir = Path.GetDirectoryName(LocalPath)
        If Not String.IsNullOrWhiteSpace(dir) AndAlso Not Directory.Exists(dir) Then
            Directory.CreateDirectory(dir)
        End If

        Using handler As New HttpClientHandler()
            handler.AllowAutoRedirect = True
            Using client As New HttpClient(handler)
                client.Timeout = TimeSpan.FromMilliseconds(DownloadTimeoutMs)
                Using resp = client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead).Result
                    resp.EnsureSuccessStatusCode()
                    Dim total As Long = If(resp.Content.Headers.ContentLength, -1L)
                    Using src = resp.Content.ReadAsStreamAsync().Result
                        Using dst = New FileStream(LocalPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, False)
                            Dim buffer(81919) As Byte
                            Dim readTotal As Long = 0
                            Dim lastReport As Integer = 0
                            While True
                                Dim n = src.Read(buffer, 0, buffer.Length)
                                If n <= 0 Then Exit While
                                dst.Write(buffer, 0, n)
                                readTotal += n
                                If total > 0 AndAlso Progress IsNot Nothing Then
                                    ' 每变化 2% 才回调一次，避免刷爆 UI 线程
                                    Dim pct = CInt(readTotal * 100 \ total)
                                    If pct >= lastReport + 2 Then
                                        lastReport = pct
                                        Report(Progress, DshInstallStage.Downloading,
                                               $"正在下载 {If(DisplayName, "组件")}… {pct}%",
                                               readTotal / CDbl(total))
                                    End If
                                End If
                            End While
                        End Using
                    End Using
                End Using
            End Using
        End Using
    End Sub

    ''' <summary>GET 一个文本响应。</summary>
    Private Function HttpGetString(Url As String) As String
        Using handler As New HttpClientHandler()
            handler.AllowAutoRedirect = True
            Using client As New HttpClient(handler)
                client.Timeout = TimeSpan.FromMilliseconds(DownloadTimeoutMs)
                Using resp = client.GetAsync(Url).Result
                    resp.EnsureSuccessStatusCode()
                    Return resp.Content.ReadAsStringAsync().Result
                End Using
            End Using
        End Using
    End Function

    ''' <summary>
    ''' 解压 zip。
    ''' </summary>
    ''' <remarks>
    ''' .NET Framework 4.8 的 <see cref="ZipFile"/> 在中文路径 + GBK 文件名时可能乱码，
    ''' 这里显式用 UTF8 解码条目名。
    ''' </remarks>
    Private Sub ExpandZipSafe(ZipPath As String, DestDir As String)
        Using archive = ZipFile.Open(ZipPath, ZipArchiveMode.Read, Encoding.UTF8)
            For Each entry In archive.Entries
                ' 目录条目（以 / 结尾）只需建目录
                If String.IsNullOrEmpty(entry.Name) Then
                    Dim d = Path.Combine(DestDir, entry.FullName)
                    If Not Directory.Exists(d) Then Directory.CreateDirectory(d)
                    Continue For
                End If
                Dim target = Path.Combine(DestDir, entry.FullName)
                Dim targetDir = Path.GetDirectoryName(target)
                If Not String.IsNullOrWhiteSpace(targetDir) AndAlso Not Directory.Exists(targetDir) Then
                    Directory.CreateDirectory(targetDir)
                End If
                entry.ExtractToFile(target, True)
            Next
        End Using
    End Sub

    ''' <summary>
    ''' 解压 tar.gz。
    ''' </summary>
    ''' <remarks>
    ''' **刻意不调用系统 tar.exe** —— POC 教训：受限 PATH / 便携分发环境下它可能不存在。
    ''' 自己解析 tar 格式，只依赖 .NET 内置的 <see cref="GZipStream"/>。
    ''' </remarks>
    Private Sub ExtractTarGzSafe(TarGzPath As String, DestDir As String)
        Using gz As New GZipStream(File.OpenRead(TarGzPath), CompressionMode.Decompress)
            Dim header(511) As Byte
            While True
                ' 读 512 字节块头
                Dim read = ReadFully(gz, header, 0, 512)
                If read < 512 Then Exit While
                ' 全零块 = 归档结束
                If header.All(Function(b) b = 0) Then Exit While

                ' 文件名：0..100
                Dim name = ReadTarString(header, 0, 100)
                ' 大小：124..136（八进制 ASCII）
                Dim sizeStr = ReadTarString(header, 124, 12).Trim()
                Dim fileSize As Long = 0
                If sizeStr.Length > 0 Then
                    Try
                        fileSize = Convert.ToInt64(sizeStr, 8)
                    Catch
                        fileSize = 0
                    End Try
                End If
                ' 类型标志：156
                Dim typeFlag As Byte = header(156)

                If String.IsNullOrWhiteSpace(name) Then Exit While

                ' GNU 长文件名扩展（类型 L）：下一个块是真实名字，跳过
                If typeFlag = AscW("L"c) Then
                    Dim longNameBuf(fileSize - 1) As Byte
                    ReadFully(gz, longNameBuf, 0, CInt(fileSize))
                    Dim skip = (512 - (CInt(fileSize) Mod 512)) Mod 512
                    If skip > 0 Then
                        Dim pad(skip - 1) As Byte
                        ReadFully(gz, pad, 0, skip)
                    End If
                    name = Encoding.UTF8.GetString(longNameBuf).TrimEnd(ChrW(0))
                    ' 再读一个头
                    read = ReadFully(gz, header, 0, 512)
                    If read < 512 Then Exit While
                    name = ReadTarString(header, 0, 100)
                    sizeStr = ReadTarString(header, 124, 12).Trim()
                    fileSize = 0
                    If sizeStr.Length > 0 Then
                        Try
                            fileSize = Convert.ToInt64(sizeStr, 8)
                        Catch
                            fileSize = 0
                        End Try
                    End If
                    typeFlag = header(156)
                End If

                ' 归一化路径分隔符，防目录穿越
                name = name.Replace("\"c, "/"c).TrimStart("/"c)
                If name.Contains("..") Then
                    ' 跳过危险条目，同时把内容读掉以保持流位置正确
                    SkipTarEntry(gz, fileSize)
                    Continue While
                End If

                Dim target = Path.Combine(DestDir, name.Replace("/"c, Path.DirectorySeparatorChar))
                Dim isDir As Boolean = (typeFlag = AscW("5"c)) OrElse name.EndsWith("/")

                If isDir Then
                    If Not Directory.Exists(target) Then Directory.CreateDirectory(target)
                ElseIf typeFlag = AscW("0"c) OrElse typeFlag = 0 Then
                    Dim targetDir = Path.GetDirectoryName(target)
                    If Not String.IsNullOrWhiteSpace(targetDir) AndAlso Not Directory.Exists(targetDir) Then
                        Directory.CreateDirectory(targetDir)
                    End If
                    Using outFs As New FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None)
                        If fileSize > 0 Then
                            Dim remaining As Long = fileSize
                            Dim buf(81919) As Byte
                            While remaining > 0
                                Dim want As Integer = CInt(Math.Min(CLng(buf.Length), remaining))
                                Dim n = gz.Read(buf, 0, want)
                                If n <= 0 Then Exit While
                                outFs.Write(buf, 0, n)
                                remaining -= n
                            End While
                        End If
                    End Using
                Else
                    ' 其它类型（符号链接等）跳过内容
                    SkipTarEntry(gz, fileSize)
                    Continue While
                End If

                ' 跳到下一个 512 边界
                Dim padding = (512 - (CInt(fileSize) Mod 512)) Mod 512
                If padding > 0 Then
                    Dim pad(padding - 1) As Byte
                    ReadFully(gz, pad, 0, padding)
                End If
            End While
        End Using
    End Sub

    ''' <summary>跳过 tar 条目内容（含对齐填充），保持流位置正确。</summary>
    Private Sub SkipTarEntry(Stream As Stream, FileSize As Long)
        Dim total As Long = FileSize + ((512 - (FileSize Mod 512)) Mod 512)
        Dim buf(8191) As Byte
        While total > 0
            Dim want As Integer = CInt(Math.Min(CLng(buf.Length), total))
            Dim n = Stream.Read(buf, 0, want)
            If n <= 0 Then Exit While
            total -= n
        End While
    End Sub

    ''' <summary>从 tar 块头里读一个以 NUL 结尾的 ASCII 字段。</summary>
    Private Function ReadTarString(Buffer As Byte(), Offset As Integer, Length As Integer) As String
        Dim endIdx As Integer = Offset
        While endIdx < Offset + Length AndAlso Buffer(endIdx) <> 0
            endIdx += 1
        End While
        Return Encoding.UTF8.GetString(Buffer, Offset, endIdx - Offset)
    End Function

    ''' <summary>尽力把流读满指定字节数，返回实际读到的数量。</summary>
    Private Function ReadFully(Stream As Stream, Buffer As Byte(), Offset As Integer, Count As Integer) As Integer
        Dim total As Integer = 0
        While total < Count
            Dim n = Stream.Read(Buffer, Offset + total, Count - total)
            If n <= 0 Then Exit While
            total += n
        End While
        Return total
    End Function

    ''' <summary>递归复制目录。</summary>
    Private Sub CopyDirectory(Source As String, Dest As String)
        If Not Directory.Exists(Dest) Then Directory.CreateDirectory(Dest)
        For Each f In Directory.GetFiles(Source)
            File.Copy(f, Path.Combine(Dest, Path.GetFileName(f)), True)
        Next
        For Each d In Directory.GetDirectories(Source)
            CopyDirectory(d, Path.Combine(Dest, Path.GetFileName(d)))
        Next
    End Sub

#End Region

#Region "辅助"

    ''' <summary>安全地触发进度回调。</summary>
    Private Sub Report(Handler As DshInstallProgressHandler, Stage As DshInstallStage,
                       Message As String, Progress As Double)
        Logger.Info($"[DSH 装配/{Stage}] {Message}")
        If Handler IsNot Nothing Then Handler(Stage, Message, Progress)
    End Sub

#End Region

End Module
