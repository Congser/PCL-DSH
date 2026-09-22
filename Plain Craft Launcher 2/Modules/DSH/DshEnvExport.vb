''' <summary>
''' PCL_DSH：把一个实例的环境导出成可分享的包。
''' </summary>
''' <remarks>
''' ═══════════════════════════════════════════════════════════════════════
''' 为什么需要它
''' ═══════════════════════════════════════════════════════════════════════
''' 「从其他环境导入」只能接收、不能产出 —— 用户没法把自己的环境交给别人：
''' 想分享一个调好的插件组合、想把对话记录备份成文件、想搬到另一台机器，
''' 都做不到。导出是导入的**另一半**，两者必须成对存在。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 与导入的对称性（硬约束）
''' ═══════════════════════════════════════════════════════════════════════
''' <list type="bullet">
''' <item><b>可选项一致</b> —— 同样是四类：运行时 / 插件 / 对话历史 / 用户配置</item>
''' <item><b>包结构一致</b> —— 由**导入侧的探测逻辑**定义。导出按
'''       <see cref="DshProfileSync.DshSyncLooksLikeHome"/> 认的形状写，
'''       这样"导出的包自己一定导得回来"</item>
''' <item><b>数据项一致</b> —— 复用 <c>DshProfileSync</c> 的那份 14 项清单，
'''       不另立一套分类</item>
''' </list>
'''
''' ═══════════════════════════════════════════════════════════════════════
''' ⚠️ 导出独有的风险：密钥外泄
''' ═══════════════════════════════════════════════════════════════════════
''' 导入时用户的 Key 是**进到自己机器**；导出时 Key 是**要给别人**的。
''' 所以导出侧的默认值刻意与导入**相反**：
''' <list type="bullet">
''' <item>导入默认全选（求省事）</item>
''' <item>导出**默认不勾选「用户配置」**（求安全）</item>
''' </list>
''' 并且提供 <c>StripSecrets</c> 选项：保留配置**结构**（有哪些接入方式、
''' BaseUrl、模型名），但把密钥值留空 —— 别人导入后只需填自己的 Key。
''' 「分享配置」不等于「分享密钥」。
''' </remarks>
Public Module DshEnvExport

#Region "可选项定义"

    ''' <summary>
    ''' 导出的四类可选项。
    ''' </summary>
    ''' <remarks>
    ''' 这个枚举是**导入导出共用**的选项集 —— 两边必须用同一份定义，
    ''' 否则会出现"导入能选、导出不能选"的不对称。
    ''' </remarks>
    Public Enum DshExportPart
        ''' <summary>运行时（Node + dsh 本体）。</summary>
        Runtime = 0
        ''' <summary>插件（依赖清单 + 本地插件目录）。</summary>
        Plugins = 1
        ''' <summary>对话历史（会话正文 + 索引）。</summary>
        Sessions = 2
        ''' <summary>用户配置（API Key + 设置 + profile patch）。</summary>
        Config = 3
    End Enum

    ''' <summary>一类的探测结果（给确认框用）。</summary>
    Public Class DshExportPartInfo
        Public Property Part As DshExportPart
        Public Property Name As String = ""
        Public Property Description As String = ""
        ''' <summary>这一类里有多少项（插件数 / 会话数 / Key 数……）。</summary>
        Public Property ItemCount As Integer = 0
        ''' <summary>估算体积（字节；-1 = 未统计）。</summary>
        Public Property Bytes As Long = -1
        ''' <summary>这一类是否可导出（没有内容时为 False）。</summary>
        Public ReadOnly Property HasContent As Boolean
            Get
                Return ItemCount > 0
            End Get
        End Property
        ''' <summary>是否**默认勾选**。</summary>
        ''' <remarks>
        ''' ⚠️ 两类刻意不默认勾：
        ''' <list type="bullet">
        ''' <item><b>运行时</b> —— 它有 39 万个文件 / 3.8 GB，复制要几分钟到十几分钟。
        '''       而运行时是**通用**的（别人自己装配即可），真正的"个人数据"是
        '''       插件 + 对话 + 配置（约 400 MB）。默认勾它会让一次导出变成大工程。</item>
        ''' <item><b>用户配置</b> —— 导出是把数据交给别人，含密钥的默认值必须保守。
        '''       见本文件顶部的密钥外泄说明。</item>
        ''' </list>
        ''' </remarks>
        Public ReadOnly Property DefaultChecked As Boolean
            Get
                Return Part <> DshExportPart.Config AndAlso Part <> DshExportPart.Runtime
            End Get
        End Property
        ''' <summary>勾选它是否需要二次确认（涉及敏感数据）。</summary>
        Public ReadOnly Property NeedsConfirm As Boolean
            Get
                Return Part = DshExportPart.Config
            End Get
        End Property
    End Class

    ''' <summary>一次导出的完整探测结果。</summary>
    Public Class DshExportProbe
        Public Property InstanceName As String = ""
        Public Property InstanceHome As String = ""
        Public Property DshVersion As String = ""
        Public Property Parts As New List(Of DshExportPartInfo)()
        ''' <summary>本机有没有可用运行时（没有就没法导出运行时）。</summary>
        Public Property RuntimeSlotId As String = ""
        Public Property Warning As String = ""

        ''' <summary>全部四类合计体积（只算有内容的）。</summary>
        Public ReadOnly Property TotalBytes As Long
            Get
                Dim sum As Long = 0
                For Each p In Parts
                    If p.HasContent AndAlso p.Bytes > 0 Then sum += p.Bytes
                Next
                Return sum
            End Get
        End Property
    End Class

#End Region

#Region "探测"

    ''' <summary>
    ''' 探测某个实例能导出什么（**只读**，不改任何东西）。
    ''' </summary>
    ''' <param name="Instance">源实例。</param>
    ''' <remarks>
    ''' 与导入侧的"先探测再确认"完全对应 —— 用户在动手前应该看清
    ''' 「要导出什么、多大、包含不包含密钥」。
    ''' </remarks>
    Public Function DshBuildExportProbe(Instance As DshInstance) As DshExportProbe
        Dim probe As New DshExportProbe()
        Try
            If Instance Is Nothing Then
                probe.Warning = "没有指定要导出的实例。"
                Return probe
            End If
            probe.InstanceName = Instance.DisplayName
            probe.InstanceHome = Instance.HomeDir

            ' ── ① 运行时 ──
            probe.Parts.Add(ProbeRuntime(probe))

            ' ── ② 插件 ──
            probe.Parts.Add(ProbePlugins(Instance))

            ' ── ③ 对话历史 ──
            probe.Parts.Add(ProbeSessions(Instance))

            ' ── ④ 用户配置 ──
            probe.Parts.Add(ProbeConfig(Instance))

            ' 本机 dsh 版本（写进清单，供导入侧做兼容性提示）
            Try
                probe.DshVersion = DshRuntime.GetInstalledDshVersion()
            Catch ex As Exception
                Logger.Warn(ex, "DSH：读取 dsh 版本用于导出失败")
            End Try
        Catch ex As Exception
            Logger.Error(ex, "DSH：探测导出内容失败")
            probe.Warning = "探测失败：" & ex.Message
        End Try
        Return probe
    End Function

    Private Function ProbeRuntime(Probe As DshExportProbe) As DshExportPartInfo
        Dim info As New DshExportPartInfo With {
            .Part = DshExportPart.Runtime,
            .Name = "运行时",
            .Description = "Node + dsh 本体（约几百 MB）"
        }
        Try
            Dim slot = DshRuntimeSlot.DshSlotActive()
            If slot IsNot Nothing AndAlso slot.IsUsable Then
                Probe.RuntimeSlotId = slot.Id
                info.ItemCount = 1
                ' 用不跟随链接的统计 —— 它算的是**真实文件**总量，这正是复制会带走的字节数。
                ' （链接只重建、不占空间；pnpm 的 6721 个目录链接本身是 0 字节）
                info.Bytes = DshRuntimeSlot.DshSlotMeasureFolder(slot.Dir)
            Else
                info.Description = "本机没有可用的运行时"
            End If
        Catch ex As Exception
            Logger.Warn(ex, "DSH：探测运行时用于导出失败")
            info.Description = "读取运行时信息失败"
        End Try
        Return info
    End Function

    Private Function ProbePlugins(Instance As DshInstance) As DshExportPartInfo
        Dim info As New DshExportPartInfo With {
            .Part = DshExportPart.Plugins,
            .Name = "插件",
            .Description = "依赖清单 + 本地插件目录"
        }
        Try
            Dim n As Integer = DshPluginMarket.ListInstalledPlugins(Instance).Count
            info.ItemCount = n
            ' 插件体积：profile 下的 node_modules
            Try
                ' ⚠️ 变量名避开 dir（VB 内置函数，项目约定里记过这个坑家族）
                Dim profileDir As String = DshPluginMarket.ProfileDirOf(Instance)
                Dim nm As String = profileDir & "node_modules\"
                If Directory.Exists(nm) Then info.Bytes = DshMigrate.MeasureDirectorySize(nm)
            Catch ex As Exception
                Logger.Warn(ex, "DSH：估算插件体积失败")
            End Try
        Catch ex As Exception
            Logger.Warn(ex, "DSH：探测插件用于导出失败")
        End Try
        Return info
    End Function

    Private Function ProbeSessions(Instance As DshInstance) As DshExportPartInfo
        Dim info As New DshExportPartInfo With {
            .Part = DshExportPart.Sessions,
            .Name = "对话历史",
            .Description = "会话正文 + 索引 + 附件"
        }
        Try
            info.ItemCount = DshSessionCopy.DshSessionCount(Instance)
            ' 体积：sessions + storages + attachments
            Dim total As Long = 0
            For Each name As String In {"sessions", "storages", "attachments"}
                Try
                    Dim p As String = Instance.HomeDir & name & "\"
                    If Directory.Exists(p) Then total += DshMigrate.MeasureDirectorySize(p)
                Catch ex As Exception
                    Logger.Warn(ex, $"DSH：估算 {name} 体积失败")
                End Try
            Next
            info.Bytes = total
        Catch ex As Exception
            Logger.Warn(ex, "DSH：探测会话用于导出失败")
        End Try
        Return info
    End Function

    Private Function ProbeConfig(Instance As DshInstance) As DshExportPartInfo
        Dim info As New DshExportPartInfo With {
            .Part = DshExportPart.Config,
            .Name = "用户配置（含 API Key）",
            .Description = "API Key + 设置 + profile 配置"
        }
        Try
            Dim keys As Dictionary(Of String, String) = DshCredentials.ReadRefs(Instance.HomeDir)
            info.ItemCount = keys.Count
        Catch ex As Exception
            Logger.Warn(ex, "DSH：探测配置用于导出失败")
        End Try
        Return info
    End Function

#End Region

#Region "执行"

    ''' <summary>一次导出的结果。</summary>
    Public Class DshExportResult
        Public Property Success As Boolean = False
        Public Property Message As String = ""
        ''' <summary>导出到的文件路径。</summary>
        Public Property OutputPath As String = ""
        ''' <summary>实际打包的字节数。</summary>
        Public Property Bytes As Long = 0
        ''' <summary>各部分的明细（"插件：3 个"）。</summary>
        Public Property Parts As New List(Of String)()
        ''' <summary>失败明细。</summary>
        Public Property Failures As New List(Of String)()
        ''' <summary>包里是否含密钥（用于结果提示）。</summary>
        Public Property ContainsSecrets As Boolean = False
        ''' <summary>含几个密钥。</summary>
        Public Property SecretCount As Integer = 0
    End Class

    ''' <summary>
    ''' 把实例的选中部分导出成一个 zip 包。
    ''' </summary>
    ''' <param name="Instance">源实例。</param>
    ''' <param name="Parts">要导出的部分（来自探测结果的勾选）。</param>
    ''' <param name="OutputPath">目标 zip 路径。</param>
    ''' <param name="StripSecrets">
    ''' 是否剔除密钥值（保留结构）。**默认 True** ——
    ''' 「分享配置」通常不等于「分享密钥」。
    ''' </param>
    ''' <param name="Progress">进度回调（阶段, 说明, 0-1）。</param>
    ''' <remarks>
    ''' 阻塞调用，请放在后台线程。
    '''
    ''' ⚠️ 包结构按**导入侧认得的形状**写（见本文件顶部说明）：
    ''' <code>
    ''' &lt;zip 根&gt;\
    '''   pcl-dsh-manifest.json      ← 清单（格式版本 / 包含项 / dsh 版本）
    '''   home\                      ← 实例数据（导入侧认这个）
    '''     sessions\ storages\ ...
    '''     profiles\web\package.json
    '''     .credentials.yaml
    '''   runtime\                   ← 可选：运行时本体
    ''' </code>
    ''' </remarks>
    Public Function DshEnvExportToZip(Instance As DshInstance,
                                      Parts As List(Of DshExportPart),
                                      OutputPath As String,
                                      Optional StripSecrets As Boolean = True,
                                      Optional Progress As DshProfileSync.DshSyncProgressHandler = Nothing) As DshExportResult
        Dim res As New DshExportResult()
        Dim tempRoot As String = Nothing
        Try
            If Instance Is Nothing Then
                res.Message = "没有指定要导出的实例。"
                Return res
            End If
            If Parts Is Nothing OrElse Parts.Count = 0 Then
                res.Message = "没有勾选任何要导出的内容。"
                Return res
            End If
            If String.IsNullOrWhiteSpace(OutputPath) Then
                res.Message = "没有指定导出位置。"
                Return res
            End If

            Report(Progress, "准备", "正在准备导出……", -1)

            ' ── 在临时目录里搭出包结构，最后整体压缩 ──
            tempRoot = Path.Combine(Path.GetTempPath(),
                                    "pcl_dsh_export_" & Guid.NewGuid().ToString("N").Substring(0, 8))
            Dim homeDir As String = Path.Combine(tempRoot, "home")
            Dim runtimeDir As String = Path.Combine(tempRoot, "runtime")
            Directory.CreateDirectory(homeDir)

            Dim secretCount As Integer = 0

            ' ── ① 运行时 ──
            If Parts.Contains(DshExportPart.Runtime) Then
                Try
                    Dim slot = DshRuntimeSlot.DshSlotActive()
                    If slot IsNot Nothing AndAlso slot.IsUsable Then
                        ' ⭐ 把复制内部的进度（0~0.9）**映射**到本阶段占的区间。
                        '   为什么要映射：四个部分各占总进度的一段，而复制自己
                        '   报的是"我这部分完成了多少"。直接透传会让进度条来回跳。
                        '   复制是耗时最长的一环（39 万个文件），所以给它最大的区间。
                        DshMigrate.DshCopyTreePreservingLinks(
                            slot.Dir, runtimeDir, Nothing,
                            MakeStageProgress(Progress, "运行时", 0.02, 0.60))
                        res.Parts.Add($"运行时（{slot.DisplayName}）")
                    Else
                        res.Failures.Add("运行时：本机没有可用的运行时")
                    End If
                Catch ex As Exception
                    Logger.Error(ex, "DSH：导出运行时失败")
                    res.Failures.Add("运行时：" & ex.Message)
                End Try
            End If

            ' ── ② 插件（写进 home\profiles\）──
            If Parts.Contains(DshExportPart.Plugins) Then
                Try
                    Report(Progress, "插件", "正在收集插件清单……", 0.62)
                    Dim n As Integer = CopyProfileForExport(Instance, homeDir)
                    If n > 0 Then res.Parts.Add($"插件（{n} 个）")
                Catch ex As Exception
                    Logger.Error(ex, "DSH：导出插件失败")
                    res.Failures.Add("插件：" & ex.Message)
                End Try
            End If

            ' ── ③ 对话历史 ──
            If Parts.Contains(DshExportPart.Sessions) Then
                Try
                    Dim copied As Integer = 0
                    Dim entries As String() = DshProfileSync.DshSyncDataEntriesPublic
                    Dim totalEntries As Integer = entries.Length
                    Dim idx As Integer = 0
                    For Each name As String In entries
                        idx += 1
                        Dim src As String = Path.Combine(Instance.HomeDir, name)
                        If Not File.Exists(src) AndAlso Not Directory.Exists(src) Then Continue For
                        Dim dst As String = Path.Combine(homeDir, name)
                        Try
                            ' ⭐ 每一项的复制都带进度 —— 对话历史可能有几百 MB，
                            '   不报进度用户会以为卡住了
                            Report(Progress, "对话历史",
                                   $"正在复制 {name}（{idx}/{totalEntries}）……",
                                   0.64 + 0.16 * (idx / Math.Max(1, totalEntries)))
                            If File.Exists(src) Then
                                File.Copy(src, dst, True)
                            Else
                                DshMigrate.DshCopyTreePreservingLinks(
                                    src, dst, Nothing,
                                    MakeStageProgress(Progress, "对话历史",
                                                      0.64 + 0.16 * ((idx - 1) / Math.Max(1, totalEntries)),
                                                      0.64 + 0.16 * (idx / Math.Max(1, totalEntries))))
                            End If
                            copied += 1
                        Catch ex As Exception
                            Logger.Warn(ex, $"DSH：导出 {name} 失败")
                            res.Failures.Add($"{name}：{ex.Message}")
                        End Try
                    Next
                    If copied > 0 Then res.Parts.Add($"对话历史（{DshSessionCopy.DshSessionCount(Instance)} 个会话）")
                Catch ex As Exception
                    Logger.Error(ex, "DSH：导出对话历史失败")
                    res.Failures.Add("对话历史：" & ex.Message)
                End Try
            End If

            ' ── ④ 用户配置 ──
            If Parts.Contains(DshExportPart.Config) Then
                Try
                    Report(Progress, "用户配置", "正在收集配置……", 0.82)
                    secretCount = CopyConfigForExport(Instance, homeDir, StripSecrets)
                    res.ContainsSecrets = (secretCount > 0)
                    res.SecretCount = secretCount
                    Dim desc As String = If(StripSecrets, "用户配置（不含密钥）",
                                            $"用户配置（含 {secretCount} 个密钥）")
                    res.Parts.Add(desc)
                Catch ex As Exception
                    Logger.Error(ex, "DSH：导出配置失败")
                    res.Failures.Add("配置：" & ex.Message)
                End Try
            End If

            ' ── 写清单 ──
            Report(Progress, "收尾", "正在写清单……", 0.85)
            WriteManifest(tempRoot, Instance, Parts, StripSecrets, secretCount)

            ' ── 压缩 ──
            ' ⚠️ 压缩是**单次不可中断**的调用，内部没有进度回调 ——
            '   所以只能显示不确定进度（-1），让浮层转圈而不是假装知道百分比。
            '   这一步在 3.8 GB 时可能要好几分钟。
            Report(Progress, "压缩", "正在打包（体积大时可能要几分钟，请耐心等待）……", -1)
            Dim outDir As String = Path.GetDirectoryName(OutputPath)
            If Not String.IsNullOrWhiteSpace(outDir) AndAlso Not Directory.Exists(outDir) Then
                Directory.CreateDirectory(outDir)
            End If
            If File.Exists(OutputPath) Then File.Delete(OutputPath)
            System.IO.Compression.ZipFile.CreateFromDirectory(
                tempRoot, OutputPath, System.IO.Compression.CompressionLevel.Optimal, False)

            res.OutputPath = OutputPath
            res.Bytes = New FileInfo(OutputPath).Length
            res.Success = res.Failures.Count = 0

            Dim sb As New StringBuilder()
            sb.AppendLine($"已导出到：{OutputPath}")
            sb.AppendLine($"体积：{DshDoctor.FormatBytes(res.Bytes)}")
            sb.AppendLine()
            sb.AppendLine("包含：")
            For Each p As String In res.Parts
                sb.AppendLine("· " & p)
            Next
            sb.AppendLine()
            If res.ContainsSecrets Then
                sb.AppendLine($"⚠ 这个包**含 {res.SecretCount} 个 API Key** —— 发给别人等于把密钥给了他。")
                sb.AppendLine("   如果只是想分享配置，建议重新导出并勾选「剔除密钥」。")
            ElseIf Parts.Contains(DshExportPart.Config) Then
                sb.AppendLine("✓ 不含密钥（已剔除密钥值，只保留配置结构）。")
                sb.AppendLine("   别人导入后填自己的 Key 即可使用。")
            End If
            If res.Failures.Count > 0 Then
                sb.AppendLine()
                sb.AppendLine("以下内容未能导出：")
                For Each f As String In res.Failures
                    sb.AppendLine("· " & f)
                Next
            End If
            res.Message = sb.ToString()

            Logger.Info($"DSH：环境导出完成 → {OutputPath}（{DshDoctor.FormatBytes(res.Bytes)}，含密钥={res.ContainsSecrets}）")
        Catch ex As Exception
            Logger.Error(ex, "DSH：导出环境失败")
            res.Success = False
            res.Message = "导出失败：" & ex.Message
        Finally
            ' 清理临时目录（失败也不能留垃圾）
            If tempRoot IsNot Nothing AndAlso Directory.Exists(tempRoot) Then
                Try
                    DshMigrate.DeleteDirectoryRobust(tempRoot)
                Catch ex As Exception
                    Logger.Warn(ex, $"DSH：清理导出临时目录失败：{tempRoot}")
                End Try
            End If
        End Try
        Return res
    End Function

    ''' <summary>
    ''' 把实例的 profile 目录（package.json + cordis.patch.yml）复制进导出包。
    ''' </summary>
    ''' <returns>插件数量。</returns>
    Private Function CopyProfileForExport(Instance As DshInstance, HomeDir As String) As Integer
        Dim count As Integer = 0
        Try
            Dim profileName As String = If(String.IsNullOrWhiteSpace(Instance.Profile),
                                           ModDSH.DshProfileName, Instance.Profile)
            Dim srcProfile As String = DshPluginMarket.ProfileDirOf(Instance, profileName)
            Dim dstProfile As String = Path.Combine(HomeDir, "profiles", profileName)
            Directory.CreateDirectory(dstProfile)

            ' package.json —— 导入侧靠它重建依赖
            Dim pkg As String = Path.Combine(srcProfile, "package.json")
            If File.Exists(pkg) Then
                File.Copy(pkg, Path.Combine(dstProfile, "package.json"), True)
                Try
                    Dim obj As JObject = JObject.Parse(File.ReadAllText(pkg, Encoding.UTF8))
                    Dim deps = TryCast(obj("dependencies"), JObject)
                    If deps IsNot Nothing Then count = deps.Count
                Catch ex As Exception
                    Logger.Warn(ex, "DSH：读取导出插件数失败")
                End Try
            End If

            ' cordis.patch.yml —— 插件的权限预设写在里面，必须一起带走
            Dim patch As String = Path.Combine(srcProfile, "cordis.patch.yml")
            If File.Exists(patch) Then File.Copy(patch, Path.Combine(dstProfile, "cordis.patch.yml"), True)

            ' pnpm-workspace.yaml —— allowBuilds 在里面
            Dim ws As String = Path.Combine(srcProfile, "pnpm-workspace.yaml")
            If File.Exists(ws) Then File.Copy(ws, Path.Combine(dstProfile, "pnpm-workspace.yaml"), True)

            ' 本地插件（link: 依赖）目录也要带走，否则导入侧装不上
            Dim localPlugins As String = Path.Combine(Instance.HomeDir, "local-plugins")
            If Directory.Exists(localPlugins) Then
                Try
                    DshMigrate.DshCopyTreePreservingLinks(
                        localPlugins, Path.Combine(HomeDir, "local-plugins"))
                Catch ex As Exception
                    Logger.Warn(ex, "DSH：导出本地插件目录失败")
                End Try
            End If
        Catch ex As Exception
            Logger.Error(ex, "DSH：复制 profile 用于导出失败")
            Throw
        End Try
        Return count
    End Function

    ''' <summary>
    ''' 把用户配置复制进导出包。
    ''' </summary>
    ''' <param name="StripSecrets">
    ''' True = 保留结构但把密钥值留空（写占位符）。
    ''' </param>
    ''' <returns>实际带走的密钥个数（StripSecrets 时为 0）。</returns>
    Private Function CopyConfigForExport(Instance As DshInstance, HomeDir As String,
                                         StripSecrets As Boolean) As Integer
        Dim count As Integer = 0
        Try
            ' ① 凭据文件
            Dim credSrc As String = Instance.CredentialsFile
            Dim credDst As String = Path.Combine(HomeDir, ".credentials.yaml")
            If File.Exists(credSrc) Then
                If StripSecrets Then
                    ' 保留 refs 段的**键名**，值换成占位符 —— 这样导入后
                    ' 用户能看出"原来配了哪几个接入方式"，只需填值
                    Dim keys As Dictionary(Of String, String) = DshCredentials.ReadRefs(Instance.HomeDir)
                    count = 0
                    Dim sb As New StringBuilder()
                    sb.AppendLine("# 由 PCL_DSH 导出：密钥值已剔除，导入后请填入你自己的 Key。")
                    sb.AppendLine("refs:")
                    For Each kv In keys
                        sb.AppendLine($"  {kv.Key}: ""<在此填入你的 API Key>""")
                    Next
                    File.WriteAllText(credDst, sb.ToString(), New UTF8Encoding(False))
                Else
                    File.Copy(credSrc, credDst, True)
                    Try
                        count = DshCredentials.ReadRefs(Instance.HomeDir).Count
                    Catch ex As Exception
                        Logger.Warn(ex, "DSH：统计导出密钥数失败")
                    End Try
                End If
            End If

            ' ② settings.yaml
            Dim settingsSrc As String = Path.Combine(Instance.HomeDir, "settings.yaml")
            If File.Exists(settingsSrc) Then
                File.Copy(settingsSrc, Path.Combine(HomeDir, "settings.yaml"), True)
            End If

            ' ③ home 级 patch
            Dim patchSrc As String = Path.Combine(Instance.HomeDir, "cordis.patch.yml")
            If File.Exists(patchSrc) Then
                File.Copy(patchSrc, Path.Combine(HomeDir, "cordis.patch.yml"), True)
            End If
        Catch ex As Exception
            Logger.Error(ex, "DSH：复制配置用于导出失败")
            Throw
        End Try
        Return count
    End Function

    ''' <summary>
    ''' 写导出清单。
    ''' </summary>
    ''' <remarks>
    ''' 清单的作用：
    ''' <list type="bullet">
    ''' <item>导入侧读到它就**不用再猜**，直接展示准确清单</item>
    ''' <item><c>formatVersion</c> 让"来自更新版本的包"能给出明确提示，
    '''       而不是报一句看不懂的错</item>
    ''' <item>没有清单的包**也能导入**（回退到扫描识别）—— 向后兼容手搓的包</item>
    ''' </list>
    ''' </remarks>
    Private Sub WriteManifest(Root As String, Instance As DshInstance,
                              Parts As List(Of DshExportPart),
                              StripSecrets As Boolean, SecretCount As Integer)
        Try
            Dim obj As New JObject()
            obj("formatVersion") = DshExportFormatVersion
            obj("generator") = "PCL DSH 改版"
            obj("exportedAt") = DateTime.Now.ToString("o")
            obj("sourceInstance") = Instance.DisplayName

            Try
                obj("dshVersion") = DshRuntime.GetInstalledDshVersion()
            Catch ex As Exception
                Logger.Warn(ex, "DSH：写清单时读取 dsh 版本失败")
            End Try

            Dim arr As New JArray()
            For Each p In Parts
                arr.Add(p.ToString())
            Next
            obj("parts") = arr

            obj("secretsStripped") = StripSecrets
            obj("secretCount") = SecretCount

            Dim manifestPath As String = Path.Combine(Root, DshManifestFileName)
            File.WriteAllText(manifestPath,
                              obj.ToString(Newtonsoft.Json.Formatting.Indented),
                              New UTF8Encoding(False))
        Catch ex As Exception
            ' 清单写失败不该让整个导出失败 —— 导入侧能靠扫描识别
            Logger.Warn(ex, "DSH：写导出清单失败（包仍可用，导入侧会靠扫描识别）")
        End Try
    End Sub

