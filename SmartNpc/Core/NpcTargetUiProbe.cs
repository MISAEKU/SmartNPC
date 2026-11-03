using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace SmartNpc.Core;

/// <summary>
/// v0.18.48 —— 《冒险者》取证探针（<b>纯只读</b>，不写任何游戏内存）。
///
/// <para><b>为什么要它</b>：v0.18.46 / v0.18.47 两轮都是"在她身上找《冒险者》"，全落空：</para>
/// <list type="bullet">
/// <item>v0.18.46：<c>FC槽(1B98)=ptr=0x7FF61D3EA9C2 used=0 len=1 text=" "</c>；</item>
/// <item>v0.18.47：全字段安全扫描 <c>Utf8候选=3｜内联冒险者=[]</c>。</item>
/// <item>v0.18.48 静态取证：字符串「冒险者」在 ffxiv_dx11.exe 里 <b>UTF-8 / UTF-16LE 都是 0 命中</b>
///       （exe 里仅有的《》属于《最终幻想14》启动报错提示），⇒ 它<b>不在代码里，在游戏数据表 exd 里</b>，
///       由目标信息栏 addon 的文本节点渲染，所以改她身上的字节永远改不掉。</item>
/// </list>
///
/// <para>本探针一次采样同时回答三件事：</para>
/// <list type="number">
/// <item>她的 <c>ClassJob</c> / <c>Level</c>，以及 ClassJob 表里哪个 row 的名字是「冒险者」（用 Lumina 查）；</item>
/// <item>她 Character 上 <c>+0x1B98</c>（AgentHUD_UpdateTargetInfo 里 Pc 分支读走的那个槽位）
///       与 <c>+0x1A0</c> 的 <b>原始 32 字节 + 按 Utf8String 实锤布局解出来的内容</b>
///       （⚠️ 真实布局是 <c>+0x00 StringPtr / +0x08 BufSize / +0x10 BufUsed / +0x18 StringLength</c>，
///        v0.18.46 按 +0x0C/+0x10 读，读串了）；</item>
/// <item>目标信息栏各个 addon 里<b>所有文本节点的实际文本</b> —— UI 上到底渲染了什么，一次看清楚。</item>
/// </list>
///
/// <para>落盘 <c>target-ui.txt</c>：<b>按内容去重</b>保留最近 24 个不同状态（选中她 / 选中别人 都能留下），
/// 每 0.5 秒采样一次。</para>
/// </summary>
internal static unsafe class NpcTargetUiProbe
{
    private const float Interval   = 0.5f;
    private const int   KeepSamples = 24;

    private static readonly string FilePath = BuildPath();
    private static readonly List<string> Samples = new();

    private static DateTime _last = DateTime.MinValue;
    private static nint     _lastNpc;
    private static string   _lastSignature = string.Empty;
    private static int      _seq;

    /// <summary>目标信息栏相关的 addon（内部名不受本地化影响）。</summary>
    private static readonly string[] AddonNames =
    {
        "_TargetInfo",
        "_TargetInfoMainTarget",
        "_TargetInfoBuffDebuff",
        "_TargetInfoCastBar",
        "_TargetInfoSubTarget",
        "_FocusTargetInfo",
    };

    /// <summary>最近一次采样的摘要（面板显示用）。</summary>
    public static string LastSummary { get; private set; } = "尚未采样";

    /// <summary>最近一次错误。</summary>
    public static string LastError { get; private set; } = string.Empty;

