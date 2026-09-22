Imports System.IO
Imports System.Linq
Imports System.Text
Imports Newtonsoft.Json.Linq

''' <summary>
''' 从**另一个 dsh 环境**把配置搬到一个 PCL 实例里。
'''
''' ## 解决什么问题
''' 用户手上常常已经有一份「手工搭好的」dsh 环境（例如默认的 <c>~/.dsh</c>），
''' 里面装了一堆插件、配好了 API Key。换成 PCL 管理之后，
''' 每个实例有自己的 <c>DSH_HOME</c>，等于要从零再配一遍 —— 很烦。
'''
''' 这个模块就是「把这些配置搬过来」：**插件** + **API Key**。
'''
''' ## 为什么插件走「重新安装」而不是「复制 node_modules」
''' 复制看起来更快，但会留下一个和 pnpm 状态不一致的目录：
''' <list type="bullet">
''' <item>
''' 复制过去的包不在目标 profile 的 <c>pnpm-lock.yaml</c> 里。
''' 用户以后在 PCL 里点一次「安装插件」，pnpm 会把它们当成多余的包**清掉** ——
''' 插件会莫名其妙消失。
''' </item>
''' <item>
''' <c>link:</c> 依赖复制过去后仍然指向**旧环境**的路径，两边被绑死，
''' 删掉旧环境就断。
''' </item>
''' </list>
''' 所以走 <c>dsh plugin add</c> 重放依赖清单：pnpm 自己解析版本、自己维护
''' 锁文件、自己建链接，结果和「手动一个个装」完全一致。
''' 代价是要联网，但源环境已经把包下载过一次，pnpm 的全局存储通常是热的，
''' 实际很快。
'''
''' ## 只搬「配置」，不搬「会话」
''' 刻意**不**搬 sessions / storages / attachments / 各种插件自己的数据目录。
''' 那些东西量很大、结构随插件版本变，而且搬过来容易和实例已有的数据打架。
''' 如果用户要连会话一起搬，那应该用「迁移数据目录」或者直接换 <c>DSH_HOME</c>。
''' </summary>
Public Module DshProfileSync

#Region "常量"

    ''' <summary>默认的 dsh 环境目录名（放在用户主目录下）。</summary>
    Private Const DshSyncDefaultFolderName As String = ".dsh"

    ''' <summary>反斜杠字符（VB 里不能写成字符字面量，见项目备忘）。</summary>
    Private ReadOnly DshSyncBS As Char = ChrW(92)

    ''' <summary>profile 级 patch 文件名（dsh 官方约定）。</summary>
    Private Const DshPatchFileName As String = "cordis.patch.yml"

    ''' <summary>
    ''' 「全量导入」时要一并搬的**实例级**数据项（相对于 <c>DSH_HOME</c>）。
    ''' </summary>
    ''' <remarks>
    ''' 「插件 + API Key」只够让新环境能跑起来；用户真正的使用痕迹在这些地方。
    '''
    ''' ⭐ <c>sessions</c> 是**最关键的一项** —— dsh 的对话正文存在
    ''' <c>sessions\&lt;路径编码的工作区&gt;\&lt;会话id&gt;\session.v3.jsonl.zstd</c>。
    ''' 而 <c>storages</c> 里只有**索引与缓存**
    ''' （<c>storages\session_projcache\sessions\*.json</c>）。
    ''' 少了 <c>sessions</c> 就会变成「索引在、正文不在」：
    ''' 会话列表能看到，点开却是空的 —— 用户报的「全量导入带不过来聊天记录」就是这个。
    ''' 最初版本的清单漏了它，2026-09-22 修复。
    '''
    ''' 其余按用途分三类：
    ''' <list type="bullet">
    ''' <item><b>会话相关</b>：<c>sessions</c>（正文）、<c>storages</c>（索引）、
    '''       <c>dsh-session-archive</c>（归档）、<c>attachments</c>（附件）、
    '''       <c>task-board</c>（任务看板）</item>
    ''' <item><b>偏好与积累</b>：<c>auto-approve</c>（审批白名单 —— 用户一条条
    '''       攒出来的信任规则，重新攒很烦）</item>
    ''' <item><b>外观</b>：<c>pets</c> / <c>skins</c> / <c>skin-center</c> /
    '''       <c>whale-*</c> 与对应的 <c>*.json</c> 状态文件</item>
    ''' </list>
    '''
    ''' ⚠️ 刻意**不搬**的：
    ''' <list type="bullet">
    ''' <item><c>.anonymous-user-id</c> —— 设备标识，跟着机器走，搬过去会造成
    '''       两台机器共用一个 id</item>
    ''' <item><c>profiles\</c> —— 插件目录由「重放依赖清单」重建（见本文件顶部说明）</item>
    ''' <item><c>dsh-usage</c> / <c>.dshw-*.json</c> —— 用量统计，体积会涨且没有迁移价值</item>
    ''' <item><c>certs</c> —— 本机证书，换机器后本来就失效</item>
    ''' </list>
    ''' </remarks>
    Private ReadOnly DshSyncDataEntries As String() = {
        "sessions",
        "storages",
        "dsh-session-archive",
        "attachments",
        "task-board",
        "auto-approve",
        "pets",
        "skins",
        "skin-center",
        "whale-audio",
        "whale-roles",
        "whale-bubble-imgs",
        "pet.json",
        "skin-center-active.json"
    }

#End Region

