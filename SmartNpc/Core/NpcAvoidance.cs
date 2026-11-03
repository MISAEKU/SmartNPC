using System;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace SmartNpc.Core;

/// <summary>
/// 场景碰撞检测（v0.18.0）。
///
/// <para><b>核心思路：把角色当成一根"从脚到胸的竖条"，去问场景几何体
/// "我这根条上有几段被挡住"。</b></para>
///
/// <para>判据<b>不是</b>"射线有多高"，而是"挡住了我身体的百分之多少"：</para>
/// <list type="bullet">
/// <item><b>台阶 / 门槛</b>（约 0.2 米）：只挡住最底下 1 段 → 占比低 → 放行 ✓</item>
/// <item><b>箱子 / 矮台</b>（约 0.5–1 米）：挡住底下 2～3 段 → 占比过线 → 阻挡 ✓</item>
/// <item><b>围栏 / 墙面</b>（1.5 米以上）：整条全挡 → 挡死 → 只能绕 / 跳 / 瞬移 ✓</item>
/// </list>
///
/// <para><b>为什么横向要打三条（左肩 / 中心 / 右肩）</b>：
/// 角色不是一根零宽度的线。只打中心线的话，斜着蹭到箱子角、栏杆端头、墙的拐角
/// 会完全检测不到 —— 这正是"明明看着擦过去了，她却直接穿进去"的来源。</para>
///
/// <para><b>三态分类（决定能不能跳过去）</b>：
/// <list type="bullet">
/// <item><c>Clear</c>：被挡的段数低于台阶阈值 → 通行。</item>
/// <item><c>LowObstacle</c>：中上段被挡、<b>最底下那一段是空的</b> → 悬空障碍（栏杆横杆、桌面、矮台留缝）→ 可以跳过去。</item>
/// <item><c>Wall</c>：<b>连脚踝那一档都挡住了</b> → 立面从地面贯通 → 实心墙 → 跳也白跳，只能绕 / 瞬移。</item>
/// </list>
/// </para>
/// </summary>
internal static unsafe class NpcAvoidance
{
    /// <summary>往前探多远（米）。留一步刹车距离即可，太长会让她在门口也停住。</summary>
    public const float ProbeDistance = 1.6f;

    /// <summary>命中距离小于这个值才算"挡在脸上"（米）。</summary>
    public const float ContactDistance = 0.75f;

    /// <summary>
    /// 身体半宽（米）。左右各偏移这么多再打一条射线，模拟"肩膀"。
    /// 0.28 ≈ 人类肩宽的一半，够覆盖箱子角 / 栏杆端头 / 墙拐角。
    /// </summary>
    private const float HalfWidth = 0.28f;

    /// <summary>
    /// 沿身体高度取样的纵向档位（米，相对脚下）。<b>由低到高排列。</b>
    ///
    /// <para>这几档的选择：脚踝(0.10) 过门槛与贴地箱角；小腿(0.35) 打箱子；
    /// 腰(0.70) 打围栏下沿；胸(1.10) 打桌沿与墙裙；肩(1.50) 打齐肩障碍。</para>
    /// </summary>
    private static readonly float[] SampleHeights = [0.10f, 0.35f, 0.70f, 1.10f, 1.50f];

    /// <summary>
    /// 只对"超过这个高度才挡"的障碍尝试跳跃（米）。
    /// 低于它的属于地面起伏，本来就能走上去，不需要跳。
    /// </summary>
    public const float JumpableMinHeight = 0.5f;

    /// <summary>
    /// 在「她前方这么远」采样真实地面高度，用来区分贴地斜坡与真正的立面障碍（米）。
    /// 太近会取到她脚下的高度（斜坡上偏低），太远会取到障碍另一侧；0.5 米刚好在脚下与障碍之间。
    /// </summary>
    private const float NearSampleDistance = 0.5f;

    /// <summary>射线参数（native 必须解引用 4 元素数组，传 null 直接崩）。</summary>
    private static readonly int[] FlagsGame = [0x4000, 0, 0x4000, 0];

