Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports Newtonsoft.Json.Linq

''' <summary>
''' 模型接入方式（Provider）—— 官方 API 与任意多个自定义网关。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 模型
''' ═══════════════════════════════════════════════════════════════════════
''' 把「接入方式」抽象成一个 <see cref="DshProvider"/> 列表：
'''   - 列表**第一项永远是官方 API**（id 固定为 <c>official</c>），不可删除
'''   - 其余是用户自己加的自定义网关，可以有任意多个
'''   - 每个 provider 有**自己的凭据名**（<see cref="DshProvider.KeyRef"/>），
'''     所以它们的密钥互不覆盖，切换不用重新粘贴
'''   - **每个实例各自选一个 provider**（存在实例自己的 settings.yaml 里）
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 配置落到哪里
''' ═══════════════════════════════════════════════════════════════════════
'''   · provider 列表（不含密钥）→ <c>DSH_ROOT/providers.json</c>
'''   · 端点与协议 → 实例的 <c>settings.yaml</c> 的 <c>llm-deepseek</c> 分节
'''     （官方 provider 就是**删掉**这个分节，让 dsh 用内置默认值）
'''   · 密钥 → 实例的 <c>.credentials.yaml</c> 的 <c>refs</c> 段，键名 = provider 的 KeyRef
'''
''' 为什么用 settings.yaml 而不是 patch 文件：它是官方 web「模型」页写的地方，
''' <c>watch: true</c> → 改完立刻生效，不用重启服务；而且粒度是"某个插件的配置"，
''' 比 patch 的"按 id 替换整个 config"细。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' baseURL 的路径规则（照抄官方文档，别自己发挥）
''' ═══════════════════════════════════════════════════════════════════════
''' 来自 <c>packages/llm/llm-deepseek/README.zh.md</c>：
'''   - Chat：追加 <c>/chat/completions</c>
'''   - Messages：**只把末尾严格匹配的 <c>/v1</c> 路径段**视为已有版本，
'''     追加 <c>/messages</c>；其他基址一律追加 <c>/v1/messages</c>
'''   - 末尾斜线不改变结果
''' 所以「连接测试」拼探测地址时必须按同一套规则来，否则测的地址和实际用的不是一个。
''' </summary>
Public Module DshApiConfig

