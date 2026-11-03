namespace SmartNpc.Core;

/// <summary>
/// 行为与跟随的**固定常量**。
///
/// <para>为什么不再是配置项（v0.13.0 起）：这些数字以前暴露在面板上让用户调，
/// 但它们互相牵连（改了跑的距离却没改到位判定 → 她会在两个状态之间反复横跳），
/// 而且绝大多数人一辈子只会动两三个。真正需要按手感的
/// 「身后距离 / 侧移 / 瞬移距离 / 速度倍率 / 游荡范围」都留在配置里。</para>
///
/// <para>值全部沿用精简前的默认值，<b>行为保持不变</b>。</para>
/// </summary>
internal static class BehaviourTuning
{
    /// <summary>拉开到这个距离就开始小跑（米）。</summary>
    public const float RunDistance = 7.5f;

    /// <summary>回到这个距离以内就恢复常速走（米）。</summary>
    public const float WalkDistance = 4.0f;

    /// <summary>
    /// 已经"到位"之后，玩家走到这个距离以外就重新跟着走（米）。
    ///
    /// ⚠️ 没有它她会**站在原地发呆**：到位后进入待机，而待机要待满 20 秒才考虑动，
    /// 而且动也是去"游荡"（围着旧锚点转），不是跟人 —— 于是你走远了她还在原地。
    /// 比 ArriveDistance 大一截是迟滞：不然她会在边界上一步一停地抖。
    /// </summary>
    public const float FollowAgainDistance = 4.5f;

    /// <summary>超出这个距离认为"玩家把她甩掉了"（米）。</summary>
    public const float StrayDistance = 13.0f;

    /// <summary>进入这个半径算"到位"（米）。</summary>
    public const float ArriveDistance = 2.6f;

    /// <summary>游荡选点时离当前位置的最小距离（米），避免原地打转。</summary>
    public const float RoamMin = 2.0f;

    /// <summary>待机小动作的最小间隔（秒）。</summary>
    public const float IdleGestureMin = 6.0f;

    /// <summary>待机小动作的最大间隔（秒）。</summary>
    public const float IdleGestureMax = 15.0f;

    /// <summary>站定这么久之后开始环顾四周（秒）。</summary>
    public const float LookAroundAfter = 20.0f;

    /// <summary>待机这么多帧之后才考虑搭话，避免刚说完又找话说。</summary>
    public const int ChatTriggerIdle = 45;
}

/// <summary>
/// 说话与开关类的固定值。
///
/// <para>以前是复选框，但这些没有"关掉之后更有用"的场景，
/// 只有"关掉之后功能缺失"—— 留着只会让人以为关掉更好。</para>
/// </summary>
internal static class BrainTuning
{
    /// <summary>自言自语的最小间隔（秒）。</summary>
    public const int MinIdleSeconds = 18;

    /// <summary>自言自语的最大间隔（秒）。</summary>
    public const int MaxIdleSeconds = 40;

    /// <summary>是否允许她自言自语。</summary>
    public const bool AutoChatter = true;

    /// <summary>她说的话是否显示在聊天栏。</summary>
    public const bool ShowInChat = true;

    /// <summary>行为系统总开关。</summary>
    public const bool BehaviourEnabled = true;

    /// <summary>召唤时照抄玩家当前外观。</summary>
    public const bool CopyPlayerOnSummon = true;

    /// <summary>骑乘中隐藏她。</summary>
    public const bool HideWhileRiding = true;
}

/// <summary>
/// 说话配音（v0.18.36 引入，v0.18.37 改成真正的角色语音）。
///
/// <para>⚠️ 这一组是<b>可变</b>的静态值（其余 Tuning 都是 const）：
/// 配音开关、嗓音、行号由用户在面板里改，改完要<b>立刻生效</b>，不能等重新召唤。
/// 由 <c>NpcController.ApplySettings</c> 每次推一遍。</para>
/// </summary>
internal static class VoiceTuning
{
    /// <summary>说话时是否发声。</summary>
    public static bool Enabled = true;

    /// <summary>嗓音编号（写进 <c>Character.Vfx.VoiceId</c>）。0 = 不改动（沿用抄外观继承来的）。</summary>
    public static int PresetId;

    /// <summary>语音行号（<c>VfxContainer.LoadCharacterSound</c> 的第一个参数）。0 = 不发声。</summary>
    public static int LineId;

    /// <summary>两次配音之间的最短间隔（秒）——她连着说两句时不该叠音。</summary>
    public static float MinInterval = 0.35f;
}

/// <summary>
/// 头顶说话气泡（v0.18.49）——同样是可变静态值，由 <c>NpcController.ApplySettings</c> 每次推一遍，
/// 面板改完立刻生效（不必等她再说一句）。
/// </summary>
internal static class BalloonTuning
{
    /// <summary>说话时头顶是否弹游戏原生气泡（<c>Character.YellBalloon</c>）。</summary>
    public static bool Enabled = true;

    /// <summary>气泡停留时长（秒）——由我们自己托管关闭，不依赖语义未定的原生时长参数。</summary>
    public static float Seconds = 4f;
}

