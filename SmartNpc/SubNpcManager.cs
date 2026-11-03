using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using SmartNpc.Core;

namespace SmartNpc;

/// <summary>
/// 副 NPC（随从）管理器（v0.18.69.22）。
///
/// <para>最多 <see cref="Configuration.SubNpcSlotCount"/> 个槽位，每个槽位一套与主 NPC
/// <b>完全同源</b>的实例栈（NpcFactory / NpcAppearance / NpcMotion / NpcAnimator /
/// NpcBrain / NpcBehavior）—— 都是可实例化类，直接各建一份，互不共享状态。</para>
///
/// <para>与主 NPC 的差别（刻意做简，v1 范围）：
/// · 跟随玩家（站位按槽位侧向偏移错开，不和主 NPC 叠在一起）+ 逛荡 + 按各自人格闲聊；
/// · 不做天气/时段/职业/表情回应这类控制器级台词触发器（主 NPC 专属）；
/// · 不挂 Penumbra 个人分组（外观走 Glamourer 设计 / 抄玩家）；
/// · 换区判死后按主配置的「换区自动重召唤」节奏自己重新召唤。</para>
///
/// <para>⚠️ 每帧驱动由 <see cref="NpcController.OnFrameworkUpdate"/> 在**所有主 NPC
/// 提前 return 之前**调 <see cref="Tick"/> —— 主 NPC 不在场/点亮中/判死帧都不能饿死副 NPC。</para>
/// </summary>
public sealed unsafe class SubNpcManager
{
    private IPluginLog _log;
    private IObjectTable?  _objectTable;
    private IClientState?  _clientState;
    private IChatGui?      _chat;
    private IDataManager?  _data;
    private ICondition?    _condition;
    private Configuration? _config;
    private GlamourerBridge? _glamourer;

    /// <summary>由主控制器接上（复用同一条存档通道）。</summary>
    public Action? ConfigSaveRequested { get; set; }

    private void SaveConfig() => ConfigSaveRequested?.Invoke();

    /// <summary>点亮结束后的站稳时长（与主 NPC 同值）。</summary>
    private const float SettleSeconds = 1.2f;

    /// <summary>最多等动画接管多久（秒，与主 NPC 同值）。</summary>
    private const float AnimWaitMaxSeconds = 4f;

    // ── 玩家实测速度（子 NPC 跑/走判定用；自己平滑一份，不依赖主控制器的私有状态）──
    private Vector3 _lastPlayerPos;
    private bool _hasPlayerPos;
    private float _playerSpeed;
    private float _playerRawSpeed;

    /// <summary>
    /// 启动自动召唤是否已武装过（v0.18.69.23）。
    /// 首次进游戏世界那一帧武装一次：启用的槽位等一会儿自动召唤。
    /// </summary>
    private bool _autoSummonArmed;

    private readonly SubNpcRuntime[] _slots;

    public SubNpcManager(IPluginLog? log)
    {
        _log = log ?? NpcController.NullLogForSub();
        _slots = new SubNpcRuntime[Configuration.SubNpcSlotCount];
        for (var i = 0; i < _slots.Length; ++i)
            _slots[i] = new SubNpcRuntime();
    }

    public void Attach(IObjectTable objectTable, IClientState clientState, IChatGui chat,
                       IDataManager data, Configuration config, ICondition? condition,
                       GlamourerBridge? glamourer)
    {
        _objectTable = objectTable;
        _clientState = clientState;
        _chat        = chat;
        _data        = data;
        _config      = config;
        _condition   = condition;
        _glamourer   = glamourer;
    }

    /// <summary>槽位是否活着（在场）。</summary>
    public bool IsAlive(int slot)
        => slot >= 0 && slot < _slots.Length && _slots[slot].Npc is { IsAlive: true };

    /// <summary>槽位状态描述（面板用）。</summary>
    public string StatusText(int slot)
    {
        if (slot < 0 || slot >= _slots.Length)
            return "？";
        var rt = _slots[slot];
        if (rt.Npc is { IsAlive: true })
            return rt.SpawnDraw is not null ? "点亮中…" : "在场";
        if (rt.RespawnTimer > 0f)
            return $"稍后自动回来（{rt.RespawnTimer:0} 秒）";
        return "未召唤";
    }

    /// <summary>槽位角色地址（不在场 = IntPtr.Zero。副 NPC 窗口的语音试听用）。</summary>
    public nint NpcAddressOf(int slot)
        => slot >= 0 && slot < _slots.Length && _slots[slot].Npc is { IsAlive: true } h
            ? h.Address
            : nint.Zero;

    // ───────────────────────── 召唤 / 送走 ─────────────────────────

    public void Summon(int slot, bool announce = true)
    {
        if (_config is null || _objectTable is null || _data is null)
            return;

        if (slot < 0 || slot >= _slots.Length || slot >= _config.SubNpcs.Count)
            return;

        var setting = _config.SubNpcs[slot];
        var rt = _slots[slot];

        var player = _objectTable.LocalPlayer;
        if (player is null || player.Address == nint.Zero)
        {
            _chat?.PrintError("[SmartNpc] 还没进入游戏世界，稍后再试。");
            return;
        }

        if (rt.Npc is { IsAlive: true })
        {
            if (announce) _chat?.Print("[SmartNpc] 这个副 NPC 已经在场了。");
            return;
        }

        if (!setting.Enabled)
        {
            if (announce) _chat?.Print("[SmartNpc] 这个槽位还没启用（先勾上「启用」）。");
            return;
        }

        rt.RespawnTimer = 0f;

        NpcFactory.LocalPlayerAddress = player.Address;
        if (!NpcFactory.TrySpawn(false, out var handle, out var error))
        {
            _log.Error("[SmartNpc] 副 NPC {0} 创建失败：{1}", slot + 1, error);
            _chat?.PrintError($"[SmartNpc] 副 NPC {slot + 1} 创建失败：{error}");
            return;
        }

        rt.Npc        = handle;
        rt.Appearance = new NpcAppearance(_log);
        rt.Motion     = new NpcMotion();
        rt.Animator   = new NpcAnimator(_log, _data);
        rt.Brain      = new NpcBrain(_log, _chat, rt.Animator);
        rt.Behaviour  = new NpcBehavior(_log);

        rt.Motion.OnJump         = () => rt.Animator?.PlayJump();
        rt.Motion.OnJumpFalling  = () => rt.Animator?.NotifyJumpApex();
        rt.Motion.OnJumpLanded   = () => rt.Animator?.PlayJumpLand();

        rt.Appearance.Bind(handle);

        // Emote 表动作名灌给语义匹配器（与主 NPC 同一套静态表，重复调用无害）。
        Core.NpcGesture.Initialize(rt.Animator.AvailableActions);

        ApplySlotSettings(slot, forcePushIdle: true);
        rt.Animator.Bind(handle);
        rt.Brain.Bind(handle);

        // 落点：玩家身后 FollowBehind、侧向 FollowSide（三槽错开），落地贴地 + 防穿墙。
        var at  = player.Position;
        var cfg = _config;
        var yaw   = player.Rotation;
        var fwd   = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        var right = new Vector3(fwd.Z, 0f, -fwd.X);

        var px = at.X - fwd.X * setting.FollowBehind + right.X * setting.FollowSide;
        var pz = at.Z - fwd.Z * setting.FollowBehind + right.Z * setting.FollowSide;

        var py = NpcGround.Resolve(px, pz, at.Y, anchorToTarget: true);
        if (!float.IsFinite(py) || MathF.Abs(py - at.Y) > 6f)
            py = at.Y;

        var ox   = px - at.X;
        var oz   = pz - at.Z;
        var olen = MathF.Sqrt(ox * ox + oz * oz);
        if (olen > 0.05f
            && NpcAvoidance.RaycastPath(
                new Vector3(at.X, at.Y, at.Z),
                new Vector3(ox / olen, 0f, oz / olen),
                olen + 0.2f, out _))
        {
            px = at.X;
            pz = at.Z;
            py = at.Y;
        }

        NpcFactory.TeleportTo(handle, new FFXIVClientStructs.FFXIV.Common.Math.Vector3(px, py, pz), 0f);
        handle.TerritoryId = _clientState?.TerritoryType ?? 0;
        rt.LastTerritory   = 0;   // 首帧重新认区（下一帧走 rt.LastTerritory==0 分支记下）

        // 先写模型再请求绘制（与主 NPC 同序 —— 顺序反了 = 死锁）。
        rt.Appearance.PrepareModel(player.Address, BrainTuning.CopyPlayerOnSummon);
        rt.SpawnDraw = new NpcFactory.SpawnDrawState();

        ApplySlotDesign(slot, announce: false);

        NpcTargetable.Apply(
            (GameObject*)handle.Address,
            (GameObject*)player.Address,
            setting.TargetableByPlayer ?? cfg.TargetableByPlayer,
            forceTrace: false,
            ourIndex: handle.ObjectIndex);

        if (VoiceTuning.PresetId > 0)
            NpcVoice.ApplyPreset((Character*)handle.Address, VoiceTuning.PresetId);

        // 名字：召唤瞬间就写一次（之后配置改名由 ApplySettings 的变更侦测接管）。
        rt.SeenName = null;
        PushName(slot);

        _log.Information("[SmartNpc] 副 NPC {0}（{1}）已创建：{2}", slot + 1, setting.Name, handle);
        if (announce)
            _chat?.Print($"[SmartNpc] 副 NPC {slot + 1}「{Core.NpcNaming.EffectiveName(setting.Name, "无名")}」来了。");
    }

