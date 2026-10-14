# DeskTools 开发计划（Shelf / 置物架）

> 状态：**P0–P4 已实现并实测通过**（详见 §12 实施进度）
> 目标平台：Windows 10 1809+ / Windows 11（当前开发机为 Windows 10 19045）
> 一句话定义：一个常驻右下角的**置顶小面板**，把「临时要用的东西」以卡片（unit）形式暂存，随时呼出、随时取用。

---

## 1. 已确认的环境事实

| 项 | 结论 |
| --- | --- |
| OS | Windows 10 专业版 19045（无 Win11 圆角/云母材质，需按 Win10 兼容设计） |
| .NET SDK | 9.0.310 / 10.0.300 均已安装 |
| 桌面运行时 | Microsoft.WindowsDesktop.App 3.1 → 10.0.8 齐全 |
| Visual Studio | VS2022 / VS18 已装，但 **未安装 WPF 项目模板** |
| 构建方式 | `dotnet new wpf` + `dotnet build` 已实测**成功**（net10.0-windows，0 错误 0 警告） |
| NuGet | nuget.org 可达（HTTP 200），可正常还原第三方包 |
| 工作区 | `C:\project\DeskTools` 当前为空 |

> 结论：**WPF 路线零阻塞**，全程可用 CLI 构建/运行/测试，不依赖 VS 图形界面。

---

## 2. 技术选型

| 维度 | 选型 | 理由 |
| --- | --- | --- |
| UI 框架 | **WPF (.NET 8, net8.0-windows)** | 覆盖 Win10，托盘/全局热键/无边框窗口/拖拽/剪贴板全部成熟；纯 XAML 可完全自定义扁平样式 |
| 目标框架 | net8.0-windows（LTS） | 比 net10 更稳、运行时已装；后续可一行改到 net10 |
| MVVM | **CommunityToolkit.Mvvm**（source generator） | 官方维护，`[ObservableProperty]` / `[RelayCommand]` 大幅减少样板，是当前 WPF 商业项目主流 |
| 依赖注入 | Microsoft.Extensions.DependencyInjection + Hosting | 组合根集中注册，表现层只依赖接口 |
| 日志 | Microsoft.Extensions.Logging（文件 sink） | 便于排查剪贴板/热键这类系统级偶发问题 |
| 托盘图标 | **H.NotifyIcon.Wpf** | 现代库，支持 Win10/11、单文件发布；同时提供 XAML 声明式写法 |
| 全局热键 | 自建 `GlobalHotkeyService`（RegisterHotKey + 隐藏消息窗口） | 无需第三方包，行为可控 |
| 拖拽 / 剪贴板 | 内置 WPF `DragDrop` / `Clipboard`（带重试） | 零依赖 |
| 持久化 | **JSON（System.Text.Json）+ 图片落盘为独立 png 文件** | 实现简单、可手工排查、天然可版本化；仓储接口预留 SQLite 升级 |
| 测试 | xUnit + FluentAssertions，`tests/DeskTools.Tests` | 逻辑层可脱离 UI 单测 |
| 打包 | `dotnet publish` self-contained 单文件 + Inno Setup（可选） | 先保证能跑，安装包放收尾阶段 |

**不做的事（明确排除，避免范围膨胀）**：跨平台、云端同步、多面板、富文本编辑、OCR、AI 摘要。

---

## 3. 分层架构（表现层 / 逻辑层严格分离）

依赖方向永远是**向内**：`App → Application → Domain`，`Infrastructure` 只实现接口、不被内层引用。