#Region "patch 文件清理"

    ''' <summary>
    ''' 清理 profile patch YAML 里的「空数组占位符」<c>[]</c>。
    ''' </summary>
    ''' <param name="Raw">原始文件内容。</param>
    ''' <returns>可安全解析的内容；<paramref name="Raw"/> 为空时返回空串。</returns>
    ''' <remarks>
    ''' ⭐ 为什么必须做这件事（真实报障，代价很大）：
    '''
    ''' dsh 新建 profile 时会用模板生成 <c>cordis.patch.yml</c>，模板里带一行 <c>[]</c>
    ''' （见 <c>dsh-app-boot</c> 的 <c>PROFILE_PATCH_TEMPLATE</c>）—— 那是「空数组」占位符。
    '''
    ''' 而 <c>dsh-approval-gate</c> 插件的「一键初始化」是**纯文本追加**：
    ''' 它发现文件里没有 <c>- id: permission</c> 就把预设块拼到**文件末尾**：
    ''' <code>
    ''' []
    ''' # ── 自动审批模式 ──
    ''' - id: permission          ← 追加在这里
    ''' </code>
    ''' 在 YAML 里 <c>[]</c> 是一个**完整的空数组文档**，后面不能再直接跟 <c>- </c>
    ''' （那需要 <c>---</c> 文档分隔符）。于是 dsh 启动时报：
    ''' <code>
    ''' YAMLException: end of the stream or a document separator is expected
    ''' </code>
    ''' 结果是**服务完全起不来**。
    '''
    ''' 清理方式：删掉所有**独立成行**的 <c>[]</c>（允许前后有空白）。
    ''' 判定「独立成行」而不是「内容等于 []」，是因为真实的空 patch 文件
    ''' 通常是「几行注释 + 一行 <c>[]</c>」，删掉 <c>[]</c> 后剩注释 → YAML 解析成 null，
    ''' 语义上同样等价于「空 patch」，正是我们想要的。
    '''
    ''' ⚠️ 只处理**整行**的 <c>[]</c>：<c>key: []</c>（空数组作为某个键的值）
    ''' 是合法且常见的写法，绝不能碰。
    ''' </remarks>
    Public Function DshSanitizePatchYaml(Raw As String) As String
        If String.IsNullOrWhiteSpace(Raw) Then Return ""

        Dim lines As String() = Raw.Replace(vbCrLf, vbLf).Split(ChrW(10))
        Dim kept As New List(Of String)(lines.Length)
        Dim removed As Integer = 0

        For Each line As String In lines
            ' ⚠️ 用 Trim() 顺带吃掉 BOM（U+FEFF）与空白 —— 带 BOM 的文件首行是
            '   "\uFEFF[]"，不处理就匹配不上「独立成行的 []」。
            If line.Trim().TrimStart(ChrW(&HFEFF)).Trim() = "[]" Then
                removed += 1
                Continue For
            End If
            kept.Add(line)
        Next

        If removed > 0 Then
            Logger.Info($"DSH：已清理 profile patch 里的 {removed} 处空数组占位符 []")
        End If

        Return String.Join(vbCrLf, kept).Trim()
    End Function

    ''' <summary>
    ''' 判断一份 patch YAML 里有没有**真正的内容**（注释、空行、空数组占位符都不算）。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 别用「清理掉 <c>[]</c> 之后是不是空串」来判 ——
    ''' dsh 模板生成的文件是「几行注释 + 一行 <c>[]</c>」，
    ''' 删掉 <c>[]</c> 还剩注释，会被误判成「有内容」。
    ''' 而那个文件里其实**没有任何用户配置**，正是应该被覆盖的情况。
    '''
    ''' 判据：逐行看，跳过这三类，只要还剩别的行（<c>- id:</c>、<c>key: value</c> …）
    ''' 才算有内容：
    ''' <list type="number">
    ''' <item>空行</item>
    ''' <item>以 <c>#</c> 开头的注释行</item>
    ''' <item><c>[]</c> —— 空数组占位符（dsh 模板写的「空 patch」，语义就是没有条目）</item>
    ''' </list>
    '''
    ''' ⚠️ **必须容忍 BOM**：带 BOM 的文件首行是 <c>\uFEFF# ...</c>，
    ''' 直接 <c>Trim().StartsWith("#")</c> 会判成「不是注释」→ 误认为有内容。
    ''' （实测踩过：用 PowerShell 的 <c>Set-Content -Encoding UTF8</c> 写出的文件就带 BOM。）
    ''' </remarks>
    Public Function DshPatchHasContent(Raw As String) As Boolean
        If String.IsNullOrWhiteSpace(Raw) Then Return False
        For Each line As String In Raw.Replace(vbCrLf, vbLf).Split(ChrW(10))
            ' 去掉 BOM（U+FEFF）和所有空白后再判断
            Dim t As String = line.Trim().TrimStart(ChrW(&HFEFF)).Trim()
            If t.Length = 0 Then Continue For
            If t.StartsWith("#"c) Then Continue For
            ' 空数组占位符 = 空 patch，不算内容（否则模板文件会被误判成"有配置"）
            If t = "[]" Then Continue For
            Return True
        Next
        Return False
    End Function

#End Region

#Region "进度"

    ''' <summary>同步进度回调。</summary>
    Public Delegate Sub DshSyncProgressHandler(Stage As String, Message As String, Progress As Double)

    Private Sub Report(Handler As DshSyncProgressHandler, Stage As String, Message As String, Progress As Double)
        If Handler Is Nothing Then Return
        Try
            Handler(Stage, Message, Progress)
        Catch ex As Exception
            Logger.Warn(ex, "DSH：同步进度回调出错（已忽略）")
        End Try
    End Sub

#End Region

