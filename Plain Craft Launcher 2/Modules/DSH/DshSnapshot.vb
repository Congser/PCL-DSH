''' <summary>
''' PCL_DSH：实例快照。
''' </summary>
''' <remarks>
''' ═══════════════════════════════════════════════════════════════════════
''' 为什么需要它
''' ═══════════════════════════════════════════════════════════════════════
''' dsh 实例的数据散在十几个目录里（插件、密钥、会话、审批规则、皮肤……），
''' 一旦哪次升级 / 装插件把环境搞坏了，用户没有任何"回到上一个好状态"的手段 ——
''' 只能一个个手动排查。快照就是给这条退路。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 两档，差别只在**要不要把依赖也装进去**
''' ═══════════════════════════════════════════════════════════════════════
''' <list type="bullet">
''' <item><b>快速快照</b>（默认）：跳过 <c>node_modules</c>。
'''       实例数据里 400+ MB 都是依赖，而它们**可以被重装** ——
'''       快照只记 <c>package.json</c> / <c>pnpm-lock.yaml</c>，
'''       恢复时按清单重装即可。结果：几 MB、秒级完成。</item>
''' <item><b>完整快照</b>：连 <c>node_modules</c> 一起。
'''       好处是完全离线可恢复；代价是几百 MB、几十秒。
'''       ⚠️ 它依赖 <see cref="DshMigrate.DshCopyTreePreservingLinks"/> ——
'''       直接按文件树复制会解引用 pnpm 的 2474 个链接，把依赖树搞坏。</item>
''' </list>
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 恢复的语义：**按条目覆盖**，不是整目录替换
''' ═══════════════════════════════════════════════════════════════════════
''' 只把快照里**有**的顶层条目覆盖回实例，不去动快照里没有的东西。
''' 这样「快速快照」恢复后 <c>node_modules</c> 仍然在原地，
''' 不会因为"快照里没有它"而被删掉。
''' </remarks>
Public Module DshSnapshot

#Region "常量"

    ''' <summary>反斜杠字符（VB 里不能写字面量，见项目备忘）。</summary>
    Private ReadOnly SnapshotBS As Char = ChrW(92)

    ''' <summary>快照根目录名（放在数据根下，与 instances / runtime 平级）。</summary>
    Private Const SnapshotFolderName As String = "snapshots"

    ''' <summary>清单文件名。</summary>
    Private Const ManifestName As String = "manifest.json"

    ''' <summary>快照数据的存放目录名（在快照目录里）。</summary>
    Private Const PayloadFolderName As String = "data"

    ''' <summary>档位标识。</summary>
    Public Const TierQuick As String = "quick"
    Public Const TierFull As String = "full"

    ''' <summary>
    ''' 「快速快照」要跳过的条目名。
    ''' </summary>
    ''' <remarks>
    ''' <c>node_modules</c> 是唯一值得跳过的 —— 它占了实例数据的绝大部分体积，
    ''' 而且完全可以从 <c>package.json</c> + <c>pnpm-lock.yaml</c> 重装出来。
    ''' <c>.pnpm</c> 本来就在 <c>node_modules</c> 里，写在这里是双保险。
    ''' </remarks>
    Private ReadOnly SnapshotSkipNames As String() = {"node_modules", ".pnpm"}

#End Region

#Region "数据模型"

    ''' <summary>一个快照的描述。</summary>
    Public Class DshSnapshotInfo
        Public Property Id As String = ""
        Public Property Name As String = ""
        Public Property CreatedAt As DateTime = DateTime.MinValue
        ''' <summary>档位：<see cref="TierQuick"/> 或 <see cref="TierFull"/>。</summary>
        Public Property Tier As String = TierQuick
        Public Property Note As String = ""
        Public Property FileCount As Integer = 0
        Public Property TotalBytes As Long = 0
        ''' <summary>快照目录（不写进清单，运行时推导）。</summary>
        Public Property Dir As String = ""

        ''' <summary>给界面用的一行描述。</summary>
        Public ReadOnly Property Describe As String
            Get
                Dim tierText As String = If(String.Equals(Tier, TierFull, StringComparison.OrdinalIgnoreCase),
                                            "完整", "快速")
                Return $"{CreatedAt:yyyy-MM-dd HH:mm}  ·  {tierText}  ·  {DshDoctor.FormatBytes(TotalBytes)}"
            End Get
        End Property
    End Class

    ''' <summary>快照操作结果。</summary>
    Public Class DshSnapshotResult
        Public Property Success As Boolean = False
        Public Property Message As String = ""
        Public Property Snapshot As DshSnapshotInfo
    End Class

