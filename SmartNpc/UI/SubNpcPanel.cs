using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using SmartNpc.Core;

namespace SmartNpc.UI;

/// <summary>
/// 单个副 NPC 的设置<b>子页</b>（v0.18.69.23 三轮，用户裁定）：
/// 主设置窗口顶部横栏（<c>ImGui.TabBar</c>）四个子页之一 —— 主 NPC + 副 NPC 1/2/3。
///
/// <para><b>与主 NPC 页 1:1 对齐</b>：控件种类、滑条量程、格式串全部照搬主页；
/// 头部视线一族除外（副 NPC 没有视线层实现，不是「设置」而是「没有的功能」）。</para>
///
/// <para><b>跟随主 NPC 的方式（用户裁定）</b>：不做每项一个小勾选 ——
/// 每个大分组标题下放一个<b>「跟随主 NPC 设置」按钮</b>，点了把整组的覆盖值
/// 清空（回到用主面板的值）；组内控件直接显示当前生效值（覆盖 ?? 主配置），
/// 一编辑就写进本槽（自动脱离跟随）。</para>
///
/// <para>改动即时生效（主循环 PushRuntimeSettings 每帧直推）；改动后 1.5 秒自动落盘。</para>
/// </summary>
public sealed unsafe class SubNpcPanel
{
    private readonly Configuration _config;
    private readonly NpcController _controller;
    private readonly int _slot;

    private static readonly string[] PersonaNames = ["安静", "元气", "傲娇", "弱气"];

    /// <summary>改动后延迟落盘的倒计时（&gt;0 = 有未保存的改动）。</summary>
    private float _saveTimer;

    public SubNpcPanel(int slot, Configuration config, NpcController controller)
    {
        _slot       = slot;
        _config     = config;
        _controller = controller;
    }

    private SubNpcSetting S
        => _config.SubNpcs[Math.Clamp(_slot, 0, _config.SubNpcs.Count - 1)];

    private Configuration M => _config;

    /// <summary>任何会改配置的控件都调一下：1.5 秒后自动落盘。</summary>
    private void Touch() => _saveTimer = 1.5f;

