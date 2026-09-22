''' <summary>
''' PCL_DSH：把一个实例的插件配置复制到其他实例。
''' </summary>
''' <remarks>
''' ═══════════════════════════════════════════════════════════════════════
''' 为什么能直接复用「从其他环境导入」
''' ═══════════════════════════════════════════════════════════════════════
''' 实例的数据目录（<c>instances\inst_&lt;id&gt;\</c>）本身就是一个**完整的
''' dsh 环境**（<c>DSH_HOME</c>）—— 有 <c>profiles\</c>、<c>.credentials.yaml</c>、
''' <c>local-plugins\</c>…… 而 <see cref="DshProfileSync.DshSyncBuildPlan"/>
''' 的参数就叫 <c>SourceHome</c>，只要求"一个 dsh 环境目录"。
'''
''' 所以「实例 A 的插件导给实例 B」= 把 A 的数据目录当源环境导入到 B。
''' 这个模块只是**换个说法、换个入口**，底层一行都不用重写 ——
''' 顺带自动继承了同步模块踩过的所有坑的修复：
''' <list type="bullet">
''' <item><c>link:</c> 本地插件要先把目录搬到目标、再改 <c>package.json</c> 的路径</item>
''' <item>profile 的 <c>cordis.patch.yml</c> 要一起搬（approval-gate 的权限预设写在里面）</item>
''' <item>装完要**兜底补齐 bundles 注册项**（否则包在 node_modules 里却不被加载）</item>
''' <item><c>spec</c> 必须复用源 <c>package.json</c> 里的原始值，不要自己拼</item>
''' </list>
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 与「对话历史复制」的分工
''' ═══════════════════════════════════════════════════════════════════════
''' <list type="bullet">
''' <item><b>插件复制</b>（本模块）：搬插件 + API Key + profile 配置。
'''       <c>IncludeData:=False</c> —— **不碰对话历史**</item>
''' <item><b>对话历史复制</b>（<see cref="DshSessionCopy"/>）：只搬会话。
'''       **不碰插件与密钥**</item>
''' </list>
''' 两者刻意分开：搬插件会跑 pnpm（慢、要联网），搬对话是纯文件复制（快、离线）。
''' 混在一起会让用户没法只做想要的那件事。
'''
''' ⚠️ 关于 API Key：这里**会**把源实例的密钥一起带过去（用户往往就是想要这个 ——
''' 新实例配好插件后还得再粘一次 Key 很烦）。界面上必须**明确告知**。
''' </remarks>
Public Module DshPluginCopy

#Region "数据模型"

    ''' <summary>一次插件复制的统计。</summary>
    Public Class DshPluginCopyResult
        Public Property Success As Boolean = False
        Public Property Message As String = ""
        ''' <summary>源实例的插件总数（含被跳过的）。</summary>
        Public Property SourceCount As Integer = 0
        ''' <summary>要安装的常规插件数。</summary>
        Public Property SpecCount As Integer = 0
        ''' <summary>要搬运的本地插件数。</summary>
        Public Property LinkCount As Integer = 0
        ''' <summary>要写入的 API Key 数。</summary>
        Public Property KeyCount As Integer = 0
        ''' <summary>失败明细。</summary>
        Public Property Failures As New List(Of String)()
        ''' <summary>处理过的目标实例数。</summary>
        Public Property TargetCount As Integer = 0
    End Class

    ''' <summary>探测结果（给确认框用）。</summary>
    Public Class DshPluginProbe
        Public Property SourceName As String = ""
        Public Property SourceCount As Integer = 0
        Public Property SpecCount As Integer = 0
        Public Property LinkCount As Integer = 0
        Public Property KeyCount As Integer = 0
        Public Property HasPatch As Boolean = False
        Public Property Warning As String = ""
        Public ReadOnly Property HasAnything As Boolean
            Get
                Return SpecCount > 0 OrElse LinkCount > 0 OrElse KeyCount > 0
            End Get
        End Property
    End Class

#End Region

#Region "探测"

    ''' <summary>
    ''' 先看看能搬什么（只读，不改任何东西）。
    ''' </summary>
    ''' <param name="Source">源实例。</param>
    ''' <remarks>
    ''' 用**源实例自己的 profile 名**去读它 —— 不同实例的 profile 名可能不同
    ''' （虽然默认都是 <c>web</c>）。
    ''' </remarks>
    Public Function DshPluginCopyProbe(Source As DshInstance) As DshPluginProbe
        Dim probe As New DshPluginProbe()
        Try
            If Source Is Nothing Then
                probe.Warning = "没有指定源实例。"
                Return probe
            End If
            probe.SourceName = Source.DisplayName

            Dim profileName As String = If(String.IsNullOrWhiteSpace(Source.Profile),
                                           ModDSH.DshProfileName, Source.Profile)
            ' 目标传源实例自己 —— BuildPlan 只用到 Instance 的 HomeDir 来算"相对路径"，
            ' 这里我们只需要计数，不关心目标
            Dim plan As DshProfileSync.DshSyncPlan =
                DshProfileSync.DshSyncBuildPlan(Source.HomeDir, profileName, Source, IncludeData:=False)

            probe.SourceCount = plan.SourceDepCount
            probe.SpecCount = plan.Specs.Count
            probe.LinkCount = plan.LinkDeps.Count
            probe.KeyCount = plan.ApiKeys.Count
            probe.HasPatch = Not String.IsNullOrWhiteSpace(plan.ProfilePatchContent)
            probe.Warning = If(plan.Warning, "")
            Return probe
        Catch ex As Exception
            Logger.Error(ex, "DSH：探测插件复制内容失败")
            probe.Warning = "读取源实例失败：" & ex.Message
            Return probe
        End Try
    End Function

