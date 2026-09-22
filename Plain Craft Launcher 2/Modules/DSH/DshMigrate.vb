Imports System.IO
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Text

''' <summary>
''' DSH 数据目录的一键迁移。
'''
''' ## 用户敲定的方案（第 4 轮 2-e = B）
''' 把整个 <c>DshRoot</c> 搬到用户指定的位置，支持**跨盘**。
'''
''' ## 为什么不能用 <c>Directory.Move</c>
''' <see cref="Directory.Move"/> 在**同一卷内**是原子的改名操作，又快又安全；
''' 但**跨卷**它只能抛出 <c>IOException</c>（Windows 上不允许直接移动目录到另一卷）。
''' 而「迁移到别的盘」恰恰是本功能最常见的使用场景 ——
''' 所以必须自己实现「复制 → 校验 → 删除原目录」的降级路径。
'''
''' ## 迁移后 PCL 读取路径必须同步更新（用户特别强调）
''' 这一点靠 <see cref="ModDSH.DshRoot"/> 是**唯一真相来源**来保证：
''' 它从设置 <c>DshDataRoot</c> 读取，所有子路径都从它推导。
''' 因此迁移的收尾动作就是写这个设置 —— 写完之后，
''' runtime / instances / 日志 / 清单 / 实例 HomeDir 全部立刻指向新位置，
''' 不需要任何一处额外改动。
'''
''' ⚠️ 但有一个例外必须手工处理：**实例对象里已经算好的绝对路径**。
''' <see cref="DshInstance.HomeDir"/> 是每次调用现算的（从 <c>Id</c> 推导），
''' 所以没问题；但清单文件 <c>instances.json</c> 里若存了绝对路径，
''' 搬过去之后就会指回旧位置。本模块在迁移后**重新加载清单**来消除这种可能。
''' </summary>
Public Module DshMigrate

#Region "常量"

    ''' <summary>
    ''' 反斜杠**字符**（用于 <c>TrimEnd</c>）。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ 为什么不用字面量 <c>"\"c</c>：
    ''' VB 里反斜杠**不是**转义字符，所以 <c>"\"</c> 是一个**完整的字符串**（内容就是一个反斜杠）。
    ''' 编译器贪婪匹配，把 <c>"\"c</c> 解析成「字符串 "\" 后跟标识符 c」，
    ''' 于是报一堆莫名其妙的 BC32017 / BC30037（"应为逗号"、"字符无效"）。
    ''' 用 <c>ChrW(92)</c> 构造最稳，且意图一目了然。
    ''' </remarks>
    Private ReadOnly BS As Char() = {ChrW(92)}

    ''' <summary>反斜杠字符串（用于 <c>EndsWith</c> / 拼接）。</summary>
    Private ReadOnly BS_STR As String = ChrW(92)

    ''' <summary>
    ''' 单次复制允许的最长时间（毫秒）。60 分钟。
    ''' </summary>
    ''' <remarks>
    ''' 运行时目录里 dsh 的依赖树有上万个文件、总计数百 MB，
    ''' 在机械硬盘上跨盘复制可能要好几分钟。给足余量。
    ''' </remarks>
    Private Const CopyTimeoutMs As Integer = 3600000

    ''' <summary>
    ''' Windows 长路径前缀。
    ''' </summary>
    ''' <remarks>
    ''' ⚠️ dsh 的依赖树里有深层嵌套的 <c>node_modules</c>，
    ''' 拼出来的绝对路径很容易超过 260 字符。此时 .NET Framework 的
    ''' <c>Directory.GetFiles</c> / <c>File.Copy</c> 会抛
    ''' <c>PathTooLongException</c>。
    '''
    ''' 加 <c>\\?\</c> 前缀可以让 Win32 API 接受最长约 32767 字符的路径。
    ''' 注意前缀**只对绝对路径有效**，且路径里不能有 <c>/</c> 或 <c>.</c> 相对段。
    ''' </remarks>
    Private Const LongPathPrefix As String = "\\?\"

#End Region

#Region "数据模型"

    ''' <summary>迁移进度回调。</summary>
    ''' <param name="Stage">当前阶段描述。</param>
    ''' <param name="Message">人类可读的说明。</param>
    ''' <param name="Progress">0-1 的进度；未知时为 -1。</param>
    Public Delegate Sub DshMigrateProgressHandler(Stage As String, Message As String, Progress As Double)

    ''' <summary>迁移结果。</summary>
    Public Class DshMigrateResult
        ''' <summary>是否成功。</summary>
        Public Property Success As Boolean = False
        ''' <summary>旧目录。</summary>
        Public Property OldRoot As String = ""
        ''' <summary>新目录。</summary>
        Public Property NewRoot As String = ""
        ''' <summary>结果说明（给用户看）。</summary>
        Public Property Message As String = ""
        ''' <summary>复制的文件总数。</summary>
        Public Property FileCount As Integer = 0
        ''' <summary>重建的符号链接 / junction 数量。</summary>
        Public Property LinkCount As Integer = 0
        ''' <summary>链接重建不完整时的告警（为空表示没问题）。</summary>
        Public Property LinkWarning As String = ""
        ''' <summary>复制的总字节数。</summary>
        Public Property TotalBytes As Long = 0
        ''' <summary>是否走了「跨盘降级」路径（复制 + 删除）。</summary>
        Public Property WasCrossVolume As Boolean = False
        ''' <summary>
        ''' 原目录是否被完整删除。
        ''' </summary>
        ''' <remarks>
        ''' 即使为 False，迁移本身也是成功的（设置已指向新位置）——
        ''' 只是旧目录还占着磁盘，可以稍后手动删。
        ''' </remarks>
        Public Property OldRootRemoved As Boolean = False
    End Class

#End Region

