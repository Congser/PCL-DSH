Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports Newtonsoft.Json.Linq

''' <summary>
''' dsh 插件市场 —— 插件发现与安装。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 数据源（两套，可切换 —— 决策 4-a / 4-b）
''' ═══════════════════════════════════════════════════════════════════════
'''
''' **① 主数据源（默认）**：静态索引站
''' <code>GET https://awesome-dsh-plugin.com/plugins.json</code>
'''
'''   实测：HTTP 200、**4.2 MB**、约 3.9 秒、**无 API Key、无配额限制**、每日更新。
'''   当前收录 **4062 个**插件（vs GitHub topic 源只能拿到一页 50 个，差 80 倍）。
'''   附带 23 个分类、中文简介、精确的 npm 包名与版本号、正确的 install 命令、
'''   以及点赞 / 新增日期等元信息。
'''
'''   这是**首选**数据源 —— 有它就不需要代理回退，也不需要配额约束。
'''   代价只有一点点带宽（4.2 MB，缓存到本地后不再重复下载）。
'''
''' **② 回退数据源**：GitHub topic 搜索
''' <code>GET https://api.github.com/search/repositories?q=topic:dsh-plugin&amp;sort=stars&amp;order=desc</code>
'''
'''   ⚠️ **匿名配额极紧**：search 接口 10 次/小时。
'''   所以走这个源时必须做本地缓存 —— 一次请求把结果存盘，
'''   之后切页 / 重复搜索全部走缓存，只有「缓存过期」或用户主动刷新才真发请求。
'''
'''   ⚠️ 两个**实测踩过的坑**：
'''
'''     1. **`total_count` 不可信**。查 <c>topic:dsh-plugin</c> 时 GitHub 报告
'''        <c>total_count = 15583</c>，但实际返回的 50 项**全部**确实带该 topic，
'''        也就是说真实匹配量远小于报告值。这是 GitHub topic 搜索的已知问题
'''        （<c>topic:</c> 会被当成词项组合，计数用的查询比结果查询更宽松）。
'''        所以界面上**不要**拿这个数字说「共 N 个」，只报「已获取前 N 个」。
'''
'''     2. **`per_page` 上限就是 100**，而且每翻一页都要再消耗一次搜索配额。
'''        匿名额度只有 10 次/小时，所以这里只取一页 50 条，不做翻页。
'''
'''   **存在的唯一理由**：万一索引站挂了 / 被墙 / 数据陈旧，用户还能切回去用。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 为什么 GitHub 源要有代理回退
''' ═══════════════════════════════════════════════════════════════════════
''' 国内直连 api.github.com 有时不通。实测可用的公共反代是 <c>gh-proxy.com</c>：
'''   <c>https://gh-proxy.com/https://api.github.com/...</c>
'''
''' ⚠️ 但要说清楚：**镜像解决连通性，不解决配额**。
''' 反代转发的是同一个 GitHub 接口，匿名配额照样消耗。
''' 所以「换镜像」和「做缓存」是互补的两件事，缺一不可。
'''
''' 实测过不可用的（别再往里加）：ghproxy.net（403）、
''' ghfast.top / gh.llkk.cc（超时）、hub.fastgit.org / kkgithub / bgithub.xyz（DNS 或超时）。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 插件是怎么装上的
''' ═══════════════════════════════════════════════════════════════════════
''' <c>dsh plugin --profile &lt;name&gt; add &lt;spec&gt;</c>
''' 该子命令把剩余参数**原样转发给 pnpm**，工作目录是
''' <c>$DSH_HOME/profiles/&lt;name&gt;/</c>。
'''
''' 由此推出两个重要结论：
'''   1. **插件是按实例隔离的** —— 每个实例有自己的 DSH_HOME，也就有自己的插件集
'''   2. pnpm 必须在 PATH 上（退出码 127 就是"没找到 pnpm"）
'''
''' 另外：git 托管的插件（<c>github:owner/repo</c>）安装时会跑 prepare 脚本，
''' pnpm 默认拦截，需要在 profile 目录的 <c>pnpm-workspace.yaml</c> 里
''' 写 <c>allowBuilds</c>。这是上游行为，我们只能把报错原文转给用户。
''' </summary>
Public Module DshPluginMarket

#Region "常量"

    ''' <summary>GitHub topic 名。用 <c>topic:xxx</c> 查询语法（不是 <c>#xxx</c>）。</summary>
    Public Const DshPluginTopic As String = "dsh-plugin"

    ''' <summary>缓存有效期（分钟）—— GitHub topic 数据源专用。</summary>
    ''' <remarks>
    ''' ⚠️ **这个值受配额硬约束，不能随意调小。**
    ''' GitHub 匿名 search 配额是 **10 次/小时**，缓存 30 分钟意味着最坏情况 2 次/小时。
    ''' 用户曾要求「缓存 30 秒过期」（决策 3-a），但**在 GitHub 数据源下 30 秒会瞬间打爆配额**
    ''' （30 秒 = 120 次/小时，超配额 11 倍 → 直接被 403）。
    '''
    ''' 决策 4-a 会把数据源换成 `awesome-dsh-plugin.com` 的静态 CDN 文件（**无配额**），
    ''' 届时才能安全地用到 <see cref="DshPluginCacheTtlSecondsCdn"/>。
    ''' 在那之前，调小这个常量只会让用户更快被限流。
    ''' </remarks>
    Public Const DshPluginCacheTtlMinutes As Integer = 30

    ''' <summary>缓存有效期（秒）—— 静态 CDN 数据源专用（无配额，可以短）。</summary>
    Public Const DshPluginCacheTtlSecondsCdn As Integer = 30

    ''' <summary>
    ''' 当前生效的缓存有效期（秒）。
    ''' </summary>
    ''' <remarks>
    ''' 按当前数据源返回：GitHub topic 受配额限制 → 用长 TTL；
    ''' CDN 静态文件无配额 → 用短 TTL（决策 3-a 要求的 30 秒）。
    ''' </remarks>
    Public ReadOnly Property DshPluginCacheTtlSeconds As Integer
        Get
            ' ⚠️ 单行 If 不带 End If —— 写成多行块才行
            If DshPluginUseCdnSource Then
                Return DshPluginCacheTtlSecondsCdn
            End If
            Return DshPluginCacheTtlMinutes * 60
        End Get
    End Property

    ''' <summary>
    ''' 是否使用静态 CDN 数据源（awesome-dsh-plugin.com）。
    ''' </summary>
    ''' <remarks>
    ''' 决策 4-a / 4-b：默认 **True**（走索引站），用户可在设置里切回 GitHub topic。
    ''' 索引站无配额、插件数是 topic 源的 80 倍，没有理由不作为默认。
    ''' </remarks>
    Public ReadOnly Property DshPluginUseCdnSource As Boolean
        Get
            Try
                Return Settings.Get(Of Boolean)("DshPluginUseCdn")
            Catch
                Return True
            End Try
        End Get
    End Property

    ''' <summary>索引站根地址（用于拼插件详情页链接）。</summary>
    Public Const DshPluginIndexSite As String = "https://awesome-dsh-plugin.com"

    ''' <summary>索引站的插件清单（静态 JSON，无配额）。</summary>
    Public Const DshPluginIndexUrl As String = DshPluginIndexSite & "/plugins.json"

    ''' <summary>索引站 + 插件数的展示文案（设置页与状态行共用）。</summary>
    Public ReadOnly Property DshPluginSourceText As String
        Get
            If DshPluginUseCdnSource Then Return "插件索引站（awesome-dsh-plugin.com）"
            Return "GitHub（topic:dsh-plugin）"
        End Get
    End Property

    ''' <summary>
    ''' 缓存有效期文案（用于界面提示）。
    ''' </summary>
    Public ReadOnly Property DshPluginCacheTtlText As String
        Get
            If DshPluginUseCdnSource Then Return $"{DshPluginCacheTtlSecondsCdn} 秒"
            Return $"{DshPluginCacheTtlMinutes} 分钟"
        End Get
    End Property

    ''' <summary>GitHub API 根地址。</summary>
    Private Const GitHubApiBase As String = "https://api.github.com"

    ''' <summary>可用的公共反代前缀（按顺序尝试）。</summary>
    ''' <remarks>实测只有 gh-proxy.com 稳定；其余要么 403 要么超时，不要再加回来。</remarks>
    Private ReadOnly ProxyPrefixes As String() = {
        "https://gh-proxy.com/"
    }

#End Region

#Region "路径"

    ''' <summary>插件市场缓存文件。</summary>
    Public ReadOnly Property DshPluginCacheFile As String
        Get
            Return ModDSH.DshRoot & "plugin-cache.json"
        End Get
    End Property

    ''' <summary>某个实例某个 profile 的目录（pnpm 的工作目录）。</summary>
    Public Function ProfileDirOf(Instance As DshInstance, Optional Profile As String = Nothing) As String
        If Instance Is Nothing Then Return Nothing
        Dim p As String = If(String.IsNullOrWhiteSpace(Profile),
                             If(String.IsNullOrWhiteSpace(Instance.Profile), ModDSH.DshProfileName, Instance.Profile),
                             Profile)
        Return Instance.HomeDir & "profiles\" & p & "\"
    End Function

    ''' <summary>某个实例某个 profile 的 package.json（已安装插件记在这里）。</summary>
    Public Function ProfilePackageJsonOf(Instance As DshInstance, Optional Profile As String = Nothing) As String
        Dim dir As String = ProfileDirOf(Instance, Profile)
        If String.IsNullOrWhiteSpace(dir) Then Return Nothing
        Return dir & "package.json"
    End Function

#End Region

#Region "数据模型"

    ''' <summary>一个市场里的插件（可能来自 GitHub topic，也可能来自索引站）。</summary>
    Public Class DshPluginInfo
        ''' <summary>owner/repo（**永远**是干净的仓库名，不含子路径）。</summary>
        Public Property FullName As String
        ''' <summary>
        ''' 界面上显示的插件名。
        ''' </summary>
        ''' <remarks>
        ''' 索引站源下可能是带子路径的形式（如 <c>dsh-forge-studio#plugin-notes</c>），
        ''' 因为它更能说明「具体是仓库里的哪个子包」。
        ''' 为空时回落到 <see cref="RepoName"/>。
        ''' </remarks>
        Public Property DisplayName As String
        ''' <summary>仓库简介（已按当前语言挑选）。</summary>
        Public Property Description As String
        ''' <summary>仓库地址。</summary>
        Public Property HtmlUrl As String
        ''' <summary>星标数（GitHub 源有；索引站源通常为 0）。</summary>
        Public Property Stars As Integer
        ''' <summary>主要语言（仅 GitHub 源有）。</summary>
        Public Property Language As String
        ''' <summary>最近更新时间。</summary>
        Public Property UpdatedAt As DateTime?
        ''' <summary>默认分支名（拼 raw 地址时要用）。</summary>
        Public Property DefaultBranch As String

        '══════ 索引站（awesome-dsh-plugin.com）专有字段 ══════
        ' 这些字段让安装链路可以**跳过联网探测**：
        ' 索引站已经告诉我们确切的 npm 包名、版本、install 命令，
        ' 不必再去抓 package.json、再查一次 npm registry 确认来源。

        ''' <summary>索引站给的 npm 包名（可能为空 —— 只有约一半插件发布了 npm 包）。</summary>
        Public Property NpmName As String

        ''' <summary>索引站给的版本号。</summary>
        Public Property Version As String

        ''' <summary>索引站给的安装命令（如 <c>dsh plugin --profile web add @x/y</c>）。</summary>
        Public Property InstallCommand As String

        ''' <summary>分类 key（如 <c>ui</c> / <c>memory</c>）。</summary>
        Public Property Category As String

        ''' <summary>索引站详情页地址。</summary>
        Public Property PageUrl As String

        ''' <summary>收录进索引站的日期（用于「最新收录」排序）。</summary>
        Public Property AddedAt As DateTime?

        ''' <summary>仓库名（不含 owner）。</summary>
        Public ReadOnly Property RepoName As String
            Get
                If String.IsNullOrWhiteSpace(FullName) Then Return ""
                Dim idx As Integer = FullName.IndexOf("/"c)
                If idx < 0 Then Return FullName
                Return FullName.Substring(idx + 1)
            End Get
        End Property

        ''' <summary>列表里实际显示的名字。</summary>
        Public ReadOnly Property Display As String
            Get
                If Not String.IsNullOrWhiteSpace(DisplayName) Then Return DisplayName
                If Not String.IsNullOrWhiteSpace(FullName) Then Return FullName
                Return NpmName
            End Get
        End Property

        ''' <summary>owner。</summary>
        Public ReadOnly Property OwnerName As String
            Get
                If String.IsNullOrWhiteSpace(FullName) Then Return ""
                Dim idx As Integer = FullName.IndexOf("/"c)
                If idx <= 0 Then Return ""
                Return FullName.Substring(0, idx)
            End Get
        End Property

        ''' <summary>筛选 / 排序时用来比对的稳定键（小写 fullName）。</summary>
        Public ReadOnly Property Key As String
            Get
                Return If(FullName, "").ToLowerInvariant()
            End Get
        End Property

        ''' <summary>是否有索引站给的 npm 包名。</summary>
        Public ReadOnly Property HasNpm As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(NpmName)
            End Get
        End Property

        ''' <summary>给用户看的更新日期。</summary>
        Public ReadOnly Property UpdatedText As String
            Get
                If UpdatedAt Is Nothing Then Return "未知"
                Return CType(UpdatedAt, DateTime).ToString("yyyy-MM-dd")
            End Get
        End Property

        ''' <summary>是否带中文简介（用于标记「中文简介」徽标）。</summary>
        Public Property HasChineseDesc As Boolean

        ''' <summary>列表里显示的描述（空描述给个占位）。</summary>
        Public ReadOnly Property DescriptionText As String
            Get
                If String.IsNullOrWhiteSpace(Description) Then Return "（作者没有填写简介）"
                Return Description.Trim()
            End Get
        End Property

        ''' <summary>pnpm 可直接安装的兜底 spec。</summary>
        ''' <remarks>
        ''' 仓库不一定发布了 npm 包，这时用 <c>github:owner/repo</c> ——
        ''' pnpm 支持从 GitHub 直接拉取。代价是会在安装时跑 prepare 脚本，
        ''' 需要用户在 profile 的 pnpm-workspace.yaml 里放行。
        ''' </remarks>
        Public ReadOnly Property GitHubSpec As String
            Get
                Return "github:" & FullName
            End Get
        End Property
    End Class

    ''' <summary>市场搜索结果 + 缓存元信息。</summary>
    Public Class DshPluginSearchResult
        Public Property Items As New List(Of DshPluginInfo)
        ''' <summary>这次结果是不是从缓存来的。</summary>
        Public Property FromCache As Boolean
        ''' <summary>缓存的抓取时间（FromCache 时有意义）。</summary>
        Public Property FetchedAt As DateTime?
        ''' <summary>实际使用的数据源（直连 / 反代 / 索引站 / 缓存）。</summary>
        Public Property Source As String
        ''' <summary>
        ''' GitHub 报告的匹配总数。
        ''' </summary>
        ''' <remarks>
        ''' ⚠️ **不要直接展示给用户**。topic 查询下这个数字明显偏大
        ''' （实测 topic:dsh-plugin 报 15583，而实际结果集只有几十个）。
        ''' 留着只是为了记日志、排查问题。
        '''
        ''' 索引站源下这个字段是**可信的**（就是 plugins 数组的长度），
        ''' 但界面上仍然统一用 <see cref="Items"/>.Count —— 避免两套语义。
        ''' </remarks>
        Public Property TotalCount As Integer
        ''' <summary>请求时用的 per_page，用来判断"可能还有更多"。</summary>
        Public Property PageSize As Integer = 50

        ''' <summary>索引站源下回传的分类表（key → 中文名）。GitHub 源为空。</summary>
        Public Property Categories As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

        ''' <summary>索引站源下回传的索引更新时间。</summary>
        Public Property IndexUpdatedAt As String

        ''' <summary>本次返回是否已经填满了请求页（意味着后面可能还有）。</summary>
        ''' <remarks>索引站源一次给全量，永远为 False。</remarks>
        Public ReadOnly Property MayHaveMore As Boolean
            Get
                If IsFullList Then Return False
                Return Items.Count >= PageSize AndAlso PageSize > 0
            End Get
        End Property

        ''' <summary>是不是一次拿到的全量列表（索引站源为 True）。</summary>
        Public Property IsFullList As Boolean

        Public ReadOnly Property CacheAgeText As String
            Get
                If Not FromCache OrElse FetchedAt Is Nothing Then Return ""
                Dim span As TimeSpan = DateTime.Now - CType(FetchedAt, DateTime)
                ' TTL 可能是 30 秒（索引站源），所以秒级也要显示，否则永远显示「刚刚」
                If span.TotalSeconds < 60 Then Return $"{CInt(span.TotalSeconds)} 秒前"
                If span.TotalMinutes < 60 Then Return $"{CInt(span.TotalMinutes)} 分钟前"
                If span.TotalHours < 24 Then Return $"{CInt(span.TotalHours)} 小时前"
                Return $"{CInt(span.TotalDays)} 天前"
            End Get
        End Property
    End Class

    ''' <summary>索引站里一个分类。</summary>
    Public Class DshPluginCategory
        ''' <summary>分类 key（英文，如 <c>ui</c>）。</summary>
        Public Property Key As String
        ''' <summary>中文名。</summary>
        Public Property NameZh As String
        ''' <summary>英文名。</summary>
        Public Property NameEn As String
        ''' <summary>该分类下的插件数（按当前列表统计）。</summary>
        Public Property Count As Integer

        ''' <summary>下拉框里显示的文字。</summary>
        Public ReadOnly Property Display As String
            Get
                Dim label As String = If(String.IsNullOrWhiteSpace(NameZh), Key, NameZh)
                Return $"{label}（{Count}）"
            End Get
        End Property
    End Class

    ''' <summary>排序方式。</summary>
    Public Enum DshPluginSort
        ''' <summary>星标降序（GitHub 源默认）。</summary>
        Stars = 0
        ''' <summary>最新收录优先（索引站源默认）。</summary>
        Newest = 1
        ''' <summary>名称 A→Z。</summary>
        NameAsc = 2
        ''' <summary>有 npm 包的排前面（安装成功率最高）。</summary>
        NpmFirst = 3
    End Enum

