using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using SmartNpc.Core;

namespace SmartNpc.UI;

/// <summary>
/// 控制面板 —— 这个插件的主要交互界面。
///
/// <para>结构（v0.18.16 重排）：窗口 → <b>6 个可折叠分组</b> → 分组内再按小节细分。
/// 分组按"用户想干什么"划界，同类参数收拢在一处：</para>
///
/// <list type="bullet">
/// <item><b>外观与召唤</b> —— 她长什么样、怎么出现（衣着 / 设计 / 出生点 / 换区）</item>
/// <item><b>性格与动作</b> —— 她怎么表现（人格 / 动作 / 动作时机）</item>
/// <item><b>跟随与瞬移</b> —— 她怎么跟你（站位 / 跟随 / 三个瞬移参数收拢在一处）</item>
/// <item><b>绕障</b> —— 她怎么绕路（避障 / 绕行）</item>
/// <item><b>站定节奏与视线</b> —— 她站着时干嘛（小动作 / 游荡 / 头部视线一族）</item>
/// <item><b>高级与调试</b> —— 默认折叠（行号手填 / 转身速度 / 诊断开关）</item>
/// </list>
///
/// <para>⚠️ 只动了<b>排版与归属</b>：所有配置键、语义、联动（如避障开关同步 Motion）
/// 与重构前完全一致。</para>
/// </summary>
public sealed unsafe class ConfigWindow : Window
{
    private readonly Configuration _config;
    private readonly NpcController _controller;

    // v0.18.50：毒舌 → 傲娇、学者 → 弱气（两档整档重做）。
    private static readonly string[] PersonaNames = ["安静", "元气", "傲娇", "弱气"];

    /// <summary>动作列表<b>不能硬编码</b>：Emote.Name 是客户端本地化名（国服=中文），
    /// 写死 "wave"/"nod" 这类英文名永远匹配不上。真实名字从表里动态取，见 <see cref="_actionNames"/>。</summary>
    private string[] _actionNames = [];
    private string   _actionQuery = string.Empty;
    private string   _actionPick  = string.Empty;

    /// <summary>「立刻选中她」最近一次的返回（v0.18.39）。</summary>
    private string _selectResult = string.Empty;

    /// <summary>
    /// v0.18.69.23 二轮：三个副 NPC 的设置子页（主设置窗口横栏 TabBar 的后三个 Tab）。
    /// 用户裁定：不再用独立浮动窗口 —— 一个设置窗口、四个子页（主 NPC + 三副）。
    /// </summary>
    private readonly SubNpcPanel[] _subPanels;

