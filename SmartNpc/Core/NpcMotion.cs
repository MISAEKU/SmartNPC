using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using GameVector3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;

namespace SmartNpc.Core;

/// <summary>
/// 移动执行层：把 <see cref="MoveGoal"/> 变成真实的位移、朝向与动画状态。
///
/// ── 这一层只做执行，不做决策 ──
/// 「她现在该去哪」是 <see cref="NpcBehavior"/> 的事，「怎么走过去」是这里的事。
/// 两者混在一起（旧版本就是这样）的后果是：任何一点自主性都写不进去，
/// 因为决策刚算出一个"想去的点"，下一帧就被跟随公式覆盖了。
///
/// ── 三条不能破的规矩 ──
/// 1. <b>目标点只依赖玩家，不依赖她自己。</b>期望站位若掺入 NPC 当前状态（朝向、位置），
///    就形成正反馈环 → 绕圈。这个坑踩过一次，见下面的历史记录。
/// 2. <b>速度分级要有加减速，不能只有速度上限。</b>真正的走/跑切换是
///    "加速 → 匀速 → 减速"，直接跳变会像滑块在拖。
/// 3. <b>朝向要单独处理。</b>转身和走路是两件事：她可以站着转身，
///    也可以边走边转（转向角速度有上限）。
/// </summary>
// ── 以下两个类型原本住在 NpcSteering.cs ──
// v0.13.0 删掉整个避让模块后，它们是移动流程的骨架（Gait 决定用哪套动作、
// MoveGoal 是一次移动请求），所以搬到这里，跟使用方住在一起。

/// <summary>移动姿态。它决定用哪一套动作动画 —— 行走播 walk，奔跑播 run。</summary>
public enum Gait
{
    /// <summary>站定不动。</summary>
    站立,

    /// <summary>常速行走。</summary>
    行走,

    /// <summary>小跑。被拉开、或玩家自己在跑时用这个。</summary>
    奔跑,
}

/// <summary>
/// 一次移动请求：<b>世界坐标</b>的目标点 + 姿态 + 到达判定半径。
///
/// <para>为什么目标点是世界坐标而不是"玩家身后 N 米"：
/// 旧实现每帧重算 <c>玩家位置 - forward × 距离</c>，她就会永远在追一个移动的点，
/// <b>永远不会"到达"</b>，于是"到达 → 停顿 → 观察"这套行为根本无从触发。
/// 改成世界坐标后，"走过去"才是一个真正的过程。</para>
/// </summary>
/// <param name="Point">目标点（Y 取 <paramref name="GroundY"/>，这里的 Y 仅作参考）。</param>
/// <param name="Gait">用什么姿态过去。</param>
/// <param name="GroundY">到达后要贴的地面高度。</param>
/// <param name="SlowRadius">进入这个半径开始减速（米）。</param>
/// <param name="StopRadius">进入这个半径算到达（米）。</param>
public readonly record struct MoveGoal(
    Vector3 Point,
    Gait    Gait        = Gait.行走,
    float   GroundY     = 0f,
    float   SlowRadius  = 1.4f,
    float   StopRadius  = 0.30f);

public sealed unsafe class NpcMotion
{
    // ── 速度：跟着玩家走（v0.13.0 起不再用固定值）──
    //
    // 以前写死走 2.2 / 跑 4.6，问题是**和玩家实际速度无关**：
    // 换了软体地面、或者玩家自己加速，她还老速度，于是"她比我快"或"她拖后腿"。
    // 现在每帧由控制器实测玩家速度喂进来，基速直接由它派生 ——
    // 你走她小走、你跑她小跑，手感天然一致。
    //
    // 动画播放速度按它换算（见 NpcAnimator 的 ReferenceWalkSpeed），
    // 两边不一致就会出现"脚在倒着走"。

    /// <summary>
    /// 玩家的实测速度（米/秒，水平分量，已平滑）。
    /// 由 <c>NpcController</c> 每帧测量后喂进来。
    /// </summary>
    public float PlayerSpeed { get; set; }

    /// <summary>速度倍率（配置里的「速度倍率」）。1 = 与玩家完全同速。</summary>
    public float SpeedScale { get; set; } = 1f;

    /// <summary>本帧的转身角速度（弧度/秒，带符号）。转身动作靠它判断"她在转吗、往哪转、转多快"。</summary>
    public float YawRate { get; private set; }

    /// <summary>
    /// v0.16.2：允许身体<b>不受站定限幅约束</b>地转过去。
    /// 头/眼追不上目标时，随机选中的"转身追"分支会把这个打开 ——
    /// 于是她是真的转身体（并带转身动作），而不是被硬掰过去的平移旋转。
    /// </summary>
    public bool AllowFullTurn { get; set; }

    /// <summary>
    /// 站定转向的基准朝向：她"停下那一刻"面朝的方向。
    /// 站定时只允许从这个方向偏 <see cref="MotionTuning.StandingTurnLimit"/> 以内。
    /// </summary>
    private float _restYaw;

    // ── 转向 ──

    /// <summary>最大转向角速度（米/秒→弧度/秒）。见 <see cref="MotionTuning.TurnRate"/>。</summary>

    /// <summary>朝向缓强度：站定时转身比移动中略慢一点（更稳）。</summary>
    private const float TurnStiffness = 12f;

    /// <summary>
    /// 触发「转身动作」所需的最小站定转身角（弧度，约 29°）。
    /// 小于它的小幅转正仍用平滑旋转（免得转一点就硬播转身动画），
    /// 大于它才播转身动画 —— 这正是用户要的"旋转得有动作"。
    /// </summary>
    private const float TurnAnimMinAngle = 0.5f;

    /// <summary>站定时是否正在做明显的原地转身（供动画层决定是否播转身动作）。</summary>
    public bool TurningInPlace { get; private set; }

    /// <summary>原地转身的转向：&lt; 0 左转，&gt; 0 右转。</summary>
    public int TurnDirection { get; private set; }

    // ── 瞬移兜底 ──

    /// <summary>超过这个距离直接瞬移（米）。玩家跑图、传送后慢慢挪既跟不上又难看。</summary>
    public float TeleportDistance { get; set; } = 14f;

    /// <summary>垂直落差超过这个值也瞬移（上下楼、跳台、换图）。</summary>
    // ⚠️ v0.16.2：原来是 4f，一段楼梯就超了 → 她被瞬移上楼（用户明确要求不能靠瞬移解决）。
    // 放宽容差，让她靠每帧的贴地采样（NpcGround.Resolve 已可上可下）自己逐级爬。
    // 只在真的离谱（比如隔了一整层、掉出地图）时才退回瞬移。
    // ⚠️ v0.17.0 起可从面板调（Configuration.TeleportHeight）。
    // 原来是 4f，一段楼梯的高差就超了 → 她被瞬移上楼（用户要求不能靠瞬移解决）。
    // 现在基本只有"水平距离拉开"才会瞬移，高差靠贴地采样自己爬。
    public float TeleportHeight { get; set; } = 20f;