#Region "常量"

    ''' <summary>官方 provider 的固定 id。</summary>
    Public Const OfficialProviderId As String = "official"

    ''' <summary>官方 provider 的凭据名（也是 llm-deepseek 的默认 apiKeyEnv）。</summary>
    Public Const KeyRefOfficial As String = "DEEPSEEK_API_KEY"

    ''' <summary>自定义 provider 凭据名的前缀。</summary>
    Public Const ProviderKeyRefPrefix As String = "PCL_PROVIDER_"

    ''' <summary>messages 协议（Anthropic 风格）。</summary>
    Public Const ProtocolMessages As String = "messages"

    ''' <summary>chat-completions 协议（OpenAI 风格）。</summary>
    Public Const ProtocolChat As String = "chat-completions"

    ''' <summary>官方 chat 根地址。</summary>
    Public Const OfficialBaseUrl As String = "https://api.deepseek.com"

    ''' <summary>官方 messages 根地址。</summary>
    Public Const OfficialMessagesBaseUrl As String = "https://api.deepseek.com/anthropic"

#End Region

#Region "Provider 模型"

    ''' <summary>一种模型接入方式。</summary>
    Public Class DshProvider
        ''' <summary>唯一 id；官方固定为 <c>official</c>。</summary>
        Public Property Id As String = ""
        ''' <summary>给用户看的名字。</summary>
        Public Property Name As String = ""
        ''' <summary>API 基址；官方留空（表示用 dsh 内置默认值）。</summary>
        Public Property BaseUrl As String = ""
        ''' <summary>协议。</summary>
        Public Property Protocol As String = ProtocolChat
        ''' <summary>要暴露的模型名；留空则沿用 dsh 内置目录。</summary>
        Public Property Model As String = ""
        ''' <summary>模型名的上下文窗口（写进 models 段用）。</summary>
        Public Property ContextWindow As Integer = 128000

        ''' <summary>是不是官方 API。</summary>
        Public ReadOnly Property IsOfficial As Boolean
            Get
                Return String.Equals(Id, OfficialProviderId, StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        ''' <summary>
        ''' 这个 provider 使用的凭据名。
        ''' </summary>
        ''' <remarks>
        ''' 每个 provider 一个独立的 ref 名，所以它们的密钥互不覆盖 ——
        ''' 用户在官方和几个自定义网关之间切换时不需要重新粘贴密钥。
        ''' </remarks>
        Public ReadOnly Property KeyRef As String
            Get
                If IsOfficial Then Return KeyRefOfficial
                Return ProviderKeyRefPrefix & Id.ToUpperInvariant()
            End Get
        End Property

        ''' <summary>给用户看的一行描述。</summary>
        Public ReadOnly Property DescribeText As String
            Get
                If IsOfficial Then Return "DeepSeek 官方 API"
                Dim proto As String = If(String.Equals(Protocol, ProtocolMessages, StringComparison.OrdinalIgnoreCase),
                                         "messages", "chat-completions")
                Return $"{proto} · {BaseUrl}"
            End Get
        End Property

        ''' <summary>用于界面显示的标签。</summary>
        Public ReadOnly Property DisplayLabel As String
            Get
                Return If(String.IsNullOrWhiteSpace(Name), DescribeText, Name)
            End Get
        End Property

        Public Function Clone() As DshProvider
            Return New DshProvider With {
                .Id = Id, .Name = Name, .BaseUrl = BaseUrl, .Protocol = Protocol,
                .Model = Model, .ContextWindow = ContextWindow
            }
        End Function
    End Class

    ''' <summary>官方 provider（每次调用都新建，避免调用方改到共享实例）。</summary>
    Public Function CreateOfficialProvider() As DshProvider
        Return New DshProvider With {
            .Id = OfficialProviderId,
            .Name = "DeepSeek 官方 API",
            .BaseUrl = "",
            .Protocol = ProtocolMessages,
            .Model = "",
            .ContextWindow = 1000000
        }
    End Function

#End Region

#Region "Provider 列表的持久化"

    ''' <summary>provider 列表的存放位置（不含密钥）。</summary>
    Public ReadOnly Property ProvidersFile As String
        Get
            Return ModDSH.DshRoot & "providers.json"
        End Get
    End Property

    ''' <summary>生成一个新的 provider id（短、便于拼凭据名）。</summary>
    Public Function NewProviderId() As String
        Return Guid.NewGuid().ToString("N").Substring(0, 8)
    End Function

    ''' <summary>
    ''' 读取 provider 列表。官方永远在第一项。
    ''' </summary>
    ''' <remarks>文件不存在或损坏时返回"只有官方"的默认列表。</remarks>
    Public Function LoadProviders() As List(Of DshProvider)
        Dim result As New List(Of DshProvider) From {CreateOfficialProvider()}

        Try
            If Not DshRuntime.FileExistsSafe(ProvidersFile) Then Return result

            Dim root As JObject = JObject.Parse(File.ReadAllText(ProvidersFile, Encoding.UTF8))
            Dim arr = TryCast(root("providers"), JArray)
            If arr Is Nothing Then Return result

            For Each item In arr
                Dim o = TryCast(item, JObject)
                If o Is Nothing Then Continue For

                Dim id As String = o("id")?.ToString()
                '官方由代码固定提供，不读文件里的（防止被改坏）
                If String.IsNullOrWhiteSpace(id) OrElse
                   String.Equals(id, OfficialProviderId, StringComparison.OrdinalIgnoreCase) Then Continue For

                Dim p As New DshProvider With {
                    .Id = id.Trim(),
                    .Name = If(o("name")?.ToString(), "未命名网关"),
                    .BaseUrl = If(o("baseUrl")?.ToString(), "").Trim(),
                    .Protocol = If(o("protocol")?.ToString(), ProtocolChat).Trim(),
                    .Model = If(o("model")?.ToString(), "").Trim(),
                    .ContextWindow = If(o("contextWindow") Is Nothing, 128000, CInt(o("contextWindow")))
                }
                If p.ContextWindow <= 0 Then p.ContextWindow = 128000
                result.Add(p)
            Next
        Catch ex As Exception
            Logger.Warn($"DSH：读取 provider 列表失败，使用默认值：{ex.Message}")
        End Try

        Return result
    End Function

    ''' <summary>保存 provider 列表（原子写入）。官方那一项不写进文件。</summary>
    Public Sub SaveProviders(Providers As List(Of DshProvider))
        Try
            ModDSH.DshEnsureDirectories()

            Dim arr As New JArray()
            For Each p In Providers
                If p Is Nothing OrElse p.IsOfficial Then Continue For
                Dim o As New JObject()
                o("id") = p.Id
                o("name") = p.Name
                o("baseUrl") = p.BaseUrl
                o("protocol") = p.Protocol
                o("model") = p.Model
                o("contextWindow") = p.ContextWindow
                arr.Add(o)
            Next

            Dim root As New JObject()
            root("version") = 1
            root("providers") = arr

            Dim path As String = ProvidersFile
            Dim tmp As String = DshMigrate.DshAtomicTempPath(path)
            File.WriteAllText(tmp, root.ToString(Newtonsoft.Json.Formatting.Indented), New UTF8Encoding(False))

            If File.Exists(path) Then
                File.Replace(tmp, path, Nothing)
            Else
                File.Move(tmp, path)
            End If
        Catch ex As Exception
            Logger.Error(ex, "DSH：保存 provider 列表失败")
        End Try
    End Sub

    ''' <summary>按 id 找 provider；找不到返回 Nothing。</summary>
    Public Function GetProvider(Providers As List(Of DshProvider), Id As String) As DshProvider
        If Providers Is Nothing OrElse String.IsNullOrWhiteSpace(Id) Then Return Nothing
        Return Providers.FirstOrDefault(
            Function(p) String.Equals(p.Id, Id, StringComparison.OrdinalIgnoreCase))
    End Function

    ''' <summary>按凭据名反查 provider。</summary>
    Public Function GetProviderByKeyRef(Providers As List(Of DshProvider), KeyRef As String) As DshProvider
        If Providers Is Nothing OrElse String.IsNullOrWhiteSpace(KeyRef) Then Return Nothing
        Return Providers.FirstOrDefault(
            Function(p) String.Equals(p.KeyRef, KeyRef, StringComparison.OrdinalIgnoreCase))
    End Function

