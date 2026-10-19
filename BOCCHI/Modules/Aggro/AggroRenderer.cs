using System;
using System.Numerics;
using BOCCHI.Data;
using Pictomancy;

namespace BOCCHI.Modules.Aggro;

/// <summary>繪製端的輸出口：一個環形扇區（inner..outer、minAngle..maxAngle，角度照 Pictomancy 的慣例）。</summary>
public interface IAggroDrawSink
{
    void Fan(Vector3 origin, float innerRadius, float outerRadius, float minAngle, float maxAngle, uint color);
}

/// <summary>每幀從設定抄一份的繪製參數（半徑單位：米，不含模型半徑）。</summary>
public readonly record struct AggroDrawSettings(
    bool DrawSight,
    float SightRadius,
    float SightHalfAngle,
    bool DrawSound,
    float SoundRadius,
    bool DrawProximity,
    float ProximityRadius,
    bool DrawMagic,
    float MagicRadius,
    bool DrawBlood,
    float BloodRadius,
    bool DrawUnknown,
    float UnknownRadius);

/// <summary>
/// 只讀快照、不配置。外框一律用「很窄的環形扇區」畫：Pictomancy 的線條（AddCircle／AddFan）每條都會
/// 把路徑複製成新陣列，每幀每隻怪約 0.6～1.2 KB；扇區填色走預配的實例緩衝區，不配置。
/// </summary>
public static class AggroRenderer
{
    // 「可能」細而淡、「確定」粗而實；同一種感知類型同一個色相。
    public const float PossibleBand = 0.06f;

    public const float CertainBand = 0.14f;

    public const float PossibleAlpha = 0.40f;

    public const float CertainAlpha = 0.90f;

    private const float FacingLength = 2.5f;

    public static readonly Vector3 SightColor = new(1.00f, 0.30f, 0.25f);

    public static readonly Vector3 SoundColor = new(0.30f, 0.65f, 1.00f);

    public static readonly Vector3 ProximityColor = new(1.00f, 0.65f, 0.15f);

    public static readonly Vector3 MagicColor = new(0.75f, 0.45f, 1.00f);

    public static readonly Vector3 BloodColor = new(1.00f, 0.25f, 0.60f);

    public static readonly Vector3 UnknownColor = new(0.80f, 0.80f, 0.80f);

    public static int Draw(ReadOnlySpan<AggroEntry> entries, in AggroDrawSettings settings, IAggroDrawSink sink)
    {
        var fans = 0;
        foreach (ref readonly var entry in entries)
        {
            if (entry.State == AggroState.None)
            {
                continue;
            }

            var certain = entry.State == AggroState.Certain;
            var band = certain ? CertainBand : PossibleBand;
            var alpha = certain ? CertainAlpha : PossibleAlpha;
            var hitbox = MathF.Max(entry.HitboxRadius, 0f);
            var senses = entry.Senses;

            if ((senses & AggroSense.Sight) != 0 && settings.DrawSight)
            {
                fans += Cone(sink, entry.Position, entry.Rotation, settings.SightRadius + hitbox, settings.SightHalfAngle, hitbox, band, Pack(SightColor, alpha));
            }

            if ((senses & AggroSense.Sound) != 0 && settings.DrawSound)
            {
                fans += Ring(sink, entry.Position, settings.SoundRadius + hitbox, band, Pack(SoundColor, alpha));
            }

            if ((senses & AggroSense.Proximity) != 0 && settings.DrawProximity)
            {
                fans += Ring(sink, entry.Position, settings.ProximityRadius + hitbox, band, Pack(ProximityColor, alpha));
            }

            if ((senses & AggroSense.Magic) != 0 && settings.DrawMagic)
            {
                fans += Ring(sink, entry.Position, settings.MagicRadius + hitbox, band, Pack(MagicColor, alpha));
            }

            if ((senses & AggroSense.Blood) != 0 && entry.BloodActive && settings.DrawBlood)
            {
                fans += Ring(sink, entry.Position, settings.BloodRadius + hitbox, band, Pack(BloodColor, alpha));
            }

            if ((senses & AggroSense.Unknown) != 0 && settings.DrawUnknown)
            {
                fans += Ring(sink, entry.Position, settings.UnknownRadius + hitbox, band, Pack(UnknownColor, alpha));
            }
        }

        return fans;
    }

    /// <summary>遊戲朝向（sin θ, cos θ）換成 Pictomancy 扇區角度：a＝−θ。</summary>
    public static float FacingAngle(float rotation)
    {
        return -rotation;
    }

    /// <summary>ImGui 的 ABGR 打包（與 ImGui.ColorConvertFloat4ToU32 相同），不經原生呼叫。</summary>
    public static uint Pack(Vector3 rgb, float alpha)
    {
        static uint Channel(float v)
        {
            return (uint)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);
        }

        return Channel(rgb.X) | Channel(rgb.Y) << 8 | Channel(rgb.Z) << 16 | Channel(alpha) << 24;
    }

    private static int Ring(IAggroDrawSink sink, Vector3 origin, float radius, float band, uint color)
    {
        sink.Fan(origin, MathF.Max(radius - band, 0f), radius, 0f, MathF.Tau, color);
        return 1;
    }

    private static int Cone(IAggroDrawSink sink, Vector3 origin, float rotation, float radius, float halfAngle, float hitbox, float band, uint color)
    {
        var facing = FacingAngle(rotation);
        sink.Fan(origin, MathF.Max(radius - band, 0f), radius, facing - halfAngle, facing + halfAngle, color);
        Spoke(sink, origin, hitbox, radius, facing - halfAngle, band, color);
        Spoke(sink, origin, hitbox, radius, facing + halfAngle, band, color);
        Spoke(sink, origin, hitbox * 0.5f, hitbox + FacingLength, facing, band * 1.5f, color);
        return 4;
    }

    // 從 inner 到 outer 的一條徑向線：用極窄的扇區近似，外端寬度＝width。
    private static void Spoke(IAggroDrawSink sink, Vector3 origin, float inner, float outer, float angle, float width, uint color)
    {
        if (outer <= inner || outer <= 0f)
        {
            return;
        }

        var halfSpread = width / (2f * outer);
        sink.Fan(origin, inner, outer, angle - halfSpread, angle + halfSpread, color);
    }
}

/// <summary>正式繪製用的輸出口：包一個本幀的 Pictomancy 繪製清單，並守住實例緩衝區上限。</summary>
public sealed class PictomancyAggroSink : IAggroDrawSink
{
    // Pictomancy 的扇區緩衝區一幀 2048 個、超過會擲例外；留一半給同一外掛的其他模組。
    public const int Budget = 1024;

    private PctDrawList? list;

    private int used;

    public int Used
    {
        get => used;
    }

    public void Begin(PctDrawList drawList)
    {
        list = drawList;
        used = 0;
    }

    public void End()
    {
        list = null;
    }

    public void Fan(Vector3 origin, float innerRadius, float outerRadius, float minAngle, float maxAngle, uint color)
    {
        if (list == null || used >= Budget)
        {
            return;
        }

        used++;
        list.AddFanFilled(origin, innerRadius, outerRadius, minAngle, maxAngle, color);
    }
}
