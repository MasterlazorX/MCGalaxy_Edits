/*
    Copyright 2015-2024 MCGalaxy

    Dual-licensed under the Educational Community License, Version 2.0 and
    the GNU General Public License, Version 3 (the "Licenses"); you may
    not use this file except in compliance with the Licenses. You may
    obtain a copy of the Licenses at

    https://opensource.org/license/ecl-2-0/
    https://www.gnu.org/licenses/gpl-3.0.html

    Unless required by applicable law or agreed to in writing,
    software distributed under the Licenses are distributed on an "AS IS"
    BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express
    or implied. See the Licenses for the specific language governing
    permissions and limitations under the Licenses.
 */
using System;
using BlockID = System.UInt16;

namespace MCGalaxy.Network
{
    /// <summary> Survival simulation mode negotiated for a particular level. </summary>
    /// <remarks> Sent verbatim as the 'mode' byte of SURV_HELLO. Off means the SurvivalTest
    /// sub-protocol is inactive on this level even for a capable client. </remarks>
    public enum SurvivalMode : byte
    {
        Off     = 0, // not a survival map
        Classic = 1, // Classic 0.30 survival test
        Indev   = 2, // Indev survival
    }

    /// <summary> Indev world theme, sent as the 'theme' byte of SURV_WORLDINFO. </summary>
    public enum SurvivalTheme : byte
    {
        Normal = 0, Hell = 1, Paradise = 2, Woods = 3, Floating = 4,
    }

    /// <summary> Server side of the "SurvivalTest" sub-protocol spoken by survival-test ClassiCube clients. </summary>
    /// <remarks>
    /// This is the <b>foundation</b> only, mirroring the client-side foundation documented in
    /// ClassiCube's doc/survival-handshake.md. It:
    ///   1. relies on the SurvivalTest CPE extension for the per-connection capability (see CPESupport.cs),
    ///   2. sends the per-map SURV_HELLO / SURV_WORLDINFO handshake when a capable client enters a survival level, and
    ///   3. receives, bounds-checks, validates and logs inbound client intents.
    /// The actual state appliers (mobs, inventory, drops, health, day/night, ...) and the client mode-flip
    /// are deferred. Every message id is already reserved below so that later work is fill-in against a fixed
    /// wire contract (ClassiCube's src/SurvivalNet.h), not new protocol design.
    ///
    /// Two-layer design (why capability and activation are separate):
    ///   * Capability is negotiated once at login via the CPE extension and lives for the whole connection.
    ///   * Activation is per-map: a player hops between a plain Classic level and a survival level without
    ///     reconnecting, so "is <i>this</i> map survival?" must ride a per-map message (SURV_HELLO), not the
    ///     one-shot CPE handshake.
    ///
    /// Wire format: each message is [id:1][fields...] carried inside a single fixed 64-byte CPE PluginMessage
    /// payload on channel <see cref="Channel"/> (0xB0). Multi-byte fields are big-endian; unused tail bytes are
    /// zero and ignored. 0xB0 sits high on purpose to avoid clashing with a channel a plugin might casually
    /// pick - do not reuse it for anything else.
    /// </remarks>
    public static class SurvivalNet
    {
        /// <summary> CPE PluginMessages channel that all survival traffic rides on. </summary>
        public const byte Channel = 0xB0;

        /// <summary> Sub-protocol revision. The authoritative version is the negotiated SurvivalTest CPE
        /// extension version; this byte is reserved for finer-grained same-ext-version sub-revisions. </summary>
        public const byte ProtoVersion = 1;

        // ----- server -> client message ids (0x01 - 0x50) -----
        public const byte HELLO        = 0x01;
        public const byte WORLDINFO    = 0x02;
        public const byte HEALTH       = 0x03;
        public const byte TIME         = 0x04;
        public const byte MOB_SPAWN    = 0x10;
        public const byte MOB_MOVE     = 0x11;
        public const byte MOB_STATE    = 0x12;
        public const byte MOB_DESPAWN  = 0x13;
        public const byte INV_FULL     = 0x20;
        public const byte INV_SLOT     = 0x21;
        public const byte CONT_OPEN    = 0x22;
        public const byte CONT_SLOT    = 0x23;
        public const byte FURN_PROG    = 0x24;
        public const byte CURSOR       = 0x25;
        public const byte DROP_SPAWN   = 0x30;
        public const byte DROP_PICKUP  = 0x31;
        public const byte DROP_REMOVE  = 0x32;
        public const byte BLOCKMETA    = 0x40;
        public const byte PLAYER_EQUIP = 0x50;

