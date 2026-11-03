using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using NativeObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;

namespace SmartNpc.Core;

/// <summary>
/// 住宅家具/庭具障碍层（v0.18.17 引入，v0.18.18 加固 + 落盘诊断）。
///
/// <para><b>为什么 BGCollision 射线看不见家具：</b>
/// 她的避障（<see cref="NpcAvoidance"/>）依赖 <c>BGCollisionModule</c> 射线
/// （layerMask=1），那套射线只认<b>地形 + 静态建筑</b>碰撞体。住宅区摆放的
/// 家具/庭具（<c>ObjectKind.HousingEventObject</c>）的碰撞体不在其中 ——
/// 而她每帧 <c>SetPosition</c> 直接写位置，游戏自己的碰撞响应不参与，于是穿模。</para>
///
/// <para><b>v0.18.18 加固（第一版实测无效后的三个疑点全部堵上）：</b>
/// ① 逐对象 try/catch —— 旧版整表一个 try，对象表里一格坏的整个扫描就放弃；
/// ② HitboxRadius ≤ 0 的家具给兜底半径 0.7（旧版直接丢弃 = 全部漏掉）；
/// ③ 扫描结果落盘 <c>housing-trace.txt</c>（2s 节流）—— 各类型对象计数 + 家具圆样本，
///    "家具到底收没收到、半径多大、类型对不对" 一眼可查，不再盲猜。</para>
///
/// <para><b>v0.18.19（第二轮实测 circles=0，trace 只留最后一次扫描且没记全类型）：</b>
/// ① trace 改<b>追加式</b>（带行数上限裁剪），并记录 24m 内<b>全类型对象普查</b>
///    + 最近的非玩家对象样本 —— 家具在对象表里到底是什么类型，下一轮数据直接给答案；
/// ② 家具若被挂成 <c>EventObj</c>（市场板/召唤铃这类实体交互物的类型）也能兜住：
///    障碍来源扩成 HousingEventObject + EventObj。代价是城里个别无形体的
///    任务光点也会被绕开 —— 可接受的保守。</para>
///
/// <para><b>已知取舍：</b>家具形状按圆粗化；地毯/垫子也会被算成障碍。</para>
/// </summary>
internal static unsafe class NpcHousing
{
    private struct Obstacle
    {
        public Vector3 Pos;
        public float   Radius;
        public uint    SubKind;
        public string  Name;

        // ── v0.18.20 可站立顶面（修"她站不上庭具桥/家具平台"）──
        //
        // 用户点破的本质：家具不是"要绕开的障碍"，桥/平台是**可以站上去的地面**。
        // BGCollision 射线（layerMask=1）只认地形+静态建筑，家具顶面永远不在
        // 射线答案里 —— 她走到桥上就被 NpcGround 打到桥下的地形，直接"沉"下去。
        //
        // 解法：扫描时顺手算每件家具的世界包围盒（DrawObject.ComputeAxisAlignedBounds），
        // 盒顶 Max.Y 就是"能站的那个面"。顶面高度合理（离家具底 0.05~1.6 米）
        // 的标记为 Walkable —— 它们**从障碍表里除名**（能站的东西不是墙），
        // 改由 NpcGround 在水平落点命中包围盒时把地面高度顶到 TopY。
        public Vector3 Min;
        public Vector3 Max;
        public float   TopY;
        public bool    Walkable;
        public bool    HasBounds;
    }

    /// <summary>当前生效的家具圆。双缓冲：扫描成功才整体替换。</summary>
    // ⚠️ 不能 readonly：扫描用元组交换整体替换缓冲（readonly 字段禁止赋值）。
    private static List<Obstacle> Obstacles = new(64);
    private static List<Obstacle> Scanning = new(64);

    private static float _scanCooldown;
    private static float _traceCooldown;
    private static int   _lastCircleCount = -1;   // -1 = 尚未扫描过，首轮必写
    private static int   _traceLines;

