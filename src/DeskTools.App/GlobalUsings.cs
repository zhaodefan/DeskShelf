// 全局 using。
// 注意两点：
// 1. 启用 UseWPF 的工程不会自动获得 System.IO / System.Net 等隐式 using，这里显式补齐；
// 2. 本解决方案的命名空间以 DeskTools.* 开头，会遮蔽 System.Windows.Shapes.Path 等
//    短类型名，因此统一用别名 Path = System.IO.Path（界面代码一律使用 PathGeometry 而非 Path）。
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
global using Path = System.IO.Path;

// 命名空间 DeskTools.Application 会遮蔽 WPF 的 System.Windows.Application，
// 因此统一用别名引用 WPF 应用对象。
global using WpfApp = System.Windows.Application;
