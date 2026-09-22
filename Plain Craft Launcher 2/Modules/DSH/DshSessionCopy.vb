''' <summary>
''' PCL_DSH：把某个实例的对话历史复制到其他实例。
''' </summary>
''' <remarks>
''' ═══════════════════════════════════════════════════════════════════════
''' 为什么需要它
''' ═══════════════════════════════════════════════════════════════════════
''' 每个实例的 <c>DSH_HOME</c> 是独立的（<c>instances\inst_&lt;id&gt;\</c>），
''' 所以对话历史**天然不互通** —— 这是多实例的设计目的。
''' 但用户常常会希望「把散在各个实例里的对话集中到一处」，
''' 这一层就是干这个的。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 语义：**合并 + 只补缺**（目标已有的会话一个都不碰）
''' ═══════════════════════════════════════════════════════════════════════
''' 例：实例1 有 {1,2,3,4}，实例2 有 {4,5,6,7}，用实例1 复制到实例2 →
''' <b>结果 {1,2,3,4,5,6,7}</b>：只补进 1/2/3，<b>实例2 原有的 4/5/6/7 完全不动</b>。
'''
''' 为什么选「只补缺」而不是「较新的覆盖」：
''' <list type="bullet">
''' <item>聊天记录是**累积型数据**，丢一条就是永久的</item>
''' <item>只补缺 = **完全不修改目标实例已有的任何东西** → 天然安全，
'''       不需要"先备份再回滚"那套，出不了事</item>
''' <item>冲突场景本来就罕见：实例之间是隔离的，同一个 session-id
'''       同时出现在两边只可能是之前复制过</item>
''' </list>
''' 代价：如果同一个会话在两边都继续聊过，只能保留目标那份。
''' 需要"取较新的"时可以再加开关（见 <c>DshSessionCopyToInstances</c> 的 PreferNewer 参数）。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' ⚠️ 索引与正文必须**成对**搬
''' ═══════════════════════════════════════════════════════════════════════
''' dsh 把一次会话拆成两处：
''' <list type="bullet">
''' <item><b>索引</b> <c>storages\session_projcache\sessions\&lt;session-id&gt;.json</c>
'''       —— 没有它，会话不会出现在列表里</item>
''' <item><b>正文</b> <c>sessions\&lt;工作区编码&gt;\&lt;session-id&gt;\session.v3.jsonl.zstd</c>
'''       —— 没有它，点进去是空的</item>
''' </list>
''' 只搬一边就是「列表有、打不开」（全量导入曾经踩过这个坑）。
''' 这里两者一起判断、一起搬。
'''
''' 工作区目录**不用管** —— 它是索引里 <c>record.identity.cwd</c> 的路径编码
''' （<c>D:\harness</c> → <c>--D-harness--</c>，中文会被编码成 <c>~xxxx~</c>），
''' 照搬源目录名即可。
''' </remarks>
Public Module DshSessionCopy