        // ----- client -> server message ids (0x80 - 0x87) -----
        public const byte ATTACK       = 0x80;
        public const byte USE_ITEM     = 0x81;
        public const byte SLOT_CLICK   = 0x82;
        public const byte RESULT_CLICK = 0x83;
        public const byte CONT_CLOSE   = 0x84;
        public const byte HELD_SLOT    = 0x85;
        public const byte DROP_ITEM    = 0x86;
        public const byte RESPAWN      = 0x87;

        /// <summary> SURV_HELLO flag bits (byte 2). </summary>
        [Flags]
        public enum HelloFlags : byte
        {
            None       = 0x00,
            Enhanced   = 0x01, // bit0
            Creative   = 0x02, // bit1
            Pvp        = 0x04, // bit2
            DeathDrops = 0x08, // bit3
        }

        /// <summary> SURV_WORLDINFO flag bits (byte 5). </summary>
        [Flags]
        public enum WorldFlags : byte
        {
            None     = 0x00,
            Floating = 0x01, // bit0
        }


        /// <summary> Whether survival traffic should flow for this player on this level. </summary>
        /// <remarks> Both the per-connection capability (client negotiated SurvivalTest) and the per-map
        /// activation (level's SurvivalMode is not Off) must hold. This is the golden routing rule:
        /// never send a packet a session did not negotiate. </remarks>
        public static bool Active(Player p, Level lvl) {
            return p != null && p.Session != null && p.Session.hasSurvival
                && lvl != null && lvl.Config.SurvivalMode != SurvivalMode.Off;
        }


        // ==================== server -> client ====================

        /// <summary> Sends the per-map SURV_HELLO + SURV_WORLDINFO handshake to a player entering a survival level. </summary>
        /// <remarks> No-op for stock/Classic clients and for non-survival maps, so Classic play is entirely unaffected. </remarks>
        public static void SendHandshake(Player p, Level lvl) {
            if (!Active(p, lvl)) return;
            LevelConfig cfg = lvl.Config;

            SendHello(p, cfg);
            SendWorldInfo(p, lvl, cfg);
            Logger.Log(LogType.Debug, "survival: sent handshake to {0} for {1} (mode {2})",
                       p.name, lvl.name, cfg.SurvivalMode);
        }

        static void SendHello(Player p, LevelConfig cfg) {
            byte[] msg = new byte[Packet.PluginMessageDataLength];
            msg[0] = HELLO;
            msg[1] = (byte)cfg.SurvivalMode;
            msg[2] = (byte)HelloFlagsFor(cfg);
            msg[3] = ProtoVersion;
            SendMessage(p, msg);
        }

        // SURV_WORLDINFO carries the non-Classic world parameters. In this v1 foundation the heights are a
        // single byte each, exactly as documented in doc/survival-handshake.md 5; the fuller int16 heights
        // and the remaining .mclevel env set are deferred ( 25). Environment colours continue to be driven for
        // survival clients through the stock EnvColors CPE path (SendCurrentEnv), so they are not duplicated here.
        static void SendWorldInfo(Player p, Level lvl, LevelConfig cfg) {
            int water = EnvValue(cfg, EnvProp.EdgeLevel,   lvl.Height); // "ocean" surface elevation
            int sides = EnvValue(cfg, EnvProp.SidesOffset, lvl.Height); // bedrock offset from that (default -2)
            int ground = water + sides;

            // MCGalaxy stores the "sides" (bedrock) block as EdgeBlock and the horizon (water) block as HorizonBlock.
            BlockID sidesBlock = cfg.EdgeBlock    == Block.Invalid ? Block.Bedrock : cfg.EdgeBlock;
            BlockID fluidBlock = cfg.HorizonBlock == Block.Invalid ? Block.Water   : cfg.HorizonBlock;

            byte[] msg = new byte[Packet.PluginMessageDataLength];
            msg[0] = WORLDINFO;
            msg[1] = ClampByte(ground);
            msg[2] = ClampByte(water);
            msg[3] = RawBlock(p, fluidBlock);  // fluid the surface uses
            msg[4] = (byte)cfg.SurvivalTheme;
            msg[5] = (byte)WorldFlagsFor(cfg);
            msg[6] = RawBlock(p, sidesBlock);  // map sides ("bedrock")
            msg[7] = RawBlock(p, fluidBlock);  // horizon/edge block
            SendMessage(p, msg);
        }

