using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace SmartNpc.Core;

/// <summary>
/// 地面高度采样：问游戏"这个 XZ 上的地面到底多高"。
///
/// <para><b>为什么必须有它</b>：以前她贴的是<b>玩家当前的 Y</b>。
/// 你站在坡上、台阶上、石头上时，你的 Y 比她脚下的地面高一截，
/// 她就被整个抬到半空 —— "悬浮、踏空而行"就是这么来的。
/// 贴自己的地面才能各走各的。</para>
///
/// <para>用的是游戏自己的碰撞射线（<c>BGCollisionModule.RaycastMaterialFilter</c>），
/// 和游戏判定"脚踩在哪"走的是同一套数据，所以台阶、桥面、斜坡都对得上。
/// 第三方插件（例如 AQuestReborn）也是同样的思路：按 NPC 自己的 XZ 取地面高度。</para>
/// </summary>
internal static unsafe class NpcGround
{
    /// <summary>从参考高度往上抬多少开始往下打。够跨过一级台阶，又不至于打到头顶的天花板。</summary>
    private const float UpStart = 1.6f;

    /// <summary>最多往下找多远（米）。</summary>
    private const float DownRange = 20f;

    /// <summary>
    ///  sanity 阈值：命中的点与参考高度差这么多就认为"打歪了"（比如打到了下层地板），丢弃。
    /// </summary>
    private const float PlausibleRange = 4.5f;

    // ── v0.18.63 家具/室内地板碰撞层（NpcMaskProbe 实锤）──
    //
    // mask-probe.txt（2026-10-25）: 玩家站在住宅内地板/家具上时，bit1(mask=2) 与
    // bit3(mask=8) 稳定命中同一高度（室内地板 -71.22，比地形射线的 -87.9 高 16 米）
    // —— 住宅内地板 + 家具碰撞就住在这两层。此前 Sample 只信 mask=1，室内她
    // 会一路沉到室外地形里（TopSurfaceAt 包围盒只能救"有 DrawObject 的可站立家具"，
    // 救不了地板本身）。
    private const int FurnitureMask = 0b1010;   // bit1(mask=2) + bit3(mask=8)

    /// <summary>
    /// 家具层命中最多比参考高度高出多少才可信（米）。
    /// ⚠️ 防的就是"站在桥/楼梯下面，射线起点已经在桥面之上，第一命中是头顶的桥面"
    /// —— 她会被整个吸到桥上去。0.8 = 够迈一级台阶/上矮台，远低于任何能钻进去的桥洞。
    /// </summary>
    private const float FurnitureStepUp = 0.8f;

    private static float  _lastY      = float.NaN;
    private static int    _skip;
    private const  int    SampleEvery = 2;   // 每 2 次调用打一次射线，够用且省开销

