# 《抽卡版PVZ》修改器工程 —— 交给 AI 的完整说明书（AI 提示词）

> **这份文档解决什么问题**：本工程最反常识的三件事，只丢源码给 AI 必然踩：
> ① **BepInEx 在本作不可用**（游戏 mscorlib 被链接器裁剪，预加载器必崩）→ 必须用**编译期 Cecil 注入**；
> ② **游戏里没有 `TcpListener`** → 面板与 mod 之间只能用**文件命令通道**（写 JSON 文件互相轮询）；
> ③ **编译通过 ≠ 运行期可用**（mscorlib 缺 API → 运行期 `MissingMethodException`）→ 必须跑 `API检查` 工具。
>
> **怎么用**：整篇贴给 AI + 附上你要改的那几个 `.cs`；要求它遵守第 0 章规则与第 15 章红线；
> 先让它输出「改动计划 + 涉及文件 + 验证方法」，你确认后再写代码。
>
> **口径**：涉及游戏内部结构的事实都标注了取证方式（反编译 / 实测 / 工具验证）。未标注的属**推测**，AI 不得当事实使用。

---

## 0. 给 AI 的硬性工作规则

1. **不许引入运行时 Hook 框架**。BepInEx 5 在本作**实测崩**（见 1.4），本工程走「编译期改 `Assembly-CSharp.dll` + 全局启动点」。
2. **不许用被裁剪的 API**。游戏 `mscorlib.dll`/`System.dll` 被 Unity 链接器大面积裁剪：`TcpListener`、`LINQ` 部分方法、`File.AppendAllText`、`DateTime.ToString(string)`、`Application.productName`… **编译期报错或运行期 `MissingMethodException`**。见第 7 章白名单/黑名单。
3. **写完 mod 必须跑 `API检查`**：
   ```powershell
   dotnet run --project 源码\API检查\API检查.csproj -c Release -- Verify <我们的.dll>
   ```
   它拿每个成员引用去游戏程序集里核对。**不通过就不许说"完成"**。
4. **不许用 TCP/HTTP 做 mod↔面板通信**（游戏没有 `TcpListener`）。一律走**文件命令通道**（第 5.3 章）。
5. **改了注入器规则必须重跑 `发布.ps1`**：`工具\安装器\安装器.exe` 是自包含单文件，**规则在编译期嵌进去**（历史事故：加了第 5 条规则但安装器还是第 4 条，且无任何报错）。
6. **改完必须证明"装的是新版"**：核对状态文件 `modVersion` 的构建号 + `MOD\PvzGachaMod.dll` 与游戏内那份 **SHA256 一致**（历史事故：静默装了两天旧 DLL）。
7. **每个功能必须具备三个层次**（缺一层就是假开关）：① `FeatureCatalog`（面板看得见）② `ModSettings`（存得下、重启还在）③ **消费点**（真有代码读它并作用到游戏）。
8. **失败通道必须打通**：命令失败要写 `lastActionOk=false` + 原因，否则面板无法区分"还没到"和"被拒了"，用户只会看到"坏了"。
9. **面板两条铁律**：① 乐观更新（先认下自己的值）② 不要每次 Tick 整页重建 UI。
10. **不许发明 API 签名**：所有游戏成员访问都用 `API检查` 验证过；不确定就先 `Type` / `Member` / `Has` 查询。

---

## 1. 这是什么

### 1.1 一句话
给 **《抽卡版PVZ》0.60.0**（Unity **2022.3.62f3**，**x64 Mono**，非 IL2CPP）做的功能型修改器：
**编译期 Mono.Cecil 注入 `Assembly-CSharp.dll`** + **全局启动点**（`RuntimeInitializeOnLoads.json`）+ **文件命令通道**的双端架构
（游戏内 `PvzGachaMod.dll` ↔ 外部 WPF 面板「抽卡版修改器」）。

### 1.2 交付物（`D:\抽卡版\修改器\`，也是发布根）
| 交付物 | 形态 | 说明 |
|---|---|---|
| MOD | `MOD\PvzGachaMod.dll` | 编译进游戏的补丁程序集（编译期引用 `Assembly-CSharp.dll`，`Private=false`） |
| 注入器 | `工具\安装器\注入器.exe` | Cecil 注入器：`patch` / `restore` / `verify`，带 `.orig` 备份 |
| 安装器 | `工具\安装器\安装器.exe` | 一键安装：注册启动点 + 注入 + 校验（**规则编译期嵌入**） |
| 面板 | `工具\面板\抽卡版修改器.exe` | WPF 自包含单文件（另附 6 个 `*_cor3.dll` 必须同目录） |
| 脚本 | `安装.cmd` / `卸载.cmd` / `使用说明.md` | `安装.cmd` 末尾有 `pause` → 自动化请直接调 `安装器.exe install <游戏目录>` |