    /// <summary>
    /// 扫描节流（秒）。
    /// ⚠️ v0.18.24：0.5 → 0.25 —— "走过"标也在扫描期打，扫得越勤她对
    /// 玩家路径的反应越快（用户点名"扫描也要灵敏"）。家具不会自己动，
    /// 变的是"玩家踩过哪些圆"，0.25s 跟手且开销可忽略（~300 条目查表）。
    /// </summary>
    private const float ScanInterval = 0.25f;

    /// <summary>诊断落盘节流（秒）。</summary>
    private const float TraceInterval = 2f;

    /// <summary>扫描半径（米）。瞬移兜底默认 14 + 出生偏移，24 覆盖她所有可能去的落点。</summary>
    private const float ScanRadius = 24f;

    /// <summary>HitboxRadius 无效（≤0）时的兜底半径（米）。</summary>
    private const float FallbackRadius = 0.7f;

    /// <summary>家具圆半径上限（米）。异常大的读数钳住，免得半个房间都是障碍。</summary>
    private const float MaxRadius = 5f;

    /// <summary>顶面比家具落点高不到这个数 = 就在地面上，不算"可站上台面"。</summary>
    private const float WalkableTopMin = 0.05f;

    /// <summary>
    /// 顶面比家具落点高出多少以内才算"可以站上去"（米）。
    /// 桥面/平台/桌面在这个范围；书柜/墙体/大树的上沿远超它 —— 那种东西
    /// 顶上能站也不该走上去穿模，继续当障碍。
    /// </summary>
    private const float WalkableTopMax = 1.6f;

    /// <summary>垂直容差（米）。楼上楼下的家具不算障碍。</summary>
    private const float VerticalTolerance = 4f;

    /// <summary>
    /// 单个扫描周期最多收录多少个家具圆。
    /// ⚠️ v0.18.24：96 → 192 —— v0.18.23 室内实测 circles=96/96 打满
    ///（HousingManager 接通后一个房间就有 200+ 件），打满 = 后面的家具漏收。
    /// </summary>
    private const int MaxObstacles = 192;

    /// <summary>换区 / 送走她时清空（旧地图的家具在新地图上不存在）。</summary>
    public static void Reset()
    {
        Obstacles.Clear();
        Scanning.Clear();
        _scanCooldown = 0f;
    }

    /// <summary>
    /// 每帧调用（内部 0.5s 节流）。center 用<b>玩家</b>位置 ——
    /// 出生点/瞬移落点都围绕玩家，扫描圈以玩家为心才能提前覆盖落点。
    /// </summary>
    public static void Tick(float deltaTime, IObjectTable? objectTable, Vector3 center, nint selfAddress)
    {
        _scanCooldown -= deltaTime;
        if (_scanCooldown <= 0f)
        {
            _scanCooldown = ScanInterval;
            Scan(objectTable, center, selfAddress);

            // ⚠️ v0.18.19：家具圆数量一变化就立刻落盘 —— 上一轮的 trace 是覆盖写，
            // 用户"测家具那一下"的数据被后续空地心跳冲掉了，等于没测。
            if (Obstacles.Count != _lastCircleCount)
            {
                _lastCircleCount = Obstacles.Count;
                WriteTrace(center);
            }
        }

        _traceCooldown -= deltaTime;
        if (_traceCooldown <= 0f)
        {
            _traceCooldown = TraceInterval;
            WriteTrace(center);
        }
    }