#Region "计划"

    ''' <summary>一条待同步的本地依赖（<c>link:</c> / <c>file:</c>）。</summary>
    Public Class DshSyncLinkDep
        ''' <summary>依赖名。</summary>
        Public Property Name As String
        ''' <summary>源环境里的绝对路径。</summary>
        Public Property SourcePath As String
        ''' <summary>目标环境里的绝对路径（执行时填）。</summary>
        Public Property TargetPath As String
    End Class

    ''' <summary>
    ''' 一个要安装的依赖。
    ''' </summary>
    ''' <remarks>
    ''' 刻意把 <see cref="Name"/> 和 <see cref="Spec"/> 分开存：
    ''' <list type="bullet">
    ''' <item><c>Spec</c> 是给 pnpm 的（可能是 <c>包名@版本</c>、tarball URL、本地路径）</item>
    ''' <item><c>Name</c> 是**依赖名**，也就是要写进 <c>dsh.profile.bundles</c> 的那个标识</item>
    ''' </list>
    ''' 两者在 tarball / 本地路径的情况下完全不同，混用会把 bundles 写坏。
    ''' </remarks>
    Public Class DshSyncSpec
        ''' <summary>依赖名（写进 bundles 用）。</summary>
        Public Property Name As String
        ''' <summary>传给 pnpm 的 spec。</summary>
        Public Property Spec As String
    End Class

    ''' <summary>一次同步的计划（先算清楚，给用户看了再执行）。</summary>
    Public Class DshSyncPlan
        ''' <summary>源环境目录（DSH_HOME）。</summary>
        Public Property SourceHome As String
        ''' <summary>要读的 profile 名。</summary>
        Public Property ProfileName As String
        ''' <summary>目标实例。</summary>
        Public Property Instance As DshInstance

        ''' <summary>要搬的 API Key（ref 名 → 值）。</summary>
        Public Property ApiKeys As New List(Of KeyValuePair(Of String, String))()
        ''' <summary>要装的常规依赖（spec + 依赖名）。</summary>
        Public Property Specs As New List(Of DshSyncSpec)()
        ''' <summary>本地路径依赖，需要先把目录搬过去。</summary>
        Public Property LinkDeps As New List(Of DshSyncLinkDep)()
        ''' <summary>源 profile 里已经装好的插件总数（含被跳过的）。</summary>
        Public Property SourceDepCount As Integer
        ''' <summary>源 profile 的 bundles 列表长度（仅供参考）。</summary>
        Public Property SourceBundleCount As Integer

        ''' <summary>
        ''' 要同步的 profile 级 patch 文件内容（<c>cordis.patch.yml</c>）；没有则为 Nothing。
        ''' </summary>
        ''' <remarks>
        ''' ⚠️ 这个文件**必须**一起搬。很多插件的关键配置写在里面，最典型的是
        ''' <c>dsh-approval-gate</c> —— 它的 <c>auto-approve</c> 权限预设
        ''' 只能在 profile 的 patch 里声明（插件自己的 README 写明
        ''' "the preset table is frozen at composition time"）。
        ''' 只搬依赖不搬这个文件，插件是装上了、核心功能却是残的。
        ''' </remarks>
        Public Property ProfilePatchContent As String
        ''' <summary>源环境的 patch 文件路径（仅用于展示）。</summary>
        Public Property ProfilePatchSource As String

        ''' <summary>是否执行「全量导入」（连使用数据一起搬）。</summary>
        Public Property IncludeData As Boolean = False

        ''' <summary>全量导入时要搬的实例级数据项（相对于源 DSH_HOME）。</summary>
        Public Property DataItems As New List(Of String)()

        ''' <summary>这些数据项合计体积（字节；-1 表示未统计）。</summary>
        Public Property DataBytes As Long = -1

        ''' <summary>有没有任何东西可搬。</summary>
        Public ReadOnly Property HasAnything As Boolean
            Get
                Return ApiKeys.Count > 0 OrElse Specs.Count > 0 OrElse LinkDeps.Count > 0 OrElse
                       Not String.IsNullOrWhiteSpace(ProfilePatchContent) OrElse DataItems.Count > 0
            End Get
        End Property

        ''' <summary>一行摘要，给确认框用。</summary>
        Public ReadOnly Property Summary As String
            Get
                Dim parts As New List(Of String)
                If ApiKeys.Count > 0 Then parts.Add($"{ApiKeys.Count} 个 API Key")
                If Specs.Count > 0 Then parts.Add($"{Specs.Count} 个插件")
                If LinkDeps.Count > 0 Then parts.Add($"{LinkDeps.Count} 个本地插件")
                If Not String.IsNullOrWhiteSpace(ProfilePatchContent) Then parts.Add("1 份 profile 配置")
                If DataItems.Count > 0 Then parts.Add($"{DataItems.Count} 项使用数据")
                If parts.Count = 0 Then Return "没有可同步的内容"
                Return String.Join(" + ", parts)
            End Get
        End Property

        ''' <summary>可以放行的提醒。</summary>
        Public Property Warning As String

        ''' <summary>
        ''' 源环境配套的 dsh 版本（尽力猜；猜不到为 Nothing）。
        ''' </summary>
        ''' <remarks>
        ''' 仅用于在确认框里提醒「版本不跟着搬」，**不影响同步行为**。
        ''' 见 <c>DshSyncDetectSourceDshVersion</c> 的注释。
        ''' </remarks>
        Public Property SourceDshVersion As String
    End Class

    ''' <summary>同步结果。</summary>
    Public Class DshSyncResult
        ''' <summary>是否成功（部分失败也算 False，但 Message 里会写清楚）。</summary>
        Public Property Success As Boolean
        ''' <summary>给用户看的说明。</summary>
        Public Property Message As String
        ''' <summary>成功写入的 API Key 个数。</summary>
        Public Property KeysWritten As Integer
        ''' <summary>成功安装的插件个数。</summary>
        Public Property PluginsInstalled As Integer
        ''' <summary>成功搬运的使用数据项个数（全量导入时）。</summary>
        Public Property DataCopied As Integer
        ''' <summary>失败明细。</summary>
        Public Property Failures As New List(Of String)()
    End Class

#End Region

#Region "探测源环境"

    ''' <summary>默认的 dsh 环境目录（<c>%USERPROFILE%\.dsh</c>），存在才返回。</summary>
    Public Function DshSyncDefaultSourceHome() As String
        Try
            Dim home As String = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            If String.IsNullOrWhiteSpace(home) Then Return Nothing
            Dim candidate As String = Path.Combine(home, DshSyncDefaultFolderName)
            If Directory.Exists(candidate) Then Return candidate
        Catch ex As Exception
            Logger.Warn(ex, "DSH：探测默认 dsh 环境目录失败")
        End Try
        Return Nothing
    End Function

    ''' <summary>
    ''' 判断一个目录像不像 dsh 环境（DSH_HOME）。
    ''' </summary>
    ''' <remarks>
    ''' 判据刻意宽松：只要有 <c>profiles\</c> 或者 <c>.credentials.yaml</c> 之一就算。
    ''' 因为用户可能有个只配了 key、还没装过插件的环境。
    ''' </remarks>
    Public Function DshSyncLooksLikeHome(HomeDir As String) As Boolean
        Try
            If String.IsNullOrWhiteSpace(HomeDir) OrElse Not Directory.Exists(HomeDir) Then Return False
            If Directory.Exists(Path.Combine(HomeDir, "profiles")) Then Return True
            If File.Exists(Path.Combine(HomeDir, ".credentials.yaml")) Then Return True
            Return False
        Catch
            Return False
        End Try
    End Function

    ''' <summary>列出源环境里存在的 profile 名。</summary>
    Public Function DshSyncListProfiles(SourceHome As String) As List(Of String)
        Dim result As New List(Of String)
        Try
            ' ⚠️ 局部变量不能叫 dir —— Dir 是 VB 内置函数（项目里踩过的坑）
            Dim profilesDir As String = Path.Combine(SourceHome, "profiles")
            If Not Directory.Exists(profilesDir) Then Return result
            For Each subDir In Directory.GetDirectories(profilesDir)
                Dim nm As String = Path.GetFileName(subDir)
                ' 跳过 pnpm 的存储目录之类
                If nm.StartsWith(".") Then Continue For
                ' 必须含 package.json 才算一个 profile
                If File.Exists(Path.Combine(subDir, "package.json")) Then result.Add(nm)
            Next
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：枚举 {SourceHome} 的 profile 失败")
        End Try
        Return result
    End Function

