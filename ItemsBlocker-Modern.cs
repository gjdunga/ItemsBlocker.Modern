// ItemsBlocker (modernized & refactored for 2025 Rust/uMod)
// Author: Gabriel Dungan, gjdunga@gmail.com) — based on legacy ItemsBlocker 3.1.2 by Vlad-00003 (see uMod #2407)
// License: MIT 
// Notes:
// - Unified block system that supports per-player, global (everyone), and whole-wipe scopes
// - Admin-only /block and /unblock (chat) + console commands (itemsblocker.block / itemsblocker.unblock)
// - Works with item shortnames or English display names (case-insensitive)
// - Robust time parsing: 30m, 2h, 1d, 90s, "wipe" (until next wipe), "now"/"0" (instant — mostly for testing)
// - Bypass permission to exempt certain users from all blocking
// - Minimal chat UI; no CUI overlays (kept code lean and server-performance friendly)
// - Clean data persistence; auto-prunes expired rules
// - Blocks: equipping items, wearing clothing, and loading blocked ammo into guns
//
// Example (chat):
//   /block rifle.ak 2h all                 -> block AK for everyone for 2 hours
//   /block rifle.ak wipe all               -> block AK for everyone until next wipe
//   /block metal.facemask 1d player 76561198000000000
//   /unblock rifle.ak all
//   /blocklist
//
// Example (console as admin):
//   itemsblocker.block "rifle.ak" "2h" "all"
//   itemsblocker.block "metal.facemask" "1d" "player" "76561198...."
//   itemsblocker.unblock "rifle.ak" "all"
//
// Permissions:
//   itemsblocker.admin   -> can run /block, /unblock, /blocklist
//   itemsblocker.bypass  -> player not affected by any blocks
//
// Compatibility: Oxide/uMod for Rust (tested on 2025-09 API snapshots).
// If Rust/uMod APIs change, adapt hook signatures accordingly.