#End Region

#Region "缓存"

    ''' <summary>
    ''' 读取本地缓存。
    ''' </summary>
    ''' <returns>缓存内容；不存在或损坏时返回 Nothing。</returns>
    Public Function ReadCache() As DshPluginSearchResult
        Dim path As String = DshPluginCacheFile
        If Not DshRuntime.FileExistsSafe(path) Then Return Nothing

        Try
            Dim root As JObject = JObject.Parse(File.ReadAllText(path, Encoding.UTF8))

            Dim result As New DshPluginSearchResult With {.FromCache = True}
            Dim fetched As String = root("fetchedAt")?.ToString()
            Dim dt As DateTime
            If Not String.IsNullOrWhiteSpace(fetched) AndAlso
               DateTime.TryParse(fetched, Globalization.CultureInfo.InvariantCulture,
                                 Globalization.DateTimeStyles.RoundtripKind, dt) Then
                result.FetchedAt = dt
            End If
            result.Source = If(root("source")?.ToString(), "缓存")
            result.TotalCount = If(root("totalCount") Is Nothing, 0, CInt(root("totalCount")))
            result.PageSize = If(root("pageSize") Is Nothing, 50, CInt(root("pageSize")))
            result.IsFullList = (If(root("isFullList") Is Nothing, False, CBool(root("isFullList"))))
            result.IndexUpdatedAt = root("indexUpdatedAt")?.ToString()

            Dim cats = TryCast(root("categories"), JObject)
            If cats IsNot Nothing Then
                For Each prop As KeyValuePair(Of String, JToken) In cats
                    result.Categories(prop.Key) = prop.Value?.ToString()
                Next
            End If

            Dim arr = TryCast(root("items"), JArray)
            If arr IsNot Nothing Then
                For Each item In arr
                    Dim o = TryCast(item, JObject)
                    If o Is Nothing Then Continue For
                    result.Items.Add(PluginFromJson(o))
                Next
            End If

            Return result
        Catch ex As Exception
            '缓存坏了就当作没有 —— 下次刷新会重建，不该因此报错给用户
            Logger.Warn($"DSH：插件市场缓存解析失败，将忽略：{ex.Message}")
            Return Nothing
        End Try
    End Function

    ''' <summary>缓存是否还在有效期内（有效期按当前数据源决定）。</summary>
    Public Function IsCacheFresh(Cache As DshPluginSearchResult) As Boolean
        If Cache Is Nothing OrElse Cache.FetchedAt Is Nothing Then Return False
        Dim ageSeconds As Double = (DateTime.Now - CType(Cache.FetchedAt, DateTime)).TotalSeconds
        Return ageSeconds < DshPluginCacheTtlSeconds
    End Function

    ''' <summary>把结果写入缓存（原子写入）。</summary>
    Private Sub WriteCache(Result As DshPluginSearchResult)
        Try
            ModDSH.DshEnsureDirectories()

            Dim arr As New JArray()
            For Each item In Result.Items
                arr.Add(PluginToJson(item))
            Next

            Dim root As New JObject()
            root("version") = 2
            root("fetchedAt") = DateTime.Now.ToString("o")
            root("source") = Result.Source
            root("totalCount") = Result.TotalCount
            root("pageSize") = Result.PageSize
            root("isFullList") = Result.IsFullList
            root("indexUpdatedAt") = Result.IndexUpdatedAt

            If Result.Categories.Count > 0 Then
                Dim cats As New JObject()
                For Each kv In Result.Categories
                    cats(kv.Key) = kv.Value
                Next
                root("categories") = cats
            End If

            root("items") = arr

            Dim path As String = DshPluginCacheFile
            Dim tmp As String = DshMigrate.DshAtomicTempPath(path)
            File.WriteAllText(tmp, root.ToString(Newtonsoft.Json.Formatting.Indented), New UTF8Encoding(False))

            If File.Exists(path) Then
                File.Replace(tmp, path, Nothing)
            Else
                File.Move(tmp, path)
            End If

            Logger.Info($"DSH：插件市场缓存已更新（{Result.Items.Count} 项）")
        Catch ex As Exception
            Logger.Error(ex, "DSH：写入插件市场缓存失败")
        End Try
    End Sub

    Private Function PluginToJson(p As DshPluginInfo) As JObject
        Dim o As New JObject()
        o("fullName") = p.FullName
        o("displayName") = p.DisplayName
        o("description") = p.Description
        o("htmlUrl") = p.HtmlUrl
        o("stars") = p.Stars
        o("language") = p.Language
        o("defaultBranch") = p.DefaultBranch
        o("updatedAt") = If(p.UpdatedAt Is Nothing, Nothing, CType(p.UpdatedAt, DateTime).ToString("o"))
        '索引站专有字段 —— 漏掉任何一个都会让「换源后安装」退化成联网探测
        o("npmName") = p.NpmName
        o("version") = p.Version
        o("installCommand") = p.InstallCommand
        o("category") = p.Category
        o("pageUrl") = p.PageUrl
        o("addedAt") = If(p.AddedAt Is Nothing, Nothing, CType(p.AddedAt, DateTime).ToString("o"))
        o("hasChineseDesc") = p.HasChineseDesc
        Return o
    End Function

    Private Function PluginFromJson(o As JObject) As DshPluginInfo
        Dim info As New DshPluginInfo With {
            .FullName = If(o("fullName")?.ToString(), ""),
            .DisplayName = o("displayName")?.ToString(),
            .Description = o("description")?.ToString(),
            .HtmlUrl = If(o("htmlUrl")?.ToString(), ""),
            .Stars = If(o("stars") Is Nothing, 0, CInt(o("stars"))),
            .Language = o("language")?.ToString(),
            .DefaultBranch = If(o("defaultBranch")?.ToString(), "main"),
            .NpmName = o("npmName")?.ToString(),
            .Version = o("version")?.ToString(),
            .InstallCommand = o("installCommand")?.ToString(),
            .Category = o("category")?.ToString(),
            .PageUrl = o("pageUrl")?.ToString(),
            .HasChineseDesc = (If(o("hasChineseDesc") Is Nothing, False, CBool(o("hasChineseDesc"))))
        }
        Dim updated As String = o("updatedAt")?.ToString()
        Dim dt As DateTime
        If Not String.IsNullOrWhiteSpace(updated) AndAlso
           DateTime.TryParse(updated, Globalization.CultureInfo.InvariantCulture,
                             Globalization.DateTimeStyles.RoundtripKind, dt) Then
            info.UpdatedAt = dt
        End If
        Dim added As String = o("addedAt")?.ToString()
        Dim dt2 As DateTime
        If Not String.IsNullOrWhiteSpace(added) AndAlso
           DateTime.TryParse(added, Globalization.CultureInfo.InvariantCulture,
                             Globalization.DateTimeStyles.RoundtripKind, dt2) Then
            info.AddedAt = dt2
        End If
        Return info
    End Function

#End Region