#End Region

#Region "路径"

    ''' <summary>快照根目录（<c>&lt;数据根&gt;\snapshots\</c>）。</summary>
    Public Function DshSnapshotRoot() As String
        Return ModDSH.DshRoot & SnapshotFolderName & SnapshotBS
    End Function

    ''' <summary>某个实例的快照目录。</summary>
    Public Function DshSnapshotInstanceDir(Instance As DshInstance) As String
        If Instance Is Nothing Then Return Nothing
        Return DshSnapshotRoot() & "inst_" & Instance.Id & SnapshotBS
    End Function

    ''' <summary>确保目录存在。</summary>
    Private Function EnsureDir(TargetDir As String) As Boolean
        Try
            If String.IsNullOrWhiteSpace(TargetDir) Then Return False
            If Not Directory.Exists(TargetDir) Then Directory.CreateDirectory(TargetDir)
            Return True
        Catch ex As Exception
            Logger.Error(ex, $"DSH：创建快照目录失败：{TargetDir}")
            Return False
        End Try
    End Function

#End Region

#Region "读取清单"

    ''' <summary>列出某个实例的所有快照（新的在前）。</summary>
    Public Function DshSnapshotList(Instance As DshInstance) As List(Of DshSnapshotInfo)
        Dim result As New List(Of DshSnapshotInfo)
        Try
            Dim rootDir As String = DshSnapshotInstanceDir(Instance)
            If String.IsNullOrWhiteSpace(rootDir) OrElse Not Directory.Exists(rootDir) Then Return result

            For Each subDir As String In Directory.GetDirectories(rootDir)
                Dim info As DshSnapshotInfo = ReadManifest(subDir)
                If info IsNot Nothing Then result.Add(info)
            Next
            result.Sort(Function(a, b) b.CreatedAt.CompareTo(a.CreatedAt))
        Catch ex As Exception
            Logger.Error(ex, "DSH：列出快照失败")
        End Try
        Return result
    End Function

    ''' <summary>读一个快照目录的清单；没有或损坏返回 Nothing。</summary>
    Private Function ReadManifest(SnapshotDir As String) As DshSnapshotInfo
        Try
            Dim manifestPath As String = Path.Combine(SnapshotDir, ManifestName)
            If Not File.Exists(manifestPath) Then Return Nothing
            Dim o As Newtonsoft.Json.Linq.JObject =
                Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(manifestPath, Encoding.UTF8))
            Dim info As New DshSnapshotInfo With {
                .Id = If(o("id")?.ToString(), Path.GetFileName(SnapshotDir.TrimEnd(SnapshotBS))),
                .Name = If(o("name")?.ToString(), ""),
                .Tier = If(o("tier")?.ToString(), TierQuick),
                .Note = If(o("note")?.ToString(), ""),
                .FileCount = If(o("fileCount") Is Nothing, 0, CInt(o("fileCount"))),
                .TotalBytes = If(o("totalBytes") Is Nothing, 0L, CLng(o("totalBytes"))),
                .Dir = SnapshotDir
            }
            Dim created As String = o("createdAt")?.ToString()
            Dim dt As DateTime
            If Not String.IsNullOrWhiteSpace(created) AndAlso
               DateTime.TryParse(created, Globalization.CultureInfo.InvariantCulture,
                                 Globalization.DateTimeStyles.RoundtripKind, dt) Then
                info.CreatedAt = dt
            End If
            If String.IsNullOrWhiteSpace(info.Name) Then info.Name = info.Id
            Return info
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：读取快照清单失败：{SnapshotDir}")
            Return Nothing
        End Try
    End Function

    ''' <summary>所有实例的快照合计体积。</summary>
    Public Function DshSnapshotTotalSize() As Long
        Try
            Dim rootDir As String = DshSnapshotRoot()
            If Not Directory.Exists(rootDir) Then Return 0L
            Return DshMigrate.MeasureDirectorySize(rootDir)
        Catch ex As Exception
            Logger.Warn(ex, "DSH：统计快照总体积失败")
            Return 0L
        End Try
    End Function

