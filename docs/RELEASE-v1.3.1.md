# PCL DSH 改版 v1.3.1

> 修复 v1.3 里三处**写死旧目录**导致的连锁问题。建议 v1.3 用户升级。

---

## 修了什么

v1.3 引入了「多版本并存」—— 运行时装进 `runtime\versions\<版本>\`，
不再共用 `runtime\dsh\`。但有**三处代码仍在写死那个旧目录**，
于是当 `runtime\dsh\` 不存在时（比如你清空环境后重新导入），
会出现一串看似无关的问题。

### 症状

用导入的运行时启动，报：

> DSH：实例「默认实例」启动失败：启动 dsh 进程失败: 目录名称无效。
> 错误类型：System.ComponentModel.Win32Exception
> 在 System.Diagnostics.Process.StartWithCreateProcess

体检报告：

> ✗ dsh 安装不完整：缺少 package.json。
> 安装目录：E:\test\runtime\dsh\

点「一键修复」后，**又自动切换到新装的版本**，插件配置在新版本里看不到，
表现成「修了但问题还在」。

### 三处根因

| 位置 | 原来写的 | 问题 |
|---|---|---|
| **启动服务** | `.WorkingDirectory = runtime\dsh\` | 目录不存在 → `Process.Start` 直接抛「目录名称无效」 |
| **体检** | 查 `runtime\dsh\node_modules` | 查错目录 → 必然误报「缺少 package.json」 |
| **一键修复** | 清 `runtime\dsh\node_modules`；装完自动切换 | 该清的没清；装完把用户切到新版本 |

### 修法

**① 启动工作目录：从入口脚本反推**

新增 `DshRuntime.DshDeriveWorkDir` —— 入口的固定形状是
`<运行目录>\node_modules\@deepseek-ai\dsh\lib\bin.js`，
裁掉已知的相对部分就得到运行目录，再校验它真的存在。

不依赖任何全局状态，无论槽位在 `versions\` 还是 `imported\` 下都算得对。
**反推失败时不设工作目录**（用系统默认），而不是设一个错的。

**② 体检：从激活槽位反推**

入口路径本来就是从激活槽位解析的，但依赖树检查却写死旧目录 —— 改为统一从入口反推。

顺带修正一个判据：**导入的运行时不一定有 `.pnpm` 虚拟存储**
（可能是别人机器上装好后直接打包过来的），
所以只在**确认是 pnpm 安装**（存在 `pnpm-workspace.yaml` 或 `.modules.yaml`）时，
才把「store 为空」当成残缺。

**③ 一键修复：清理激活槽位 + 装完切回**

- 清理**当前激活槽位**的 `node_modules`（而不是写死的旧目录）
- 装完后**切回修复前的槽位**，并在结果里说明「已切回原来的运行时」

---

## 验证

实测条件：`runtime\dsh\` 不存在（正是触发条件）。

**修复前**：`Process.Start` 抛「目录名称无效」，启动失败。

**修复后**：
```
命令：node.exe "E:\test\runtime\versions\0.1.6-alpha.2\node_modules\@deepseek-ai\dsh\lib\bin.js"
DSH[默认实例]：服务就绪，端口 19387
状态变更：Running
弹出提示：实例「默认实例」启动成功！
```

---

## 升级方式

解压到**原位置**覆盖同名文件。数据目录不受影响。

从 v1.2 或更早升级的，另见 [`docs/升级说明-v1.2到v1.3.md`](docs/升级说明-v1.2到v1.3.md)。

---

## ⚠️ 来源声明

本仓库是**第三方基于 PCL 独立进行二次创作的产物**，与 PCL 官方**没有任何关系**。

- 上游项目：**Plain Craft Launcher (PCL)**
- 原作者：**龙腾猫跃**（[GitHub](https://github.com/Meloong-Git/PCL) · [爱发电](https://meloong.com/afd/a/LTCat)）
- 本改版**不含** Minecraft 启动功能（已整体剥离）
- 完整许可文本见仓库根目录的 [`LICENCE`](LICENCE)

> 如果你喜欢这个壳子，请去支持原作者。**本项目不接收任何赞助。**
