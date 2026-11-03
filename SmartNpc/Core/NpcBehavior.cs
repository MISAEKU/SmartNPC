using System;
using System.Numerics;
using Dalamud.Plugin.Services;

namespace SmartNpc.Core;

/// <summary>
/// 行为层：决定「她现在该干什么、去哪」。
///
/// ── 旧版为什么是"一根会跟随的柱子" ──
/// 它每帧返回一对相对偏移（身后 1.6 米、侧移 0.7 米），由移动层换算成坐标。
/// 后果有三个：
///   1. 目标点每帧都跟着玩家变 → <b>永远不会"到达"</b>，于是
///      "到了→停下→观察四周"这套行为物理上无法触发；
///   2. 玩家的位置是唯一基准 → 想"往别处走走"的意图下一帧就被覆盖；
///   3. 状态机里那些"待机/观望/搭话"只是换了个站位，人在原地不动 —— 像贴图。
///
/// ── 新版的核心改动 ──
/// <b>目标点变成世界坐标，而且"选点"和"走向点"分离。</b>
/// 她会在玩家附近<b>自己挑一个地方</b>（去那边看看 / 换个位置站 / 绕到前面），
/// 走过去，在那儿待一会儿，再挑下一个。这才是"有自主意识"的全部来源 ——
/// 不是加了新动画，而是<b>目标不再每帧被重置</b>。
///
/// ── 硬约束：选点只依赖玩家 ──
/// 选点用的基准是<b>玩家位置 + 玩家朝向</b>，绝不掺入 NPC 自己的状态。
/// 掺进去就形成正反馈环 → 绕圈。这条在 <see cref="NpcMotion"/> 里踩过一次。
/// 注意"不掺入自己"指的是<b>不把她的位置/朝向当基准</b>；
/// 而选点之后她<b>认准那个世界坐标</b>走到底，这正是我们要的。
/// </summary>
public sealed unsafe class NpcBehavior(IPluginLog log)
{
    /// <summary>她在干什么。诊断与面板都会显示。</summary>
    public enum Behaviour
    {
        /// <summary>走向玩家附近（速度分级在这一档里切换走/跑）。</summary>
        跟随,

        /// <summary>在玩家附近自主走动，去一个自己挑的点。</summary>
        游荡,

        /// <summary>站定，随机插待机小动作。</summary>
        待机,

        /// <summary>原地环顾四周。</summary>
        观望,

        /// <summary>走向玩家身边停下来搭话。</summary>
        搭话,

        /// <summary>战斗 / 生产中：贴紧玩家，不做小动作，不闲聊。</summary>
        警戒,
    }

    public Behaviour Current { get; private set; } = Behaviour.跟随;

    /// <summary>本次状态已持续多久（秒）。</summary>
    public float StateElapsed { get; private set; }

    /// <summary>总开关。关掉后退回"只站在玩家身后"的旧行为。</summary>
    public bool Enabled { get; set; } = true;

    // ── 外部输入 ──

    /// <summary>玩家当前是否在战斗。</summary>
    public bool PlayerInCombat { get; set; }

    /// <summary>玩家当前是否在采集 / 制作（制作里她不该凑过来）。</summary>
    public bool PlayerIsCrafting { get; set; }

    /// <summary>
    /// 她到位进入待机后，玩家离开超过这个距离就重新跟随（米，v0.17.6 面板可调）。
    /// 语义按用户定义：看<b>玩家离她多远</b>，不是看玩家速度。
    /// </summary>
    public float FollowAgainDistance { get; set; } = 4.5f;

    /// <summary>
    /// 「移动中不做动作」关掉时为 true：允许在跟随 / 游荡（她在移动）的状态下
    /// 也插待机小动作 —— 插上之后她会被铁律（Paused）拦下站住做完再走。
    /// 开着「移动中不做动作」时保持 false，只有待机 / 观望（站定）才触发。
    /// </summary>
    public bool GesturesWhileMoving { get; set; }

    // ── 调参 ──