#End Region

#Region "创建"

    ''' <summary>
    ''' 给实例创建一个快照。
    ''' </summary>
    ''' <param name="Instance">目标实例。</param>
    ''' <param name="Name">快照名（空则按时间自动命名）。</param>
    ''' <param name="Full">True = 完整快照（含依赖）；False = 快速快照。</param>
    ''' <param name="Note">备注。</param>
    ''' <param name="Progress">进度回调（阶段, 说明, 0-1）。</param>
    ''' <remarks>
    ''' 创建前会先停掉实例 —— 依赖目录正被写的时候复制会得到半截文件。
    ''' </remarks>
    Public Function DshSnapshotCreate(Instance As DshInstance, Name As String, Full As Boolean,
                                      Optional Note As String = "",
                                      Optional Progress As DshMigrate.DshMigrateProgressHandler = Nothing) As DshSnapshotResult
        Dim result As New DshSnapshotResult()
        If Instance Is Nothing Then
            result.Message = "没有指定实例。"
            Return result
        End If

        Try
            ' 实例在跑就先停掉 —— 否则复制到一半依赖被改写，快照是坏的
            If Instance.IsRunning OrElse Instance.HasLiveProcess Then
                Report(Progress, "停止", "正在停止实例……", -1)
                DshService.StopService(Instance, Force:=True)
            End If

            Dim stamp As String = DateTime.Now.ToString("yyyyMMdd-HHmmss")
            Dim tier As String = If(Full, TierFull, TierQuick)
            Dim snapId As String = stamp
            Dim baseDir As String = DshSnapshotInstanceDir(Instance)
            If Not EnsureDir(baseDir) Then
                result.Message = "无法创建快照目录。"
                Return result
            End If

            Dim snapDir As String = Path.Combine(baseDir, snapId)
            ' 同一秒内连点两次也不至于互相覆盖
            Dim suffix As Integer = 1
            While Directory.Exists(snapDir)
                suffix += 1
                snapDir = Path.Combine(baseDir, snapId & "-" & suffix)
            End While
            snapId = Path.GetFileName(snapDir)

            Dim payloadDir As String = Path.Combine(snapDir, PayloadFolderName)
            Directory.CreateDirectory(payloadDir)

            Report(Progress, "快照", "正在复制实例数据……", 0.1)
            Dim skip As String() = If(Full, Nothing, SnapshotSkipNames)
            Dim stats As DshMigrate.CopyStats =
                DshMigrate.DshCopyTreePreservingLinks(Instance.HomeDir, payloadDir, skip, Progress)

            ' 写清单
            Dim info As New DshSnapshotInfo With {
                .Id = snapId,
                .Name = If(String.IsNullOrWhiteSpace(Name), $"快照 {DateTime.Now:MM-dd HH:mm}", Name.Trim()),
                .CreatedAt = DateTime.Now,
                .Tier = tier,
                .Note = If(Note, "").Trim(),
                .FileCount = stats.FileCount,
                .TotalBytes = stats.TotalBytes,
                .Dir = snapDir
            }
            WriteManifest(snapDir, info)

            result.Success = True
            result.Snapshot = info
            result.Message = $"已创建{If(Full, "完整", "快速")}快照「{info.Name}」" & vbCrLf &
                             $"{stats.FileCount} 个文件、{DshDoctor.FormatBytes(stats.TotalBytes)}" &
                             If(stats.LinkCount > 0, $"，{stats.LinkCount} 个符号链接", "")
            Logger.Info($"DSH：已创建快照 {snapId}（{tier}，{stats.FileCount} 文件）→ {snapDir}")
        Catch ex As Exception
            Logger.Error(ex, "DSH：创建快照失败")
            result.Success = False
            result.Message = "创建快照失败：" & ex.Message
        End Try
        Return result
    End Function

    ''' <summary>写清单文件。</summary>
    Private Sub WriteManifest(SnapshotDir As String, Info As DshSnapshotInfo)
        Try
            Dim o As New Newtonsoft.Json.Linq.JObject From {
                {"version", 1},
                {"id", Info.Id},
                {"name", Info.Name},
                {"createdAt", Info.CreatedAt.ToString("o")},
                {"tier", Info.Tier},
                {"note", Info.Note},
                {"fileCount", Info.FileCount},
                {"totalBytes", Info.TotalBytes}
            }
            File.WriteAllText(Path.Combine(SnapshotDir, ManifestName), o.ToString(), New UTF8Encoding(False))
        Catch ex As Exception
            Logger.Error(ex, "DSH：写快照清单失败")
            Throw
        End Try
    End Sub