#End Region

#Region "常量"

    ''' <summary>导出包的清单文件名。</summary>
    Public Const DshManifestFileName As String = "pcl-dsh-manifest.json"

    ''' <summary>
    ''' 导出包格式版本。
    ''' </summary>
    ''' <remarks>
    ''' 导入侧读到更高的版本号时，应提示"这个包来自更新版本的 PCL DSH"。
    ''' 改动包结构时**必须**递增它。
    ''' </remarks>
    Public Const DshExportFormatVersion As Integer = 1

#End Region

#Region "进度"

    ''' <summary>
    ''' 把某一阶段内部的进度映射到总进度的一个区间里。
    ''' </summary>
    ''' <param name="Outer">外层的进度回调。</param>
    ''' <param name="StageName">阶段名（显示用）。</param>
    ''' <param name="StartPct">本阶段在总进度里的起点（0~1）。</param>
    ''' <param name="EndPct">本阶段在总进度里的终点（0~1）。</param>
    ''' <remarks>
    ''' ⭐ 为什么需要它：
    ''' 四个导出部分各占总进度的一段，而每部分内部的回调报的是"我这部分完成了多少"
    ''' （比如复制报 0~0.9）。直接透传会让进度条**来回跳** ——
    ''' 用户看到"复制 90%"突然变成"插件 10%"会以为出错了。
    ''' 映射后总进度**单调递增**，符合直觉。
    '''
    ''' 负进度（-1，表示"进行中但不知道百分比"）原样透传 —— 那是给不确定进度的场景用的。
    ''' </remarks>
    Private Function MakeStageProgress(Outer As DshProfileSync.DshSyncProgressHandler,
                                       StageName As String,
                                       StartPct As Double, EndPct As Double) As DshMigrate.DshMigrateProgressHandler
        If Outer Is Nothing Then Return Nothing
        Return Sub(innerStage As String, message As String, innerPct As Double)
                   Try
                       If innerPct < 0 Then
                           ' 不确定进度：原样透传
                           Outer(StageName, message, -1)
                       Else
                           Dim mapped As Double = StartPct + (EndPct - StartPct) * Math.Max(0, Math.Min(1, innerPct))
                           Outer(StageName, message, mapped)
                       End If
                   Catch ex As Exception
                       Logger.Warn(ex, "DSH：导出阶段进度映射出错（已忽略）")
                   End Try
               End Sub
    End Function

    Private Sub Report(Handler As DshProfileSync.DshSyncProgressHandler,
                       Stage As String, Message As String, Progress As Double)
        If Handler Is Nothing Then Return
        Try
            Handler(Stage, Message, Progress)
        Catch ex As Exception
            Logger.Warn(ex, "DSH：导出进度回调出错（已忽略）")
        End Try
    End Sub

#End Region

End Module