    /// <summary>
    /// 玩家悬空判据（v0.18.69.19）：玩家比她高出这个值 = 在天上（坐骑飞行 / 跳台 / 电梯），
    /// <b>所有瞬移兜底冻结</b>（<see cref="TryResummonNearPlayer"/> 返回 false）。
    /// 玩家落地后自动恢复 —— 那时的一次性瞬移正是想要的。
    /// </summary>
    public float PlayerAirGap { get; set; } = 5f;

    /// <summary>
    /// 被挡住超过这个秒数就<b>强制瞬移</b>到你身边（0 = 不兜底，v0.18.2）。
    /// 由控制器每帧从配置喂入。
    ///
    /// <para>⚠️ 这一条是为了堵一个<b>死锁</b>：下面避障分支里，跳跃要求
    /// <c>ProbeFront == LowObstacle</c>（实心墙跳不过去），
    /// 而旧的瞬移要求 <c>distance &gt; 2f</c>。于是"被实心墙 / 关着的门挡住，
    /// 且目标就在 2 米以内"这一组合<b>两条出路同时失效</b> → 她永远站在原地。
    /// 偏偏跟随的站位点就在你身后约 1.8 米，正好落进这个死锁 ——
    /// 用户实测「跟着跟着突然愣在原地不动」。</para>
    ///
    /// <para>现在多一条无条件出路：卡住超过这个秒数就瞬移到你身边（落点取玩家脚下，
    /// 那必然是可站立的地面），彻底消除"卡住出不来"。</para>
    /// </summary>
    public float StuckTeleportSeconds { get; set; } = 3f;

    /// <summary>
    /// 绕行承诺时长（秒，v0.18.3）。被挡后沿挑好的偏转方向走这么久再重新看路。
    /// 太短容易在墙前来回拐，太长会绕远；建议 0.8~1.5 秒。
    /// </summary>
    public float DetourDuration { get; set; } = 1.1f;

    /// <summary>一次绕行结束/中断后，隔多久才允许再挑新方向（秒）。</summary>
    public float DetourCooldown { get; set; } = 0.8f;

    /// <summary>
    /// 跟随强度（引力，v0.18.4）。1 = 与玩家同速追；
    /// <b>&gt;1 = 落后时追得更凶</b>（奔跑全程提速，迟滞带之外的行走也提速）；
    /// &lt;1 = 更松弛。由面板「跟随强度」滑条喂入。
    /// </summary>
    public float FollowStrength { get; set; } = 1.25f;

    /// <summary>
    /// 行走档的「落后线」：距离超过它（还在迟滞带之外）时，行走也按引力提速追赶；
    /// 贴身（迟滞带内）保持与你同速，不会怼着你走。
    /// </summary>
    public float WalkLagDistance { get; set; } = 4f;

    /// <summary>出生点前后偏移（瞬移兜底 / 脱困落点用，与 Configuration.SpawnOffsetBehind 同源）。</summary>
    public float SpawnOffsetBehind { get; set; } = 1.2f;

    /// <summary>出生点侧向偏移（同上）。</summary>
    public float SpawnOffsetSide { get; set; } = 0.6f;

    /// <summary>玩家朝向（弧度）。出生点式落点以它为基准，控制器每帧喂入。</summary>
    public float PlayerRotation { get; set; }

    /// <summary>
    /// 玩家当前位置（水平）。由控制器每帧喂入，<b>只用于兜底瞬移的落点</b>。
    /// 有了它，"卡住出不来"就一定有解：直接出现在你脚下。
    /// </summary>
    public Vector3 PlayerPosition { get; set; }

    // ── 内部状态 ──
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private float            _lastTime;
    private bool             _hasLastTime;

    /// <summary>当前速度（米/秒）。带加减速，是连续量，不是每帧重算的瞬时值。</summary>
    public float CurrentSpeed { get; private set; }

    /// <summary>当前朝向（弧度）。平滑逼近 <see cref="NpcHandle.DesiredRotation"/>。</summary>
    public float CurrentRotation { get; private set; }

    /// <summary>本帧是否真的在移动（决定要不要播走路动画）。</summary>
    public bool IsMoving { get; private set; }

    /// <summary>本帧的姿态。诊断与动画层都读它。</summary>
    public Gait CurrentGait { get; private set; } = Gait.站立;

    // ── 卡死保险丝（v0.17.4）──
    private float   _stuckTime;
    private Vector3 _stuckRef;

    // ── 承诺式绕行（v0.18.3，v0.18.5 路线优化）──
    private float   _detourTimer;     // 绕行承诺剩余时间（>0 = 绕行中）
    private Vector3 _detourDir;       // 承诺的绕行方向（绝对单位向量，不随目标摆动）
    private int     _detourSign;      // 方向记忆：>0 右 / <0 左 / 0 未定（防左右横跳）
    private float   _detourCooldown;  // 绕行结束后的再挑冷却

    // ── 无进度看门狗（v0.18.3）──
    private float   _watchdogTime;
    private Vector3 _watchdogRef;

    // ── 跳跃（v0.17.8）──
    // 被矮障碍挡住时"尝试跳过去"：物理抛物线 + 动画层播跳跃时间轴。
    private bool  _jumping;
    private float _jumpVelY;
    private float _jumpCooldown;
    private bool  _jumpFallSignaled;   // 本次滞空是否已切到下落动画（v0.18.9）
    private float _mirrorCooldown;     // 镜像跳跃自己的短节流（v0.18.9，0.5s，别和避障跳跃的 2.5s 混用）
    private float _jumpRotation;       // 起跳时的朝向（v0.18.10：空中锁定——FF14 玩家跳跃也是空中朝向不变；
                                       // 每帧转向会让 SetRotation 反复重置动画状态，一次性跳跃动作就被
                                       // 反复从头重播 = 抽搐）

    /// <summary>起跳时回调（控制器把它接到动画层的 PlayJump）。</summary>
    public Action? OnJump { get; set; }

    /// <summary>起跳后转为下落时回调一次（动画层切到空中/掉落时间轴 32，v0.18.9）。</summary>
    public Action? OnJumpFalling { get; set; }

