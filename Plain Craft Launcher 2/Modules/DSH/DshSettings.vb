Imports System.IO
Imports System.Text

''' <summary>
''' dsh 用户设置文档（<c>$DSH_HOME/settings.yaml</c>）的命名空间级读写。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 这个文件为什么重要
''' ═══════════════════════════════════════════════════════════════════════
''' 它是**官方 web 端「Models 页」写的东西**，也是覆盖 <c>llm-deepseek</c>
''' 适配器配置的**唯一热生效通道**：
'''
'''   - 结构是「命名空间 → 用户分节」的映射，命名空间就是插件名
'''     （<c>llm-deepseek</c> / <c>llm-pi-ai</c> / <c>ui-onboarding</c> …）
'''   - 分节内容会**覆盖**该插件的 entry config
'''     （源码：<c>register(ns, schema, { base: entry })</c>，用户分节叠在 base 之上）
'''   - <c>watch: true</c> + 100ms debounce → **改完立刻生效，不用重启**
'''
''' 相比之下 patch 文件（<c>cordis.patch.yml</c>）是"按 id 替换整个 config"，
''' 粒度更粗、语义更绕。所以模型配置一律走这里。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 为什么做「命名空间块级补丁」而不是整体重写 YAML
''' ═══════════════════════════════════════════════════════════════════════
''' 文档里还有别的命名空间（<c>ui-onboarding</c> 等）以及用户的注释与排版。
''' 整体重新序列化会把它们全部打乱甚至丢掉 —— 而官方文档明确说
''' 「用户可以直接编辑文档」「写入会保留注释与排版」。
''' 所以我们只替换属于自己的那一个顶层块，其余字节原样保留。
'''
''' ⚠️ 这与 <see cref="DshCredentials"/> 的 refs 段补丁是同一套思路，
''' 但**只支持标量字段**：我们目前要写的就 protocol / apiKeyEnv / baseURL 三个。
''' 将来若要写数组（例如 <c>models</c>），这里需要扩展。
''' </summary>
Public Module DshSettings

#Region "常量"

    ''' <summary>DeepSeek 适配器的命名空间（源码里 <c>const NS = 'llm-deepseek'</c>）。</summary>
    Public Const NsDeepSeek As String = "llm-deepseek"

#End Region

#Region "路径"

    ''' <summary>某个实例的设置文档路径。</summary>
    Public Function SettingsFileFor(Instance As DshInstance) As String
        If Instance Is Nothing Then Return Nothing
        Return Instance.HomeDir & "settings.yaml"
    End Function

#End Region

#Region "读取"

    ''' <summary>
    ''' 读取某个命名空间的标量字段。
    ''' </summary>
    ''' <returns>键 → 值；文件或分节不存在时返回空字典。</returns>
    Public Function ReadSection(Instance As DshInstance, Ns As String) As Dictionary(Of String, String)
        Dim result As New Dictionary(Of String, String)(StringComparer.Ordinal)
        If Instance Is Nothing OrElse String.IsNullOrWhiteSpace(Ns) Then Return result

        Dim path As String = SettingsFileFor(Instance)
        If Not DshRuntime.FileExistsSafe(path) Then Return result

        Try
            Dim lines As String() = File.ReadAllLines(path, Encoding.UTF8)
            Dim inSection As Boolean = False

            For Each raw In lines
                Dim line As String = raw
                '顶层键：无缩进、非空、非注释
                Dim isTopLevel As Boolean =
                    line.Length > 0 AndAlso line(0) <> " "c AndAlso line(0) <> ChrW(9) AndAlso
                    Not line.TrimStart().StartsWith("#", StringComparison.Ordinal)

                If isTopLevel Then
                    '遇到下一个顶层键就说明本分节结束
                    If inSection Then Exit For
                    If RegexIsNamespace(line, Ns) Then inSection = True
                    Continue For
                End If

                If Not inSection Then Continue For
                Dim trimmed As String = line.Trim()
                If trimmed.Length = 0 OrElse trimmed.StartsWith("#", StringComparison.Ordinal) Then Continue For

                Dim colon As Integer = trimmed.IndexOf(":"c)
                If colon <= 0 Then Continue For
                Dim key As String = trimmed.Substring(0, colon).Trim()
                Dim value As String = trimmed.Substring(colon + 1).Trim()
                result(key) = ParseScalar(value)
            Next
        Catch ex As Exception
            Logger.Error(ex, $"DSH：读取 settings.yaml 失败：{path}")
        End Try

        Return result
    End Function

    ''' <summary>某个命名空间在文档里是否存在。</summary>
    Public Function HasSection(Instance As DshInstance, Ns As String) As Boolean
        If Instance Is Nothing Then Return False
        Dim path As String = SettingsFileFor(Instance)
        If Not DshRuntime.FileExistsSafe(path) Then Return False
        Try
            Return File.ReadAllLines(path, Encoding.UTF8).Any(
                Function(l) l.Length > 0 AndAlso l(0) <> " "c AndAlso l(0) <> ChrW(9) AndAlso
                              RegexIsNamespace(l, Ns))
        Catch ex As Exception
            Return False
        End Try
    End Function

    ''' <summary>这一行是不是目标命名空间的顶层键行。</summary>
    Private Function RegexIsNamespace(Line As String, Ns As String) As Boolean
        Return System.Text.RegularExpressions.Regex.IsMatch(
            Line, "^" & System.Text.RegularExpressions.Regex.Escape(Ns) & "\s*:")
    End Function