        static void SendMessage(Player p, byte[] payload) {
            p.Send(Packet.PluginMessage(Channel, payload));
        }


        // ==================== client -> server ====================

        /// <summary> Handles an inbound CPE PluginMessage, dispatching survival channel traffic. </summary>
        /// <remarks> Registered on OnPluginMessageReceivedEvent; ignores every other channel. </remarks>
        public static void HandlePluginMessage(Player p, byte channel, byte[] data) {
            if (channel != Channel) return;                // not survival traffic
            if (data == null || data.Length < 1) return;   // bounds-check: never read past the payload
            byte id = data[0];

            // A negotiated capability is a capability, not a permission or a trust anchor - so every inbound
            // message is validated regardless of whether the sender advertised SurvivalTest. A client that
            // never negotiated it has no business sending here; drop it. (When appliers land they must still
            // re-validate reach/cooldown/slot/container access and correct the client authoritatively.)
            if (p.Session == null || !p.Session.hasSurvival) {
                Logger.Log(LogType.Debug, "survival: dropped 0x{0:X2} from {1} (SurvivalTest not negotiated)", id, p.name);
                return;
            }

            switch (id) {
                case ATTACK:
                case USE_ITEM:
                case SLOT_CLICK:
                case RESULT_CLICK:
                case CONT_CLOSE:
                case HELD_SLOT:
                case DROP_ITEM:
                case RESPAWN:
                    // TODO(survival): validate + apply the intent authoritatively. Deferred to the
                    // integrated-server session; for now the framing is logged so the wire can be verified.
                    Logger.Log(LogType.Debug, "survival: intent 0x{0:X2} from {1} (handler deferred)", id, p.name);
                    break;
                default:
                    Logger.Log(LogType.Debug, "survival: unexpected msg 0x{0:X2} from {1}", id, p.name);
                    break;
            }
        }


        // ==================== helpers ====================

        static HelloFlags HelloFlagsFor(LevelConfig cfg) {
            HelloFlags f = HelloFlags.None;
            if (cfg.SurvivalEnhanced)   f |= HelloFlags.Enhanced;
            if (cfg.SurvivalCreative)   f |= HelloFlags.Creative;
            if (cfg.SurvivalPvP)        f |= HelloFlags.Pvp;
            if (cfg.SurvivalDeathDrops) f |= HelloFlags.DeathDrops;
            return f;
        }

        static WorldFlags WorldFlagsFor(LevelConfig cfg) {
            WorldFlags f = WorldFlags.None;
            if (cfg.SurvivalTheme == SurvivalTheme.Floating) f |= WorldFlags.Floating;
            return f;
        }

        /// <summary> Resolves an env property to a concrete value, substituting the map default when unset. </summary>
        static int EnvValue(LevelConfig cfg, EnvProp prop, int height) {
            int v = cfg.GetEnvProp(prop);
            return v == EnvConfig.ENV_USE_DEFAULT ? EnvConfig.DefaultEnvProp(prop, height) : v;
        }

        /// <summary> Converts a server block id to the raw id the client understands, clamped to a byte. </summary>
        static byte RawBlock(Player p, BlockID block) {
            BlockID raw = p.Session.ConvertBlock(block);
            return raw > Block.CLASSIC_MAX_BLOCK ? (byte)Block.Bedrock : (byte)raw;
        }

        static byte ClampByte(int v) {
            if (v < 0)   return 0;
            if (v > 255) return 255;
            return (byte)v;
        }
    }
}