    /// <summary>落地时回调一次（动画层播落地动画 33，v0.18.9）。</summary>
    public Action? OnJumpLanded { get; set; }

    /// <summary>
    /// 是否允许跳跃（v0.17.13）。由动画层喂入：<b>动作表里没有跳跃动画就不许跳</b> ——
    /// 物理在飞、骨骼不动是最糟的观感（用户实测"跳一下完全没有动作"）。
    /// </summary>
    public bool JumpEnabled { get; set; }

    /// <summary>她当前是否在跳跃中（v0.18.8，镜像跳跃功能供动画层/控制器查询）。</summary>
    public bool IsJumping => _jumping;

    /// <summary>
    /// 镜像玩家跳跃（v0.18.8 demo）：以<b>玩家实测的起跳垂直速度</b>起跳，
    /// 弧线大致与玩家一致。已在跳跃中 / 冷却未过 / 没有跳跃动画时静默忽略
    ///（控制器每帧都可能调用，靠这里的节流防连跳）。
    /// </summary>
    public bool TriggerMirrorJump(float takeoffVy)
    {
        // ⚠️ v0.18.9：不再借用避障跳跃的 2.5 秒冷却（那是给"卡住脱困跳"用的，
        // 玩家连续跳一次就被吞一次 = 用户实测"连续跳效果不好，似乎有内置冷却"）。
        // 镜像跳跃用自己 0.5 秒的短节流，只防同一次滞空内重复触发。
        if (_jumping || _mirrorCooldown > 0f || !JumpEnabled)
            return false;

        _jumping          = true;
        _jumpVelY         = Math.Clamp(takeoffVy, 4.5f, 8f);   // 贴近玩家起跳速度，弧线才同步
        _mirrorCooldown   = 0.5f;
        _jumpFallSignaled = false;
        _jumpRotation     = CurrentRotation;                   // 空中朝向锁定（v0.18.10）
        OnJump?.Invoke();
        return true;
    }

    private const float JumpVelocity   = 5.2f;   // 起跳初速（米/秒）→ 最高约 0.75 米
    private const float JumpGravity    = 18f;    // 下落加速度（米/秒²）→ 滞空约 0.6 秒
    private const float JumpCooldown   = 2.5f;   // 两次尝试跳跃的最小间隔（秒）
    private const float JumpAfterStuck = 0.6f;   // 被挡这么久才尝试跳跃（秒）

    /// <summary>说话时停下来（比"边走边念台词"自然得多）。</summary>
    public bool Paused { get; set; }

    /// <summary>
    /// 是否启用墙体避障（v0.17.3）。
    /// 关掉她就恢复成直线穿模；开着就会贴墙滑 / 侧向绕。
    /// </summary>
    public bool AvoidanceEnabled { get; set; } = true;

    /// <summary>重置内部状态。换区 / 重新召唤时调用，否则会带着上一段的速度继续冲。</summary>
    public void Reset()
    {
        CurrentSpeed    = 0f;
        CurrentGait     = Gait.站立;
        IsMoving        = false;
        _hasLastTime    = false;
        _lastTime       = 0f;
        _stuckTime      = 0f;
        _stuckRef       = Vector3.Zero;
        _jumping        = false;
        _jumpVelY       = 0f;
        _jumpCooldown   = 0f;
        _jumpFallSignaled = false;
        _mirrorCooldown = 0f;
        _detourTimer    = 0f;
        _detourDir      = Vector3.Zero;
        _detourSign     = 0;
        _detourCooldown = 0f;
        _watchdogTime   = 0f;
        _watchdogRef    = Vector3.Zero;
    }