#End Region

#Region "写入"

    ''' <summary>
    ''' 写入（或替换）某个命名空间的分节。
    ''' </summary>
    ''' <param name="Instance">目标实例。</param>
    ''' <param name="Ns">命名空间。</param>
    ''' <param name="Fields">键 → 值。值必须是标量。</param>
    ''' <param name="FailureReason">失败原因（给用户看）。</param>
    ''' <returns>成功返回 True。</returns>
    ''' <remarks>
    ''' 只替换属于这个命名空间的顶层块，文档里的其它命名空间、注释、排版全部原样保留。
    ''' 原子写入（临时文件 + File.Replace），避免 dsh 的 watcher 读到半截文件。
    ''' </remarks>
    Public Function WriteSection(Instance As DshInstance,
                                 Ns As String,
                                 Fields As IList(Of KeyValuePair(Of String, String)),
                                 ByRef FailureReason As String) As Boolean
        FailureReason = Nothing

        If Instance Is Nothing Then
            FailureReason = "实例不存在。"
            Return False
        End If
        If String.IsNullOrWhiteSpace(Ns) Then
            FailureReason = "命名空间不能为空。"
            Return False
        End If
        If Fields Is Nothing OrElse Fields.Count = 0 Then
            Return RemoveSection(Instance, Ns)
        End If

        Dim lines As New List(Of String)
        For Each kv In Fields
            If String.IsNullOrWhiteSpace(kv.Key) Then Continue For
            lines.Add($"  {kv.Key}: {FormatScalar(kv.Value)}")
        Next

        Return WriteSectionRaw(Instance, Ns, lines, FailureReason)
    End Function

    ''' <summary>
    ''' 写入某个命名空间的分节，字段内容由调用方直接给出 YAML 行。
    ''' </summary>
    ''' <param name="Instance">目标实例。</param>
    ''' <param name="Ns">命名空间。</param>
    ''' <param name="BodyLines">
    ''' 分节正文，**每行都要自带缩进**（约定用两个空格）。
    ''' 需要嵌套结构（例如 <c>models</c> 这种列表）时走这个重载。
    ''' </param>
    ''' <param name="FailureReason">失败原因（给用户看）。</param>
    ''' <remarks>
    ''' 只替换属于这个命名空间的顶层块，文档里的其它命名空间、注释、排版全部原样保留。
    ''' 原子写入（临时文件 + File.Replace），避免 dsh 的 watcher 读到半截文件。
    ''' </remarks>
    Public Function WriteSectionRaw(Instance As DshInstance,
                                    Ns As String,
                                    BodyLines As IList(Of String),
                                    ByRef FailureReason As String) As Boolean
        FailureReason = Nothing

        If Instance Is Nothing Then
            FailureReason = "实例不存在。"
            Return False
        End If
        If String.IsNullOrWhiteSpace(Ns) Then
            FailureReason = "命名空间不能为空。"
            Return False
        End If
        If BodyLines Is Nothing OrElse BodyLines.Count = 0 Then
            Return RemoveSection(Instance, Ns)
        End If

        Try
            ModDSH.DshEnsureInstanceDirectory(Instance)
            Dim path As String = SettingsFileFor(Instance)

            Dim lines As New List(Of String)
            If DshRuntime.FileExistsSafe(path) Then lines.AddRange(File.ReadAllLines(path, Encoding.UTF8))

            '── 1. 摘掉旧的同名分节（含它的所有缩进行）──
            Dim cleaned As New List(Of String)
            Dim skipping As Boolean = False
            For Each raw In lines
                Dim isTopLevel As Boolean =
                    raw.Length > 0 AndAlso raw(0) <> " "c AndAlso raw(0) <> ChrW(9) AndAlso
                    Not raw.TrimStart().StartsWith("#", StringComparison.Ordinal)

                If isTopLevel Then
                    skipping = RegexIsNamespace(raw, Ns)
                    If skipping Then Continue For
                ElseIf skipping Then
                    '分节内的缩进行（含空行）一并丢掉
                    Continue For
                End If

                cleaned.Add(raw)
            Next

            '── 2. 去掉尾部空行，保证追加后排版整齐 ──
            While cleaned.Count > 0 AndAlso cleaned(cleaned.Count - 1).Trim().Length = 0
                cleaned.RemoveAt(cleaned.Count - 1)
            End While

            '── 3. 追加新分节 ──
            If cleaned.Count > 0 Then cleaned.Add("")
            cleaned.Add(Ns & ":")
            For Each bodyLine In BodyLines
                cleaned.Add(bodyLine)
            Next

            WriteAllLinesAtomic(path, cleaned)
            Logger.Info($"DSH[{Instance.DisplayName}]：已写入 settings.yaml 的 {Ns} 分节（{BodyLines.Count} 行）")
            Return True
        Catch ex As Exception
            FailureReason = $"写入设置文档失败：{ex.Message}"
            Logger.Error(ex, $"DSH：写入 settings.yaml 失败：{Ns}")
            Return False
        End Try
    End Function

    ''' <summary>删除某个命名空间的分节（其余内容原样保留）。</summary>
    Public Function RemoveSection(Instance As DshInstance, Ns As String) As Boolean
        If Instance Is Nothing Then Return False

        Try
            Dim path As String = SettingsFileFor(Instance)
            If Not DshRuntime.FileExistsSafe(path) Then Return True

            Dim lines As String() = File.ReadAllLines(path, Encoding.UTF8)
            Dim cleaned As New List(Of String)
            Dim skipping As Boolean = False
            Dim removed As Integer = 0

            For Each raw In lines
                Dim isTopLevel As Boolean =
                    raw.Length > 0 AndAlso raw(0) <> " "c AndAlso raw(0) <> ChrW(9) AndAlso
                    Not raw.TrimStart().StartsWith("#", StringComparison.Ordinal)

                If isTopLevel Then
                    skipping = RegexIsNamespace(raw, Ns)
                    If skipping Then
                        removed += 1
                        Continue For
                    End If
                ElseIf skipping Then
                    removed += 1
                    Continue For
                End If

                cleaned.Add(raw)
            Next

            If removed = 0 Then Return True

            While cleaned.Count > 0 AndAlso cleaned(cleaned.Count - 1).Trim().Length = 0
                cleaned.RemoveAt(cleaned.Count - 1)
            End While

            WriteAllLinesAtomic(path, cleaned)
            Logger.Info($"DSH[{Instance.DisplayName}]：已移除 settings.yaml 的 {Ns} 分节")
            Return True
        Catch ex As Exception
            Logger.Error(ex, $"DSH：移除 settings.yaml 分节失败：{Ns}")
            Return False
        End Try
    End Function

#End Region

#Region "YAML 标量（受限版本）"

    ''' <summary>
    ''' 把一个值格式化成 YAML 标量。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 这是**受限版本**：只处理「短、单行、无特殊字符」的标量，
    ''' 因为本模块目前只写 <c>protocol</c> / <c>apiKeyEnv</c> / <c>baseURL</c> 三个字段，
    ''' 它们的取值范围是枚举名、标识符和 URL。
    '''
    ''' 需要处理任意用户字符串（含换行、引号、块标量）的那套完整逻辑在
    ''' <see cref="DshCredentials"/> 里 —— 凭据文件必须能无损承载任何字符，
    ''' 而设置文档不需要。刻意不共用：共用会把"受限"这个前提藏起来，
    ''' 将来有人在设置文档里塞长文本就会踩坑。
    ''' </remarks>
    Private Function FormatScalar(Value As String) As String
        Dim v As String = If(Value, "")

        If v.Length = 0 Then Return """"""""
        If IsPlainSafe(v) Then Return v

        '需要引号：用双引号并转义反斜杠与双引号
        Return """" & v.Replace("\", "\\").Replace("""", "\""") & """"
    End Function

    ''' <summary>
    ''' 值能否裸写（不加引号）。
    ''' </summary>
    ''' <remarks>
    ''' 允许的字符集刻意收得很窄：字母、数字与 <c>_ . / : @ ? &amp; = + ~ % # -</c>。
    ''' 覆盖了 URL、包名、环境变量名；不含空格与引号，也就不会触发 YAML 的任何特殊规则。
    ''' 首字符额外排除 <c>- ? : # &amp; * ! | &gt; ' " % @ `</c> 这些指示符。
    ''' </remarks>
    Private Function IsPlainSafe(v As String) As Boolean
        If v.Length = 0 Then Return False
        Const Allowed As String = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.:/@?&=+~%#-"
        For i As Integer = 0 To v.Length - 1
            If Allowed.IndexOf(v(i)) < 0 Then Return False
        Next
        '首字符不能是指示符（- ? : # 等），否则 YAML 会当成语法
        Const LeadingBad As String = "-?:#&*!|>'""%@`"
        If LeadingBad.IndexOf(v(0)) >= 0 Then Return False
        Return True
    End Function

    ''' <summary>把 YAML 标量解析回字符串（支持裸值 / 单引号 / 双引号）。</summary>
    Private Function ParseScalar(raw As String) As String
        Dim v As String = If(raw, "").Trim()
        If v.Length = 0 Then Return ""

        If v.Length >= 2 AndAlso v.StartsWith("""", StringComparison.Ordinal) AndAlso v.EndsWith("""", StringComparison.Ordinal) Then
            Dim inner As String = v.Substring(1, v.Length - 2)
            Return inner.Replace("\""", """").Replace("\\", "\")
        End If
        If v.Length >= 2 AndAlso v.StartsWith("'", StringComparison.Ordinal) AndAlso v.EndsWith("'", StringComparison.Ordinal) Then
            Return v.Substring(1, v.Length - 2).Replace("''", "'")
        End If

        '裸值里若带注释，按 YAML 规则切掉 " #" 之后的部分
        Dim hashIdx As Integer = v.IndexOf(" #", StringComparison.Ordinal)
        If hashIdx >= 0 Then v = v.Substring(0, hashIdx).TrimEnd()

        Return v
    End Function

#End Region

#Region "原子写入"

    ''' <summary>
    ''' 原子写入：先写临时文件，再替换目标。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 必须原子 —— dsh 的 settings 提供方在 <c>watch</c> 下盯着这个文件，
    ''' 直接覆写有可能让它读到写了一半的内容（半截 YAML 会被判为非法文档）。
    ''' </remarks>
    Private Sub WriteAllLinesAtomic(Path As String, Lines As List(Of String))
        Dim dir As String = IO.Path.GetDirectoryName(Path)
        If Not String.IsNullOrWhiteSpace(dir) AndAlso Not Directory.Exists(dir) Then
            Directory.CreateDirectory(dir)
        End If

        Dim tmp As String = Path & ".pcltmp"
        'YAML 用 UTF8 无 BOM；带 BOM 时某些解析器会把第一行的键名带上不可见字符
        File.WriteAllText(tmp, String.Join(vbCrLf, Lines) & vbCrLf, New UTF8Encoding(False))

        If File.Exists(Path) Then
            File.Replace(tmp, Path, Nothing)
        Else
            File.Move(tmp, Path)
        End If
    End Sub

#End Region

End Module
