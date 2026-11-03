using System;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace SmartNpc.Core;

/// <summary>
/// 她真正的「说话配音」——<b>角色语音</b>，不是音效（v0.18.37）。
///
/// <para><b>线索与出处</b>：她做动作时本来就有正常配音 —— 说明游戏自己有一条
/// 「角色发声」的通路，而且音色由角色自带的<b>嗓音编号</b>决定（我们抄玩家外观时
/// 一并继承了玩家的嗓音，所以听着像玩家）。本机 <c>FFXIVClientStructs.dll</c>
/// 解出来的两条实锤：</para>
///
/// <list type="bullet">
///   <item><c>Character.Vfx</c>（<c>VfxContainer</c>）里有 <c>VoiceId</c>（ushort）
///         —— 就是角色创建时选的「嗓音」；</item>
///   <item><c>VfxContainer.LoadCharacterSound(int soundNumber, int unk2, nint unkGameObject,
///         ulong autoRelease, int weaponDataIndex, int unk6, ulong unk7)</c>
///         —— 游戏自己的角色发声入口，做动作时的配音就是走它。</item>
/// </list>
///
/// <para>所以这里做的是：① 面板选嗓音 → 写 <c>VoiceId</c>；
/// ② 她说话 / 点试听 → 调 <c>LoadCharacterSound</c>。
/// <b>不再用 <c>UIGlobals.PlaySoundEffect</c></b>（那是 UI 音效，用户明确否决）。</para>
/// </summary>
internal static unsafe class NpcVoice
{
    private static DateTime _lastPlay = DateTime.MinValue;

    /// <summary>上一次发声是否成功（面板提示用）。</summary>
    public static bool LastPlayOk { get; private set; }

    /// <summary>上一次发声是否因为节流被跳过（面板提示用）。</summary>
    public static bool LastSkippedByThrottle { get; private set; }

    /// <summary>上一次失败的原因（面板提示用）。</summary>
    public static string LastError { get; private set; } = string.Empty;

    /// <summary>
    /// 写嗓音预设（角色创建时那种「嗓音」）。
    /// </summary>
    /// <param name="c">她的 Character*。</param>
    /// <param name="voiceId">嗓音编号；<b>0 = 不改动</b>（沿用抄外观时继承来的嗓音）。</param>
    public static void ApplyPreset(Character* c, int voiceId)
    {
        if (c == null || voiceId <= 0)
            return;

        try
        {
            c->Vfx.VoiceId = (ushort)Math.Clamp(voiceId, 0, 65535);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>读她当前的嗓音编号（面板显示用）。</summary>
    public static ushort ReadPreset(Character* c)
    {
        if (c == null)
            return 0;
        try { return c->Vfx.VoiceId; }
        catch { return 0; }
    }

    /// <summary>
    /// 说话配音（带节流）。
    /// </summary>
    /// <param name="c">她的 Character*。</param>
    /// <param name="line">语音行号；0 = 不发声。</param>
    /// <param name="minIntervalSeconds">距上次发声不足这么久就不发（防刷屏）。</param>
    public static bool TryPlay(Character* c, int line, float minIntervalSeconds = 0.35f)
    {
        LastSkippedByThrottle = false;

        if (line <= 0)
        {
            LastPlayOk = false;
            return false;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastPlay).TotalSeconds < minIntervalSeconds)
        {
            LastSkippedByThrottle = true;
            return false;
        }

        return Play(c, line, now);
    }

    /// <summary>面板「试听」用：不受节流限制（用户主动点，当然要出声）。</summary>
    public static bool Audition(Character* c, int line)
        => Play(c, line, DateTime.UtcNow);

    private static bool Play(Character* c, int line, DateTime now)
    {
        if (c == null || line <= 0)
        {
            LastPlayOk = false;
            return false;
        }

        try
        {
            // ⚠️ 参数全部按本机 FFXIVClientStructs.dll 解出来的真实签名传：
            //    LoadCharacterSound(int soundNumber, int unk2, nint unkGameObject,
            //                      ulong autoRelease, int weaponDataIndex, int unk6, ulong unk7)
            // unkGameObject 传她自己的地址 —— 声源就定位在她身上（跟着她走，远了会变轻）。
            // autoRelease 传 1 = 播完由游戏自己回收。
            c->Vfx.LoadCharacterSound(line, 0, (nint)c, 1UL, 0, 0, 0UL);

            _lastPlay  = now;
            LastPlayOk = true;
            LastError  = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            LastPlayOk = false;
            LastError  = ex.Message;
            return false;
        }
    }
}
