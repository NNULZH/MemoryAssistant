using MemoryAssistant.Infrastructure.Configuration;

namespace MemoryAssistant.Tests;

/// <summary>
/// 配置加载回归：appsettings.local.json 只应覆盖"它真实写了的键"。
/// 旧实现把文件反序列化成 AppSettings 再逐属性合并，缺失字段会带上类默认值
/// （Model 默认 deepseek-chat），导致本地文件里只有一个 apiKey 也会把
/// appsettings.json 里的 model 冲掉——实测踩到过。
/// </summary>
public sealed class ConfigurationLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ma_cfg_" + Guid.NewGuid().ToString("N")[..8]);

    public ConfigurationLoaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败不影响结论 */ }
    }

    private string Write(string fileName, string json)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void LocalFile_DoesNotClobberKeysItOmits()
    {
        var basePath = Write("appsettings.json", """
        {
          "llm": { "model": "deepseek-reasoner", "temperature": 0.7 },
          "agent": { "enableLlmPlanning": false, "maxRounds": 8 }
        }
        """);
        Write("appsettings.local.json", """{ "llm": { "apiKey": "sk-test" } }""");

        var s = ConfigurationLoader.Load(basePath);

        Assert.Equal("deepseek-reasoner", s.Llm.Model);
        Assert.Equal(0.7, s.Llm.Temperature);
        Assert.Equal("sk-test", s.Llm.ApiKey);
        // bool 也要保持：local 里没提 agent，就不能被默认值翻回去
        Assert.False(s.Agent.EnableLlmPlanning);
        Assert.Equal(8, s.Agent.MaxRounds);
    }

    [Fact]
    public void LocalFile_OverridesKeysItSets()
    {
        var basePath = Write("appsettings.json", """
        { "llm": { "model": "deepseek-reasoner" }, "agent": { "enableLlmPlanning": false } }
        """);
        Write("appsettings.local.json", """
        { "llm": { "model": "deepseek-chat" }, "agent": { "enableLlmPlanning": true } }
        """);

        var s = ConfigurationLoader.Load(basePath);

        Assert.Equal("deepseek-chat", s.Llm.Model);
        Assert.True(s.Agent.EnableLlmPlanning);
    }

    [Fact]
    public void MissingAndBrokenFiles_FallBackToDefaults()
    {
        var basePath = Write("appsettings.json", "{ 这不是合法 JSON");

        var s = ConfigurationLoader.Load(basePath);

        Assert.Equal("deepseek-flash", s.Llm.Model);       // 类默认值
        Assert.True(s.Agent.EnableLlmPlanning);           // 新默认：模型自主规划
        Assert.Equal(6, s.Agent.MaxRounds);
    }
}