```
DeskTools.sln
├─ src/
│  ├─ DeskTools.Domain/            【纯逻辑】零外部依赖，无 WPF 引用
│  │   ├─ Units/                   ShelfUnit(abstract) / TextUnit / ImageUnit / FolderUnit
│  │   │                           UnitKind / UnitCapabilities / UnitOrigin
│  │   ├─ Shelf/                   Shelf（聚合根：有序集合 + 增删改排序 + 领域事件）
│  │   ├─ ValueObjects/            UnitId / UnitPosition / StorageKey
│  │   └─ RawContent.cs            归一化输入：Text / Image(bytes,w,h) / Files(paths)
│  │
│  ├─ DeskTools.Application/       【应用逻辑】编排用例，仍无 WPF 引用
│  │   ├─ Abstractions/            IClipboardService / IUnitFactory / IShelfStore /
│  │   │                           ISettingsStore / IHotkeyService / ISystemLauncher /
│  │   │                           IThumbnailRenderer / IShelfFileResolver
│  │   ├─ UseCases/                PasteFromClipboard / IngestDrop / CopyUnitToClipboard /
│  │   │                           EditUnitText / RemoveUnit / OpenUnit / MoveUnit
│  │   ├─ Catalog/                 UnitCatalog：kind → 显示名/图标/能力/处理器的唯一注册表
│  │   └─ ShelfService.cs          唯一的逻辑入口，UI 只跟它说话
│  │
│  ├─ DeskTools.Infrastructure/    【系统适配层】所有 OS/P/Invoke/文件 IO 都关在这里
│  │   ├─ Clipboard/               WpfClipboardService（含 COM 占用重试、HTML→纯文本降级）
│  │   ├─ DragDrop/                OLE IDataObject → RawContent 的解析器
│  │   ├─ Storage/                 JsonShelfStore / ImageFileStore / LocalSettingsStore
│  │   ├─ Hotkeys/                 GlobalHotkeyService（RegisterHotKey + HwndSource）
│  │   ├─ Launchers/               ShellLauncher（打开图片/文件夹/所在目录）
│  │   ├─ Windows/                 WindowPlacement（工作区定位、DPI、Win32 扩展样式）
│  │   └─ Imaging/                 WIC 缩略图渲染（DecodePixelWidth + 后台线程 + Freeze）
│  │
│  └─ DeskTools.App/               【表现层】WPF，唯一允许引用 System.Windows 的项目
│      ├─ App.xaml(.cs)            组合根：DI 注册、单实例互斥、异常兜底
│      ├─ Views/                   PanelWindow / TrayIcon / Units/*UnitView
│      ├─ ViewModels/              PanelViewModel / UnitViewModel 基类 + 各类型子类
│      ├─ Templates/               ItemTemplateSelector + DataTemplate 资源字典
│      ├─ Themes/                  Tokens.xaml（设计令牌）/ Controls.xaml（控件样式）
│      └─ Behaviors/               HoverRevealBehavior / KeyBindingHost / DragDropHost
│
└─ tests/DeskTools.Tests/          Domain + Application + ViewModel 单测（Fake 服务）
```

**关键分离约定**

1. ViewModel **绝不**直接 `using System.Windows.Clipboard`，一律走 `IClipboardService`；
2. 所有跨线程回调通过 `IUiDispatcher` 回到 UI 线程，Domain 完全不知道线程存在；
3. 「粘贴」和「拖拽落盘」走**同一条** `Ingest(RawContent)` 管线 —— 这是可扩展性的核心；
4. 展示相关的一切（图标、标题、是否显示复制按钮）来自 `UnitCatalog` 的 `UnitPresentation`，**不写 if-else 判断类型**。

---

## 4. 可扩展 Unit 模型（第 4 条需求的落地方式）

### 4.1 领域侧

```csharp
public abstract class ShelfUnit {
    public UnitId Id { get; }
    public UnitKind Kind { get; }              // 可扩展的枚举/标识
    public DateTimeOffset CreatedAt { get; }
    public UnitOrigin Origin { get; }          // Clipboard / DragDrop
    public abstract UnitCapabilities Capabilities { get; }  // CanCopyToClipboard / CanEditText / OpenBehavior
}

public sealed class TextUnit   : ShelfUnit { public string Text { get; set; } }
public sealed class ImageUnit  : ShelfUnit { public StorageKey ImageKey; public int Width, Height; public long SizeBytes; }
public sealed class FolderUnit : ShelfUnit { public string Path { get; } }
```

### 4.2 新增一种 unit 类型需要改动的**全部**位置（固定 4 处）

| # | 位置 | 内容 |
| --- | --- | --- |
| 1 | `Domain/Units/XxxUnit.cs` | 新领域类型 + 能力声明 |
| 2 | `Application/Catalog/UnitCatalog.cs` | 注册：图标、显示名、缩略图策略、可用命令 |
| 3 | `Infrastructure/Storage/ShelfPersistenceMapper.cs` | JSON 多态读写（`$type` 判别符） |
| 4 | `App/Views/Units/XxxUnitView.xaml` + `Templates` 一行注册 | 新卡片外观 |