    /// <summary>硬碰撞扫描（SweepStep）的取样高度：小腿 / 腰。</summary>
    private static readonly float[] SweepHeights = [0.35f, 0.70f];

    /// <summary>前方障碍的三态分类。</summary>
    public enum BlockKind
    {
        /// <summary>通畅：只有最底下被挡（台阶），或完全没挡。</summary>
        Clear,

        /// <summary>实心墙：从脚踝到肩全挡 → 跳不过去。</summary>
        Wall,

        /// <summary>悬空矮障：脚踝处是空的，中上段被挡 → 可跳。</summary>
        LowObstacle,
    }

    /// <summary>
    /// 探测某个方向能不能过去。
    ///
    /// <para>做法：沿 <paramref name="unit"/> 方向，在「左肩 / 中心 / 右肩」
    /// 三个横向位置各打一组射线；每个位置再按 <see cref="SampleHeights"/>
    /// 逐档取样。命中点只要<b>明显高于地面</b>（超过 <see cref="JumpableMinHeight"/>）
    /// 就算真障碍 —— 斜坡、门口小门槛这类贴地的几何会被忽略，
    /// 只有墙 / 台阶 / 关着的门这种立面才拦她。三档横向里任意一档过不去，
    /// 这一侧就过不去。</para>
    /// </summary>
    public static BlockKind Probe(Vector3 from, Vector3 unit)
    {
        try
        {
            var bg = Framework.Instance()->BGCollisionModule;
            if (bg is null)
                return BlockKind.Clear;

            // ── 住宅家具/庭具（v0.18.17）──
            // BGCollision 射线看不见 HousingEventObject 的碰撞体（见 NpcHousing 类注释），
            // 所以在家具圆检测里补上：线段撞圆 = 实心障碍（家具没法从中间跳过去）。
            // 判定口径与下面的射线一致：命中距离进入 ContactDistance 内才算"挡在脸上"。
            var housingHit = NpcHousing.SegmentHit(from, unit, ProbeDistance, HalfWidth);
            if (housingHit < ContactDistance)
                return BlockKind.Wall;

            // 水平右向量：绕 Y 轴把 forward 转 90°。
            var right = new Vector3(unit.Z, 0f, -unit.X);
            var rl    = right.Length();
            right = rl > 0.0001f ? right / rl : Vector3.UnitX;

            // 地形基准：在她前方 0.5 米采样真实地面（anchorToTarget 强制打一条新射线，
            // 不走 NpcGround 的节流缓存，免得拿到旧楼层的高度）。
            var nearGround = NpcGround.Resolve(
                from.X + unit.X * NearSampleDistance,
                from.Z + unit.Z * NearSampleDistance,
                from.Y, anchorToTarget: true);

            var worstReal = 0;        // 三个横向位置里，被「真障碍」挡得最厉害的那个
            var worstBase = false;    // 它脚下那档（贴地档）也真被挡住了吗

            for (var lane = -1; lane <= 1; ++lane)
            {
                var lateral = right * (lane * HalfWidth);
                var baseX   = from.X + lateral.X;
                var baseZ   = from.Z + lateral.Z;

                var realBlocked = 0;
                var baseBlocked = false;

                for (var i = 0; i < SampleHeights.Length; ++i)
                {
                    var origin = new Vector3(baseX, from.Y + SampleHeights[i], baseZ);

                    // ⚠️⚠️ 只用 layerMask=1 —— 照抄游戏自己移动碰撞的 static 包装参数。
                    //
                    // v0.18.5 修「面前明明没有障碍却被卡在原地」：旧版这里在 mask=1
                    // 没中时还会用 mask=-1 补一枪，而 -1 会命中**游戏移动碰撞根本不理会**
                    // 的东西（隐形边界 / 几何背面 / 废弃碰撞体）—— 于是空地上也会判出
                    // "被挡"，她就在什么都没有的地方硬停。mask=1 = 玩家走不过去的地方
                    // 她才算被挡，与"不穿墙"红线完全一致（玩家能走她就能走）。
                    var hit = Cast(bg, origin, unit, ProbeDistance, 1, FlagsGame, out var h1)
                           && h1.Distance > 0.02f && h1.Distance < ContactDistance;

                    if (!hit)
                        continue;

                    // 命中的绝对高度（unit 水平，Y 不变）。
                    var hitY     = from.Y + SampleHeights[i];
                    // ⚠️ 与地面基准的差：贴地（斜坡 / 门槛）≈ 0；真正的立面障碍 >> 0。
                    // 旧逻辑没有这一步，斜坡也会被算成挡路 → 她硬停、再也跟不动。
                    var vertical = hitY - nearGround;

                    // 脚下那档有几何 = 立面从地面贯通（实墙）；脚下空 = 悬空矮障（桌底），可跳。
                    if (i == 0)
                        baseBlocked = true;

                    // 只有「明显高于地面」的命中才算真障碍；贴地的命中直接忽略 ——
                    // 那是她正踩着 / 正走上去的地面，不是路障。
                    if (vertical > JumpableMinHeight)
                        realBlocked++;
                }

                if (DebugEnabled)
                    WriteVolumeDiag(lane, LaneHitsBuffer(lane, baseX, baseZ, from, unit));

                if (realBlocked > worstReal)
                {
                    worstReal = realBlocked;
                    worstBase = baseBlocked;
                }
            }

            // 没有任何「明显高于地面」的命中 → 只是地面起伏（斜坡 / 门口小门槛），放行
            if (worstReal < 1)
                return BlockKind.Clear;

            // 脚下那档也真被挡（立面从地面贯通）= 实心墙；脚下有缝 = 悬空矮障，可跳
            return worstBase ? BlockKind.Wall : BlockKind.LowObstacle;
        }
        catch
        {
            // 射线失败绝不能让她卡住 —— 当没挡
            return BlockKind.Clear;
        }
    }

