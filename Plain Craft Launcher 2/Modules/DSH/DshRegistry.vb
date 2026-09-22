Imports System.Net.Http
Imports Newtonsoft.Json.Linq

''' <summary>
''' dsh 的 npm 版本目录 —— 「镜像版本」页的数据源。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 数据来源
''' ═══════════════════════════════════════════════════════════════════════
''' <c>GET {registry}/@deepseek-ai%2Fdsh</c>
'''
''' 返回的 JSON 里我们只关心三段：
'''   - <c>versions{}</c> —— 版本号 → 该版本的 package.json（含 <c>dist.tarball</c>）
'''   - <c>time{}</c>     —— 版本号 → 发布日期（另有 <c>created</c> / <c>modified</c> 两个特殊键）
'''   - <c>dist-tags</c>  —— 例如 <c>latest</c> → 具体版本号
'''
''' 实测（2026-09）官方源共 22 个版本，且
''' npm 官方 / npmmirror / 腾讯云镜像三者返回完全一致 —— 所以换源只影响下载速度，
''' 不影响可选项。
'''
''' ⚠️ 这个接口**不需要认证、也没有配额限制**（不同于 GitHub 的搜索 API），
''' 所以不必做缓存，用户点刷新就真刷。
''' </summary>
Public Module DshRegistry

#Region "镜像源"

    ''' <summary>一个 npm 注册表镜像。</summary>
    Public Class DshMirror
        ''' <summary>显示名。</summary>
        Public Property Name As String
        ''' <summary>注册表根地址（不带尾部斜杠）。</summary>
        Public Property Url As String
        ''' <summary>给用户看的一句话说明。</summary>
        Public Property Note As String

        Public Sub New(Name As String, Url As String, Note As String)
            Me.Name = Name
            Me.Url = Url
            Me.Note = Note
        End Sub
    End Class

    ''' <summary>内置的镜像源列表。顺序即设置页里的顺序。</summary>
    Public ReadOnly Property DshNpmMirrors As List(Of DshMirror)
        Get
            Return New List(Of DshMirror) From {
                New DshMirror("npm 官方源", "https://registry.npmjs.org", "最权威，国内访问可能较慢"),
                New DshMirror("npmmirror（淘宝）", "https://registry.npmmirror.com", "国内速度快，与官方同步"),
                New DshMirror("腾讯云镜像", "https://mirrors.cloud.tencent.com/npm", "国内速度快，与官方同步")
            }
        End Get
    End Property

    ''' <summary>当前选中的镜像源索引。</summary>
    Public Property DshCurrentMirrorIndex As Integer
        Get
            Try
                Dim idx As Integer = Settings.Get(Of Integer)("DshNpmMirror")
                Dim mirrors = DshNpmMirrors
                If idx < 0 OrElse idx >= mirrors.Count Then Return 0
                Return idx
            Catch ex As Exception
                Logger.Warn($"DSH：读取 npm 镜像设置失败，回退官方源：{ex.Message}")
                Return 0
            End Try
        End Get
        Set(value As Integer)
            Try
                Settings.Set("DshNpmMirror", value)
            Catch ex As Exception
                Logger.Error(ex, "DSH：保存 npm 镜像设置失败")
            End Try
        End Set
    End Property

    ''' <summary>当前选中的镜像源。</summary>
    Public ReadOnly Property DshCurrentMirror As DshMirror
        Get
            Dim mirrors = DshNpmMirrors
            Dim idx As Integer = DshCurrentMirrorIndex
            If idx < 0 OrElse idx >= mirrors.Count Then Return mirrors(0)
            Return mirrors(idx)
        End Get
    End Property

    ''' <summary>
    ''' 当前镜像源的 URL；第一个镜像（官方源）返回 Nothing。
    ''' </summary>
    ''' <remarks>
    ''' 之所以要专门返回 Nothing 而不是直接把官方源 URL 传下去：
    ''' <see cref="DshInstaller.InstallDshVersion"/> 把 <c>RegistryUrl = Nothing</c>
    ''' 解释为「用 pnpm 默认源」。显式传官方 URL 也能跑，但会多写一次
    ''' pnpm 配置，且在用户自己改过 .npmrc 的场景下反而更不可控。
    ''' </remarks>
    Public ReadOnly Property DshCurrentMirrorUrl As String
        Get
            Dim m = DshCurrentMirror
            If m Is Nothing Then Return Nothing
            ' 官方源就是 pnpm 默认值，传 Nothing 让它走默认
            If m.Url Is Nothing OrElse m.Url.StartsWith("https://registry.npmjs.org", StringComparison.OrdinalIgnoreCase) Then
                Return Nothing
            End If
            Return m.Url
        End Get
    End Property

#End Region

#Region "数据模型"

    ''' <summary>一个可安装的 dsh 版本。</summary>
    Public Class DshVersionInfo
        ''' <summary>版本号，如 0.1.6-alpha.2。</summary>
        Public Property Version As String
        ''' <summary>发布日期（源里没有该字段时为 Nothing）。</summary>
        Public Property PublishedAt As DateTime?
        ''' <summary>
        ''' 是否是 <c>dist-tags.latest</c> 指向的版本。
        ''' </summary>
        ''' <remarks>
        ''' ⚠️ 这不等于「版本号最大」。
        ''' npm 的 <c>latest</c> 标签**不会**指向预发布版本，所以当一个包的
        ''' 稳定线还停在 0.1.5-rc.2、而预发布线已经跑到 0.1.6-alpha.2 时，
        ''' <c>latest</c> 仍然是 0.1.5-rc.2 —— 比已安装的版本还旧。
        ''' 所以这里叫「稳定版」，另外用 <see cref="IsNewest"/> 表示「版本号最大」。
        ''' </remarks>
        Public Property IsLatest As Boolean
        ''' <summary>是否是版本号最大的那个（按 semver 比较）。</summary>
        Public Property IsNewest As Boolean
        ''' <summary>tarball 地址。</summary>
        Public Property Tarball As String

        ''' <summary>是否是预发布版本（版本号里带 -alpha / -beta / -rc 之类）。</summary>
        Public ReadOnly Property IsPrerelease As Boolean
            Get
                If String.IsNullOrWhiteSpace(Version) Then Return False
                Return Version.Contains("-")
            End Get
        End Property

        ''' <summary>给用户看的发布日期文案。</summary>
        Public ReadOnly Property PublishedText As String
            Get
                If PublishedAt Is Nothing Then Return "未知日期"
                Return CType(PublishedAt, DateTime).ToString("yyyy-MM-dd")
            End Get
        End Property
    End Class