> 若新类型只是「已有内容的另一种呈现」（例如链接、颜色值），甚至可以只做 1 + 4。类型扩展不触碰 `ShelfService`、`PanelViewModel` 主干。

### 4.3 三类 unit 的行为矩阵（按需求整理）

| 行为 | 文本 unit | 图片 unit | 文件夹 unit |
| --- | --- | --- | --- |
| 面板内展示 | 文本内容（最多 3 行，超出省略号） | 缩略图（等比缩放，最大高度限制） | 文件夹图标 + 路径（长路径中间省略） |
| 来源 | 剪贴板文本 / 拖拽文本 | 剪贴板图片 / 拖拽图片文件 | 仅资源管理器拖拽 |
| 选中 + Ctrl+C | 复制文本 | 复制图片 | 无操作（能力声明为 false，按钮置灰/隐藏） |
| 双击 | 进入行内编辑 | 系统默认看图程序打开 | 资源管理器打开该路径 |
| 附加操作 | 右键「复制 / 编辑 / 移除」 | 右键「复制 / 打开 / 打开所在目录 / 移除」 | 右键「打开 / 复制路径 / 移除」 |
| 悬停小标 | 复制、移除 | 复制、移除 | 仅移除 |

**已识别的边界情况（必须在实现中处理）**

- 拖拽**单个图片文件**（.png/.jpg/...）→ 按「图片 unit」入库；拖拽**多个/其他文件** → 每个文件夹入 `FolderUnit`，普通文件按规则暂定忽略或提示（待决策项 Q6）。
- 从浏览器拖文字时，OLE 常只给 `HTML Format` 不给 `UnicodeText`，需要做 HTML→纯文本降级，失败则拒绝落盘并给出提示。
- 剪贴板被其他进程占用时 `Clipboard` API 会抛 `COMException`，必须**重试 3~5 次 + 20~50ms 间隔**。
- 拖拽来源以管理员权限运行时，`IDataObject` 可能取不到数据（UIPI 限制），需要 try/catch 静默失败。

---

## 5. UI 设计方案（极简扁平）

### 5.1 设计令牌（`Themes/Tokens.xaml`，全部集中，禁止散落硬编码颜色）

| 令牌 | 值 | 用途 |
| --- | --- | --- |
| `Color.Bg` | `#FFFFFFFF` | 面板底色 |
| `Color.CardBg` / `CardBgHover` / `CardBgSelected` | `#FFFFFFFF` / `#FFF7F7F7` / `#FFF2F6FC` | 卡片三态 |
| `Color.Border` / `BorderSelected` | `#FFE6E6E6` / `#FF3B82F6` | 卡片描边（选中 2px 强调） |
| `Color.Text` / `TextSecondary` / `TextMuted` | `#FF1F2328` / `#FF6E7781` / `#FF9AA1A9` | 三级文字 |
| `Color.Accent` | `#FF3B82F6` | 唯一强调色（悬停图标、选中、焦点框、拖拽指示线） |
| `Radius.Card` / `Radius.Window` / `Radius.Button` | `4` / `6` / `3` | 圆角（Win10 需自绘窗口圆角，用 Grid+Clip 实现） |
| `Space.*` | 4 的倍数：4 / 8 / 12 / 16 | 间距栅格 |
| `Font.Ui` | `Microsoft YaHei UI, Segoe UI` 13px / 行高 20 | 中文优先 |
| `Anim.Fast` | 120ms `CubicEase` | 悬停显隐、选中、面板淡入 |
| 其他 | **无阴影、无渐变、无投影、单色图标** | 扁平化硬约束 |

**暗色主题（已定：v1 范围内）落地方式**：上表全部令牌拆成 `Themes/Tokens.Light.xaml` 与 `Themes/Tokens.Dark.xaml` **两套同名 key**，UI 一律用 `{DynamicResource}` 引用，切换时替换合并字典即可，**不改任何 View / ViewModel 代码**。默认跟随系统（读注册表 `AppsUseLightTheme`，Win10 1809+ 可用），设置页可强制浅色/深色。为避免主题切换时性能抖动，卡片背景等高频元素用 `DynamicResource`，图标几何等低频资源用 `StaticResource`。

