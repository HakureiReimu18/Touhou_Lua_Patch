using System;

namespace Touhou.MartialArts
{
    internal static class PunchMotion
    {
        internal readonly struct Pose
        {
            internal readonly float Lean, Dip;
            internal Pose(float lean, float dip) { Lean = lean; Dip = dip; }
        }

        internal static float SafeValue(float value, float fallback, float min, float max) =>
            float.IsNaN(value) || float.IsInfinity(value) ? fallback : Math.Clamp(value, min, max);

        internal static Pose Evaluate(float elapsed, float duration)
        {
            if (!float.IsFinite(elapsed) || !float.IsFinite(duration)
                || duration <= 0f || elapsed <= 0f || elapsed >= duration) return default;
            float t = elapsed / duration;
            // 对齐原来的 40% 出拳、15% 保持、45% 收拳，不改手臂轨迹。
            if (t < 0.12f)
            {
                float windup = Smooth(t / 0.12f);
                return new Pose(-0.2f * windup, windup);
            }
            if (t < 0.4f)
            {
                float drive = Smooth((t - 0.12f) / 0.28f);
                return new Pose(-0.2f + 1.2f * drive, 1f - 0.8f * drive);
            }
            if (t < 0.55f) return new Pose(1f, 0.2f);
            float recovery = 1f - Smooth((t - 0.55f) / 0.45f);
            return new Pose(recovery, 0.2f * recovery);
        }

        static float Smooth(float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return t * t * (3f - 2f * t);
        }
    }
}