**打包位置**（用户指定）：`d:\杂交版关卡\release\最终打包_YYYYMMDD\抽卡版修改器\` + 同级 `抽卡版修改器.zip`。

### 1.3 真实工程规模（`D:\抽卡版\开源`，70 文件 / 11,884 行）
| 目录 | 文件数 | 说明 |
|---|---|---|
| `PvzGachaMod\` | 9 | MOD 本体：`ModBootstrap`(启动点) / `ModRuntime`(每帧) / `ModActions`(功能实现) / `Hooks`(注入钩子) / `ScriptEngine`(脚本引擎) / `SaveData` / `BackgroundKeepAlive` |
| `共享\` | 12 | **跨端共享**：`FeatureCatalog`(功能表) / `ModSettings`(设置) / `CommandApi`(命令分发+失败通道) / `ModLogic`(纯逻辑，可离线单测) / `InstructionSet` / `ScriptParser` / `ChannelFiles`(文件通道) / `SavePaths` / `SaveHash` / `MiniJson` / `ModDiag` / `CapabilityRegistry` |
| `修改器面板\` | 10 | WPF 面板：`MainWindow.xaml.cs`(101KB) / `Skin.xaml` / `SkinHybrid.xaml` / `PanelClient`(文件通道客户端) / `SaveEditorLogic` / `GameInfo` / `ToggleSwitch` / `AcrylicHelper` |
| `注入器\` | 2 | Cecil 注入器（**5 条规则**在 `Program.cs`） |
| `安装器\` | 2 | 安装/卸载 + 启动点注册 |
| `API检查\` | 2 | **成员引用核对工具**（`Verify` / `Type` / `Member` / `Has`） |
| `测试\` | 7 | 自研控制台断言器：LogicTests / ChannelTests / PanelTests / CatalogTests / ScriptTests / JsonTests |
| `工具脚本\` | 12 | PowerShell 自动化：截图 / 点导航 / 端到端验证 / 验证后台运行 / 验证设置持久化… |
| 根 | 6 | `构建.ps1` / `构建MOD.ps1` / `发布.ps1` / `注册启动点.ps1` / `README.md` / `LICENSE` |

### 1.4 为什么不用 BepInEx（**实测推翻，勿再试**）
| 缺失项 | 后果 |
|---|---|
| `System.Reflection.Module.GetPEKind` ✗ | BepInEx 预加载器 `PlatformUtils.SetPlatform()` 崩溃 |
| `System.Reflection.Assembly.LoadFile` ✗ | 绕过上面后 `PreloaderRunner.LoadCriticalAssemblies()` 又崩 |
| 现象 | 游戏能跑，但**没有** `BepInEx\LogOutput.log`，只有 `preloader_<时间戳>.log` 报 `MissingMethodException` |
| Doorstop | **本身是好的**（`winhttp.dll` 代理被加载并执行到我们的代码）；坏的是 BepInEx 预加载器 |
| 结论 | 本作只能「编译期改游戏程序集」，mod 不能依赖运行时反射框架 |

> 存在但不可依赖：`DynamicMethod` / `Reflection.Emit` / `AssemblyResolve` / `GetTypes` / `LoadFrom` 在（但框架缺的正是关键那两个）。

### 1.5 三个工程的关系（别跨工程套方案）
| 工程 | 游戏 | 运行时 | 注入 | 通信 |
|---|---|---|---|---|
| **抽卡版（本工程）** | 抽卡版PVZ 0.60.0 | Unity 2022.3 **Mono（被裁剪）** | Cecil 改 `Assembly-CSharp.dll` | **文件命令通道** |
| 杂交版 | 杂交重制版 0.27 | Godot 4.7 C#（AOT 裁剪） | Cecil 合并/独立 DLL | HTTP `127.0.0.1:28999` |
| 融合版 | 融合版 3.9 | Unity 2022.3 **IL2CPP** | BepInEx 6 (IL2CPP) + Harmony | HTTP `127.0.0.1:27400` |

---

## 2. 开源模式与许可

### 2.1 许可：Apache License 2.0（宽松型，非 copyleft）
**可以**：个人使用/修改/自用分发 ✅、商业使用 ✅、**闭源二次开发** ✅、再许可 ✅、专利授权 ✅
**必须**：保留 `LICENSE`/`NOTICE`、**修改过的文件显著标注「已被修改」**、NOTICE 随分发物传递、**不得使用作者商标**做背书
**免责**：按"现状"提供，不对账号/存档/设备后果负责。

### 2.2 本开源**不包含**
游戏本体、`Assembly-CSharp.dll`、`抽卡版PVZ_Data`、美术资源、`save.json` 样本、签名私钥。
→ AI 提"下载游戏资源""读游戏贴图"这类方案时要注意：**只能要求用户自备正版游戏**。

### 2.3 这是什么 / 边界
- 定位：单机增强 + 便捷操作（免费抽卡 / 指定稀有度 / 自定义货架 / 图鉴解锁 / 实体属性 / 脚本引擎）。
- 边界：**不做绕过付费内容、不做联网作弊**（本作无联机，天然干净）。
- 提醒：改存档会触碰游戏的"作弊者"标记（见 6.2，**仅显示文字，不封玩法**），要如实告知用户。

---

## 3. 环境要求

### 3.1 必需环境
| 项 | 要求 | 为什么 | 自检 |
|---|---|---|---|
| OS | Windows 10/11 x64 | 目标平台 | — |
| PowerShell | **pwsh 7** | ⚠️ PS 5.1 读无 BOM 中文 `.ps1` 按 ANSI 解析 → parse error；脚本一律 `pwsh -File` | `pwsh -v` |
| **.NET Framework 目标包** | **本机没有！** | ⚠️ mod 工程是 `net472 + DisableImplicitFrameworkReferences + NoStdLib + FrameworkPathOverride` → **MSBuild 会用别处的 mscorlib 编译通过，运行期却 `MissingMethodException`** | 见 3.3 |
| .NET SDK | 8+（面板 `net8.0-windows`、工具用） | 面板与工具链 | `dotnet --list-sdks` |
| Mono.Cecil | 注入器依赖（交付目录带 `工具\Mono.Cecil.dll`） | IL 读写 | 编译/运行成功 |
| 游戏本体 | `D:\抽卡版\电脑\抽卡0.60.0正式版` | 注入目标 | 见 3.2 |

### 3.2 关键路径
| 用途 | 路径 |
|---|---|
| 游戏目录 | `D:\抽卡版\电脑\抽卡0.60.0正式版` |
| 待注入程序集 | `抽卡版PVZ_Data\Managed\Assembly-CSharp.dll`（注入器自动备份 `.orig`） |
| 启动点注册 | `抽卡版PVZ_Data\ScriptingAssemblies.json`（加 `PvzGachaMod`）+ `RuntimeInitializeOnLoads.json`（登记 `ModBootstrap.Init`） |
| MOD 落地 | `MOD\PvzGachaMod.dll`（与游戏内那份必须 SHA256 一致） |
| 存档 | `%USERPROFILE%\AppData\LocalLow\MiaoDouzi\抽卡版PVZ\save.json` |
| 反作弊校验 | 同目录 `save.json.md5` |
| 命令文件（面板写） | `修改器命令.json` |
| 状态文件（mod 写） | `修改器状态.json` |
| 设置文件 | `修改器设置.json` |
| 反编译参考 | `D:\抽卡版\_recon\src\`（116 个 .cs，只读参考） |

### 3.3 环境自检（AI 应先让用户跑这个）
```powershell
pwsh -v
dotnet --list-sdks
Test-Path 'D:\抽卡版\电脑\抽卡0.60.0正式版\抽卡版PVZ_Data\Managed\Assembly-CSharp.dll'
Test-Path 'D:\抽卡版\电脑\抽卡0.60.0正式版\抽卡版PVZ_Data\Managed\Assembly-CSharp.dll.orig'   # 干净基准
Get-Process | Where-Object { $_.ProcessName -like '*PVZ*' } | Select-Object Id,ProcessName     # 装机前关游戏
# ★ 最关键的一条：成员引用核对
dotnet run --project 源码\API检查\API检查.csproj -c Release -- Verify 构建产物\PvzGachaMod.dll
```

---

## 4. 目录与文件职责（全景）

### 4.1 `PvzGachaMod\` —— MOD 本体（注入进游戏进程）
| 文件 | 职责 | 关键点 |
|---|---|---|
| `ModBootstrap.cs` | **全局启动点**：`Init()` 由 Unity 在启动时调用（与场景无关，实测在主菜单 `Zhucaidan` 就触发） | 注册方式见 5.1；启动即读设置、起帧循环 |
| `ModRuntime.cs`（24KB） | **每帧驱动**：轮询命令文件 → 分发 → 应用设置 → 写状态文件 | 所有逻辑**必须 try/catch**（一个异常会掐掉整个 Update） |
| `ModActions.cs`（15.5KB） | **功能实现总库**（对应杂交版的 `GameCheats`） | 每个功能一个方法，内部用 `ModLogic` 做纯逻辑 |
| `Hooks.cs`（8.1KB） | **注入钩子的落地端**：注入器插进游戏方法的调用都指向这类静态方法 | 钩子要极轻（几行），重活交给 `ModActions` |
| `ScriptEngine.cs`（10.7KB） | 内嵌脚本引擎（用户可写脚本驱动 mod） | 与 `共享\ScriptParser` 配合 |
| `SaveData.cs` | 存档读写封装（`GameStart.GameData`） | 见 6.1；改完要重算 `save.json.md5` |
| `BackgroundKeepAlive.cs`（5.9KB） | 应对"失焦即冻结"（见 6.5） | — |

### 4.2 `共享\` —— 双端共享（**面板与 mod 都引用同一套**，保证契约一致）
| 文件 | 职责 | 关键点 |
|---|---|---|
| `FeatureCatalog.cs`（18.9KB） | **功能表**：每个功能的 key/中文名/类型/范围/默认值/分组 | 面板靠它渲染 UI；mod 靠它校验；**新增功能第一站** |
| `ModSettings.cs`（3.7KB） | **设置存储**：扁平 `key → value`，读时走 `TrySet`（自动夹取+校验） | 持久化到 `修改器设置.json`；**启动读、每条命令成功后写** |
| `CommandApi.cs`（12.6KB） | **命令分发 + 失败通道** | ⚠️ `Error()` **必须是实例方法**并写 `LastActionOk=false`（见坑 14.x） |
| `ModLogic.cs`（11.8KB） | **纯逻辑**（不含 Unity/引擎依赖） | 所有非平凡判断抽到这里 → **可离线单测**（`测试\LogicTests`） |
| `InstructionSet.cs` / `ScriptParser.cs` | 脚本指令集与解析 | 与 `ScriptEngine` 配套 |
| `ChannelFiles.cs` | **文件通道**读写（命令/状态文件） | 原子写（先写临时文件再替换）避免半截 JSON |
| `SavePaths.cs` / `SaveHash.cs` | 存档路径解析 / **3 次 MD5** 反作弊哈希 | 见 6.2 |
| `MiniJson.cs` | 极简 JSON（**不用 System.Text.Json / Newtonsoft**） | 被裁剪环境下的安全选择 |
| `ModDiag.cs`（9.5KB） | **诊断埋点**：calls / applied / errors / runs / fixed / scene / objects | 面板「诊断」页的结论来源 |
| `CapabilityRegistry.cs` | 能力声明（本 mod 支持哪些功能 key） | 新旧版本兼容检查 |

### 4.3 `修改器面板\` —— WPF 面板（外部进程）
| 文件 | 职责 | 关键点 |
|---|---|---|
| `MainWindow.xaml.cs`（101KB） | 主窗：分类导航 + 功能控件 + 诊断页 + 存档编辑 | 铁律：乐观更新 + 不整页重建 |
| `PanelClient.cs`（15.1KB） | **文件通道客户端**：写命令 → 轮询状态 → 计算 `GameAlive` / `GameFrozen` | 见 6.5 |
| `SaveEditorLogic.cs` | 存档编辑逻辑（与 `共享\SaveData` 对齐） | — |
| `GameInfo.cs` | 进程检测 + **`FocusGame()`**（`SetForegroundWindow` + `SW_RESTORE`） | 面板「切到游戏」按钮 |
| `Skin.xaml` / `SkinHybrid.xaml` | 皮肤资源（换肤改这里） | — |
| `ToggleSwitch` / `AcrylicHelper` | 控件与视觉 | — |

### 4.4 工具链（`注入器\` / `安装器\` / `API检查\` / `工具脚本\`）
| 文件 | 职责 | 关键点 |
|---|---|---|
| `注入器\Program.cs`（19KB） | **5 条注入规则**（见 5.2）+ `patch`/`restore`/`verify` | `patch` 每次都先从 `.orig` 重置再全量注入 |
| `安装器\Program.cs`（15.4KB） | 安装/卸载：注册启动点 + 调注入器 + 拷贝 MOD | **规则编译期嵌入** → 改规则必须重跑 `发布.ps1` |
| `API检查\Program.cs`（11.6KB） | **成员引用核对**：`Verify` / `Type` / `Member` / `Has` | 当前状态：✓ 83 个类型全部可解析 |
| `工具脚本\*.ps1`（12 个） | 自动化验证：截图 / 点导航 / 端到端 / 后台运行 / 设置持久化 / 脚本引擎 | **纯 ASCII**（PS 5.1 中文脚本会坏） |
| `测试\`（7 文件） | 自研控制台断言器 | 改 `共享\` 前后都应保持通过 |

---

## 5. 核心设计（架构与数据流）

### 5.1 全局启动点（为什么不用 Harmony/场景挂载）
```
① 把 PvzGachaMod.dll 名字加进 抽卡版PVZ_Data\ScriptingAssemblies.json 的 names
② 在同目录 RuntimeInitializeOnLoads.json 的 root 里登记：
   {"assemblyName":"PvzGachaMod","nameSpace":"PvzGachaMod","className":"ModBootstrap",
    "methodName":"Init","loadTypes":2,"isUnityClass":false}
