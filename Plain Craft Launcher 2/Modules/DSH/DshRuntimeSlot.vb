Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Text
Imports Newtonsoft.Json.Linq

''' <summary>
''' dsh 运行时「槽位」—— 让多份 dsh 运行时共存，并可随时切换。
'''
''' ## 为什么要这个东西
''' 改造前，运行时是**一个写死的目录**（<c>runtime\dsh\</c>），
''' 由 <c>pnpm add</c> 安装。想换版本只能覆盖，装完新的旧的就没法回去了。
'''
''' 但实际使用中会出现「我有另一套现成的 dsh 环境，想直接用」的需求 ——
''' 例如一个已经打包好的便携版（自带 node + 一份扁平 node_modules）。
''' 那种包**不是** pnpm 装的，塞进 <c>runtime\dsh\</c> 会和 pnpm 的
''' 虚拟存储布局打架，所以必须让它独立占一个目录。
'''
''' ## 槽位模型
''' <code>
''' runtime\
'''     node\                     私有 Node（可选）
'''     pnpm\                     私有 pnpm
'''     dsh\                      槽位 "npm" —— pnpm add 的落点
'''     imported\imp_xxxxxxxx\    槽位 "imp_xxxxxxxx" —— 导入的包
'''     runtimes.json             清单：只记导入槽位 + 当前激活的 id
''' </code>
'''
''' ⭐ **切换 = 改 <c>runtimes.json</c> 的 <c>active</c> 一个字段**。
''' 入口脚本、版本号、启动用的 Node、体检报告全部从「激活槽位」推导，
''' 所以不需要在别处补任何改动。
'''
''' ## 为什么 npm 槽位不进清单
''' 它是隐式存在、永远有的（装没装 dsh 都能算一个槽位），
''' 而且版本号要**实时读磁盘**（用户可能刚重装过）。写进清单只会多一份
''' 会和现实脱节的副本，所以干脆不存 —— 见 <see cref="DshSlotList"/>。
''' </summary>
Public Module DshRuntimeSlot

#Region "常量"

    ''' <summary>槽位类型：pnpm 安装的官方槽位。</summary>
    Public Const DshSlotKindNpm As String = "npm"

    ''' <summary>槽位类型：导入的本地包。</summary>
    Public Const DshSlotKindImported As String = "imported"

    ''' <summary>npm 槽位的固定 id。</summary>
    Public Const DshSlotNpmId As String = "npm"

    ''' <summary>dsh 入口相对于「dsh 基准目录」的路径。</summary>
    ''' <remarks>
    ''' ⚠️ 必须和 <c>DshRuntime.DshEntryRelative</c> 保持一致 ——
    ''' 那边是「相对于安装目录」，这里也是同样的相对结构，
    ''' 因为导入包里 <c>app\</c> 下面就是这个形状。
    ''' </remarks>
    Private ReadOnly DshSlotEntryRel As String =
        Path.Combine("node_modules", "@deepseek-ai", "dsh", "lib", "bin.js")

    ''' <summary>dsh 的 package.json 相对路径。</summary>
    Private ReadOnly DshSlotPkgRel As String =
        Path.Combine("node_modules", "@deepseek-ai", "dsh", "package.json")

    ''' <summary>dsh 的 npm 包名。</summary>
    Private Const DshSlotPackageName As String = "@deepseek-ai/dsh"

    ''' <summary>
    ''' 导入时跳过的**顶层**目录名。
    ''' </summary>
    ''' <remarks>
    ''' 这些是打包工具与编辑器的工作目录，对「跑 dsh」毫无用处。
    ''' 用户实测的包里 <c>build-tools\</c> 有 6.3 MB。
    ''' </remarks>
    Private ReadOnly DshSlotSkipDirs As String() = {"build-tools", ".workbuddy-ai"}

    ''' <summary>
    ''' 导入时跳过的**顶层**文件扩展名。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 只作用于顶层，不会碰 <c>node\</c> 或 <c>app\</c> 里的东西。
    ''' 目的是丢掉便携包自带的 SEA 启动器（实测 92 MB 的 <c>.exe</c>）、
    ''' 批处理脚本、图标、以及一堆调试用的 <c>.txt</c> / <c>.log</c>。
    ''' 这些对 PCL 来说全是死重量。
    ''' </remarks>
    Private ReadOnly DshSlotSkipExts As String() = {".exe", ".bat", ".cmd", ".ps1", ".ico", ".log", ".txt", ".bak"}

    ''' <summary>BFS 搜索时最多访问多少个目录（防止在大目录里失控）。</summary>
    Private Const DshSlotMaxDirsVisited As Integer = 400

    ''' <summary>BFS 搜索的最大深度。</summary>
    Private Const DshSlotMaxDepth As Integer = 3

    ''' <summary>
    ''' 反斜杠字符。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ **绝不能用「双引号 + 反斜杠 + 双引号 + c」那种字符字面量写法**。
    ''' VB 没有转义字符，双引号反斜杠双引号本身就已经是一个**完整的字符串字面量**，
    ''' 后面跟的 <c>c</c> 会被当成一个标识符，于是编译器报一堆莫名其妙的
    ''' BC30035 / BC32017 / BC30648，而且报错位置会飘到很远的地方
    ''' （连 <c>End Module</c> 都会跟着报错）。
    ''' 这个坑在项目里已经踩过多次，统一用 <c>ChrW(92)</c> 表达。
    ''' </remarks>
    Private ReadOnly DshSlotBS As Char = ChrW(92)

#End Region

#Region "进度上报"

    ''' <summary>导入进度回调。</summary>
    ''' <param name="Stage">阶段名（用于界面上的小标题）。</param>
    ''' <param name="Message">人类可读的描述。</param>
    ''' <param name="Progress">0-1；未知时为 -1。</param>
    Public Delegate Sub DshSlotProgressHandler(Stage As String, Message As String, Progress As Double)

    Private Sub Report(Handler As DshSlotProgressHandler, Stage As String, Message As String, Progress As Double)
        If Handler Is Nothing Then Return
        Try
            Handler(Stage, Message, Progress)
        Catch ex As Exception
            Logger.Warn(ex, "DSH：槽位导入进度回调出错（已忽略）")
        End Try
    End Sub

#End Region

#Region "槽位信息"

    ''' <summary>一个运行时槽位。</summary>
    Public Class DshSlotInfo
        ''' <summary>槽位 id（<c>npm</c> 或 <c>imp_xxxxxxxx</c>）。</summary>
        Public Property Id As String
        ''' <summary><c>npm</c> 或 <c>imported</c>。</summary>
        Public Property Kind As String
        ''' <summary>dsh 版本号。</summary>
        Public Property Version As String
        ''' <summary>入口脚本相对于槽位目录的路径。</summary>
        Public Property EntryRelative As String
        ''' <summary>自带 node.exe 相对于槽位目录的路径；没有则为 Nothing。</summary>
        Public Property NodeRelative As String
        ''' <summary>自带 Node 的版本号。</summary>
        Public Property NodeVersion As String
        ''' <summary>导入时间。</summary>
        Public Property ImportedAt As DateTime?
        ''' <summary>导入时的来源名（原目录名或 zip 文件名）。</summary>
        Public Property SourceName As String
        ''' <summary>导入时跳过的字节数（给用户一个「省了多少」的交代）。</summary>
        Public Property SkippedBytes As Long

        ''' <summary>槽位目录的绝对路径。</summary>
        Public ReadOnly Property Dir As String
            Get
                Return DshSlotDirOf(Id)
            End Get
        End Property

        ''' <summary>是否是 pnpm 安装的官方槽位。</summary>
        Public ReadOnly Property IsNpm As Boolean
            Get
                Return String.Equals(Kind, DshSlotKindNpm, StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        ''' <summary>是否是导入的本地槽位。</summary>
        Public ReadOnly Property IsImported As Boolean
            Get
                Return Not IsNpm
            End Get
        End Property

        ''' <summary>是否自带 Node。</summary>
        Public ReadOnly Property HasOwnNode As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(NodeRelative)
            End Get
        End Property

        ''' <summary>入口脚本的绝对路径。</summary>
        Public ReadOnly Property EntryPath As String
            Get
                If String.IsNullOrWhiteSpace(EntryRelative) Then Return Nothing
                Return Path.Combine(Dir, EntryRelative)
            End Get
        End Property

        ''' <summary>自带 node.exe 的绝对路径；没有则为 Nothing。</summary>
        Public ReadOnly Property NodeExePath As String
            Get
                If Not HasOwnNode Then Return Nothing
                Return Path.Combine(Dir, NodeRelative)
            End Get
        End Property

        ''' <summary>入口脚本是否真的存在（用来区分「装了」和「只是个空壳」）。</summary>
        Public ReadOnly Property IsUsable As Boolean
            Get
                Dim p As String = EntryPath
                If String.IsNullOrWhiteSpace(p) Then Return False
                Try
                    Return File.Exists(p)
                Catch
                    Return False
                End Try
            End Get
        End Property

        ''' <summary>界面上显示的名字。</summary>
        Public ReadOnly Property DisplayName As String
            Get
                Dim ver As String = If(String.IsNullOrWhiteSpace(Version), "未知版本", Version)
                If IsNpm Then Return $"官方安装 · {ver}"
                Return $"本地导入 · {ver}"
            End Get
        End Property

        ''' <summary>一行副标题，说明这个槽位的来路。</summary>
        Public ReadOnly Property DetailText As String
            Get
                Dim parts As New List(Of String)
                If IsNpm Then
                    parts.Add("由 pnpm 从 npm 镜像安装")
                Else
                    If Not String.IsNullOrWhiteSpace(SourceName) Then parts.Add($"来源：{SourceName}")
                    If ImportedAt.HasValue Then parts.Add($"导入于 {ImportedAt.Value:yyyy-MM-dd HH:mm}")
                End If
                If HasOwnNode Then
                    parts.Add($"自带 Node {If(String.IsNullOrWhiteSpace(NodeVersion), "未知版本", NodeVersion)}")
                Else
                    parts.Add("使用外部 Node")
                End If
                Return String.Join(" · ", parts)
            End Get
        End Property
    End Class

    ''' <summary>槽位目录：npm 槽位是固定目录，导入槽位在 <c>imported\</c> 下各占一个。</summary>
    Private Function DshSlotDirOf(SlotId As String) As String
        If String.IsNullOrWhiteSpace(SlotId) Then Return Nothing
        If String.Equals(SlotId, DshSlotNpmId, StringComparison.OrdinalIgnoreCase) Then
            Return ModDSH.DshInstallDir
        End If
        Return ModDSH.DshImportedDir & SlotId & "\"
    End Function

#End Region

#Region "清单读写"

    ''' <summary>
    ''' 读取全部槽位。
    ''' </summary>
    ''' <returns>第一项恒为 npm 槽位，其后是导入槽位。</returns>
    ''' <remarks>
    ''' npm 槽位的版本号是**实时读磁盘**的，不存清单 ——
    ''' 否则用户重装一次 dsh，清单里就留了个过期版本号。
    ''' </remarks>
    Public Function DshSlotList() As List(Of DshSlotInfo)
        Dim result As New List(Of DshSlotInfo)

        ' ── ① npm 槽位：永远排第一，永远存在 ──
        Dim npmSlot As New DshSlotInfo With {
            .Id = DshSlotNpmId,
            .Kind = DshSlotKindNpm,
            .EntryRelative = DshSlotEntryRel
        }
        Try
            npmSlot.Version = DshRuntime.GetNpmSlotVersion()
        Catch ex As Exception
            Logger.Warn(ex, "DSH：读取 npm 槽位版本失败")
        End Try
        result.Add(npmSlot)

        ' ── ② 导入槽位：从清单读 ──
        result.AddRange(DshSlotReadManifest())

        ' 只有「入口真的存在」的导入槽位才露出来 ——
        ' 目录被用户手动删掉时，不该还在界面上占一行
        Return result.Where(Function(s) s.IsNpm OrElse s.IsUsable).ToList()
    End Function

    ''' <summary>读取清单里的导入槽位（不做可用性过滤）。</summary>
    Private Function DshSlotReadManifest() As List(Of DshSlotInfo)
        Dim result As New List(Of DshSlotInfo)
        Dim manifest As String = ModDSH.DshSlotsFile
        Try
            If Not File.Exists(manifest) Then Return result
            Dim root As JObject = JObject.Parse(File.ReadAllText(manifest, Encoding.UTF8))
            Dim arr = TryCast(root("slots"), JArray)
            If arr Is Nothing Then Return result

            For Each token As JToken In arr
                Dim obj = TryCast(token, JObject)
                If obj Is Nothing Then Continue For
                Dim info As New DshSlotInfo With {
                    .Id = DshSlotTextOf(obj, "id"),
                    .Kind = DshSlotKindImported,
                    .Version = DshSlotTextOf(obj, "version"),
                    .EntryRelative = DshSlotTextOf(obj, "entryRelative"),
                    .NodeRelative = DshSlotTextOf(obj, "nodeRelative"),
                    .NodeVersion = DshSlotTextOf(obj, "nodeVersion"),
                    .SourceName = DshSlotTextOf(obj, "sourceName"),
                    .SkippedBytes = DshSlotLongOf(obj, "skippedBytes")
                }
                Dim at As String = DshSlotTextOf(obj, "importedAt")
                If Not String.IsNullOrWhiteSpace(at) Then
                    Dim parsed As DateTime
                    If DateTime.TryParse(at, parsed) Then info.ImportedAt = parsed
                End If
                If String.IsNullOrWhiteSpace(info.EntryRelative) Then info.EntryRelative = DshSlotEntryRel
                If Not String.IsNullOrWhiteSpace(info.Id) Then result.Add(info)
            Next
        Catch ex As Exception
            Logger.Error(ex, "DSH：读取运行时槽位清单失败")
        End Try
        Return result
    End Function

    ''' <summary>把导入槽位写回清单（npm 槽位不写）。</summary>
    Private Sub DshSlotWriteManifest(Slots As List(Of DshSlotInfo))
        Try
            ModDSH.DshEnsureDirectories()
            Dim arr As New JArray()
            For Each s In Slots
                If s.IsNpm Then Continue For
                Dim obj As New JObject()
                obj("id") = s.Id
                obj("version") = s.Version
                obj("entryRelative") = s.EntryRelative
                If s.HasOwnNode Then obj("nodeRelative") = s.NodeRelative
                If Not String.IsNullOrWhiteSpace(s.NodeVersion) Then obj("nodeVersion") = s.NodeVersion
                If Not String.IsNullOrWhiteSpace(s.SourceName) Then obj("sourceName") = s.SourceName
                If s.ImportedAt.HasValue Then obj("importedAt") = s.ImportedAt.Value.ToString("o")
                If s.SkippedBytes > 0 Then obj("skippedBytes") = s.SkippedBytes
                arr.Add(obj)
            Next

            Dim root As New JObject()
            root("version") = 1
            root("active") = DshSlotActiveId()
            root("slots") = arr

            Dim target As String = ModDSH.DshSlotsFile
            Dim temp As String = DshMigrate.DshAtomicTempPath(target)
            File.WriteAllText(temp, root.ToString(Newtonsoft.Json.Formatting.Indented), New UTF8Encoding(False))
            If File.Exists(target) Then File.Delete(target)
            File.Move(temp, target)
        Catch ex As Exception
            Logger.Error(ex, "DSH：写入运行时槽位清单失败")
        End Try
    End Sub

    Private Function DshSlotTextOf(obj As JObject, Key As String) As String
        Dim t = obj(Key)
        If t Is Nothing Then Return Nothing
        Dim s As String = t.ToString()
        If String.IsNullOrWhiteSpace(s) Then Return Nothing
        Return s.Trim()
    End Function

    Private Function DshSlotLongOf(obj As JObject, Key As String) As Long
        Dim t = obj(Key)
        If t Is Nothing Then Return 0
        Try
            Return t.Value(Of Long)()
        Catch
            Return 0
        End Try
    End Function

#End Region

#Region "激活槽位"

    ''' <summary>
    ''' 当前激活的槽位 id。
    ''' </summary>
    ''' <remarks>
    ''' 读不到、或者指向一个已经不存在/不可用的槽位时，**静默回落到 npm 槽位**。
    ''' 这一点很重要：导入的目录被用户手动删掉后，程序不能因为
    ''' 「激活槽位找不到」而整个起不来 —— 回落到官方槽位至少还能跑。
    ''' </remarks>
    Public Function DshSlotActiveId() As String
        Dim wanted As String = Nothing
        Try
            Dim manifest As String = ModDSH.DshSlotsFile
            If File.Exists(manifest) Then
                Dim root As JObject = JObject.Parse(File.ReadAllText(manifest, Encoding.UTF8))
                wanted = DshSlotTextOf(root, "active")
            End If
        Catch ex As Exception
            Logger.Warn(ex, "DSH：读取激活槽位失败，按官方槽位处理")
        End Try

        If String.IsNullOrWhiteSpace(wanted) Then Return DshSlotNpmId
        If String.Equals(wanted, DshSlotNpmId, StringComparison.OrdinalIgnoreCase) Then Return DshSlotNpmId

        ' 校验它是否真的还在
        Dim found As DshSlotInfo = DshSlotReadManifest().FirstOrDefault(
            Function(s) String.Equals(s.Id, wanted, StringComparison.OrdinalIgnoreCase) AndAlso s.IsUsable)
        If found Is Nothing Then
            Logger.Warn($"DSH：激活槽位 {wanted} 已不存在或不可用，回落到官方槽位")
            Return DshSlotNpmId
        End If
        Return found.Id
    End Function

    ''' <summary>当前激活的槽位；任何异常情况下返回 npm 槽位（不会是 Nothing）。</summary>
    Public Function DshSlotActive() As DshSlotInfo
        Dim id As String = DshSlotActiveId()
        Dim all As List(Of DshSlotInfo) = Nothing
        Try
            all = DshSlotList()
        Catch ex As Exception
            Logger.Warn(ex, "DSH：枚举槽位失败")
        End Try

        If all IsNot Nothing Then
            Dim hit As DshSlotInfo = all.FirstOrDefault(
                Function(s) String.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
            If hit IsNot Nothing Then Return hit
        End If

        ' 连枚举都失败了 —— 至少造一个能用的 npm 槽位出来
        Return New DshSlotInfo With {
            .Id = DshSlotNpmId,
            .Kind = DshSlotKindNpm,
            .EntryRelative = DshSlotEntryRel
        }
    End Function

    ''' <summary>
    ''' 切换激活槽位。
    ''' </summary>
    ''' <returns>成功返回 Nothing；失败返回给用户看的原因。</returns>
    Public Function DshSlotSetActive(SlotId As String) As String
        If String.IsNullOrWhiteSpace(SlotId) Then Return "没有指定要切换到的槽位。"

        Dim target As DshSlotInfo = DshSlotList().FirstOrDefault(
            Function(s) String.Equals(s.Id, SlotId, StringComparison.OrdinalIgnoreCase))
        If target Is Nothing Then Return "找不到这个运行时槽位。"
        If Not target.IsUsable Then
            Return "这个槽位里没有找到 dsh 入口脚本（lib\bin.js），可能已被删除或不完整。"
        End If

        Try
            ModDSH.DshEnsureDirectories()
            Dim arr As New JArray()
            For Each s In DshSlotList()
                If s.IsNpm Then Continue For
                Dim obj As New JObject()
                obj("id") = s.Id
                obj("version") = s.Version
                obj("entryRelative") = s.EntryRelative
                If s.HasOwnNode Then obj("nodeRelative") = s.NodeRelative
                If Not String.IsNullOrWhiteSpace(s.NodeVersion) Then obj("nodeVersion") = s.NodeVersion
                If Not String.IsNullOrWhiteSpace(s.SourceName) Then obj("sourceName") = s.SourceName
                If s.ImportedAt.HasValue Then obj("importedAt") = s.ImportedAt.Value.ToString("o")
                If s.SkippedBytes > 0 Then obj("skippedBytes") = s.SkippedBytes
                arr.Add(obj)
            Next

            Dim root As New JObject()
            root("version") = 1
            root("active") = target.Id
            root("slots") = arr

            ' ⚠️ 局部变量名不能叫 file —— 会遮蔽 System.IO.File（VB 大小写不敏感），
            '   于是 File.WriteAllText 会被解析成「String 的成员」而报 BC30456。
            '   这是项目里已经踩过的同类坑（path / dir / step 都是这个家族）。
            Dim manifestPath As String = ModDSH.DshSlotsFile
            Dim temp As String = DshMigrate.DshAtomicTempPath(manifestPath)
            File.WriteAllText(temp, root.ToString(Newtonsoft.Json.Formatting.Indented), New UTF8Encoding(False))
            If File.Exists(manifestPath) Then File.Delete(manifestPath)
            File.Move(temp, manifestPath)

            Logger.Info($"DSH：运行时槽位已切换到 {target.Id}（{target.DisplayName}）")
            Return Nothing
        Catch ex As Exception
            Logger.Error(ex, "DSH：切换运行时槽位失败")
            Return $"切换失败：{ex.Message}"
        End Try
    End Function

#End Region

#Region "探测"

    ''' <summary>一次导入前的探测结果。</summary>
    Public Class DshSlotPreview
        ''' <summary>是否通过校验。</summary>
        Public Property Ok As Boolean
        ''' <summary>不通过的原因（<see cref="Ok"/> 为 True 时是 Nothing）。</summary>
        Public Property FailReason As String
        ''' <summary><c>zip</c> 或 <c>folder</c>。</summary>
        Public Property SourceKind As String
        ''' <summary>来源路径。</summary>
        Public Property SourcePath As String
        ''' <summary>dsh 版本号。</summary>
        Public Property Version As String
        ''' <summary>dsh 基准目录相对于来源根的路径（可能是空串）。</summary>
        Public Property DshBaseRelative As String
        ''' <summary>入口脚本相对于来源根的路径。</summary>
        Public Property EntryRelative As String
        ''' <summary>自带 node 相对于来源根的路径；没有则为 Nothing。</summary>
        Public Property NodeRelative As String
        ''' <summary>自带 Node 版本。</summary>
        Public Property NodeVersion As String
        ''' <summary>自带 Node 是否满足 dsh 的版本要求。</summary>
        Public Property NodeSatisfies As Boolean
        ''' <summary>可以放行的提醒（例如 Node 版本不满足）。</summary>
        Public Property Warning As String

        ''' <summary>给用户看的一行摘要。</summary>
        Public ReadOnly Property Summary As String
            Get
                Dim parts As New List(Of String)
                parts.Add($"dsh {If(String.IsNullOrWhiteSpace(Version), "未知版本", Version)}")
                If Not String.IsNullOrWhiteSpace(NodeRelative) Then
                    parts.Add($"自带 Node {If(String.IsNullOrWhiteSpace(NodeVersion), "未知版本", NodeVersion)}")
                Else
                    parts.Add("不含 Node，将使用 PCL 的 Node")
                End If
                Return String.Join(" · ", parts)
            End Get
        End Property
    End Class

    ''' <summary>
    ''' 探测一个**文件夹**是不是 dsh 运行时包。
    ''' </summary>
    ''' <param name="SourceDir">待探测的目录。</param>
    ''' <remarks>
    ''' 只读目录结构，**不复制任何文件** —— 所以可以放心地在
    ''' 「先探测给用户看、确认了再导入」的流程里当第一步。
    ''' </remarks>
    Public Function DshSlotPreviewFolder(SourceDir As String) As DshSlotPreview
        Dim result As New DshSlotPreview With {
            .SourceKind = "folder",
            .SourcePath = SourceDir,
            .NodeSatisfies = True
        }

        If String.IsNullOrWhiteSpace(SourceDir) OrElse Not Directory.Exists(SourceDir) Then
            result.FailReason = "目录不存在。"
            Return result
        End If

        Dim baseDir As String = DshSlotFindDshBase(SourceDir)
        If String.IsNullOrWhiteSpace(baseDir) Then
            result.FailReason = "没有找到 dsh 入口脚本（node_modules\@deepseek-ai\dsh\lib\bin.js）。" & vbCrLf &
                                "请确认这个目录里装的是 DeepSeek Harness 运行时。"
            Return result
        End If

        Dim pkgPath As String = Path.Combine(baseDir, DshSlotPkgRel)
        If Not File.Exists(pkgPath) Then
            result.FailReason = "找到了入口脚本，但缺少 package.json：" & vbCrLf & pkgPath
            Return result
        End If

        Dim pkg As JObject = Nothing
        Try
            pkg = JObject.Parse(File.ReadAllText(pkgPath, Encoding.UTF8))
        Catch ex As Exception
            result.FailReason = "package.json 解析失败：" & ex.Message
            Return result
        End Try

        Dim pkgName As String = If(pkg("name")?.ToString(), "").Trim()
        If Not String.Equals(pkgName, DshSlotPackageName, StringComparison.OrdinalIgnoreCase) Then
            result.FailReason = $"包名不匹配：期望 {DshSlotPackageName}，实际是「{pkgName}」。" & vbCrLf &
                                "这可能是一个普通 Node 项目，不是 dsh 运行时。"
            Return result
        End If

        Dim ver As String = If(pkg("version")?.ToString(), "").Trim()
        If String.IsNullOrWhiteSpace(ver) Then
            result.FailReason = "package.json 里没有 version 字段，无法确定版本。"
            Return result
        End If

        result.Version = ver
        result.DshBaseRelative = DshSlotRelativeOf(SourceDir, baseDir)
        result.EntryRelative = If(result.DshBaseRelative = "", DshSlotEntryRel,
                                  Path.Combine(result.DshBaseRelative, DshSlotEntryRel))

        ' ── 找自带 Node（可选）──
        Dim nodeExe As String = DshSlotFindNode(SourceDir, baseDir)
        If Not String.IsNullOrWhiteSpace(nodeExe) Then
            result.NodeRelative = DshSlotRelativeOf(SourceDir, nodeExe)
            Dim nodeVer As String = DshRuntime.ReadNodeVersionVerbose(nodeExe)
            result.NodeVersion = nodeVer
            Dim ok As Boolean = DshRuntime.NodeVersionSatisfies(DshRuntime.ParseNodeVersion(nodeVer))
            result.NodeSatisfies = ok
            If Not ok Then
                result.Warning = $"包自带的 Node 版本是 {If(String.IsNullOrWhiteSpace(nodeVer), "未知", nodeVer)}，" &
                                 "不满足 dsh 的要求（需要 22.19 以上或 24.0 以上）。" & vbCrLf &
                                 "仍然可以导入，但启动时可能会失败 —— 建议改用 PCL 的 Node。"
            End If
        Else
            result.Warning = "这个包里没有找到 node.exe，导入后将使用 PCL 自己的 Node。"
        End If

        result.Ok = True
        Return result
    End Function

    ''' <summary>
    ''' 轻量探测一个 **zip** 是不是 dsh 运行时包。
    ''' </summary>
    ''' <remarks>
    ''' 只看 zip 的中央目录（条目名），**不解压** —— 对 200 MB 的包也是秒回。
    ''' 所以适合在「确认框里先告诉用户这是什么版本」时用。
    ''' 真正导入后还会再用 <see cref="DshSlotPreviewFolder"/> 复核一遍。
    ''' </remarks>
    Public Function DshSlotPeekZip(ZipPath As String) As DshSlotPreview
        Dim result As New DshSlotPreview With {
            .SourceKind = "zip",
            .SourcePath = ZipPath,
            .NodeSatisfies = True
        }

        If String.IsNullOrWhiteSpace(ZipPath) OrElse Not File.Exists(ZipPath) Then
            result.FailReason = "文件不存在。"
            Return result
        End If

        Try
            Using archive = ZipFile.Open(ZipPath, ZipArchiveMode.Read, Encoding.UTF8)
                Dim entryName As String = Nothing
                Dim nodeName As String = Nothing
                For Each entry In archive.Entries
                    Dim norm As String = entry.FullName.Replace("/"c, DshSlotBS)
                    If entryName Is Nothing AndAlso norm.EndsWith(DshSlotEntryRel, StringComparison.OrdinalIgnoreCase) Then
                        entryName = norm
                    End If
                    If nodeName Is Nothing AndAlso
                       norm.EndsWith("node\node.exe", StringComparison.OrdinalIgnoreCase) Then
                        nodeName = norm
                    End If
                    If entryName IsNot Nothing AndAlso nodeName IsNot Nothing Then Exit For
                Next

                If entryName Is Nothing Then
                    result.FailReason = "压缩包里没有 dsh 入口脚本（node_modules\@deepseek-ai\dsh\lib\bin.js）。"
                    Return result
                End If

                ' 从 zip 里直接读 package.json，拿到版本号
                Dim pkgEntry As ZipArchiveEntry = Nothing
                Dim pkgSuffix As String = DshSlotPkgRel
                For Each entry In archive.Entries
                    Dim norm As String = entry.FullName.Replace("/"c, DshSlotBS)
                    If norm.EndsWith(pkgSuffix, StringComparison.OrdinalIgnoreCase) Then
                        pkgEntry = entry
                        Exit For
                    End If
                Next

                If pkgEntry Is Nothing Then
                    result.FailReason = "压缩包里有入口脚本，但缺少 dsh 的 package.json。"
                    Return result
                End If

                Using reader As New StreamReader(pkgEntry.Open(), Encoding.UTF8)
                    Dim pkg As JObject = JObject.Parse(reader.ReadToEnd())
                    Dim pkgName As String = If(pkg("name")?.ToString(), "").Trim()
                    If Not String.Equals(pkgName, DshSlotPackageName, StringComparison.OrdinalIgnoreCase) Then
                        result.FailReason = $"包名不匹配：期望 {DshSlotPackageName}，实际是「{pkgName}」。"
                        Return result
                    End If
                    result.Version = If(pkg("version")?.ToString(), "").Trim()
                End Using

                result.EntryRelative = entryName
                If nodeName IsNot Nothing Then result.NodeRelative = nodeName

                If nodeName Is Nothing Then
                    result.Warning = "这个包里没有找到 node.exe，导入后将使用 PCL 自己的 Node。"
                Else
                    result.Warning = "包自带 Node，导入后会优先使用它（更贴近原环境）。"
                End If

                result.Ok = True
            End Using
        Catch ex As Exception
            Logger.Error(ex, $"DSH：探测 zip 失败：{ZipPath}")
            result.FailReason = "读取压缩包失败：" & ex.Message
        End Try

        Return result
    End Function

    ''' <summary>
    ''' 快速判断一个 zip 是不是 dsh 运行时包（给拖拽用）。
    ''' </summary>
    ''' <remarks>
    ''' 拖拽进来时**必须先做这个判断**，否则 zip 会被 MC 的整合包安装逻辑先接走。
    ''' 只看条目名，不解压。
    ''' </remarks>
    Public Function DshSlotLooksLikeRuntimeZip(ZipPath As String) As Boolean
        Try
            If String.IsNullOrWhiteSpace(ZipPath) OrElse Not File.Exists(ZipPath) Then Return False
            If Not ZipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) Then Return False
            Using archive = ZipFile.Open(ZipPath, ZipArchiveMode.Read, Encoding.UTF8)
                For Each entry In archive.Entries
                    Dim norm As String = entry.FullName.Replace("/"c, DshSlotBS)
                    If norm.EndsWith(DshSlotEntryRel, StringComparison.OrdinalIgnoreCase) Then Return True
                Next
            End Using
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：判断 {ZipPath} 是否为运行时包时出错（按否处理）")
        End Try
        Return False
    End Function

    ''' <summary>
    ''' 估算一个待导入目录的体积（给确认框显示用）。
    ''' </summary>
    ''' <remarks>
    ''' 刻意不含任何跳过规则 —— 用户想看到的是「这个东西有多大」，
    ''' 至于最终会省下多少，导入完成后会用实际数字告诉他。
    ''' </remarks>
    Public Function DshSlotMeasureFolder(SourceDir As String) As Long
        If String.IsNullOrWhiteSpace(SourceDir) OrElse Not Directory.Exists(SourceDir) Then Return 0
        Return DshSlotMeasureTree(SourceDir)
    End Function

#End Region

#Region "导入"

    ''' <summary>一次导入的结果。</summary>
    Public Class DshSlotImportResult
        ''' <summary>是否成功。</summary>
        Public Property Success As Boolean
        ''' <summary>失败原因或成功提示。</summary>
        Public Property Message As String
        ''' <summary>导入后的槽位信息。</summary>
        Public Property Slot As DshSlotInfo
        ''' <summary>实际复制的文件数。</summary>
        Public Property FilesCopied As Integer
        ''' <summary>实际复制的字节数。</summary>
        Public Property BytesCopied As Long
        ''' <summary>被跳过的字节数。</summary>
        Public Property SkippedBytes As Long
    End Class

    ''' <summary>
    ''' 从一个文件夹导入运行时。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ **复制而不是移动** —— 用户的原目录必须原样保留。
    ''' 所以磁盘占用会翻倍，这是刻意的取舍（安全 &gt; 省空间）。
    ''' </remarks>
    Public Function DshSlotImportFromFolder(SourceDir As String,
                                            Optional Progress As DshSlotProgressHandler = Nothing) As DshSlotImportResult
        Dim preview As DshSlotPreview = DshSlotPreviewFolder(SourceDir)
        If Not preview.Ok Then
            Return New DshSlotImportResult With {.Success = False, .Message = preview.FailReason}
        End If
        Return DshSlotImportCore(preview, Progress)
    End Function

    ''' <summary>从一个 zip 导入运行时。</summary>
    Public Function DshSlotImportFromZip(ZipPath As String,
                                         Optional Progress As DshSlotProgressHandler = Nothing) As DshSlotImportResult
        Dim preview As DshSlotPreview = DshSlotPeekZip(ZipPath)
        If Not preview.Ok Then
            Return New DshSlotImportResult With {.Success = False, .Message = preview.FailReason}
        End If
        Return DshSlotImportCore(preview, Progress)
    End Function

    ''' <summary>导入的公共实现：落地到暂存区 → 复核 → 移入正式位置 → 写清单。</summary>
    ''' <remarks>
    ''' 刻意走「先落到暂存区、复核通过再挪进正式目录」两步。
    ''' 直接往 <c>imported\imp_xxx\</c> 里写的话，中途失败（磁盘满、文件被占）
    ''' 会留下一个半成品目录，下次启动时它会被当成一个「可用槽位」露出来。
    ''' </remarks>
    Private Function DshSlotImportCore(Preview As DshSlotPreview,
                                       Progress As DshSlotProgressHandler) As DshSlotImportResult
        Dim staging As String = ModDSH.DshRuntimeDir & ".import-staging\"
        Dim finalDir As String = Nothing
        Try
            ModDSH.DshEnsureDirectories()

            ' ── 清理上次失败留下的暂存区 ──
            Report(Progress, "准备", "正在准备导入……", -1)
            DshSlotDeleteTreeSafe(staging)
            Directory.CreateDirectory(staging)

            ' ── ① 落地到暂存区 ──
            Dim stats As New DshSlotCopyStats()
            If Preview.SourceKind = "zip" Then
                DshSlotExtractZipFiltered(Preview.SourcePath, staging, stats, Progress)
            Else
                DshSlotCopyTreeFiltered(Preview.SourcePath, staging, stats, Progress)
            End If

            ' ── ② 复核：暂存区里到底长什么样 ──
            Report(Progress, "校验", "正在复核导入的内容……", 0.9)
            Dim verified As DshSlotPreview = DshSlotPreviewFolder(staging)
            If Not verified.Ok Then
                DshSlotDeleteTreeSafe(staging)
                Return New DshSlotImportResult With {
                    .Success = False,
                    .Message = "导入的内容没有通过校验：" & vbCrLf & verified.FailReason
                }
            End If

            ' ── ③ 移入正式位置 ──
            Report(Progress, "落地", "正在放入运行时目录……", 0.95)
            Dim newId As String = "imp_" & Guid.NewGuid().ToString("N").Substring(0, 8)
            finalDir = ModDSH.DshImportedDir & newId & "\"
            If Not Directory.Exists(ModDSH.DshImportedDir) Then Directory.CreateDirectory(ModDSH.DshImportedDir)

            Try
                Directory.Move(staging, finalDir)
            Catch
                ' 跨盘或目标已存在时 Move 会失败 —— 退回复制
                DshSlotCopyTreeRaw(staging, finalDir)
                DshSlotDeleteTreeSafe(staging)
            End Try

            ' ── ④ 写清单 ──
            Dim info As New DshSlotInfo With {
                .Id = newId,
                .Kind = DshSlotKindImported,
                .Version = verified.Version,
                .EntryRelative = verified.EntryRelative,
                .NodeRelative = verified.NodeRelative,
                .NodeVersion = verified.NodeVersion,
                .ImportedAt = DateTime.Now,
                .SourceName = DshSlotSourceNameOf(Preview),
                .SkippedBytes = stats.SkippedBytes
            }

            Dim all As List(Of DshSlotInfo) = DshSlotReadManifest()
            all.Add(info)
            DshSlotWriteManifest(all)

            Logger.Info($"DSH：已导入运行时槽位 {newId}（{info.DisplayName}），" &
                        $"复制 {stats.FilesCopied} 个文件 / {DshDoctor.FormatBytes(stats.BytesCopied)}，" &
                        $"跳过 {DshDoctor.FormatBytes(stats.SkippedBytes)}")

            Report(Progress, "完成", "导入完成", 1)
            Return New DshSlotImportResult With {
                .Success = True,
                .Message = $"已导入 {info.DisplayName}",
                .Slot = info,
                .FilesCopied = stats.FilesCopied,
                .BytesCopied = stats.BytesCopied,
                .SkippedBytes = stats.SkippedBytes
            }
        Catch ex As Exception
            Logger.Error(ex, "DSH：导入运行时失败")
            DshSlotDeleteTreeSafe(staging)
            ' 中途失败时把已经挪过去的半成品清掉，别留个坏槽位
            If finalDir IsNot Nothing Then
                Try
                    If Directory.Exists(finalDir) Then DshSlotDeleteTreeSafe(finalDir)
                Catch
                End Try
            End If
            Return New DshSlotImportResult With {.Success = False, .Message = "导入失败：" & ex.Message}
        End Try
    End Function

    ''' <summary>删除一个导入的槽位。</summary>
    ''' <returns>成功返回 Nothing；失败返回原因。</returns>
    ''' <remarks>
    ''' 只允许删导入的槽位 —— npm 槽位是 pnpm 管的，删了会让
    ''' <c>package.json</c> 和磁盘不一致，应该走「卸载」而不是删目录。
    ''' 另外：**不允许删掉当前激活的那个**（先切走再删）。
    ''' </remarks>
    Public Function DshSlotDelete(SlotId As String) As String
        If String.IsNullOrWhiteSpace(SlotId) Then Return "没有指定要删除的槽位。"
        If String.Equals(SlotId, DshSlotNpmId, StringComparison.OrdinalIgnoreCase) Then
            Return "官方安装的槽位不能在这里删除 —— 它是 pnpm 管理的。"
        End If
        If String.Equals(SlotId, DshSlotActiveId(), StringComparison.OrdinalIgnoreCase) Then
            Return "不能删除当前正在使用的运行时，请先切换到别的运行时。"
        End If

        Dim target As DshSlotInfo = DshSlotReadManifest().FirstOrDefault(
            Function(s) String.Equals(s.Id, SlotId, StringComparison.OrdinalIgnoreCase))
        If target Is Nothing Then Return "找不到这个运行时槽位。"

        Try
            ' ⚠️ 变量名不能叫 dir —— Dir 是 VB 内置函数，会被当成关键字
            Dim slotDir As String = target.Dir
            If Directory.Exists(slotDir) Then DshSlotDeleteTreeSafe(slotDir)
            Dim all As List(Of DshSlotInfo) = DshSlotReadManifest()
            all.RemoveAll(Function(s) String.Equals(s.Id, SlotId, StringComparison.OrdinalIgnoreCase))
            DshSlotWriteManifest(all)
            Logger.Info($"DSH：已删除运行时槽位 {SlotId}")
            Return Nothing
        Catch ex As Exception
            Logger.Error(ex, $"DSH：删除运行时槽位 {SlotId} 失败")
            Return $"删除失败：{ex.Message}"
        End Try
    End Function

