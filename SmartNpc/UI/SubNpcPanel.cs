using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using SmartNpc.Core;

namespace SmartNpc.UI;

/// <summary>
/// 单个副 NPC 的设置<b>子页</b>（v0.18.69.23 二轮，用户裁定）：
/// 不再是独立浮动窗口 —— 主设置窗口顶部有一条横栏
///（<c>ImGui.TabBar</c>），放四个子页：主 NPC + 副 NPC 1/2/3，
/// 每个 NPC 的全部设置都在自己的子页里，点横栏切换。
///
/// <para>内容与主 NPC 页<b>全量对齐</b>：外观 / 性格与台词 / 待机姿势 /
/// 跟随与瞬移 / 绕障 / 站定节奏 / 口型与动画 / 语音与气泡。
/// 跟随/瞬移/绕障/游荡/口型等运动参数是「跟随主 NPC 设置」+ 独立覆盖
///（可空字段，勾着跟随就用主面板的值，取消勾选后本槽独立调）。</para>
///
/// <para>改动即时生效（每帧 Sanitize+ApplySettings）；改动后 1.5 秒自动落盘
///（主面板没有周期保存的机制，这里不能跟着学 —— 副 NPC 的设置大多只在
/// 本子页改，丢了很难受）。</para>
/// </summary>
public sealed unsafe class SubNpcPanel
{
    private readonly Configuration _config;
    private readonly NpcController _controller;
    private readonly int _slot;

    private static readonly string[] PersonaNames = ["安静", "元气", "傲娇", "弱气"];

    private List<NpcAnimator.IdleEmoteChoice>? _idleChoices;

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
        { _controller.SubNpcs?.CopyFromMain(_slot); Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把主 NPC 的全部设置抄进这个槽位（站位偏移保留自己的）。\n所有「跟随主」开关回到跟随状态，语音/气泡回到跟随模式。\n她在场就立刻生效。");

        ImGui.Spacing();

        if (Header("基本信息", open: true))   { ImGui.Indent(); DrawBasics(s);        ImGui.Unindent(); }
        if (Header("外观", open: true))       { ImGui.Indent(); DrawDesign(s);        ImGui.Unindent(); }
        if (Header("性格与台词", open: true)) { ImGui.Indent(); DrawLines(s);         ImGui.Unindent(); }
        if (Header("待机姿势", open: true))   { ImGui.Indent(); DrawIdlePose(s);      ImGui.Unindent(); }
        if (Header("跟随与瞬移", open: true)) { ImGui.Indent(); DrawFollow(s);        ImGui.Unindent(); }
        if (Header("绕障", open: true))       { ImGui.Indent(); DrawAvoid(s);         ImGui.Unindent(); }
        if (Header("站定节奏", open: true))   { ImGui.Indent(); DrawIdleRhythm(s);    ImGui.Unindent(); }
        if (Header("口型与动画", open: false)){ ImGui.Indent(); DrawAnim(s);          ImGui.Unindent(); }
        if (Header("语音与气泡", open: false)){ ImGui.Indent(); DrawVoiceBalloon(s);  ImGui.Unindent(); }

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
    /// 「跟随主 NPC 设置」开关 + 值控件：勾着 = 用主面板的值（控件禁用）；
    /// 取消勾选后这个副 NPC 独立调。可空覆盖字段的统一 UI。
    /// </summary>
    private bool OvToggle(string key, bool inherit, Action<bool> set)
    {
        var inh = inherit;
        if (ImGui.Checkbox($"跟随主 NPC 设置##ov{key}", ref inh))
        { set(inh); Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("勾选 = 用主 NPC 面板里同一个设置的值。\n取消勾选后，这里调的只对这个副 NPC 生效。");
        return !inh;
    }

    private void OvSlider(string key, string label, float? ov, float mainVal,
                          float min, float max, string fmt, Action<float> set)
    {
        var v = ov ?? mainVal;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"{label}##ov{key}", ref v, min, max, fmt))
        { set(v); Touch(); }
    }