    /// <summary>
    /// 每帧调用。整个执行层就这一个入口。
    /// </summary>
    /// <param name="npc">她。</param>
    /// <param name="goal">决策层给的<b>世界坐标</b>目标。</param>
    /// <param name="desiredFacing">希望她面朝的方向（弧度）。
    /// <b>只在站定时才用</b>；一旦真的走起来就改成看前进方向。</param>
    /// 也可以是"看着玩家"——由调用方决定。</param>
    public void Update(NpcHandle npc, MoveGoal goal, float desiredFacing)
    {
        if (npc.Address == nint.Zero)
        {
            IsMoving = false;
            return;
        }

        var now       = (float)_clock.Elapsed.TotalSeconds;
        var deltaTime = _hasLastTime ? Math.Clamp(now - _lastTime, 0f, 0.25f) : 0.016f;
        _lastTime     = now;
        _hasLastTime  = true;

        var gameObject = (GameObject*)npc.Address;
        var current    = ToNumerics(gameObject->Position);

        var toTarget = new Vector3(goal.Point.X - current.X, 0f, goal.Point.Z - current.Z);
        var distance = toTarget.Length();

        // ── 瞬移兜底 ──
        // 优先于一切：跑图 / 传送之后慢慢挪既跟不上又难看。
        var heightGap = MathF.Abs(goal.GroundY - current.Y);

        // ⚠️ v0.17.2：瞬移分支也必须遵守"做动作就不移动"。
        // 这条分支在旧代码里**完全没检查 Paused** —— 于是只要玩家跑远 / 上下楼，
        // 她就会一边保持表情姿态一边被瞬移过去，正是用户说的"又开始边走边做动作"，
        // 而且它专挑高差大的时候发作（和瞬移出问题的场景完全重合，很容易混淆）。
        if (!Paused
            && (distance > TeleportDistance || heightGap > TeleportHeight)
            && TryResummonNearPlayer(npc))       // v0.18.69.19：玩家悬空 → 冻结，继续正常移动逻辑
        {
            // v0.18.7：瞬移 = 在她身边"再次召唤"（落点出生点 + 状态全归零 + 面朝玩家）。
            npc.NeedsTeleport = true;
            _hasLastTime      = false;    // 位置跳了，别让下一帧的 delta 变成巨大位移
            return;
        }

        npc.NeedsTeleport = false;

        // 镜像跳跃节流倒计时（v0.18.9）
        if (_mirrorCooldown > 0f)
            _mirrorCooldown -= deltaTime;

        // ── 无进度看门狗（v0.18.3）──
        //
        // 与"她为什么没走"完全解耦的最后一道保险：不管是因为避障误判、
        // 绕行失败、动作停顿叠加还是状态机死角 —— 只要「目标还需要移动、
        // 人却长时间没挪窝」，就强制脱困（瞬移到玩家身边）。
        //
        // 阈值取 StuckTeleportSeconds + 2：比避障分支里那几条兜底更晚触发，
        // 让"有明确原因的卡住"先走自己的出路，这里只兜真正的无解情况。
        // 待机/观望（目标=自己脚下）不累计 —— 她本来就该站着。
        var flatNow = new Vector3(current.X, 0f, current.Z);

        if ((flatNow - _watchdogRef).LengthSquared() > 0.16f     // 挪过 0.4 米 = 有进度
            || distance <= goal.StopRadius + 0.3f                // 已经到位，站着是正常的
            || _jumping)                                          // 跳跃中，位置本来就在变
        {
            _watchdogRef  = flatNow;
            _watchdogTime = 0f;
        }
        else
        {
            _watchdogTime += deltaTime;

            if (StuckTeleportSeconds > 0f && _watchdogTime > StuckTeleportSeconds + 2f
                && TryResummonNearPlayer(npc))   // v0.18.69.19：玩家悬空 → 冻结，计时保留
            {
                _watchdogTime = 0f;
                _watchdogRef  = flatNow;
                _hasLastTime  = false;
            }
        }

        // ── 目标姿态（还没算实际速度）──
        var wantedGait = ResolveGait(goal.Gait, distance, goal);

        // ── 加减速 ──
        // 关键：速度是**连续量**。旧实现每帧按"离目标还多远"重算一个瞬时速度，
        // 起步瞬间就是满速、停下瞬间就是零 —— 这正是"滑块在拖"的观感来源。
        // 这里改成对速度本身做加减速，才有"起步→匀速→减速"的节奏。
        var targetSpeed = TargetSpeed(wantedGait, distance, goal);
        targetSpeed *= Math.Clamp(SpeedScale, 0.25f, 2.5f);

        var rate = targetSpeed > CurrentSpeed ? MotionTuning.Acceleration : MotionTuning.Deceleration;
        CurrentSpeed = Approach(CurrentSpeed, targetSpeed, rate * deltaTime);

        // ── 停住的条件 ──
        var stopped = Paused
                   || wantedGait == Gait.站立
                   || (distance <= goal.StopRadius && CurrentSpeed < 0.05f);

        if (stopped)
        {
            CurrentSpeed = 0f;
            IsMoving     = false;
            CurrentGait  = Gait.站立;

            // v0.18.8 demo：镜像跳跃在**站定状态**下也要跑垂直物理 ——
            // 否则站着触发跳跃只有动画没有位移（悬在半空定格到落地为止）。
            // 移动中的跳跃由下方主流程的正常积分处理，这里只兜站定的情况。
            if (_jumping)
            {
                var jx = gameObject->Position.X;
                var jy = gameObject->Position.Y;
                var jz = gameObject->Position.Z;

                _jumpVelY -= JumpGravity * deltaTime;
                var jumpY  = jy + _jumpVelY * deltaTime;
                var jGround = NpcGround.Resolve(jx, jz, jy, anchorToTarget: true);

                // v0.18.9：下落/落地信号（与移动路径同款）
                if (!_jumpFallSignaled && _jumpVelY < 0f)
                {
                    _jumpFallSignaled = true;
                    OnJumpFalling?.Invoke();
                }

                if (_jumpVelY < 0f && jumpY <= jGround + 0.05f)
                {
                    _jumping          = false;
                    _jumpFallSignaled = false;
                    OnJumpLanded?.Invoke();
                    NpcFactory.TeleportTo(npc, ToGame(new Vector3(jx, jGround, jz)), _jumpRotation);
                }
                else
                {
                    NpcFactory.TeleportTo(npc, ToGame(new Vector3(jx, jumpY, jz)), _jumpRotation);
                }
            }

            // 站着时看她想看的地方（一般是玩家；观望状态下会带一个摆动偏角）。
            // ⚠️ 一帧只能转一次。旧版这里转一次、下面走起来又转一次，
            // 角速度直接翻倍，而第一次转的结果被第二次调用整个覆盖掉 ——
            // 于是"站着看玩家"这条根本不起作用。
            RotateTowards(npc, desiredFacing, deltaTime, Gait.站立);

            // 停下时兜底朝向，免得歪着停。
            // ⚠️ 同样只在真的偏了才写：每帧 SetRotation 会重置动画状态（见 NpcFactory.TeleportTo 的注释）。
            if (MathF.Abs(CurrentRotation - gameObject->Rotation) > 0.0001f)
                gameObject->SetRotation(CurrentRotation);
            return;
        }

        // ── 走起来 ──
        // 接近目标时按比例收速（arrive 行为），否则她会冲过头再掉头。
        var dir     = distance > 0.001f ? toTarget / distance : Vector3.Zero;
        var goalDir = dir;   // 主方向（朝目标）；绕行时以它为基准偏转。

        // ── 避障与绕行（v0.18.3，承诺式绕行）──
        //
        // ⚠️⚠️ 前两版各错一次，这版把用户的两条要求一起满足：
        //   ①「旁边能过就马上绕过去」（要真正的避障跟随能力）；
        //   ②「绝不穿墙穿模」（红线）。
        //
        //   v0.17.x 扇形找路：每帧从 0° 重扫、每帧换方向 → 原地抽搐；
        //   v0.18.x 硬停等跳/瞬移：被墙挡住就站死 → "旁边明明能过却不动"。
        //
        //   现在是中间态 —— **承诺式绕行**：
        //   · 被挡的第一帧：扇形探一次（优先沿用上次成功的那一侧 —— 方向记忆防横跳），
        //     挑一个「探测为通畅」的偏转角，承诺沿它走 DetourDuration 秒；
        //   · 承诺期内<b>每帧仍对绕行方向重新做碰撞探测</b>（红线：方向必须帧帧合法），
        //     只是不再看主方向 —— 主方向本来就是被挡的才要绕；
        //   · 绕行方向也被堵（拐进死角）→ 中断承诺，交回硬停 + 跳/瞬移兜底；
        //   · 承诺期满自动回主方向：绕过去了就直走，没绕过就接着挑 —— 一段段接龙绕过墙角；
        //   · 挑不出任何通畅方向（死胡同 / 厚墙）才硬停等跳/瞬移。
        var blocked = false;

        if (_detourTimer > 0f && !_jumping)
        {
            _detourTimer -= deltaTime;
            dir = _detourDir;   // 绝对方向承诺：路线不随玩家移动甩摆（v0.18.5）。

            // v0.18.6 中和：承诺期内向目标方向**缓慢回弯**（≤0.8 rad/s）。
            // v0.18.5 的纯直线绕行弧拖得太远（观感"避障太过"），
            // v0.18.4 完全锚死目标方向又被玩家移动甩得乱摆 ——
            // 取中间：一条稳定的弧线，绕过障碍后圆润地收回来，又不会甩摆。
            if (goalDir.LengthSquared() > 0.0001f)
            {
                var goalYaw   = MathF.Atan2(goalDir.X, goalDir.Z);
                var detourYaw = MathF.Atan2(_detourDir.X, _detourDir.Z);
                var home      = NormalizeAngle(goalYaw - detourYaw);
                _detourDir    = RotateHorizontal(_detourDir, Math.Clamp(home, -0.8f * deltaTime, 0.8f * deltaTime));
                dir           = _detourDir;
            }

            if (AvoidanceEnabled && NpcAvoidance.IsBlocked(current, dir))
            {
                // 绕行方向被堵 = 贴墙走到了拐角：不停车，就近挑通畅角顺墙滑过去；
                // 真死角 / 被三面围死才中断承诺落入硬停。
                if (TryPickDetour(current, dir, out var slideDeg))
                {
                    _detourDir   = RotateHorizontal(dir, slideDeg * (MathF.PI / 180f));
                    dir          = _detourDir;
                    _detourTimer = MathF.Max(_detourTimer, 0.4f);   // 滑行续约，不算新绕行
                }
                else
                {
                    _detourTimer    = 0f;
                    _detourCooldown = Math.Clamp(DetourCooldown, 0.3f, 0.6f);   // v0.18.6 中和：0.4 → 0.4~0.6
                    blocked         = true;
                }
            }
        }
        else if (!_jumping && AvoidanceEnabled && NpcAvoidance.IsBlocked(current, dir))
        {
            blocked = true;
        }

        // 刚被挡（冷却已过）：立刻挑绕行方向，本帧就沿它走 —— "旁边能过就马上过"。
        if (blocked && _detourCooldown <= 0f && distance > 0.5f
            && TryPickDetour(current, goalDir, out var pickedDeg))
        {
            _detourDir   = RotateHorizontal(goalDir, pickedDeg * (MathF.PI / 180f));
            dir          = _detourDir;
            _detourTimer = MathF.Max(DetourDuration, 0.3f);
            blocked      = false;
        }

        if (_detourCooldown > 0f)
            _detourCooldown -= deltaTime;

        if (blocked)
        {
            // 站在障碍前不动，但要保持面朝目标（原地等着 / 准备跳）。
            RotateTowards(npc, MathF.Atan2(dir.X, dir.Z), deltaTime, Gait.站立);

            if (MathF.Abs(CurrentRotation - gameObject->Rotation) > 0.0001f)
                gameObject->SetRotation(CurrentRotation);

            IsMoving    = false;
            CurrentGait = Gait.站立;

            // 卡住计时：位置几乎没变就累加，到阈值依次触发跳跃 / 瞬移。
            var here2 = new Vector3(gameObject->Position.X, 0f, gameObject->Position.Z);
            var ref2  = new Vector3(_stuckRef.X, 0f, _stuckRef.Z);

            if ((here2 - ref2).LengthSquared() > 0.25f)
            {
                _stuckRef  = current;
                _stuckTime = 0f;
            }
            else
            {
                _stuckTime += deltaTime;

                // ① 卡住 0.6 秒 → 只在**悬空矮障**前尝试跳跃（实心墙跳不过去）。
                if (_stuckTime > JumpAfterStuck && !_jumping && _jumpCooldown <= 0f
                    && distance > 1.0f
                    && JumpEnabled
                    && NpcAvoidance.ProbeFront(current, dir) == NpcAvoidance.BlockKind.LowObstacle)
                {
                    _jumping      = true;
                    _jumpVelY     = JumpVelocity;
                    _jumpCooldown = JumpCooldown;
                    _jumpRotation = CurrentRotation;      // 空中朝向锁定（v0.18.10）
                    _jumpFallSignaled = false;
                    OnJump?.Invoke();
                }

                // ② 卡住 3 秒（跳也没用 / 没有跳跃空间）→ 瞬移兜底。
                //
                // ⚠️⚠️ v0.18.2：**移除了 `distance > 2f` 这个条件**。
                // 它和上面的跳跃条件一起构成死锁：
                //   跳跃要求 ProbeFront == LowObstacle —— 实心墙 / 关着的门不是矮障，跳不了；
                //   瞬移要求 distance > 2f        —— 而跟随站位点就在你身后约 1.8 米，不满足。
                // 两者同时失效 = 她永远站在原地。行为层的"玩家走开就跟随"也救不了，
                // 因为此时玩家就在她旁边（距离没超标）——用户实测「愣在原地不动」就是这么来的。
                //
                // v0.18.7：瞬移语义统一为「在她身边再次召唤」。
                if (_stuckTime > 3f && !_jumping && TryResummonNearPlayer(npc))
                {
                    npc.NeedsTeleport = true;
                    _stuckTime        = 0f;
                    _stuckRef         = current;
                    _hasLastTime      = false;
                }

                // ③ 终极兜底（v0.18.2）：上面那条瞬移把落点放在"墙的这一侧"，
                // 如果墙很厚 / 门关着，她可能仍然站在障碍前，计时又会被重置重来，
                // 于是形成"瞬移→还是被挡→再瞬移"的循环。
                // 所以再加一条**无条件**出路：卡满 StuckTeleportSeconds 直接出现在你身边。
                // 落点取玩家脚下 —— 那是她一定站得住的地方，绝不会卡住。
                if (StuckTeleportSeconds > 0f && _stuckTime > StuckTeleportSeconds && !_jumping
                    && TryResummonNearPlayer(npc))
                {
                    _stuckTime   = 0f;
                    _stuckRef    = current;
                    _hasLastTime = false;
                }
            }

            // 冷却计时推进
            if (_jumpCooldown > 0f)
                _jumpCooldown -= deltaTime;

            return;
        }

        // 没被挡：卡住计时正常复位（走过来了）。
        _stuckRef  = current;
        _stuckTime = 0f;

        var dirLen = dir.Length();
        if (dirLen > 0.0001f)
            dir /= dirLen;

        var step = MathF.Min(CurrentSpeed * deltaTime, distance);

        // ── 硬碰撞钳制（v0.18.18）──
        //
        // 决策层（避障/绕行）只是"尽量不走进碰撞体"，判漏了照样穿模 —— 第一版家具
        // 防线就是这么被绕过的。这里把**位移本身**变成碰撞感知的：实际要走的这一步
        // 先沿行进方向扫一遍（静态几何 + 家具圆），撞上就缩短步长。
        // 她每帧 SetPosition，游戏的碰撞响应帮不了她 —— 这个钳制就是"参与碰撞"的等效物。
        // 跳跃中不钳（抛物线本来就允许离地越过矮障）。
        if (!_jumping && AvoidanceEnabled && step > 0.001f && dirLen > 0.0001f)
        {
            var allowed = NpcAvoidance.SweepStep(current, dir, step);
            if (allowed < step)
                step = MathF.Max(0f, allowed - 0.05f);
        }


        // 移动中面朝前进方向 —— 边走边死盯着玩家看会像被人拖着走，很假。
        RotateTowards(npc, MathF.Atan2(dir.X, dir.Z), deltaTime, wantedGait);

        // ── 垂直方向 ──
        //
        // ⚠️ 关键修正（v0.14.0）：贴的是**她自己脚下**的地面高度，不是玩家的 Y。
        // 以前 goal.GroundY 传的是 playerPosition.Y，于是你一上坡 / 站到石头上，
        // 她就被整体抬到你脚底的高度 → 悬在半空"踏空而行"。
        // 现在每帧按她自己的 XZ 问游戏要地面高度（见 Core/NpcGround.cs）。
        var next    = new Vector3(current.X + dir.X * step, current.Y, current.Z + dir.Z * step);
        var groundY = NpcGround.Resolve(next.X, next.Z, goal.GroundY);

        float targetY;
        if (_jumping)
        {
            // ── 跳跃抛物线（v0.17.8）──
            // 欧拉积分：每帧 Y += 速度×dt，速度 -= 重力×dt。
            // 位置每帧经 TeleportTo 写回，下一帧的 current.Y 就是上一帧的积分值。
            _jumpVelY -= JumpGravity * deltaTime;
            var jumpY  = current.Y + _jumpVelY * deltaTime;

            // v0.18.9：垂直速度转负（到顶开始下落）→ 通知动画层切空中/掉落时间轴
            if (!_jumpFallSignaled && _jumpVelY < 0f)
            {
                _jumpFallSignaled = true;
                OnJumpFalling?.Invoke();
            }

            if (_jumpVelY < 0f && jumpY <= groundY + 0.05f)
            {
                // 落地：交回贴地逻辑，并通知动画层播落地动画
                _jumping          = false;
                _jumpFallSignaled = false;
                OnJumpLanded?.Invoke();
                targetY = groundY;
            }
            else
            {
                targetY = jumpY;
            }
        }
        else
        {
            // 小差距用指数逼近（看起来像"走下坡"），大差距（上下楼）才直接对齐。
            var dy = groundY - current.Y;
            targetY = MathF.Abs(dy) > 2f
                ? groundY
                : current.Y + dy * (1f - MathF.Exp(-8f * deltaTime));
        }

        next = new Vector3(next.X, targetY, next.Z);

        // 跳跃期间朝向锁定在起跳时的朝向（v0.18.10）—— 与 FF14 玩家跳跃一致，
        // 也避免 SetRotation 每帧重置动画状态导致一次性跳跃动作反复重播。
        NpcFactory.TeleportTo(npc, ToGame(next), _jumping ? _jumpRotation : CurrentRotation);

        // 冷却计时推进（跳跃冷却在阻挡分支里已经推过一次，这里只处理正常行进）。
        if (_jumpCooldown > 0f)
            _jumpCooldown -= deltaTime;

        IsMoving    = true;
        CurrentGait = CurrentSpeed > MotionTuning.RunGaitThreshold ? Gait.奔跑 : Gait.行走;
    }