#Region "常量"

    ''' <summary>反斜杠字符（VB 里不能写字面量，见项目备忘）。</summary>
    Private ReadOnly SessBS As Char = ChrW(92)

    ''' <summary>会话索引目录（相对实例数据根）。</summary>
    Private Const SessIndexRel As String = "storages\session_projcache\sessions"

    ''' <summary>会话正文根目录（相对实例数据根）。</summary>
    Private Const SessBodyRel As String = "sessions"

#End Region

#Region "数据模型"

    ''' <summary>一次对话历史复制的统计。</summary>
    Public Class DshSessionCopyResult
        Public Property Success As Boolean = False
        Public Property Message As String = ""
        ''' <summary>源实例的会话总数。</summary>
        Public Property SourceCount As Integer = 0
        ''' <summary>新补进去的会话数（索引 + 正文都成功才算）。</summary>
        Public Property AddedCount As Integer = 0
        ''' <summary>因为目标已有而跳过的数量。</summary>
        Public Property SkippedCount As Integer = 0
        ''' <summary>失败明细。</summary>
        Public Property Failures As New List(Of String)()
        ''' <summary>处理过的目标实例数。</summary>
        Public Property TargetCount As Integer = 0
    End Class

    ''' <summary>源实例里的一个会话。</summary>
    Private Class SessEntry
        Public Property Id As String = ""
        ''' <summary>索引文件绝对路径。</summary>
        Public Property IndexPath As String = ""
        ''' <summary>正文目录绝对路径；没有正文时为 Nothing。</summary>
        Public Property BodyDir As String = ""
        ''' <summary>正文所在的工作区目录名（<c>--D-harness--</c> 这种）。</summary>
        Public Property Workspace As String = ""
    End Class

#End Region

#Region "路径"

    Private Function SessIndexDir(Instance As DshInstance) As String
        If Instance Is Nothing Then Return Nothing
        Return Path.Combine(Instance.HomeDir, SessIndexRel)
    End Function

    Private Function SessBodyRoot(Instance As DshInstance) As String
        If Instance Is Nothing Then Return Nothing
        Return Path.Combine(Instance.HomeDir, SessBodyRel)
    End Function

#End Region

#Region "扫描源实例"

    ''' <summary>
    ''' 扫出源实例里的全部会话（索引 + 正文成对收集）。
    ''' </summary>
    ''' <remarks>
    ''' 以**索引**为准来枚举会话（索引才是"这个会话存在"的标志），
    ''' 然后去 <c>sessions\</c> 下按 session-id 找对应的正文目录。
    ''' 找不到正文的索引也会被带上 —— 它可能本来就是空会话，
    ''' 照搬过去至少不会比源环境更差。
    ''' </remarks>
    Private Function ScanSource(Source As DshInstance) As List(Of SessEntry)
        Dim result As New List(Of SessEntry)()
        Try
            Dim idxDir As String = SessIndexDir(Source)
            If Not Directory.Exists(idxDir) Then Return result

            ' 先把正文目录按 session-id 建索引，避免每个会话都去遍历一遍
            Dim bodyMap As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            Dim bodyRoot As String = SessBodyRoot(Source)
            If Directory.Exists(bodyRoot) Then
                For Each wsDir As String In Directory.GetDirectories(bodyRoot)
                    Dim wsName As String = Path.GetFileName(wsDir)
                    For Each sessDir As String In Directory.GetDirectories(wsDir)
                        Dim sid As String = Path.GetFileName(sessDir)
                        ' 同一个 id 出现多次时保留第一个（正常情况不会重复）
                        If Not bodyMap.ContainsKey(sid) Then
                            bodyMap(sid) = wsName & SessBS & sid
                        End If
                    Next
                Next
            End If

            For Each idxFile As String In Directory.GetFiles(idxDir, "*.json")
                Dim sid As String = Path.GetFileNameWithoutExtension(idxFile)
                If String.IsNullOrWhiteSpace(sid) Then Continue For
                Dim entry As New SessEntry With {
                    .Id = sid,
                    .IndexPath = idxFile
                }
                Dim rel As String = Nothing
                If bodyMap.TryGetValue(sid, rel) Then
                    entry.Workspace = rel.Split(SessBS)(0)
                    entry.BodyDir = Path.Combine(bodyRoot, rel)
                End If
                result.Add(entry)
            Next
        Catch ex As Exception
            Logger.Error(ex, $"DSH：扫描 {Source.DisplayName} 的会话失败")
        End Try
        Return result
    End Function

#End Region

