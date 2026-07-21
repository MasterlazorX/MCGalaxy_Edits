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
using MCGalaxy.Tasks;
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

    /// <summary> What a client that has NOT negotiated SurvivalTest may do on a
    /// survival-mode map (networking-plan §16's ClassicClientPolicy). </summary>
    /// <remarks> The hard invariant behind the default: a non-survival client
    /// bypasses tools, consumption, drops and physics, so letting it place/break
    /// would corrupt the authoritative survival world. </remarks>
    public enum SurvivalVisitorPolicy : byte
    {
        Visitor = 0, // may join and look, but block changes are rejected (default)
        Allow   = 1, // may build normally (the map owner's choice to accept desync)
        Deny    = 2, // may not even join the map
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
            SendTime(p);   // seed the client with the current world time right away
            SendHealth(p); // and the current health/score
            SurvivalMobs.SendLevelMobs(p, lvl); // phase 3: the level's live mob population
            // phase 4: the server-owned inventory + cursor. NOT on creative maps -
            // there the client keeps the genuine local palette inventory (the
            // server tracks no inventory in creative: free build, no consume),
            // and streaming would wipe the palette the HELLO just filled.
            if (!cfg.SurvivalCreative) SurvivalInventory.SendAll(p);
            Logger.Log(LogType.Debug, "survival: sent handshake to {0} for {1} (mode {2})",
                       p.name, lvl.name, cfg.SurvivalMode);
        }

        /// <summary> Re-sends the handshake to every capable player on a level. Used after a live config
        /// change (e.g. the /Survival command) so it takes effect without a rejoin. If the map is no longer
        /// survival, sends a mode-off HELLO so the client leaves survival mode. </summary>
        public static void RefreshLevel(Level lvl) {
            if (lvl == null) return;
            // phase 1: the Indev block set follows the survival mode (level-scoped
            // BlockDefinitions, pushed live to CPE clients by Sync itself)
            SurvivalBlocks.Sync(lvl);
            Player[] players = PlayerInfo.Online.Items;
            foreach (Player p in players)
            {
                if (p.level != lvl || p.Session == null) continue;
                if (!p.Session.hasSurvival) {
                    // the map stopped being survival: give spectators their
                    // normal environment + entity view back
                    if (lvl.Config.SurvivalMode == SurvivalMode.Off) {
                        SurvivalFallbacks.RestoreEnv(p);
                        SurvivalFallbacks.ClearMirror(p);
                    }
                    continue;
                }
                if (lvl.Config.SurvivalMode != SurvivalMode.Off) SendHandshake(p, lvl);
                else SendHello(p, lvl.Config); // mode 0 -> client leaves survival mode
                // Hack permissions are resolved from the survival config while a survival map is
                // active (Hacks.MakeHackControl), so re-send them alongside the new HELLO flags.
                p.SendMapMotd();
            }
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
            if (p.Session.Supports(CpeExt.SurvivalTest, 2)) {
                // v2 layout: ground/water are SIGNED int16 BE - floating maps
                // genuinely use groundLevel -128 / waterLevel -127 (or -16 hell),
                // which v1's u8 fields clamped to 0 (visible as a spurious dirt
                // horizon plane under floating islands - user-diagnosed!)
                msg[1] = (byte)(ground >> 8); msg[2] = (byte)ground;
                msg[3] = (byte)(water >> 8);  msg[4] = (byte)water;
                msg[5] = RawBlock(p, fluidBlock);  // fluid the surface uses
                msg[6] = (byte)cfg.SurvivalTheme;
                msg[7] = (byte)WorldFlagsFor(cfg);
                msg[8] = RawBlock(p, sidesBlock);  // map sides ("bedrock")
                msg[9] = RawBlock(p, fluidBlock);  // horizon/edge block
            } else {
                // v1 layout (legacy clients): u8 levels, clamped
                msg[1] = ClampByte(ground);
                msg[2] = ClampByte(water);
                msg[3] = RawBlock(p, fluidBlock);  // fluid the surface uses
                msg[4] = (byte)cfg.SurvivalTheme;
                msg[5] = (byte)WorldFlagsFor(cfg);
                msg[6] = RawBlock(p, sidesBlock);  // map sides ("bedrock")
                msg[7] = RawBlock(p, fluidBlock);  // horizon/edge block
            }
            SendMessage(p, msg);
        }

        internal static void SendMessage(Player p, byte[] payload) {
            p.Send(Packet.PluginMessage(Channel, payload));
        }

        // ==================== container GUIs (rest of phase 4) ====================

        /// <summary> SURV_CONT_OPEN: [kind][slotCount]. Kinds: 0 = force-close the
        /// open container screen, 1 chest (27), 2 furnace (3), 3 large chest (54),
        /// 4 workbench (no container slots - the client opens its 3x3 grid). </summary>
        public static void SendContOpen(Player p, byte kind, byte slots) {
            byte[] msg = new byte[Packet.PluginMessageDataLength];
            msg[0] = CONT_OPEN;
            msg[1] = kind;
            msg[2] = slots;
            SendMessage(p, msg);
        }

        /// <summary> SURV_CONT_SLOT: [slot(0..53 container-relative)][id:u16][count][dmg:i16]. </summary>
        public static void SendContSlot(Player p, int slot, ushort id, byte count, short dmg) {
            byte[] msg = new byte[Packet.PluginMessageDataLength];
            msg[0] = CONT_SLOT;
            msg[1] = (byte)slot;
            msg[2] = (byte)(id >> 8); msg[3] = (byte)id;
            msg[4] = count;
            msg[5] = (byte)(dmg >> 8); msg[6] = (byte)dmg;
            SendMessage(p, msg);
        }

        /// <summary> SURV_FURN_PROG: [burn(0..12)][cook(0..24)] - the open furnace's
        /// pre-scaled flame height + arrow width. Always 0 until item smelting lands. </summary>
        public static void SendFurnProg(Player p, byte burn, byte cook) {
            byte[] msg = new byte[Packet.PluginMessageDataLength];
            msg[0] = FURN_PROG;
            msg[1] = burn;
            msg[2] = cook;
            SendMessage(p, msg);
        }


        // ==================== day / night clock (SURV_TIME) ====================
        //
        // The server owns the day/night cycle (the client must not run it locally in MP - see
        // networking-plan.md 15.2 / 17.4). A single clock is advanced on the scheduler and pushed
        // to every survival player. For v1 the clock is shared across survival maps; a per-map clock
        // is a future refinement (Indev worlds each keep their own TimeOfDay).
        //
        // SURV_TIME wire layout (v1): [id=0x04][worldTime: u16 BE][skyLight: u8]
        //   worldTime - 0 .. DAY_TICKS-1 (0 sunrise, 6000 noon, 12000 sunset, 18000 midnight)
        //   skyLight  - 0..15 standard sky light, eased across dawn/dusk

        const int DAY_TICKS       = 24000;                    // Minecraft/Indev convention: a full day
        const int TICKS_PER_TICK  = 20;                       // world ticks advanced per scheduler pass
        static readonly TimeSpan TIME_INTERVAL = TimeSpan.FromSeconds(1); // -> a 20 minute day

        static int worldTime; // read/written across threads; int access is atomic, slight staleness is fine
        static SchedulerTask timeTask;

        /// <summary> Starts the survival day/night clock. Called once from CorePlugin. </summary>
        public static void Start() {
            SurvivalBlocks.SyncLoadedLevels(); // levels loaded before our hooks registered
            SurvivalMobs.Start();
            if (timeTask != null) return;
            timeTask = Server.MainScheduler.QueueRepeat(TimeTick, null, TIME_INTERVAL);
        }

        /// <summary> Stops the survival day/night clock. </summary>
        public static void Stop() {
            SurvivalMobs.Stop();
            if (timeTask == null) return;
            Server.MainScheduler.Cancel(timeTask);
            timeTask = null;
        }

        static void TimeTick(SchedulerTask task) {
            worldTime = (worldTime + TICKS_PER_TICK) % DAY_TICKS;
            Player[] players = PlayerInfo.Online.Items;
            foreach (Player p in players)
            {
                if (Active(p, p.level)) {
                    SendTime(p);
                    TickDeathDwell(p);
                } else if (p.level != null && p.Session != null &&
                           p.level.Config.SurvivalMode != SurvivalMode.Off) {
                    // §21 fallback: non-survival clients see the day/night cycle
                    // as scaled environment colours instead of the sub-protocol
                    SurvivalFallbacks.TickEnv(p, p.level);
                }
            }
        }

        static void SendTime(Player p) {
            int time = worldTime;
            byte[] msg = new byte[Packet.PluginMessageDataLength];
            msg[0] = TIME;
            msg[1] = (byte)(time >> 8); // worldTime, big-endian u16
            msg[2] = (byte)time;
            msg[3] = SkyLight(time);
            SendMessage(p, msg);
        }

        /// <summary> Sky light right now - the mob simulation's day/night input
        /// (sunburn, darkness spawn rule, spider light-flee). </summary>
        internal static byte CurrentSkyLight() { return SkyLight(worldTime); }

        /// <summary> Current world time (0..23999; 0 sunrise, 6000 noon, 12000 sunset). </summary>
        public static int WorldTime { get { return worldTime; } }

        /// <summary> CurrentSkyLight for callers outside the assembly-internal sim. </summary>
        public static byte CurrentSkyLightPublic() { return CurrentSkyLight(); }

        /// <summary> Sets the world clock (debug / testing: forcing night to check monster
        /// spawns, sunburn, the client's celestial sky). Pushed to every survival player
        /// immediately rather than waiting for the next 1 s clock tick. </summary>
        public static void SetWorldTime(int time) {
            worldTime = ((time % DAY_TICKS) + DAY_TICKS) % DAY_TICKS;
            Player[] players = PlayerInfo.Online.Items;
            foreach (Player p in players)
            {
                if (Active(p, p.level)) SendTime(p);
            }
        }

        /// <summary> Standard 0..15 sky light for the given world time, with short dawn/dusk ramps. </summary>
        static byte SkyLight(int time) {
            const int day = 15, night = 4;
            if (time < 11000) return day;                                        // daytime
            if (time < 12000) return (byte)(day   - (day - night) * (time - 11000) / 1000); // dusk
            if (time < 23000) return night;                                      // night
            return (byte)(night + (day - night) * (time - 23000) / 1000);        // dawn
        }


        // ==================== health / respawn (SURV_HEALTH / SURV_RESPAWN) ====================
        //
        // The server owns health and score; the client renders them and sends a respawn *intent*, which the
        // server validates and answers authoritatively. Health/score live in Player.Extras so no core Player
        // field is needed and they follow the player across a /goto within one session.
        //
        // SURV_HEALTH wire layout (v1): [id=0x03][health: u8][score: i32 BE]
        //   health 0..MAX_HEALTH (Indev/Classic convention: 20 == 10 hearts)

        public const int MAX_HEALTH = 20;
        const string HEALTH_KEY = "survival.health";
        const string SCORE_KEY  = "survival.score";
        const string DWELL_KEY  = "survival.deathDwell"; // seconds left before the safety auto-respawn

        /// <summary> How long a dead player may sit on the death screen before the server revives
        /// them anyway (client gone unresponsive, intent lost, ...). Counted down by TimeTick. </summary>
        const int RESPAWN_TIMEOUT_SECS = 30;

        /// <summary> Current survival health for a player (defaults to full). </summary>
        public static int GetHealth(Player p) { return p.Extras.GetInt(HEALTH_KEY, MAX_HEALTH); }

        /// <summary> Whether this player is dead (health 0), held on the death screen awaiting
        /// their SURV_RESPAWN intent or the safety timeout. </summary>
        public static bool IsDead(Player p) { return GetHealth(p) == 0; }

        /// <summary> Whether HandleDeath must NOT auto-respawn this player: survival-test clients
        /// show a Game Over screen at 0 HP and ask to come back via SURV_RESPAWN when ready. </summary>
        public static bool HoldsDeathScreen(Player p) {
            return Active(p, p.level) && IsDead(p);
        }

        /// <summary> Sets a player's survival health (clamped) and pushes SURV_HEALTH if they're on a survival map. </summary>
        public static void SetHealth(Player p, int health) {
            if (health < 0)          health = 0;
            if (health > MAX_HEALTH) health = MAX_HEALTH;
            p.Extras[HEALTH_KEY] = health;
            if (Active(p, p.level)) SendHealth(p);
        }

        static void SendHealth(Player p) {
            int health = GetHealth(p);
            int score  = p.Extras.GetInt(SCORE_KEY, 0);
            byte[] msg = new byte[Packet.PluginMessageDataLength];
            msg[0] = HEALTH;
            msg[1] = (byte)health;
            msg[2] = (byte)(score >> 24); // score, big-endian i32
            msg[3] = (byte)(score >> 16);
            msg[4] = (byte)(score >>  8);
            msg[5] = (byte)score;
            SendMessage(p, msg);
        }

        // Client asked to respawn (SURV_RESPAWN). Only meaningful while dead on a survival map:
        // the genuine flow holds health at 0 (client shows the death camera + Game Over screen)
        // until this intent - or the safety timeout - revives them.
        static void HandleRespawn(Player p) {
            if (!Active(p, p.level)) return;
            if (!IsDead(p)) {
                // Stray/duplicate intent - correct the client authoritatively instead of applying it
                // (a respawn-while-alive would otherwise be a free teleport to spawn).
                SendHealth(p);
                Logger.Log(LogType.Debug, "survival: ignored respawn intent from {0} (not dead)", p.name);
                return;
            }
            Revive(p, "respawn intent");
        }

        /// <summary> Ends the death-screen dwell: repositions the player to spawn, then restores full
        /// health - the client removes its Game Over screen when the health rise arrives. </summary>
        static void Revive(Player p, string why) {
            p.Extras.Remove(DWELL_KEY);
            PlayerActions.Respawn(p);
            SetHealth(p, MAX_HEALTH);
            Logger.Log(LogType.Debug, "survival: {0} revived ({1})", p.name, why);
        }

        // Safety net: a dead player whose SURV_RESPAWN never arrives is revived after the timeout,
        // so nobody is stranded on the death screen forever. Runs from TimeTick (1s cadence).
        static void TickDeathDwell(Player p) {
            if (!IsDead(p)) return;
            int left = p.Extras.GetInt(DWELL_KEY, RESPAWN_TIMEOUT_SECS) - 1;
            p.Extras[DWELL_KEY] = left;
            if (left <= 0) Revive(p, "safety timeout");
        }

        // ---- graduated combat damage (phase 3: mobs hit for partial HP) ----
        //
        // Mob.hurt()'s dual-threshold invulnerability, applied to the PLAYER: while
        // the 20-tick window is fresher than its half-point only damage exceeding
        // the hit that opened it lands (and only the excess); past halfway a fresh
        // hit lands fully and re-arms the window. Counted down by TickPlayerCombat
        // (called at 20 TPS from the mob scheduler for survival players).

        const string INVINC_KEY  = "survival.invincTicks";
        const string LASTHP_KEY  = "survival.lastHitHealth";

        internal static void TickPlayerCombat(Player p) {
            int invinc = p.Extras.GetInt(INVINC_KEY, 0);
            if (invinc > 0) p.Extras[INVINC_KEY] = invinc - 1;
        }

        /// <summary> Deals graduated damage to a survival player (mob melee, explosions).
        /// Lethal damage flows into HandleDeath, so the death-screen dwell applies. </summary>
        public static void DamagePlayer(Player p, int damage, string deathMsg) {
            if (!Active(p, p.level) || IsDead(p) || damage <= 0) return;

            int invinc = p.Extras.GetInt(INVINC_KEY, 0);
            int health = GetHealth(p);
            int last   = p.Extras.GetInt(LASTHP_KEY, health);
            if (invinc > 10) {
                if (last - damage >= health) return; // absorbed by the fresh window
                health = last - damage;
            } else {
                p.Extras[LASTHP_KEY] = health;
                p.Extras[INVINC_KEY] = 20;
                health -= damage;
            }

            if (health <= 0) {
                // route through HandleDeath so the message, death count and the
                // death-screen dwell all behave exactly like any other death
                SetHealth(p, 1);
                p.HandleDeath(Block.Stone, deathMsg, false, true);
            } else {
                SetHealth(p, health); // the drop plays the client's hurt tilt/sound
            }
        }

        /// <summary> Score credit for a player-credited mob kill (c0.30 mode only). </summary>
        internal static void AddScore(Player p, int points) {
            p.Extras[SCORE_KEY] = p.Extras.GetInt(SCORE_KEY, 0) + points;
            if (Active(p, p.level)) SendHealth(p);
        }

        /// <summary>
        /// Bridges MCGalaxy's death detection (fall, drown, lava, killer blocks, weapons, /kill, ...) into
        /// the survival health flow. Registered on OnPlayerDiedEvent, which fires inside HandleDeath just
        /// before MCGalaxy would reposition the player - health is held at 0 and HandleDeath skips that
        /// auto-respawn (HoldsDeathScreen), so the client dwells on its Game Over screen until its
        /// SURV_RESPAWN intent (or the safety timeout) revives it.
        /// </summary>
        /// <remarks> Graduated Indev-style damage (partial HP from fall distance, drowning/fire ticks, ...)
        /// is a future refinement: MCGalaxy only detects lethal hazards, not partial damage. </remarks>
        public static void OnPlayerDied(Player p, BlockID cause, ref TimeSpan cooldown) {
            if (!Active(p, p.level)) return;
            SetHealth(p, 0); // SURV_HEALTH(0): death camera + Game Over screen, held until revive
            p.Extras[DWELL_KEY] = RESPAWN_TIMEOUT_SECS;
            Logger.Log(LogType.Debug, "survival: {0} died (cause block {1}), holding death screen", p.name, cause);
        }

        /// <summary> Suppresses further deaths while a player is already dead on the death screen -
        /// the hazard that killed them keeps ticking at the death spot (lava, drowning, ...).
        /// Registered on OnPlayerDyingEvent. </summary>
        public static void OnPlayerDying(Player p, BlockID cause, ref bool cancel) {
            if (HoldsDeathScreen(p)) cancel = true;
        }

        /// <summary> A map change tears down the client's per-map survival state (death screen included),
        /// so a player who leaves a level while dead is restored to full health rather than arriving
        /// on the new map at 0 HP. Registered on OnJoinedLevelEvent. </summary>
        public static void OnJoinedLevel(Player p, Level prevLevel, Level level, ref bool announce) {
            if (p.Session == null || !p.Session.hasSurvival) return;
            // any container the player had open belonged to the previous level
            SurvivalInventory.OnLeftLevel(p);
            if (!IsDead(p)) return;
            p.Extras.Remove(DWELL_KEY);
            SetHealth(p, MAX_HEALTH);
        }


        // ==================== test / debug ====================

        /// <summary> Test aid: on connect, tell the player (and the server console) whether their
        /// client was detected as a survival-test client via the CPE handshake, or as a normal client. </summary>
        /// <remarks> Called from ConnectHandler.HandleConnect. Purely diagnostic - safe to gate behind a
        /// config flag or remove once wire testing is done; it is the only place that announces detection. </remarks>
        public static void AnnounceClient(Player p) {
            if (p.Session != null && p.Session.hasSurvival) {
                p.Message("&aConnected via the survival client &S(handshake verified)");
                Logger.Log(LogType.UserActivity, "{0} connected via the survival client (SurvivalTest handshake verified)", p.name);
            } else {
                p.Message("&eConnected via a normal client &S(no survival handshake)");
                Logger.Log(LogType.UserActivity, "{0} connected via a normal client (no SurvivalTest handshake)", p.name);
            }
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
                case RESPAWN:
                    HandleRespawn(p);
                    break;
                case ATTACK:
                    // [id][targetKind(0 mob/1 player)][targetId:u16 BE] - reach and
                    // state are validated inside (a capability is not a permission)
                    if (data.Length >= 4) {
                        SurvivalMobs.HandleAttack(p, data[1], (data[2] << 8) | data[3]);
                    }
                    break;
                case HELD_SLOT:
                    // [id][hotbarIndex] - tracked for place-consume preference (phase 4)
                    if (data.Length >= 2) SurvivalInventory.HandleHeldSlot(p, data[1]);
                    break;
                case SLOT_CLICK:
                    // [id][slotIdx:u16 BE][button(0 L/1 R)] - the GuiContainer click model
                    // runs on the server's slots + cursor and echoes the result
                    if (data.Length >= 4) {
                        SurvivalInventory.HandleSlotClick(p, (data[1] << 8) | data[2], data[3]);
                    }
                    break;
                case RESULT_CLICK:
                    SurvivalInventory.HandleResultClick(p);
                    break;
                case CONT_CLOSE:
                    SurvivalInventory.HandleContClose(p);
                    break;
                case USE_ITEM:
                    // [id][heldSlot][x:i16][y:i16][z:i16 BE][face] - right-click use.
                    // v1 scope: opens container GUIs (chest/large chest/furnace/
                    // workbench); eating/tools land with the item definitions.
                    if (data.Length >= 9) {
                        SurvivalInventory.HandleUseItem(p, data[1],
                            (short)((data[2] << 8) | data[3]),
                            (short)((data[4] << 8) | data[5]),
                            (short)((data[6] << 8) | data[7]), data[8]);
                    }
                    break;
                case DROP_ITEM:
                    // TODO(survival): DROP_ITEM needs drop entities (phase 5).
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