    /// <summary>按姿态与剩余距离决定目标速度（arrive：越近越慢）。</summary>
    private float TargetSpeed(Gait gait, float distance, MoveGoal goal)
    {
        if (gait == Gait.站立)
            return 0f;

        var baseSpeed = BaseSpeedFor(gait, distance);

        // arrive：进入 SlowRadius 之后按比例收速，最后刚好停在 StopRadius 上。
        // 不这么做的话她会一直冲过目标、掉头、再冲过来。
        if (distance < goal.SlowRadius)
            baseSpeed *= Math.Clamp(distance / MathF.Max(goal.SlowRadius, 0.01f), 0.15f, 1f);

        return baseSpeed;
    }

    /// <summary>
    /// 基速：**由玩家实测速度派生**，不再是写死的数字。
    ///
    /// <list type="bullet">
    /// <item>奔跑姿态 → 玩家速度 × <see cref="MotionTuning.RunCatchUp"/>（不乘就永远追不上），
    ///       同时动画层切到 run 动作 —— 这就是"跑起来要用跑的动作"。</item>
    /// <item>行走姿态 → 与玩家同速。</item>
    /// <item>玩家几乎静止 → 用兜底步速，否则她只能站着等你先动。</item>
    /// </list>
    /// 上下限用来兜住瞬移 / 极端加速，别把她甩飞。
    /// </summary>
    private float BaseSpeedFor(Gait gait, float distance)
    {
        // v0.18.4：跟随强度（引力）。
        // 奔跑全程放大（追人就是要有"非追上不可"的劲）；行走只在她仍落后于
        // 迟滞带下限（WalkLagDistance）时放大 —— 贴身时保持同速，不怼着你走。
        //
        // ⚠️ 行走提速的上限必须压在跑走分界（RunGaitThreshold=3.0）之下：
        // CurrentGait 按速度切姿态，速度一过 3.0 就判成奔跑 → 下一帧走奔跑公式
        // → 又慢回来 → 又判成行走 …… 姿态来回横跳（"走两步跑两步"）。
        // 2.9 = 分界之下留 0.1 余量。
        var strength = Math.Clamp(FollowStrength, 0.5f, 2.5f);

        if (gait == Gait.奔跑)
        {
            var chase = PlayerSpeed > 0.5f ? PlayerSpeed * MotionTuning.RunCatchUp
                                           : MotionTuning.FallbackRun;
            // v0.18.20：7.2 上限撤销，恢复 9.0 —— slide-trace + 用户目击证实平移与
            // 速度无关（她站得笔直地在滑 = 走路动画根本没播，换什么上限都一样滑）。
            // 真正的病灶在动画层，见 slide-trace 的动画状态列。
            return Math.Clamp(chase * strength, 1.5f, MathF.Max(MotionTuning.MaxRunSpeed, 9f));
        }

        var same = PlayerSpeed > 0.3f ? PlayerSpeed : MotionTuning.FallbackWalk;
        var lag  = distance > WalkLagDistance ? MathF.Max(strength, 1f) : 1f;
        var cap  = lag > 1f ? MotionTuning.RunGaitThreshold - 0.1f : MotionTuning.MaxWalkSpeed;
        return Math.Clamp(same * lag, 0.8f, cap);
    }

