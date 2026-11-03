using System;
using System.IO;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
namespace SmartNpc.Core;

/// <summary>
/// v0.18.43 —— #5「一开始选不中 / 选中后被自动取消」根因取证探针（<b>纯只读</b>）。
///
/// <para><b>静态反汇编实锤</b>（对国服 ffxiv_dx11.exe，过程与产物在 <c>_re/</c> 目录）：</para>
/// <list type="bullet">
/// <item>游戏点击选中 = <c>TargetSystem.SetHardTarget(ts, 解析(hover 的 GameObjectId))</c>，
///       <b>所有</b>调用方都先把对象指针换成"按 GID 解析回来的指针"再设；</item>
/// <item>解析器（真实 RVA 0x842690；⚠️ _re 产物里标 0x842290 —— 扫描索引当文件偏移用了，所有 _re 地址都比真实小 0x400，已用 .pdata 核实）：
///       <c>id == 0xE0000000 → NULL</c>；在 <c>ObjectArrays._gameObjectIdSorted</c>
///       （偏移 0x1998，容量 0x333 项，计数在 0x4CC8 —— 真实二分函数 RVA 0xAFD7D0 实锤）里<b>按 Id 二分查找</b>；
///       找到后若 <c>[obj+0x190] &amp; 4</c> 置位 → <b>仍然返回 NULL</b>；</item>
/// <item><c>SetHardTarget(ts, NULL)</c> 不是"什么都不做"，而是<b>把当前硬目标清空</b>。</item>
/// </list>
///
/// <para>⇒ 只要"解析她的 GID"返回 NULL：游戏自己的点击选不中；而任何一帧里
/// 谁再对她走一遍"解析失败 → SetHardTarget(NULL)"，已选中的目标就被清掉。
/// 另外 v0.18.42 的教训也在此定案：<c>GameObject+0x8C</c> 存的是
/// <b>"COM 槽位 + 200"</b>（游戏 <c>GetIndexByObject</c> 就是这么算的），
/// 她读出 216 = 槽位 16，<b>那个字段从头到尾都是对的</b>。</para>
///
/// <para>本探针每 0.5 秒落盘（registry-probe.txt，只读不写游戏内存）：</para>
/// <list type="bullet">
/// <item>按游戏语义复刻的二分查找对她/玩家的两种结果（含/不含 +0x190 位检查）；</item>
/// <item>她/玩家的指针与 Id 在排序表里的线性扫描位置、排序被破坏的次数；</item>
/// <item><c>[她+0x190]</c> 字节原始值。</item>
/// </list>
/// </summary>
internal static unsafe class NpcRegistryProbe
{
    private const float Interval = 0.5f;
    private const int Capacity = 0x333;          // 819 项（0x3330-0x1998 = 0x1998 字节 / 8）
    private const int OffsetGameObjectIdSorted = 0x1998;
    private const int OffsetCount = 0x4CC8;

    private static nint _oaStatic;
    private static nint _lastNpc;
    private static DateTime _last = DateTime.MinValue;
    private static int _lines;
    private static readonly string FilePath = BuildPath();

    /// <summary>最近一次采样的摘要（面板显示用）。</summary>
    public static string LastSummary { get; private set; } = "尚未采样";

    /// <summary>最近一次错误（定位失败 / 采样异常）。</summary>
    public static string LastError { get; private set; } = string.Empty;

    /// <summary>注册表静态地址是否已定位。</summary>
    public static bool Ready => _oaStatic != nint.Zero;

