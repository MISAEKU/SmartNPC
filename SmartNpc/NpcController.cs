using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using SmartNpc.Core;

namespace SmartNpc;

/// <summary>
/// NPC 的整体编排：生成 / 每帧驱动 / 销毁。
///
/// 分工（四层，每层只干一件事）：
///   · <see cref="NpcFactory"/>   —— 造人与销毁（游戏接口）
///   · <see cref="NpcAppearance"/>—— 外观写入与换装
///   · <see cref="NpcBehavior"/>  —— 决策：去哪、用什么姿态（产出世界坐标目标）
///   · <see cref="NpcMotion"/>    —— 执行：位移、加减速、转向、动画状态
///   · <see cref="NpcAnimator"/>  —— 表情与动作
///   · <see cref="NpcBrain"/>     —— 人格、台词、说话
///
/// ⚠️ 分层不是为了好看，是因为旧版把决策和执行揉在一起时，
/// 决策刚算出的"想去哪"会被下一帧的跟随公式覆盖掉 —— 自主性一个字也写不进去。
/// </summary>
public sealed unsafe class NpcController : IDisposable
{
    private IPluginLog _log;

    private NpcHandle? _npc;

    // ── 玩家实测速度（v0.13.0）──
    // 移动层的基速由它派生，于是"你走她小走、你跑她小跑"。
    // 只取水平分量：斜坡/跳台上的垂直速度不代表走路快慢。
    private Vector3 _lastPlayerPos;
    private bool    _hasPlayerPos;
    private float   _playerSpeed;

    // ── 玩家跳跃检测（v0.18.8 demo）──
    private float _lastPlayerY;      // 玩家上一帧 Y（算垂直速度用）
    private bool  _hasPlayerY;
    private float _playerVySmooth;   // 玩家垂直速度（轻平滑）
    private bool  _wasPlayerGrounded = true;   // 玩家上一帧是否在地面（离地判定用，v0.18.10）
    private NpcFactory.SpawnDrawState? _spawnDraw;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private float _lastTime;
    private float _delta;

    // ── 站稳窗口（v0.18.17，修「换区后她出现时立刻跟随会平移一段距离」）──
    //
    // 现象：自动重召唤点亮刚结束，如果玩家立刻走动触发跟随，她会以"没有走路
    // 动画"的姿势平移一小段，之后才恢复正常 —— 模型刚重建完的那个短窗口里，
    // 位置在写、走路动画还没接管（点亮期被 return 挡住不动，一点亮就立刻能动）。
    //
    // 修法：点亮结束后压 <see cref="SettleSeconds"/> 秒的"站稳窗口"，
    // 期间强制 <c>_motion.Paused = true</c>（站着、播待机），窗口过后才允许跟随。
    // 手动召唤同样经过点亮流程，一并受益。
    private float _settleTimer;

    /// <summary>点亮结束后的站稳时长（秒）。太短平移残留，太长"召唤了却不动"。</summary>
    private const float SettleSeconds = 1.2f;

    // ── 动画接管控闸（v0.18.21，真正治"站笔直滑行"）──
    //
    // slide-trace 实锤：站稳窗口 1.2s 到期后她开始位移，但**动画要到约 2.2s 才接管**
    // （模型重建期间我们写进 BaseOverride 的值每帧都被游戏冲掉，base 读回来是 0）。
    // 中间这 1 秒多她照常位移、没有走路动画 = 用户看到的"站的笔直的滑动"。
    //
    // 靠猜时间永远对不齐（模型加载快慢随机器/场景变），所以改成**闭环闸门**：
    // 写进去的值真的留住了（NpcAnimator.AnimationConfirmed）才允许位移。
    private float _animWait;

    /// <summary>最多等动画接管多久（秒）。到点放行，避免模型异常时她永远站那儿不动。</summary>
    private const float AnimWaitMaxSeconds = 4f;

    /// <summary>
    /// 平移取证剩余帧数（v0.18.18）。点亮结束后记 5 秒（300 帧）：
    /// 逻辑位置 / 绘制对象位置 / 玩家位置逐帧落盘 slide-trace.txt。
    /// <b>如果逻辑位置不动而绘制对象在滑 → 游戏自己的插值在作怪；
    /// 如果逻辑位置自己在动 → 移动层/瞬移在作怪</b>。两条路修法完全不同，
    /// v0.18.17 的站稳窗口没治好平移，就是因为缺这份分诊数据。
    /// </summary>
    private int _slideTraceFrames;


    /// <summary>
    /// 上一帧到这一帧的秒数。
    ///
    /// ⚠️ 这行曾经是个**恒等于 0 的 bug**，而且一次废掉三个功能：
    /// 动作的自动停止计时、自动闲聊的计时、动画自检的计时，全部因为 <c>delta == 0</c> 而永不推进。
    /// 表现为"动作停不下来""人格完全不生效"。
    /// 修法就一条：**先算 delta，再更新 _lastTime**，顺序不能反。
    /// </summary>
    public float FrameDelta => _delta;
    private NpcAppearance? _appearance;
    private bool _wasTeleported;

    /// <summary>上一次见到她时所在的区域。换区后要清掉行为层的"玩家静止"计时，
    /// 否则她会以为玩家一直没动，换区完立刻凑过来搭话。
    /// ⚠️ <c>NpcHandle.TerritoryId</c> 是 <c>uint</c>，字段类型要跟着用 uint。</summary>
    private uint _lastTerritory;

    /// <summary>对象表验证连续失败的帧数（v0.17.7）。连续 60 帧失败才判死。</summary>
    private int _invalidFrames;

    /// <summary>玩家瞬时速度（未平滑，米/秒）。打断动作判定用 —— 要的就是"一走就断"。</summary>
    private float _playerRawSpeed;

    private GlamourerBridge? _glamourer;
    private NpcMotion? _motion;
    private NpcAnimator? _animator;
    private NpcBrain? _brain;
    private NpcBehavior? _behaviour;

    /// <summary>v0.18.55：Penumbra 个人分组桥（召唤挂 / 送走解）。</summary>
    private Core.PenumbraBridge? _penumbra;

    /// <summary>行为层（面板与诊断用它读当前状态）。</summary>
    public NpcBehavior? Behaviour => _behaviour;

    /// <summary>
    /// 她的 <c>Character*</c> 地址（v0.18.37：面板「试听」要用它发声）。
    /// 不在场时为 <see cref="nint.Zero"/>。
    /// </summary>
    public nint NpcAddress => _npc is { IsAlive: true } h ? h.Address : nint.Zero;

    /// <summary>避障层（面板与诊断读它）。</summary>

    /// <summary>移动层（面板与诊断读它）。</summary>
    public NpcMotion? Motion => _motion;

    private IFramework?        _framework;
    private IObjectTable?      _objectTable;
    private IClientState?      _clientState;
    private IChatGui?          _chat;
    private Dalamud.Plugin.Services.IDataManager? _data;
    private Configuration? _config;
    private IDalamudPluginInterface? _pi;
    private ICondition? _condition;

    /// <summary>自建诊断文件（由 <c>Plugin</c> 注入）。Dalamud 日志到 100MB 就停写，不能依赖它。</summary>
    private DiagFile? _diag;

    /// <summary>诊断落盘通道。⚠️ setter 会同步转发给动画器 —— 姿势管线（PlayEmote 成败、
    /// 兜底切换）的关键行也要落盘，而 Serilog 信息行不进任何本机可读日志（v0.18.64）。</summary>
    public DiagFile? Diag
    {
        get => _diag;
        set
        {
            _diag = value;
            if (_animator is not null)
                _animator.Diag = value;
        }
    }

    /// <summary>帧计数，用于周期性落盘诊断。</summary>
    private int _frames;

    public NpcController(IPluginLog? log = null)
    {
        // ⚠️ 构造期 Dalamud 还没注入 [PluginService]，传进来的 log 完全可能是 null。
        // 这里**不能**直接 _log = log 然后就到处 _log.Information(...)——
        // 那会在"第一条日志"上炸掉，而构造函数抛异常 = 整个插件加载不出来。
        // 真实的 log 会在 Attach() 里补上（那时服务已经注入好了）。
        _log = log ?? NullLog.Instance;
        NpcFactory.DiagnosticLog = message => _log.Information("[SmartNpc] {0}", message);
    }

    /// <summary>
    /// 服务还没注入时的空日志：所有方法都是安全的空操作。
    ///
    /// 为什么不直接让 <c>_log</c> 可空然后到处判空？因为有 30 多处调用点，
    /// 散落的判空迟早会漏一处 —— 漏那一处就是插件加载失败。
    /// 换成一个"什么都不做"的对象，一处判空都不用加。
    ///
    /// ⚠️ <c>IPluginLog</c> 继承自 <c>ILog</c>，所以<b>五个级别</b>
    /// （Error/Warning/Information/Debug/Verbose）外加 <c>Info</c> 别名、
    /// <c>Write</c>、两个属性全都要实现，少一个都编译不过。
    /// 带 <c>Exception</c> 的重载签名是 <c>Exception?</c> + <c>params object[]</c>
    /// （不是 <c>object?[]</c>）—— 写错可空性只是警告，但数组元素类型不匹配会直接报错。
    /// </summary>
    private sealed class NullLog : IPluginLog
    {
        public static readonly NullLog Instance = new();
        private NullLog() { }

