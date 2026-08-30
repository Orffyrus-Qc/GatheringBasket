// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Gathering Basket", "Orffyrus", "1.0.0")]
    [Description("Adds a virtual 12-slot gathering basket with per-item auto-routing and crafting integration.")]
    public class GatheringBasket : RustPlugin
    {
        private const string UsePermission = "gatheringbasket.use";
        private const string AdminPermission = "gatheringbasket.admin";
        private const string VipPermission = "gatheringbasket.vip";
        private const string ResetPermission = "gatheringbasket.reset";
        private const string ButtonUi = "GB.Button";
        private const string RoutingUi = "GB.Routing";
        private const string DataFileName = "GatheringBasket";
        private const string LootPanel = "generic_resizable";
        private const string StoragePrefab = "assets/prefabs/misc/item drop/item_drop.prefab";
        private const string BasketLootName = "Gathering Basket";
        private const int DataVersion = 1;

        [PluginReference]
        private Plugin ItemRetriever;

        private PluginConfig _config;
        private StoredData _data;
        private readonly Dictionary<ulong, BasketState> _states = new Dictionary<ulong, BasketState>();
        private readonly Dictionary<NetworkableId, ulong> _entities = new Dictionary<NetworkableId, ulong>();
        private readonly HashSet<ulong> _busy = new HashSet<ulong>();
        private readonly HashSet<ulong> _stackHooked = new HashSet<ulong>();
        private readonly Dictionary<ulong, OwnedMoveFrame> _ownedMoves = new Dictionary<ulong, OwnedMoveFrame>();
        private readonly HashSet<ulong> _dirty = new HashSet<ulong>();
        private Timer _autoSaveTimer;
        private string _buttonIconPng;

        #region Types

        private enum RouteMode
        {
            Inventory,
            Basket
        }

        private class BasketState
        {
            public ulong UserId;
            public DroppedItemContainer Entity;
            public ItemContainer Container;
            public bool Viewing;
            public PlayerData Data = new PlayerData();
        }

        private class OwnedMoveFrame
        {
            public int Frame;
            public readonly HashSet<int> ItemIds = new HashSet<int>();
        }

        private class PluginConfig
        {
            [JsonProperty("BasketSlots")]
            public int BasketSlots = 12;

            [JsonProperty("VipBasketSlots")]
            public int VipBasketSlots = 18;

            [JsonProperty("EnableAutoRouting")]
            public bool EnableAutoRouting = true;

            [JsonProperty("EnableCraftingIntegration")]
            public bool EnableCraftingIntegration = true;

            [JsonProperty("CraftingPriority")]
            public string CraftingPriority = "BasketFirst";

            [JsonProperty("KeepBasketOnDeath")]
            public bool KeepBasketOnDeath = true;

            [JsonProperty("SaveRoutingPreferences")]
            public bool SaveRoutingPreferences = true;

            [JsonProperty("AutoSaveInterval")]
            public int AutoSaveInterval = 300;

            [JsonProperty("AllowPlayerReset")]
            public bool AllowPlayerReset = false;

            [JsonProperty("GrantUseToDefaultGroup")]
            public bool? GrantUseToDefaultGroup = true;

            [JsonProperty("ResetContentsOnMapWipe")]
            public bool ResetContentsOnMapWipe = true;

            [JsonProperty("ResetRoutingOnMapWipe")]
            public bool ResetRoutingOnMapWipe = true;

            [JsonProperty("ShowInventoryButton")]
            public bool ShowInventoryButton = true;

            [JsonProperty("ButtonText")]
            public string ButtonText = "BASKET";

            [JsonProperty("ButtonAnchorMin")]
            public string ButtonAnchorMin = "1 0";

            [JsonProperty("ButtonAnchorMax")]
            public string ButtonAnchorMax = "1 0";

            [JsonProperty("ButtonOffsetMin")]
            public string ButtonOffsetMin = "-447 18";

            [JsonProperty("ButtonOffsetMax")]
            public string ButtonOffsetMax = "-335 54";

            [JsonProperty("RoutingAnchorMin")]
            public string RoutingAnchorMin = "0.5 0";

            [JsonProperty("RoutingAnchorMax")]
            public string RoutingAnchorMax = "0.5 0";

            [JsonProperty("RoutingOffsetLeft")]
            public float RoutingOffsetLeft = 200f;

            [JsonProperty("RoutingOffsetRight")]
            public float RoutingOffsetRight = 580f;

            [JsonProperty("RoutingOffsetBottom")]
            public float RoutingOffsetBottom = 300f;

            [JsonProperty("AllowedCategories", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> AllowedCategories = new List<string> { "Food" };

            [JsonProperty("AllowedShortnames", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> AllowedShortnames = new List<string>
            {
                "cloth",
                "plantfiber",
                "worm",
                "grub",
                "honey",
                "compost"
            };

            [JsonProperty("AllowedShortnamePrefixes", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> AllowedShortnamePrefixes = new List<string>
            {
                "seed.",
                "clone."
            };

            [JsonProperty("AllowedShortnameSuffixes", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> AllowedShortnameSuffixes = new List<string>
            {
                ".berry"
            };

            [JsonProperty("BlockedShortnames", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> BlockedShortnames = new List<string>();

            [JsonIgnore]
            public bool BasketCraftFirst =>
                string.Equals(CraftingPriority, "BasketFirst", StringComparison.OrdinalIgnoreCase);

            public void EnsureDefaults()
            {
                if (BasketSlots < 1)
                    BasketSlots = 12;
                if (BasketSlots > 30)
                    BasketSlots = 30;
                if (VipBasketSlots < BasketSlots)
                    VipBasketSlots = BasketSlots;
                if (VipBasketSlots > 30)
                    VipBasketSlots = 30;
                if (AutoSaveInterval < 30)
                    AutoSaveInterval = 30;
                if (AllowedCategories == null)
                    AllowedCategories = new List<string> { "Food" };
                if (AllowedShortnames == null)
                    AllowedShortnames = new List<string>();
                if (AllowedShortnamePrefixes == null)
                    AllowedShortnamePrefixes = new List<string>();
                if (AllowedShortnameSuffixes == null)
                    AllowedShortnameSuffixes = new List<string>();
                if (BlockedShortnames == null)
                    BlockedShortnames = new List<string>();
                if (string.IsNullOrEmpty(CraftingPriority))
                    CraftingPriority = "BasketFirst";
                if (string.IsNullOrEmpty(ButtonText))
                    ButtonText = "BASKET";
                if (string.IsNullOrEmpty(ButtonAnchorMin))
                    ButtonAnchorMin = "1 0";
                if (string.IsNullOrEmpty(ButtonAnchorMax))
                    ButtonAnchorMax = "1 0";
                if (string.IsNullOrEmpty(ButtonOffsetMin))
                    ButtonOffsetMin = "-447 18";
                if (string.IsNullOrEmpty(ButtonOffsetMax))
                    ButtonOffsetMax = "-335 54";
                if (string.IsNullOrEmpty(RoutingAnchorMin))
                    RoutingAnchorMin = "0.5 0";
                if (string.IsNullOrEmpty(RoutingAnchorMax))
                    RoutingAnchorMax = "0.5 0";
                if (RoutingOffsetRight <= RoutingOffsetLeft)
                {
                    RoutingOffsetLeft = 200f;
                    RoutingOffsetRight = 580f;
                }
                if (RoutingOffsetBottom < 50f)
                    RoutingOffsetBottom = 300f;
                if (!GrantUseToDefaultGroup.HasValue)
                    GrantUseToDefaultGroup = true;
            }
        }

        private class StoredData
        {
            public int DataVersion = 1;
            public Dictionary<string, PlayerData> Players = new Dictionary<string, PlayerData>();
        }

        private class PlayerData
        {
            public List<StoredItem> Items = new List<StoredItem>();
            public Dictionary<string, string> Routing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private class StoredItem
        {
            public string Shortname;
            public int ItemId;
            public int Amount;
            public ulong Skin;
            public int Slot = -1;
            public float Condition;
            public float MaxCondition;
            public bool HasCondition;
            public int Flags;
            public string Text;
            public int DataInt;
            public float DataFloat;
            public int BlueprintAmount;
            public int BlueprintTarget;
            public bool HasInstanceData;
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(UsePermission, this);
            permission.RegisterPermission(AdminPermission, this);
            permission.RegisterPermission(VipPermission, this);
            permission.RegisterPermission(ResetPermission, this);
            GrantDefaultUsePermission();
            LoadData();
        }

        private void GrantDefaultUsePermission()
        {
            if (_config != null && _config.GrantUseToDefaultGroup == false)
                return;

            if (!permission.GroupHasPermission("default", UsePermission))
                permission.GrantGroupPermission("default", UsePermission, this);
        }

        private void OnServerInitialized()
        {
            GrantDefaultUsePermission();
            RegisterButtonIcon();

            foreach (BasePlayer player in BasePlayer.activePlayerList)
                SetupPlayer(player);

            if (_config.AutoSaveInterval > 0)
                _autoSaveTimer = timer.Every(_config.AutoSaveInterval, SaveDirty);
        }

        private void Unload()
        {
            _autoSaveTimer?.Destroy();

            foreach (BasePlayer player in BasePlayer.activePlayerList)
                DestroyUi(player);

            foreach (BasketState state in _states.Values.ToList())
            {
                CaptureItems(state);
                KillHost(state, false);
            }

            SaveData();
            _states.Clear();
            _entities.Clear();
            _busy.Clear();
            _stackHooked.Clear();
        }

        private void OnServerSave()
        {
            SaveDirty();
        }

        private void OnNewSave(string filename)
        {
            if (_config.ResetContentsOnMapWipe || _config.ResetRoutingOnMapWipe)
            {
                foreach (PlayerData playerData in _data.Players.Values)
                {
                    if (_config.ResetContentsOnMapWipe)
                        playerData.Items.Clear();
                    if (_config.ResetRoutingOnMapWipe)
                        playerData.Routing.Clear();
                }

                foreach (BasketState state in _states.Values)
                {
                    if (_config.ResetContentsOnMapWipe)
                    {
                        state.Data.Items.Clear();
                        state.Container?.Clear();
                    }

                    if (_config.ResetRoutingOnMapWipe)
                        state.Data.Routing.Clear();
                }

                SaveData();
                Puts("Map wipe detected. Gathering Basket data was reset according to configuration.");
            }
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(MessagesEn, this);
            lang.RegisterMessages(MessagesFr, this, "fr");
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                    throw new JsonException("Configuration was empty.");
                _config.EnsureDefaults();
            }
            catch (Exception ex)
            {
                PrintWarning("Could not read the configuration; defaults were loaded. " + ex.Message);
                _config = new PluginConfig();
                _config.EnsureDefaults();
            }

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            _config.EnsureDefaults();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        #endregion

        #region Commands

        [ChatCommand("basket")]
        private void BasketChatCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (args != null && args.Length > 0 && args[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                TryResetOwnBasket(player);
                return;
            }

            ToggleBasket(player);
        }

        [ConsoleCommand("basket")]
        private void BasketConsoleCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (arg.HasArgs() && arg.GetString(0).Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                TryResetOwnBasket(player);
                return;
            }

            ToggleBasket(player);
        }

        [ChatCommand("basketadmin")]
        private void BasketAdminChatCommand(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player))
            {
                Reply(player, "NoPermission");
                return;
            }

            HandleAdminArgs(player, args);
        }

        [ConsoleCommand("basketadmin")]
        private void BasketAdminConsoleCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player != null && !IsAdmin(player))
            {
                Reply(player, "NoPermission");
                return;
            }

            string[] args = arg.Args == null ? new string[0] : arg.Args.Select(value => value.ToString()).ToArray();
            if (player == null)
            {
                Puts(HandleAdminArgs(null, args) ?? string.Empty);
                return;
            }

            HandleAdminArgs(player, args);
        }

        [ConsoleCommand("gb.ui")]
        private void BasketUiCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !CanUse(player))
                return;

            string action = arg.GetString(0, string.Empty).ToLowerInvariant();
            if (action == "open")
            {
                ToggleBasket(player);
                return;
            }

            if (action == "route")
            {
                string shortname = arg.GetString(1, string.Empty);
                string mode = arg.GetString(2, string.Empty);
                SetRoute(player, shortname, mode);
            }
        }

        private string HandleAdminArgs(BasePlayer admin, string[] args)
        {
            if (args == null || args.Length < 2)
            {
                Reply(admin, "AdminHelp");
                return "basketadmin <view|clear|resetroute> <player>";
            }

            string verb = args[0].ToLowerInvariant();
            BasePlayer target = FindPlayer(string.Join(" ", args.Skip(1).ToArray()));
            if (target == null)
            {
                Reply(admin, "PlayerNotFound");
                return "Player not found.";
            }

            BasketState state = GetOrCreateState(target);
            EnsureHost(target, state);

            if (verb == "view")
            {
                if (admin == null)
                    return "view requires an in-game admin.";
                OpenBasket(admin, state);
                Reply(admin, "AdminView", target.displayName);
                return null;
            }

            if (verb == "clear")
            {
                state.Container?.Clear();
                state.Data.Items.Clear();
                MarkDirty(state.UserId);
                SavePlayer(state);
                Reply(admin, "AdminCleared", target.displayName);
                return $"Cleared basket for {target.displayName}";
            }

            if (verb == "resetroute")
            {
                state.Data.Routing.Clear();
                MarkDirty(state.UserId);
                SavePlayer(state);
                if (state.Viewing && admin != null)
                    DrawRoutingUi(admin, state);
                Reply(admin, "AdminRouteReset", target.displayName);
                return $"Reset routing for {target.displayName}";
            }

            Reply(admin, "AdminHelp");
            return "basketadmin <view|clear|resetroute> <player>";
        }

        private void TryResetOwnBasket(BasePlayer player)
        {
            if (!CanUse(player))
            {
                Reply(player, "NoPermission");
                return;
            }

            if (!_config.AllowPlayerReset && !permission.UserHasPermission(player.UserIDString, ResetPermission) && !IsAdmin(player))
            {
                Reply(player, "ResetDenied");
                return;
            }

            BasketState state = GetOrCreateState(player);
            EnsureHost(player, state);
            state.Container?.Clear();
            state.Data.Items.Clear();
            MarkDirty(state.UserId);
            SavePlayer(state);
            Reply(player, "ResetDone");
        }

        #endregion

        #region Player lifecycle

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null)
                return;

            timer.Once(0.4f, () =>
            {
                if (player != null && player.IsConnected)
                    SetupPlayer(player);
            });
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (player == null)
                return;

            ulong userId = player.userID;
            BasketState state;
            if (_states.TryGetValue(userId, out state))
            {
                CaptureItems(state);
                SavePlayer(state);
                KillHost(state, false);
                _states.Remove(userId);
            }

            _stackHooked.Remove(userId);
            _busy.Remove(userId);
            _ownedMoves.Remove(userId);
            DestroyUi(player);
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            SetupPlayer(player);
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            SetupPlayer(player);
            ShowButton(player);
        }

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null)
                return;

            BasketState state;
            if (_states.TryGetValue(player.userID, out state))
            {
                CloseBasket(player);
                CaptureItems(state);
                SavePlayer(state);

                if (!_config.KeepBasketOnDeath)
                {
                    state.Container?.Clear();
                    state.Data.Items.Clear();
                    MarkDirty(state.UserId);
                    SavePlayer(state);
                }
            }

            DestroyUi(player);
        }

        private void SetupPlayer(BasePlayer player)
        {
            if (player == null || !player.IsConnected || !CanUse(player))
                return;

            BasketState state = GetOrCreateState(player);
            EnsureHost(player, state);
            HookPlayerInventory(player);
            AttachCrafting(player, state);
            ShowButton(player);
        }

        #endregion

        #region Container host

        private BasketState GetOrCreateState(BasePlayer player)
        {
            ulong userId = player.userID;
            BasketState state;
            if (_states.TryGetValue(userId, out state))
            {
                if (state.Data == null)
                    state.Data = LoadPlayerData(userId);
                return state;
            }

            state = new BasketState
            {
                UserId = userId,
                Data = LoadPlayerData(userId)
            };
            _states[userId] = state;
            return state;
        }

        private PlayerData LoadPlayerData(ulong userId)
        {
            PlayerData data;
            if (_data.Players.TryGetValue(userId.ToString(CultureInfo.InvariantCulture), out data) && data != null)
            {
                if (data.Items == null)
                    data.Items = new List<StoredItem>();
                if (data.Routing == null)
                    data.Routing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return data;
            }

            data = new PlayerData();
            _data.Players[userId.ToString(CultureInfo.InvariantCulture)] = data;
            return data;
        }

        private int GetSlotCount(BasePlayer player)
        {
            if (player != null && permission.UserHasPermission(player.UserIDString, VipPermission))
                return _config.VipBasketSlots;
            return _config.BasketSlots;
        }

        private void EnsureHost(BasePlayer player, BasketState state)
        {
            if (state.Entity != null && !state.Entity.IsDestroyed && state.Container != null)
            {
                state.Container.capacity = GetSlotCount(player);
                state.Container.SetFlag(ItemContainer.Flag.NoItemInput, false);
                state.Container.SetFlag(ItemContainer.Flag.IsLocked, false);
                state.Entity.maxItemCount = GetSlotCount(player);
                state.Entity.CancelInvoke(state.Entity.RemoveMe);
                ApplyBasketIdentity(state.Entity);
                HideHostVisuals(state.Entity);
                AlignHost(player, state.Entity);
                AttachCrafting(player, state);
                return;
            }

            DroppedItemContainer entity = GameManager.server.CreateEntity(StoragePrefab, GetHiddenPosition(player)) as DroppedItemContainer;
            if (entity == null)
            {
                PrintError("Failed to create Gathering Basket host entity.");
                return;
            }

            entity.enableSaving = false;
            entity.syncPosition = false;
            entity.OwnerID = player.userID;
            entity.maxItemCount = GetSlotCount(player);
            entity.ItemBasedDespawn = false;
            entity.onlyOwnerLoot = false;
            ApplyBasketIdentity(entity);
            entity.Spawn();
            entity.CancelInvoke(entity.RemoveMe);
            ApplyBasketIdentity(entity);
            HideHostVisuals(entity);

            if (entity.inventory == null)
                entity.inventory = entity.CreateContainer();

            entity.inventory.capacity = GetSlotCount(player);
            entity.inventory.entityOwner = entity;
            entity.inventory.playerOwner = player;
            entity.inventory.allowedContents = ItemContainer.ContentsType.Generic;
            entity.inventory.SetFlag(ItemContainer.Flag.NoItemInput, false);
            entity.inventory.SetFlag(ItemContainer.Flag.IsLocked, false);
            entity.inventory.canAcceptItem = (item, slot) => CanBasketAccept(item);
            entity.inventory.onDirty += () => MarkDirty(state.UserId);
            entity.inventory.onItemAddedRemoved += (item, added) => OnBasketItemChanged(player, state);

            RestoreItems(entity.inventory, state.Data.Items);

            state.Entity = entity;
            state.Container = entity.inventory;
            if (entity.net != null)
                _entities[entity.net.ID] = state.UserId;

            AlignHost(player, entity);
            AttachCrafting(player, state);
        }

        private static void ApplyBasketIdentity(DroppedItemContainer entity)
        {
            if (entity == null)
                return;

            entity.lootPanelName = LootPanel;
            entity.playerName = BasketLootName;
            entity.playerSteamID = 0UL;
            entity._name = BasketLootName;
            entity.SendNetworkUpdate();
        }

        private static void HideHostVisuals(DroppedItemContainer entity)
        {
            if (entity == null)
                return;

            foreach (Renderer renderer in entity.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = false;

            foreach (Collider collider in entity.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;

            Rigidbody body = entity.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.isKinematic = true;
                body.detectCollisions = false;
            }
        }

        private void AlignHost(BasePlayer player, DroppedItemContainer entity)
        {
            if (player == null || entity == null || entity.IsDestroyed)
                return;

            Vector3 pos = player.ServerPosition;
            pos.y -= 2f;
            entity.transform.position = pos;
            entity.UpdateNetworkGroup();
            entity.SendNetworkUpdateImmediate();
        }

        private static Vector3 GetHiddenPosition(BasePlayer player)
        {
            Vector3 pos = player != null ? player.ServerPosition : Vector3.zero;
            pos.y -= 2f;
            return pos;
        }

        private void KillHost(BasketState state, bool capture)
        {
            if (state == null)
                return;

            if (capture)
                CaptureItems(state);

            DroppedItemContainer entity = state.Entity;
            state.Entity = null;
            state.Container = null;
            state.Viewing = false;

            if (entity == null || entity.IsDestroyed)
                return;

            if (entity.net != null)
                _entities.Remove(entity.net.ID);

            entity.CancelInvoke(entity.RemoveMe);
            entity.inventory?.Clear();
            entity.Kill(BaseNetworkable.DestroyMode.None);
        }

        private static void DestroyComponent<T>(BaseEntity entity) where T : Component
        {
            T component = entity.GetComponent<T>();
            if (component != null)
                UnityEngine.Object.Destroy(component);
        }

        private bool IsOurEntity(BaseEntity entity)
        {
            return entity != null && entity.net != null && _entities.ContainsKey(entity.net.ID);
        }

        private BasketState FindStateByContainer(ItemContainer container)
        {
            if (container == null)
                return null;

            foreach (BasketState state in _states.Values)
            {
                if (state.Container == container)
                    return state;
            }

            return null;
        }

        private void OnBasketItemChanged(BasePlayer player, BasketState state)
        {
            MarkDirty(state.UserId);
            if (player != null && state.Viewing)
                DrawRoutingUi(player, state);
        }

        #endregion

        #region Open / close

        private void ToggleBasket(BasePlayer player)
        {
            if (!CanUse(player))
            {
                Reply(player, "NoPermission");
                return;
            }

            BasketState state = GetOrCreateState(player);
            if (state.Viewing && player.inventory?.loot != null && player.inventory.loot.IsLooting(state.Container))
            {
                CloseBasket(player);
                return;
            }

            OpenBasket(player, state);
        }

        private void OpenBasket(BasePlayer player, BasketState state)
        {
            if (player == null || state == null)
                return;

            BasePlayer owner = BasePlayer.FindByID(state.UserId) ?? player;
            EnsureHost(owner, state);
            if (state.Entity == null || state.Container == null)
                return;

            ApplyBasketIdentity(state.Entity);
            AlignHost(player, state.Entity);

            player.EndLooting();
            state.Entity.PlayerOpenLoot(player);
            if (player.inventory?.loot != null)
                player.inventory.loot.PositionChecks = false;

            if (player.inventory?.loot == null || !player.inventory.loot.IsLooting(state.Container))
            {
                player.inventory.loot.Clear();
                player.inventory.loot.PositionChecks = false;
                player.inventory.loot.entitySource = state.Entity;
                player.inventory.loot.itemSource = null;
                player.inventory.loot.AddContainer(state.Container);
                player.inventory.loot.SendImmediate();
                player.ClientRPC(RpcTarget.Player("RPC_OpenLootPanel", player), LootPanel);
            }

            state.Viewing = true;
            DrawRoutingUi(player, state);
        }

        private void CloseBasket(BasePlayer player)
        {
            if (player == null)
                return;

            BasketState state;
            if (_states.TryGetValue(player.userID, out state) && state.Viewing)
            {
                CaptureItems(state);
                MarkDirty(state.UserId);
                state.Viewing = false;
            }

            if (player.inventory?.loot != null && player.inventory.loot.IsLooting())
            {
                if (state != null && (player.inventory.loot.entitySource == state.Entity || player.inventory.loot.IsLooting(state.Container)))
                    player.EndLooting();
            }

            CuiHelper.DestroyUi(player, RoutingUi);
        }

        #endregion

        #region Allowed items

        private bool CanBasketAccept(Item item)
        {
            return item != null && item.info != null && IsAllowedItem(item.info);
        }

        private bool IsAllowedItem(ItemDefinition def)
        {
            if (def == null)
                return false;

            string shortname = def.shortname;
            if (string.IsNullOrEmpty(shortname))
                return false;

            if (_config.BlockedShortnames.Contains(shortname, StringComparer.OrdinalIgnoreCase))
                return false;

            if (_config.AllowedShortnames.Contains(shortname, StringComparer.OrdinalIgnoreCase))
                return true;

            for (int i = 0; i < _config.AllowedShortnamePrefixes.Count; i++)
            {
                string prefix = _config.AllowedShortnamePrefixes[i];
                if (!string.IsNullOrEmpty(prefix) && shortname.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            for (int i = 0; i < _config.AllowedShortnameSuffixes.Count; i++)
            {
                string suffix = _config.AllowedShortnameSuffixes[i];
                if (!string.IsNullOrEmpty(suffix) && shortname.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            string category = def.category.ToString();
            return _config.AllowedCategories.Contains(category, StringComparer.OrdinalIgnoreCase);
        }

        #endregion

        #region Routing

        private bool ShouldAutoRoute(BasePlayer player, ItemDefinition def)
        {
            if (!_config.EnableAutoRouting || player == null || def == null || !CanUse(player))
                return false;
            if (!IsAllowedItem(def))
                return false;

            BasketState state;
            if (!_states.TryGetValue(player.userID, out state))
                return false;

            string mode;
            if (!state.Data.Routing.TryGetValue(def.shortname, out mode))
                return false;

            return mode.Equals("Basket", StringComparison.OrdinalIgnoreCase);
        }

        private void SetRoute(BasePlayer player, string shortname, string mode)
        {
            if (string.IsNullOrEmpty(shortname) || !_config.EnableAutoRouting)
                return;

            BasketState state = GetOrCreateState(player);
            bool basket = mode.Equals("Basket", StringComparison.OrdinalIgnoreCase);
            state.Data.Routing[shortname] = basket ? "Basket" : "Inventory";
            MarkDirty(state.UserId);
            if (state.Viewing)
                DrawRoutingUi(player, state);

            Reply(player, basket ? "RouteBasket" : "RouteInventory", shortname);
        }

        private bool TryRouteItem(BasePlayer player, Item item)
        {
            if (player == null || item == null || item.info == null)
                return false;
            if (_busy.Contains(player.userID))
                return false;
            if (!ShouldAutoRoute(player, item.info))
                return false;

            BasketState state = GetOrCreateState(player);
            EnsureHost(player, state);
            if (state.Container == null)
                return false;

            _busy.Add(player.userID);
            try
            {
                return item.MoveToContainer(state.Container, -1, true);
            }
            finally
            {
                _busy.Remove(player.userID);
            }
        }

        private void RouteFromOwnedStack(BasePlayer player, ItemContainer container, Item item, int amount)
        {
            if (player == null || item == null || amount <= 0)
                return;
            if (_busy.Contains(player.userID) || !ShouldAutoRoute(player, item.info))
                return;
            if (WasOwnedMove(player, item.info.itemid))
                return;

            Item captured = item;
            ItemContainer capturedContainer = container;
            int capturedAmount = amount;
            NextTick(() =>
            {
                if (captured == null || captured.parent != capturedContainer || captured.amount <= 0)
                    return;
                if (!ShouldAutoRoute(player, captured.info))
                    return;

                BasketState state = GetOrCreateState(player);
                EnsureHost(player, state);
                if (state.Container == null)
                    return;

                int toMove = Math.Min(capturedAmount, captured.amount);
                if (toMove <= 0)
                    return;

                _busy.Add(player.userID);
                try
                {
                    Item moving = toMove >= captured.amount ? captured : captured.SplitItem(toMove);
                    if (moving == null)
                        return;
                    if (!moving.MoveToContainer(state.Container, -1, true))
                        moving.MoveToContainer(capturedContainer, -1, true);
                }
                finally
                {
                    _busy.Remove(player.userID);
                }
            });
        }

        private void MarkOwnedMove(BasePlayer player, int itemId)
        {
            if (player == null)
                return;

            OwnedMoveFrame frame;
            if (!_ownedMoves.TryGetValue(player.userID, out frame) || frame.Frame != Time.frameCount)
            {
                frame = new OwnedMoveFrame { Frame = Time.frameCount };
                _ownedMoves[player.userID] = frame;
            }

            frame.ItemIds.Add(itemId);
        }

        private bool WasOwnedMove(BasePlayer player, int itemId)
        {
            OwnedMoveFrame frame;
            return _ownedMoves.TryGetValue(player.userID, out frame)
                   && frame.Frame == Time.frameCount
                   && frame.ItemIds.Contains(itemId);
        }

        private bool IsPlayerOwnedContainer(BasePlayer player, ItemContainer container)
        {
            if (player?.inventory == null || container == null)
                return false;
            if (container == player.inventory.containerMain || container == player.inventory.containerBelt || container == player.inventory.containerWear)
                return true;

            BasketState state;
            return _states.TryGetValue(player.userID, out state) && state.Container == container;
        }

        private void HookPlayerInventory(BasePlayer player)
        {
            if (player?.inventory?.containerMain == null || player.inventory.containerBelt == null)
                return;
            if (!_stackHooked.Add(player.userID))
                return;

            BasePlayer captured = player;
            player.inventory.containerMain.onItemAddedToStack += (item, amount) => RouteFromOwnedStack(captured, captured.inventory.containerMain, item, amount);
            player.inventory.containerBelt.onItemAddedToStack += (item, amount) => RouteFromOwnedStack(captured, captured.inventory.containerBelt, item, amount);
        }

        #endregion

        #region Crafting

        private void AttachCrafting(BasePlayer player, BasketState state)
        {
            if (!_config.EnableCraftingIntegration)
                return;
            if (player?.inventory?.crafting == null || state?.Container == null)
                return;

            List<ItemContainer> containers = player.inventory.crafting.containers;
            if (containers == null)
                return;

            containers.Remove(state.Container);
            if (_config.BasketCraftFirst)
                containers.Insert(0, state.Container);
            else
                containers.Add(state.Container);

            RegisterItemRetriever(player, state);
        }

        private void RegisterItemRetriever(BasePlayer player, BasketState state)
        {
            if (ItemRetriever == null || state?.Entity == null || state.Container == null)
                return;

            try
            {
                ItemRetriever.Call("API_AddContainer", this, player, state.Entity, state.Container, null);
            }
            catch
            {
                // Optional integration. Vanilla ItemCrafter.containers is the primary path.
            }
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin == null || plugin.Name != "ItemRetriever")
                return;

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                BasketState state;
                if (_states.TryGetValue(player.userID, out state))
                    RegisterItemRetriever(player, state);
            }
        }

        #endregion

        #region Hooks

        private object CanLootEntity(BasePlayer player, DroppedItemContainer container)
        {
            if (!IsOurEntity(container))
                return null;
            if (player != null && (container.OwnerID == player.userID || IsAdmin(player)))
                return null;
            return true;
        }

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null)
                return;

            BasketState state;
            if (!_states.TryGetValue(player.userID, out state))
                return;

            if (IsOurEntity(entity))
            {
                state.Viewing = true;
                DrawRoutingUi(player, state);
                return;
            }

            if (state.Viewing)
            {
                state.Viewing = false;
                CuiHelper.DestroyUi(player, RoutingUi);
            }
        }

        private void OnPlayerLootEnd(PlayerLoot loot)
        {
            BasePlayer player = loot?.GetComponent<BasePlayer>();
            if (player != null)
                CuiHelper.DestroyUi(player, RoutingUi);

            foreach (BasketState state in _states.Values)
            {
                if (!state.Viewing)
                    continue;
                if (loot != null && (loot.entitySource == state.Entity || loot.IsLooting(state.Container)))
                {
                    state.Viewing = false;
                    CaptureItems(state);
                    MarkDirty(state.UserId);
                }
            }
        }

        private object CanAcceptItem(ItemContainer container, Item item, int targetPos)
        {
            BasketState state = FindStateByContainer(container);
            if (state == null)
                return null;
            if (!CanBasketAccept(item))
                return ItemContainer.CanAcceptResult.CannotAccept;
            return null;
        }

        private void OnItemRemovedFromContainer(ItemContainer container, Item item)
        {
            if (container == null || item?.info == null)
                return;

            BasePlayer player = container.playerOwner ?? container.GetOwnerPlayer();
            if (player == null)
            {
                BasketState basket = FindStateByContainer(container);
                if (basket != null)
                    player = BasePlayer.FindByID(basket.UserId);
            }

            if (player != null && IsPlayerOwnedContainer(player, container))
                MarkOwnedMove(player, item.info.itemid);
        }

        private void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            if (container == null || item?.info == null)
                return;

            BasePlayer player = container.playerOwner ?? container.GetOwnerPlayer();
            if (player == null)
                return;
            if (container != player.inventory?.containerMain && container != player.inventory?.containerBelt)
                return;
            if (_busy.Contains(player.userID) || !ShouldAutoRoute(player, item.info))
                return;
            if (WasOwnedMove(player, item.info.itemid))
                return;

            Item captured = item;
            NextTick(() =>
            {
                if (captured == null || captured.parent != container)
                    return;
                TryRouteItem(player, captured);
            });
        }

        private object OnDispenserGather(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            if (TryRouteItem(player, item))
                return true;
            return null;
        }

        private void OnDispenserBonus(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            NextTick(() => TryRouteItem(player, item));
        }

        private void OnCollectiblePickedup(CollectibleEntity entity, BasePlayer player, Item item)
        {
            if (player == null || item == null)
                return;

            NextTick(() =>
            {
                if (item == null || item.parent == null)
                    return;
                if (player.inventory != null && (item.parent == player.inventory.containerMain || item.parent == player.inventory.containerBelt))
                    TryRouteItem(player, item);
            });
        }

        private void OnGrowableGathered(GrowableEntity plant, Item item, BasePlayer player)
        {
            if (player == null || item == null)
                return;

            NextTick(() =>
            {
                if (item == null || item.parent == null)
                    return;
                if (player.inventory != null && (item.parent == player.inventory.containerMain || item.parent == player.inventory.containerBelt))
                    TryRouteItem(player, item);
            });
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            DroppedItemContainer container = entity as DroppedItemContainer;
            if (container == null || container.net == null)
                return;

            ulong userId;
            if (!_entities.TryGetValue(container.net.ID, out userId))
                return;

            BasketState state;
            if (_states.TryGetValue(userId, out state) && state.Entity == container)
            {
                CaptureItems(state);
                SavePlayer(state);
                state.Entity = null;
                state.Container = null;
            }

            _entities.Remove(container.net.ID);
        }

        #endregion

        #region Persistence

        private void LoadData()
        {
            try
            {
                _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFileName) ?? new StoredData();
            }
            catch (Exception ex)
            {
                PrintWarning("Could not read data file; a new one will be created. " + ex.Message);
                _data = new StoredData();
            }

            if (_data.Players == null)
                _data.Players = new Dictionary<string, PlayerData>();
            _data.DataVersion = DataVersion;
        }

        private void SaveData()
        {
            foreach (BasketState state in _states.Values)
                CaptureItems(state);

            Interface.Oxide.DataFileSystem.WriteObject(DataFileName, _data);
            _dirty.Clear();
        }

        private void SaveDirty()
        {
            if (_dirty.Count == 0)
                return;

            foreach (ulong userId in _dirty.ToList())
            {
                BasketState state;
                if (_states.TryGetValue(userId, out state))
                    SavePlayer(state);
            }

            Interface.Oxide.DataFileSystem.WriteObject(DataFileName, _data);
            _dirty.Clear();
        }

        private void SavePlayer(BasketState state)
        {
            if (state == null)
                return;

            CaptureItems(state);
            if (!_config.SaveRoutingPreferences)
                state.Data.Routing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            _data.Players[state.UserId.ToString(CultureInfo.InvariantCulture)] = state.Data;
        }

        private void MarkDirty(ulong userId)
        {
            _dirty.Add(userId);
        }

        private void CaptureItems(BasketState state)
        {
            if (state?.Container == null)
                return;

            var items = new List<StoredItem>();
            foreach (Item item in state.Container.itemList.ToList())
            {
                StoredItem stored = ToStored(item);
                if (stored != null)
                    items.Add(stored);
            }

            state.Data.Items = items;
        }

        private StoredItem ToStored(Item item)
        {
            if (item?.info == null)
                return null;

            var stored = new StoredItem
            {
                Shortname = item.info.shortname,
                ItemId = item.info.itemid,
                Amount = item.amount,
                Skin = item.skin,
                Slot = item.position,
                Condition = item.condition,
                MaxCondition = item.maxCondition,
                HasCondition = item.hasCondition,
                Flags = (int)item.flags,
                Text = item.text
            };

            if (item.instanceData != null)
            {
                stored.HasInstanceData = true;
                stored.DataInt = item.instanceData.dataInt;
                stored.DataFloat = item.instanceData.dataFloat;
                stored.BlueprintAmount = item.instanceData.blueprintAmount;
                stored.BlueprintTarget = item.instanceData.blueprintTarget;
            }

            return stored;
        }

        private void RestoreItems(ItemContainer container, List<StoredItem> storedItems)
        {
            if (container == null || storedItems == null)
                return;

            container.Clear();
            foreach (StoredItem stored in storedItems)
            {
                Item item = CreateStoredItem(stored);
                if (item == null)
                    continue;

                if (!item.MoveToContainer(container, stored.Slot, false))
                    item.MoveToContainer(container, -1, true);
            }
        }

        private Item CreateStoredItem(StoredItem stored)
        {
            if (stored == null || stored.Amount <= 0)
                return null;

            Item item = stored.ItemId != 0
                ? ItemManager.CreateByItemID(stored.ItemId, stored.Amount, stored.Skin)
                : ItemManager.CreateByName(stored.Shortname, stored.Amount, stored.Skin);

            if (item == null && !string.IsNullOrEmpty(stored.Shortname))
                item = ItemManager.CreateByName(stored.Shortname, stored.Amount, stored.Skin);
            if (item == null)
                return null;

            if (stored.HasCondition)
            {
                item.maxCondition = stored.MaxCondition > 0f ? stored.MaxCondition : item.maxCondition;
                item.condition = stored.Condition;
            }

            item.flags = (Item.Flag)stored.Flags;
            if (!string.IsNullOrEmpty(stored.Text))
                item.text = stored.Text;

            if (stored.HasInstanceData)
            {
                item.instanceData = new ProtoBuf.Item.InstanceData
                {
                    dataInt = stored.DataInt,
                    dataFloat = stored.DataFloat,
                    blueprintAmount = stored.BlueprintAmount,
                    blueprintTarget = stored.BlueprintTarget,
                    ShouldPool = false
                };
            }

            return item;
        }

        #endregion

        #region UI

        private void ShowButton(BasePlayer player)
        {
            if (player == null || !_config.ShowInventoryButton || !CanUse(player))
            {
                if (player != null)
                    CuiHelper.DestroyUi(player, ButtonUi);
                return;
            }

            var elements = new CuiElementContainer();
            elements.Add(new CuiPanel
            {
                Image = { Color = "0.12 0.18 0.10 0.92" },
                RectTransform =
                {
                    AnchorMin = _config.ButtonAnchorMin,
                    AnchorMax = _config.ButtonAnchorMax,
                    OffsetMin = _config.ButtonOffsetMin,
                    OffsetMax = _config.ButtonOffsetMax
                }
            }, "Inventory", ButtonUi);

            elements.Add(new CuiButton
            {
                Button = { Color = "0.36 0.48 0.22 0.95", Command = "gb.ui open" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                Text = { Text = string.Empty }
            }, ButtonUi, ButtonUi + ".Btn");

            if (!string.IsNullOrEmpty(_buttonIconPng))
            {
                elements.Add(new CuiElement
                {
                    Parent = ButtonUi + ".Btn",
                    Name = ButtonUi + ".Icon",
                    Components =
                    {
                        new CuiRawImageComponent { Png = _buttonIconPng, Color = "1 1 1 1" },
                        new CuiRectTransformComponent { AnchorMin = "0.03 0.12", AnchorMax = "0.24 0.88" }
                    }
                });
            }
            else
            {
                elements.Add(new CuiElement
                {
                    Parent = ButtonUi + ".Btn",
                    Name = ButtonUi + ".Icon",
                    Components =
                    {
                        new CuiImageComponent { Sprite = "assets/icons/loot.png", Color = "0.90 0.95 0.78 1" },
                        new CuiRectTransformComponent { AnchorMin = "0.03 0.12", AnchorMax = "0.24 0.88" }
                    }
                });
            }

            elements.Add(new CuiElement
            {
                Parent = ButtonUi + ".Btn",
                Name = ButtonUi + ".Text",
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = _config.ButtonText,
                        FontSize = 12,
                        Align = TextAnchor.MiddleCenter,
                        Color = "0.90 0.95 0.78 1"
                    },
                    new CuiRectTransformComponent { AnchorMin = "0.17 0", AnchorMax = "0.98 1" }
                }
            });

            CuiHelper.DestroyUi(player, ButtonUi);
            CuiHelper.AddUi(player, elements);
        }

        private void RegisterButtonIcon()
        {
            try
            {
                string dataPath = Path.Combine(Interface.Oxide.DataDirectory, Name, "basket.png");
                string pluginPath = Path.Combine(Interface.Oxide.PluginDirectory, "icons", "basket.png");
                string source = File.Exists(dataPath) ? dataPath : pluginPath;
                if (!File.Exists(source) && File.Exists(Path.Combine(Interface.Oxide.PluginDirectory, "..", "data", Name, "basket.png")))
                    source = Path.GetFullPath(Path.Combine(Interface.Oxide.PluginDirectory, "..", "data", Name, "basket.png"));

                if (!File.Exists(source) || CommunityEntity.ServerInstance == null || CommunityEntity.ServerInstance.net == null)
                    return;

                byte[] bytes = File.ReadAllBytes(source);
                uint crc = FileStorage.server.Store(bytes, FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID, 0u);
                if (crc != 0)
                    _buttonIconPng = crc.ToString();
            }
            catch (Exception ex)
            {
                PrintWarning("Could not load the basket button icon. " + ex.Message);
            }
        }

        private void DrawRoutingUi(BasePlayer player, BasketState state)
        {
            CuiHelper.DestroyUi(player, RoutingUi);
            if (player == null || state?.Container == null)
                return;

            List<ItemDefinition> types = !_config.EnableAutoRouting
                ? new List<ItemDefinition>()
                : state.Container.itemList
                    .Where(item => item?.info != null)
                    .Select(item => item.info)
                    .GroupBy(def => def.shortname)
                    .Select(group => group.First())
                    .OrderBy(def => def.displayName?.english ?? def.shortname)
                    .Take(12)
                    .ToList();

            const float titleHeight = 28f;
            const float rowHeight = 24f;
            float height = titleHeight + 4f + types.Count * rowHeight;
            if (_config.EnableAutoRouting && types.Count == 0)
                height += 18f;

            float left = _config.RoutingOffsetLeft;
            float right = _config.RoutingOffsetRight;
            float bottom = _config.RoutingOffsetBottom;

            var elements = new CuiElementContainer();
            elements.Add(new CuiPanel
            {
                Image = { Color = "0.09 0.11 0.08 0.96" },
                RectTransform =
                {
                    AnchorMin = _config.RoutingAnchorMin,
                    AnchorMax = _config.RoutingAnchorMax,
                    OffsetMin = $"{left:0.##} {bottom:0.##}",
                    OffsetMax = $"{right:0.##} {(bottom + height):0.##}"
                }
            }, "Overlay", RoutingUi);

            elements.Add(new CuiPanel
            {
                Image = { Color = "0.16 0.22 0.12 0.98" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0", OffsetMin = "0 0", OffsetMax = $"0 {titleHeight}" }
            }, RoutingUi, RoutingUi + ".Title");

            elements.Add(new CuiLabel
            {
                Text =
                {
                    Text = Msg(player, "BasketTitle"),
                    FontSize = 13,
                    Align = TextAnchor.MiddleCenter,
                    Color = "0.90 0.95 0.78 1"
                },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, RoutingUi + ".Title");

            if (!_config.EnableAutoRouting)
            {
                CuiHelper.AddUi(player, elements);
                return;
            }

            if (types.Count == 0)
            {
                elements.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = Msg(player, "RoutingHint"),
                        FontSize = 10,
                        Align = TextAnchor.MiddleCenter,
                        Color = "0.70 0.74 0.62 1"
                    },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0", OffsetMin = $"4 {titleHeight}", OffsetMax = $"-4 {titleHeight + 18f}" }
                }, RoutingUi);
            }

            for (int i = 0; i < types.Count; i++)
            {
                ItemDefinition def = types[i];
                float rowBottom = titleHeight + 2f + i * rowHeight;
                float rowTop = rowBottom + rowHeight - 2f;
                bool basket = GetRoute(state, def.shortname) == RouteMode.Basket;
                string label = def.displayName?.english ?? def.shortname;

                elements.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = label,
                        FontSize = 11,
                        Align = TextAnchor.MiddleLeft,
                        Color = "0.88 0.90 0.80 1"
                    },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0", OffsetMin = $"8 {rowBottom}", OffsetMax = $"-120 {rowTop}" }
                }, RoutingUi);

                elements.Add(new CuiButton
                {
                    Button =
                    {
                        Color = basket ? "0.30 0.46 0.20 0.95" : "0.18 0.18 0.16 0.90",
                        Command = $"gb.ui route {def.shortname} Basket"
                    },
                    RectTransform = { AnchorMin = "1 0", AnchorMax = "1 0", OffsetMin = $"-114 {rowBottom + 1f}", OffsetMax = $"-60 {rowTop - 1f}" },
                    Text = { Text = Msg(player, "RouteBasketShort"), FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, RoutingUi);

                elements.Add(new CuiButton
                {
                    Button =
                    {
                        Color = !basket ? "0.42 0.34 0.18 0.95" : "0.18 0.18 0.16 0.90",
                        Command = $"gb.ui route {def.shortname} Inventory"
                    },
                    RectTransform = { AnchorMin = "1 0", AnchorMax = "1 0", OffsetMin = $"-56 {rowBottom + 1f}", OffsetMax = $"-8 {rowTop - 1f}" },
                    Text = { Text = Msg(player, "RouteInventoryShort"), FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, RoutingUi);
            }

            CuiHelper.AddUi(player, elements);
        }

        private RouteMode GetRoute(BasketState state, string shortname)
        {
            string mode;
            if (state.Data.Routing.TryGetValue(shortname, out mode) && mode.Equals("Basket", StringComparison.OrdinalIgnoreCase))
                return RouteMode.Basket;
            return RouteMode.Inventory;
        }

        private void DestroyUi(BasePlayer player)
        {
            if (player == null)
                return;
            CuiHelper.DestroyUi(player, ButtonUi);
            CuiHelper.DestroyUi(player, RoutingUi);
        }

        #endregion

        #region API

        private ItemContainer API_GetBasketContainer(ulong userId)
        {
            BasketState state;
            return _states.TryGetValue(userId, out state) ? state.Container : null;
        }

        private int API_GetBasketItemAmount(ulong userId, int itemId)
        {
            ItemContainer container = API_GetBasketContainer(userId);
            return container == null ? 0 : container.GetAmount(itemId, true, true);
        }

        private bool API_IsBasketContainer(ItemContainer container)
        {
            return FindStateByContainer(container) != null;
        }

        #endregion

        #region Helpers

        private bool CanUse(BasePlayer player)
        {
            return player != null && permission.UserHasPermission(player.UserIDString, UsePermission);
        }

        private bool IsAdmin(BasePlayer player)
        {
            return player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, AdminPermission));
        }

        private void Reply(BasePlayer player, string key, params object[] args)
        {
            if (player == null)
                return;
            player.ChatMessage(Msg(player, key, args));
        }

        private string Msg(BasePlayer player, string key, params object[] args)
        {
            string template = lang.GetMessage(key, this, player?.UserIDString);
            return args != null && args.Length > 0 ? string.Format(template, args) : template;
        }

        private BasePlayer FindPlayer(string query)
        {
            if (string.IsNullOrEmpty(query))
                return null;

            BasePlayer exact = BasePlayer.FindAwakeOrSleeping(query);
            if (exact != null)
                return exact;

            foreach (BasePlayer player in BasePlayer.allPlayerList)
            {
                if (player == null)
                    continue;
                if (player.UserIDString == query)
                    return player;
                if (!string.IsNullOrEmpty(player.displayName) && player.displayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    return player;
            }

            IPlayer found = covalence.Players.FindPlayer(query);
            if (found != null)
                return BasePlayer.FindAwakeOrSleeping(found.Id);

            return null;
        }

        #endregion

        #region Lang

        private readonly Dictionary<string, string> MessagesEn = new Dictionary<string, string>
        {
            ["NoPermission"] = "You do not have permission to use the Gathering Basket.",
            ["ResetDenied"] = "You are not allowed to reset your Gathering Basket.",
            ["ResetDone"] = "Your Gathering Basket was emptied.",
            ["RouteBasket"] = "Future {0} will go to your Gathering Basket.",
            ["RouteInventory"] = "Future {0} will stay in your inventory.",
            ["BasketTitle"] = "GATHERING BASKET",
            ["RoutingHint"] = "Move items here to set auto routing",
            ["RouteBasketShort"] = "BASKET",
            ["RouteInventoryShort"] = "INV",
            ["RoutingTitle"] = "AUTO ROUTING",
            ["PlayerNotFound"] = "Player not found.",
            ["AdminHelp"] = "Usage: /basketadmin <view|clear|resetroute> <player>",
            ["AdminView"] = "Opened {0}'s Gathering Basket.",
            ["AdminCleared"] = "Cleared {0}'s Gathering Basket.",
            ["AdminRouteReset"] = "Reset routing preferences for {0}."
        };

        private readonly Dictionary<string, string> MessagesFr = new Dictionary<string, string>
        {
            ["NoPermission"] = "Vous n'avez pas la permission d'utiliser le Gathering Basket.",
            ["ResetDenied"] = "Vous n'etes pas autorise a reinitialiser votre Gathering Basket.",
            ["ResetDone"] = "Votre Gathering Basket a ete vide.",
            ["RouteBasket"] = "Les prochains {0} iront dans votre Gathering Basket.",
            ["RouteInventory"] = "Les prochains {0} resteront dans votre inventaire.",
            ["BasketTitle"] = "GATHERING BASKET",
            ["RoutingHint"] = "Deplacez des objets ici pour regler le routage",
            ["RouteBasketShort"] = "PANIER",
            ["RouteInventoryShort"] = "INV",
            ["RoutingTitle"] = "ROUTAGE AUTO",
            ["PlayerNotFound"] = "Joueur introuvable.",
            ["AdminHelp"] = "Usage : /basketadmin <view|clear|resetroute> <joueur>",
            ["AdminView"] = "Panier de {0} ouvert.",
            ["AdminCleared"] = "Panier de {0} vide.",
            ["AdminRouteReset"] = "Preferences de routage de {0} reinitialisees."
        };

        #endregion
    }
}
