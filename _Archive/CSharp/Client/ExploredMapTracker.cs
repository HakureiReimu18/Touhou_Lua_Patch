using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ExploredMap
{
    /// <summary>
    /// 探索追踪核心：每帧 think 钩子驱动，维护一张覆盖整个关卡的网格化"战争迷雾"地图，
    /// 以及本地玩家的航线点列。单机与多人（客户端本地）均可用，数据不跨端同步。
    ///
    /// 探索来源：
    ///   1. 肉身视野：玩家周围 15m 持续标记（走过的航道）
    ///   2. 手持海图仪：装备且通电时，玩家周围 60m 每 1/3 秒标记（ScanRange 可在 XML 调）
    ///   3. 潜艇被动声呐：主潜艇上有通电 Sonar 时，潜艇周围 80m 每秒标记
    ///   4. 主动声呐脉冲：任一 Sonar 处于 Active 模式时，潜艇周围 150m 每 5 秒标记
    ///
    /// 已知限制：换关卡（Level.Loaded 引用变化）时重置；不做存档持久化与多人同步。
    /// </summary>
    internal static class ExploredMapTracker
    {
        // ———— 可调参数 ————
        public const int GridW = 256;                  // 网格横向格数（覆盖 6.4 km）
        public const int GridH = 128;                  // 网格纵向格数（覆盖 3.2 km 深度）
        public const float CellSize = 2500f;           // 每格 25 m（世界单位，100 = 1 m）
        public const float SelfRevealRadius = 1500f;   // 肉身视野 15 m
        public const float SubPassiveRadius = 8000f;   // 潜艇被动声呐 80 m
        public const float SubPingRadius = 15000f;     // 主动声呐脉冲 150 m
        public const int MaxPathPoints = 6000;         // 航线上限（超出后抽稀）

        // 单元状态：0 未知 / 1 开阔水域 / 2 岩壁
        private static readonly byte[] cells = new byte[GridW * GridH];
        private static readonly List<Vector2> path = new List<Vector2>();

        private static Level currentLevel;
        private static uint tick;
        private static bool textureDirty = true;
        private static Texture2D mapTexture;
        private static readonly Color[] texBuffer = new Color[GridW * GridH];

        private static readonly Color UnknownColor = new Color(3, 12, 16);
        private static readonly Color OpenColor = new Color(12, 52, 82);
        private static readonly Color WallColor = new Color(62, 52, 40);

        // Sonar.CurrentMode 通过反射读取（规避版本间枚举签名差异），失败时静默降级
        private static PropertyInfo sonarModeProp;
        private static bool sonarModeResolved;
        // 运行时异常只记录一次，避免每帧刷屏
        private static bool wallCheckErrorLogged;
        private static bool textureErrorLogged;

        /// <summary>模组卸载后置为 false，追踪转为惰性。</summary>
        public static bool Enabled { get; set; }

        public static Texture2D MapTexture => mapTexture;
        public static IReadOnlyList<Vector2> Path => path;

        /// <summary>追踪节拍入口（由 Level.Update 后置补丁每帧调用）。</summary>
        public static void Tick(float deltaTime)
        {
            if (!Enabled)
            {
                return;
            }
            Level level = Level.Loaded;
            Character player = Character.Controlled;
            if (level == null || player == null)
            {
                return;
            }

            if (!ReferenceEquals(level, currentLevel))
            {
                ResetForLevel(level);
            }
            tick++;

            RecordPath(player.WorldPosition);

            // 1. 肉身视野
            if (tick % 10 == 0)
            {
                RevealAround(player.WorldPosition, SelfRevealRadius);
            }

            // 2. 手持海图仪扫描
            if (tick % 20 == 0 && TryGetEquippedScanRange(player, out float scanRange))
            {
                RevealAround(player.WorldPosition, scanRange);
            }

            // 3 & 4. 主潜艇声呐（被动持续 + 主动脉冲）
            Submarine sub = Submarine.MainSub;
            if (sub != null && HasPoweredSonar(sub, out bool activePing))
            {
                if (tick % 60 == 0)
                {
                    RevealAround(sub.WorldPosition, SubPassiveRadius);
                }
                if (activePing && tick % 300 == 0)
                {
                    RevealAround(sub.WorldPosition, SubPingRadius);
                }
            }

            if (textureDirty && tick % 20 == 0)
            {
                RebuildTexture();
            }
        }

        /// <summary>模组卸载时清状态（Dispose 调用）。</summary>
        public static void ResetState()
        {
            currentLevel = null;
            Array.Clear(cells, 0, cells.Length);
            path.Clear();
            textureDirty = true;
            try { mapTexture?.Dispose(); } catch { }
            mapTexture = null;
        }

        private static void ResetForLevel(Level level)
        {
            currentLevel = level;
            Array.Clear(cells, 0, cells.Length);
            path.Clear();
            textureDirty = true;
        }

        // ———— 航线记录 ————

        private static void RecordPath(Vector2 pos)
        {
            if (path.Count == 0 || Vector2.DistanceSquared(path[path.Count - 1], pos) > 200f * 200f)
            {
                path.Add(pos);
                if (path.Count > MaxPathPoints)
                {
                    // 抽稀：隔点保留，航线长度翻倍
                    for (int i = path.Count - 1; i >= 0; i -= 2)
                    {
                        path.RemoveAt(i);
                    }
                }
            }
        }

        // ———— 探索标记 ————

        private static void RevealAround(Vector2 center, float radius)
        {
            int minGX = Math.Max(0, (int)MathF.Floor((center.X - radius) / CellSize));
            int maxGX = Math.Min(GridW - 1, (int)MathF.Floor((center.X + radius) / CellSize));
            int minGY = Math.Max(0, (int)MathF.Floor((-center.Y - radius) / CellSize));
            int maxGY = Math.Min(GridH - 1, (int)MathF.Floor((-center.Y + radius) / CellSize));
            float r2 = radius * radius;

            for (int gy = minGY; gy <= maxGY; gy++)
            {
                for (int gx = minGX; gx <= maxGX; gx++)
                {
                    int idx = gy * GridW + gx;
                    if (cells[idx] != 0)
                    {
                        continue;
                    }
                    Vector2 cellCenter = new Vector2((gx + 0.5f) * CellSize, -(gy + 0.5f) * CellSize);
                    if (Vector2.DistanceSquared(cellCenter, center) > r2)
                    {
                        continue;
                    }
                    cells[idx] = IsWall(cellCenter) ? (byte)2 : (byte)1;
                    textureDirty = true;
                }
            }
        }

        /// <summary>判定世界坐标点是否在岩壁内（主洞壁单元 + 附加墙体）。</summary>
        private static bool IsWall(Vector2 worldPos)
        {
            try
            {
                var wallCells = Level.Loaded.GetCells(worldPos, 1);
                if (wallCells != null)
                {
                    foreach (var cell in wallCells)
                    {
                        if (cell.IsPointInside(worldPos))
                        {
                            return true;
                        }
                    }
                }
                var extraWalls = Level.Loaded.ExtraWalls;
                if (extraWalls != null)
                {
                    foreach (LevelWall wall in extraWalls)
                    {
                        if (wall.IsPointInside(worldPos))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (!wallCheckErrorLogged)
                {
                    wallCheckErrorLogged = true;
                    LuaCsLogger.LogError($"[探索海图仪] 岩壁判定失败（后续不再重复记录）: {e.Message}");
                }
            }
            return false;
        }

        // ———— 设备 / 声呐探测 ————

        /// <summary>玩家手持的海图仪是否通电；是则输出其扫描半径（取最大）。</summary>
        public static bool TryGetEquippedScanRange(Character player, out float scanRange)
        {
            scanRange = 0f;
            if (player?.HeldItems == null)
            {
                return false;
            }
            foreach (Item held in player.HeldItems)
            {
                if (held == null)
                {
                    continue;
                }
                foreach (Items.ExploredMapComponent comp in held.GetComponents<Items.ExploredMapComponent>())
                {
                    if (comp.Voltage > 0.01f && comp.ScanRange > scanRange)
                    {
                        scanRange = comp.ScanRange;
                    }
                }
            }
            return scanRange > 0f;
        }

        private static bool HasPoweredSonar(Submarine sub, out bool activePing)
        {
            activePing = false;
            bool powered = false;
            foreach (Item item in sub.GetItems(false))
            {
                foreach (Sonar sonar in item.GetComponents<Sonar>())
                {
                    if (sonar.Voltage <= 0.05f)
                    {
                        continue;
                    }
                    powered = true;
                    if (IsSonarInActiveMode(sonar))
                    {
                        activePing = true;
                    }
                }
            }
            return powered;
        }

        private static bool IsSonarInActiveMode(Sonar sonar)
        {
            try
            {
                if (!sonarModeResolved)
                {
                    sonarModeResolved = true;
                    sonarModeProp = typeof(Sonar).GetProperty("CurrentMode", BindingFlags.Public | BindingFlags.Instance);
                }
                return sonarModeProp?.GetValue(sonar)?.ToString() == "Active";
            }
            catch
            {
                return false;
            }
        }

        // ———— 纹理 ————

        private static void RebuildTexture()
        {
            GraphicsDevice gd = GameMain.Instance?.GraphicsDevice;
            if (gd == null)
            {
                return;
            }
            try
            {
                if (mapTexture == null || mapTexture.IsDisposed)
                {
                    mapTexture = new Texture2D(gd, GridW, GridH);
                }
                for (int i = 0; i < cells.Length; i++)
                {
                    texBuffer[i] = cells[i] switch
                    {
                        1 => OpenColor,
                        2 => WallColor,
                        _ => UnknownColor
                    };
                }
                mapTexture.SetData(texBuffer);
                textureDirty = false;
            }
            catch (Exception e)
            {
                if (!textureErrorLogged)
                {
                    textureErrorLogged = true;
                    LuaCsLogger.LogError($"[探索海图仪] 地图纹理重建失败（后续不再重复记录）: {e.Message}");
                }
            }
        }
    }
}