    private static void Scan(IObjectTable? objectTable, Vector3 center, nint selfAddress)
    {
        if (objectTable is null)
            return;

        Scanning.Clear();
        KindCounts.Clear();
        NearSamples.Clear();
        BoxSamples.Clear();
        SeenAddresses.Clear();
        HousingSeenCount = 0;

        var length = 0;
        try { length = objectTable.Length; } catch { return; }

        for (var i = 0; i < length; i++)
        {
            // ⚠️ v0.18.18：逐对象 try —— 对象表任何一格读取出错都不能拖垮整个扫描。
            // 旧版整表一个 try，一格坏 = 全盘放弃 = 缓存永远空 = 照样穿模。
            nint address;
            byte kind;
            Vector3 pos;
            float rawRadius;
            uint subKind;
            string name = "";
            try
            {
                var obj = objectTable[i];
                if (obj is null)
                    continue;

                address = obj.Address;
                if (address == nint.Zero || address == selfAddress)
                    continue;

                var go = (GameObject*)address;
                kind   = (byte)go->ObjectKind;
                pos    = new Vector3(go->Position.X, go->Position.Y, go->Position.Z);
                rawRadius = go->HitboxRadius;
                subKind   = go->SubKind;
                name      = ReadName(address);
            }
            catch
            {
                continue;
            }

            // ⚠️ v0.18.23：过滤与收录逻辑移入 AddObstacle（HousingManager 来源共用）。
            AddObstacle(address, kind, pos, rawRadius, subKind, name, center, fromObjectTable: true);
        }

        // ── v0.18.23：真正的家具/庭具在 HousingManager（对象表里根本没有）──
        //
        // v0.18.22 的名字落盘一锤定音：对象表 24m 内只有「传唤铃/简易以太之光/
        // 市场布告板」三个固定设施（EventObj），用户摆的庭具桥/平台一件都不在 ——
        // 住宅摆放物不注册进 Framework 对象表，由 HousingManager 自管：
        //   HousingManager.Instance()->GetFurnitureManager()->ObjectManager
        //     ->ObjectArray._objects[i]（室外 200 / 室内 600 槽）。
        // 每个条目 HousingObject 直接继承 GameObject（Position/HitboxRadius/
        // DrawObject 全在标准偏移，CS 源码 [Inherits<GameObject>] 确认）——
        // 当 GameObject* 用，现有半径/包围盒/可站立顶面逻辑全部直接生效，
        // 而且这批对象**真的有 DrawObject**，v0.18.20 的顶面方案这才真正跑得起来。
        try
        {
            var hm = HousingManager.Instance();
            if (hm is not null)
            {
                var fm = hm->GetFurnitureManager();
                if (fm is not null)
                {
                    var arr = &fm->ObjectManager.ObjectArray;
                    var capacity = Math.Min((int)arr->ObjectCount, 640);
                    var objs = arr->Objects;   // Pointer<GameObject>*（_objects 的公开访问器）
                    var housingSeen = 0;
                    for (var i = 0; i < capacity; i++)
                    {
                        GameObject* entry;
                        try { entry = objs[i].Value; }
                        catch { continue; }
                        if (entry is null)
                            continue;
                        housingSeen++;
                        if (!SeenAddresses.Add((nint)entry))
                            continue;

                        var hkind = (byte)entry->ObjectKind;
                        var hpos  = entry->Position;
                        if (!float.IsFinite(hpos.X) || !float.IsFinite(hpos.Y) || !float.IsFinite(hpos.Z))
                            continue;

                        // 住宅数组里只会有住宅对象 —— 不过 kind 过滤，直接收。
                        AddObstacle((nint)entry, hkind, hpos, entry->HitboxRadius,
                                    entry->SubKind, ReadName((nint)entry), center,
                                    fromObjectTable: false);
                    }
                    HousingSeenCount = housingSeen;
                }
                else
                {
                    HousingSeenCount = -1;   // manager 在但家具管理器没加载
                }
            }
            else
            {
                HousingSeenCount = -1;
            }
        }
        catch
        {
            // 不在住宅区 / 结构没加载：对象表路径照旧，绝不能拖垮扫描
            HousingSeenCount = -1;
        }

        // 双缓冲交换：只整体替换，绝不清空旧缓存再慢慢填（中间帧会全裸奔）。
        (Obstacles, Scanning) = (Scanning, Obstacles);

        // ⚠️ 计数变量不能叫 walkable —— 上面的 for 循环体（子作用域）里已经用过这个名字，
        // C# 会因"子作用域与外层重名"直接报 CS0136。
        var walkCount = 0;
        foreach (var o in Obstacles)
            if (o.Walkable)
                walkCount++;
        WalkableCount = walkCount;

        // ⚠️ v0.18.28：Walked 标（玩家实测缓存派生）随脚印路线一起退役 ——
        // 玩家走过的地方不再豁免障碍圆，障碍判定回归纯几何。
    }