    /// <summary>目标姿态：<c>StopRadius</c> 内一律视为站立（已经到位了）。</summary>
    private static Gait ResolveGait(Gait requested, float distance, MoveGoal goal)
    {
        if (distance <= goal.StopRadius)
            return Gait.站立;

        return requested;
    }

    /// <summary>
    /// 朝向平滑：按最大角速度逼近，而不是直接赋值。
    ///
    /// 直接 <c>SetRotation(target)</c> 的话她会"瞬移式转头"——很假，
    /// 而且游戏自己的朝向插值会和我们打架，出现抖动。
    ///
    /// <para><b>站定时（v0.14.0）走的是另一条规则</b>：
    /// 以前她会把整个身子一路拧向玩家，看起来就是"整个人原地旋转"。
    /// 现在改成"只从停下时的朝向偏最多 <see cref="MotionTuning.StandingTurnLimit"/>，
    /// 而且转得很慢、还有死区" —— 剩下的部分交给<b>头部/视线</b>去看（游戏自己的 look-at），
    /// 真要整个人转过来，是走两步顺带转的。</para>
    /// </summary>
    private void RotateTowards(NpcHandle npc, float target, float deltaTime, Gait gait)
    {
        var wanted = NormalizeAngle(target);

        if (gait == Gait.站立)
        {
            // 站定：先按"停下时的朝向"限幅，再套死区 + 慢速。
            var raw     = NormalizeAngle(wanted - _restYaw);

            // v0.16.2：开了 AllowFullTurn 就**不再限幅** ——
            // 头/眼追不上时让她真的把身体转过去（并带转身动作），而不是硬掰成平移旋转。
            var offset  = AllowFullTurn
                        ? raw
                        : Math.Clamp(raw, -MotionTuning.StandingTurnLimit, MotionTuning.StandingTurnLimit);
            var diff    = NormalizeAngle(NormalizeAngle(_restYaw + offset) - CurrentRotation);

            // v0.18.1：站定时若还需明显转身，标记「正在原地转身」让动画层播转身动作，
            // 而不是整个人干滑着转过去。旧逻辑只有"头/眼追不上 + 随机选中转身追"才触发，
            // 正常跟随里几乎碰不到，于是转身动画形同虚设（用户反馈"转身的动作直接没了"）。
            TurningInPlace = MathF.Abs(diff) > TurnAnimMinAngle;
            TurnDirection  = diff > 0f ? 1 : -1;

            if (MathF.Abs(diff) < MotionTuning.StandingTurnDeadzone)
            {
                YawRate             = 0f; // 已进入死区：不再转身，转身动画应收起
                npc.DesiredRotation = CurrentRotation;
                return;
            }

            var slow = Math.Clamp(diff * (1f - MathF.Exp(-MotionTuning.StandingTurnStiffness * deltaTime)),
                                  -MotionTuning.StandingTurnRate * deltaTime,
                                   MotionTuning.StandingTurnRate * deltaTime);
            CurrentRotation     = NormalizeAngle(CurrentRotation + slow);
            YawRate             = deltaTime > 0f ? slow / deltaTime : 0f;
            npc.DesiredRotation = CurrentRotation;
            return;
        }

        // 移动中：面朝前进方向。同时把"停下时的朝向基准"持续更新成当前朝向，
        // 这样她一停住，_restYaw 就是她最后走的方向，衔接得上。
        var diffMoving = NormalizeAngle(wanted - CurrentRotation);
        var maxRate    = MotionTuning.TurnRate * deltaTime;
        var eased      = diffMoving * (1f - MathF.Exp(-TurnStiffness * deltaTime));

        var moved           = Math.Clamp(eased, -maxRate, maxRate);
        CurrentRotation     = NormalizeAngle(CurrentRotation + moved);
        YawRate             = deltaTime > 0f ? moved / deltaTime : 0f;
        _restYaw            = CurrentRotation;
        npc.DesiredRotation = CurrentRotation;
    }