#Region "查询"

    ''' <summary>
    ''' 获取插件列表（自动按当前数据源路由）。
    ''' </summary>
    ''' <param name="ForceRefresh">为 True 时忽略缓存，强制联网。</param>
    ''' <param name="TimeoutMs">单次请求超时（毫秒）。</param>
    ''' <exception cref="InvalidOperationException">联网失败且没有可用缓存。</exception>
    ''' <remarks>
    ''' 阻塞调用，请放在后台线程。
    ''' 索引站源（默认）一次拿全量 4000+ 个，不走缓存也能随时刷；
    ''' GitHub 源只拿一页 50 个，且**必须**走缓存省配额。
    ''' </remarks>
    Public Function FetchPlugins(Optional ForceRefresh As Boolean = False,
                                 Optional TimeoutMs As Integer = 20000) As DshPluginSearchResult
        '1. 缓存够新就直接用 —— 这是省配额（或省带宽）的关键
        If Not ForceRefresh Then
            Dim cached As DshPluginSearchResult = ReadCache()
            If IsCacheFresh(cached) Then
                Logger.Info($"DSH：插件市场走缓存（{cached.Items.Count} 项）")
                Return cached
            End If
        End If

        '2. 按数据源分流
        If DshPluginUseCdnSource Then
            Return FetchPluginsFromIndex(TimeoutMs)
        End If
        Return FetchPluginsFromGitHub(TimeoutMs)
    End Function

    ''' <summary>
    ''' 从插件索引站拉全量列表（决策 4-a 的主数据源）。
    ''' </summary>
    ''' <remarks>
    ''' 静态 JSON 文件，**无配额、无 API Key**，实测 4.2 MB / 3.9 秒。
    ''' 失败时退回过期缓存 —— 索引站挂了不应该让页面变空白。
    ''' </remarks>
    Private Function FetchPluginsFromIndex(TimeoutMs As Integer) As DshPluginSearchResult
        Dim lastError As Exception = Nothing
        Try
            '大文件，超时要放宽：20 秒在慢网络上不够
            Dim budget As Integer = Math.Max(TimeoutMs, 60000)
            Logger.Info($"DSH：插件市场请求 → {DshPluginIndexUrl}")
            Dim body As String = DshRegistry.HttpGetString(DshPluginIndexUrl, budget, "application/json")
            Dim result As DshPluginSearchResult = ParseIndexResult(body)
            result.Source = "插件索引站"

            WriteCache(result)
            Logger.Info($"DSH：插件索引站拿到 {result.Items.Count} 项，{result.Categories.Count} 个分类")
            Return result
        Catch ex As Exception
            lastError = ex
            Logger.Warn($"DSH：插件索引站请求失败：{ex.Message}")
        End Try

        '退回过期缓存
        Dim stale As DshPluginSearchResult = ReadCache()
        If stale IsNot Nothing AndAlso stale.Items.Count > 0 Then
            Logger.Warn("DSH：插件索引站请求失败，退回使用过期缓存")
            stale.Source = "过期缓存（索引站不可用）"
            Return stale
        End If

        '索引站也挂了 → 试一次 GitHub 兜底（哪怕只拿到 50 个，也比空白页强）
        Logger.Warn("DSH：插件索引站不可用且无缓存，尝试回退到 GitHub 源")
        Try
            Dim fb As DshPluginSearchResult = FetchPluginsFromGitHub(TimeoutMs)
            fb.Source = fb.Source & "（索引站不可用，已回退）"
            Return fb
        Catch ex2 As Exception
            Throw New InvalidOperationException(
                "无法获取插件列表。" & vbCrLf & vbCrLf &
                "可能的原因：" & vbCrLf &
                "  · 网络不通或需要代理" & vbCrLf &
                $"  · 插件索引站 {DshPluginIndexSite} 暂时不可用" & vbCrLf &
                "  · GitHub 匿名接口配额已用尽（搜索接口每小时 10 次）" & vbCrLf & vbCrLf &
                $"索引站错误：{If(lastError IsNot Nothing, lastError.Message, "未知")}" & vbCrLf &
                $"GitHub 错误：{ex2.Message}", lastError)
        End Try
    End Function

    ''' <summary>
    ''' 解析索引站的 plugins.json。
    ''' </summary>
    ''' <remarks>
    ''' 字段是**扁平的**，比 GitHub 那套 <c>full_name</c>/<c>stargazers_count</c> 友好得多。
    ''' 需要留意两处：
    '''   · <c>description</c> 是 <c>{en, zh}</c> 对象（都实测 4062/4062 有），优先取中文
    '''   · <c>npm</c>/<c>version</c> 只有约一半插件有 —— 缺失时安装链路要回退到 git
    ''' </remarks>
    Private Function ParseIndexResult(Body As String) As DshPluginSearchResult
        Dim root As JObject
        Try
            root = JObject.Parse(Body)
        Catch ex As Exception
            Throw New InvalidOperationException($"插件索引站返回的内容不是合法 JSON：{ex.Message}", ex)
        End Try

        Dim result As New DshPluginSearchResult With {
            .FromCache = False,
            .IsFullList = True,
            .PageSize = 0
        }
        result.IndexUpdatedAt = root("updated")?.ToString()

        '分类表：{ "ui": {"en": "User Interface", "zh": "界面"} , ... }
        Dim cats = TryCast(root("categories"), JObject)
        If cats IsNot Nothing Then
            For Each prop As KeyValuePair(Of String, JToken) In cats
                Dim co = TryCast(prop.Value, JObject)
                If co Is Nothing Then Continue For
                result.Categories(prop.Key) = If(co("zh")?.ToString(), prop.Key)
            Next
        End If

        Dim arr = TryCast(root("plugins"), JArray)
        If arr Is Nothing Then
            Throw New InvalidOperationException("插件索引站的 JSON 里没有 plugins 数组。")
        End If

        For Each item In arr
            Dim o = TryCast(item, JObject)
            If o Is Nothing Then Continue For

            '⚠️ **绝对不能**用 `owner + "/" + name` 拼 fullName。
            '   实测 4062 条里有 **431 条**（10.6%）的 name 带 `#子路径` 后缀，
            '   因为它们的插件在 monorepo 的子包里。例如：
            '     name = "dsh-forge-studio#plugin-notes"
            '     url  = "https://github.com/0x7A7A6572/dsh-forge-studio/tree/main/packages/plugin-notes"
            '   拼出来会变成 `0x7A7A6572/dsh-forge-studio#plugin-notes` —— 这不是合法仓库名，
            '   拿它去查已安装状态、拼 github: spec 全都会错。
            '   而 `url` 字段**实测 4062/4062 都能解析出干净的 owner/repo**，所以以它为准。
            Dim full As String = RepoFromGitHubUrl(o("url")?.ToString())
            If String.IsNullOrWhiteSpace(full) Then
                '兜底：url 缺失时才退回 owner/name（并把 # 后缀砍掉）
                Dim owner As String = If(o("owner")?.ToString(), "")
                Dim name As String = If(o("name")?.ToString(), "")
                Dim hash As Integer = name.IndexOf("#"c)
                If hash > 0 Then name = name.Substring(0, hash)
                full = If(String.IsNullOrWhiteSpace(owner), name, owner & "/" & name)
            End If
            If String.IsNullOrWhiteSpace(full) Then Continue For

            '索引站里 stars 可能是 null，也可能是个数；没有就是 0
            Dim starCount As Integer = 0
            If o("stars") IsNot Nothing AndAlso o("stars").Type <> JTokenType.Null Then
                Try
                    starCount = CInt(o("stars"))
                Catch
                    starCount = 0
                End Try
            End If

            '索引站里的 name 字段（可能带 # 子路径）留着当**展示名**，
            '因为它比仓库名更能说明「这个插件具体是哪个子包」。
            Dim displayName As String = If(o("name")?.ToString(), "").Trim()
            If displayName = "" Then displayName = full.Substring(full.IndexOf("/"c) + 1)

            '⚠️ 对象初始化式（{...}）里**不能有独立成行的注释** —— 会中断隐式续行，
            '   报一堆 BC30201/BC30370/BC30205。注释只能跟在有代码那行末尾。
            Dim info As New DshPluginInfo With {
                .FullName = full,
                .DisplayName = displayName,
                .HtmlUrl = o("url")?.ToString(),
                .NpmName = TrimOrNothing(o("npm")?.ToString()),
                .Version = TrimOrNothing(o("version")?.ToString()),
                .InstallCommand = TrimOrNothing(o("install")?.ToString()),
                .Category = TrimOrNothing(o("category")?.ToString()),
                .PageUrl = TrimOrNothing(o("page")?.ToString()),
                .Stars = starCount,
                .DefaultBranch = "main"
            }

            '简介优先取中文
            Dim desc = TryCast(o("description"), JObject)
            If desc IsNot Nothing Then
                Dim zh As String = desc("zh")?.ToString()
                Dim en As String = desc("en")?.ToString()
                If Not String.IsNullOrWhiteSpace(zh) Then
                    info.Description = zh.Trim()
                    info.HasChineseDesc = True
                Else
                    info.Description = If(en, "").Trim()
                End If
            Else
                info.Description = o("description")?.ToString()
            End If

            '收录日期 —— 索引站给的是 yyyy-MM-dd
            Dim added As String = o("added")?.ToString()
            Dim dt As DateTime
            If Not String.IsNullOrWhiteSpace(added) AndAlso
               DateTime.TryParse(added, Globalization.CultureInfo.InvariantCulture,
                                 Globalization.DateTimeStyles.None, dt) Then
                info.AddedAt = dt
                '没有真·更新时间时，用收录日期兜底，别在界面上显示「未知」
                info.UpdatedAt = dt
            End If

            result.Items.Add(info)
        Next

        result.TotalCount = result.Items.Count
        Return result
    End Function

    ''' <summary>空白字符串归一成 Nothing（JSON 里可能是 <c>""</c>）。</summary>
    Private Function TrimOrNothing(Value As String) As String
        If String.IsNullOrWhiteSpace(Value) Then Return Nothing
        Return Value.Trim()
    End Function

    ''' <summary>
    ''' 从一个 GitHub 仓库 URL 里抽出 <c>owner/repo</c>。
    ''' </summary>
    ''' <returns>抽不出来返回 Nothing。</returns>
    ''' <remarks>
    ''' 只取**前两段路径**，后面的 <c>/tree/main/packages/xxx</c> 之类一概丢掉 ——
    ''' 索引站里 431 个 monorepo 子包的 url 都是这个形态。
    ''' 实测索引站 4062 条 url **全部**能解析成功，所以这是可靠的 fullName 来源。
    ''' </remarks>
    Private Function RepoFromGitHubUrl(Url As String) As String
        If String.IsNullOrWhiteSpace(Url) Then Return Nothing
        Dim s As String = Url.Trim()

        '定位 github.com/ 之后的部分
        Dim marker As Integer = s.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase)
        If marker < 0 Then Return Nothing
        s = s.Substring(marker + "github.com/".Length)

        'query / fragment 一律砍掉
        For Each cut As Char In {"?"c, "#"c}
            Dim i As Integer = s.IndexOf(cut)
            If i >= 0 Then s = s.Substring(0, i)
        Next

        Dim parts As String() = s.Split("/"c)
        If parts.Length < 2 Then Return Nothing

        Dim owner As String = parts(0).Trim()
        Dim repo As String = parts(1).Trim()
        If repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase) Then
            repo = repo.Substring(0, repo.Length - 4)
        End If
        If owner = "" OrElse repo = "" Then Return Nothing

        '合法性粗筛：GitHub 的 owner/repo 只允许字母数字 . _ -
        For Each ch In owner & repo
            If Not (Char.IsLetterOrDigit(ch) OrElse ch = "."c OrElse ch = "_"c OrElse ch = "-"c) Then
                Return Nothing
            End If
        Next

        Return owner & "/" & repo
    End Function

    ''' <summary>
    ''' 从 GitHub topic 拉一页列表（回退数据源）。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 配额极紧（10 次/小时），只在用户显式切到该源时才会走到这里。
    ''' 直连不通时会依次试公共反代（只解决连通性，不解决配额）。
    ''' </remarks>
    Private Function FetchPluginsFromGitHub(TimeoutMs As Integer) As DshPluginSearchResult
        Dim attempts As New List(Of String) From {GitHubApiBase}
        For Each prefix In ProxyPrefixes
            attempts.Add(prefix.TrimEnd("/"c) & "/" & GitHubApiBase)
        Next

        Dim lastError As Exception = Nothing
        For Each baseUrl In attempts
            Try
                Dim url As String =
                    $"{baseUrl}/search/repositories?q=topic:{DshPluginTopic}&sort=stars&order=desc&per_page=50"
                Logger.Info($"DSH：插件市场请求 → {url}")

                Dim body As String = HttpGetString(url, TimeoutMs)
                Dim result As DshPluginSearchResult = ParseSearchResult(body)
                result.Source = If(baseUrl = GitHubApiBase, "GitHub 直连", "公共反代")

                WriteCache(result)
                Logger.Info($"DSH：插件市场拿到 {result.Items.Count} 项（共 {result.TotalCount}）")
                Return result
            Catch ex As Exception
                lastError = ex
                Logger.Warn($"DSH：插件市场请求失败（{baseUrl}）：{ex.Message}")
            End Try
        Next

        '3. 联网全挂 → 退回过期缓存，总比空手而归强
        Dim stale As DshPluginSearchResult = ReadCache()
        If stale IsNot Nothing AndAlso stale.Items.Count > 0 Then
            Logger.Warn("DSH：插件市场联网失败，退回使用过期缓存")
            stale.Source = "过期缓存（联网失败）"
            Return stale
        End If

        Throw New InvalidOperationException(
            "无法访问 GitHub 插件市场。" & vbCrLf & vbCrLf &
            "可能的原因：" & vbCrLf &
            "  · 网络不通或需要代理" & vbCrLf &
            "  · 匿名调用配额已用尽（搜索接口每小时 10 次，等一会儿再试）" & vbCrLf & vbCrLf &
            $"最后一次错误：{If(lastError IsNot Nothing, lastError.Message, "未知")}", lastError)
    End Function

    ''' <summary>解析 GitHub 搜索接口的返回体。</summary>
    Private Function ParseSearchResult(Body As String) As DshPluginSearchResult
        Dim root As JObject
        Try
            root = JObject.Parse(Body)
        Catch ex As Exception
            Throw New InvalidOperationException($"GitHub 返回的内容不是合法 JSON：{ex.Message}", ex)
        End Try

        'GitHub 在配额耗尽时返回 403 + message 字段，这里给个明确的说法
        Dim message As String = root("message")?.ToString()
        If Not String.IsNullOrWhiteSpace(message) AndAlso root("items") Is Nothing Then
            Throw New InvalidOperationException($"GitHub 拒绝了请求：{message}")
        End If

        Dim result As New DshPluginSearchResult With {.FromCache = False}
        result.TotalCount = If(root("total_count") Is Nothing, 0, CInt(root("total_count")))
        ' 记日志就好，别信这个数（见 DshPluginSearchResult.TotalCount 的注释）
        Logger.Info($"DSH：GitHub 报告的匹配总数 = {result.TotalCount}（topic 查询下不可信）")

        Dim arr = TryCast(root("items"), JArray)
        If arr Is Nothing Then Return result

        For Each item In arr
            Dim o = TryCast(item, JObject)
            If o Is Nothing Then Continue For

            Dim info As New DshPluginInfo With {
                .FullName = If(o("full_name")?.ToString(), ""),
                .Description = o("description")?.ToString(),
                .HtmlUrl = If(o("html_url")?.ToString(), ""),
                .Stars = If(o("stargazers_count") Is Nothing, 0, CInt(o("stargazers_count"))),
                .Language = o("language")?.ToString(),
                .DefaultBranch = If(o("default_branch")?.ToString(), "main")
            }
            Dim updated As String = o("updated_at")?.ToString()
            Dim dt As DateTime
            If Not String.IsNullOrWhiteSpace(updated) AndAlso
               DateTime.TryParse(updated, Globalization.CultureInfo.InvariantCulture,
                                 Globalization.DateTimeStyles.RoundtripKind, dt) Then
                info.UpdatedAt = dt
            End If

            If Not String.IsNullOrWhiteSpace(info.FullName) Then result.Items.Add(info)
        Next

        Return result
    End Function

    ''' <summary>
    ''' 按分类 + 关键词筛选并排序。
    ''' </summary>
    ''' <param name="Items">源列表（不会被修改）。</param>
    ''' <param name="Keyword">关键词，匹配 owner/repo 或简介；空则不过滤。</param>
    ''' <param name="Category">分类 key；空 / <c>"*"</c> 表示全部。</param>
    ''' <param name="Sort">排序方式。</param>
    ''' <returns>新的列表（引用复用 <see cref="DshPluginInfo"/> 对象）。</returns>
    ''' <remarks>
    ''' 4062 条数据在 UI 线程上做一次全量过滤 + 排序约 10~20 ms，
    ''' 所以这里直接同步做，不需要后台线程 —— 反而省掉一次跨线程封送。
    ''' 但**渲染**必须在 UI 线程上分批做（见 PageDownloadPlugin.RenderPlugins）。
    ''' </remarks>
    Public Function FilterAndSort(Items As IEnumerable(Of DshPluginInfo),
                                  Optional Keyword As String = Nothing,
                                  Optional Category As String = Nothing,
                                  Optional Sort As DshPluginSort = DshPluginSort.Stars) As List(Of DshPluginInfo)
        If Items Is Nothing Then Return New List(Of DshPluginInfo)

        Dim q As String = If(Keyword, "").Trim()
        Dim cat As String = If(Category, "").Trim()

        Dim filtered As IEnumerable(Of DshPluginInfo) = Items

        '分类筛选
        If cat <> "" AndAlso cat <> "*" Then
            filtered = filtered.Where(
                Function(p) String.Equals(p.Category, cat, StringComparison.OrdinalIgnoreCase))
        End If

        '关键词：空格分隔的多词按 AND 处理（"memory 存" 这种组合更精确）
        If q <> "" Then
            Dim terms As String() = q.Split({" "c, "　"c}, StringSplitOptions.RemoveEmptyEntries)
            For Each term In terms
                Dim t As String = term.ToLowerInvariant()
                filtered = filtered.Where(
                    Function(p)
                        Return (If(p.FullName, "").ToLowerInvariant().Contains(t)) OrElse
                               (If(p.Description, "").ToLowerInvariant().Contains(t)) OrElse
                               (If(p.NpmName, "").ToLowerInvariant().Contains(t)) OrElse
                               (If(p.Category, "").ToLowerInvariant().Contains(t))
                    End Function)
            Next
        End If

        Dim list As List(Of DshPluginInfo) = filtered.ToList()

        Select Case Sort
            Case DshPluginSort.Newest
                '没有收录日期的排最后（用 MinValue 兜底）
                list.Sort(Function(a, b)
                              Dim da As DateTime = If(a.AddedAt Is Nothing, DateTime.MinValue, CType(a.AddedAt, DateTime))
                              Dim db As DateTime = If(b.AddedAt Is Nothing, DateTime.MinValue, CType(b.AddedAt, DateTime))
                              Dim cmp As Integer = db.CompareTo(da)
                              If cmp <> 0 Then Return cmp
                              Return String.Compare(If(a.FullName, ""), If(b.FullName, ""), StringComparison.OrdinalIgnoreCase)
                          End Function)
            Case DshPluginSort.NameAsc
                list.Sort(Function(a, b) String.Compare(If(a.FullName, ""), If(b.FullName, ""),
                                                        StringComparison.OrdinalIgnoreCase))
            Case DshPluginSort.NpmFirst
                '有 npm 包的排前面（安装成功率最高），同档内按星标
                list.Sort(Function(a, b)
                              Dim cmp As Integer = b.HasNpm.CompareTo(a.HasNpm)
                              If cmp <> 0 Then Return cmp
                              cmp = b.Stars.CompareTo(a.Stars)
                              If cmp <> 0 Then Return cmp
                              Return String.Compare(If(a.FullName, ""), If(b.FullName, ""), StringComparison.OrdinalIgnoreCase)
                          End Function)
            Case Else
                list.Sort(Function(a, b)
                              Dim cmp As Integer = b.Stars.CompareTo(a.Stars)
                              If cmp <> 0 Then Return cmp
                              Dim da As DateTime = If(a.AddedAt Is Nothing, DateTime.MinValue, CType(a.AddedAt, DateTime))
                              Dim db As DateTime = If(b.AddedAt Is Nothing, DateTime.MinValue, CType(b.AddedAt, DateTime))
                              Return db.CompareTo(da)
                          End Function)
        End Select

        Return list
    End Function

    ''' <summary>
    ''' 从一批插件里统计各分类的数量（用于填充分类下拉框）。
    ''' </summary>
    ''' <param name="Items">插件列表。</param>
    ''' <param name="KnownCategories">索引站给的 key→中文名 映射。</param>
    ''' <returns>按数量降序的分类列表。</returns>
    Public Function BuildCategories(Items As IEnumerable(Of DshPluginInfo),
                                    Optional KnownCategories As Dictionary(Of String, String) = Nothing) As List(Of DshPluginCategory)
        Dim result As New List(Of DshPluginCategory)
        If Items Is Nothing Then Return result

        Dim counter As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
        For Each p In Items
            Dim k As String = If(p.Category, "").Trim()
            If k = "" Then Continue For
            If counter.ContainsKey(k) Then
                counter(k) += 1
            Else
                counter(k) = 1
            End If
        Next

        For Each kv In counter
            Dim zh As String = Nothing
            If KnownCategories IsNot Nothing Then KnownCategories.TryGetValue(kv.Key, zh)
            result.Add(New DshPluginCategory With {
                .Key = kv.Key,
                .NameZh = If(String.IsNullOrWhiteSpace(zh), kv.Key, zh),
                .NameEn = kv.Key,
                .Count = kv.Value
            })
        Next

        result.Sort(Function(a, b)
                        Dim cmp As Integer = b.Count.CompareTo(a.Count)
                        If cmp <> 0 Then Return cmp
                        Return String.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase)
                    End Function)
        Return result
    End Function

    ''' <summary>一次安装的计划：用什么 spec、从哪来、有没有坑。</summary>
    Public Class PluginInstallPlan
        ''' <summary>实际传给 pnpm 的 spec。</summary>
        Public Property Spec As String
        ''' <summary>来源：<c>npm</c> / <c>github</c> / <c>tarball</c> / <c>local</c>。</summary>
        Public Property Source As String
        ''' <summary>第几层分支得出的方案（①②③）—— 排查时用，不影响界面。</summary>
        Public Property Branch As String
        ''' <summary>package.json 里声明的包名（可能为空）。</summary>
        Public Property PackageName As String
        ''' <summary>该仓库的 package.json 里有没有 <c>dsh</c> 字段。</summary>
        Public Property IsDshPlugin As Boolean
        ''' <summary>给用户看的提醒；没有问题时为 Nothing。</summary>
        Public Property Warning As String

        ''' <summary>是否已知（而非探测出来的）结果 —— 索引站给的元信息可信，不必再联网。</summary>
        Public Property FromIndex As Boolean

        Public ReadOnly Property SourceText As String
            Get
                Select Case Source
                    Case "npm" : Return "npm 包"
                    Case "local" : Return "本地路径"
                    Case "gitlink" : Return "GitHub 仓库（链接导入）"
                    Case "tarball" : Return "发布包（.tgz）"
                    Case "gitsub" : Return "GitHub 仓库（monorepo 子包）"
                    Case Else : Return "GitHub 仓库（git 安装）"
                End Select
            End Get
        End Property
    End Class

    ''' <summary>
    ''' 为一次插件安装做计划：定 spec、判断它到底是不是 dsh 插件。
    ''' </summary>
    ''' <param name="Plugin">市场条目。</param>
    ''' <param name="TimeoutMs">单次请求超时。</param>
    ''' <remarks>
    ''' ⚠️ **不能直接把 package.json 里的 <c>name</c> 当 npm 包名用**。
    ''' 这是实测踩过的坑：GitHub 的 <c>topic:</c> 搜索很宽松，
    ''' 结果里混着大量**根本没发布到 npm 的普通仓库**（它们的 package.json 里
    ''' <c>name</c> 只是个本地包名）。拿它去 <c>pnpm add</c>，
    ''' pnpm 会去 npm 注册表找一个不存在的包，最后抛出一个完全看不懂的错：
    ''' <code>
    ''' ERR_PNPM_PACKAGE_MANAGER_ADD_RESOLVE_LATEST
    ''' Failed to decode metadata from https://registry.npmjs.org/open-design:
    ''' missing field 'dist-tags' at line 1 column 60
    ''' </code>
    ''' （npm 对不存在的包返回的是错误对象，里面当然没有 <c>dist-tags</c>。）
    '''
    ''' 所以必须**先去 npm 确认这个包真的存在、而且就是这个仓库发的**，
    ''' 确认不了就退回 <c>github:owner/repo</c>。
    ''' </remarks>
    Public Function PlanPluginInstall(Plugin As DshPluginInfo,
                                      Optional TimeoutMs As Integer = 15000) As PluginInstallPlan
        Dim plan As New PluginInstallPlan()
        If Plugin Is Nothing OrElse String.IsNullOrWhiteSpace(Plugin.FullName) Then
            plan.Spec = Nothing
            plan.Warning = "插件信息不完整。"
            Return plan
        End If

        '默认兜底：直接用 GitHub 仓库安装（pnpm 支持 github:owner/repo）
        plan.Spec = Plugin.GitHubSpec
        plan.Source = "github"

        '── ① 索引站给了 npm 包名 → 直接用，不用联网探测 ──
        '这是走索引站源时的主要路径，也是最省事的一条：
        '索引站的数据是它自己从各仓库爬/核对过的，比自己临时抓 package.json 更靠谱。
        If Plugin.HasNpm Then
            plan.Spec = Plugin.NpmName
            plan.Source = "npm"
            plan.PackageName = Plugin.NpmName
            plan.IsDshPlugin = True      '索引站里收录的都是 dsh 插件
            plan.FromIndex = True
            Logger.Info($"DSH：{Plugin.FullName} → 索引站给的 npm 包 {Plugin.NpmName}" &
                        If(String.IsNullOrWhiteSpace(Plugin.Version), "", $"@{Plugin.Version}"))
            Return plan
        End If

        '── ② 索引站没给 npm 包名 → 复用索引站自己的 install spec ──
        '索引站收录但没 npm 包，说明作者没发布到 npm，这时只能用索引站给的那条命令。
        '
        '⚠️ **绝对不能自己拼 `github:owner/repo`**。实测 4062 条里：
        '   · 431 条的插件在仓库子包里（`name` 带 `#子路径`）
        '   · 其中 151 条用 `#path:` 定位
        '     （例如 `github:ayahunter/dsh-trail#path:/packages/bundle`）
        '   · 另 23 条**只发布了 .tgz 发布包**，没有 npm 包也没有 #path
        '     （例如 `https://github.com/a/b/releases/latest/download/x.tgz`，
        '       而且索引站给这条加了双引号）
        '拼 `github:owner/repo` 会装到仓库根 —— 那通常没有 dsh 入口，
        '会失败、或者装了个 monorepo 的空壳。
        Dim specInfo = ClassifyIndexSpec(Plugin.InstallCommand)
        If Not String.IsNullOrWhiteSpace(specInfo.Value) Then
            plan.Spec = specInfo.Value
            Select Case specInfo.Kind
                Case "tarball"
                    plan.Source = "tarball"
                Case "git"
                    '带 `#path:` 的要标成 monorepo 子包，界面上提示更准确
                    plan.Source = If(specInfo.Value.Contains("#path:"), "gitsub", "github")
                Case Else
                    plan.Source = "npm"
            End Select
        End If

        '索引站里的插件可以确信是 dsh 插件（收录时筛过），不用再警告用户
        If Not String.IsNullOrWhiteSpace(Plugin.InstallCommand) Then
            plan.IsDshPlugin = True
            plan.FromIndex = True
            plan.PackageName = Plugin.NpmName
            Logger.Info($"DSH：{Plugin.FullName} 在索引站里没有 npm 包，" &
                        $"改用索引站给的 spec（{plan.Source}）：{plan.Spec}")
            Return plan
        End If

        '── ③ 既没 npm 也没索引信息（GitHub 源 / 手动导入）→ 老办法：抓 package.json 探测 ──
        Dim branch As String = If(String.IsNullOrWhiteSpace(Plugin.DefaultBranch), "main", Plugin.DefaultBranch)
        Dim pkg As JObject = Nothing
        Dim branchUsed As String = branch
        For Each candidate In {branch, "main", "master"}
            Try
                Dim body As String = HttpGetString(
                    $"https://raw.githubusercontent.com/{Plugin.FullName}/{candidate}/package.json", TimeoutMs)
                pkg = JObject.Parse(body)
                branchUsed = candidate
                Exit For
            Catch ex As Exception
                Logger.Warn($"DSH：读取 {Plugin.FullName}@{candidate} 的 package.json 失败：{ex.Message}")
            End Try
        Next

        If pkg Is Nothing Then
            plan.Warning = "读不到这个仓库的 package.json，无法确认它是不是 dsh 插件，将直接尝试用 git 安装。"
            Return plan
        End If

        '有没有 dsh 字段 = 是不是 dsh 插件
        plan.IsDshPlugin = pkg("dsh") IsNot Nothing

        Dim pkgName As String = pkg("name")?.ToString()
        If Not String.IsNullOrWhiteSpace(pkgName) Then plan.PackageName = pkgName.Trim()

        If Not plan.IsDshPlugin Then
            plan.Warning = "这个仓库的 package.json 里没有 dsh 字段 —— 它可能不是 dsh 插件" &
                           "（GitHub 的 topic 搜索比较宽松，会把只是提到 dsh 的项目也收进来）。"
        End If

        '── 只有 npm 上确实存在、且就是这个仓库发布的，才用包名安装 ──
        If Not String.IsNullOrWhiteSpace(plan.PackageName) Then
            If NpmPackageMatchesRepo(plan.PackageName, Plugin.FullName, TimeoutMs) Then
                plan.Spec = plan.PackageName
                plan.Source = "npm"
                Logger.Info($"DSH：{Plugin.FullName} → npm 包 {plan.PackageName}")
            Else
                Logger.Info($"DSH：{Plugin.FullName} 的包名 {plan.PackageName} 不在 npm 上（或不是这个仓库发的），改用 git 安装")
            End If
        End If

        Return plan
    End Function

    ''' <summary>
    ''' 为一个「手动导入」的条目做安装计划（决策 4-d）。
    ''' </summary>
    ''' <param name="Spec">用户输入的安装标识：npm 包名 / github:o/r / GitHub 链接 / 本地路径。</param>
    ''' <param name="TimeoutMs">单次请求超时。</param>
    ''' <returns>安装计划；无法识别时 <see cref="PluginInstallPlan.Spec"/> 为 Nothing。</returns>
    ''' <remarks>
    ''' 三种入口最终都归一成这里：
    '''   · GitHub 链接 <c>https://github.com/o/r</c> → <c>github:o/r</c>
    '''   · 本地文件夹 / tarball → 直接用路径（pnpm 支持 file: 与绝对路径）
    '''   · 剪贴板 → 自动判断属于上面哪一种
    '''
    ''' ⚠️ 与市场安装一样，**要先探测它是不是 dsh 插件**，
    ''' 非 dsh 插件走「警告式确认框」（决策 4-e），用户执意要装就继续。
    ''' </remarks>
    Public Function PlanManualImport(Spec As String,
                                     Optional TimeoutMs As Integer = 15000) As PluginInstallPlan
        Dim plan As New PluginInstallPlan()
        Dim raw As String = If(Spec, "").Trim()
        If raw = "" Then
            plan.Warning = "没有填写任何内容。"
            Return plan
        End If

        '── 本地路径（绝对路径 / 盘符 / UNC / file:）──
        Dim looksLocal As Boolean =
            raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase) OrElse
            raw.StartsWith("\\", StringComparison.Ordinal) OrElse
            (raw.Length >= 3 AndAlso Char.IsLetter(raw(0)) AndAlso raw(1) = ":"c AndAlso
             (raw(2) = "\"c OrElse raw(2) = "/"c))
        If looksLocal Then
            Dim pathOnly As String = If(raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase),
                                        raw.Substring(5), raw).Trim()
            If Not Directory.Exists(pathOnly) AndAlso Not File.Exists(pathOnly) Then
                plan.Warning = $"本地路径不存在：{pathOnly}"
                Return plan
            End If
            plan.Spec = pathOnly
            plan.Source = "local"
            '本地目录的 package.json 能直接读，顺便判断是不是 dsh 插件
            Dim pkgPath As String = If(Directory.Exists(pathOnly), Path.Combine(pathOnly, "package.json"), Nothing)
            If pkgPath IsNot Nothing AndAlso File.Exists(pkgPath) Then
                Try
                    Dim pkg As JObject = JObject.Parse(File.ReadAllText(pkgPath, Encoding.UTF8))
                    plan.IsDshPlugin = pkg("dsh") IsNot Nothing
                    plan.PackageName = pkg("name")?.ToString()
                    If Not plan.IsDshPlugin Then
                        plan.Warning = "这个目录的 package.json 里没有 dsh 字段 —— 它可能不是 dsh 插件。"
                    End If
                Catch ex As Exception
                    Logger.Warn($"DSH：读取本地 {pkgPath} 失败：{ex.Message}")
                    plan.Warning = "读不到本地目录的 package.json，无法确认它是不是 dsh 插件。"
                End Try
            Else
                plan.Warning = "本地路径下没有 package.json，无法确认它是不是 dsh 插件。"
            End If
            Return plan
        End If

        '── GitHub 链接 → owner/repo ──
        Dim fullName As String = ParseGitHubFullName(raw)
        If fullName IsNot Nothing Then
            '复用市场那条链路：先当成一个「只有 fullName 的条目」走探测
            plan = PlanPluginInstall(New DshPluginInfo With {
                                         .FullName = fullName,
                                         .HtmlUrl = "https://github.com/" & fullName,
                                         .DefaultBranch = "main"
                                     }, TimeoutMs)
            plan.Source = "gitlink"
            Return plan
        End If

        '── 剩下的一律当 npm 包名 / pnpm spec 处理 ──
        '"pkg@1.2.3" 这种带版本号的写法要认出来，别把 @ 当成 scope 分隔符
        plan.Spec = raw
        plan.Source = "npm"
        plan.PackageName = StripVersion(raw)
        'npm spec 无法可靠地判断是不是 dsh 插件（要下载整包），
        '所以标记为「未知」→ 让 UI 走警告式确认
        plan.IsDshPlugin = False
        plan.Warning = "这是一个 npm 包标识，无法在安装前确认它是不是 dsh 插件。"
        Return plan
    End Function

    ''' <summary>
    ''' 从各种 GitHub 链接形式里抽出 <c>owner/repo</c>。
    ''' </summary>
    ''' <returns>抽不出来返回 Nothing。</returns>
    ''' <remarks>
    ''' 支持：https://github.com/o/r、http://…、git@github.com:o/r.git、
    ''' github.com/o/r、o/r、github:o/r、以及带 /tree/main 之类尾巴的链接。
    ''' </remarks>
    Public Function ParseGitHubFullName(Text As String) As String
        If String.IsNullOrWhiteSpace(Text) Then Return Nothing
        Dim s As String = Text.Trim()

        '带前缀的归一化
        For Each prefix In {"git+", "git://", "ssh://git@", "https://", "http://", "github:"}
            If s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) Then
                s = s.Substring(prefix.Length)
            End If
        Next
        s = s.Replace("git@github.com:", "")
        If s.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase) Then
            s = s.Substring("github.com/".Length)
        End If

        '去掉 .git 与任何子路径（/tree/main、/blob/... 等）
        s = s.TrimEnd("/"c)
        If s.EndsWith(".git", StringComparison.OrdinalIgnoreCase) Then
            s = s.Substring(0, s.Length - 4)
        End If

        Dim parts As String() = s.Split("/"c)
        If parts.Length < 2 Then Return Nothing

        Dim owner As String = parts(0).Trim()
        Dim repo As String = parts(1).Trim()
        If owner = "" OrElse repo = "" Then Return Nothing

        '合法性粗筛：GitHub 的 owner/repo 只允许字母数字 . _ -
        For Each ch In owner & repo
            If Not (Char.IsLetterOrDigit(ch) OrElse ch = "."c OrElse ch = "_"c OrElse ch = "-"c) Then
                Return Nothing
            End If
        Next

        Return owner & "/" & repo
    End Function

    ''' <summary>把 <c>pkg@1.2.3</c> / <c>@scope/pkg@1.2.3</c> 里的版本号剥掉。</summary>
    Private Function StripVersion(Spec As String) As String
        If String.IsNullOrWhiteSpace(Spec) Then Return Spec
        Dim s As String = Spec.Trim()
        '@scope/pkg 开头那个 @ 不算分隔符，所以从下标 1 开始找
        Dim at As Integer = s.LastIndexOf("@"c)
        If at <= 0 Then Return s
        Return s.Substring(0, at)
    End Function

    ''' <summary>
    ''' 从索引站给的安装命令里抠出 pnpm 的 spec。
    ''' </summary>
    ''' <param name="InstallCommand">形如 <c>dsh plugin --profile web add &lt;spec&gt;</c>。</param>
    ''' <returns>spec；解析不出来返回 Nothing。</returns>
    ''' <remarks>
    ''' 为什么要复用索引站的命令而不是自己拼：
    ''' **monorepo 子包必须带 <c>#path:</c>**（如
    ''' <c>github:ayahunter/dsh-trail#path:/packages/bundle</c>），
    ''' 而仓库名本身推不出子路径 —— 那个信息只有索引站有。
    '''
    ''' 解析方式：找到第一个 <c>add</c> 之后的**第一个 token**。
    ''' 不去理解 spec 的语法（那归 pnpm 管），原样搬过来最不容易出错。
    ''' </remarks>
    ''' <summary>
    ''' 从索引站给的 <c>dsh plugin ... add &lt;spec&gt;</c> 命令里抠出 spec。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 两个必须处理的实测坑：
    ''' <list type="number">
    ''' <item>
    ''' **索引站会给 spec 加双引号**。例如
    ''' <c>dsh plugin --profile web add "https://github.com/a/b/releases/latest/download/x.tgz"</c>
    ''' —— 按空格切出来的 token 是带引号的字面量，
    ''' 直接交给 pnpm 会去找一个名字里含引号的包，必然失败。所以要**去掉首尾引号**。
    ''' </item>
    ''' <item>
    ''' 不能假设 spec 一定是 npm 包名或 <c>github:</c> 简写。实测 4062 条里有：
    ''' <c>github:owner/repo#path:/sub/dir</c>（151 条 monorepo 子包）、
    ''' 以及 <c>https://.../releases/download/.../*.tgz</c>（23 条只有发布包的）。
    ''' 这两类**都不能**退回 <c>github:owner/repo</c> ——
    ''' 那样装到的是 monorepo 根包，不是用户想装的那个子包。
    ''' </item>
    ''' </list>
    ''' </remarks>
    Public Function ExtractSpecFromInstallCommand(InstallCommand As String) As String
        Return ClassifyIndexSpec(InstallCommand).Value
    End Function

    ''' <summary>
    ''' 抠出索引站的 spec 并判断它属于哪一类。
    ''' </summary>
    ''' <returns>
    ''' Value = spec（抠不到为 Nothing）；Kind = <c>npm</c> / <c>git</c> / <c>tarball</c>。
    ''' </returns>
    Public Function ClassifyIndexSpec(InstallCommand As String) As (Value As String, Kind As String)
        If String.IsNullOrWhiteSpace(InstallCommand) Then Return (Nothing, Nothing)

        Dim parts As String() = InstallCommand.Trim().Split({" "c, vbTab}, StringSplitOptions.RemoveEmptyEntries)
        Dim spec As String = Nothing
        For i As Integer = 0 To parts.Length - 2
            If String.Equals(parts(i), "add", StringComparison.OrdinalIgnoreCase) Then
                spec = parts(i + 1).Trim()
                Exit For
            End If
        Next
        If String.IsNullOrWhiteSpace(spec) Then Return (Nothing, Nothing)

        '去掉首尾成对的引号（索引站有 23 条 .tgz 命令是带引号的）
        If spec.Length >= 2 Then
            Dim first As Char = spec(0)
            Dim last As Char = spec(spec.Length - 1)
            If (first = """"c AndAlso last = """"c) OrElse
               (first = "'"c AndAlso last = "'"c) Then
                spec = spec.Substring(1, spec.Length - 2).Trim()
            End If
        End If
        If spec = "" Then Return (Nothing, Nothing)

        Return (spec, ClassifySpecKind(spec))
    End Function

    ''' <summary>判断一个 spec 是 npm 包、git 仓库，还是发布包（tarball）。</summary>
    Public Function ClassifySpecKind(Spec As String) As String
        If String.IsNullOrWhiteSpace(Spec) Then Return "npm"
        Dim s As String = Spec.Trim()
        If s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) OrElse
           s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) Then
            'GitHub 的归档有两种：仓库地址（当 git 用）和 releases 产物（当 tarball 用）
            Dim low As String = s.ToLowerInvariant()
            If low.Contains("/releases/") OrElse low.EndsWith(".tgz") OrElse
               low.EndsWith(".tar.gz") Then
                Return "tarball"
            End If
            Return "git"
        End If
        If s.StartsWith("github:", StringComparison.OrdinalIgnoreCase) OrElse
           s.StartsWith("git+", StringComparison.OrdinalIgnoreCase) OrElse
           s.StartsWith("git@", StringComparison.OrdinalIgnoreCase) OrElse
           s.EndsWith(".git", StringComparison.OrdinalIgnoreCase) Then
            Return "git"
        End If
        Return "npm"
    End Function

    ''' <summary>
    ''' 从剪贴板文本里推断该用哪个导入入口（决策 4-d 的「剪贴板导入」）。
    ''' </summary>
    ''' <returns>推断出的 spec；无语义时返回 Nothing。</returns>
    ''' <remarks>
    ''' 剪贴板里可能是：GitHub 链接、npm 包名、本地路径、甚至一句带链接的说明文字。
    ''' 所以先做整体识别，失败再**从文本里抠出第一个 URL** 再试一次 ——
    ''' 用户复制的经常是「作者说可以用 xxx 安装 https://github.com/a/b」这种整段话。
    ''' </remarks>
    Public Function DetectImportSpecFromClipboard(Text As String) As String
        If String.IsNullOrWhiteSpace(Text) Then Return Nothing
        Dim s As String = Text.Trim()

        '① 整体就是一条可用 spec
        If ParseGitHubFullName(s) IsNot Nothing Then Return s
        If LooksLikeLocalPath(s) Then Return s
        '单行、没有空格、有斜杠或 @ → 当成 npm spec
        If Not s.Contains(vbLf) AndAlso Not s.Contains(" ") AndAlso
           (s.Contains("/") OrElse s.Contains("@")) Then
            Return s
        End If

        '② 从整段文字里抠 URL
        For Each token In s.Split({" "c, vbCr, vbLf, vbTab, "　"c}, StringSplitOptions.RemoveEmptyEntries)
            Dim t As String = token.Trim().TrimEnd("."c, ","c, "。"c, "，"c, ")"c, "）"c, "]"c)
            If t.Contains("github.com") AndAlso ParseGitHubFullName(t) IsNot Nothing Then Return t
        Next

        '③ 退化：只有一行且不像句子，就让调用方去试
        If Not s.Contains(vbLf) AndAlso s.Length <= 200 Then Return s
        Return Nothing
    End Function

    ''' <summary>粗略判断一个字符串像不像本地路径。</summary>
    Private Function LooksLikeLocalPath(Text As String) As Boolean
        If String.IsNullOrWhiteSpace(Text) Then Return False
        Dim s As String = Text.Trim()
        If s.StartsWith("file:", StringComparison.OrdinalIgnoreCase) Then Return True
        If s.StartsWith("\\", StringComparison.Ordinal) Then Return True
        Return s.Length >= 3 AndAlso Char.IsLetter(s(0)) AndAlso s(1) = ":"c AndAlso
               (s(2) = "\"c OrElse s(2) = "/"c)
    End Function

    ''' <summary>
    ''' 检查一个 npm 包名对应的包是否声明了 <c>dsh</c> 字段（决策 4-e 的判据）。
    ''' </summary>
    ''' <param name="PackageName">npm 包名。</param>
    ''' <param name="TimeoutMs">超时。</param>
    ''' <returns>
    ''' True = 确认是 dsh 插件；False = 确认不是；Nothing = 查不到（网络问题或包不存在）。
    ''' </returns>
    ''' <remarks>
    ''' 走 registry 的完整元数据接口取 <c>versions[latest]</c> 看有没有 <c>dsh</c> 字段。
    ''' 这是**安装前唯一**能确认 npm 包身份的办法 —— 毕竟不能为了确认就把整包下下来。
    ''' 查不到时返回 Nothing，由调用方按「未知」处理（走警告式确认）。
    ''' </remarks>
    Public Function ProbeNpmPackageIsDsh(PackageName As String,
                                         Optional TimeoutMs As Integer = 12000) As Boolean?
        If String.IsNullOrWhiteSpace(PackageName) Then Return Nothing
        Try
            Dim encoded As String = PackageName.Replace("/", "%2F")
            Dim body As String = HttpGetString($"https://registry.npmjs.org/{encoded}", TimeoutMs)
            Dim root As JObject = JObject.Parse(body)
            If root("dist-tags") Is Nothing Then Return False

            Dim latest As String = root("dist-tags")("latest")?.ToString()
            Dim versions = TryCast(root("versions"), JObject)
            If versions Is Nothing Then Return Nothing

            Dim verObj As JObject = Nothing
            If Not String.IsNullOrWhiteSpace(latest) Then
                verObj = TryCast(versions(latest), JObject)
            End If
            If verObj Is Nothing Then
                '没有 dist-tags 或版本对不上，退而取任意一个版本的元数据
                Dim first = versions.Properties().FirstOrDefault()
                If first IsNot Nothing Then verObj = TryCast(first.Value, JObject)
            End If
            If verObj Is Nothing Then Return Nothing

            Return verObj("dsh") IsNot Nothing
        Catch ex As Exception
            Logger.Warn($"DSH：探测 npm 包 {PackageName} 的 dsh 字段失败：{ex.Message}")
            Return Nothing
        End Try
    End Function

    ''' <summary>兼容旧调用：只取 spec。</summary>
    Public Function ResolveNpmPackageName(Plugin As DshPluginInfo,
                                          Optional TimeoutMs As Integer = 15000) As String
        Dim plan As PluginInstallPlan = PlanPluginInstall(Plugin, TimeoutMs)
        If plan Is Nothing OrElse plan.Source <> "npm" Then Return Nothing
        Return plan.Spec
    End Function

    ''' <summary>
    ''' 从 npm 包名里取出「不带 scope 的基名」。
    ''' </summary>
    ''' <remarks>
    ''' 用来和已安装列表做模糊比对：<c>@anonyjcy/dsh-j-space</c> 和 <c>dsh-j-space</c>
    ''' 在很多场合下指的是同一个东西，用户也可能用任一种拼法安装过。
    ''' </remarks>
    Public Function NpmBaseName(PackageName As String) As String
        If String.IsNullOrWhiteSpace(PackageName) Then Return ""
        Dim s As String = PackageName.Trim()
        Dim idx As Integer = s.LastIndexOf("/"c)
        If idx >= 0 AndAlso idx < s.Length - 1 Then Return s.Substring(idx + 1)
        Return s
    End Function

    ''' <summary>
    ''' 某个包名是否存在于 npm，且其 repository 指向给定的 GitHub 仓库。
    ''' </summary>
    ''' <param name="PackageName">npm 包名（可带 scope）。</param>
    ''' <param name="FullName">GitHub 的 <c>owner/repo</c>。</param>
    ''' <remarks>
    ''' 只判断"包名存在"是不够的 —— 同名包可能完全是另一个项目。
    ''' 再比对一次 repository.url 才能真正确认是同一个东西。
    ''' </remarks>
    Public Function NpmPackageMatchesRepo(PackageName As String, FullName As String,
                                          Optional TimeoutMs As Integer = 12000) As Boolean
        If String.IsNullOrWhiteSpace(PackageName) OrElse String.IsNullOrWhiteSpace(FullName) Then Return False

        Try
            'scope 里的 / 必须转义成 %2F，否则注册表当成路径
            Dim encoded As String = PackageName.Replace("/", "%2F")
            Dim body As String = HttpGetString($"https://registry.npmjs.org/{encoded}", TimeoutMs)
            Dim root As JObject = JObject.Parse(body)

            '没有 dist-tags 说明这不是一个已发布的包（npm 对不存在的包返回错误对象）
            If root("dist-tags") Is Nothing Then Return False

            'repository 可能是对象 {type,url}，也可能直接是字符串
            Dim repoToken As JToken = root("repository")
            Dim repoUrl As String = Nothing
            If repoToken IsNot Nothing Then
                If TypeOf repoToken Is JObject Then
                    repoUrl = repoToken("url")?.ToString()
                Else
                    repoUrl = repoToken.ToString()
                End If
            End If

            If String.IsNullOrWhiteSpace(repoUrl) Then
                '没有 repository 字段：包名存在，但无法确认是不是同一个仓库，保守起见不用
                Logger.Warn($"DSH：npm 包 {PackageName} 没有 repository 字段，无法确认来源")
                Return False
            End If

            '把 git+https://github.com/o/r.git、git://github.com/o/r、github:o/r 统统归一化
            Dim normalized As String = repoUrl.ToLowerInvariant()
            For Each prefix In {"git+", "git://", "https://", "http://", "ssh://git@"}
                If normalized.StartsWith(prefix, StringComparison.Ordinal) Then
                    normalized = normalized.Substring(prefix.Length)
                End If
            Next
            normalized = normalized.Replace("github.com/", "").Replace(".git", "").TrimEnd("/"c)

            Dim target As String = FullName.ToLowerInvariant().TrimEnd("/"c)
            Dim matched As Boolean = normalized = target OrElse normalized.EndsWith("/" & target, StringComparison.Ordinal)

            If Not matched Then
                Logger.Info($"DSH：npm 包 {PackageName} 的仓库是 {repoUrl}，与 {FullName} 不符")
            End If
            Return matched
        Catch ex As Exception
            Logger.Warn($"DSH：查询 npm 包 {PackageName} 失败：{ex.Message}")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 把 pnpm 的原始报错翻译成用户能看懂的话。
    ''' </summary>
    ''' <param name="Output">pnpm 的输出。</param>
    ''' <returns>翻译结果；认不出来时返回 Nothing（由调用方显示原文）。</returns>
    Public Function TranslatePnpmError(Output As String) As String
        If String.IsNullOrWhiteSpace(Output) Then Return Nothing
        Dim text As String = Output

        'npm 上找不到这个包（最常见：把 GitHub 仓库名当成了 npm 包名）
        If text.Contains("ERR_PNPM_PACKAGE_MANAGER_ADD_RESOLVE_LATEST") OrElse
           text.Contains("missing field 'dist-tags'") OrElse
           text.Contains("ERR_PNPM_FETCH_404") OrElse
           text.Contains("404 Not Found - GET https://registry.npmjs.org") Then
            Return "在 npm 上找不到这个包。" & vbCrLf &
                   "这个仓库很可能没有发布到 npm —— 本程序会改用 GitHub 仓库方式安装，" &
                   "如果仍然失败，说明它不能直接用 pnpm 安装（可能不是 dsh 插件，或是 monorepo）。"
        End If

        If text.Contains("ERR_PNPM_GIT_UNKNOWN_ERROR") OrElse text.Contains("git") AndAlso text.Contains("not found") Then
            Return "无法从 GitHub 拉取仓库。请确认本机已安装 git，并且能访问 github.com" &
                   "（可以先用「网络工具箱」里的 IP 优选加速）。"
        End If

        If text.Contains("ERR_PNPM_NO_MATCHING_VERSION") Then
            Return "找不到符合要求的版本。可能是版本号写错了，或者该包已下架。"
        End If

        If text.Contains("ERR_PNPM_EPERM") OrElse text.Contains("EPERM") OrElse text.Contains("EBUSY") Then
            Return "文件被占用。请先停止该实例正在运行的 DSH 服务，再重试。"
        End If

        If text.Contains("ERR_PNPM_FETCH_") OrElse text.Contains("ETIMEDOUT") OrElse text.Contains("ENOTFOUND") Then
            Return "网络请求失败。可以换一个 npm 镜像源，或用「网络工具箱」检查连通性。"
        End If

        If text.Contains("prepare") AndAlso text.Contains("allowBuilds") Then
            Return "该包在安装时需要执行构建脚本，被 pnpm 拦下了。请按提示把包名加进 allowBuilds 后重试。"
        End If

        Return Nothing
    End Function

#End Region

#Region "已安装插件"

    ''' <summary>
    ''' 读取某个实例当前已安装的插件（直接看 profile 目录的 package.json）。
    ''' </summary>
    ''' <returns>包名 → 版本号。目录或文件不存在时返回空字典。</returns>
    ''' <remarks>
    ''' 为什么不去解析 <c>dsh plugin ... list</c> 的输出：
    ''' 那只是把 pnpm 的文本输出透传出来，格式随 pnpm 版本变化，
    ''' 解析它既脆又慢（还要起一个 node 进程）。
    ''' profile 目录的 package.json 是 pnpm 自己维护的真相来源，读它最稳。
    ''' </remarks>
    Public Function ListInstalledPlugins(Instance As DshInstance,
                                         Optional Profile As String = Nothing) As Dictionary(Of String, String)
        Dim result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

        Dim path As String = ProfilePackageJsonOf(Instance, Profile)
        If Not DshRuntime.FileExistsSafe(path) Then Return result

        Try
            Dim root As JObject = JObject.Parse(File.ReadAllText(path, Encoding.UTF8))
            Dim deps = TryCast(root("dependencies"), JObject)
            If deps Is Nothing Then Return result

            For Each prop As KeyValuePair(Of String, JToken) In deps
                result(prop.Key) = prop.Value?.ToString()
            Next
        Catch ex As Exception
            Logger.Error(ex, $"DSH：读取已安装插件失败：{path}")
        End Try

        Return result
    End Function

#End Region

#Region "安装 / 卸载"

    ''' <summary>插件操作的进度回调。</summary>
    ''' <param name="Stage">阶段文案。</param>
    ''' <param name="Percent">0~1；负数表示不确定。</param>
    Public Delegate Sub DshPluginProgressHandler(Stage As String, Percent As Double)

    ''' <summary>
    ''' 安装一个插件到指定实例。
    ''' </summary>
    ''' <param name="Instance">目标实例（决定 DSH_HOME）。</param>
    ''' <param name="Spec">pnpm 的包标识：npm 包名 / <c>github:owner/repo</c> / 本地路径 / tarball。</param>
    ''' <param name="Progress">进度回调。</param>
    ''' <exception cref="InvalidOperationException">pnpm 缺失或安装失败。</exception>
    ''' <remarks>
    ''' 阻塞调用，请放在后台线程。
    ''' 安装期间 dsh 服务**不需要**重启 —— 插件是在下次启动 profile 时加载的，
    ''' 但正在运行的服务不会热加载新插件。UI 上要提示用户重启服务。
    ''' </remarks>
    Public Sub InstallPlugin(Instance As DshInstance, Spec As String,
                             Optional Progress As DshPluginProgressHandler = Nothing)
        RunPluginCommand(Instance, {"add", Spec}, Progress, "安装")
    End Sub

    ''' <summary>
    ''' 一次安装多个插件。
    ''' </summary>
    ''' <param name="Instance">目标实例。</param>
    ''' <param name="Specs">pnpm 的包标识列表。</param>
    ''' <param name="Progress">进度回调。</param>
    ''' <remarks>
    ''' 刻意**合并成一条 <c>pnpm add</c>** 而不是循环调用 <see cref="InstallPlugin"/>：
    ''' <list type="bullet">
    ''' <item>N 次调用 = N 次 pnpm 冷启动 + N 次依赖树求解，装十几个插件要等很久</item>
    ''' <item>合并成一次，pnpm 能一次性把公共依赖去重，装出来的树也更小</item>
    ''' </list>
    ''' 给「从其他 dsh 环境同步插件」用的就是这条路径。
    ''' </remarks>
    Public Sub InstallPlugins(Instance As DshInstance, Specs As IEnumerable(Of String),
                              Optional Progress As DshPluginProgressHandler = Nothing)
        If Specs Is Nothing Then Return
        Dim list As List(Of String) = Specs.Where(Function(s) Not String.IsNullOrWhiteSpace(s)).ToList()
        If list.Count = 0 Then Return

        Dim args As New List(Of String) From {"add"}
        args.AddRange(list)
        RunPluginCommand(Instance, args.ToArray(), Progress, "安装")
    End Sub

    ''' <summary>从指定实例卸载一个插件。</summary>
    Public Sub UninstallPlugin(Instance As DshInstance, Spec As String,
                               Optional Progress As DshPluginProgressHandler = Nothing)
        RunPluginCommand(Instance, {"remove", Spec}, Progress, "卸载")
    End Sub

    ''' <summary>
    ''' 把 pnpm 的一行输出翻译成给用户看的进度文案。
    ''' </summary>
    ''' <remarks>
    ''' ⭐ 为什么需要它（真实痛点）：
    ''' 插件安装要联网下载依赖，国内直连 npmjs.org 经常超时。实测一轮安装
    ''' 出现过这些日志：
    ''' <code>
    ''' [WARN] GET https://registry.npmjs.org/...tgz error
    '''        (os error 10054 远程主机强迫关闭了一个现有的连接) — 2
    ''' [WARN] Will retry in 1m. 0 retries left.
    ''' [WARN] Request took 150672ms: https://registry.npmjs.org/@lezer%2Frust
    ''' [WARN] Tarball download average speed 25 KiB/s ... is below 50 KiB/s
    ''' </code>
    ''' 而界面上的进度浮层只会说「正在安装 E:\...\gal-view」——
    ''' **用户看到卡十几分钟，会以为程序死了**，然后强制结束 PCL，
    ''' 留下半装的环境（比"等久一点"糟糕得多）。
    '''
    ''' 这里把关键行转成人话透传上去，让用户知道"它在动，只是网络慢"。
    ''' 只识别几类有意义的行，其余一律忽略 —— 不要把 pnpm 的原始刷屏
    ''' 糊到进度条上（那反而看不清）。
    ''' </remarks>
    Private Sub ReportPnpmLine(Progress As DshPluginProgressHandler,
                               Line As String, ActionName As String)
        If Progress Is Nothing OrElse String.IsNullOrWhiteSpace(Line) Then Return
        Try
            Dim t As String = Line.Trim()

            ' ① 网络重试 —— 最要紧的一类，用户最需要知道
            If t.Contains("Will retry in") Then
                ' 形如 "[WARN] Will retry in 1m. 0 retries left."
                Dim retries As String = ""
                Dim m As Text.RegularExpressions.Match =
                    Text.RegularExpressions.Regex.Match(t, "(\d+)\s*retr(?:y|ies)\s+left")
                If m.Success Then retries = $"（还剩 {m.Groups(1).Value} 次重试）"
                Report(Progress, $"网络不稳，正在重试{retries}……（可以继续等，不用关掉 PCL）", -1)
                Return
            End If

            If t.Contains("error (Failed to fetch") OrElse
               t.Contains("operation timed out") OrElse
               t.Contains("远程主机强迫关闭") Then
                Report(Progress, "下载超时，正在重试……（网络较慢时插件安装可能要几分钟）", -1)
                Return
            End If

            ' ② 慢速下载
            If t.Contains("is below") AndAlso t.Contains("KiB/s") Then
                Report(Progress, "下载速度较慢，请耐心等待……", -1)
                Return
            End If

            ' ③ pnpm 的进度行 —— 直接透传，它本身就带 "downloaded N" 这种信息
            If t.Contains("Progress: resolved") Then
                Dim m As Text.RegularExpressions.Match =
                    Text.RegularExpressions.Regex.Match(t, "downloaded\s+(\d+).*?added\s+(\d+)")
                If m.Success Then
                    Report(Progress, $"正在下载依赖（已下载 {m.Groups(1).Value} 个，已装入 {m.Groups(2).Value} 个）……", -1)
                End If
                Return
            End If

            ' ④ 供应链闸门通过
            If t.Contains("Lockfile passes supply-chain policies") Then
                Report(Progress, "依赖清单已通过供应链校验，开始下载……", -1)
                Return
            End If

            ' ⑤ 完成
            If t.Contains("Done in") Then
                Report(Progress, $"{ActionName}完成，正在收尾……", -1)
                Return
            End If
        Catch ex As Exception
            ' 翻译失败绝不能影响安装本身
            Logger.Warn(ex, "DSH：解析 pnpm 输出行失败（已忽略）")
        End Try
    End Sub

    ''' <summary>
    ''' 判断这轮 pnpm 输出是不是网络类失败。
    ''' </summary>
    ''' <remarks>
    ''' 用于给出「换个镜像源」的建议。判据来自实测日志：
    ''' <list type="bullet">
    ''' <item><c>operation timed out</c> —— 请求超时（实测单次能到 150 秒）</item>
    ''' <item><c>os error 10054</c> / <c>远程主机强迫关闭</c> —— 连接被重置</item>
    ''' <item><c>Failed to fetch</c> —— 拉取失败（网络层）</item>
    ''' <item><c>is below ... KiB/s</c> —— 速度低于阈值，pnpm 会判为不可接受</item>
    ''' <item><c>ECONNRESET</c> / <c>ETIMEDOUT</c> / <c>ENOTFOUND</c> —— 常见网络错误码</item>
    ''' </list>
    ''' 刻意**不**把普通的 404 / 包名拼错算进来 —— 那是另一类问题，换源没用。
    ''' </remarks>
    Private Function IsNetworkFailure(Tail As List(Of String)) As Boolean
        If Tail Is Nothing OrElse Tail.Count = 0 Then Return False
        Try
            For Each line As String In Tail
                If String.IsNullOrWhiteSpace(line) Then Continue For
                If line.Contains("operation timed out") OrElse
                   line.Contains("os error 10054") OrElse
                   line.Contains("远程主机强迫关闭") OrElse
                   line.Contains("Failed to fetch") OrElse
                   line.Contains("ECONNRESET") OrElse
                   line.Contains("ETIMEDOUT") OrElse
                   line.Contains("ENOTFOUND") OrElse
                   line.Contains("is below") AndAlso line.Contains("KiB/s") Then
                    Return True
                End If
            Next
            Return False
        Catch ex As Exception
            Logger.Warn(ex, "DSH：判断网络失败失败（按非网络处理）")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 执行一次 <c>dsh plugin</c> 命令。
    ''' </summary>
    ''' <param name="Instance">目标实例。</param>
    ''' <param name="PnpmArgs">要转发给 pnpm 的参数。</param>
    ''' <param name="Progress">进度回调。</param>
    ''' <param name="ActionName">动作名（"安装" / "卸载"），用于文案。</param>
    Private Sub RunPluginCommand(Instance As DshInstance,
                                 PnpmArgs As String(),
                                 Progress As DshPluginProgressHandler,
                                 ActionName As String)
        If Instance Is Nothing Then Throw New ArgumentNullException(NameOf(Instance))
        If PnpmArgs Is Nothing OrElse PnpmArgs.Length = 0 Then
            Throw New ArgumentException("缺少要转发给 pnpm 的参数。", NameOf(PnpmArgs))
        End If

        '运行时环境必须先就绪（要有 node 与 pnpm）
        Report(Progress, "检测运行环境", -1)
        Dim runtime = DshInstaller.EnsureRuntime(
            Sub(stage As DshInstaller.DshInstallStage, text As String, pct As Double)
                Report(Progress, text, pct)
            End Sub)

        If String.IsNullOrWhiteSpace(runtime.DshEntry) Then
            Throw New InvalidOperationException("dsh 尚未安装，无法管理插件。")
        End If
        If String.IsNullOrWhiteSpace(runtime.PnpmExe) Then
            Throw New InvalidOperationException(
                "未找到 pnpm，无法管理插件。" & vbCrLf &
                "pnpm 只影响插件管理，不影响 dsh 的基础运行。")
        End If

        Dim profileName As String =
            If(String.IsNullOrWhiteSpace(Instance.Profile), ModDSH.DshProfileName, Instance.Profile)

        '参数里含空格或特殊字符时要加引号，否则会被拆成多个参数
        Dim quoted As String = String.Join(" ", PnpmArgs.Select(Function(a) QuoteArg(a)))
        Dim args As String = $"""{runtime.DshEntry}"" plugin --profile {profileName} {quoted}"

        Report(Progress, $"正在{ActionName}：{PnpmArgs.Last()}", -1)
        Logger.Info($"DSH[{Instance.DisplayName}]：插件命令：{runtime.NodeExe} {args}")

        '工作目录设在实例的 profile 目录下 —— dsh 的 plugin 子命令是
        '「在 profile 目录里跑 pnpm」，cwd 影响 pnpm 找 workspace 的方式
        Dim workDir As String = ProfileDirOf(Instance, profileName)
        If Not Directory.Exists(workDir) Then Directory.CreateDirectory(workDir)

        Dim psi As New ProcessStartInfo(runtime.NodeExe, args) With {
            .UseShellExecute = False,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .CreateNoWindow = True,
            .StandardOutputEncoding = Encoding.UTF8,
            .StandardErrorEncoding = Encoding.UTF8,
            .WorkingDirectory = workDir
        }

        '⚠️ 环境变量必须走 DshRuntime 的封装（见 DshRuntime.SetEnvSafe 的注释）
        DshRuntime.SeedEnvironment(psi)
        DshRuntime.SetEnvSafe(psi, "DSH_HOME", Instance.HomeDir)
        DshRuntime.SetEnvSafe(psi, "NO_COLOR", "1")
        'dsh 的 plugin 子命令内部要调 pnpm，必须让它在 PATH 上找到
        DshRuntime.PrependPath(psi,
                               Path.GetDirectoryName(runtime.PnpmExe),
                               Path.GetDirectoryName(runtime.NodeExe))
        '换源也一并支持 —— 用户在「镜像版本」页选过的源这里同样生效
        DshRuntime.SetEnvSafe(psi, "NPM_CONFIG_REGISTRY", DshRegistry.DshCurrentMirror.Url)

        Dim tail As New List(Of String)

        ' ⭐ pnpm 有两道「闸门」会让安装以非零码退出，但它们**都是可以自动放行的**：
        '   ① allowBuilds —— 依赖带安装脚本时，pnpm 默认拦下来并且拒绝返回 0
        '      （ERR_PNPM_IGNORED_BUILDS）。错误里会列出包名，照着写进 profile 的
        '      pnpm-workspace.yaml 即可。dsh 自己的安装流程就是这么干的。
        '   ② minimumReleaseAge —— pnpm 的「新发布 24 小时内不装」供应链保护。
        '      插件生态更新极快，经常会撞上。
        '   以前这两条只给用户一句「请自己去改 yaml」，体验很差
        '   （实测用户在旧环境里就是手工调这个文件才装上的）。
        '   现在改成：失败 → 解析错误 → 自动补齐配置 → 重试。
        '   最多重试 3 轮，且**每轮都必须真的改动了配置**才继续，
        '   避免在无法自动修复的错误上空转。
        Dim attempt As Integer = 0
        While True
            attempt += 1
            tail.Clear()

            Dim exitCode As Integer = 0
            Using p As New Process()
                p.StartInfo = psi
                AddHandler p.OutputDataReceived,
                    Sub(sender, e)
                        If e.Data Is Nothing Then Return
                        TrackTail(tail, e.Data)
                        Logger.Info($"DSH[plugin] {e.Data}")
                        ReportPnpmLine(Progress, e.Data, ActionName)
                    End Sub
                AddHandler p.ErrorDataReceived,
                    Sub(sender, e)
                        If e.Data Is Nothing Then Return
                        TrackTail(tail, e.Data)
                        Logger.Warn($"DSH[plugin] {e.Data}")
                        ReportPnpmLine(Progress, e.Data, ActionName)
                    End Sub

                p.Start()
                p.BeginOutputReadLine()
                p.BeginErrorReadLine()

                ' 插件安装要下载依赖，给足时间。
                ' ⚠️ 从 10 分钟放宽到 30 分钟：国内直连 npmjs.org 实测会出现
                '    单次请求 150 秒（`Request took 150672ms`）、
                '    下载速度 25 KiB/s 的情况，10 分钟**真的不够** ——
                '    会被误判成"超时"然后杀掉一个其实在正常推进的安装，
                '    留下一半装好的环境。宁可等久一点，也不要半途而废。
                If Not p.WaitForExit(1800000) Then
                    Try
                        p.Kill()
                    Catch
                    End Try
                    Throw New InvalidOperationException(
                        $"{ActionName}超时（超过 30 分钟）。这通常是网络问题 —— " &
                        "可以换个镜像源后重试。最后输出：" & vbCrLf &
                        String.Join(vbCrLf, tail))
                End If
                exitCode = p.ExitCode
            End Using

            If exitCode = 0 Then Exit While

            ' ── 失败：看看能不能自动补配置再试一次 ──
            Dim patched As Boolean = False
            Try
                patched = PatchProfilePnpmConfig(workDir, tail)
            Catch ex As Exception
                Logger.Warn(ex, "DSH：自动修补 profile 的 pnpm 配置失败")
            End Try

            If patched AndAlso attempt < 3 Then
                Report(Progress, $"pnpm 拦下了构建脚本，已自动放行，正在重试（第 {attempt + 1} 次）……", -1)
                Logger.Info($"DSH[plugin]：检测到 pnpm 闸门，已修补 {workDir}\pnpm-workspace.yaml，重试第 {attempt + 1} 次")
                Continue While
            End If

            ' ── 补不了：把原因讲清楚 ──
            Dim hint As String = ""
            If tail.Any(Function(l) l.Contains("pnpm was not found")) Then
                hint = vbCrLf & vbCrLf & "pnpm 未被找到。可以在「设置 → 启动」里让它自动装配。"
            ElseIf patched Then
                hint = vbCrLf & vbCrLf &
                       "已经自动放行过构建脚本但仍然失败，请查看上面的 pnpm 输出。" & vbCrLf &
                       $"配置文件：{Path.Combine(workDir, "pnpm-workspace.yaml")}"
            ElseIf tail.Any(Function(l) l.Contains("allowBuilds")) Then
                hint = vbCrLf & vbCrLf &
                       "该插件的依赖需要跑安装脚本，而 pnpm 默认拦截。" & vbCrLf &
                       $"请把错误里列出的包名加进 {Path.Combine(workDir, "pnpm-workspace.yaml")} 的 allowBuilds 后重试。"
            End If

            ' ⭐ 网络类失败 → 主动提示换源
            ' 这是国内用户最常遇到的失败原因（直连 npmjs.org 超时 / 连接被重置），
            ' 而"换个镜像源"往往一次就好。不提示的话用户只会以为是软件坏了。
            If IsNetworkFailure(tail) Then
                Dim cur As DshRegistry.DshMirror = DshRegistry.DshCurrentMirror
                hint &= vbCrLf & vbCrLf &
                        "看起来是**网络问题**（下载超时或连接被重置），不是插件本身的问题。" & vbCrLf &
                        $"当前用的是「{cur.Name}」。建议换成国内镜像源后重试：" & vbCrLf &
                        "「下载 → 镜像版本 → 镜像源」里选「npmmirror（淘宝）」或「腾讯云镜像」。"
            End If

            Throw New InvalidOperationException(
                $"{ActionName}失败（退出码 {exitCode}）。最后输出：" & vbCrLf &
                String.Join(vbCrLf, tail) & hint)
        End While

        Report(Progress, $"{ActionName}完成", 1)
        Logger.Info($"DSH[{Instance.DisplayName}]：插件{ActionName}完成")
    End Sub

    ''' <summary>
    ''' 解析 pnpm 的失败输出，把 profile 的 <c>pnpm-workspace.yaml</c> 补成能过闸门的样子。
    ''' </summary>
    ''' <param name="ProfileDir">profile 目录（pnpm 的工作目录）。</param>
    ''' <param name="Tail">pnpm 的输出尾部。</param>
    ''' <returns>是否真的改动了配置（没改动就不值得重试）。</returns>
    ''' <remarks>
    ''' 处理三种情况：
    ''' <list type="number">
    ''' <item>
    ''' <b>占位符</b>：profile 初建时 dsh 写的是
    ''' <c>allowBuilds: { 某包: set this to true or false }</c> —— 这是**模板占位符**，
    ''' pnpm 不认，会直接报错。落定成 <c>false</c>
    ''' （含义是「已确认，但不去构建」，与 <c>true</c> 相比更安全：
    ''' 不执行第三方安装脚本，用包自带的预编译产物）。
    ''' </item>
    ''' <item>
    ''' <b>被拦下的构建脚本</b>：从 <c>Ignored build scripts: a@1, b@2</c> 里
    ''' 抠出包名补进 <c>allowBuilds</c>。
    ''' </item>
    ''' <item>
    ''' <b>24 小时新包闸门</b>：<c>minimumReleaseAge</c>。插件生态更新快，很容易撞上。
    ''' 这里退化成把该 profile 的闸门关掉（<c>minimumReleaseAge: 0</c>）——
    ''' 因为逐个列举 <c>minimumReleaseAgeExclude</c> 需要解析具体版本号，
    ''' 而且插件是用户主动要装的，风险由用户承担。
    ''' </item>
    ''' </list>
    ''' </remarks>
    Private Function PatchProfilePnpmConfig(ProfileDir As String, Tail As List(Of String)) As Boolean
        If String.IsNullOrWhiteSpace(ProfileDir) Then Return False
        If Tail Is Nothing OrElse Tail.Count = 0 Then Return False

        Dim joined As String = String.Join(" ", Tail)
        Dim names As New List(Of String)()
        Dim needAgeOff As Boolean = False

        ' ── ① 被拦下的构建脚本 ──
        Dim marker As String = "Ignored build scripts:"
        Dim idx As Integer = joined.IndexOf(marker, StringComparison.OrdinalIgnoreCase)
        If idx >= 0 Then
            Dim rest As String = joined.Substring(idx + marker.Length)
            ' 后面跟着的 help 提示不属于包名列表
            Dim helpIdx As Integer = rest.IndexOf("help:", StringComparison.OrdinalIgnoreCase)
            If helpIdx >= 0 Then rest = rest.Substring(0, helpIdx)

            ' ⚠️ 只按**逗号**切分，然后**去掉 token 内部的空白**。
            '
            ' 踩过的坑（实测，代价很隐蔽）：pnpm 的这行提示会**按终端宽度折行**，
            ' 一个包名可能被拆到两行上：
            '     Ignored build scripts: cloudflared@0.7.3, cpu-features@0.0.10, node-
            '     pty@1.0.0, ssh2@1.17.0
            ' 而这些行在调用方是用**空格**拼起来的（String.Join(" ", Tail)），
            ' 于是 `node-pty` 变成了 `node-` 和 `pty` 两个 token，
            ' 双双被写进 allowBuilds → 配置里出现 `'node-': false` 和 `'pty': false`
            ' 两条垃圾键（用户机器上真实出现过）。
            '
            ' 按逗号切分 + 抹掉内部空白，正好能把折行拼回去：
            '     " node- pty@1.0.0"  →  "node-pty@1.0.0"
            ' 而正常的逗号分隔列表（含折行）也不受影响。
            For Each rawToken In rest.Split({","c}, StringSplitOptions.RemoveEmptyEntries)
                Dim cleaned As String = rawToken.Replace(" ", "").Replace(vbTab, "").Replace(vbCr, "").Replace(vbLf, "")
                cleaned = cleaned.Trim().TrimEnd("."c, ";"c)
                Dim nm As String = StripPackageVersion(cleaned)
                ' 再校验一次包名形状 —— 解析错了宁可漏掉一个，也不要往配置里写垃圾键
                If IsPlausiblePackageName(nm) Then
                    If Not names.Contains(nm) Then names.Add(nm)
                ElseIf Not String.IsNullOrWhiteSpace(nm) Then
                    Logger.Warn($"DSH：从 pnpm 提示里解析出可疑包名，已跳过：{nm}")
                End If
            Next
        End If

        ' ── ② 新包年龄闸门 ──
        If joined.IndexOf("minimumReleaseAge", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
           joined.IndexOf("minimum release age", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
           joined.IndexOf("release age", StringComparison.OrdinalIgnoreCase) >= 0 Then
            needAgeOff = True
        End If

        ' ── 写文件 ──
        Dim wsPath As String = Path.Combine(ProfileDir, "pnpm-workspace.yaml")
        Dim lines As New List(Of String)()
        Try
            If File.Exists(wsPath) Then lines.AddRange(File.ReadAllLines(wsPath, Encoding.UTF8))
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：读取 {wsPath} 失败，将重写")
            lines.Clear()
        End Try

        If lines.Count = 0 Then
            lines.Add("packages:")
            lines.Add("  - .")
            lines.Add("")
            lines.Add("nodeLinker: hoisted")
            lines.Add("autoInstallPeers: false")
        End If

        Dim changed As Boolean = False

        ' ① 占位符落定
        For i As Integer = 0 To lines.Count - 1
            If lines(i).Contains("set this to true or false") Then
                lines(i) = lines(i).Replace("set this to true or false", "false")
                changed = True
            End If
        Next

        ' ①b 清掉 allowBuilds 里**明确的折行残留键**
        ' 早期版本的解析 bug 会把折行的包名劈成两半（`node-pty` → `node-` + `pty`），
        ' 前半截以 `-` 结尾 —— 那不是合法 npm 包名，是铁证。
        ' 顺手清掉，用户不用去改配置文件。
        ' （只清以 `-` / `.` 结尾的；像 `pty` 这种形状合法、只是多余的键
        '   无法与真实包名区分，留着无害 —— pnpm 会忽略未安装的包。）
        Dim inAllow As Boolean = False
        Dim stale As New List(Of Integer)()
        For i As Integer = 0 To lines.Count - 1
            Dim rawLine As String = lines(i)
            Dim tLine As String = rawLine.Trim()
            If tLine.StartsWith("allowBuilds:", StringComparison.Ordinal) Then
                inAllow = True
                Continue For
            End If
            If Not inAllow Then Continue For
            ' 非空、非注释、且**顶格** → 离开 allowBuilds 区块
            If tLine.Length > 0 AndAlso Not tLine.StartsWith("#"c) AndAlso
               Not (rawLine.StartsWith(" ") OrElse rawLine.StartsWith(vbTab)) Then
                inAllow = False
                Continue For
            End If
            If tLine.Length = 0 OrElse tLine.StartsWith("#"c) Then Continue For
            Dim colon As Integer = tLine.IndexOf(":"c)
            If colon <= 0 Then Continue For
            Dim keyName As String = tLine.Substring(0, colon).Trim().Trim("'"c, """"c)
            If keyName.EndsWith("-"c) OrElse keyName.EndsWith("."c) Then stale.Add(i)
        Next
        ' 倒序删，避免下标错位
        For i As Integer = stale.Count - 1 To 0 Step -1
            Logger.Info($"DSH：清掉 allowBuilds 里的折行残留键：{lines(stale(i)).Trim()}")
            lines.RemoveAt(stale(i))
            changed = True
        Next

        ' ② 补 allowBuilds 条目
        Dim toAdd As New List(Of String)()
        For Each nm In names.Distinct()
            Dim exists As Boolean = lines.Any(
                Function(l) l.TrimStart().StartsWith($"'{nm}':", StringComparison.Ordinal) OrElse
                            l.TrimStart().StartsWith($"{nm}:", StringComparison.Ordinal))
            If Not exists Then toAdd.Add(nm)
        Next

        If toAdd.Count > 0 Then
            Dim anchor As Integer = -1
            For i As Integer = 0 To lines.Count - 1
                If lines(i).TrimStart().StartsWith("allowBuilds:", StringComparison.Ordinal) Then anchor = i
            Next
            If anchor < 0 Then
                lines.Add("")
                lines.Add("# 由 PCL_DSH 自动追加：放行下列依赖的安装脚本")
                lines.Add("# false = 已确认但不执行其安装脚本（用包自带的预编译产物）")
                lines.Add("allowBuilds:")
                anchor = lines.Count - 1
            End If

            ' 插到 allowBuilds 段已有子项的末尾（遇到空行就停）
            Dim insertAt As Integer = anchor + 1
            While insertAt < lines.Count
                Dim t As String = lines(insertAt)
                If t.Trim() = "" Then Exit While
                If Not (t.StartsWith(" ") OrElse t.StartsWith(vbTab)) Then Exit While
                insertAt += 1
            End While

            For Each nm In toAdd
                lines.Insert(insertAt, $"  '{nm}': false")
                insertAt += 1
                changed = True
            Next
        End If

        ' ③ 关掉新包年龄闸门
        If needAgeOff Then
            Dim hasAge As Boolean = lines.Any(Function(l) l.TrimStart().StartsWith("minimumReleaseAge:", StringComparison.Ordinal))
            If Not hasAge Then
                lines.Add("")
                lines.Add("# 由 PCL_DSH 自动追加：插件生态更新太快，24 小时闸门会挡住正常安装")
                lines.Add("minimumReleaseAge: 0")
                changed = True
            End If
        End If

        If changed Then
            Try
                File.WriteAllLines(wsPath, lines, New UTF8Encoding(False))
                Logger.Info($"DSH：已自动修补 {wsPath}")
            Catch ex As Exception
                Logger.Error(ex, $"DSH：写入 {wsPath} 失败")
                Return False
            End Try
        End If

        Return changed
    End Function

    ''' <summary>
    ''' 去掉 <c>包名@版本</c> 里的版本部分。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 不能简单地按第一个 <c>@</c> 切 —— scope 包形如 <c>@scope/name@1.2.3</c>，
    ''' 第一个 <c>@</c> 是 scope 的一部分。要按**最后一个** <c>@</c> 切，
    ''' 并且那个 <c>@</c> 不能在第 0 位（否则会把 <c>@scope/name</c> 切坏）。
    ''' </remarks>
    Private Function StripPackageVersion(Token As String) As String
        If String.IsNullOrWhiteSpace(Token) Then Return Nothing
        Dim t As String = Token.Trim()
        Dim at As Integer = t.LastIndexOf("@"c)
        If at > 0 Then t = t.Substring(0, at)
        Return t.Trim()
    End Function

    ''' <summary>
    ''' 判断一个字符串像不像合法的 npm 包名。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 这是**防写垃圾进配置文件**的最后一道闸，不是洁癖。
    ''' 从 pnpm 的报错文本里抠包名本质上是"解析人类可读输出"，
    ''' 一旦它改格式或折行，就可能抠出半截名字（实测出过
    ''' <c>node-</c> / <c>pty</c> 这种被折行劈开的东西）。
    ''' 那些垃圾键会被写进 <c>pnpm-workspace.yaml</c> 的 <c>allowBuilds</c>，
    ''' 而 pnpm 对未知键是**直接报错**的 —— 等于把一次安装失败换成了一次配置损坏。
    ''' 宁可漏掉一个包（用户还能手动加），也不要写坏配置。
    '''
    ''' 规则（按 npm 的包名文法简化）：
    ''' <list type="bullet">
    ''' <item>可选的 <c>@scope/</c> 前缀</item>
    ''' <item>主体只能是小写字母、数字、<c>-</c>、<c>_</c>、<c>.</c>、<c>~</c></item>
    ''' <item>不能以 <c>-</c> 或 <c>.</c> 结尾（折行残留的典型特征）</item>
    ''' <item>长度 1~214</item>
    ''' </list>
    ''' </remarks>
    Private Function IsPlausiblePackageName(Name As String) As Boolean
        If String.IsNullOrWhiteSpace(Name) Then Return False
        Dim t As String = Name.Trim()
        If t.Length = 0 OrElse t.Length > 214 Then Return False
        ' 折行残留的典型形态：以 - 或 . 结尾
        If t.EndsWith("-"c) OrElse t.EndsWith("."c) Then Return False
        ' 不能含空白或路径分隔符
        If t.Contains(" ") OrElse t.Contains("/") AndAlso Not t.StartsWith("@") Then Return False
        If t.Contains(ChrW(92)) OrElse t.Contains(":") Then Return False
        Return Text.RegularExpressions.Regex.IsMatch(
            t, "^(@[a-z0-9\-._~]+/)?[a-z0-9\-._~]+$")
    End Function

    Private Sub Report(Progress As DshPluginProgressHandler, Stage As String, Percent As Double)
        If Progress Is Nothing Then Return
        Try
            Progress(Stage, Percent)
        Catch ex As Exception
            Logger.Warn($"DSH：插件进度回调抛异常（可忽略）：{ex.Message}")
        End Try
    End Sub

    ''' <summary>把一行输出追加进环形缓冲（上限 120 行）。</summary>
    Private Sub TrackTail(Buffer As List(Of String), Line As String)
        Buffer.Add(Line)
        If Buffer.Count > 120 Then Buffer.RemoveAt(0)
    End Sub

    ''' <summary>给命令行参数加引号。</summary>
    ''' <remarks>
    ''' ⚠️ 旧实现有两个问题，都会让参数被拆错：
    ''' <list type="number">
    ''' <item>只在含空格 / <c>&amp;</c> / <c>?</c> 时才加引号 —— 但
    ''' <c>|</c> <c>&lt;</c> <c>&gt;</c> <c>^</c> <c>(</c> <c>)</c> 等同样是 cmd 的元字符，
    ''' 一个都不拦。</item>
    ''' <item>引号内的转义用 <c>\"</c> —— 那是**反斜杠语言**的写法，Windows 命令行不用它。
    ''' 正确做法是双写：<c>""</c>。</item>
    ''' </list>
    ''' 参数里最可能出现的特殊字符是**路径里的空格**（插件 spec 可以是本地目录），
    ''' 所以这里改成保守策略：**一律加引号**，只把内层引号双写。
    ''' </remarks>
    Private Function QuoteArg(Value As String) As String
        If Value Is Nothing Then Return """"""
        ' Windows 命令行里，字符串内的引号靠**双写**转义（不是 \" —— 那是 C 系语言）
        Return """" & Value.Replace("""", """""") & """"
    End Function

#End Region

#Region "HTTP"

    ''' <summary>
    ''' 发一个 GET 并把响应体读成字符串。
    ''' </summary>
    ''' <remarks>
    ''' 复用 <see cref="DshRegistry.HttpGetString"/> 里的**共享** HttpClient ——
    ''' 那里踩过「每次 new 一个 HttpClient 会导致同一 host 连续建连失败」的坑
    ''' （PCL 启动时把 <c>ServicePointManager.ReusePort</c> 打开了）。
    ''' </remarks>
    Private Function HttpGetString(Url As String, TimeoutMs As Integer) As String
        ' GitHub 要求带 User-Agent（共享客户端上已设），并且更喜欢这个 Accept
        Return DshRegistry.HttpGetString(Url, TimeoutMs, "application/vnd.github+json")
    End Function

#End Region

End Module