#End Region

#Region "恢复"

    ''' <summary>
    ''' 把实例恢复到某个快照。
    ''' </summary>
    ''' <param name="Instance">目标实例。</param>
    ''' <param name="SnapshotId">快照 Id。</param>
    ''' <param name="Progress">进度回调。</param>
    ''' <remarks>
    ''' ⚠️ 恢复前会**自动给当前状态也拍一个快照**（叫「恢复前自动备份」）——
    ''' 恢复本身是个覆盖操作，万一用户点错了还能退回来。
    ''' 这条退路不占什么空间（快速档几 MB），但能救命。
    '''
    ''' 覆盖语义：只把快照里**有**的顶层条目覆盖回去，不动快照里没有的东西
    ''' （所以快速快照恢复后 node_modules 还在原地，不会被误删）。
    ''' </remarks>
    Public Function DshSnapshotRestore(Instance As DshInstance, SnapshotId As String,
                                       Optional Progress As DshMigrate.DshMigrateProgressHandler = Nothing) As DshSnapshotResult
        Dim result As New DshSnapshotResult()
        If Instance Is Nothing Then
            result.Message = "没有指定实例。"
            Return result
        End If

        Try
            Dim target As DshSnapshotInfo = DshSnapshotList(Instance).FirstOrDefault(
                Function(s) String.Equals(s.Id, SnapshotId, StringComparison.OrdinalIgnoreCase))
            If target Is Nothing Then
                result.Message = "找不到这个快照。"
                Return result
            End If

            Dim payloadDir As String = Path.Combine(target.Dir, PayloadFolderName)
            If Not Directory.Exists(payloadDir) Then
                result.Message = "这个快照的数据目录不存在，可能已被手动删除。"
                Return result
            End If

            ' 先停实例
            If Instance.IsRunning OrElse Instance.HasLiveProcess Then
                Report(Progress, "停止", "正在停止实例……", -1)
                DshService.StopService(Instance, Force:=True)
            End If

            ' 自动给当前状态留一条退路
            Report(Progress, "备份", "正在备份当前状态……", 0.05)
            Dim safety As DshSnapshotResult = DshSnapshotCreate(
                Instance, $"恢复前自动备份 {DateTime.Now:MM-dd HH:mm}", Full:=False,
                Note:=$"恢复快照「{target.Name}」之前自动创建")
            If Not safety.Success Then
                Logger.Warn($"DSH：恢复前的自动备份失败（继续恢复）：{safety.Message}")
            End If

            ' 按条目覆盖
            Report(Progress, "恢复", "正在恢复数据……", 0.3)
            Dim restored As Integer = 0
            Dim failures As New List(Of String)
            For Each entryPath As String In Directory.GetFileSystemEntries(payloadDir)
                Dim entryName As String = Path.GetFileName(entryPath)
                Dim dst As String = Path.Combine(Instance.HomeDir, entryName)
                Try
                    If File.Exists(entryPath) Then
                        File.Copy(entryPath, dst, True)
                    Else
                        ' 目录：先删掉目标再整体复制（避免新旧内容混在一起）
                        If Directory.Exists(dst) Then Directory.Delete(dst, True)
                        DshMigrate.DshCopyTreePreservingLinks(entryPath, dst)
                    End If
                    restored += 1
                Catch ex As Exception
                    Logger.Error(ex, $"DSH：恢复 {entryName} 失败")
                    failures.Add($"{entryName}：{ex.Message}")
                End Try
            Next

            ' 依赖：快速快照**没有**依赖（那是它体积小的原因），
            ' 恢复后按 profile 的 package.json 重装一遍。
            ' ⭐ 这里刻意复用同步模块（拿实例自己当源）而不是自己写一套装插件的逻辑 ——
            '    它已经处理了 spec 解析、link: 依赖、bundles 补齐这些坑，
            '    重写一遍只会漏掉其中某一步。
            If String.Equals(target.Tier, TierQuick, StringComparison.OrdinalIgnoreCase) Then
                Report(Progress, "依赖", "正在按清单重建插件依赖……", 0.7)
                Try
                    Dim repairPlan As DshProfileSync.DshSyncPlan =
                        DshProfileSync.DshSyncBuildPlan(Instance.HomeDir, Instance.Profile, Instance)
                    If repairPlan.Specs.Count > 0 OrElse repairPlan.LinkDeps.Count > 0 Then
                        Dim repairResult As DshProfileSync.DshSyncResult =
                            DshProfileSync.DshSyncExecute(repairPlan,
                                Sub(stage As String, message As String, pct As Double)
                                    Report(Progress, "依赖", message, If(pct >= 0, 0.7 + pct * 0.28, -1))
                                End Sub)
                        If Not repairResult.Success Then
                            failures.Add("插件依赖未能完全重建：" & repairResult.Message)
                        End If
                    End If
                Catch ex As Exception
                    ' 依赖重装失败不算致命 —— 数据已经回来了，用户可以手动点「一键修复」
                    Logger.Warn(ex, "DSH：恢复后重装依赖失败")
                    failures.Add("插件依赖重装失败，可在「维护与修复」里点「一键修复」")
                End Try
            End If

            Report(Progress, "完成", "恢复完成。", 1)
            result.Success = failures.Count = 0
            result.Snapshot = target
            result.Message = $"已恢复到「{target.Name}」（{restored} 项）" & vbCrLf &
                             If(safety.Success, $"恢复前的状态已自动备份为「{safety.Snapshot.Name}」。", "") &
                             If(failures.Count > 0, vbCrLf & vbCrLf & "以下项目未能恢复：" & vbCrLf &
                                String.Join(vbCrLf, failures), "")
            Logger.Info($"DSH：已从快照 {SnapshotId} 恢复实例 {Instance.DisplayName}（{restored} 项）")
        Catch ex As Exception
            Logger.Error(ex, "DSH：恢复快照失败")
            result.Success = False
            result.Message = "恢复失败：" & ex.Message
        End Try
        Return result
    End Function