    /// <summary>本轮扫描去重（对象表与 HousingManager 可能撞同一个地址）。</summary>
    private static readonly HashSet<nint> SeenAddresses = new(128);

    /// <summary>本轮从 HousingManager 数组里看到的条目数（-1 = 不可用）。诊断用。</summary>
    public static int HousingSeenCount { get; private set; }

    /// <summary>
    /// 把一个候选对象收进障碍表（对象表 / HousingManager 两个来源共用）。
    /// 返回 false = 被 kind/垂直/距离/容量过滤。
    /// </summary>
    private static bool AddObstacle(nint address, byte kind, Vector3 pos, float rawRadius,
                                    uint subKind, string name, Vector3 center, bool fromObjectTable)
    {
        // 对象表来源只认这两类（其余是玩家/NPC/光柱，不归家具层管）；
        // HousingManager 来源跳过过滤 —— 数组里只会有住宅对象。
        if (fromObjectTable
            && kind != (byte)NativeObjectKind.HousingEventObject
            && kind != (byte)NativeObjectKind.EventObj)
            return false;

        // 垂直方向差太远 = 楼上楼下，不算。
        if (MathF.Abs(pos.Y - center.Y) > VerticalTolerance)
            return false;

        var dx = pos.X - center.X;
        var dz = pos.Z - center.Z;
        var distSq = dx * dx + dz * dz;
        if (distSq > ScanRadius * ScanRadius)
            return false;

        // ── 普查记账：全类型计数 + 就近样本 ──
        CensusKind(kind);
        RecordNearSample(kind, subKind, rawRadius, pos, distSq, name);
        SeenAddresses.Add(address);

        // ⚠️ 半径 ≤0 不再丢弃 —— 部分家具的 HitboxRadius 可能没填，
        // 丢弃 = 该漏的还是漏。给兜底半径，宁可多绕半步也不穿模。
        var r = rawRadius;
        if (r <= 0f)
            r = FallbackRadius;
        r = Math.Clamp(r, 0.2f, MaxRadius);

        // ── 可站立顶面：读世界包围盒 ──
        // ComputeAxisAlignedBounds 返回世界空间 AABB（DrawObject 自带世界位移/旋转），
        // Max.Y 就是"能站的那个面"的高度。签名 (AxisAlignedBounds*) -> AxisAlignedBounds*：
        // 参数是输出缓冲区，必须给真实内存再取地址传入（v0.18.21 的教训）。
        var hasBounds = false;
        var bMin      = Vector3.Zero;
        var bMax      = Vector3.Zero;
        var walkable  = false;
        var hasDraw   = false;
        try
        {
            var drawObj = ((GameObject*)address)->DrawObject;
            if (drawObj != null)
            {
                hasDraw = true;
                FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds buf;
                var bp = drawObj->ComputeAxisAlignedBounds(&buf);
                if (bp != null)
                {
                    var bMinY = bp->Min.Y;
                    var bMaxY = bp->Max.Y;
                    if (float.IsFinite(bMinY) && float.IsFinite(bMaxY)
                        && bMaxY > bMinY
                        && MathF.Abs(bMaxY - pos.Y) < 30f)   // 明显发疯的读数直接不信
                    {
                        bMin      = bp->Min;
                        bMax      = bp->Max;
                        hasBounds = true;
                        var rise  = bMaxY - pos.Y;
                        walkable  = rise > WalkableTopMin && rise <= WalkableTopMax;
                    }
                }
            }
        }
        catch
        {
            // 包围盒读不到就退回纯障碍：绝不能影响扫描本身
        }

        if (Scanning.Count >= MaxObstacles)
            return false;

        Scanning.Add(new Obstacle
        {
            Pos = pos, Radius = r, SubKind = subKind, Name = name,
            Min = bMin, Max = bMax, TopY = hasBounds ? bMax.Y : pos.Y,
            Walkable = walkable, HasBounds = hasBounds,
        });
        RecordBoxSample(kind, pos, name, hasDraw, hasBounds, bMin.Y, bMax.Y);
        return true;
    }