    /// <summary>把 a 朝 b 推进，每步最多 step。</summary>
    private static float Approach(float current, float target, float step)
    {
        if (current < target) return MathF.Min(current + step, target);
        return MathF.Max(current - step, target);
    }

    /// <summary>归一到 (-π, π]。</summary>
    public static float NormalizeAngle(float a)
    {
        while (a >  MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    public static Vector3 ToNumerics(GameVector3 v) => new(v.X, v.Y, v.Z);
    public static GameVector3 ToGame(Vector3 v)      => new(v.X, v.Y, v.Z);

    /// <summary>
    /// 扇形挑一个「探测为通畅」的偏转角（v0.18.3）。
    ///
    /// <para><b>只在被挡的第一帧调用一次</b>，不是每帧 —— 旧版每帧从 0° 重扫、
    /// 每帧换方向，才有了"原地抽搐"。这里靠两点防横跳：</para>
    /// <list type="bullet">
    /// <item><b>方向记忆</b>：优先沿用上次绕行成功的那一侧（<c>_detourSign</c>），</item>
    /// <item><b>承诺制</b>：挑中后在 <see cref="DetourDuration"/> 秒内不再重新挑。</item>
    /// </list>
    ///
    /// <para>候选角（v0.18.6 中和：20° 步进、±90°，共 10 个）—— v0.18.5 的 15° 起绕
    /// 太敏感（一点小障碍就大弧线，观感"避障太过"），回调到 20° 起步。
    /// 同侧先扫一遍再对侧，先近后远 —— 偏转越小路线越顺。
    /// 每个候选都用 <see cref="NpcAvoidance.Probe"/> 实测，
    /// 挑出来的方向一定是<b>碰撞通畅</b>的，绝不把她领进几何体。</para>
    ///
    /// <para>两处调用：被挡起绕时 <paramref name="baseDir"/> = 目标方向；
    /// 贴墙滑行遇拐角时 = 当前绕行方向（就近顺墙滑）。</para>
    /// </summary>
    private bool TryPickDetour(Vector3 from, Vector3 baseDir, out float deg)
    {
        deg = 0f;

        if (baseDir.LengthSquared() < 0.0001f)
            return false;

        var sign = _detourSign != 0 ? _detourSign : (Random.Shared.Next(2) == 0 ? 1 : -1);

        Span<float> candidates =
        [
            sign * 20f, sign * 40f, sign * 60f, sign * 80f, sign * 90f,
           -sign * 20f, -sign * 40f, -sign * 60f, -sign * 80f, -sign * 90f,
        ];

        foreach (var c in candidates)
        {
            var candidate = RotateHorizontal(baseDir, c * (MathF.PI / 180f));

            if (NpcAvoidance.Probe(from, candidate) == NpcAvoidance.BlockKind.Clear)
            {
                deg         = c;
                _detourSign = c > 0f ? 1 : -1;
                return true;
            }
        }

        return false;
    }

    /// <summary>绕 Y 轴旋转水平向量（弧度）。与 FFXIV 的 yaw 约定一致（Atan2(x, z)）。</summary>
    private static Vector3 RotateHorizontal(Vector3 v, float rad)
    {
        var cos = MathF.Cos(rad);
        var sin = MathF.Sin(rad);
        return new Vector3(v.X * cos + v.Z * sin, 0f, v.Z * cos - v.X * sin);
    }

    /// <summary>
    /// 出生点式落点（v0.18.4）：以玩家朝向为基准、按 <see cref="SpawnOffsetBehind"/> /
    /// <see cref="SpawnOffsetSide"/> 偏移，贴地采样并做<b>站位空间校验</b>。
    ///
    /// <para>这是所有"瞬移类落点"的统一出口（瞬移兜底 / 卡住脱困共用）——
    /// 也就是用户说的「瞬移 = 重生在出生点」：出生点在哪，面板两个滑条说了算。</para>
    ///
    /// <para>⚠️ 不穿墙红线：落点若被几何体包住（玩家贴墙站时偏移点会在墙里），
    /// 一律退回玩家脚下 —— 那里必然可站立。</para>
    /// </summary>
    private (Vector3 Point, float Y) SpawnPointLanding(float playerGroundY)
    {
        var to    = PlayerPosition;
        var yaw   = PlayerRotation;
        var fwd   = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        var right = new Vector3(fwd.Z, 0f, -fwd.X);

        var x = to.X - fwd.X * SpawnOffsetBehind + right.X * SpawnOffsetSide;
        var z = to.Z - fwd.Z * SpawnOffsetBehind + right.Z * SpawnOffsetSide;

        // ⚠️ 贴地按落点自己的 XZ 采样，且绕开节流：
        // 玩家所在层就是目标层，用玩家高度当锚才不会被缓存里的旧楼层带偏。
        var y = NpcGround.Resolve(x, z, playerGroundY, anchorToTarget: true);
        if (!float.IsFinite(y) || MathF.Abs(y - playerGroundY) > 6f)
            y = playerGroundY;

        if (!NpcAvoidance.HasStandRoom(new Vector3(x, y, z)))
            return (new Vector3(to.X, 0f, to.Z), playerGroundY);

        return (new Vector3(x, 0f, z), y);
    }

    /// <summary>
    /// 瞬移守卫 + 执行（v0.18.69.19）：玩家悬空（比她高出 <see cref="PlayerAirGap"/>）
    /// 时<b>冻结所有瞬移兜底</b>并返回 false。
    ///
    /// <para>⚠️ 为什么必须冻：玩家在空中时 <see cref="SpawnPointLanding"/> 的贴地锚
    /// （玩家 Y）下方没有地面，落点会被写成玩家的空中 Y → 她被放到空中 →
    /// 下一帧 <c>heightGap = |goal.GroundY − 她的Y|</c> 变成整个飞行高度
    /// （&gt; <see cref="TeleportHeight"/>）→ 又触发瞬移 → <b>每帧重召唤一次</b>，
    /// 即用户实测的「玩家骑坐骑在天上时她反复刷新」。</para>
    ///
    /// <para>冻结期间她在地面照常追 / 等；4 个调用方的计时<b>都保留</b>，
    /// 所以「玩家落地」会自然成为触发瞬移的扳机 —— 那一次性的落点正是想要的。</para>
    ///
    /// <para>返回 true = 已瞬移；false = 被冻结。</para>
    /// </summary>
    private bool TryResummonNearPlayer(NpcHandle npc)
    {
        if (npc.Address != nint.Zero && PlayerAirGap > 0f)
        {
            var herY = ((GameObject*)npc.Address)->Position.Y;
            if (PlayerPosition.Y - herY > PlayerAirGap)
                return false;
        }
        ResummonNearPlayer(npc);
        return true;
    }

    /// <summary>
    /// 瞬移的统一语义（v0.18.7）：**在她身边"再次召唤"**。
    ///
    /// <para>落点 = 出生点（玩家朝向基准 + 面板「出生点·前后/左右」偏移，
    /// 经贴地采样与 <see cref="NpcAvoidance.HasStandRoom"/> 站位校验，
    /// 站不下自动退回玩家脚下）。落定后面朝玩家、运动状态全部归零
    ///（见 <see cref="SoftResummon"/>）—— 观感和行为上等价于重新召唤了一次。</para>
    ///
    /// <para>历史版本在这里做过"沿路径探测落点 / 落在墙这一侧 / 落点站位校验后改出生点"
    /// 一整套花活，实际效果就是两个字：复杂 —— 落点离玩家忽远忽近、状态带着旧账
    /// （朝向突变、绕行残留、落地又立刻被判挡）。现在一刀切：<b>瞬移=召唤</b>。</para>
    /// </summary>
    private void ResummonNearPlayer(NpcHandle npc)
    {
        var (point, y) = SpawnPointLanding(PlayerPosition.Y);

        // 落定后面朝玩家 —— "再次召唤"的观感：出现在你身边，看着你。
        var fx     = PlayerPosition.X - point.X;
        var fz     = PlayerPosition.Z - point.Z;
        var facing = MathF.Atan2(fx, fz);

        SoftResummon(npc, new Vector3(point.X, y, point.Z), facing);
    }

    /// <summary>
    /// 软重召唤：位置写过去之后，把运动层的<b>全部状态归零</b> ——
    /// 速度、姿态、朝向、卡住计时、绕行承诺、看门狗、跳跃残留。
    ///
    /// <para>等价于"销毁再召唤一次"，但<b>不走对象销毁/重建</b>：
    /// CreateBattleCharacter + PrepareModel 是重操作且有换区窗口的崩溃史（C0000005），
    /// 频繁触发才是真正的风险源。只写位置 + 清托管状态，拿到重召唤的全部好处，零风险。</para>
    /// </summary>
    private void SoftResummon(NpcHandle npc, Vector3 landing, float facing)
    {
        NpcFactory.TeleportTo(npc, ToGame(landing), facing);

        Reset();                          // 全部运动状态归零（含绕行 / 看门狗 / 跳跃 / 卡住计时）

        CurrentRotation     = facing;     // 朝向从"面朝玩家"重新开始，
        _restYaw            = facing;     // 站定转向的基准也以它为原点
        npc.DesiredRotation = facing;

        _stuckRef    = landing;
        _watchdogRef = new Vector3(landing.X, 0f, landing.Z);
    }
}