#End Region

#Region "地址拼接（照抄官方规则）"

    ''' <summary>按官方规则拼出 chat-completions 的完整请求地址。</summary>
    Public Function BuildChatUrl(BaseUrl As String) As String
        If String.IsNullOrWhiteSpace(BaseUrl) Then Return Nothing
        Return BaseUrl.TrimEnd("/"c) & "/chat/completions"
    End Function

    ''' <summary>
    ''' 按官方规则拼出 messages 的完整请求地址。
    ''' </summary>
    ''' <remarks>
    ''' 规则：**只把末尾严格匹配的 <c>/v1</c> 路径段视为已有 Anthropic API 版本**，
    ''' 这时追加 <c>/messages</c>；其他基址一律追加 <c>/v1/messages</c>。
    ''' 所以 <c>https://api.deepseek.com/anthropic</c> → <c>.../anthropic/v1/messages</c>。
    ''' </remarks>
    Public Function BuildMessagesUrl(BaseUrl As String) As String
        If String.IsNullOrWhiteSpace(BaseUrl) Then Return Nothing
        Dim b As String = BaseUrl.TrimEnd("/"c)
        If b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) Then Return b & "/messages"
        Return b & "/v1/messages"
    End Function

    ''' <summary>拼出「列出模型」的探测地址（OpenAI 兼容约定）。</summary>
    Public Function BuildModelsUrl(BaseUrl As String) As String
        If String.IsNullOrWhiteSpace(BaseUrl) Then Return Nothing
        Return BaseUrl.TrimEnd("/"c) & "/models"
    End Function

    ''' <summary>某个 provider 实际会用到的请求地址（官方返回其默认端点）。</summary>
    Public Function ResolveRequestUrl(Provider As DshProvider) As String
        If Provider Is Nothing Then Return Nothing
        If Provider.IsOfficial Then Return BuildMessagesUrl(OfficialMessagesBaseUrl)
        If String.Equals(Provider.Protocol, ProtocolMessages, StringComparison.OrdinalIgnoreCase) Then
            Return BuildMessagesUrl(Provider.BaseUrl)
        End If
        Return BuildChatUrl(Provider.BaseUrl)
    End Function

#End Region

#Region "把 provider 应用到实例"

    ''' <summary>
    ''' 把某个 provider 写进实例的 settings.yaml。
    ''' </summary>
    ''' <param name="Instance">目标实例。</param>
    ''' <param name="Provider">要使用的 provider。</param>
    ''' <param name="FailureReason">失败原因（给用户看）。</param>
    ''' <returns>成功返回 True。</returns>
    ''' <remarks>
    ''' 官方 provider = **删掉** <c>llm-deepseek</c> 分节，让 dsh 用内置默认值。
    ''' 注意**不能**写一个 baseURL 指向官方地址的分节 —— 官方文档明确说
    ''' 「删除该覆盖即可使用官方 Messages 默认值」，
    ''' 而显式写 <c>https://api.deepseek.com</c> 会被当成 **Chat** 根地址，行为完全不同。
    ''' </remarks>
    Public Function ApplyToInstance(Instance As DshInstance, Provider As DshProvider,
                                    ByRef FailureReason As String) As Boolean
        FailureReason = Nothing
        If Instance Is Nothing Then
            FailureReason = "实例不存在。"
            Return False
        End If
        If Provider Is Nothing Then
            FailureReason = "接入方式不存在。"
            Return False
        End If

        Try
            If Provider.IsOfficial Then
                Return DshSettings.RemoveSection(Instance, DshSettings.NsDeepSeek)
            End If

            Dim baseUrl As String = If(Provider.BaseUrl, "").Trim()
            If baseUrl.Length = 0 Then
                FailureReason = $"「{Provider.DisplayLabel}」还没有填写 API 地址。"
                Return False
            End If
            If Not baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) Then
                FailureReason = "API 地址必须以 http:// 或 https:// 开头。"
                Return False
            End If

            Dim proto As String =
                If(String.Equals(Provider.Protocol, ProtocolMessages, StringComparison.OrdinalIgnoreCase),
                   ProtocolMessages, ProtocolChat)

            Dim body As New List(Of String) From {
                $"  protocol: {proto}",
                $"  apiKeyEnv: {Provider.KeyRef}",
                $"  baseURL: {baseUrl}"
            }

            '填了模型名才写 models 段 —— 不填就让 dsh 用内置目录
            Dim model As String = If(Provider.Model, "").Trim()
            If model.Length > 0 Then
                body.Add("  models:")
                body.Add($"    - id: {model}")
                body.Add($"      contextWindow: {If(Provider.ContextWindow > 0, Provider.ContextWindow, 128000)}")
            End If

            Return DshSettings.WriteSectionRaw(Instance, DshSettings.NsDeepSeek, body, FailureReason)
        Catch ex As Exception
            FailureReason = $"应用配置失败：{ex.Message}"
            Logger.Error(ex, $"DSH：向实例「{Instance.DisplayName}」应用接入方式失败")
            Return False
        End Try
    End Function

    ''' <summary>把某个 provider 应用到全部实例。</summary>
    ''' <returns>成功的实例数；失败清单通过 <paramref name="Failed"/> 返回。</returns>
    Public Function ApplyToAll(Provider As DshProvider, ByRef Failed As List(Of String)) As Integer
        Failed = New List(Of String)
        Dim ok As Integer = 0

        For Each inst In ModDSH.DshInstances
            Dim reason As String = Nothing
            If ApplyToInstance(inst, Provider, reason) Then
                ok += 1
            Else
                Failed.Add($"{inst.DisplayName}（{reason}）")
            End If
        Next

        Logger.Info($"DSH：接入方式「{If(Provider Is Nothing, "?", Provider.DisplayLabel)}」已应用到 {ok} 个实例（失败 {Failed.Count} 个）")
        Return ok
    End Function

