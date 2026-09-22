Imports System.Diagnostics
Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports Newtonsoft.Json.Linq

''' <summary>
''' PCL_DSH 网络工具箱 —— GitHub / npm 的加速与诊断。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 这套方案是怎么来的
''' ═══════════════════════════════════════════════════════════════════════
''' 用户要求「按 SteamTools（Watt Toolkit）的做法做加速」。逆向下来它的手段是：
'''   本地反向代理（YARP）+ 自签根证书（MITM）+ SNI 重写 + DNS 拦截 + 改系统代理。
'''
''' 但本工程是 **.NET Framework 4.8 + 老式 vbproj + 无 NuGet**
''' （只有裸 &lt;Reference&gt;，没有 PackageReference / packages.config），
''' <c>YARP.ReverseProxy</c> 根本拿不到。要用 BCL 自研完整 MITM 反代的话，
''' 得自己实现 TLS 终止 + 装根证书 + 改系统代理，代价是：
'''   - 需要管理员权限
'''   - 要在用户机器上装一张根证书（等于把中间人的钥匙交出去）
'''   - 会与 VPN / 其它代理软件冲突
'''   - 一旦证书出问题，用户所有 HTTPS 都会报警告
'''
''' 而**针对 GitHub 这个具体场景**，其实有更划算的做法：
''' SteamTools 的 GitHub 加速主力手段本来就是 **DNS/域名 IP 优选** ——
''' 因为 GitHub 的 CDN 节点很多，国内不同 ISP 能连通的节点差异极大，
''' 把域名指到「当前网络真正连得快的那个 IP」就能显著提速。
''' 这**不需要证书、不需要改系统代理**，只需要改 hosts。
'''
''' 所以本模块提供的能力是：
'''   1. 多源 DNS 解析（DoH + 系统 DNS）拿到候选 IP 池
'''   2. 对每个 IP 做 TCP 握手测速，挑出最快的
'''   3. 把优选结果写进 hosts 的**受管区块**（可一键还原）
'''   4. 镜像站测速与跳转（反代 / jsDelivr 之类）
'''   5. 全链路网络诊断（DNS → TCP → TLS → HTTP），指出卡在哪一步
'''
''' ═══════════════════════════════════════════════════════════════════════
''' hosts 操作的安全约定（**必须遵守**）
''' ═══════════════════════════════════════════════════════════════════════
'''   - 只动 <see cref="BlockBegin"/> 与 <see cref="BlockEnd"/> 之间的行，
'''     区块外的任何内容**一个字节都不碰**
'''   - 首次修改前先备份原文件到 <see cref="HostsBackupFile"/>
'''   - 提供一键还原（删掉受管区块，其余原样）
'''   - 写入走「临时文件 + 提权 cmd copy」，只弹一次 UAC，不重启 PCL
''' </summary>
Public Module DshNetTool

#Region "路径与常量"

    ''' <summary>系统 hosts 文件。</summary>
    Public ReadOnly Property HostsFile As String
        Get
            Return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers\etc\hosts")
        End Get
    End Property

    ''' <summary>首次修改前的原始 hosts 备份。</summary>
    Public ReadOnly Property HostsBackupFile As String
        Get
            Return ModDSH.DshRoot & "hosts.backup.txt"
        End Get
    End Property

    ''' <summary>受管区块起始标记。</summary>
    Public Const BlockBegin As String =
        "# >>> PCL DSH 加速 开始（本段由 PCL DSH 改版自动维护，请勿手动修改） >>>"

    ''' <summary>受管区块结束标记。</summary>
    Public Const BlockEnd As String =
        "# <<< PCL DSH 加速 结束 <<<"

    ''' <summary>当前进程是否已提权。</summary>
    Public ReadOnly Property IsElevated As Boolean
        Get
            Try
                Return WindowsUtils.HasAdminRole()
            Catch ex As Exception
                Logger.Warn($"DSH：检测管理员权限失败：{ex.Message}")
                Return False
            End Try
        End Get
    End Property

#End Region

#Region "加速目标"

    ''' <summary>一个可以走 IP 优选的域名。</summary>
    Public Class AccelTarget
        ''' <summary>域名。</summary>
        Public Property Host As String
        ''' <summary>给用户看的说明。</summary>
        Public Property Note As String
        ''' <summary>该域名用什么端口测速。</summary>
        Public Property Port As Integer = 443
        ''' <summary>用户是否勾选了它。</summary>
        Public Property Enabled As Boolean = True

        Public Sub New(Host As String, Note As String, Optional Port As Integer = 443)
            Me.Host = Host
            Me.Note = Note
            Me.Port = Port
        End Sub
    End Class

    ''' <summary>
    ''' 默认的加速目标。
    ''' </summary>
    ''' <remarks>
    ''' 这几个是 GitHub 全链路里最常被卡住的域名：
    '''   - github.com / api.github.com —— 网页与 API
    '''   - raw.githubusercontent.com —— 读仓库里的单个文件（插件市场解析 package.json 靠它）
    '''   - objects.githubusercontent.com —— Release / LFS 的实体文件
    '''   - codeload.github.com —— 下载 zip / tarball
    '''   - registry.npmjs.org —— dsh 版本列表与 pnpm 装包
    ''' </remarks>
    Public ReadOnly Property DefaultTargets As List(Of AccelTarget)
        Get
            Return New List(Of AccelTarget) From {
                New AccelTarget("github.com", "GitHub 主站（网页、clone）"),
                New AccelTarget("api.github.com", "GitHub API（插件市场搜索走这里）"),
                New AccelTarget("raw.githubusercontent.com", "读取仓库原始文件"),
                New AccelTarget("objects.githubusercontent.com", "Release / LFS 实体文件"),
                New AccelTarget("codeload.github.com", "下载仓库 zip / tarball"),
                New AccelTarget("registry.npmjs.org", "npm 注册表（dsh 版本与依赖）")
            }
        End Get
    End Property

#End Region