    public void Dismiss(int slot)
    {
        if (slot < 0 || slot >= _slots.Length)
            return;

        var rt = _slots[slot];
        rt.RespawnTimer = 0f;             // 手动送走：取消等待中的自动重召唤
        rt.StartupSummonPending = false;  // 手动送走：本次会话不再自动回来（与主 NPC 同语义）

        if (rt.Npc is { } handle && handle.IsAlive)
        {
            NpcBalloon.Forget(handle.Address);
            NpcFactory.Despawn(handle);
            _log.Information("[SmartNpc] 副 NPC {0} 已送走。", slot + 1);
        }

        rt.Clear();
    }

    public void DismissAll()
    {
        for (var i = 0; i < _slots.Length; ++i)
            Dismiss(i);
    }

    /// <summary>把主 NPC 的设置抄进槽位（站位偏移保留槽位自己的），在场就立刻生效。</summary>
    public void CopyFromMain(int slot)
    {
        if (_config is null || slot < 0 || slot >= _slots.Length || slot >= _config.SubNpcs.Count)
            return;

        var main = _config;
        var s    = main.SubNpcs[slot];

        // ⚠️ 名字不抄（v0.18.69.23 二轮，用户明确：缇娅是主 NPC 的名字）——
        // 名字是这个 NPC 的身份，抄过去会出现两个「缇娅」。称谓跟人格口吻走，照抄。
        s.Persona           = main.Persona;
        s.PlayerTitle       = main.PlayerTitle;
        s.GlamourerDesignId = main.GlamourerDesignId;
        s.IdleEmoteRowId    = main.IdleEmoteRowId;
        s.IdleEmoteName     = main.IdleEmoteName;
        // FollowSide / FollowBehind 保留槽位自己的值 —— 三个副 NPC 都抄过去会叠在同一格。

        // v0.18.69.23：台词开关 / 节奏 / 姿势 / 动作概率全部抄齐（功能对齐）。
        s.AutoChatter          = main.AutoChatter;
        s.ShowInChat           = main.ShowInChat;
        s.MinIdleSeconds       = main.MinIdleSeconds;
        s.MaxIdleSeconds       = main.MaxIdleSeconds;
        s.LineCooldownSeconds  = main.LineCooldownSeconds;
        s.CloseTouchDistance   = main.CloseTouchDistance;
        s.SceneReactions       = main.SceneReactions;
        s.WeatherLines         = main.WeatherLines;
        s.WeatherNameLines     = main.WeatherNameLines;
        s.SpecialWeatherLines  = main.SpecialWeatherLines;
        s.DayPhaseLines        = main.DayPhaseLines;
        s.CombatLines          = main.CombatLines;
        s.TouchLines           = main.TouchLines;
        s.JobLines             = main.JobLines;
        s.EmoteReactions       = main.EmoteReactions;
        s.IdleAutoRandom       = main.IdleAutoRandom;
        s.IdleRelaxSeconds     = main.IdleRelaxSeconds;
        s.IdleSwitchSeconds    = main.IdleSwitchSeconds;
        s.GestureChancePercent = main.GestureChancePercent;

        // 语音 / 气泡先置为「跟随主 NPC 设置」—— 用户想单独调再在子页里取消继承。
        s.VoiceInherit   = true;
        s.BalloonInherit = true;

        // 全部可空覆盖置回 null = 跟随主 NPC 的同名设置（要独立调再取消跟随）。
        s.SpeedScale               = null;
        s.FollowStrength           = null;
        s.ArriveDistance           = null;
        s.FollowAgainDistance      = null;
        s.WalkDistance             = null;
        s.TeleportDistance         = null;
        s.TeleportHeight           = null;
        s.StuckTeleportSeconds     = null;
        s.AvoidanceEnabled         = null;
        s.DetourDuration           = null;
        s.DetourCooldown           = null;
        s.RoamRange                = null;
        s.RoamMin                  = null;
        s.BlockGesturesWhileMoving = null;
        s.IdleGestureMin           = null;
        s.IdleGestureMax           = null;
        s.LipSyncEnabled           = null;
        s.LipFlapSeconds           = null;
        s.WalkSpeedScale           = null;
        s.TurnPlaybackSpeed        = null;
        s.CameraHeadLook           = null;
        s.AlwaysGazeAtPlayer       = null;
        s.NeverGazeAtPlayer        = null;
        s.GazeLookPercent          = null;
        s.GazeLookMin              = null;
        s.GazeLookMax              = null;
        s.GazeAwayMin              = null;
        s.GazeAwayMax              = null;
        s.HeadTurnLimitDeg         = null;
        s.TurnChancePercent        = null;
        s.TargetableByPlayer       = null;
        s.AutoRespawnAfterZoneChange = null;

        SaveConfig();
        ApplySlotSettings(slot, forcePushIdle: true);
        ApplySlotDesign(slot, announce: true);
        PushName(slot);

        _chat?.Print($"[SmartNpc] 副 NPC {slot + 1} 已复制主 NPC 设置"
                     + (IsAlive(slot) ? "，并已即时生效。" : "（召唤时生效）。"));
    }

    // ───────────────────────── 设置推送 ─────────────────────────

    /// <summary>
    /// 面板改了主配置（含副 NPC 自己的槽位字段）时由 <c>NpcController.ApplySettings</c> 转发。
    /// 有变更侦测：在场就即时生效，不在场等下次召唤。
    /// </summary>
    public void ApplySettings(Configuration config)
    {
        for (var i = 0; i < _slots.Length && i < config.SubNpcs.Count; ++i)
        {
            var rt = _slots[i];
            var s  = config.SubNpcs[i];

            if (rt.Npc is not { IsAlive: true })
                continue;

            // 在场的槽位：有副作用的推送走变更侦测（召唤走 forcePushIdle 全量推）。
            if (rt.SeenPersona != s.Persona)
            {
                rt.SeenPersona = s.Persona;
                if (rt.Brain is not null && Enum.IsDefined(typeof(NpcBrain.Persona), s.Persona))
                    rt.Brain.SetPersona((NpcBrain.Persona)s.Persona, speak: false);
                if (rt.Behaviour is not null)
                    rt.Behaviour.FollowBehind = FollowDistanceFor((NpcBrain.Persona)s.Persona, s.FollowBehind);
            }

            if (rt.SeenIdleRow != s.IdleEmoteRowId)
            {
                rt.SeenIdleRow = s.IdleEmoteRowId;
                PushIdlePose(i);
            }

            if (rt.SeenDesignId != s.GlamourerDesignId)
                ApplySlotDesign(i, announce: true);

            if (rt.SeenName != s.Name)
                PushName(i);

            // 全部数值/开关直推（含覆盖 ?? 主配置值）—— 每帧推也无害，改了立即生效。
            PushRuntimeSettings(i);
        }
    }