图标：MVP 用内置矢量 `Path` 几何（复制/关闭/文件夹/编辑），不引入图标包，避免版权与包体问题。

### 5.2 面板布局

```
┌─ 32px 顶栏（可拖拽移动窗口）────────────────────────────┐
│  ⠿ DeskTools            ⟳清空   📌置顶   ✕              │   6px 内边距
├─────────────────────────────────────────────────────────┤
│  ┌───────────────────────────────────────────┐  ← 卡片  │   1px 边框 + 4px 圆角
│  │ 文本内容第一行…                            │         │   悬停/选中时右上角
│  │ 文本内容第二行…                            │         │   淡入 ⧉复制 ✕移除
│  └───────────────────────────────────────────┘          │   卡片间距 8px
│  ┌───────────────────────────────────────────┐          │
│  │ [缩略图]                                   │          │
│  └───────────────────────────────────────────┘          │
│  ┌───────────────────────────────────────────┐          │
│  │ 📁 项目资料\2024\…\设计稿                  │          │
│  └───────────────────────────────────────────┘          │
│                                                         │
│              （空态）这里还什么都没有                       │
│              按 Ctrl+V 粘贴，或把文件拖进来                 │
└─────────────────────────────────────────────────────────┘
   默认 360 × 480，可缩放并记忆；右下角 8×8 缩放手柄
```

- 滚动：`ScrollViewer` + 虚拟化 `ItemsControl`（`VirtualizingStackPanel`），大批量 unit 不掉帧。
- 窗口：`WindowStyle=None` + `AllowsTransparency=True`（或 Win10 下 DWM 阴影方案）+ `Topmost=True`；定位用 `SystemParameters.WorkArea`（多屏取「鼠标所在屏」的工作区），**不覆盖任务栏**。
- 呼出时自动 `Activate()` + 聚焦，保证 `Ctrl+V` 立即可用；`Deactivated` 可选自动隐藏（待决策 Q4）。

### 5.3 视觉状态机（每个 unit 卡片）

`Default → Hover（显示小标） → Selected（2px 蓝边） → Editing（文本框接管，Esc 取消 / 失焦保存） / Dragging（半透明 + 拖拽指示线） / Error（红色 1px 边 + 一行说明，仅针对落盘失败）`

---

## 6. 交互逻辑规格

| 触发 | 行为 |
| --- | --- |
| 全局热键 **Alt+X**（已定） | 切换面板显示/隐藏；隐藏时保存窗口位置尺寸。注册失败→托盘气泡 + 设置页改键 |
| 面板内 `Ctrl+V` | 读剪贴板 → `Ingest` → 新卡片**追加到列表末尾**（旧→新，先到的先看到） |
| 拖拽外部内容到面板 | 悬停时显示 2px 蓝色插入指示线 → 落盘到指示位置，`DragDrop` 全区域接管；顶栏、列表空白、边缘缩放热区均已实测可落盘 |
| 单击卡片 | 单选；`Ctrl+点击` 切换该卡片的多选状态；`Shift+点击` 区间选择；点击空白处清空选择 |
| 悬停卡片 | 右上角 120ms 淡入「复制 / 移除」小标 |
| `Ctrl+C` | 复制**选中** unit；焦点在编辑框内时让编辑器处理（不劫持） |
| `Delete` | 移除全部选中 unit |
| 双击卡片 | 文本→行内编辑；图片→默认看图程序；文件夹→资源管理器（**仅单选时生效**，多选下双击=聚焦该卡片） |
| 右键卡片 | 上下文菜单（按能力矩阵动态生成，来自 `UnitCatalog`，不硬编码）；右键未选中的卡片时先把它设为唯一选中 |
| 顶栏 | 撤销、清空、**最小化**、**设置**（按实测反馈**去掉关闭按钮**） |
| 退出 | 仅托盘右键菜单「退出 DeskTools」；`Esc` / `Alt+F4` 只收起面板 || 托盘图标 | 双击=切换面板；右键=显示/隐藏/设置/退出；启动即常驻 |
| 开机自启 | 注册表 `HKCU\...\Run`，**默认关**，设置面板可开 |