#Region "DNS 解析"

    ''' <summary>
    ''' 用 DNS over HTTPS 解析一个域名的 A 记录。
    ''' </summary>
    ''' <param name="Host">域名。</param>
    ''' <param name="TimeoutMs">超时（毫秒）。</param>
    ''' <returns>去重后的 IPv4 列表；失败返回空列表。</returns>
    ''' <remarks>
    ''' 为什么用 DoH 而不是 <see cref="Dns.GetHostAddresses"/>：
    '''   1. 系统 DNS 往往只返回一个 IP（运营商就近调度），
    '''      而 GitHub 的 CDN 有很多节点，只拿一个就无从优选
    '''   2. 系统 DNS 结果有缓存，反复调用拿到的永远一样，测速没意义
    '''   3. 不同 DoH 供应商的调度结果不同，**多问几家再汇总**才能凑出足够多的候选
    ''' </remarks>
    Public Function ResolveViaDoh(Host As String, Optional TimeoutMs As Integer = 5000) As List(Of String)
        Dim result As New List(Of String)

        '⚠️ 只列**实测能通**的三家。Google 的 dns.google 与 Quad9 在这里都是 20 秒超时，
        '   留着只会让每个域名白白多等十几秒。要加就先自己测一遍。
        Dim providers As String() = {
            $"https://223.5.5.5/resolve?name={Host}&type=A",
            $"https://doh.pub/dns-query?name={Host}&type=A",
            $"https://1.1.1.1/dns-query?name={Host}&type=A"
        }

        '⚠️ 必须**并行**问。
        '   串行的话总耗时是三家之和 —— 实测网络诊断里 DNS 一步就花了 8230 ms，
        '   而单家其实只要 300~1700 ms（有一家超时就把整体拖长了）。
        '   并行后总耗时≈最慢的那一家。
        Dim lockObj As New Object()
        Dim threads As New List(Of Threading.Thread)

        For Each url In providers
            Dim captured As String = url
            Dim t As New Threading.Thread(
                Sub()
                    Try
                        Dim body As String = DshRegistry.HttpGetString(captured, TimeoutMs, "application/dns-json")
                        Dim root As JObject = JObject.Parse(body)
                        Dim answers = TryCast(root("Answer"), JArray)
                        If answers Is Nothing Then Return

                        SyncLock lockObj
                            For Each item In answers
                                Dim o = TryCast(item, JObject)
                                If o Is Nothing Then Continue For
                                'type 1 = A 记录（CNAME 是 5，别收进来）
                                If If(o("type") Is Nothing, 0, CInt(o("type"))) <> 1 Then Continue For
                                Dim ip As String = o("data")?.ToString()
                                If String.IsNullOrWhiteSpace(ip) Then Continue For
                                ip = ip.Trim()
                                If Not IsValidIPv4(ip) Then Continue For
                                If Not result.Contains(ip) Then result.Add(ip)
                            Next
                        End SyncLock
                    Catch ex As Exception
                        '单个 DoH 供应商失败不影响整体 —— 反正还有别的家和系统 DNS
                        Logger.Warn($"DSH：DoH 查询失败（{captured}）：{ex.Message}")
                    End Try
                End Sub)
            t.IsBackground = True
            t.Start()
            threads.Add(t)
        Next

        For Each t In threads
            t.Join(TimeoutMs + 1000)
        Next

        Logger.Info($"DSH：DoH 解析 {Host} → {result.Count} 个候选 IP")
        Return result
    End Function

    ''' <summary>把系统 DNS 的结果也并进候选池。</summary>
    Public Function ResolveViaSystem(Host As String) As List(Of String)
        Dim result As New List(Of String)
        Try
            For Each addr In Dns.GetHostAddresses(Host)
                '只要 IPv4 —— hosts 文件对 IPv6 的写法容易踩坑，而且 GitHub 的 v6 大多不通
                If addr.AddressFamily = AddressFamily.InterNetwork Then
                    Dim s As String = addr.ToString()
                    If Not result.Contains(s) Then result.Add(s)
                End If
            Next
        Catch ex As Exception
            Logger.Warn($"DSH：系统 DNS 解析 {Host} 失败：{ex.Message}")
        End Try
        Return result
    End Function

    ''' <summary>
    ''' 汇总一个域名的全部候选 IP（DoH + 系统 DNS）。
    ''' </summary>
    Public Function ResolveCandidates(Host As String, Optional TimeoutMs As Integer = 8000) As List(Of String)
        Dim result As List(Of String) = ResolveViaDoh(Host, TimeoutMs)
        For Each ip In ResolveViaSystem(Host)
            If Not result.Contains(ip) Then result.Add(ip)
        Next
        Return result
    End Function

    Private Function IsValidIPv4(Text As String) As Boolean
        Dim parts As String() = Text.Split("."c)
        If parts.Length <> 4 Then Return False
        For Each p In parts
            Dim v As Integer
            If Not Integer.TryParse(p, v) Then Return False
            If v < 0 OrElse v > 255 Then Return False
        Next
        Return True
    End Function

#End Region

#Region "TCP 测速"

    ''' <summary>一次 TCP 握手测速的结果。</summary>
    Public Class TcpProbe
        Public Property Ip As String
        ''' <summary>握手成功且返回 True。</summary>
        Public Property Ok As Boolean
        ''' <summary>握手耗时（毫秒）；失败为 -1。</summary>
        Public Property LatencyMs As Integer = -1
        ''' <summary>失败原因。</summary>
        Public Property [Error] As String
    End Class

    ''' <summary>
    ''' 对一个 IP 做 TCP 握手测速。
    ''' </summary>
    ''' <param name="Ip">目标 IP。</param>
    ''' <param name="Port">端口。</param>
    ''' <param name="TimeoutMs">超时（毫秒）。</param>
    ''' <returns>耗时毫秒；失败返回 -1。</returns>
    ''' <remarks>
    ''' 用 TCP 握手而不是 ICMP Ping：
    '''   1. Ping 需要原始套接字，普通权限不一定能用
    '''   2. **很多 CDN 节点不回 ICMP**，Ping 不通但 HTTPS 完全正常 —— 用 Ping 会误杀好节点
    '''   3. 握手耗时才是真正影响体验的量（用户要的是建连快，不是回声快）
    ''' </remarks>
    Public Function ProbeTcp(Ip As String, Port As Integer, TimeoutMs As Integer) As Integer
        Dim sw As Stopwatch = Stopwatch.StartNew()
        Try
            Using client As New TcpClient()
                Dim task = client.ConnectAsync(Ip, Port)
                If Not task.Wait(TimeoutMs) Then
                    '超时：把 socket 关掉让挂起的连接尽快失败，避免泄漏
                    Try
                        client.Close()
                    Catch
                    End Try
                    Return -1
                End If
                sw.Stop()
                If client.Connected Then Return CInt(sw.ElapsedMilliseconds)
                Return -1
            End Using
        Catch ex As Exception
            Return -1
        End Try
    End Function

    ''' <summary>
    ''' 并发地对一批 IP 测速。
    ''' </summary>
    ''' <param name="Ips">候选 IP。</param>
    ''' <param name="Port">端口。</param>
    ''' <param name="TimeoutMs">单个 IP 的超时。</param>
    ''' <param name="MaxParallel">同时进行的连接数上限。</param>
    ''' <returns>按耗时升序（失败的排最后）。</returns>
    ''' <remarks>
    ''' ⚠️ **不能用 <c>Task.Run</c> 逐项丢进线程池**。
    ''' 线程池的线程数增长很慢（大约每 500 ms 才补一个），
    ''' 72 个候选要等它慢慢长到 72 个线程，光排队就是几十秒 ——
    ''' 实测「优选」卡住不动就是这个原因。
    ''' 这里改成**自己起线程**并分批控制并发量：连接是纯 IO 等待，不占 CPU，
    ''' 48 路并发既快又不会把对方当成扫描攻击。
    ''' </remarks>
    Public Function ProbeTcpBatch(Ips As List(Of String), Port As Integer,
                                  Optional TimeoutMs As Integer = 3000,
                                  Optional MaxParallel As Integer = 48) As List(Of TcpProbe)
        Dim results As New List(Of TcpProbe)
        If Ips Is Nothing OrElse Ips.Count = 0 Then Return results

        Dim lockObj As New Object()
        Dim batchSize As Integer = Math.Max(1, MaxParallel)

        For start As Integer = 0 To Ips.Count - 1 Step batchSize
            Dim batch As List(Of String) = Ips.GetRange(start, Math.Min(batchSize, Ips.Count - start))
            Dim threads As New List(Of Threading.Thread)

            For Each ip In batch
                Dim captured As String = ip
                Dim t As New Threading.Thread(
                    Sub()
                        Dim ms As Integer = ProbeTcp(captured, Port, TimeoutMs)
                        Dim probe As New TcpProbe With {
                            .Ip = captured,
                            .Ok = ms >= 0,
                            .LatencyMs = ms,
                            .[Error] = If(ms >= 0, Nothing, "握手超时或失败")
                        }
                        SyncLock lockObj
                            results.Add(probe)
                        End SyncLock
                    End Sub)
                t.IsBackground = True
                t.Start()
                threads.Add(t)
            Next

            For Each t In threads
                t.Join(TimeoutMs + 2000)
            Next
        Next

        '快的排前面，失败（-1）排最后
        results.Sort(Function(a, b)
                         If a.Ok <> b.Ok Then Return If(a.Ok, -1, 1)
                         Return a.LatencyMs.CompareTo(b.LatencyMs)
                     End Function)

        Return results
    End Function

    ''' <summary>一次「域名 → 优选 IP」的完整结果。</summary>
    Public Class HostOptimization
        Public Property Host As String
        Public Property Port As Integer = 443
        ''' <summary>全部候选（含失败项），按耗时升序。</summary>
        Public Property Probes As New List(Of TcpProbe)
        ''' <summary>候选是怎么来的（给用户看的说明）。</summary>
        Public Property Detail As String
        ''' <summary>已通过 TLS 证书校验的 IP（按耗时升序）。</summary>
        Public Property Verified As New List(Of TcpProbe)

        ''' <summary>最优 IP（已通过 TLS 校验）；一个都没过时为 Nothing。</summary>
        Public ReadOnly Property BestIp As String
            Get
                Dim best = Verified.FirstOrDefault()
                Return If(best Is Nothing, Nothing, best.Ip)
            End Get
        End Property

        Public ReadOnly Property BestLatency As Integer
            Get
                Dim best = Verified.FirstOrDefault()
                Return If(best Is Nothing, -1, best.LatencyMs)
            End Get
        End Property

        Public ReadOnly Property SuccessCount As Integer
            Get
                '⚠️ 不能写 Probes.Count(Function(p) p.Ok) ——
                '   List(Of T) 自带 Count **属性**，VB 会优先绑到它，
                '   然后报 BC32016「Count 没有任何参数」。
                '   加一层 Where 就能强制走 Enumerable.Count 扩展方法。
                Return Probes.Where(Function(p) p.Ok).Count()
            End Get
        End Property
    End Class

    ''' <summary>
    ''' GitHub 网段缓存（避免每个域名都去打一次 /meta）。
    ''' </summary>
    Private _GithubCidrs As List(Of String) = Nothing
    Private _GithubCidrsAt As DateTime = DateTime.MinValue
    Private ReadOnly GithubCidrSync As New Object()

    ''' <summary>取 GitHub 网段（带 6 小时缓存）。</summary>
    Private Function GetGithubCidrsCached() As List(Of String)
        SyncLock GithubCidrSync
            If _GithubCidrs IsNot Nothing AndAlso
               (DateTime.Now - _GithubCidrsAt).TotalHours < 6 Then
                Return _GithubCidrs
            End If
            _GithubCidrs = FetchGithubCidrs()
            _GithubCidrsAt = DateTime.Now
            Return _GithubCidrs
        End SyncLock
    End Function

    ''' <summary>
    ''' 完整地优化一个域名：收集候选 → 并发测速 → TLS 校验 → 给出最优 IP。
    ''' </summary>
    ''' <param name="Target">目标域名。</param>
    ''' <param name="TimeoutMs">单个候选的 TCP 握手超时。</param>
    ''' <param name="VerifyTop">最多尝试校验几个候选。</param>
    ''' <param name="WantVerified">攒够几个通过校验的就可以停。</param>
    ''' <param name="MaxCandidates">候选上限。GitHub 的网段有 60 段，每段采 3 个就有 180 个，
    ''' 全测一遍没必要 —— 采样是均匀的，取前 72 个已经足够覆盖到快节点。</param>
    ''' <remarks>阻塞调用，请放在后台线程。单个域名一般 6~15 秒。</remarks>
    Public Function OptimizeHost(Target As AccelTarget,
                                 Optional TimeoutMs As Integer = 2500,
                                 Optional VerifyTop As Integer = 8,
                                 Optional WantVerified As Integer = 3,
                                 Optional MaxCandidates As Integer = 72) As HostOptimization
        Dim result As New HostOptimization With {
            .Host = Target.Host,
            .Port = Target.Port
        }

        '── 1. 收集候选 ──
        Dim candidates As New List(Of String)
        Dim sources As New List(Of String)
        Dim isGithub As Boolean = IsGithubFamilyHost(Target.Host)

        'GitHub 域名**不走 DoH**：
        '  /meta 网段给出的候选池比 DoH 强得多（DoH 只会返回那唯一一个 A 记录），
        '  而 DoH 一次要 1~4 秒 —— 六个域名就是十几秒的纯浪费。
        '  系统 DNS 顺手取一下（快，且有系统缓存），作为"官方解析结果"的基准。
        If isGithub Then
            Dim sysIps As List(Of String) = ResolveViaSystem(Target.Host)
            For Each ip In sysIps
                If Not candidates.Contains(ip) Then candidates.Add(ip)
            Next
            If sysIps.Count > 0 Then sources.Add($"系统 DNS {sysIps.Count} 个")
        Else
            Dim dnsIps As List(Of String) = ResolveCandidates(Target.Host, 3000)
            For Each ip In dnsIps
                If Not candidates.Contains(ip) Then candidates.Add(ip)
            Next
            If dnsIps.Count > 0 Then sources.Add($"DNS {dnsIps.Count} 个")
        End If

        'GitHub 家族：从官方 /meta 网段采样，这才是候选池的主力
        If isGithub Then
            Dim cidrs As List(Of String) = GetGithubCidrsCached()
            Dim sampled As Integer = 0
            For Each cidr In cidrs
                For Each ip In SampleIpsFromCidr(cidr, 3)
                    If candidates.Count >= MaxCandidates Then Exit For
                    If Not candidates.Contains(ip) Then
                        candidates.Add(ip)
                        sampled += 1
                    End If
                Next
                If candidates.Count >= MaxCandidates Then Exit For
            Next
            If sampled > 0 Then sources.Add($"GitHub 网段采样 {sampled} 个")
        End If

        If candidates.Count = 0 Then
            result.Detail = "没有收集到任何候选 IP"
            Return result
        End If

        result.Detail = $"候选来源：{String.Join("、", sources)}"

        '── 2. 并发 TCP 测速 ──
        result.Probes = ProbeTcpBatch(candidates, Target.Port, TimeoutMs)

        '── 3. TLS 校验 ──
        'TCP 通 ≠ 这个 IP 会为手上的域名提供正确证书，必须验一次。
        '按延迟顺序依次验，**攒够 WantVerified 个就停**：
        '  - 只验前 3 个不够 —— GitHub 的网段里混着只服务 web、不服务 objects/codeload 的 IP，
        '    前 3 个全被拒时就会误报「找不到可用 IP」（实测就是这么踩的）
        '  - 全验也不划算 —— 每个失败要等满 3 秒超时
        '所以用「最多试 VerifyTop 个、攒够 WantVerified 个就收手」来平衡成功率与耗时。
        Dim attempted As Integer = 0
        For Each probe In result.Probes.Where(Function(p) p.Ok)
            If result.Verified.Count >= WantVerified Then Exit For
            If attempted >= VerifyTop Then Exit For
            attempted += 1

            If VerifyIpServesHost(probe.Ip, Target.Host, Target.Port, 3000) Then
                result.Verified.Add(probe)
            End If
        Next

        Return result
    End Function

#End Region

#Region "GitHub 官方网段（候选 IP 的主要来源）"

    ''' <summary>
    ''' GitHub 官方 CDN 网段，从 <c>GET https://api.github.com/meta</c> 拿。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ **这才是候选 IP 的正确来源**，一开始想当然地靠「多问几家 DoH 凑候选」是错的：
    ''' 实测 github.com 现在只有一个 A 记录（20.205.243.166），
    ''' 腾讯 / 阿里 / Cloudflare 三家 DoH 返回的**完全一样**，
    ''' 候选池里只有 1 个 IP —— 「优选」就退化成了摆设。
    '''
    ''' 而 GitHub 的 <c>/meta</c> 接口会给出 web / api / git / pages 四组 CIDR 网段
    ''' （实测 web 40 段 + api 26 段）。这些网段是 Fastly 的 anycast 段，
    ''' **段内绝大多数 IP 都能服务 GitHub**，所以按段采样就能拿到真正可用的候选池。
    ''' 实测：45 个采样里 30 个可连通，最快的 46 ms，比 DNS 给的那个 73 ms 快不少。
    ''' </remarks>
    Public Function FetchGithubCidrs(Optional TimeoutMs As Integer = 15000) As List(Of String)
        Dim result As New List(Of String)
        Try
            Dim body As String = DshRegistry.HttpGetString(
                "https://api.github.com/meta", TimeoutMs, "application/vnd.github+json")
            Dim root As JObject = JObject.Parse(body)

            'web / api / git / pages 全都要：
            '  - github.com        → web
            '  - api.github.com    → api
            '  - raw.githubusercontent.com / *.github.io → pages 与 web 段
            '四组都是同一批 Fastly 段，合并去重即可，不必按域名细分
            For Each key In {"web", "api", "git", "pages"}
                Dim arr = TryCast(root(key), JArray)
                If arr Is Nothing Then Continue For
                For Each item In arr
                    Dim cidr As String = item?.ToString()
                    If String.IsNullOrWhiteSpace(cidr) Then Continue For
                    If Not result.Contains(cidr) Then result.Add(cidr)
                Next
            Next

            Logger.Info($"DSH：GitHub /meta 返回 {result.Count} 个网段")
        Catch ex As Exception
            '拿不到网段不算致命 —— 后面还有 DoH / 系统 DNS 兜底
            Logger.Warn($"DSH：获取 GitHub 网段失败：{ex.Message}")
        End Try
        Return result
    End Function

    ''' <summary>
    ''' 从一段 CIDR 里均匀采样出若干个代表性 IP。
    ''' </summary>
    ''' <param name="Cidr">形如 <c>140.82.112.0/20</c>。</param>
    ''' <param name="MaxPerRange">最多采几个。</param>
    ''' <remarks>
    ''' 不能整段展开：<c>/20</c> 有 4096 个地址、<c>/8</c> 有 1600 万个，
    ''' 全测一遍既慢又会把对方当成扫描攻击。
    ''' 均匀采样（按 stride 跳）比「取前 N 个」好 —— 前 N 个往往集中在同一台边缘设备上，
    ''' 采样结果会失去区分度。
    ''' </remarks>
    Public Function SampleIpsFromCidr(Cidr As String, Optional MaxPerRange As Integer = 3) As List(Of String)
        Dim result As New List(Of String)
        If String.IsNullOrWhiteSpace(Cidr) Then Return result

        Dim slash As Integer = Cidr.IndexOf("/"c)
        If slash < 0 Then Return result

        Dim baseIp As String = Cidr.Substring(0, slash).Trim()
        Dim prefix As Integer
        If Not Integer.TryParse(Cidr.Substring(slash + 1).Trim(), prefix) Then Return result
        '只处理 /8 ~ /32；再宽的段采样命中率太低，没意义
        If prefix < 8 OrElse prefix > 32 Then Return result

        Dim baseVal As ULong = 0
        If Not TryIpToUInt(baseIp, baseVal) Then Return result

        Dim hostBits As Integer = 32 - prefix
        Dim total As ULong = 1UL << hostBits
        If total <= 1 Then
            result.Add(baseIp)
            Return result
        End If
        If total = 2 Then
            '只有网络地址和广播地址，取一个
            result.Add(UIntToIp(baseVal + 1UL))
            Return result
        End If

        Dim usable As ULong = total - 2UL
        Dim take As Integer = CInt(Math.Min(CULng(MaxPerRange), usable))
        Dim stride As ULong = Math.Max(1UL, usable \ CULng(take))

        For i As Integer = 0 To take - 1
            Dim offset As ULong = 1UL + CULng(i) * stride
            If offset >= total - 1UL Then Exit For
            result.Add(UIntToIp(baseVal + offset))
        Next

        Return result
    End Function

    ''' <summary>IP 字符串 → 32 位无符号整数。</summary>
    Private Function TryIpToUInt(Ip As String, ByRef Value As ULong) As Boolean
        Value = 0
        If String.IsNullOrWhiteSpace(Ip) Then Return False
        Dim parts As String() = Ip.Split("."c)
        If parts.Length <> 4 Then Return False

        Dim acc As ULong = 0
        For Each p In parts
            Dim v As Integer
            If Not Integer.TryParse(p, v) Then Return False
            If v < 0 OrElse v > 255 Then Return False
            acc = (acc << 8) Or CULng(v)
        Next
        Value = acc
        Return True
    End Function

    ''' <summary>32 位无符号整数 → IP 字符串。</summary>
    Private Function UIntToIp(Value As ULong) As String
        Return $"{((Value >> 24) And 255UL)}.{((Value >> 16) And 255UL)}.{((Value >> 8) And 255UL)}.{Value And 255UL}"
    End Function

    ''' <summary>
    ''' 判断某个域名是不是 GitHub 家族的（决定要不要用 /meta 网段）。
    ''' </summary>
    Public Function IsGithubFamilyHost(Host As String) As Boolean
        If String.IsNullOrWhiteSpace(Host) Then Return False
        Dim h As String = Host.Trim().ToLowerInvariant()
        Return h = "github.com" OrElse h.EndsWith(".github.com", StringComparison.Ordinal) OrElse
               h.EndsWith(".githubusercontent.com", StringComparison.Ordinal) OrElse
               h.EndsWith(".github.io", StringComparison.Ordinal) OrElse
               h.EndsWith(".githubassets.com", StringComparison.Ordinal)
    End Function

    ''' <summary>
    ''' 用指定 IP + SNI 做一次 TLS 握手，验证这个 IP 确实能服务该域名。
    ''' </summary>
    ''' <param name="Ip">候选 IP。</param>
    ''' <param name="Host">域名（用作 SNI 与证书校验目标）。</param>
    ''' <param name="Port">端口。</param>
    ''' <param name="TimeoutMs">超时。</param>
    ''' <remarks>
    ''' 这一步不能省。TCP 能连上不代表这个 IP 会为你手上的域名提供**正确证书** ——
    ''' Fastly 的 anycast 段里混着很多别的站点，指错了轻则证书告警，
    ''' 重则把请求发到不相干的服务上。
    ''' 用 <c>SslStream.AuthenticateAsClient(Host)</c> 且**不覆盖证书校验回调**，
    ''' 就能同时验证「握手能完成」与「证书匹配该域名」两件事。
    ''' </remarks>
    Public Function VerifyIpServesHost(Ip As String, Host As String, Port As Integer,
                                       Optional TimeoutMs As Integer = 5000) As Boolean
        Try
            Using client As New TcpClient()
                If Not client.ConnectAsync(Ip, Port).Wait(TimeoutMs) Then Return False
                Using stream As Net.Security.SslStream = New Net.Security.SslStream(client.GetStream(), False)
                    stream.ReadTimeout = TimeoutMs
                    stream.WriteTimeout = TimeoutMs
                    stream.AuthenticateAsClient(Host)
                    Return stream.IsAuthenticated
                End Using
            End Using
        Catch ex As Exception
            Return False
        End Try
    End Function