    /// <summary>
    /// 把一个槽位的全部运行参数直推下去（数值/开关类，无副作用，每帧调也无害）。
    /// 覆盖字段 null = 用主 NPC 的同名配置值；ApplySlotSettings（召唤）与
    /// ApplySettings（面板变更）都走这里，改完立即生效、不用重新召唤。
    /// </summary>
    private void PushRuntimeSettings(int slot)
    {
        if (_config is null || slot >= _config.SubNpcs.Count)
            return;

        var rt = _slots[slot];
        var s  = _config.SubNpcs[slot];

        if (rt.Motion is not null)
        {
            rt.Motion.SpeedScale           = s.SpeedScale           ?? _config.SpeedScale;
            rt.Motion.TeleportDistance     = s.TeleportDistance     ?? _config.TeleportDistance;
            rt.Motion.TeleportHeight       = s.TeleportHeight       ?? _config.TeleportHeight;
            rt.Motion.AvoidanceEnabled     = s.AvoidanceEnabled     ?? _config.AvoidanceEnabled;
            rt.Motion.StuckTeleportSeconds = s.StuckTeleportSeconds ?? _config.StuckTeleportSeconds;
            rt.Motion.DetourDuration       = s.DetourDuration       ?? _config.DetourDuration;
            rt.Motion.DetourCooldown       = s.DetourCooldown       ?? _config.DetourCooldown;
            rt.Motion.FollowStrength       = s.FollowStrength       ?? _config.FollowStrength;
            rt.Motion.WalkLagDistance      = s.WalkDistance         ?? _config.WalkDistance;
            rt.Motion.SpawnOffsetBehind    = _config.SpawnOffsetBehind;
            rt.Motion.SpawnOffsetSide      = s.FollowSide;   // 召唤落点用槽位的侧偏
            if (rt.Animator is not null)
                rt.Motion.JumpEnabled = rt.Animator.HasJumpAnim;
        }

        if (rt.Behaviour is not null)
        {
            rt.Behaviour.Enabled        = BrainTuning.BehaviourEnabled;
            rt.Behaviour.RunDistance    = BehaviourTuning.RunDistance;
            rt.Behaviour.WalkDistance   = BehaviourTuning.WalkDistance;
            rt.Behaviour.StrayDistance  = BehaviourTuning.StrayDistance;
            rt.Behaviour.ArriveDistance = Math.Clamp(s.ArriveDistance ?? _config.ArriveDistance, 0.5f, 8f);
            rt.Behaviour.RoamRange      = Math.Clamp(s.RoamRange ?? _config.RoamRange, 1f, 20f);
            rt.Behaviour.RoamMin        = Math.Clamp(s.RoamMin ?? _config.RoamMin, 0.5f, 8f);
            rt.Behaviour.FollowAgainDistance = Math.Clamp(s.FollowAgainDistance ?? _config.FollowAgainDistance, 1f, 30f);
            rt.Behaviour.GesturesWhileMoving = !(s.BlockGesturesWhileMoving ?? _config.BlockGesturesWhileMoving);
            rt.Behaviour.IdleGestureMin = s.IdleGestureMin ?? _config.IdleGestureMin;
            rt.Behaviour.IdleGestureMax = MathF.Max(s.IdleGestureMax ?? _config.IdleGestureMax,
                                                     rt.Behaviour.IdleGestureMin + 1f);
            rt.Behaviour.FollowBehind   = FollowDistanceFor((NpcBrain.Persona)s.Persona, s.FollowBehind);
            rt.Behaviour.FollowSide     = s.FollowSide;
        }

        if (rt.Brain is not null)
        {
            // ── 台词节奏 / 闲聊（全部按槽位）──
            rt.Brain.AutoChatter    = s.AutoChatter;
            rt.Brain.MinIdleSeconds = s.MinIdleSeconds;
            rt.Brain.MaxIdleSeconds = s.MaxIdleSeconds;
            rt.Brain.ShowInChat     = s.ShowInChat;
            rt.Brain.LineCooldownSeconds = Math.Max(0f, s.LineCooldownSeconds);
            rt.Brain.NearChatDistance    = Math.Clamp(s.CloseTouchDistance, 0.3f, 8f);
            rt.Brain.Name = NpcNaming.EffectiveName(s.Name, "她");
            rt.Brain.PlayerTitle = string.IsNullOrWhiteSpace(s.PlayerTitle)
                ? NpcLines.DefaultTitle
                : s.PlayerTitle.Trim();

            // ── 呈现层按槽位覆盖（null/继承 = 沿用全局）──
            rt.Brain.VoiceEnabledOverride   = s.VoiceInherit ? null : s.VoiceEnabled;
            rt.Brain.VoiceLineIdOverride    = s.VoiceInherit ? null : Math.Clamp(s.VoiceLineId, 0, 9999);
            rt.Brain.BalloonEnabledOverride = s.BalloonInherit ? null : s.BalloonEnabled;
            rt.Brain.BalloonSecondsOverride = s.BalloonInherit ? null : Math.Clamp(s.BalloonSeconds, 0.5f, 30f);
            rt.Brain.GestureChanceOverride  = Math.Clamp(s.GestureChancePercent, 0f, 100f);
        }

        if (rt.Animator is not null)
        {
            rt.Animator.LipSyncEnabled = s.LipSyncEnabled ?? _config.LipSyncEnabled;
            rt.Animator.LipFlapSeconds = Math.Clamp(s.LipFlapSeconds ?? _config.LipFlapSeconds, 0.05f, 0.6f);
            rt.Animator.WalkSpeedScale    = s.WalkSpeedScale    ?? _config.WalkSpeedScale;
            rt.Animator.TurnPlaybackSpeed = s.TurnPlaybackSpeed ?? _config.TurnPlaybackSpeed;
            rt.Animator.IdleRelaxSeconds  = Math.Clamp(s.IdleRelaxSeconds, 0f, 600f);
            rt.Animator.IdleSwitchSeconds = Math.Clamp(s.IdleSwitchSeconds, 5f, 600f);
            rt.Animator.IdleAutoRandom    = s.IdleAutoRandom;
        }
    }

    /// <summary>召唤时的全量推送：直推 + 人格/姿势/外观/名字的有副作用部分。</summary>
    private void ApplySlotSettings(int slot, bool forcePushIdle)
    {
        if (_config is null)
            return;

        var rt = _slots[slot];
        var s  = _config.SubNpcs[slot];

        PushRuntimeSettings(slot);

        if (rt.Brain is not null && Enum.IsDefined(typeof(NpcBrain.Persona), s.Persona))
        {
            rt.SeenPersona = s.Persona;
            rt.Brain.SetPersona((NpcBrain.Persona)s.Persona, speak: false);
        }

        if (forcePushIdle)
        {
            rt.SeenIdleRow = s.IdleEmoteRowId;
            PushIdlePose(slot);
        }

        rt.SeenDesignId = s.GlamourerDesignId;
        rt.SeenName     = null;   // 强制 PushName 重写一次
        PushName(slot);
    }

    private List<NpcAnimator.IdleEmoteChoice>? _idleChoices;

