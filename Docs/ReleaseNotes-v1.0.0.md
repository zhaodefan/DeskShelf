# DeskTools v1.0.0

一个常驻 Windows 桌面的**置顶小面板**：把临时要用的东西（文本、图片、文件夹路径）暂存成卡片，随时用 `Alt+X` 呼出取用，不用在资源管理器里反复翻找。

![面板与设置](https://raw.githubusercontent.com/zhaodefan/DeskTools/main/Docs/screenshots/p2-all-three-cards-dark.png)

## 下载

| 文件 | 说明 |
| --- | --- |
| **DeskTools-1.0.0-setup.exe**（推荐） | 安装程序。**免管理员权限**（per-user 安装），自动创建开始菜单与桌面快捷方式，可选开机自启 |
| **DeskTools-1.0.0-portable-win-x64.zip** | 便携版。解压后直接运行 `DeskTools.exe`，不写注册表、不留安装痕迹 |

> 自包含单文件，**无需预先安装 .NET 运行时**。

**系统要求**：Windows 10 1809+ / Windows 11，64 位。

## 主要功能

- **置顶小面板**：默认出现在鼠标所在屏的右下角，位置与尺寸自动记忆；可拖拽移动、可缩放
- **三类 unit**
  - 文本：`Ctrl+V` 存入，双击行内编辑，`Ctrl+C` 复制
  - 图片：粘贴或拖入图片文件，展示缩略图，双击用系统看图程序打开
  - 文件夹：从资源管理器拖入，双击用资源管理器打开
- **拖拽落盘**：文件夹 / 图片 / 普通文件都能拖进来；**拖入普通文件会存它所在的目录**（"我要经常回到这个目录"）；`.lnk` 快捷方式会先解析真实目标
- **多选与撤销**：`Ctrl+点击` 多选、`Shift+点击` 区间选择；删除后 5 秒内可撤销
- **快捷呼出**：全局热键 `Alt+X`（可在设置里改成任意组合，带占用检测）
- **托盘常驻**：双击托盘图标切换面板；退出走托盘右键菜单
- **明暗主题**：跟随系统 / 强制浅色 / 强制深色，切换即时预览
- **设置界面**：改热键、开机自启、缩略图开关、unit 数量上限、打开数据目录、清空面板
- **数据安全**：原子写入 + 防抖保存；数据文件损坏时自动备份并以空面板启动，不会因此启动失败

## 快捷键

| 按键 | 行为 |
| --- | --- |
| `Alt+X` | 全局：呼出 / 收起面板（可改） |
| `Ctrl+V` | 面板内：存入剪贴板内容 |
| `Ctrl+C` | 复制选中的 unit |
| `Ctrl+Z` | 撤销最近一次删除 |
| `Delete` | 移除选中的 unit |
| `Esc` | 编辑中→提交；否则收起面板（不退出程序） |
| 双击 unit | 文本→编辑；图片→看图程序；文件夹→资源管理器 |

## 数据位置

```
%APPDATA%\DeskTools\
├─ shelf.json      面板内容    ├─ settings.json  设置
├─ images\         图片原件与缩略图
├─ logs\           运行日志（排查问题用，保留 7 天）
└─ backup\         损坏数据文件的自动备份
```

卸载时**默认保留**这些数据（会询问），便于以后重装继续使用。

## 已知限制

- **把资源管理器窗口（标题栏 / 地址栏）直接拖进面板保存该目录**：做不到。拖放是"拖源"主导的协议，标题栏属于非客户区，系统把它处理成"移动窗口"而非数据传输，应用侧无法介入。请改从**列表里拖文件夹**，或**拖目录里的任意文件**（会存其所在目录）。详细技术分析见仓库 `Docs/Development-Plan.md` §13。
- **开机自启**写在 `HKCU\...\Run`，指向当前 exe 路径；移动便携版目录后需要在设置里重新勾选一次。
- `Alt+X` 属于菜单助记符空间，个别程序前台时可能被其菜单占用；如遇冲突可在设置里换成 `Ctrl+Shift+...` 组合。

## 从源码构建

```powershell
dotnet build .\DeskTools.slnx
dotnet test  .\tests\DeskTools.Tests\DeskTools.Tests.csproj
dotnet run --project .\src\DeskTools.App\DeskTools.App.csproj
```

一键出安装包（需要 .NET 8 SDK；安装包还需要 `winget install --id JRSoftware.InnoSetup`）：

```powershell
powershell -File tools\Build-Release.ps1 -Version 1.0.0
```

## 架构

```
Domain (纯逻辑)  ←  Application (用例)  ←  Infrastructure (系统适配)
                                        ←  App (WPF 表现层)
```

- 表现层与逻辑层严格分离：`Domain` / `Application` 不含任何 WPF 引用，ViewModel 不直接碰剪贴板
- **unit 类型可扩展**：新增类型只需改 4 处（领域类型 + `UnitCatalog` 登记 + 持久化映射 + 一个 `DataTemplate`），模板按 `UnitCatalog.ViewKey` 自动分派，选择器零改动
- 74 个单元测试覆盖领域语义、多选复制规则、撤销还原、热键键名映射、设置保存/取消

---

**首次发布**。欢迎在 Issues 里反馈问题或提需求。