    /// <summary>拉远到超过这个距离就<b>跑</b>着追（米）。</summary>
    public float RunDistance { get; set; } = 7.5f;

    /// <summary>回到这个距离以内才降回<b>走</b>（米）。</summary>
    public float WalkDistance { get; set; } = 4.0f;

    /// <summary>近到这个距离以内算"到位"（米）。</summary>
    public float ArriveDistance { get; set; } = 2.4f;

    /// <summary>超过这个距离说明跟丢了，直接追并取消当前目标（米）。</summary>
    public float StrayDistance { get; set; } = 13f;

    /// <summary>游荡最远能走多远（米，相对锚点）。</summary>
    public float RoamRange { get; set; } = 7f;

    /// <summary>游荡至少要离开玩家多远（米）。</summary>
    public float RoamMin { get; set; } = 2.0f;

    /// <summary>待机时随机插小动作的频率（秒区间）。</summary>
    public float IdleGestureMin { get; set; } = 6f;
    public float IdleGestureMax { get; set; } = 15f;

    /// <summary>待机多久之后她会动一动（秒）。</summary>
    public float LookAroundAfter { get; set; } = 20f;

    /// <summary>环顾持续多久（秒）。</summary>
    public float LookAroundDuration { get; set; } = 3.5f;

    /// <summary>玩家静止这么久之后，她会过来搭话（秒）。0 = 不主动搭话。</summary>
    public float ChatTriggerIdle { get; set; } = 45f;

    /// <summary>一次搭话持续多久（秒）。</summary>
    public float ChatDuration { get; set; } = 8f;

    /// <summary>搭话时站多近（米）。</summary>
    public float ChatDistance { get; set; } = 1.5f;

    /// <summary>玩家移动的判定阈值（米），小于它算"没动"。</summary>
    public float StillRadius { get; set; } = 0.12f;

    /// <summary>常态跟随时站多远（米，相对玩家身后）。</summary>
    public float FollowBehind { get; set; } = 1.8f;

    /// <summary>常态跟随时的侧移（米）。</summary>
    public float FollowSide { get; set; } = 0.8f;

    // ── 运行时状态 ──
    private float   _stateFor;
    private float   _nextGesture;
    private float   _playerStillFor;
    private Vector3 _lastPlayerPos;
    private bool    _hasLastPlayerPos;
    private Vector3 _roamTarget;
    private bool    _hasRoamTarget;
    private float   _roamCooldown;
    private Vector3 _anchorPlayerPos;
    private bool    _hasAnchor;

    /// <summary>本帧选定的目标（诊断用）。</summary>
    public Vector3 CurrentTarget { get; private set; }

    /// <summary>换区后复位，并清掉游荡目标（那个坐标在新地图毫无意义）。</summary>
    public void NotifyZoneChanged()
    {
        _hasLastPlayerPos = false;
        _hasRoamTarget    = false;
        _hasAnchor        = false;
        _playerStillFor   = 0f;
        _roamCooldown     = 0f;
        Enter(Behaviour.跟随);
    }

    public void Reset()
    {
        _hasLastPlayerPos = false;
        _hasRoamTarget    = false;
        _hasAnchor        = false;
        _playerStillFor   = 0f;
        _stateFor         = 0f;
        _roamCooldown     = 0f;
        Enter(Behaviour.跟随);
    }

    private void Enter(Behaviour next)
    {
        if (Current == next)
            return;

        Current = next;
        _stateFor = 0f;

        // 目标不该跨状态存活。留在游荡里的坐标对"待机"是没有意义的。
        if (next != Behaviour.游荡)
            _hasRoamTarget = false;

        // 换到需要"站着不动"的状态时立刻给一个随机延时，
        // 否则会出现"刚站定就弹动作"的突兀感。
        _nextGesture = Random.Shared.Next(
            Math.Max(1, (int)IdleGestureMin),
            Math.Max(2, (int)IdleGestureMax) + 1);

        log.Debug("[SmartNpc] 行为切换 → {0}", next);
    }