    private void PushIdlePose(int slot)
    {
        if (_config is null)
            return;

        var rt = _slots[slot];
        if (rt.Animator is null)
            return;

        var row = Math.Clamp(_config.SubNpcs[slot].IdleEmoteRowId, 0, ushort.MaxValue);
        if (row == 0)
        {
            rt.Animator.SetIdleEmote(0, 0, 0);
            return;
        }

        _idleChoices ??= NpcAnimator.LoadIdleChoices();
        var choice = _idleChoices.FirstOrDefault(c => c.EmoteRowId == row);
        rt.Animator.SetIdleEmote(choice?.PlayEmoteRow ?? 0, choice?.LoopTimeline ?? 0, (ushort)row);
    }

    /// <summary>套用槽位配置的 Glamourer 设计（Guid.Empty = 保持抄玩家的外观不动）。</summary>
    private void ApplySlotDesign(int slot, bool announce)
    {
        if (_config is null)
            return;

        var rt = _slots[slot];
        var s  = _config.SubNpcs[slot];
        rt.SeenDesignId = s.GlamourerDesignId;

        if (s.GlamourerDesignId == Guid.Empty || rt.Appearance is null || rt.Npc is not { IsAlive: true })
            return;

        if (_glamourer is null)
        {
            if (announce) _chat?.Print("[SmartNpc] Glamourer 桥接不可用，外观保持抄玩家的。");
            return;
        }

        var design = _glamourer.ResolveDesign(s.GlamourerDesignId);
        if (design is null)
        {
            if (announce) _chat?.PrintError("[SmartNpc] 副 NPC 的外观设计读不出来：" + _glamourer.LastError);
            return;
        }

        rt.Appearance.ApplyDesign(design);
        if (announce)
            _chat?.Print($"[SmartNpc] 副 NPC {slot + 1} 换上了「{s.GlamourerDesignName}」。");
    }

    private void PushName(int slot)
    {
        if (_config is null)
            return;

        var rt = _slots[slot];
        var s  = _config.SubNpcs[slot];
        if (rt.Npc is not { IsAlive: true } handle)
            return;

        rt.SeenName = s.Name;
        var wanted = NpcNaming.EffectiveName(s.Name, handle.GameAssignedName);
        NpcNaming.WriteName(handle.Address, wanted);
    }

    /// <summary>人格 → 跟随距离倍率（与主 NPC 同表）。</summary>
    private static float FollowDistanceFor(NpcBrain.Persona persona, float configured)
        => persona switch
        {
            NpcBrain.Persona.安静 => configured * 1.6f,
            NpcBrain.Persona.弱气 => configured * 1.3f,
            NpcBrain.Persona.元气 => configured * 0.75f,
            _                      => configured,
        };

    // ───────────────────────── 每帧驱动 ─────────────────────────

    /// <summary>
    /// 每帧驱动全部副 NPC。由主控制器在<b>所有主 NPC 提前 return 之前</b>调用 ——
    /// 主 NPC 不在场 / 点亮中 / 判死帧都必须照常跑。
    /// </summary>
    public void Tick(float delta)
    {
        if (_config is null || _objectTable is null)
            return;

        var player = _objectTable.LocalPlayer;
        if (player is null || player.Address == nint.Zero)
            return;

        // ── 启动自动召唤（v0.18.69.23）：启用的槽位，本次启动进世界后自动叫出来 ──
        // 与主 NPC 的「打开插件自动召唤」同一个总开关（AutoSummonOnLoad）；
        // 首次检测到本地玩家那一帧武装一次，走既有的 RespawnTimer 通道（等世界加载稳定）。
        if (!_autoSummonArmed)
        {
            _autoSummonArmed = true;

            if (_config.AutoSummonOnLoad)
            {
                for (var i = 0; i < _slots.Length && i < _config.SubNpcs.Count; ++i)
                {
                    if (_config.SubNpcs[i].Enabled && _slots[i].Npc is null)
                    {
                        _slots[i].RespawnTimer         = Math.Clamp(_config.AutoRespawnDelay, 1f, 15f);
                        _slots[i].StartupSummonPending = true;
                    }
                }
            }
        }

        var playerPos = player.Position;

        // 玩家实测速度（指数平滑，与主控制器同参数）。
        var rawSpeed = 0f;
        if (_hasPlayerPos)
        {
            var dxp = playerPos.X - _lastPlayerPos.X;
            var dzp = playerPos.Z - _lastPlayerPos.Z;
            rawSpeed = MathF.Sqrt(dxp * dxp + dzp * dzp) / MathF.Max(delta, 0.001f);
        }
        _hasPlayerPos  = true;
        _lastPlayerPos = playerPos;
        _playerSpeed   = _playerSpeed * 0.85f + rawSpeed * 0.15f;
        _playerRawSpeed = rawSpeed;

        var territory = _clientState?.TerritoryType ?? 0;

        // ⚠️ 这版 Dalamud 的 IPlayerCharacter 不能显式写类型名（主控制器全程用 var 推断绕开），
        // 所以这里把各触发器需要的玩家数据**取样成标量**传下去，不传玩家对象。
        var playerRot = player.Rotation;
        var playerAddress = player.Address;

        // 职业表（值类型 RowRef，读失败就 null，槽位侧 0.4s 节流自己看着办）。
        Lumina.Excel.RowRef<Lumina.Excel.Sheets.ClassJob>? jobRow = null;
        try { jobRow = player.ClassJob; } catch { }

        // 玩家血量（掉血关心用；读失败保持上次值即可）。
        uint maxHp = 0, currentHp = 0;
        try { maxHp = player.MaxHp; currentHp = player.CurrentHp; } catch { }

        // 玩家 EntityId（头部视线 SetHeadTarget 用；读失败置 0 = 视线闸门直接收回）。
        uint playerEntityId = 0;
        try { playerEntityId = player.EntityId; } catch { }

        for (var i = 0; i < _slots.Length; ++i)
        {
            if (i >= _config.SubNpcs.Count)
                break;   // Sanitize 还没跑过（理论上不该发生），别越界
            TickSlot(i, delta, playerAddress, playerPos, playerRot, territory, jobRow, maxHp, currentHp, playerEntityId);
        }
    }