#End Region

#Region "版本比较"

    ''' <summary>
    ''' 按 semver 比较两个版本号。
    ''' </summary>
    ''' <returns>Left 较新返回正数，相同返回 0，Right 较新返回负数。</returns>
    ''' <remarks>
    ''' 为什么不用 PCL 现成的 <c>CompareVersion</c>：
    '''   1. 它是给 Minecraft 版本号（<c>1.14 Pre-Release 2</c> 之类）写的启发式解析，
    '''      对 npm 的严格 semver 并不精确
    '''   2. 配套的 <c>CompareVersionGE</c> 返回的是 **Boolean**，
    '''      直接塞给 <c>List.Sort</c> 的比较器会被隐式转成 -1/0，
    '''      产生不自洽的顺序（实测把最老的版本排到了最前面）
    ''' 而且 DSH 模块本来就应该不依赖 Minecraft 代码，自己实现更干净。
    ''' </remarks>
    Public Function CompareSemver(Left As String, Right As String) As Integer
        Dim l As Semver = ParseSemver(Left)
        Dim r As Semver = ParseSemver(Right)

        '解析失败的兜底：退化成字符串比较，至少顺序稳定
        If l Is Nothing OrElse r Is Nothing Then
            Return String.CompareOrdinal(If(Left, ""), If(Right, ""))
        End If

        For i As Integer = 0 To 2
            Dim c As Integer = l.Numbers(i).CompareTo(r.Numbers(i))
            If c <> 0 Then Return c
        Next

        '主体相同 → 有预发布标识的**更小**（semver 规则：1.0.0-alpha < 1.0.0）
        If l.PreRelease.Length = 0 AndAlso r.PreRelease.Length = 0 Then Return 0
        If l.PreRelease.Length = 0 Then Return 1
        If r.PreRelease.Length = 0 Then Return -1

        '两边都有预发布标识：逐个标识比较
        Dim n As Integer = Math.Max(l.PreRelease.Length, r.PreRelease.Length)
        For i As Integer = 0 To n - 1
            '标识少的更小（1.0.0-alpha < 1.0.0-alpha.1）
            If i >= l.PreRelease.Length Then Return -1
            If i >= r.PreRelease.Length Then Return 1

            Dim a As String = l.PreRelease(i)
            Dim b As String = r.PreRelease(i)

            Dim an As Integer, bn As Integer
            Dim aIsNum As Boolean = Integer.TryParse(a, an)
            Dim bIsNum As Boolean = Integer.TryParse(b, bn)

            If aIsNum AndAlso bIsNum Then
                If an <> bn Then Return an.CompareTo(bn)
            ElseIf aIsNum Then
                '数字标识 < 字母标识
                Return -1
            ElseIf bIsNum Then
                Return 1
            Else
                Dim c As Integer = String.CompareOrdinal(a, b)
                If c <> 0 Then Return c
            End If
        Next

        Return 0
    End Function

    ''' <summary>解析后的 semver 片段。</summary>
    Private Class Semver
        Public Property Numbers As Integer() = {0, 0, 0}
        Public Property PreRelease As String() = {}
    End Class

    ''' <summary>把 <c>major.minor.patch-pre.release+build</c> 拆开。解析失败返回 Nothing。</summary>
    Private Function ParseSemver(Version As String) As Semver
        If String.IsNullOrWhiteSpace(Version) Then Return Nothing

        Dim s As String = Version.Trim()
        '去掉构建元数据（+ 之后的部分）
        Dim plus As Integer = s.IndexOf("+"c)
        If plus >= 0 Then s = s.Substring(0, plus)

        Dim result As New Semver()

        '预发布标识（第一个 - 之后的部分）
        Dim dash As Integer = s.IndexOf("-"c)
        If dash >= 0 Then
            result.PreRelease = s.Substring(dash + 1).Split("."c)
            s = s.Substring(0, dash)
        End If

        Dim parts As String() = s.Split("."c)
        If parts.Length = 0 Then Return Nothing

        Dim nums As Integer() = {0, 0, 0}
        For i As Integer = 0 To Math.Min(2, parts.Length - 1)
            Dim v As Integer
            If Not Integer.TryParse(parts(i), v) Then Return Nothing
            nums(i) = v
        Next
        result.Numbers = nums

        Return result
    End Function

#End Region

#Region "查询"

    ''' <summary>
    ''' 拉取全部可用版本（按版本号降序，最新在前）。
    ''' </summary>
    ''' <param name="MirrorUrl">注册表地址；Nothing = 从当前选中的源开始，失败自动换源。</param>
    ''' <param name="TimeoutMs">单次请求的超时（毫秒）。</param>
    ''' <exception cref="InvalidOperationException">所有镜像源都失败。</exception>
    ''' <remarks>阻塞调用，请放在后台线程。</remarks>
    Public Function FetchVersions(Optional MirrorUrl As String = Nothing,
                                  Optional TimeoutMs As Integer = 25000) As List(Of DshVersionInfo)
        Dim attempts As List(Of DshMirror) = BuildMirrorAttempts(MirrorUrl)

        Dim lastError As Exception = Nothing
        Dim errors As New List(Of String)

        For Each mirror In attempts
            Try
                Dim url As String = BuildPackageUrl(mirror.Url)
                Logger.Info($"DSH：查询 dsh 版本列表 → {url}")

                Dim body As String = HttpGetString(url, TimeoutMs, "application/json")
                Dim result As List(Of DshVersionInfo) = ParseVersions(body)

                DshLastFetchMirrorName = mirror.Name
                If Not String.Equals(mirror.Url, DshCurrentMirror.Url, StringComparison.OrdinalIgnoreCase) then
                    '选中的源不通，换了别的源 —— 值得记一条警告，排查网络问题时是重要线索
                    Logger.Warn($"DSH：镜像源「{DshCurrentMirror.Name}」不可用，已自动改用「{mirror.Name}」")
                End If

                Return result
            Catch ex As Exception
                lastError = ex
                errors.Add($"{mirror.Name}：{DescribeNetworkError(ex)}")
                Logger.Warn($"DSH：镜像源「{mirror.Name}」查询失败：{ex.Message}")
            End Try
        Next

        Throw New InvalidOperationException(
            "所有镜像源都连接失败。" & vbCrLf & vbCrLf &
            String.Join(vbCrLf, errors) & vbCrLf & vbCrLf &
            "请检查网络或代理设置。如果只是某一个源不通，可以在下方换一个源再试。", lastError)
    End Function

    ''' <summary>
    ''' 构造「要依次尝试的镜像源」列表。
    ''' </summary>
    ''' <param name="ExplicitUrl">用户显式指定的地址；Nothing 表示按当前选中项优先。</param>
    ''' <remarks>
    ''' 指定的源排第一，其余源作为回退依次排在后面。
    ''' 三个源的版本列表完全一致（实测），所以换源只是换条路，不会换到不同的数据。
    ''' </remarks>
    Private Function BuildMirrorAttempts(ExplicitUrl As String) As List(Of DshMirror)
        Dim result As New List(Of DshMirror)

        If Not String.IsNullOrWhiteSpace(ExplicitUrl) Then
            result.Add(New DshMirror("指定源", ExplicitUrl.TrimEnd("/"c), ""))
            Return result
        End If

        Dim current As DshMirror = DshCurrentMirror
        result.Add(current)
        For Each m In DshNpmMirrors
            If Not String.Equals(m.Url, current.Url, StringComparison.OrdinalIgnoreCase) Then result.Add(m)
        Next

        Return result
    End Function

    ''' <summary>拼出某个源的包元数据地址。</summary>
    Private Function BuildPackageUrl(RegistryUrl As String) As String
        Return $"{RegistryUrl.TrimEnd("/"c)}/{ModDSH.DshPackageName.Replace("/", "%2F")}"
    End Function

    ''' <summary>把网络异常翻译成人话（用于错误提示）。</summary>
    Public Function DescribeNetworkError(ex As Exception) As String
        If ex Is Nothing Then Return "未知错误"

        'AggregateException 是 GetAwaiter().GetResult() 的常见包装
        Dim inner As Exception = If(TypeOf ex Is AggregateException, CType(ex, AggregateException).GetBaseException(), ex)

        Select Case True
            Case TypeOf inner Is TaskCanceledException, TypeOf inner Is OperationCanceledException
                Return "请求超时"
            Case TypeOf inner Is Net.WebException
                Return $"网络错误（{CType(inner, Net.WebException).Status}）"
            Case TypeOf inner Is HttpRequestException
                Return $"连接失败：{inner.Message}"
            Case TypeOf inner Is Net.Sockets.SocketException
                Return $"套接字错误：{inner.Message}"
            Case Else
                Return inner.Message
        End Select
    End Function

    ''' <summary>解析注册表返回的包元数据。</summary>
    Private Function ParseVersions(Body As String) As List(Of DshVersionInfo)
        Dim root As JObject
        Try
            root = JObject.Parse(Body)
        Catch ex As Exception
            Throw New InvalidOperationException($"镜像源返回的内容不是合法的 JSON：{ex.Message}", ex)
        End Try

        Dim versionsObj = TryCast(root("versions"), JObject)
        If versionsObj Is Nothing Then
            Throw New InvalidOperationException("镜像源返回的内容里没有 versions 字段。")
        End If

        'dist-tags.latest
        Dim latest As String = Nothing
        Dim tags = TryCast(root("dist-tags"), JObject)
        If tags IsNot Nothing Then latest = tags("latest")?.ToString()

        'time{} 用于拿发布日期
        Dim timeObj = TryCast(root("time"), JObject)

        Dim result As New List(Of DshVersionInfo)
        For Each prop As KeyValuePair(Of String, JToken) In versionsObj
            Dim ver As String = prop.Key
            If String.IsNullOrWhiteSpace(ver) Then Continue For

            Dim info As New DshVersionInfo With {
                .Version = ver,
                .IsLatest = String.Equals(ver, latest, StringComparison.OrdinalIgnoreCase)
            }

            Dim pkg = TryCast(prop.Value, JObject)
            If pkg IsNot Nothing Then
                info.Tarball = pkg("dist")?("tarball")?.ToString()
            End If

            If timeObj IsNot Nothing Then
                Dim rawTime As String = timeObj(ver)?.ToString()
                Dim dt As DateTime
                If Not String.IsNullOrWhiteSpace(rawTime) AndAlso
                   DateTime.TryParse(rawTime, Globalization.CultureInfo.InvariantCulture,
                                     Globalization.DateTimeStyles.RoundtripKind, dt) Then
                    info.PublishedAt = dt
                End If
            End If

            result.Add(info)
        Next

        '按 semver 降序（最新在前）。
        '⚠️ 必须用 CompareSemver 而不是 PCL 的 CompareVersionGE ——
        '   后者返回 Boolean，当作 List.Sort 的比较器会被隐式转成 -1/0，
        '   产生不自洽的顺序（实测把 0.0.1-rc.1 排到了最前面）。
        result.Sort(Function(a, b) -CompareSemver(a.Version, b.Version))

        '标记版本号最大的那个。注意这与 dist-tags.latest（稳定版）**不是一回事**。
        If result.Count > 0 Then result(0).IsNewest = True

        Logger.Info($"DSH：拿到 {result.Count} 个版本，最新={If(result.Count > 0, result(0).Version, "?")}，稳定版={If(latest, "未知")}")
        Return result
    End Function

    ''' <summary>最近一次成功查询用的是哪个镜像源（给界面显示用）。</summary>
    Public Property DshLastFetchMirrorName As String = Nothing

    ''' <summary>
    ''' 查询当前注册表里 <c>latest</c> 指向的版本号（即「稳定版」）。
    ''' </summary>
    Public Function FetchLatestVersion(Optional MirrorUrl As String = Nothing,
                                       Optional TimeoutMs As Integer = 25000) As String
        Dim all As List(Of DshVersionInfo) = FetchVersions(MirrorUrl, TimeoutMs)
        Dim latest = all.FirstOrDefault(Function(v) v.IsLatest)
        If latest IsNot Nothing Then Return latest.Version
        '没有 dist-tags 就退化成"版本号最大的"
        Return If(all.Count > 0, all(0).Version, Nothing)
    End Function

    ''' <summary>
    ''' 按**当前更新通道**从列表里挑出「该装的那个版本」。
    ''' </summary>
    ''' <param name="Versions">已拉取的版本列表。</param>
    ''' <returns>目标版本；列表为空时返回 Nothing。</returns>
    ''' <remarks>
    ''' 通道语义（见 <see cref="ModDSH.DshChannel"/>）：
    '''   · 稳定 → 优先 <c>dist-tags.latest</c>；没有 dist-tags 时退化成
    '''     「**非预发布**且版本号最大」；连非预发布都没有才回落到版本号最大
    '''   · 预览 → 版本号最大的那个（含 pre-release）
    '''
    ''' ⚠️ 「稳定」通道必须**显式排除预发布**，不能只信 <c>IsLatest</c>：
    ''' npm 的 <c>latest</c> 标签虽然按约定不指向预发布，但一个包**完全只有
    ''' 预发布版本**时（dsh 的早期阶段就是这样）latest 也会落到预发布上。
    ''' 只判 <c>IsLatest</c> 会让「稳定通道」装上一个 alpha 版。
    ''' </remarks>
    Public Function ResolveVersionForChannel(Versions As List(Of DshVersionInfo),
                                             Channel As ModDSH.DshChannel) As DshVersionInfo
        If Versions Is Nothing OrElse Versions.Count = 0 Then Return Nothing

        If Channel = ModDSH.DshChannel.Preview Then
            '预览：版本号最大的（IsNewest 由 ParseVersions 标好），没有标记就取第一个
            Return If(Versions.FirstOrDefault(Function(v) v.IsNewest), Versions(0))
        End If

        '稳定：先看 dist-tags.latest，但必须不是预发布
        Dim latest = Versions.FirstOrDefault(Function(v) v.IsLatest)
        If latest IsNot Nothing AndAlso Not latest.IsPrerelease Then Return latest

        '回落到「非预发布且版本号最大」。列表已按版本号降序，取第一个即可。
        Dim stableOnly = Versions.FirstOrDefault(Function(v) Not v.IsPrerelease)
        If stableOnly IsNot Nothing Then Return stableOnly

        '整个包只有预发布版本 —— 没有稳定版可装，如实回落到最新预发布，
        '由调用方在界面上提醒用户（而不是返回 Nothing 让按钮失灵）
        Return If(Versions.FirstOrDefault(Function(v) v.IsNewest), Versions(0))
    End Function

    ''' <summary>当前已安装的 dsh 版本。</summary>
    Public ReadOnly Property DshInstalledVersion As String
        Get
            Return DshRuntime.GetInstalledDshVersion()
        End Get
    End Property