#End Region

#Region "网络诊断"

    ''' <summary>诊断里的一步。</summary>
    Public Class DiagStep
        ''' <summary>步骤名（DNS / TCP / TLS / HTTP）。</summary>
        Public Property Name As String
        Public Property Ok As Boolean
        ''' <summary>耗时（毫秒）；不适用为 -1。</summary>
        Public Property LatencyMs As Integer = -1
        ''' <summary>结果说明。</summary>
        Public Property Detail As String

        Public ReadOnly Property LatencyText As String
            Get
                If LatencyMs < 0 Then Return "—"
                Return $"{LatencyMs} ms"
            End Get
        End Property
    End Class

    ''' <summary>
    ''' 对一个 URL 做全链路诊断。
    ''' </summary>
    ''' <param name="Url">完整 URL（https）。</param>
    ''' <param name="TimeoutMs">每一步的超时。</param>
    ''' <returns>DNS → TCP → TLS → HTTP 四步的结果。</returns>
    ''' <remarks>
    ''' 分步计时的价值在于**指出卡在哪一环**：
    '''   - DNS 慢 → 换 DNS / 用 hosts 固定 IP
    '''   - TCP 慢或失败 → 节点不通，需要 IP 优选
    '''   - TLS 慢 → 握手被干扰（常见于 SNI 阻断）
    '''   - HTTP 慢 → 服务器本身慢或限速
    ''' 只说「连不上」是没用的，用户不知道该改什么。
    ''' </remarks>
    Public Function Diagnose(Url As String, Optional TimeoutMs As Integer = 8000) As List(Of DiagStep)
        Dim steps As New List(Of DiagStep)

        Dim uri As Uri = Nothing
        If Not Uri.TryCreate(Url, UriKind.Absolute, uri) Then
            steps.Add(New DiagStep With {.Name = "URL", .Ok = False, .Detail = "地址格式不正确"})
            Return steps
        End If

        Dim host As String = uri.Host
        Dim port As Integer = If(uri.Port > 0, uri.Port, If(uri.Scheme = "https", 443, 80))

        '── 1. DNS ──
        Dim dnsStep As New DiagStep With {.Name = "DNS 解析"}
        Dim sw As Stopwatch = Stopwatch.StartNew()
        Dim ips As New List(Of String)
        Try
            ips = ResolveCandidates(host, TimeoutMs)
            sw.Stop()
            dnsStep.LatencyMs = CInt(sw.ElapsedMilliseconds)
            dnsStep.Ok = ips.Count > 0
            dnsStep.Detail = If(ips.Count > 0,
                                $"解析到 {ips.Count} 个 IPv4：{String.Join("、", ips.Take(4))}{If(ips.Count > 4, " …", "")}",
                                "没有解析到任何 IPv4 地址")
        Catch ex As Exception
            sw.Stop()
            dnsStep.LatencyMs = CInt(sw.ElapsedMilliseconds)
            dnsStep.Ok = False
            dnsStep.Detail = ex.Message
        End Try
        steps.Add(dnsStep)

        '── 2. TCP ──
        Dim tcpStep As New DiagStep With {.Name = $"TCP 握手（:{port}）"}
        If ips.Count = 0 Then
            tcpStep.Ok = False
            tcpStep.Detail = "跳过：上一步没有拿到 IP"
        Else
            Dim probes As List(Of TcpProbe) = ProbeTcpBatch(ips, port, TimeoutMs)
            '同上：必须绕开 List.Count 属性，走 Enumerable.Count
            Dim okCount As Integer = probes.Where(Function(p) p.Ok).Count()
            Dim best = probes.FirstOrDefault(Function(p) p.Ok)
            tcpStep.Ok = okCount > 0
            tcpStep.LatencyMs = If(best Is Nothing, -1, best.LatencyMs)
            tcpStep.Detail = If(okCount > 0,
                                $"{okCount}/{probes.Count} 个 IP 可连通，最快 {best.LatencyMs} ms（{best.Ip}）",
                                $"全部 {probes.Count} 个 IP 都连不上 —— 需要做 IP 优选或换网络")
        End If
        steps.Add(tcpStep)

        '── 3. TLS ──
        Dim tlsStep As New DiagStep With {.Name = "TLS 握手"}
        If Not tcpStep.Ok Then
            tlsStep.Ok = False
            tlsStep.Detail = "跳过：TCP 不通"
        Else
            Try
                sw = Stopwatch.StartNew()
                Dim handler As New Net.Http.HttpClientHandler With {.AllowAutoRedirect = False}
                Using c As New Net.Http.HttpClient(handler) With {.Timeout = TimeSpan.FromMilliseconds(TimeoutMs)}
                    Using resp = c.GetAsync($"https://{host}/").GetAwaiter().GetResult()
                        sw.Stop()
                        tlsStep.LatencyMs = CInt(sw.ElapsedMilliseconds)
                        '能拿到状态码就说明 TLS 握手完成了（4xx/5xx 也算成功）
                        tlsStep.Ok = True
                        tlsStep.Detail = $"TLS 握手完成，HTTP {CInt(resp.StatusCode)}"
                    End Using
                End Using
            Catch ex As Exception
                sw.Stop()
                tlsStep.LatencyMs = CInt(sw.ElapsedMilliseconds)
                tlsStep.Ok = False
                Dim inner As Exception = If(TypeOf ex Is AggregateException, CType(ex, AggregateException).GetBaseException(), ex)
                tlsStep.Detail = $"TLS/HTTPS 失败：{inner.Message}"
            End Try
        End If
        steps.Add(tlsStep)

        '── 4. 目标地址 ──
        Dim httpStep As New DiagStep With {.Name = "目标地址"}
        If Not tlsStep.Ok Then
            httpStep.Ok = False
            httpStep.Detail = "跳过：TLS 未通过"
        Else
            Try
                sw = Stopwatch.StartNew()
                Using resp = DshRegistry.DshHttpClient.GetAsync(Url).GetAwaiter().GetResult()
                    sw.Stop()
                    httpStep.LatencyMs = CInt(sw.ElapsedMilliseconds)
                    httpStep.Ok = resp.IsSuccessStatusCode
                    httpStep.Detail = $"HTTP {CInt(resp.StatusCode)}"
                End Using
            Catch ex As Exception
                sw.Stop()
                httpStep.LatencyMs = CInt(sw.ElapsedMilliseconds)
                httpStep.Ok = False
                httpStep.Detail = DshRegistry.DescribeNetworkError(ex)
            End Try
        End If
        steps.Add(httpStep)

        Return steps
    End Function