    private void TickSlot(int slot, float delta, nint playerAddress, Vector3 playerPos,
                          float playerRotation, uint territory,
                          Lumina.Excel.RowRef<Lumina.Excel.Sheets.ClassJob>? jobRow,
                          uint maxHp, uint currentHp, uint playerEntityId)
    {
        var rt = _slots[slot];
        var setting = _config!.SubNpcs[slot];

        // 不在场：只推进自动重召唤倒计时。
        if (rt.Npc is null || !rt.Npc.IsAlive)
        {
            if (rt.RespawnTimer > 0f)
            {
                rt.RespawnTimer -= delta;
                if (rt.RespawnTimer <= 0f && setting.Enabled
                    && ((setting.AutoRespawnAfterZoneChange ?? _config.AutoRespawnAfterZoneChange)
                        || rt.StartupSummonPending))
                {
                    // 启动召唤无视「换区自动重召唤」开关（那是换区通道的开关，见 69.20）；
                    // 手动送走的槽位两条通道都不走（Dismiss 已清零计时器）。
                    rt.StartupSummonPending = false;
                    Summon(slot, announce: false);
                }
            }
            return;
        }

        // ── 每帧验证她真的还活着（与主 NPC 同判据：创建她的同一张表）──
        var com = ClientObjectManager.Instance();
        var rawObj = com is null ? null : com->GetObjectByIndex(rt.Npc.ObjectIndex);
        if (rawObj is null || (nint)rawObj != rt.Npc.Address)
        {
            rt.InvalidFrames++;
            rt.Animator?.FreezeFor(2);

            if (rt.InvalidFrames > 60)
            {
                _log.Information("[SmartNpc] 副 NPC {0} 对象失效（换区或被清理），稍后自动回来。", slot + 1);
                NpcBalloon.Forget(rt.Npc.Address);
                rt.Clear();
                rt.RespawnTimer = (setting.AutoRespawnAfterZoneChange ?? _config.AutoRespawnAfterZoneChange)
                    ? Math.Clamp(_config.AutoRespawnDelay, 1f, 15f)
                    : 0f;
            }
            return;
        }

        rt.InvalidFrames = 0;
        rt.Animator?.TickFreeze();

        // 换区这一帧到此为止（角色即将被游戏销毁，别继续驱动）。
        if (rt.LastTerritory == 0)
        {
            rt.LastTerritory = territory;
        }
        else if (territory != rt.LastTerritory)
        {
            rt.LastTerritory = territory;
            rt.Behaviour?.NotifyZoneChanged();
            rt.Brain?.ResetScene();
            rt.Motion?.Reset();
            rt.Animator?.Silence();
            return;
        }

        var handle = rt.Npc;

        // ── 推进点亮（分帧状态机），点亮期间不驱动 ──
        if (rt.SpawnDraw is not null)
        {
            if (!rt.SpawnDraw.Step(handle, out _, out _))
                return;

            rt.SpawnDraw = null;
            NpcTargetable.Apply(
                (GameObject*)handle.Address,
                (GameObject*)playerAddress,
                setting.TargetableByPlayer ?? _config.TargetableByPlayer,
                forceTrace: false,
                ourIndex: handle.ObjectIndex);

            // v0.18.69.23 五轮：点亮刚完成 —— 模型重建可能已把名字缓冲盖回默认名，
            // 立刻补写一次（下一帧名牌就跟着配置名走）。
            rt.NameCheckTimer = 0f;
            rt.SettleTimer = SettleSeconds;
            rt.AnimWait    = 0f;
            return;
        }

        // 可选中 + 嗓音保持 + 气泡生命周期（各自喂各自的地址，互不干扰）。
        var npcGo   = (GameObject*)handle.Address;
        var playerGo = (GameObject*)playerAddress;
        NpcTargetable.Apply(npcGo, playerGo,
            setting.TargetableByPlayer ?? _config.TargetableByPlayer,
            ourIndex: handle.ObjectIndex);

        // 嗓音按槽位：跟随主 NPC 设置，或用槽位自己的嗓音编号（每帧保持，换装会冲掉）。
        var presetId = setting.VoiceInherit ? VoiceTuning.PresetId : Math.Clamp(setting.VoicePresetId, 0, 255);
        if (presetId > 0)
            NpcVoice.ApplyPreset((Character*)handle.Address, presetId);
        NpcBalloon.Tick((Character*)handle.Address);

        // ── 名字重申（v0.18.69.23 五轮，用户反馈「头上的 ID 没跟着她起的名字」）──
        // 召唤时只写一次会被后续的模型重建/换装盖掉：每 0.5s 读回名字缓冲核对，
        // 不等于配置名（留空 = 游戏分配的默认名）就补写。读比对是纯内存操作，开销可忽略。
        rt.NameCheckTimer -= delta;
        if (rt.NameCheckTimer <= 0f)
        {
            rt.NameCheckTimer = 0.5f;
            var wantedName = NpcNaming.EffectiveName(setting.Name, handle.GameAssignedName);
            if (!string.Equals(NpcNaming.ReadName(handle.Address), wantedName, StringComparison.Ordinal))
                NpcNaming.WriteName(handle.Address, wantedName);
        }

        if (rt.Motion is null || rt.Animator is null || rt.Brain is null || rt.Behaviour is null)
            return;

        // ── 决策 → 执行（与主 NPC 同一套三层；头部视线见下方 ApplyHeadLook）──
        var here       = NpcMotion.ToNumerics(npcGo->Position);
        var toPlayer   = playerPos - here;
        var playerDist = new Vector3(toPlayer.X, 0f, toPlayer.Z).Length();

        rt.Behaviour.TrackPlayerStillness(playerPos, delta);
        rt.Behaviour.PlayerInCombat = _condition?[ConditionFlag.InCombat] ?? false;
        rt.Behaviour.PlayerIsCrafting =
            (_condition?[ConditionFlag.Crafting] ?? false)
         || (_condition?[ConditionFlag.ExecutingCraftingAction] ?? false)
         || (_condition?[ConditionFlag.Gathering] ?? false)
         || (_condition?[ConditionFlag.ExecutingGatheringAction] ?? false)
         || (_condition?[ConditionFlag.Fishing] ?? false);

        rt.Behaviour.Update(playerDist, delta);

        var goal = rt.Behaviour.Goal(here, playerPos, playerRotation, playerDist);
        if (_playerSpeed >= MotionTuning.RunGaitThreshold)
            goal = goal with { Gait = Gait.奔跑 };

        // 站定时整个身体对准玩家（副 NPC 没有头部注视层，身体对准就是"看着你"）。
        var facing = MathF.Atan2(playerPos.X - here.X, playerPos.Z - here.Z);

        var actionBusy = rt.Animator.IsBusy;

        var settling = rt.SettleTimer > 0f;
        if (settling)
        {
            rt.SettleTimer -= delta;
            if (playerDist > 2.5f || _playerRawSpeed > 0.8f)
                rt.SettleTimer = 0f;
        }

        var animReady = rt.Animator.AnimationConfirmed;
        if (!animReady && rt.AnimWait < AnimWaitMaxSeconds)
            rt.AnimWait += delta;
        var waitingAnim = !animReady && rt.AnimWait < AnimWaitMaxSeconds;

        rt.Motion.Paused = settling || waitingAnim
                           || rt.Brain.IsSpeaking || actionBusy;

        if (rt.WasActionBusy && !actionBusy)
            rt.Animator.ResetLocomotionBookkeeping();
        rt.WasActionBusy = actionBusy;

        rt.Motion.SpeedScale = 1f;
        rt.Motion.PlayerPosition = playerPos;
        rt.Motion.PlayerRotation = playerRotation;

        rt.Motion.Update(handle, goal, facing);

        rt.Appearance?.TickRebuild();

        var turning = !rt.Motion.IsMoving && rt.Motion.TurningInPlace;
        rt.Animator.SetTurning(turning ? rt.Motion.TurnDirection : 0);
        rt.Animator.SetWalking(rt.Motion.IsMoving, rt.Motion.CurrentSpeed, rt.Motion.CurrentGait);
        rt.Animator.ApplyAuthority(rt.Motion.IsMoving, rt.Motion.CurrentSpeed, rt.Motion.CurrentGait);

        // 头部视线（v0.18.69.23 四轮）：与主 NPC 同位置（ApplyAuthority 之后）、同一套状态机。
        ApplyHeadLook(slot, delta, playerDist, playerPos, playerEntityId);

        // 待机小动作（与主 NPC 同一门控：玩家走时不插；闸门按槽位覆盖）。
        var blockWhileMoving = setting.BlockGesturesWhileMoving ?? _config.BlockGesturesWhileMoving;
        var playerMoving = _playerRawSpeed > 0.25f;
        var gestureGate = blockWhileMoving
            ? (!rt.Motion.IsMoving && !playerMoving)
            : !playerMoving;

        if (gestureGate && !rt.Animator.IsTurning && rt.Behaviour.WantsIdleGesture(delta))
        {
            rt.Animator.PlayIdleGesture((int)rt.Brain.CurrentPersona);
            if (blockWhileMoving)
                rt.Motion.Paused = true;
        }

        // 情境反应 + 闲聊（各自人格）。
        var lastScene = rt.Brain.CurrentScene;
        var scene = rt.Behaviour.PlayerInCombat ? NpcBrain.Scene.战斗
                  : rt.Behaviour.PlayerIsCrafting ? NpcBrain.Scene.劳作
                  : rt.Brain.IsSpeaking ? lastScene
                  : rt.Behaviour.StateElapsed > 30f ? NpcBrain.Scene.久站
                  : NpcBrain.Scene.平静;
        rt.Brain.CurrentScene = scene;

        // 槽位开 = 这个副 NPC 自己对情境反应（走她自己的大脑，与主 NPC 互不影响）。
        if (setting.SceneReactions)
            rt.Brain.UpdateSceneReaction(scene, delta);

        // ── 台词触发器（v0.18.69.23）：与主 NPC 同一套判定，状态按槽位独立 ──
        TickSubLineTriggers(slot, delta, playerAddress, playerPos, here, playerDist, playerRotation, jobRow, maxHp, currentHp);

        rt.Animator.UpdateLipSync(rt.Brain.IsSpeaking, delta);
        rt.Brain.Update(handle, playerPos, delta);
    }

