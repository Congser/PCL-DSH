Imports System.IO

''' <summary>
''' 用**系统浏览器**打开 dsh 的 Web UI。
'''
''' 这是 PCL_DSH 的界面承载方式：不内嵌 WebView2，而是把 dsh 的地址交给
''' 用户自己的浏览器。
'''
''' 为什么这样更好：
'''   1. **省体积** —— 不用把 3 个 WebView2 程序集（约 1 MB）嵌进 exe
'''   2. **无残留进程** —— 不再有 msedgewebview2.exe 需要回收
'''   3. **规避死锁** —— WebView2 初始化必须在 UI 线程且不能同步等待，
'''      稍有不慎就整个窗口卡死（本项目实测踩过一次）
'''   4. **体验更好** —— 用户能用自己熟悉的浏览器、书签、扩展、密码管理器
'''   5. **贴近官方设计** —— dsh 默认行为就是弹系统浏览器，
'''      我们只是把 `--no-open` 换成"由外壳决定何时打开"
'''
''' 代价：界面不再嵌在 PCL 窗口里，而是独立窗口。这是刻意的取舍。
''' </summary>
Public Module DshBrowser

#Region "浏览器选择"

    ''' <summary>浏览器配置方式。</summary>
    Public Enum DshBrowserKind
        ''' <summary>用系统默认浏览器（ShellExecute，最简单可靠）。</summary>
        SystemDefault = 0
        ''' <summary>用用户指定的浏览器可执行文件。</summary>
        Custom = 1
    End Enum

    ''' <summary>当前配置的浏览器方式。</summary>
    Public ReadOnly Property DshBrowserCurrentKind As DshBrowserKind
        Get
            Return CType(Settings.Get(Of Integer)("DshBrowserType"), DshBrowserKind)
        End Get
    End Property

    ''' <summary>用户指定的浏览器可执行文件路径（未设置时为空）。</summary>
    Public ReadOnly Property DshBrowserCustomPath As String
        Get
            Return If(Settings.Get(Of String)("DshBrowserPath"), "").Trim()
        End Get
    End Property

    ''' <summary>是否在服务就绪后自动打开浏览器。</summary>
    Public ReadOnly Property DshBrowserShouldAutoOpen As Boolean
        Get
            Return Settings.Get(Of Boolean)("DshBrowserAutoOpen")
        End Get
    End Property

    ''' <summary>
    ''' 当前配置是否可用（自定义模式下会检查文件是否存在）。
    ''' </summary>
    Public ReadOnly Property DshBrowserIsConfigured As Boolean
        Get
            Select Case DshBrowserCurrentKind
                Case DshBrowserKind.SystemDefault
                    Return True
                Case DshBrowserKind.Custom
                    Return Not String.IsNullOrWhiteSpace(DshBrowserCustomPath) AndAlso
                           FileExistsSafe(DshBrowserCustomPath)
                Case Else
                    Return False
            End Select
        End Get
    End Property

    ''' <summary>
    ''' 给用户看的当前浏览器描述文案。
    ''' </summary>
    Public Function DshBrowserDescribe() As String
        Select Case DshBrowserCurrentKind
            Case DshBrowserKind.SystemDefault
                Return "系统默认浏览器"
            Case DshBrowserKind.Custom
                Dim p As String = DshBrowserCustomPath
                If String.IsNullOrWhiteSpace(p) Then Return "未指定（请选择浏览器）"
                If Not FileExistsSafe(p) Then Return $"路径不存在：{p}"
                Return Path.GetFileName(p)
            Case Else
                Return "未配置"
        End Select
    End Function

#End Region

#Region "打开"

    ''' <summary>
    ''' 用配置好的浏览器打开 dsh 的 Web UI。
    ''' </summary>
    ''' <param name="ReadyUrl">带认证 token 的就绪 URL。</param>
    ''' <returns>成功返回 True；失败已在内部提示用户。</returns>
    ''' <remarks>
    ''' 必须传**带 token 的原始 URL**：dsh 靠它完成首次认证并下发 cookie。
    ''' 之后浏览器里再访问不带 token 的地址也能凭 cookie 通过。
    ''' </remarks>
    Public Function OpenDsh(ReadyUrl As String) As Boolean
        If String.IsNullOrWhiteSpace(ReadyUrl) Then
            Logger.Warn("DSH：就绪 URL 为空，无法打开浏览器")
            Hint("DSH 服务地址为空，无法打开浏览器。", HintType.Red)
            Return False
        End If

        Try
            Select Case DshBrowserCurrentKind
                Case DshBrowserKind.Custom
                    Return OpenWithCustomBrowser(ReadyUrl)
                Case Else
                    Return OpenWithSystemBrowser(ReadyUrl)
            End Select
        Catch ex As Exception
            Logger.Error(ex, "DSH：打开浏览器失败")
            '失败时把地址复制到剪贴板，用户还能手动访问 —— 不要让人卡死在这儿
            ClipboardSet(ReadyUrl, False)
            Hint("无法打开浏览器，DSH 地址已复制到剪贴板，可手动粘贴访问。", HintType.Red)
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 打开某个实例的 Web UI。
    ''' </summary>
    ''' <param name="Instance">目标实例。</param>
    ''' <returns>成功返回 True；失败已在内部提示用户。</returns>
    ''' <remarks>
    ''' 优先用**带 token 的原始 URL**：dsh 靠它完成首次认证并下发 cookie。
    ''' 没有 token URL 时（例如 PCL 重启后服务是上一次留下的）回退到普通地址 ——
    ''' 浏览器里若已有 cookie 照样能过。
    ''' </remarks>
    Public Function OpenInstance(Instance As DshInstance) As Boolean
        If Instance Is Nothing Then
            Hint("实例不存在，无法打开界面。", HintType.Red)
            Return False
        End If

        Dim url As String = Instance.ReadyUrl
        If String.IsNullOrWhiteSpace(url) Then url = Instance.DisplayUrl
        If String.IsNullOrWhiteSpace(url) Then
            Hint($"实例「{Instance.DisplayName}」尚未就绪，无法打开界面。", HintType.Red)
            Return False
        End If

        Return OpenDsh(url)
    End Function

    ''' <summary>
    ''' 用系统默认浏览器打开。
    ''' </summary>
    ''' <remarks>
    ''' 走 PCL 的 <see cref="OpenWebsite"/>，它内部用 ShellExecute，
    ''' 能正确处理 http 协议的默认关联，并且在失败时会自动复制链接到剪贴板。
    ''' </remarks>
    Private Function OpenWithSystemBrowser(ReadyUrl As String) As Boolean
        Logger.Info("DSH：用系统默认浏览器打开界面")
        OpenWebsite(ReadyUrl)
        Return True
    End Function

    ''' <summary>
    ''' 用用户指定的浏览器打开。
    ''' </summary>
    ''' <remarks>
    ''' 参数模板里的 <c>{url}</c> 占位符会被替换成实际地址。
    ''' 没写占位符时，URL 作为**唯一参数**追加 —— 这是绝大多数浏览器的通用写法。
    '''
    ''' 支持 <c>--app={url}</c> 这类"应用模式"参数，让 DSH 界面开起来像原生应用：
    ''' 没有地址栏和标签页，体验接近内嵌 WebView2，但仍是独立进程。
    ''' </remarks>
    Private Function OpenWithCustomBrowser(ReadyUrl As String) As Boolean
        Dim exePath As String = DshBrowserCustomPath
        If String.IsNullOrWhiteSpace(exePath) Then
            Logger.Warn("DSH：配置为自定义浏览器但未指定路径，回退到系统默认浏览器")
            Hint("未指定浏览器路径，已改用系统默认浏览器打开。", HintType.Red)
            Return OpenWithSystemBrowser(ReadyUrl)
        End If
        If Not FileExistsSafe(exePath) Then
            Logger.Warn($"DSH：自定义浏览器不存在（{exePath}），回退到系统默认浏览器")
            Hint($"指定的浏览器不存在（{exePath}），已改用系统默认浏览器。", HintType.Red)
            Return OpenWithSystemBrowser(ReadyUrl)
        End If

        Dim argTemplate As String = If(Settings.Get(Of String)("DshBrowserArgs"), "").Trim()
        Dim args As String

        If String.IsNullOrWhiteSpace(argTemplate) Then
            '没有模板：URL 作为唯一参数
            args = QuoteArg(ReadyUrl)
        ElseIf argTemplate.Contains("{url}") Then
            '有占位符：替换掉（占位符本身不加引号，由用户模板自带的引号决定）
            args = argTemplate.Replace("{url}", QuoteArg(ReadyUrl))
        Else
            '有参数但没有占位符：URL 追加到末尾
            args = argTemplate & " " & QuoteArg(ReadyUrl)
        End If

        Logger.Info($"DSH：用自定义浏览器打开界面 → {exePath} {args}")
        StartProcess(New ProcessStartInfo With {
            .FileName = exePath,
            .Arguments = args,
            .UseShellExecute = False
        })
        Return True
    End Function

    ''' <summary>
    ''' 给命令行参数加引号。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 与 <c>DshPluginMarket.QuoteArg</c> 同源的两个坑，这里也一起修掉：
    ''' <list type="number">
    ''' <item>只在含空格 / <c>&amp;</c> / <c>?</c> 时才加引号 —— 但
    ''' <c>|</c> <c>&lt;</c> <c>&gt;</c> <c>^</c> 同样是 cmd 元字符，一个都不拦。
    ''' 这里的 URL 带 token（base64url 字符集），虽然实际不会含元字符，
    ''' 但「按内容决定要不要加引号」本身就是脆弱设计。</item>
    ''' <item>引号内的转义用 <c>\"</c> —— 那是反斜杠语言（C/JS）的写法，
    ''' Windows 命令行靠**双写** <c>""</c> 转义。</item>
    ''' </list>
    ''' 改成一律加引号、内层双写。
    ''' </remarks>
    Private Function QuoteArg(Value As String) As String
        If Value Is Nothing Then Return """"""
        Return """" & Value.Replace("""", """""") & """"
    End Function

#End Region

#Region "探测已安装的浏览器"

    ''' <summary>
    ''' 一个探测到的浏览器。
    ''' </summary>
    Public Class BrowserInfo
        Public Property Name As String
        Public Property Path As String
        Public Sub New(Name As String, Path As String)
            Me.Name = Name
            Me.Path = Path
        End Sub
    End Class

    ''' <summary>
    ''' 探测本机常见浏览器的安装路径，用于设置页给用户直接选。
    ''' </summary>
    ''' <returns>找到的浏览器列表；未安装的不会出现。</returns>
    ''' <remarks>
    ''' 只查**标准安装路径**，不做全盘扫描 —— 全盘扫描太慢，
    ''' 而且会让用户以为程序卡住了。找不到就让用户手动选文件。
    ''' </remarks>
    Public Function DetectBrowsers() As List(Of BrowserInfo)
        Dim result As New List(Of BrowserInfo)

        '常见浏览器的标准安装位置（ProgramFiles / ProgramFilesX86 / LocalAppData 都查）
        Dim candidates As (Name As String, RelPath As String)() = {
            ("Microsoft Edge", "Microsoft\Edge\Application\msedge.exe"),
            ("Google Chrome", "Google\Chrome\Application\chrome.exe"),
            ("Mozilla Firefox", "Mozilla Firefox\firefox.exe"),
            ("Brave", "BraveSoftware\Brave-Browser\Application\brave.exe"),
            ("Vivaldi", "Vivaldi\Application\vivaldi.exe"),
            ("Opera", "Programs\Opera\opera.exe"),
            ("360 安全浏览器", "360Chrome\Chrome\Application\360chrome.exe"),
            ("QQ 浏览器", "Tencent\QQBrowser\QQBrowser.exe")
        }

        Dim roots As String() = {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        }

        For Each item In candidates
            For Each root In roots
                If String.IsNullOrWhiteSpace(root) Then Continue For
                Try
                    Dim full As String = IO.Path.Combine(root, item.RelPath)
                    If FileExistsSafe(full) AndAlso Not result.Any(Function(x) x.Path.Equals(full, StringComparison.OrdinalIgnoreCase)) Then
                        result.Add(New BrowserInfo(item.Name, full))
                    End If
                Catch ex As Exception
                    '单个候选失败不影响整体探测
                    Logger.Warn($"DSH：探测浏览器时跳过 {item.Name}（{ex.Message}）")
                End Try
            Next
        Next

        Logger.Info($"DSH：浏览器探测完成，找到 {result.Count} 个")
        Return result
    End Function

#End Region

End Module