using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust;
using Oxide.Game.Rust.Cui;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("ItemsBlocker", "Gabriel Dungan (modernized)", "4.0.0")]
    [Description("Block items/ammo/clothing per-player, globally, or for the whole wipe, with admin commands.")]
    public class ItemsBlocker : RustPlugin
    {
        #region Permissions & Constants

        private const string PERM_ADMIN  = "itemsblocker.admin";
        private const string PERM_BYPASS = "itemsblocker.bypass";

        private const string CHAT_PREFIX = "<color=#ff3d57>[ItemsBlocker]</color> ";

        #endregion

        #region Data Models

        // Scope of a block rule
        public enum BlockScope
        {
            Global = 0,     // affects everyone
            Player = 1,     // affects a specific playerId
            WipeGlobal = 2  // affects everyone until next wipe (does not rely on wall-clock time)
        }

        // A single block entry for an item (keyed by item shortname)
        [Serializable]
        public class BlockEntry
        {
            // Global until timestamp (UTC) — null means no active timed global block
            public DateTime? GlobalUntilUtc;

            // Wipe-global flag (true = blocked until next wipe signal; reset on wipe)
            public bool WipeGlobal;

            // Per-player blocks (until timestamp UTC)
            public Dictionary<ulong, DateTime> PerPlayerUntilUtc = new Dictionary<ulong, DateTime>();

            // Helper: prune expired entries (called periodically / on save-load)
            public void PruneExpired(DateTime nowUtc)
            {
                if (GlobalUntilUtc.HasValue && GlobalUntilUtc.Value <= nowUtc)
                    GlobalUntilUtc = null;

                var toRemove = PerPlayerUntilUtc.Where(kv => kv.Value <= nowUtc).Select(kv => kv.Key).ToList();
                foreach (var id in toRemove)
                    PerPlayerUntilUtc.Remove(id);
            }

            public bool IsEmpty()
            {
                return !GlobalUntilUtc.HasValue && !WipeGlobal && PerPlayerUntilUtc.Count == 0;
            }
        }

        // Root storage
        [Serializable]
        public class BlockData
        {
            // Map: item shortname -> BlockEntry
            public Dictionary<string, BlockEntry> Items = new Dictionary<string, BlockEntry>(StringComparer.OrdinalIgnoreCase);

            // Metadata to help diagnostics
            public DateTime LastSaveUtc = DateTime.UtcNow;

            public void Prune(DateTime nowUtc)
            {
                foreach (var kv in Items.Values)
                    kv.PruneExpired(nowUtc);

                var empty = Items.Where(kv => kv.Value.IsEmpty()).Select(kv => kv.Key).ToList();
                foreach (var key in empty)
                    Items.Remove(key);
            }
        }

        private BlockData _data;

        #endregion

        #region Configuration

        public class PluginConfig
        {
            [JsonProperty("Use chat notifications")]
            public bool UseChat = true;

            [JsonProperty("Notify when blocked attempt occurs")]
            public bool NotifyOnBlockedAttempt = true;

            [JsonProperty("Allowed name matching by display name (english) in addition to shortname")]
            public bool AllowDisplayNameMatch = true;

            [JsonProperty("Default time for /block when duration omitted (e.g., '2h', '1d', 'wipe')")]
            public string DefaultDuration = "2h";
        }

        private PluginConfig _config;

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                    throw new Exception("Config file is empty");
            }
            catch
            {
                PrintWarning("Failed to read config; creating new default config.");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Hooks

        private void Init()
        {
            permission.RegisterPermission(PERM_ADMIN, this);
            permission.RegisterPermission(PERM_BYPASS, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            // nothing additional atm
        }

        private void Unload()
        {
            SaveData();
        }

        // Reset wipe-scoped blocks on new save (wipe)
        private void OnNewSave(string filename)
        {
            int cleared = 0;
            foreach (var kv in _data.Items)
            {
                if (kv.Value.WipeGlobal)
                {
                    kv.Value.WipeGlobal = false;
                    if (kv.Value.IsEmpty()) cleared++;
                }
            }

            if (cleared > 0)
            {
                var removeKeys = _data.Items.Where(kv => kv.Value.IsEmpty()).Select(kv => kv.Key).ToList();
                foreach (var k in removeKeys) _data.Items.Remove(k);
            }

            SaveData();
            Puts($"ItemsBlocker: wipe detected → cleared wipe-global flags ({cleared} candidates).");
        }

        // Item equip blocking
        private object CanEquipItem(PlayerInventory inventory, Item item)
        {
            var player = inventory?.GetComponent<BasePlayer>();
            if (player == null || item == null)
                return null;

            if (ShouldBypass(player)) return null;

            var shortname = item.info?.shortname;
            if (string.IsNullOrEmpty(shortname)) return null;

            if (IsBlockedFor(player.userID, shortname))
            {
                NotifyBlocked(player, item.info.displayName?.english ?? shortname);
                return false;
            }
            return null;
        }

        // Clothing wear blocking
        private object CanWearItem(PlayerInventory inventory, Item item)
        {
            var player = inventory?.GetComponent<BasePlayer>();
            if (player == null || item == null)
                return null;

            if (ShouldBypass(player)) return null;

            var shortname = item.info?.shortname;
            if (string.IsNullOrEmpty(shortname)) return null;

            if (IsBlockedFor(player.userID, shortname))
            {
                NotifyBlocked(player, item.info.displayName?.english ?? shortname);
                return false;
            }
            return null;
        }

        // Ammo changing / magazine reload
        private object OnReloadMagazine(BasePlayer player, BaseProjectile projectile)
        {
            if (player == null || projectile == null) return null;
            if (ShouldBypass(player)) return null;

            var ammo = projectile.primaryMagazine?.ammoType;
            if (ammo == null) return null;

            var shortname = ammo.shortname;
            if (string.IsNullOrEmpty(shortname)) return null;

            if (IsBlockedFor(player.userID, shortname))
            {
                projectile.SendNetworkUpdateImmediate();
                NotifyBlocked(player, ammo.displayName?.english ?? shortname);
                return false;
            }
            return null;
        }

        #endregion

        #region Blocking Logic

        private bool ShouldBypass(BasePlayer player)
        {
            if (player == null) return true;
            if (player.userID <= 0 || player.IsNpc) return true; // ignore NPCs
            if (permission.UserHasPermission(player.UserIDString, PERM_BYPASS)) return true;
            return false;
        }

        private bool IsBlockedFor(ulong playerId, string itemShortname)
        {
            if (string.IsNullOrEmpty(itemShortname)) return false;

            BlockEntry entry;
            if (!_data.Items.TryGetValue(itemShortname, out entry))
                return false;

            var now = DateTime.UtcNow;

            // Global timed
            if (entry.GlobalUntilUtc.HasValue && entry.GlobalUntilUtc.Value > now)
                return true;

            // Wipe-global
            if (entry.WipeGlobal)
                return true;

            // Per-player timed
            DateTime perUntil;
            if (entry.PerPlayerUntilUtc.TryGetValue(playerId, out perUntil) && perUntil > now)
                return true;

            // Auto-prune if all expired
            entry.PruneExpired(now);
            if (entry.IsEmpty())
                _data.Items.Remove(itemShortname);
            return false;
        }

        private void NotifyBlocked(BasePlayer player, string itemLabel)
        {
            if (!_config.NotifyOnBlockedAttempt) return;
            player.ChatMessage($"{CHAT_PREFIX}The item <color=#ffd75a>{itemLabel}</color> is currently <color=#ff6a6a>blocked</color> for you.");
        }

        #endregion

        #region Admin Commands (Chat + Console)

        // Chat: /block <itemName> [durationOr'wipe'] [all|player <steamIdOrName>]
        [ChatCommand("block")]
        private void CmdBlockChat(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player)) return;
            HandleBlockCommand(player, args);
        }

        // Chat: /unblock <itemName> [all|player <steamIdOrName>]
        [ChatCommand("unblock")]
        private void CmdUnblockChat(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player)) return;
            HandleUnblockCommand(player, args);
        }

        [ChatCommand("blocklist")]
        private void CmdBlockListChat(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player)) return;
            ShowBlockList(player);
        }

        // Console mirror
        [ConsoleCommand("itemsblocker.block")]
        private void CmdBlockConsole(ConsoleSystem.Arg arg)
        {
            if (!IsConsoleAdmin(arg)) return;
            HandleBlockCommand(null, arg.Args ?? Array.Empty<string>());
        }

        [ConsoleCommand("itemsblocker.unblock")]
        private void CmdUnblockConsole(ConsoleSystem.Arg arg)
        {
            if (!IsConsoleAdmin(arg)) return;
            HandleUnblockCommand(null, arg.Args ?? Array.Empty<string>());
        }

        [ConsoleCommand("itemsblocker.blocklist")]
        private void CmdBlockListConsole(ConsoleSystem.Arg arg)
        {
            if (!IsConsoleAdmin(arg)) return;
            PrintToConsoleBlockList();
        }

        private bool IsAdmin(BasePlayer player)
        {
            if (player == null) return false;
            if (permission.UserHasPermission(player.UserIDString, PERM_ADMIN)) return true;
            player.ChatMessage($"{CHAT_PREFIX}You lack permission <color=#ddd>{PERM_ADMIN}</color>.");
            return false;
        }

        private bool IsConsoleAdmin(ConsoleSystem.Arg arg)
        {
            // server console OR player with admin perm
            if (arg.Connection == null) return true;
            var player = arg.Player();
            return player != null && permission.UserHasPermission(player.UserIDString, PERM_ADMIN);
        }

        private void HandleBlockCommand(BasePlayer caller, string[] args)
        {
            if (args.Length < 1)
            {
                Reply(caller, $"Usage: /block <itemName> [duration|'wipe'] [all|player <steamIdOrName>]");
                return;
            }

            var itemToken = args[0];
            var shortname = ResolveItemShortname(itemToken);
            if (shortname == null)
            {
                Reply(caller, $"Unknown item: <color=#ffd75a>{itemToken}</color>. Use shortname or English display name.");
                return;
            }

            // Defaults
            string durationToken = _config.DefaultDuration;
            string scopeToken = "all";
            string playerToken = null;

            if (args.Length >= 2) durationToken = args[1];
            if (args.Length >= 3) scopeToken = args[2];
            if (args.Length >= 5 && scopeToken.Equals("player", StringComparison.OrdinalIgnoreCase))
                playerToken = args[3]; // support both 3 or 4 args forms; if only 3 provided with "player" we expect next token

            // Support alternate form: /block item player <id> 2h
            if (args.Length >= 3 && args[1].Equals("player", StringComparison.OrdinalIgnoreCase))
            {
                scopeToken = "player";
                playerToken = args[2];
                if (args.Length >= 4) durationToken = args[3];
            }

            // Parse scope
            BlockScope scope;
            ulong targetId = 0;

            if (scopeToken.Equals("all", StringComparison.OrdinalIgnoreCase) || scopeToken.Equals("global", StringComparison.OrdinalIgnoreCase))
                scope = BlockScope.Global;
            else if (scopeToken.Equals("player", StringComparison.OrdinalIgnoreCase))
            {
                scope = BlockScope.Player;
                if (!TryResolvePlayerId(playerToken, out targetId))
                {
                    Reply(caller, $"Cannot resolve player: <color=#ffd75a>{playerToken}</color>. Provide SteamID or exact name.");
                    return;
                }
            }
            else if (scopeToken.Equals("wipe", StringComparison.OrdinalIgnoreCase) || durationToken.Equals("wipe", StringComparison.OrdinalIgnoreCase))
            {
                // If user writes [...] wipe all
                scope = BlockScope.WipeGlobal;
            }
            else
            {
                // If scope omitted but duration is 'wipe', treat as wipe-global
                if (durationToken.Equals("wipe", StringComparison.OrdinalIgnoreCase))
                    scope = BlockScope.WipeGlobal;
                else
                    scope = BlockScope.Global;
            }

            // Parse duration (if not wipe)
            DateTime? untilUtc = null;
            if (scope != BlockScope.WipeGlobal)
            {
                if (durationToken.Equals("wipe", StringComparison.OrdinalIgnoreCase))
                {
                    scope = BlockScope.WipeGlobal;
                }
                else if (TryParseDuration(durationToken, out var span))
                {
                    if (span <= TimeSpan.Zero)
                    {
                        Reply(caller, "Duration must be > 0. Examples: 30m, 2h, 1d, 90s, wipe");
                        return;
                    }
                    untilUtc = DateTime.UtcNow.Add(span);
                }
                else
                {
                    Reply(caller, $"Invalid duration: <color=#ffd75a>{durationToken}</color>. Examples: 30m, 2h, 1d, 90s, wipe");
                    return;
                }
            }

            // Apply
            var entry = GetOrCreate(shortname);
            switch (scope)
            {
                case BlockScope.Global:
                    entry.GlobalUntilUtc = untilUtc;
                    entry.WipeGlobal = false;
                    break;
                case BlockScope.WipeGlobal:
                    entry.WipeGlobal = true;
                    entry.GlobalUntilUtc = null;
                    break;
                case BlockScope.Player:
                    if (untilUtc == null)
                    {
                        Reply(caller, "Per-player blocks require a concrete duration (not 'wipe').");
                        return;
                    }
                    entry.PerPlayerUntilUtc[targetId] = untilUtc.Value;
                    break;
            }

            SaveData();
            var label = GetItemLabel(shortname);
            switch (scope)
            {
                case BlockScope.Global:
                    Reply(caller, $"Blocked <color=#ffd75a>{label}</color> for <color=#ddd>everyone</color> until <color=#cfd>{untilUtc:yyyy-MM-dd HH:mm} UTC</color>.");
                    break;
                case BlockScope.WipeGlobal:
                    Reply(caller, $"Blocked <color=#ffd75a>{label}</color> for <color=#ddd>everyone</color> for the <color=#cfd>entire wipe</color>.");
                    break;
                case BlockScope.Player:
                    Reply(caller, $"Blocked <color=#ffd75a>{label}</color> for player <color=#ddd>{targetId}</color> until <color=#cfd>{untilUtc:yyyy-MM-dd HH:mm} UTC</color>.");
                    break;
            }
        }

        private void HandleUnblockCommand(BasePlayer caller, string[] args)
        {
            if (args.Length < 1)
            {
                Reply(caller, "Usage: /unblock <itemName> [all|player <steamIdOrName>]");
                return;
            }

            var itemToken = args[0];
            var shortname = ResolveItemShortname(itemToken);
            if (shortname == null)
            {
                Reply(caller, $"Unknown item: <color=#ffd75a>{itemToken}</color>.");
                return;
            }

            if (!_data.Items.TryGetValue(shortname, out var entry))
            {
                Reply(caller, $"No active blocks found for <color=#ffd75a>{GetItemLabel(shortname)}</color>.");
                return;
            }

            // Defaults: all
            string scopeToken = args.Length >= 2 ? args[1] : "all";

            if (scopeToken.Equals("all", StringComparison.OrdinalIgnoreCase) || scopeToken.Equals("global", StringComparison.OrdinalIgnoreCase))
            {
                entry.GlobalUntilUtc = null;
                entry.WipeGlobal = false;
            }
            else if (scopeToken.Equals("player", StringComparison.OrdinalIgnoreCase))
            {
                string playerToken = args.Length >= 3 ? args[2] : null;
                if (!TryResolvePlayerId(playerToken, out var targetId))
                {
                    Reply(caller, $"Cannot resolve player: <color=#ffd75a>{playerToken}</color>.");
                    return;
                }
                entry.PerPlayerUntilUtc.Remove(targetId);
            }
            else if (scopeToken.Equals("wipe", StringComparison.OrdinalIgnoreCase))
            {
                entry.WipeGlobal = false;
            }
            else
            {
                Reply(caller, "Scope must be one of: all | player <idOrName> | wipe");
                return;
            }

            if (entry.IsEmpty())
                _data.Items.Remove(shortname);

            SaveData();
            Reply(caller, $"Unblocked <color=#ffd75a>{GetItemLabel(shortname)}</color> for scope <color=#ddd>{scopeToken}</color>.");
        }

        private void ShowBlockList(BasePlayer caller)
        {
            var now = DateTime.UtcNow;
            _data.Prune(now);
            SaveData();

            if (_data.Items.Count == 0)
            {
                Reply(caller, "No active item blocks.");
                return;
            }

            Reply(caller, "<color=#ffd75a>Active blocks:</color>");
            foreach (var kv in _data.Items)
            {
                var sn = kv.Key;
                var e = kv.Value;

                if (e.WipeGlobal)
                    Reply(caller, $"- {GetItemLabel(sn)}: wipe-global");

                if (e.GlobalUntilUtc.HasValue && e.GlobalUntilUtc.Value > now)
                {
                    var left = e.GlobalUntilUtc.Value - now;
                    Reply(caller, $"- {GetItemLabel(sn)}: global for {FormatSpan(left)} (until {e.GlobalUntilUtc.Value:HH:mm UTC})");
                }

                foreach (var pp in e.PerPlayerUntilUtc.ToArray())
                {
                    if (pp.Value <= now) continue;
                    var left = pp.Value - now;
                    Reply(caller, $"    · player {pp.Key}: {FormatSpan(left)} (until {pp.Value:HH:mm UTC})");
                }
            }
        }

        private void PrintToConsoleBlockList()
        {
            var now = DateTime.UtcNow;
            _data.Prune(now);
            SaveData();

            if (_data.Items.Count == 0)
            {
                Puts("No active item blocks.");
                return;
            }

            Puts("Active blocks:");
            foreach (var kv in _data.Items)
            {
                var sn = kv.Key;
                var e = kv.Value;

                if (e.WipeGlobal)
                    Puts($"- {GetItemLabel(sn)}: wipe-global");

                if (e.GlobalUntilUtc.HasValue && e.GlobalUntilUtc.Value > now)
                {
                    var left = e.GlobalUntilUtc.Value - now;
                    Puts($"- {GetItemLabel(sn)}: global for {FormatSpan(left)} (until {e.GlobalUntilUtc.Value:HH:mm UTC})");
                }

                foreach (var pp in e.PerPlayerUntilUtc.ToArray())
                {
                    if (pp.Value <= now) continue;
                    var left = pp.Value - now;
                    Puts($"    · player {pp.Key}: {FormatSpan(left)} (until {pp.Value:HH:mm UTC})");
                }
            }
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player != null) player.ChatMessage($"{CHAT_PREFIX}{message}");
            else Puts(StripTags(message));
        }

        private string StripTags(string s) => s?.Replace("<color=#ff3d57>", "").Replace("</color>", "")
                                              .Replace("<color=#ffd75a>", "").Replace("<color=#ddd>", "")
                                              .Replace("<color=#cfd>", "");

        #endregion

        #region Helpers: Items, Players, Time

        private BlockEntry GetOrCreate(string shortname)
        {
            if (!_data.Items.TryGetValue(shortname, out var entry))
            {
                entry = new BlockEntry();
                _data.Items[shortname] = entry;
            }
            return entry;
        }

        // Resolve input token -> item shortname
        private string ResolveItemShortname(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;

            // First, try exact shortname
            var def = ItemManager.FindItemDefinition(token);
            if (def != null)
                return def.shortname;

            // Allow quoted names with spaces
            token = token.Trim('"');

            // Optionally try match by english display name (case-insensitive)
            if (_config.AllowDisplayNameMatch)
            {
                var match = ItemManager.itemList?.FirstOrDefault(x =>
                    string.Equals(x.displayName?.english, token, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                    return match.shortname;
            }

            // Fuzzy contains on display name (last resort)
            var fuzzy = ItemManager.itemList?.FirstOrDefault(x =>
                x.displayName?.english != null &&
                x.displayName.english.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);

            return fuzzy?.shortname;
        }

        private string GetItemLabel(string shortname)
        {
            var def = ItemManager.FindItemDefinition(shortname);
            return def?.displayName?.english ?? shortname;
        }

        private bool TryResolvePlayerId(string token, out ulong id)
        {
            id = 0;
            if (string.IsNullOrEmpty(token)) return false;

            // SteamID numeric
            if (ulong.TryParse(token, out id)) return true;

            // Try find online by name
            var ply = BasePlayer.activePlayerList
                .FirstOrDefault(p => string.Equals(p.displayName, token, StringComparison.OrdinalIgnoreCase));

            if (ply != null)
            {
                id = ply.userID;
                return true;
            }
            return false;
        }

        private bool TryParseDuration(string token, out TimeSpan span)
        {
            span = TimeSpan.Zero;
            if (string.IsNullOrEmpty(token)) return false;

            token = token.Trim().ToLowerInvariant();

            if (token == "0" || token == "now")
            {
                span = TimeSpan.Zero;
                return true;
            }

            // Simple suffix parsing: s, m, h, d
            // Supports compound like "90m", "2h", "1d", "45s"
            double value;
            if (token.EndsWith("ms"))
            {
                if (double.TryParse(token.Substring(0, token.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    span = TimeSpan.FromMilliseconds(value);
                    return true;
                }
            }
            else if (token.EndsWith("s"))
            {
                if (double.TryParse(token.Substring(0, token.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    span = TimeSpan.FromSeconds(value);
                    return true;
                }
            }
            else if (token.EndsWith("m"))
            {
                if (double.TryParse(token.Substring(0, token.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    span = TimeSpan.FromMinutes(value);
                    return true;
                }
            }
            else if (token.EndsWith("h"))
            {
                if (double.TryParse(token.Substring(0, token.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    span = TimeSpan.FromHours(value);
                    return true;
                }
            }
            else if (token.EndsWith("d"))
            {
                if (double.TryParse(token.Substring(0, token.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    span = TimeSpan.FromDays(value);
                    return true;
                }
            }
            else
            {
                // Try plain minutes if numeric
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    span = TimeSpan.FromMinutes(value);
                    return true;
                }
            }
            return false;
        }

        private string FormatSpan(TimeSpan ts)
        {
            if (ts.TotalDays >= 1)
                return $"{(int)ts.TotalDays}d {ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
            return $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
        }

        #endregion

        #region Data IO

        private const string DATA_FILE = "ItemsBlocker.modern";

        private void LoadData()
        {
            try
            {
                _data = Interface.Oxide.DataFileSystem.ReadObject<BlockData>(DATA_FILE) ?? new BlockData();
            }
            catch
            {
                _data = new BlockData();
            }
            _data.Prune(DateTime.UtcNow);
            SaveData(); // persist any cleanup
        }

        private void SaveData()
        {
            _data.LastSaveUtc = DateTime.UtcNow;
            Interface.Oxide.DataFileSystem.WriteObject(DATA_FILE, _data, true);
        }

        #endregion
    }
}
