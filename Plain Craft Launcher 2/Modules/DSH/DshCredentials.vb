Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports System.Text.RegularExpressions

''' <summary>
''' dsh 凭据文件（<c>$DSH_HOME/.credentials.yaml</c>）的读写。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 为什么直接写文件而不是调 dsh 的 HTTP 接口
''' ═══════════════════════════════════════════════════════════════════════
''' dsh 的 web 端有一个 <c>dsh-api-settings-controller</c>，暴露
''' <c>credentials.describe / set / unset</c> 三个远程方法，底层就是同一个存储。
''' 但调用它需要 dsh 服务**已经在运行**并且要带认证 token ——
''' 而「配置 API Key」这件事恰恰经常发生在服务还没起来的时候。
''' 直接写文件没有这个前置依赖，且与官方行为等价（已实测）。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 文件格式（实测）
''' ═══════════════════════════════════════════════════════════════════════
''' <code>
''' version: 1
''' refs:
'''   DEEPSEEK_API_KEY: sk-xxxxxxxx
''' records:
'''   client-connection/browser-session:
'''     kind: grant
'''     payload: {...}
''' </code>
'''
''' 读取优先级（高 → 低）：
'''   启动环境变量 &gt; <b>凭据文件 refs</b> &gt; <c>&lt;cwd&gt;/.env</c> &gt; <c>$DSH_HOME/.env</c>
''' 也就是说，写 <c>refs</c> 段能**压过**两个 .env 文件，且立刻生效 —— 不需要重启。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 为什么必须做「行级补丁」而不是整体重写 YAML
''' ═══════════════════════════════════════════════════════════════════════
''' <c>records</c> 段里存着浏览器会话的授权记录，格式复杂且由 dsh 自己维护；
''' 用户也可能在文件里写了注释。整体重新序列化会：
'''   1. 丢掉所有注释
'''   2. 丢掉或改坏 records 段（dsh 的 yaml 库对某些类型有自己的 tag 约定）
'''   3. 把用户手写的排版全打乱
''' 所以这里只在 <c>refs:</c> 块内部做最小改动：定位 → 替换/插入/删除一行。
''' 其余字节原样保留。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 踩坑记录
''' ═══════════════════════════════════════════════════════════════════════
'''   - ref 名文法严格为 <c>^[A-Za-z_][A-Za-z0-9_]*$</c>，不合法会被 dsh 拒绝
'''   - **空值会被拒绝**：想删密钥必须**删掉整个键**，不能设成空字符串
'''   - 值里出现冒号 / 井号 / 引号 / 反斜杠 / 中文 / emoji / 换行都必须无损往返
''' </summary>
Public Module DshCredentials

#Region "常量"

    ''' <summary>
    ''' DeepSeek API Key 使用的默认 ref 名。
    ''' 对应 dsh <c>llm-deepseek</c> 插件的 <c>apiKeyEnv</c> 默认值。
    ''' </summary>
    Public Const DeepSeekApiKeyRef As String = "DEEPSEEK_API_KEY"

    ''' <summary>DeepSeek 官方 API 根地址（chat-completions 协议用的那个）。</summary>
    Private Const DeepSeekApiBase As String = "https://api.deepseek.com"

    ''' <summary>ref 名的合法文法。与 dsh 内部的校验规则一致。</summary>
    Private ReadOnly RefNamePattern As New Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled)

    ''' <summary>块标量指示符。</summary>
    Private ReadOnly BlockScalarMarkers As String() = {"|", "|-", "|+", ">", ">-", ">+"}

#End Region

