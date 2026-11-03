using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using SmartNpc.Core;

namespace SmartNpc.UI;

/// <summary>
/// 单个副 NPC 的独立设置窗口（v0.18.69.23）。
///
/// <para>用户要求「别把所有设置都堆在一个页面里」：主面板的副 NPC 区只留横栏
/// （状态 + 召唤/送走 + 复制 + 打开设置窗口），全部详细设置按槽位拆到这里的
/// 独立窗口 —— 每个副 NPC 一个窗口，可以同时开多个对照着调。</para>
///
/// <para>结构沿用主面板的分组折叠习惯：基本信息 → 外观 → 站位 → 台词 →
/// 待机姿势 → 语音与气泡。所有改动直接写槽位配置，每帧经
/// <see cref="NpcController.ApplySettings"/> 转发即时生效，关窗时落盘。</para>
/// </summary>
public sealed unsafe class SubNpcWindow : Window
{
    private readonly Configuration _config;
    private readonly NpcController _controller;
    private readonly int _slot;

    private static readonly string[] PersonaNames = ["安静", "元气", "傲娇", "弱气"];

    public SubNpcWindow(int slot, Configuration config, NpcController controller)
        : base(GetTitle(slot, config), ImGuiWindowFlags.None)
    {
        _slot       = slot;
        _config     = config;
        _controller = controller;

        Size    = new Vector2(400f, 620f);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360f, 380f),
            MaximumSize = new Vector2(620f, 900f),
        };
    }

    /// <summary>标题带槽位号与当前名字（名字改了跟着变）。</summary>
    private static string GetTitle(int slot, Configuration config)
    {
        var name = slot < config.SubNpcs.Count
            ? Core.NpcNaming.EffectiveName(config.SubNpcs[slot].Name, $"副 NPC {slot + 1}")
            : $"副 NPC {slot + 1}";
        return $"副 NPC {slot + 1}（{name}）###SmartNpcSub{slot}";
    }

    private SubNpcSetting S
        => _config.SubNpcs[Math.Clamp(_slot, 0, _config.SubNpcs.Count - 1)];

    public override void Draw()
    {
        var s = S;

        // ── 顶部：状态 + 召唤/送走 ──
        var alive = _controller.SubNpcs?.IsAlive(_slot) ?? false;

        var enabled = s.Enabled;
        if (ImGui.Checkbox($"启用槽位##subwen{_slot}", ref enabled))
            s.Enabled = enabled;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("启用后才能召唤这个槽位。");

        ImGui.SameLine();
        ImGui.TextDisabled(_controller.SubNpcs?.StatusText(_slot) ?? "？");

        ImGui.Spacing();

        var halfWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        var summonLabel = alive ? $"送走##wsum{_slot}" : $"召唤##wsum{_slot}";
        if (ImGui.Button(summonLabel, new Vector2(halfWidth, 0f)))
        {
            if (alive) _controller.SubNpcs!.Dismiss(_slot);
            else       _controller.SubNpcs!.Summon(_slot);
        }

        ImGui.SameLine();
        if (ImGui.Button($"复制并使用主NPC设置##wcopy{_slot}", new Vector2(-1f, 0f)))
            _controller.SubNpcs?.CopyFromMain(_slot);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把主 NPC 的全部设置抄进这个槽位（站位偏移保留自己的）。\n语音 / 气泡置为「跟随主 NPC 设置」。她在场就立刻生效。");

        ImGui.Spacing();

        if (Header("基本信息", open: true))  { ImGui.Indent(); DrawBasics(s);      ImGui.Unindent(); }
        if (Header("外观",       open: true))  { ImGui.Indent(); DrawDesign(s);     ImGui.Unindent(); }
        if (Header("站位",       open: true))  { ImGui.Indent(); DrawPosition(s);   ImGui.Unindent(); }
        if (Header("台词",       open: true))  { ImGui.Indent(); DrawLines(s);      ImGui.Unindent(); }
        if (Header("待机姿势",   open: false)) { ImGui.Indent(); DrawIdlePose(s);   ImGui.Unindent(); }
        if (Header("语音与气泡", open: false)) { ImGui.Indent(); DrawVoiceBalloon(s); ImGui.Unindent(); }

        // 与主面板同策略：每帧校验 + 推给运行中的模块（即时生效）。
        _config.Sanitize();
        _controller.ApplySettings(_config);
    }

    /// <summary>关窗时落盘（每帧写盘太浪费，开窗期间的改动最后存一次）。</summary>
    public override void OnClose() => _controller.SaveNow();

    /// <summary>分组标题（可折叠）。返回 false = 收起，跳过整组内容。</summary>
    private static bool Header(string title, bool open)
    {
        ImGui.Spacing();
        return ImGui.CollapsingHeader(title,
            open ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
    }

    private static void SubLabel(string title)
    {
        ImGui.Spacing();
        ImGui.TextDisabled($"── {title} ──");
        ImGui.Spacing();
    }

    // ── 基本信息 ─────────────────────────────────────────────────

    private void DrawBasics(SubNpcSetting s)
    {
        var name = s.Name ?? string.Empty;
        if (ImGui.InputText($"名字##wname{_slot}", ref name, 24))
            s.Name = name;
        ImGui.TextDisabled("留空 = 显示游戏分配的默认名。");

        var persona = s.Persona;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo($"性格##wp{_slot}", PersonaNames[Math.Clamp(persona, 0, PersonaNames.Length - 1)]))
        {
            for (var p = 0; p < PersonaNames.Length; ++p)
            {
                if (ImGui.Selectable($"{PersonaNames[p]}##wp{_slot}_{p}", persona == p))
                    s.Persona = p;
            }
            ImGui.EndCombo();
        }

        var hint = persona switch
        {
            0 => "安静：话少、跟得远、动作小。",
            1 => "元气：话多、贴得近、爱招手拍手。",
            2 => "傲娇：嘴硬心软，落点全是关心。",
            _ => "弱气：小心翼翼，但很依赖你。",
        };
        ImGui.TextDisabled($"{hint}（台词 {Core.NpcLines.CountFor((Core.NpcBrain.Persona)persona)} 条）");

        var title = s.PlayerTitle ?? string.Empty;
        if (ImGui.InputText($"对你的称谓##wtitle{_slot}", ref title, 16))
            s.PlayerTitle = title;
        ImGui.TextDisabled("台词里的 {{称谓}} 会替换成这个。留空 = 「主人」。");
    }

    // ── 外观 ─────────────────────────────────────────────────────

    private void DrawDesign(SubNpcSetting s)
    {
        var designs = _controller.GetDesigns();

        var designName = "（照抄玩家外观）";
        if (s.GlamourerDesignId != Guid.Empty)
        {
            designName = s.GlamourerDesignName;
            foreach (var (id, dn) in designs)
                if (id == s.GlamourerDesignId) { designName = dn; break; }
        }

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo($"外观设计##wdesign{_slot}", designName))
        {
            if (ImGui.Selectable($"（照抄玩家外观）##wnone{_slot}", s.GlamourerDesignId == Guid.Empty))
            {
                s.GlamourerDesignId   = Guid.Empty;
                s.GlamourerDesignName = string.Empty;
            }

            foreach (var (id, dn) in designs)
            {
                if (!ImGui.Selectable($"{dn}##wd{_slot}{id}", id == s.GlamourerDesignId))
                    continue;
                s.GlamourerDesignId   = id;
                s.GlamourerDesignName = dn;
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled(designs.Count == 0
            ? "没读到 Glamourer 设计，外观将照抄玩家。"
            : "选中即换上；召唤时自动套用。");
    }

    // ── 站位 ─────────────────────────────────────────────────────

    private void DrawPosition(SubNpcSetting s)
    {
        var side = Math.Clamp(s.FollowSide, -6f, 6f);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"侧向站位##wside{_slot}", ref side, -6f, 6f, "%.1f 米"))
            s.FollowSide = side;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("负 = 站你左手边，正 = 右手边。\n三个槽位默认错开，避免叠在同一格。");

        var behind = Math.Clamp(s.FollowBehind, 0.5f, 6f);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"跟随距离##wbehind{_slot}", ref behind, 0.5f, 6f, "%.1f 米"))
            s.FollowBehind = behind;
    }

    // ── 台词 ─────────────────────────────────────────────────────

    private void DrawLines(SubNpcSetting s)
    {
        SubLabel("触发开关");

        var chatter = s.AutoChatter;
        if (ImGui.Checkbox($"没事自己闲聊##wchatter{_slot}", ref chatter))
            s.AutoChatter = chatter;

        ImGui.SameLine();
        var inChat = s.ShowInChat;
        if (ImGui.Checkbox($"台词进聊天栏##winchat{_slot}", ref inChat))
            s.ShowInChat = inChat;

        var weather = s.WeatherLines;
        if (ImGui.Checkbox($"天气台词##ww{_slot}", ref weather)) s.WeatherLines = weather;
        ImGui.SameLine();
        var dayPhase = s.DayPhaseLines;
        if (ImGui.Checkbox($"时段台词##wdp{_slot}", ref dayPhase)) s.DayPhaseLines = dayPhase;
        ImGui.SameLine();
        var combat = s.CombatLines;
        if (ImGui.Checkbox($"战斗台词##wc{_slot}", ref combat)) s.CombatLines = combat;
        ImGui.SameLine();
        var touch = s.TouchLines;
        if (ImGui.Checkbox($"互动台词##wt{_slot}", ref touch)) s.TouchLines = touch;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("互动台词 = 你把她选成目标 / 一直看着她 / 贴得很近 / 你掉血时她的关心。");

        var job = s.JobLines;
        if (ImGui.Checkbox($"换职业台词##wjob{_slot}", ref job)) s.JobLines = job;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("你换成另一个职业时她说一句跟那个职业有关的话。\n只在真的切到了不同职业时说一次。");

        var wName = s.WeatherNameLines;
        if (ImGui.Checkbox($"天气台词用真实名词##wwn{_slot}", ref wName)) s.WeatherNameLines = wName;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("开启：直接把天气名词说进句子里（例：碧空 →「今天真是碧空如洗呢~！」）。");

        ImGui.SameLine();
        var wSpecial = s.SpecialWeatherLines;
        if (ImGui.Checkbox($"副本特殊天气也开口##wwsp{_slot}", ref wSpecial)) s.SpecialWeatherLines = wSpecial;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("妖雾 / 极光这类副本异象。默认关闭 —— 在没有雨的副本里喊「下雨了」很出戏。");

        var scene = s.SceneReactions;
        if (ImGui.Checkbox($"对战斗 / 生产有反应##wscene{_slot}", ref scene))
            s.SceneReactions = scene;

        var emoteReact = s.EmoteReactions;
        if (ImGui.Checkbox($"回应你对她做的表情动作##wemote{_slot}", ref emoteReact))
            s.EmoteReactions = emoteReact;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("⚠️ 必须**先选中她**，然后对她做表情动作，她才会回一句。\n没选中她时做动作 → 一句都不说。\n同一个动作只回一次，之间有 3 秒防连击。");

        SubLabel("节奏");

        var cooldown = s.LineCooldownSeconds;
        if (ImGui.SliderFloat($"两句台词最短间隔##wcd{_slot}", ref cooldown, 0f, 60f, "%.0f 秒"))
            s.LineCooldownSeconds = cooldown;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("对她所有台词生效的统一闸门。觉得她话太密就往大调。\n0 = 不冷却。");

        var minIdle = s.MinIdleSeconds;
        if (ImGui.SliderInt($"闲聊最短间隔##wmin{_slot}", ref minIdle, 3, 300, "%.0f 秒"))
            s.MinIdleSeconds = Math.Clamp(minIdle, 3, 3600);
        var maxIdle = s.MaxIdleSeconds;
        if (ImGui.SliderInt($"闲聊最长间隔##wmax{_slot}", ref maxIdle, 5, 600, "%.0f 秒"))
            s.MaxIdleSeconds = Math.Clamp(maxIdle, 5, 3600);
        if (maxIdle < minIdle)
            s.MaxIdleSeconds = minIdle + 1;

        var closeDist = s.CloseTouchDistance;
        if (ImGui.SliderFloat($"贴近触发距离##wclose{_slot}", ref closeDist, 0.05f, 6f, "%.2f 米"))
            s.CloseTouchDistance = closeDist;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("你站定、并靠到这个距离以内持续 3 秒，她才会说「被靠近」那类台词。\n也是贴身闲聊的触发距离。");

        var gChance = s.GestureChancePercent;
        if (ImGui.SliderFloat($"台词配动作的几率##wgch{_slot}", ref gChance, 0f, 100f, "%.0f%%"))
            s.GestureChancePercent = gChance;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("台词的含义决定「配哪个动作」，这个百分数决定「配不配」。\n0 = 说话时永远不比划；100 = 匹配得上就一定比划。");
    }

    // ── 待机姿势 ─────────────────────────────────────────────────

    private void DrawIdlePose(SubNpcSetting s)
    {
        var choices = _controller.GetIdleEmotes();

        var idleName = "游戏默认待机";
        if (s.IdleEmoteRowId != 0)
        {
            var match = choices.FirstOrDefault(c => c.EmoteRowId == s.IdleEmoteRowId);
            idleName = match?.Name
                       ?? (string.IsNullOrEmpty(s.IdleEmoteName) ? $"#{s.IdleEmoteRowId}" : s.IdleEmoteName);
        }

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo($"待机姿势##widle{_slot}", idleName))
        {
            if (ImGui.Selectable($"游戏默认待机##widleNone{_slot}", s.IdleEmoteRowId == 0))
            {
                s.IdleEmoteRowId = 0;
                s.IdleEmoteName  = string.Empty;
            }

            foreach (var c in choices)
            {
                if (!ImGui.Selectable($"{c.Name}##widle{_slot}_{c.EmoteRowId}", c.EmoteRowId == s.IdleEmoteRowId))
                    continue;
                s.IdleEmoteRowId = c.EmoteRowId;
                s.IdleEmoteName  = c.Name;
            }

            ImGui.EndCombo();
        }
        ImGui.TextDisabled("玩家 /pose 的 6 个站立姿势。走动自动中断、停下自动恢复。");

        var autoRandom = s.IdleAutoRandom;
        if (ImGui.Checkbox($"自动随机改变姿势##wrandom{_slot}", ref autoRandom))
            s.IdleAutoRandom = autoRandom;

        var relax = Math.Clamp(s.IdleRelaxSeconds, 0f, 120f);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"放松等待时间##wrelax{_slot}", ref relax, 0f, 120f, "%.0f 秒"))
            s.IdleRelaxSeconds = Math.Clamp(relax, 0f, 600f);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("停止移动后，站满这么久才进入待机姿势。\n0 = 停下立即进入。\n（自动随机开着时，这个只管\"首次进姿势\"。）");

        if (s.IdleAutoRandom)
        {
            var switchSec = Math.Clamp(s.IdleSwitchSeconds, 5f, 300f);
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.SliderFloat($"随机切换间隔##wswitch{_slot}", ref switchSec, 5f, 300f, "%.0f 秒"))
                s.IdleSwitchSeconds = Math.Clamp(switchSec, 5f, 600f);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("「自动随机改变姿势」开着时：进入姿势后，每隔多久自己换下一个。\n实际下限 5 秒。");
        }
    }

    // ── 语音与气泡 ───────────────────────────────────────────────

    private void DrawVoiceBalloon(SubNpcSetting s)
    {
        SubLabel("说话配音（角色语音）");

        var vInherit = s.VoiceInherit;
        if (ImGui.Checkbox($"跟随主 NPC 的语音设置##wvinh{_slot}", ref vInherit))
            s.VoiceInherit = vInherit;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("勾选 = 用主面板「说话配音」里配的嗓音与开关。\n取消勾选后，下面的设置只对这个副 NPC 生效。");

        if (!vInherit)
        {
            var vOn = s.VoiceEnabled;
            if (ImGui.Checkbox($"她说话时发声##wvon{_slot}", ref vOn))
                s.VoiceEnabled = vOn;

            var preset = s.VoicePresetId;
            if (ImGui.SliderInt($"嗓音##wvp{_slot}", ref preset, 0, 24, preset == 0 ? "沿用外观来源" : "%d"))
                s.VoicePresetId = Math.Clamp(preset, 0, 255);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("0 = 不改，沿用抄外观时从玩家继承来的嗓音。\n想要 24 以上的编号：先拖到 24 再用「自定义嗓音」填。");

            var presetText = Math.Clamp(s.VoicePresetId, 0, 255).ToString();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(70);
            if (ImGui.InputText($"##wvpnum{_slot}", ref presetText, 4))
            {
                if (int.TryParse(presetText, out var pp))
                    s.VoicePresetId = Math.Clamp(pp, 0, 255);
            }

            var line = s.VoiceLineId;
            if (ImGui.SliderInt($"语音行号##wvl{_slot}", ref line, 0, 40, line == 0 ? "不发声" : "%d"))
                s.VoiceLineId = Math.Clamp(line, 0, 9999);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("说话时具体发哪一声，0 = 不发声。\n行号和实际声音没有离线对照表，用「试听」按耳朵挑。");

            ImGui.SameLine();
            if (ImGui.SmallButton($"试听##wva{_slot}"))
            {
                var addr = _controller.SubNpcs?.NpcAddressOf(_slot) ?? nint.Zero;
                if (addr != nint.Zero)
                    NpcVoice.Audition((Character*)addr, Math.Max(0, s.VoiceLineId));
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("让她立刻发一声当前行号的声音。⚠️ 需要她已经出现在场上。");

            var lineText = s.VoiceLineId.ToString();
            ImGui.SetNextItemWidth(90);
            if (ImGui.InputText($"自定义行号##wvlc{_slot}", ref lineText, 6))
            {
                if (int.TryParse(lineText, out var parsed))
                    s.VoiceLineId = Math.Clamp(parsed, 0, 9999);
            }
        }

        SubLabel("头顶说话气泡");

        var bInherit = s.BalloonInherit;
        if (ImGui.Checkbox($"跟随主 NPC 的气泡设置##wbinh{_slot}", ref bInherit))
            s.BalloonInherit = bInherit;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("勾选 = 用主面板「头顶说话气泡」里的开关与时长。\n取消勾选后，下面的设置只对这个副 NPC 生效。");

        if (!bInherit)
        {
            var bOn = s.BalloonEnabled;
            if (ImGui.Checkbox($"她说话时头顶弹气泡##wbon{_slot}", ref bOn))
                s.BalloonEnabled = bOn;

            var bSec = s.BalloonSeconds;
            if (ImGui.SliderFloat($"停留时长##wbsec{_slot}", ref bSec, 0.5f, 30f, "%.1f"))
                s.BalloonSeconds = bSec;

            ImGui.SameLine();
            if (ImGui.SmallButton($"试弹一句##wbtest{_slot}"))
            {
                var addr2 = _controller.SubNpcs?.NpcAddressOf(_slot) ?? nint.Zero;
                if (addr2 != nint.Zero)
                    NpcBalloon.Show((Character*)addr2, "（试一句）今天的风，挺舒服的。", s.BalloonSeconds);
            }
        }
    }
}