#End Region

#Region "HTTP"

    Private ReadOnly HttpSync As New Object()
    Private _Http As HttpClient = Nothing

    ''' <summary>
    ''' 全模块共享的 <see cref="HttpClient"/>。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 每次请求都 <c>New HttpClient</c> 是 .NET Framework 上的经典反模式，
    ''' 在这里是**实测踩过的坑**：
    '''   - 每次都会新建连接组、重新做 TCP + TLS 握手
    '''   - 配合 PCL 在启动时设的全局 <c>ServicePointManager.ReusePort = True</c>
    '''     （它给外出 socket 打开 SO_REUSEADDR），对同一个 host 连续快速建连会偶发失败
    '''   - 现象很迷惑：**第一次成功，第二次开始报"无法连接"**，
    '''     而同一时刻用 Python 请求同一个 URL 完全正常
    ''' 改成共享实例后连接被 keep-alive 复用，不再反复建连，问题消失。
    ''' </remarks>
    Public ReadOnly Property DshHttpClient As HttpClient
        Get
            If _Http IsNot Nothing Then Return _Http
            SyncLock HttpSync
                If _Http Is Nothing Then
                    Dim handler As New HttpClientHandler With {
                        .AllowAutoRedirect = True,
                        .AutomaticDecompression = Net.DecompressionMethods.GZip Or Net.DecompressionMethods.Deflate,
                        .UseCookies = False
                    }
                    Dim c As New HttpClient(handler) With {.Timeout = TimeSpan.FromSeconds(60)}
                    ' 有些镜像/网关会拦没有 UA 的请求
                    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PCL-DSH/1.0")
                    _Http = c
                End If
            End SyncLock
            Return _Http
        End Get
    End Property

    ''' <summary>发一个 GET 并把响应体读成字符串。</summary>
    ''' <param name="Url">完整地址。</param>
    ''' <param name="TimeoutMs">本次请求的超时（毫秒）。用 CTS 实现，不动共享客户端的全局超时。</param>
    ''' <param name="Accept">Accept 头。Nothing 表示不设置。</param>
    ''' <exception cref="InvalidOperationException">网络失败或非 2xx 响应。</exception>
    Public Function HttpGetString(Url As String, TimeoutMs As Integer,
                                  Optional Accept As String = Nothing) As String
        Using cts As New Threading.CancellationTokenSource(Math.Max(1000, TimeoutMs))
            Using req As New HttpRequestMessage(HttpMethod.Get, Url)
                If Not String.IsNullOrWhiteSpace(Accept) Then
                    req.Headers.TryAddWithoutValidation("Accept", Accept)
                End If

                Using resp = DshHttpClient.SendAsync(req, cts.Token).GetAwaiter().GetResult()
                    Dim body As String = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    If Not resp.IsSuccessStatusCode Then
                        Throw New InvalidOperationException(
                            $"HTTP {CInt(resp.StatusCode)}　{Truncate(body, 200)}")
                    End If
                    Return body
                End Using
            End Using
        End Using
    End Function

    Private Function Truncate(Text As String, Max As Integer) As String
        If String.IsNullOrEmpty(Text) Then Return ""
        If Text.Length <= Max Then Return Text
        Return Text.Substring(0, Max) & "…"
    End Function

#End Region

End Module