#End Region

#Region "读实例当前用的是哪个 provider"

    ''' <summary>
    ''' 读出某个实例当前使用的 provider。
    ''' </summary>
    ''' <param name="Instance">目标实例。</param>
    ''' <param name="Providers">provider 列表（用来把凭据名映射回 provider）。</param>
    ''' <returns>找不到时返回官方 provider（这也正是"没有 llm-deepseek 分节"的含义）。</returns>
    ''' <remarks>
    ''' 真相来源就是实例自己的 settings.yaml —— **不需要额外存一份"这个实例选了谁"的标记**：
    '''   有 <c>llm-deepseek</c> 分节且 apiKeyEnv 指向某个 provider → 就是它
    '''   没有分节 → 官方（dsh 的内置默认值）
    ''' 这样即使用户直接编辑了 settings.yaml，界面显示也不会和实际不符。
    ''' </remarks>
    Public Function GetInstanceProvider(Instance As DshInstance,
                                        Providers As List(Of DshProvider)) As DshProvider
        Dim official As DshProvider = GetProvider(Providers, OfficialProviderId)
        If official Is Nothing Then official = CreateOfficialProvider()
        If Instance Is Nothing Then Return official

        Try
            Dim section As Dictionary(Of String, String) = DshSettings.ReadSection(Instance, DshSettings.NsDeepSeek)
            If section.Count = 0 Then Return official

            Dim keyRef As String = Nothing
            If section.ContainsKey("apiKeyEnv") Then keyRef = section("apiKeyEnv")
            If String.IsNullOrWhiteSpace(keyRef) Then Return official

            Dim matched As DshProvider = GetProviderByKeyRef(Providers, keyRef)
            '认不出来的凭据名：可能是用户手改的 settings.yaml。
            '这时候不能当成官方（端点其实是自定义的），构造一个临时 provider 如实反映
            If matched Is Nothing Then
                Dim baseUrl As String = If(section.ContainsKey("baseURL"), section("baseURL"), "")
                Dim proto As String = If(section.ContainsKey("protocol"), section("protocol"), ProtocolChat)
                Return New DshProvider With {
                    .Id = "unknown",
                    .Name = $"未知接入方式（{keyRef}）",
                    .BaseUrl = baseUrl,
                    .Protocol = proto
                }
            End If
            Return matched
        Catch ex As Exception
            Logger.Error(ex, "DSH：读取实例的接入方式失败")
            Return official
        End Try
    End Function

