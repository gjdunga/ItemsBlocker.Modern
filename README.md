# ItemsBlocker.Modern
UMOD - (RUST) ItemsBlocker (modernized &amp; refactored for 2025 Rust/uMod)


What’s new (quick hits)

Scopes

all/global → blocks everyone for a duration

player <steamIdOrName> → blocks a single player for a duration

wipe → blocks everyone until next wipe (OnNewSave resets)

Admin-only commands (chat + console mirrors)

Chat:

/block <item> [duration|'wipe'] [all|player <steamIdOrName>]
examples:
/block rifle.ak 2h all
/block metal.facemask 1d player 76561198000000000
/block rocket.warhead wipe all

/unblock <item> [all|player <steamIdOrName>|wipe]

/blocklist

Console:

itemsblocker.block "rifle.ak" "2h" "all"

itemsblocker.block "metal.facemask" "1d" "player" "76561198..."

itemsblocker.unblock "rifle.ak" "all"

itemsblocker.blocklist

Time parsing: 30m, 2h, 1d, 90s, wipe, now/0 (testing)

Permissions

itemsblocker.admin → can run commands

itemsblocker.bypass → immune to blocks

Matching
Accepts shortnames (preferred) or English display names (case-insensitive). Fuzzy display-name contains is a last resort.

Blocks enforced on
CanEquipItem (weapons/tools), CanWearItem (clothing/armor), and OnReloadMagazine (ammo).

Data
Efficient JSON structure with auto-pruning on expiry; wipe-scoped flags clear on OnNewSave.

Install

Drop ItemsBlocker-Modern.cs into oxide/plugins/.

Grant yourself admin permission:

oxide.grant user <yourSteamId> itemsblocker.admin

(Optional) Grant bypass to trusted players:

oxide.grant user <steamId> itemsblocker.bypass

Reload if needed: oxide.reload ItemsBlocker