#End Region

#Region "执行"

    ''' <summary>
    ''' 把 <paramref name="Source"/> 的插件配置复制到 <paramref name="Targets"/>。
    ''' </summary>
    ''' <param name="Source">源实例。</param>
    ''' <param name="Targets">目标实例列表。</param>
    ''' <param name="IncludeKeys">是否一并复制 API Key（默认 True）。</param>
    ''' <param name="Progress">进度回调（阶段, 说明, 0-1）。</param>
    Public Function DshPluginCopyToInstances(Source As DshInstance,
                                             Targets As List(Of DshInstance),
                                             Optional IncludeKeys As Boolean = True,
                                             Optional Progress As DshProfileSync.DshSyncProgressHandler = Nothing) As DshPluginCopyResult
        Dim res As New DshPluginCopyResult()
        Try
            If Source Is Nothing Then
                res.Message = "没有指定源实例。"
                Return res
            End If
            If Targets Is Nothing OrElse Targets.Count = 0 Then
                res.Message = "没有指定目标实例。"
                Return res
            End If

            Dim srcProfile As String = If(String.IsNullOrWhiteSpace(Source.Profile),
                                          ModDSH.DshProfileName, Source.Profile)

            Report(Progress, "准备", $"正在读取「{Source.DisplayName}」的插件清单……", -1)

            ' 逐目标执行。**不复用同一个 Plan** —— Plan 里的 LinkDeps.TargetPath
            ' 会在执行时被写入具体路径，多个目标共用会互相污染。
            For Each target As DshInstance In Targets
                If target Is Nothing Then Continue For
                If String.Equals(target.Id, Source.Id, StringComparison.OrdinalIgnoreCase) Then Continue For

                res.TargetCount += 1
                Try
                    Dim plan As DshProfileSync.DshSyncPlan =
                        DshProfileSync.DshSyncBuildPlan(Source.HomeDir, srcProfile, target, IncludeData:=False)
                    ' 不需要 Key 时清掉，省得往目标写
                    If Not IncludeKeys Then plan.ApiKeys.Clear()

                    res.SourceCount = Math.Max(res.SourceCount, plan.SourceDepCount)
                    res.SpecCount += plan.Specs.Count
                    res.LinkCount += plan.LinkDeps.Count
                    res.KeyCount += plan.ApiKeys.Count

                    If Not plan.HasAnything Then
                        Continue For
                    End If

                    Dim one As DshProfileSync.DshSyncResult =
                        DshProfileSync.DshSyncExecute(plan,
                            Sub(stage As String, message As String, pct As Double)
                                Report(Progress, stage, $"→ {target.DisplayName}：{message}", pct)
                            End Sub)
                    If Not one.Success Then
                        res.Failures.Add($"{target.DisplayName}：{one.Message}")
                    End If
                Catch ex As Exception
                    Logger.Error(ex, $"DSH：向 {target.DisplayName} 复制插件失败")
                    res.Failures.Add($"{target.DisplayName}：{ex.Message}")
                End Try
            Next

            res.Success = res.Failures.Count = 0
            Dim parts As New List(Of String)
            If res.SpecCount > 0 Then parts.Add($"{res.SpecCount} 个插件")
            If res.LinkCount > 0 Then parts.Add($"{res.LinkCount} 个本地插件")
            If res.KeyCount > 0 Then parts.Add($"{res.KeyCount} 个 API Key")

            res.Message = $"已把「{Source.DisplayName}」的插件配置复制到 {res.TargetCount} 个实例" &
                          If(parts.Count > 0, "：" & vbCrLf & "· " & String.Join(vbCrLf & "· ", parts), "。") & vbCrLf &
                          "（未复制对话历史）"
            If res.Failures.Count > 0 Then
                res.Message &= vbCrLf & vbCrLf & "以下项目未能复制：" & vbCrLf & String.Join(vbCrLf, res.Failures)
            End If
            Logger.Info($"DSH：插件复制完成 —— 插件 {res.SpecCount}，本地 {res.LinkCount}，密钥 {res.KeyCount}，失败 {res.Failures.Count}")
        Catch ex As Exception
            Logger.Error(ex, "DSH：复制插件失败")
            res.Success = False
            res.Message = "复制失败：" & ex.Message
        End Try
        Return res
    End Function

#End Region

#Region "进度"

    Private Sub Report(Handler As DshProfileSync.DshSyncProgressHandler,
                       Stage As String, Message As String, Progress As Double)
        If Handler Is Nothing Then Return
        Try
            Handler(Stage, Message, Progress)
        Catch ex As Exception
            Logger.Warn(ex, "DSH：插件复制进度回调出错（已忽略）")
        End Try
    End Sub

#End Region

End Module