#End Region

#Region "构建计划"

    ''' <summary>
    ''' 读源环境，算出「要搬什么」。
    ''' </summary>
    ''' <remarks>
    ''' 只读不写，所以可以放心地在「先给用户看、确认了再执行」的流程里当第一步。
    ''' </remarks>
    Public Function DshSyncBuildPlan(SourceHome As String,
                                     ProfileName As String,
                                     Instance As DshInstance,
                                     Optional IncludeData As Boolean = False) As DshSyncPlan
        Dim plan As New DshSyncPlan With {
            .SourceHome = SourceHome,
            .ProfileName = ProfileName,
            .Instance = Instance,
            .IncludeData = IncludeData
        }

        If String.IsNullOrWhiteSpace(SourceHome) OrElse Not Directory.Exists(SourceHome) Then
            plan.Warning = "源环境目录不存在。"
            Return plan
        End If
        If Instance Is Nothing Then
            plan.Warning = "没有指定目标实例。"
            Return plan
        End If

        ' ── ① API Key ──
        Try
            Dim refs As Dictionary(Of String, String) = DshCredentials.ReadRefs(SourceHome)
            If refs IsNot Nothing Then
                For Each kv In refs
                    If String.IsNullOrWhiteSpace(kv.Key) OrElse String.IsNullOrWhiteSpace(kv.Value) Then Continue For
                    plan.ApiKeys.Add(New KeyValuePair(Of String, String)(kv.Key, kv.Value))
                Next
            End If
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：读取 {SourceHome} 的凭据失败")
            plan.Warning = "读不到源环境的凭据文件，API Key 可能搬不过来。"
        End Try

        ' ── ② 实例级使用数据（仅「全量导入」时）──
        ' 见 DshSyncDataEntries 的说明：这些才是用户真正的使用痕迹
        ' （聊天记录、任务看板、审批白名单、外观偏好）。
        '
        ' ⚠️ 这一段刻意放在**插件解析之前**，且**不受下面任何提前返回影响**。
        '    数据搬运与插件清单无关 —— 源环境哪怕没有 profile（从没装过插件），
        '    用户的会话记录依然应该能搬过来。
        '    （最初版本把它放在插件解析之后，只要 package.json 缺失或解析失败
        '      就会直接 return，全量导入会**静默地什么都不搬** —— 这种"看起来成功
        '      但什么都没做"的失败最难查。）
        If IncludeData Then
            Try
                Dim total As Long = 0L
                For Each entryName As String In DshSyncDataEntries
                    Dim src As String = Path.Combine(SourceHome, entryName)
                    If Not (Directory.Exists(src) OrElse File.Exists(src)) Then Continue For
                    plan.DataItems.Add(entryName)
                    total += DshSyncMeasure(SourceHome, entryName)
                Next
                plan.DataBytes = total
                Logger.Info($"DSH：全量导入将搬运 {plan.DataItems.Count} 项使用数据（{total} 字节）")
            Catch ex As Exception
                Logger.Warn(ex, "DSH：统计待同步的使用数据失败")
            End Try
        End If

        ' ── ③ 插件 ──
        Dim profileDir As String = Path.Combine(SourceHome, "profiles", If(ProfileName, ""))
        Dim pkgPath As String = Path.Combine(profileDir, "package.json")
        If Not File.Exists(pkgPath) Then
            If plan.Warning Is Nothing Then
                plan.Warning = $"源环境里没有 profile「{ProfileName}」，只搬 API Key" &
                               If(plan.DataItems.Count > 0, " 与使用数据", "") & "。"
            End If
            Return plan
        End If

        ' ── ③b profile 级 patch 文件（cordis.patch.yml）──
        ' 见 ProfilePatchContent 的注释：不搬它，approval-gate 之类插件的
        ' 核心功能就是残的。这里顺手做一次「空数组占位符」清理，
        ' 让搬过去的内容一定可解析（源环境可能是干净的手工版，也可能带占位符）。
        Try
            Dim patchPath As String = Path.Combine(profileDir, DshPatchFileName)
            If File.Exists(patchPath) Then
                Dim raw As String = File.ReadAllText(patchPath, Encoding.UTF8)
                Dim cleaned As String = DshSanitizePatchYaml(raw)
                If Not String.IsNullOrWhiteSpace(cleaned) Then
                    plan.ProfilePatchContent = cleaned
                    plan.ProfilePatchSource = patchPath
                End If
            End If
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：读取 {profileDir} 的 patch 文件失败")
        End Try

        Dim pkg As JObject = Nothing
        Try
            pkg = JObject.Parse(File.ReadAllText(pkgPath, Encoding.UTF8))
        Catch ex As Exception
            ' ⚠️ 注意这里也**不能**把已经收集好的数据丢掉 —— DataItems 上面就填好了，
            '    Return 只是放弃解析插件清单。
            plan.Warning = "源 profile 的 package.json 解析失败（插件清单跳过）：" & ex.Message
            Return plan
        End Try

        Dim deps = TryCast(pkg("dependencies"), JObject)
        If deps IsNot Nothing Then
            For Each prop As JProperty In deps.Properties()
                plan.SourceDepCount += 1
                Dim depName As String = prop.Name
                Dim depValue As String = If(prop.Value?.ToString(), "").Trim()
                If String.IsNullOrWhiteSpace(depName) Then Continue For

                ' dsh 自己的基础包不用装 —— 目标环境本来就有
                If depName.StartsWith("@deepseek-ai/", StringComparison.OrdinalIgnoreCase) Then Continue For

                If depValue.StartsWith("link:", StringComparison.OrdinalIgnoreCase) OrElse
                   depValue.StartsWith("file:", StringComparison.OrdinalIgnoreCase) Then
                    Dim rawPath As String = depValue.Substring(depValue.IndexOf(":"c) + 1).Trim()
                    Dim absPath As String = DshSyncResolvePath(profileDir, rawPath)
                    If absPath IsNot Nothing AndAlso Directory.Exists(absPath) Then
                        plan.LinkDeps.Add(New DshSyncLinkDep With {
                            .Name = depName,
                            .SourcePath = absPath
                        })
                    Else
                        plan.Warning = $"本地插件 {depName} 的目录找不到，会被跳过：{rawPath}"
                    End If
                ElseIf depValue.StartsWith("workspace:", StringComparison.OrdinalIgnoreCase) Then
                    ' workspace 协议只在 monorepo 内部有意义，搬过来一定失败
                    Continue For
                ElseIf depValue.StartsWith("npm:", StringComparison.OrdinalIgnoreCase) Then
                    ' 别名依赖：npm:真实包名@版本
                    plan.Specs.Add(New DshSyncSpec With {.Name = depName, .Spec = depValue.Substring(4)})
                ElseIf depValue.StartsWith("http", StringComparison.OrdinalIgnoreCase) OrElse
                       depValue.StartsWith("git", StringComparison.OrdinalIgnoreCase) Then
                    ' 已经是完整 spec
                    plan.Specs.Add(New DshSyncSpec With {.Name = depName, .Spec = depValue})
                ElseIf depValue = "*" OrElse depValue = "latest" Then
                    plan.Specs.Add(New DshSyncSpec With {.Name = depName, .Spec = depName})
                Else
                    plan.Specs.Add(New DshSyncSpec With {.Name = depName, .Spec = $"{depName}@{depValue}"})
                End If
            Next
        End If

        ' bundles 列表长度只用于展示
        Try
            Dim bundles = pkg("dsh")?("profile")?("bundles")
            If bundles IsNot Nothing Then plan.SourceBundleCount = bundles.Count()
        Catch
        End Try

        ' ── ③ 提醒「dsh 版本不跟着搬」──
        ' ⭐ 这是用户真实困惑过的一点：他看到同步过来的插件和 Key 都对，
        ' 但 dsh 版本是 PCL 装的最新版，而不是自己那个环境在用的版本。
        ' 原因是 **DSH_HOME 里只有数据、没有程序本体** —— 版本由运行时决定。
        ' 界面上说清楚，比让用户自己猜好得多。
        Try
            plan.SourceDshVersion = DshSyncDetectSourceDshVersion(SourceHome)
        Catch ex As Exception
            Logger.Warn(ex, "DSH：探测源环境的 dsh 版本失败")
        End Try

        Return plan
    End Function

    ''' <summary>
    ''' 尽力猜出源环境「配套的」dsh 版本，仅供展示与提醒用。
    ''' </summary>
    ''' <remarks>
    ''' DSH_HOME 里通常**没有** dsh 本体，所以这里只能找间接线索：
    ''' <list type="number">
    ''' <item>源环境自己的 <c>node_modules\@deepseek-ai\dsh\package.json</c>
    '''       （有些人会把本体也装在 DSH_HOME 下）</item>
    ''' <item>profile 的 <c>pnpm-lock.yaml</c> 里锁定的 <c>@deepseek-ai/dsh</c> 版本</item>
    ''' </list>
    ''' 找不到就返回 Nothing —— 这个信息只用于提示，**不影响同步行为**。
    ''' </remarks>
    Private Function DshSyncDetectSourceDshVersion(SourceHome As String) As String
        ' 线索 1：源环境自带的本体
        Try
            Dim p As String = Path.Combine(SourceHome, "node_modules", "@deepseek-ai", "dsh", "package.json")
            If File.Exists(p) Then
                Dim o As JObject = JObject.Parse(File.ReadAllText(p, Encoding.UTF8))
                Dim v As String = If(o("version")?.ToString(), "").Trim()
                If Not String.IsNullOrWhiteSpace(v) Then Return v
            End If
        Catch ex As Exception
            Logger.Warn(ex, "DSH：读取源环境的 dsh 本体版本失败")
        End Try

        ' 线索 2：从 lock 文件里抠（形状类似 `'@deepseek-ai/dsh@0.1.5-rc.1':`）
        Try
            Dim lockPath As String = Path.Combine(SourceHome, "profiles", "pnpm-lock.yaml")
            If File.Exists(lockPath) Then
                For Each line As String In File.ReadLines(lockPath)
                    Dim idx As Integer = line.IndexOf("@deepseek-ai/dsh@", StringComparison.Ordinal)
                    If idx < 0 Then Continue For
                    Dim rest As String = line.Substring(idx + "@deepseek-ai/dsh@".Length)
                    Dim m As Text.RegularExpressions.Match =
                        Text.RegularExpressions.Regex.Match(rest, "^\d+\.\d+\.\d+[-\w.]*")
                    If m.Success Then Return m.Value
                Next
            End If
        Catch ex As Exception
            Logger.Warn(ex, "DSH：从 lock 文件解析 dsh 版本失败")
        End Try

        Return Nothing
    End Function

    ''' <summary>把 package.json 里的相对路径解析成绝对路径。</summary>
    Private Function DshSyncResolvePath(BaseDir As String, RawPath As String) As String
        Try
            If String.IsNullOrWhiteSpace(RawPath) Then Return Nothing
            Dim p As String = RawPath.Replace("/"c, DshSyncBS).Trim()
            If Path.IsPathRooted(p) Then Return Path.GetFullPath(p)
            Return Path.GetFullPath(Path.Combine(BaseDir, p))
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：解析本地插件路径失败：{RawPath}")
            Return Nothing
        End Try
    End Function

    ''' <summary>统计某个数据项的体积（字节）；失败返回 0。</summary>
    ''' <summary>统计某个待同步条目的体积（只用于界面展示）。</summary>
    ''' <remarks>
    ''' ⚠️ 刻意**不用** <c>Directory.GetFiles(..., SearchOption.AllDirectories)</c>：
    ''' <list type="bullet">
    ''' <item>那个 API **遇到任何一层没权限就整体抛异常**，
    '''       于是整个体积显示变成 0（"0 字节"会让用户以为没东西可搬）</item>
    ''' <item>它**会跟随符号链接** —— 而 pnpm 的 <c>node_modules</c> 是链接图，
    '''       同一份文件会被重复计入，体积虚高</item>
    ''' </list>
    ''' 改为复用 <see cref="DshMigrate.MeasureDirectorySize"/>（逐层遍历 +
    ''' 不跟随链接），语义与全项目一致。
    ''' </remarks>
    Private Function DshSyncMeasure(Home As String, EntryName As String) As Long
        Try
            Dim target As String = Path.Combine(Home, EntryName)
            If File.Exists(target) Then
                Return New FileInfo(target).Length
            End If
            If Not Directory.Exists(target) Then Return 0L
            Return DshMigrate.MeasureDirectorySize(target)
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：统计 {EntryName} 体积失败")
            Return 0L
        End Try
    End Function

