# 桌面置物架 · DeskShelf

> 项目名（英文）：**DeskShelf**　中文名：**桌面置物架**
> 早期版本曾用名 DeskTools，更名后会自动迁移旧数据目录（见下文「数据位置」）。

一个常驻 Windows 桌面的**置顶小面板**：把临时要用的东西（文本、图片、文件夹路径）暂存成卡片，
随时用快捷键呼出取用，不用在资源管理器里反复翻找。

- 目标框架：`.NET 8 (net8.0-windows)` + `WPF`
- 平台：Windows 10 1809+ / Windows 11（64 位）
- 当前版本：**v1.2.3**（应用内在线更新 + 新建文本 unit + 若干修复）

---

## 下载安装

到 [**Releases**](https://github.com/zhaodefan/DeskShelf/releases) 页面下载：

| 文件 | 说明 |
| --- | --- |
| `DeskShelf-<版本>-setup.exe` | 安装程序。**免管理员权限**，自动创建开始菜单 / 桌面快捷方式，可选开机自启 |
| `DeskShelf-<版本>-portable-win-x64.zip` | 便携版。解压即用，不写注册表 |

自包含单文件，**无需预先安装 .NET 运行时**。

## 从源码构建

```powershell
# 构建 / 运行 / 测试
dotnet build .\DeskShelf.slnx
dotnet run --project .\src\DeskShelf.App\DeskShelf.App.csproj
dotnet test  .\tests\DeskShelf.Tests\DeskShelf.Tests.csproj

# 可选的联网集成测试（默认跳过，会访问真实 GitHub API）
# 验证 Release JSON 契约、安装包识别与下载完整性
$env:DESKSHELF_UPDATE_INTEGRATION = "1"
$env:GITHUB_TOKEN = (gh auth token)   # 可选，未认证只有 60 次/小时
dotnet test  .\tests\DeskShelf.Tests\DeskShelf.Tests.csproj --filter "FullyQualifiedName~Integration"

# 一键出发布产物（单文件 + 便携 zip + 安装程序）
# 生成安装包需要 Inno Setup：winget install --id JRSoftware.InnoSetup
powershell -File tools\Build-Release.ps1 -Version 1.2.0
```

> Visual Studio 未安装 WPF 项目模板时，请统一使用 `dotnet` CLI 构建与运行。

---

## 已实现的功能

| 功能 | 说明 |
| --- | --- |
| 面板窗口 | 无边框、置顶、可缩放、可拖动；默认出现在**鼠标所在屏的右下角**，位置与尺寸自动记忆 |
| 全局热键 | `Alt+X` 呼出 / 收起面板（被占用时托盘提示，可在设置中改键） |
| 托盘常驻 | 双击托盘图标切换面板；右键菜单提供显示/隐藏、清空、打开数据目录、退出 |
| 顶栏按钮 | 撤销、清空、**最小化**（收起面板，托盘图标继续常驻）、**设置**。**不提供关闭按钮** —— 退出只走托盘右键菜单，避免误触 |
| 设置界面 | 顶栏「设置」打开独立窗口：改呼出热键（**按键录入** + 占用检测）、明暗主题（即时预览）、开机自启、图片缩略图开关、unit 数量上限、打开数据目录、清空面板。开关类**即时生效**，热键在「保存」时生效，**取消可整体回滚** |
| 退出方式 | 右键任务栏通知区域的小图标 → **退出桌面置物架**（`Esc` / `Alt+F4` 只会收起面板，不会退出） |
| 在线更新 | 设置 → 更新 里「检查更新」→「下载并安装」：自动下载官方安装包、后台静默升级并重启；也可右键托盘图标选「检查更新…」 |
| 新建文本 | 标题栏下方的「+ 新建文本」按钮：新建一个空白文本 unit 并立刻进入编辑态，直接打字即可 |
| 文本 unit | `Ctrl+V` 粘贴；面板内展示文本；双击行内编辑；`Ctrl+C` 复制文本 |
| 图片 unit | 粘贴图片 / 拖入图片文件；展示缩略图；双击用系统看图程序打开；`Ctrl+C` 复制图片 |
| 文件夹 unit | 从资源管理器拖入；展示「文件夹图标 + 路径」；双击用资源管理器打开；按需求**不参与复制** |
| 拖入普通文件 | 自动存**它所在的目录**（拖文件的真实意图通常是「我要经常回到这个目录」）；同一目录只存一次 |
| 拖入快捷方式 | `.lnk` 先解析真实目标，再按上述规则处理 |
| 拖拽落盘 | 文件夹、图片文件、普通文件均可拖入；无法识别的格式会给出明确提示 |
| 多选与撤销 | `Ctrl+点击` 多选、`Shift+点击` 区间选择；删除后 5 秒内可撤销（`Ctrl+Z` 或消息条按钮） |
| 明暗主题 | 跟随系统 / 强制浅色 / 强制深色，在设置窗口切换并即时预览 |
| 单实例 | 重复启动会提示并退出，避免热键冲突与数据竞争 |
| 持久化 | 内容与设置存于 `%APPDATA%\DeskShelf\`，原子写入 + 防抖保存 + 损坏文件自动备份 |

## 快捷键

| 按键 | 行为 |
| --- | --- |
| `Alt+X` | 全局：呼出 / 收起面板（可在设置里改成任意组合） |
| `Ctrl+V` | 面板内：把剪贴板内容存为新 unit（追加到列表末尾） |
| `Ctrl+C` | 复制选中的 unit（焦点在编辑框内时由编辑器处理） |
| `Ctrl+Z` | 撤销最近一次删除 / 清空 |
| `Delete` | 移除选中的 unit |
| `Esc` | 编辑中→提交退出编辑；设置窗口内→关闭窗口；否则收起面板（**不退出程序**） |
| `Alt+F4` | 收起面板（同上，退出请用托盘菜单） |
| 双击 unit | 文本→行内编辑；图片→看图程序；文件夹→资源管理器 |
| 右键托盘图标 | 显示/隐藏面板、清空面板、打开数据目录、**退出桌面置物架** |

## 数据位置

```
%APPDATA%\DeskShelf\
├─ shelf.json           置物架内容（schemaVersion 化，便于未来迁移）
├─ settings.json        热键、面板位置尺寸、主题等
├─ images\              原图与缩略图（按内容哈希命名，重复粘贴不重复占空间）
├─ updates\             在线更新下载的安装包（升级完成后可手动删除）
├─ logs\                滚动日志（保留 7 天，排查剪贴板/热键问题用）
└─ backup\              损坏数据文件的自动备份
```

> **从 v1.0.0（DeskTools）升级**：首次启动时会自动把 `%APPDATA%\DeskTools` 整体迁移到
> `%APPDATA%\DeskShelf`（优先移动，失败则复制后删除；迁移失败不会影响启动），
> 旧数据不会丢失。卸载时默认保留该目录。

---

## 架构

依赖方向永远是向内的：`App → Application → Domain`，`Infrastructure` 只实现接口。

```
src/
├─ DeskShelf.Domain/          纯逻辑，零外部依赖（连 WPF 都不引用）
│   ├─ Units/                 ShelfUnit 基类 + TextUnit / ImageUnit / FolderUnit + 能力声明
│   ├─ Shelf/                 Shelf 聚合根（有序集合、增删改移、撤销快照）
│   └─ RawContent.cs          归一化输入：文本 / 图片字节 / 路径集合
│
├─ DeskShelf.Application/     用例编排，同样与 UI 无关
│   ├─ Abstractions/          IClipboardService / IImageStore / IShelfStore / IHotkeyService …
│   ├─ Catalog/UnitCatalog    「unit 类型 → 展示规格 + 能力」的唯一注册表
│   ├─ Services/ShelfService  唯一逻辑入口（摄取、复制、删除、撤销、打开）
│   └─ Undo/                  内存撤销栈
│
├─ DeskShelf.Infrastructure/  所有 OS 调用与 P/Invoke 关在这里
│   ├─ Clipboard/             剪贴板读取（含占用重试、HTML→纯文本降级）
│   ├─ Content/               UnitContentFactory：全应用唯一的 unit 创建入口
│   ├─ Storage/               JSON 仓储、图片文件存储（哈希命名 + 缩略图）
│   ├─ Hotkeys/               RegisterHotKey 全局热键
│   ├─ Windows/               显示器定位、开机自启注册表
│   └─ Launchers/             用默认程序打开图片 / 资源管理器打开路径
│
└─ DeskShelf.App/             WPF 表现层（唯一允许 using System.Windows 的工程）
    ├─ Views/PanelWindow      面板窗口（视觉在 XAML，代码只处理不可声明的窗口与原始输入）
    ├─ ViewModels/            PanelViewModel + UnitViewModel
    ├─ Templates/             DataTemplate 与按 ViewKey 分派的模板选择器
    ├─ Themes/                设计令牌（浅色/深色）、控件样式、图标几何
    └─ Services/              托盘宿主、缩略图加载调度、样例数据注入
```

**四条硬约束**（代码评审重点）：

1. ViewModel 中**不允许**出现 `System.Windows.Clipboard`，一律走 `IClipboardService`；
2. 「粘贴」与「拖拽」共用同一条 `Ingest(RawContent)` 管线，不允许各写一套；
3. 展示细节（图标、按钮可用性）一律来自 `UnitCatalog` 的能力声明，**禁止** `if (unit is TextUnit)` 式分支；
4. `Domain` / `Application` 的引用中不允许出现 `PresentationFramework`。

### 新增一种 unit 类型要改的地方（固定 4 处）

| # | 位置 | 内容 |
| --- | --- | --- |
| 1 | `Domain/Units/XxxUnit.cs` | 新领域类型 + 能力声明 |
| 2 | `Application/Catalog/UnitCatalog.cs` | 注册显示名、图标键、`ViewKey`、能力 |
| 3 | `Infrastructure/Storage/JsonShelfStore.cs` | 持久化映射（`$type` 判别符） |
| 4 | `App/Templates/UnitTemplates.xaml` | 与 `ViewKey` **同名**的 DataTemplate |

`ShelfService`、`PanelViewModel`、模板选择器都无需改动 —— `UnitTemplateSelector` 直接以
`UnitCatalog.ViewKey` 作为资源键查找模板。另有单测 `UnitCatalog_RegistersEveryKind`
保证漏登记时立刻失败。

---

## 开发辅助

### 设计走查样例数据

「图片 / 文件夹」unit 正常只能由拖拽产生，自动化环境下难以触发。需要做视觉走查时：

```powershell
$env:DeskShelf_SEED_SAMPLE = "1"
dotnet run --project .\src\DeskShelf.App\DeskShelf.App.csproj
```

启动后会注入一条文本、一张渐变图片、一个用户主目录文件夹，覆盖三类卡片。
不设该环境变量时**不会**有任何注入行为。

### 手工测试清单（系统级行为无法单测）

- [ ] DPI 缩放 100% / 125% / 150% / 200% 下的定位与清晰度
- [ ] 多显示器（副屏在左 / 主副不同缩放）下右下角定位
- [ ] 任务栏位于顶部 / 左侧 / 自动隐藏时面板不遮挡
- [ ] `Alt+X` 在画图、写字板、Excel、浏览器前台时是否被当作菜单加速键
- [ ] 剪贴板被 Office / 截图工具占用时连续快速 `Ctrl+V`
- [ ] 从 Chrome / VS Code / 记事本 / 资源管理器 / 微信分别拖拽
- [ ] 以管理员权限运行的程序向本工具拖拽（应被 UIPI 拦下并静默失败）
- [ ] 400+ unit 时的滚动流畅度与内存占用
- [ ] 拔掉副屏后窗口是否回到可见区域

## 文档

- 完整开发计划与设计依据：[Docs/Development-Plan.md](Docs/Development-Plan.md)
- 界面走查截图：[Docs/screenshots/](Docs/screenshots/)