    // ── v0.18.69.23 四轮：头部视线（与主 NPC ApplyHeadLook 同一套状态机，状态按槽位独立）──
    //
    // 核心 = 写 Character.TargetId 让游戏引擎自己做头/眼追踪（AnamCore.SetHeadTarget 同款）。
    // 优先级（与主 NPC v0.18.32 最终裁定一致）：
    //   ① 始终不注视玩家 → 完全不看；
    //   ② 站定时用头/视线看 = 纯移动闸门（走动收回视线）；
    //   ③ 始终注视玩家 = 注视频率（勾=一直看；不勾=占视周期，整段保持不逐帧重掷）。
    // 全部旋钮读槽位覆盖（null = 主配置值）。

    private static readonly Random GazeRng = new();

    private void ApplyHeadLook(int slot, float delta, float playerDist, Vector3 playerPos, uint playerEntityId)
    {
        var rt = _slots[slot];
        var s  = _config!.SubNpcs[slot];

        if (rt.Npc is not { IsAlive: true } handle || handle.Address == nint.Zero)
            return;

        var character = (Character*)handle.Address;
        var go        = (GameObject*)handle.Address;

        // ①「始终不注视玩家」：最高优先级，直接完全关闭注视。
        if (s.NeverGazeAtPlayer ?? _config.NeverGazeAtPlayer)
        {
            ClearHeadTarget(character);
            rt.OverflowMode   = 0;
            rt.OverflowTimer  = 0f;
            rt.TurningActive  = false;
            rt.TurningTimeout = 0f;
            rt.GazeOn         = true;
            rt.GazeTimer      = 0f;
            return;
        }

        var standing     = !(rt.Motion?.IsMoving ?? false);
        var standingGate = s.CameraHeadLook ?? _config.CameraHeadLook;

        // ② 移动闸门：勾「站定时看」时，走动一律收回视线。
        if (standingGate && !standing)
        {
            ClearHeadTarget(character);
            rt.OverflowMode   = 0;
            rt.OverflowTimer  = 0f;
            rt.TurningActive  = false;
            rt.TurningTimeout = 0f;
            rt.GazeOn         = true;
            rt.GazeTimer      = 0f;
            return;
        }

        // 距离闸门（所有注视模式共用）。
        if (playerDist > MotionTuning.HeadLookMaxDistance || playerEntityId == 0)
        {
            ClearHeadTarget(character);
            rt.OverflowMode  = 0;
            rt.OverflowTimer = 0f;
            rt.GazeOn        = true;
            rt.GazeTimer     = 0f;
            return;
        }

        // ③ 注视频率：「始终注视玩家」勾 = 一直看；不勾 = 占视周期。
        if (!(s.AlwaysGazeAtPlayer ?? _config.AlwaysGazeAtPlayer))
        {
            rt.GazeTimer -= delta;
            if (rt.GazeTimer <= 0f)
            {
                var lookPct = Math.Clamp(s.GazeLookPercent ?? _config.GazeLookPercent, 0f, 100f);
                var lookMin = MathF.Max(0.5f, s.GazeLookMin ?? _config.GazeLookMin);
                var lookMax = MathF.Max(lookMin, s.GazeLookMax ?? _config.GazeLookMax);
                var awayMin = MathF.Max(0.5f, s.GazeAwayMin ?? _config.GazeAwayMin);
                var awayMax = MathF.Max(awayMin, s.GazeAwayMax ?? _config.GazeAwayMax);

                if (GazeRng.Next(100) < lookPct)
                {
                    rt.GazeOn    = true;
                    rt.GazeTimer = lookMin + (float)GazeRng.NextDouble() * (lookMax - lookMin);
                }
                else
                {
                    rt.GazeOn    = false;
                    rt.GazeTimer = awayMin + (float)GazeRng.NextDouble() * (awayMax - awayMin);
                }
            }

            if (!rt.GazeOn)
            {
                ClearHeadTarget(character);
                rt.OverflowMode   = 0;
                rt.OverflowTimer  = 0f;
                rt.TurningActive  = false;   // 故意不看 → 进行中的转身也一并收掉
                rt.TurningTimeout = 0f;
                return;
            }
        }

        // 她当前的朝向，与"要看向玩家"的朝向，差多少。
        var toPlayer = MathF.Atan2(playerPos.X - go->Position.X, playerPos.Z - go->Position.Z);
        var diff     = NormalizeAngle(toPlayer - go->Rotation);
        var absDelta = MathF.Abs(diff);

        // 头/眼转幅：度 → 弧度。
        var headTurnLimit = MathF.Max(0.1f, (s.HeadTurnLimitDeg ?? _config.HeadTurnLimitDeg) * MathF.PI / 180f);

        // 移动中绝不转身追：头/眼够得着就追、够不着就收回。
        if (!standing)
        {
            if (absDelta <= headTurnLimit)
                SetHeadTarget(character, playerEntityId);
            else
                ClearHeadTarget(character);

            rt.OverflowMode  = 0;
            rt.OverflowTimer = 0f;
            return;
        }

        if (!rt.TurningActive)
        {
            if (absDelta <= headTurnLimit)
            {
                // 头/眼够得着：交给游戏引擎自己追踪。
                SetHeadTarget(character, playerEntityId);
                rt.OverflowMode  = 0;
                rt.OverflowTimer = 0f;
                return;
            }

            // 超出头/眼转幅：随机二选一（放弃追踪 / 转身追），决定保持住不逐帧重掷。
            if (rt.OverflowTimer <= 0f)
            {
                var turnChance = Math.Clamp(s.TurnChancePercent ?? _config.TurnChancePercent, 0f, 100f);
                rt.OverflowMode  = GazeRng.Next(100) < turnChance ? 2 : 1;
                rt.OverflowTimer = MotionTuning.HeadOverflowHoldSeconds;
                rt.OverflowDir   = diff > 0f ? 1 : -1;
            }

            rt.OverflowTimer -= delta;

            if (rt.OverflowMode == 2)
            {
                rt.TurningActive  = true;
                rt.TurningTimeout = MotionTuning.TurnMaxSeconds;
            }
        }
        else
        {
            // 已经在转身：一直转到真的对上为止（迟滞），超时兜底防死循环。
            rt.OverflowMode = 2;
            rt.OverflowDir  = diff > 0f ? 1 : -1;

            rt.TurningTimeout -= delta;

            if (absDelta < MotionTuning.TurnDoneThreshold || rt.TurningTimeout <= 0f)
            {
                rt.TurningActive  = false;
                rt.OverflowMode   = 0;
                rt.OverflowTimer  = 0f;
                rt.TurningTimeout = 0f;
            }
        }

        if (rt.OverflowMode == 1)
            ClearHeadTarget(character);                 // ① 放弃追踪
        else
            SetHeadTarget(character, playerEntityId);   // ② 转身追
    }

    /// <summary>照 AnamCore.SetHeadTarget：设 TargetId，游戏引擎自动做头/眼追踪。</summary>
    private static void SetHeadTarget(Character* character, uint entityId)
    {
        character->TargetId.ObjectId = entityId;
        character->TargetId.Type     = 0;
    }