    private static string BuildPath()
    {
        try
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncherCN", "pluginConfigs", "SmartNpc", "registry-probe.txt");
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Plugin 构造时用 SigScanner 把 ObjectArrays 静态实例定位好；失败传 0（探针自动停用）。</summary>
    public static void Initialize(nint objectArraysStaticAddress, string locateNote)
    {
        _oaStatic = objectArraysStaticAddress;
        LastError = Ready ? string.Empty : locateNote;
        Append($"== 探针初始化 == ObjectArrays 静态地址=0x{objectArraysStaticAddress:X} {locateNote}");
    }

    /// <summary>v0.18.43.2：sig 修正（40 53 前缀 + cmp 真实编码 48 3B C8），并核实 _re 地址系统性偏小 0x400。
    /// 两步定位：先 ScanText 精确定位解析函数本体（真实 RVA 0x842690，函数头 20 字节唯一），
    /// 再从函数内 <c>lea rcx,[rip+disp32]</c>（位于 函数+0x16，disp 在 +0x19）算出注册表实例地址（真实 RVA 0x2A89DD0）。</summary>
    public static void InitializeFromResolver(nint resolver, string note)
    {
        if (resolver == nint.Zero)
        {
            Initialize(nint.Zero, "解析器定位失败 " + note);
            return;
        }

        try
        {
            int disp = *(int*)(resolver + 0x19);
            nint oa = resolver + 0x1D + disp;
            long delta = (long)oa - (long)resolver;
            if (delta <= 0 || delta > 0x8000000)
            {
                Initialize(nint.Zero, $"lea 目标异常 delta=0x{delta:X}");
                return;
            }

            _oaStatic = oa;
            int count = *(int*)(_oaStatic + OffsetCount);
            LastError = string.Empty;
            Append($"== 探针初始化(v2) == 解析器=0x{resolver:X} lea_delta=0x{delta:X} " +
                   $"ObjectArrays=0x{oa:X} 立即读计数={count} {note}");
        }
        catch (Exception ex)
        {
            Initialize(nint.Zero, "从解析器推地址异常: " + ex.Message);
        }
    }

    public static void Tick(GameObject* npc, GameObject* player)
    {
        if (!Ready || npc == null)
            return;

        var now = DateTime.UtcNow;

        // 她的地址变了（刚召唤 / 换区重建）→ 立刻强制采样一次
        var force = npc != (GameObject*)_lastNpc;
        if (!force && (now - _last).TotalSeconds < Interval)
            return;

        _last = now;
        _lastNpc = (nint)npc;

        try
        {
            Sample(npc, player);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Append("采样异常: " + ex.Message);
        }
    }

    private static void Sample(GameObject* npc, GameObject* player)
    {
        // 不经 CS 的 ObjectArrays 类型（部分 CS 版本未暴露该类型），直接按实锤偏移读：
        //   +0x1998 = GameObjectId 排序数组（GameObject*[819]），+0x4CC8 = 计数（int）。
        int count = *(int*)(_oaStatic + OffsetCount);
        if (count < 0 || count > Capacity)
        {
            Append($"表计数异常 count={count}（应在 0..{Capacity}）—— 结构布局可能漂移，探针停用");
            LastError = $"count={count}";
            _oaStatic = nint.Zero;   // 布局不对就别再摸了，只读也不能读出乱地址
            return;
        }

        var arr = (GameObject**)(_oaStatic + OffsetGameObjectIdSorted);

        ulong herId = npc->GetGameObjectId().Id;
        ulong plyId = player != null ? player->GetGameObjectId().Id : 0;

        // ① 按游戏语义复刻的二分查找（含/不含 +0x190 位检查）
        var (her0, _) = BinarySearch(arr, count, herId, applyBit190Check: true);
        var (her1, _) = BinarySearch(arr, count, herId, applyBit190Check: false);
        nint ply0 = 0;
        if (player != null)
            (ply0, _) = BinarySearch(arr, count, plyId, applyBit190Check: true);

        // ② 线性扫描：指针在不在表里、同 Id 首现位置、排序被破坏次数
        int herPtrIdx = -1, herIdIdx = -1, plyPtrIdx = -1, sortViolations = 0;
        ulong prev = 0;
        int nullSlots = 0;
        int scan = Math.Min(count, Capacity);
        for (int i = 0; i < scan; i++)
        {
            var o = arr[i];
            if (o == null)
            {
                nullSlots++;
                continue;
            }

            ulong id = o->GetGameObjectId().Id;
            if (id < prev)
                sortViolations++;
            prev = id;

            if (o == npc)
                herPtrIdx = i;
            if (id == herId && herIdIdx < 0)
                herIdIdx = i;
            if (player != null && o == player)
                plyPtrIdx = i;
        }

        // ③ 关键字节：+0x190（bit4=0x04 会令解析返回 NULL）
        byte herFlag190 = *(byte*)((byte*)npc + 0x190);
        byte plyFlag190 = player != null ? *(byte*)((byte*)player + 0x190) : (byte)0;

        string line =
            (count == 0 ? "⚠计数=0(地址大概率错了) " : "") +
            $"计数={count} 空槽={nullSlots} 排序违例={sortViolations} | " +
            $"她: Id=0x{herId:X16} 二分[查bit190]={P(her0)} 二分[跳检查]={P(her1)} " +
            $"指针在表={Idx(herPtrIdx)} 同Id在表={Idx(herIdIdx)} +0x190=0x{herFlag190:X2} | " +
            $"玩家: 二分[查bit190]={P(ply0)} 指针在表={Idx(plyPtrIdx)} +0x190=0x{plyFlag190:X2}";

        LastSummary = line;
        Append(line);
    }

    /// <summary>复刻 真实RVA 0xAFD7D0 的二分 + 解析器的 +0x190 位检查（_re 旧标注 0xAFD3D0/0x842290，均偏小 0x400）。纯读。
    /// 返回 nint（C# 元组不允许指针类型参数），0 = NULL。</summary>
    private static (nint obj, int mid) BinarySearch(GameObject** arr, int count, ulong id, bool applyBit190Check)
    {
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var obj = arr[mid];
            if (obj == null)
                return (0, mid);   // 排序表里有空槽 —— 这本身就是要找的问题

            ulong oid = obj->GetGameObjectId().Id;
            if (oid < id)
            {
                lo = mid + 1;
            }
            else if (oid > id)
            {
                hi = mid - 1;
            }
            else
            {
                if (applyBit190Check && (*(byte*)((nint)obj + 0x190) & 0x04) != 0)
                    return (0, mid);
                return ((nint)obj, mid);
            }
        }

        return (0, -1);
    }

    private static string P(nint o) => o != nint.Zero ? "命中" : "NULL";

    private static string Idx(int i) => i >= 0 ? $"Y(i={i})" : "N";

    private static void Append(string line)
    {
        if (string.IsNullOrEmpty(FilePath))
            return;
        try
        {
            if (_lines == 0)
                File.WriteAllText(FilePath, string.Empty, new UTF8Encoding(false));
            if (_lines < 4000)
                _lines++;
            File.AppendAllText(FilePath,
                DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + "\n",
                new UTF8Encoding(false));
        }
        catch
        {
            // 诊断写不出来绝不影响插件
        }
    }
}
