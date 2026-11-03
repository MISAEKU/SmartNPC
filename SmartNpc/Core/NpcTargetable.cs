using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace SmartNpc.Core;

/// <summary>
/// 「可被玩家选中」这件事的**唯一**出口（v0.18.37）。
///
/// <para><b>为什么要单独一个文件</b>：v0.18.35 只在 <c>NpcController</c> 里每帧
/// <c>TargetableStatus |= IsTargetable</c>，用户实测「刚召唤出来选不中，换区之后才行」——
/// 说明我们只写对了<b>其中一个</b>字段，而游戏判定"能不能选"其实看一整套状态。</para>
///
/// <para><b>本机 <c>FFXIVClientStructs.dll</c> 解出来的实锤</b>（全部是真实签名，不是猜的）：</para>
/// <list type="bullet">
///   <item><c>ObjectTargetableFlags</c>：<c>Unk0=0x01</c>、<c>IsTargetable=0x02</c>、
///         <c>Unk1=0x04</c>、<c><b>ReadyToDraw=0x40</b></c>（注意不是 0x08）；</item>
///   <item>除了 <c>TargetableStatus</c>，GameObject 上还有<b>三个</b>目标状态字节：
///         <c>CurrentTargetStatus</c>、<c>NextTargetStatus</c>、<c>TargetStatus</c>（都是 byte），
///         外加 <c>CurrentDistance</c> / <c>NextDistance</c> / <c>Visibility</c>；</item>
///   <item>游戏自己的入口：<c>void SetTargetStatus(byte)</c>、<c>void SetDistance(byte)</c>、
///         <c>void SetVisibility(byte)</c>、<c>void UpdateNextDistanceAndTargetStatus()</c>、
///         <c>bool GetIsTargetable()</c>、<c>void SetReadyToDraw()</c>。</item>
/// </list>
///
/// <para><b>本版不靠猜</b>：把"她 vs 玩家"的这一整套字节逐项落盘
/// <c>target-trace.txt</c>。玩家是<b>必然可选中</b>的对照组 —— 换区前后各留一段，
/// 哪一位不一样就是哪一位在作祟。</para>
///
/// <para><b>第一轮数据（用户 2026-10-11 反馈的 trace）已经排除掉一半嫌疑</b>：</para>
/// <list type="bullet">
///   <item>召唤那一瞬她身上是 <c>0x41</c>（Unk0|ReadyToDraw，<b>没有</b> IsTargetable），
///         我们写完后 <c>0x43</c>，点亮后游戏自己补成 <c>0xFB</c> —— <b>和玩家一字不差</b>；</item>
///   <item><c>写前 == 写后 == 0xFB</c> —— 游戏<b>没有</b>把我们的位抢回去；</item>
///   <item><c>GetIsTargetable()=Y</c>、<c>IsReadyToDraw()=Y</c>、<c>DrawObject</c> 有。</item>
/// </list>
/// <para>⇒ **"能不能选"这一位根本不是凶手**。剩下的嫌疑在"游戏有没有把她放进
/// 可点选清单"：v0.18.38 的采样加上了
/// ① 她<b>真实</b>所在的对象表槽位（扫表比对地址，不信任那个可能已偏移漂移的
/// <c>GameObject.ObjectIndex</c> 字段 —— 它读出 216 而我们建在 16，二者必有一错）；
/// ② 她在不在 <c>TargetSystem.TargetableObjectsOnScreen</c> 里；
/// ③ <c>MouseOverTarget</c> / <c>Target</c> 是不是她（悬停与点击的两条路分开看）；
/// ④ 当前的 <c>TargetModeIndex</c>（目标过滤模式）。</para>
/// </summary>
internal static unsafe class NpcTargetable
{
    /// <summary>采样间隔（秒）。</summary>
    private const float TraceInterval = 0.5f;

    private const int MaxLines = 400;

    private static int _lines;