    /// <summary>照 AnamCore.ClearHeadTarget：置 0xE0000000，头/眼回正。</summary>
    private static void ClearHeadTarget(Character* character)
    {
        character->TargetId.ObjectId = 0xE0000000;
        character->TargetId.Type     = 0;
    }

    // ── 动作试做（副页「动作」小节用，与主 NPC 页同交互）──

    /// <summary>槽位动作表（没召唤 = 空表）。</summary>
    public IReadOnlyList<string> AvailableActionsOf(int slot)
        => slot >= 0 && slot < _slots.Length
            ? _slots[slot].Animator?.AvailableActions ?? Array.Empty<string>()
            : Array.Empty<string>();

    /// <summary>槽位的动画器（动作区状态显示用；不在场也可能刚建好 = 非 null）。</summary>
    public NpcAnimator? AnimatorOf(int slot)
        => slot >= 0 && slot < _slots.Length ? _slots[slot].Animator : null;

    /// <summary>对副 NPC 播一个动作（名字 = Emote 表的客户端本地化名）。</summary>
    public void PlayAction(int slot, string name)
    {
        if (slot < 0 || slot >= _slots.Length || string.IsNullOrWhiteSpace(name))
            return;
        _slots[slot].Animator?.Play(name);
    }

    /// <summary>停止副 NPC 当前正在播的动作。</summary>
    public void StopAction(int slot)
    {
        if (slot < 0 || slot >= _slots.Length)
            return;
        _slots[slot].Animator?.Stop();
    }

    /// <summary>
    /// 把在场且「允许被选中」的副 NPC 填进点击兜底的候选列表（v0.18.69.23 四轮）。
    /// 由主控制器每帧调用；返回填进去的数量。
    /// </summary>
    public int FillTargetableList(GameObject** list, int capacity)
    {
        if (_config is null || list is null || capacity <= 0)
            return 0;

        var n = 0;
        for (var i = 0; i < _slots.Length && i < _config.SubNpcs.Count && n < capacity; ++i)
        {
            var rt = _slots[i];
            if (rt.Npc is { IsAlive: true }
                && (_config.SubNpcs[i].TargetableByPlayer ?? _config.TargetableByPlayer))
                list[n++] = (GameObject*)rt.Npc.Address;
        }
        return n;
    }

    // ── v0.18.69.23：按槽位的台词触发器（天气/时段/职业/战斗互动/表情回应）──
    //
    // 判定逻辑与主控制器同名触发器一致；区别：
    //   · 状态记在槽位运行时里（各自的"上次天气 / 上次职业 / 注视累计"…）；
    //   · 各类开关读槽位设置（主 NPC 读主配置）；
    //   · 表情回应要求玩家当前目标**是这个副 NPC**（同一条硬目标判据）。

    private void TickSubLineTriggers(int slot, float delta, nint playerAddress,
                                      Vector3 playerPos, Vector3 here, float playerDist,
                                      float playerRotation,
                                      Lumina.Excel.RowRef<Lumina.Excel.Sheets.ClassJob>? jobRow,
                                      uint maxHp, uint currentHp)
    {
        var rt      = _slots[slot];
        var setting = _config!.SubNpcs[slot];
        var brain   = rt.Brain;
        var anim    = rt.Animator;
        if (brain is null || rt.Npc is not { IsAlive: true })
            return;

        // ── 天气 / 时段（NpcEnvironment 由主循环每帧取样，这里只判"变了没有"）──
        var weather = NpcEnvironment.Weather;
        var phase   = NpcEnvironment.Phase;

        if (!rt.EnvPrimed)
        {
            rt.EnvPrimed   = true;
            rt.SeenWeather = weather;
            rt.SeenPhase   = phase;
        }
        else
        {
            if (weather != rt.SeenWeather)
            {
                rt.SeenWeather = weather;

                var special   = weather is NpcLines.Weather.其他 or NpcLines.Weather.未知;
                var allowKind = !special || setting.SpecialWeatherLines;

                if (allowKind && setting.WeatherLines)
                {
                    var noun = NpcEnvironment.WeatherNoun;
                    var said = setting.WeatherNameLines
                            && !string.IsNullOrWhiteSpace(noun)
                            && brain.OnWeatherName(noun);

                    if (!said && !special)
                        brain.OnWeather(weather);
                }
            }

            if (phase != rt.SeenPhase)
            {
                rt.SeenPhase = phase;
                if (setting.DayPhaseLines)
                    brain.OnDayPhase(phase);
            }
        }

        // ── 职业（0.4 秒看一次，切换后只说一次）──
        rt.JobCheckTimer -= delta;
        if (rt.JobCheckTimer <= 0f)
        {
            rt.JobCheckTimer = 0.4f;

            try
            {
                if (jobRow is { } row)
                {
                    var jobId = row.RowId;

                    if (jobId != 0)
                    {
                        if (!rt.JobPrimed)
                        {
                            rt.JobPrimed = true;
                            rt.LastJobId = jobId;
                        }
                        else if (jobId != rt.LastJobId)
                        {
                            var changed = rt.LastJobId != 0;
                            rt.LastJobId = jobId;

                            if (changed && setting.JobLines)
                            {
                                var jobName = JobNameOf(row);
                                var job     = NpcJobLines.ResolveJob(jobId, jobName);

                                var jobPool = NpcJobLines.Lines(brain.CurrentPersona, job)
                                        ?? NpcJobLines.RoleLines(brain.CurrentPersona, NpcJobLines.RoleOf(job))
                                        ?? NpcJobLines.GenericLines(brain.CurrentPersona);

                                brain.OnJob(job, jobPool);
                            }
                        }
                    }
                }
            }
            catch { /* 职业表读不到就算了，0.4 秒后再试 */ }
        }

        // ── 战斗阶段 + 玩家互动（血量已由外层每帧取样传入）──
        maxHp = Math.Max(1u, maxHp);
        var hpPercent = Math.Clamp((float)currentHp / maxHp, 0f, 1f);

        if (!rt.HasHp)
        {
            rt.HasHp         = true;
            rt.LastHpPercent = hpPercent;
        }

        var drop = rt.LastHpPercent - hpPercent;

        var inCombat    = rt.Behaviour?.PlayerInCombat ?? false;
        var combatLines = setting.CombatLines;

        if (inCombat && !rt.WasInCombat)
        {
            if (combatLines)
                brain.OnCombat(NpcLines.Combat.进入);
            rt.CombatChatTimer = 9f;
        }
        else if (!inCombat && rt.WasInCombat)
        {
            if (combatLines)
                brain.OnCombat(hpPercent < 0.5f ? NpcLines.Combat.撤退 : NpcLines.Combat.胜利);
        }
        else if (inCombat)
        {
            rt.CombatChatTimer -= delta;
            if (rt.CombatChatTimer <= 0f)
            {
                rt.CombatChatTimer = 14f;
                if (combatLines)
                    brain.OnCombat(Random.Shared.Next(2) == 0
                        ? NpcLines.Combat.攻击
                        : NpcLines.Combat.称赞);
            }
        }

        rt.WasInCombat = inCombat;

        // 掉血 → 关心（一次掉 12% 以上才算"挨了一下"）。
        if (drop >= 0.12f && setting.TouchLines)
            brain.OnTouch(NpcLines.Touch.受伤关心, 25f);

        rt.LastHpPercent = hpPercent;

        // 玩家把她选成目标（Dalamud 托管目标管理器，与主 NPC 同源）。
        var targeted = Plugin.TargetManager is { } tm
                    && tm.Target is { } t
                    && t.Address == rt.Npc.Address;

        if (targeted && !rt.WasTargeted && setting.TouchLines)
            brain.OnTouch(NpcLines.Touch.被选中, 15f);

        rt.WasTargeted = targeted;

        // 被注视 / 被靠近（转身路过的不算）。
        var toNpcYaw = MathF.Atan2(here.X - playerPos.X, here.Z - playerPos.Z);
        var facing   = MathF.Abs(NormalizeAngle(toNpcYaw - playerRotation)) < 0.6f;
        var still    = _playerRawSpeed < 0.25f;
        var inRange  = playerDist is > 0.6f and < 8f;

        if (facing && still && inRange)
            rt.GazeHold += delta;
        else
            rt.GazeHold = 0f;

        var closeDist = Math.Clamp(setting.CloseTouchDistance, 0.05f, 8f);

        if (still && playerDist < closeDist)
            rt.CloseHold += delta;
        else
            rt.CloseHold = 0f;

        if (rt.CloseHold > 3f)
        {
            rt.CloseHold = 0f;
            rt.GazeHold  = 0f;
            if (setting.TouchLines)
                brain.OnTouch(NpcLines.Touch.被靠近, 60f);
        }
        else if (rt.GazeHold > 4f)
        {
            rt.GazeHold = 0f;
            if (setting.TouchLines)
                brain.OnTouch(NpcLines.Touch.被注视, 45f);
        }

        // ── 表情回应（必须玩家当前目标是她；0.15 秒探一次）──
        rt.EmoteReactCooldown -= delta;
        rt.EmoteProbeTimer    -= delta;
        if (rt.EmoteProbeTimer > 0f || !setting.EmoteReactions)
            return;

        rt.EmoteProbeTimer = 0.15f;

        ushort emoteId;
        try { emoteId = ((Character*)playerAddress)->EmoteController.EmoteId; }
        catch { return; }

        if (emoteId == 0 || emoteId == rt.LastPlayerEmoteId)
        {
            rt.LastPlayerEmoteId = emoteId;
            return;
        }

        rt.LastPlayerEmoteId = emoteId;

        if (!IsPlayerTargeting(rt.Npc.Address) || rt.EmoteReactCooldown > 0f)
            return;

        var name = NpcAnimator.EmoteNames.TryGetValue(emoteId, out var n) ? n : string.Empty;

        if (NpcEmoteReactions.IsIgnored(name))
            return;

        var kind = NpcEmoteReactions.KindFor(name);

        var pool = NpcEmoteReactions.Lines(brain.CurrentPersona, kind)
                ?? NpcEmoteReactions.Lines(brain.CurrentPersona, NpcLines.React.通用);

        if (pool is null)
            return;

        rt.EmoteReactCooldown = 3f;
        brain.OnEmote(name, pool);

        // 回应走语义映射表（与主 NPC 同一套，绝不做盲镜像）。
        var respName = NpcEmoteReactions.ResponseEmoteNameFor(name);
        var respRow  = anim?.FindEmoteRow(respName) ?? 0;

        var respOk = respRow != 0
                     && anim is not null
                     && anim.PlayResponseEmote(respRow, anim.EmoteLoopTimelineOf(respRow));

        if (!respOk)
        {
            var resolved = anim?.ResolveActionName(NpcEmoteReactions.GestureFor(kind));
            if (resolved is not null && anim is not null)
                anim.Play(resolved, NpcGesture.DurationFor(resolved));
        }
    }