### 6.1 多选规则（已定：支持多选，需明确语义）

- **选择模型**：`SelectionState` 持有 `HashSet<UnitId>` + 锚点（供 Shift 区间用），顺序按面板展示顺序。
- **`Ctrl+C` 复制多选**：按类型分派——
  - 全部为文本 → 用 `\n` 拼接后一次写入剪贴板；
  - 包含图片/文件夹等不可拼接类型 → 复制**第一个**（最靠前）选中 unit，并在面板顶部弹一行提示「已复制 1 项；混合类型暂不支持批量复制」；
  - 全部为文件夹 → 写 `FileDrop` 文件列表（资源管理器粘贴可识别）。
- **`Delete` 删除多选**：整体作为一个撤销批次。
- **悬停小标**在多选态下的语义：小标只作用于**鼠标所在的那一张卡片**，不影响整体选择。
- 多选态视觉：所有选中卡片显示 2px 蓝边框；无主次之分（不做"主选中"高亮）。

### 6.2 撤销删除（已定：5 秒窗口）

- 删除时把 `(unit, index)` 快照压入 `UndoStack`（最多 10 批，仅内存，不持久化）；
- 面板底部升起一条 5 秒消息条：「已移除 N 项 · 撤销」，点击或按 `Ctrl+Z` 撤销；超时后消息条淡出；
- **图片 unit 的撤销**：不立刻删除磁盘 png（依赖 P6 的引用计数 + 启动时 GC 清理），否则撤销后图片已丢失；
- 编辑文本后的撤销不在本次范围（只做删除撤销），避免与编辑器自身的 `Ctrl+Z` 冲突：焦点在编辑框内时 `Ctrl+Z` 归编辑器。

---

## 7. 分阶段实施计划（UI 先行）

### 阶段划分与里程碑

| 阶段 | 内容 | 交付物 / 验收标准 | 预估 |
| --- | --- | --- | --- |
| **P0 骨架** | 建 solution 与 4 个项目、目录结构、DI 组合根、日志、`.gitignore`、`Directory.Build.props`（统一 LangVersion/Nullable/TreatWarningsAsErrors） | `dotnet build` 通过；跑起来是一个空的无边框置顶窗口 | 0.5d |
| **P1 UI 外壳** | `Tokens.xaml` + 控件样式、无边框窗口 + 自绘顶栏、滚动面板、空态占位、淡入动画、右下角定位 | **M1**：呼出面板能看到极简外壳，滚动/拖拽窗口/缩放正常 | 1d |
| **P2 UI 卡片** | 三类 unit 的 `DataTemplate` + 悬停小标 + 选中态 + 行内编辑框外观 + `ItemTemplateSelector`；**全部用设计期假数据**（`SampleData` 设计时 ViewModel） | **M2**：不接任何逻辑，32 个假 unit 混合展示，悬停/选中/编辑态全部可见可调；设计走查通过 | 1.5d |
| **P3 UI 交互接线** | `PanelViewModel` / `UnitViewModel` 全套命令，先接 **Fake 服务**（内存剪贴板/内存存储）；按键绑定、双击、右键菜单、删除、选中 | **M3**：纯 UI 完成度验收 —— 可新增/编辑/删除/复制（假数据），无任何真实系统调用 | 1.5d |
| **P4 逻辑管线** | `RawContent` + `UnitFactory` + `ShelfService`；真实剪贴板（文本/图片 + 重试）、真实拖拽解析、`IClipboardService` 落地 | **M4**：Ctrl+V 真粘贴、真拖拽可用；Domain/Application 单测绿 | 2d |
| **P5 系统集成** | 全局热键（**Alt+X**）、托盘图标、单实例互斥、开机自启开关、打开图片/文件夹、隐藏而非退出 | **M5**：热键呼出关闭 + 托盘常驻，"固定最小化在任务栏"闭环 | 1.5d |
| **P6 持久化** | JSON 仓储 + 图片文件落盘 + 原子写 + 500ms 防抖自动保存 + 启动恢复 + 窗口布局记忆 + 损坏文件降级备份 | **M6**：重启后 unit 与面板位置完整恢复 | 1.5d |
| **P7 打磨** | 极端内容（超长文本/大图/超长路径）、异常提示、存储上限与清理、**暗色主题（跟随系统）**、**多选操作**、**5 秒撤销删除**、快捷键设置项 | 验收清单全过 | 2.5d |
| **P8 测试/发布** | 单测覆盖 Domain+Application+VM；`dotnet publish` 单文件；可选 Inno Setup 安装包 + README + 快捷键说明 | **M7 v1.0 候选**：可交付安装 | 1d |