/// <summary>移动层的固定值。</summary>
internal static class MotionTuning
{
    /// <summary>行走动画的播放速度倍率（1 = 按实际速度自动匹配）。</summary>
    public const float WalkSpeedScale = 1.0f;

    /// <summary>加速度（米/秒²）。从静止起步到全速 ≈ 速度 / 加速度。</summary>
    public const float Acceleration = 7.5f;

    /// <summary>减速度（米/秒²）。比加速度大，停脚才干脆。</summary>
    public const float Deceleration = 11.0f;

    /// <summary>最大转向角速度（弧度/秒）。走路 3.0、奔跑 5.0 比较自然。</summary>
    public const float TurnRate = 3.6f;


    // ── 站定时的转向（v0.14.0）──
    //
    // 以前站定时她会一路把整个身子拧向玩家，转起来就是"整个人原地旋转"，很假。
    // 真人站着你走到她侧后方时，她多半只是**转头/转眼**看你，身子不会跟着拧过去；
    // 真要转身是走两步顺带转的。这里就是把这条翻译成三个数。

    /// <summary>站定时最多只从"停下那一刻的朝向"偏这么多（弧度，0.95 ≈ 54°）。超出就不转了。</summary>
    public const float StandingTurnLimit = 0.95f;

    /// <summary>站定时的死区（弧度，0.30 ≈ 17°）。差得不多就别动，否则会一直微调地抖。</summary>
    public const float StandingTurnDeadzone = 0.30f;

    /// <summary>站定时的最大角速度（弧度/秒）。比移动中慢得多，才有"慢慢转过去"的味道。</summary>
    public const float StandingTurnRate = 2.0f;

    /// <summary>站定转向的指数平滑刚度。越小越"懒"。</summary>
    public const float StandingTurnStiffness = 4.5f;

    /// <summary>站定时头部/视线跟随镜头的最大距离（米）。离太远就别盯着了。</summary>
    public const float HeadLookMaxDistance = 14.0f;

    /// <summary>头 / 眼能转到的最大角度（弧度）。超过它头部就追不上，必须另想办法。</summary>
    public const float HeadTurnLimit = 1.15f;

    /// <summary>超转幅后"放弃追踪 / 转身追"的决定要保持多少秒（不能每帧重掷，否则抖）。</summary>
    public const float HeadOverflowHoldSeconds = 2.5f;

    /// <summary>
    /// 一旦开始转身，要一直转到与目标夹角小于这个值才算转完（弧度）。
    /// ⚠️ 不能再用 HeadTurnLimit 判定结束：那会在还剩几十度时就把转身动画掐掉，
    /// 剩下那一小段变成没有动画的干转 —— 用户反馈的"微小转向时容易出现平移"就是它。
    /// </summary>
    // ⚠️⚠️ 必须 **大于** MotionTuning.StandingTurnDeadzone(0.30)，否则永远转不完：
    // RotateTowards 在夹角 < 0.30 时就进死区、停止旋转了，
    // 若"转完"的判定比死区还小，她就永远达不到 → 转身动作无限循环（v0.16.6 实测退化）。
    public const float TurnDoneThreshold = 0.5f;

    /// <summary>转身最长持续多少秒（兜底）：超时强制结束，防止任何情况下动画死循环。</summary>
    public const float TurnMaxSeconds = 4.0f;

    /// <summary>原地转身角速度超过这个值（弧度/秒）就播转身动作。</summary>
    // ⚠️ v0.16.3：原来是 0.55，太高了。
    // 站定转身的角速度被 StandingTurnRate 钳在 1.25 rad/s 以内，稍微平缓一点转身
    // 就达不到 0.55 → SetTurning 压根没被调用 → 转身动作从来不播（脚不动）。
    // 降到 0.15：只要她在转就触发。
    public const float TurnAnimMinRate = 0.15f;

    /// <summary>
    /// 玩家静止时的兜底步速（米/秒），取 FFXIV 玩家步行速度。
    /// 没有它，玩家站着不动时她就只能原地站着。
    /// </summary>
    public const float FallbackWalk = 2.2f;

    /// <summary>玩家静止但需要"跑过去"时的兜底跑速（米/秒）。</summary>
    public const float FallbackRun = 4.5f;

    /// <summary>追玩家时的加速比（乘在玩家速度上）。1.0 = 永远追不上，1.35 = 能追上还显得利索。</summary>
    public const float RunCatchUp = 1.35f;

    /// <summary>常速行走的速度上限（米/秒），防止玩家瞬移/加速把她带飞。</summary>
    public const float MaxWalkSpeed = 3.2f;

    /// <summary>奔跑的速度上限（米/秒）。</summary>
    public const float MaxRunSpeed = 6.5f;

    /// <summary>
    /// 玩家速度超过这个值就判定为"在跑"（米/秒），于是她也切奔跑姿态 → 播 run 动画。
    /// 取 3.0 是因为玩家走路约 2.2、跑步约 4.5，3.0 正好落在两者中间当分界。
    /// </summary>
    public const float RunGaitThreshold = 3.0f;
}