    /// <summary>
    /// 取 (x, z) 处的地面高度。拿不到就返回 <paramref name="fallback"/>。
    /// </summary>
    /// <summary>
    /// 取 (x, z) 处的地面高度。拿不到就返回 <paramref name="fallback"/>。
    ///
    /// <para><b>v0.16.0 修正（"上楼梯就消失"的根因）</b>：
    /// 旧实现有一条<b>只能下、不能上的棘轮</b> ——
    /// 参考高度取的是缓存 `_lastY`，命中后还要求 `#124;y - reference#124; ≤ 4.5`，
    /// 否则<b>返回陈旧的缓存值</b>。于是：
    /// 你爬到比她缓存高的地方 → 她永远够不着（healthGap 超阈值 → 每帧被传送回那个陈旧低点，
    /// 直接被塞进楼梯几何体里 —— 看起来就是"人没了"）。
    /// </para>
    ///
    /// 现在：<b>参考点取缓存与目标的较高者</b>（射线抬到两者之上再往下打，
    /// 第一个命中就是她脚下的真实地面），并且<b>贴近目标高度也算合理</b>，
    /// 于是上楼梯能一级级爬、下台阶也能落回来。
    /// </summary>
    public static float Resolve(float x, float z, float fallback, bool anchorToTarget = false)
    {
        // ⚠️⚠️ v0.17.2：瞬移时**绝不能走节流分支**。
        //
        // 节流是为了平时省开销（每 2 帧打一次射线），返回上一帧的缓存值。
        // 但瞬移那一帧，缓存值是**另一个楼层**的高度 ——
        // 跳过射线直接返回它 = 把落点定在了旧楼层：
        //   低 → 高：落点在下层地面高度，人被塞进上层地板里（消失）；
        //   高 → 低：落点在高层地面高度，人在空中然后掉下来。
        // 由于 _skip 的奇偶性不定，"中招"的概率正好是一半 ——
        // 这就是用户实测「浮空 or 卡地里」而且**时好时坏**的真正原因。
        //
        // 上一轮只加了 anchorToTarget（把参考高度改成玩家的），
        // 却没堵住这个"跳过射线"的分支，所以完全没生效。
        if (anchorToTarget)
            return Sample(x, z, fallback, fallback);

        if (_skip++ % SampleEvery != 0)
            return float.IsNaN(_lastY) ? fallback : _lastY;

        // ⚠️ 抬到"缓存 与 目标"的较高者之上再往下打 —— 上楼梯的关键：
        // 只按缓存抬的话，射线起点还在下一层，永远打不到上面的台阶。
        var cached    = float.IsNaN(_lastY) ? fallback : _lastY;

        // ⚠️ v0.17.1（瞬移消失/空中落下的根因）：
        // `cached` 是**瞬移前旧位置**的高度，瞬移后完全不可信 ——
        //   低处 → 高处：从低处高度打射线会命中低层地面 → 她被埋进高层地板下（消失）；
        //   高处 → 低处：则会命中高台 → 落在空中再掉下来。
        // 所以瞬移采样（anchorToTarget=true）必须以**目标层（玩家高度）**为参考；
        // 正常走路仍锚在她自己的高度上（那样才能一级级爬台阶）。
        return Sample(x, z, cached, fallback);
    }

    /// <summary>
    /// 真的去打一条向下的射线取地面高度。
    /// </summary>
    /// <param name="reference">射线起点的参考高度（往上抬 <see cref="UpStart"/> 再往下打）。</param>
    /// <param name="fallback">抓不到地面时退回的高度。</param>
    private static float Sample(float x, float z, float reference, float fallback)
    {
        var origin = new Vector3(x, reference + UpStart, z);
        var down   = new Vector3(0f, -1f, 0f);

        try
        {
            // ── v0.18.63 两层各打一条向下的射线，取"起算点以下第一个命中"的较高者 ──
            // 她脚下的物理面 = 两层各自的 topmost 命中里更高的那个：
            //   · 室内：地形(mask1)打到室外 -87.9，家具层(mask2|8)打到室内地板 -71.22 → 取家具层；
            //   · 桥/家具顶：家具层打到顶面 → 取家具层（包围盒方案仍保留作无碰撞体家具的兜底）；
            //   · 野外：家具层大概率落空 → 取地形。
            var terrainOk = BGCollisionModule.RaycastMaterialFilter(origin, down, out var hit, DownRange);
            var terrainY  = terrainOk && float.IsFinite(hit.Point.Y) ? hit.Point.Y : float.NaN;

            var furnY = SampleFurnitureLayer(origin, down, reference);
            if (float.IsNaN(furnY))
                furnY = terrainY;                       // 家具层落空 → 纯地形

            // 家具层命中但地形也命中：只在"比地形高"（确实是台面）时接管；
            // 比地形还低 = 打到了地形下面的什么东西，不信。
            if (!float.IsNaN(terrainY) && !float.IsNaN(furnY) && furnY <= terrainY)
                furnY = terrainY;

            var y = furnY;

            // 起点就在参考高度上方 1.6 米、方向朝下 ——
            // **往下打的第一个命中就是她脚下的地面**，直接信它。
            // 之前那个"必须离锚点 4.5 米内"的判定反而害事：
            // 一旦被拒就走兜底，把她挪到不该在的高度（浮空 / 卡进地板）。
            if (float.IsFinite(y))
            {
                // ⚠️ v0.18.20 家具顶面接管（"站不上庭具桥/平台"的修复）：
                // 有 DrawObject 包围盒但没碰撞体的可站立家具（桥/平台），由
                // NpcHousing.TopSurfaceAt 把地面顶到盒顶 —— 与碰撞层方案互补。
                var top = NpcHousing.TopSurfaceAt(x, z, y);
                if (!float.IsNaN(top) && top > y)
                    y = top;

                // ⚠️⚠️ v0.18.28 用户裁决 + v0.18.63 扩充：地面证据只有三样，全部物理背书：
                //   1. 地形射线（mask=1，地形+静态建筑）；
                //   2. 家具/室内地板碰撞层射线（mask=2|8，NpcMaskProbe 实锤）；
                //   3. 真实包围盒顶面（NpcHousing.TopSurfaceAt，无碰撞体的可站立家具兜底）。
                // 绝不再引入任何"她的状态 → 她的地面"的回路。
                _lastY = y;
                return y;
            }
        }
        catch
        {
            // 射线失败不该让整个跟随逻辑挂掉。
        }

        // ⚠️ 完全拿不到合理高度时，<b>不要抱着旧值不放</b>：
        // 旧实现这里返回陈旧 _lastY，那正是"她永久卡在错误高度上"的成因。
        return float.IsNaN(_lastY) ? fallback : _lastY;
    }