    private void OvCheck(string key, string label, bool? ov, bool mainVal, Action<bool> set)
    {
        var v = ov ?? mainVal;
        if (ImGui.Checkbox($"{label}##ov{key}", ref v))
        { set(v); Touch(); }
    }

    // ── 基本信息 ────────────────────────────────────────────────

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
            0 => "安静：话少、跟得远、动作小。",
            1 => "元气：话多、贴得近、爱招手拍手。",
            2 => "傲娇：嘴硬心软，落点全是关心。",
            _ => "弱气：小心翼翼，但很依赖你。",
        };
        ImGui.TextDisabled(hint);

        var title = s.PlayerTitle ?? string.Empty;
        if (ImGui.InputText($"对你的称谓##wtitle{_slot}", ref title, 16))
        { s.PlayerTitle = title; Touch(); }
        ImGui.TextDisabled("台词里的 {{称谓}} 会替换成这个。留空 = 「主人」。");
    }

    // ── 外观 ────────────────────────────────────────────────────

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

    // ── 性格与台词 ──────────────────────────────────────────────

    private void DrawLines(SubNpcSetting s)
    {
        SubLabel("触发开关");

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
            ImGui.SetTooltip("互动台词 = 你把她选成目标 / 一直看着她 / 贴得很近 / 你掉血时她的关心。");

        var job = s.JobLines;
        if (ImGui.Checkbox($"换职业台词##wjob{_slot}", ref job)) { s.JobLines = job; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("你换成另一个职业时她说一句跟那个职业有关的话。\n只在真的切到了不同职业时说一次。");

        var wName = s.WeatherNameLines;
        if (ImGui.Checkbox($"天气台词用真实名词##wwn{_slot}", ref wName)) { s.WeatherNameLines = wName; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("开启：直接把天气名词说进句子里（例：碧空 →「今天真是碧空如洗呢~！」）。");

        ImGui.SameLine();
        var wSpecial = s.SpecialWeatherLines;
        if (ImGui.Checkbox($"副本特殊天气也开口##wwsp{_slot}", ref wSpecial)) { s.SpecialWeatherLines = wSpecial; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("妖雾 / 极光这类副本异象。默认关闭 —— 在没有雨的副本里喊「下雨了」很出戏。");

        var scene = s.SceneReactions;
        if (ImGui.Checkbox($"对战斗 / 生产有反应##wscene{_slot}", ref scene))
        { s.SceneReactions = scene; Touch(); }

        var emoteReact = s.EmoteReactions;
        if (ImGui.Checkbox($"回应你对她做的表情动作##wemote{_slot}", ref emoteReact))
        { s.EmoteReactions = emoteReact; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("⚠️ 必须**先选中她**，然后对她做表情动作，她才会回一句。\n没选中她时做动作 → 一句都不说。\n同一个动作只回一次，之间有 3 秒防连击。");

        SubLabel("节奏");

        var cooldown = s.LineCooldownSeconds;
        if (ImGui.SliderFloat($"两句台词最短间隔##wcd{_slot}", ref cooldown, 0f, 60f, "%.0f 秒"))
        { s.LineCooldownSeconds = cooldown; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("对她所有台词生效的统一闸门。觉得她话太密就往大调。\n0 = 不冷却。");

        var minIdle = s.MinIdleSeconds;
        if (ImGui.SliderInt($"闲聊最短间隔##wmin{_slot}", ref minIdle, 3, 300, "%.0f 秒"))
        { s.MinIdleSeconds = Math.Clamp(minIdle, 3, 3600); Touch(); }
        var maxIdle = s.MaxIdleSeconds;
        if (ImGui.SliderInt($"闲聊最长间隔##wmax{_slot}", ref maxIdle, 5, 600, "%.0f 秒"))
        { s.MaxIdleSeconds = Math.Clamp(maxIdle, 5, 3600); Touch(); }
        if (maxIdle < minIdle)
            s.MaxIdleSeconds = minIdle + 1;

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
    }

    // ── 待机姿势 ────────────────────────────────────────────────

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
        ImGui.TextDisabled("玩家 /pose 的 6 个站立姿势。走动自动中断、停下自动恢复。");

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

    // ── 跟随与瞬移 ──────────────────────────────────────────────

    private void DrawFollow(SubNpcSetting s)
    {
        var cfg = _config;

        SubLabel("站位（这个槽位自己的，没有跟随开关）");

        var side = Math.Clamp(s.FollowSide, -6f, 6f);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"侧向站位##wside{_slot}", ref side, -6f, 6f, "%.1f 米"))
        { s.FollowSide = side; Touch(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("负 = 站你左手边，正 = 右手边。\n三个槽位默认错开，避免叠在同一格。");

        var behind = Math.Clamp(s.FollowBehind, 0.5f, 6f);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderFloat($"跟随距离##wbehind{_slot}", ref behind, 0.5f, 6f, "%.1f 米"))
        { s.FollowBehind = behind; Touch(); }

        SubLabel("跟随与瞬移（可独立于主 NPC）");

        var key = $"{_slot}spd";
        if (OvToggle(key, s.SpeedScale is null, inh => s.SpeedScale = inh ? null : cfg.SpeedScale))
            OvSlider(key, "速度倍率", s.SpeedScale, cfg.SpeedScale, 0.25f, 3f, "%.2f×", v => s.SpeedScale = v);

        key = $"{_slot}fs";
        if (OvToggle(key, s.FollowStrength is null, inh => s.FollowStrength = inh ? null : cfg.FollowStrength))
            OvSlider(key, "跟随强度", s.FollowStrength, cfg.FollowStrength, 0.5f, 2.5f, "%.2f", v => s.FollowStrength = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("1 = 与玩家同速追；>1 = 落后时追得更凶；<1 = 更松弛。");

        key = $"{_slot}arr";
        if (OvToggle(key, s.ArriveDistance is null, inh => s.ArriveDistance = inh ? null : cfg.ArriveDistance))
            OvSlider(key, "到位距离", s.ArriveDistance, cfg.ArriveDistance, 0.5f, 8f, "%.1f 米", v => s.ArriveDistance = v);

        key = $"{_slot}fag";
        if (OvToggle(key, s.FollowAgainDistance is null, inh => s.FollowAgainDistance = inh ? null : cfg.FollowAgainDistance))
            OvSlider(key, "重新跟随距离", s.FollowAgainDistance, cfg.FollowAgainDistance, 1f, 30f, "%.1f 米", v => s.FollowAgainDistance = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("她到位待机后，你离开她超过这个距离就重新跟上。\n调小 = 你刚起步她就跟。");

        key = $"{_slot}lag";
        if (OvToggle(key, s.WalkDistance is null, inh => s.WalkDistance = inh ? null : cfg.WalkDistance))
            OvSlider(key, "跑走迟滞带", s.WalkDistance, cfg.WalkDistance, 0.5f, 30f, "%.1f 米", v => s.WalkDistance = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("超过这个距离她跑、回到距离以内降回走。\n没有迟滞带她会在边界反复横跳。");

        key = $"{_slot}tpd";
        if (OvToggle(key, s.TeleportDistance is null, inh => s.TeleportDistance = inh ? null : cfg.TeleportDistance))
            OvSlider(key, "瞬移距离", s.TeleportDistance, cfg.TeleportDistance, 2f, 60f, "%.0f 米", v => s.TeleportDistance = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("落后超过这个距离就直接瞬移到你身边。");

        key = $"{_slot}tph";
        if (OvToggle(key, s.TeleportHeight is null, inh => s.TeleportHeight = inh ? null : cfg.TeleportHeight))
            OvSlider(key, "高差瞬移阈值", s.TeleportHeight, cfg.TeleportHeight, 4f, 40f, "%.0f 米", v => s.TeleportHeight = v);

        key = $"{_slot}stk";
        if (OvToggle(key, s.StuckTeleportSeconds is null, inh => s.StuckTeleportSeconds = inh ? null : cfg.StuckTeleportSeconds))
            OvSlider(key, "卡住瞬移", s.StuckTeleportSeconds, cfg.StuckTeleportSeconds, 0f, 30f, "%.1f 秒", v => s.StuckTeleportSeconds = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("被挡住超过这个秒数就无条件瞬移到你身边。\n0 = 关闭兜底。");
    }

    // ── 绕障 ────────────────────────────────────────────────────

    private void DrawAvoid(SubNpcSetting s)
    {
        var cfg = _config;
        var key = $"{_slot}avd";
        if (OvToggle(key, s.AvoidanceEnabled is null, inh => s.AvoidanceEnabled = inh ? null : cfg.AvoidanceEnabled))
            OvCheck(key, "被挡住时自动绕行", s.AvoidanceEnabled, cfg.AvoidanceEnabled, v => s.AvoidanceEnabled = v);

        key = $"{_slot}det";
        if (OvToggle(key, s.DetourDuration is null, inh => s.DetourDuration = inh ? null : cfg.DetourDuration))
            OvSlider(key, "绕行持续", s.DetourDuration, cfg.DetourDuration, 0.1f, 5f, "%.1f 秒", v => s.DetourDuration = v);

        key = $"{_slot}dc";
        if (OvToggle(key, s.DetourCooldown is null, inh => s.DetourCooldown = inh ? null : cfg.DetourCooldown))
            OvSlider(key, "绕行冷却", s.DetourCooldown, cfg.DetourCooldown, 0f, 10f, "%.1f 秒", v => s.DetourCooldown = v);
    }

    // ── 站定节奏 ────────────────────────────────────────────────

    private void DrawIdleRhythm(SubNpcSetting s)
    {
        var cfg = _config;

        SubLabel("游荡");

        var key = $"{_slot}rr";
        if (OvToggle(key, s.RoamRange is null, inh => s.RoamRange = inh ? null : cfg.RoamRange))
            OvSlider(key, "游荡范围", s.RoamRange, cfg.RoamRange, 1f, 30f, "%.1f 米", v => s.RoamRange = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("站定时她自己散步的最远半径。0 太小会站着不动。");

        key = $"{_slot}rm";
        if (OvToggle(key, s.RoamMin is null, inh => s.RoamMin = inh ? null : cfg.RoamMin))
            OvSlider(key, "游荡最近距离", s.RoamMin, cfg.RoamMin, 0.5f, 20f, "%.1f 米", v => s.RoamMin = v);

        SubLabel("小动作");

        key = $"{_slot}bgm";
        if (OvToggle(key, s.BlockGesturesWhileMoving is null, inh => s.BlockGesturesWhileMoving = inh ? null : cfg.BlockGesturesWhileMoving))
            OvCheck(key, "移动时不插小动作", s.BlockGesturesWhileMoving, cfg.BlockGesturesWhileMoving, v => s.BlockGesturesWhileMoving = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("开着更干净；关掉则允许她边走边做动作（更容易平移）。");

        key = $"{_slot}igm";
        if (OvToggle(key, s.IdleGestureMin is null, inh => s.IdleGestureMin = inh ? null : cfg.IdleGestureMin))
            OvSlider(key, "小动作最短间隔", s.IdleGestureMin, cfg.IdleGestureMin, 1f, 120f, "%.0f 秒", v => s.IdleGestureMin = v);

        key = $"{_slot}igx";
        if (OvToggle(key, s.IdleGestureMax is null, inh => s.IdleGestureMax = inh ? null : cfg.IdleGestureMax))
            OvSlider(key, "小动作最长间隔", s.IdleGestureMax, cfg.IdleGestureMax, 1f, 300f, "%.0f 秒", v => s.IdleGestureMax = v);
    }

    // ── 口型与动画 ──────────────────────────────────────────────

    private void DrawAnim(SubNpcSetting s)
    {
        var cfg = _config;

        var key = $"{_slot}lip";
        if (OvToggle(key, s.LipSyncEnabled is null, inh => s.LipSyncEnabled = inh ? null : cfg.LipSyncEnabled))
            OvCheck(key, "说话时嘴巴开合", s.LipSyncEnabled, cfg.LipSyncEnabled, v => s.LipSyncEnabled = v);

        key = $"{_slot}lf";
        if (OvToggle(key, s.LipFlapSeconds is null, inh => s.LipFlapSeconds = inh ? null : cfg.LipFlapSeconds))
            OvSlider(key, "口型节奏", s.LipFlapSeconds, cfg.LipFlapSeconds, 0.05f, 0.6f, "%.2f 秒", v => s.LipFlapSeconds = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("嘴巴开合一次的时间。调小 = 嘴动得快（像急急地说）。");

        key = $"{_slot}wss";
        if (OvToggle(key, s.WalkSpeedScale is null, inh => s.WalkSpeedScale = inh ? null : cfg.WalkSpeedScale))
            OvSlider(key, "走路动画速度", s.WalkSpeedScale, cfg.WalkSpeedScale, 0.25f, 3f, "%.2f×", v => s.WalkSpeedScale = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("1 = 按实际移动速度自动匹配，一般不用动。");

        key = $"{_slot}tps";
        if (OvToggle(key, s.TurnPlaybackSpeed is null, inh => s.TurnPlaybackSpeed = inh ? null : cfg.TurnPlaybackSpeed))
            OvSlider(key, "转身动作速度", s.TurnPlaybackSpeed, cfg.TurnPlaybackSpeed, 0.25f, 3f, "%.2f×", v => s.TurnPlaybackSpeed = v);
    }

    // ── 语音与气泡 ──────────────────────────────────────────────

    private void DrawVoiceBalloon(SubNpcSetting s)
    {
        SubLabel("说话配音（角色语音）");

        var vInherit = s.VoiceInherit;
        if (ImGui.Checkbox($"跟随主 NPC 的语音设置##wvinh{_slot}", ref vInherit))
        { s.VoiceInherit = vInherit; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("勾选 = 用主 NPC 页「语音与气泡」里配的嗓音与开关。\n取消勾选后，下面的设置只对这个副 NPC 生效。");

        if (!vInherit)
        {
            var vOn = s.VoiceEnabled;
            if (ImGui.Checkbox($"她说话时发声##wvon{_slot}", ref vOn))
            { s.VoiceEnabled = vOn; Touch(); }

            var preset = s.VoicePresetId;
            if (ImGui.SliderInt($"嗓音##wvp{_slot}", ref preset, 0, 24, preset == 0 ? "沿用外观来源" : "%d"))
            { s.VoicePresetId = Math.Clamp(preset, 0, 255); Touch(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("0 = 不改，沿用抄外观时从玩家继承来的嗓音。");

            var line = s.VoiceLineId;
            if (ImGui.SliderInt($"语音行号##wvl{_slot}", ref line, 0, 40, line == 0 ? "不发声" : "%d"))
            { s.VoiceLineId = Math.Clamp(line, 0, 9999); Touch(); }
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
                { s.VoiceLineId = Math.Clamp(parsed, 0, 9999); Touch(); }
            }
        }

        SubLabel("头顶说话气泡");

        var bInherit = s.BalloonInherit;
        if (ImGui.Checkbox($"跟随主 NPC 的气泡设置##wbinh{_slot}", ref bInherit))
        { s.BalloonInherit = bInherit; Touch(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("勾选 = 用主 NPC 页「语音与气泡」里的开关与时长。\n取消勾选后，下面的设置只对这个副 NPC 生效。");

        if (!bInherit)
        {
            var bOn = s.BalloonEnabled;
            if (ImGui.Checkbox($"她说话时头顶弹气泡##wbon{_slot}", ref bOn))
            { s.BalloonEnabled = bOn; Touch(); }

            var bSec = s.BalloonSeconds;
            if (ImGui.SliderFloat($"停留时长##wbsec{_slot}", ref bSec, 0.5f, 30f, "%.1f"))
            { s.BalloonSeconds = bSec; Touch(); }

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