    private static string BuildPath()
    {
        try
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncherCN", "pluginConfigs", "SmartNpc", "target-ui.txt");
        }
        catch
        {
            return string.Empty;
        }
    }

    public static void Tick(GameObject* npc)
    {
        if (npc == null || FilePath.Length == 0)
            return;

        var now = DateTime.UtcNow;
        var force = (nint)npc != _lastNpc;
        if (!force && (now - _last).TotalSeconds < Interval)
            return;

        _last = now;
        _lastNpc = (nint)npc;

        try
        {
            Sample(npc);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private static void Sample(GameObject* npc)
    {
        var sb = new StringBuilder();

        // ── ① 她本人 ──
        var ch = (Character*)npc;
        byte job = 0, lvl = 0;
        try { job = ch->ClassJob; lvl = ch->Level; } catch { /* 字段漂移也不影响其余 */ }

        sb.Append("== #").Append(++_seq).Append(' ').Append(DateTime.Now.ToString("HH:mm:ss")).AppendLine(" ==");
        sb.Append("她 0x").Append(((nint)npc).ToString("X"))
          .Append(" 种类=").Append(npc->ObjectKind)
          .Append(" 名字=\"").Append(NpcNaming.ReadName((nint)npc)).Append('"')
          .Append(" ClassJob=").Append(job)
          .Append(" Level=").Append(lvl)
          .AppendLine();

        // ── ② 当前硬目标是谁（区分"选中她"和"选中玩家"两种状态）──
        AppendTarget(sb, npc);

        // ── ③ 她身上的可疑槽位（原始字节 + 按实锤 Utf8String 布局解码）──
        sb.AppendLine(DumpSlot((nint)npc, 0x1B98, "Char+1B98（UpdateTargetInfo 的 Pc 分支读走的槽位）"));
        sb.AppendLine(DumpSlot((nint)npc, 0x1A0,  "Char+1A0（同上，另一个被读走的 Utf8String）"));

        // ── ④ ClassJob 数据源：表里到底哪一行叫「冒险者」──
        AppendClassJob(sb, job);

        // ── ⑤ 目标信息栏 UI 实际渲染的文本 ──
        AppendAddons(sb);

        var text = sb.ToString();

        // 内容去重：状态没变就不重复落盘，这样 24 份能覆盖更多不同状态
        var signature = SignatureOf(text);
        if (signature == _lastSignature && !text.Contains("#1 "))
            return;
        _lastSignature = signature;

        Samples.Add(text);
        while (Samples.Count > KeepSamples)
            Samples.RemoveAt(0);

        LastSummary = $"已采样 {_seq} 次｜文件={FilePath}";

        try
        {
            var body = new StringBuilder();
            body.Append("SmartNpc 目标栏取证（只读）  写入时间=")
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).AppendLine();
            body.Append("⚠️ 只记录 UI 实际渲染的文本与她的字段原始值，不写任何游戏内存").AppendLine();
            body.AppendLine(new string('-', 70));
            foreach (var s in Samples)
                body.AppendLine(s);
            File.WriteAllText(FilePath, body.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 写不出文件不影响插件
        }
    }

    private static string SignatureOf(string text)
    {
        // 去掉时间/序号，只看实质内容
        var i = text.IndexOf("她 0x", StringComparison.Ordinal);
        return i < 0 ? text : text.Substring(i);
    }

    // ── 目标 ──────────────────────────────────────────────────────────────
    private static void AppendTarget(StringBuilder sb, GameObject* npc)
    {
        try
        {
            var ts = TargetSystem.Instance();
            if (ts == null) { sb.AppendLine("目标系统=不可用"); return; }

            var tgt = ts->Target;
            sb.Append("当前硬目标=").Append(tgt == null ? "（无）" : ((nint)tgt == (nint)npc ? "她" : "别人(0x" + ((nint)tgt).ToString("X") + ")"))
              .Append(" 悬停=").Append((nint)ts->MouseOverTarget == (nint)npc ? "她" : "别的")
              .AppendLine();
        }
        catch (Exception ex)
        {
            sb.AppendLine("目标系统异常: " + ex.Message);
        }
    }

    // ── 槽位原始转储 ──────────────────────────────────────────────────────
    /// <summary>
    /// 把一个 8 字节对齐槽位按两种可能解释都打出来：
    /// A) 槽位本身是 <c>Utf8String</c>（StringPtr 在 +0）；
    /// B) 槽位是指向 <c>Utf8String</c> 的指针。
    /// 两种都给原始字节，谁对一眼就能看出来。
    /// </summary>
    private static string DumpSlot(nint obj, int off, string label)
    {
        var sb = new StringBuilder();
        sb.Append("  ").Append(label).AppendLine();

        try
        {
            var p = (byte*)(obj + off);
            var hex = new StringBuilder();
            for (var i = 0; i < 32; i++)
                hex.Append(p[i].ToString("X2")).Append(' ');
            sb.Append("    原始32B: ").AppendLine(hex.ToString().TrimEnd());

            sb.Append("    解释A(槽位=Utf8String): ").AppendLine(DescribeUtf8(obj + off));

            var inner = *(nint*)(obj + off);
            if (inner != 0)
            {
                var q = (byte*)inner;
                var hex2 = new StringBuilder();
                for (var i = 0; i < 24; i++)
                    hex2.Append(q[i].ToString("X2")).Append(' ');
                sb.Append("    解释B(槽位=指针→0x").Append(inner.ToString("X")).Append("): 目标24B: ")
                  .AppendLine(hex2.ToString().TrimEnd());
                sb.Append("    解释B解码: ").AppendLine(DescribeUtf8(inner));
            }
        }
        catch (Exception ex)
        {
            sb.Append("    读取失败: ").AppendLine(ex.Message);
        }

        return sb.ToString();
    }

    /// <summary>按实锤布局解一个 Utf8String：+0 StringPtr / +8 BufSize / +0x10 BufUsed / +0x18 StringLength。</summary>
    private static string DescribeUtf8(nint slot)
    {
        try
        {
            var ptr   = *(byte**)slot;
            var size  = *(ulong*)(slot + 0x08);
            var used  = *(ulong*)(slot + 0x10);
            var len   = *(ulong*)(slot + 0x18);
            var empty = *(byte*)(slot + 0x20);
            var inline = *(byte*)(slot + 0x21);

            var text = ReadCString(ptr, 64);

            return $"StringPtr=0x{(nint)ptr:X} BufSize={size} BufUsed={used} Len={len} " +
                   $"IsEmpty={empty} Inline={inline} 文本=\"{Escape(text)}\"";
        }
        catch (Exception ex)
        {
            return "解码失败: " + ex.Message;
        }
    }

    private static string ReadCString(byte* p, int max)
    {
        if (p == null)
            return string.Empty;

        var len = 0;
        while (len < max && p[len] != 0)
            len++;

        var bytes = new byte[len];
        for (var i = 0; i < len; i++)
            bytes[i] = p[i];

        return Encoding.UTF8.GetString(bytes);
    }

    private static string Escape(string s)
        => s.Replace("\n", "\\n").Replace("\r", "\\r");

    // ── ClassJob 表 ───────────────────────────────────────────────────────
    private static void AppendClassJob(StringBuilder sb, byte jobId)
    {
        try
        {
            var sheet = Plugin.Data?.GetExcelSheet<ClassJob>();
            if (sheet == null)
            {
                sb.AppendLine("  ClassJob 表不可用");
                return;
            }

            foreach (var row in sheet)
            {
                var name = row.Name.ExtractText();
                var isHers = row.RowId == jobId;
                var isAdv  = name.Contains("冒险者", StringComparison.Ordinal);
                if (!isHers && !isAdv)
                    continue;

                sb.Append("  ClassJob[").Append(row.RowId).Append("]=\"").Append(name).Append('"')
                  .Append(isHers ? " ←她的" : "")
                  .Append(isAdv  ? " ←含「冒险者」" : "")
                  .AppendLine();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("  ClassJob 表异常: " + ex.Message);
        }
    }

    // ── UI 文本节点 ───────────────────────────────────────────────────────
    private static void AppendAddons(StringBuilder sb)
    {
        sb.AppendLine("  -- 目标栏 addon 文本节点 --");

        foreach (var name in AddonNames)
        {
            nint addr;
            try
            {
                addr = Plugin.GameGui?.GetAddonByName(name) ?? 0;
            }
            catch (Exception ex)
            {
                sb.Append("  addon ").Append(name).Append(" 取值异常: ").AppendLine(ex.Message);
                continue;
            }

            if (addr == 0)
                continue;   // 没加载就不刷屏

            try
            {
                var unit  = (AtkUnitBase*)addr;
                var count = unit->UldManager.NodeListCount;
                sb.Append("  addon ").Append(name)
                  .Append(" @0x").Append(addr.ToString("X"))
                  .Append(" 节点数=").Append(count)
                  .AppendLine();

                var n = Math.Min((int)count, 400);
                var printed = 0;
                for (var i = 0; i < n; i++)
                {
                    var node = unit->UldManager.NodeList[i];
                    if (node == null)
                        continue;
                    if (node->Type != NodeType.Text)
                        continue;

                    var text = (AtkTextNode*)node;
                    var s = DescribeUtf8((nint)(&text->NodeText));
                    sb.Append("    id=").Append(node->NodeId).Append(' ').AppendLine(s);

                    if (++printed > 40)
                    {
                        sb.AppendLine("    （后续文本节点省略）");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                sb.Append("  addon ").Append(name).Append(" 遍历异常: ").AppendLine(ex.Message);
            }
        }
    }
}