#Region "判断目标是否已有"

    ''' <summary>目标实例是否已经有这个会话（索引或正文任一存在就算有）。</summary>
    Private Function TargetHasSession(Target As DshInstance, Entry As SessEntry) As Boolean
        Try
            Dim idx As String = Path.Combine(SessIndexDir(Target), Entry.Id & ".json")
            If File.Exists(idx) Then Return True
            If Not String.IsNullOrWhiteSpace(Entry.BodyDir) Then
                Dim body As String = Path.Combine(SessBodyRoot(Target), Entry.Workspace, Entry.Id)
                If Directory.Exists(body) Then Return True
            End If
            Return False
        Catch ex As Exception
            ' 判断不了就当成"已有" —— 宁可少搬，也不要在不确定时动目标
            Logger.Warn(ex, $"DSH：判断目标是否已有会话 {Entry.Id} 失败（按已有处理）")
            Return True
        End Try
    End Function

#End Region

#Region "执行复制"

    ''' <summary>
    ''' 把 <paramref name="Source"/> 的对话历史复制到 <paramref name="Targets"/>。
    ''' </summary>
    ''' <param name="Source">源实例。</param>
    ''' <param name="Targets">目标实例列表（可以多个）。</param>
    ''' <param name="Progress">进度回调（阶段, 说明, 0-1）。</param>
    Public Function DshSessionCopyToInstances(Source As DshInstance,
                                              Targets As List(Of DshInstance),
                                              Optional Progress As DshMigrate.DshMigrateProgressHandler = Nothing) As DshSessionCopyResult
        Dim res As New DshSessionCopyResult()
        Try
            If Source Is Nothing Then
                res.Message = "没有指定源实例。"
                Return res
            End If
            If Targets Is Nothing OrElse Targets.Count = 0 Then
                res.Message = "没有指定目标实例。"
                Return res
            End If

            Report(Progress, "扫描", $"正在读取「{Source.DisplayName}」的会话……", -1)
            Dim entries As List(Of SessEntry) = ScanSource(Source)
            res.SourceCount = entries.Count
            If entries.Count = 0 Then
                res.Success = True
                res.Message = $"实例「{Source.DisplayName}」里没有对话历史，无需复制。"
                Return res
            End If
            Logger.Info($"DSH：{Source.DisplayName} 有 {entries.Count} 个会话，准备复制到 {Targets.Count} 个实例")

            Dim doneTotal As Integer = 0
            Dim totalWork As Integer = Math.Max(1, entries.Count * Targets.Count)

            For Each target As DshInstance In Targets
                If target Is Nothing Then Continue For
                ' 源和目标同一个实例就跳过（否则"自己复制给自己"没有意义）
                If String.Equals(target.Id, Source.Id, StringComparison.OrdinalIgnoreCase) Then Continue For

                res.TargetCount += 1
                Try
                    Dim idxDir As String = SessIndexDir(target)
                    If Not Directory.Exists(idxDir) Then Directory.CreateDirectory(idxDir)

                    For Each entry As SessEntry In entries
                        doneTotal += 1
                        Report(Progress, "复制",
                               $"→ {target.DisplayName}：{doneTotal}/{totalWork}", doneTotal / totalWork)

                        If TargetHasSession(target, entry) Then
                            res.SkippedCount += 1
                            Continue For
                        End If

                        Try
                            ' ① 索引
                            Dim dstIdx As String = Path.Combine(idxDir, entry.Id & ".json")
                            File.Copy(entry.IndexPath, dstIdx, True)

                            ' ② 正文（可能没有 —— 空会话）
                            If Not String.IsNullOrWhiteSpace(entry.BodyDir) AndAlso Directory.Exists(entry.BodyDir) Then
                                Dim dstBody As String = Path.Combine(SessBodyRoot(target), entry.Workspace, entry.Id)
                                If Not Directory.Exists(dstBody) Then Directory.CreateDirectory(dstBody)
                                ' 复用迁移那套「保留符号链接 + 长路径」的复制
                                DshMigrate.DshCopyTreePreservingLinks(entry.BodyDir, dstBody)
                            End If

                            res.AddedCount += 1
                        Catch ex As Exception
                            Logger.Error(ex, $"DSH：复制会话 {entry.Id} → {target.DisplayName} 失败")
                            res.Failures.Add($"{target.DisplayName} / {entry.Id}：{ex.Message}")
                        End Try
                    Next
                Catch ex As Exception
                    Logger.Error(ex, $"DSH：向 {target.DisplayName} 复制会话失败")
                    res.Failures.Add($"{target.DisplayName}：{ex.Message}")
                End Try
            Next

            res.Success = res.Failures.Count = 0
            res.Message = $"已把「{Source.DisplayName}」的对话历史复制到 {res.TargetCount} 个实例：" & vbCrLf &
                          $"· 新补入 {res.AddedCount} 个会话" & vbCrLf &
                          $"· 跳过 {res.SkippedCount} 个（目标已有，未做任何改动）" & vbCrLf &
                          $"（源实例共有 {res.SourceCount} 个会话）"
            If res.Failures.Count > 0 Then
                res.Message &= vbCrLf & vbCrLf & "以下项目未能复制：" & vbCrLf & String.Join(vbCrLf, res.Failures)
            End If
            Logger.Info($"DSH：对话历史复制完成 —— 补入 {res.AddedCount}，跳过 {res.SkippedCount}，失败 {res.Failures.Count}")
        Catch ex As Exception
            Logger.Error(ex, "DSH：复制对话历史失败")
            res.Success = False
            res.Message = "复制失败：" & ex.Message
        End Try
        Return res
    End Function

    ''' <summary>数一下某个实例有多少个会话（索引文件数）。</summary>
    Public Function DshSessionCount(Instance As DshInstance) As Integer
        Try
            Dim idxDir As String = SessIndexDir(Instance)
            If Not Directory.Exists(idxDir) Then Return 0
            Return Directory.GetFiles(idxDir, "*.json").Length
        Catch ex As Exception
            Logger.Warn(ex, "DSH：统计会话数失败")
            Return 0
        End Try
    End Function

#End Region

#Region "进度"

    Private Sub Report(Handler As DshMigrate.DshMigrateProgressHandler,
                       Stage As String, Message As String, Progress As Double)
        If Handler Is Nothing Then Return
        Try
            Handler(Stage, Message, Progress)
        Catch ex As Exception
            Logger.Warn(ex, "DSH：对话复制进度回调出错（已忽略）")
        End Try
    End Sub

#End Region

End Module
