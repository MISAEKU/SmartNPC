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
            cfg.TargetableByPlayer,
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
        rt.RespawnTimer = 0f;   // 手动送走：取消等待中的自动重召唤

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

        s.Name              = main.NpcName;
        s.Persona           = main.Persona;
        s.GlamourerDesignId = main.GlamourerDesignId;
        s.PlayerTitle       = main.PlayerTitle;
        s.IdleEmoteRowId    = main.IdleEmoteRowId;
        s.IdleEmoteName     = main.IdleEmoteName;
        // FollowSide / FollowBehind 保留槽位自己的值 —— 三个副 NPC 都抄过去会叠在同一格。

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

            // 在场的槽位：这些字段变了就立刻推（召 Summon 走 forcePushIdle 全量推）。
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

            if (rt.Behaviour is not null)
            {
                rt.Behaviour.FollowSide   = s.FollowSide;
                rt.Behaviour.FollowBehind = FollowDistanceFor((NpcBrain.Persona)s.Persona, s.FollowBehind);
            }

            if (rt.Brain is not null)
            {
                rt.Brain.PlayerTitle = string.IsNullOrWhiteSpace(s.PlayerTitle)
                    ? NpcLines.DefaultTitle
                    : s.PlayerTitle.Trim();
            }
        }
    }

    /// <summary>把一个槽位的全部运行参数推下去（召唤时全量推；ApplySettings 走变更侦测）。</summary>
    private void ApplySlotSettings(int slot, bool forcePushIdle)
    {
        if (_config is null)
            return;

        var rt = _slots[slot];
        var s  = _config.SubNpcs[slot];

        if (rt.Motion is not null)
        {
            rt.Motion.SpeedScale        = _config.SpeedScale;
            rt.Motion.TeleportDistance  = _config.TeleportDistance;
            rt.Motion.TeleportHeight    = _config.TeleportHeight;
            rt.Motion.AvoidanceEnabled  = _config.AvoidanceEnabled;
            rt.Motion.StuckTeleportSeconds = _config.StuckTeleportSeconds;
            rt.Motion.DetourDuration    = _config.DetourDuration;
            rt.Motion.DetourCooldown    = _config.DetourCooldown;
            rt.Motion.FollowStrength    = _config.FollowStrength;
            rt.Motion.WalkLagDistance   = _config.WalkDistance;
            rt.Motion.SpawnOffsetBehind = _config.SpawnOffsetBehind;
            rt.Motion.SpawnOffsetSide   = s.FollowSide;   // 召唤落点用槽位的侧偏
            if (rt.Animator is not null)
                rt.Motion.JumpEnabled = rt.Animator.HasJumpAnim;
        }

        if (rt.Behaviour is not null)
        {
            rt.Behaviour.Enabled         = BrainTuning.BehaviourEnabled;
            rt.Behaviour.RunDistance     = BehaviourTuning.RunDistance;
            rt.Behaviour.WalkDistance    = BehaviourTuning.WalkDistance;
            rt.Behaviour.StrayDistance   = BehaviourTuning.StrayDistance;
            rt.Behaviour.ArriveDistance  = Math.Clamp(_config.ArriveDistance, 0.5f, 8f);
            rt.Behaviour.RoamRange       = Math.Clamp(_config.RoamRange * 0.6f, 1f, 20f);   // 副 NPC 游荡圈收小一点
            rt.Behaviour.RoamMin         = Math.Clamp(_config.RoamMin, 0.5f, 8f);
            rt.Behaviour.FollowBehind    = FollowDistanceFor((NpcBrain.Persona)s.Persona, s.FollowBehind);
            rt.Behaviour.FollowSide      = s.FollowSide;
        }

        if (rt.Brain is not null)
        {
            rt.Brain.AutoChatter    = BrainTuning.AutoChatter;
            rt.Brain.MinIdleSeconds = BrainTuning.MinIdleSeconds;
            rt.Brain.MaxIdleSeconds = BrainTuning.MaxIdleSeconds;
            rt.Brain.ShowInChat     = BrainTuning.ShowInChat;
            rt.Brain.Name = NpcNaming.EffectiveName(s.Name, "她");
            rt.Brain.PlayerTitle = string.IsNullOrWhiteSpace(s.PlayerTitle)
                ? NpcLines.DefaultTitle
                : s.PlayerTitle.Trim();
            rt.Brain.LineCooldownSeconds = Math.Max(0f, _config.LineCooldownSeconds);
            rt.Brain.NearChatDistance    = Math.Clamp(_config.CloseTouchDistance, 0.3f, 8f);
        }

        if (rt.Animator is not null)
        {
            rt.Animator.LipSyncEnabled = _config.LipSyncEnabled;
            rt.Animator.LipFlapSeconds = Math.Clamp(_config.LipFlapSeconds, 0.05f, 0.6f);
            rt.Animator.WalkSpeedScale    = _config.WalkSpeedScale;
            rt.Animator.TurnPlaybackSpeed = _config.TurnPlaybackSpeed;
            rt.Animator.IdleRelaxSeconds  = Math.Clamp(_config.IdleRelaxSeconds, 0f, 600f);
            rt.Animator.IdleSwitchSeconds = Math.Clamp(_config.IdleSwitchSeconds, 5f, 600f);
            rt.Animator.IdleAutoRandom    = false;   // 副 NPC 不自动随机换姿势（v1 简化，姿势跟槽位设置走）
        }

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

        for (var i = 0; i < _slots.Length; ++i)
        {
            if (i >= _config.SubNpcs.Count)
                break;   // Sanitize 还没跑过（理论上不该发生），别越界
            TickSlot(i, delta, player, playerPos, territory);
        }
    }

    private void TickSlot(int slot, float delta, IGameObject player, Vector3 playerPos, uint territory)
    {
        var rt = _slots[slot];
        var setting = _config!.SubNpcs[slot];

        // 不在场：只推进自动重召唤倒计时。
        if (rt.Npc is null || !rt.Npc.IsAlive)
        {
            if (rt.RespawnTimer > 0f)
            {
                rt.RespawnTimer -= delta;
                if (rt.RespawnTimer <= 0f && setting.Enabled && _config.AutoRespawnAfterZoneChange)
                    Summon(slot, announce: false);
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
                rt.RespawnTimer = _config.AutoRespawnAfterZoneChange
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
                (GameObject*)player.Address,
                _config.TargetableByPlayer,
                forceTrace: false,
                ourIndex: handle.ObjectIndex);
            rt.SettleTimer = SettleSeconds;
            rt.AnimWait    = 0f;
            return;
        }

        // 可选中 + 嗓音保持 + 气泡生命周期（各自喂各自的地址，互不干扰）。
        var npcGo   = (GameObject*)handle.Address;
        var playerGo = (GameObject*)player.Address;
        NpcTargetable.Apply(npcGo, playerGo, _config.TargetableByPlayer, ourIndex: handle.ObjectIndex);
        if (VoiceTuning.PresetId > 0)
            NpcVoice.ApplyPreset((Character*)handle.Address, VoiceTuning.PresetId);
        NpcBalloon.Tick((Character*)handle.Address);

        if (rt.Motion is null || rt.Animator is null || rt.Brain is null || rt.Behaviour is null)
            return;

        // ── 决策 → 执行（与主 NPC 同一套三层，副 NPC 没有头/眼注视层）──
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

        var goal = rt.Behaviour.Goal(here, playerPos, player.Rotation, playerDist);
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
        rt.Motion.PlayerRotation = player.Rotation;

        rt.Motion.Update(handle, goal, facing);

        rt.Appearance?.TickRebuild();

        var turning = !rt.Motion.IsMoving && rt.Motion.TurningInPlace;
        rt.Animator.SetTurning(turning ? rt.Motion.TurnDirection : 0);
        rt.Animator.SetWalking(rt.Motion.IsMoving, rt.Motion.CurrentSpeed, rt.Motion.CurrentGait);
        rt.Animator.ApplyAuthority(rt.Motion.IsMoving, rt.Motion.CurrentSpeed, rt.Motion.CurrentGait);

        // 待机小动作（与主 NPC 同一门控：玩家走时不插）。
        var blockWhileMoving = _config.BlockGesturesWhileMoving;
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

        if (_config.SceneReactions)
            rt.Brain.UpdateSceneReaction(scene, delta);

        rt.Animator.UpdateLipSync(rt.Brain.IsSpeaking, delta);
        rt.Brain.Update(handle, playerPos, delta);
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

        // 变更侦测（ApplySettings 即时生效用）。
        public int SeenPersona = -1;
        public int SeenIdleRow = -1;
        public Guid SeenDesignId = Guid.Empty;
        public string? SeenName;

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
        }
    }
}
