''' <summary>
''' PCL_DSH：把「导出环境」产出的包**导入回实例**。
''' </summary>
''' <remarks>
''' ═══════════════════════════════════════════════════════════════════════
''' 为什么需要它（用户报的问题）
''' ═══════════════════════════════════════════════════════════════════════
''' 导出侧支持四类内容任意组合（运行时 / 插件 / 对话历史 / 用户配置），
''' 但原来的导入侧**只认运行时** —— 于是：
''' <list type="bullet">
''' <item>不含运行时的包（比如只导了插件+对话）**直接被拒收**</item>
''' <item>即使含运行时，另外三类也**只是躺在 imported\ 目录里没人管**</item>
''' </list>
''' 也就是"导得出去、导不回来"，导入导出**不对称**。
'''
''' 本模块补上另一半：把包里的 <c>home\</c> 数据按内容分别写进目标实例。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 设计：复用 DshProfileSync，不另造轮子
''' ═══════════════════════════════════════════════════════════════════════
''' 「从其他环境导入」（<see cref="DshProfileSync"/>）已经完整实现了
''' "把一个 DSH_HOME 的插件 / 密钥 / 使用数据搬进实例" ——
''' 它要的只是一个"源 home 目录"。
'''
''' 而导出包解压后正好有 <c>home\</c>，可以直接当 SourceHome 用。
''' 所以本模块的职责只有三件：
''' <list type="number">
''' <item>解压包、认出结构（<see cref="DshEnvImportProbe"/>)</item>
''' <item>把 <c>home\</c> 交给 <see cref="DshProfileSync.DshSyncBuildPlan"/></item>
''' <item>执行并汇总结果</item>
''' </list>
''' 这样插件安装逻辑、密钥写入逻辑、数据搬运逻辑**全都只有一份实现**，
''' 不会出现"导入环境"和"从其他环境导入"行为不一致的问题。
''' </remarks>
Public Module DshEnvImport

#Region "常量"

    ''' <summary>包里运行时所在的子目录名。</summary>
    Public Const DshImportRuntimeFolder As String = "runtime"

    ''' <summary>包里实例数据所在的子目录名。</summary>
    Public Const DshImportHomeFolder As String = "home"

#End Region

#Region "探测"

    ''' <summary>包里能导入的东西。</summary>
    Public Class DshEnvImportProbe
        ''' <summary>来源路径（zip 或文件夹）。</summary>
        Public Property SourcePath As String = ""
        ''' <summary>来源类型：<c>zip</c> / <c>folder</c>。</summary>
        Public Property SourceKind As String = ""

        ''' <summary>解压后的临时根目录（探测阶段就已解压；用完要清理）。</summary>
        Public Property ExtractedRoot As String = ""

        ''' <summary>运行时目录（没有则为 Nothing）。</summary>
        Public Property RuntimeDir As String = ""
        ''' <summary>实例数据目录（没有则为 Nothing）。</summary>
        Public Property HomeDir As String = ""

        ''' <summary>包里含运行时。</summary>
        Public ReadOnly Property HasRuntime As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(RuntimeDir) AndAlso Directory.Exists(RuntimeDir)
            End Get
        End Property
        ''' <summary>包里含实例数据（插件 / 对话 / 配置）。</summary>
        Public ReadOnly Property HasHomeData As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(HomeDir) AndAlso Directory.Exists(HomeDir)
            End Get
        End Property

        ''' <summary>清单里记的包含项（"Runtime" / "Plugins" / ...）。</summary>
        Public Property Parts As New List(Of String)()
        ''' <summary>导出时的 dsh 版本（清单里有的话）。</summary>
        Public Property DshVersion As String = ""
        ''' <summary>导出时的实例名（仅供参考）。</summary>
        Public Property SourceInstance As String = ""
        ''' <summary>清单里的格式版本；没有清单时为 -1。</summary>
        Public Property FormatVersion As Integer = -1

        ''' <summary>是不是「导出环境」产出的包（有清单文件）。</summary>
        Public Property IsEnvPackage As Boolean = False

        ''' <summary>探测失败的原因。</summary>
        Public Property FailReason As String = ""

        ''' <summary>可放行的提醒。</summary>
        Public Property Warning As String = ""

        ''' <summary>探测是否通过（能导入至少一样东西）。</summary>
        Public ReadOnly Property Ok As Boolean
            Get
                Return String.IsNullOrWhiteSpace(FailReason) AndAlso (HasRuntime OrElse HasHomeData)
            End Get
        End Property

        ''' <summary>给用户看的一行摘要。</summary>
        Public ReadOnly Property Summary As String
            Get
                Dim sb As New StringBuilder()
                If HasRuntime Then sb.AppendLine("· 运行时（可以装成一个独立的运行时槽位）")
                If HasHomeData Then
                    sb.AppendLine("· 实例数据（插件 / 对话历史 / 用户配置）")
                End If
                Return sb.ToString().TrimEnd()
            End Get
        End Property
    End Class

    ''' <summary>
    ''' 探测一个包能不能导入，并**解压到临时目录**（后续步骤需要真实文件）。
    ''' </summary>
    ''' <param name="SourcePath">zip 或文件夹路径。</param>
    ''' <param name="Progress">进度回调。</param>
    ''' <remarks>
    ''' ⚠️ 这个方法会**解压整个包**（可能是几百 MB），所以必须在后台线程调。
    ''' 调用方负责在结束后调 <see cref="DshEnvImportCleanup"/> 清理临时目录。
    ''' </remarks>
    Public Function DshBuildEnvImportProbe(SourcePath As String,
                                      Optional Progress As DshProfileSync.DshSyncProgressHandler = Nothing) As DshEnvImportProbe
        Dim result As New DshEnvImportProbe With {
            .SourcePath = SourcePath
        }
        Try
            If String.IsNullOrWhiteSpace(SourcePath) Then
                result.FailReason = "没有指定要导入的文件。"
                Return result
            End If

            Dim isZip As Boolean = SourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            Dim isDir As Boolean = Directory.Exists(SourcePath)
            If Not isZip AndAlso Not isDir Then
                result.FailReason = "只支持 .zip 压缩包或文件夹。"
                Return result
            End If
            result.SourceKind = If(isZip, "zip", "folder")

            ' ── ① 解压（或直接用文件夹）──
            Dim root As String = Nothing
            If isZip Then
                Report(Progress, "解压", "正在解压压缩包……", -1)
                root = DshEnvImportExtract(SourcePath, Progress)
                If String.IsNullOrWhiteSpace(root) Then
                    result.FailReason = "解压失败，请确认压缩包没有损坏、且磁盘空间足够。"
                    Return result
                End If
                result.ExtractedRoot = root
            Else
                root = SourcePath
            End If

            ' ── ② 认结构 ──
            Report(Progress, "校验", "正在检查包的内容……", 0.95)

            ' 清单（导出包才有）
            Dim manifestPath As String = Path.Combine(root, DshEnvExport.DshManifestFileName)
            If File.Exists(manifestPath) Then
                result.IsEnvPackage = True
                Try
                    Dim m As JObject = JObject.Parse(File.ReadAllText(manifestPath, Encoding.UTF8))
                    result.FormatVersion = If(m("formatVersion") Is Nothing, -1, CInt(m("formatVersion")))
                    result.DshVersion = If(m("dshVersion")?.ToString(), "")
                    result.SourceInstance = If(m("sourceInstance")?.ToString(), "")
                    Dim arr = TryCast(m("parts"), JArray)
                    If arr IsNot Nothing Then
                        For Each t In arr
                            result.Parts.Add(t.ToString())
                        Next
                    End If
                Catch ex As Exception
                    Logger.Warn(ex, "DSH：解析导出包清单失败（继续按目录结构判断）")
                    result.Warning = "包的清单文件读不出来，将按目录结构判断内容。"
                End Try
            End If

            ' 目录结构
            Dim rt As String = Path.Combine(root, DshImportRuntimeFolder)
            If Directory.Exists(rt) Then result.RuntimeDir = rt
            Dim hm As String = Path.Combine(root, DshImportHomeFolder)
            If Directory.Exists(hm) Then result.HomeDir = hm

            ' ── ③ 判断能不能导 ──
            If Not result.HasRuntime AndAlso Not result.HasHomeData Then
                If result.IsEnvPackage Then
                    ' 有清单但没有 runtime\ 和 home\ —— 包是坏的
                    result.FailReason = "这个包有 PCL DSH 的清单文件，但里面既没有 runtime\ 也没有 home\ 目录。" & vbCrLf &
                                        "包可能没有正确生成，或者在传输中损坏了。"
                Else
                    result.FailReason = "这个包里没有找到可导入的内容。" & vbCrLf & vbCrLf &
                                        "如果这是别人给你的 dsh 运行时包，它应当包含：" & vbCrLf &
                                        "  node_modules\@deepseek-ai\dsh\lib\bin.js" & vbCrLf & vbCrLf &
                                        "如果是 PCL DSH 导出的环境包，它应当包含：" & vbCrLf &
                                        "  runtime\ 和（或）home\ 目录，以及 pcl-dsh-manifest.json"
                End If
                Return result
            End If

            ' 版本兼容性提示
            If result.FormatVersion > DshEnvExport.DshExportFormatVersion Then
                result.Warning = $"这个包来自更新版本的 PCL DSH（格式 v{result.FormatVersion}，" &
                                 $"当前支持 v{DshEnvExport.DshExportFormatVersion}）。" & vbCrLf &
                                 "可能有些内容无法识别，建议升级 PCL DSH 后再导入。"
            End If

            Logger.Info($"DSH：导出包探测完成 —— 运行时={result.HasRuntime}，数据={result.HasHomeData}，" &
                        $"清单项={String.Join("/", result.Parts)}")
        Catch ex As Exception
            Logger.Error(ex, "DSH：探测导入包失败")
            result.FailReason = "探测失败：" & ex.Message
        End Try
        Return result
    End Function

    ''' <summary>把 zip 解压到临时目录，返回根目录。</summary>
    Private Function DshEnvImportExtract(ZipPath As String,
                                         Progress As DshProfileSync.DshSyncProgressHandler) As String
        Dim root As String = Path.Combine(Path.GetTempPath(),
                                          "pcl_dsh_import_" & Guid.NewGuid().ToString("N").Substring(0, 8))
        Try
            Directory.CreateDirectory(root)
            Using archive = System.IO.Compression.ZipFile.OpenRead(ZipPath)
                Dim total As Integer = archive.Entries.Count
                Dim done As Integer = 0
                For Each entry In archive.Entries
                    done += 1
                    ' 目录条目（以 / 结尾）跳过 —— 解压文件时会自动建目录
                    If String.IsNullOrWhiteSpace(entry.Name) Then Continue For

                    ' ⚠️ 防目录穿越：条目名里带 .. 的必须拒绝
                    '    （恶意 zip 可以用 ..\..\ 把文件写到包外）
                    Dim rel As String = entry.FullName.Replace("/"c, Path.DirectorySeparatorChar)
                    If rel.Contains("..") Then
                        Logger.Warn($"DSH：跳过可疑的压缩包条目（含 ..）：{entry.FullName}")
                        Continue For
                    End If

                    Dim dest As String = Path.Combine(root, rel)
                    Dim destDir As String = Path.GetDirectoryName(dest)
                    If Not String.IsNullOrWhiteSpace(destDir) AndAlso Not Directory.Exists(destDir) Then
                        Directory.CreateDirectory(destDir)
                    End If
                    Try
                        entry.ExtractToFile(dest, True)
                    Catch ex As Exception
                        Logger.Warn(ex, $"DSH：解压条目失败：{entry.FullName}")
                    End Try

                    ' 进度按条目数上报（不按字节 —— 大包条目少时按字节会卡在 0%）
                    If total > 0 AndAlso done Mod 20 = 0 Then
                        Report(Progress, "解压", $"正在解压（{done}/{total}）……", done / total * 0.9)
                    End If
                Next
            End Using
            Return root
        Catch ex As Exception
            Logger.Error(ex, $"DSH：解压失败：{ZipPath}")
            DshEnvImportCleanup(root)
            Return Nothing
        End Try
    End Function

    ''' <summary>清理探测阶段产生的临时目录。</summary>
    Public Sub DshEnvImportCleanup(ExtractedRoot As String)
        ' ⭐ 幂等：目录已不存在时静默返回。
        '    调用方会在多处调它（用户取消时由 Finally 兜底、后台任务完成时自己也清），
        '    重复调用是**预期行为**，不能报错也不能刷日志。
        If String.IsNullOrWhiteSpace(ExtractedRoot) Then Return
        Try
            If Not Directory.Exists(ExtractedRoot) Then Return
            DshMigrate.DeleteDirectoryRobust(ExtractedRoot)
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：清理导入临时目录失败：{ExtractedRoot}")
        End Try
    End Sub

#End Region

#Region "执行"

    ''' <summary>导入结果。</summary>
    Public Class DshEnvImportResult
        Public Property Success As Boolean = False
        Public Property Message As String = ""

        ''' <summary>是否导入了运行时（槽位 id）。</summary>
        Public Property RuntimeSlotId As String = ""
        ''' <summary>导入的运行时的显示名。</summary>
        Public Property RuntimeName As String = ""

        ''' <summary>写入的 API Key 个数。</summary>
        Public Property KeysWritten As Integer = 0
        ''' <summary>安装的插件个数。</summary>
        Public Property PluginsInstalled As Integer = 0
        ''' <summary>搬运的数据项个数。</summary>
        Public Property DataCopied As Integer = 0

        ''' <summary>各部分的说明（给用户看的清单）。</summary>
        Public Property Details As New List(Of String)()
        ''' <summary>失败明细。</summary>
        Public Property Failures As New List(Of String)()
    End Class

    ''' <summary>
    ''' 把探测好的包导入。
    ''' </summary>
    ''' <param name="Probe">探测结果（必须是已解压状态）。</param>
    ''' <param name="TargetInstance">
    ''' 实例数据（插件 / 对话 / 配置）要写进哪个实例。
    ''' 包里没有 <c>home\</c> 时可以是 Nothing。
    ''' </param>
    ''' <param name="ImportRuntime">是否导入运行时部分。</param>
    ''' <param name="Progress">进度回调。</param>
    ''' <remarks>
    ''' 阻塞调用，请放在后台线程。
    '''
    ''' ⚠️ 写实例数据会**覆盖**目标实例的同名文件（对话记录按会话 id 合并，
    ''' 同名会话会被覆盖）。所以调用方**必须**先让用户确认，并建议拍快照。
    ''' </remarks>
    Public Function DshEnvImportExecute(Probe As DshEnvImportProbe,
                                        TargetInstance As DshInstance,
                                        ImportRuntime As Boolean,
                                        Optional Progress As DshProfileSync.DshSyncProgressHandler = Nothing) As DshEnvImportResult
        Dim res As New DshEnvImportResult()
        Try
            If Probe Is Nothing OrElse Not Probe.Ok Then
                res.Message = If(Probe?.FailReason, "没有可导入的内容。")
                Return res
            End If

            ' ── ① 运行时 ──
            If ImportRuntime AndAlso Probe.HasRuntime Then
                Try
                    Report(Progress, "运行时", "正在导入运行时（可能较大，请稍候）……", 0.05)
                    Dim imp As DshRuntimeSlot.DshSlotImportResult =
                        DshRuntimeSlot.DshSlotImportFromFolder(
                            Probe.RuntimeDir,
                            MakeSlotProgress(Progress, "运行时", 0.05, 0.5))
                    If imp IsNot Nothing AndAlso imp.Success AndAlso imp.Slot IsNot Nothing Then
                        res.RuntimeSlotId = imp.Slot.Id
                        res.RuntimeName = imp.Slot.DisplayName
                        res.Details.Add($"运行时（{imp.Slot.DisplayName}）")
                    Else
                        res.Failures.Add("运行时：" & If(imp?.Message, "导入失败"))
                    End If
                Catch ex As Exception
                    Logger.Error(ex, "DSH：导入运行时失败")
                    res.Failures.Add("运行时：" & ex.Message)
                End Try
            End If

            ' ── ② 实例数据（插件 / 对话 / 配置）──
            If Probe.HasHomeData Then
                If TargetInstance Is Nothing Then
                    res.Failures.Add("实例数据：没有指定目标实例，跳过")
                Else
                    Try
                        Report(Progress, "数据", "正在准备导入实例数据……", 0.5)
                        ' ⭐ 复用「从其他环境导入」的全部逻辑 ——
                        '   IncludeData:=True 才会搬对话历史等使用痕迹
                        Dim plan As DshProfileSync.DshSyncPlan =
                            DshProfileSync.DshSyncBuildPlan(Probe.HomeDir, ModDSH.DshProfileName,
                                                            TargetInstance, IncludeData:=True)
                        If plan IsNot Nothing Then
                            Dim syncRes As DshProfileSync.DshSyncResult =
                                DshProfileSync.DshSyncExecute(plan, Progress)
                            If syncRes IsNot Nothing Then
                                res.KeysWritten = syncRes.KeysWritten
                                res.PluginsInstalled = syncRes.PluginsInstalled
                                res.DataCopied = syncRes.DataCopied
                                If syncRes.KeysWritten > 0 Then res.Details.Add($"API Key（{syncRes.KeysWritten} 个）")
                                If syncRes.PluginsInstalled > 0 Then res.Details.Add($"插件（{syncRes.PluginsInstalled} 个）")
                                If syncRes.DataCopied > 0 Then res.Details.Add($"使用数据（{syncRes.DataCopied} 项，含对话历史）")
                                If syncRes.Failures IsNot Nothing Then
                                    For Each f As String In syncRes.Failures
                                        res.Failures.Add(f)
                                    Next
                                End If
                            End If
                        End If
                    Catch ex As Exception
                        Logger.Error(ex, "DSH：导入实例数据失败")
                        res.Failures.Add("实例数据：" & ex.Message)
                    End Try
                End If
            End If

            ' ── 汇总 ──
            res.Success = res.Failures.Count = 0 AndAlso res.Details.Count > 0

            Dim sb As New StringBuilder()
            sb.AppendLine("导入完成。")
            sb.AppendLine()
            If res.Details.Count > 0 Then
                sb.AppendLine("已导入：")
                For Each d As String In res.Details
                    sb.AppendLine("· " & d)
                Next
            Else
                sb.AppendLine("没有任何内容被导入。")
            End If
            If TargetInstance IsNot Nothing AndAlso Probe.HasHomeData AndAlso
               (res.PluginsInstalled > 0 OrElse res.DataCopied > 0) Then
                sb.AppendLine()
                sb.AppendLine($"数据已写入实例「{TargetInstance.DisplayName}」。")
                sb.AppendLine("如果这个实例正在运行，需要重启它才能看到新内容。")
            End If
            If res.Failures.Count > 0 Then
                sb.AppendLine()
                sb.AppendLine("以下内容未能导入：")
                For Each f As String In res.Failures
                    sb.AppendLine("· " & f)
                Next
            End If
            res.Message = sb.ToString()

            Logger.Info($"DSH：导入环境完成 —— 运行时={res.RuntimeSlotId}，" &
                        $"插件={res.PluginsInstalled}，数据={res.DataCopied}，密钥={res.KeysWritten}，" &
                        $"失败={res.Failures.Count}")
        Catch ex As Exception
            Logger.Error(ex, "DSH：导入环境失败")
            res.Success = False
            res.Message = "导入失败：" & ex.Message
        End Try
        Return res
    End Function

#End Region

#Region "进度"

    ''' <summary>
    ''' 把 <see cref="DshProfileSync.DshSyncProgressHandler"/> 适配成
    ''' <see cref="DshRuntimeSlot.DshSlotProgressHandler"/>，并做区间映射。
    ''' </summary>
    ''' <remarks>
    ''' 两个委托的签名**完全一样**（<c>(String, String, Double)</c>），只是类型不同，
    ''' 所以需要一层薄适配。
    '''
    ''' 映射的原因：导入分"运行时"和"数据"两大段，各自内部从 0 报起。
    ''' 直接透传会让进度条先跑到 90% 再跳回 0%，看起来像出错了。
    ''' </remarks>
    Private Function MakeSlotProgress(Outer As DshProfileSync.DshSyncProgressHandler,
                                      StageName As String,
                                      StartPct As Double, EndPct As Double) As DshRuntimeSlot.DshSlotProgressHandler
        If Outer Is Nothing Then Return Nothing
        Return Sub(innerStage As String, message As String, innerPct As Double)
                   Try
                       If innerPct < 0 Then
                           Outer(StageName, message, -1)
                       Else
                           Dim mapped As Double = StartPct + (EndPct - StartPct) * Math.Max(0, Math.Min(1, innerPct))
                           Outer(StageName, message, mapped)
                       End If
                   Catch ex As Exception
                       Logger.Warn(ex, "DSH：导入进度映射出错（已忽略）")
                   End Try
               End Sub
    End Function

    Private Sub Report(Handler As DshProfileSync.DshSyncProgressHandler,
                       Stage As String, Message As String, Progress As Double)
        If Handler Is Nothing Then Return
        Try
            Handler(Stage, Message, Progress)
        Catch ex As Exception
            Logger.Warn(ex, "DSH：导入进度回调出错（已忽略）")
        End Try
    End Sub

#End Region

End Module