    /// <summary>上一次操作失败的原因（诊断用）。</summary>
    public static string LastError { get; private set; } = string.Empty;

    /// <summary>她当前的 TargetableStatus 字节（面板显示用）。</summary>
    public static byte LastStatus { get; private set; }

    /// <summary>她当前 GetIsTargetable() 的结果（面板显示用）。</summary>
    public static bool LastIsTargetable { get; private set; }

    /// <summary>
    /// 这一帧**写之前**她身上的字节（v0.18.37）。
    /// 它等于"上一帧写完到这一帧之间，游戏有没有把我们写的值改回去"——
    /// 写前 ≠ 写后 就是游戏在抢这个字节。
    /// </summary>
    public static byte LastBefore { get; private set; }

    private static DateTime _lastTrace = DateTime.MinValue;

    /// <summary>
    /// 每帧保持她"可被选中"。
    /// </summary>
    /// <param name="npc">她的 GameObject*。</param>
    /// <param name="player">本地玩家的 GameObject*（对照组；可为 null）。</param>
    /// <param name="want">true = 可被选中。</param>
    /// <param name="forceTrace">true = 无视节流立刻写一行采样（召唤/点亮等关键时用）。</param>
    /// <param name="ourIndex">我们建她时用的对象表索引（-1 = 不记录）。</param>
    public static void Apply(GameObject* npc, GameObject* player, bool want, bool forceTrace = false, int ourIndex = -1)
    {
        if (npc == null)
            return;

        try
        {
            var before = (byte)npc->TargetableStatus;
            LastBefore = before;

            byte desired;
            if (want)
            {
                // IsTargetable 是核心位。
                desired = (byte)(before | 0x02);

                // ⚠️ v0.18.37：以玩家为基准把"游戏认为一个可被选中的角色还带着哪些位"
                // 补齐（尤其是 ReadyToDraw=0x40）。玩家必然可选中，所以照抄他的位最稳。
                if (player != null)
                    desired |= (byte)((byte)player->TargetableStatus & 0x41);   // Unk0 | ReadyToDraw
            }
            else
            {
                desired = (byte)(before & ~0x02);
            }

            if (desired != before)
                npc->TargetableStatus = (ObjectTargetableFlags)desired;

            LastStatus       = (byte)npc->TargetableStatus;
            LastIsTargetable = npc->GetIsTargetable();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }

        var now = DateTime.UtcNow;
        if (forceTrace || (now - _lastTrace).TotalSeconds >= TraceInterval)
        {
            _lastTrace = now;
            WriteSample(npc, player, forceTrace ? "事件" : "周期", ourIndex);

            // v0.18.39：同步留一份字节级取证（内部自己节流）。
            MaybeDump(npc, player, forceTrace);
        }
    }

    private static void WriteSample(GameObject* npc, GameObject* player, string phase, int ourIndex)
    {
        if (npc == null)
            return;

        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("HH:mm:ss.f")).Append("  [").Append(phase).Append("] 她 ");
            Append(sb, npc);
            sb.Append(" 写前=0x").Append(LastBefore.ToString("X2"));

            // ── 她真实所在的对象表槽位（扫表比对地址）──
            // ⚠️ 不信任 GameObject.ObjectIndex 字段：它读出 216，而我们明明建在 16。
            // 二者必有一错，扫表是唯一中立的裁判。
            sb.Append(" 建在=").Append(ourIndex < 0 ? "?" : ourIndex.ToString())
              .Append(" 表内=").Append(ResolveTableIndex(npc).ToString())
              .Append(" GID=").Append(npc->GetGameObjectId().ObjectId.ToString());

            // ── 游戏有没有把她放进"可点选清单" ──
            sb.Append(" ｜ ").Append(DescribeTargetSystem(npc));

            sb.Append("  ｜ 玩家 ");
            if (player == null)
                sb.Append("（无）");
            else
                Append(sb, player);