    // ── 每帧决策 ──────────────────────────────────────────────────

    /// <summary>
    /// 状态机推进。<paramref name="playerDistance"/> 是她到玩家的水平距离。
    /// </summary>
    public Behaviour Update(float playerDistance, float deltaTime)
    {
        var delta = Math.Clamp(deltaTime, 0f, 0.25f);
        _stateFor += delta;
        StateElapsed = _stateFor;
        _roamCooldown = Math.Max(0f, _roamCooldown - delta);

        if (!Enabled)
        {
            Current = Behaviour.跟随;
            return Current;
        }

        // ── 1. 战斗 / 生产：警戒，压过一切 ──
        if (PlayerInCombat || PlayerIsCrafting)
        {
            Enter(Behaviour.警戒);
            return Current;
        }

        // ── 2. 跟丢了：距离大到没有讨论空间，先追回来 ──
        if (playerDistance > StrayDistance)
        {
            Enter(Behaviour.跟随);
            return Current;
        }

        // ── 3. 搭话时机：玩家站很久不动 ──
        if (ChatTriggerIdle > 0f && _playerStillFor > ChatTriggerIdle
                                 && playerDistance > ChatDistance + 0.6f
                                 && Current is not (Behaviour.搭话 or Behaviour.警戒))
        {
            Enter(Behaviour.搭话);
        }

        // ── 4. 搭话够了就走开 ──
        if (Current == Behaviour.搭话 && _stateFor > ChatDuration)
            Enter(Behaviour.跟随);

        // ── 5. 跟随 / 搭话 / 警戒：到位就转入自主活动 ──
        if (Current is Behaviour.跟随 or Behaviour.搭话 or Behaviour.警戒)
        {
            if (playerDistance <= ArriveDistance)
                Enter(Behaviour.待机);
        }

        // ── 5b. 玩家走开了：立刻回到跟随 ──
        //
        // ⚠️ 没有这条就是"她愣在原地不跟着我"：
        // 到位 → 进入待机；而待机要待满 LookAroundAfter（20 秒）才会换状态，
        // 换到的还是"游荡"（围着旧锚点自己走），不是跟随。
        // 于是你走了 5 米，她能在原地站上二十秒。
        // 迟滞距离比 ArriveDistance 大，避免在边界上一步一停地抖。
        // ── 5b. 玩家走开了：回到跟随 ──
        //
        // ⚠️ v0.17.6 语义修正（用户定义）：跟随触发只看<b>距离</b> ——
        // "玩家离开她超过一定距离就跟随"，这个距离面板可调（FollowAgainDistance）。
        // v0.17.5 用玩家速度判定是理解错了，已删。
        if (Current is Behaviour.待机 or Behaviour.观望 or Behaviour.游荡
            && playerDistance > FollowAgainDistance)
        {
            Enter(Behaviour.跟随);
            return Current;
        }

        // ── 6. 待机：待够久就动一动 ──
        //
        // 这里是"自主"的关键分叉。旧版一直停在跟随状态（原地不动，只是站位公式变一变），
        // 所以看起来是根柱子。现在她会在附近<b>自己挑一个点</b>走过去。
        if (Current == Behaviour.待机 && _stateFor > LookAroundAfter)
        {
            // 玩家也没动 → 先张望一下（比较克制）；
            // 玩家在动 → 换个地方站过去（更有"她在看着你"的意味）。
            Enter(_playerStillFor > LookAroundAfter ? Behaviour.观望 : Behaviour.游荡);
        }

        // ── 7. 观望：环顾完了回到待机 ──
        if (Current == Behaviour.观望 && _stateFor > LookAroundDuration)
        {
            _roamCooldown = 0.8f;      // 别一转身就立刻游荡出去
            Enter(Behaviour.待机);
        }

        // ── 8. 游荡：目标没了 / 玩家跑远了 → 收尾 ──
        if (Current == Behaviour.游荡)
        {
            if (!_hasRoamTarget)
                Enter(Behaviour.待机);
            else if (playerDistance > RoamRange + StrayDistance * 0.5f)
                Enter(Behaviour.跟随);
        }

        return Current;
    }

