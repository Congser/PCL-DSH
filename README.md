> # 📦 本仓库已归档
>
> **归档原因：因个人原因，插件暂停更新，源码继续公开。**
>
> 仓库已设为**只读**（不再接受 issue / PR / 推送），但源码、文档与历史版本
> 仍然可以自由查看、克隆、fork 与使用。
>
> 最后发布版本：[v1.3.1](https://github.com/Congser/PCL-DSH/releases/tag/v1.3.1)
>
> 感谢每一位使用和反馈过的人。

---

# Plain Craft Launcher (PCL) · DSH 改版

> 把 **DeepSeek Harness (`dsh`)** 装进图形界面的启动器。
> 基于 [Plain Craft Launcher (PCL)](https://github.com/Meloong-Git/PCL) 的**第三方独立二创**，
> 已剥离全部 Minecraft 功能。

---

## ⚠️ 来源声明

本仓库是**第三方基于 PCL 独立进行二次创作的产物**，与 PCL 官方**没有任何关系**。

- 上游项目：**Plain Craft Launcher (PCL)**
- 原作者：**龙腾猫跃**（[GitHub](https://github.com/Meloong-Git/PCL) · [爱发电](https://meloong.com/afd/a/LTCat)）
- 本改版遵循上游的《PCL 存储库合理使用指南》（**重度使用**），并已满足其全部要求：
  1. ✅ 明确声明这是第三方独立二创
  2. ✅ 名称以 `Plain Craft Launcher (PCL)` 开头，后缀 `DSH 改版` 表明由第三方修改
  3. ✅ 界面「关于与鸣谢」页**首位**署名龙腾猫跃并提供赞助入口；本仓库公开源码；保留 `LICENCE` 原文
  4. ✅ **不含** Minecraft 启动功能（已整体剥离），因此不涉及正版购买劝导条款
  5. ✅ 界面主色相为**蓝色**，不含任何与赞助解锁类似的功能
- 完整许可文本见仓库根目录的 [`LICENCE`](LICENCE)（请勿删除或替换）

> 如果你喜欢这个壳子，请去支持原作者。**本项目不接收任何赞助。**

---

## 这是什么

`dsh` 是一个命令行工具。这个启动器做的事，是让你**不用碰命令行**就能：

- 一键装配运行环境（Node / pnpm / dsh 本体）
- 管理多个互相隔离的 dsh 实例
- 装插件（对接收录 4000+ 插件的社区索引站）
- 配 API Key、切换模型接入方式（官方 / 自定义网关）
- 换 dsh 版本、换镜像源
- 排查问题（环境体检 / 一键修复 / AI 分析日志 / 快照回滚）

**dsh 的界面本体仍在浏览器里** —— 本项目刻意**不复刻** dsh 的 Web UI，
只做「启动器 + 文件管理器」这一层。界面交给系统浏览器承载，
这样你可以用自己熟悉的书签、扩展与密码管理器。

---

## ⚠️ 关于「多实例」：每个实例是完全独立的

**这一点很重要，很多人会以为多个实例共享同一份数据 —— 不是的。**

每个实例都有自己的 `DSH_HOME`（`<数据根>\instances\inst_<id>\`），
下面这些东西**各实例完全独立、互不影响**：

| 项目 | 说明 |
|---|---|
| **对话历史** | 各自一套。在实例 A 聊的天，实例 B 里看不到 |
| **API Key** | 各自一套。可以给不同实例配不同的 Key / 网关 |
| **插件** | 各自一套。可以给实例 A 装一堆插件，实例 B 保持干净 |
| **配置**（settings / 审批白名单 / 外观） | 各自一套 |

**共用的只有一样：dsh 运行时**（Node + dsh 本体）。
所以开多个实例**不会**重复占用几个 G 的磁盘。

### 这意味着什么

- ✅ **好处**：可以拿一个实例当"试验田"乱装插件，另一个保持稳定；
  也可以一个实例接官方 API、另一个接便宜的自定义网关
- ⚠️ **代价**：在实例 A 里装好的插件、配好的 Key、聊过的天，
  **不会自动出现在实例 B**，需要手动搬

### 怎么在实例之间搬东西

| 想搬什么 | 怎么做 |
|---|---|
| **对话历史** | 设置 → 启动 → 维护与修复 → **对话历史** → 选来源、勾目标、点复制（**只补缺**，目标已有的会话不会被改动） |
| **插件 + API Key** | 用「从其他环境导入」把实例 A 的数据目录当源环境导入到实例 B |
| **只想找某个会话在哪个实例** | 各实例数据目录下的 `sessions\` 就是它的全部对话 |

> 💡 如果你希望所有对话都在一处，最简单的办法是**只用一个实例**。
> 多实例适合"要隔离"的场景，不适合"要共享"的场景。


---

## 已完成的功能

### 运行时与实例
- [x] **一键装配**：自动探测/安装 Node、pnpm、dsh；可复用系统已有的 Node
- [x] **多实例**：每个实例有完全独立的数据目录（插件 / 密钥 / 会话分开），**共用同一份运行时**
- [x] **运行时槽位**：官方安装的 + 导入的便携包可以共存并随时切换；切换只改一个 `active` 字段
- [x] **导入运行时**：把一个便携包（zip 或文件夹）导入为可切换的槽位
- [x] **数据目录迁移**：跨盘搬迁整个数据根，支持搬回默认位置

### 配置
- [x] **模型接入方式**：官方 API / 自定义网关，按 provider 隔离密钥
- [x] **密钥管理**：直接写 dsh 自己的 `.credentials.yaml`，支持共享密钥
- [x] **镜像源与更新通道**：稳定 / 预览

### 插件
- [x] **插件社区**：索引站数据源（静态 JSON，无配额）+ GitHub topic 回退源
- [x] **搜索 / 分类 / 排序 / 分页**（4062 条按 60 条一页）
- [x] **本地插件导入**（GitHub 链接 / 文件夹 / 剪贴板）
- [x] **从其他环境导入**：先探测，再选范围
  - **仅配置**：插件 + API Key + profile 配置
  - **全量导入**：再加上对话正文（`sessions`）、会话索引、附件、
    任务看板、审批白名单、外观数据 —— 共 14 项

### 诊断与维护
- [x] **环境体检** + **一键修复**
- [x] **AI 分析日志**：日志脱敏后交给 DeepSeek 分析（官方优先，失败回退自定义网关），
      给出问题原因与处理建议。**只给建议，不会自动替你改任何东西**
- [x] **实例快照**：快速（只存配置与使用数据，几 MB）/ 完整（含依赖，可离线恢复）两档；
      恢复前自动备份当前状态
- [x] **对话历史跨实例复制**：每个实例的对话历史是独立的，
      这里可以选一个实例当来源，把它有、其他实例没有的会话复制过去
      （**只补缺** —— 目标已有的会话一个都不动）
- [x] **插件配置跨实例复制**：把实例 A 调好的插件组合搬到实例 B
      （插件 + profile 配置，可选连 API Key 一起）。
      插件会**重新安装**而不是复制 `node_modules`，这样 pnpm 锁文件才一致
- [x] **启动前自愈**：
  - 修掉 profile patch 里的 YAML 空数组占位符（第三方插件会写坏它）
  - 补上缺失的本地插件符号链接

### 数据安全相关（踩过的坑都修了）
- [x] **迁移保留符号链接语义**：pnpm 的 `node_modules` 是链接图，
      直接按文件树复制会把它摊平（实测文件数从 44598 膨胀到 212091），
      而且链接目标是**绝对路径**，必须重定位到新根
- [x] **实例 Id 白名单校验**：防止被手工编辑过的 `instances.json` 造成目录穿越
- [x] **删除实例的路径越界检查**（纵深防御）
- [x] **命令行参数转义**：Windows 命令行靠双写 `""` 转义，不是 `\"`

---

## 🐛 已修复的问题

### #1 全量导入不会带过来聊天记录

**状态**：✅ **已修复**（2026-09-22）

**现象**

「从其他环境导入 → 全量导入」之后，插件和 API Key 都正常，
但**历史对话在新环境里看不到** —— 会话列表能看到，点进去是空的。

**根因**

同步的数据项清单在
`Plain Craft Launcher 2/Modules/DSH/DshProfileSync.vb` 的常量
`DshSyncDataEntries`：

```vb
Private ReadOnly DshSyncDataEntries As String() = {
    "storages",
    "dsh-session-archive",
    "task-board",
    "auto-approve",
    "whale-audio",
    "whale-roles",
    "pet.json",
    "skin-center-active.json"
}
```

**漏掉了 `sessions`** —— 而 dsh 真正的对话数据就存在 `$DSH_HOME/sessions/` 下，
按工作区组织，每个会话一个文件：

```
$DSH_HOME/sessions/
    --D-harness--/
        session-86ef94e4-.../
            session.v3.jsonl.zstd      ← 对话正文在这里
    --C-Users-xxx-Downloads-1145--/
        session-2b573414-.../
            session.v3.jsonl.zstd
```

清单里的 `storages/` 只包含**索引与缓存**
（`storages/session_projcache/sessions/*.json`、`cost-meter/ledger.json`），
所以导入后会出现「索引在、正文不在」—— 列表里能看到会话，
点进去却打不开，或者干脆是空的。

**修法**

1. **`sessions` 加进 `DshSyncDataEntries`** —— 顺带补上
   `attachments` / `pets` / `skins` / `skin-center` / `whale-bubble-imgs`，
   清单从 8 项变成 14 项：

   ```vb
   Private ReadOnly DshSyncDataEntries As String() = {
       "sessions",          ' ← 对话正文（本次缺失的就是它）
       "storages",          ' 会话索引 / 缓存
       "dsh-session-archive",
       "attachments",
       "task-board",
       "auto-approve",
       "pets",
       "skins",
       "skin-center",
       "whale-audio",
       "whale-roles",
       "whale-bubble-imgs",
       "pet.json",
       "skin-center-active.json"
   }
   ```

2. **把数据收集提到插件解析之前** —— 原来它在插件解析**之后**，
   只要 `package.json` 缺失或解析失败就会提前 `Return`，
   全量导入会**静默地什么都不搬**。这种「看起来成功但什么都没做」的失败最难查。

**验证**

在真实环境跑了一遍：

| | 源环境 `~/.dsh` | 导入后目标实例 |
|---|---|---|
| `sessions` 文件数 | 12 | **12** ✅ |
| 工作区目录数 | 5 | **5** ✅ |

界面提示也从「使用数据：8 项（153.5 KB）」变成「**14 项（26.8 MB）**」。

**遗留注意事项**

`sessions/` 的工作区目录名是**路径编码**（`--D-harness--` 对应 `D:\harness`）。
换机器后原路径可能不存在，会话能打开但工作区要重新选。
这是 dsh 的存储格式决定的，不是本项目的 bug，界面文案里已经提醒用户。

### #2 启动时会在自己目录里凭空生成 `.minecraft`

**状态**：✅ **已修复**（2026-09-22）

**现象**

一个声称「剥离全部 Minecraft 功能」的软件，每次启动都会在自己的目录里
创建一个 `.minecraft\`（含空的 `versions\` 和 `launcher_profiles.json`）。

**根因**

上游 PCL 的 `McFolderListLoadSub`（`Modules/Minecraft/ModMinecraft.vb`）
在扫不到任何 MC 文件夹时会兜底创建一个：

```vb
'若没有可用文件夹，则创建 .minecraft
If Not CacheMcFolderList.Any() Then
    DirectoryUtils.Create(Paths.Base & ".minecraft\versions\")
    ...
End If
```

PCL_DSH 剥离 MC 功能时没动这段，于是它一直在跑。

**影响**

功能上无影响（1 KB 空壳，且 `McFolderLauncherProfilesJsonCreate` 对已存在的
`launcher_profiles.json` 会直接返回、**不覆盖**）。
但**自相矛盾**，而且有个真实风险：如果用户把 PCL_DSH 解压进**真实的 MC 目录**，
上游逻辑会把那个目录识别成「当前文件夹」并写进 `LaunchFolders` 设置，无谓地污染它。

**修法**

在 `McFolderListLoadSub` 开头加早退（**只加判断、不删上游代码** —— 删了会和上游合并冲突）：

```vb
Logger.Info("PCL_DSH：已跳过 Minecraft 文件夹扫描（本改版不含 MC 功能）")
Return
```

⚠️ **连带改了一处**：`PageLaunchLeft` 里有 `McFolderList.First.Location`，
早退后 `McFolderList` 会是**空列表**，`.First` 会抛
`InvalidOperationException`。已加判空：

```vb
If McFolderList.Any() Then
    McFolderSelected = McFolderList.First.Location
Else
    McFolderSelected = ""
End If
```

**验证**：删掉 `.minecraft` → 启动 → **未被重新创建**，日志出现
`已跳过 Minecraft 文件夹扫描`，且实例加载正常（`已加载 1 个实例`）。

### #3 删除快照报「程序权限不足」，以管理员运行也没用

**状态**：✅ **已修复**（2026-09-22）

**现象**

```
DSH: 删除快照失败: 程序权限不足。请尝试右键程序，选择以管理员身份运行……
对路径"322d8562c425d81a23c785f53dbb2bf7f6cf211216be3522582a99382954de14"的访问被拒绝。
   在 System.IO.Directory.Delete
   在 PCL.DshSnapshot.DshSnapshotDelete
```

**以管理员身份运行**——**照样失败**。

**根因**

那个路径是 `attachments\v1\objects\32\322d8562...` —— **附件对象文件**。
dsh 的附件是**内容寻址存储**，写完就设 **`ReadOnly` 属性**防篡改。

而 **`Directory.Delete(path, True)` 遇到只读文件会直接抛
`UnauthorizedAccessException`** —— 它**不会**帮你清属性。

> ⚠️ **关键认知**：这**不是权限问题，是文件属性问题** ——
> 所以报错里那句「以管理员身份运行」**根本没有用**。
> 这正是用户反馈"管理员运行也显示这个"的原因。

**修法**

把 `DshMigrate` 里已有的清属性删除逻辑提升为公共工具
`DeleteDirectoryRobust`，并**始终**走清属性流程（原来只在长路径回退时才用）：

1. 先试常规删除（快路径）
2. 失败则后序遍历：**清 `ReadOnly`** → 删文件 → 删空目录
3. ⚠️ 遇到**重解析点（符号链接 / junction）只删链接本身，不递归进去** ——
   否则会把链接指向的真实数据一起删掉

**三个调用点全改了**（后两个是同一类隐患，顺手一起修）：

| 位置 | 说明 |
|---|---|
| `DshSnapshot.DshSnapshotDelete` | 用户报的这个 |
| `DshSnapshot.DshSnapshotRestore` | 恢复时覆盖前的目录删除，同样会踩 |
| `ModDSH` 删除实例数据目录 | 实例数据里也有 `attachments`，隐患完全相同 |

**验证**

- 先精确复现：在含只读文件的快照上调用 `Directory.Delete` →
  失败，报错路径与用户截图**完全一致**
- 修复后：界面点删除 → **成功**，磁盘上快照消失

### #4 插件安装遇到网络超时会长时间卡住，界面只说「正在安装…」

**状态**：✅ **已修复**（2026-09-22）

**现象**

国内直连 npmjs.org 时，安装插件会卡很久，而进度浮层只显示
「正在安装 E:\...\gal-view」—— **用户以为程序死了，强制结束 PCL**，
留下半装的环境（比"等久一点"糟糕得多）。

实测日志：

```
[WARN] GET https://registry.npmjs.org/...tgz error
       (os error 10054 远程主机强迫关闭了一个现有的连接) — 2
[WARN] Will retry in 1m. 0 retries left.
[WARN] Request took 150672ms: https://registry.npmjs.org/@lezer%2Frust
[WARN] Tarball download average speed 25 KiB/s ... is below 50 KiB/s
```

**修法（三处）**

1. **进度透传**：新增 `ReportPnpmLine`，把 pnpm 的关键输出翻译成人话，
   让用户知道"它在动，只是网络慢"：
   - `Will retry in 1m. 0 retries left.` → 「网络不稳，正在重试（还剩 0 次重试）……（可以继续等，不用关掉 PCL）」
   - `operation timed out` → 「下载超时，正在重试……（网络较慢时插件安装可能要几分钟）」
   - `is below ... KiB/s` → 「下载速度较慢，请耐心等待……」
   - `Progress: resolved ... downloaded N` → 「正在下载依赖（已下载 N 个……）」
   - 只识别几类有意义的行，其余忽略 —— 不要把 pnpm 的原始刷屏糊到进度条上

2. **超时从 10 分钟放宽到 30 分钟** —— 实测单次请求能到 150 秒、
   下载速度 25 KiB/s，10 分钟**真的不够**，会被误判成超时然后杀掉一个
   其实在正常推进的安装

3. **网络类失败时主动建议换源**：新增 `IsNetworkFailure`，识别
   `operation timed out` / `os error 10054` / `ECONNRESET` / `Failed to fetch`
   等特征，在错误提示里附上：
   > 看起来是**网络问题**，不是插件本身的问题。当前用的是「npm 官方源」。
   > 建议换成国内镜像源后重试：「下载 → 镜像版本 → 镜像源」里选
   > 「npmmirror（淘宝）」或「腾讯云镜像」。

   （刻意**不**把 404 / 包名拼错算作网络失败 —— 那是另一类问题，换源没用）



---

## 构建

### 环境要求
- Windows
- Visual Studio 2022（含 **.NET 桌面开发** 工作负载）
- .NET Framework 4.8 开发包

### 命令

```powershell
# 编译（Debug）
msbuild "Plain Craft Launcher 2\Plain Craft Launcher 2.vbproj" -p:Configuration=Debug

# 全量重建
msbuild "Plain Craft Launcher 2\Plain Craft Launcher 2.vbproj" -t:Rebuild -p:Configuration=Debug
```

产物在 `Plain Craft Launcher 2\bin\`。直接运行 `Plain Craft Launcher 2.exe` 即可。

> **⚠️ 打包前务必核对 `bin\` 的文件清单。**
> MSBuild 有可能报 0 错误却跳过 `CopyFilesToOutputDirectory`，
> 让 `bin\` 里缺 `Plain Craft Launcher 2.exe.config` 和 `.xml`。
> 缺 config 会导致程序集绑定重定向失效（Newtonsoft.Json 等），
> 表现为偶发的程序集版本不匹配。
>
> `.vbproj` 里必须有这一条才会生成 config：
> ```xml
> <AppConfig>App.config</AppConfig>
> ```

---

## 目录结构

```
Plain Craft Launcher 2/
├─ Modules/DSH/              ← 本项目新增的全部后端逻辑
│   ├─ ModDSH.vb             实例模型、目录骨架、路径推导
│   ├─ DshService.vb         拉起/停止 dsh 进程、端口分配、启动前自愈
│   ├─ DshRuntime.vb         运行时探测、环境播种
│   ├─ DshRuntimeSlot.vb     运行时槽位（多份运行时共存）
│   ├─ DshInstaller.vb       一键装配
│   ├─ DshPluginMarket.vb    插件市场（索引站 + GitHub 回退）
│   ├─ DshProfileSync.vb     从其他环境导入
│   ├─ DshSnapshot.vb        实例快照
│   ├─ DshSessionCopy.vb     对话历史跨实例复制（只补缺）
│   ├─ DshPluginCopy.vb      插件配置跨实例复制（复用「从其他环境导入」）
│   ├─ DshLogAi.vb           AI 分析日志（含脱敏）
│   ├─ DshMigrate.vb         数据目录迁移（含符号链接重建）
│   ├─ DshDoctor.vb          环境体检
│   ├─ DshApiConfig.vb       模型接入方式
│   ├─ DshCredentials.vb     密钥读写
│   └─ …
├─ Pages/PageDSH/            ← 本项目新增的页面
│   ├─ PageDSHLeft            实例列表
│   ├─ PageDSHOnline          在线模式 / 模型接入
│   ├─ PageDSHLog             运行日志 + AI 分析
│   ├─ PageSnapshot           实例快照
│   └─ PageDSHEnv             运行环境（已并入设置页）
├─ Pages/PageDownload/
│   ├─ PageDownloadMirror     镜像版本（原「下载游戏」页改造）
│   └─ PageDownloadPlugin     插件社区（原「下载模组」页改造）
└─ …

docs/                        ← 设计与待办文档
```

**代码组织原则**：所有新增文件都放在独立的目录/文件名下，
MC 相关源码**物理保留**、靠 `.vbproj` 的 `DSH` 编译配置排除，
以便后续跟随上游合并。

---

## 许可

- 本项目遵循上游 **《PCL 分发有限许可》** 与 **《PCL 存储库合理使用指南》**
- 完整文本见 [`LICENCE`](LICENCE) —— **请勿删除或替换该文件**
- `MeloongCore/` 是上游的子项目，其许可见 `MeloongCore/LICENSE.txt`
- 上游原始 README 保留在 [`README-upstream.md.bak`](README-upstream.md.bak)

## 鸣谢

- **龙腾猫跃** —— Plain Craft Launcher 的作者，本项目的地基
- [PCL2Help](https://github.com/LTCatt/PCL2Help) —— 帮助文档库
- 以及所有为 dsh 生态贡献插件的开发者