    /// <summary>正前方障碍的三态分类 —— 供跳跃判定用。</summary>
    public static BlockKind ProbeFront(Vector3 from, Vector3 dir)
    {
        var len = dir.Length();
        return len < 0.0001f ? BlockKind.Clear : Probe(from, dir / len);
    }

    /// <summary>
    /// 用<b>指定 layerMask</b> 打一条射线（v0.18.28，碰撞层探针用）。
    /// flags 沿用游戏移动碰撞同款 [0x4000,0,0x4000,0]。
    /// </summary>
    public static bool CastWithMask(Vector3 origin, Vector3 dir, float dist, int mask, out RaycastHit hit)
    {
        hit = default;
        try
        {
            var bg = Framework.Instance()->BGCollisionModule;
            if (bg is null)
                return false;
            return Cast(bg, origin, dir, dist, mask, FlagsGame, out hit);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 硬碰撞扫描（v0.18.18）：这一步真的能走多远？
    ///
    /// <para>决策层的 <see cref="Probe"/> 只决定"往哪走"，而她每帧 SetPosition 写位置、
    /// 游戏的碰撞响应帮不了她 —— 这里对<b>实际要走的位移</b>做一次近距离扫描
    /// （静态几何两条高度 + 家具圆），返回允许的最大位移；调用方据此钳制步长。
    /// 这是「让位置写入参与碰撞」的等效物：不管决策层怎么判，位移本身绝不穿模。</para>
    ///
    /// <para>返回 <see cref="float.MaxValue"/> = 这一步全畅通。
    /// 只扫小腿(0.35)/腰(0.70)两条 —— 走路时脚踝贴地档会被脚下的坡误伤；
    /// 0.35/0.70 在台阶(每级升 ~0.3m)与陡坡(步长内升幅 &lt; 0.35m)上都不会误报。</para>
    /// </summary>
    public static float SweepStep(Vector3 from, Vector3 unit, float step)
    {
        var reach = step + 0.2f;   // 少量前探余量，把"贴着障碍停"变成"离障碍 0.2m 停"
        var best  = float.MaxValue;

        try
        {
            var bg = Framework.Instance()->BGCollisionModule;
            if (bg is not null)
            {
                // 水平单位向量
                var len = MathF.Sqrt(unit.X * unit.X + unit.Z * unit.Z);
                if (len > 0.0001f)
                {
                    var ux = unit.X / len;
                    var uz = unit.Z / len;
                    var dir = new Vector3(ux, 0f, uz);

                    foreach (var h in SweepHeights)
                    {
                        var origin = new Vector3(from.X, from.Y + h, from.Z);
                        if (Cast(bg, origin, dir, reach, 1, FlagsGame, out var hit)
                            && hit.Distance > 0.02f && hit.Distance < best)
                            best = hit.Distance;
                    }
                }
            }
        }
        catch
        {
            // 射线失败不钳制（与 Probe 的"失败当畅通"同语义）
        }

        // 家具圆（BGCollision 看不见它们，见 NpcHousing）
        var housing = NpcHousing.SegmentHit(from, unit, reach, 0.25f);
        if (housing < best)
            best = housing;

        return best;
    }


    /// <summary>这个方向能不能走（只要不是通畅就挡）。</summary>
    public static bool IsBlocked(Vector3 from, Vector3 unit)
        => Probe(from, unit) != BlockKind.Clear;

    // ── 分层诊断（v0.18.0）──
    //
    // 之前几轮反复失败，根子在于我们只记了"最终挡 / 不挡"这一个布尔值，
    // 于是"没挡住"和"根本没检测"看起来一模一样 —— 无从判断是哪一层漏了。
    // 这一版把每一档的命中情况原样落盘，看一眼就知道：
    //   · 射线压根没打到东西 → 检测没覆盖到（高度/层不对）
    //   · 打到但只有最底下一档 → 被当成台阶放行了（阈值需要调）
    //   · 打到且挡住多档 → 判定有效（这时若还穿，问题在位移端不在检测端）
    //
    // 只有调试开关打开时才写，正常游玩零开销。
    public static bool DebugEnabled;

    /// <summary>上一帧各档的命中距离（-1 = 没命中），诊断用。</summary>
    public static string LastTrace => _lastTrace ?? string.Empty;
    private static string? _lastTrace;

    private static int _lastDiagTick;

    private static void WriteVolumeDiag(int lane, float[] hits)
    {
        var t = Environment.TickCount;
        if (t - _lastDiagTick < 500)
            return;
        _lastDiagTick = t;

        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncherCN", "pluginConfigs", "SmartNpc");
            System.IO.Directory.CreateDirectory(dir);

            _lastTrace = string.Join(" ", hits.Select(h => h < 0f ? "-" : h.ToString("F2")));

            System.IO.File.AppendAllText(
                System.IO.Path.Combine(dir, "avoid-trace.txt"),
                string.Format("{0:HH:mm:ss.fff} 体积探测 lane={1} 各档命中=[{2}]\n",
                    DateTime.Now, lane, _lastTrace),
                new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // 诊断绝不影响主流程
        }
    }

    /// <summary>
    /// 路径探测：从 <paramref name="from"/> 朝 <paramref name="unit"/> 走
    /// <paramref name="dist"/> 米，路上会不会撞到东西。
    ///
    /// <para>与 <see cref="Probe"/> 用同一束射线，返回<b>最近的那个命中距离</b>。
    /// 瞬移落点用它算"能走到哪儿"，于是落点必定停在墙的<b>这一侧</b>。</para>
    /// </summary>
    public static bool RaycastPath(Vector3 from, Vector3 unit, float dist, out float hitDist)
    {
        hitDist = float.MaxValue;

        try
        {
            var bg = Framework.Instance()->BGCollisionModule;
            if (bg is null)
                return false;

            var right = new Vector3(unit.Z, 0f, -unit.X);
            var rl    = right.Length();
            right = rl > 0.0001f ? right / rl : Vector3.UnitX;

            var best = float.MaxValue;

            // ── 住宅家具/庭具（v0.18.17）──
            // 瞬移落点校验同样要认家具：出生点/落点必须停在家具的这一侧，
            // 否则会出现"重生在桌子里面"（BGCollision 对家具完全不报）。
            var housingHit = NpcHousing.SegmentHit(from, unit, dist, HalfWidth);
            if (housingHit < best)
                best = housingHit;

            for (var lane = -1; lane <= 1; ++lane)
            {
                var lateral = right * (lane * HalfWidth);
                var baseX   = from.X + lateral.X;
                var baseZ   = from.Z + lateral.Z;

                foreach (var h in SampleHeights)
                {
                    var origin = new Vector3(baseX, from.Y + h, baseZ);

                    // ⚠️ 同 Probe：只用 layerMask=1，别让幻影碰撞把瞬移落点推得离墙老远。
                    if (Cast(bg, origin, unit, dist, 1, FlagsGame, out var h1)
                        && h1.Distance > 0.02f && h1.Distance < best)
                        best = h1.Distance;
                }
            }

            if (best < float.MaxValue)
            {
                hitDist = best;
                return true;
            }
        }
        catch
        {
            // 探测失败：宁可不瞬移，也别盲穿
        }

        return false;
    }

    /// <summary>把某一横向位置各档的命中距离抓出来（诊断用，不参与判定）。</summary>
    private static float[] LaneHitsBuffer(int lane, float baseX, float baseZ, Vector3 from, Vector3 unit)
    {
        var bg = Framework.Instance()->BGCollisionModule;
        var result = new float[SampleHeights.Length];

        if (bg is null)
        {
            for (var i = 0; i < result.Length; i++)
                result[i] = -1f;
            return result;
        }

        for (var i = 0; i < SampleHeights.Length; ++i)
        {
            var origin = new Vector3(baseX, from.Y + SampleHeights[i], baseZ);

            if (Cast(bg, origin, unit, ProbeDistance, 1, FlagsGame, out var h1) && h1.Distance > 0.02f)
                result[i] = h1.Distance;
            else if (Cast(bg, origin, unit, ProbeDistance, -1, FlagsGame, out var h2) && h2.Distance > 0.02f)
                result[i] = h2.Distance;
            else
                result[i] = -1f;
        }

        return result;
    }

    /// <summary>
    /// 落点站位空间校验（v0.18.4）：以该点为中心，向四个水平方向各探一组射线，
    /// 统计「实心墙」方向的个数。≥3 个方向都是墙 = 这个点被几何体包住，
    /// 她落在这儿必然卡模 → 换落点。
    ///
    /// <para>为什么不用路径探测代替：路径探测只回答"来的路上有没有墙"，
    /// 不回答"落点本身在不在墙里" —— 站位点在你身后 1.8m + 侧 0.8m，
    /// 你贴墙站时它正好嵌进墙体（用户实测"瞬移卡障碍物"）。</para>
    ///
    /// <para>玩家模型不算碰撞（BGCollision 只含场景几何），
    /// 所以"贴着玩家站"不会被误判成没有空间。</para>
    /// </summary>
    public static bool HasStandRoom(Vector3 point)
    {
        var walls = 0;

        if (Probe(point, Vector3.UnitX)  == BlockKind.Wall) walls++;
        if (Probe(point, -Vector3.UnitX) == BlockKind.Wall) walls++;
        if (Probe(point, Vector3.UnitZ)  == BlockKind.Wall) walls++;
        if (Probe(point, -Vector3.UnitZ) == BlockKind.Wall) walls++;

        return walls < 3;
    }

    private static bool Cast(BGCollisionModule* bg, Vector3 o, Vector3 d, float dist, int mask, int[] flags, out RaycastHit hit)
    {
        hit = default;
        RaycastHit h = default;

        bool ok;
        fixed (int* pF = flags)
            ok = bg->RaycastMaterialFilter(&h, &o, &d, dist, mask, pF);

        hit = h;
        return ok;
    }
}