**总计约 14 个工作日**（按串行估算，P4/P6 与 P2/P3 部分可并行压缩）。

### 每阶段严格遵循的工程约束

1. 每个阶段结束都能 `dotnet run` 看到实际效果（不留"半截代码"）；
2. P0~P3 期间新增的任何逻辑调用都必须先有接口 + Fake 实现，真实实现留给 P4 之后；
3. UI 阶段冻结后再动逻辑，减少返工；
4. 每个 P 结束打一次 git tag（`p1-ui-shell`、`p2-ui-cards`…），便于回退对比。

### 需要的手工测试清单（系统级无法单测）

- [ ] 高分屏 125% / 150% / 200% 下窗口尺寸与卡片模糊情况
- [ ] 多显示器：主副屏、不同 DPI、副屏在左侧时右下角定位是否正确
- [ ] 任务栏：顶部/左侧/自动隐藏时面板不遮挡
- [ ] 全屏应用（游戏/视频）之上热键与置顶行为
- [ ] 全局热键 **Alt+X** 与微信/QQ/输入法冲突检测；特别验证 Alt+X 在**画图、写字板、Excel、浏览器**前台时是否触发菜单助记符而非本工具（必要时设置页改键）
- [ ] 剪贴板被 Office/截图工具占用时连续快速 Ctrl+V
- [ ] 从 Chrome/Edge/Notepad++/VS Code/资源管理器/微信 分别拖拽
- [ ] 以管理员运行的源程序拖拽到本工具
- [ ] 400+ unit 时的滚动流畅度与内存占用
- [ ] 拔掉副屏后窗口位置是否回到可见区域

---

## 8. 数据与持久化设计