#End Region

#Region "镜像站"

    ''' <summary>一个 GitHub 加速镜像。</summary>
    Public Class MirrorEntry
        Public Property Name As String
        ''' <summary>前缀。直连时为空字符串。</summary>
        Public Property Prefix As String
        ''' <summary>给用户看的说明。</summary>
        Public Property Note As String
        Public Property Enabled As Boolean = True
        ''' <summary>最近一次测速耗时（-1 = 未测/失败）。</summary>
        Public Property LatencyMs As Integer = -1
        ''' <summary>是否测速通过。</summary>
        Public Property ProbeOk As Boolean = False

        ''' <summary>是不是「直连」这一项。</summary>
        Public ReadOnly Property IsDirect As Boolean
            Get
                Return String.IsNullOrWhiteSpace(Prefix)
            End Get
        End Property
    End Class

    ''' <summary>
    ''' 内置的镜像列表。
    ''' </summary>
    ''' <remarks>
    ''' 全部**实测过**（2026-09）：
    '''   - gh-proxy.com —— 可用，且能代理 api.github.com
    '''   - jsDelivr —— 只能取仓库文件（raw 类），不能代理 API
    '''   - ghproxy.net —— 403，不可用（留作参考，默认禁用）
    '''   - ghfast.top / gh.llkk.cc —— 超时，不可用（默认禁用）
    ''' 用户可以在界面上自己增删改，所以不可用的也留着，方便他们换成新的。
    ''' </remarks>
    Public ReadOnly Property DefaultMirrors As List(Of MirrorEntry)
        Get
            Return New List(Of MirrorEntry) From {
                New MirrorEntry With {.Name = "直连", .Prefix = "", .Note = "不走任何代理，作为基准"},
                New MirrorEntry With {.Name = "gh-proxy.com", .Prefix = "https://gh-proxy.com/",
                                      .Note = "实测可用，网页 / API / raw / release 都能代理"},
                New MirrorEntry With {.Name = "jsDelivr", .Prefix = "https://cdn.jsdelivr.net/gh/",
                                      .Note = "只能取仓库文件（raw 类），不能代理 API"},
                New MirrorEntry With {.Name = "ghproxy.net", .Prefix = "https://ghproxy.net/",
                                      .Note = "实测返回 403，默认禁用", .Enabled = False},
                New MirrorEntry With {.Name = "ghfast.top", .Prefix = "https://ghfast.top/",
                                      .Note = "实测超时，默认禁用", .Enabled = False}
            }
        End Get
    End Property

    ''' <summary>
    ''' 把原始 GitHub 地址转换成镜像地址。
    ''' </summary>
    ''' <param name="OriginalUrl">原始 https://github.com/... 地址。</param>
    ''' <param name="Mirror">目标镜像。</param>
    ''' <remarks>
    ''' 三种拼接方式：
    '''   1. 直连 —— 原样返回
    '''   2. jsDelivr —— 它有自己的路径规范（<c>/gh/owner/repo@ref/path</c>），
    '''      只对 <c>raw.githubusercontent.com</c> 形式有意义，其它形式返回原地址
    '''   3. 其余反代 —— 简单前缀拼接（<c>{prefix}{原地址}</c>）
    ''' </remarks>
    Public Function ToMirrorUrl(OriginalUrl As String, Mirror As MirrorEntry) As String
        If Mirror Is Nothing OrElse Mirror.IsDirect Then Return OriginalUrl
        If String.IsNullOrWhiteSpace(OriginalUrl) Then Return OriginalUrl

        If Mirror.Prefix.Contains("cdn.jsdelivr.net") Then
            'raw.githubusercontent.com/{owner}/{repo}/{ref}/{path}
            '  → cdn.jsdelivr.net/gh/{owner}/{repo}@{ref}/{path}
            Const rawHost As String = "https://raw.githubusercontent.com/"
            If OriginalUrl.StartsWith(rawHost, StringComparison.OrdinalIgnoreCase) Then
                Dim rest As String = OriginalUrl.Substring(rawHost.Length)
                Dim parts As String() = rest.Split("/"c)
                If parts.Length >= 4 Then
                    Dim owner As String = parts(0)
                    Dim repo As String = parts(1)
                    Dim gitRef As String = parts(2)
                    Dim path As String = String.Join("/", parts.Skip(3))
                    Return $"{Mirror.Prefix}{owner}/{repo}@{gitRef}/{path}"
                End If
            End If
            '其它形式 jsDelivr 处理不了
            Return OriginalUrl
        End If

        Return Mirror.Prefix.TrimEnd("/"c) & "/" & OriginalUrl
    End Function

    ''' <summary>镜像测速结果。</summary>
    Public Class MirrorProbeResult
        Public Property Mirror As MirrorEntry
        Public Property Ok As Boolean
        Public Property LatencyMs As Integer = -1
        Public Property Detail As String
    End Class

    ''' <summary>
    ''' 对一个镜像做可用性 + 速度测试。
    ''' </summary>
    ''' <remarks>
    ''' 打的是一个很小的文件（GitHub 上的一个固定小文件），
    ''' 避免测速本身消耗太多流量。
    ''' </remarks>
    Public Function ProbeMirror(Mirror As MirrorEntry,
                                Optional TimeoutMs As Integer = 8000) As MirrorProbeResult
        Dim result As New MirrorProbeResult With {.Mirror = Mirror}
        If Mirror Is Nothing Then
            result.Detail = "镜像为空"
            Return result
        End If

        '一个体积小、长期存在的公开文件
        Const ProbePath As String = "https://raw.githubusercontent.com/microsoft/vscode/main/README.md"
        Dim url As String = ToMirrorUrl(ProbePath, Mirror)

        Dim sw As Stopwatch = Stopwatch.StartNew()
        Try
            Using resp = DshRegistry.DshHttpClient.GetAsync(url).GetAwaiter().GetResult()
                sw.Stop()
                result.LatencyMs = CInt(sw.ElapsedMilliseconds)
                result.Ok = resp.IsSuccessStatusCode
                result.Detail = If(result.Ok, $"HTTP {CInt(resp.StatusCode)}", $"HTTP {CInt(resp.StatusCode)}")
            End Using
        Catch ex As Exception
            sw.Stop()
            result.LatencyMs = CInt(sw.ElapsedMilliseconds)
            result.Ok = False
            result.Detail = DshRegistry.DescribeNetworkError(ex)
        End Try

        Return result
    End Function

#End Region

#Region "hosts 受管区块"

    ''' <summary>hosts 里是否存在受管区块。</summary>
    Public Function HasHostsBlock() As Boolean
        Try
            If Not File.Exists(HostsFile) Then Return False
            Return File.ReadAllLines(HostsFile).Any(Function(l) l.Trim().StartsWith(BlockBegin, StringComparison.Ordinal))
        Catch ex As Exception
            Logger.Error(ex, "DSH：读取 hosts 失败")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 读出受管区块里的「域名 → IP」映射。
    ''' </summary>
    ''' <returns>域名 → IP；没有区块时返回空字典。</returns>
    Public Function ReadHostsBlock() As Dictionary(Of String, String)
        Dim result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Try
            If Not File.Exists(HostsFile) Then Return result

            Dim inside As Boolean = False
            For Each raw In File.ReadAllLines(HostsFile)
                Dim line As String = raw.Trim()
                If line.StartsWith(BlockBegin, StringComparison.Ordinal) Then
                    inside = True
                    Continue For
                End If
                If line.StartsWith(BlockEnd, StringComparison.Ordinal) Then Exit For
                If Not inside Then Continue For
                If line.Length = 0 OrElse line.StartsWith("#", StringComparison.Ordinal) Then Continue For

                'hosts 格式：IP 域名 [别名...]
                Dim parts As String() = line.Split(New Char() {" "c, ChrW(9)}, StringSplitOptions.RemoveEmptyEntries)
                If parts.Length >= 2 Then result(parts(1)) = parts(0)
            Next
        Catch ex As Exception
            Logger.Error(ex, "DSH：解析 hosts 受管区块失败")
        End Try
        Return result
    End Function

    ''' <summary>
    ''' 把「域名 → IP」写进 hosts 的受管区块（替换已有区块，其余内容原样保留）。
    ''' </summary>
    ''' <param name="Entries">域名 → IP。空字典等于删除区块。</param>
    ''' <param name="FailureReason">失败原因（给用户看）。</param>
    ''' <returns>成功返回 True。</returns>
    ''' <remarks>
    ''' ⚠️ 这是**系统级修改**，实现上守三条：
    '''   1. 首次修改前把原始 hosts 备份到 <see cref="HostsBackupFile"/>
    '''   2. 只替换受管区块，区块外的行**逐行原样保留**（含用户自己的条目与注释）
    '''   3. 写入走「临时文件 + 提权 cmd copy」—— 只弹一次 UAC，不重启 PCL
    ''' </remarks>
    Public Function ApplyHostsBlock(Entries As Dictionary(Of String, String),
                                    ByRef FailureReason As String) As Boolean
        FailureReason = Nothing

        If Entries Is Nothing OrElse Entries.Count = 0 Then
            Return RemoveHostsBlock(FailureReason)
        End If

        If Not IsElevated Then
            FailureReason = "修改 hosts 需要管理员权限。请右键 PCL 选择「以管理员身份运行」后重试。"
            Return False
        End If

        Try
            '── 1. 备份（只在第一次） ──
            If Not File.Exists(HostsBackupFile) AndAlso File.Exists(HostsFile) Then
                File.Copy(HostsFile, HostsBackupFile, True)
                Logger.Info($"DSH：已备份 hosts → {HostsBackupFile}")
            End If

            '── 2. 组装新内容 ──
            Dim lines As New List(Of String)
            If File.Exists(HostsFile) Then lines.AddRange(File.ReadAllLines(HostsFile))

            '先摘掉旧的受管区块（含标记行）
            Dim cleaned As New List(Of String)
            Dim inside As Boolean = False
            For Each raw In lines
                Dim line As String = raw.Trim()
                If line.StartsWith(BlockBegin, StringComparison.Ordinal) Then
                    inside = True
                    Continue For
                End If
                If line.StartsWith(BlockEnd, StringComparison.Ordinal) Then
                    inside = False
                    Continue For
                End If
                If inside Then Continue For
                cleaned.Add(raw)
            Next

            '去掉尾部多余空行，保证追加后排版整齐
            While cleaned.Count > 0 AndAlso cleaned(cleaned.Count - 1).Trim().Length = 0
                cleaned.RemoveAt(cleaned.Count - 1)
            End While

            '── 3. 追加新区块 ──
            cleaned.Add("")
            cleaned.Add(BlockBegin)
            cleaned.Add($"# 由 PCL DSH 改版生成于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            cleaned.Add("# 想还原：在「网络工具箱」里点「还原 hosts」，或删掉本段即可")
            For Each kv In Entries
                cleaned.Add($"{kv.Value,-16} {kv.Key}")
            Next
            cleaned.Add(BlockEnd)

            '── 4. 写临时文件 → 提权复制 ──
            Dim tmp As String = Path.Combine(Path.GetTempPath(), "pcl_dsh_hosts_" & Guid.NewGuid().ToString("N").Substring(0, 8) & ".txt")
            'hosts 必须是 ANSI/UTF8 无 BOM；带 BOM 会让第一行失效
            File.WriteAllLines(tmp, cleaned, New UTF8Encoding(False))

            Dim psi As New ProcessStartInfo("cmd.exe",
                $"/c copy /y ""{tmp}"" ""{HostsFile}""") With {
                .Verb = "runas",
                .UseShellExecute = True,
                .WindowStyle = ProcessWindowStyle.Hidden,
                .CreateNoWindow = True
            }

            Using p As Process = Process.Start(psi)
                If p Is Nothing Then
                    FailureReason = "无法启动提权进程。"
                    Return False
                End If
                If Not p.WaitForExit(30000) Then
                    FailureReason = "提权写入超时。"
                    Return False
                End If
                If p.ExitCode <> 0 Then
                    FailureReason = $"写入失败（copy 退出码 {p.ExitCode}）。"
                    Return False
                End If
            End Using

            Try
                File.Delete(tmp)
            Catch
            End Try

            Logger.Info($"DSH：hosts 受管区块已写入（{Entries.Count} 条）")
            Return True
        Catch ex As Exception
            '用户点了 UAC 的「否」会抛 Win32Exception(1223)
            FailureReason = If(ex.Message.Contains("1223") OrElse ex.Message.Contains("取消"),
                               "已取消管理员授权。",
                               $"写入 hosts 失败：{ex.Message}")
            Logger.Error(ex, "DSH：写入 hosts 失败")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 删除 hosts 的受管区块，其余内容原样保留。
    ''' </summary>
    Public Function RemoveHostsBlock(ByRef FailureReason As String) As Boolean
        FailureReason = Nothing

        If Not IsElevated Then
            FailureReason = "修改 hosts 需要管理员权限。请右键 PCL 选择「以管理员身份运行」后重试。"
            Return False
        End If

        Try
            If Not File.Exists(HostsFile) Then Return True

            Dim lines As String() = File.ReadAllLines(HostsFile)
            Dim cleaned As New List(Of String)
            Dim inside As Boolean = False
            For Each raw In lines
                Dim line As String = raw.Trim()
                If line.StartsWith(BlockBegin, StringComparison.Ordinal) Then
                    inside = True
                    Continue For
                End If
                If line.StartsWith(BlockEnd, StringComparison.Ordinal) Then
                    inside = False
                    Continue For
                End If
                If inside Then Continue For
                cleaned.Add(raw)
            Next

            If cleaned.Count = lines.Length Then
                '本来就没有受管区块
                Return True
            End If

            While cleaned.Count > 0 AndAlso cleaned(cleaned.Count - 1).Trim().Length = 0
                cleaned.RemoveAt(cleaned.Count - 1)
            End While

            Dim tmp As String = Path.Combine(Path.GetTempPath(), "pcl_dsh_hosts_" & Guid.NewGuid().ToString("N").Substring(0, 8) & ".txt")
            File.WriteAllLines(tmp, cleaned, New UTF8Encoding(False))

            Dim psi As New ProcessStartInfo("cmd.exe",
                $"/c copy /y ""{tmp}"" ""{HostsFile}""") With {
                .Verb = "runas",
                .UseShellExecute = True,
                .WindowStyle = ProcessWindowStyle.Hidden,
                .CreateNoWindow = True
            }

            Using p As Process = Process.Start(psi)
                If p Is Nothing OrElse Not p.WaitForExit(30000) OrElse p.ExitCode <> 0 Then
                    FailureReason = "提权写入失败或被取消。"
                    Return False
                End If
            End Using

            Try
                File.Delete(tmp)
            Catch
            End Try

            Logger.Info("DSH：hosts 受管区块已移除")
            Return True
        Catch ex As Exception
            FailureReason = If(ex.Message.Contains("1223") OrElse ex.Message.Contains("取消"),
                               "已取消管理员授权。",
                               $"移除 hosts 区块失败：{ex.Message}")
            Logger.Error(ex, "DSH：移除 hosts 区块失败")
            Return False
        End Try
    End Function

#End Region

#Region "配置持久化"

    ''' <summary>工具箱配置文件（镜像列表 + 域名勾选状态）。</summary>
    Public ReadOnly Property ConfigFile As String
        Get
            Return ModDSH.DshRoot & "nettool.json"
        End Get
    End Property

    ''' <summary>
    ''' 读取镜像列表；没有配置文件时返回默认值。
    ''' </summary>
    Public Function LoadMirrors() As List(Of MirrorEntry)
        Dim defaults As List(Of MirrorEntry) = DefaultMirrors
        Try
            If Not DshRuntime.FileExistsSafe(ConfigFile) Then Return defaults

            Dim root As JObject = JObject.Parse(File.ReadAllText(ConfigFile, Encoding.UTF8))
            Dim arr = TryCast(root("mirrors"), JArray)
            If arr Is Nothing OrElse arr.Count = 0 Then Return defaults

            Dim result As New List(Of MirrorEntry)
            For Each item In arr
                Dim o = TryCast(item, JObject)
                If o Is Nothing Then Continue For
                result.Add(New MirrorEntry With {
                    .Name = If(o("name")?.ToString(), "未命名"),
                    .Prefix = If(o("prefix")?.ToString(), ""),
                    .Note = o("note")?.ToString(),
                    .Enabled = If(o("enabled") Is Nothing, True, CBool(o("enabled")))
                })
            Next
            Return If(result.Count > 0, result, defaults)
        Catch ex As Exception
            '配置坏了就回默认值，别让工具箱打不开
            Logger.Warn($"DSH：读取网络工具箱配置失败，使用默认值：{ex.Message}")
            Return defaults
        End Try
    End Function

    ''' <summary>读取域名勾选状态（默认目标 + 用户取消勾选的记录）。</summary>
    Public Function LoadTargets() As List(Of AccelTarget)
        Dim defaults As List(Of AccelTarget) = DefaultTargets
        Try
            If Not DshRuntime.FileExistsSafe(ConfigFile) Then Return defaults

            Dim root As JObject = JObject.Parse(File.ReadAllText(ConfigFile, Encoding.UTF8))
            Dim arr = TryCast(root("targets"), JArray)
            If arr Is Nothing OrElse arr.Count = 0 Then Return defaults

            Dim result As New List(Of AccelTarget)
            For Each item In arr
                Dim o = TryCast(item, JObject)
                If o Is Nothing Then Continue For
                Dim host As String = o("host")?.ToString()
                If String.IsNullOrWhiteSpace(host) Then Continue For
                result.Add(New AccelTarget(host, If(o("note")?.ToString(), ""),
                                           If(o("port") Is Nothing, 443, CInt(o("port")))) With {
                    .Enabled = If(o("enabled") Is Nothing, True, CBool(o("enabled")))
                })
            Next
            Return If(result.Count > 0, result, defaults)
        Catch ex As Exception
            Logger.Warn($"DSH：读取加速目标配置失败，使用默认值：{ex.Message}")
            Return defaults
        End Try
    End Function

    ''' <summary>把镜像列表与域名勾选状态写回配置文件（原子写入）。</summary>
    Public Sub SaveConfig(Mirrors As List(Of MirrorEntry), Targets As List(Of AccelTarget))
        Try
            ModDSH.DshEnsureDirectories()

            Dim mArr As New JArray()
            For Each m In Mirrors
                Dim o As New JObject()
                o("name") = m.Name
                o("prefix") = m.Prefix
                o("note") = m.Note
                o("enabled") = m.Enabled
                mArr.Add(o)
            Next

            Dim tArr As New JArray()
            For Each t In Targets
                Dim o As New JObject()
                o("host") = t.Host
                o("note") = t.Note
                o("port") = t.Port
                o("enabled") = t.Enabled
                tArr.Add(o)
            Next

            Dim root As New JObject()
            root("version") = 1
            root("mirrors") = mArr
            root("targets") = tArr

            Dim path As String = ConfigFile
            Dim tmp As String = DshMigrate.DshAtomicTempPath(path)
            File.WriteAllText(tmp, root.ToString(Newtonsoft.Json.Formatting.Indented), New UTF8Encoding(False))

            If File.Exists(path) Then
                File.Replace(tmp, path, Nothing)
            Else
                File.Move(tmp, path)
            End If
        Catch ex As Exception
            Logger.Error(ex, "DSH：保存网络工具箱配置失败")
        End Try
    End Sub

#End Region

End Module