#End Region

#Region "文件操作"

    Private Class DshSlotCopyStats
        Public Property FilesCopied As Integer
        Public Property BytesCopied As Long
        Public Property SkippedBytes As Long
    End Class

    ''' <summary>判断一个顶层条目是否应该跳过。</summary>
    Private Function DshSlotShouldSkip(Name As String, IsDir As Boolean) As Boolean
        If String.IsNullOrWhiteSpace(Name) Then Return False
        If IsDir Then
            Return DshSlotSkipDirs.Any(Function(d) String.Equals(d, Name, StringComparison.OrdinalIgnoreCase))
        End If
        Dim ext As String = Path.GetExtension(Name)
        If String.IsNullOrWhiteSpace(ext) Then Return False
        Return DshSlotSkipExts.Any(Function(e) String.Equals(e, ext, StringComparison.OrdinalIgnoreCase))
    End Function

    ''' <summary>复制一棵目录树，按规则跳过顶层无关文件。</summary>
    Private Sub DshSlotCopyTreeFiltered(SourceDir As String, DestDir As String,
                                        Stats As DshSlotCopyStats, Progress As DshSlotProgressHandler)
        If Not Directory.Exists(DestDir) Then Directory.CreateDirectory(DestDir)

        Dim rootName As String = New DirectoryInfo(SourceDir).Name
        Report(Progress, "复制", $"正在复制 {rootName}……", 0.05)

        ' ── 顶层：按跳过规则分流 ──
        ' ⚠️ 循环变量不能叫 dir —— Dir 是 VB 内置函数（见 DshSlotDelete 里的同类注释）
        For Each subDir In Directory.EnumerateDirectories(SourceDir)
            Dim nm As String = Path.GetFileName(subDir)
            If DshSlotShouldSkip(nm, True) Then
                Stats.SkippedBytes += DshSlotMeasureTree(subDir)
                Continue For
            End If
            DshSlotCopyTreeRaw(subDir, Path.Combine(DestDir, nm), Stats, Progress)
        Next

        For Each f In Directory.EnumerateFiles(SourceDir)
            Dim nm As String = Path.GetFileName(f)
            If DshSlotShouldSkip(nm, False) Then
                Stats.SkippedBytes += DshSlotFileSize(f)
                Continue For
            End If
            DshSlotCopyFileRaw(f, Path.Combine(DestDir, nm), Stats)
        Next
    End Sub

    ''' <summary>递归复制，不做任何跳过判断（已在上层过滤）。</summary>
    Private Sub DshSlotCopyTreeRaw(SourceDir As String, DestDir As String,
                                   Optional Stats As DshSlotCopyStats = Nothing,
                                   Optional Progress As DshSlotProgressHandler = Nothing)
        If Not Directory.Exists(DestDir) Then Directory.CreateDirectory(DestDir)

        Dim subDirs As String() = Nothing
        Try
            subDirs = Directory.GetDirectories(SourceDir)
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：枚举子目录失败：{SourceDir}")
        End Try
        If subDirs IsNot Nothing Then
            For Each d In subDirs
                DshSlotCopyTreeRaw(d, Path.Combine(DestDir, Path.GetFileName(d)), Stats, Progress)
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
                DshSlotCopyFileRaw(f, Path.Combine(DestDir, Path.GetFileName(f)), Stats)
            Next
        End If

        If Stats IsNot Nothing AndAlso Progress IsNot Nothing AndAlso Stats.FilesCopied Mod 2000 = 0 Then
            Report(Progress, "复制", $"已复制 {Stats.FilesCopied} 个文件……", -1)
        End If
    End Sub

    Private Sub DshSlotCopyFileRaw(SourceFile As String, DestFile As String,
                                   Optional Stats As DshSlotCopyStats = Nothing)
        Dim parent As String = Path.GetDirectoryName(DestFile)
        If Not String.IsNullOrWhiteSpace(parent) AndAlso Not Directory.Exists(parent) Then
            Directory.CreateDirectory(parent)
        End If
        File.Copy(SourceFile, DestFile, True)
        If Stats IsNot Nothing Then
            Stats.FilesCopied += 1
            Stats.BytesCopied += DshSlotFileSize(SourceFile)
        End If
    End Sub

    ''' <summary>解压 zip，按规则跳过顶层无关文件。</summary>
    Private Sub DshSlotExtractZipFiltered(ZipPath As String, DestDir As String,
                                          Stats As DshSlotCopyStats, Progress As DshSlotProgressHandler)
        Using archive = ZipFile.Open(ZipPath, ZipArchiveMode.Read, Encoding.UTF8)
            Dim prefix As String = DshSlotZipCommonPrefix(archive)
            Report(Progress, "解压", $"正在解压 {Path.GetFileName(ZipPath)}……", 0.05)

            For Each entry In archive.Entries
                Dim rel As String = entry.FullName.Replace("/"c, DshSlotBS)
                If prefix.Length > 0 Then
                    If rel.Length <= prefix.Length Then Continue For
                    rel = rel.Substring(prefix.Length)
                End If
                If rel.Length = 0 Then Continue For

                ' 只对**顶层**做跳过判断：路径里第一个反斜杠之前的部分
                Dim slash As Integer = rel.IndexOf(DshSlotBS)
                Dim topName As String = If(slash < 0, rel, rel.Substring(0, slash))
                Dim isDirEntry As Boolean = String.IsNullOrEmpty(entry.Name)
                If DshSlotShouldSkip(topName, isDirEntry) Then
                    Stats.SkippedBytes += entry.Length
                    Continue For
                End If

                If isDirEntry Then
                    Dim d As String = Path.Combine(DestDir, rel)
                    If Not Directory.Exists(d) Then Directory.CreateDirectory(d)
                    Continue For
                End If

                Dim target As String = Path.Combine(DestDir, rel)
                Dim targetDir As String = Path.GetDirectoryName(target)
                If Not String.IsNullOrWhiteSpace(targetDir) AndAlso Not Directory.Exists(targetDir) Then
                    Directory.CreateDirectory(targetDir)
                End If
                entry.ExtractToFile(target, True)
                Stats.FilesCopied += 1
                Stats.BytesCopied += entry.Length

                If Stats.FilesCopied Mod 2000 = 0 Then
                    Report(Progress, "解压", $"已解压 {Stats.FilesCopied} 个文件……", -1)
                End If
            Next
        End Using
    End Sub

    ''' <summary>
    ''' 求 zip 里所有条目的公共顶层目录前缀（含结尾反斜杠）。
    ''' </summary>
    ''' <remarks>
    ''' 用户打包时常常把整个文件夹压进去（<c>DeepSeekHarness\app\...</c>），
    ''' 于是解出来会多一层壳。把这一层剥掉，导入后的目录结构和
    ''' 「直接选文件夹导入」保持一致 —— 否则同一个包用两种方式导入
    ''' 会得到两种不同的 <c>entryRelative</c>，白白增加复杂度。
    ''' 只有当**所有**条目都在同一个顶层目录下时才剥。
    ''' </remarks>
    Private Function DshSlotZipCommonPrefix(archive As ZipArchive) As String
        Dim prefix As String = Nothing
        For Each entry In archive.Entries
            Dim rel As String = entry.FullName.Replace("/"c, DshSlotBS)
            Dim slash As Integer = rel.IndexOf(DshSlotBS)
            If slash <= 0 Then Return ""     ' 有条目直接在根上 → 没有公共前缀
            Dim top As String = rel.Substring(0, slash + 1)
            If prefix Is Nothing Then
                prefix = top
            ElseIf Not String.Equals(prefix, top, StringComparison.OrdinalIgnoreCase) Then
                Return ""
            End If
        Next
        Return If(prefix, "")
    End Function

    ''' <summary>递归求目录体积（用于统计「跳过了多少」）。</summary>
    ''' <remarks>
    ''' ⚠️ 参数名不叫 <c>Dir</c> —— 那是 VB 内置函数，会撞车（同 <c>dir</c> 循环变量那个坑）。
    ''' ⚠️ 也不用 <c>EnumerateFiles(..., AllDirectories)</c>：那个 API 遇到任何一层
    ''' 没权限就整体抛异常（体积归零），而且会跟随符号链接导致虚高。
    ''' 统一复用 <see cref="DshMigrate.MeasureDirectorySize"/>。
    ''' </remarks>
    Private Function DshSlotMeasureTree(TargetDir As String) As Long
        Try
            Return DshMigrate.MeasureDirectorySize(TargetDir)
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：统计目录体积失败：{TargetDir}")
            Return 0L
        End Try
    End Function

    ''' <summary>文件体积；读不到时返回 0。</summary>
    ''' <remarks>
    ''' ⚠️ 参数名刻意不叫 <c>File</c> —— 那会遮蔽 <c>System.IO.File</c>，
    ''' 以后只要在这个函数里写一句 <c>File.Exists(...)</c> 就会报莫名其妙的
    ''' BC30456（和 <c>path</c> / <c>dir</c> 是同一类坑）。
    ''' </remarks>
    Private Function DshSlotFileSize(FilePath As String) As Long
        Try
            Return New FileInfo(FilePath).Length
        Catch
            Return 0
        End Try
    End Function

    ''' <summary>删除一棵目录树；失败只记日志，不抛。</summary>
    ''' <remarks>
    ''' ⚠️ 参数名不叫 <c>Dir</c>（VB 内置函数，会撞车）。
    ''' 统一走 <see cref="DshMigrate.DeleteDirectoryRobust"/> —— 它会：
    ''' <list type="bullet">
    ''' <item>清掉只读属性（从压缩包解出来的文件常带只读位，
    '''       <c>Directory.Delete(True)</c> 遇到它们会直接抛）</item>
    ''' <item>逐层遍历而不是用 <c>EnumerateFiles(..., AllDirectories)</c>
    '''       —— 后者遇到任何一层没权限就整体抛异常</item>
    ''' <item>遇到重解析点只删链接本身，不跟进目标</item>
    ''' </list>
    ''' </remarks>
    Private Sub DshSlotDeleteTreeSafe(TargetDir As String)
        Try
            If Not Directory.Exists(TargetDir) Then Return
            DshMigrate.DeleteDirectoryRobust(TargetDir)
            If Directory.Exists(TargetDir) Then
                Logger.Warn($"DSH：目录删除后仍存在（可能被占用）：{TargetDir}")
            End If
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：删除目录失败：{TargetDir}")
        End Try
    End Sub

    Private Function DshSlotSourceNameOf(Preview As DshSlotPreview) As String
        Try
            If Preview.SourceKind = "zip" Then Return Path.GetFileNameWithoutExtension(Preview.SourcePath)
            Dim trimmed As String = Preview.SourcePath.TrimEnd(DshSlotBS)
            Return New DirectoryInfo(trimmed).Name
        Catch
            Return Nothing
        End Try
    End Function

#End Region

#Region "查找工具"

    ''' <summary>
    ''' 广度优先找出「dsh 基准目录」—— 即直接含
    ''' <c>node_modules\@deepseek-ai\dsh\lib\bin.js</c> 的那一层。
    ''' </summary>
    ''' <remarks>
    ''' 为什么要 BFS 而不是写死几层：
    ''' <list type="bullet">
    ''' <item>扁平安装：基准目录就是根目录本身</item>
    ''' <item>便携包：基准目录是 <c>app\</c>（根目录下第一层）</item>
    ''' <item>用户把整个文件夹又套了一层再压缩：基准目录在第二层</item>
    ''' </list>
    ''' 三种都得认。
    '''
    ''' ⚠️ 遍历时**不进 <c>node_modules</c>** —— 那里面有几万个目录，
    ''' 进去就是灾难。而 dsh 的入口恰恰在 <c>node_modules</c> 的**上一层**，
    ''' 所以不进去也完全够用。
    ''' </remarks>
    Private Function DshSlotFindDshBase(RootDir As String) As String
        Try
            Dim queue As New Queue(Of KeyValuePair(Of String, Integer))()
            queue.Enqueue(New KeyValuePair(Of String, Integer)(RootDir, 0))
            Dim visited As Integer = 0

            While queue.Count > 0 AndAlso visited < DshSlotMaxDirsVisited
                Dim cur = queue.Dequeue()
                visited += 1

                If File.Exists(Path.Combine(cur.Key, DshSlotEntryRel)) Then Return cur.Key
                If cur.Value >= DshSlotMaxDepth Then Continue While

                Try
                    For Each child In Directory.EnumerateDirectories(cur.Key)
                        Dim nm As String = Path.GetFileName(child)
                        If String.Equals(nm, "node_modules", StringComparison.OrdinalIgnoreCase) Then Continue For
                        queue.Enqueue(New KeyValuePair(Of String, Integer)(child, cur.Value + 1))
                    Next
                Catch ex As Exception
                    Logger.Warn(ex, $"DSH：枚举目录失败：{cur.Key}")
                End Try
            End While
        Catch ex As Exception
            Logger.Error(ex, $"DSH：搜索 dsh 基准目录失败：{RootDir}")
        End Try
        Return Nothing
    End Function

    ''' <summary>
    ''' 找一个可用的 node.exe。
    ''' </summary>
    ''' <remarks>
    ''' 优先沿「dsh 基准目录 → 根目录」的祖先链往上找 <c>node\node.exe</c> ——
    ''' 便携包的标准布局是 <c>根\node\</c> 配 <c>根\app\</c>，正好命中。
    ''' 找不到再退化成 BFS 全找一遍。
    ''' </remarks>
    Private Function DshSlotFindNode(RootDir As String, DshBaseDir As String) As String
        ' ── ① 沿祖先链往上找 ──
        Dim cur As String = DshBaseDir
        Dim guard As Integer = 0
        While Not String.IsNullOrWhiteSpace(cur) AndAlso guard < 8
            guard += 1
            Dim candidate As String = Path.Combine(cur, "node", "node.exe")
            If File.Exists(candidate) Then Return candidate
            Dim parent As String = Path.GetDirectoryName(cur.TrimEnd(DshSlotBS))
            If String.IsNullOrWhiteSpace(parent) Then Exit While
            If String.Equals(parent.TrimEnd(DshSlotBS), cur.TrimEnd(DshSlotBS), StringComparison.OrdinalIgnoreCase) Then Exit While
            ' 走到根目录之外就停
            If parent.Length < RootDir.TrimEnd(DshSlotBS).Length Then
                If Not String.Equals(parent.TrimEnd(DshSlotBS), RootDir.TrimEnd(DshSlotBS), StringComparison.OrdinalIgnoreCase) Then
                    Exit While
                End If
            End If
            cur = parent
            If String.Equals(cur.TrimEnd(DshSlotBS), RootDir.TrimEnd(DshSlotBS), StringComparison.OrdinalIgnoreCase) Then
                Dim atRoot As String = Path.Combine(RootDir, "node", "node.exe")
                If File.Exists(atRoot) Then Return atRoot
                Exit While
            End If
        End While

        ' ── ② BFS 兜底 ──
        Try
            Dim queue As New Queue(Of KeyValuePair(Of String, Integer))()
            queue.Enqueue(New KeyValuePair(Of String, Integer)(RootDir, 0))
            Dim visited As Integer = 0
            While queue.Count > 0 AndAlso visited < DshSlotMaxDirsVisited
                Dim item = queue.Dequeue()
                visited += 1
                Dim candidate As String = Path.Combine(item.Key, "node", "node.exe")
                If File.Exists(candidate) Then Return candidate
                If item.Value >= DshSlotMaxDepth Then Continue While
                Try
                    For Each child In Directory.EnumerateDirectories(item.Key)
                        Dim nm As String = Path.GetFileName(child)
                        If String.Equals(nm, "node_modules", StringComparison.OrdinalIgnoreCase) Then Continue For
                        queue.Enqueue(New KeyValuePair(Of String, Integer)(child, item.Value + 1))
                    Next
                Catch
                End Try
            End While
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：搜索 node.exe 失败：{RootDir}")
        End Try

        Return Nothing
    End Function

    ''' <summary>求 <paramref name="FullPath"/> 相对于 <paramref name="RootDir"/> 的路径；不在其下时返回空串。</summary>
    Private Function DshSlotRelativeOf(RootDir As String, FullPath As String) As String
        Try
            Dim root As String = RootDir.TrimEnd(DshSlotBS)
            Dim full As String = FullPath.TrimEnd(DshSlotBS)
            If String.Equals(root, full, StringComparison.OrdinalIgnoreCase) Then Return ""
            If Not full.StartsWith(root & "\", StringComparison.OrdinalIgnoreCase) Then Return ""
            Return full.Substring(root.Length + 1)
        Catch
            Return ""
        End Try
    End Function

#End Region

End Module
