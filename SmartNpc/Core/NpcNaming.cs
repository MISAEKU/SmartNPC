using System;
using System.Text;

namespace SmartNpc.Core;

/// <summary>
/// v0.18.45 她的显示名（目标信息栏 / 名牌）。
///
/// <para><b>原理</b>：目标信息栏（血条面板）与头顶名牌每帧都从 GameObject 的名字缓冲读名字。
/// <c>GameObject._name</c> 在 <c>+0x30</c>，Fixed 64 字节 NUL 结尾 UTF-8
/// （偏移 2026-10-11 已由本机 FFXIVClientStructs 元数据 dump 实锤，见 <see cref="NpcHousing"/> 的 ReadName）。
/// 把配置名写进这个缓冲，选中她时面板显示的就是自定义名，改完立即生效。</para>
///
/// <para><b>红线</b>：她是我们自己的本地对象，写自己的名字缓冲不经过任何网络路径（纯视觉）。</para>
///
/// <para><b>默认名</b>：配置名留空 = 显示游戏创建她时分配的默认名（召唤时抓拍在
/// <see cref="NpcHandle.GameAssignedName"/> 里，不随改名丢失）。</para>
/// </summary>
internal static unsafe class NpcNaming
{
    private const int BufferBytes = 64;
    private const int MaxPayload  = BufferBytes - 1;   // 最后 1 字节必须留给 NUL

    /// <summary>读对象名（NUL 结尾 UTF-8；异常返回空串）。</summary>
    public static string ReadName(nint gameObject)
    {
        try
        {
            var p = (byte*)(gameObject + 0x30);
            var len = 0;
            while (len < BufferBytes && p[len] != 0)
                len++;
            return len == 0 ? string.Empty : Encoding.UTF8.GetString(p, len);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>生效名：配置名留空 = 游戏分配的默认名。</summary>
    public static string EffectiveName(string? customName, string gameDefault)
        => string.IsNullOrWhiteSpace(customName) ? gameDefault : customName.Trim();

    /// <summary>
    /// 写名字缓冲。UTF-8 编码、按<b>字节</b>截断到 63 字节（不截破多字节序列）、补 NUL。
    /// 当前名字已是目标值时不写（省一次内存写）。
    /// </summary>
    public static void WriteName(nint gameObject, string name)
    {
        try
        {
            var current = ReadName(gameObject);
            if (current == name)
                return;

            // 按字符累加，超出字节预算即停 —— 不会把一个 UTF-8 序列截成两半。
            var bytes = new byte[MaxPayload];
            var used = 0;
            var enc = Encoding.UTF8;
            var maxChars = Math.Min(name.Length, 96);   // 63 字节上限下 CJK 最多 21 字，96 是防御性上限
            for (var i = 0; i < maxChars; i++)
            {
                var cnt = enc.GetByteCount(name, i, 1);
                if (used + cnt > MaxPayload)
                    break;
                used += enc.GetBytes(name, i, 1, bytes, used);
            }

            var p = (byte*)(gameObject + 0x30);
            for (var i = 0; i < used; i++)
                p[i] = bytes[i];
            p[used] = 0;    // NUL 结尾（把旧名字的残留一起盖掉）
        }
        catch
        {
            // 名字写不进去绝不能影响她本人
        }
    }
}