    public void Draw()
    {
        var s = S;

        // ── 顶部：状态 + 召唤/送走 + 复制主设置 ──
        var alive = _controller.SubNpcs?.IsAlive(_slot) ?? false;

        var enabled = s.Enabled;
        if (ImGui.Checkbox($"启用槽位##subwen{_slot}", ref enabled))
        { s.Enabled = enabled; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("启用后才能召唤这个槽位。\n✅ 启用的槽位会在插件启动、进入游戏世界后自动召唤出来\n（跟随主 NPC 页「打开插件自动召唤」总开关；手动送走的本次会话不会再自动来）。");

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
        { _controller.SubNpcs?.CopyFromMain(_slot); Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把主 NPC 的设置抄进这个槽位（名字和站位偏移保留自己的）。\n全部「跟随主 NPC 设置」按钮也会被按下（整槽回到跟随状态）。\n她在场就立刻生效。");

        ImGui.Spacing();

        if (Header("基本信息", open: true))   { ImGui.Indent(); DrawBasics(s);       ImGui.Unindent(); }
        if (Header("外观与召唤", open: true)) { ImGui.Indent(); DrawDesign(s);       ImGui.Unindent(); }
        if (Header("性格与台词", open: true)) { ImGui.Indent(); DrawLines(s);        ImGui.Unindent(); }
        if (Header("待机姿势", open: true))   { ImGui.Indent(); DrawIdlePose(s);     ImGui.Unindent(); }
        if (Header("跟随与瞬移", open: true)) { ImGui.Indent(); DrawFollow(s);       ImGui.Unindent(); }
        if (Header("绕障", open: true))       { ImGui.Indent(); DrawAvoid(s);        ImGui.Unindent(); }
        if (Header("站定节奏", open: true))   { ImGui.Indent(); DrawIdleRhythm(s);   ImGui.Unindent(); }
        if (Header("口型与动画", open: false)){ ImGui.Indent(); DrawAnim(s);         ImGui.Unindent(); }
        if (Header("语音与气泡", open: false)){ ImGui.Indent(); DrawVoiceBalloon(s); ImGui.Unindent(); }

        // 改动后延迟落盘（不打断操作，也不丢设置）。
        if (_saveTimer > 0f)
        {
            _saveTimer -= ImGui.GetIO().DeltaTime;
            if (_saveTimer <= 0f)
                _controller.SaveNow();
        }
    }

    // ── 通用小组件 ──────────────────────────────────────────────

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

    /// <summary>
    /// 大分组顶部的「跟随主 NPC 设置」按钮（v0.18.69.23 三轮，用户裁定）：
    /// 点一下把整组的覆盖清空（回到用主面板的值），不再做每项一个小勾选。
    /// </summary>
    private void FollowMainButton(string key, string tip, Action clearAll)
    {
        if (ImGui.Button($"跟随主 NPC 设置##fmb{key}", new Vector2(-1f, 0f)))
        { clearAll(); Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tip);
        ImGui.Spacing();
    }

    // ── 基本信息（名字不跟随主 —— 名字是这个 NPC 的身份）──────────

    private void DrawBasics(SubNpcSetting s)
    {
        var name = s.Name ?? string.Empty;
        if (ImGui.InputText($"名字##wname{_slot}", ref name, 24))
        { s.Name = name; Touch(); }
        ImGui.TextDisabled("留空 = 显示游戏分配的默认名。");

        var persona = s.Persona;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo($"性格##wp{_slot}", PersonaNames[Math.Clamp(persona, 0, PersonaNames.Length - 1)]))
        {
            for (var p = 0; p < PersonaNames.Length; ++p)
            {
                if (ImGui.Selectable($"{PersonaNames[p]}##wp{_slot}_{p}", persona == p))
                { s.Persona = p; Touch(); }
            }
            ImGui.EndCombo();
        }

        var hint = persona switch
        {
            0 => "安静：话少、跟得远、动作小。温柔克制，一直在这儿。",
            1 => "元气：话多、贴得近、爱招手拍手。喜欢明晃晃写在脸上。",
            2 => "傲娇：嘴硬心软，才不是 / 别误会。壳是硬的，芯子里全是关心。",
            _ => "弱气：小心翼翼，那个…… / 对不起 / 可以吗。怕添麻烦，但很依赖你。",
        };
        ImGui.TextDisabled($"{hint}（台词 {Core.NpcLines.CountFor((Core.NpcBrain.Persona)persona)} 条）");

        var title = s.PlayerTitle ?? string.Empty;
        if (ImGui.InputText($"对你的称谓##wtitle{_slot}", ref title, 24))
        { s.PlayerTitle = title; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("台词里的 {{称谓}} 会替换成这里填的词。\n留空 = 用默认的「主人」。");
    }

    // ── 外观与召唤（设计 + 「跟随主」按钮）───────────────────────

    private void DrawDesign(SubNpcSetting s)
    {
        FollowMainButton(
            $"design{_slot}",
            "点一下 = 和主 NPC 用同一个外观设计（之后仍可单独换）。",
            () =>
            {
                s.GlamourerDesignId = M.GlamourerDesignId;
                // 主配置只存 Id 不存名备份 —— 名字按 Id 从设计列表里回查，查不到留空（显示时兜底）。
                s.GlamourerDesignName = _controller.GetDesigns()
                    .FirstOrDefault(d => d.Id == M.GlamourerDesignId).Name ?? string.Empty;
            });

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
                Touch();
            }

            foreach (var (id, dn) in designs)
            {
                if (!ImGui.Selectable($"{dn}##wd{_slot}{id}", id == s.GlamourerDesignId))
                    continue;
                s.GlamourerDesignId   = id;
                s.GlamourerDesignName = dn;
                Touch();
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled(designs.Count == 0
            ? "没读到 Glamourer 设计，外观将照抄玩家。"
            : "选中即换上；召唤时自动套用。");
    }

    // ── 性格与台词（开关/节奏照主页文案与量程；组按钮 = 抄主配置）──

    private void DrawLines(SubNpcSetting s)
    {
        FollowMainButton(
            $"lines{_slot}",
            "点一下 = 台词的触发开关与节奏全部照抄主 NPC 面板的当前值（之后仍可单独调）。",
            () =>
            {
                s.AutoChatter          = M.AutoChatter;
                s.ShowInChat           = M.ShowInChat;
                s.WeatherLines         = M.WeatherLines;
                s.WeatherNameLines     = M.WeatherNameLines;
                s.SpecialWeatherLines  = M.SpecialWeatherLines;
                s.DayPhaseLines        = M.DayPhaseLines;
                s.CombatLines          = M.CombatLines;
                s.TouchLines           = M.TouchLines;
                s.JobLines             = M.JobLines;
                s.SceneReactions       = M.SceneReactions;
                s.EmoteReactions       = M.EmoteReactions;
                s.LineCooldownSeconds  = M.LineCooldownSeconds;
                s.CloseTouchDistance   = M.CloseTouchDistance;
                s.GestureChancePercent = M.GestureChancePercent;
            });

        var title = s.PlayerTitle ?? string.Empty;
        if (ImGui.InputText($"玩家称谓##wtitle{_slot}", ref title, 24))
        { s.PlayerTitle = title; Touch(); }

        var chatter = s.AutoChatter;
        if (ImGui.Checkbox($"没事自己闲聊##wchatter{_slot}", ref chatter))
        { s.AutoChatter = chatter; Touch(); }

        ImGui.SameLine();
        var inChat = s.ShowInChat;
        if (ImGui.Checkbox($"台词进聊天栏##winchat{_slot}", ref inChat))
        { s.ShowInChat = inChat; Touch(); }

        var weather = s.WeatherLines;
        if (ImGui.Checkbox($"天气台词##ww{_slot}", ref weather)) { s.WeatherLines = weather; Touch(); }
        ImGui.SameLine();
        var dayPhase = s.DayPhaseLines;
        if (ImGui.Checkbox($"时段台词##wdp{_slot}", ref dayPhase)) { s.DayPhaseLines = dayPhase; Touch(); }
        ImGui.SameLine();
        var combat = s.CombatLines;
        if (ImGui.Checkbox($"战斗台词##wc{_slot}", ref combat)) { s.CombatLines = combat; Touch(); }
        ImGui.SameLine();
        var touch = s.TouchLines;
        if (ImGui.Checkbox($"互动台词##wt{_slot}", ref touch)) { s.TouchLines = touch; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("互动台词 = 你把她选成目标 / 你一直看着她 / 你贴得很近 / 你掉血时她的关心。");

        ImGui.SameLine();
        var job = s.JobLines;
        if (ImGui.Checkbox($"换职业台词##wjob{_slot}", ref job)) { s.JobLines = job; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("你换成另一个职业时她说一句跟那个职业有关的话。\n只在「真的切到了不同的职业」时说一次。");

        var wName = s.WeatherNameLines;
        if (ImGui.Checkbox($"天气台词用真实名词##wwn{_slot}", ref wName)) { s.WeatherNameLines = wName; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("开启：直接把游戏里那个天气名词说进句子里。\n例：碧空 →「今天真是碧空如洗呢~！」。");

        ImGui.SameLine();
        var wSpecial = s.SpecialWeatherLines;
        if (ImGui.Checkbox($"副本特殊天气也开口##wwsp{_slot}", ref wSpecial)) { s.SpecialWeatherLines = wSpecial; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("妖雾 / 极光这类副本异象。默认关闭 —— 在没有雨的副本里喊「下雨了」很出戏。");

        var scene = s.SceneReactions;
        if (ImGui.Checkbox($"对战斗 / 生产有反应##wscene{_slot}", ref scene))
        { s.SceneReactions = scene; Touch(); }

        // 量程与主页一致（0~60 / 0.05~6 / 0~100）。
        var cooldown = s.LineCooldownSeconds;
        if (ImGui.SliderFloat($"两句台词最短间隔##wcd{_slot}", ref cooldown, 0f, 60f, "%.0f 秒"))
        { s.LineCooldownSeconds = cooldown; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("对所有台词生效的统一闸门（闲聊 / 天气 / 时段 / 战斗 / 互动）。\n0 = 不冷却。觉得她话太密就往大调。\n⚠️ 你主动点的（打招呼、右键菜单）不受它限制。");

        var closeDist = s.CloseTouchDistance;
        if (ImGui.SliderFloat($"贴近触发距离##wclose{_slot}", ref closeDist, 0.05f, 6f, "%.2f 米"))
        { s.CloseTouchDistance = closeDist; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("你站定、并靠到这个距离以内持续 3 秒，她才会说「被靠近」那类台词。\n也是贴身闲聊的触发距离。");

        var gChance = s.GestureChancePercent;
        if (ImGui.SliderFloat($"台词配动作的几率##wgch{_slot}", ref gChance, 0f, 100f, "%.0f%%"))
        { s.GestureChancePercent = gChance; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("台词的含义决定「配哪个动作」，这个百分数决定「配不配」。\n0 = 说话时永远不比划；100 = 匹配得上就一定比划。");

        var emoteReact = s.EmoteReactions;
        if (ImGui.Checkbox($"回应你对她做的表情动作##wemote{_slot}", ref emoteReact))
        { s.EmoteReactions = emoteReact; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("⚠️ 必须**先选中她**，然后对她做表情动作，她才会回一句。\n没选中她时做动作 → 一句都不说。\n同一个动作只回一次，之间有 3 秒防连击。");
    }

    // ── 待机姿势（与主页「待机姿势」小节同款：下拉 + 节奏 + 组按钮）──

    private void DrawIdlePose(SubNpcSetting s)
    {
        FollowMainButton(
            $"idle{_slot}",
            "点一下 = 待机姿势与姿势节奏照抄主 NPC 面板的当前值（之后仍可单独调）。",
            () =>
            {
                s.IdleEmoteRowId    = M.IdleEmoteRowId;
                s.IdleEmoteName     = M.IdleEmoteName;
                s.IdleAutoRandom    = M.IdleAutoRandom;
                s.IdleRelaxSeconds  = M.IdleRelaxSeconds;
                s.IdleSwitchSeconds = M.IdleSwitchSeconds;
            });

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
                Touch();
            }

            foreach (var c in choices)
            {
                if (!ImGui.Selectable($"{c.Name}##widle{_slot}_{c.EmoteRowId}", c.EmoteRowId == s.IdleEmoteRowId))
                    continue;
                s.IdleEmoteRowId = c.EmoteRowId;
                s.IdleEmoteName  = c.Name;
                Touch();
            }

            ImGui.EndCombo();
        }
        ImGui.TextDisabled("玩家 /pose 的站立姿势。走动自动中断、停下自动恢复。");

        // 量程与主页一致（0~120 / 5~300）。
        var autoRandom = s.IdleAutoRandom;
        if (ImGui.Checkbox($"自动随机改变姿势##wrandom{_slot}", ref autoRandom))
        { s.IdleAutoRandom = autoRandom; Touch(); }

        var relax = Math.Clamp(s.IdleRelaxSeconds, 0f, 120f);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"放松等待时间##wrelax{_slot}", ref relax, 0f, 120f, "%.0f 秒"))
        { s.IdleRelaxSeconds = Math.Clamp(relax, 0f, 600f); Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("停止移动后，站满这么久才进入待机姿势。\n0 = 停下立即进入。\n（自动随机开着时，这个只管\"首次进姿势\"。）");

        if (s.IdleAutoRandom)
        {
            var switchSec = Math.Clamp(s.IdleSwitchSeconds, 5f, 300f);
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.SliderFloat($"随机切换间隔##wswitch{_slot}", ref switchSec, 5f, 300f, "%.0f 秒"))
            { s.IdleSwitchSeconds = Math.Clamp(switchSec, 5f, 600f); Touch(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("「自动随机改变姿势」开着时：进入姿势后，每隔多久自己换下一个。\n实际下限 5 秒。");
        }
    }

    // ── 跟随与瞬移（量程 1:1 照主页「跟随与瞬移」组）──────────────

    private void DrawFollow(SubNpcSetting s)
    {
        FollowMainButton(
            $"follow{_slot}",
            "点一下 = 速度 / 跟随强度 / 瞬移三件套全部回到主 NPC 面板的值（之后仍可单独调）。",
            () =>
            {
                s.SpeedScale           = null;
                s.FollowStrength       = null;
                s.FollowAgainDistance  = null;
                s.WalkDistance         = null;
                s.TeleportDistance     = null;
                s.TeleportHeight       = null;
                s.StuckTeleportSeconds = null;
            });

        SubLabel("跟随");

        // 站位是这个槽位自己的（三槽错开用），没有跟随语义。
        var side = Math.Clamp(s.FollowSide, -6f, 6f);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"侧向站位##wside{_slot}", ref side, -6f, 6f, "%.2f 米"))
        { s.FollowSide = side; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("负 = 站你左手边，正 = 右手边。\n三个槽位默认错开，避免叠在同一格。");

        // 站位是这个槽位自己的（三槽错开用），没有跟随语义。量程与主页一致（-6~6，负值=站前面）。
        var behind = Math.Clamp(s.FollowBehind, -6f, 6f);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"身后距离##wbehind{_slot}", ref behind, -6f, 6f, "%.2f 米"))
        { s.FollowBehind = behind; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("负值 = 站到你**前面**。三槽默认错开，避免叠在同一格。");

        // 量程与主页一致：重新跟随 1~15、跟随强度 0.5~2.5、速度倍率 0.6~1.5、迟滞带 0.5~30。
        var followAgain = s.FollowAgainDistance ?? M.FollowAgainDistance;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"重新跟随距离##wfag{_slot}", ref followAgain, 1f, 15f, "%.1f 米"))
        { s.FollowAgainDistance = followAgain; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("她到位站定后，你离开她超过这个距离就重新跟上来。\n调小 = 你刚起步她就跟；调大 = 允许你先走一段。");

        var followStr = s.FollowStrength ?? M.FollowStrength;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"跟随强度（引力）##wfs{_slot}", ref followStr, 0.5f, 2.5f, "%.2f"))
        { s.FollowStrength = followStr; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("她追你时有多\"黏\"：\n>1 = 落后时跑得更快、迟滞带外也提速，贴得更紧；\n1 = 与你同速追；\n<1 = 更松弛。\n建议 1.0~1.5。");

        var scale = s.SpeedScale ?? M.SpeedScale;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"速度倍率##wscale{_slot}", ref scale, 0.6f, 1.5f, "%.2f×"))
        { s.SpeedScale = scale; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("1.00 = 与你完全同速；调高一点，她追上来时更利索。");

        var lag = s.WalkDistance ?? M.WalkDistance;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"跑走迟滞带##wlag{_slot}", ref lag, 0.5f, 30f, "%.1f 米"))
        { s.WalkDistance = lag; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("超过这个距离她跑、回到距离以内降回走。\n没有迟滞带她会在边界反复横跳。");

        SubLabel("瞬移兜底");

        // 量程与主页一致：瞬移 3~60、高差 4~40、卡住 0~15。
        var teleport = s.TeleportDistance ?? M.TeleportDistance;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"跟丢瞬移距离##wtele{_slot}", ref teleport, 3f, 60f, "%.0f 米"))
        { s.TeleportDistance = teleport; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("与她的水平距离超过这个值就瞬移。\n要大于上面的「跟丢」距离，否则她会不停瞬移。");

        var tpHeight = s.TeleportHeight ?? M.TeleportHeight;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"高差瞬移阈值##wtph{_slot}", ref tpHeight, 4f, 40f, "%.0f 米"))
        { s.TeleportHeight = tpHeight; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("与她的高低差超过这个值才瞬移。\n调大 = 更少瞬移、更多自己爬楼梯。");

        var stuckTp = s.StuckTeleportSeconds ?? M.StuckTeleportSeconds;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"卡住多久瞬移到你身边##wstk{_slot}", ref stuckTp, 0f, 15f, "%.1f 秒"))
        { s.StuckTeleportSeconds = stuckTp; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("被墙 / 门 / 障碍挡住、原地挪不动超过这么久，就直接瞬移到你的脚下。\n0 = 关闭兜底（可能卡在原地出不来）。\n建议 2~5 秒。");
    }

    // ── 绕障（量程 1:1 照主页「绕障」组）─────────────────────────

    private void DrawAvoid(SubNpcSetting s)
    {
        FollowMainButton(
            $"avoid{_slot}",
            "点一下 = 避障开关与绕行节奏全部回到主 NPC 面板的值（之后仍可单独调）。",
            () =>
            {
                s.AvoidanceEnabled = null;
                s.DetourDuration   = null;
                s.DetourCooldown   = null;
            });

        // 量程与主页一致：绕行 0.3~3、间隔 0~3。
        var avoid = s.AvoidanceEnabled ?? M.AvoidanceEnabled;
        if (ImGui.Checkbox($"墙体避障##wavd{_slot}", ref avoid))
        { s.AvoidanceEnabled = avoid; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("开启：被墙 / 栏杆挡住会自动挑一个通畅方向绕过去（承诺式绕行）。\n实在绕不过去才跳跃 / 瞬移兜底。\n关闭：直线走，撞上就站住。");

        var detourDur = s.DetourDuration ?? M.DetourDuration;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"绕行时长##wdet{_slot}", ref detourDur, 0.3f, 3f, "%.1f 秒"))
        { s.DetourDuration = detourDur; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("被挡住后沿绕行方向走多久再重新看路。\n太短容易在墙前来回拐，太长会绕远。建议 0.8~1.5 秒。");

        var detourCd = s.DetourCooldown ?? M.DetourCooldown;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"绕行间隔##wdc{_slot}", ref detourCd, 0f, 3f, "%.1f 秒"))
        { s.DetourCooldown = detourCd; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("一次绕行中断（拐进死角）后，隔多久才允许再挑新方向。\n调 0 = 立刻可以再挑（贴身绕墙角更顺，但可能轻微抖）。");
    }

    // ── 站定节奏（量程 1:1 照主页「站定节奏与视线」组的游荡/小动作）──

    private void DrawIdleRhythm(SubNpcSetting s)
    {
        FollowMainButton(
            $"idleR{_slot}",
            "点一下 = 游荡范围 / 小动作间隔 / 移动闸门全部回到主 NPC 面板的值（之后仍可单独调）。",
            () =>
            {
                s.RoamRange                = null;
                s.IdleGestureMin           = null;
                s.IdleGestureMax           = null;
                s.BlockGesturesWhileMoving = null;
            });

        SubLabel("游荡与小动作");

        // 量程与主页一致：游荡 1~20、小动作 1~60 / 2~180。
        var roamMax = s.RoamRange ?? M.RoamRange;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"游荡范围##wrr{_slot}", ref roamMax, 1f, 20f, "%.1f 米"))
        { s.RoamRange = roamMax; Touch(); }

        var gMin = s.IdleGestureMin ?? M.IdleGestureMin;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"小动作最短间隔##wgmn{_slot}", ref gMin, 1f, 60f, "%.0f 秒"))
        { s.IdleGestureMin = MathF.Max(1f, gMin); Touch(); }

        var gMax = s.IdleGestureMax ?? M.IdleGestureMax;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"小动作最长间隔##wgmx{_slot}", ref gMax, 2f, 180f, "%.0f 秒"))
        { s.IdleGestureMax = MathF.Max((s.IdleGestureMin ?? M.IdleGestureMin) + 1f, gMax); Touch(); }

        ImGui.TextDisabled($"她会每隔 {s.IdleGestureMin ?? M.IdleGestureMin:0}-{s.IdleGestureMax ?? M.IdleGestureMax:0} 秒做个小动作。");

        var blockMoving = s.BlockGesturesWhileMoving ?? M.BlockGesturesWhileMoving;
        if (ImGui.Checkbox($"移动时不做动作##wbm{_slot}", ref blockMoving))
        { s.BlockGesturesWhileMoving = blockMoving; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("开着更干净；关掉则允许她边走边做动作（更容易平移）。");
    }

    // ── 口型与动画（量程 1:1 照主页台词组的口型 + 高级组的转身速度）──

    private void DrawAnim(SubNpcSetting s)
    {
        FollowMainButton(
            $"anim{_slot}",
            "点一下 = 口型开关 / 口型节奏 / 转身动作速度全部回到主 NPC 面板的值（之后仍可单独调）。",
            () =>
            {
                s.LipSyncEnabled    = null;
                s.LipFlapSeconds    = null;
                s.TurnPlaybackSpeed = null;
            });

        var lipSync = s.LipSyncEnabled ?? M.LipSyncEnabled;
        if (ImGui.Checkbox($"说话时嘴巴开合##wlip{_slot}", ref lipSync))
        { s.LipSyncEnabled = lipSync; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("走的是底层口型层（Timeline.LipsOverride），**不是**表情动作 ——\n所以她可以一边说话一边比划，两者互不干扰。");

        if (lipSync)
        {
            // 量程与主页一致：0.05~0.6。
            var flap = s.LipFlapSeconds ?? M.LipFlapSeconds;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.SliderFloat($"嘴巴开合一次的时间##wlf{_slot}", ref flap, 0.05f, 0.6f, "%.2f 秒"))
            { s.LipFlapSeconds = flap; Touch(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("调小 = 嘴动得快（像急急地说）；调大 = 慢慢地说。");
        }

        // 量程与主页「高级与调试」组一致：0.25~3。
        var turnSpeed = s.TurnPlaybackSpeed ?? M.TurnPlaybackSpeed;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"转身动作速度##wtps{_slot}", ref turnSpeed, 0.25f, 3f, "%.2f×"))
        { s.TurnPlaybackSpeed = turnSpeed; Touch(); }
    }

    // ── 语音与气泡（组按钮 = 回到跟随主 NPC；编辑单项自动脱离跟随）──

    private void DrawVoiceBalloon(SubNpcSetting s)
    {
        FollowMainButton(
            $"vb{_slot}",
            "点一下 = 语音与气泡全部跟随主 NPC 页的设置（之后仍可单独调）。",
            () =>
            {
                s.VoiceInherit   = true;
                s.BalloonInherit = true;
            });

        SubLabel("说话配音（角色语音）");

        var vInherit = s.VoiceInherit;
        if (vInherit)
        {
            ImGui.TextDisabled("（当前跟随主 NPC 页的语音设置。调下面任何一项就会单独生效。）");
            ImGui.BeginDisabled();
        }

        var vOn = s.VoiceEnabled;
        if (ImGui.Checkbox($"她说话时发声##wvon{_slot}", ref vOn))
        { s.VoiceEnabled = vOn; s.VoiceInherit = false; Touch(); }

        var preset = s.VoicePresetId;
        if (ImGui.SliderInt($"嗓音##wvp{_slot}", ref preset, 0, 24, preset == 0 ? "沿用外观来源" : "%d"))
        { s.VoicePresetId = Math.Clamp(preset, 0, 255); s.VoiceInherit = false; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("0 = 不改，沿用抄外观时从玩家继承来的嗓音。");

        var line = s.VoiceLineId;
        if (ImGui.SliderInt($"语音行号##wvl{_slot}", ref line, 0, 40, line == 0 ? "不发声" : "%d"))
        { s.VoiceLineId = Math.Clamp(line, 0, 9999); s.VoiceInherit = false; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("说话时具体发哪一声，0 = 不发声。\n行号和实际声音没有离线对照表，用「试听」按耳朵挑。");

        ImGui.SameLine();
        if (ImGui.SmallButton($"试听##wva{_slot}"))
        {
            var addr = _controller.SubNpcs?.NpcAddressOf(_slot) ?? nint.Zero;
            if (addr != nint.Zero)
            {
                s.VoiceInherit = false;   // 试听的就是本槽自己的行号
                NpcVoice.Audition((Character*)addr, Math.Max(0, s.VoiceLineId));
                Touch();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("让她立刻发一声当前行号的声音。⚠️ 需要她已经出现在场上。");

        if (vInherit)
            ImGui.EndDisabled();

        SubLabel("头顶说话气泡");

        var bInherit = s.BalloonInherit;
        if (bInherit)
        {
            ImGui.TextDisabled("（当前跟随主 NPC 页的气泡设置。调下面任何一项就会单独生效。）");
            ImGui.BeginDisabled();
        }

        var bOn = s.BalloonEnabled;
        if (ImGui.Checkbox($"她说话时头顶弹气泡##wbon{_slot}", ref bOn))
        { s.BalloonEnabled = bOn; s.BalloonInherit = false; Touch(); }

        // 量程与主页一致：0.5~30。
        var bSec = s.BalloonSeconds;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"停留时长##wbsec{_slot}", ref bSec, 0.5f, 30f, "%.1f"))
        { s.BalloonSeconds = bSec; s.BalloonInherit = false; Touch(); }

        ImGui.SameLine();
        if (ImGui.SmallButton($"试弹一句##wbtest{_slot}"))
        {
            var addr2 = _controller.SubNpcs?.NpcAddressOf(_slot) ?? nint.Zero;
            if (addr2 != nint.Zero)
                NpcBalloon.Show((Character*)addr2, "（试一句）今天的风，挺舒服的。", s.BalloonSeconds);
        }

        if (bInherit)
            ImGui.EndDisabled();
    }
}