→ Unity 启动时自动调用 ModBootstrap.Init()，与场景无关（实测在主菜单就触发）
工具：源码\注册启动点.ps1（带 .orig 备份、幂等、-Unregister 可撤销）
```

### 5.2 三条实现路径（必须选对，别混用）
| 路径 | 适用 | 例子 |
|---|---|---|
| **A. 全局启动点** | 只要"开机就跑起来"的部分 | 读设置、起帧循环、写状态文件 |
| **B. IL 注入（必须用）** | **无法在托管侧调用**的拦截：UI 点击、私有方法、返回值改写 | 免费抽卡 / 指定稀有度 / 自定义货架 / 免费刷新 |
| **C. 纯托管反射** | 能反射拿到的字段/对象 | 改血量、改阳光、改卡牌冷却字段 |

**当前 5 条注入规则**（`注入器\Program.cs` 的 Rules 表）
| # | 目标 | 钩子 | 语义 |
|---|---|---|---|
| 1 | `shopBuyBottom.OnMouseDown` 开头 | `BuyPrefix()` | 免费抽卡（先垫钱/拦扣费） |
| 2 | `shopBuyBottom.OnMouseDown` 每个 `ret` 前 | `BuyPostfix()` | 抽卡后统一收尾（3 处 ret） |
| 3 | `Wins.getXiyouGroup` 开头 | `OverrideTier(ref int)` | **指定稀有度**（第 0 档起覆盖） |
| 4 | `shangdian.buhuo` 开头 | `OverrideShelfInPlace(int[])` | **自定义货架**（原地改数组） |
| 5 | `shangdianshuaxin.OnMouseDown` 开头 | `FreeRefreshPrefix()` | 商店刷新免费（先垫 300，游戏再扣 300 → 净 0） |

**注入器两条铁律**
1. **每次都从 `.orig` 备份重置后再全量注入**（幂等检测只看钩子名 → 规则改了会被当成"已注入"跳过并残留，历史上导致"商店一开就崩"）。
2. **改完 IL 必须反编译核对实参**：`dotnet ilspycmd -t <类型> <dll> | Select-String "Hooks\."`。

### 5.3 文件命令通道（本工程唯一的跨进程通道）
```
面板 → 写 修改器命令.json（命令+参数+快照）
mod  → 每帧（或降频）读命令文件 → 分发 → 执行 → 写 修改器状态.json（值+lastActionOk+lastResult+diag+modVersion）
面板 → 轮询状态文件 → 更新 UI / 显示错误原因
```
**为什么不是 TCP**：游戏被裁剪到**没有 `TcpListener`**（编译期 `CS1069`）。
**要点**
- **原子写**（临时文件 + 替换）→ 防半截 JSON 被对面读到；
- 命令文件用"**序号/时间戳**"判断是否是新命令（避免重复执行）；
- 状态文件**必须**带 `modVersion`（构建号）+ `lastActionOk` + `lastResult` + `diag`；
- 面板"待生效"字样**已改名"同步中"**，并带**自愈重发**（每 2.5s 重发整份快照，**封顶 5 次**后明说原因）。

### 5.4 关键设计决策与「为什么」
| 决策 | 为什么 |
|---|---|
| 编译期注入而非 BepInEx | 见 1.4（预加载器必崩） |
| **文件通道**而非 TCP | 游戏没有 `TcpListener` |
| **零 NuGet 依赖**，全 HintPath 引用游戏程序集 | 目标框架是 net472 + NoStdLib + FrameworkPathOverride，NuGet 会引入不兼容程序集 |
| 非平凡逻辑抽到 `共享\ModLogic.cs` | **可离线单测**（游戏跑不起来的机器上也能测） |
| `FeatureCatalog` 单一来源 | 面板与 mod 共用 → 不会出现"面板有、mod 不认识" |
| 编译期强类型引用 `Assembly-CSharp.dll`（`Private=false`） | 补丁代码强类型、可编译期检查；运行期仍 try/catch 降级 |
| 诊断埋点（calls/applied） | 用户只会说"没用"；埋点让面板能直接给结论（"没走到" vs "走到了没生效"） |

---

## 6. 游戏内部逻辑（必须知道的真实结构）

### 6.1 存档
| 事实 | 值 | 取证 |
|---|---|---|
| 路径 | `%USERPROFILE%\AppData\LocalLow\MiaoDouzi\抽卡版PVZ\save.json` | 实测 |
| 类 | `GameStart.GameData`（Unity `JsonUtility` 序列化） | 反编译 |
| **游戏不缓存存档** | 每个操作都是 `File.ReadAllText` → `FromJson` → 改 → 写回。**没有内存常驻副本** | 反编译三处操作 |
| 推论 | **外部直接改 `save.json` 立刻生效**，不用重进游戏（但 `shangdian.Start()` 只在进商店时跑一次货架生成 → 要立刻看到货架变化需主动调 `shuaxinHuojia()`） | 实测 |

### 6.2 反作弊（必须知道，但影响有限）
| 事实 | 值 |
|---|---|
| `save.json.md5` = **对文件字节连做 3 次 MD5** 的小写 hex | 反编译 |
| 不匹配 → `First.ZUOBIZHE = true` | 反编译 |
| 后果 | **仅**在卡面/选关页显示"作弊者"文字，**不封锁玩法** |
| 因此 | 改存档后**必须**同步重算 `save.json.md5`（`共享\SaveHash.cs`），否则用户会看到"作弊者"字样而被吓到 |

### 6.3 抽卡与稀有度
| 事实 | 值 | 取证 |
|---|---|---|
| 稀有度 **6 档** | `Wins.getXiyouGroup(0..5)`；各档卡数 **14 / 24 / 42 / 39 / 19 / 4** | 反编译 `Wins.cs:352` |
| 第 5 档（最高） | 卡 id 集合 `{32, 46, 101, 124}` | 反编译 |
| 加权抽卡 | `Wins.Chouka()` —— **private 实例方法** | 反编译 `Wins.cs:392` |
| 注入点 | `Wins.getXiyouGroup`（规则 #3）→ `OverrideTier(ref int)` | 注入器 |
| ⚠️ 副作用 | **本工程自己的"图鉴全解锁"也靠 `getXiyouGroup` 枚举全集** → 枚举时必须**旁路**注入（见 6.7） |

### 6.4 商店与货架
| 事实 | 值 | 取证 |
|---|---|---|
| 生成货架 | `shangdian.shopChouka(GameStart.GameData)` → `int[6]` | 反编译 |
| **`9999` = 空格** | `huojia.UpdateText` 里 `id == 9999` 显示"一片空白" | 反编译 |
| 购买 | `shopBuyBottom.OnMouseDown()` | 反编译 |
| 商品 id | 0–12（8/10/11/12 = 劣质/普通/稀有/史诗卡包；9 = 叶子保护伞） | 反编译 |
| 刷新 | `shangdianshuaxin.OnMouseDown`：`if (coin >= 300) { ...; coin -= 300; }`（300 是**字面量**改不了） | 反编译 |
| 刷新货架 | `shangdian.shuaxinHuojia()` | 反编译 |
| ⚠️ 五个标记的语义 | `liekabao` / `ptkabao` / `xykabao` / `sskabao` / `canbaohusan` + `chushisun==7` 是**排除条件**（true = 已买过 → **别再刷出来**）→ 「卡包全解锁」要置 **false** | 反编译 `shopChouka` 循环 |

### 6.5 ★ 游戏失焦就停止更新（重大 UX 事实，实测）
| 事实 | 值 |
|---|---|
| 本作构建时**没有勾选 Run In Background** | 窗口一失焦 → **主循环直接停住**（`Update` 不再被调用、状态文件不再更新） |
| 重新聚焦 | 立刻恢复（实测 age 从 44s → 0s） |
| 已排除 | 不是 vsync/遮挡限帧（`QualitySettings.vSyncCount = 0` + 置顶可见都无效） |
| 无法从托管侧修复 | `Application.runInBackground` 与 `Application.targetFrameRate` **都被裁剪**（`Application` 只剩 24 个成员）；`Application.get_isFocused` / `QualitySettings.get/set_vSyncCount` 在 |
| `Time.realtimeSinceStartup` | **也被裁掉** → mod 里要计时就用**帧计数** |
| **面板的正确做法** | `GameAlive` = 状态文件新鲜 **且** 进程在；`GameFrozen` = 进程在但状态文件停住 → 显示「已连接・游戏在后台暂停」，**绝不显示未连接**，并说明「改动切回游戏后生效」；提供 `FocusGame()` 按钮让它立刻生效 |
| 测试要点 | 默认冻结时 SKIP；`PVZMOD_TEST_FOCUS=1` 才主动切前台跑完整往返 |

### 6.6 关键字段与心跳点（反编译确认）
| 对象 | 成员 | 说明 |
|---|---|---|
| 僵尸 | `Zombie.ZombieHp` / `Zombie.TakeDamage(int,int,bool)` | 血量与伤害入口 |
| 植物 | `Planting.HP` + `maxHP` | 血量 |
| 阳光 | `First.Sun` | 阳光值 |
| 卡牌 | `CardClick.pro`（**private** `Cardproperties`） | 含 `nowcoolDown`（冷却） |
| 太阳生成 | `Sun.startTime`（**private**） | — |
| **心跳点** | `First.Update()`（`First.cs:1389`，**private**） | mod 帧循环可挂靠/参考 |
| 暂停语义 | **`Time.timeScale = 0` 是游戏的暂停/失败界面值**（`anniuClick.cs:627`、`shibai.cs:15`） | ⚠️ 加速类补丁**必须**判断 `timeScale <= 0` 时不覆盖 |
| 内置调试菜单 | `anniuClick`（门控 `First.zuobi`）已有：秒杀僵尸 / 2X 加速 / 造阳光 | 与本工程功能有重叠，注意别打架 |
| ⚠️ 不能当开关的字段 | `First.jianbukecui` 曾被当成零冷却开关 → 但它**还参与出怪与阳光逻辑**（`First.cs:1430/1435/1501`） | 零冷却改用 `Postfix CardClick.Update` + 反射拿私有 `pro` 置 `pro.nowcoolDown = 0` |

### 6.7 ★ 图鉴全解锁的三层根因（2026-09-10 修完，务必理解）
用户报"解锁全部卡牌还是没用"。**不是一层，是三层叠一起**：
1. **功能实现调用了自己的注入点**：`PlantUniverse()` 靠 `Wins.getXiyouGroup(0..5)` 枚举六档池子拼"植物全集"，
   而 `getXiyouGroup` **正是我们注入 `OverrideTier` 的方法** → 用户设了抽卡档位（4 档）后六次调用全被改写 → 全集塌缩成 29 个（而这 29 个早就解锁）→「已解锁 30 → 30」看不出变化。
   **修法**：`Hooks.BypassTierOverride` 旁路标志，枚举期间置真让钩子放行（连 `tierCalls` 都别计，否则诊断虚高）。
   **通用教训**：只要功能 A 的数据来自游戏方法 M，而 M 又是功能 B 的注入目标，**就必须有旁路机制**。
2. **"全集"必须来自权威卡牌表**：靠稀有度池拼会**漏掉不在任何池里的卡**（实测漏 47 号）→ 图鉴那张永远盖"未解锁"遮罩。
   权威来源（按优先级都试一遍取最大值）：`guanqiaStart.cards.Length`（public，按植物 id 索引）→
   `GameObject.Find("CanvasCard").GetComponent<guanqiaStart>().cards.Length`（对象被 `SetActive(false)` 时 `FindObjectOfType` 找不到，这个找得到）→ `First.plantId.Length`。
   **拿不到时必须在面板上明说"可能有漏卡"**，不能装作全集。
3. **图鉴卡牌不会自动重读**：`CardClick.Start()` 读一次 `scores` 设 `findThis`，`!findThis` 就盖 0.7 alpha 遮罩，**不会重读**。
   **修法**：解锁完若当前在 `植物图鉴`/`僵尸图鉴` 场景 → `SceneManager.LoadScene(sc.name)` 重载一次。

**实测对照**：修复前 全集 29 / 运行时卡牌表贡献 0 / 实际解锁 153→153（无变化）；修复后 全集 153 / 贡献 152 / 实际解锁 **153→154**（补上漏掉的 47）。

---

## 7. 运行时限制（API 白名单 / 黑名单）

> 本作 `mscorlib.dll` 被 Unity 链接器**大面积裁剪**。**编译期报错**（CS1069/CS1061）或**运行期 `MissingMethodException`** 都可能发生。
> 因此：**新增任何 API 前先查这张表；写完必须跑 `API检查`。**

### 7.1 黑名单（实测缺失，禁止使用）
| 禁止 | 症状 |
|---|---|
| `System.Net.Sockets.TcpListener` | **编译期** `CS1069` → 不能 TCP |
| `LINQ` 部分方法（如 `Enumerable.Sum`） | **编译期** `CS1061` → 别用 LINQ |
| `File.AppendAllText`（两个重载都没有） | 追加要自己"读+写" |
| `DateTime.ToString(string)` | **运行期** `MissingMethodException` → 用**整数秒**计时 |
| `Application.productName` / `Application.dataPath` | 缺失 → 别用 |
| `Application.runInBackground` / `Application.targetFrameRate` | 缺失 → 无法修复"失焦冻结"（见 6.5） |
| `Time.realtimeSinceStartup` | 缺失 → 用帧计数 |
| `System.Reflection.Module.GetPEKind` / `Assembly.LoadFile` | 缺失 → 这就是 BepInEx 不能用的原因 |

### 7.2 白名单（实测可用）
`File.Exists` / `ReadAllText` / `WriteAllText`（两参）、`HashAlgorithm.Create("MD5")` / `MD5.Create`、`StringBuilder`、`List`/`Dictionary`、
`Type.GetType`、`Assembly.GetTypes`、`AddComponent<T>`、`FindObjectsOfType<T>`、
反射 `Type.GetField/GetValue/SetValue`、`Time.timeScale`、`GameObject.Find`、`Input.GetKeyDown`、
IMGUI（`OnGUI` 内 `GUI.*`）、`SceneManager.GetActiveScene`、`QualitySettings.get/set_vSyncCount`、`Application.get_isFocused`、`DynamicMethod`。

### 7.3 编译期校验不可信（本工程最特殊的约束）
- 本机**没有 .NET Framework 目标包** → MSBuild 用了别处的 `mscorlib` → 会出现「**编译通过、运行 `MissingMethodException`**」。
- 所以**每次构建后必须跑**：
  ```powershell
  dotnet run --project 源码\API检查\API检查.csproj -c Release -- Verify <我们的.dll>
  ```
  → 拿每个成员引用去游戏程序集里核对，不通过不许运行。当前状态：**✓ 83 个类型全部可解析**。
- `API检查` 还有三种查询模式：`Type <类型>` / `Member <类型> <成员>` / `Has ...` —— **选 API 前先查**。
- mod 工程配置（csproj 必须照抄）：
  ```xml
  <TargetFramework>net472</TargetFramework>
  <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>
  <NoStdLib>true</NoStdLib>
  <FrameworkPathOverride>...\抽卡版PVZ_Data\Managed</FrameworkPathOverride>
  ```
  → 框架程序集**直接引用游戏自带的那份**。

### 7.4 代码风格硬要求
| 要求 | 原因 |
|---|---|
| 不用 LINQ | 被裁 |
| 追加文件用"读+写" | `File.AppendAllText` 缺失 |
| 计时用帧计数 | `Time.realtimeSinceStartup` / `DateTime.ToString(string)` 缺失 |
| 非平凡逻辑放 `共享\ModLogic.cs` | 可离线单测 |
| 所有钩子极轻 + try/catch | 钩子异常会掐掉游戏原方法 |
| JSON 用 `共享\MiniJson` | 不引入外部依赖 |

---

## 8. 构建 / 注入 / 部署

### 8.1 全量构建（脚本 `构建.ps1` / `发布.ps1`）
```powershell
pwsh -File 源码\构建.ps1                 # 编译 mod（含 BuildStamp 构建号）→ 落到交付根 MOD\
pwsh -File 源码\发布.ps1                 # 打包交付物到 工具\ 与发布目录
# 安装（★ 不要调 安装.cmd —— 它末尾有 pause 会挂住）
& "D:\抽卡版\修改器\工具\安装器\安装器.exe" install "D:\抽卡版\电脑\抽卡0.60.0正式版"
# 校验注入状态
& "D:\抽卡版\修改器\工具\安装器\安装器.exe" verify "D:\抽卡版\电脑\抽卡0.60.0正式版"   # 应报 已注入 5
```
**必须核对两件事**
1. 状态文件 `modVersion` 的**构建号 == 刚构建的**；
2. `MOD\PvzGachaMod.dll` 与**游戏内那份 SHA256 一致**。
> **历史事故**：`构建.ps1` 曾把产物写到 `源码\MOD`，而安装器读交付根 `修改器\MOD` → **静默装了两天旧 DLL**，用户报"很多功能用不了"。
> 修法：产物落到交付根 + **复制后 SHA256 自检，不一致就 throw**；并加**构建号**写进状态文件（一眼看出装的是哪版）。

### 8.2 只改面板时（不必重装 mod）
```powershell
dotnet publish 源码\修改器面板\修改器面板.csproj -c Release -o 工具\面板 `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true --self-contained true -r win-x64
```
> ⚠️ 打包前**先关掉面板进程**，否则 exe 被占用发布失败。