    /// <summary>
    /// 水平线段 <paramref name="from"/> → <paramref name="from"/> + <paramref name="unit"/> × <paramref name="dist"/>
    /// 会不会撞进某个家具圆。返回<b>进入圆的进入距离</b>（<see cref="float.MaxValue"/> = 通畅）。
    ///
    /// <para><paramref name="selfRadius"/> 是她的身体半宽；起点已经落在某个圆内时该圆忽略
    /// —— 否则她站在家具旁时所有方向都被判挡，卡死在原地。</para>
    /// </summary>
    public static float SegmentHit(Vector3 from, Vector3 unit, float dist, float selfRadius)
    {
        var best = float.MaxValue;
        if (Obstacles.Count == 0 || dist <= 0f)
            return best;

        var len = MathF.Sqrt(unit.X * unit.X + unit.Z * unit.Z);
        if (len < 0.0001f)
            return best;

        var ux = unit.X / len;
        var uz = unit.Z / len;

        foreach (var o in Obstacles)
        {
            // ⚠️ v0.18.20：可站立的家具（桥/平台）**不是墙** —— 挡她只会让她永远
            // 上不了桥。可站立性交给 NpcGround 的顶面接管，避障只管真正过不去的。
            if (o.Walkable)
                continue;

            var ox = o.Pos.X - from.X;
            var oz = o.Pos.Z - from.Z;

            // 起点在圆内：忽略这个圆（她已经在里面/贴着，判挡只会卡死自己）。
            var r2 = o.Radius * o.Radius;
            if (ox * ox + oz * oz <= r2)
                continue;

            // 圆心在行进方向上的投影。
            var t  = ox * ux + oz * uz;
            var rr = o.Radius + selfRadius;
            if (t > dist + rr)
                continue;

            // 线段上离圆心最近的点。
            var tc = Math.Clamp(t, 0f, dist);
            var cx = ox - ux * tc;
            var cz = oz - uz * tc;
            var d2 = cx * cx + cz * cz;

            if (d2 <= rr * rr)
            {
                // 进入圆的距离：投影点往回收 √(rr² - d²)。
                var hit = t - MathF.Sqrt(rr * rr - d2);
                if (hit < 0f)
                    hit = 0f;

                if (hit < best)
                    best = hit;
            }
        }

        return best;
    }

    /// <summary>诊断用：当前缓存了多少个家具圆。</summary>
    public static int ObstacleCount => Obstacles.Count;

    /// <summary>诊断用：其中有多少个是"可站立"的（顶面接管生效的）。</summary>
    public static int WalkableCount { get; private set; }

