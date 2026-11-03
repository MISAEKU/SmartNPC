using System;
using System.Numerics;
using System.Text;

namespace SmartNpc.Core;

/// <summary>
/// BGCollision 碰撞层探针（v0.18.28 —— "家具碰撞箱"搜索器）。
///
/// <para><b>要回答的问题</b>：家具的碰撞体到底在 BGCollision 的哪个 layerMask 位上？
/// 她的地面射线一直只认 layerMask=1（地形+静态建筑），家具顶面永远打不到；
/// 而玩家明显会被家具挡住 —— 说明家具碰撞在碰撞系统的<b>某个层</b>里。</para>
///
/// <para><b>方法</b>：玩家站在家具顶上时（脚高明显高于地形射线），把 32 个层位
/// 逐个向下打射线——命中高度明显高于地形（= 打在家具面）的位就是候选家具层。
/// 找到后 <see cref="NpcGround"/> 直接对家具层打射线取顶面，就事论事，
/// 物理背书，不再依赖任何玩家脚印/轨迹。</para>
///
/// <para>结果落盘 <c>mask-probe.txt</c>：只在有候选命中时写行（站空地时不刷屏）。</para>
/// </summary>
internal static class NpcMaskProbe
{
    private const float Interval = 0.7f;
    private const int   MaxLines = 300;

    private static float _cooldown;
    private static int   _lines;

    public static void Tick(float deltaTime, Vector3 playerPos)
    {
        _cooldown -= deltaTime;
        if (_cooldown > 0f)
            return;
        _cooldown = Interval;

        try
        {
            var origin = new Vector3(playerPos.X, playerPos.Y + 2.5f, playerPos.Z);
            var down   = new Vector3(0f, -1f, 0f);

            // 基准：mask=1 的地形高度（她脚下的"官方地面"）
            if (!NpcAvoidance.CastWithMask(origin, down, 30f, 1, out var terrainHit))
                return;
            var terrainY = terrainHit.Point.Y;
            if (!float.IsFinite(terrainY))
                return;

            var sb = new StringBuilder();
            sb.Append(string.Format("{0:HH:mm:ss.fff} 玩家Y={1:F2} 地形={2:F2} 抬升={3:F2}",
                DateTime.Now, playerPos.Y, terrainY, playerPos.Y - terrainY));

            var found = 0;
            for (var bit = 0; bit < 32; bit++)
            {
                var mask = 1 << bit;
                if (!NpcAvoidance.CastWithMask(origin, down, 30f, mask, out var hit))
                    continue;

                var hy = hit.Point.Y;
                if (!float.IsFinite(hy))
                    continue;

                // 只关心"明显高于地形、且在玩家附近"的命中 —— 那才是家具碰撞
                if (hy > terrainY + 0.3f && hy < playerPos.Y + 2.0f)
                {
                    sb.Append("  bit").Append(bit).Append('@').Append(hy.ToString("F2"));
                    found++;
                }
            }

            // 站空地 / 没有任何家具层候选时不写行，防刷屏
            if (found == 0)
                return;

            WriteLine(sb.ToString());
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
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncherCN", "pluginConfigs", "SmartNpc");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "mask-probe.txt");

            if (_lines >= MaxLines)
            {
                var old = System.IO.File.ReadAllLines(path);
                var keep = Math.Min(old.Length, 80);
                var tail = new string[keep + 1];
                tail[0] = "……（前面已裁剪）……";
                Array.Copy(old, old.Length - keep, tail, 1, keep);
                System.IO.File.WriteAllLines(path, tail, new UTF8Encoding(false));
                _lines = tail.Length;
            }

            System.IO.File.AppendAllText(path, line + "\n", new UTF8Encoding(false));
            _lines++;
        }
        catch
        {
            // 诊断绝不影响主流程
        }
    }
}