### 8.3 交付物清单（`发布.ps1` 产出）
```
安装.cmd  卸载.cmd  使用说明.md
工具\安装器\{安装器.exe, 注入器.exe, 注入器.runtimeconfig.json}
工具\面板\抽卡版修改器.exe + 6 个 *_cor3.dll
工具\Mono.Cecil.dll
MOD\PvzGachaMod.dll
```
**不该放**：`源码\`（开发用）、`_shots\`（截图）、`工具\面板_免运行时\`（陈旧副本，已不再产出）。
`安装.cmd` 用 `%~dp0` → **整个目录可随便搬**，不用改路径。

### 8.4 打包后必须核对
1. `MOD\PvzGachaMod.dll` 的 SHA256 == 交付根那份；
2. **包内**安装器 `verify` 输出含最新规则名（如 `FreeRefreshPrefix`）且报 `已注入 5`
   —— 用来排除"打进包的是旧安装器"（这个坑真实发生过）。

---

## 9. 编译 / 运行不通过怎么办（SOP）

### 9.1 第一步：看完整输出
```
❌ $log | Select-Object -Last 2      # 会把真错误吞掉，只剩"0 错误"的假象
✅ $log | Select-String 'error CS|error MSB' | Select-Object -First 10
```

### 9.2 报错 → 根因 → 处置
| 报错 | 根因 | 处置 |
|---|---|---|
| `CS1069 未能找到类型 TcpListener` | 游戏程序集被裁剪 | 改用**文件命令通道** |
| `CS1061 不包含 Sum 的定义` | LINQ 被裁 | 手写循环求和 |
| `CS0246 找不到 Unity/游戏类型` | csproj 的 `FrameworkPathOverride`/HintPath 不对 | 指向游戏 `Managed` 目录 |
| `CS0019 Count 用于 string[]` | Unity 部分 API 返回数组 | 用 `.Length` |
| `CS0102/CS0111 已定义` | 重复定义（新旧版本都加了同名方法） | 删重复，复用已有 |
| **运行期 `MissingMethodException`**（编译通过） | **本机 mscorlib 与游戏那份不同** → 必须 `API检查` | 换等价写法或补引用 |
| 运行期钩子抛异常 → 游戏行为异常/崩 | 钩子没 try/catch，或 IL 注入错（栈不平衡） | 钩子极简化 + try/catch；`patch` 从 `.orig` 重置重注入 |
| **商店一开就崩** | 注入传错实参（历史：`buhuo` 传了 `data` 而不是 `shop_List`） | **反编译核对实参**：`ilspycmd -t shangdian <dll> | Select-String "Hooks\."` |
| 安装器报 `已注入 4`（源码直跑报 5/5） | **安装器是旧的**（规则编译期嵌入） | 重跑 `发布.ps1` |
| 功能装了但游戏里没变 | 装的是旧 DLL / 游戏没重启 / 功能是空壳 | 见 9.3 |

### 9.3 "功能没用"的标准排查顺序
1. **游戏重启了吗**（mod 只在启动时加载）；
2. **装的是新版吗**：状态文件 `modVersion` 构建号 + DLL SHA256；
3. **三层齐吗**：`FeatureCatalog` / `ModSettings` / **消费点** →
   **审计手法**：取 `ModSettings.cs` 所有 public 字段，对每个字段在排除 `ModSettings.cs`/`FeatureCatalog.cs`/`*Tests.cs` 后 grep 引用点，**计数=0 就是空壳**；
   （那次审计：33 个功能里恰好 2 个空壳：`UnlockPacks`、`FreeRefresh`）
   ⚠️ **注意假实现**：消费点计数=2 也可能只是"判断一下 + 加个计数器" → 必须打开方法体确认它在写游戏字段。
4. **看诊断页**：`runs == 0` → 没走到（注入点没被游戏调用）；`applied == 0` → 走到了但没作用到对象上（如没进关卡/没进商店）；
5. **看失败通道**：标题栏下方是否显示 `lastResult`（被拒原因）；
6. **看是否被冻结**（6.5）：游戏失焦时改动要切回游戏才生效。

---

## 10. 加一个功能的完整流程（三层结构）

| 步 | 动作 | 自证 |
|---|---|---|
| 1 | `FeatureCatalog.cs` 加功能定义（key/中文名/类型/范围/默认值/分组） | 面板能渲染出来 |
| 2 | `ModSettings.cs` 加字段 + 持久化（`TrySet` 自动夹取） | 重启保持 |
| 3 | **实现消费点**（`ModActions.cs`；需要拦截就加注入规则到 `注入器\Program.cs` 的 Rules） | 打开方法体确认它真的在写游戏字段 |
| 4 | 若加规则 → **同步 `android`？无（本作只有 PC）**；但**必须重跑 `发布.ps1`**（安装器内嵌规则） | 包内 `verify` 报新规则名 |
| 5 | `共享\ModLogic.cs` 放纯逻辑 + `测试\` 加断言 | `dotnet run --project 源码\测试` 通过 |
| 6 | 构建 → `API检查 Verify` → 安装 → SHA256 核对 | 三步全过 |
| 7 | 真机：进对应界面 → 操作 → 看状态文件 `lastResult` / 诊断页 | 给出期望现象与失败看哪条 |

---

## 11. 设计文档怎么写（本工程规范）

**位置**：`docs/superpowers/specs/YYYY-MM-DD-<主题>-design.md`；实施计划 `docs/superpowers/plans/YYYY-MM-DD-<主题>.md`

**12 节模板**：背景与动机 / 目标（可验收）/ 非目标 / **现状与取证（每条游戏事实写取证方式）** / 方案（文件→方法→注入点）/ 备选与否决原因 / 数据结构与协议变更 / 风险与影响面 / **验证方法（可执行命令或步骤）** / 回滚方案 / 任务拆解（每步可独立验证）/ 验收标准。

**三条硬要求**
1. 游戏内部事实**必须附反编译或实测证据**；
2. "完成了"必须能用一条命令或一个界面动作复现；
3. 不许写"后续优化"（要么进任务，要么进非目标）。

**真实反例（务必引以为戒）**
> 设计文档曾写「`liekabao` 等五个标记置 **true** = 卡包全解锁」。
> 实际反编译：这五个是**排除条件**（true = 已买过 → 别再刷），置 true 会**把卡包全踢出货架**。
> **文档写的是"我的理解"，不是"事实"。涉及游戏语义必须附证据。**

---

## 12. 工程标准

### 12.1 面板交互两条铁律（否则用户会以为"点了没用"）
1. **乐观更新**：点击后先把面板自己的值认下来（`_pending[key] = value`），游戏回信一致才摘掉标记。
   否则游戏失焦暂停期间，界面按旧状态重绘 → **勾选弹回去**，看起来就是坏的。
2. **不要每次 Tick 整页重建**：只在「有效值签名」变化时重绘。每秒重建会让点击落空。

### 12.2 契约层
- `FeatureDef` 要带 `Options`（值 + 标签）→ 面板渲染**分段按钮**，用户**不用输数字**。
- 货架改**可视化点选**（先选槽位再点商品）。

### 12.3 哨兵值规范（重要）
- **绝不能复用游戏已有的语义值**：货架数组里 **9999 = 空格**，面板当初把"留空（不修改）"也实现成 9999 →
  默认全 9999 → 点一下"应用到货架"6 格全变空格（用户原话"刷新空气"）。
- **修法**：新增独立哨兵 `ModLogic.ShelfKeepId = -1`（商品 0..12、空格 9999，不冲突），
  遇到 -1 跳过并计 `ShelfSkipped`；面板默认全 -1，并把 9999 明确标注为"空格"。
- **写进存档前必须消解哨兵**：`ModActions.SetShelf` 把 -1 换成货架原值（读不到存档时兜底 9999）。

### 12.4 失败通道（本工程最重要的一条）
- `CommandApi.Error()` **必须是实例方法**并写 `LastActionOk=false` + `LastActionResult=message`；
- `ApplySnapshot` 对不认识的 key **不能静默 `continue`** → 收集 `unknown`，`applied>0` 时仍返回 `ok:true`（让已应用的项落盘），
  但置 `LastActionOk=false` 并**点名**哪些 key + 提示重跑安装；
- 面板侧：`CommandErrorText()` 把原因显示在状态行 + 标题栏染警告色；「待生效」全部改叫「同步中」；
  **自愈重发**（每 2.5s 重发整份快照，幂等，**封顶 5 次**）→ 超限明说"已重试 5 次仍未被确认，多数是该项需要进关卡/进商店才有作用对象"。
- **教训**：写"乐观 UI"时，必须同时把**失败通道**打通。只上报成功结果的协议，会让 UI 无法区分"还没到"和"被拒了"，最后全被用户读成"坏了"。

### 12.5 构建与发布纪律
- 脚本一律 `pwsh -File`（PS 5.1 中文解析坏）；
- 工具脚本**纯 ASCII**：不能写 `Get-Process "抽卡版PVZ"` 这种中文字面量，用
  `Get-Process | Where-Object { $_.ProcessName -like '*PVZ*' }`；
- 产物落到**最终生效位置**并 SHA256 自检；
- 改了注入规则 → 重跑 `发布.ps1`；
- 打包前关面板进程；打包后核对包内 `verify` 结果。

### 12.6 文档同步
`使用说明.md` **不会自动跟着功能更新**，打包前必须对一遍。历史发现过这些过期：
「抽卡注入 4/4」→ 应为 **5/5**、「22 项 6 个分组」→ 应为 **33 项 8 个分组」、
「卡包全解锁 = 卡包里所有卡直接解锁」→ 实际是"让四种卡包都能刷进货架"、
「会标一个待生效」→ 应为「同步中」、缺了整个「系统」组与「实体属性」组、失焦章节没提"开游戏后台运行"能解决。
**凡文档里写死数字（N/N、N 项）的，一定会过期** → 打包前用 `Select-String` 扫 `4/4|待生效|N 项|N 个分组|旧功能名`。

---

## 13. 调试与诊断手册

### 13.1 三个文件就是全部真相
| 文件 | 谁写 | 看什么 |
|---|---|---|
| `修改器命令.json` | 面板 | 面板到底发了什么（含快照与序号） |
| `修改器状态.json` | mod | 值 / `lastActionOk` / `lastResult` / `diag` / `modVersion` |
| `修改器设置.json` | mod | 持久化是否成功 |

### 13.2 诊断埋点怎么读（面板「诊断」页）
- 钩子侧：`calls` / `applied` / `errors` → **`calls = 0` 说明注入点没被游戏走到**（不是逻辑错）；
- 每帧类：`runs` / `fixed` → **`fixed = 0` 说明当时场上没对象**（比如没进关卡）；
- 环境：`scene` / `objects` / `objectsKind` / `firstFound` / `notes`；
- 结论照抄："runs==0 → 没走到；applied==0 → 走到了但没生效"。
- `diag` 字段抛异常时降级为 `{}`，**不能连带面板读不了状态**。

### 13.3 工具脚本（12 个，自动化验证）
`工具_截屏.ps1` / `工具_截面板.ps1` / `工具_截游戏窗.ps1` / `工具_点导航.ps1` / `工具_滚到底截图.ps1` /
`工具_端到端验证.ps1` / `工具_最终验收.ps1` / `工具_验证后台运行.ps1` / `工具_验证后台运行2.ps1` /
`工具_对照实验后台运行.ps1` / `工具_验证设置持久化.ps1` / `工具_验证脚本引擎.ps1` / `工具_搜程序集字符串.ps1`
→ 改动后**尽量用脚本复现**，不要靠人工点。

### 13.4 反编译
```powershell
dotnet "D:\杂交版关卡\tools\ilspy\ilspycmd9\tools\net8.0\any\ilspycmd.dll" -p -o <输出目录> <Assembly-CSharp.dll>
# 单类型（核对注入实参）
dotnet "D:\杂交版关卡\tools\ilspy\ilspycmd9\tools\net8.0\any\ilspycmd.dll" -t shangdian <Assembly-CSharp.dll> | Select-String "Hooks\."
```
> `grep` 非工作区路径要用终端 `Select-String`（grep 工具只搜工作区）。

---

## 14. 血泪坑清单（AI 优先读）

| # | 现象 | 根因 | 正确做法 |
|---|---|---|---|
| 1 | BepInEx 装不上（无 LogOutput.log） | mscorlib 缺 `GetPEKind` / `LoadFile` | 改用编译期 Cecil 注入 |
| 2 | 想用 TCP 通信 → 编译不过 | 缺 `TcpListener` | 文件命令通道 |
| 3 | 编译通过但运行崩 | 本机 mscorlib ≠ 游戏那份 | **每次构建后跑 `API检查 Verify`** |
| 4 | 「很多功能用不了」但构建一路成功 | 产物落错目录 → **静默装了两天旧 DLL** | 落到最终生效位置 + **SHA256 自检** |
| 5 | 关游戏后设置全回默认 | 只存内存 | 持久化 `修改器设置.json`（启动读、命令成功后写） |
| 6 | 面板永远显示「待生效」 | `Error()` 是 static，从不写 `LastActionOk` → UI 只能看到"值没变" | `Error` 改实例方法 + 写原因；补 `unknown` key 上报；面板显示原因 + 自愈重发 |
| 7 | 勾选"弹回去" | 游戏失焦暂停时界面按旧状态重绘 | 乐观更新（`_pending`） |
| 8 | 每秒点不中控件 | 每 Tick 整页重建 UI | 只在有效值签名变化时重绘 |
| 9 | 「刷新空气」（货架 6 格全变空） | 哨兵值复用了游戏的 9999（=空格） | 用独立哨兵 -1 + 写存档前消解 |
| 10 | 「卡包全解锁」把卡包全踢出货架 | 那五个标记是**排除条件**，文档写反了 | 置 **false**；文档按反编译纠正 |
| 11 | 「解锁全部卡牌」没用（三层叠加） | ①功能调用自己的注入点 → 全集塌缩 ②全集靠池子拼 → 漏卡 ③图鉴不重读 | 旁路标志 + 权威卡牌表 + 重载场景 |
| 12 | 商店一开就崩 | `buhuo` 注入传错实参（传了 `data` 而非 `shop_List`） | 改完 IL **反编译核对实参**；`patch` 从 `.orig` 重置 |
| 13 | 改了规则但安装器没生效 | 安装器**编译期嵌入**规则 | 改规则 → 重跑 `发布.ps1`；核对包内 `verify` |
| 14 | 「很多功能用不了」其实是没走到 | 注入器幂等检测只看钩子名 → 旧注入残留被当"已注入" | `patch` 每次从 `.orig` 全量重置 |
| 15 | 面板显示"未连接"但游戏好好的 | 游戏失焦主循环停住 | 区分 `GameAlive` / `GameFrozen`，显示"已连接・游戏在后台暂停" |
| 16 | 加速功能在暂停界面乱来 | `Time.timeScale = 0` 是游戏的暂停/失败值 | 补丁判断 `timeScale <= 0` 时不覆盖 |
| 17 | 零冷却改 `First.jianbukecui` 引发连锁 bug | 该字段还参与出怪与阳光逻辑 | 改 `Postfix CardClick.Update` + 私有 `pro.nowcoolDown = 0` |
| 18 | 自动化调 `安装.cmd` 一直挂着 | 末尾有 `pause` | 直接调 `安装器.exe install <目录>` |
| 19 | 脚本报 parse error | PS 5.1 读无 BOM 中文 `.ps1` | 一律 `pwsh -File`；脚本尽量纯 ASCII |
| 20 | 面板发布失败 | 面板进程还开着，exe 被占用 | 打包前关进程 |
| 21 | 打包后功能对不上说明文档 | `使用说明.md` 不会自动同步 | 打包前 `Select-String` 扫过期数字/字样 |
| 22 | IL 注入函数但游戏行为怪 | 栈不平衡 / 参数编号错（实例方法 `arg0 = this`） | 规范里一律写 IL 编号；`patch` 后 `verify` + 真机 |
| 23 | 改了存档后显示"作弊者" | `save.json.md5` 未同步（3 次 MD5） | 用 `共享\SaveHash.cs` 重算 |
| 24 | 改完货架没反应（要重进商店） | `shangdian.Start()` 只进货架生成一次 | 改完主动调 `shuaxinHuojia()` |

---

## 15. 红线（绝对禁止）

1. 禁止把游戏本体 / `Assembly-CSharp.dll` / `save.json` 样本 / 私钥提交到仓库。
2. 禁止引入运行时 Hook 框架（BepInEx/Harmony）—— 本作不可用。
3. 禁止用 TCP/HTTP 做通信（无可用的 `TcpListener`）。
4. 禁止跳过 `API检查` 就宣布完成。
5. 禁止用猜的字段名/方法签名；不确定先 `API检查` 的 `Type`/`Member`/`Has` 查询。
6. 禁止复用游戏已有语义值当哨兵。
7. 禁止只实现"看得见"（`FeatureCatalog`）而漏掉**消费点**。
8. 禁止空 catch；禁止吞掉失败原因。
9. 禁止为了让编译过去而注释掉功能调用。
10. 禁止改功能却不更新 `使用说明.md` 里与之相关的描述。

---

## 16. 术语表

| 术语 | 含义 |
|---|---|
| **全局启动点** | 通过 `ScriptingAssemblies.json` + `RuntimeInitializeOnLoads.json` 让 Unity 启动时调用 `ModBootstrap.Init()` |
| **IL 注入器** | `注入器\Program.cs`：Cecil 改 `Assembly-CSharp.dll`，5 条规则，带 `.orig` 备份 |
| **安装器** | 一键安装：注册启动点 + 调注入器 + 拷 MOD；**规则编译期嵌入** |
| **API检查** | 成员引用核对工具（`Verify`/`Type`/`Member`/`Has`）—— 本工程特有且必跑 |
| **文件命令通道** | 面板写命令文件、mod 轮询执行、mod 写状态文件、面板轮询显示 |
| **三层结构** | `FeatureCatalog`（看得见）/ `ModSettings`（存得下）/ **消费点**（真生效） |
| **空壳功能** | 只有前两层、没有消费点（审计手法：grep 字段引用点计数=0） |
| **失败通道** | `lastActionOk` + `lastResult`，让 UI 能区分"还没到"和"被拒了" |
| **旁路（bypass）** | 功能 A 依赖的方法同时是功能 B 的注入点 → B 生效时让 A 的枚举放行 |
| **哨兵值** | 表示"不修改"的特殊值，**必须**与游戏已有语义值不冲突 |
| **游戏冻结（失焦）** | 本作未开 Run In Background → 失焦时主循环停住（常态，不是异常） |
| **构建号** | `BuildStamp.g.cs` 里的 `yyMMdd-HHmm`，写进状态文件用来识别版本 |
| **诊断埋点** | calls / applied / runs / fixed 计数，用来区分"没走到"与"走到了没生效" |

---

## 17. 给 AI 的第一个任务（自检：证明你读懂了）

动手前先回答这 8 题（不确定就写"需取证"）：
1. 本工程为什么不能用 BepInEx？至少说出两个缺失的 API。
2. mod 与面板之间用什么通道通信？为什么不用 TCP？通道要满足哪些工程要求（原子写 / 序号 / 状态字段）？
3. mod 的三种实现路径分别是什么？各举一个功能例子。
4. 当前有哪 5 条注入规则？分别解决什么问题？
5. 为什么"编译通过"不能说明能跑？本工程的对应工具叫什么、怎么用？
6. 加一个功能必须动哪几个文件？三层结构分别是什么？怎么审计"空壳功能"？
7. 用户报"功能没用"，你的排查顺序是什么（至少 6 步）？
8. 游戏失焦时面板应该显示什么？为什么不能显示"未连接"？

**答完再动手**。每次交付按第 10 章流程自证，按第 15 章红线自查。

---

> 文档与源码不一致时，**以源码 + 反编译取证为准**，并修正本文档对应小节。
