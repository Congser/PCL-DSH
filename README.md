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
- [x] **从其他环境导入**：把另一份 dsh 环境的插件 + 密钥 + profile 配置搬过来

### 诊断与维护
- [x] **环境体检** + **一键修复**
- [x] **AI 分析日志**：日志脱敏后交给 DeepSeek 分析（官方优先，失败回退自定义网关），
      给出问题原因与处理建议。**只给建议，不会自动替你改任何东西**
- [x] **实例快照**：快速（只存配置与使用数据，几 MB）/ 完整（含依赖，可离线恢复）两档；
      恢复前自动备份当前状态
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

## 🐛 已知问题

### #1 全量导入不会带过来聊天记录

**状态**：已定位，**尚未修复**（欢迎 PR）

**现象**

「从其他环境导入 → 全量导入」之后，插件和 API Key 都正常，
但**历史对话在新环境里看不到**。

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

把 `sessions` 加进 `DshSyncDataEntries` 即可：

```vb
Private ReadOnly DshSyncDataEntries As String() = {
    "sessions",          ' ← 对话正文（本次缺失的就是它）
    "storages",          ' 会话索引 / 缓存
    "dsh-session-archive",
    "task-board",
    "auto-approve",
    "attachments",       ' 附件
    "pets",              ' 宠物数据
    "skins",             ' 皮肤
    "skin-center",       ' 皮肤中心缓存
    "whale-audio",
    "whale-roles",
    "whale-bubble-imgs",
    "pet.json",
    "skin-center-active.json"
}
```

**顺带注意两点**

1. `sessions/` 的工作区目录名是**路径编码**（`--D-harness--` 对应 `D:\harness`）。
   换机器后原路径可能不存在，会话能打开但工作区要重新选。
   这是 dsh 的存储格式决定的，**不是本项目的 bug**，但界面文案里应该提醒用户。
2. `skin-center` 有十几 MB，全量导入会让体积涨不少 —— 可以考虑做成可勾选项。

**如何验证**

```powershell
# 源环境有几个会话文件
(Get-ChildItem "$env:USERPROFILE\.dsh\sessions" -Recurse -File).Count

# 导入后目标实例应该一样多
(Get-ChildItem "<数据根>\instances\<实例id>\sessions" -Recurse -File).Count
```

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