        public void Error(string messageTemplate, params object[] values) { }
        public void Error(Exception? exception, string messageTemplate, params object[] values) { }
        public void Fatal(string messageTemplate, params object[] values) { }
        public void Fatal(Exception? exception, string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Warning(Exception? exception, string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Information(Exception? exception, string messageTemplate, params object[] values) { }
        public void Debug(string messageTemplate, params object[] values) { }
        public void Debug(Exception? exception, string messageTemplate, params object[] values) { }
        public void Verbose(string messageTemplate, params object[] values) { }
        public void Verbose(Exception? exception, string messageTemplate, params object[] values) { }

        /// <summary>Serilog 的 Info 级别别名，与 <see cref="Information"/> 同一个出口。</summary>
        public void Info(string messageTemplate, params object[] values) { }
        public void Info(Exception? exception, string messageTemplate, params object[] values) { }

        public void Write(Serilog.Events.LogEventLevel level, Exception? exception,
                          string messageTemplate, params object[] values) { }

        public Serilog.ILogger Logger => Serilog.Log.Logger;

        /// <summary>接口要求可读写，所以给个带默认值的自动属性。</summary>
        public Serilog.Events.LogEventLevel MinimumLogLevel { get; set; } = Serilog.Events.LogEventLevel.Verbose;
    }

    /// <summary>绑定 Dalamud 服务并开始每帧驱动。</summary>
    public void Attach(IFramework framework, IObjectTable objectTable, IClientState clientState,
                       IChatGui chat, IDalamudPluginInterface pi, Dalamud.Plugin.Services.IDataManager data,
                       Configuration config, ICondition? condition = null, IPluginLog? log = null)
    {
        // 到这里服务已经注入完毕了：如果构造期拿到的 log 是空的（NullLog），
        // 就在这儿换成真的 —— 否则插件虽然能加载，但全程没有日志可查。
        if (log is not null) _log = log;

        _data   = data;
        _config = config;
        _framework   = framework;
        _objectTable = objectTable;
        _clientState = clientState;
        _chat        = chat;
        _pi          = pi;

        // 战斗 / 生产状态要靠它判断（"该不该凑过来搭话"）。
        // 没给也不致命：行为层会一直当成"平静"，退化成纯跟随。
        _condition = condition;

        // Glamourer 设计读取（只读磁盘，不注册 IPC，不进它的状态系统）。
        // 建失败不致命——只是"外观设计"这个功能用不了。
        try { _glamourer = new GlamourerBridge(pi, data, _log); }
        catch (Exception ex) { _log.Error(ex, "[SmartNpc] Glamourer 桥接初始化失败，外观设计功能不可用。"); }

        // v0.18.55：Penumbra 个人分组（引用官方 Penumbra.Api，只调用不注册）。
        // 建失败不致命——只是"单独 mod 分组"这个功能用不了。
        try { _penumbra = new Core.PenumbraBridge(pi, _log); }
        catch (Exception ex) { _log.Error(ex, "[SmartNpc] Penumbra 桥接初始化失败，分组功能不可用。"); }

        // v0.18.69.22：副 NPC（随从）管理器 —— 最多 3 个槽位，各持一套与主 NPC
        // 同源的运行栈。由本控制器的每帧回调在**所有主 NPC 提前 return 之前**驱动。
        SubNpcs = new SubNpcManager(_log);
        SubNpcs.Attach(objectTable, clientState, chat, data, config, condition, _glamourer);
        SubNpcs.ConfigSaveRequested = SaveConfig;

        _framework.Update += OnFrameworkUpdate;
    }

    /// <summary>副 NPC（随从）管理器（v0.18.69.22，Attach 后可用）。</summary>
    public SubNpcManager? SubNpcs { get; private set; }

    /// <summary>副 NPC 管理器要落存档时借道这里（SaveConfig 是私有的，包一层）。</summary>
    internal void SaveNow() => SaveConfig();

    /// <summary>副 NPC 管理器在服务未注入时用的空日志（NullLog 是私有的，包一层）。</summary>
    internal static IPluginLog NullLogForSub() => NullLog.Instance;

    public bool HasNpc => _npc is { IsAlive: true };

    /// <summary>动作/表情模块（供面板读它从 Emote 表载入的真实动作名）。</summary>
    public NpcAnimator? Animator => _animator;

    /// <summary>由 Plugin 接到 <c>SavePluginConfig</c>（配置对象本身不该管持久化）。</summary>
    public Action? ConfigSaveRequested { get; set; }

    private void SaveConfig() => ConfigSaveRequested?.Invoke();

    // ── v0.18.45 显示名同步（目标信息栏/名牌）──
    /// <summary>上一轮推送进 ApplySettings 的配置名 —— 用于侦测"玩家改了名"。</summary>
    private string _seenConfigName = string.Empty;

    /// <summary>已经对哪个对象地址写过年 —— 换区重召唤（地址变了）要重写一次。</summary>
    private nint _namedAddress;

    /// <summary>首次加载配置的那次变更不落盘（内容就是刚读出来的，存了也白存）。</summary>
    private bool _nameSyncPrimed;

    /// <summary>读不到就是空列表——面板据此显示"Glamourer 没装/没找到设计"。</summary>
    public IReadOnlyList<(Guid Id, string Name)> GetDesigns()
        => _glamourer?.GetDesigns() ?? new List<(Guid, string)>();

    public bool GlamourerAvailable => _glamourer is not null && _glamourer.GetDesigns().Count > 0;

    // ── v0.18.55 Penumbra 个人分组 ──

    /// <summary>Penumbra 的分组列表（没装/没开就是空列表，面板显示提示）。</summary>
    public IReadOnlyList<(Guid Id, string Name)> GetPenumbraCollections()
        => _penumbra?.GetCollections() ?? new List<(Guid, string)>();

    /// <summary>面板选中一个分组：记住 + 她在场就立刻挂上（带 redraw）。</summary>
    public bool ApplyPenumbraCollection(Guid id, string name, out string error)
    {
        error = string.Empty;

        if (_penumbra is null)
        {
            error = "Penumbra 桥接不可用。";
            return false;
        }

        if (!_penumbra.IsPenumbraRunning)
        {
            error = string.IsNullOrEmpty(_penumbra.LastError) ? "Penumbra 没在运行。" : _penumbra.LastError;
            return false;
        }

        _config.PenumbraCollectionId   = id;
        _config.PenumbraCollectionName = name ?? string.Empty;
        SaveConfig();

        if (_npc is { IsAlive: true } handle)
        {
            var (ok, msg) = _penumbra.AssignToObject(handle.ObjectIndex, id);
            if (ok)
                _penumbra.Redraw(handle.ObjectIndex);   // 她已经在场，补 redraw 让 mod 立刻生效
            else
                error = msg;

            _chat?.Print(ok
                ? $"[SmartNpc] 已把她挂进 Penumbra 分组「{name}」：{msg}"
                : $"[SmartNpc] 挂分组失败：{msg}");
            return ok;
        }

        _chat?.Print($"[SmartNpc] 已记住分组「{name}」，召唤时自动挂上。");
        return true;
    }

    /// <summary>面板选「不挂分组」：清配置 + 她在场就解绑。</summary>
    public void ClearPenumbraAssignment(out string error)
    {
        error = string.Empty;

        _config.PenumbraCollectionId   = Guid.Empty;
        _config.PenumbraCollectionName = string.Empty;
        SaveConfig();

        if (_npc is { IsAlive: true } handle && _penumbra is not null)
        {
            var (_, msg) = _penumbra.ClearObject(handle.ObjectIndex);
            _chat?.Print($"[SmartNpc] Penumbra {msg}。");
        }
    }

    /// <summary>她的当前分组状态描述（面板状态行用）。空 = 没挂/查不到。</summary>
    public string PenumbraAssignedName
        => _npc is { IsAlive: true } handle && _penumbra is not null
            ? _penumbra.GetAssignedName(handle.ObjectIndex) ?? string.Empty
            : string.Empty;

    // ── v0.18.57 可选待机姿势（玩家 /pose 的 6 个站立姿势）──

    /// <summary>候选清单缓存（不依赖召唤 —— 面板在她不在场时也要能选）。</summary>
    private List<NpcAnimator.IdleEmoteChoice>? _idleChoices;

    /// <summary>ApplySettings 已推给动画层的姿势序号（-1 = 还没推过；v0.18.58 变化才推）。</summary>
    private int _lastPushedIdleRow = -1;

    /// <summary>ApplySettings 已推给动画层的自动随机开关（和姿势一起做变化检测）。</summary>
    private bool _lastPushedIdleAuto;

    /// <summary>可选待机姿势候选（姿势一~六，= 玩家 /pose 全集）。</summary>
    public IReadOnlyList<NpcAnimator.IdleEmoteChoice> GetIdleEmotes()
    {
        if (_idleChoices is null)
        {
            try { _idleChoices = NpcAnimator.LoadIdleChoices(); }
            catch (Exception ex)
            {
                _log.Error(ex, "[SmartNpc] 待机姿势候选加载失败。");
                _idleChoices = new List<NpcAnimator.IdleEmoteChoice>();
            }
        }

        return _idleChoices;
    }

    /// <summary>设置待机动作（0 = 回游戏默认待机）。记住选择 + 她在场立刻生效。</summary>
    public bool SetIdleEmote(int rowId, string name, out string error)
    {
        error = string.Empty;

        var choice = rowId == 0 ? null : GetIdleEmotes().FirstOrDefault(c => c.EmoteRowId == rowId);
        if (rowId != 0 && choice is null)
        {
            error = "不认识的待机动作。";
            return false;
        }

        _config.IdleEmoteRowId = rowId;
        _config.IdleEmoteName  = name ?? string.Empty;
        _lastPushedIdleRow = rowId;   // 面板直推过了，别让 ApplySettings 再推一次
        SaveConfig();

        // 她不在场也没关系 —— 下次召唤 ApplySettings 会把配置推给动画层。
        _animator?.SetIdleEmote(choice?.PlayEmoteRow ?? 0,
                                 choice?.LoopTimeline ?? 0,
                                 (ushort)Math.Clamp(rowId, 0, ushort.MaxValue));
        return true;
    }

    /// <summary>套用一份 Glamourer 设计，并记住选择。</summary>
    public bool ApplyDesign(Guid id, string name, out string error)
    {
        error = string.Empty;

        if (!HasNpc || _appearance is null)
        {
            error = "先召唤她。";
            return false;
        }

        if (_glamourer is null)
        {
            error = "Glamourer 桥接不可用。";
            return false;
        }

        var design = _glamourer.ResolveDesign(id);
        if (design is null)
        {
            error = string.IsNullOrEmpty(_glamourer.LastError) ? "这个设计读不出来。" : _glamourer.LastError;
            return false;
        }

        _config.GlamourerDesignId = id;
        SaveConfig();

        _appearance.ApplyDesign(design);
        _log.Information("[SmartNpc] 已套用 Glamourer 设计「{0}」。", name);
        _chat?.Print($"[SmartNpc] 换上了「{name}」。");
        return true;
    }

    /// <summary>手填行走时间轴行号（0 = 交回给自动发现的结果）。</summary>
    public void SetWalkTimeline(int rowId)
    {
        if (_config is not null)
        {
            _config.WalkTimelineId = Math.Clamp(rowId, 0, ushort.MaxValue);
            SaveConfig();
        }

        if (_animator is null)
            return;

        _animator.WalkSpeedScale    = _config?.WalkSpeedScale ?? 1f;    // ⚠️ 之前读的是常量，配置项一直是死的
        _animator.TurnPlaybackSpeed = _config?.TurnPlaybackSpeed ?? 1f;
        _animator.RestoreAutoWalkTimeline(_config?.WalkTimelineId ?? 0);
        _log.Information("[SmartNpc] 行走时间轴设为 #{0}。", _config?.WalkTimelineId ?? 0);
    }

    /// <summary>把配置里记的设计套到她身上（召唤成功后调用）。</summary>
    private void ApplyConfiguredDesign()
    {
        var id = _config?.GlamourerDesignId ?? Guid.Empty;
        if (id == Guid.Empty || _glamourer is null)
            return;

        var design = _glamourer.ResolveDesign(id);
        if (design is null || _appearance is null)
            return;

        _appearance.ApplyDesign(design);
    }

    /// <summary>把设置窗口里的配置推给各个运行模块（即时生效，不需重新召唤）。</summary>
    public void ApplySettings(Configuration config)
    {
        if (_motion is not null)
        {
            // 速度分级。⚠️ 动画播放速度的分母（ReferenceWalkSpeed / ReferenceRunSpeed）
            // 必须和这两个值一致，否则会出现"脚在倒着走"。
            _motion.SpeedScale       = config.SpeedScale;
            _motion.TeleportDistance = config.TeleportDistance;
            _motion.TeleportHeight   = config.TeleportHeight;   // 高差瞬移阈值（面板可调）
            _motion.AvoidanceEnabled = config.AvoidanceEnabled;   // 墙体避障（v0.17.3 接活）

            // v0.18.2：卡住多久强制瞬移到玩家身边（0 = 关闭兜底）。
            _motion.StuckTeleportSeconds = config.StuckTeleportSeconds;

            // v0.18.3：承诺式绕行参数（这两项配置在 0.13.0 就存在，现在真正接上了）。
            _motion.DetourDuration = config.DetourDuration;
            _motion.DetourCooldown = config.DetourCooldown;

            // v0.18.4：跟随强度（引力）+ 出生点式瞬移落点。
            _motion.FollowStrength    = config.FollowStrength;
            _motion.WalkLagDistance   = config.WalkDistance;
            _motion.SpawnOffsetBehind = config.SpawnOffsetBehind;
            _motion.SpawnOffsetSide   = config.SpawnOffsetSide;

            // v0.17.13：没有跳跃动画就不许跳（宁撞墙也不飞而无骨）。
            _motion.JumpEnabled = _animator?.HasJumpAnim ?? false;
        }


        if (_brain is not null)
        {
            _brain.AutoChatter     = BrainTuning.AutoChatter;
            _brain.MinIdleSeconds  = BrainTuning.MinIdleSeconds;
            _brain.MaxIdleSeconds  = BrainTuning.MaxIdleSeconds;
            _brain.ShowInChat      = BrainTuning.ShowInChat;

            // v0.18.45：气泡/台词用的名字 —— 配置名留空回退"她"，不把空串塞进台词前缀。
            var shownName = Core.NpcNaming.EffectiveName(config.NpcName, "她");
            if (_brain.Name != shownName)
                _brain.Name = shownName;

            // v0.18.37 说话配音（真正的角色语音）：可变静态值，改完立刻生效（不等重新召唤）。
            Core.VoiceTuning.Enabled  = config.VoiceEnabled;
            Core.VoiceTuning.PresetId = Math.Clamp(config.VoicePresetId, 0, 255);
            Core.VoiceTuning.LineId   = Math.Clamp(config.VoiceLineId, 0, 9999);

            // v0.18.49 头顶气泡（同样是改完立刻生效：不必等她说下一句）。
            Core.BalloonTuning.Enabled = config.BalloonEnabled;
            Core.BalloonTuning.Seconds = Math.Clamp(config.BalloonSeconds, 0.5f, 30f);

            // v0.18.50 玩家称谓（替换台词里的 {{称谓}}）。留空 = NpcLines 里的默认「主人」。
            _brain.PlayerTitle = string.IsNullOrWhiteSpace(config.PlayerTitle)
                ? Core.NpcLines.DefaultTitle
                : config.PlayerTitle.Trim();

            // v0.18.50 艾时校准旋钮（-1 = 跟着游戏内时间自动算）。
            Core.NpcEnvironment.HourOverride = config.EorzeaHourOverride;

            // ── v0.18.51 ──
            // 台词节奏：两句之间的全局最短间隔（0 = 不冷却）。
            _brain.LineCooldownSeconds = Math.Max(0f, config.LineCooldownSeconds);

            // 动作密度：命中概率 + 最小间隔。
            Core.NpcGesture.ChancePercent = Math.Clamp(config.GestureChancePercent, 0f, 100f);
            Core.NpcGesture.MinGapSeconds = Math.Max(0f, config.GestureMinGapSeconds);

            // v0.18.53 说话开合（口型层，见 NpcAnimator.UpdateLipSync）
            if (_animator is not null)
            {
                _animator.LipSyncEnabled = config.LipSyncEnabled;
                _animator.LipFlapSeconds = Math.Clamp(config.LipFlapSeconds, 0.05f, 0.6f);

                // v0.18.57/58 可选待机姿势 + 自动随机：姿势只在"配置变了"时推 ——
                // 动画层开着自动随机时会自己换姿势，每帧硬推会把随机结果打回去。
                _animator.IdleAutoRandom   = config.IdleAutoRandom;
                _animator.IdleRelaxSeconds = Math.Clamp(config.IdleRelaxSeconds, 0f, 600f);   // 下限 0（v0.18.61）
                _animator.IdleSwitchSeconds = Math.Clamp(config.IdleSwitchSeconds, 5f, 600f); // v0.18.66：随机切换间隔（实际下限 5s）

                // v0.18.66：闲聊距离池的「贴身」档阈值接「贴近触发距离」（原 NpcBrain 硬编码 2.2f，
                // 面板滑条管不到闲聊台词 —— 用户实测"贴近触发距离失效"的主因）。
                _brain.NearChatDistance = Math.Clamp(config.CloseTouchDistance, 0.3f, 8f);
                var idleRow = Math.Clamp(config.IdleEmoteRowId, 0, ushort.MaxValue);
                if (idleRow != _lastPushedIdleRow || config.IdleAutoRandom != _lastPushedIdleAuto)
                {
                    var idleChoice = idleRow == 0 ? null : GetIdleEmotes().FirstOrDefault(c => c.EmoteRowId == idleRow);
                    _animator.SetIdleEmote(idleChoice?.PlayEmoteRow ?? 0,
                                           idleChoice?.LoopTimeline ?? 0,
                                           (ushort)idleRow);
                    _lastPushedIdleRow  = idleRow;
                    _lastPushedIdleAuto = config.IdleAutoRandom;
                }
            }

            // 时段来源：0 = 游戏内艾欧泽亚时间，1 = 现实本地时间。
            Core.NpcEnvironment.DayPhaseSource = config.DayPhaseSource == 1 ? 1 : 0;

            // v0.18.39 点击兜底开关。
            NpcTargetable.FallbackEnabled = config.TargetClickFallback;

            // 配置里的人格才是权威（面板选过的、关游戏重开都还在）。
            if (Enum.IsDefined(typeof(NpcBrain.Persona), config.Persona)
                && (int)_brain.CurrentPersona != config.Persona)
            {
                _brain.SetPersona((NpcBrain.Persona)config.Persona, speak: false);
            }
        }

        // 行为系统：注意放在 <see cref="NpcBrain"/> 之后 —— 人格会影响跟随距离，
        // 而行为层是站在"跟随距离"之上的更高一层，得等下面这些都定好了再推。
        ApplyBehaviourSettings(config);

        // ── v0.18.45 显示名（目标信息栏/名牌）实时同步 ──
        // 配置名写进她 GameObject+0x30 的名字缓冲（64B NUL 结尾 UTF-8），
        // 面板每帧从那里读 —— 写完立即生效。留空 = 显示游戏分配的默认名。
        // 变更侦测：配置名变了、或对象地址换了（换区重召唤），才动手+落存档。
        var configName = config.NpcName ?? string.Empty;
        var nameChanged = _seenConfigName != configName;
        _seenConfigName = configName;

        if (_npc is { IsAlive: true }
            && (nameChanged || _namedAddress != _npc.Address))
        {
            _namedAddress = _npc.Address;
            var wanted = Core.NpcNaming.EffectiveName(configName, _npc.GameAssignedName);
            Core.NpcNaming.WriteName(_npc.Address, wanted);

            // 命名后存档同步：真改名的那次写盘一次（首次加载配置的那次不算）。
            if (nameChanged && _nameSyncPrimed)
                SaveConfig();
        }

        _nameSyncPrimed = true;

        // ── v0.18.69.22：副 NPC 设置转发（内部有变更侦测，每帧调也无害）──
        SubNpcs?.ApplySettings(config);
    }

    /// <summary>人格 → 跟随距离的倍率（不是覆盖，用户设定的距离是基准）。</summary>
    private static float FollowDistanceFor(NpcBrain.Persona persona, float configured)
        => persona switch
        {
            NpcBrain.Persona.安静 => configured * 1.6f,
            NpcBrain.Persona.弱气 => configured * 1.3f,   // v0.18.50：学者 → 弱气（怕贴太近，但比你以为的近）
            NpcBrain.Persona.元气 => configured * 0.75f,
            _                      => configured,
        };

    /// <summary>把行为系统相关的配置推给 <see cref="NpcBehavior"/>。</summary>
    private void ApplyBehaviourSettings(Configuration config)
    {
        if (_behaviour is null)
            return;

        _behaviour.Enabled          = BrainTuning.BehaviourEnabled;
        if (_afkActive && _behaviour is not null)
            _behaviour.Enabled = false;   // 挂机期间：面板改设置也不许恢复游荡/搭话
        _behaviour.RunDistance     = BehaviourTuning.RunDistance;
        _behaviour.WalkDistance    = BehaviourTuning.WalkDistance;
        _behaviour.StrayDistance   = BehaviourTuning.StrayDistance;
        _behaviour.ArriveDistance  = config.ArriveDistance;   // ⚠️ 之前读的是常量，配置项一直是死的
        _behaviour.RoamRange       = config.RoamRange;
        _behaviour.RoamMin         = config.RoamMin;   // ⚠️ 之前读的是常量，配置项一直是死的
        // 待机小动作的频率（v0.14.0）：这两个是"她有多爱动"最直接的旋钮，
        // 用户明确要留着，所以从常量改回读配置，常量只当兜底。
        var gestureMin = config.IdleGestureMin > 0 ? config.IdleGestureMin : BehaviourTuning.IdleGestureMin;
        var gestureMax = config.IdleGestureMax > 0 ? config.IdleGestureMax : BehaviourTuning.IdleGestureMax;
        _behaviour.IdleGestureMin  = gestureMin;
        _behaviour.IdleGestureMax  = MathF.Max(gestureMax, gestureMin + 1f);
        _behaviour.LookAroundAfter = config.LookAroundAfter;   // ⚠️ 之前读的是常量，配置项一直是死的
        _behaviour.ChatTriggerIdle = BehaviourTuning.ChatTriggerIdle;

        // 人格也决定"她贴多近"—— 安静的人会跟你保持距离。
        // ⚠️ 必须在这里算，而不是在 _brain 那段：那里的人格可能还没被改成配置里的值。
        _behaviour.FollowBehind = FollowDistanceFor(
            _brain?.CurrentPersona ?? NpcBrain.Persona.元气, config.FollowBehind);

        _behaviour.FollowSide = config.SideOffset;

        // v0.17.6：跟随触发距离（面板可调）+ 开关B的行为层入口。
        _behaviour.FollowAgainDistance = config.FollowAgainDistance;

        // v0.18.2：「移动起来就打断动作」这个开关已删除。
        // 它和"铁律 Paused=动作中"天生冲突：打断 → 计时归零 → 停顿条件解除 →
        // 她又开始走 → 动作又满足"她停着才插"的门槛 → 再插 → 再断，拉锯成抽搐。
        // 现在只有一条规则：**做动作时就是站着**，动作播完自然再走。
        _behaviour.GesturesWhileMoving = !config.BlockGesturesWhileMoving;
    }

    public void ToggleSummon()
    {
        if (HasNpc)
            Dismiss();
        else
            Summon();
    }

    /// <summary>换区自动重召唤的倒计时（秒）。&gt;0 = 等待中；到 0 自动召唤。</summary>
    private float _respawnTimer;

    /// <summary>插件启动后的一次性武装标记（v0.18.69.20 自动召唤）。首帧消费。</summary>
    private bool _autoSummonArmed = true;

    // ── v0.18.69.21 挂机模式 ──
    private bool _afkActive;                 // 当前是否处于挂机（她坐着、静音）
    private float _afkIdleSeconds;           // 玩家无操作累计秒数
    private Vector3 _afkLastPos;             // 上一帧玩家位置（位移 = 有操作）
    private bool _afkPosValid;
    private uint _afkLastCondBits;           // 上一帧玩家状态位快照（变化 = 有操作）
    private ushort _afkSeenEmoteId;          // 上次见过的玩家表情（新表情 = 有操作）
    private bool _afkBehaviourWasEnabled = true;
    private ushort _afkSitRow;               // 「坐下」表情行号（进入时解析）
    private ushort _afkSitLoop;              // 「坐下」循环段时间轴
    private float _afkReassert;              // 坐姿自愈重试节流

    /// <summary>挂机检测要盯的玩家状态：任何一个翻转都算"有操作"。</summary>
    private static readonly ConditionFlag[] AfkWatchFlags =
    [
        ConditionFlag.InCombat,
        ConditionFlag.Mounted,
        ConditionFlag.Crafting,
        ConditionFlag.ExecutingCraftingAction,
        ConditionFlag.Gathering,
        ConditionFlag.ExecutingGatheringAction,
        ConditionFlag.Fishing,
        ConditionFlag.OccupiedInEvent,
        ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.BetweenAreas,
        ConditionFlag.WaitingForDuty,
    ];

    public void Summon(bool announce = true)
    {
        if (HasNpc)
        {
            _log.Information("[SmartNpc] 已经有一个 NPC 了。");
            return;
        }

        _respawnTimer = 0f;   // 任何一次召唤都取消等待中的自动重召唤
        ResetAfkState();      // 重新召唤：挂机状态清零，重新计时

        if (!NpcFactory.TrySpawn(false, out var handle, out var error))
        {
            _log.Error("[SmartNpc] 创建失败：{0}", error);
            if (announce)
                _chat?.PrintError($"[SmartNpc] 创建失败：{error}");
            Diag?.Write(BuildDiagnostics("召唤失败：" + error));
            return;
        }

        _npc        = handle;
        _appearance = new NpcAppearance(_log);
        _motion     = new NpcMotion();
        _animator   = new NpcAnimator(_log, _data);
        _animator.Diag = _diag;   // v0.18.64：姿势管线关键行落盘（重召唤时 Diag 可能早已赋值）
        _lastPushedIdleRow = -1;   // 新动画层，配置姿势必须重新推一遍（v0.18.58）
        _brain      = new NpcBrain(_log, _chat, _animator);
        _behaviour  = new NpcBehavior(_log);

        // v0.17.8：避障卡住时的跳跃 → 动画层播跳跃时间轴。
        _motion.OnJump = () => _animator?.PlayJump();

        // v0.18.9：跳跃三段动画 —— 起跳(31) / 空中下落(32) / 落地(33)。
        // v0.18.13：到顶不再直接切 32 —— 32 的唯一正点是"31 播完"（槽位轮询）；
        // apex 只做记录 + 扫描异常时的兜底（见 NpcAnimator.Update）。
        _motion.OnJumpFalling = () => _animator?.NotifyJumpApex();
        _motion.OnJumpLanded  = () => _animator?.PlayJumpLand();

        _appearance.Bind(handle);

        // v0.18.55：Penumbra 个人分组 —— 必须赶在 PrepareModel **之前**挂，
        // 这样她的资源从第一次加载起就按该分组解析，天然生效、无需补 redraw。
        var penumbraId = _config?.PenumbraCollectionId ?? Guid.Empty;
        if (penumbraId != Guid.Empty && _penumbra is not null)
        {
            var (ok, msg) = _penumbra.AssignToObject(handle.ObjectIndex, penumbraId);
            if (!ok)
                _chat?.Print($"[SmartNpc] Penumbra 分组挂载失败：{msg}");
        }

        // v0.18.51：把 Emote 表里的真实动作名灌给语义动作匹配器 ——
        // 它只认"表里真的存在"的名字，没灌过的话所有台词都匹配不到动作（退化成只说话）。
        Core.NpcGesture.Initialize(_animator.AvailableActions);
        DumpActionNames();

        ApplySettings(_config!);
        _animator.Bind(handle);
        _brain.Bind(handle);

        _log.Information("[SmartNpc] 已创建 NPC：{0}｜游戏算出的可用索引={1}｜实得索引={2}｜创建瞬间类型={3}",
                         handle, NpcFactory.LastNextAvailableIndex, NpcFactory.LastSpawnIndex, NpcFactory.CreatedKind);

        // 放到玩家身边（v0.18.3：落点偏移可在面板自定义，不再死钉在玩家坐标上）。
        var player = _objectTable?.LocalPlayer;
        if (player is not null)
        {
            var at  = player.Position;
            var cfg = _config!;

            // 以玩家朝向为基准：身后 SpawnOffsetBehind 米、侧向 SpawnOffsetSide 米。
            var yaw   = player.Rotation;
            var fwd   = new System.Numerics.Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
            var right = new System.Numerics.Vector3(fwd.Z, 0f, -fwd.X);

            var px = at.X - fwd.X * cfg.SpawnOffsetBehind + right.X * cfg.SpawnOffsetSide;
            var pz = at.Z - fwd.Z * cfg.SpawnOffsetBehind + right.Z * cfg.SpawnOffsetSide;

            var py = NpcGround.Resolve(px, pz, at.Y, anchorToTarget: true);
            if (!float.IsFinite(py) || MathF.Abs(py - at.Y) > 6f)
                py = at.Y;

            // ⚠️ 不穿墙红线：出生点若被墙挡（玩家贴墙站时偏移点会在墙里），
            // 退回玩家脚下 —— 那里必然可站立，绝不出生在几何体里。
            var ox   = px - at.X;
            var oz   = pz - at.Z;
            var olen = MathF.Sqrt(ox * ox + oz * oz);

            if (olen > 0.05f
                && NpcAvoidance.RaycastPath(
                    new System.Numerics.Vector3(at.X, at.Y, at.Z),
                    new System.Numerics.Vector3(ox / olen, 0f, oz / olen),
                    olen + 0.2f,
                    out _))
            {
                px = at.X;
                pz = at.Z;
                py = at.Y;
            }

            NpcFactory.TeleportTo(handle, new FFXIVClientStructs.FFXIV.Common.Math.Vector3(px, py, pz), 0f);
            handle.TerritoryId = _clientState?.TerritoryType ?? 0;
        }

        // ⚠️ 顺序决定成败：**先写模型，再请求绘制**。
        //
        // 刚创建出来的 actor 是空壳（没有任何模型），游戏不会为它构建绘制对象，
        // 于是 DrawObject 永远是空、IsReadyToDraw() 永远 false。
        // 先等绘制、后写模型 = 死锁（实测：卡满超时，日志「绘制对象=空」）。
        var prepared = _appearance.PrepareModel(player?.Address ?? nint.Zero,
                                               Core.BrainTuning.CopyPlayerOnSummon);
        if (!prepared)
        {
            _log.Warning("[SmartNpc] 模型准备失败（拿不到玩家或对象已失效），仍继续尝试点亮。");
        }

        // ⚠️ 而且**不能在这里用 IFramework.RunOnTick**：召唤是玩家点按钮/输指令触发的，
        // 那时不在 framework 线程上，RunOnTick 的回调根本不会被执行（实测：日志只有"已创建"，
        // 后面什么都没有）。所以这里只**记下状态**，由每帧回调 OnFrameworkUpdate 去推进。
        _spawnDraw = new NpcFactory.SpawnDrawState();

        // 召唤成功后：如果配置里记了 Glamourer 设计，就直接套上。
        ApplyConfiguredDesign();

        // 能看到但点不到 → 走 v0.18.37 的统一出口（并且**强制**留一行采样：
        // 这一行就是"刚召唤出来"那一瞬间的现场，和换区之后的现场对照用）。
        NpcTargetable.Apply(
            (GameObject*)handle.Address,
            player is not null && player.Address != nint.Zero ? (GameObject*)player.Address : null,
            _config?.TargetableByPlayer ?? true,
            forceTrace: true,
            ourIndex: handle.ObjectIndex);

        // 嗓音预设：造出来就先写一次（之后每帧保持）。
        if (VoiceTuning.PresetId > 0)
            NpcVoice.ApplyPreset((Character*)handle.Address, VoiceTuning.PresetId);

        if (announce)
            _chat?.Print("[SmartNpc] 她正在走过来……");
    }

    public void Dismiss()
    {
        _respawnTimer = 0f;   // 手动送走：绝不能被自动重召唤"复活"
        ResetAfkState();
        _framework?.Update -= OnFrameworkUpdate;

        if (_npc is { } handle && handle.IsAlive)
        {
            // v0.18.55：先解绑 Penumbra 个人分组（趁她还在对象表里），
            // 再销毁 —— 否则 Penumbra 会留一条指向已销毁对象的指派。
            if ((_config?.PenumbraCollectionId ?? Guid.Empty) != Guid.Empty && _penumbra is not null)
                _penumbra.ClearObject(handle.ObjectIndex);

            NpcFactory.Despawn(handle);
            _log.Information("[SmartNpc] 已销毁 NPC。");
            _chat?.Print("[SmartNpc] 她离开了。");
        }

        _npc        = null;
        _spawnDraw  = null;
        _appearance = null;
        _motion     = null;
        _animator   = null;
        _brain      = null;
        _behaviour  = null;

        NpcHousing.Reset();   // 家具圆缓存一并清掉（v0.18.17）

        _framework?.Update += OnFrameworkUpdate;
        Diag?.Write(BuildDiagnostics("已让她离开"));
    }

    /// <summary>把玩家当前的外观复制一份给她（最省心的默认长相）。</summary>
    public void ApplyAppearanceFromPlayer()
    {
        if (_npc is null || _appearance is null || _objectTable?.LocalPlayer is not { } player)
            return;

        var (customize, equipment) = NpcAppearance.CopyFromPlayer((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address);
        _appearance.Apply(customize, equipment);
        _log.Information("[SmartNpc] 已套用玩家外观。");
    }

    /// <summary>裸体（无装备），便于确认骨架是否正确。</summary>
    public void ApplyAppearanceNaked()
    {
        if (_npc is null || _appearance is null)
            return;

        _appearance.Apply(NpcAppearance.DefaultCustomize(), new byte[NpcAppearance.EquipmentLength]);
        _log.Information("[SmartNpc] 已重置为默认体型与裸装。");
    }

    public void SetPersona(string persona)
    {
        if (!Enum.TryParse<NpcBrain.Persona>(persona, out var parsed))
        {
            _log.Information("[SmartNpc] 未知人格：{0}（可选：安静 / 元气 / 傲娇 / 弱气）", persona);
            return;
        }

        if (_brain is null)
            return;

        _brain.SetPersona(parsed);

        // ⚠️ 必须把选择写回配置。以前只改脑子、不写配置，
        // 于是界面上那个勾永远停在旧的那一项 —— 用户点谁都像"选不上"。
        if (_config is not null && _config.Persona != (int)parsed)
        {
            _config.Persona = (int)parsed;
            SaveConfig();
        }

        // 人格换了，跟随距离跟着变（安静的人会跟你保持距离）。
        if (_behaviour is not null && _config is not null)
            _behaviour.FollowBehind = FollowDistanceFor(parsed, _config.FollowBehind);
    }

    /// <summary>打开控制面板（供指令与右键菜单调用）。</summary>
    public Action? OpenPanelRequested { get; set; }

    public void OpenPanel() => OpenPanelRequested?.Invoke();

    /// <summary>
    /// 立刻停止当前动作（面板按钮 / <c>/smartnpc stop</c>）。
    /// </summary>
    /// <param name="quiet">
    /// <b>true = 不往聊天栏说话</b>。
    ///
    /// ⚠️ 内部自动打断（"你在走、得跟上"）必须传 true —— 那个判定是<b>每帧</b>
    /// 都可能成立的，一旦说话就会疯狂刷屏（用户实测反馈的「已停止，回到站姿」
    /// 满屏刷的元凶）。只有玩家主动点面板按钮 / 输指令时才允许提示。
    /// </param>
    public void StopAction(bool quiet = false)
    {
        if (!HasNpc || _animator is null)
        {
            if (!quiet)
                _chat?.Print("[SmartNpc] 现在没有 NPC。");
            return;
        }

        var stopped = _animator.Stop();
        _brain?.CancelSpeaking();   // ⚠️ 连"说话停走"的 2.2 秒墙一起拆，否则打断不彻底
        _log.Information("[SmartNpc] 停止动作：{0}", stopped ? "已还原待机" : "本来就没有动作在播");

        if (!quiet)
            _chat?.Print(stopped ? "[SmartNpc] 已停止，回到站姿。" : "[SmartNpc] 现在没有动作在播。");
    }

    /// <summary>
    /// 面板「试说一句」（v0.18.50）：不挑时机、不看开关，立刻说一句指定类别的台词。
    /// 主要用于听感校对 —— 不用等到真的下雨 / 真的进战斗才知道她会说啥。
    /// </summary>
    public void TrySpeak(Core.TryTopic topic)
    {
        if (_brain is null)
        {
            _chat?.Print("[SmartNpc] 她还没出现。");
            return;
        }

        switch (topic)
        {
            case Core.TryTopic.天气:   _brain.ForceSample(Core.NpcEnvironment.Weather); break;
            case Core.TryTopic.时段:   _brain.ForceSample(Core.NpcEnvironment.Phase); break;
            case Core.TryTopic.战斗:   _brain.ForceSample(Core.NpcLines.Combat.进入); break;
            case Core.TryTopic.被选中: _brain.ForceSample(Core.NpcLines.Touch.被选中); break;
            case Core.TryTopic.关心:   _brain.ForceSample(Core.NpcLines.Touch.受伤关心); break;
            case Core.TryTopic.称赞:   _brain.ForceSample(Core.NpcLines.Touch.战斗称赞); break;
        }
    }

    /// <summary>打个招呼：随机挑一句开场白 + 一个动作。</summary>
    public void Greet()
    {
        if (!HasNpc || _brain is null)
        {
            _chat?.Print("[SmartNpc] 她还没出现。");
            return;
        }

        _brain.Greet();
    }

    // ── v0.18.51 右键菜单的互动项 ──────────────────────────────────
    //
    // 用户要求：右键菜单里除了「打个招呼」「打开面板」，还要有「询问天气」「询问时间」
    // 这类主动互动。这里每一项都对应 NpcLines.Ask 的一个桶。

    /// <summary>问她现在什么天气 —— 她会用游戏里那个真实名词回答（"碧空""薄雾"）。</summary>
    public void AskWeather() => Ask(Core.NpcLines.Ask.问天气);

    /// <summary>问她现在几点 —— 艾欧泽亚时间的小时数（或现实时间，看配置）。</summary>
    public void AskTime() => Ask(Core.NpcLines.Ask.问时间);

    /// <summary>问她心情怎么样。</summary>
    public void AskMood() => Ask(Core.NpcLines.Ask.问心情);

    /// <summary>夸夸她。</summary>
    public void Praise() => Ask(Core.NpcLines.Ask.被夸奖);

    /// <summary>跟她说谢谢。</summary>
    public void Thank() => Ask(Core.NpcLines.Ask.被道谢);

    /// <summary>摸摸她的头。</summary>
    public void PatHead() => Ask(Core.NpcLines.Ask.被摸头);

    /// <summary>让她坐下歇一会儿。</summary>
    public void Rest() => Ask(Core.NpcLines.Ask.歇息);

    /// <summary>跟她道别。</summary>
    public void Farewell() => Ask(Core.NpcLines.Ask.告别);

    private void Ask(Core.NpcLines.Ask topic)
    {
        if (!HasNpc || _brain is null)
        {
            _chat?.Print("[SmartNpc] 她还没出现。");
            return;
        }

        _brain.OnAsk(topic);
    }

    /// <summary>
    /// 只读诊断：把她此刻的"可绘制性"打进日志。
    ///
    /// 刻意<b>不创建也不销毁</b>任何对象 —— 出问题时按一下，
    /// 日志里就有索引 / 类型 / 就绪 / 绘制对象 / 渲染标志 / 模型 / 坐标，
    /// 足够判断卡在"没模型"、"没绘制对象"还是"有绘制对象但不可见"。
    /// </summary>
    public void LogDiagnostics()
    {
        var report = BuildDiagnostics("手动诊断");

        // ⚠️ 写自己的文件 —— Dalamud 日志到 100MB 就停了，写进去也看不到。
        Diag?.Write(report);
        _log.Information("[SmartNpc] 诊断已写入 {0}", Diag?.FilePath ?? "（诊断文件不可用）");
        _log.Information("[SmartNpc] {0}", report);
        _chat?.Print($"[SmartNpc] 诊断已写入：{Diag?.FilePath ?? "（不可用）"}");
    }

    /// <summary>
    /// 一份能独立回答"卡在哪一步"的状态报告。刻意写得啰嗦一点：
    /// 排查时最有价值的是**逐项把值打出来**，而不是一句"没找到"。
    /// </summary>
    private string BuildDiagnostics(string reason)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("原因=").AppendLine(reason);
        sb.Append("版本=").AppendLine(Plugin.DisplayVersion);
        sb.Append("心跳帧=").Append(_frames).Append("｜帧间隔=").AppendLine(_delta.ToString("0.000"));
        sb.Append("有无 NPC=").Append(HasNpc).AppendLine();
        sb.Append("本地玩家=").Append(_objectTable?.LocalPlayer is { } p
            ? $"地址=0x{p.Address:X} 坐标={p.Position}"
            : "（取不到）").AppendLine();

        if (_npc is { } handle)
        {
            sb.Append("NPC=").AppendLine(handle.ToString());
            sb.Append("  快照｜").AppendLine(NpcFactory.Describe(handle));
            sb.Append("  点亮状态机=").AppendLine(_spawnDraw is null ? "（已结束）" : "进行中");

            // v0.18.37：「能不能被选中」的一整套状态 —— 排查"选不中"的第一现场。
            sb.Append("  可选中｜TS=0x").Append(NpcTargetable.LastStatus.ToString("X2"))
              .Append("（写前 0x").Append(NpcTargetable.LastBefore.ToString("X2")).Append("）")
              .Append(" GetIsTargetable=").Append(NpcTargetable.LastIsTargetable)
              .AppendLine();
            sb.Append("  点击兜底｜接管 ").Append(NpcTargetable.FallbackHits)
              .Append(" 次｜最近：").AppendLine(NpcTargetable.FallbackLast);
        }
        else
        {
            sb.Append("对象表下一个可用索引=").AppendLine(NpcFactory.LastNextAvailableIndex.ToString());
        }

        if (_motion is not null)
            sb.Append("移动：姿态=").Append(_motion.CurrentGait)
              .Append(" 移动中=").Append(_motion.IsMoving)
              .Append(" 速度=").Append(_motion.CurrentSpeed.ToString("0.00"))
              .Append("（玩家 ").Append(_playerSpeed.ToString("0.0"))
              .Append(" × 倍率 ").Append(Core.MotionTuning.WalkSpeedScale.ToString("0.0"))
              .Append("）")
              .Append(" 朝向=").Append(_motion.CurrentRotation.ToString("0.00"))
              .Append(" 贴地Y=").AppendLine(_npc is null ? "—"
                        : ((GameObject*)_npc.Address)->Position.Y.ToString("0.00"));

        if (_brain is not null)
            sb.Append("人格=").Append(_brain.CurrentPersona)
              .Append(" 情境=").Append(_brain.CurrentScene)
              .Append(" 说话中=").Append(_brain.IsSpeaking)
              .Append(" 称谓=").AppendLine(_brain.PlayerTitle);

        // v0.18.50：游戏内天气 / 时段的**原始读数**（分错类时一眼看得出原因）。
        sb.Append("环境｜").AppendLine(Core.NpcEnvironment.Describe());

        // 行为层：排查"她怎么不动了"时，这一行最有用——
        // 能直接看出她是卡在"跟随"（在追）、"游荡"（自己走了）、"搭话"还是"警戒"。
        if (_behaviour is not null)
            sb.Append("行为=").Append(_behaviour.Describe())
              .Append(" 启用=").Append(_behaviour.Enabled)
              .Append(" 起跑=").Append(_behaviour.RunDistance.ToString("0.0"))
              .Append(" 降回走=").Append(_behaviour.WalkDistance.ToString("0.0"))
              .Append(" 跟丢=").Append(_behaviour.StrayDistance.ToString("0.0"))
              .Append(" 游荡=").Append(_behaviour.RoamMin.ToString("0.0"))
              .Append("~").Append(_behaviour.RoamRange.ToString("0.0"))
              .AppendLine(" 米");

        sb.Append("玩家速度=").Append(_playerSpeed.ToString("0.00")).AppendLine(" 米/秒");

        if (_animator is not null)
        {
            sb.Append("动画：走路时间轴=#").Append(_animator.WalkTimeline)
              .Append("（来源=").Append(_animator.WalkTimelineSource).Append("）")
              .Append("｜奔跑时间轴=#").Append(_animator.RunTimeline)
              .Append("（来源=").Append(_animator.RunTimelineSource).Append("）")
              .Append(" 走路中=").Append(_animator.IsWalking)
              .Append(" 奔跑中=").Append(_animator.IsRunning)
              .Append(" 当前动作=").Append(_animator.CurrentAction ?? "（无）")
              // v0.18.15：排查"脚下常驻踩水涟漪"——Mode 被 Swimming 化会直接解释它
              .Append("｜Mode=").Append(_animator.ActualMode)
              .Append(" Base=").Append(_animator.ActualBaseOverride)
              .AppendLine();

        // v0.16.6 诊断：把转身相关的**真实**状态写进诊断文件，
        // 用来定位"转身动作中途断掉"究竟是谁干的 —— 不再靠猜。
        sb.Append("转身诊断：转身中=").Append(_turningActive)
          .Append(" 模式=").Append(_overflowMode)
          .Append(" 剩余=").Append(_turningTimeout.ToString("0.0"))
          .Append(" BaseOverride(游戏实际值)=").AppendLine(_animator.ActualBaseOverride.ToString());
        }

        sb.Append("Glamourer 可用=").AppendLine(GlamourerAvailable.ToString());
        return sb.ToString();
    }

    /// <summary>给设置面板用：当前玩家速度、她的速度与姿态（纯读取，不改状态）。</summary>
    public void DescribeSpeed(out float playerSpeed, out float herSpeed, out string gait)
    {
        playerSpeed = _playerSpeed;
        herSpeed    = _motion?.CurrentSpeed ?? 0f;
        gait        = _motion?.CurrentGait.ToString() ?? "（无）";
    }

    /// <summary>给设置面板用：走 / 跑动作的行号与来源（纯读取，不改状态）。</summary>
    public void DescribeTimelines(out int walk, out int run, out string walkSrc, out string runSrc)
    {
        walk    = _animator?.WalkTimeline ?? 0;
        run     = _animator?.RunTimeline ?? 0;
        walkSrc = _animator?.WalkTimelineSource ?? "未知";
        runSrc  = _animator?.RunTimelineSource ?? "未知";
    }

    public void Say(string text)
    {
        if (HasNpc && _brain is not null)
            _brain.Speak(text);
        else
            _log.Information("[SmartNpc]（还没有 NPC，无法说话）");
    }

    // ── v0.18.69.21 挂机模式 ──────────────────────────────────────
    //
    // 检测：位置位移 / 状态位翻转 / 新表情动作，任一发生 = "玩家有操作"。
    // 超时进入：她坐下（姿势同款两段式）+ 全部自动台词静默 + 行为系统停摆（不游荡不搭话）。
    // 退出：玩家一动立刻起身，一切恢复。

    /// <summary>每帧推进挂机检测。playerPos = 玩家当前位置。</summary>
    private void TickAfkMode(Vector3 playerPos, float delta)
    {
        var activity = false;

        // 1) 位置：位移超过 5cm 算操作
        if (_afkPosValid && Vector3.DistanceSquared(playerPos, _afkLastPos) > 0.0025f)
            activity = true;
        _afkLastPos  = playerPos;
        _afkPosValid = true;

        // 2) 状态位：战斗/骑乘/采集/事件等任一翻转算操作
        if (_condition is not null)
        {
            uint bits = 0;
            for (var i = 0; i < AfkWatchFlags.Length; i++)
                if (_condition[AfkWatchFlags[i]])
                    bits |= 1u << i;
            if (bits != _afkLastCondBits)
            {
                _afkLastCondBits = bits;
                activity = true;
            }
        }

        // 3) 玩家表情：Character+0x630 EmoteController+0x14 EmoteId（与表情回应同源）
        var player = _objectTable?.LocalPlayer;
        if (player is not null && player.Address != nint.Zero)
        {
            try
            {
                var emoteId = ((Character*)player.Address)->EmoteController.EmoteId;
                if (emoteId != 0 && emoteId != _afkSeenEmoteId)
                {
                    _afkSeenEmoteId = emoteId;
                    activity = true;
                }
            }
            catch { /* 偏移异常时忽略表情源，位置/状态位仍在 */ }
        }

        if (activity)
        {
            _afkIdleSeconds = 0f;
            if (_afkActive)
                ExitAfk("玩家恢复操作");
            return;
        }

        if (_afkActive)
        {
            // 自愈：坐姿被任何原因顶掉（移动接管/模型重建/表情槽被占）就重新坐下。
            // ⚠️ v0.18.69.21：判据用 PoseLoopActive（循环基底真非零）而不是
            // AfkSitActive（自设标志，外力清坐姿后它仍是 true → 自愈永远不触发，
            // 表现就是用户看到的"站起来之后没有任何动作"）。
            _afkReassert -= delta;
            if (_afkReassert <= 0f)
            {
                _afkReassert = 2f;
                if (_animator is not null && _afkSitRow != 0
                    && !_animator.PoseLoopActive && !_animator.IsWalking)
                {
                    _log.Information("[SmartNpc] 挂机坐姿丢失，重新坐下。");
                    _animator.EnterAfkSit(_afkSitRow, _afkSitLoop);
                }
            }
            return;
        }

        if (!(_config?.AfkModeEnabled ?? false) || !HasNpc || player is null)
            return;

        _afkIdleSeconds += delta;
        if (_afkIdleSeconds >= (_config?.AfkTimeoutSeconds ?? 300f))
            EnterAfk();
    }

    private void EnterAfk()
    {
        _afkActive = true;
        _afkReassert = 3f;

        // 1) 台词全静默：切掉正在说的 + 关自动闲聊/配动作（NpcBrain 内建开关）
        _brain?.CancelSpeaking();
        if (_brain is not null)
            _brain.SuppressGestureAndChatter = true;

        // 2) 行为停摆：不游荡、不搭话、不做闲置小动作（Enabled=false 退回纯跟随，
        //    玩家没动 = 她原地站着 = 正好坐下）
        if (_behaviour is not null)
        {
            _afkBehaviourWasEnabled = _behaviour.Enabled;
            _behaviour.Enabled = false;
        }

        // 3) 坐下：解析「坐下到地上」表情行（用户指定动作用词；找不到再退「坐下」），
        //    走姿势同款两段式
        _afkSitRow = 0;
        _afkSitLoop = 0;
        if (_animator is not null)
        {
            var row = _animator.FindEmoteRow("坐下到地上", "地面坐下", "坐在地上", "坐下");
            if (row != 0)
            {
                _afkSitRow  = row;
                _afkSitLoop = _animator.EmoteLoopTimelineOf(row);
                if (!_animator.EnterAfkSit(_afkSitRow, _afkSitLoop))
                    _log.Warning("[SmartNpc] 挂机坐姿启动失败（冻结期/不在场），稍后自愈重试。");
            }
            else
            {
                _log.Warning("[SmartNpc] 挂机模式：表情表里没找到「坐下」，只静音不坐下。");
            }
        }

        _log.Information("[SmartNpc] 进入挂机模式（无操作 {0:F0} 秒）。", _config?.AfkTimeoutSeconds ?? 300f);
        _chat?.Print($"[SmartNpc] 你已挂机，{_config?.NpcName ?? "她"} 坐下休息了（一动就会站起来）。");
        Diag?.Write(BuildDiagnostics("进入挂机模式"));
    }

    private void ExitAfk(string reason)
    {
        _afkActive = false;
        _afkIdleSeconds = 0f;

        _brain?.CancelSpeaking();
        if (_brain is not null)
            _brain.SuppressGestureAndChatter = false;

        if (_behaviour is not null)
            _behaviour.Enabled = _afkBehaviourWasEnabled;

        _animator?.ExitAfkSit();

        _log.Information("[SmartNpc] 挂机结束（{0}）。", reason);
        Diag?.Write(BuildDiagnostics("退出挂机模式：" + reason));
    }

    /// <summary>召唤/送走/换区：挂机状态整体清零（她重新计时，不会带着坐姿复活）。</summary>
    private void ResetAfkState()
    {
        _afkActive      = false;
        _afkIdleSeconds = 0f;
        _afkPosValid    = false;
        _afkSeenEmoteId = 0;
        _afkSitRow      = 0;
        _afkSitLoop     = 0;
        _afkReassert    = 0f;
        _animator?.ExitAfkSit();
    }

    public void Emote(string name)
    {
        if (HasNpc && _animator is not null)
            _animator.Play(name);
        else
            _log.Information("[SmartNpc]（还没有 NPC，无法做动作）");
    }

    public void PrintStatus()
    {
        if (!HasNpc || _npc is null)
        {
            _log.Information("[SmartNpc] 当前没有 NPC。");
            return;
        }

        _log.Information("[SmartNpc] {0}｜实体 {1}｜区域 {2}", _npc, _npc.EntityId, _npc.TerritoryId);
        _log.Information("[SmartNpc] 行为：{0}", _behaviour?.Describe() ?? "（无）");
        _log.Information("[SmartNpc] 移动：{0}｜{1:0.00} 米/秒",
                         _motion?.CurrentGait, _motion?.CurrentSpeed ?? 0f);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // 先算 delta，再更新 _lastTime —— 顺序反了 delta 就恒为 0。
        var now = (float)_clock.Elapsed.TotalSeconds;
        _delta    = Math.Clamp(now - _lastTime, 0f, 0.25f);
        _lastTime = now;

        // ⚠️ 心跳必须先于任何 return —— 它要能证明"每帧回调到底有没有在跑"。
        // 没有 NPC 时也要写，否则"召唤没反应"和"插件根本没驱动"会混成一团。
        _frames++;
        if (Diag is not null && _frames % 1800 == 0)
            Diag.Write(BuildDiagnostics("周期心跳（每 1800 帧）"));

        // ── 碰撞层探针（v0.18.28）──
        // 脚印/轨迹路线（v0.18.22~27）已按用户裁决整体退役。
        // 现在这里只做一件事：定期把 32 个 BGCollision 层位打一遍射线，
        // 找出家具碰撞所在的层（命中明显高于地形 = 打在家具面上）。
        // 找到后地面采样直接对家具层打专用射线 —— 就事论事，物理背书。
        // 必须放在所有提前 return **之前**：她不在场时也要探测。
        var player0 = _objectTable?.LocalPlayer;
        if (player0 is not null && player0.Address != nint.Zero)
            NpcMaskProbe.Tick(_delta, player0.Position);

        // ── v0.18.50 游戏内天气 / 时段取样 ──
        // ⚠️ 必须放在所有提前 return **之前**：她不在场时面板也要显示当前天气与艾时。
        // 内部 1 秒节流，代价可以忽略。
        Core.NpcEnvironment.Update(_data, _delta);

        // ── #5 可被选中为目标（v0.18.35 引入，v0.18.37 重写）──
        //
        // 用户实测「刚召唤出来选不中，换区之后才行」—— 说明只写
        // TargetableStatus 的 IsTargetable 位（0x02）不够，游戏判定"能不能选"
        // 还看 ReadyToDraw(0x40) 等别的位。v0.18.37 起：
        //   ① 以**本地玩家为基准**补齐位（玩家必然可选中，所以照抄他的位最稳）；
        //   ② 把 TargetableStatus / 三个目标状态字节 / 距离 / 可见性 / GetIsTargetable()
        //      的「她 vs 玩家」对照落盘 target-trace.txt —— 换区前后各留一段，
        //      哪一位不一样就是哪一位在作祟。不靠猜。
        // 放在所有提前 return 之前：她刚被造出来、还在等点亮时也要保持。
        if (_npc is { } npcHandle && npcHandle.IsAlive && npcHandle.Address != nint.Zero)
        {
            var npcGo = (GameObject*)npcHandle.Address;
            var playerGo = player0 is not null && player0.Address != nint.Zero
                ? (GameObject*)player0.Address
                : null;

            NpcTargetable.Apply(npcGo, playerGo, _config?.TargetableByPlayer ?? true,
                                ourIndex: npcHandle.ObjectIndex);

            // v0.18.39 点击兜底：游戏认出"鼠标在她身上"却没把她设成硬目标时，
            // 我们替它把这一下接上（详见 NpcTargetable.TickClick 注释）。
            // v0.18.69.23 四轮：副 NPC 一起参与 —— 左键沿只读一次，悬停谁就接管谁。
            {
                var clickList = stackalloc GameObject*[4];
                var clickN = 0;
                if (_config?.TargetableByPlayer ?? true)
                    clickList[clickN++] = npcGo;
                if (SubNpcs is not null)
                    clickN += SubNpcs.FillTargetableList(clickList + clickN, 4 - clickN);
                if (clickN > 0)
                    NpcTargetable.TickClick(clickList, clickN);
            }

            // v0.18.41 根因取证：记录「选中她 → 被游戏自动取消」的瞬间状态（纯日志）
            NpcTargetable.TraceDeselect(npcGo);

            // v0.18.43 根因探针：ObjectArrays（GID 排序注册表）状态 —— 游戏选中
            // 走"按 GID 解析"，解析失败=点不中；SetHardTarget(NULL)=清目标。纯只读。
            NpcRegistryProbe.Tick(npcGo, playerGo);

            // v0.18.48《冒险者》取证：目标栏 addon 的文本节点 + 她的字段原始值（纯只读）。
            // v0.18.46/47 两轮都在她身上找《冒险者》全落空；exe 里也 0 命中 ⇒ 文本来自 exd，
            // 只能在 UI 层看清楚到底渲染了什么。落盘 target-ui.txt。
            NpcTargetUiProbe.Tick(npcGo);

            // 嗓音预设每帧保持：换装 / 模型重建会把 VoiceId 冲回外观来源的值。
            if (VoiceTuning.PresetId > 0)
                NpcVoice.ApplyPreset((Character*)npcHandle.Address, VoiceTuning.PresetId);

            // v0.18.49 气泡生命周期由我们托管：到点 / 换人（换区重召唤）就收回。
            NpcBalloon.Tick((Character*)npcHandle.Address);
        }
        else
        {
            // 她不在了（送走 / 判死）—— 气泡必须跟着一起没，绝不能挂在野指针上。
            NpcBalloon.Tick(null);
        }

        // ── 副 NPC 每帧驱动（v0.18.69.22）──
        // ⚠️ 必须放在主 NPC 的所有提前 return 之前：主 NPC 不在场 / 点亮中 / 判死帧，
        // 副 NPC 都要照常被驱动，否则主 NPC 一送走副 NPC 就全部僵住。
        SubNpcs?.Tick(FrameDelta);

        // ── 插件加载自动召唤（v0.18.69.20）──
        // 首帧一次性武装：复用换区重召唤的 _respawnTimer 通道 —— 它天然满足全部约束：
        // 玩家未进世界不召唤（条件里有 LocalPlayer）、手动送走 Dismiss 清零即取消、
        // 手动召唤 Summon 也清零即取消。之后与换区重召唤行为完全一致。
        if (_autoSummonArmed)
        {
            _autoSummonArmed = false;
            if (_config?.AutoSummonOnLoad == true && _respawnTimer <= 0f)
            {
                _respawnTimer = Math.Clamp(_config?.AutoRespawnDelay ?? 3f, 2f, 15f);
                _log.Information("[SmartNpc] 自动召唤已武装：进游戏世界后 {0:F1}s 召唤。", _respawnTimer);
            }
        }

        // ── 换区自动重召唤（v0.17.8）──
        // 必须在 _npc 空检查**之前**推进：判死后 _npc 就是空的。
        // 只在"没有 NPC、且不是手动送走的状态"下倒计时到点召唤。
        if (_respawnTimer > 0f)
        {
            _respawnTimer -= _delta;

            if (_respawnTimer <= 0f && _npc is null && _objectTable?.LocalPlayer is not null)
            {
                _log.Information("[SmartNpc] 自动重新召唤（换区跟随）。");
                Summon(announce: false);
            }
        }

        if (_npc is null || !_npc.IsAlive || _objectTable is null)
            return;

        var player = _objectTable.LocalPlayer;
        if (player is null || player.Address == nint.Zero)
            return;

        // 每帧把玩家地址喂给工厂：造人时的第③道闸要用它确认"拿到的不是玩家"。
        NpcFactory.LocalPlayerAddress = player.Address;

        // ── 住宅家具障碍层（v0.18.17，内部 0.5s 节流）──
        // 扫描圈以玩家为心：出生点 / 瞬移落点都围绕玩家，提前覆盖。
        // 放在点亮检查**之前** —— 点亮期间每帧提前 return，放后面会饿死扫描。
        NpcHousing.Tick(_delta, _objectTable, player.Position, _npc?.Address ?? nint.Zero);


        // 推进「点亮绘制」的分帧状态机。
        if (_spawnDraw is not null && _npc is not null)
        {
            if (!_spawnDraw.Step(_npc, out var ok, out var message))
                return;

            _spawnDraw = null;
            Diag?.Write(BuildDiagnostics(ok ? "点亮成功" : "点亮失败：" + message));

            // v0.18.37：点亮是"她真正出现在世界上"的那一刻 —— 强制留一行对照采样。
            NpcTargetable.Apply(
                (GameObject*)_npc.Address,
                player is not null && player.Address != nint.Zero ? (GameObject*)player.Address : null,
                _config?.TargetableByPlayer ?? true,
                forceTrace: true,
                ourIndex: _npc.ObjectIndex);

            // ⚠️ v0.18.17 站稳窗口：点亮一结束动画系统未必已接管（走路指令写进去
            // 也可能不生效），此刻开跑必然是"没腿的平移"。压 1.2 秒让她先站定。
            _settleTimer = SettleSeconds;

            // v0.18.21：站稳之后还要等"动画真的接上"才开始位移（见 _animWait 注释）。
            _animWait = 0f;

            // v0.18.18：同刻开始 5 秒平移取证（见 _slideTraceFrames 注释）。
            _slideTraceFrames = 300;

            if (ok)
            {
                _log.Information("[SmartNpc] {0} {1}", _npc, message);
                _chat?.Print($"[SmartNpc] {Core.NpcNaming.EffectiveName(_config?.NpcName, "她")}来了。");
            }
            else
            {
                // ⚠️ 失败时**不销毁**：留着她，面板上的「抄玩家外观 / 重置为裸装」
                // 还能再触发一次重建绘制，等于给一个不用重开游戏的重试入口。
                _log.Error("[SmartNpc] {0} 点亮失败：{1}", _npc, message);
                _chat?.PrintError($"[SmartNpc] 她没能出现：{message}。"
                                  + "可在面板里试「抄玩家外观」或「重置为裸装」再点一次。");
            }

            return;
        }

        var playerPos = player.Position;

        // ── 平移取证（v0.18.18）──
        // 放在点亮检查之后：点亮期间每帧提前 return，取证只需要"出现之后"的数据。
        if (_slideTraceFrames > 0 && _npc is not null)
        {
            _slideTraceFrames--;
            TraceSlideFrame(300 - _slideTraceFrames);
        }


        // ── 玩家实测速度 → 喂给移动层当基速 ──
        // 指数平滑（0.85 / 0.15）：单帧位移噪声（被顶了一下、传送落地、
        // 帧率抖动）不该让她的速度跟着抽搐。
        var rawSpeed = 0f;
        if (_hasPlayerPos)
        {
            var dxp = playerPos.X - _lastPlayerPos.X;
            var dzp = playerPos.Z - _lastPlayerPos.Z;
            rawSpeed = MathF.Sqrt(dxp * dxp + dzp * dzp) / MathF.Max(FrameDelta, 0.001f);
        }

        _hasPlayerPos = true;
        _lastPlayerPos = playerPos;
        _playerSpeed   = _playerSpeed * 0.85f + rawSpeed * 0.15f;

        // v0.17.9：保留未平滑的瞬时速度 —— 打断动作判定用它。
        // 平滑值起步要爬几百毫秒才过阈值，用户要的是"我一走她就断"。
        _playerRawSpeed = rawSpeed;

        // ── 玩家跳跃检测（v0.18.10：离地判定，修"上楼梯一直播起跳动作"）──
        //
        // v0.18.8 用垂直速度 >3 判跳 —— 跑步上楼梯/陡坡的垂直分速也有 3~4，
        // 于是上楼时每隔 0.5s 就误触发一次跳跃（用户实测"上楼梯会一直播放
        // 抽搐的起跳动作"）。
        //
        // 现在改成**离地判定**：跳跃的本质是"人离开地面"，爬坡永远贴地。
        //   grounded = 脚底离地面 <0.15 米；airborne = >0.35 米（中间保持原状态防抖）。
        //   触发 = 上一帧还在地面 + 这一帧离地 + 正在上升(vy>1.5)。
        // 走出悬崖边缘（离地但没在上升）不会触发 —— 她不该跟着你跳崖。
        if (_hasPlayerY && FrameDelta > 0f)
        {
            var playerVy    = (playerPos.Y - _lastPlayerY) / FrameDelta;
            _playerVySmooth = _playerVySmooth * 0.5f + playerVy * 0.5f;

            // 玩家脚下地面用只读探测（不写她的地面缓存，见 NpcGround.ProbeGround）
            var pg       = NpcGround.ProbeGround(playerPos.X, playerPos.Z, playerPos.Y);
            var height   = playerPos.Y - pg;
            var airborne = height > 0.35f;
            var grounded = height < 0.15f;

            if (_motion is { JumpEnabled: true }
                && !_motion.IsJumping
                && _wasPlayerGrounded && airborne
                && _playerVySmooth > 1.5f
                && rawSpeed < 8f
                && !(_condition?[ConditionFlag.Mounted] ?? false))
            {
                _motion.TriggerMirrorJump(playerVy);
            }

            if (grounded)      _wasPlayerGrounded = true;
            else if (airborne) _wasPlayerGrounded = false;
            // 中间地带保持原状态，防止贴地抖动时状态来回翻
        }
        _lastPlayerY = playerPos.Y;
        _hasPlayerY  = true;

        if (_motion is not null)
            _motion.PlayerSpeed = _playerSpeed;

        // ⚠️ v0.17.4：「移动中不做动作」在 brain 侧的执行信号。
        // ⚠️ v0.17.15：信号改成**玩家是否在移动**——她在走/被铁律摁停时都算，
        // "玩家在走还开口说话"就是打断循环的另一半。
        if (_brain is not null)
            _brain.SuppressGestureAndChatter =
                (_config?.BlockGesturesWhileMoving ?? true) && _playerRawSpeed > 0.25f;

        // 玩家的朝向（站位要相对**玩家朝向**算，不能相对 NPC 自己——那会绕圈）。
        var playerRotation = ((GameObject*)player.Address)->Rotation;

        // 她到玩家的水平距离：行为层要拿它做决策，移动层要拿它算速度。
        var here       = NpcMotion.ToNumerics(((GameObject*)_npc.Address)->Position);
        var toPlayer   = playerPos - here;
        var playerDist = new Vector3(toPlayer.X, 0f, toPlayer.Z).Length();

        // ⚠️⚠️ v0.17.7：每帧验证她真的还活着 —— 但必须用**游戏原生**的对象管理器。
        //
        // 血泪史：
        //   · v0.17.5 之前不验证 → 换区后缓存地址变野指针，IsAlive 只查"地址非零"
        //     照样 true → C0000005 崩了两回（crash-20261008180932 / 181414）。
        //   · v0.17.6 改用 Dalamud 的 IObjectTable 验证 → **它查不到（或地址对不上）
        //     我们自建的 EventNpc**，每帧都误判死亡 → 插件第一帧就停止驱动
        //     → 她呆立原地、一秒后"消失了请重新召唤"（用户实测）。
        //
        // ClientObjectManager 是**创建她的同一张表**（NpcFactory.TrySpawn 用的就是它），
        // GetObjectByIndex 与创建时同源，这才靠得住。
        //
        // 另外验证失败**不再立刻判死**：连续 60 帧（约 1 秒）都失败才算 ——
        // 换区瞬间对象表会有短暂空窗，立即判死会把"能自愈的抖动"升级成"人没了"。
        // 失败帧里不驱动、不碰任何指针，动画层保持冻结（防野指针写入）。
        var com = ClientObjectManager.Instance();
        var rawObj = com is null ? null : com->GetObjectByIndex(_npc.ObjectIndex);
        if (rawObj is null || (nint)rawObj != _npc.Address)
        {
            _invalidFrames++;
            _animator?.FreezeFor(2);

            if (_invalidFrames > 60)
            {
                _log.Warning("[SmartNpc] NPC 对象已失效（换区或被游戏清理），已停止驱动，请重新召唤。");
                _animator?.Silence();     // 只清托管状态，绝不碰原生字段
                _appearance = null;
                _motion     = null;
                _animator   = null;
                _brain      = null;
                _behaviour  = null;
                _spawnDraw  = null;
                _npc        = null;
                _invalidFrames = 0;
                NpcHousing.Reset();   // 家具圆缓存一并清掉（v0.18.17）
                _chat?.Print("[SmartNpc] 她在换区时消失了。");

                // ⚠️ v0.17.8：自动重召唤 —— 每次换区都要手动点一次太麻烦（用户反馈）。
                // 等区域加载稳定后在玩家身边重新出现。
                ResetAfkState();   // 换区：挂机坐姿/静音一并清零，重新计时
                if (_config?.AutoRespawnAfterZoneChange ?? true)
                {
                    _respawnTimer = Math.Clamp(_config?.AutoRespawnDelay ?? 3f, 1f, 15f);
                    _chat?.Print("[SmartNpc] 稍后她会自动跟过来。");
                }
            }

            return;
        }

        _invalidFrames = 0;

        // 每帧递减动画冻结计数（换区后进入的窗口期）。
        _animator?.TickFreeze();

        // 换区检测：她会被游戏搬到新区域，"玩家静止了多久"这个计时必须清零，
        // 不然换完区她会觉得玩家一直站着没动，立刻凑过来搭话（很蠢）。
        //
        // ⚠️⚠️ v0.17.6：旧版用 _npc.TerritoryId 做对比——那是**召唤时写进去的缓存**，
        // 之后从不更新，所以 `territory != _lastTerritory` 永远不成立，
        // 整个换区复位是死代码（这也是换区崩溃长期没被发现的原因之一）。
        // 现在直接用客户端状态的 TerritoryType。
        var territory = _clientState?.TerritoryType ?? 0;
        if (_lastTerritory == 0)
        {
            _lastTerritory = territory;
        }
        else if (territory != _lastTerritory)
        {
            _lastTerritory = territory;
            _behaviour?.NotifyZoneChanged();
            _brain?.ResetScene();

            // v0.18.50：天气 / 时段换区后重新取，并且不算"变化"（否则一进新图就连说两句）。
            Core.NpcEnvironment.ResetZone();
            ResetLineTriggers();

            // ⚠️ 移动层与避障层也要复位：
            //   · 不清速度 → 换完区她带着上一段的速度继续冲；
            _motion?.Reset();
            NpcGround.Reset();        // 上一张地图的地面高度不能拿来当参考
                                      //（避障层已无状态，v0.18.0 起无需复位）
            NpcHousing.Reset();       // 上一张地图的家具圆在新地图上不存在（v0.18.17）

            // ⚠️ 崩溃主修：动画模块进入冻结窗口 ——
            // 接下来 ~2 秒内 SetWalking / SetTurning / PlayByTimeline / Stop
            // 全部只清托管记账、不写任何原生字段（见 NpcAnimator.Frozen）。
            // loading 期间游戏正在销毁重建角色，此时碰它的动画系统 = C0000005。
            _animator?.Silence();

            _hasPlayerPos = false;
            _playerSpeed  = 0f;

            _log.Debug("[SmartNpc] 换区（#{0}），行为 / 移动状态已复位，动画冻结 2 秒。", territory);

            // 换区这一帧到此为止：对象表还不稳定，别继续驱动她。
            return;
        }

        // ── 决策 → 避障 → 执行 ──
        //
        // ⚠️ 三层的顺序是这个设计的全部意义，不能调换：
        //   行为层：决定「现在该干什么、去哪」，产出**世界坐标**的 MoveGoal；
        //   避障层：把「直线走过去」修正成「绕开挡路的东西」；
        //   移动层：把目标变成真实的位移 + 朝向 + 动画。
        // 旧版把决策和执行揉在一起时，决策刚算出的"想去的点"下一帧就被跟随公式
        // 覆盖掉了 —— 于是任何一点自主性都写不进去。
        if (_behaviour is not null)
        {
            _behaviour.TrackPlayerStillness(playerPos, FrameDelta);

            _behaviour.PlayerInCombat = _condition?[ConditionFlag.InCombat] ?? false;

            // 制作 / 采集时玩家正忙，她凑过来搭话很讨嫌（还会挡视野）→ 归为"劳作"。
            // ⚠️ 枚举名是 Crafting / ExecutingCraftingAction，**没有** IsCrafting（那是想当然）。
            // 制作台/采集点打开时是 Crafting+Gathering，动手过程中才切到 Executing*Action；
            // 两个都要查，只查一个会在"已经在做了"的时候漏判。
            _behaviour.PlayerIsCrafting =
                (_condition?[ConditionFlag.Crafting] ?? false)
             || (_condition?[ConditionFlag.ExecutingCraftingAction] ?? false)
             || (_condition?[ConditionFlag.Gathering] ?? false)
             || (_condition?[ConditionFlag.ExecutingGatheringAction] ?? false)
             || (_condition?[ConditionFlag.Fishing] ?? false);

            _behaviour.Update(playerDist, FrameDelta);

            // 关键：目标点是**世界坐标**，而且"选点"与"走向点"分离 ——
            // 选完就认准那个坐标走到底，下一帧不会重置。
            var goal = _behaviour.Goal(here, playerPos, playerRotation, playerDist);

            // 玩家自己就在跑 → 她也跟着小跑。
            // 不这样写的话，只有"被拉开"才会跑：站着不动时你一开跑，
            // 她还是走路姿态 → 播 walk 动画，人在平移腿在小步迈，很假。
            if (_playerSpeed >= Core.MotionTuning.RunGaitThreshold)
                goal = goal with { Gait = Core.Gait.奔跑 };

            // 朝向：站着时看着玩家（观望状态额外加一个来回摆的偏角，
            // 不然"环顾"只是播了个动作、身体还钉在你身上）；
            // 一旦走起来，移动层内部会改成面朝前进方向。
            //
            // ⚠️ v0.16.1 回滚：上一版把这条改成"身体别转、交给头部 LookAt"，
            // 实测**适得其反** —— 游戏并不为我们自造的 EventNpc 执行 LookAt 求解，
            // 头不会转，而身体又被禁止转 → 她彻底不看玩家了（比原来还差）。
            // 先恢复"整个身体对准玩家"这个至少能让她看着你的行为，
            // 真正的头部 IK 要按 AQuestReborn 的做法另做，不能靠猜字段硬写。
            var toPlayerYaw = MathF.Atan2(playerPos.X - here.X, playerPos.Z - here.Z);

            // v0.16.2：头/眼超转幅、且随机选中了"转身追" → 让她把身体真的转过去。
            // AllowFullTurn 会解除 NpcMotion 的站定限幅，于是她是转过去的（带转身动作），
            // 而不是被硬掰成一个平移旋转。
            var turnBody = _overflowMode == 2;

            if (_motion is not null)
                _motion.AllowFullTurn = turnBody;

            // ⚠️ v0.16.8：头/眼够得着时**身体保持不动**。
            // 原来无条件把 facing 设成"朝向玩家"，站定时身体会小幅旋转最多 ±54°（StandingTurnLimit），
            // 而这种小幅旋转**不播转身动作**（转身只在超转幅时才触发，用户明确要求的）
            // → 两者矛盾，观感就是"小角度旋转时概率平移"（是否落在那个区间内决定好坏）。
            //
            // 现在：只有超转幅且随机选中了"转身追"才转身体；
            // 其余时候身体维持原朝向，"看着你"完全交给头部 IK（TargetId）。
            var facing = turnBody ? toPlayerYaw
                                  : (_motion?.CurrentRotation ?? toPlayerYaw);

            if (_motion is not null)
            {
                // ⚠️ v0.17.1：动作与移动**互斥** ——
                // 边做动作边移动 = 表情通路(AnimLock+BaseOverride)和走路动画互相抢 = 必然平移。
                // 规则：做动作就不移动；一旦需要跟随（离你超过到位距离）就打断动作。
                var actionBusy = _animator?.IsBusy ?? false;

                // ⚠️⚠️ v0.17.4 铁律：动作播放期间**永远**不允许移动 —— 无条件。
                // 用户原话："仍然不允许边走边做"。
                // 这条不是开关能关掉的：表情通路(AnimLock+BaseOverride)和走路动画
                // 互相抢的结果必然是平移，所以"做动作→人停下"是物理层面的强制，
                // 开关只决定**动作什么时候开始 / 什么时候被打断**（见下面两个开关）。
                //
                // v0.18.17：站稳窗口（点亮刚结束）同样禁止移动 —— 那个窗口里走路
                // 动画还没接管，一动就是"没腿的平移"（换区重召唤后最明显）。
                var settling = _settleTimer > 0f;
                if (settling)
                {
                    _settleTimer -= FrameDelta;

                    // v0.18.19：玩家已经走远/在移动 → 提前结束站稳。
                    // slide-trace 实锤：她站满 1.2 秒的代价是差距拉开到 6 米，
                    // 之后必然是一段 9 m/s 的滑行冲刺。她出现时玩家根本没在看
                    // "站姿"，看的是"赶紧跟上"——差距控制优先于站稳仪式感。
                    if (playerDist > 2.5f || _playerRawSpeed > 0.8f)
                        _settleTimer = 0f;
                }

                // ⚠️⚠️ v0.18.21 动画接管控闸：站稳窗口到期**不直接放行** ——
                // 先问动画层"我写进去的值留住了吗"，没留住就继续站（最多 4 秒）。
                // 这是 v0.18.17~20 一直差的那 1 秒：靠固定时间永远猜不准模型何时就绪。
                var animReady = _animator?.AnimationConfirmed ?? false;
                if (!animReady && _animWait < AnimWaitMaxSeconds)
                    _animWait += FrameDelta;

                var waitingAnim = !animReady && _animWait < AnimWaitMaxSeconds;

                // v0.18.69.21：挂机中冻结移动驱动 —— Enabled=false 仍是「跟随」态，
                // 她与跟随点有半米内偏差时会走两步微调 → ApplyAuthority 移动接管
                // 清掉坐姿循环基底 → "坐下去又站起来"。挂机 = 钉在原地，自愈兜底。
                _motion.Paused = _afkActive
                                 || settling || waitingAnim
                                 || (_brain?.IsSpeaking ?? false) || actionBusy;


                // ⚠️ 动作刚结束的那一帧：清掉时间轴记账。
                // StopAction 清了 BaseOverride，但 _locoTimeline 记账还留着旧值，
                // SetWalking 会误判"已在播"而不重播 → 动作切回跟随的瞬间平移。
                if (_wasActionBusy && !actionBusy)
                    _animator?.ResetLocomotionBookkeeping();
                _wasActionBusy = actionBusy;

                //
                // ⚠️ v0.18.2：「移动起来就打断动作」的开关与整段打断逻辑**已删除**。
                //
                // 删它的理由不是它"不好用"，而是它<b>在结构上必然制造抖动</b>：
                //   打断 → 动作计时归零 → 下面铁律 `Paused = actionBusy` 解除
                //        → 她重新判定为"在移动" → 而插动作的门槛要求"她站着"
                //        → 但她已经站住了 → 立刻插新动作 → 又被打断 → ……
                // trace-action.txt 里每 1~2 秒一个 PLAY 的循环就是这个。
                //
                // 现在语义干净得多：<b>做动作就站着做完</b>，做完自然接着走。
                // 代价是"玩家已经跑了、她还在把 2 秒的点头做完"才开始跟，
                // 但这个代价换来的是不会抽搐，用户明确选择了这条（"你完全不会写这个"）。
                // 真正的"跟不上"兜底改由 NpcMotion.StuckTeleportSeconds 负责。
                //

                // 警戒时步子放慢一点 —— 不是跑，是"贴过来但别冲过来"。
                _motion.SpeedScale = _behaviour.Current == NpcBehavior.Behaviour.警戒 ? 0.85f : 1f;

                // ⚠️ v0.18.2：喂玩家位置 —— "卡住太久瞬移到你身边"的落点靠它。
                _motion.PlayerPosition = playerPos;
                _motion.PlayerRotation = player.Rotation;   // v0.18.4：出生点式落点的朝向基准

                _motion.Update(_npc, goal, facing);
            }
        }

        // 外观：推进"重建绘制对象"的分帧流程。
        _appearance?.TickRebuild();

        // 她真的在动吗？动了就播对应姿态的动画，并把播放速度对上实际速度（否则是太空步）。
        // ⚠️ 第三个参数是姿态：走和跑必须切**不同的时间轴**，
        // 用走路动画跑 = 腿迈小步、身体平移，比没有动画更假。
        // 放在 motion 之后、brain 之前：表情优先级更高，brain 里可能临时插一个动作。
        // 转身动作：站定原地转时播一条转身动画（用户要"旋转得有动作"，不是干滑）。
        // 必须在 SetWalking 之前：转身优先于待机；停下转身时由这里收尾回到待机，
        // 随后 SetWalking 的守卫会发现"已在播待机"而不会重播。走起来时转身自动让位给走/跑。
        if (_motion is not null && _animator is not null)
        {
            // ⚠️ v0.18.1：转身动画的触发条件扩展为「站定 + 移动层判定需要明显原地转身」。
            // 旧实现只在「头/眼追不上 + 随机选中'转身追'」(_overflowMode==2) 时才播，
            // 于是正常跟随里她停下转正时永远不播转身动作（用户反馈"转身的动作直接没了"）。
            // 现在改用 NpcMotion 每帧算出的 TurningInPlace / TurnDirection：
            // 站定且需转超过 TurnAnimMinAngle（≈29°）就播左右转身动作，
            // 身体平滑转正的小角度仍不播（仍是平滑旋转，不硬切动作）。
            var turning = !_motion.IsMoving && _motion.TurningInPlace;

            _animator.SetTurning(turning ? _motion.TurnDirection : 0);
        }

        // 走路 / 奔跑 / 待机动画（跟随 / 移动的执行层算出来的姿态）。
        if (_motion is not null && _animator is not null)
            _animator.SetWalking(_motion.IsMoving, _motion.CurrentSpeed, _motion.CurrentGait);

        //
        // ⚠️⚠️ v0.18.0 动画权威出口 —— 每帧无条件落地一次。
        //
        // 放在全帧<b>最后</b>（所有记账都写完、brain 也插完动作之后）：
        // 这样它读到的"当前该播什么"才是本帧的最终结论，
        // 也就不存在"两条通路谁后跑谁生效"的竞争。
        //
        // 这一行是整个"打断"与"边走边做"问题的答案：
        //   · 移动时 → 只写走路时间轴 → 表情字段即便残留也不可见（无法边走边做）
        //   · 停下且计时归零（被打断）→ 立刻落回待机 → 表现为"动作被截停"
        //   · 停下且有计时 → 写表情时间轴 → 表情真的播出来
        //
        if (_motion is not null && _animator is not null)
            _animator.ApplyAuthority(_motion.IsMoving, _motion.CurrentSpeed, _motion.CurrentGait);

        // 头部 / 视线：站定时让她用头/眼睛看向你（而不是整个人拧过去）。
        // 走起来时收回来，免得边走边扭头。
        ApplyHeadLook(playerDist, playerPos, player.EntityId);

        // ── 逐帧诊断流水 ──
        // ⚠️ 默认**关闭**（v0.16.9：定位完成后按要求停用并删除了 turn-trace.txt）。
        // 需要复查"动画被谁清掉"时，在设置里打开「转身逐帧诊断」即可重新生成。
        if (_config?.TurnTraceDebug ?? false)
        {
            if (_turningActive)
                _turnTraceTail = 20;

            if (_turningActive || _turnTraceTail > 0)
            {
                TraceTurn();
                if (!_turningActive)
                    _turnTraceTail--;
            }
        }

        // 待机 / 观望时随机插一个小动作，让她看起来是"活着的"。
        // ⚠️ 必须在走路动画<b>之后</b>判断：站着才播小动作，走路时播动作会互相打断。
        //
        // ⚠️ v0.17.2：这里插完动作后必须**立刻再刹一次车**。
        // 移动（_motion.Update）在帧中段已经跑完了，那时 `Paused` 还是 false，
        // 于是她这一帧已经把位移走出去了 —— 表情是帧末才播的，
        // 表现就是"她又边走边做动作了"。
        // 补一句 Paused=true 至少能让**下一帧**立刻停住（原本要等一整帧，
        // 期间还在滑）。真正彻底的解决方案是让闲聊在移动之前问"我能不能现在开口"，
        // 那要求把 brain 的计时挪到帧首，风险大，先这样挡住观感。
        // 待机 / 观望时随机插一个小动作，让她看起来是"活着的"。
        //
        // ⚠️⚠️ v0.17.15（trace-action.txt 实锤的"边走边做动作"真凶）：
        // 「移动打断动作」开启时，这里必须同时看**玩家是否在移动**——
        //
        //   她做动作 → 玩家走 → 打断（A）→ 她被铁律摁停的那一帧 IsMoving=false
        //   → 本门槛"站着才插"恰好满足 → 立刻插新动作 → 又被打断 → 又插……
        //
        //   trace-action.txt 里每 1~2 秒一个 PLAY（点头/行礼/挠头轮播）就是这个循环。
        //   A 开关的完整语义是"移动起来就打断 + **不再插新的**"，缺了后半句就是拉锯。
        //
        // 玩家停下后（rawSpeed 回落），小动作自然恢复。
        var blockWhileMoving = _config?.BlockGesturesWhileMoving ?? true;
        var playerMoving = _playerRawSpeed > 0.25f;

        var gestureGate = blockWhileMoving
            ? (_motion is { IsMoving: false } && !playerMoving)   // 站定 且 玩家没在走
            : !playerMoving;                                       // A 开：玩家走时永不插

        if (_behaviour is not null && _animator is not null
            && gestureGate
            && !_animator.IsTurning   // ⚠️ 转身途中别插小动作：它走表情通路，会把转身动作冲掉
            && _behaviour.WantsIdleGesture(FrameDelta))
        {
            _animator.PlayIdleGesture((int)(_brain?.CurrentPersona ?? NpcBrain.Persona.元气));

            // 动作刚开始播 → 立刻禁止移动，别让她带着这个姿态继续滑。
            if (_motion is not null && blockWhileMoving)
                _motion.Paused = true;
        }

        // 被甩开了（瞬移跟上）→ 立刻说一句抱怨，而不是等下一次闲聊计时。
        if (_npc.NeedsTeleport && !_wasTeleported)
            _brain?.NoticeTeleported();

        _wasTeleported = _npc.NeedsTeleport;

        // 情境反应：看她对玩家在做的事有没有反应（战斗 / 劳作 / 久站 / 落后）。
        // ⚠️ 用 _wasTeleported 判"落后"：它在 NoticeTeleported 之后才被更新，
        // 这里读到的正是"这一帧刚发生了瞬移"，正是我们要的语义。
        // 放在最后，因为它依赖本帧算出的距离、行为状态与瞬移标记。
        // ── v0.18.69.21 挂机模式：先推进检测（可能刚进入/退出），再用它做台词闸门 ──
        TickAfkMode(playerPos, FrameDelta);

        if (_brain is not null && _behaviour is not null && !_afkActive)
        {
            // 她正在说话时保持当前情境不变，否则一句台词会被判成"情境变了"而打断。
            var lastScene = _brain.CurrentScene;

            var scene = _behaviour.PlayerInCombat   ? NpcBrain.Scene.战斗
                      : _behaviour.PlayerIsCrafting  ? NpcBrain.Scene.劳作
                      : _wasTeleported               ? NpcBrain.Scene.落后
                      : _brain.IsSpeaking             ? lastScene
                      : _behaviour.StateElapsed > 30f ? NpcBrain.Scene.久站
                      :                                NpcBrain.Scene.平静;

            _brain.CurrentScene = scene;

            if (_config?.SceneReactions ?? true)
                _brain.UpdateSceneReaction(scene, FrameDelta);
        }

        // ── v0.18.50：天气 / 时段 / 战斗 / 互动 的台词触发 ──
        // 放在情境反应之后：那边是"大情境"，这边是"具体事件"，
        // 两边共用 NpcBrain 的说话通道，谁先谁后只影响同一帧里谁抢到嘴。
        // ⚠️ 挂机模式下全部静默（v0.18.69.21）——她坐着休息，一句话都不说。
        if (!_afkActive)
        {
            UpdateEnvironmentLines();
            UpdateJobLines();
            UpdateCombatAndTouchLines(here, playerPos, playerRotation, playerDist);
            UpdatePlayerEmoteReaction();
        }

        // v0.18.53 说话开合：她此刻在不在说话由 NpcBrain 说了算，
        // 动画层只在**口型值真的变了**时才调游戏那个入口（绝不每帧写）。
        _animator?.UpdateLipSync(_brain?.IsSpeaking ?? false, FrameDelta);

        _brain?.Update(_npc, playerPos, FrameDelta);
    }

    // ── v0.18.50 台词触发器 ────────────────────────────────────────
    //
    // 控制器只做一件事：**判定发生了什么**；"这句话该怎么说"全在 NpcBrain / NpcLines。
    // 每个触发器都是「沿 + 冷却」：只在状态翻转的那一帧开口，绝不会每帧都判成新事件。

    private Core.NpcLines.Weather  _seenWeather = Core.NpcLines.Weather.未知;
    private Core.NpcLines.DayPhase _seenPhase  = Core.NpcLines.DayPhase.白天;
    private bool  _envPrimed;
    private bool  _wasInCombat;
    private float _combatChatTimer;
    private float _lastHpPercent = 1f;
    private bool  _hasHp;
    private bool  _wasTargeted;
    private float _gazeHold;
    private float _closeHold;

    // ── v0.18.51 职业切换 ──
    private uint  _lastJobId;      // 上次开口时玩家是哪个职业（ClassJob 行号）
    private bool  _jobPrimed;      // false = 还没记过"当前职业"，首次只记不说
    private float _jobCheckTimer;  // 不必每帧查表，0.4 秒看一次
    private string _lastJobName = string.Empty;

    /// <summary>换区 / 重新召唤时把触发器的记忆清掉。</summary>
    private void ResetLineTriggers()
    {
        _envPrimed        = false;
        _wasInCombat      = false;
        _combatChatTimer  = 0f;
        _hasHp            = false;
        _lastHpPercent    = 1f;
        _wasTargeted      = false;
        _gazeHold        = 0f;
        _closeHold        = 0f;

        // 职业记忆也一起清：换区后重新"认一次"当前职业，不该立刻冒一句。
        _jobPrimed     = false;
        _jobCheckTimer = 0f;
        _lastJobId     = 0;
        _lastJobName   = string.Empty;
    }

    /// <summary>
    /// 玩家换了职业 → 说一句跟那个职业有关的话（v0.18.51）。
    ///
    /// <para>⚠️ <b>触发纪律</b>（用户明确要求）：切换后只触发一次，
    /// 下一次开口必须是切到了<b>与目前不同</b>的职业。
    /// 所以这里比的是职业 id：<c>jobId == _lastJobId</c> 直接返回 —— 一句都不说。</para>
    /// </summary>
    private void UpdateJobLines()
    {
        if (_brain is null)
            return;

        // 不用每帧查表：换职业最快也要几秒，0.4 秒看一次绰绰有余。
        _jobCheckTimer -= FrameDelta;
        if (_jobCheckTimer > 0f)
            return;
        _jobCheckTimer = 0.4f;

        uint jobId;
        string jobName;

        try
        {
            // ⚠️ 用 Dalamud 的<b>托管属性</b>，不去猜 Character+0x?? 的偏移（v0.18.42 的 C0000005 教训）。
            // ⚠️ 而且它不是 byte —— 这个版本的 ICharacter.ClassJob 是 **RowRef<ClassJob>**，
            // 行号在 .RowId、名字在 .Value.Name，省得自己再查一遍表。
            var player = _objectTable?.LocalPlayer;
            if (player is null)
                return;

            var row   = player.ClassJob;
            jobId     = row.RowId;
            jobName   = JobNameOf(row);
        }
        catch
        {
            return;
        }

        // 0 = 还没进角色 / 读不出来。别把它当成"换职业了"。
        if (jobId == 0)
            return;

        // 第一次只记不说：刚进游戏就知道你是骑士，不该张口就来一句。
        //
        // ⚠️ v0.18.52：**她不在场时也照记**（只更新记忆、不开口）。
        // 旧版把整个检查放在"她必须在场"的分支里，于是你趁她不在时换职业、
        // 再把她叫出来，她会拿"旧职业→新职业"补一句 —— 那句话你已经听不到了，很怪。
        // 现在任何时候都同步记忆，只有她确实在身边时才真的说。
        if (!_jobPrimed)
        {
            _jobPrimed   = true;
            _lastJobId   = jobId;
            _lastJobName = jobName;
            return;
        }

        if (jobId == _lastJobId)
            return;

        var changed = _lastJobId != 0;      // 从"未知"恢复过来不算换职业
        _lastJobId   = jobId;
        _lastJobName = jobName;

        if (!changed || !(_config?.JobLines ?? true))
            return;

        if (_npc is null || !_npc.IsAlive)
            return;                          // 她不在场：只记账，不开口

        // v0.18.52：名字解析有三层兜底（职业名 → 职能 → 行号表），
        // 全都落空才用最普通的"换了吗"那句。详情见 NpcJobLines.ResolveJob。
        var job = Core.NpcJobLines.ResolveJob(jobId, jobName);

        var pool = Core.NpcJobLines.Lines(_brain.CurrentPersona, job)
                ?? Core.NpcJobLines.RoleLines(_brain.CurrentPersona, Core.NpcJobLines.RoleOf(job))
                ?? Core.NpcJobLines.GenericLines(_brain.CurrentPersona);

        if (Core.NpcJobLines.LastResolved.Length > 0)
            LogJobResolved(jobId, job);

        _brain.OnJob(job, pool);
    }

    /// <summary>
    /// 把"行号 → 职业名"的解析结果写进诊断（v0.18.52）。
    ///
    /// <para>用户反馈"生产采集类总是触发无指向的通用句"，说明名字没对上。
    /// 与其猜，不如<b>每次换职业都把行号和解析出来的名字记下来</b>，
    /// 一眼就能看出是表里的 key 写错了，还是客户端返回的名字不一样。</para>
    /// </summary>
    private void LogJobResolved(uint jobId, string resolved)
    {
        var note = string.Concat("[SmartNpc] 职业切换: 行号=", jobId.ToString(),
                                 " Lumina名=\"", _lastJobName, "\" 解析为=\"", resolved,
                                 "\" 来源=", Core.NpcJobLines.LastResolved);

        _log.Information(note);
        Diag?.Append(note);
    }

    // ── v0.18.53：玩家对她做表情动作 → 她有反应 ────────────────────

    private ushort _lastPlayerEmoteId;
    private float  _emoteProbeTimer;
    private float  _emoteReactCooldown;

    /// <summary>玩家上一次对她做的动作（诊断显示用）。</summary>
    public string LastPlayerEmote { get; private set; } = string.Empty;

    /// <summary>
    /// 每 0.15 秒探一次玩家是不是新起了一个表情动作，并且<b>当前目标是她</b>。
    ///
    /// <para>⚠️ <b>必须选中她</b>（用户原话："一定要选中她时"）：玩家没选中她就做动作，
    /// 一句都不说 —— 那不是对她说，她凭什么接话。</para>
    /// </summary>
    private void UpdatePlayerEmoteReaction()
    {
        if (_brain is null)
            return;

        _emoteReactCooldown -= FrameDelta;
        _emoteProbeTimer    -= FrameDelta;

        if (_emoteProbeTimer > 0f)
            return;

        _emoteProbeTimer = 0.15f;

        if (_npc is not { IsAlive: true })
            return;

        if (!(_config?.EmoteReactions ?? true))
            return;

        var player = _objectTable?.LocalPlayer;
        if (player is null || player.Address == nint.Zero)
            return;

        ushort emoteId;

        try
        {
            // Character +0x630 EmoteController，其中 +0x14 = EmoteId（dnfile 实锤）。
            emoteId = ((Character*)player.Address)->EmoteController.EmoteId;
        }
        catch
        {
            return;
        }

        // 同一个动作不算"又做了一次"；动作收尾（归 0）也不算。
        if (emoteId == 0 || emoteId == _lastPlayerEmoteId)
        {
            _lastPlayerEmoteId = emoteId;
            return;
        }

        _lastPlayerEmoteId = emoteId;

        if (!IsPlayerTargetingHer())
            return;

        if (_emoteReactCooldown > 0f)
            return;

        var name = NpcAnimator.EmoteNames.TryGetValue(emoteId, out var n) ? n : string.Empty;

        // v0.18.69.11：忽略名单（/pose「改变姿势」等）—— 不是对她做的互动，
        // 不说话、不比划、不占冷却。放在归类之前，防止台词窜台。
        if (Core.NpcEmoteReactions.IsIgnored(name))
            return;

        // v0.18.69.9：台词归类走 KindFor（覆盖表先行）—— 与回应动作共用同一套逐动作语义，
        // 否则会出现"她敬礼却说'递毛巾'"的错位。
        var kind = Core.NpcEmoteReactions.KindFor(name);

        var pool = Core.NpcEmoteReactions.Lines(_brain.CurrentPersona, kind)
                ?? Core.NpcEmoteReactions.Lines(_brain.CurrentPersona, NpcLines.React.通用);

        if (pool is null)
            return;

        _emoteReactCooldown = 3f;      // 防连击：连做两个动作不会连说两句
        LastPlayerEmote     = name.Length > 0 ? name : "#" + emoteId;

        _brain.OnEmote(name, pool);

        // v0.18.62：回应走**语义映射表**（用户否决"一律镜像"）—— 每个动作都要有
        // "合适"的回应：军礼回军礼、跳舞回拍手、亲昵回害羞、攻击回害怕、声援回加油……
        // 没有专属对应时兜底点头（绝不做"她也做同一个动作"的盲镜像）。
        var respName = Core.NpcEmoteReactions.ResponseEmoteNameFor(name);
        var respRow  = _animator?.FindEmoteRow(respName) ?? 0;

        var respOk = respRow != 0
                     && _animator is not null
                     && _animator.PlayResponseEmote(respRow, _animator.EmoteLoopTimelineOf(respRow));

        // 兜底：管线回应没成（行不支持等）→ 走旧的动作通路比划一下。
        if (!respOk)
        {
            var resolved = _animator?.ResolveActionName(Core.NpcEmoteReactions.GestureFor(kind));
            if (resolved is not null && _animator is not null)
                _animator.Play(resolved, Core.NpcGesture.DurationFor(resolved));

            respRow = 0;
        }

        var note = string.Concat("[SmartNpc] 玩家对她做动作: 行号=", emoteId.ToString(),
                                 " 名=\"", LastPlayerEmote, "\" 归类=", kind,
                                 " 回应PlayEmote=", respRow != 0 ? respRow.ToString() : "（走旧动作通路）");
        _log.Information(note);
        Diag?.Append(note);
    }

    /// <summary>
    /// 玩家的<b>硬目标</b>是不是她。
    ///
    /// <para>走 <c>TargetSystem.Instance()->Target</c>（+0x80，dnfile 实锤字段名 <c>Target</c>），
    /// 直接跟她的地址比 —— 比 EntityId 更直接，不怕对象表索引那一层出岔子。</para>
    ///
    /// <para>⚠️ 取不到 TargetSystem / 她不在场 → <b>false</b>：宁可不说，也不要乱接话。</para>
    /// </summary>
    private bool IsPlayerTargetingHer()
    {
        if (_npc is not { IsAlive: true })
            return false;

        try
        {
            var ts = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
            if (ts is null)
                return false;

            var hard = ts->Target;
            return hard is not null && (nint)hard == _npc.Address;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>动作名清单只落一次盘（每次召唤都写一遍没必要）。</summary>
    private static bool _actionsDumped;

    /// <summary>
    /// 把 Emote 表里<b>所有</b>真实动作名写成 <c>actions.txt</c>（v0.18.52）。
    ///
    /// <para>为什么要这一份：语义动作匹配（<see cref="Core.NpcGesture"/>）只能拿
    /// "表里真的存在"的名字去配。用户点名要用「手抚胸 / 飞吻 / 表达爱意 / 激励 /
    /// 表情：微笑」这几个，我得先确认客户端里的名字<b>一字不差</b>是什么 ——
    /// 猜错了不会崩，只会导致这条规则<b>永远匹配不上</b>（退化成只说话），
    /// 这种"静默失效"最难查。所以落一份全表，一眼就能对。</para>
    ///
    /// <para>只写一次，写失败就算了（不影响任何功能）。</para>
    /// </summary>
    private void DumpActionNames()
    {
        if (_actionsDumped || Diag is null)
            return;

        _actionsDumped = true;

        try
        {
            var names = _animator?.AvailableActions;
            if (names is null || names.Count == 0)
                return;

            var body = new System.Text.StringBuilder();
            body.Append("SmartNpc 动作表（Emote 表真实名字，共 ").Append(names.Count).AppendLine(" 个）");
            body.AppendLine("写入时间=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            body.AppendLine(new string('-', 60));

            foreach (var n in names)
                body.AppendLine(n);

            System.IO.File.WriteAllText(
                System.IO.Path.Combine(Diag.DirectoryPath, "actions.txt"),
                body.ToString(),
                new System.Text.UTF8Encoding(false));

            // 用户点名的那几个到底在不在表里 —— 直接标出来。
            var wanted = new[] { "手抚胸", "害羞", "飞吻", "表达爱意", "激励", "表情：微笑", "表情：幸福", "表情：担心" };
            var found = new System.Text.StringBuilder("[SmartNpc] 动作核对：");

            foreach (var w in wanted)
            {
                var hit = string.Empty;

                foreach (var n in names)
                {
                    if (n.Contains(w, StringComparison.Ordinal))
                    {
                        hit = n;
                        break;
                    }
                }

                found.Append(w).Append(hit.Length > 0 ? "=有(" + hit + ")" : "=无").Append("  ");
            }

            Diag.Append(found.ToString());
        }
        catch
        {
            // 落盘失败无所谓
        }
    }

    /// <summary>从 <c>RowRef&lt;ClassJob&gt;</c> 里取职业名（取不到返回空串，由兜底台词接手）。</summary>
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

    /// <summary>游戏内天气 / 时段变化 → 说一句。</summary>
    private void UpdateEnvironmentLines()
    {
        // 取样已经在 OnFrameworkUpdate 最前面跑过了（她不在场时也要跑），
        // 这里只负责"变了没有 → 要不要开口"。
        if (_brain is null)
            return;

        var weather = Core.NpcEnvironment.Weather;
        var phase   = Core.NpcEnvironment.Phase;

        // 第一次只记不说话：刚进游戏就读到一个天气，不该张口就来一句"下雨了"。
        if (!_envPrimed)
        {
            _envPrimed   = true;
            _seenWeather = weather;
            _seenPhase   = phase;
            return;
        }

        if (weather != _seenWeather)
        {
            _seenWeather = weather;

            // 特殊天气（副本异象等）：默认只记不开口 —— 在没有雨的副本里喊"下雨了"很出戏。
            // 打开了 SpecialWeatherLines 才沿用那个名词说一句。
            var special   = weather is Core.NpcLines.Weather.其他 or Core.NpcLines.Weather.未知;
            var allowKind = !special || (_config?.SpecialWeatherLines ?? false);

            if (allowKind && (_config?.WeatherLines ?? true))
            {
                // v0.18.51：优先说**带真实天气名词**的那句（"今天真是碧空如洗呢~！"）。
                // 名词取不到时才退回按晴/雨/雪归类的关心台词。
                var noun = Core.NpcEnvironment.WeatherNoun;

                var said = (_config?.WeatherNameLines ?? true)
                        && !string.IsNullOrWhiteSpace(noun)
                        && _brain.OnWeatherName(noun);

                if (!said && !special)
                    _brain.OnWeather(weather);
            }
        }

        if (phase != _seenPhase)
        {
            _seenPhase = phase;

            if (_config?.DayPhaseLines ?? true)
                _brain.OnDayPhase(phase);
        }
    }

    /// <summary>
    /// 战斗阶段（进入 / 交战中 / 受击 / 胜利 / 撤退）+ 玩家互动（选中 / 注视 / 靠近）。
    /// </summary>
    private void UpdateCombatAndTouchLines(Vector3 here, Vector3 playerPos, float playerRotation, float playerDist)
    {
        if (_brain is null || _behaviour is null || _npc is null)
            return;

        var player = _objectTable?.LocalPlayer;
        if (player is null)
            return;

        var dt = FrameDelta;

        // ── 血量 ──────────────────────────────────────────────
        var maxHp     = Math.Max(1u, player.MaxHp);
        var hpPercent = Math.Clamp((float)player.CurrentHp / maxHp, 0f, 1f);

        if (!_hasHp)
        {
            _hasHp         = true;
            _lastHpPercent = hpPercent;
        }

        var drop = _lastHpPercent - hpPercent;

        // ── 战斗阶段 ──────────────────────────────────────────
        var inCombat    = _behaviour.PlayerInCombat;
        var combatLines = _config?.CombatLines ?? true;

        if (inCombat && !_wasInCombat)
        {
            if (combatLines)
                _brain.OnCombat(Core.NpcLines.Combat.进入);

            _combatChatTimer = 9f;   // 进战斗先安静一会儿，别和开场白撞车
        }
        else if (!inCombat && _wasInCombat)
        {
            // 脱战：血量难看算"撤退"，还算健康就是"胜利"。
            if (combatLines)
                _brain.OnCombat(hpPercent < 0.5f
                    ? Core.NpcLines.Combat.撤退
                    : Core.NpcLines.Combat.胜利);
        }
        else if (inCombat)
        {
            _combatChatTimer -= dt;
            if (_combatChatTimer <= 0f)
            {
                _combatChatTimer = 14f;

                if (combatLines)
                {
                    // v0.18.51：原来的「暴击 / 直击专属夸赞」检测不到（要 hook ActionEffect 包，
                    // 成本高且易随版本失效），按用户裁决**直接并入普通战斗触发** ——
                    // 交战期间一半说助威、一半说夸赞。
                    _brain.OnCombat(Random.Shared.Next(2) == 0
                        ? Core.NpcLines.Combat.攻击
                        : Core.NpcLines.Combat.称赞);
                }
            }
        }

        _wasInCombat = inCombat;

        // ── 掉血 → 关心 ────────────────────────────────────────
        // 一次掉 12% 以上才算"挨了一下"，否则小怪蹭一下就会一直念。
        if (drop >= 0.12f && (_config?.TouchLines ?? true))
            _brain.OnTouch(Core.NpcLines.Touch.受伤关心, 25f);

        _lastHpPercent = hpPercent;

        // ── 玩家把她选成目标 ───────────────────────────────────
        var targeted = Plugin.TargetManager is { } tm
                    && tm.Target is { } t
                    && t.Address == _npc.Address;

        if (targeted && !_wasTargeted && (_config?.TouchLines ?? true))
            _brain.OnTouch(Core.NpcLines.Touch.被选中, 15f);

        _wasTargeted = targeted;

        // ── 玩家在看她 / 贴得很近 ──────────────────────────────
        //
        // 「被注视」= 玩家**面朝**她、距离适中、而且站住了（转身路过的不算）。
        // 她平时跟在身后，所以你要真的回头看她，才会触发——这正是我们要的语义。
        var toNpcYaw = MathF.Atan2(here.X - playerPos.X, here.Z - playerPos.Z);
        var facing   = MathF.Abs(NormalizeAngle(toNpcYaw - playerRotation)) < 0.6f;   // ±35°
        var still    = _playerRawSpeed < 0.25f;
        var inRange  = playerDist is > 0.6f and < 8f;

        if (facing && still && inRange)
            _gazeHold += dt;
        else
            _gazeHold = 0f;

        // v0.18.51：贴近距离改为可自定义（配置 CloseTouchDistance，默认 2 米）。
        var closeDist = Math.Clamp(_config?.CloseTouchDistance ?? 2f, 0.05f, 8f);

        if (still && playerDist < closeDist)
            _closeHold += dt;
        else
            _closeHold = 0f;

        // 两个都满足时优先说"被靠近"（那是更明确的互动），并且**一帧只说一句**。
        if (_closeHold > 3f)
        {
            _closeHold = 0f;
            _gazeHold = 0f;

            if (_config?.TouchLines ?? true)
                _brain.OnTouch(Core.NpcLines.Touch.被靠近, 60f);
        }
        else if (_gazeHold > 4f)
        {
            _gazeHold = 0f;

            if (_config?.TouchLines ?? true)
                _brain.OnTouch(Core.NpcLines.Touch.被注视, 45f);
        }
    }

    /// <summary>把角度差折到 -π ~ +π。</summary>
    private static float NormalizeAngle(float a)
    {
        while (a > MathF.PI)  a -= 2f * MathF.PI;
        while (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
    }

    /// <summary>
    /// 站定时把"允许看向镜头"打开，让她的头 / 视线跟着你（第三人称下镜头就在你背后）。
    /// </summary>
    // ── v0.16.2：头 / 眼超转幅时的二选一状态 ──
    // 0=没超转幅  1=放弃追踪  2=转身追（身体转，带左右转身动作）
    private int   _overflowMode;
    private float _overflowTimer;
    private int   _overflowDir;   // 超转幅时该往哪边转（+1 / -1）

    // ──「始终注视玩家」关闭时的占视状态（#2）──
    // 占比与时长全部面板可调（Configuration.GazeLookPercent / GazeLookMin/Max /
    // GazeAwayMin/Max，v0.18.34），整段保持再重掷（见 ApplyHeadLook 内注释）。
    private bool  _gazeOn    = true;    // 当前是否处于"在看你"的时段
    private float _gazeTimer;           // 本时段剩余秒数（≤0 = 该重掷了）

    /// <summary>是否已经进入"转身追"且还没转完 —— 用于迟滞，避免半途掐掉转身动画。</summary>
    private bool  _turningActive;
    private float _turningTimeout;  // 转身剩余超时（秒），兜底防死循环
    private float _lastDelta;       // 最近一帧与目标的角度差（诊断用）
    private bool  _wasActionBusy;   // 上一帧是否在做动作（检测动作结束的下降沿）
    private int   _turnTraceTail;   // 转身结束后再追记多少帧（诊断用）

    /// <summary>逐帧流水文件：只在转身期间（及结束后若干帧）写，用来抓"动作断在哪个帧"。</summary>
    private static readonly string TurnTracePath = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "XIVLauncherCN", "pluginConfigs", "SmartNpc", "turn-trace.txt");

    private void TraceTurn()
    {
        try
        {
            var bo     = _animator?.ActualBaseOverride ?? 0;
            var moving = _motion?.IsMoving ?? false;
            var speed  = _motion?.CurrentSpeed ?? 0f;
            var busy   = _animator?.IsBusy ?? false;

            System.IO.File.AppendAllText(TurnTracePath,
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:HH:mm:ss.fff} delta={1:F3} active={2} mode={3} left={4:F2} BO={5} moving={6} speed={7:F2} busy={8} walking={9} turning={10}",
                    System.DateTime.Now, _lastDelta, _turningActive, _overflowMode,
                    _turningTimeout, bo, moving, speed, busy,
                    _animator?.IsWalking ?? false, _animator?.IsTurning ?? false)
                + System.Environment.NewLine,
                new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // 诊断写入绝不能影响插件本身
        }
    }
    private static readonly Random OverflowRng = new();

    /// <summary>平移取证流水文件（v0.18.18）：点亮结束后 5 秒，逐帧记录三个位置。</summary>
    private static readonly string SlideTracePath = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "XIVLauncherCN", "pluginConfigs", "SmartNpc", "slide-trace.txt");

    /// <summary>
    /// 平移取证的一帧（v0.18.18）。三列位置各司其职：
    /// <b>L</b>=逻辑位置（GameObject.Position，我们每帧写的那个）；
    /// <b>D</b>=绘制对象位置（CharacterBase.Position，画面上实际显示的）；
    /// <b>P</b>=玩家位置。L 不动而 D 在滑 = 游戏插值在作怪；L 自己在动 = 移动层在作怪。
    /// </summary>
    private void TraceSlideFrame(int frameIndex)
    {
        try
        {
            if (frameIndex == 1)
            {
                System.IO.File.WriteAllText(SlideTracePath,
                    string.Format("── 点亮结束 {0:HH:mm:ss.fff}（站稳窗口 {1:0.0}s，之后允许移动）──\n",
                        System.DateTime.Now, SettleSeconds),
                    new System.Text.UTF8Encoding(false));
            }

            if (_npc is null || _npc.Address == nint.Zero)
                return;

            var lp = ((GameObject*)_npc.Address)->Position;

            var dp = "D=（无绘制对象）";
            var draw = NpcFactory.GetDrawObject(_npc);
            if (draw is not null)
            {
                var p = draw->Position;
                dp = string.Format("D=({0:F2},{1:F2},{2:F2})", p.X, p.Y, p.Z);
            }

            var player = _objectTable?.LocalPlayer;
            var pp     = player is null ? System.Numerics.Vector3.Zero : player.Position;

            // v0.18.20：动画状态取证 —— "站笔直滑行"到底是不是 sequencer 空转。
            // play=false + spd>0 = 她在位移但 sequencer 里没有任何在播的时间轴。
            var tlWalk  = _animator?.WalkTimeline ?? 0;
            var tlRun   = _animator?.RunTimeline ?? 0;
            var bo      = _animator?.ActualBaseOverride ?? 0;
            var mode    = _animator?.ActualMode;
            var gait    = _motion?.CurrentGait.ToString() ?? "?";
            var want    = _animator?.WrittenBase ?? 0;
            var stuck   = _animator?.AnimationConfirmed ?? false;

            System.IO.File.AppendAllText(SlideTracePath,
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:HH:mm:ss.fff} f={1} L=({2:F2},{3:F2},{4:F2}) {5} P=({6:F2},{7:F2},{8:F2}) settle={9} animWait={10:F2} spd={11:F2} gait={12} tl={13}/{14} want={15} base={16} stuck={17} mode={18}\n",
                    System.DateTime.Now, frameIndex,
                    lp.X, lp.Y, lp.Z, dp,
                    pp.X, pp.Y, pp.Z,
                    _settleTimer > 0f ? 1 : 0,
                    _animWait,
                    _motion?.CurrentSpeed ?? 0f,
                    gait, tlWalk, tlRun, want, bo, stuck ? 1 : 0, mode),
                new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // 诊断写入绝不能影响插件本身
        }
    }


    private void ApplyHeadLook(float playerDist, Vector3 playerPos, uint playerEntityId)
    {
        if (_npc is null || _npc.Address == nint.Zero)
            return;

        var character = (Character*)_npc.Address;
        var go        = (GameObject*)_npc.Address;

        // ── v0.18.31「始终不注视玩家」：最高优先级，直接完全关闭注视 ──
        if (_config?.NeverGazeAtPlayer ?? false)
        {
            ClearHeadTarget(character);
            _overflowMode   = 0;
            _overflowTimer  = 0f;
            _turningActive  = false;
            _turningTimeout = 0f;
            _gazeOn         = true;
            _gazeTimer      = 0f;
            return;
        }

        // ── v0.18.32 注视模式仲裁（用户最终裁定，优先级从高到低）──
        // ①「始终不注视玩家」 → 完全不看（方法开头已处理）；
        // ②「站定时用头/视线看」= **纯移动闸门，不再覆盖频率**：
        //      勾选 = 怎么看交给③决定，但一走起来视线立刻收回、头回正目视前方；
        //      不勾 = 怎么看同样交给③决定，站定/行动都照③执行；
        // ③「始终注视玩家」= 注视频率（两种闸门状态下都生效）：
        //      勾选 = 一直看着你；不勾 = 自然张望：占比/时长面板可调
        //     （默认约 60% 时间看你，10~20 秒；约 40% 移开，5~10 秒）。
        // ⚠️ 占视状态必须**整段保持数秒**，绝不能逐帧重掷 —— 否则她的头会在
        // "看你/看别处"之间高频抖动（和转身追"决定必须保持住"是同一个教训）。
        // ⚠️ 与超转幅的「转身追 / 干脆不看」二选一互不干涉：视线移开的时段
        // 直接跳过整段追踪逻辑（她此刻是**故意不看**，不该触发转身追）。
        var standing     = !(_motion?.IsMoving ?? false);
        var standingGate = _config?.CameraHeadLook ?? true;

        // ② 移动闸门：勾「站定时看」时，走动一律收回视线
        if (standingGate && !standing)
        {
            ClearHeadTarget(character);
            _overflowMode   = 0;
            _overflowTimer  = 0f;
            _turningActive  = false;
            _turningTimeout = 0f;
            _gazeOn         = true;   // 占视状态复位：下次进来默认"在看你"
            _gazeTimer      = 0f;
            return;
        }

        // 距离闸门（所有注视模式共用）
        if (playerDist > Core.MotionTuning.HeadLookMaxDistance || playerEntityId == 0)
        {
            ClearHeadTarget(character);
            _overflowMode  = 0;
            _overflowTimer = 0f;
            _gazeOn        = true;
            _gazeTimer     = 0f;
            return;
        }

        // ③ 注视频率（v0.18.32：不再被②覆盖，始终生效）：
        //    「始终注视玩家」勾 = 一直看；不勾 = 40% 占视周期。
        if (!(_config?.AlwaysGazeAtPlayer ?? true))
        {
            _gazeTimer -= FrameDelta;
            if (_gazeTimer <= 0f)
            {
                // 占比与时长全部面板可调（v0.18.34）
                var lookPct  = Math.Clamp(_config?.GazeLookPercent ?? 60f, 0f, 100f);
                var lookMin  = MathF.Max(0.5f, _config?.GazeLookMin ?? 10f);
                var lookMax  = MathF.Max(lookMin,  _config?.GazeLookMax ?? 20f);
                var awayMin  = MathF.Max(0.5f, _config?.GazeAwayMin ?? 5f);
                var awayMax  = MathF.Max(awayMin,  _config?.GazeAwayMax ?? 10f);

                if (OverflowRng.Next(100) < lookPct)
                {
                    _gazeOn    = true;                                    // 注视 lookMin~lookMax 秒
                    _gazeTimer = lookMin + (float)OverflowRng.NextDouble() * (lookMax - lookMin);
                }
                else
                {
                    _gazeOn    = false;                                   // 移开视线 awayMin~awayMax 秒
                    _gazeTimer = awayMin + (float)OverflowRng.NextDouble() * (awayMax - awayMin);
                }
            }

            if (!_gazeOn)
            {
                ClearHeadTarget(character);
                _overflowMode   = 0;
                _overflowTimer  = 0f;
                _turningActive  = false;   // 故意不看 → 进行中的转身也一并收掉
                _turningTimeout = 0f;
                return;
            }
        }

        // 她当前的朝向，与"要看向玩家"的朝向，差多少
        var toPlayer = MathF.Atan2(playerPos.X - go->Position.X, playerPos.Z - go->Position.Z);
        var delta    = Core.NpcMotion.NormalizeAngle(toPlayer - go->Rotation);
        _lastDelta   = delta;   // 诊断用

        var absDelta = MathF.Abs(delta);

        // 头/眼转幅：面板可调（度 → 弧度）
        var headTurnLimit = MathF.Max(0.1f, (_config?.HeadTurnLimitDeg ?? 66f) * MathF.PI / 180f);

        // ⚠️ v0.18.31：**移动中绝不转身追**。
        // 行动时身体面朝前进方向（移动层管），头/眼够得着就追、够不着就收回 ——
        // 边走边触发转身动画/身体转向会与 locomotion 打架（用户要的"行动时一直看"
        // 指头/眼视线，不是把整个人拧过去）。
        if (!standing)
        {
            if (absDelta <= headTurnLimit)
                SetHeadTarget(character, playerEntityId);
            else
                ClearHeadTarget(character);

            _overflowMode  = 0;
            _overflowTimer = 0f;
            return;
        }

        if (!_turningActive)
        {
            if (absDelta <= headTurnLimit)
            {
                // 头 / 眼够得着：交给游戏引擎自己追踪（AnamCore.SetHeadTarget）
                SetHeadTarget(character, playerEntityId);
                _overflowMode  = 0;
                _overflowTimer = 0f;
                return;
            }

            // ── 超出头/眼转幅 ──
            // 随机二选一：① 放弃追踪 ② 真的转身体（带转身动作）。
            // ⚠️ 决定必须**保持住**：每帧重掷会让她在两种行为之间抖。
            if (_overflowTimer <= 0f)
            {
                // 面板可调的"转身追 vs 干脆不看"比例（%，默认 80）
                var turnChance = Math.Clamp(_config?.TurnChancePercent ?? 80f, 0f, 100f);
                _overflowMode  = OverflowRng.Next(100) < turnChance ? 2 : 1;
                _overflowTimer = Core.MotionTuning.HeadOverflowHoldSeconds;
                _overflowDir   = delta > 0f ? 1 : -1;
            }

            _overflowTimer -= FrameDelta;

            if (_overflowMode == 2)
            {
                _turningActive  = true;
                _turningTimeout = Core.MotionTuning.TurnMaxSeconds;
            }
        }
        else
        {
            // ⚠️ 已经在转身：**一直转到真的对上为止**（迟滞）。
            // 不能再用 HeadTurnLimit 判结束 —— 那会在还剩几十度时把转身动画掐掉，
            // 剩下那一小段变成没有动画的干转（用户反馈"微小转向时容易平移"就是它）。
            _overflowMode = 2;
            _overflowDir  = delta > 0f ? 1 : -1;

            _turningTimeout -= FrameDelta;

            // 双保险：转到位 或 超时，任一满足即结束。
            // 超时兜底是为了防止"永远对不上"导致转身动画无限循环。
            if (absDelta < Core.MotionTuning.TurnDoneThreshold || _turningTimeout <= 0f)
            {
                _turningActive  = false;
                _overflowMode   = 0;
                _overflowTimer  = 0f;
                _turningTimeout = 0f;
            }
        }

        if (_overflowMode == 1)
            ClearHeadTarget(character);                 // ① 放弃追踪
        else
            SetHeadTarget(character, playerEntityId);   // ② 转身追
    }

    /// <summary>照 AnamCore.SetHeadTarget：设 TargetId，游戏引擎自动做头 / 眼追踪。</summary>
    private static void SetHeadTarget(Character* character, uint entityId)
    {
        character->TargetId.ObjectId = entityId;
        character->TargetId.Type     = 0;
    }

    /// <summary>照 AnamCore.ClearHeadTarget：置 0xE0000000，头 / 眼回正。</summary>
    private static void ClearHeadTarget(Character* character)
    {
        character->TargetId.ObjectId = 0xE0000000;
        character->TargetId.Type     = 0;
    }

    public void Dispose()
    {
        // 同步销毁：见 Plugin.Dispose 的注释（IFramework 是插件作用域的）。
        if (_framework is not null)
            _framework.Update -= OnFrameworkUpdate;

        // v0.18.69.22：副 NPC 全部送走。
        SubNpcs?.Dispose();
        SubNpcs = null;

        if (_npc is { } handle && handle.IsAlive)
        {
            // 销毁前把"看向镜头"收回来，别给游戏留一个改过的标志位。
            if (handle.Address != nint.Zero)
                ((Character*)handle.Address)->LookAt.IsFacingCamera = false;

            NpcFactory.Despawn(handle);
        }

        NpcGround.Reset();
        _npc = null;
    }
}