    /// <summary>
    /// 家具/室内地板层（mask 2|8）的向下射线（v0.18.63）。
    ///
    /// <para>命中必须 ≤ reference + <see cref="FurnitureStepUp"/>：站桥/楼梯下时射线
    /// 起点可能已在头顶台面之上，不设上限她会被吸上去。命中比参考低太多（掉出世界
    /// 的读数）也不信 —— 但室内地板≈参考本身，0.8 米余量足够覆盖一切"能站的台面"。</para>
    /// </summary>
    private static float SampleFurnitureLayer(Vector3 origin, Vector3 down, float reference)
    {
        try
        {
            if (!NpcAvoidance.CastWithMask(origin, down, DownRange, FurnitureMask, out var hit))
                return float.NaN;

            var y = hit.Point.Y;
            if (!float.IsFinite(y))
                return float.NaN;

            // 头顶台面拒绝（见 XML 注释）；比参考低 30 米以上的发疯读数也拒。
            if (y > reference + FurnitureStepUp || y < reference - 30f)
                return float.NaN;

            return y;
        }
        catch
        {
            // 探测失败不影响地形射线兜底
            return float.NaN;
        }
    }

    /// <summary>换区 / 重新召唤时清掉缓存，别拿上一张地图的地面高度当参考。</summary>
    public static void Reset()
    {
        _lastY = float.NaN;
        _skip  = 0;
    }

    /// <summary>
    /// 只读探测 (x, z) 处的地面高度，<b>不读写 _lastY 缓存</b>（v0.18.10）。
    ///
    /// <para>给「玩家是否离地」这类<b>别人家</b>的探测用：玩家的地面高度一旦
    /// 混进缓存，她自己的贴地采样就会被带偏（坡道上瞬移到玩家高度）。
    /// 每次调用都真打一条射线，调用方自己控制频率。</para>
    /// </summary>
    public static float ProbeGround(float x, float z, float fallback)
    {
        var origin = new Vector3(x, fallback + UpStart, z);
        var down   = new Vector3(0f, -1f, 0f);

        try
        {
            if (BGCollisionModule.RaycastMaterialFilter(origin, down, out var hit, DownRange)
                && float.IsFinite(hit.Point.Y))
                return hit.Point.Y;
        }
        catch
        {
            // 射线失败不该让调用方挂掉
        }

        return fallback;
    }
}