- **位置**：`%APPDATA%\DeskTools\`（`shelf.json`、`settings.json`、`images\*.png`、`logs\`）
- **JSON 结构（带 schemaVersion，便于未来迁移）**

```json
{
  "schemaVersion": 1,
  "units": [
    { "$type": "text",   "id": "...", "createdAt": "...", "origin": "clipboard", "text": "..." },
    { "$type": "image",  "id": "...", "imageKey": "ab12cd.png", "width": 800, "height": 600, "sizeBytes": 102400 },
    { "$type": "folder", "id": "...", "path": "D:\\项目\\设计稿" }
  ]
}
```

- **图片策略**：SHA-256 前 16 位命名去重；后台线程生成 ≤256px 缩略图供 UI 用，原图另行保存（重复粘贴同一图片不重复占空间）。
- **健壮性**：写入用「临时文件 + `File.Replace`」原子替换；解析失败则备份为 `shelf.corrupt.{ts}.json` 并以空面板启动，绝不因数据损坏启动失败。
- **容量**：默认上限（如 500 unit / 500MB），超限时提示并允许一键清理（P7）。

---

## 9. 质量保障

- **单测范围**：`Shelf` 聚合的增删改排序、`UnitFactory` 的 RawContent→Unit 映射、`ShelfService` 用例（用 Fake 剪贴板/存储/启动器）、`PanelViewModel` 命令行为与选中状态机。
- **不单测**：Win32 定位、OLE 拖拽、RegisterHotKey、DWM（改由第 7 节手工清单覆盖）。
- **静态约束**：`Domain`/`Application` 项目引用中**不允许出现** `PresentationFramework`；用 `Directory.Build.props` + 代码评审保证。
- **日志**：剪贴板重试、拖拽解析失败、存储写入失败三类必须落日志。

---

## 10. 风险登记

| 风险 | 影响 | 对策 |
| --- | --- | --- |
| 全局热键被占用 | 呼出失败，核心体验受损 | 注册失败即托盘气泡提示 + 设置页可改键 |
| 拖拽数据格式千奇百怪 | 部分来源拖不进来 | 统一 `IDataObject` 解析器 + 明确的降级链（FileDrop → UnicodeText → HTML→文本）+ 失败提示 |
| WPF 图片内存膨胀 | 大量大图卡顿/内存高 | 后台解码、`DecodePixelWidth`、`Freeze()`、缩略图与原件分离 |
| Win10 无 Win11 圆角/材质 | 视觉低于预期 | 自绘圆角（Grid+Clip），不用 Mica；扁平风格本就弱依赖材质 |
| 面板抢焦点打断用户操作 | 体验差 | 呼出即聚焦（需 Ctrl+V）；可配置「不激活呼出」（P7） |
| 剪贴板 COM 偶发失败 | Ctrl+V 无反应 | 重试 + 失败提示 + 日志 |
| 范围膨胀（想加 OCR/云同步） | 无法交付 v1 | 明确列入「不做的事」，进 backlog |

---

## 11. 已决策项（本次评审与你确认，作为 P0 起点的冻结基线）

| # | 事项 | **最终决策** |
| --- | --- | --- |
| D1 | UI 框架 / 目标框架 | **WPF + net8.0-windows**（已实测构建通过） |
| D2 | 全局热键 | **Alt+X**（注册失败→托盘提示 + 设置页可改键；`Alt+字母` 属菜单助记符空间，需在手工清单中做冲突检测） |
| D3 | 失焦行为 | **不自动隐藏**，仅热键 / 顶栏 ✕ 隐藏 |
| D4 | 新 unit 插入位置 | **追加到列表末尾（旧→新）** |
| D5 | 托盘图标常驻 | **要** |
| D6 | 开机自启 | **做，但默认关闭**，设置面板可开 |
| D7 | 暗色主题 | **纳入 v1**，跟随系统 + 可强制（令牌级切换，见 5.1） |
| D8 | 多选 + 撤销删除 | **纳入 v1**（规则见 6.1 / 6.2，删除提供 5 秒撤销窗口） |
| D9 | 非图片普通文件拖入 | 暂不入库并提示；后续再加「文件 unit」（可在 P7 评估） |
| D10 | 界面语言 | 仅简体中文，文案集中资源字典，预留多语言能力 |
| D11 | 面板默认尺寸 | 360×480，可缩放并记忆 |
| D12 | 持久化方案 | JSON + 图片独立落盘（`%APPDATA%\DeskTools\`），仓储接口预留 SQLite 升级 |

> 以上 12 项在 P0 起生效；如需调整，改动成本在 P3 之前都很低，P4 之后（逻辑管线与持久化落地）再改动会牵动多个项目。

**进入 P0 前唯一仍开放的问题**：内核数据结构（`ShelfUnit` 的能力表达）用「`UnitKind` 枚举 + 注册表」还是「完全开放的 string kind + 插件发现」？—— **已决策：采用封闭枚举 + 注册表**，并已实现。

---

## 12. 实施进度

### 已完成（P0–P4）

| 阶段 | 状态 | 实际交付 |
| --- | --- | --- |
| P0 骨架 | ✅ | `DeskTools.slnx` + 4 工程 + DI 组合根 + 文件日志 + 单实例互斥 + `Directory.Build.props` 统一告警即失败 |
| P1 UI 外壳 | ✅ | 设计令牌双主题、控件样式、无边框置顶窗口、36px 可拖拽顶栏、滚动面板、空态、消息条、缩放热区（Win32 `WM_NCHITTEST`） |
| P2 UI 卡片 | ✅ | 三类卡片模板 + 悬停小标 + 选中/编辑态 + `UnitTemplateSelector`（按 `ViewKey` 分派，新增类型零改动） |
| P3 UI 接线 | ✅ | `PanelViewModel` / `UnitViewModel` 全套命令、多选（Ctrl/Shift）、行内编辑提交、右键菜单数据 |
| P4 逻辑管线 | ✅ | `RawContent` + `UnitContentFactory` + `ShelfService`、真实剪贴板（重试 + HTML 降级）、拖拽解析、图片哈希落盘与缩略图 |
| P5 系统集成 | ✅ | `Alt+X` 全局热键（含占用提示）、托盘图标与菜单、单实例、开机自启服务（默认关） |
| P6 持久化 | ✅ | `shelf.json` / `settings.json` 原子写、防抖保存、损坏备份、窗口位置记忆与拔屏回退 |
| P8 测试 | ✅（部分） | 41 个单测（Domain 集合语义、Catalog 注册完整性、多选复制语义、撤销精确还原、设置校验、HTML 降级） |

### 尚未完成（P7）

| 项 | 说明 |
| --- | --- |
| 设置界面 | 当前顶栏「设置」按钮打开数据目录作为降级；需要真正的设置窗口（改热键、开关自启、主题强制、缩略图开关） |
| 拖拽排序与插入指示线 | 领域层已支持 `Move` / `Insert`，UI 侧尚未接线 |
| 单元测试补充 | `PanelViewModel` 命令级测试、`JsonShelfStore` 往返测试、缩略图管线测试 |
| 发布打包 | `dotnet publish` 单文件 + 可选 Inno Setup 安装包 |
| 存储清理 | 未引用图片的 GC 清理（当前删除 unit 不会删除磁盘图片，等待撤销窗口与引用计数一起做） |

### 实施中修正的设计细节（与初版计划的差异）

1. **`Alt+X` 热键**：`Alt+字母` 属菜单助记符空间，已加入手工冲突检测清单（画图/写字板/Excel/浏览器前台）。
2. **卡片外壳抽成 `UnitCard` 控件**：初版打算用 `ContentPresenter` 承载模板，但 `ContentPresenter` 不能定义 `ControlTemplate`；改为自定义 `ContentControl` 派生控件，三类卡片共用一份外壳。
3. **新增 `ThumbnailLoadScheduler`**：图片解码必须异步，但逐张 `Task.Run` 会产生解码风暴；改为单通道 + 卡片间让步。
4. **新增 `ImageBytesCache`（LRU）**：避免滚动时反复读盘与重复解码。
5. **新增 `HtmlTextConverter`（位于 Application 层）**：浏览器拖拽常只提供 `HTML Format`，必须做 HTML→纯文本降级，这是平台无关的纯文本处理，因此不在 Infrastructure。
6. **剪贴板与拖拽共用一个 `DataObjectReader`**：避免两条通道的格式优先级出现差异（否则会出现「复制能存、拖动存不进去」）。
7. **顶栏去掉关闭按钮、退出只走托盘菜单**（实测反馈调整）：初版把「✕ = 退出」放在顶栏，实际预览时连续两次被误点导致程序退出。现改为：顶栏只保留撤销 / 清空 / 最小化 / 设置；`Esc` 与 `Alt+F4` 也只收起面板；退出唯一入口是托盘右键菜单（`PanelWindow.PrepareForShutdown()` 放行关闭拦截）。
8. **拖入普通文件改为存「文件所在目录」**（实测反馈调整）：原规则是「非图片文件不入库并提示」，但用户拖文件的真实意图往往是「我要经常回到这个目录」。改为存入父目录（同目录去重、`.lnk` 先解析目标），这也让「拖资源管理器地址栏面包屑 / 标题栏来保存当前目录」可以直接生效 —— 那是 Explorer 自身提供的拖拽源，会投递标准 `FileDrop`。

### 实测验证方式（不依赖人工点击）

自动化环境下通过以下手段完成了真实链路验证：

- 启动真实进程并**枚举窗口**确认面板出现、位置为「工作区右下角 - 16px」；
- 用 `SendKeys` 发送真实 `Ctrl+V` / `Delete` / `Ctrl+Z`，并核对 `shelf.json` 与日志；
- 真实截图 + 视觉走查（浅色 / 深色各一轮），据此修复了 4 个真实缺陷：
  ① 主题切换被 `UnitTemplates.xaml` 中重复合并的令牌字典覆盖；
  ② `H.NotifyIcon` 要求 `BitmapImage.UriSource`，从内存流构造的帧会导致托盘图标加载失败；
  ③ 缩略图解码与编码跨线程，触发 `BitmapSource` 线程亲和性异常（真实 bug，非测试代码问题）；
  ④ 悬停小标叠在亮色图片上几乎不可见；
- `DESKTOOLS_SEED_SAMPLE=1` 注入样例数据，用于覆盖「图片 / 文件夹」两类只能由拖拽产生的卡片。