#Region "预检"

    ''' <summary>
    ''' 迁移前的可行性检查。
    ''' </summary>
    ''' <param name="TargetRoot">目标根目录。</param>
    ''' <returns>可以迁移时返回 Nothing；否则返回拒绝的理由。</returns>
    ''' <remarks>
    ''' 这一步**必须在真正动手之前**跑完。迁移是重操作，
    ''' 中途失败会留下一个半成品目录 —— 能提前拦掉的都要提前拦掉。
    ''' </remarks>
    Public Function PreflightCheck(TargetRoot As String) As String
        If String.IsNullOrWhiteSpace(TargetRoot) Then Return "请先选择目标目录。"

        Dim target As String = TargetRoot.Trim()
        
        If Not target.EndsWith(BS_STR) Then target &= BS_STR

        Dim source As String = ModDSH.DshRoot

        ' 1. 不能是同一个目录
        If String.Equals(source.TrimEnd(BS), target.TrimEnd(BS), StringComparison.OrdinalIgnoreCase) Then
            Return "目标目录与当前数据目录相同，无需迁移。"
        End If

        ' 2. 不能互相嵌套 —— 会造成无限递归复制
        If target.StartsWith(source, StringComparison.OrdinalIgnoreCase) Then
            Return "目标目录不能位于当前数据目录内部，否则会造成递归复制。" & vbCrLf &
                   $"当前：{source}" & vbCrLf & $"目标：{target}"
        End If
        If source.StartsWith(target, StringComparison.OrdinalIgnoreCase) Then
            Return "目标目录不能是当前数据目录的上级目录。" & vbCrLf &
                   $"当前：{source}" & vbCrLf & $"目标：{target}"
        End If

        ' 3. 不能指到盘根（权限 + 会把盘塞满）
        Dim targetRootOnly = Path.GetPathRoot(target)
        If String.Equals(target.TrimEnd(BS), If(targetRootOnly, "").TrimEnd(BS), StringComparison.OrdinalIgnoreCase) Then
            Return "请不要直接选择磁盘根目录，请在其中新建一个文件夹（例如 D:\PCL-DSH）。"
        End If

        ' 4. 有实例在跑就不能搬 —— 文件被占用，复制必然失败
        Try
            ModDSH.DshEnsureInstancesLoaded()
            Dim running = ModDSH.DshInstances.Where(Function(i) i.IsRunning OrElse i.HasLiveProcess).ToList()
            If running.Count > 0 Then
                Return $"有 {running.Count} 个实例正在运行，无法迁移：" & vbCrLf &
                       String.Join(vbCrLf, running.Select(Function(i) "　· " & i.Name)) & vbCrLf &
                       "请先在「启动」页停止全部实例。"
            End If
        Catch ex As Exception
            Logger.Warn(ex, "DSH：迁移预检无法确认实例状态，继续")
        End Try

        ' 5. 目标目录如果已存在且有内容，要问清楚
        Try
            If Directory.Exists(target) Then
                Dim entries = Directory.GetFileSystemEntries(target)
                If entries.Length > 0 Then
                    Dim hasDshMark = File.Exists(Path.Combine(target, "instances.json")) OrElse
                                     Directory.Exists(Path.Combine(target, "runtime"))
                    If hasDshMark Then
                        Return "目标目录里已经有一份 PCL DSH 数据。" & vbCrLf &
                               "请换一个空目录，或先把旧的那份移走 —— " & vbCrLf &
                               "把两份数据混在一起会导致实例清单错乱。"
                    End If
                    Return "目标目录不是空的，直接迁移会把两批文件混在一起。" & vbCrLf &
                           "请选择一个空文件夹（可以新建一个）。"
                End If
            End If
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：检查目标目录失败：{ex.Message}")
            Return $"无法访问目标目录：{ex.Message}"
        End Try

        ' 6. 目标盘要有足够空间（源码大小 + 10% 余量）
        Try
            Dim needBytes As Long = MeasureDirectorySize(source)
            Dim targetDriveRoot = Path.GetPathRoot(Path.GetFullPath(target))
            If Not String.IsNullOrWhiteSpace(targetDriveRoot) Then
                Dim di As New DriveInfo(targetDriveRoot)
                If di.IsReady Then
                    Dim required As Long = CLng(needBytes * 1.1)
                    If di.AvailableFreeSpace < required Then
                        Return $"目标磁盘空间不足。" & vbCrLf &
                               $"需要约 {DshDoctor.FormatBytes(required)}（含 10% 余量），" & vbCrLf &
                               $"但 {targetDriveRoot} 只剩 {DshDoctor.FormatBytes(di.AvailableFreeSpace)}。"
                    End If
                End If
            End If
        Catch ex As Exception
            ' 空间算不出来不阻塞 —— 真不够的话复制过程中会报错
            Logger.Warn(ex, "DSH：迁移预检无法确认磁盘空间，继续")
        End Try

        ' 7. 试着建一下目录，确认有写权限
        Try
            If Not Directory.Exists(target) Then Directory.CreateDirectory(target)
            Dim probe = Path.Combine(target, ".pcl-dsh-write-probe")
            File.WriteAllText(probe, "probe")
            File.Delete(probe)
        Catch ex As Exception
            Return $"没有向目标目录写入的权限：{ex.Message}" & vbCrLf &
                   "如果目标在系统盘根目录或受保护位置，请换一个位置。"
        End Try

        Return Nothing
    End Function

#End Region

#Region "执行迁移"

    ''' <summary>
    ''' 把数据目录迁移到新位置。**阻塞调用，必须放在后台线程。**
    ''' </summary>
    ''' <param name="TargetRoot">目标根目录（会自动补末尾反斜杠）。</param>
    ''' <param name="Progress">可选的进度回调。</param>
    ''' <returns>迁移结果。</returns>
    ''' <remarks>
    ''' 流程：
    ''' <list type="number">
    '''   <item>预检（见 <see cref="PreflightCheck"/>）</item>
    '''   <item>同盘 → <c>Directory.Move</c>（原子、瞬时）</item>
    '''   <item>跨盘 → 复制 → 校验文件数 → 写设置 → 删原目录</item>
    '''   <item>重新加载实例清单，确保内存里的路径全部指向新位置</item>
    ''' </list>
    '''
    ''' ⭐ **写设置这一步是迁移的关键收尾动作**：写完 <c>DshDataRoot</c> 之后，
    ''' <see cref="ModDSH.DshRoot"/> 立刻返回新值，所有下游路径自动跟上。
    ''' </remarks>
    Public Function Migrate(TargetRoot As String,
                            Optional Progress As DshMigrateProgressHandler = Nothing) As DshMigrateResult
        Dim result As New DshMigrateResult()
        Dim target As String = If(TargetRoot, "").Trim()
        If Not target.EndsWith(BS_STR) Then target &= BS_STR
        Dim source As String = ModDSH.DshRoot
        result.OldRoot = source
        result.NewRoot = target

        ' ---- 0. 预检 ----
        Report(Progress, "检查", "正在检查目标位置……", -1)
        Dim reject As String = PreflightCheck(TargetRoot)
        If reject IsNot Nothing Then
            result.Message = reject
            Logger.Warn($"DSH：迁移被预检拒绝 —— {reject}")
            Return result
        End If

        Dim crossVolume As Boolean = Not SameVolume(source, target)
        result.WasCrossVolume = crossVolume
        Logger.Info($"DSH：开始迁移数据目录：{source} → {target}（跨盘={crossVolume}）")

        ' ---- 1. 尝试同盘 Move ----
        If Not crossVolume Then
            Report(Progress, "移动", "正在移动目录（同盘，瞬间完成）……", -1)
            Try
                If Not Directory.Exists(target) Then
                    ' Move 要求目标是**不存在**的路径（存在时报"目录已存在"）
                    Directory.Move(source.TrimEnd(BS), target.TrimEnd(BS))
                Else
                    ' 目标已存在（预检确认过是空的）→ 退化为复制
                    crossVolume = True
                    result.WasCrossVolume = True
                End If

                If Not crossVolume Then
                    result.Success = True
                    result.OldRootRemoved = True
                    result.FileCount = -1
                    result.TotalBytes = -1
                    FinishMigration(result, Progress)
                    Return result
                End If
            Catch ex As IOException
                ' 同盘 Move 也可能因为被占用而失败 → 降级到复制
                Logger.Warn($"DSH：同盘移动失败（{ex.Message}），降级为复制")
                crossVolume = True
                result.WasCrossVolume = True
            Catch ex As Exception
                result.Message = $"移动目录失败：{ex.Message}"
                Logger.Error(ex, "DSH：迁移失败（Move 阶段）")
                Return result
            End Try
        End If

        ' ---- 2. 复制（跨盘，或 Move 失败后的降级路径）----
        Try
            If Not Directory.Exists(target) Then Directory.CreateDirectory(target)

            Dim copyResult = CopyDirectoryRecursive(source, target, Progress)

            ' 校验：两边文件数必须一致，否则不删源
            ' ⚠️ CountFiles 默认**不跟随链接** —— 跟随的话两边都会虚高、一致通过，
            '    这道校验就等于没做（旧版就是这样，见 CountFiles 的说明）。
            Report(Progress, "校验", "正在校验复制结果……", 0.95)
            Dim srcCount As Integer = CountFiles(source)
            Dim srcLinks As Integer = CountLinks(source)
            If copyResult.FileCount <> srcCount Then
                result.Success = False
                result.FileCount = copyResult.FileCount
                result.TotalBytes = copyResult.TotalBytes
                result.Message = $"复制不完整（源 {srcCount} 个文件，目标 {copyResult.FileCount} 个），" & vbCrLf &
                                 "已保留原目录不做删除。" & vbCrLf &
                                 "请检查目标磁盘空间后重试，或直接删除目标目录再试一次。"
                Logger.Error($"DSH：迁移校验失败 —— 源 {srcCount} / 目标 {copyResult.FileCount}")
                Return result
            End If
            ' 链接数也要对得上：少了说明有链接没重建成功，那棵树就是坏的
            If copyResult.LinkFailed > 0 OrElse (srcLinks > 0 AndAlso copyResult.LinkCount < srcLinks) Then
                Logger.Warn($"DSH：链接重建不完整 —— 源 {srcLinks} 个，成功 {copyResult.LinkCount} 个，失败 {copyResult.LinkFailed} 个")
                result.LinkWarning = $"有 {srcLinks - copyResult.LinkCount} 个符号链接没能重建，" & vbCrLf &
                                     "依赖树可能不完整，建议迁移后点一次「立即修复」重装运行时。"
            End If

            result.Success = True
            result.FileCount = copyResult.FileCount
            result.TotalBytes = copyResult.TotalBytes
            result.LinkCount = copyResult.LinkCount

            ' ---- 3. 写设置（关键收尾：让全局路径立刻指向新位置）----
            FinishMigration(result, Progress)

            ' ---- 4. 删原目录 ----
            Report(Progress, "清理", "正在删除原目录……", 0.98)
            Try
                DeleteDirectoryLongPath(source)
                result.OldRootRemoved = True
                Logger.Info($"DSH：已删除原数据目录 → {source}")
            Catch ex As Exception
                result.OldRootRemoved = False
                Logger.Warn($"DSH：删除原数据目录失败（不影响迁移结果）：{ex.Message}")
            End Try

            Dim linkNote As String = If(result.LinkCount > 0, $"，{result.LinkCount} 个符号链接已重建", "")
            If result.OldRootRemoved Then
                result.Message = $"迁移完成。{result.FileCount} 个文件{linkNote}已搬到新位置，原目录已清理。"
            Else
                result.Message = $"迁移完成。{result.FileCount} 个文件{linkNote}已搬到新位置。" & vbCrLf &
                                 "⚠️ 原目录未能删除（可能有文件被占用），可以稍后手动删除。" & vbCrLf &
                                 $"原位置：{source}"
            End If
            If Not String.IsNullOrWhiteSpace(result.LinkWarning) Then
                result.Message &= vbCrLf & vbCrLf & "⚠️ " & result.LinkWarning
            End If

            Report(Progress, "完成", result.Message, 1)
            Return result
        Catch ex As Exception
            result.Success = False
            result.Message = $"迁移过程中出错：{ex.Message}" & vbCrLf &
                             "原目录未被删除，可以安全重试。"
            Logger.Error(ex, "DSH：迁移失败（复制阶段）")
            Return result
        End Try
    End Function

    ''' <summary>
    ''' 迁移的收尾：写设置 + 重新加载实例清单。
    ''' </summary>
    ''' <remarks>
    ''' ⭐ 这是整个迁移**最关键的一步**，也是用户特别强调的部分。
    ''' 只搬文件而不改设置 = 迁移实际上没生效（PCL 还指着旧目录）。
    ''' </remarks>
    Private Sub FinishMigration(result As DshMigrateResult, Progress As DshMigrateProgressHandler)
        Report(Progress, "应用", "正在更新数据目录设置……", 0.97)

        ' 写设置 —— ModDSH.DshRoot 立刻返回新值，所有下游路径自动跟上
        Settings.Set("DshDataRoot", result.NewRoot)
        Logger.Info($"DSH：数据目录设置已更新 → {ModDSH.DshRoot}")

        ' 重新加载实例清单：清单是从 DshRoot 推导的路径读的，
        ' 重载确保内存里的实例对象全部指向新位置（而不是旧的对象缓存）
        Try
            ModDSH.DshReloadInstancesAfterMigration()
        Catch ex As Exception
            Logger.Error(ex, "DSH：迁移后重载实例清单失败")
        End Try

        ' 重建目录骨架（防止用户搬完后新位置少了某个子目录）
        Try
            ModDSH.DshEnsureDirectories()
        Catch ex As Exception
            Logger.Warn(ex, "DSH：迁移后重建目录骨架失败")
        End Try
    End Sub

    ''' <summary>把数据目录迁回出厂默认位置。</summary>
    Public Function MigrateToDefault(Optional Progress As DshMigrateProgressHandler = Nothing) As DshMigrateResult
        Return Migrate(ModDSH.DshDefaultRoot, Progress)
    End Function

#End Region

#Region "符号链接（junction / symlink）支持"

    ''' <summary>
    ''' ⭐ 为什么迁移必须特殊处理符号链接
    ''' </summary>
    ''' <remarks>
    ''' ═══════════════════════════════════════════════════════════════════════
    ''' 真实缺陷（2026-09-22 用实验证实，代价很大）
    ''' ═══════════════════════════════════════════════════════════════════════
    ''' 旧实现用 <c>Directory.GetFiles</c> / <c>GetDirectories</c> + <c>File.Copy</c> 复制，
    ''' 会把**所有重解析点解引用成真实目录**。而 pnpm 的 <c>node_modules</c>
    ''' 是一张**链接图**，不是文件树：
    ''' <code>
    ''' node_modules\@deepseek-ai\dsh  →  .pnpm\@deepseek-ai+dsh@0.1.6-...\node_modules\@deepseek-ai\dsh
    ''' </code>
    ''' 实测 <c>runtime</c> 里有 **2474 个重解析点**。解引用后：
    ''' <list type="bullet">
    ''' <item>链接语义全部丢失，pnpm 后续操作面对一棵语义已损坏的树</item>
    ''' <item>文件数从 44598 膨胀到 **212091**（多出 16.7 万个重复文件，4.75 倍）</item>
    ''' <item>迁移耗时从秒级涨到 2.5 分钟，磁盘白占几个 GB</item>
    ''' </list>
    '''
    ''' ⚠️ 而且**不能简单地"原样保留链接"** —— pnpm 写的链接目标是
    ''' **绝对路径**（<c>E:\test\runtime\...</c>）。原样搬走的话，
    ''' 链接会指向旧位置，同样是坏的。
    ''' 所以正确做法是：**在新位置重建链接，并把落在旧根之内的目标重定位到新根**。
    '''
    ''' 另外 <see cref="CountFiles"/> 原本也跟随链接，导致
    ''' 「源/目标文件数一致」这道完整性校验**完全失效**（两边都虚高、一致通过）。
    ''' </remarks>
    Private Const IO_REPARSE_TAG_MOUNT_POINT As UInteger = &HA0000003UI
    Private Const IO_REPARSE_TAG_SYMLINK As UInteger = &HA000000CUI
    Private Const FSCTL_SET_REPARSE_POINT As UInteger = &H900A4UI
    Private Const FSCTL_GET_REPARSE_POINT As UInteger = &H900A8UI

    Private Const GENERIC_WRITE As UInteger = &H40000000UI
    Private Const FILE_SHARE_RW As UInteger = 3UI
    Private Const OPEN_EXISTING As UInteger = 3UI
    Private Const FILE_FLAG_BACKUP_SEMANTICS As UInteger = &H2000000UI
    Private Const FILE_FLAG_OPEN_REPARSE_POINT As UInteger = &H200000UI
    Private ReadOnly INVALID_HANDLE_VALUE As New IntPtr(-1)

    <System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet:=System.Runtime.InteropServices.CharSet.Unicode, SetLastError:=True)>
    Private Function CreateFileW(lpFileName As String, dwDesiredAccess As UInteger, dwShareMode As UInteger,
                                 lpSecurityAttributes As IntPtr, dwCreationDisposition As UInteger,
                                 dwFlagsAndAttributes As UInteger, hTemplateFile As IntPtr) As IntPtr
    End Function

    <System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError:=True)>
    Private Function DeviceIoControl(hDevice As IntPtr, dwIoControlCode As UInteger,
                                     InBuffer As Byte(), nInBufferSize As UInteger,
                                     OutBuffer As Byte(), nOutBufferSize As UInteger,
                                     ByRef lpBytesReturned As UInteger, lpOverlapped As IntPtr) As Boolean
    End Function

    <System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError:=True)>
    Private Function CloseHandle(hObject As IntPtr) As Boolean
    End Function

    ''' <summary>判断路径是不是重解析点（junction / 符号链接）。</summary>
    Public Function DshIsReparsePoint(ItemPath As String) As Boolean
        If String.IsNullOrWhiteSpace(ItemPath) Then Return False
        Try
            Dim attr As FileAttributes = File.GetAttributes(ToLongPath(ItemPath))
            Return (attr And FileAttributes.ReparsePoint) = FileAttributes.ReparsePoint
        Catch ex As Exception
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 读取重解析点的目标路径；不是链接或读不到时返回 <c>Nothing</c>。
    ''' </summary>
    ''' <remarks>
    ''' .NET Framework 的 <c>DirectoryInfo</c> **没有** <c>Target</c> 属性
    ''' （那是 .NET Core / .NET 5+ 才有的），只能自己发
    ''' <c>FSCTL_GET_REPARSE_POINT</c> 并解析 <c>REPARSE_DATA_BUFFER</c>。
    '''
    ''' 缓冲区布局（<c>SubstituteNameOffset</c> 相对 PathBuffer 起点）：
    ''' <code>
    '''   0  ReparseTag            (4)
    '''   4  ReparseDataLength     (2)
    '''   6  Reserved              (2)
    '''   8  SubstituteNameOffset  (2)
    '''  10  SubstituteNameLength  (2)
    '''  12  PrintNameOffset       (2)
    '''  14  PrintNameLength       (2)
    '''  16  PathBuffer  ← junction 的路径从这里开始
    '''  20  PathBuffer  ← symlink 的路径从这里开始（多一个 4 字节 Flags）
    ''' </code>
    ''' </remarks>
    Public Function DshReadLinkTarget(ItemPath As String) As String
        Dim h As IntPtr = INVALID_HANDLE_VALUE
        Try
            h = CreateFileW(ToLongPath(ItemPath), 0UI, FILE_SHARE_RW, IntPtr.Zero,
                            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS Or FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero)
            If h = INVALID_HANDLE_VALUE Then Return Nothing

            Dim buf(16383) As Byte
            Dim returned As UInteger = 0
            If Not DeviceIoControl(h, FSCTL_GET_REPARSE_POINT, Nothing, 0UI, buf, CUInt(buf.Length), returned, IntPtr.Zero) Then
                Return Nothing
            End If
            If returned < 16UI Then Return Nothing

            Dim tag As UInteger = BitConverter.ToUInt32(buf, 0)
            Dim nameOffset As Integer
            Dim nameLength As Integer
            If tag = IO_REPARSE_TAG_MOUNT_POINT Then
                nameOffset = BitConverter.ToUInt16(buf, 8)
                nameLength = BitConverter.ToUInt16(buf, 10)
            ElseIf tag = IO_REPARSE_TAG_SYMLINK Then
                nameOffset = BitConverter.ToUInt16(buf, 8)
                nameLength = BitConverter.ToUInt16(buf, 10)
            Else
                Return Nothing
            End If

            Dim pathStart As Integer = If(tag = IO_REPARSE_TAG_MOUNT_POINT, 16, 20)
            Dim start As Integer = pathStart + nameOffset
            If start < 0 OrElse nameLength <= 0 OrElse start + nameLength > CInt(returned) Then Return Nothing

            Dim raw As String = Encoding.Unicode.GetString(buf, start, nameLength)
            ' junction 的 substitute name 带 NT 命名空间前缀，去掉它才是普通路径
            If raw.StartsWith("\??\", StringComparison.Ordinal) Then raw = raw.Substring(4)
            If raw.StartsWith("\\?\", StringComparison.Ordinal) Then raw = raw.Substring(4)
            Return raw
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：读取链接目标失败（{ItemPath}）")
            Return Nothing
        Finally
            If h <> INVALID_HANDLE_VALUE Then CloseHandle(h)
        End Try
    End Function

    ''' <summary>
    ''' 在 <paramref name="LinkPath"/> 处创建指向 <paramref name="TargetPath"/> 的 junction。
    ''' </summary>
    ''' <remarks>
    ''' 优先走 <c>DeviceIoControl</c> + <c>FSCTL_SET_REPARSE_POINT</c>
    ''' —— 比每个链接起一个 <c>mklink</c> 进程快几个数量级（实测 runtime 里有 2474 个链接）。
    ''' P/Invoke 失败才回退到 <c>mklink /J</c>。
    ''' </remarks>
    Public Function DshCreateJunction(LinkPath As String, TargetPath As String) As Boolean
        If String.IsNullOrWhiteSpace(LinkPath) OrElse String.IsNullOrWhiteSpace(TargetPath) Then Return False

        ' 目标目录必须已经存在，否则 junction 会变成悬空的
        If Not Directory.Exists(ToLongPath(TargetPath)) Then
            Logger.Warn($"DSH：链接目标不存在，跳过重建：{TargetPath}")
            Return False
        End If

        ' junction 必须挂在一个**已存在的空目录**上
        Try
            If Directory.Exists(ToLongPath(LinkPath)) Then
                ' 已经存在：如果本身就是链接就先删掉重建，否则不动（避免误删真实内容）
                If Not DshIsReparsePoint(LinkPath) Then
                    Logger.Warn($"DSH：目标位置已有真实目录，跳过链接重建：{LinkPath}")
                    Return False
                End If
                Directory.Delete(ToLongPath(LinkPath), False)
            End If
            Directory.CreateDirectory(ToLongPath(LinkPath))
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：准备链接挂载点失败（{LinkPath}）")
            Return False
        End Try

        If DshCreateJunctionRaw(LinkPath, TargetPath) Then Return True

        ' 回退：mklink /J（不需要管理员权限）
        Try
            Dim psi As New ProcessStartInfo With {
                .FileName = "cmd.exe",
                .Arguments = "/c mklink /J """ & LinkPath & """ """ & TargetPath & """",
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True
            }
            ' ⚠️ 任何 Process.Start 之前都必须播种环境，否则子进程拿不到 SystemRoot 等变量
            DshRuntime.SeedEnvironment(psi)
            Using p As Process = Process.Start(psi)
                If p IsNot Nothing Then
                    p.StandardOutput.ReadToEnd()
                    p.StandardError.ReadToEnd()
                    p.WaitForExit(15000)
                    If p.HasExited AndAlso p.ExitCode = 0 Then Return True
                End If
            End Using
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：mklink 回退也失败（{LinkPath}）")
        End Try
        Return False
    End Function

    ''' <summary>用 FSCTL_SET_REPARSE_POINT 直接写 junction（快路径）。</summary>
    Private Function DshCreateJunctionRaw(LinkPath As String, TargetPath As String) As Boolean
        Dim h As IntPtr = INVALID_HANDLE_VALUE
        Try
            h = CreateFileW(ToLongPath(LinkPath), GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero,
                            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS Or FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero)
            If h = INVALID_HANDLE_VALUE Then Return False

            ' 目标要写成 NT 命名空间形式
            Dim substitute As String = "\??\" & TargetPath
            Dim subBytes As Byte() = Encoding.Unicode.GetBytes(substitute)
            Dim printBytes As Byte() = Encoding.Unicode.GetBytes(TargetPath)

            ' PathBuffer = substitute + NUL + print + NUL
            Dim pathBufLen As Integer = subBytes.Length + 2 + printBytes.Length + 2
            ' ReparseDataLength = 4 个 USHORT 字段(8) + PathBuffer
            Dim dataLen As Integer = 8 + pathBufLen
            Dim total As Integer = 8 + dataLen
            Dim buf(total - 1) As Byte

            BitConverter.GetBytes(IO_REPARSE_TAG_MOUNT_POINT).CopyTo(buf, 0)
            BitConverter.GetBytes(CUShort(dataLen)).CopyTo(buf, 4)
            BitConverter.GetBytes(CUShort(0)).CopyTo(buf, 6)
            BitConverter.GetBytes(CUShort(0)).CopyTo(buf, 8)
            BitConverter.GetBytes(CUShort(subBytes.Length)).CopyTo(buf, 10)
            BitConverter.GetBytes(CUShort(subBytes.Length + 2)).CopyTo(buf, 12)
            BitConverter.GetBytes(CUShort(printBytes.Length)).CopyTo(buf, 14)
            Array.Copy(subBytes, 0, buf, 16, subBytes.Length)
            Array.Copy(printBytes, 0, buf, 16 + subBytes.Length + 2, printBytes.Length)

            Dim returned As UInteger = 0
            Return DeviceIoControl(h, FSCTL_SET_REPARSE_POINT, buf, CUInt(buf.Length), Nothing, 0UI, returned, IntPtr.Zero)
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：写入 junction 失败（{LinkPath}）")
            Return False
        Finally
            If h <> INVALID_HANDLE_VALUE Then CloseHandle(h)
        End Try
    End Function

    ''' <summary>
    ''' 把链接目标从旧根重定位到新根。
    ''' </summary>
    ''' <remarks>
    ''' pnpm 写的链接目标是**绝对路径**，指向旧的数据根。
    ''' 迁移后必须改写成新根下的对应路径，否则链接全部指向已删除的旧位置。
    ''' 目标不在旧根之内（例如指向全局 store）时原样返回 —— 那种链接本来就不该动。
    ''' </remarks>
    Public Function DshRebaseLinkTarget(Target As String, OldRoot As String, NewRoot As String) As String
        If String.IsNullOrWhiteSpace(Target) Then Return Target
        If String.IsNullOrWhiteSpace(OldRoot) OrElse String.IsNullOrWhiteSpace(NewRoot) Then Return Target
        Try
            Dim oldAbs As String = System.IO.Path.GetFullPath(OldRoot).TrimEnd(BS)
            Dim newAbs As String = System.IO.Path.GetFullPath(NewRoot).TrimEnd(BS)
            Dim tgtAbs As String = System.IO.Path.GetFullPath(Target)
            If String.Equals(oldAbs, newAbs, StringComparison.OrdinalIgnoreCase) Then Return tgtAbs

            ' 只改写「严格位于旧根之下」的目标
            If tgtAbs.StartsWith(oldAbs & BS_STR, StringComparison.OrdinalIgnoreCase) Then
                Return newAbs & tgtAbs.Substring(oldAbs.Length)
            End If
            Return tgtAbs
        Catch ex As Exception
            Logger.Warn(ex, $"DSH：重定位链接目标失败（{Target}）")
            Return Target
        End Try
    End Function

