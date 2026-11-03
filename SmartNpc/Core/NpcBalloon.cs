using System;
using System.IO;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace SmartNpc.Core;

/// <summary>
/// v0.18.49 —— 她说话时头顶弹<b>游戏原生气泡</b>（不是我们自己画的 UI）。
///
/// <para><b>出处（本机 FFXIVClientStructs.dll 元数据实锤，不是猜的）</b>：
/// 气泡是<b>每个 Character 自带</b>的结构 —— <c>Character+0x21E0 Balloon</c>（普通聊天气泡）
/// 与 <c>Character+0x2260 YellBalloon</c>（<c>NpcYellBalloon</c>，NPC 喊话气泡）。
/// 后者带真正的开/关入口：</para>
///
/// <list type="bullet">
///   <item><c>NpcYellBalloon.OpenBalloon(string text, float, bool, float, bool, bool, bool, byte)</c></item>
///   <item><c>NpcYellBalloon.CloseBalloon()</c></item>
///   <item>结构字段：<c>+0x00 Text</c>(Utf8String) / <c>+0x68 Character</c> / <c>+0x70 PlayTimer</c> /
///         <c>+0x74 DelayTime</c> / <c>+0x78 State</c> / <c>+0x7C CloseType</c> /
///         <c>+0x80 ParentBone</c> / <c>+0x81 Flags</c></item>
///   <item><c>NpcYellBalloonFlags</c> = None / Valid / SoftOpen / IgnoreRangeCheck / PrintToLog</item>
/// </list>
///
/// <para>⚠️ <b>参数语义尚未实锤</b>（只有类型没有名字）：8 个参数里能确定的是第 1 个是文本，
/// 最后 1 个是 <c>ParentBone</c>。所以本模块<b>不把"什么时候关"交给那个不确定的参数</b>——
/// 时长由我们自己托管（<see cref="Tick"/> 到点调 <c>CloseBalloon</c>），
/// 这是"任何兜底都必须有一条无条件出路"这条铁律在这里的具体形态：
/// 万一时长参数没生效，气泡也不会永远挂她头上。</para>
///
/// <para><b>红线</b>：只碰她自己这个 Character 结构里的 YellBalloon 字段，
/// 不动任何游戏全局注册（v0.18.44 的 EntityId 教训：改游戏用来做全局索引的字段会出大事）。
/// 她是我们自己创建的对象，游戏不会对她用 NPC 喊话，这块内存本来就是空的。</para>
/// </summary>
internal static unsafe class NpcBalloon
{
    private static readonly string FilePath = BuildPath();

    private static nint     _owner;              // 当前气泡挂在谁身上（0 = 没有）
    private static DateTime _closesAt = DateTime.MinValue;
    private static DateTime _lastProbe = DateTime.MinValue;

    /// <summary>最近一次操作的结果（面板显示用）。</summary>
    public static string LastSummary { get; private set; } = "尚未弹出过气泡";

    /// <summary>最近一次失败原因（面板显示用）。</summary>
    public static string LastError { get; private set; } = string.Empty;

    /// <summary>气泡当前是否挂在她头上（面板显示用）。</summary>
    public static bool IsOpen => _owner != nint.Zero;

    private static string BuildPath()
    {
        try
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncherCN", "pluginConfigs", "SmartNpc", "balloon-trace.txt");
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 在她头顶弹一句。
    /// </summary>
    /// <param name="c">她的 Character*。</param>
    /// <param name="text">台词（游戏自己排版/换行）。</param>
    /// <param name="seconds">停留时长（秒）—— 由我们托管，不依赖那个语义未定的参数。</param>
    public static bool Show(Character* c, string text, float seconds)
    {
        if (c == null || string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            // 气泡要挂在她身上：结构里的 Character 指针就是"宿主"。
            c->YellBalloon.Character = c;

            // ★ 参数语义由 2026-10-12 12:56 的用户实测数据反推实锤（不是猜）：
            //   初版写成 (text, 0f, false, seconds, …) ⇒ 回读 `PlayTimer=0.00 Delay=4.00 State=Waiting`——
            //   第 4 个 float 是 **DelayTime（延迟多久才弹）**，第 2 个 float 才是 **PlayTimer（显示时长）**。
            //   于是气泡一直停在 Waiting 等那 4 秒，而我们 4 秒后正好把它收了 ⇒ 永远看不见。
            //   正确传法：时长放第 2 个参数，延迟给 0。
            //   三个 bool 仍按最保守的 false 传（实测 Flags 只置了 Valid=0x01，它们不是 flag 位）。
            c->YellBalloon.OpenBalloon(text, seconds, false, 0f, false, false, false, 0);

            _owner    = (nint)c;
            _closesAt = DateTime.UtcNow.AddSeconds(Math.Max(0.5f, seconds));

            LastError   = string.Empty;
            LastSummary = $"已弹出 \"{Trim(text)}\"｜State={c->YellBalloon.State} " +
                          $"PlayTimer={c->YellBalloon.PlayTimer:0.00} Delay={c->YellBalloon.DelayTime:0.00} " +
                          $"Flags=0x{(byte)c->YellBalloon.Flags:X2}";
            Append(LastSummary);
            return true;
        }
        catch (Exception ex)
        {
            LastError   = ex.Message;
            LastSummary = "弹出失败：" + ex.Message;
            Append(LastSummary);
            return false;
        }
    }

    /// <summary>
    /// 每帧推进：到点就收。换人（换区重召唤）也立刻收 —— 旧气泡不该跟着新对象。
    /// </summary>
    public static void Tick(Character* c)
    {
        if (_owner == nint.Zero)
            return;

        var now     = DateTime.UtcNow;
        var expired = now >= _closesAt;
        var changed = c == null || (nint)c != _owner;

        // 开着的时候每秒回读一次原生结构 —— 看它有没有真的从 Waiting 走到 Active。
        // 上次就是靠这行数据反推出参数传反了，留着以后排查。
        if (!expired && !changed && _owner != nint.Zero && (now - _lastProbe).TotalSeconds >= 1.0)
        {
            _lastProbe = now;
            try
            {
                var b = &((Character*)_owner)->YellBalloon;
                Append($"存活中 State={b->State} PlayTimer={b->PlayTimer:0.00} Delay={b->DelayTime:0.00} " +
                       $"Flags=0x{(byte)b->Flags:X2}");
            }
            catch { /* 读不到就算了 */ }
            return;
        }

        if (!expired && !changed)
            return;

        try
        {
            ((Character*)_owner)->YellBalloon.CloseBalloon();
            Append($"已收回（{(changed ? "对象已换" : "到点")}）");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Append("收回失败：" + ex.Message);
        }
        finally
        {
            _owner = nint.Zero;
        }
    }

    /// <summary>立刻收（她被送走 / 插件卸载 / 用户手动关）。</summary>
    public static void Hide()
    {
        if (_owner == nint.Zero)
            return;

        try
        {
            ((Character*)_owner)->YellBalloon.CloseBalloon();
            Append("已收回（手动）");
        }
        catch { /* 收不掉也不能影响别的 */ }
        finally
        {
            _owner = nint.Zero;
        }
    }

    private static string Trim(string s)
        => s.Length <= 24 ? s : s.Substring(0, 24) + "…";

    private static void Append(string line)
    {
        if (FilePath.Length == 0)
            return;

        try
        {
            File.AppendAllText(FilePath,
                               DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine,
                               new UTF8Encoding(false));
        }
        catch
        {
            // 写不出文件不影响气泡本身
        }
    }
}