            WriteLine(sb.ToString());
        }
        catch
        {
            // 诊断绝不影响主流程
        }
    }

    /// <summary>
    /// 扫一遍对象表，找出她的真实槽位。
    /// <c>GameObject.ObjectIndex</c> 字段可能随版本漂移，扫表比对地址是最稳的裁判。
    /// </summary>
    private static int ResolveTableIndex(GameObject* npc)
    {
        try
        {
            var com = ClientObjectManager.Instance();
            if (com == null)
                return -1;

            for (ushort i = 1; i < 600; i++)
            {
                var o = com->GetObjectByIndex(i);
                if ((nint)o == (nint)npc)
                    return i;
            }
        }
        catch
        {
            // 诊断绝不影响主流程
        }

        return -1;
    }

    /// <summary>她在某个 GameObjectArray 里的第几项（-1 = 不在，&lt;-1 = 扫描失败）。</summary>
    private static string InArray(GameObjectArray arr, nint self)
    {
        if (self == nint.Zero)
            return "—";

        try
        {
            for (var i = 0; i < arr.Length; i++)
            {
                if ((nint)arr[i] == self)
                    return i + "/" + arr.Length;
            }

            return "不在/" + arr.Length;
        }
        catch (Exception ex)
        {
            return "ERR(" + ex.Message + ")";
        }
    }

    /// <summary>
    /// 目标系统这一侧的现场：她在不在"屏上可点选"清单里、鼠标现在指着谁、
    /// 当前目标是谁、目标过滤模式是几号。
    /// </summary>
    private static string DescribeTargetSystem(GameObject* npc)
    {
        var self = (nint)npc;

        try
        {
            var ts = TargetSystem.Instance();
            if (ts == null)
                return "目标系统=不可用";

            var sb = new StringBuilder();

            // ① 她在不在 TargetableObjectsOnScreen（屏上可点选清单）
            var onScreen = false;
            var onScreenCount = -1;
            try
            {
                var arr = ts->TargetableObjectsOnScreen;
                onScreenCount = arr.Length;
                for (var i = 0; i < arr.Length; i++)
                {
                    if ((nint)arr[i] == self)
                    {
                        onScreen = true;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                sb.Append("清单扫描失败(").Append(ex.Message).Append(')');
            }

            sb.Append("清单数=").Append(onScreenCount)
              .Append(" 她在清单=").Append(onScreen ? "Y" : "N");

            // ①b 三个"过滤后"的清单（当前目标模式实际用的是这些，不是上面那个全量清单）
            sb.Append(" 过滤1=").Append(InArray(ts->ObjectFilterArray1, self))
              .Append(" 过滤2=").Append(InArray(ts->ObjectFilterArray2, self))
              .Append(" 过滤3=").Append(InArray(ts->ObjectFilterArray3, self));

            // ①c 点击那条路真正的两个闸门：在不在屏上 / 在不在视野距离内
            sb.Append(" 在屏=");
            try { sb.Append(ts->IsObjectOnScreen(npc) ? "Y" : "N"); }
            catch (Exception ex) { sb.Append("ERR(").Append(ex.Message).Append(')'); }

            sb.Append(" 在视野=");
            try { sb.Append(ts->IsObjectInViewRange(npc) ? "Y" : "N"); }
            catch (Exception ex) { sb.Append("ERR(").Append(ex.Message).Append(')'); }

            // ② 鼠标指着谁 / 当前目标是谁（悬停与点击是两条路，分开看）
            sb.Append(" 悬停=").Append((nint)ts->MouseOverTarget == self ? "她" : "别的")
              .Append(" 目标=").Append((nint)ts->Target == self ? "她" : "别的")
              .Append(" 模式=").Append(ts->TargetModeIndex.ToString())
              .Append(" 目标类型=").Append(npc->GetTargetType());

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return "目标系统异常:" + ex.Message;
        }
    }

    private static void Append(StringBuilder sb, GameObject* go)
    {
        var ts  = (byte)go->TargetableStatus;
        var tgt = go->GetIsTargetable();
        var rdy = go->IsReadyToDraw();

        sb.Append("idx=").Append(go->ObjectIndex)
          .Append(" kind=").Append(go->ObjectKind)
          .Append(" ent=0x").Append(go->EntityId.ToString("X"))
          .Append(" TS=0x").Append(ts.ToString("X2"))
          // ⚠️ GameObject.TargetStatus 已被 CS 标为过时：它就是 NextTargetStatus 的旧名字。
          // 所以这里只有两个真实的目标状态字节：Current 与 Next。
          .Append(" 状态=").Append(go->CurrentTargetStatus)
          .Append('/').Append(go->NextTargetStatus)
          .Append(" 距离=").Append(go->CurrentDistance)
          .Append('/').Append(go->NextDistance)
          .Append(" Vis=").Append(go->Visibility)
          .Append(" 可选中=").Append(tgt ? "Y" : "N")
          .Append(" 就绪=").Append(rdy ? "Y" : "N")
          .Append(" 绘制=").Append(go->DrawObject != null ? "有" : "空");
    }

    // ══════════════════════════════════════════════════════════════════
    //  自动取消选中 · 根因取证（v0.18.41 调查用，纯日志、不改任何目标）
    //
    //  用户否决了「每帧抢回目标」的锁方案（那是在跟症状打架）。正确方向是找出
    //  「游戏凭什么清掉指向她的硬目标」——在 Target 从「她」变成「别的/空」的那一瞬，
    //  把她和目标的全部校验相关状态拍下来（含前若干帧的环形快照），看是哪个字段先变。
    //  本段只读、只写日志，绝不改任何目标状态。
    // ══════════════════════════════════════════════════════════════════

    private static bool     _lastTargetWasHer;
    private static int      _ringIdx;
    private static string[] _ring = new string[20];

    /// <summary>
    /// 每帧调用：环形记录她的校验相关状态；一旦 <c>Target</c> 从「她」变成「别的/空」，
    /// 立刻把前若干帧快照 + 当前全量状态落盘，定位游戏清目标的触发字段。
    /// 纯取证，不改动任何目标。
    /// </summary>
    public static void TraceDeselect(GameObject* npc)
    {
        if (npc == null) { _lastTargetWasHer = false; return; }

        TargetSystem* ts;
        try { ts = TargetSystem.Instance(); }
        catch { return; }
        if (ts == null) return;

        var self  = (nint)npc;
        var target = (nint)ts->Target;
        var nowHer = target == self;

        string snap;
        try
        {
            var onList = false;
            try
            {
                var arr = ts->TargetableObjectsOnScreen;
                for (var i = 0; i < arr.Length; i++) { if ((nint)arr[i] == self) { onList = true; break; } }
            }
            catch { onList = false; }

            var onScreen = false; var inView = false;
            try { onScreen = ts->IsObjectOnScreen(npc); } catch { }
            try { inView   = ts->IsObjectInViewRange(npc); } catch { }

            snap = "她:列表=" + (onList ? "Y" : "N")
                 + " 在屏=" + (onScreen ? "Y" : "N")
                 + " 视野=" + (inView ? "Y" : "N")
                 + " TS=0x" + ((byte)npc->TargetableStatus).ToString("X2")
                 + " 可选中=" + (npc->GetIsTargetable() ? "Y" : "N")
                 + " 类型=" + npc->GetTargetType()
                 + " 绘制=" + (npc->DrawObject != null ? "有" : "空")
                 + " kind=" + npc->ObjectKind
                 + " 目标=" + (nowHer ? "她" : (target == nint.Zero ? "空" : "别的"));
        }
        catch (Exception ex) { snap = "快照失败:" + ex.Message; }

        _ring[_ringIdx] = DateTime.Now.ToString("HH:mm:ss.f") + " " + snap;
        _ringIdx = (_ringIdx + 1) % _ring.Length;

        if (_lastTargetWasHer && !nowHer)
        {
            var sb = new StringBuilder();
            sb.Append("\n=== 自动取消选中事件 @ ").Append(DateTime.Now.ToString("HH:mm:ss.f")).Append(" ===\n");
            sb.Append("—— 前若干帧（旧→新）——\n");
            for (var i = 0; i < _ring.Length; i++)
            {
                var idx = (_ringIdx + i) % _ring.Length;   // 从最旧到最新
                if (_ring[idx] != null) sb.Append("  ").Append(_ring[idx]).Append('\n');
            }
            sb.Append("—— 当前全量 ——\n  ").Append(DescribeTargetSystem(npc)).Append('\n');
            WriteLine(sb.ToString());
        }

        _lastTargetWasHer = nowHer;
    }

    // ══════════════════════════════════════════════════════════════════
    //  点击兜底（v0.18.39）
    //
    //  第二轮 trace 露出的矛盾：她**在**屏上可点选清单里（她在清单=Y），
    //  GetIsTargetable()=Y，鼠标悬停甚至能认出她（悬停=她），
    //  但「点下去」这条路的产物（TargetSystem.Target）长时间不是她。
    //
    //  也就是说：**"能不能选"的字节没问题，是"点击 → 硬目标"这段接线没接上**。
    //  与其继续在外面猜闸门，不如在她被证实"鼠标确实指在她身上"的那一刻，
    //  替游戏把硬目标设上 —— 触发条件极窄（必须悬停=她 且 当前目标≠她），
    //  不会误伤正常操作。
    // ══════════════════════════════════════════════════════════════════

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VkLButton = 0x01;

    private static bool _mouseDown;

    /// <summary>兜底一共成功接管了几次（面板显示用）。</summary>
    public static int FallbackHits { get; private set; }

    /// <summary>最近一次点击发生了什么（面板显示用）。</summary>
    public static string FallbackLast { get; private set; } = "（尚未触发）";

    /// <summary>是否启用点击兜底。</summary>
    public static bool FallbackEnabled { get; set; } = true;

    /// <summary>
    /// 每帧调用：捕捉玩家左键的**按下沿**，必要时替游戏把硬目标设成她。
    /// v0.18.69.23 四轮：支持多 NPC（主 NPC + 副 NPC 一起参与）—— 左键沿只读一次，
    /// 悬停 / 名牌命中谁就接管谁（单 NPC 场景行为与原版完全一致）。
    /// </summary>
    /// <param name="npcs">候选 NPC 的 GameObject* 列表（可为栈上数组）。</param>
    /// <param name="count">候选数量（≤0 = 只维持鼠标沿状态）。</param>
    public static void TickClick(GameObject** npcs, int count)
    {
        if (npcs is null || count <= 0)
        {
            _mouseDown = false;
            return;
        }

        bool down;
        try
        {
            down = (GetAsyncKeyState(VkLButton) & 0x8000) != 0;
        }
        catch (Exception ex)
        {
            FallbackLast = "鼠标状态读取失败:" + ex.Message;
            return;
        }

        var rising = down && !_mouseDown;
        _mouseDown = down;

        if (!rising || !FallbackEnabled)
            return;

        try
        {
            var ts = TargetSystem.Instance();
            if (ts == null)
                return;

            // 找"被悬停（悬停目标 / 名牌任一命中）且还不是硬目标"的那一个。
            GameObject* hit = null;
            for (var i = 0; i < count; ++i)
            {
                var npc = npcs[i];
                if (npc is null)
                    continue;

                var self    = (nint)npc;
                var hovered = (nint)ts->MouseOverTarget == self
                           || (nint)ts->MouseOverNameplateTarget == self;

                if (hovered && (nint)ts->Target != self)
                {
                    hit = npc;
                    break;
                }
            }

            if (hit is null)
            {
                FallbackLast = "悬停不在候选里 / 已是目标 → 未接管";
                return;
            }

            var selfHit = (nint)hit;
            var sb = new StringBuilder();

            // ① 游戏自己的入口：SetHardTarget(GameObject*, bool, bool, int)
            var ok = false;
            try
            {
                ok = ts->SetHardTarget(hit, true, true, 0);
            }
            catch (Exception ex)
            {
                sb.Append(" SetHardTarget抛错:").Append(ex.Message);
            }

            var after1 = (nint)ts->Target == selfHit;
            sb.Append(" SetHardTarget=").Append(ok ? "真" : "假")
              .Append(" 点后=").Append(after1 ? "她" : "别的");

            // ② 入口返回假 / 没生效时的二段兜底：直接落字段
            //    （Target 指针 + TargetObjectId，UI 读的就是这两个）
            if (!after1)
            {
                try
                {
                    ts->Target = hit;
                    ts->TargetObjectId = hit->GetGameObjectId();
                    sb.Append(" 直写字段→").Append((nint)ts->Target == selfHit ? "她" : "别的");
                }
                catch (Exception ex)
                {
                    sb.Append(" 直写抛错:").Append(ex.Message);
                }
            }

            if ((nint)ts->Target == selfHit)
                FallbackHits++;

            FallbackLast = sb.ToString();
            WriteLine(DateTime.Now.ToString("HH:mm:ss.f") + "  [点击兜底] " + FallbackLast);
        }
        catch (Exception ex)
        {
            FallbackLast = "异常:" + ex.Message;
        }
    }

    /// <summary>
    /// 无条件把硬目标设成她（面板「立刻选中她」按钮）。
    /// 它同时也是**最直接的探针**：如果连这个都设不上，说明问题在更底层，
    /// 而不是"点击那条路的闸门"。返回一行人话结果供面板显示。
    /// </summary>
    public static string SelectNow(GameObject* npc)
    {
        if (npc == null)
            return "她不在场";

        try
        {
            var ts = TargetSystem.Instance();
            if (ts == null)
                return "目标系统不可用";

            var self = (nint)npc;
            var sb   = new StringBuilder();

            var ok = false;
            try
            {
                ok = ts->SetHardTarget(npc, true, true, 0);
            }
            catch (Exception ex)
            {
                sb.Append("SetHardTarget抛错:").Append(ex.Message).Append(' ');
            }

            sb.Append("SetHardTarget=").Append(ok ? "真" : "假")
              .Append(" 之后=").Append((nint)ts->Target == self ? "她" : "别的");

            if ((nint)ts->Target != self)
            {
                try
                {
                    ts->Target         = npc;
                    ts->TargetObjectId = npc->GetGameObjectId();
                    sb.Append(" 直写字段→").Append((nint)ts->Target == self ? "她" : "别的");
                }
                catch (Exception ex)
                {
                    sb.Append(" 直写抛错:").Append(ex.Message);
                }
            }

            var result = sb.ToString();
            WriteLine(DateTime.Now.ToString("HH:mm:ss.f") + "  [手动选中] " + result);
            return result;
        }
        catch (Exception ex)
        {
            return "异常:" + ex.Message;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  字节级差异转储（v0.18.39）
    //
    //  前面逐字段猜了三轮（TargetableStatus / 目标状态字节 / 清单 / 悬停），
    //  都没抓到真凶。这里一次性把「她 vs 玩家」GameObject 头 512 字节
    //  整段倒出来（十六进制 + 逐字节差异），配合本机解出来的字段偏移表
    //  直接比对 —— 哪一位不一样是**数出来的**，不是猜出来的。
    // ══════════════════════════════════════════════════════════════════

    private const int DumpBytes = 0x200;

    private static DateTime _lastDump = DateTime.MinValue;
    private static nint     _dumpAddr;
    private static int      _dumps;

    /// <summary>
    /// 必要时写一份 <c>go-dump.txt</c>。召唤/点亮强制写，之后每 2 秒最多一份、上限 40 份。
    /// </summary>
    public static void MaybeDump(GameObject* npc, GameObject* player, bool force)
    {
        if (npc == null)
            return;

        var addr = (nint)npc;
        if (addr != _dumpAddr)
        {
            // 换人了：旧取证作废，从头再来一份干净的。
            _dumpAddr = addr;
            _dumps    = 0;
            _lastDump = DateTime.MinValue;
            ResetDumpFile();
        }

        var now = DateTime.UtcNow;
        if (!force && (_dumps >= 40 || (now - _lastDump).TotalSeconds < 2.0))
            return;

        _lastDump = now;
        _dumps++;

        try
        {
            WriteDump(npc, player);
        }
        catch
        {
            // 诊断绝不影响主流程
        }
    }

    private static void WriteDump(GameObject* npc, GameObject* player)
    {
        var sb = new StringBuilder();
        sb.Append("=== ").Append(DateTime.Now.ToString("HH:mm:ss.f"))
          .Append("  她=0x").Append(((nint)npc).ToString("X"))
          .Append("  玩家=0x").Append((player == null ? 0L : (long)player).ToString("X")).Append(" ===");

        WriteDumpLine(sb.ToString());
        WriteDumpLine("偏移    她                               玩家");

        for (var off = 0; off < DumpBytes; off += 16)
        {
            var hers = new StringBuilder();
            var his  = new StringBuilder();

            for (var i = 0; i < 16; i++)
                hers.Append(ByteAt(npc, off + i)).Append(' ');

            for (var i = 0; i < 16; i++)
                his.Append(player == null ? "-- " : ByteAt(player, off + i)).Append(' ');

            WriteDumpLine("0x" + off.ToString("X3") + "  " + hers.ToString().TrimEnd() + "  |  " + his.ToString().TrimEnd());
        }

        WriteDumpLine("--- 逐字节差异（她 ≠ 玩家）---");
        var diffs = 0;
        for (var off = 0; off < DumpBytes; off++)
        {
            if (player == null)
                break;

            var a = ReadByte(npc, off);
            var b = ReadByte(player, off);
            if (a == b)
                continue;

            diffs++;
            if (diffs <= 240)
                WriteDumpLine("0x" + off.ToString("X3") + "  她=0x" + a.ToString("X2") + "  玩家=0x" + b.ToString("X2"));
        }

        WriteDumpLine("差异共 " + diffs + " 字节");
    }

    private static string ByteAt(GameObject* go, int off)
    {
        return go == null ? "--" : ReadByte(go, off).ToString("X2");
    }

    private static byte ReadByte(GameObject* go, int off)
    {
        if (go == null || off < 0 || off >= DumpBytes)
            return 0;

        return *((byte*)go + off);
    }

    private static void ResetDumpFile()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncherCN", "pluginConfigs", "SmartNpc");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "go-dump.txt"), string.Empty, new UTF8Encoding(false));
        }
        catch
        {
            // 诊断绝不影响主流程
        }
    }

    private static void WriteDumpLine(string line)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncherCN", "pluginConfigs", "SmartNpc");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, "go-dump.txt");
            File.AppendAllText(path, line + "\n", new UTF8Encoding(false));
        }
        catch
        {
            // 诊断绝不影响主流程
        }
    }

    private static void WriteLine(string line)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncherCN", "pluginConfigs", "SmartNpc");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "target-trace.txt");

            if (_lines >= MaxLines)
            {
                var old  = File.ReadAllLines(path);
                var keep = Math.Min(old.Length, 120);
                var tail = new string[keep + 1];
                tail[0] = "……（前面已裁剪）……";
                Array.Copy(old, old.Length - keep, tail, 1, keep);
                File.WriteAllLines(path, tail, new UTF8Encoding(false));
                _lines = tail.Length;
            }

            File.AppendAllText(path, line + "\n", new UTF8Encoding(false));
            _lines++;
        }
        catch
        {
            // 诊断绝不影响主流程
        }
    }
}