#End Region

#Region "底层复制 / 删除"

    ''' <summary>复制统计结果。</summary>
    Public Class CopyStats
        Public Property FileCount As Integer = 0
        Public Property TotalBytes As Long = 0
        ''' <summary>重建的符号链接 / junction 数量。</summary>
        Public Property LinkCount As Integer = 0
        ''' <summary>链接重建失败的数量（失败不致命，但要报给用户）。</summary>
        Public Property LinkFailed As Integer = 0
    End Class

    ''' <summary>
    ''' 复制一棵目录树，**保留符号链接语义**（快照等功能复用）。
    ''' </summary>
    ''' <param name="SourceRoot">源根目录。</param>
    ''' <param name="TargetRoot">目标根目录。</param>
    ''' <param name="SkipNames">要跳过的条目名（大小写不敏感）；Nothing 表示不跳过任何东西。</param>
    ''' <param name="Progress">进度回调，可为 Nothing。</param>
    ''' <remarks>
    ''' 「快速快照」用它配合 <paramref name="SkipNames"/> 跳过 <c>node_modules</c>
    ''' 之类的依赖目录 —— 依赖靠重装恢复，不必进快照。
    ''' 跳过的判定对**目录和文件都生效**，且只按名字比（不按路径）。
    ''' </remarks>
    Public Function DshCopyTreePreservingLinks(SourceRoot As String, TargetRoot As String,
                                               Optional SkipNames As String() = Nothing,
                                               Optional Progress As DshMigrateProgressHandler = Nothing) As CopyStats
        Return CopyDirectoryRecursive(SourceRoot, TargetRoot, Progress, SkipNames)
    End Function

    ''' <summary>
    ''' 递归复制目录，带进度上报与长路径处理。
    ''' </summary>
    ''' <remarks>
    ''' 刻意**不用** <c>Directory.GetFiles(..., AllDirectories)</c> 枚举：
    ''' 那个 API 遇到任何一层没权限就整体抛出，会让整个迁移失败。
    ''' 这里改成自己按层遍历，单层失败只跳过那一层。
    ''' </remarks>
    Private Function CopyDirectoryRecursive(SourceRoot As String,
                                            TargetRoot As String,
                                            Progress As DshMigrateProgressHandler,
                                            Optional SkipNames As String() = Nothing) As CopyStats
        Dim stats As New CopyStats()
        Dim startTicks As Long = Environment.TickCount
        ' 待重建的符号链接（链接路径 → 目标路径），复制全部结束后统一创建
        Dim pendingLinks As New List(Of KeyValuePair(Of String, String))()
        ' 要跳过的条目名（快速快照用它排除 node_modules 之类）
        Dim skip As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        If SkipNames IsNot Nothing Then
            For Each s As String In SkipNames
                If Not String.IsNullOrWhiteSpace(s) Then skip.Add(s.Trim())
            Next
        End If

        ' 先数一遍总数，进度才有意义
        Dim totalFiles As Integer = CountFiles(SourceRoot)
        Dim done As Integer = 0

        Dim stack As New Stack(Of KeyValuePair(Of String, String))
        stack.Push(New KeyValuePair(Of String, String)(SourceRoot.TrimEnd(BS), TargetRoot.TrimEnd(BS)))

        While stack.Count > 0
            If Environment.TickCount - startTicks > CopyTimeoutMs Then
                Throw New TimeoutException($"复制超时（已超过 {CopyTimeoutMs \ 60000} 分钟）。")
            End If

            Dim pair = stack.Pop()
            Dim srcDir = pair.Key
            Dim dstDir = pair.Value

            Try
                If Not Directory.Exists(dstDir) Then Directory.CreateDirectory(dstDir)
            Catch ex As Exception
                Logger.Warn($"DSH：创建目标目录失败（{dstDir}）：{ex.Message}")
                Continue While
            End Try

            ' 复制本层的文件
            Dim files As String() = {}
            Try
                files = Directory.GetFiles(srcDir)
            Catch ex As Exception
                Logger.Warn($"DSH：枚举源目录失败（{srcDir}）：{ex.Message}")
            End Try

            For Each filePath In files
                Dim name = Path.GetFileName(filePath)
                If skip.Contains(name) Then Continue For
                Dim dstFile = Path.Combine(dstDir, name)
                Try
                    ' ⚠️ 文件级符号链接同样要重建，不能解引用成内容副本。
                    '    这里只**登记**，真正的创建放到复制全部结束之后 ——
                    '    理由见下面 pendingLinks 的说明。
                    If DshIsReparsePoint(filePath) Then
                        Dim fTgt As String = DshReadLinkTarget(filePath)
                        If Not String.IsNullOrWhiteSpace(fTgt) Then
                            Dim fNew As String = DshRebaseLinkTarget(fTgt, SourceRoot, TargetRoot)
                            pendingLinks.Add(New KeyValuePair(Of String, String)(dstFile, fNew))
                            done += 1
                            Continue For
                        End If
                    End If

                    ' 长路径要用 \\?\ 前缀的 API
                    CopyFileLongPath(filePath, dstFile)
                    stats.FileCount += 1
                    Try
                        stats.TotalBytes += New FileInfo(filePath).Length
                    Catch
                        ' 大小读不到就算了，只是个进度展示
                    End Try
                Catch ex As Exception
                    Logger.Warn($"DSH：复制文件失败（{filePath}）：{ex.Message}")
                End Try

                done += 1
                If totalFiles > 0 Then
                    ' 进度只占到 90%，留 10% 给校验和删除阶段
                    Report(Progress, "复制",
                           $"正在复制（{done}/{totalFiles}）：{name}", done / totalFiles * 0.9)
                ElseIf done Mod 200 = 0 Then
                    Report(Progress, "复制", $"正在复制：已处理 {done} 个文件", -1)
                End If
            Next

            ' 把子目录压栈（深度优先，与递归等价）
            Dim dirs As String() = {}
            Try
                dirs = Directory.GetDirectories(srcDir)
            Catch ex As Exception
                Logger.Warn($"DSH：枚举子目录失败（{srcDir}）：{ex.Message}")
            End Try

            For Each subDir In dirs
                Dim name = Path.GetFileName(subDir)
                If skip.Contains(name) Then Continue For
                Dim dstSub = Path.Combine(dstDir, name)

                ' ⭐ 关键分支：链接**不递归进去**，而是在目标位置重建
                '    （递归进去 = 解引用 = 把链接图摊平成真实文件树，见本文件顶部的说明）
                '    只登记，不立刻创建 —— 理由见 pendingLinks 的说明。
                If DshIsReparsePoint(subDir) Then
                    Dim linkTgt As String = DshReadLinkTarget(subDir)
                    If String.IsNullOrWhiteSpace(linkTgt) Then
                        stats.LinkFailed += 1
                    Else
                        Dim newTgt As String = DshRebaseLinkTarget(linkTgt, SourceRoot, TargetRoot)
                        pendingLinks.Add(New KeyValuePair(Of String, String)(dstSub, newTgt))
                    End If
                    Continue For
                End If

                stack.Push(New KeyValuePair(Of String, String)(subDir, dstSub))
            Next
        End While

        ' ── 第二趟：统一重建符号链接 ──
        ' ⭐ 为什么必须放到复制之后（实测踩过，第一版就是错的）：
        '   pnpm 的链接目标是 `.pnpm/<pkg>@<ver>/node_modules/<pkg>` 这类**同树内路径**。
        '   第一版在遍历到链接时就立刻创建，但那时目标目录**可能还没被复制到**
        '   （深度优先顺序不保证目标先于链接出现）→ 目标不存在 → 创建失败。
        '   实测：2475 个链接里当场失败 1422 个。
        '   放到最后统一创建，所有目标都已经落地，才能全部成功。
        '
        '   另外链接可能指向另一个链接（pnpm 的 hoisted 布局里就有），
        '   所以要**多轮直到没有进展**，而不是只跑一遍。
        Dim roundNo As Integer = 0
        While pendingLinks.Count > 0 AndAlso roundNo < 4
            roundNo += 1
            Dim stillPending As New List(Of KeyValuePair(Of String, String))
            For Each pair In pendingLinks
                If DshCreateJunction(pair.Key, pair.Value) Then
                    stats.LinkCount += 1
                Else
                    stillPending.Add(pair)
                End If
            Next
            If stillPending.Count = pendingLinks.Count Then
                ' 一轮下来一个都没成功 → 再跑也没用，收工
                stats.LinkFailed += stillPending.Count
                Exit While
            End If
            pendingLinks = stillPending
        End While
        If pendingLinks.Count > 0 Then
            stats.LinkFailed += pendingLinks.Count
        End If
        If roundNo > 1 Then Logger.Info($"DSH：符号链接分 {roundNo} 轮重建完成")

        Return stats
    End Function

    ''' <summary>
    ''' 复制单个文件，自动处理超过 260 字符的长路径。
    ''' </summary>
    ''' <remarks>
    ''' dsh 的依赖树里有形如
    ''' <c>node_modules/.pnpm/@scope+pkg@1.2.3/node_modules/@scope/pkg/dist/...</c>
    ''' 的深层路径，拼出来常常超过 MAX_PATH。
    ''' 加 <c>\\?\</c> 前缀让 Win32 API 绕过这个限制。
    ''' </remarks>
    Private Sub CopyFileLongPath(Source As String, Target As String)
        If Source.Length < 250 AndAlso Target.Length < 250 Then
            ' 短路径走普通 API —— 更快，且有更好的错误信息
            File.Copy(Source, Target, True)
            Return
        End If

        Dim srcLong As String = ToLongPath(Source)
        Dim dstLong As String = ToLongPath(Target)
        If Not CopyFileW(srcLong, dstLong, False) Then
            Dim err As Integer = Marshal.GetLastWin32Error()
            Throw New IOException($"CopyFileW 失败（Win32 错误 {err}）：{Source}")
        End If
    End Sub

    ''' <summary>删除整个目录，自动处理长路径。</summary>
    Private Sub DeleteDirectoryLongPath(Target As String)
        DeleteDirectoryRobust(Target)
    End Sub

    ''' <summary>
    ''' 健壮地删除整个目录树（清只读属性 + 长路径 + 链接安全）。
    ''' </summary>
    ''' <remarks>
    ''' ⭐ 为什么不能直接用 <c>Directory.Delete(path, True)</c>：
    '''
    ''' <c>Directory.Delete</c> 遇到**带 <c>ReadOnly</c> 属性的文件会直接抛
    ''' <c>UnauthorizedAccessException</c>** —— 它不会帮你清属性。
    ''' 而 dsh 的附件是**内容寻址存储**（<c>attachments\v1\objects\&lt;xx&gt;\&lt;hash&gt;</c>），
    ''' 写完就设只读防篡改，所以那些文件全是只读的。
    ''' 结果：快照一旦包含 <c>attachments</c>，删除就必然失败。
    '''
    ''' ⚠️ 关键：这**不是权限问题，是文件属性问题** —— 所以报错里提示的
    ''' 「以管理员身份运行」**根本没有用**（实测确认过）。
    ''' 正确做法是把 <c>ReadOnly</c> 清掉再删。
    '''
    ''' 另外两个细节：
    ''' <list type="bullet">
    ''' <item>用**栈**做后序遍历，先删完子项再删空目录 —— 避免递归过深</item>
    ''' <item>遇到**重解析点（符号链接 / junction）时只删链接本身，不跟进目标** ——
    '''       否则会把链接指向的真实数据一起删掉（快照里可能含 link: 插件的链接）</item>
    ''' </list>
    ''' </remarks>
    Public Sub DeleteDirectoryRobust(Target As String)
        If String.IsNullOrWhiteSpace(Target) Then Return
        If Not Directory.Exists(Target) Then Return

        ' 快路径：先试一次常规删除。全部可写时这是最快的路径。
        Try
            Directory.Delete(Target, True)
            Return
        Catch ex As UnauthorizedAccessException
            ' 大概率是只读属性 —— 走下面的清属性流程
            Logger.Info($"DSH：常规删除被拒（{Target}），改走清属性流程：{ex.Message}")
        Catch ex As Exception
            Logger.Warn($"DSH：常规删除失败（{ex.Message}），尝试清属性 + 长路径")
        End Try

        ' 后序遍历：先把所有子项删掉，最后删空目录
        Dim dirs As New List(Of String)
        Dim stack As New Stack(Of String)
        stack.Push(Target)
        While stack.Count > 0
            Dim dir As String = stack.Pop()
            dirs.Add(dir)
            Try
                For Each subDir As String In Directory.GetDirectories(dir)
                    ' ⚠️ 重解析点只删链接本身，不递归进去 —— 否则会删掉目标里的真实数据
                    Dim isLink As Boolean = False
                    Try
                        isLink = (New DirectoryInfo(subDir).Attributes And FileAttributes.ReparsePoint) <> 0
                    Catch ex As Exception
                        Logger.Warn($"DSH：读取 {subDir} 属性失败（按普通目录处理）")
                    End Try
                    If isLink Then
                        Try
                            Directory.Delete(subDir, False)
                        Catch ex As Exception
                            Logger.Warn($"DSH：删除链接失败（{subDir}）：{ex.Message}")
                        End Try
                    Else
                        stack.Push(subDir)
                    End If
                Next
            Catch ex As Exception
                Logger.Warn($"DSH：枚举待删目录失败（{dir}）：{ex.Message}")
            End Try

            Try
                For Each f As String In Directory.GetFiles(dir)
                    Try
                        ' ⭐ 关键一步：清掉只读等属性，否则 Delete 会抛 UnauthorizedAccessException
                        Dim fa As FileAttributes = File.GetAttributes(f)
                        If (fa And FileAttributes.ReadOnly) <> 0 Then
                            File.SetAttributes(f, fa And Not FileAttributes.ReadOnly)
                        End If
                        File.Delete(f)
                    Catch ex As Exception
                        Logger.Warn($"DSH：删除文件失败（{f}）：{ex.Message}")
                    End Try
                Next
            Catch ex As Exception
                Logger.Warn($"DSH：枚举待删文件失败（{dir}）：{ex.Message}")
            End Try
        End While

        ' 倒序（子目录在前）删空目录
        For i As Integer = dirs.Count - 1 To 0 Step -1
            Try
                Directory.Delete(dirs(i), False)
            Catch ex As Exception
                Logger.Warn($"DSH：删除目录失败（{dirs(i)}）：{ex.Message}")
            End Try
        Next
    End Sub

    <DllImport("kernel32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Function CopyFileW(<MarshalAs(UnmanagedType.LPWStr)> ExistingFileName As String,
                               <MarshalAs(UnmanagedType.LPWStr)> NewFileName As String,
                               FailIfExists As Boolean) As Boolean
    End Function

    ''' <summary>给绝对路径加上 <c>\\?\</c> 长路径前缀。</summary>
    ''' <remarks>
    ''' 必须是**已归一化的绝对路径**：前缀模式下 Win32 不做任何路径解析，
    ''' 出现 <c>..</c> 或 <c>/</c> 反而会失败。
    ''' </remarks>
    Private Function ToLongPath(Path As String) As String
        If Path.StartsWith(LongPathPrefix, StringComparison.Ordinal) Then Return Path
        Dim full As String = System.IO.Path.GetFullPath(Path)
        ' UNC 路径要用 \\?\UNC\ 前缀，形式不同
        If full.StartsWith("\\", StringComparison.Ordinal) Then
            Return LongPathPrefix & "UNC\" & full.Substring(2)
        End If
        Return LongPathPrefix & full
    End Function

    ''' <summary>统计目录里的文件总数（用于进度与校验）。</summary>
    ''' <remarks>
    ''' ⚠️ **不跟随重解析点**。这一点很关键：
    ''' pnpm 的 <c>node_modules</c> 是链接图，同一个包会被多个链接指到。
    ''' 跟随的话同一个文件会被重复计数 ——
    ''' 实测整个数据目录真实是 44598 个文件，跟随链接数出来是 212091 个。
    ''' 而「源/目标文件数一致」是迁移的**完整性校验**，两边都虚高就会一致通过，
    ''' 校验等于没做（这正是旧版的缺陷之一）。
    ''' </remarks>
    Public Function CountFiles(Root As String) As Integer
        Return CountFiles(Root, False)
    End Function

    ''' <summary>
    ''' 统计文件数。
    ''' </summary>
    ''' <param name="Root">起始目录。</param>
    ''' <param name="FollowLinks">是否跟随重解析点。默认 False（见 <see cref="CountFiles"/> 的说明）。</param>
    Public Function CountFiles(Root As String, FollowLinks As Boolean) As Integer
        If Not Directory.Exists(Root) Then Return 0
        Dim count As Integer = 0
        Dim stack As New Stack(Of String)
        stack.Push(Root)
        While stack.Count > 0
            Dim curDir = stack.Pop()
            Try
                count += Directory.GetFiles(curDir).Length
            Catch ex As Exception
                Logger.Warn($"DSH：统计文件数失败（{curDir}）：{ex.Message}")
            End Try
            Try
                For Each subDir In Directory.GetDirectories(curDir)
                    If Not FollowLinks AndAlso DshIsReparsePoint(subDir) Then Continue For
                    stack.Push(subDir)
                Next
            Catch ex As Exception
                Logger.Warn($"DSH：统计子目录失败（{curDir}）：{ex.Message}")
            End Try
        End While
        Return count
    End Function

    ''' <summary>统计目录里的重解析点数量（迁移前预检用）。</summary>
    Public Function CountLinks(Root As String) As Integer
        If Not Directory.Exists(Root) Then Return 0
        Dim count As Integer = 0
        Dim stack As New Stack(Of String)
        stack.Push(Root)
        While stack.Count > 0
            Dim curDir = stack.Pop()
            Try
                For Each subDir In Directory.GetDirectories(curDir)
                    If DshIsReparsePoint(subDir) Then
                        count += 1
                        Continue For
                    End If
                    stack.Push(subDir)
                Next
            Catch ex As Exception
                Logger.Warn($"DSH：统计链接失败（{curDir}）：{ex.Message}")
            End Try
        End While
        Return count
    End Function

    ''' <summary>统计目录总字节数。</summary>
    Public Function MeasureDirectorySize(Root As String) As Long
        If Not Directory.Exists(Root) Then Return 0L
        Dim total As Long = 0
        Dim stack As New Stack(Of String)
        stack.Push(Root)
        While stack.Count > 0
            Dim dir = stack.Pop()
            Try
                For Each f In Directory.GetFiles(dir)
                    Try
                        total += New FileInfo(f).Length
                    Catch
                        ' 单个文件读不到就跳过
                    End Try
                Next
            Catch ex As Exception
                Logger.Warn($"DSH：统计目录大小失败（{dir}）：{ex.Message}")
            End Try
            Try
                For Each subDir In Directory.GetDirectories(dir)
                    stack.Push(subDir)
                Next
            Catch ex As Exception
                Logger.Warn($"DSH：统计子目录失败（{dir}）：{ex.Message}")
            End Try
        End While
        Return total
    End Function

    ''' <summary>判断两个路径是否在同一个卷上。</summary>
    ''' <remarks>
    ''' 只比较盘符就够用 —— 本功能只可能被用在 <c>C:\</c>、<c>D:\</c> 这类盘符路径上。
    ''' </remarks>
    Private Function SameVolume(Left As String, Right As String) As Boolean
        Try
            Dim l = Path.GetPathRoot(Path.GetFullPath(Left))
            Dim r = Path.GetPathRoot(Path.GetFullPath(Right))
            If String.IsNullOrWhiteSpace(l) OrElse String.IsNullOrWhiteSpace(r) Then Return False
            Return String.Equals(l, r, StringComparison.OrdinalIgnoreCase)
        Catch ex As Exception
            Logger.Warn(ex, "DSH：判断卷标失败，按跨盘处理")
            Return False
        End Try
    End Function

#End Region

#Region "进度上报"

    ''' <summary>包一层，避免回调自己抛异常把迁移带崩。</summary>
    Private Sub Report(Progress As DshMigrateProgressHandler,
                       Stage As String, Message As String, Value As Double)
        If Progress Is Nothing Then Return
        Try
            Progress(Stage, Message, Value)
        Catch ex As Exception
            Logger.Warn(ex, "DSH：迁移进度回调抛异常，已忽略")
        End Try
    End Sub

#End Region

End Module