    /// <summary>
    /// 读对象名（v0.18.22，诊断落盘用）。
    /// <c>GameObject._name</b> 在 +0x030，Fixed 64 字节 NUL 结尾 —— 偏移取自本机
    /// FFXIVClientStructs 元数据 dump，不走任何可能不存在的虚函数。
    /// </summary>
    private static unsafe string ReadName(nint address)
    {
        try
        {
            var p = (byte*)(address + 0x30);
            var len = 0;
            while (len < 64 && p[len] != 0)
                len++;
            return len == 0 ? "" : System.Text.Encoding.UTF8.GetString(p, len);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// (x, z) 水平落点若站在某件<b>可站立</b>家具的正上方，返回那件家具的顶面高度；
    /// 否则返回 NaN（v0.18.20）。
    ///
    /// <para><paramref name="refY"/> 是她此刻的参考高度（射线打到的地形）：
    /// 只接受与参考高度差 ≤3.5 米的顶面 —— 楼上/头顶的架子不该把楼下的人吸上去。</para>
    ///
    /// <para>命中判定用<b>世界包围盒</b>（比 HitboxRadius 的圆准得多：桥是长条，
    /// 圆会把桥两头外的水面也算成"桥上"）。外扩 0.15 米容差，免得贴边就掉下去。</para>
    /// </summary>
    public static float TopSurfaceAt(float x, float z, float refY)
    {
        var best = float.NaN;
        foreach (var o in Obstacles)
        {
            if (!o.Walkable || !o.HasBounds)
                continue;

            if (x < o.Min.X - 0.15f || x > o.Max.X + 0.15f
                || z < o.Min.Z - 0.15f || z > o.Max.Z + 0.15f)
                continue;

            if (MathF.Abs(o.TopY - refY) > 3.5f)
                continue;

            if (float.IsNaN(best) || o.TopY > best)
                best = o.TopY;
        }

        return best;
    }

    // ── 落盘诊断（v0.18.18 引入，v0.18.19 重写为追加式 + 全类型普查）──
    //
    // 第一轮的教训：trace 是覆盖写、只留最后一次扫描，用户"测家具那一下"的数据
    // 被后续空地心跳冲掉了；而且没记"周围还有什么类型的对象"，circles=0 分不清
    // 是"没家具"还是"家具不叫这个名字"。现在：
    //   · 追加写 + 行数上限裁剪 —— 历史不丢；
    //   · 24m 内全类型对象计数（kinds{}）—— 家具挂着什么类型一目了然；
    //   · 最近的非玩家对象样本（near[...]）—— 类型/SubKind/半径/位置齐全。

    private static readonly Dictionary<int, int> KindCounts = new();

    private struct NearSampleInfo
    {
        public byte    Kind;
        public uint    SubKind;
        public float   Radius;
        public Vector3 Pos;
        public float   DistSq;
        public string  Name;
    }

    private static readonly List<NearSampleInfo> NearSamples = new(16);

    /// <summary>包围盒原始读数样本（v0.18.21）：诊断专用，只在 trace 里用。</summary>
    private struct BoxSampleInfo
    {
        public byte    Kind;
        public float   PosY;
        public bool    HasDraw;
        public bool    HasBounds;
        public float   MinY;
        public float   MaxY;
        public string  Name;
    }

    private static readonly List<BoxSampleInfo> BoxSamples = new(16);

    private static void RecordBoxSample(byte kind, Vector3 pos, string name, bool hasDraw, bool hasBounds,
                                        float minY, float maxY)
    {
        BoxSamples.Add(new BoxSampleInfo
        {
            Kind = kind, PosY = pos.Y, HasDraw = hasDraw, HasBounds = hasBounds,
            MinY = minY, MaxY = maxY, Name = name,
        });

        if (BoxSamples.Count > 24)
            BoxSamples.RemoveRange(0, BoxSamples.Count - 24);
    }

    private static void CensusKind(byte kind)
    {
        KindCounts.TryGetValue(kind, out var n);
        KindCounts[kind] = n + 1;
    }

    private static void RecordNearSample(byte kind, uint subKind, float radius, Vector3 pos, float distSq,
                                         string name)
    {
        NearSamples.Add(new NearSampleInfo
        {
            Kind = kind, SubKind = subKind, Radius = radius, Pos = pos, DistSq = distSq, Name = name,
        });

        // 只留最近的 16 个候选，扫完再裁。
        if (NearSamples.Count > 24)
        {
            NearSamples.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
            NearSamples.RemoveRange(16, NearSamples.Count - 16);
        }
    }

    private static void WriteTrace(Vector3 center)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncherCN", "pluginConfigs", "SmartNpc");
            System.IO.Directory.CreateDirectory(dir);

            var sb = new System.Text.StringBuilder();
            sb.Append(string.Format("{0:HH:mm:ss} c=({1:F1},{2:F1},{3:F1}) circles={4}/{5} walk={6} housing={7}",
                DateTime.Now, center.X, center.Y, center.Z, Obstacles.Count, MaxObstacles,
                WalkableCount, HousingSeenCount));

            // 全类型普查：kind:数量, ...
            var kinds = new List<int>(KindCounts.Keys);
            kinds.Sort();
            sb.Append(" kinds{");
            var first = true;
            foreach (var k in kinds)
            {
                if (!first) sb.Append(',');
                sb.Append(k).Append(':').Append(KindCounts[k]);
                first = false;
            }
            sb.Append('}');

            // 就近样本（最多 8 个，已按距离排序）。
            if (NearSamples.Count > 0)
            {
                NearSamples.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
                var n = Math.Min(NearSamples.Count, 8);
                sb.Append(" near[");
                for (var i = 0; i < n; i++)
                {
                    var s = NearSamples[i];
                    if (i > 0) sb.Append(" | ");
                    sb.Append($"k{s.Kind} r{(s.Radius > 0f ? s.Radius.ToString("F2") : "无")} "
                              + $"({s.Pos.X:F1},{s.Pos.Y:F1},{s.Pos.Z:F1})");
                    if (!string.IsNullOrEmpty(s.Name))
                        sb.Append("「").Append(s.Name.Length > 14 ? s.Name[..14] : s.Name).Append('」');
                }
                sb.Append(']');
            }

            // 可站立顶面清单（最多 6 个）：验证"桥/平台的顶面到底算到了多高"。
            var tops = new List<Obstacle>(8);
            foreach (var o in Obstacles)
                if (o.Walkable && o.HasBounds)
                    tops.Add(o);
            if (tops.Count > 0)
            {
                sb.Append(" tops[");
                for (var i = 0; i < Math.Min(tops.Count, 6); i++)
                {
                    var o = tops[i];
                    if (i > 0) sb.Append(" | ");
                    sb.Append($"top{o.TopY:F2} box({o.Min.X:F1},{o.Min.Z:F1})~({o.Max.X:F1},{o.Max.Z:F1})");
                }
                sb.Append(']');
            }

            // 包围盒原始读数（最多 10 件，带名字）：分得清"没绘制对象 / 返回空 / 高度门槛不对"。
            if (BoxSamples.Count > 0)
            {
                sb.Append(" boxes[");
                for (var i = 0; i < Math.Min(BoxSamples.Count, 10); i++)
                {
                    var s = BoxSamples[i];
                    if (i > 0) sb.Append(" | ");
                    sb.Append($"k{s.Kind} y{s.PosY:F1} draw{(s.HasDraw ? "Y" : "N")} "
                              + $"bb{(s.HasBounds ? $"{s.MinY:F2}~{s.MaxY:F2}" : "无")}");
                    if (!string.IsNullOrEmpty(s.Name))
                        sb.Append("「").Append(s.Name.Length > 12 ? s.Name[..12] : s.Name).Append('」');
                }
                sb.Append(']');
            }

            sb.Append('\n');
            var line = sb.ToString();

            var path = System.IO.Path.Combine(dir, "housing-trace.txt");

            // 追加写；超过 200 行裁到最近 60 行（防无限膨胀）。
            if (_traceLines >= 200)
            {
                var old = System.IO.File.ReadAllLines(path);
                var keep = Math.Min(old.Length, 60);
                var tail = new string[keep + 1];
                tail[0] = "……（前面已裁剪）……";
                Array.Copy(old, old.Length - keep, tail, 1, keep);
                System.IO.File.WriteAllLines(path, tail, new System.Text.UTF8Encoding(false));
                _traceLines = tail.Length;
            }

            System.IO.File.AppendAllText(path, line, new System.Text.UTF8Encoding(false));
            _traceLines++;
        }
        catch
        {
            // 诊断绝不影响主流程
        }
    }
}