#End Region

#Region "连接测试"

    ''' <summary>一次连接测试的结果。</summary>
    Public Class ApiProbeResult
        Public Property Ok As Boolean
        ''' <summary>耗时（毫秒）；失败为 -1。</summary>
        Public Property LatencyMs As Integer = -1
        ''' <summary>给用户看的说明。</summary>
        Public Property Detail As String
        ''' <summary>实际探测的地址（方便排查）。</summary>
        Public Property Endpoint As String
        ''' <summary>HTTP 状态码；未发出请求为 0。</summary>
        Public Property StatusCode As Integer = 0
    End Class

    ''' <summary>
    ''' 测试一组「地址 + 协议 + 密钥」能否真的调通。
    ''' </summary>
    ''' <param name="BaseUrl">基址。</param>
    ''' <param name="ProtocolName">协议。</param>
    ''' <param name="ApiKey">密钥。</param>
    ''' <param name="TimeoutMs">超时。</param>
    ''' <remarks>
    ''' 阻塞调用，请放在后台线程。
    '''
    ''' 探测策略（按「尽量便宜、又要真实验证」取舍）：
    '''   1. 先打 <c>GET {base}/models</c> —— OpenAI 兼容网关的通用约定，
    '''      不消耗 token，能直接区分 401（密钥错）与 404（地址错）
    '''   2. 404/405 时退到真正的最小对话请求（messages 协议走 <c>/v1/messages</c>，
    '''      chat 走 <c>/chat/completions</c>），用 <c>max_tokens: 1</c> 把成本压到最低
    ''' 实测官方端点上四种「端点 × 鉴权头」组合都返回 401 JSON（不是 404），
    ''' 说明两种鉴权头都被接受，可以放心同时带上。
    ''' </remarks>
    Public Function TestConnection(BaseUrl As String, ProtocolName As String, ApiKey As String,
                                   Optional TimeoutMs As Integer = 20000) As ApiProbeResult
        Dim result As New ApiProbeResult()
        Dim key As String = If(ApiKey, "").Trim()

        If String.IsNullOrWhiteSpace(key) Then
            result.Detail = "没有可测试的密钥，请先填写。"
            Return result
        End If
        If String.IsNullOrWhiteSpace(BaseUrl) Then
            result.Detail = "没有填写 API 地址。"
            Return result
        End If

        '── 第 1 步：GET /models ──
        Dim modelsUrl As String = BuildModelsUrl(BaseUrl)
        Dim r1 As ApiProbeResult = TestOnce(modelsUrl, "GET", Nothing, key, ProtocolName, TimeoutMs)
        If r1.Ok Then Return r1
        '401/403 是**确定的**结论（地址对、密钥错），不必再退
        If r1.StatusCode = 401 OrElse r1.StatusCode = 403 Then Return r1

        '── 第 2 步：真正的最小对话请求 ──
        Dim talkUrl As String =
            If(String.Equals(ProtocolName, ProtocolMessages, StringComparison.OrdinalIgnoreCase),
               BuildMessagesUrl(BaseUrl), BuildChatUrl(BaseUrl))
        Dim body As String = BuildMinimalBody()
        Dim r2 As ApiProbeResult = TestOnce(talkUrl, "POST", body, key, ProtocolName, TimeoutMs)
        If r2.Ok Then Return r2

        '两个都不通：把更有信息量的那个报给用户。
        '优先报 401（密钥问题）而不是 404（路径问题）—— 后者常常只是网关没实现 /models。
        If r2.StatusCode = 401 OrElse r2.StatusCode = 403 Then Return r2
        If r1.StatusCode = 404 OrElse r1.StatusCode = 405 Then Return r2
        Return r1
    End Function

    ''' <summary>发一次请求并翻译结果。</summary>
    Private Function TestOnce(Url As String, Method As String, Body As String,
                              ApiKey As String, ProtocolName As String,
                              TimeoutMs As Integer) As ApiProbeResult
        Dim result As New ApiProbeResult With {.Endpoint = Url}
        If String.IsNullOrWhiteSpace(Url) Then
            result.Detail = "地址为空。"
            Return result
        End If

        Dim sw As Diagnostics.Stopwatch = Diagnostics.Stopwatch.StartNew()
        Try
            Using cts As New Threading.CancellationTokenSource(Math.Max(2000, TimeoutMs))
                Using req As New HttpRequestMessage(If(Method = "POST", HttpMethod.Post, HttpMethod.Get), Url)
                    '两种鉴权头都带上：OpenAI 系用 Authorization，Anthropic 系用 x-api-key。
                    '实测官方端点两者都接受，多带一个没有副作用。
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " & ApiKey)
                    req.Headers.TryAddWithoutValidation("x-api-key", ApiKey)
                    req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01")
                    req.Headers.TryAddWithoutValidation("Accept", "application/json")

                    If Method = "POST" AndAlso Not String.IsNullOrWhiteSpace(Body) Then
                        req.Content = New Net.Http.StringContent(Body, Encoding.UTF8, "application/json")
                    End If

                    Using resp = DshRegistry.DshHttpClient.SendAsync(req, cts.Token).GetAwaiter().GetResult()
                        sw.Stop()
                        result.LatencyMs = CInt(sw.ElapsedMilliseconds)
                        result.StatusCode = CInt(resp.StatusCode)

                        Dim text As String = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()

                        If resp.IsSuccessStatusCode Then
                            result.Ok = True
                            result.Detail = $"连接成功（HTTP {result.StatusCode}，{result.LatencyMs} ms）"
                            Return result
                        End If

                        result.Ok = False
                        result.Detail = DescribeHttpStatus(result.StatusCode, text)
                        Return result
                    End Using
                End Using
            End Using
        Catch ex As Exception
            sw.Stop()
            result.LatencyMs = CInt(sw.ElapsedMilliseconds)
            result.Ok = False
            result.Detail = DshRegistry.DescribeNetworkError(ex)
            Return result
        End Try
    End Function

    ''' <summary>把 HTTP 状态码翻译成人话，并尽量把服务端返回的原因带出来。</summary>
    Private Function DescribeHttpStatus(Code As Integer, Body As String) As String
        Dim reason As String = ExtractErrorMessage(Body)

        Select Case Code
            Case 401
                Return $"密钥被拒绝（HTTP 401）。{If(reason, "请确认密钥正确且未过期。")}"
            Case 403
                Return $"访问被拒绝（HTTP 403）。{If(reason, "可能是密钥权限不足，或该网关不允许此来源。")}"
            Case 404
                Return $"地址不存在（HTTP 404）。{If(reason, "请检查 API 地址是否写对、以及是否需要在末尾加 /v1。")}"
            Case 405
                Return $"该地址不接受这种请求方式（HTTP 405）。{reason}"
            Case 429
                Return $"请求过于频繁（HTTP 429）。{If(reason, "稍后再试。")}"
            Case 500, 502, 503, 504
                Return $"网关侧错误（HTTP {Code}）。{If(reason, "这通常是对方服务的问题，不是密钥或地址写错。")}"
            Case Else
                Return $"意外的状态码 HTTP {Code}。{reason}"
        End Select
    End Function

    ''' <summary>从错误响应里挖出 message 字段。</summary>
    Private Function ExtractErrorMessage(Body As String) As String
        If String.IsNullOrWhiteSpace(Body) Then Return Nothing
        Try
            Dim o As JObject = JObject.Parse(Body)
            Dim msg As String = o("error")?("message")?.ToString()
            If String.IsNullOrWhiteSpace(msg) Then msg = o("message")?.ToString()
            If String.IsNullOrWhiteSpace(msg) Then Return Nothing
            '太长就截断，错误提示里塞不下整段
            If msg.Length > 160 Then msg = msg.Substring(0, 160) & "…"
            Return msg
        Catch ex As Exception
            '不是 JSON —— 直接把前一段原文带出来，总比没有强
            Dim t As String = Body.Trim()
            If t.Length = 0 Then Return Nothing
            If t.Length > 120 Then t = t.Substring(0, 120) & "…"
            Return t
        End Try
    End Function

    ''' <summary>构造一个最小的对话请求体（成本压到最低）。</summary>
    Private Function BuildMinimalBody() As String
        Dim o As New JObject()
        o("model") = "deepseek-flash"
        o("max_tokens") = 1
        o("messages") = New JArray From {
            New JObject From {{"role", "user"}, {"content", "ping"}}
        }
        Return o.ToString(Newtonsoft.Json.Formatting.None)
    End Function

#End Region

End Module