    /// <summary>
    /// 产出本帧的移动目标。
    ///
    /// ⚠️ 必须在 <see cref="Update"/> 之后调用。<b>返回值是"世界坐标"</b>，
    /// 不是相对玩家的偏移 —— 这是"她能自己走去某个地方"的前提。
    /// </summary>
    public MoveGoal Goal(Vector3 selfPosition, Vector3 playerPosition, float playerRotation, float playerDistance)
    {
        // 锚点：玩家移动超过一定距离就重设，让"游荡"跟着玩家走而不是留在原地。
        if (!_hasAnchor)
        {
            _anchorPlayerPos = playerPosition;
            _hasAnchor = true;
        }
        else if (Flat(playerPosition - _anchorPlayerPos).Length() > 3.5f)
        {
            _anchorPlayerPos = playerPosition;
            _hasRoamTarget   = false;   // 旧目标已不在合理范围，重新挑
        }

        var forward = new Vector3(MathF.Sin(playerRotation), 0f, MathF.Cos(playerRotation));
        var right   = new Vector3(forward.Z, 0f, -forward.X);

        switch (Current)
        {
            // 警戒：贴紧玩家。只用玩家当基准 → 反馈环仍然是断的。
            case Behaviour.警戒:
                CurrentTarget = playerPosition - forward * 0.9f + right * 0.45f;
                return new MoveGoal(CurrentTarget, Gait.行走, playerPosition.Y, 1.0f, 0.5f);

            // 搭话：走到你正前方，正对着你。
            case Behaviour.搭话:
                CurrentTarget = playerPosition + forward * ChatDistance * 0.4f - right * 0.4f;
                return new MoveGoal(CurrentTarget, Gait.行走, playerPosition.Y, 1.0f, 0.35f);

            // 游荡：走向<b>她在前面选好的世界坐标</b>。
            case Behaviour.游荡:
                if (!_hasRoamTarget)
                    PickRoamTarget(playerPosition, forward, right);

                if (!_hasRoamTarget)
                {
                    // 冷却期内选不出点 → 老实站着，别原地抖动。
                    CurrentTarget = selfPosition;
                    return new MoveGoal(CurrentTarget, Gait.站立, playerPosition.Y, 1f, 0.3f);
                }

                CurrentTarget = _roamTarget;
                return new MoveGoal(CurrentTarget, Gait.行走, _roamTarget.Y, 1.2f, 0.45f);

            // 观望 / 待机：站着不动。目标点设成自己当前位置。
            case Behaviour.观望:
            case Behaviour.待机:
                CurrentTarget = selfPosition;
                return new MoveGoal(CurrentTarget, Gait.站立, playerPosition.Y, 1f, 0.3f);

            // 跟随：唯一的"活"目标 —— 距离大就跑。
            default:
            {
                var spot = playerPosition - forward * FollowBehind + right * FollowSide;

                // ⚠️ 迟滞：7.5m 起跑、回到 4m 才降回走。
                // 单一阈值会在边界反复横跳（"跑两步→走两步→又跑"），看起来很蠢。
                var gait = playerDistance > RunDistance ? Gait.奔跑 : Gait.行走;

                CurrentTarget = spot;
                // ⚠️ StopRadius 绝不能用 ArriveDistance(2.6m)：它比 FollowBehind(1.8m) 还大，
                // 于是不管站位参数怎么调，新落点永远落在"已到位"判定圈内 → 她一步都不挪，
                // 站位滑条完全无效（用户实测反馈）。
                // 站位是精确落点，用小半径：差半米她就会走过去补位。
                return new MoveGoal(spot, gait, playerPosition.Y, 1.4f, 0.5f);
            }
        }
    }

