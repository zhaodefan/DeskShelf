using Xunit;

namespace DeskShelf.Tests;

/// <summary>
/// 只在设置了指定环境变量时才运行的测试。
/// </summary>
/// <remarks>
/// 用于访问真实网络的集成测试：默认在测试列表里显示为「已跳过」，
/// 需要时用环境变量启用，避免日常构建依赖网络与消耗 API 配额。
/// </remarks>
public sealed class IntegrationFactAttribute : FactAttribute
{
    /// <summary>创建集成测试特性。</summary>
    /// <param name="environmentVariable">启用开关的环境变量名。</param>
    /// <param name="reason">默认跳过时的说明。</param>
    public IntegrationFactAttribute(string environmentVariable, string reason)
    {
        if (Environment.GetEnvironmentVariable(environmentVariable) != "1")
        {
            Skip = reason;
        }
    }
}