#Region "路径"

    ''' <summary>
    ''' 解析目标 home 目录。传 Nothing / 空 时回退到**主实例**的 home。
    ''' </summary>
    ''' <remarks>
    ''' 多实例改造后凭据是**按实例**存放的（每个实例一个 DSH_HOME）。
    ''' 但绝大多数调用点只关心「当前那个实例」，所以给个默认值能让调用方少写很多代码。
    ''' </remarks>
    Private Function ResolveHome(HomeDir As String) As String
        If String.IsNullOrWhiteSpace(HomeDir) Then Return ModDSH.DshHomeDir
        Return HomeDir
    End Function

    ''' <summary>某个 home 目录对应的凭据文件路径。</summary>
    Public Function CredentialsFileFor(HomeDir As String) As String
        Dim h As String = ResolveHome(HomeDir)
        If h.EndsWith("\", StringComparison.Ordinal) Then Return h & ".credentials.yaml"
        Return h & "\.credentials.yaml"
    End Function

    ''' <summary>
    ''' **主实例**的 dsh 凭据文件绝对路径（兼容属性）。
    ''' </summary>
    ''' <remarks>
    ''' 恒等于 <c>$DSH_HOME/.credentials.yaml</c>。
    ''' 我们启动 dsh 时会强制把 <c>DSH_HOME</c> 设成实例自己的 home 目录，
    ''' 所以这个路径是权威的，不需要去读环境变量。
    ''' </remarks>
    Public ReadOnly Property DshCredentialsFile As String
        Get
            Return CredentialsFileFor(Nothing)
        End Get
    End Property

    ''' <summary>主实例的凭据文件是否已经存在（兼容属性）。</summary>
    Public ReadOnly Property DshCredentialsExists As Boolean
        Get
            Return DshRuntime.FileExistsSafe(DshCredentialsFile)
        End Get
    End Property

    ''' <summary>
    ''' 确保某个实例的凭据文件存在，不存在则写入一份最小骨架。
    ''' </summary>
    ''' <param name="Instance">目标实例。</param>
    ''' <returns>文件最终存在时返回 True。</returns>
    ''' <remarks>
    ''' ⚠️ **必须带 <c>version: 1</c>**，否则 dsh 会以
    ''' <c>uses the pre-release flat layout</c> 拒绝整份文件 —— 那是硬失败，不是警告。
    '''
    ''' ⚠️ 文件**已存在时绝不覆盖**：凭据文件里除了 <c>refs</c> 还有 dsh 自己维护的
    ''' <c>records</c> 段，覆写会把用户已授权的记录清掉。
    '''
    ''' 这是幂等的，可以在「一键修复」里反复调用。
    ''' </remarks>
    Public Function EnsureCredentialsFile(Instance As DshInstance) As Boolean
        If Instance Is Nothing Then Return False
        ' ⚠️ 变量名不能用 Path —— VB 大小写不敏感，会遮蔽 System.IO.Path，
        ' 于是下面 Path.GetDirectoryName 会被解析成 String 的成员 → BC30456
        Dim credFile As String = CredentialsFileFor(Instance.HomeDir)

        If DshRuntime.FileExistsSafe(credFile) Then Return True

        Try
            Dim dir As String = Path.GetDirectoryName(credFile)
            If Not String.IsNullOrWhiteSpace(dir) AndAlso Not Directory.Exists(dir) Then
                Directory.CreateDirectory(dir)
            End If

            ' 最小合法骨架。refs 段留空，dsh 读起来是「还没有任何密钥」。
            Dim body As String =
                "# 由 PCL DSH 创建。dsh 会自行填充 records 段，请勿手动删除本文件。" & vbCrLf &
                "version: 1" & vbCrLf &
                "refs: {}" & vbCrLf

            File.WriteAllText(credFile, body, New UTF8Encoding(False))
            Logger.Info($"DSH：已创建凭据文件骨架 → {credFile}")
            Return True
        Catch ex As Exception
            Logger.Error(ex, $"DSH：创建凭据文件失败：{credFile}")
            Return False
        End Try
    End Function

#End Region

#Region "校验"

    ''' <summary>
    ''' ref 名是否合法（<c>^[A-Za-z_][A-Za-z0-9_]*$</c>）。
    ''' </summary>
    Public Function IsValidRefName(Name As String) As Boolean
        If String.IsNullOrWhiteSpace(Name) Then Return False
        Return RefNamePattern.IsMatch(Name.Trim())
    End Function

    ''' <summary>
    ''' 校验用户输入的 API Key 是否「看起来像那么回事」。
    ''' </summary>
    ''' <returns>合法返回 Nothing；否则返回给用户看的错误说明。</returns>
    ''' <remarks>
    ''' 只做**形状校验**，不联网 —— 真正的有效性由 <see cref="ProbeApiKey"/> 负责。
    ''' 形状校验存在的意义是拦住最常见的误操作：粘贴了空串、粘了半截、
    ''' 或者把别的东西（比如 GitHub token）粘进来了。
    ''' </remarks>
    Public Function ValidateApiKeyShape(Value As String) As String
        Dim v As String = If(Value, "").Trim()

        If v.Length = 0 Then
            Return "API Key 不能为空。若要清除已保存的密钥，请点击「清除」按钮。"
        End If
        If v.Length < 16 Then
            Return $"API Key 太短了（当前 {v.Length} 个字符），请检查是否复制完整。"
        End If
        If v.Contains(" ") OrElse v.Contains(vbTab) OrElse v.Contains(vbCr) OrElse v.Contains(vbLf) Then
            Return "API Key 中不应包含空格或换行，请检查是否多复制了内容。"
        End If
        If Not v.StartsWith("sk-", StringComparison.OrdinalIgnoreCase) Then
            Return "DeepSeek 的 API Key 通常以 sk- 开头，请确认没有复制错。"
        End If
        Return Nothing
    End Function

#End Region

#Region "读取"

    ''' <summary>
    ''' 读取某个 home 下 <c>refs</c> 段里的全部键值对。
    ''' </summary>
    ''' <param name="HomeDir">目标 home；Nothing = 主实例。</param>
    ''' <returns>键值对字典。文件不存在或没有 refs 段时返回空字典（不抛异常）。</returns>
    Public Function ReadRefs(Optional HomeDir As String = Nothing) As Dictionary(Of String, String)
        Dim result As New Dictionary(Of String, String)(StringComparer.Ordinal)
        Dim path As String = CredentialsFileFor(HomeDir)
        If Not DshRuntime.FileExistsSafe(path) Then Return result

        Dim lines As String()
        Try
            lines = File.ReadAllLines(path, Encoding.UTF8)
        Catch ex As Exception
            Logger.Error(ex, $"DSH：读取凭据文件失败：{path}")
            Return result
        End Try

        Dim headerIdx As Integer = -1
        Dim blockEnd As Integer = -1
        If Not LocateRefsBlock(lines, headerIdx, blockEnd) Then Return result

        Dim i As Integer = headerIdx + 1
        While i < blockEnd
            Dim line As String = lines(i)
            Dim trimmed As String = line.TrimStart()

            '空行与注释跳过
            If trimmed.Length = 0 OrElse trimmed(0) = "#"c Then
                i += 1
                Continue While
            End If

            Dim m As Match = Regex.Match(trimmed, "^(""[^""]*""|'[^']*'|[^:\s]+)\s*:(.*)$")
            If Not m.Success Then
                i += 1
                Continue While
            End If

            Dim key As String = UnquoteScalar(m.Groups(1).Value)
            Dim rest As String = m.Groups(2).Value.Trim()

            If IsBlockScalarMarker(rest) Then
                '块标量：把后续缩进行拼起来
                Dim sb As New StringBuilder()
                Dim j As Integer = i + 1
                While j < blockEnd
                    Dim bl As String = lines(j)
                    If bl.Trim().Length = 0 Then
                        sb.Append(vbLf)
                        j += 1
                        Continue While
                    End If
                    If IndentOf(bl) = 0 Then Exit While
                    sb.Append(bl.TrimStart()).Append(vbLf)
                    j += 1
                End While
                Dim folded As String = sb.ToString()
                If rest.StartsWith("|", StringComparison.Ordinal) Then
                    '字面块：保留换行，去掉尾随空行
                    folded = folded.TrimEnd(vbLf, vbCr)
                Else
                    '折叠块：换行变空格
                    folded = folded.Replace(vbLf, " ").Trim()
                End If
                result(key) = folded
                i = j
            Else
                result(key) = ParseScalar(rest)
                i += 1
            End If
        End While

        Return result
    End Function

    ''' <summary>读取单个 ref 的值。不存在时返回 Nothing。</summary>
    Public Function GetRef(Ref As String, Optional HomeDir As String = Nothing) As String
        If String.IsNullOrWhiteSpace(Ref) Then Return Nothing
        Dim all = ReadRefs(HomeDir)
        Dim value As String = Nothing
        If all.TryGetValue(Ref.Trim(), value) Then Return value
        Return Nothing
    End Function

    ''' <summary>该 ref 是否已配置（且值非空）。</summary>
    Public Function HasRef(Ref As String, Optional HomeDir As String = Nothing) As Boolean
        Return Not String.IsNullOrWhiteSpace(GetRef(Ref, HomeDir))
    End Function

    ''' <summary>读取 DeepSeek API Key。未配置时返回 Nothing。</summary>
    Public Function GetDeepSeekApiKey(Optional HomeDir As String = Nothing) As String
        Return GetRef(DeepSeekApiKeyRef, HomeDir)
    End Function

    ''' <summary>DeepSeek API Key 是否已配置。</summary>
    Public Function HasDeepSeekApiKey(Optional HomeDir As String = Nothing) As Boolean
        Return HasRef(DeepSeekApiKeyRef, HomeDir)
    End Function

    ''' <summary>
    ''' 是否**所有**实例都已配置 DeepSeek API Key。
    ''' </summary>
    ''' <remarks>
    ''' 主页要显示「在线模式是否可用」，就得看全体而不是某一个 ——
    ''' 用户新建一个实例后如果密钥没同步过去，那个实例就是不可用的。
    ''' </remarks>
    Public Function HasDeepSeekApiKeyEverywhere() As Boolean
        Dim all = ModDSH.DshInstances
        If all.Count = 0 Then Return HasDeepSeekApiKey()
        For Each inst In all
            If Not HasDeepSeekApiKey(inst.HomeDir) Then Return False
        Next
        Return True
    End Function

    ''' <summary>从任意实例读一把密钥出来（用于填充输入框 / 显示状态）。</summary>
    ''' <remarks>
    ''' 各实例的密钥理论上应该一致（都靠同步写入），
    ''' 但用户可以对单个实例单独设置。这里取第一个能读到的，够用。
    ''' </remarks>
    Public Function GetAnyKey(Ref As String) As String
        If String.IsNullOrWhiteSpace(Ref) Then Return Nothing
        For Each inst In ModDSH.DshInstances
            Dim v As String = GetRef(Ref, inst.HomeDir)
            If Not String.IsNullOrWhiteSpace(v) Then Return v
        Next
        Return Nothing
    End Function

    ''' <summary>
    ''' 读取「共享密钥」—— 以**主实例**（列表第一个）为准。
    ''' </summary>
    ''' <remarks>
    ''' 这是"共享 vs 单独"这套模型的锚点：
    '''   - 主卡片「保存并同步」= 把所有实例都刷成这把
    '''   - 某个实例的密钥与它相同 → 显示"共享"
    '''   - 不同 → 显示"单独设置"
    ''' 不需要额外存一份"谁被单独设置过"的标记，比较值就够了。
    ''' </remarks>
    Public Function GetSharedKey(Ref As String) As String
        Dim primary As DshInstance = ModDSH.DshPrimaryInstance
        If primary Is Nothing Then Return GetAnyKey(Ref)
        Return GetRef(Ref, primary.HomeDir)
    End Function

    ''' <summary>该实例的密钥是否与共享密钥一致（含"两者都为空"）。</summary>
    Public Function IsKeyShared(Ref As String, Instance As DshInstance) As Boolean
        If Instance Is Nothing Then Return False
        Dim mine As String = GetRef(Ref, Instance.HomeDir)
        Dim sharedKey As String = GetSharedKey(Ref)
        If String.IsNullOrWhiteSpace(mine) AndAlso String.IsNullOrWhiteSpace(sharedKey) Then Return True
        Return String.Equals(mine, sharedKey, StringComparison.Ordinal)
    End Function

    ''' <summary>某个 ref 是否在**所有**实例上都已配置。</summary>
    Public Function HasKeyEverywhere(Ref As String) As Boolean
        Dim all = ModDSH.DshInstances
        If all.Count = 0 Then Return HasRef(Ref)
        For Each inst In all
            If Not HasRef(Ref, inst.HomeDir) Then Return False
        Next
        Return True
    End Function

#End Region

#Region "写入"

    ''' <summary>
    ''' 设置一个 ref 的值（行级补丁，保留文件其余内容）。
    ''' </summary>
    ''' <param name="Ref">ref 名，必须满足 <c>^[A-Za-z_][A-Za-z0-9_]*$</c>。</param>
    ''' <param name="Value">值。**不允许为空** —— 删除请用 <see cref="UnsetRef"/>。</param>
    ''' <param name="HomeDir">目标 home；Nothing = 主实例。</param>
    ''' <exception cref="ArgumentException">ref 名不合法，或值为空。</exception>
    ''' <remarks>
    ''' ⚠️ 空值必须走 <see cref="UnsetRef"/>：
    ''' dsh 会拒绝空字符串值的 ref（不是当作"未设置"，而是直接报错），
    ''' 所以「清空输入框」这个动作在语义上等于「删除这个键」。
    ''' </remarks>
    Public Sub SetRef(Ref As String, Value As String, Optional HomeDir As String = Nothing)
        Dim key As String = If(Ref, "").Trim()
        If Not IsValidRefName(key) Then
            Throw New ArgumentException(
                $"非法的凭据名「{Ref}」。" & vbCrLf &
                "只允许字母、数字与下划线，且不能以数字开头。", NameOf(Ref))
        End If

        Dim raw As String = If(Value, "")
        If raw.Length = 0 Then
            Throw New ArgumentException(
                "凭据值不能为空。若要删除该凭据，请使用「清除」功能。", NameOf(Value))
        End If

        Try
            Dim path As String = CredentialsFileFor(HomeDir)
            Dim dir As String = IO.Path.GetDirectoryName(path)
            If Not String.IsNullOrWhiteSpace(dir) AndAlso Not Directory.Exists(dir) Then
                Directory.CreateDirectory(dir)
            End If
            Dim lines As List(Of String) =
                If(DshRuntime.FileExistsSafe(path),
                   New List(Of String)(File.ReadAllLines(path, Encoding.UTF8)),
                   New List(Of String)())

            Dim headerIdx As Integer = -1
            Dim blockEnd As Integer = -1

            If Not LocateRefsBlock(lines.ToArray(), headerIdx, blockEnd) Then
                '文件里还没有 refs 段 —— 建一个
                InsertRefsBlock(lines)
                '重建后重新定位
                If Not LocateRefsBlock(lines.ToArray(), headerIdx, blockEnd) Then
                    Throw New InvalidOperationException("无法在凭据文件中建立 refs 段。")
                End If
            End If

            '⚠️ version 段是**必须**的。
            ' dsh 的 parseCredentialsDocument 会先检查 `'version' in root`，
            ' 缺失就直接抛 "uses the pre-release flat layout"，**整份文件作废**。
            ' 也就是说：只写 refs 不写 version，用户看到的会是「密钥明明填了却用不了」。
            EnsureVersionKey(lines)

            '插过 version 之后行号会整体下移，必须**重新定位**，否则会把值写错位置
            If Not LocateRefsBlock(lines.ToArray(), headerIdx, blockEnd) Then
                Throw New InvalidOperationException("建立 version 段后无法重新定位 refs 段。")
            End If

            Dim indent As String = DetectChildIndent(lines, headerIdx, blockEnd)
            Dim keyIdx As Integer = FindKeyLine(lines, headerIdx, blockEnd, key)

            '⚠️ FormatScalar 返回的**第一项必须接在 `key:` 同一行**上。
            ' 早期版本把它当成独立一行追加，结果写出：
            '     DEEPSEEK_API_KEY:
            '     sk-xxxx
            ' YAML 解析器会把第二行当成新的（隐含）映射键 → 整份文件报错。
            ' 简单标量的第一项就是值本身；块标量的第一项是指示符（|-）。
            Dim parts As List(Of String) = FormatScalar(raw, indent & "  ")
            Dim newLines As New List(Of String) From {indent & key & ": " & parts(0)}
            If parts.Count > 1 Then newLines.AddRange(parts.Skip(1))

            If keyIdx >= 0 Then
                '替换：先把旧条目（含续行）整段删掉，再在同样的位置插入新条目
                Dim oldIndent As Integer = IndentOf(lines(keyIdx))
                lines.RemoveAt(keyIdx)
                While keyIdx < lines.Count
                    Dim l As String = lines(keyIdx)
                    If l.Trim().Length = 0 Then Exit While
                    If IndentOf(l) > oldIndent Then
                        lines.RemoveAt(keyIdx)
                    Else
                        Exit While
                    End If
                End While
                lines.InsertRange(keyIdx, newLines)
            Else
                '新增：插到 refs 块最后一个非空子项之后
                lines.InsertRange(LastChildInsertIndex(lines, headerIdx, blockEnd), newLines)
            End If

            WriteAllLinesAtomic(path, lines)
            Logger.Info($"DSH：已写入凭据 {key}（长度 {raw.Length}）")
        Catch ex As ArgumentException
            Throw
        Catch ex As Exception
            Logger.Error(ex, $"DSH：写入凭据失败：{Ref}")
            Throw New InvalidOperationException($"写入凭据失败：{ex.Message}", ex)
        End Try
    End Sub

    ''' <summary>
    ''' 删除一个 ref（整行移除，含块标量续行）。
    ''' </summary>
    ''' <param name="Ref">ref 名。</param>
    ''' <param name="HomeDir">目标 home；Nothing = 主实例。</param>
    ''' <returns>确实删掉了返回 True；本来就不存在返回 False。</returns>
    Public Function UnsetRef(Ref As String, Optional HomeDir As String = Nothing) As Boolean
        Dim key As String = If(Ref, "").Trim()
        If Not IsValidRefName(key) Then Return False

        Dim path As String = CredentialsFileFor(HomeDir)
        If Not DshRuntime.FileExistsSafe(path) Then Return False

        Try
            Dim lines As New List(Of String)(File.ReadAllLines(path, Encoding.UTF8))
            Dim headerIdx As Integer = -1
            Dim blockEnd As Integer = -1
            If Not LocateRefsBlock(lines.ToArray(), headerIdx, blockEnd) Then Return False

            Dim keyIdx As Integer = FindKeyLine(lines, headerIdx, blockEnd, key)
            If keyIdx < 0 Then Return False

            Dim keyIndent As Integer = IndentOf(lines(keyIdx))
            lines.RemoveAt(keyIdx)
            While keyIdx < lines.Count
                Dim l As String = lines(keyIdx)
                If l.Trim().Length = 0 Then Exit While
                If IndentOf(l) > keyIndent Then
                    lines.RemoveAt(keyIdx)
                Else
                    Exit While
                End If
            End While

            WriteAllLinesAtomic(path, lines)
            Logger.Info($"DSH：已删除凭据 {key}")
            Return True
        Catch ex As Exception
            Logger.Error(ex, $"DSH：删除凭据失败：{Ref}")
            Throw New InvalidOperationException($"删除凭据失败：{ex.Message}", ex)
        End Try
    End Function

    ''' <summary>写入 DeepSeek API Key（先做形状校验）。</summary>
    ''' <param name="Value">密钥明文。</param>
    ''' <param name="HomeDir">目标 home；Nothing = 主实例。</param>
    ''' <exception cref="ArgumentException">Key 形状不合法。</exception>
    Public Sub SetDeepSeekApiKey(Value As String, Optional HomeDir As String = Nothing)
        Dim problem As String = ValidateApiKeyShape(Value)
        If problem IsNot Nothing Then Throw New ArgumentException(problem, NameOf(Value))
        SetRef(DeepSeekApiKeyRef, Value.Trim(), HomeDir)
    End Sub

    ''' <summary>清除已保存的 DeepSeek API Key。</summary>
    ''' <param name="HomeDir">目标 home；Nothing = 主实例。</param>
    ''' <returns>确实清掉了返回 True。</returns>
    Public Function ClearDeepSeekApiKey(Optional HomeDir As String = Nothing) As Boolean
        Return UnsetRef(DeepSeekApiKeyRef, HomeDir)
    End Function

    ''' <summary>
    ''' 把某个 ref 的密钥写入**所有实例**（含将来新建的）。
    ''' </summary>
    ''' <param name="Ref">凭据名。官方与自定义用不同的名字，互不覆盖。</param>
    ''' <param name="Value">密钥明文。</param>
    ''' <param name="Failed">失败清单（实例显示名）。</param>
    ''' <returns>写入成功的实例数。</returns>
    ''' <remarks>
    ''' 为什么是「同步到全部」而不是「只写当前实例」：
    ''' 每个实例有自己的 DSH_HOME，也就有自己的凭据文件。用户心里只有「一把密钥」，
    ''' 如果只写当前实例，新建的实例会莫名其妙用不了 —— 那才是真的难排查。
    ''' 代价是密钥明文会出现在多处，这一点在 UI 上要说清楚。
    '''
    ''' ⚠️ 这会**覆盖**所有实例，包括用户单独设置过的那些。
    ''' UI 上必须把这一点讲明；想保留个别实例的独立密钥就不要走这个入口。
    ''' </remarks>
    Public Function SyncKeyToAllInstances(Ref As String, Value As String,
                                          Optional ByRef Failed As List(Of String) = Nothing) As Integer
        Dim problem As String = ValidateApiKeyShape(Value)
        If problem IsNot Nothing Then Throw New ArgumentException(problem, NameOf(Value))
        If Not IsValidRefName(Ref) Then
            Throw New ArgumentException($"非法的凭据名「{Ref}」。", NameOf(Ref))
        End If

        Failed = New List(Of String)
        Dim ok As Integer = 0

        For Each inst In ModDSH.DshInstances
            Try
                SetRef(Ref, Value.Trim(), inst.HomeDir)
                ok += 1
            Catch ex As Exception
                Logger.Error(ex, $"DSH：向实例「{inst.DisplayName}」同步 {Ref} 失败")
                Failed.Add(inst.DisplayName)
            End Try
        Next

        Logger.Info($"DSH：{Ref} 已同步到 {ok} 个实例（失败 {Failed.Count} 个）")
        Return ok
    End Function

    ''' <summary>兼容包装：同步 DeepSeek 官方密钥。</summary>
    Public Function SyncApiKeyToAllInstances(Value As String,
                                             Optional ByRef Failed As List(Of String) = Nothing) As Integer
        Return SyncKeyToAllInstances(DeepSeekApiKeyRef, Value, Failed)
    End Function

    ''' <summary>从所有实例中清除某个 ref 的密钥。</summary>
    ''' <param name="Ref">凭据名。</param>
    ''' <returns>实际清除了凭据的实例数。</returns>
    Public Function ClearKeyFromAllInstances(Ref As String) As Integer
        Dim count As Integer = 0
        For Each inst In ModDSH.DshInstances
            Try
                If UnsetRef(Ref, inst.HomeDir) Then count += 1
            Catch ex As Exception
                Logger.Error(ex, $"DSH：清除实例「{inst.DisplayName}」的 {Ref} 失败")
            End Try
        Next
        Logger.Info($"DSH：已从 {count} 个实例清除 {Ref}")
        Return count
    End Function

    ''' <summary>兼容包装：清除 DeepSeek 官方密钥。</summary>
    Public Function ClearApiKeyFromAllInstances() As Integer
        Return ClearKeyFromAllInstances(DeepSeekApiKeyRef)
    End Function

    ''' <summary>从任意实例读一把官方密钥出来（兼容包装）。</summary>
    Public Function GetAnyDeepSeekApiKey() As String
        Return GetAnyKey(DeepSeekApiKeyRef)
    End Function

#End Region

#Region "展示辅助"

    ''' <summary>
    ''' 把密钥打码成可安全显示 / 截图的形式，形如 <c>sk-1234••••••••cdef</c>。
    ''' </summary>
    ''' <remarks>
    ''' 只保留首 7 位与末 4 位 —— 足够让用户确认「是不是我那一把」，
    ''' 又不至于泄露到能被直接使用的程度。
    ''' </remarks>
    Public Function MaskKey(Value As String) As String
        Dim v As String = If(Value, "").Trim()
        If v.Length = 0 Then Return "（未配置）"
        If v.Length <= 12 Then Return v.Substring(0, 2) & "••••••••"
        Return v.Substring(0, 7) & "••••••••" & v.Substring(v.Length - 4)
    End Function

    ''' <summary>给用户看的当前 API Key 状态描述。</summary>
    ''' <param name="HomeDir">目标 home；Nothing = 主实例。</param>
    Public Function DescribeApiKeyState(Optional HomeDir As String = Nothing) As String
        If Not HasDeepSeekApiKey(HomeDir) Then Return "未配置 API Key"
        Return MaskKey(GetDeepSeekApiKey(HomeDir))
    End Function

    ''' <summary>
    ''' 生成一份「配置摘要」，用于排查问题时展示（**不含明文密钥**）。
    ''' </summary>
    ''' <param name="HomeDir">目标 home；Nothing = 主实例。</param>
    Public Function Describe(Optional HomeDir As String = Nothing) As String
        Dim file As String = CredentialsFileFor(HomeDir)
        Dim sb As New StringBuilder()
        sb.AppendLine($"凭据文件：{file}")
        sb.AppendLine($"文件存在：{If(DshRuntime.FileExistsSafe(file), "是", "否")}")

        Dim refs = ReadRefs(HomeDir)
        sb.AppendLine($"refs 条目数：{refs.Count}")
        For Each kv In refs
            sb.AppendLine($"  - {kv.Key} = {MaskKey(kv.Value)}")
        Next

        Return sb.ToString().TrimEnd()
    End Function

#End Region

#Region "联网探测"

    ''' <summary>
    ''' 用真实请求验证一把 API Key 是否可用。
    ''' </summary>
    ''' <param name="ApiKey">待验证的密钥。</param>
    ''' <param name="TimeoutMs">超时（毫秒）。</param>
    ''' <param name="BaseUrl">
    ''' 要验证的 API 基址；留空 = 官方地址。
    ''' ⚠️ 自定义网关的密钥**必须**传它自己的地址 —— 拿官方端点去验一个
    ''' 第三方网关的密钥必然 401，会误报"密钥被拒绝"。
    ''' </param>
    ''' <returns>可用返回 Nothing；否则返回给用户看的错误说明。</returns>
    ''' <remarks>
    ''' 打的是 <c>GET /models</c> —— OpenAI 兼容协议里最便宜的鉴权端点，
    ''' 不消耗 token 配额，只验证密钥本身。
    ''' **阻塞调用**，请放在后台线程。
    ''' </remarks>
    Public Function ProbeApiKey(ApiKey As String, Optional TimeoutMs As Integer = 15000,
                                Optional BaseUrl As String = Nothing) As String
        Dim shapeProblem As String = ValidateApiKeyShape(ApiKey)
        If shapeProblem IsNot Nothing Then Return shapeProblem

        Dim baseAddr As String = If(String.IsNullOrWhiteSpace(BaseUrl), DeepSeekApiBase, BaseUrl.TrimEnd("/"c))
        Try
            Using handler As New HttpClientHandler With {.AllowAutoRedirect = False}
                Using client As New HttpClient(handler) With {.Timeout = TimeSpan.FromMilliseconds(TimeoutMs)}
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " & ApiKey.Trim())
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PCL-DSH/1.0")

                    Using resp = client.GetAsync(baseAddr & "/models").GetAwaiter().GetResult()
                        Dim code As Integer = CInt(resp.StatusCode)
                        If code = 200 Then
                            Logger.Info("DSH：API Key 验证通过")
                            Return Nothing
                        End If
                        If code = 401 OrElse code = 403 Then
                            Return $"密钥被拒绝（HTTP {code}），请确认密钥正确且未过期。"
                        End If
                        If code = 402 Then
                            Return "密钥有效，但账户余额不足（HTTP 402）。"
                        End If
                        If code = 429 Then
                            Return "请求过于频繁（HTTP 429），请稍后再试。"
                        End If
                        Return $"服务返回了意外的状态码：HTTP {code}"
                    End Using
                End Using
            End Using
        Catch ex As AggregateException
            Return DescribeHttpFailure(ex.GetBaseException())
        Catch ex As Exception
            Return DescribeHttpFailure(ex)
        End Try
    End Function

    ''' <summary>把网络异常翻译成人话。</summary>
    Private Function DescribeHttpFailure(ex As Exception) As String
        Logger.Warn(ex, "DSH：API Key 验证请求失败")
        Select Case True
            Case TypeOf ex Is TaskCanceledException
                Return "请求超时，请检查网络连接。"
            Case TypeOf ex Is HttpRequestException
                Return "无法连接 api.deepseek.com，请检查网络或代理设置。"
            Case Else
                Return $"验证失败：{ex.Message}"
        End Select
    End Function

#End Region

#Region "YAML 行级补丁（内部）"

    ''' <summary>统计一行开头的空白字符数。</summary>
    Private Function IndentOf(line As String) As Integer
        Dim n As Integer = 0
        While n < line.Length AndAlso (line(n) = " "c OrElse line(n) = ChrW(9))
            n += 1
        End While
        Return n
    End Function

    ''' <summary>
    ''' 定位顶层的 <c>refs:</c> 块。
    ''' </summary>
    ''' <param name="Lines">文件全部行。</param>
    ''' <param name="HeaderIdx">输出：<c>refs:</c> 所在行的下标。</param>
    ''' <param name="BlockEnd">输出：块结束行的下标（该行已不属于 refs 块）。</param>
    ''' <returns>找到返回 True。</returns>
    Private Function LocateRefsBlock(Lines As String(),
                                     ByRef HeaderIdx As Integer,
                                     ByRef BlockEnd As Integer) As Boolean
        HeaderIdx = -1
        BlockEnd = -1

        For i As Integer = 0 To Lines.Length - 1
            Dim l As String = Lines(i)
            If l.Length = 0 Then Continue For
            '必须是顶层键（无缩进），否则可能是某个嵌套结构里的同名键
            If l(0) = " "c OrElse l(0) = ChrW(9) Then Continue For
            If Regex.IsMatch(l, "^refs\s*:") Then
                HeaderIdx = i
                Exit For
            End If
        Next

        If HeaderIdx < 0 Then Return False

        '块结束 = 下一个顶层非空行
        BlockEnd = Lines.Length
        For i As Integer = HeaderIdx + 1 To Lines.Length - 1
            Dim l As String = Lines(i)
            If l.Trim().Length = 0 Then Continue For
            If l(0) <> " "c AndAlso l(0) <> ChrW(9) Then
                BlockEnd = i
                Exit For
            End If
        Next

        Return True
    End Function

    ''' <summary>
    ''' 在 refs 块里找某个键所在的行号。
    ''' </summary>
    ''' <returns>行号；未找到返回 -1。</returns>
    Private Function FindKeyLine(Lines As List(Of String),
                                 HeaderIdx As Integer,
                                 BlockEnd As Integer,
                                 Key As String) As Integer
        Dim pattern As String = "^(""[^""]*""|'[^']*'|" & Regex.Escape(Key) & ")\s*:"
        For i As Integer = HeaderIdx + 1 To Math.Min(BlockEnd, Lines.Count) - 1
            Dim t As String = Lines(i).TrimStart()
            If t.Length = 0 OrElse t(0) = "#"c Then Continue For
            Dim m As Match = Regex.Match(t, pattern)
            If Not m.Success Then Continue For
            '确认真的是这个键（处理带引号的情形）
            If String.Equals(UnquoteScalar(m.Groups(1).Value), Key, StringComparison.Ordinal) Then Return i
        Next
        Return -1
    End Function

    ''' <summary>探测 refs 块内现有的子项缩进；没有子项时返回两个空格。</summary>
    Private Function DetectChildIndent(Lines As List(Of String),
                                       HeaderIdx As Integer,
                                       BlockEnd As Integer) As String
        For i As Integer = HeaderIdx + 1 To Math.Min(BlockEnd, Lines.Count) - 1
            Dim l As String = Lines(i)
            If l.Trim().Length = 0 Then Continue For
            If l.TrimStart()(0) = "#"c Then Continue For
            Dim n As Integer = IndentOf(l)
            If n > 0 Then Return New String(" "c, n)
        Next
        Return "  "
    End Function

    ''' <summary>算出「插入到 refs 块末尾」应该用的行下标。</summary>
    Private Function LastChildInsertIndex(Lines As List(Of String),
                                          HeaderIdx As Integer,
                                          BlockEnd As Integer) As Integer
        Dim insertAt As Integer = HeaderIdx + 1
        For i As Integer = HeaderIdx + 1 To Math.Min(BlockEnd, Lines.Count) - 1
            If Lines(i).Trim().Length > 0 Then insertAt = i + 1
        Next
        Return insertAt
    End Function

    ''' <summary>
    ''' 确保顶层存在 <c>version: 1</c>。
    ''' </summary>
    ''' <remarks>
    ''' 位置选在「第一个顶层非注释行」之前 —— 这样能排在 <c>refs:</c> / <c>records:</c>
    ''' 前面（与 dsh 自己的排版一致），同时不会插到文件开头的注释块之上。
    ''' </remarks>
    Private Sub EnsureVersionKey(Lines As List(Of String))
        For i As Integer = 0 To Lines.Count - 1
            Dim l As String = Lines(i)
            If l.Length = 0 Then Continue For
            If l(0) = " "c OrElse l(0) = ChrW(9) Then Continue For
            If Regex.IsMatch(l, "^version\s*:") Then Return   '已存在
        Next

        '插到「第一个顶层非注释行」之前；没有这样的行就追加到末尾
        Dim insertAt As Integer = Lines.Count
        For i As Integer = 0 To Lines.Count - 1
            Dim l As String = Lines(i)
            If l.Trim().Length = 0 Then Continue For
            If l(0) = " "c OrElse l(0) = ChrW(9) Then Continue For
            If l.TrimStart()(0) = "#"c Then Continue For
            insertAt = i
            Exit For
        Next

        Lines.Insert(insertAt, "version: 1")
    End Sub

    ''' <summary>
    ''' 往文件里插一个空的 <c>refs:</c> 段。
    ''' </summary>
    ''' <remarks>
    ''' 位置尽量贴近 dsh 自己的排版（<c>version</c> 之后、<c>records</c> 之前）。
    ''' 找不到 <c>version:</c> 就插到文件最前面。
    ''' </remarks>
    Private Sub InsertRefsBlock(Lines As List(Of String))
        Dim versionIdx As Integer = -1
        For i As Integer = 0 To Lines.Count - 1
            Dim l As String = Lines(i)
            If l.Length = 0 Then Continue For
            If l(0) = " "c OrElse l(0) = ChrW(9) Then Continue For
            If Regex.IsMatch(l, "^version\s*:") Then
                versionIdx = i
                Exit For
            End If
        Next

        If versionIdx >= 0 Then
            Lines.Insert(versionIdx + 1, "refs:")
        Else
            Lines.Insert(0, "refs:")
        End If
    End Sub

    ''' <summary>是否是块标量指示符。</summary>
    Private Function IsBlockScalarMarker(s As String) As Boolean
        For Each m As String In BlockScalarMarkers
            If String.Equals(s, m, StringComparison.Ordinal) Then Return True
        Next
        Return False
    End Function

    ''' <summary>
    ''' 把值格式化成一行或多行 YAML 标量。
    ''' </summary>
    ''' <param name="Value">原始值。</param>
    ''' <param name="ContinuationIndent">块标量正文的缩进。</param>
    ''' <returns>
    ''' 行列表。<b>第 0 项必须接在 <c>key:</c> 的同一行上</b>；
    ''' 后续项（仅块标量会有）作为独立的缩进行追加。
    ''' </returns>
    Private Function FormatScalar(Value As String, ContinuationIndent As String) As List(Of String)
        Dim result As New List(Of String)
        Dim v As String = If(Value, "")

        '含换行 → 用字面块标量，保证逐字节往返
        If v.Contains(vbLf) OrElse v.Contains(vbCr) Then
            result.Add("|-")
            Dim normalized As String = v.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf)
            For Each ln As String In normalized.Split(ChrW(10))
                result.Add(If(ln.Length = 0, "", ContinuationIndent & ln))
            Next
            '块标量末尾的空行会改变语义，去掉
            While result.Count > 1 AndAlso result(result.Count - 1).Length = 0
                result.RemoveAt(result.Count - 1)
            End While
            Return result
        End If

        If NeedsQuoting(v) Then
            result.Add(DoubleQuote(v))
        Else
            result.Add(v)
        End If
        Return result
    End Function

    ''' <summary>
    ''' 会被 YAML 解析成**非字符串**的裸标量。
    ''' </summary>
    ''' <remarks>
    ''' 不引起来的话，<c>1234567890</c> 会变成整数、<c>true</c> 会变成布尔、
    ''' <c>null</c> / <c>~</c> 会变成空值 —— 而 dsh 的凭据校验要求值必须是字符串
    ''' （<c>typeof v !== 'string'</c> 直接抛错）。
    ''' 对 API Key 来说几乎不会撞上，但模块是通用的，正确性不能打折。
    ''' </remarks>
    Private ReadOnly NonStringScalarPattern As New Regex(
        "^(?:" &
        "[+-]?(?:\d[\d_]*|\d[\d_]*\.\d*|\.\d[\d_]*)(?:[eE][+-]?\d+)?|" &   ' 整数 / 浮点 / 指数
        "0[xX][0-9a-fA-F_]+|0[oO][0-7_]+|0[bB][01_]+|" &                   ' 十六 / 八 / 二进制
        "[+-]?\.(?:inf|Inf|INF|nan|NaN|NAN)|" &                            ' 无穷 / 非数
        "null|Null|NULL|~|" &                                              ' 空值
        "true|True|TRUE|false|False|FALSE|" &                              ' 布尔
        "yes|Yes|YES|no|No|NO|on|On|ON|off|Off|OFF" &                      ' YAML 1.1 布尔（保守起见也引）
        ")$", RegexOptions.Compiled)

    ''' <summary>
    ''' 判断裸标量写法是否会出问题。
    ''' </summary>
    ''' <remarks>
    ''' 触发条件取自 YAML 对 plain scalar 的限制：
    '''   - 空串
    '''   - 首尾有空白（会被吃掉）
    '''   - 以指示符字符开头（<c>- ? : , [ ] { } # &amp; * ! | &gt; ' " % @ `</c>）
    '''   - 含 <c>": "</c> 或结尾是 <c>:</c>（会被当成映射）
    '''   - 含 <c>" #"</c>（会被当成注释）
    '''   - 含制表符
    '''   - 会被解析成数字 / 布尔 / 空值
    ''' </remarks>
    Private Function NeedsQuoting(v As String) As Boolean
        If v.Length = 0 Then Return True
        If Not String.Equals(v, v.Trim(), StringComparison.Ordinal) Then Return True

        Const Indicators As String = "-?:,[]{}#&*!|>'""%@`"
        If Indicators.IndexOf(v(0)) >= 0 Then Return True

        If v.Contains(": ") OrElse v.EndsWith(":", StringComparison.Ordinal) Then Return True
        If v.Contains(" #") Then Return True
        If v.Contains(ChrW(9)) Then Return True
        If NonStringScalarPattern.IsMatch(v) Then Return True

        Return False
    End Function

    ''' <summary>转成 YAML 双引号标量。</summary>
    Private Function DoubleQuote(v As String) As String
        Dim sb As New StringBuilder("""")
        For Each c As Char In v
            Select Case c
                Case """"c
                    sb.Append("\""")
                Case "\"c
                    sb.Append("\\")
                Case ChrW(10)
                    sb.Append("\n")
                Case ChrW(13)
                    sb.Append("\r")
                Case ChrW(9)
                    sb.Append("\t")
                Case Else
                    If AscW(c) < 32 Then
                        sb.Append("\x").Append(AscW(c).ToString("x2"))
                    Else
                        sb.Append(c)
                    End If
            End Select
        Next
        sb.Append("""")
        Return sb.ToString()
    End Function

    ''' <summary>把 YAML 标量解析回原始字符串（支持裸值 / 单引号 / 双引号）。</summary>
    Private Function ParseScalar(raw As String) As String
        Dim v As String = If(raw, "").Trim()
        If v.Length = 0 Then Return ""

        If v.Length >= 2 AndAlso v.StartsWith("""", StringComparison.Ordinal) AndAlso v.EndsWith("""", StringComparison.Ordinal) Then
            Return UnescapeDouble(v.Substring(1, v.Length - 2))
        End If
        If v.Length >= 2 AndAlso v.StartsWith("'", StringComparison.Ordinal) AndAlso v.EndsWith("'", StringComparison.Ordinal) Then
            Return v.Substring(1, v.Length - 2).Replace("''", "'")
        End If

        '裸值里若带注释，按 YAML 规则切掉 " #" 之后的部分
        Dim hashIdx As Integer = v.IndexOf(" #", StringComparison.Ordinal)
        If hashIdx >= 0 Then v = v.Substring(0, hashIdx).TrimEnd()

        Return v
    End Function

    ''' <summary>去掉键名可能带的外层引号。</summary>
    Private Function UnquoteScalar(v As String) As String
        Dim s As String = If(v, "").Trim()
        If s.Length >= 2 AndAlso s.StartsWith("""", StringComparison.Ordinal) AndAlso s.EndsWith("""", StringComparison.Ordinal) Then
            Return UnescapeDouble(s.Substring(1, s.Length - 2))
        End If
        If s.Length >= 2 AndAlso s.StartsWith("'", StringComparison.Ordinal) AndAlso s.EndsWith("'", StringComparison.Ordinal) Then
            Return s.Substring(1, s.Length - 2).Replace("''", "'")
        End If
        Return s
    End Function

    ''' <summary>还原双引号标量里的转义序列。</summary>
    Private Function UnescapeDouble(v As String) As String
        Dim sb As New StringBuilder(v.Length)
        Dim i As Integer = 0
        While i < v.Length
            Dim c As Char = v(i)
            If c <> "\"c OrElse i = v.Length - 1 Then
                sb.Append(c)
                i += 1
                Continue While
            End If

            Dim n As Char = v(i + 1)
            Select Case n
                Case "n"c
                    sb.Append(vbLf)
                    i += 2
                Case "r"c
                    sb.Append(vbCr)
                    i += 2
                Case "t"c
                    sb.Append(ChrW(9))
                    i += 2
                Case """"c
                    sb.Append("""")
                    i += 2
                Case "\"c
                    sb.Append("\")
                    i += 2
                Case "0"c
                    sb.Append(ChrW(0))
                    i += 2
                Case "x"c
                    If i + 3 < v.Length Then
                        Dim hex As String = v.Substring(i + 2, 2)
                        Dim code As Integer
                        If Integer.TryParse(hex, Globalization.NumberStyles.HexNumber, Globalization.CultureInfo.InvariantCulture, code) Then
                            sb.Append(ChrW(code))
                            i += 4
                        Else
                            sb.Append(c)
                            i += 1
                        End If
                    Else
                        sb.Append(c)
                        i += 1
                    End If
                Case Else
                    sb.Append(n)
                    i += 2
            End Select
        End While
        Return sb.ToString()
    End Function

    ''' <summary>
    ''' 原子写入：先写临时文件，再替换目标。
    ''' </summary>
    ''' <remarks>
    ''' 凭据文件被写坏是不可接受的（用户会莫名其妙用不了 DSH，
    ''' 而且里面还有 dsh 自己维护的会话记录）。
    ''' 先写 <c>.tmp</c> 再 <c>File.Replace</c>，保证任何时刻磁盘上的文件都是完整的。
    ''' </remarks>
    Private Sub WriteAllLinesAtomic(Path As String, Lines As List(Of String))
        Dim dir As String = IO.Path.GetDirectoryName(Path)
        If Not String.IsNullOrWhiteSpace(dir) AndAlso Not Directory.Exists(dir) Then
            Directory.CreateDirectory(dir)
        End If

        Dim tmp As String = DshMigrate.DshAtomicTempPath(Path)
        Dim utf8NoBom As New UTF8Encoding(False)

        '统一换行符，并保证文件以换行结尾
        Dim text As String = String.Join(vbCrLf, Lines) & vbCrLf
        File.WriteAllText(tmp, text, utf8NoBom)

        If File.Exists(Path) Then
            'File.Replace 需要目标存在；保留一份 .bak 由系统处理，这里直接替换
            File.Replace(tmp, Path, Nothing)
        Else
            File.Move(tmp, Path)
        End If
    End Sub

#End Region

End Module