#End Region

#Region "删除"

    ''' <summary>删除一个快照。成功返回 Nothing，失败返回原因。</summary>
    Public Function DshSnapshotDelete(Instance As DshInstance, SnapshotId As String) As String
        Try
            Dim target As DshSnapshotInfo = DshSnapshotList(Instance).FirstOrDefault(
                Function(s) String.Equals(s.Id, SnapshotId, StringComparison.OrdinalIgnoreCase))
            If target Is Nothing Then Return "找不到这个快照。"

            ' 纵深防御：只允许删实例快照目录**之下**的东西
            Dim baseDir As String = DshSnapshotInstanceDir(Instance)
            If String.IsNullOrWhiteSpace(baseDir) Then Return "实例目录异常，拒绝删除。"
            Dim baseAbs As String = Path.GetFullPath(baseDir).TrimEnd(SnapshotBS)
            Dim tgtAbs As String = Path.GetFullPath(target.Dir).TrimEnd(SnapshotBS)
            If Not tgtAbs.StartsWith(baseAbs & SnapshotBS, StringComparison.OrdinalIgnoreCase) Then
                Logger.Error($"DSH：拒绝删除越界的快照目录：{tgtAbs}")
                Return "快照路径越界，拒绝删除。"
            End If

            If Directory.Exists(target.Dir) Then Directory.Delete(target.Dir, True)
            Logger.Info($"DSH：已删除快照 {SnapshotId}")
            Return Nothing
        Catch ex As Exception
            Logger.Error(ex, "DSH：删除快照失败")
            Return "删除失败：" & ex.Message
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
            Logger.Warn(ex, "DSH：快照进度回调出错（已忽略）")
        End Try
    End Sub

#End Region

End Module