    /// <summary>
    /// 在玩家附近挑一个"想过去看看"的点。
    ///
    /// 分布是有讲究的，不是纯随机：
    ///   · 角度<b>偏向玩家正前方</b>（±60°）—— 她会走到你看得见的地方。
    ///     跑到你背后就等于消失了，那不叫随从；
    ///   · 距离在 [RoamMin, RoamRange] 之间，指数 0.65 偏向近距离；
    ///   · <b>不掺入她自己的位置</b>（见类注释的硬约束）。
    /// </summary>
    private void PickRoamTarget(Vector3 playerPosition, Vector3 forward, Vector3 right)
    {
        if (_roamCooldown > 0f)
            return;

        // ±60°，再三次方把分布压向中心 → 大多数时候走"稍微偏一点"，
        // 偶尔来一个大幅度的绕到侧面。纯均匀分布会让她一半时间走到玩家斜后方，
        // 看着像迷路了。
        var angle = (Random.Shared.NextSingle() * 2f - 1f) * MathF.PI / 3f;
        angle *= angle * angle;

        var radius = RoamMin + (RoamRange - RoamMin) * MathF.Pow(Random.Shared.NextSingle(), 0.65f);

        var dir = forward * MathF.Cos(angle) + right * MathF.Sin(angle);

        _roamTarget  = _anchorPlayerPos + dir * radius;
        _roamTarget.Y = playerPosition.Y;   // 高度用玩家的，坡道上才不会挖进地里

        _hasRoamTarget = true;
        log.Debug("[SmartNpc] 游荡目标 → {0:0.0},{1:0.0}（距锚点 {2:0.0} 米，偏移 {3:0}°）",
                  _roamTarget.X, _roamTarget.Z, radius, angle * 180f / MathF.PI);
    }

    // ── 每帧被调用的小接口 ────────────────────────────────────────

    /// <summary>待机 / 观望时要不要插一个小动作。<b>调用方每帧问一次</b>。</summary>
    public bool WantsIdleGesture(float deltaTime)
    {
        var stationary = Current is Behaviour.待机 or Behaviour.观望;

        // ⚠️ v0.17.6：开关B（移动中不做动作）关掉时，跟随 / 游荡状态也允许触发 ——
        // 这是"可以在移动时开始播放动作"的入口。插上之后控制器里的铁律
        // （Paused = actionBusy）会让她站住做完再走，永远不边走边做。
        if (!stationary && !GesturesWhileMoving)
            return false;

        _nextGesture -= deltaTime;
        if (_nextGesture > 0f)
            return false;

        _nextGesture = Random.Shared.Next(
            Math.Max(1, (int)IdleGestureMin),
            Math.Max(2, (int)IdleGestureMax) + 1);

        return true;
    }

    /// <summary>环顾时的朝向偏移（弧度）。用摆动而不是固定角度，固定角度看着像卡住。</summary>
    public float LookAwayYawOffset =>
        Current == Behaviour.观望
            ? MathF.Sin((float)_stateFor * (MathF.Tau / 2.4f)) * 0.9f
            : 0f;

    /// <summary>累计"玩家静止了多久"。需要在每帧调用。</summary>
    public void TrackPlayerStillness(Vector3 playerPosition, float deltaTime)
    {
        if (!_hasLastPlayerPos)
        {
            _lastPlayerPos    = playerPosition;
            _hasLastPlayerPos = true;
            return;
        }

        if (Flat(playerPosition - _lastPlayerPos).LengthSquared() > StillRadius * StillRadius)
        {
            _lastPlayerPos  = playerPosition;
            _playerStillFor = 0f;
            return;
        }

        _playerStillFor += deltaTime;
    }

    // ── 辅助 ──────────────────────────────────────────────────────

    private static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);

    /// <summary>诊断用的一行摘要。</summary>
    public string Describe() =>
        $"{Current}（{StateElapsed:0.0}s｜静止 {_playerStillFor:0.0}s"
        + $"｜战斗={PlayerInCombat} 生产={PlayerIsCrafting}"
        + $"｜目标 {CurrentTarget.X:0.0},{CurrentTarget.Z:0.0}）";
}