    /// <summary>玩家的硬目标是不是这个地址（与主 NPC 同判据，TargetSystem 直比地址）。</summary>
    private static bool IsPlayerTargeting(nint address)
    {
        try
        {
            var ts = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
            if (ts is null)
                return false;

            var hard = ts->Target;
            return hard is not null && (nint)hard == address;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把角度差折到 -π ~ +π。</summary>
    private static float NormalizeAngle(float a)
    {
        while (a > MathF.PI)  a -= 2f * MathF.PI;
        while (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
    }

    /// <summary>行号 → 职业名（三层兜底前的原始名，与主 NPC 同源）。</summary>
    private static string JobNameOf(Lumina.Excel.RowRef<Lumina.Excel.Sheets.ClassJob> row)
    {
        try
        {
            var s = row.Value.Name.ToString();
            return s ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>插件卸载：全部送走。</summary>
    public void Dispose() => DismissAll();

    /// <summary>一个槽位的运行时栈（与主 NPC 同源组件，各持一份）。</summary>
    private sealed class SubNpcRuntime
    {
        public NpcHandle? Npc;
        public NpcAppearance? Appearance;
        public NpcMotion? Motion;
        public NpcAnimator? Animator;
        public NpcBrain? Brain;
        public NpcBehavior? Behaviour;
        public NpcFactory.SpawnDrawState? SpawnDraw;

        public float SettleTimer;
        public float AnimWait;
        public bool WasActionBusy;
        public int InvalidFrames;
        public uint LastTerritory;
        public float RespawnTimer;

        /// <summary>
        /// 这次的自动重召唤来自「启动自动召唤」（无视换区重召唤开关）。
        /// 手动送走 / 换区判死都会清掉 —— 只有启动武装的那一次有效。
        /// </summary>
        public bool StartupSummonPending;

        // 变更侦测（ApplySettings 即时生效用）。
        public int SeenPersona = -1;
        public int SeenIdleRow = -1;
        public Guid SeenDesignId = Guid.Empty;
        public string? SeenName;

        // 台词触发器状态（v0.18.69.23，与主 NPC 同名状态各自的份）。
        public bool EnvPrimed;          // 天气/时段首次取样
        public NpcLines.Weather SeenWeather;
        public NpcLines.DayPhase SeenPhase;
        public bool JobPrimed;          // 职业首次取样
        public uint LastJobId;
        public float JobCheckTimer;     // 0.4s 节流
        public bool WasInCombat;
        public float CombatChatTimer;
        public bool HasHp;
        public float LastHpPercent = 1f;
        public bool WasTargeted;
        public float GazeHold;
        public float CloseHold;
        public ushort LastPlayerEmoteId;
        public float EmoteProbeTimer;   // 0.15s 节流
        public float EmoteReactCooldown;

        // ── 头部视线状态机（v0.18.69.23 四轮，与主 NPC 同名状态各自的份）──
        // 0=没超转幅 1=放弃追踪 2=转身追（身体转）。
        public int OverflowMode;
        public float OverflowTimer;
        public int OverflowDir;
        public bool GazeOn = true;      // 「始终注视玩家」不勾时的占视状态
        public float GazeTimer;
        public bool TurningActive;      // 转身迟滞：转到位或超时前不掐
        public float TurningTimeout;

        // ── 名字重申（v0.18.69.23 五轮）──
        // 名字缓冲会被点亮/模型重建盖回默认名：每 0.5s 核对一次，被盖就补写。
        public float NameCheckTimer;

        /// <summary>清空运行时（槽位配置保留）。</summary>
        public void Clear()
        {
            Npc        = null;
            Appearance = null;
            Motion     = null;
            Animator   = null;
            Brain      = null;
            Behaviour  = null;
            SpawnDraw  = null;
            SettleTimer    = 0f;
            AnimWait       = 0f;
            WasActionBusy  = false;
            InvalidFrames  = 0;
            LastTerritory  = 0;

            // 启动召唤的一次性武装到此为止（换区判死后走「换区自动重召唤」通道）。
            StartupSummonPending = false;

            // 台词触发器状态一并重置 —— 重召唤后从"首次取样"重新开始。
            EnvPrimed  = false;
            JobPrimed  = false;
            WasInCombat = false;
            HasHp       = false;
            WasTargeted = false;
            GazeHold    = 0f;
            CloseHold   = 0f;
            LastPlayerEmoteId = 0;
            EmoteReactCooldown = 0f;

            // 视线状态一并重置（与主 NPC 语义一致：新的一局从"在看你"开始）。
            OverflowMode   = 0;
            OverflowTimer  = 0f;
            OverflowDir    = 0;
            GazeOn         = true;
            GazeTimer      = 0f;
            TurningActive  = false;
            TurningTimeout = 0f;
            NameCheckTimer = 0f;
        }
    }
}
