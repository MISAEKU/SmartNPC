using System;
using System.IO;
using System.Text;

namespace SmartNpc.Core;

/// <summary>
/// 自建诊断文件 —— **不依赖 Dalamud 日志**。
///
/// 为什么需要它：`%APPDATA%\XIVLauncherCN\dalamud.log` 达到 100MB 上限后
/// **直接停写且没有轮转**（本机实测：文件停在 104,857,721 字节，之后插件日志一行都进不去）。
/// 一旦它停了，"让用户贴日志"这条排查链路就断了，插件的任何行为都无从验证。
///
/// 所以插件自己落一份 `pluginConfigs/SmartNpc/last-diag.txt`：
/// 加载过程、每帧心跳、召唤结果都写在这里，任何时候都能看。
///
/// ⚠️ `IDalamudPluginInterface.ConfigDirectory` 是 **DirectoryInfo 不是 string**，要 `.FullName`。
/// </summary>
public sealed class DiagFile
{
    private readonly object _gate = new();

    public DiagFile(string directory)
    {
        DirectoryPath = directory;

        try { Directory.CreateDirectory(directory); }
        catch { /* 建不了目录就只是写不出文件，不影响功能 */ }

        FilePath = Path.Combine(directory, "last-diag.txt");
    }

    public string DirectoryPath { get; }

    public string FilePath { get; }

    /// <summary>整份覆盖写。适合"当前状态快照"。</summary>
    public void Write(string content)
    {
        var body = new StringBuilder();
        body.Append("SmartNpc 诊断").Append("  写入时间=").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).AppendLine();
        body.Append("文件位置=").AppendLine(FilePath);
        body.AppendLine(new string('-', 60));
        body.AppendLine(content);

        try
        {
            lock (_gate)
                File.WriteAllText(FilePath, body.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 写文件失败绝不能影响插件本身
        }
    }

    /// <summary>追加一行（带时间戳）。适合"加载过程"这类流水。</summary>
    public void Append(string line)
    {
        try
        {
            lock (_gate)
                File.AppendAllText(FilePath, DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine,
                                   new UTF8Encoding(false));
        }
        catch
        {
            // 同上
        }
    }

    public override string ToString() => FilePath;
}

/// <summary>
/// 模块初始化探针 —— 用来区分「程序集根本没被真正加载」和「加载了但没构造 Plugin」。
///
/// `[ModuleInitializer]` 在程序集被**真正**加载进运行时时执行。
/// 如果 Dalamud 只是用 MetadataLoadContext 读元数据（扫描阶段），它不会跑。
/// 于是 `module-loaded.txt` 存在与否 = 程序集有没有被真正加载。
/// </summary>
internal static class LoadProbe
{
    internal const string MarkerName = "module-loaded.txt";

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncherCN", "pluginConfigs", "SmartNpc");

            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerName),
                              DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  程序集已被加载" + Environment.NewLine);
        }
        catch
        {
            // 探针失败无所谓
        }
    }
}
