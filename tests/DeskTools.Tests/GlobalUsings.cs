// 测试工程全局 using：
// 1. 补齐 UseWPF 工程缺失的隐式 using；
// 2. Path 用别名指向 System.IO.Path，避免被 DeskTools.* 命名空间遮蔽。
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
global using Path = System.IO.Path;