    public ConfigWindow(Configuration config, NpcController controller)
        : base("智能 NPC###SmartNpcConfig")
    {
        _config     = config;
        _controller = controller;

        _subPanels = new SubNpcPanel[Configuration.SubNpcSlotCount];
        for (var i = 0; i < Configuration.SubNpcSlotCount; ++i)
            _subPanels[i] = new SubNpcPanel(i, config, controller);

        Size = new Vector2(430f, 640f);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400f, 420f),
            MaximumSize = new Vector2(700f, 900f),
        };
    }

    public override void Draw()
    {
        // ── 顶部横栏（v0.18.69.23 二轮，用户裁定）：一个设置窗口、四个子页 ──
        // 主 NPC + 副 NPC 1/2/3，每个 NPC 的全部设置都在自己的子页里，点横栏切换。
        if (!ImGui.BeginTabBar("SmartNpcNpcTabs"))
            return;

        if (ImGui.BeginTabItem("主 NPC"))
        {
            DrawHeader();

            if (Header("外观与召唤", open: true))     { ImGui.Indent(); DrawAppearanceBlock(); ImGui.Unindent(); }
            if (Header("性格与动作", open: true))     { ImGui.Indent(); DrawPersonaBlock();    ImGui.Unindent(); }
            if (Header("跟随与瞬移", open: true))     { ImGui.Indent(); DrawFollowBlock();     ImGui.Unindent(); }
            if (Header("绕障",       open: true))     { ImGui.Indent(); DrawAvoidanceBlock();  ImGui.Unindent(); }
            if (Header("站定节奏与视线", open: true)) { ImGui.Indent(); DrawIdleBlock();       ImGui.Unindent(); }
            if (Header("高级与调试", open: false))    { ImGui.Indent(); DrawAdvancedBlock();   ImGui.Unindent(); }

            ImGui.EndTabItem();
        }

        for (var i = 0; i < Configuration.SubNpcSlotCount && i < _config.SubNpcs.Count; ++i)
        {
            var s    = _config.SubNpcs[i];
            var alive = _controller.SubNpcs?.IsAlive(i) ?? false;
            var name  = Core.NpcNaming.EffectiveName(s.Name, $"副 {i + 1}");
            var label = $"副 {i + 1}·{name}{(alive ? " ●" : "")}##subtab{i}";

            if (ImGui.BeginTabItem(label))
            {
                _subPanels[i].Draw();
                ImGui.EndTabItem();
            }
        }

        ImGui.EndTabBar();

        DrawFooter();

        // 所有控件都直接改 _config，这里统一做一次校验 + 推给运行中的模块。
        // （折叠只影响绘制，不影响应用 —— 收起的参数依然生效。）
        ApplyLiveSettings();
    }

    /// <summary>分组标题（可折叠）。返回 false = 收起，跳过整组内容。</summary>
    private static bool Header(string title, bool open)
    {
        ImGui.Spacing();
        return ImGui.CollapsingHeader(title,
            open ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
    }

    /// <summary>分组内的小节标题（第二级）。</summary>
    private static void SubLabel(string title)
    {
        ImGui.Spacing();
        ImGui.TextDisabled($"── {title} ──");
        ImGui.Spacing();
    }

    // ── 顶部：召唤与状态 ──────────────────────────────────────────

    private void DrawHeader()
    {
        var present = _controller.HasNpc;

        var displayName = Core.NpcNaming.EffectiveName(_config.NpcName, "她");
        ImGui.TextWrapped(present
            ? $"{displayName}正跟着你。"
            : $"{displayName}还没出现，按下面那个按钮叫她过来。");

        ImGui.Spacing();

        var buttonWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;

        if (present)
        {
            if (ImGui.Button("让她离开", new Vector2(buttonWidth, 32f)))
                _controller.Dismiss();
        }
        else
        {
            if (ImGui.Button("叫她过来", new Vector2(buttonWidth, 32f)))
                _controller.Summon();
        }

        ImGui.SameLine();

        if (ImGui.Button("改她的名字", new Vector2(buttonWidth, 32f)))
            ImGui.OpenPopup("改名");

        if (ImGui.BeginPopup("改名"))
        {
            var name = _config.NpcName;
            if (ImGui.InputText("名字", ref name, 24))
                _config.NpcName = name;

            // v0.18.45：留空 = 目标信息栏/名牌显示游戏分配的默认名；改名实时生效并存档。
            ImGui.TextDisabled("改完点别处就生效；留空 = 显示游戏默认名");
            ImGui.EndPopup();
        }
    }

    // （v0.18.69.23 二轮：副 NPC 的设置整体挪进主窗口顶部的横栏子页，见 _subPanels / SubNpcPanel。）

    private void DrawAppearanceBlock()
    {
        SubLabel("衣着");

        var width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;

        Group(!_controller.HasNpc, () =>
        {
            if (ImGui.Button("照抄我的外观", new Vector2(width, 0f)))
                _controller.ApplyAppearanceFromPlayer();

            ImGui.SameLine();

            if (ImGui.Button("换成裸体默认", new Vector2(width, 0f)))
                _controller.ApplyAppearanceNaked();
        });

        ImGui.Spacing();

        DrawDesignSection();

        DrawPenumbraSection();

        SubLabel("召唤");

        // ── v0.18.3：出生点偏移（召唤 / 换区重召唤共用）──
        // 从旧的「跟随与动作」段落挪来 —— 它描述的是"她怎么出现"，不是"她怎么跟"。
        var spawnBehind = _config.SpawnOffsetBehind;
        if (ImGui.SliderFloat("出生点·前后##spawnb", ref spawnBehind, -4f, 4f, "%.1f 米"))
            _config.SpawnOffsetBehind = spawnBehind;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("召唤 / 换区重召唤时，她出现在你身后多少米。\n负数 = 出现在你面前；0 = 紧贴你的位置。\n改动在下一次召唤时生效。");

        var spawnSide = _config.SpawnOffsetSide;
        if (ImGui.SliderFloat("出生点·左右##spawns", ref spawnSide, -4f, 4f, "%.1f 米"))
            _config.SpawnOffsetSide = spawnSide;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("召唤 / 换区重召唤时，她在你侧向偏多少米。\n落点被墙挡住时会自动退回你的脚下，不会出生在几何体里。");

        var autoRespawn = _config.AutoRespawnAfterZoneChange;
        if (ImGui.Checkbox("换区后自动跟随##autorespawn", ref autoRespawn))
            _config.AutoRespawnAfterZoneChange = autoRespawn;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("开启：换区后她会自动在你身边重新出现，不用每次手动召唤。\n关闭：换区后提示你手动召唤。");

        // v0.18.69.20：打开插件自动召唤
        var autoSummon = _config.AutoSummonOnLoad;
        if (ImGui.Checkbox("打开插件自动召唤##autosummon", ref autoSummon))
            _config.AutoSummonOnLoad = autoSummon;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("开启：进游戏后她会自动出现在你身边，不用每次手动召唤。\n手动送走后本次会话不会再自动召唤，下次启动插件才会。");
    }

    private void DrawDesignSection()
    {
        var designs = _controller.GetDesigns();

        if (designs.Count == 0)
        {
            ImGui.TextDisabled("没读到 Glamourer 设计。确认 Glamourer 已安装、且 designs 目录里有内容。");
            ImGui.Spacing();
            return;
        }

        // 当前选中的设计名（从配置里的 id 反查）。
        var currentId   = _config.GlamourerDesignId;
        var currentName = "（不套设计）";
        foreach (var (id, name) in designs)
        {
            if (id == currentId) { currentName = name; break; }
        }

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("设计", currentName))
        {
            if (ImGui.Selectable("（不套设计）##none", currentId == Guid.Empty))
            {
                _config.GlamourerDesignId = Guid.Empty;

                // 回到"照抄玩家"的朴素样子。
                if (_controller.HasNpc)
                    _controller.ApplyAppearanceFromPlayer();
            }

            foreach (var (id, name) in designs)
            {
                if (!ImGui.Selectable($"{name}##d{id}", id == currentId))
                    continue;

                if (_controller.HasNpc)
                {
                    if (_controller.ApplyDesign(id, name, out var error))
                        _config.GlamourerDesignId = id;
                    else
                        ImGui.TextDisabled($"套用失败：{error}");
                }
                else
                {
                    // 没召唤就先记下来，召唤时会自动套上。
                    _config.GlamourerDesignId = id;
                    ImGui.TextDisabled("已记住这份设计，召唤后自动套用。");
                }
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled($"共 {designs.Count} 份设计，选中即换上并记住。召唤时也会自动套用。");
        ImGui.Spacing();
    }

    /// <summary>
    /// v0.18.55：Penumbra 个人分组。
    /// 选一个 collection（建议在 Penumbra 里专门建一个，比如「SmartNpc」，
    /// 把只想对她一个人生效的 mod 放进去）。召唤时自动挂上，
    /// Penumbra 的对象面板里能看到这条个人指派。
    /// </summary>
    private void DrawPenumbraSection()
    {
        SubLabel("Penumbra 分组");

        var collections = _controller.GetPenumbraCollections();

        if (collections.Count == 0)
        {
            ImGui.TextDisabled("没读到 Penumbra 分组。确认 Penumbra 已安装并启用。");
            ImGui.Spacing();
            return;
        }

        // 当前选中的分组名（配置里的 id 反查；查不到用备份名）。
        var currentId   = _config.PenumbraCollectionId;
        var currentName = "（不挂分组）";
        if (currentId != Guid.Empty)
        {
            currentName = collections.FirstOrDefault(c => c.Id == currentId).Name;
            if (string.IsNullOrEmpty(currentName))
                currentName = string.IsNullOrEmpty(_config.PenumbraCollectionName)
                    ? "已记住的分组"
                    : _config.PenumbraCollectionName;
        }

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("分组", currentName))
        {
            if (ImGui.Selectable("（不挂分组）##penNone", currentId == Guid.Empty))
                _controller.ClearPenumbraAssignment(out _);

            foreach (var (id, name) in collections)
            {
                if (!ImGui.Selectable($"{name}##p{id}", id == currentId))
                    continue;

                if (!_controller.ApplyPenumbraCollection(id, name, out var error))
                    ImGui.TextDisabled($"挂载失败：{error}");
            }

            ImGui.EndCombo();
        }

        // 状态行：她在场时显示 Penumbra 侧的真实指派（反查回来的，不是配置回显）。
        var status = _controller.HasNpc
            ? _controller.PenumbraAssignedName
            : string.Empty;
        ImGui.TextDisabled(status.Length > 0
            ? $"她当前挂在：「{status}」（召唤/换区自动挂上，送走自动解绑）。"
            : "召唤时自动挂上；只有这个分组里的 mod 会对她生效。");
        ImGui.Spacing();
    }

    /// <summary>
    /// v0.18.57：可选待机姿势。候选 = 玩家 /pose 的 6 个站立姿势（姿势一~六），
    /// 与玩家换姿势的机制完全一致（起手段踢一次 + 循环段当基底）。
    /// 她站着时保持选中的姿势，走动自动中断、停下自动恢复；人格小动作照常穿插。
    /// </summary>
    private void DrawIdleEmoteSection()
    {
        var choices = _controller.GetIdleEmotes();

        var currentId   = _config.IdleEmoteRowId;
        var currentName = "游戏默认待机";
        if (currentId != 0)
        {
            var match = choices.FirstOrDefault(c => c.EmoteRowId == currentId);
            currentName = match?.Name
                          ?? (string.IsNullOrEmpty(_config.IdleEmoteName) ? $"#{currentId}" : _config.IdleEmoteName);
        }

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("待机动作", currentName))
        {
            if (ImGui.Selectable("游戏默认待机##idleNone", currentId == 0))
                _controller.SetIdleEmote(0, string.Empty, out _);

            foreach (var c in choices)
            {
                if (!ImGui.Selectable($"{c.Name}##idle{c.EmoteRowId}", c.EmoteRowId == currentId))
                    continue;

                if (!_controller.SetIdleEmote(c.EmoteRowId, c.Name, out var error))
                    ImGui.TextDisabled(error);
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled("玩家 /pose 的 6 个站立姿势。停止移动满「放松等待时间」后进入；走动自动中断。");

        // v0.18.58/59：放松等待时间（停止移动后多久进入姿势）——始终有效；
        // 自动随机勾上后，进入的姿势随机挑（与上次不同）。
        var autoRandom = _config.IdleAutoRandom;
        if (ImGui.Checkbox("自动随机改变站立时的姿势##idlerandom", ref autoRandom))
            _config.IdleAutoRandom = autoRandom;

        {
            var relax = Math.Clamp(_config.IdleRelaxSeconds, 0f, 120f);
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.SliderFloat("放松等待时间##idlerelax", ref relax, 0f, 120f, "%.0f 秒"))
                _config.IdleRelaxSeconds = Math.Clamp(relax, 0f, 600f);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("停止移动后，站满这么久才进入待机姿势。\n0 = 停下立即进入。\n（自动随机开着时，这个只管\"首次进姿势\"。）");
        }

        // v0.18.66：随机姿势切换间隔 —— 与放松等待时间解耦（用户实测共用一个值时，
        // Relax 调小会导致起手动画每 3 秒重播一次）。
        if (_config.IdleAutoRandom)
        {
            var switchSec = Math.Clamp(_config.IdleSwitchSeconds, 5f, 300f);
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.SliderFloat("随机切换间隔##idleswitch", ref switchSec, 5f, 300f, "%.0f 秒"))
                _config.IdleSwitchSeconds = Math.Clamp(switchSec, 5f, 600f);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("「自动随机改变姿势」开着时：进入姿势后，每隔多久自己换下一个。\n实际下限 5 秒 —— 再小起手动画就会连着播。");
        }

        ImGui.Spacing();
    }

    // ── 分组 2：性格与动作 ────────────────────────────────────────

    private void DrawPersonaBlock()
    {
        SubLabel("人格");

        DrawPersonaSection();

        DrawLinesSection();

        SubLabel("动作");

        DrawActionSection();

        SubLabel("待机动作");

        DrawIdleEmoteSection();

        SubLabel("动作时机");

        var blockMoving = _config.BlockGesturesWhileMoving;
        if (ImGui.Checkbox("移动时不做动作##blockmoving", ref blockMoving))
            _config.BlockGesturesWhileMoving = blockMoving;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("开启：移动期间不触发任何新动作 / 闲聊，停下后才来。\n关闭：移动中也可以开始做动作，但她会站住做完再走（永远不边走边做）。");
    }

    private void DrawPersonaSection()
    {
        var count  = PersonaNames.Length;
        var width  = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * (count - 1)) / count;

        for (var i = 0; i < count; ++i)
        {
            if (i > 0)
                ImGui.SameLine();

            var selected = _config.Persona == i;

            // ⚠️ 之前把"当前人格"画成了 **禁用按钮**，于是那一项点不动、
            // 而且勾永远停在那里 —— 用户报"元气有个勾但完全无法选择"。
            // 现在四个都可点，选中项只是换个底色。
            if (selected)
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.24f, 0.45f, 0.28f, 1f));

            var label = selected ? $"{PersonaNames[i]} ✓" : PersonaNames[i];
            if (ImGui.Button(label, new Vector2(width, 0f)))
                _controller.SetPersona(PersonaNames[i]);

            if (selected)
                ImGui.PopStyleColor();
        }

        var personaHint = _config.Persona switch
        {
            0 => "安静：话少、跟得远、动作小。温柔克制，一直在这儿。",
            1 => "元气：话多、贴得近、爱招手拍手。喜欢明晃晃写在脸上。",
            2 => "傲娇：嘴硬心软，才不是 / 别误会。壳是硬的，芯子里全是关心。",
            _ => "弱气：小心翼翼，那个…… / 对不起 / 可以吗。怕添麻烦，但很依赖你。",
        };

        ImGui.TextDisabled(personaHint);

        // v0.18.50：让"这套台词到底有多少条"可见 —— 用户要求每种人格 ≥39 条。
        ImGui.SameLine();
        ImGui.TextDisabled($"（台词 {Core.NpcLines.CountFor((Core.NpcBrain.Persona)_config.Persona)} 条）");

        ImGui.Spacing();
    }

    /// <summary>
    /// v0.18.50 台词库面板：称谓变量 + 各类触发开关 + 现状显示 + 试说按钮。
    ///
    /// <para>⚠️ 天气 / 时段<b>全部读游戏内状态</b>（用户明确要求），
    /// 所以这里显示的是 <see cref="Core.NpcEnvironment"/> 的读数，
    /// 连同原始值一起显示 —— 万一分错类，一眼就能看出来。</para>
    /// </summary>
    private void DrawLinesSection()
    {
        SubLabel("台词");

        // ── 玩家称谓（{{称谓}} 变量）──
        var title = _config.PlayerTitle ?? string.Empty;
        if (ImGui.InputText("玩家称谓##playertitle", ref title, 24))
        {
            _config.PlayerTitle = title;
            _controller.ApplySettings(_config);   // 立刻生效，不必等下一句
        }
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("台词里的 {{称谓}} 会替换成这里填的词。\n留空 = 用默认的「主人」。\n例：{{称谓}}今天也真漂亮 → 主人今天也真漂亮");

        // ── 游戏内现状 ──
        ImGui.TextDisabled(Core.NpcEnvironment.Describe());
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("天气 / 时段全部取自游戏内状态（WeatherManager + 艾欧泽亚时间），不用现实时钟。\n后面那串原始值是用来校准的：如果小时数不对，在下面手动钉死即可。");

        var err = Core.NpcEnvironment.LastError;
        if (err.Length > 0)
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.4f, 1f), "读数异常：" + err);

        // ── 各类触发开关 ──
        var weather = _config.WeatherLines;
        if (ImGui.Checkbox("天气台词##wlines", ref weather)) _config.WeatherLines = weather;
        ImGui.SameLine();

        var dayPhase = _config.DayPhaseLines;
        if (ImGui.Checkbox("时段台词##plines", ref dayPhase)) _config.DayPhaseLines = dayPhase;
        ImGui.SameLine();

        var combat = _config.CombatLines;
        if (ImGui.Checkbox("战斗台词##clines", ref combat)) _config.CombatLines = combat;
        ImGui.SameLine();

        var touch = _config.TouchLines;
        if (ImGui.Checkbox("互动台词##tlines", ref touch)) _config.TouchLines = touch;

        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("互动台词 = 你把她选成目标 / 你一直看着她 / 你贴得很近 / 你掉血时她的关心。");

        ImGui.SameLine();

        var job = _config.JobLines;
        if (ImGui.Checkbox("换职业台词##joblines", ref job)) _config.JobLines = job;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("你换成另一个职业时她说一句跟那个职业有关的话。\n"
                           + "⚠️ 只在「真的切到了不同的职业」时说一次：\n"
                           + "骑士→武士 说一次、武士→骑士 再说一次，骑士→骑士 一句都不说。");

        // ── v0.18.51 天气名词 ──
        var wName = _config.WeatherNameLines;
        if (ImGui.Checkbox("天气台词用真实名词##wnamelines", ref wName)) _config.WeatherNameLines = wName;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("开启：直接把游戏里那个天气名词说进句子里。\n例：碧空 →「今天真是碧空如洗呢~！」、薄雾 →「今天起了点薄雾呢。」\n关闭：只说按晴/雨/雪归类的关心台词。");

        ImGui.SameLine();

        var wSpecial = _config.SpecialWeatherLines;
        if (ImGui.Checkbox("副本特殊天气也开口##wspecial", ref wSpecial)) _config.SpecialWeatherLines = wSpecial;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("妖雾 / 极光这类副本异象。默认关闭 —— 在没有雨的副本里喊「下雨了」很出戏。");

        // ── v0.18.51 时段来源 ──
        var src = _config.DayPhaseSource;
        if (ImGui.RadioButton("时段跟游戏内艾时##srcgame", ref src, 0))
        {
            _config.DayPhaseSource = src;
            _controller.ApplySettings(_config);
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("时段跟现实时间##srcreal", ref src, 1))
        {
            _config.DayPhaseSource = src;
            _controller.ApplySettings(_config);
        }
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("艾欧泽亚一天只有 70 分钟真实时间，和现实时钟完全不同步：\n现实凌晨一点，游戏里完全可能正是黄昏。\n想要她按你的现实作息说话就选「现实时间」。");

        // ── v0.18.51 台词节奏 / 动作密度 ──
        var cooldown = _config.LineCooldownSeconds;
        if (ImGui.SliderFloat("两句台词最短间隔##linecd", ref cooldown, 0f, 60f, "%.0f 秒"))
        {
            _config.LineCooldownSeconds = cooldown;
            _controller.ApplySettings(_config);
        }
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("对所有台词生效的统一闸门（闲聊 / 天气 / 时段 / 战斗 / 互动）。\n0 = 不冷却。觉得她话太密就往大调。\n⚠️ 你主动点的（打招呼、右键菜单、面板试说）不受它限制。");

        var closeDist = _config.CloseTouchDistance;
        // v0.18.52：下限 0.5 → 0.05（用户要"几乎贴在一起"才算贴近），刻度也细到两位小数。
        if (ImGui.SliderFloat("贴近触发距离##closedist", ref closeDist, 0.05f, 6f, "%.2f 米"))
        {
            _config.CloseTouchDistance = closeDist;
            _controller.ApplySettings(_config);
        }
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("你站定、并靠到这个距离以内持续 3 秒，她才会说「被靠近」那类台词。");

        var gChance = _config.GestureChancePercent;
        if (ImGui.SliderFloat("台词配动作的几率##gchance", ref gChance, 0f, 100f, "%.0f%%"))
        {
            _config.GestureChancePercent = gChance;
            _controller.ApplySettings(_config);
        }
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("台词的含义决定「配哪个动作」，这个百分数决定「配不配」。\n0 = 说话时永远不比划（只保留待机小动作）；100 = 匹配得上就一定比划。");

        var gGap = _config.GestureMinGapSeconds;
        if (ImGui.SliderFloat("两个动作最短间隔##ggap", ref gGap, 0f, 30f, "%.0f 秒"))
        {
            _config.GestureMinGapSeconds = gGap;
            _controller.ApplySettings(_config);
        }
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("防止她一个动作接一个动作地比划不停。调大 = 更安静，只在关键句上动一下。");

        // ── v0.18.53：说话时的嘴巴开合 + 对她做动作的回应 ──
        var emoteReact = _config.EmoteReactions;
        if (ImGui.Checkbox("回应你对她做的表情动作##emotereact", ref emoteReact))
        {
            _config.EmoteReactions = emoteReact;
            _controller.ApplySettings(_config);
        }
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("⚠️ 必须**先选中她**，然后对她做表情动作，她才会回一句。\n"
                           + "没选中她时做动作 → 一句都不说（那不是对她说）。\n"
                           + "同一个动作只回一次，连做两个之间有 3 秒防连击。");

        ImGui.SameLine();

        var lipSync = _config.LipSyncEnabled;
        if (ImGui.Checkbox("说话时嘴巴开合##lipsync", ref lipSync))
        {
            _config.LipSyncEnabled = lipSync;
            _controller.ApplySettings(_config);
        }
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("走的是底层口型层（Timeline.LipsOverride），**不是**表情动作 ——\n"
                           + "所以她可以一边说话一边比划，两者互不干扰。");

        if (_config.LipSyncEnabled)
        {
            var flap = _config.LipFlapSeconds;
            if (ImGui.SliderFloat("嘴巴开合一次的时间##lipflap", ref flap, 0.05f, 0.6f, "%.2f 秒"))
            {
                _config.LipFlapSeconds = flap;
                _controller.ApplySettings(_config);
            }
            if (ImGui.IsItemHovered())
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("调小 = 嘴动得快（像急急地说）；调大 = 慢慢地说。");
        }

        // ── 小时校准（-1 = 自动）──
        var hourOv = _config.EorzeaHourOverride;
        if (ImGui.SliderInt("手动钉死艾时##hourov", ref hourOv, -1, 23))
        {
            _config.EorzeaHourOverride = hourOv;
            Core.NpcEnvironment.HourOverride = hourOv;
        }
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("-1 = 跟着游戏内艾欧泽亚时间自动算（默认）。\n0~23 = 手动钉死，用于校准");

        // ── 试说一句（不用等触发条件）──
        ImGui.Spacing();

        if (ImGui.Button("试说·天气##tryw"))
            _controller.TrySpeak(Core.TryTopic.天气);
        ImGui.SameLine();
        if (ImGui.Button("试说·时段##tryp"))
            _controller.TrySpeak(Core.TryTopic.时段);
        ImGui.SameLine();
        if (ImGui.Button("试说·战斗##tryc"))
            _controller.TrySpeak(Core.TryTopic.战斗);
        ImGui.SameLine();
        if (ImGui.Button("试说·被选中##tryt"))
            _controller.TrySpeak(Core.TryTopic.被选中);

        ImGui.SameLine();
        if (ImGui.Button("试说·关心##tryh"))
            _controller.TrySpeak(Core.TryTopic.关心);

        ImGui.SameLine();
        if (ImGui.Button("试说·称赞##trypr"))
            _controller.TrySpeak(Core.TryTopic.称赞);

        ImGui.Spacing();
    }

    private void DrawActionSection()
    {
        var animator = _controller.Animator;

        // ⚠️ 之前这里只有"播"没有"停"，用户反馈"没有停止的方法"。
        // 所以状态显示和停止按钮放在最上面，一眼可见。
        var state = animator switch
        {
            null                                          => "（还没有 NPC）",
            { IsBusy: true }                              => $"正在播：{animator.CurrentAction}",
            { IsWalking: true }                           => "正在走路",
            _                                             => "站立中（待机）",
        };

        ImGui.TextUnformatted(state);

        ImGui.BeginDisabled(!(_controller.HasNpc && animator is { IsBusy: true }));
        if (ImGui.Button("停止动作"))
            _controller.StopAction();
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled(animator is { WalkTimeline: > 0 }
            ? $"走路动画 #{animator.WalkTimeline}"
            : "走路动画：没找到（她会一直平移）");

        ImGui.Spacing();

        // 表里的真实名字（本地化）。没召唤时也能先看，只是点了没反应。
        var names = animator?.AvailableActions ?? [];
        if (!ReferenceEquals(names, _actionNames))
            _actionNames = names as string[] ?? names.ToArray();

        if (_actionNames.Length == 0)
        {
            ImGui.TextDisabled("动作表没载入。");
            ImGui.Spacing();
            return;
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("搜索", ref _actionQuery);

        ImGui.SetNextItemWidth(-1f);
        var preview = string.IsNullOrEmpty(_actionPick) ? "（选一个动作…）" : _actionPick;
        if (ImGui.BeginCombo("动作", preview))
        {
            for (var i = 0; i < _actionNames.Length; ++i)
            {
                var name = _actionNames[i];

                if (!string.IsNullOrWhiteSpace(_actionQuery)
                    && !name.Contains(_actionQuery, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (ImGui.Selectable($"{name}##act{i}", string.Equals(name, _actionPick, StringComparison.Ordinal)))
                {
                    _actionPick = name;
                    _controller.Emote(name);
                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled($"共 {_actionNames.Length} 个动作，来自 Emote 表（客户端本地化名）。");
        ImGui.Spacing();
    }

    // ── 分组 3：跟随与瞬移 ────────────────────────────────────────

    /// <summary>把散在三个旧段落里的瞬移参数收拢到一处：瞬移距离 / 高差阈值 / 卡住兜底。</summary>
    private void DrawFollowBlock()
    {
        SubLabel("跟随");

        // 站位。走位只依赖玩家位置与朝向，避障由避障模块单独处理。
        var behind = _config.FollowBehind;
        if (ImGui.SliderFloat("身后距离##follow", ref behind, -6f, 6f, "%.2f 米"))
            _config.FollowBehind = behind;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("负值 = 站到你**前面**。");

        var side = _config.SideOffset;
        if (ImGui.SliderFloat("侧移##side", ref side, -8f, 8f, "%.2f 米"))
            _config.SideOffset = side;

        // ⚠️ v0.17.6（语义按用户定义）：跟随触发 = **玩家离开她一定距离**，距离可调。
        var followAgain = _config.FollowAgainDistance;
        if (ImGui.SliderFloat("重新跟随距离##followagain", ref followAgain, 1f, 15f, "%.1f 米"))
            _config.FollowAgainDistance = followAgain;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("她到位站定后，你离开她超过这个距离就重新跟上来。\n调小 = 你刚起步她就跟；调大 = 允许你先走一段。");

        // ── v0.18.4：跟随强度（引力）──
        var followStr = _config.FollowStrength;
        if (ImGui.SliderFloat("跟随强度（引力）##followstr", ref followStr, 0.5f, 2.5f, "%.2f"))
            _config.FollowStrength = followStr;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("她追你时有多\"黏\"：\n>1 = 落后时跑得更快、迟滞带外也提速，贴得更紧；\n1 = 与你同速追；\n<1 = 更松弛。\n建议 1.0~1.5，太高会显得她一刻不停地怼着你。");

        // v0.13.0：基速不再写死，而是每帧实测本地玩家的速度后派生 ——
        // 你走她小走、你跑她小跑。只留一个倍率供微调。
        _controller.DescribeSpeed(out var pSpeed, out var hSpeed, out var gaitName);
        ImGui.TextDisabled($"你：{pSpeed:0.00} 米/秒｜她：{hSpeed:0.00} 米/秒（{gaitName}）");

        var scale = _config.SpeedScale;
        if (ImGui.SliderFloat("速度倍率##speedscale", ref scale, 0.6f, 1.5f, "%.2f×"))
            _config.SpeedScale = scale;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("1.00 = 与你完全同速；调高一点，她追上来时更利索。");

        SubLabel("瞬移兜底");

        // ⚠️ 三个瞬移参数原本散在「站位」「细则参数」「跟随与动作」三个段落，
        // v0.18.16 收拢到一处 —— 它们描述的是同一件事：什么情况下放弃走路直接瞬移。
        var teleport = _config.TeleportDistance;
        if (ImGui.SliderFloat("跟丢瞬移距离##tele", ref teleport, 3f, 60f, "%.0f 米"))
            _config.TeleportDistance = teleport;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("与她的水平距离超过这个值就瞬移。\n要大于上面的「跟丢」距离，否则她会不停瞬移。");

        var tpHeight = _config.TeleportHeight;
        if (ImGui.SliderFloat("高差瞬移阈值##teleheight", ref tpHeight, 4f, 40f, "%.0f 米"))
            _config.TeleportHeight = tpHeight;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("与她的高低差超过这个值才瞬移。\n调大 = 更少瞬移、更多自己爬楼梯。");

        // ── v0.18.2：卡住兜底 ──
        // 「移动起来就打断动作」已删除（结构上必然与"铁律 Paused"拉锯成抽搐）。
        // 现在用"卡住多久瞬移到你身边"来兜底 —— 走位移层，不干扰动画。
        var stuckTp = _config.StuckTeleportSeconds;
        if (ImGui.SliderFloat("卡住多久瞬移到你身边##stucktp", ref stuckTp, 0f, 15f, "%.1f 秒"))
            _config.StuckTeleportSeconds = stuckTp;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("被墙 / 门 / 障碍挡住、原地挪不动超过这么久，就直接瞬移到你的脚下。\n0 = 关闭兜底（可能卡在原地出不来）。\n建议 2~5 秒。");
    }

    // ── 分组 4：绕障 ─────────────────────────────────────────────

    /// <summary>绕行与避让。游戏里没有可用的导航接口，这里是纯几何方案：
    /// 卡住 → 侧向绕一下；实在绕不过 → 跳跃 / 瞬移兜底。</summary>
    private void DrawAvoidanceBlock()
    {
        // ⚠️ v0.17.3：这几个配置项从 0.13.0 起一直是死的（模块被删了），
        // 现在把模块建回来、并且真正接上总开关。
        var avoid = _config.AvoidanceEnabled;
        if (ImGui.Checkbox("墙体避障##avoid", ref avoid))
        {
            _config.AvoidanceEnabled = avoid;
            if (_controller.Motion is { } m)
                m.AvoidanceEnabled = avoid;
        }
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("开启：被墙 / 栏杆挡住会自动挑一个通畅方向绕过去（承诺式绕行）。\n实在绕不过去才跳跃 / 瞬移兜底。\n关闭：直线走，撞上就站住。");

        // ── v0.18.3：这两项配置从 0.13.0 就存在，现在绕行模块真正接上了 ──
        var detourDur = _config.DetourDuration;
        if (ImGui.SliderFloat("绕行时长##detourdur", ref detourDur, 0.3f, 3f, "%.1f 秒"))
        {
            _config.DetourDuration = detourDur;
            if (_controller.Motion is { } m1)
                m1.DetourDuration = detourDur;
        }
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("被挡住后沿绕行方向走多久再重新看路。\n太短容易在墙前来回拐，太长会绕远。建议 0.8~1.5 秒。");

        var detourCd = _config.DetourCooldown;
        if (ImGui.SliderFloat("绕行间隔##detourcd", ref detourCd, 0f, 3f, "%.1f 秒"))
        {
            _config.DetourCooldown = detourCd;
            if (_controller.Motion is { } m2)
                m2.DetourCooldown = detourCd;
        }
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("一次绕行中断（拐进死角）后，隔多久才允许再挑新方向。\n调 0 = 立刻可以再挑（贴身绕墙角更顺，但可能轻微抖）。");
    }

    // ── 分组 5：站定节奏与视线 ────────────────────────────────────

    /// <summary>她站着不动时的行为：游荡、小动作、头部视线。
    /// ⚠️ 头部视线一族（开关 / 转幅 / 转身概率）原本散在「行为」和「细则参数」两处，收拢到这里。</summary>
    private void DrawIdleBlock()
    {
        SubLabel("游荡与小动作");

        // v0.14.0：到位判定 / 环顾等待 / 搭话阈值仍是固定经验值（互相牵连）。
        // 但「待机小动作的间隔」是"她有多爱动"最直接的旋钮，留在面板。
        var roamMax = _config.RoamRange;
        if (ImGui.SliderFloat("游荡范围##roammax", ref roamMax, 1f, 20f, "%.1f 米"))
            _config.RoamRange = roamMax;

        var gMin = _config.IdleGestureMin;
        if (ImGui.SliderFloat("小动作最短间隔##gmin", ref gMin, 1f, 60f, "%.0f 秒"))
            _config.IdleGestureMin = MathF.Max(1f, gMin);

        var gMax = _config.IdleGestureMax;
        if (ImGui.SliderFloat("小动作最长间隔##gmax", ref gMax, 2f, 180f, "%.0f 秒"))
            _config.IdleGestureMax = MathF.Max(_config.IdleGestureMin + 1f, gMax);

        ImGui.TextDisabled($"她会每隔 {_config.IdleGestureMin:0}-{_config.IdleGestureMax:0} 秒做个小动作。");

        SubLabel("头部视线");

        // v0.18.32 最终语义（用户裁定，优先级从高到低）：
        //   「始终不注视玩家」   = 最高优先级：勾=完全不看，上面两项失效（置灰）。
        //   「站定时用头/视线看」= 纯移动闸门（不再覆盖频率）：勾=怎么看交给下一项，走动时收回视线；
        //                          不勾=同样交给下一项，站定/行动都照它执行。
        //   「始终注视玩家」     = 注视频率（始终生效）：勾=一直看；不勾=60% 占视（10~20 秒看 / 5~10 秒移开）。
        var never = _config.NeverGazeAtPlayer;

        if (never)
            ImGui.BeginDisabled();

        var head = _config.CameraHeadLook;
        if (ImGui.Checkbox("站定时用头 / 视线看（走动时收回）##headlook", ref head))
            _config.CameraHeadLook = head;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("勾选 = 怎么看由下一项决定，但一走起来视线立刻收回、头回正目视前方。\n不勾选 = 同样由下一项决定，站定/行动都照它执行。");

        var alwaysGaze = _config.AlwaysGazeAtPlayer;
        if (ImGui.Checkbox("始终注视玩家##alwaysgaze", ref alwaysGaze))
            _config.AlwaysGazeAtPlayer = alwaysGaze;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("勾选 = 一直看着你（行动中指头/眼视线，超出转幅时收回，绝不边走边转身）。\n不勾选 = 自然张望：按下面的占比与时长轮流看你 / 移开视线。");

        // v0.18.34：自然张望参数面板可调（「始终注视玩家」不勾选时生效）。
        if (!alwaysGaze && !never)
        {
            var lookPct = _config.GazeLookPercent;
            if (ImGui.SliderFloat("在看你占比##gazepct", ref lookPct, 0f, 100f, "%.0f%%"))
                _config.GazeLookPercent = lookPct;

            var lookMin = _config.GazeLookMin;
            if (ImGui.SliderFloat("看你最短##gazelookmin", ref lookMin, 1f, 60f, "%.0f 秒"))
                _config.GazeLookMin = MathF.Max(1f, lookMin);

            var lookMax = _config.GazeLookMax;
            if (ImGui.SliderFloat("看你最长##gazelookmax", ref lookMax, 2f, 120f, "%.0f 秒"))
                _config.GazeLookMax = MathF.Max(_config.GazeLookMin + 1f, lookMax);

            var awayMin = _config.GazeAwayMin;
            if (ImGui.SliderFloat("移开最短##gazeawaymin", ref awayMin, 1f, 60f, "%.0f 秒"))
                _config.GazeAwayMin = MathF.Max(1f, awayMin);

            var awayMax = _config.GazeAwayMax;
            if (ImGui.SliderFloat("移开最长##gazeawaymax", ref awayMax, 2f, 120f, "%.0f 秒"))
                _config.GazeAwayMax = MathF.Max(_config.GazeAwayMin + 1f, awayMax);

            ImGui.TextDisabled($"自然张望：约 {_config.GazeLookPercent:0}% 的时间看你（每次 {_config.GazeLookMin:0}~{_config.GazeLookMax:0} 秒），\n其余移开视线（每次 {_config.GazeAwayMin:0}~{_config.GazeAwayMax:0} 秒）。");
        }

        if (never)
            ImGui.EndDisabled();

        if (ImGui.Checkbox("始终不注视玩家##nevergaze", ref never))
            _config.NeverGazeAtPlayer = never;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("勾选 = 她完全不看玩家（优先级最高，\n上面两项立即失效并置灰）。");

        var headLimit = _config.HeadTurnLimitDeg;
        if (ImGui.SliderFloat("头眼转幅##headlimit", ref headLimit, 20f, 160f, "%.0f°"))
            _config.HeadTurnLimitDeg = headLimit;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("她与目标的角度超过这个值，头/眼就追不上了，\n才会触发「转身追」或「干脆不看」。");

        var chance = _config.TurnChancePercent;
        if (ImGui.SliderFloat("超转幅时转身概率##turnchance", ref chance, 0f, 100f, "%.0f%%"))
            _config.TurnChancePercent = chance;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("超出头眼转幅时，有多少概率选择「带动作地转身」。\n剩余概率为「干脆不看」。");

        var react = _config.SceneReactions;
        if (ImGui.Checkbox("对战斗 / 生产有反应##react", ref react))
            _config.SceneReactions = react;

        // ── v0.18.69.21 挂机模式 ──
        SubLabel("挂机模式");

        var afk = _config.AfkModeEnabled;
        if (ImGui.Checkbox("你挂机时她坐下休息##afkmode", ref afk))
        {
            _config.AfkModeEnabled = afk;
            _controller.ApplySettings(_config);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("你连续一段时间没有任何操作（不移动、无状态变化、无表情动作），\n"
                           + "她就原地坐下并停止一切自动说话；你一动她立刻站起来恢复正常。\n"
                           + "手动送走或换区会重新计时。");

        var afkSecs = _config.AfkTimeoutSeconds;
        if (ImGui.InputFloat("无操作阈值（秒）##afksecs", ref afkSecs, 10f, 60f, "%.0f"))
        {
            _config.AfkTimeoutSeconds = Math.Clamp(afkSecs, 1f, 3600f);
            _controller.ApplySettings(_config);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("多少秒没操作算挂机：最小 1 秒，最大 1 小时（3600 秒）。\n默认 300 = 5 分钟。改动立即生效并重新计时。");

        if (afk)
            ImGui.TextDisabled($"挂机阈值：{_config.AfkTimeoutSeconds:0} 秒（{TimeSpan.FromSeconds(_config.AfkTimeoutSeconds).TotalMinutes:0.#} 分钟）。");

        SubLabel("目标交互");

        var targetable = _config.TargetableByPlayer;
        if (ImGui.Checkbox("可以被选中为目标（鼠标点选 / Tab）##targetable", ref targetable))
            _config.TargetableByPlayer = targetable;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("让她能被鼠标点击或 Tab 选中为目标。\n关闭则恢复默认的不可选中。");

        var clickFb = _config.TargetClickFallback;
        if (ImGui.Checkbox("点她没反应时，替游戏把目标接上##clickfallback", ref clickFb))
            _config.TargetClickFallback = clickFb;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("只在「鼠标确实悬停在她身上、但点下去目标不是她」这一刻才动手，\n"
                           + "不会干扰正常点击。\n"
                           + "关掉 = 只保持可选中位，完全不碰目标系统。");

        ImGui.SameLine();
        if (ImGui.SmallButton("立刻选中她##selectnow"))
        {
            var addr = _controller.NpcAddress;
            _selectResult = addr != nint.Zero
                ? NpcTargetable.SelectNow((GameObject*)addr)
                : "她不在场";
        }
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("无条件把硬目标设成她。\n它同时是最直接的探针：连这个都设不上，\n说明问题在更底层，不在「点击那条路」。");

        ImGui.TextUnformatted($"兜底接管 {NpcTargetable.FallbackHits} 次｜最近：{NpcTargetable.FallbackLast}");
        if (_selectResult.Length > 0)
            ImGui.TextUnformatted("手动选中：" + _selectResult);

        SubLabel("说话配音（角色语音）");

        var voiceOn = _config.VoiceEnabled;
        if (ImGui.Checkbox("她说话时发声##voiceon", ref voiceOn))
            _config.VoiceEnabled = voiceOn;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("她每次开口时用她自己的嗓音发一声。\n走的是游戏自己的角色发声通路（做动作时的配音也是这条），\n不是 UI 音效。");

        // ① 嗓音：决定"用哪种声音"
        var preset = _config.VoicePresetId;
        if (ImGui.SliderInt("嗓音##voicepreset", ref preset, 0, 24, preset == 0 ? "沿用外观来源" : "%d"))
            _config.VoicePresetId = Math.Clamp(preset, 0, 255);
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("角色创建时选的那种嗓音（写进 Character.Vfx.VoiceId）。\n0 = 不改，沿用抄外观时从玩家继承来的嗓音。\n改完立刻生效，做动作的配音也会跟着变。");

        var presetText = _config.VoicePresetId.ToString();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70);
        if (ImGui.InputText("##voicepresetnum", ref presetText, 4))
        {
            if (int.TryParse(presetText, out var pp))
                _config.VoicePresetId = Math.Clamp(pp, 0, 255);
        }
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("想要 24 以上的嗓音编号就在这里填。");

        // ② 语音行号：决定"发哪一声"
        var line = _config.VoiceLineId;
        if (ImGui.SliderInt("语音行号##voiceline", ref line, 0, 40, line == 0 ? "不发声" : "%d"))
            _config.VoiceLineId = Math.Clamp(line, 0, 9999);
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("说话时具体发哪一声，0 = 不发声。\n行号和实际声音没有离线对照表，用「试听」按耳朵挑。");

        ImGui.SameLine();
        if (ImGui.SmallButton("试听##voiceaudition"))
        {
            var addr = _controller.NpcAddress;
            if (addr != nint.Zero)
                NpcVoice.Audition((Character*)addr, Math.Max(0, _config.VoiceLineId));
        }
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("让她立刻发一声当前行号的声音（不受节流限制）。\n⚠️ 需要她已经出现在场上。");

        var lineText = _config.VoiceLineId.ToString();
        ImGui.SetNextItemWidth(90);
        if (ImGui.InputText("自定义行号##voicecustom", ref lineText, 6))
        {
            if (int.TryParse(lineText, out var parsed))
                _config.VoiceLineId = Math.Clamp(parsed, 0, 9999);
        }
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("想要 40 以上的行号就在这里填（0 = 不发声）。");

        ImGui.TextDisabled(NpcVoice.LastPlayOk || _config.VoiceLineId == 0
            ? "提示：让她说句话（或点「试听」）就能听到实际效果。"
            : "提示：上一声没能发出来 —— 行号无效，或她还没出现。");

        if (!string.IsNullOrEmpty(NpcVoice.LastError))
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.4f, 1f), "接口报错：" + NpcVoice.LastError);

        SubLabel("头顶说话气泡");

        var balloonOn = _config.BalloonEnabled;
        if (ImGui.Checkbox("她说话时头顶弹气泡##balloonon", ref balloonOn))
            _config.BalloonEnabled = balloonOn;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("走游戏自己的 NPC 喊话气泡（Character.YellBalloon.OpenBalloon），\n"
                           + "和游戏里 NPC 头顶冒话是同一套，不是我们另画的 UI。\n"
                           + "关掉 = 只在聊天栏看到台词。");

        var balloonSec = _config.BalloonSeconds;
        if (ImGui.SliderFloat("停留时长##balloonsec", ref balloonSec, 0.5f, 30f, "%.1f"))
            _config.BalloonSeconds = balloonSec;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("喂给 NpcYellBalloon 的 PlayTimer，到点由我们自己收回。\n"
                           + "⚠️ 这个字段的单位还没实锤：\n"
                           + "  气泡一闪就没 → 游戏是按帧递减的，把值调大（240 ≈ 60fps 下 4 秒）。\n"
                           + "  气泡停太久 → 调小。");

        ImGui.SameLine();
        if (ImGui.SmallButton("试弹一句##balloontest"))
        {
            var addr2 = _controller.NpcAddress;
            if (addr2 != nint.Zero)
                NpcBalloon.Show((Character*)addr2, "（试一句）今天的风，挺舒服的。", _config.BalloonSeconds);
        }
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("立刻在她头顶弹一句，看清楚气泡长什么样、停多久。");

        ImGui.TextDisabled("最近：" + NpcBalloon.LastSummary);

        if (!string.IsNullOrEmpty(NpcBalloon.LastError))
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.4f, 1f), "接口报错：" + NpcBalloon.LastError);
    }

    // ── 分组 6：高级与调试（默认折叠）────────────────────────────

    private void DrawAdvancedBlock()
    {
        SubLabel("行走动画");

        // 自动取的是玩家角色自己的 normal/walk 与 normal/run（在 ActionTimeline 表里
        // 精确匹配这两行）。旧实现取"第一个名字里带 run 的行"，那取到的是技能动画。
        var manual = _config.WalkTimelineId;
        if (ImGui.InputInt("手填行走行号（0 = 自动）", ref manual))
            _config.WalkTimelineId = Math.Clamp(manual, 0, ushort.MaxValue);

        _controller.DescribeTimelines(out var walk, out var run, out var walkSrc, out var runSrc);
        ImGui.TextDisabled(walk == 0 ? "行走动作：没找到"
                                      : $"行走动作 #{walk}（{walkSrc}）");
        ImGui.TextDisabled(run  == 0 ? "奔跑动作：没找到，跑起来会沿用走路动作"
                                      : $"奔跑动作 #{run}（{runSrc}）");
        ImGui.TextDisabled("播放速度按实际移动速度自动匹配，不需要手调。");

        SubLabel("转身与诊断");

        var turnSpeed = _config.TurnPlaybackSpeed;
        if (ImGui.SliderFloat("转身动作速度##turnspeed", ref turnSpeed, 0.25f, 3f, "%.2f×"))
            _config.TurnPlaybackSpeed = turnSpeed;

        var trace = _config.TurnTraceDebug;
        if (ImGui.Checkbox("转身逐帧诊断（写 turn-trace.txt）##trace", ref trace))
            _config.TurnTraceDebug = trace;
        if (ImGui.IsItemHovered())
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("排查「转身动作中断」时才开。会逐帧写文件，平时保持关闭。");

        ImGui.Spacing();

        // 只读诊断：把她此刻的"可绘制性"打进日志（索引/类型/就绪/绘制对象/渲染标志/模型/坐标）。
        // 不创建、不销毁任何对象 —— 出问题时按一下，日志里就有定位所需的一切。
        if (ImGui.Button("诊断：打印她的当前状态"))
            _controller.LogDiagnostics();
    }

    // ── 底部 ─────────────────────────────────────────────────────

    private void DrawFooter()
    {
        ImGui.Separator();
        ImGui.TextDisabled($"Smart Npc  v{Plugin.DisplayVersion}");
        ImGui.TextDisabled("纯视觉效果插件：不参与战斗、制作、任务，也不会上传任何数据。");
    }

    /// <summary>
    /// 把一段控件包进"禁用区"。
    /// <c>ImGui.BeginDisabled</c> 返回 void，不能用 using，所以统一走这里。
    /// </summary>
    private static void Group(bool disabled, Action body)
    {
        ImGui.BeginDisabled(disabled);
        body();
        ImGui.EndDisabled();
    }

    private void ApplyLiveSettings()
    {
        _config.Sanitize();
        _controller.ApplySettings(_config);
    }
}