#End Region

#Region "执行"
    ''' <summary>
    ''' 执行同步计划。
    ''' </summary>
    ''' <remarks>
    ''' 顺序刻意是「先 Key 后插件」：
    ''' API Key 是几乎瞬时、且失败也不影响插件的动作，
    ''' 而插件安装可能跑好几分钟。先落 Key，用户中途关掉窗口也能保住 key。
    ''' 阻塞调用，请放在后台线程。
    ''' </remarks>
    Public Function DshSyncExecute(Plan As DshSyncPlan,
                                   Optional Progress As DshSyncProgressHandler = Nothing) As DshSyncResult
        Dim result As New DshSyncResult()
        If Plan Is Nothing Then
            result.Message = "没有同步计划。"
            Return result
        End If

        ' ── ① API Key ──
        If Plan.ApiKeys.Count > 0 Then
            Report(Progress, "凭据", $"正在写入 {Plan.ApiKeys.Count} 个 API Key……", 0.05)
            For Each kv In Plan.ApiKeys
                Try
                    DshCredentials.SetRef(kv.Key, kv.Value, Plan.Instance.HomeDir)
                    result.KeysWritten += 1
                    Logger.Info($"DSH：已同步凭据 {kv.Key} → {Plan.Instance.DisplayName}")
                Catch ex As Exception
                    Logger.Error(ex, $"DSH：写入凭据 {kv.Key} 失败")
                    result.Failures.Add($"API Key {kv.Key}：{ex.Message}")
                End Try
            Next
        End If

        ' ── ② 本地插件目录（先搬目录，再一起装）──
        Dim specs As New List(Of String)()
        Dim bundleNames As New List(Of String)()
        For Each s In Plan.Specs
            specs.Add(s.Spec)
            If Not String.IsNullOrWhiteSpace(s.Name) Then bundleNames.Add(s.Name)
        Next

        ' ── ②b profile 级 patch 文件 ──
        ' ⚠️ 必须在装插件**之前**写好：dsh 在安装过程中就可能重新 compose profile，
        ' 一个带空数组占位符的 patch 会让它直接解析失败。
        If Not String.IsNullOrWhiteSpace(Plan.ProfilePatchContent) Then
            Report(Progress, "配置", "正在写入 profile 配置……", 0.12)
            Try
                Dim targetDir As String = Path.Combine(Plan.Instance.HomeDir, "profiles", Plan.ProfileName)
                If Not Directory.Exists(targetDir) Then Directory.CreateDirectory(targetDir)
                Dim target As String = Path.Combine(targetDir, DshPatchFileName)

                ' 目标已有**真正的内容**时不动它 —— 用户可能已经在里面加了自己的东西。
                ' ⚠️ 判据必须用 DshPatchHasContent（剥掉注释和空行后再看有没有东西），
                '   **不能**只看「清理掉 [] 之后是不是空串」：
                '   dsh 模板生成的文件是「几行注释 + 一行 []」，
                '   删掉 [] 之后还剩注释 —— 会被误判成"有内容"而跳过写入。
                '   那恰好是最该被覆盖的情况（模板文件里没有任何用户配置）。
                Dim shouldWrite As Boolean = True
                If File.Exists(target) Then
                    Dim existing As String = File.ReadAllText(target, Encoding.UTF8)
                    If DshPatchHasContent(existing) Then
                        shouldWrite = False
                        Logger.Info($"DSH：目标 profile 已有自定义配置，跳过写入：{target}")
                    End If
                End If

                If shouldWrite Then
                    File.WriteAllText(target, Plan.ProfilePatchContent, New UTF8Encoding(False))
                    Logger.Info($"DSH：已同步 profile 配置 → {target}")
                End If
            Catch ex As Exception
                Logger.Error(ex, "DSH：写入 profile 配置失败")
                result.Failures.Add("写入 profile 配置：" & ex.Message)
            End Try
        End If

        ' ── ②c 实例级使用数据（全量导入时）──
        ' 放到插件安装**之前**：这些是纯数据搬运，不依赖插件；
        ' 而且万一后面 pnpm 装插件失败，至少用户的聊天记录已经安全落地了。
        If Plan.DataItems.Count > 0 Then
            Report(Progress, "使用数据", $"正在搬运 {Plan.DataItems.Count} 项使用数据……", 0.10)
            For Each entryName As String In Plan.DataItems
                Try
                    Dim src As String = Path.Combine(Plan.SourceHome, entryName)
                    Dim dst As String = Path.Combine(Plan.Instance.HomeDir, entryName)
                    If File.Exists(src) Then
                        File.Copy(src, dst, True)
                    ElseIf Directory.Exists(src) Then
                        ' 目录：先删掉目标再整体复制 —— 避免新旧内容混在一起
                        ' （会话表是"全量快照"语义，合并两边只会得到一份谁也不认识的数据）
                        If Directory.Exists(dst) Then
                            Try
                                DshMigrate.DeleteDirectoryRobust(dst)
                            Catch ex As Exception
                                Logger.Warn(ex, $"DSH：清理目标 {entryName} 失败，改为覆盖复制")
                            End Try
                        End If
                        DshSyncCopyTree(src, dst)
                    Else
                        Continue For
                    End If
                    result.DataCopied += 1
                    Logger.Info($"DSH：已同步使用数据 {entryName} → {Plan.Instance.DisplayName}")
                Catch ex As Exception
                    Logger.Error(ex, $"DSH：同步使用数据 {entryName} 失败")
                    result.Failures.Add($"使用数据 {entryName}：{ex.Message}")
                End Try
            Next
        End If

        If Plan.LinkDeps.Count > 0 Then
            Report(Progress, "本地插件", $"正在搬运 {Plan.LinkDeps.Count} 个本地插件……", 0.15)
            Dim localRoot As String = Path.Combine(Plan.Instance.HomeDir, "local-plugins")
            Try
                If Not Directory.Exists(localRoot) Then Directory.CreateDirectory(localRoot)
            Catch ex As Exception
                Logger.Error(ex, "DSH：创建 local-plugins 目录失败")
                result.Failures.Add("无法创建 local-plugins 目录：" & ex.Message)
            End Try

            For Each link In Plan.LinkDeps
                Try
                    Dim folderName As String = New DirectoryInfo(link.SourcePath).Name
                    Dim targetDir As String = Path.Combine(localRoot, folderName)
                    If Directory.Exists(targetDir) Then
                        ' 已经有一份就不重复搬，直接用它
                        Logger.Info($"DSH：本地插件 {folderName} 已存在，复用 {targetDir}")
                    Else
                        DshSyncCopyTree(link.SourcePath, targetDir)
                    End If
                    link.TargetPath = targetDir
                    specs.Add(targetDir)
                    ' 本地插件进 bundles 用的是**依赖名**，不是路径
                    If Not String.IsNullOrWhiteSpace(link.Name) Then bundleNames.Add(link.Name)
                Catch ex As Exception
                    Logger.Error(ex, $"DSH：搬运本地插件 {link.Name} 失败")
                    result.Failures.Add($"本地插件 {link.Name}：{ex.Message}")
                End Try
            Next
        End If

        ' ── ③ 插件（一条命令批量装）──
        If specs.Count > 0 Then
            Report(Progress, "插件", $"正在安装 {specs.Count} 个插件（可能需要几分钟）……", 0.3)
            Try
                ' 把 pnpm 的进度直接透传上去
                DshPluginMarket.InstallPlugins(
                    Plan.Instance,
                    specs,
                    Sub(stage As String, pct As Double)
                        Report(Progress, "插件", stage, If(pct >= 0, 0.3 + pct * 0.65, -1))
                    End Sub)
                result.PluginsInstalled = specs.Count
            Catch ex As Exception
                Logger.Error(ex, "DSH：批量安装插件失败")
                result.Failures.Add("插件安装：" & ex.Message)
            End Try

            ' ── ④ 兜底：确保它们真的进了 profile 的 bundles 列表 ──
            ' ⚠️ 这一步不能省。实测：如果依赖**已经**在 package.json 里
            '   （例如上一次安装中途失败留下的），dsh 的 plugin 子命令会认为
            '   「没有新东西要装」，于是**跳过** bundles 更新 —— 包在 node_modules 里、
            '   依赖也写进了 package.json，但 profile 启动时根本不会加载它们。
            '   这是最坏的一种失败：用户以为装好了，界面里却什么都没有。
            '   所以我们自己按「依赖名」把 bundles 补齐，让结果与成功路径一致。
            If bundleNames.Count > 0 Then
                Report(Progress, "注册", "正在把插件注册进 profile……", 0.97)
                Try
                    Dim added As Integer = DshSyncEnsureBundles(Plan.Instance, Plan.ProfileName, bundleNames)
                    If added > 0 Then Logger.Info($"DSH：已补齐 {added} 个 bundle 注册项")
                Catch ex As Exception
                    Logger.Error(ex, "DSH：补齐 bundles 列表失败")
                    result.Failures.Add("注册插件到 profile：" & ex.Message)
                End Try
            End If
        End If

        Report(Progress, "完成", "同步完成", 1)

        result.Success = (result.Failures.Count = 0)
        If result.Success Then
            Dim parts As New List(Of String)
            If result.KeysWritten > 0 Then parts.Add($"写入 {result.KeysWritten} 个 API Key")
            If result.PluginsInstalled > 0 Then parts.Add($"安装 {result.PluginsInstalled} 个插件")
            If result.DataCopied > 0 Then parts.Add($"搬运 {result.DataCopied} 项使用数据")
            result.Message = If(parts.Count = 0, "没有需要同步的内容。", "已完成：" & String.Join("、", parts))
        Else
            result.Message = "部分内容同步失败：" & vbCrLf & String.Join(vbCrLf, result.Failures)
        End If
        Return result
    End Function

    ''' <summary>
    ''' 确保这些依赖名出现在 profile 的 <c>dsh.profile.bundles</c> 里。
    ''' </summary>
    ''' <param name="Instance">目标实例。</param>
    ''' <param name="ProfileName">profile 名。</param>
    ''' <param name="Names">依赖名（不是 spec）。</param>
    ''' <returns>新增了几个条目。</returns>
    ''' <remarks>
    ''' 为什么不能只依赖 <c>dsh plugin add</c> 自己更新 bundles：
    ''' 实测当依赖**已经**在 <c>package.json</c> 里时（上一次安装中途失败会留下这种状态），
    ''' dsh 认为「没有新东西要装」而**跳过** bundles 更新。
    ''' 结果是包在 <c>node_modules</c> 里、依赖也写进了 package.json，
    ''' 但 profile 启动时完全不加载 —— 用户以为装好了，界面里却什么都没有。
    ''' 所以这里自己补一遍，让「安装成功」和「真的会被加载」这两件事对齐。
    ''' 幂等：已经在列表里的不会重复添加。
    ''' </remarks>
    Private Function DshSyncEnsureBundles(Instance As DshInstance,
                                         ProfileName As String,
                                         Names As IEnumerable(Of String)) As Integer
        If Instance Is Nothing Then Return 0
        Dim profileDir As String = Path.Combine(Instance.HomeDir, "profiles", If(ProfileName, ""))
        Dim pkgPath As String = Path.Combine(profileDir, "package.json")
        If Not File.Exists(pkgPath) Then Return 0

        Dim root As JObject = JObject.Parse(File.ReadAllText(pkgPath, Encoding.UTF8))

        ' 逐层补齐 dsh.profile.bundles —— 任何一层缺失都建出来
        Dim dshNode = TryCast(root("dsh"), JObject)
        If dshNode Is Nothing Then
            dshNode = New JObject()
            root("dsh") = dshNode
        End If
        Dim profileNode = TryCast(dshNode("profile"), JObject)
        If profileNode Is Nothing Then
            profileNode = New JObject()
            dshNode("profile") = profileNode
        End If
        Dim bundles = TryCast(profileNode("bundles"), JArray)
        If bundles Is Nothing Then
            bundles = New JArray()
            profileNode("bundles") = bundles
        End If

        Dim existing As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each token As JToken In bundles
            Dim s As String = token.ToString().Trim()
            If Not String.IsNullOrWhiteSpace(s) Then existing.Add(s)
        Next

        Dim added As Integer = 0
        For Each nm In Names
            If String.IsNullOrWhiteSpace(nm) Then Continue For
            Dim clean As String = nm.Trim()
            If existing.Contains(clean) Then Continue For
            bundles.Add(clean)
            existing.Add(clean)
            added += 1
        Next

        If added = 0 Then Return 0

        Dim temp As String = DshMigrate.DshAtomicTempPath(pkgPath)
        File.WriteAllText(temp, root.ToString(Newtonsoft.Json.Formatting.Indented), New UTF8Encoding(False))
        If File.Exists(pkgPath) Then File.Delete(pkgPath)
        File.Move(temp, pkgPath)
        Logger.Info($"DSH：已把 {added} 个插件写进 {ProfileName} 的 bundles 列表")
        Return added
    End Function

    ''' <summary>递归复制目录（保留结构，不做任何跳过）。</summary>
    Private Sub DshSyncCopyTree(SourceDir As String, DestDir As String)
        If Not Directory.Exists(DestDir) Then Directory.CreateDirectory(DestDir)

        Dim subDirs As String() = Nothing
        Try
            subDirs = Directory.GetDirectories(SourceDir)
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：枚举目录失败：{SourceDir}")
        End Try
        If subDirs IsNot Nothing Then
            For Each d In subDirs
                ' 跳过 node_modules —— 本地插件的依赖会由 pnpm 自己装
                Dim nm As String = Path.GetFileName(d)
                If String.Equals(nm, "node_modules", StringComparison.OrdinalIgnoreCase) Then Continue For
                DshSyncCopyTree(d, Path.Combine(DestDir, nm))
            Next
        End If

        Dim files As String() = Nothing
        Try
            files = Directory.GetFiles(SourceDir)
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：枚举文件失败：{SourceDir}")
        End Try
        If files IsNot Nothing Then
            For Each f In files
                Try
                    File.Copy(f, Path.Combine(DestDir, Path.GetFileName(f)), True)
                Catch ex As Exception
                    Logger.Warn(ex, $"DSH：复制文件失败：{f}")
                End Try
            Next
        End If
    End Sub

#End Region

End Module
