using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Game.Rust.Cui;
using Rust.Ai.Gen2;
using UnityEngine;
using UnityEngine.AI;

namespace Oxide.Plugins
{
    [Info("Livestock Airdrops", "Swannie", "1.10.5")]
    [Description("Skinned supply signals call a cargo plane that drops live livestock, carried down by party balloons.")]
    public class LivestockAirdrops : RustPlugin
    {
        #region Fields

        private const string PermAdmin = "livestockairdrops.admin";
        private const string SupplySignalShortname = "supply.signal";
        private const float BaseDescentSpeed = 6f;
        private const float LandingGraceSeconds = 5f;
        private const float MaxDescentSeconds = 180f;
        private const float PlaneStuckGraceSeconds = 60f;
        private const float HerdSpreadRadius = 3f;
        private const float HerdHeightStagger = 1.5f;
        private const float BalloonRiseSpeed = 3f;
        private const float BalloonReleaseSeconds = 8f;
        private const float MaxPoppedSpeedMultiplier = 4f;
        private const string MovedMarkerLabel = "LIVESTOCK";
        private const float MovedMarkerSeconds = 900f;

        private static readonly Color[] BalloonPalette =
        {
            new Color(0.90f, 0.20f, 0.20f), new Color(0.20f, 0.45f, 0.95f), new Color(0.98f, 0.82f, 0.15f),
            new Color(0.25f, 0.80f, 0.35f), new Color(0.95f, 0.45f, 0.75f), new Color(0.60f, 0.35f, 0.90f)
        };

        private static LivestockAirdrops _instance;
        private PluginConfig _config;

        private readonly Dictionary<BaseEntity, PendingDrop> _armedSignals = new Dictionary<BaseEntity, PendingDrop>();
        private readonly Dictionary<CargoPlane, PendingDrop> _planes = new Dictionary<CargoPlane, PendingDrop>();
        private readonly HashSet<BaseEntity> _protectedAnimals = new HashSet<BaseEntity>();
        private readonly List<DescentController> _descents = new List<DescentController>();
        private readonly HashSet<string> _warnedOnce = new HashSet<string>();
        // Livestock throws per player this frame, so a plain signal refunded in the same frame can be restored.
        private readonly Dictionary<ulong, KeyValuePair<AnimalConfig, int>> _recentThrows = new Dictionary<ulong, KeyValuePair<AnimalConfig, int>>();
        private readonly List<string> _balloonPrefabs = new List<string>();
        private string _speechBubblePrefab;
        private readonly Dictionary<ulong, AdminPanelState> _adminPanels = new Dictionary<ulong, AdminPanelState>();
        // Signals owed to each player. Saved to disk so refunds survive restarts and offline owners.
        private Dictionary<ulong, List<RefundEntry>> _refunds = new Dictionary<ulong, List<RefundEntry>>();
        private readonly HashSet<ulong> _refundFullWarned = new HashSet<ulong>();
        private readonly List<SmokeStop> _smokeStops = new List<SmokeStop>();
        private readonly List<MovedMarker> _movedMarkers = new List<MovedMarker>();
        // Drops used this wipe, by player then signal skin. Counted even with no limit set, so a limit added later is accurate.
        private Dictionary<ulong, Dictionary<ulong, int>> _wipeUses = new Dictionary<ulong, Dictionary<ulong, int>>();
        private int _nextDropId;
        private List<DropRecord> _history = new List<DropRecord>();
        private int _historyVersion;

        private class MovedMarker
        {
            public ulong OwnerId;
            public Vector3 Position;
        }

        private class PendingDrop
        {
            public AnimalConfig Animal;
            public ulong OwnerId;
            public SupplySignal Signal;
            public int AnimalsInFlight;
            public int Id;
            public bool IsTest;
            public int SpawnedCount;
            public int LandedCount;
            public bool AnyLanded => LandedCount > 0;
            public bool Recorded;
            public Vector3 LastPosition;
            public bool MovedToGround;
            public bool NoWalkableGround;
            public bool CountedForWipe;
        }

        #endregion

        #region Configuration

        private class PluginConfig
        {
            [JsonProperty("General Settings")]
            public GeneralSettings General = new GeneralSettings();

            [JsonProperty("Airdrop Configurations")]
            public List<AnimalConfig> Airdrops = new List<AnimalConfig>();

            public static PluginConfig CreateDefault() => new PluginConfig
            {
                Airdrops = new List<AnimalConfig>
                {
                    new AnimalConfig
                    {
                        Name = "Prize Cow",
                        BalloonText = "MOO",
                        SkinId = 3813316121,
                        PrefabPath = "assets/rust.ai/agents/cow/cow.prefab",
                        SpawnCount = 1,
                        Notification = "A cargo plane is delivering a live Cow to your location!"
                    },
                    new AnimalConfig
                    {
                        Name = "Enraged Bull",
                        BalloonText = "MOO!",
                        SkinId = 3813325374,
                        PrefabPath = "assets/rust.ai/agents/bull/bull.prefab",
                        SpawnCount = 1,
                        Notification = "WARNING: An aggressive Bull is being dropped at your location!"
                    },
                    new AnimalConfig
                    {
                        Name = "Domestic Sheep",
                        BalloonText = "BAA",
                        SkinId = 3813326419,
                        PrefabPath = "assets/rust.ai/agents/sheep/sheep.prefab",
                        SpawnCount = 1,
                        Notification = "A Sheep is floating down from the skies!"
                    },
                    new AnimalConfig
                    {
                        Name = "Proud Ram",
                        BalloonText = "BAA!",
                        SkinId = 3813327235,
                        PrefabPath = "assets/rust.ai/agents/sheep/sheep.prefab",
                        Sex = "Male",
                        SpawnCount = 1,
                        Notification = "A Ram is floating in. Mind the horns!"
                    },
                    new AnimalConfig
                    {
                        Name = "Baby Lamb",
                        BalloonText = "baa",
                        SkinId = 3813328103,
                        PrefabPath = "assets/rust.ai/agents/sheep/lamb.prefab",
                        SpawnCount = 1,
                        Notification = "A tiny Lamb is floating down from the sky. Aww!"
                    },
                    new AnimalConfig
                    {
                        Name = "Baby Calf",
                        BalloonText = "moo",
                        SkinId = 3813328745,
                        PrefabPath = "assets/rust.ai/agents/calf/calf.prefab",
                        MalePrefabPath = "assets/rust.ai/agents/calf/calfmale.prefab",
                        SpawnCount = 1,
                        Notification = "A wobbly little Calf is floating down to you!"
                    }
                }
            };
        }

        private class GeneralSettings
        {
            [JsonProperty("Drop Altitude (Meters)")]
            public float DropAltitude = 150f;

            [JsonProperty("Plane Flight Speed")]
            public float PlaneSpeed = 40f;

            [JsonProperty("Descent Speed Modifier")]
            public float DescentSpeedModifier = 1f;

            [JsonProperty("Prevent Damage To Animal On Landing")]
            public bool PreventLandingDamage = true;

            [JsonProperty("Use Party Balloons")]
            public bool UseBalloons = true;

            // Replace, not merge: Newtonsoft would otherwise append the saved list onto these defaults.
            [JsonProperty("Balloon Prefabs (random mix, empty = auto-detect)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> BalloonPrefabs = new List<string>
            {
                "assets/prefabs/misc/birthday_balloons_2025/latex_balloon.deployed.prefab",
                "assets/prefabs/misc/birthday_balloons_2025/circle_balloon.deployed.prefab",
                "assets/prefabs/misc/birthday_balloons_2025/heart_balloon.deployed.prefab",
                "assets/prefabs/misc/birthday_balloons_2025/star_balloon.deployed.prefab"
            };

            // Floats on top and shows the animal's Balloon Text; blank = put the text on every balloon instead.
            [JsonProperty("Speech Bubble Prefab (shows Balloon Text)")]
            public string SpeechBubblePrefab = "assets/prefabs/misc/birthday_balloons_2025/speechbubble_balloon.deployed.prefab";

            [JsonProperty("Balloon Count")]
            public int BalloonCount = 8;

            // Sent to clients via networkEntityScale; spacing and bubble height grow with it.
            [JsonProperty("Balloon Scale")]
            public float BalloonScale = 2f;

            [JsonProperty("Speech Bubble Scale")]
            public float SpeechBubbleScale = 3f;

            // Height of the balloon strings' base above the animal's feet.
            [JsonProperty("Balloon Height Above Animal")]
            public float BalloonHeight = 1.3f;

            [JsonProperty("Popped Balloons Speed Up Descent")]
            public bool PoppedBalloonsSpeedUp = true;

            [JsonProperty("Thrower Can Lead Animal Immediately")]
            public bool TrustThrower = true;

            [JsonProperty("Thrower Owns Animal")]
            public bool ThrowerOwnsAnimal = true;

            // Vanilla smokes for 210 s after the signal pops; this ends it once the last animal is down.
            [JsonProperty("Stop Signal Smoke After Landing (Seconds, -1 = vanilla)")]
            public float StopSmokeAfterLanding = 10f;
        }

        private class AnimalConfig
        {
            [JsonProperty("Animal Name")]
            public string Name;

            [JsonProperty("Signal Skin ID")]
            public ulong SkinId;

            [JsonProperty("Prefab Path")]
            public string PrefabPath;

            // For prefabs that cover both sexes (sheep/lamb), or with a Male Prefab Path (calf). Cow and bull are fixed by prefab.
            [JsonProperty("Sex (Random, Male, Female)")]
            public string Sex = "Random";

            // Species whose sexes are separate prefabs: Prefab Path is the female, this the male.
            [JsonProperty("Male Prefab Path (optional)")]
            public string MalePrefabPath = "";

            [JsonProperty("Spawn Count")]
            public int SpawnCount = 1;

            [JsonProperty("Notification Message")]
            public string Notification;

            [JsonProperty("Balloon Text")]
            public string BalloonText = "";

            [JsonProperty("Limit Per Player Per Wipe (0 = no limit)")]
            public int WipeLimit = 0;
        }

        protected override void LoadDefaultConfig() => _config = PluginConfig.CreateDefault();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                    throw new JsonException();
            }
            catch
            {
                PrintError("Configuration file is invalid; using defaults without overwriting it.");
                LoadDefaultConfig();
                return;
            }

            if (_config.General == null)
                _config.General = new GeneralSettings();
            if (_config.Airdrops == null)
                _config.Airdrops = new List<AnimalConfig>();

            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You don't have permission to use this command.",
                ["Usage"] = "Usage: giveanimal <player> <animal>\nAnimals: {0}",
                ["PlayerNotFound"] = "No player found matching '{0}'.",
                ["MultiplePlayers"] = "Multiple players match '{0}': {1}",
                ["AnimalNotFound"] = "No animal named '{0}'. Animals: {1}",
                ["ItemFailed"] = "Failed to create the supply signal item.",
                ["Given"] = "Gave '{0}' signal to {1}.",
                ["Received"] = "You received a '{0}' signal. Throw it to call in a livestock drop!",
                ["Refunded"] = "Your '{0}' drop failed, so the signal has been returned to you.",
                ["RefundedDeploymentError"] = "An error occurred in the deployment of your livestock signal and your signal has been returned. Any signal cooldown you have still applies.",
                ["MovedToGround"] = "Your '{0}' couldn't land where it was dropped, so it was moved to the nearest walkable ground. Look for the {1} marker on your map.",
                ["WipeLimitReached"] = "You have used all {1} '{0}' drops allowed per player this wipe. Your signal has been returned.",
                ["RefundInventoryFull"] = "A failed livestock drop is waiting to be returned to you. Make space in your inventory to receive the signal."
            }, this);
        }

        private string Msg(string key, string userId, params object[] args)
        {
            string message = lang.GetMessage(key, this, userId);
            return args.Length > 0 ? string.Format(message, args) : message;
        }

        #endregion

        #region Oxide Hooks

        private void Init()
        {
            _instance = this;
            permission.RegisterPermission(PermAdmin, this);
            LoadRefunds();
            LoadHistory();
            LoadWipeUses();
        }

        private void OnNewSave(string filename)
        {
            _wipeUses.Clear();
            SaveWipeUses();
        }

        private void OnServerInitialized()
        {
            foreach (AnimalConfig animal in _config.Airdrops)
            {
                if (!PrefabExists(animal.PrefabPath))
                    PrintWarning($"'{animal.Name}': prefab '{animal.PrefabPath}' does not exist on this server. Use 'livestockairdrops.prefabs <filter>' to find the right path.");
                if (!string.IsNullOrEmpty(animal.MalePrefabPath) && !PrefabExists(animal.MalePrefabPath))
                    PrintWarning($"'{animal.Name}': male prefab '{animal.MalePrefabPath}' does not exist on this server.");
            }

            if (_config.General.UseBalloons)
            {
                ResolveBalloonPrefabs();
                if (_balloonPrefabs.Count == 0 && _speechBubblePrefab == null)
                    PrintWarning("No party balloon prefab found; animals will descend without balloons.");
            }

            foreach (var duplicate in _config.Airdrops.GroupBy(a => a.SkinId).Where(g => g.Count() > 1))
                PrintWarning($"Skin ID {duplicate.Key} is used by multiple animals; only '{duplicate.First().Name}' will be used.");

            DeliverAllRefunds();
            timer.Every(RefundRetrySeconds, DeliverAllRefunds);
            timer.Every(AdminPanelRefreshSeconds, RefreshAdminPanels);
        }

        private void Unload()
        {
            foreach (KeyValuePair<CargoPlane, PendingDrop> entry in _planes)
            {
                if (entry.Key != null && !entry.Key.IsDestroyed)
                    entry.Key.Kill();

                QueueRefund(entry.Value, "the plugin unloaded while the plane was flying");
            }

            // A signal still counting down would otherwise call an ordinary supply drop once the plugin is gone.
            foreach (KeyValuePair<BaseEntity, PendingDrop> entry in _armedSignals)
            {
                if (entry.Key == null || entry.Key.IsDestroyed)
                    continue;

                entry.Key.Kill();
                QueueRefund(entry.Value, "the plugin unloaded before the plane was called");
            }

            foreach (DescentController descent in _descents.ToList())
            {
                if (descent != null)
                    descent.ForceLand();
            }

            foreach (MovedMarker marker in _movedMarkers.ToList())
                RemoveMovedMarker(marker);

            foreach (BasePlayer player in BasePlayer.activePlayerList)
                CuiHelper.DestroyUi(player, UiRoot);

            BalloonRelease.KillAll();
            _instance = null;
        }

        private void OnPlayerDisconnected(BasePlayer player) => _adminPanels.Remove(player.userID);

        private void OnExplosiveThrown(BasePlayer player, BaseEntity entity, ThrownWeapon weapon) => ArmSignal(player, entity, weapon);

        private void OnExplosiveDropped(BasePlayer player, BaseEntity entity, ThrownWeapon weapon) => ArmSignal(player, entity, weapon);

        private void OnCargoPlaneSignaled(CargoPlane plane, SupplySignal signal)
        {
            PendingDrop pending;
            if (plane == null || signal == null || !_armedSignals.TryGetValue(signal, out pending))
                return;

            _armedSignals.Remove(signal);
            pending.Signal = signal;
            pending.LastPosition = plane.dropPosition;
            CountWipeUse(pending);
            PurgeDeadPlanes();
            ConfigurePlane(plane);
            _planes[plane] = pending;

            BasePlayer owner = BasePlayer.FindByID(pending.OwnerId);
            if (owner != null && !string.IsNullOrEmpty(pending.Animal.Notification))
                owner.ChatMessage(FormatNotification(pending.Animal));

            timer.Once(plane.secondsToTake + PlaneStuckGraceSeconds, () =>
            {
                PendingDrop undelivered;
                if (!_planes.TryGetValue(plane, out undelivered))
                    return;

                _planes.Remove(plane);
                if (plane != null && !plane.IsDestroyed)
                    plane.Kill();

                RefundDrop(undelivered, "the plane never dropped its cargo");
            });
        }

        private void OnSupplyDropDropped(BaseEntity drop, CargoPlane plane)
        {
            PendingDrop pending;
            if (drop == null || plane == null || !_planes.TryGetValue(plane, out pending))
                return;

            _planes.Remove(plane);
            Vector3 position = drop.transform.position;
            NextTick(() =>
            {
                if (drop != null && !drop.IsDestroyed)
                    drop.Kill();
            });

            SpawnHerd(position, pending);
        }

        // Other plugins (e.g. SignalCooldown's refund) recreate a thrown signal as a new item, losing its name
        // and sometimes its skin.
        private void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            if (item == null || !string.IsNullOrEmpty(item.name) || item.info.shortname != SupplySignalShortname)
                return;

            if (item.skin != 0)
            {
                AnimalConfig animal = FindAnimalBySkin(item.skin);
                if (animal != null)
                    ApplyAnimal(item, animal);
                return;
            }

            // A plain signal arriving the same frame the owner threw a livestock signal is that signal's refund.
            BasePlayer owner = container?.playerOwner;
            KeyValuePair<AnimalConfig, int> recent;
            if (owner == null || !_recentThrows.TryGetValue(owner.userID, out recent) || recent.Value != Time.frameCount)
                return;

            _recentThrows.Remove(owner.userID);
            ApplyAnimal(item, recent.Key);
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null || !_protectedAnimals.Contains(entity))
                return null;

            // Players can still shoot a falling cow; only environmental damage is blocked.
            if (info.InitiatorPlayer != null)
                return null;

            return true;
        }

        #endregion

        #region Drop Logic

        private void ArmSignal(BasePlayer player, BaseEntity entity, ThrownWeapon weapon)
        {
            if (player == null || !(entity is SupplySignal))
                return;

            ulong skin = weapon?.GetItem()?.skin ?? 0;
            if (skin == 0)
                skin = entity.skinID;

            AnimalConfig animal = FindAnimalBySkin(skin);
            if (animal == null)
                return;

            // The thrown entity doesn't reliably carry the item's skin; other plugins read it from there
            // (BotReSpawn's "ignore skinned supply grenades", SignalCooldown's refund).
            entity.skinID = skin;
            _recentThrows[player.userID] = new KeyValuePair<AnimalConfig, int>(animal, Time.frameCount);

            // A cooldown plugin that ran before us already killed it and refunded a plain signal.
            if (entity.IsDestroyed)
            {
                RestoreRefundedSignal(player, animal);
                return;
            }

            PurgeDeadSignals();
            if (WipeLimitReached(player.userID, animal))
            {
                // Next tick so a cooldown plugin has run first: if it refunded this throw, ours would be a duplicate.
                NextTick(() => RejectOverLimitSignal(player, entity, animal));
                return;
            }

            _armedSignals[entity] = new PendingDrop { Animal = animal, OwnerId = player.userID, Id = ++_nextDropId, LastPosition = entity.transform.position };
        }

        // The refund is the newest plain supply signal in the inventory (signals don't stack, item uids only grow).
        private void RestoreRefundedSignal(BasePlayer player, AnimalConfig animal)
        {
            Item refund = player.inventory.containerMain.itemList
                .Concat(player.inventory.containerBelt.itemList)
                .Where(i => i.info.shortname == SupplySignalShortname && i.skin == 0 && string.IsNullOrEmpty(i.name))
                .OrderByDescending(i => i.uid.Value)
                .FirstOrDefault();

            if (refund == null)
                return;

            _recentThrows.Remove(player.userID);
            ApplyAnimal(refund, animal);
        }

        private static void ApplyAnimal(Item item, AnimalConfig animal)
        {
            item.skin = animal.SkinId;
            item.name = animal.Name;
            item.MarkDirty();
        }

        private void ConfigurePlane(CargoPlane plane)
        {
            GeneralSettings settings = _config.General;
            Vector3 dropPosition = plane.dropPosition;
            float altitude = TerrainMeta.HeightMap.GetHeight(dropPosition) + settings.DropAltitude;

            plane.startPos.y = altitude;
            plane.endPos.y = altitude;
            plane.secondsToTake = Vector3.Distance(plane.startPos, plane.endPos) / Mathf.Max(1f, settings.PlaneSpeed);
            plane.transform.position = plane.startPos;
            plane.transform.rotation = Quaternion.LookRotation(plane.endPos - plane.startPos);
        }

        private void SpawnHerd(Vector3 position, PendingDrop pending)
        {
            AnimalConfig animal = pending.Animal;
            pending.LastPosition = position;
            int count = Mathf.Clamp(animal.SpawnCount, 1, 10);
            for (int i = 0; i < count; i++)
            {
                Vector3 offset = Vector3.zero;
                if (count > 1)
                {
                    offset = Quaternion.Euler(0f, 360f * i / count, 0f) * Vector3.forward * HerdSpreadRadius;
                    offset.y = i * HerdHeightStagger;
                }

                if (SpawnAirdropAnimal(position + offset, pending) != null)
                {
                    pending.AnimalsInFlight++;
                    pending.SpawnedCount++;
                }
            }

            if (pending.AnimalsInFlight == 0)
            {
                RecordDrop(pending, ResultFailed, "Could not be spawned. Signal returned.");
                RefundDrop(pending, "the animal could not be spawned");
                ScheduleSmokeStop(pending);
            }
        }

        private BaseEntity SpawnAirdropAnimal(Vector3 position, PendingDrop pending)
        {
            AnimalConfig config = pending.Animal;
            ulong ownerId = pending.OwnerId;
            bool hasMalePrefab = !string.IsNullOrEmpty(config.MalePrefabPath);

            // With separate male/female prefabs, "Random" has to be rolled here to pick the prefab.
            bool? male = ParseSex(config.Sex);
            if (male == null && hasMalePrefab)
                male = Random.Range(0f, 1f) < 0.5f;

            string prefab = male == true && hasMalePrefab ? config.MalePrefabPath : config.PrefabPath;
            BaseEntity animal = GameManager.server.CreateEntity(prefab, position, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            if (animal == null)
            {
                PrintError($"Failed to create animal from prefab '{prefab}'.");
                return null;
            }

            LivestockAnimal livestock = animal as LivestockAnimal;
            if (livestock != null && male.HasValue)
            {
                // Same as LivestockVendor: stops InitShared re-rolling the sex at random.
                livestock.IsBeingPurchased = true;
                livestock.IsMale = male.Value;
            }

            animal.Spawn();

            if (livestock != null && ownerId != 0)
            {
                // The same calls LivestockVendor makes for a bought animal.
                if (_config.General.ThrowerOwnsAnimal)
                    livestock.SetOwner(ownerId, LivestockAnimal.OwnerChangeReason.Purchased);
                if (_config.General.TrustThrower)
                    livestock.SetFamiliarity(ownerId, ConVar.Livestock.trustToLead);
            }

            GeneralSettings settings = _config.General;
            DescentController descent = animal.gameObject.AddComponent<DescentController>();
            descent.Init(animal, pending, BaseDescentSpeed * Mathf.Max(0.1f, settings.DescentSpeedModifier));
            _descents.Add(descent);

            if (settings.PreventLandingDamage)
                _protectedAnimals.Add(animal);

            return animal;
        }

        private void OnAnimalLanded(BaseEntity animal, PendingDrop pending)
        {
            if (pending != null)
                pending.LandedCount++;

            OnAnimalDelivered(pending);
            if (_protectedAnimals.Contains(animal))
                timer.Once(LandingGraceSeconds, () => _protectedAnimals.Remove(animal));
        }

        private void OnAnimalLost(BaseEntity animal, PendingDrop pending)
        {
            OnAnimalDelivered(pending);
            _protectedAnimals.Remove(animal);
        }

        private void OnAnimalDelivered(PendingDrop pending)
        {
            if (pending == null)
                return;

            pending.AnimalsInFlight--;
            if (pending.AnimalsInFlight <= 0)
            {
                RecordFinishedDrop(pending);
                ScheduleSmokeStop(pending);
            }
        }

        // A personal map note, so only the owner sees it. The game caps its own label length at 10 characters.
        private void AddMovedMarker(PendingDrop pending, Vector3 position)
        {
            BasePlayer owner = BasePlayer.FindAwakeOrSleepingByID(pending.OwnerId);
            if (owner == null)
                return;

            if (owner.State.pointsOfInterest == null)
                owner.State.pointsOfInterest = Facepunch.Pool.Get<List<ProtoBuf.MapNote>>();

            ProtoBuf.MapNote note = Facepunch.Pool.Get<ProtoBuf.MapNote>();
            note.noteType = (int)BasePlayer.MapNoteType.PointOfInterest;
            note.worldPosition = position;
            note.label = MovedMarkerLabel;
            owner.State.pointsOfInterest.Add(note);
            RefreshMapNotes(owner);

            MovedMarker marker = new MovedMarker { OwnerId = pending.OwnerId, Position = position };
            _movedMarkers.Add(marker);
            timer.Once(MovedMarkerSeconds, () => RemoveMovedMarker(marker));

            if (owner.IsConnected)
                owner.ChatMessage(Msg("MovedToGround", owner.UserIDString, pending.Animal.Name, MovedMarkerLabel));
        }

        // Matched by label and position because the player may have deleted it, and deleted notes are recycled.
        private void RemoveMovedMarker(MovedMarker marker)
        {
            _movedMarkers.Remove(marker);
            BasePlayer owner = BasePlayer.FindAwakeOrSleepingByID(marker.OwnerId);
            List<ProtoBuf.MapNote> notes = owner != null ? owner.State.pointsOfInterest : null;
            if (notes == null)
                return;

            int index = notes.FindIndex(n => n.label == MovedMarkerLabel && n.worldPosition == marker.Position);
            if (index < 0)
                return;

            notes[index].Dispose();
            notes.RemoveAt(index);
            RefreshMapNotes(owner);
        }

        private static void RefreshMapNotes(BasePlayer player)
        {
            player.DirtyPlayerState();
            if (player.IsConnected)
                player.SendMarkersToClient();
        }

        private void ScheduleSmokeStop(PendingDrop pending)
        {
            SupplySignal signal = pending.Signal;
            float delay = _config.General.StopSmokeAfterLanding;
            if (signal == null || delay < 0f)
                return;

            pending.Signal = null;
            float wait = Mathf.Max(0f, delay);
            SmokeStop smokeStop = new SmokeStop { Drop = pending, Position = signal.transform.position, EndsAt = Time.realtimeSinceStartup + wait };
            _smokeStops.Add(smokeStop);
            timer.Once(wait, () =>
            {
                _smokeStops.Remove(smokeStop);
                if (signal != null && !signal.IsDestroyed)
                    signal.FinishUp();
            });
        }

        #endregion

        #region Wipe Limits

        private const string WipeUsesDataFile = "LivestockAirdrops_WipeUses";
        private const int MaxWipeLimit = 99;

        private void LoadWipeUses()
        {
            try
            {
                _wipeUses = Oxide.Core.Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, Dictionary<ulong, int>>>(WipeUsesDataFile)
                    ?? new Dictionary<ulong, Dictionary<ulong, int>>();
            }
            catch (JsonException)
            {
                PrintError($"Could not read oxide/data/{WipeUsesDataFile}.json, so per-wipe counts start from zero.");
                _wipeUses = new Dictionary<ulong, Dictionary<ulong, int>>();
            }
        }

        private void SaveWipeUses() => Oxide.Core.Interface.Oxide.DataFileSystem.WriteObject(WipeUsesDataFile, _wipeUses);

        private int WipeUsesOf(ulong playerId, ulong skin)
        {
            Dictionary<ulong, int> uses;
            int count;
            return _wipeUses.TryGetValue(playerId, out uses) && uses.TryGetValue(skin, out count) ? count : 0;
        }

        // Signals still counting down are included, or a player could throw several before the first one counts.
        private bool WipeLimitReached(ulong playerId, AnimalConfig animal)
        {
            if (animal.WipeLimit <= 0)
                return false;

            int waiting = _armedSignals.Values.Count(p => p.OwnerId == playerId && p.Animal == animal);
            return WipeUsesOf(playerId, animal.SkinId) + waiting >= animal.WipeLimit;
        }

        private void RejectOverLimitSignal(BasePlayer player, BaseEntity entity, AnimalConfig animal)
        {
            if (entity == null || entity.IsDestroyed)
                return;

            entity.Kill();
            if (player == null)
                return;

            Item item = ItemManager.CreateByName(SupplySignalShortname, 1, animal.SkinId);
            if (item != null)
            {
                item.name = animal.Name;
                player.GiveItem(item);
            }

            player.ChatMessage(Msg("WipeLimitReached", player.UserIDString, animal.Name, animal.WipeLimit));
        }

        // Counted once the plane is called: a cooldown plugin can still cancel the throw before that.
        private void CountWipeUse(PendingDrop pending)
        {
            Dictionary<ulong, int> uses;
            if (!_wipeUses.TryGetValue(pending.OwnerId, out uses))
                _wipeUses[pending.OwnerId] = uses = new Dictionary<ulong, int>();

            uses[pending.Animal.SkinId] = WipeUsesOf(pending.OwnerId, pending.Animal.SkinId) + 1;
            pending.CountedForWipe = true;
            SaveWipeUses();
        }

        private void ReleaseWipeUse(PendingDrop pending)
        {
            if (!pending.CountedForWipe)
                return;

            pending.CountedForWipe = false;
            int remaining = WipeUsesOf(pending.OwnerId, pending.Animal.SkinId) - 1;
            Dictionary<ulong, int> uses;
            if (!_wipeUses.TryGetValue(pending.OwnerId, out uses))
                return;

            if (remaining > 0)
                uses[pending.Animal.SkinId] = remaining;
            else
                uses.Remove(pending.Animal.SkinId);

            SaveWipeUses();
        }

        #endregion

        #region Drop History

        private const string HistoryDataFile = "LivestockAirdrops_History";
        private const int MaxHistoryEntries = 200;
        private const string ResultLanded = "LANDED";
        private const string ResultLost = "LOST";
        private const string ResultFailed = "FAILED";
        private const string ResultRefunded = "REFUNDED";
        private const string ResultCancelled = "CANCELLED";

        private class DropRecord
        {
            public System.DateTime Time;
            public string Animal;
            public ulong SkinId;
            public ulong OwnerId;
            public string OwnerName;
            public string Grid;
            public string Result;
            public string Detail;
        }

        private void LoadHistory()
        {
            try
            {
                _history = Oxide.Core.Interface.Oxide.DataFileSystem.ReadObject<List<DropRecord>>(HistoryDataFile) ?? new List<DropRecord>();
            }
            catch (JsonException)
            {
                PrintError($"Could not read oxide/data/{HistoryDataFile}.json, so drop history starts empty.");
                _history = new List<DropRecord>();
            }
        }

        private void RecordFinishedDrop(PendingDrop pending)
        {
            // Already recorded means already settled: a cancelled drop was refunded before its animals were removed.
            if (pending.Recorded)
                return;

            if (pending.AnyLanded)
            {
                string landed = pending.SpawnedCount > 1 ? $"{pending.LandedCount} of {pending.SpawnedCount} landed. " : "";
                RecordDrop(pending, ResultLanded, landed + (pending.MovedToGround ? "Moved to walkable ground." : ""));
                return;
            }

            RecordDrop(pending, ResultLost, (pending.NoWalkableGround ? "No walkable ground nearby." : "Destroyed before landing.") + " Signal returned.");
            RefundDrop(pending, pending.NoWalkableGround ? "there was no walkable ground nearby" : "the animal was destroyed before landing");
        }

        // Saved on every entry, as drops are infrequent and the last ones matter most after a crash.
        // Each drop is recorded once: a cancelled drop's animals also report back as lost.
        private void RecordDrop(PendingDrop pending, string result, string detail)
        {
            if (pending == null || pending.IsTest || pending.Recorded)
                return;

            pending.Recorded = true;
            _history.Add(new DropRecord
            {
                Time = System.DateTime.Now,
                Animal = pending.Animal.Name,
                SkinId = pending.Animal.SkinId,
                OwnerId = pending.OwnerId,
                OwnerName = DropOwnerName(pending),
                Grid = MapHelper.PositionToString(pending.LastPosition),
                Result = result,
                Detail = detail
            });

            if (_history.Count > MaxHistoryEntries)
                _history.RemoveAt(0);

            _historyVersion++;
            Oxide.Core.Interface.Oxide.DataFileSystem.WriteObject(HistoryDataFile, _history);
        }

        #endregion

        #region Refunds

        private const string RefundDataFile = "LivestockAirdrops_Refunds";
        private const float RefundRetrySeconds = 30f;

        private class RefundEntry
        {
            public ulong Skin;
            public bool CancelledByAdmin;
        }

        private void LoadRefunds()
        {
            try
            {
                _refunds = Oxide.Core.Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, List<RefundEntry>>>(RefundDataFile)
                    ?? new Dictionary<ulong, List<RefundEntry>>();
            }
            catch (JsonException)
            {
                PrintError($"Could not read oxide/data/{RefundDataFile}.json, so any refunds in it will not be delivered.");
                _refunds = new Dictionary<ulong, List<RefundEntry>>();
            }
        }

        private void SaveRefunds() => Oxide.Core.Interface.Oxide.DataFileSystem.WriteObject(RefundDataFile, _refunds);

        private void RefundDrop(PendingDrop pending, string reason, bool cancelledByAdmin = false)
        {
            QueueRefund(pending, reason, cancelledByAdmin);
            if (pending != null)
                DeliverRefunds(BasePlayer.FindByID(pending.OwnerId));
        }

        // Saves straight away. Unload relies on this alone, because a shutdown may already be past its inventory save.
        private void QueueRefund(PendingDrop pending, string reason, bool cancelledByAdmin = false)
        {
            if (pending == null || pending.OwnerId == 0 || pending.IsTest)
                return;

            RecordDrop(pending, cancelledByAdmin ? ResultCancelled : ResultRefunded, char.ToUpperInvariant(reason[0]) + reason.Substring(1));
            ReleaseWipeUse(pending);

            List<RefundEntry> entries;
            if (!_refunds.TryGetValue(pending.OwnerId, out entries))
                _refunds[pending.OwnerId] = entries = new List<RefundEntry>();

            entries.Add(new RefundEntry { Skin = pending.Animal.SkinId, CancelledByAdmin = cancelledByAdmin });
            SaveRefunds();

            // The drop is over, so the thrown signal shouldn't keep marking the spot.
            SupplySignal signal = pending.Signal;
            pending.Signal = null;
            if (signal != null && !signal.IsDestroyed)
                signal.FinishUp();

            Puts($"Refund queued for {pending.OwnerId}: '{pending.Animal.Name}' signal, because {reason}.");
        }

        private void DeliverAllRefunds()
        {
            foreach (ulong ownerId in _refunds.Keys.ToList())
                DeliverRefunds(BasePlayer.FindByID(ownerId));
        }

        private void DeliverRefunds(BasePlayer player)
        {
            List<RefundEntry> entries;
            if (player == null || !player.IsConnected || player.IsDead() || player.IsSleeping() || !_refunds.TryGetValue(player.userID, out entries))
                return;

            List<RefundEntry> undelivered = new List<RefundEntry>();
            bool inventoryFull = false;
            foreach (RefundEntry entry in entries)
            {
                AnimalConfig animal = FindAnimalBySkin(entry.Skin);
                Item item = animal != null ? ItemManager.CreateByName(SupplySignalShortname, 1, entry.Skin) : null;
                if (item == null)
                {
                    WarnOnce($"A refund for signal skin {entry.Skin} can't be delivered: no animal in the config uses that skin.");
                    undelivered.Add(entry);
                    continue;
                }

                // Not BasePlayer.GiveItem: with a full inventory that drops the signal on the ground.
                item.name = animal.Name;
                if (!player.inventory.GiveItem(item))
                {
                    item.Remove();
                    undelivered.Add(entry);
                    inventoryFull = true;
                    continue;
                }

                player.ChatMessage(Msg(entry.CancelledByAdmin ? "RefundedDeploymentError" : "Refunded", player.UserIDString, animal.Name));
                Puts($"Refund delivered to {player.displayName} [{player.UserIDString}]: '{animal.Name}' signal.");
            }

            if (inventoryFull)
            {
                if (_refundFullWarned.Add(player.userID))
                    player.ChatMessage(Msg("RefundInventoryFull", player.UserIDString));
            }
            else
            {
                _refundFullWarned.Remove(player.userID);
            }

            if (undelivered.Count == entries.Count)
                return;

            if (undelivered.Count == 0)
                _refunds.Remove(player.userID);
            else
                _refunds[player.userID] = undelivered;

            SaveRefunds();
        }

        #endregion

        #region Descent Controller

        private class DescentController : MonoBehaviour
        {
            private static readonly int GroundMask = LayerMask.GetMask("Terrain", "World", "Construction", "Deployed");

            private const float WalkableSampleRadius = 10f;
            private const int SearchDirections = 8;
            private const float MaxWadingDepth = 0.5f;
            private const float MovedReportDistance = 5f;
            private static readonly float[] SearchRingDistances = { 15f, 30f, 50f };

            private readonly List<Behaviour> _suspended = new List<Behaviour>();
            private BaseEntity _animal;
            private BaseNPC2 _npc;
            private RustNavMeshAgent _agent;
            private FSMComponent _fsm;
            private readonly List<BaseEntity> _balloons = new List<BaseEntity>();
            private int _balloonsAttached;
            private Rigidbody _body;
            private bool _bodyWasKinematic;
            private float _speed;
            private float _startedAt;
            private bool _finished;

            private PendingDrop _drop;

            public void Init(BaseEntity animal, PendingDrop drop, float speed)
            {
                _drop = drop;
                AnimalConfig config = drop.Animal;
                _animal = animal;
                _speed = speed;
                _startedAt = Time.realtimeSinceStartup;

                _npc = animal as BaseNPC2;
                if (_npc != null)
                    SuspendGen2AI();
                else
                    SuspendLegacyAI();

                _body = animal.GetComponent<Rigidbody>();
                if (_body != null)
                {
                    _bodyWasKinematic = _body.isKinematic;
                    _body.isKinematic = true;
                }

                if (_instance._config.General.UseBalloons)
                    AttachBalloons(config);
            }

            // Gen2 animals (all current livestock): same pause the game uses when an NPC is mounted.
            private void SuspendGen2AI()
            {
                _agent = _npc.GetComponent<RustNavMeshAgent>();
                if (_agent != null)
                    _agent.Pause(this);

                _fsm = _npc.GetComponent<FSMComponent>();
                if (_fsm != null)
                    _fsm.SetFsmActive(false);
            }

            private void ResumeGen2AI(Vector3 groundPosition)
            {
                _npc.transform.position = groundPosition;

                // Unpause re-warps the agent onto the navmesh at the current transform position.
                if (_agent != null)
                    _agent.Unpause(this);
                if (_fsm != null)
                    _fsm.SetFsmActive(true);
            }

            // Older AI: disables brain/navigator components so animals can't aggro mid-air.
            private void SuspendLegacyAI()
            {
                foreach (Behaviour behaviour in _animal.GetComponentsInChildren<Behaviour>(true))
                {
                    if (behaviour == null || !behaviour.enabled || behaviour is BaseEntity || behaviour == this)
                        continue;

                    string typeName = behaviour.GetType().Name;
                    if (behaviour is NavMeshAgent || typeName.Contains("Brain") || typeName.Contains("Navigator"))
                    {
                        behaviour.enabled = false;
                        _suspended.Add(behaviour);
                    }
                }
            }

            // A real bunch: every string is tied to one knot on the animal's back (a balloon's origin is the
            // bottom of its string) and each balloon is tilted outward, so they fan out at the top.
            private void AttachBalloons(AnimalConfig config)
            {
                GeneralSettings settings = _instance._config.General;
                List<string> prefabs = _instance._balloonPrefabs;
                string speechBubble = _instance._speechBubblePrefab;
                bool textOnBubble = speechBubble != null && !string.IsNullOrEmpty(config.BalloonText);

                int count = Mathf.Clamp(settings.BalloonCount, 1, 16);
                float scale = Mathf.Clamp(settings.BalloonScale, 0.5f, 5f);
                float bubbleScale = Mathf.Clamp(settings.SpeechBubbleScale, 0.5f, 6f);
                int ringCount = textOnBubble ? count - 1 : count;
                int colourStart = Random.Range(0, BalloonPalette.Length);
                Vector3 knot = new Vector3(0f, settings.BalloonHeight, 0f);

                // The bubble goes straight up from the middle; its longer (scaled) string lifts it above the rest.
                if (textOnBubble)
                    AttachBalloon(speechBubble, knot, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f), bubbleScale, Color.white, Color.black, config.BalloonText);

                if (prefabs.Count == 0)
                    return;

                float yawOffset = Random.Range(0f, 360f);
                for (int i = 0; i < ringCount; i++)
                {
                    // Alternate a steeper outer tilt with a gentler inner one once there are many, so they don't overlap.
                    bool outer = ringCount > 6 && i % 2 == 1;
                    float tilt = (ringCount == 1 ? 0f : outer ? 24f : 13f) + Random.Range(-3f, 3f);
                    float yaw = yawOffset + 360f * i / ringCount;
                    Quaternion rotation = Quaternion.Euler(0f, yaw, 0f) * Quaternion.Euler(tilt, Random.Range(0f, 360f), 0f);

                    string prefab = prefabs[Random.Range(0, prefabs.Count)];
                    Color colour = BalloonPalette[(colourStart + i) % BalloonPalette.Length];
                    float size = scale * Random.Range(0.9f, 1.1f);
                    AttachBalloon(prefab, knot, rotation, size, colour, Color.white, textOnBubble ? null : config.BalloonText);
                }
            }

            private void AttachBalloon(string prefab, Vector3 localPosition, Quaternion localRotation, float scale, Color balloonColour, Color textColour, string text)
            {
                PartyBalloon balloon = GameManager.server.CreateEntity(prefab, _animal.transform.position) as PartyBalloon;
                if (balloon == null)
                {
                    _instance.WarnOnce($"Balloon prefab '{prefab}' could not be spawned.");
                    return;
                }

                balloon.enableSaving = false;
                balloon.SetParent(_animal);
                balloon.transform.localPosition = localPosition;
                balloon.transform.localRotation = localRotation;
                if (!Mathf.Approximately(scale, 1f))
                {
                    balloon.networkEntityScale = true;
                    balloon.transform.localScale = Vector3.one * scale;
                }
                balloon.Spawn();

                balloon.SetBalloonColour(balloonColour);
                balloon.SetTextColour(textColour);
                if (!string.IsNullOrEmpty(text))
                    balloon.SetBalloonText(text);
                balloon.SendNetworkUpdate();

                Rigidbody body = balloon.GetComponent<Rigidbody>();
                if (body != null)
                {
                    body.isKinematic = true;
                    body.useGravity = false;
                }

                _balloons.Add(balloon);
                _balloonsAttached = _balloons.Count;
            }

            // Each balloon shot off makes the animal drop faster, up to MaxPoppedSpeedMultiplier with none left.
            private float CurrentSpeed()
            {
                if (_balloonsAttached == 0 || !_instance._config.General.PoppedBalloonsSpeedUp)
                    return _speed;

                int alive = _balloons.Count(b => b != null && !b.IsDestroyed);
                float popped = 1f - (float)alive / _balloonsAttached;
                return _speed * Mathf.Lerp(1f, MaxPoppedSpeedMultiplier, popped);
            }

            private void FixedUpdate()
            {
                if (_finished)
                    return;

                if (_animal == null || _animal.IsDestroyed)
                {
                    Abort();
                    return;
                }

                // NpcSleepingComponent re-activates the FSM when players come near, so hold it off every step.
                if (_fsm != null)
                    _fsm.SetFsmActive(false);

                Vector3 position = _animal.transform.position;
                if (IsOutOfBounds(position))
                {
                    _animal.Kill();
                    Abort();
                    return;
                }

                float ground = GetGroundHeight(position);
                float nextY = position.y - CurrentSpeed() * Time.fixedDeltaTime;

                if (nextY <= ground || Time.realtimeSinceStartup - _startedAt > MaxDescentSeconds)
                {
                    Land(new Vector3(position.x, ground, position.z));
                    return;
                }

                _animal.transform.position = new Vector3(position.x, nextY, position.z);
                _animal.SendNetworkUpdate_Position();
            }

            public PendingDrop Drop => _drop;
            public bool InFlight => !_finished && _animal != null && !_animal.IsDestroyed;
            public int BalloonsLeft => _balloons.Count(b => b != null && !b.IsDestroyed);
            public Vector3 GroundBelow
            {
                get
                {
                    Vector3 position = _animal.transform.position;
                    return new Vector3(position.x, GetGroundHeight(position), position.z);
                }
            }
            public float HeightAboveGround => _animal.transform.position.y - GroundBelow.y;

            // Balloons are released first so they float away instead of vanishing with the animal.
            public void Cancel()
            {
                if (_finished)
                    return;

                BaseEntity animal = _animal;
                Abort();
                if (animal != null && !animal.IsDestroyed)
                    animal.Kill();
            }

            public void ForceLand()
            {
                if (_finished || _animal == null || _animal.IsDestroyed)
                {
                    Abort();
                    return;
                }

                Vector3 position = _animal.transform.position;
                Land(new Vector3(position.x, GetGroundHeight(position), position.z));
            }

            private bool TryFindWalkableGround(ref Vector3 groundPosition)
            {
                Vector3 walkable;
                if (!TrySampleDryGround(groundPosition, out walkable) && !TrySearchRings(groundPosition, out walkable))
                    return false;

                if (Vector3.Distance(groundPosition, walkable) >= MovedReportDistance && !_drop.MovedToGround)
                {
                    _drop.MovedToGround = true;
                    _instance?.AddMovedMarker(_drop, walkable);
                }

                groundPosition = walkable;
                return true;
            }

            // The seabed counts as navmesh, so anything deeper than wading depth is rejected.
            private bool TrySampleDryGround(Vector3 position, out Vector3 walkable)
            {
                return _npc.TrySampleNavmesh(position, WalkableSampleRadius, out walkable)
                    && WaterLevel.GetWaterDepth(walkable, false, false) <= MaxWadingDepth;
            }

            // A single wide sample returns the closest navmesh, which offshore is always seabed, so sample around
            // the landing spot instead. Closest ring first: the animal only moves as far as it has to.
            private bool TrySearchRings(Vector3 centre, out Vector3 walkable)
            {
                foreach (float distance in SearchRingDistances)
                {
                    for (int i = 0; i < SearchDirections; i++)
                    {
                        Vector3 point = centre + Quaternion.Euler(0f, 360f * i / SearchDirections, 0f) * Vector3.forward * distance;
                        point.y = TerrainMeta.HeightMap.GetHeight(point);
                        if (TrySampleDryGround(point, out walkable))
                            return true;
                    }
                }

                walkable = default(Vector3);
                return false;
            }

            private void Land(Vector3 groundPosition)
            {
                // Without an agent there is nothing to check against, so it lands where it is.
                if (_npc != null && _agent != null && !TryFindWalkableGround(ref groundPosition))
                {
                    _instance?.PrintWarning($"{_npc.ShortPrefabName} had no walkable ground near {groundPosition}; removing it.");
                    _drop.NoWalkableGround = true;
                    Cancel();
                    return;
                }

                _finished = true;
                ReleaseBalloons();

                if (_body != null)
                    _body.isKinematic = _bodyWasKinematic;

                if (_npc != null)
                    ResumeGen2AI(groundPosition);
                else
                    ResumeLegacyAI(groundPosition);

                _animal.SendNetworkUpdateImmediate();
                _instance?.OnAnimalLanded(_animal, _drop);
                Destroy(this);
            }

            private void ResumeLegacyAI(Vector3 groundPosition)
            {
                Vector3 finalPosition = groundPosition;
                NavMeshHit navHit;
                if (NavMesh.SamplePosition(groundPosition, out navHit, 4f, NavMesh.AllAreas)
                    && Mathf.Abs(navHit.position.y - groundPosition.y) < 2f)
                {
                    finalPosition = navHit.position;
                }

                _animal.transform.position = finalPosition;

                foreach (Behaviour behaviour in _suspended)
                {
                    if (behaviour == null)
                        continue;

                    behaviour.enabled = true;
                    NavMeshAgent agent = behaviour as NavMeshAgent;
                    if (agent != null)
                        agent.Warp(finalPosition);
                }
            }

            private void Abort()
            {
                // Already landed: it was counted then. A dead (destroyed) animal still counts as delivered.
                bool alreadyCounted = _finished;
                _finished = true;
                ReleaseBalloons();
                if (!alreadyCounted)
                    _instance?.OnAnimalLost(_animal, _drop);
                Destroy(this);
            }

            // On landing the balloons let go and drift up into the sky before disappearing.
            private void ReleaseBalloons()
            {
                foreach (BaseEntity balloon in _balloons)
                {
                    if (balloon == null || balloon.IsDestroyed)
                        continue;

                    balloon.SetParent(null, true, true);
                    balloon.gameObject.AddComponent<BalloonRelease>().Init(balloon);
                }

                _balloons.Clear();
            }

            private void OnDestroy() => _instance?._descents.Remove(this);

            private static float GetGroundHeight(Vector3 position)
            {
                RaycastHit hit;
                if (Physics.Raycast(position + Vector3.up * 0.5f, Vector3.down, out hit, 1000f, GroundMask, QueryTriggerInteraction.Ignore))
                    return hit.point.y;

                return TerrainMeta.HeightMap.GetHeight(position);
            }

            private static bool IsOutOfBounds(Vector3 position)
            {
                float halfSize = TerrainMeta.Size.x * 0.5f + 250f;
                return Mathf.Abs(position.x) > halfSize || Mathf.Abs(position.z) > halfSize || position.y < -100f;
            }
        }

        private class BalloonRelease : MonoBehaviour
        {
            private static readonly List<BalloonRelease> Active = new List<BalloonRelease>();

            private BaseEntity _balloon;
            private float _killAt;
            private Vector3 _drift;

            public void Init(BaseEntity balloon)
            {
                _balloon = balloon;
                _killAt = Time.realtimeSinceStartup + BalloonReleaseSeconds;
                _drift = new Vector3(Random.Range(-0.6f, 0.6f), BalloonRiseSpeed, Random.Range(-0.6f, 0.6f));
                Active.Add(this);
            }

            private void FixedUpdate()
            {
                if (_balloon == null || _balloon.IsDestroyed || Time.realtimeSinceStartup >= _killAt)
                {
                    Finish();
                    return;
                }

                _balloon.transform.position += _drift * Time.fixedDeltaTime;
                _balloon.SendNetworkUpdate_Position();
            }

            private void Finish()
            {
                if (_balloon != null && !_balloon.IsDestroyed)
                    _balloon.Kill();
                Destroy(this);
            }

            private void OnDestroy() => Active.Remove(this);

            public static void KillAll()
            {
                foreach (BalloonRelease release in Active.ToList())
                {
                    if (release != null)
                        release.Finish();
                }
            }
        }

        #endregion

        #region Commands

        // Plain console + chat commands: the covalence [Command] gave no response over RCON.
        [ConsoleCommand("giveanimal")]
        private void ConsoleGiveAnimal(ConsoleSystem.Arg arg)
        {
            BasePlayer caller = arg.Player();
            string callerId = caller?.UserIDString;
            if (caller != null && !permission.UserHasPermission(callerId, PermAdmin))
            {
                arg.ReplyWith(Msg("NoPermission", callerId));
                return;
            }

            string[] args = arg.Args?.Select(a => a.ToString()).ToArray() ?? new string[0];
            arg.ReplyWith(GiveAnimal(callerId, args));
        }

        [ChatCommand("giveanimal")]
        private void ChatGiveAnimal(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                player.ChatMessage(Msg("NoPermission", player.UserIDString));
                return;
            }

            player.ChatMessage(GiveAnimal(player.UserIDString, args));
        }

        // Returns the reply for the caller; callerId is null for the server console / RCON.
        private string GiveAnimal(string callerId, string[] args)
        {
            if (args.Length < 2)
                return Msg("Usage", callerId, AnimalNameList());

            string error;
            BasePlayer target = FindTargetPlayer(callerId, args[0], out error);
            if (target == null)
                return error;

            string animalName = string.Join(" ", args.Skip(1));
            AnimalConfig animal = FindAnimalByName(animalName);
            if (animal == null)
                return Msg("AnimalNotFound", callerId, animalName, AnimalNameList());

            Item item = ItemManager.CreateByName(SupplySignalShortname, 1, animal.SkinId);
            if (item == null)
                return Msg("ItemFailed", callerId);

            item.name = animal.Name;
            target.GiveItem(item);

            Puts($"Gave '{animal.Name}' signal (skin {animal.SkinId}) to {target.displayName} [{target.UserIDString}].");
            if (target.IsConnected)
                target.ChatMessage(Msg("Received", target.UserIDString, animal.Name));

            return Msg("Given", callerId, animal.Name, target.displayName);
        }

        [ConsoleCommand("livestockairdrops.prefabs")]
        private void CmdFindPrefabs(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !permission.UserHasPermission(arg.Connection.userid.ToString(), PermAdmin))
            {
                arg.ReplyWith(Msg("NoPermission", arg.Connection.userid.ToString()));
                return;
            }

            // Every space-separated term must appear in the path, e.g. "rust.ai ram".
            string filter = arg.HasArgs() ? string.Join(" ", arg.Args).ToLowerInvariant() : "livestock";
            string[] terms = filter.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            List<string> matches = StringPool.toNumber.Keys
                .Where(path => path.EndsWith(".prefab") && terms.All(term => path.Contains(term)))
                .OrderBy(path => path)
                .ToList();

            if (matches.Count == 0)
            {
                arg.ReplyWith($"No prefabs containing '{filter}'.");
                return;
            }

            const int maxResults = 100;
            string result = string.Join("\n", matches.Take(maxResults));
            if (matches.Count > maxResults)
                result += $"\n... and {matches.Count - maxResults} more; narrow the filter.";

            arg.ReplyWith($"{matches.Count} prefab(s) containing '{filter}':\n{result}");
        }

        private BasePlayer FindTargetPlayer(string callerId, string query, out string error)
        {
            error = null;
            List<BasePlayer> players = BasePlayer.activePlayerList.Concat(BasePlayer.sleepingPlayerList).ToList();

            BasePlayer byId = players.FirstOrDefault(p => p.UserIDString == query);
            if (byId != null)
                return byId;

            List<BasePlayer> exact = players.Where(p => p.displayName.Equals(query, System.StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1)
                return exact[0];

            List<BasePlayer> partial = players.Where(p => p.displayName.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (partial.Count == 1)
                return partial[0];

            error = partial.Count == 0
                ? Msg("PlayerNotFound", callerId, query)
                : Msg("MultiplePlayers", callerId, query, string.Join(", ", partial.Select(p => p.displayName)));
            return null;
        }

        #endregion

        #region Admin Panel

        private const string UiRoot = "LivestockAirdrops.Root";
        private const string UiBody = "LivestockAirdrops.Body";
        private const string UiCommand = "livestockairdrops.ui";
        private const string UiBold = "robotocondensed-bold.ttf";
        private const string UiRegular = "robotocondensed-regular.ttf";
        private const string UiDefaultStatus = "Saved changes apply to the next drop.";
        private const string TabAnimals = "animals";
        private const string TabSettings = "settings";
        private const string TabActive = "active";
        private const string TabHistory = "history";
        private const string UiHistoryStatus = "Most recent first. Test drops are not recorded.";
        private const int HistoryRowsPerPage = 10;
        private const string UiActiveStatus = "Updates live while this panel is open.";
        private const string UiActiveTabLabel = "LivestockAirdrops.ActiveTab";
        private const int ActiveDropsPerPage = 5;
        private const float AdminPanelRefreshSeconds = 1f;
        private const int MaxDisplayedNameLength = 20;
        private const int PanelWidth = 1000;
        private const int PanelHeight = 580;
        private const int FooterTop = 520;
        private const int MaxAnimalsWithoutPaging = 6;
        private const int AnimalsPerPage = 5;
        private const int MaxSpawnCount = 10;
        private const float TestDropForwardDistance = 12f;

        private static class UiColor
        {
            public static readonly string Backdrop = "0 0 0 0.6";
            public static readonly string Panel = Hex("26271f");
            public static readonly string Bar = Hex("1d1e18");
            public static readonly string Inset = Hex("1f2019");
            public static readonly string Field = Hex("17180f");
            public static readonly string Row = Hex("2a2b23");
            public static readonly string RowSelected = Hex("4f5043");
            public static readonly string Tab = Hex("33342b");
            public static readonly string TabSelected = Hex("a8361f");
            public static readonly string Neutral = Hex("45463a");
            public static readonly string Green = Hex("4f6b24");
            public static readonly string Blue = Hex("2f6f9f");
            public static readonly string Red = Hex("9f3220");
            public static readonly string Amber = Hex("d9a441");
            public static readonly string Text = Hex("e8e2d6");
            public static readonly string Muted = Hex("c4beae");
            public static readonly string OnGreen = Hex("f0f6df");
            public static readonly string White = "1 1 1 1";
            public static readonly string ErrorText = Hex("ff9b85");
            public static readonly string Clear = "0 0 0 0";

            private static string Hex(string hex)
            {
                int value = System.Convert.ToInt32(hex, 16);
                return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###} 1",
                    ((value >> 16) & 255) / 255f, ((value >> 8) & 255) / 255f, (value & 255) / 255f);
            }
        }

        private class AdminPanelState
        {
            public PluginConfig Draft;
            public string Tab = TabAnimals;
            public int AnimalIndex;
            public string GiveTarget = "";
            public string Status;
            public bool StatusIsError;
            public int ActivePage;
            public string ActiveSignature;
            public int ShownActiveCount;
            public int HistoryPage;
            public int ShownHistoryVersion;
        }

        private class SmokeStop
        {
            public PendingDrop Drop;
            public Vector3 Position;
            public float EndsAt;
        }

        private class ActiveDrop
        {
            public PendingDrop Drop;
            public string Status;
            public string StatusColor;
            public string StatusTextColor;
            public string Detail;
            public string Height;
            public Vector3 Position;
            public bool CanLand;
            public bool CanCancel;
        }

        private class UiSetting
        {
            public string Key;
            public string Label;
            public System.Func<GeneralSettings, bool> GetToggle;
            public System.Action<GeneralSettings, bool> SetToggle;
            public System.Func<GeneralSettings, float> GetNumber;
            public System.Action<GeneralSettings, float> SetNumber;
            public float Increment;
            public float Min;
            public float Max;
            public string Format = "0";

            public bool IsToggle => GetToggle != null;

            // Values hand-set outside the range are left alone until stepped back towards it, so a click never jumps them.
            public void Step(GeneralSettings settings, int direction)
            {
                float value = GetNumber(settings);
                float next = direction > 0
                    ? (value >= Max ? value : Mathf.Min(value + Increment, Max))
                    : (value <= Min ? value : Mathf.Max(value - Increment, Min));
                SetNumber(settings, Mathf.Round(next * 100f) / 100f);
            }
        }

        private class UiSettingGroup
        {
            public string Title;
            public UiSetting[] Settings;
        }

        private static UiSetting Toggle(string key, string label, System.Func<GeneralSettings, bool> get, System.Action<GeneralSettings, bool> set)
        {
            return new UiSetting { Key = key, Label = label, GetToggle = get, SetToggle = set };
        }

        private static UiSetting Number(string key, string label, System.Func<GeneralSettings, float> get, System.Action<GeneralSettings, float> set, float increment, float min, float max, string format = "0")
        {
            return new UiSetting { Key = key, Label = label, GetNumber = get, SetNumber = set, Increment = increment, Min = min, Max = max, Format = format };
        }

        // Limits match the clamps the drop code applies.
        private static readonly UiSettingGroup[] LeftSettingGroups =
        {
            new UiSettingGroup { Title = "PLANE", Settings = new[]
            {
                Number("altitude", "Drop altitude (metres)", s => s.DropAltitude, (s, v) => s.DropAltitude = v, 10f, 50f, 500f),
                Number("planespeed", "Flight speed", s => s.PlaneSpeed, (s, v) => s.PlaneSpeed = v, 5f, 10f, 150f)
            } },
            new UiSettingGroup { Title = "DESCENT", Settings = new[]
            {
                Number("descent", "Descent speed modifier", s => s.DescentSpeedModifier, (s, v) => s.DescentSpeedModifier = v, 0.1f, 0.1f, 5f, "0.0"),
                Toggle("protect", "Prevent landing damage", s => s.PreventLandingDamage, (s, v) => s.PreventLandingDamage = v),
                Toggle("popped", "Popped balloons speed up descent", s => s.PoppedBalloonsSpeedUp, (s, v) => s.PoppedBalloonsSpeedUp = v)
            } }
        };

        private static readonly UiSettingGroup[] RightSettingGroups =
        {
            new UiSettingGroup { Title = "BALLOONS", Settings = new[]
            {
                Toggle("balloons", "Use party balloons", s => s.UseBalloons, (s, v) => s.UseBalloons = v),
                Number("balloons.count", "Balloon count", s => s.BalloonCount, (s, v) => s.BalloonCount = Mathf.RoundToInt(v), 1f, 1f, 16f),
                Number("balloons.scale", "Balloon scale", s => s.BalloonScale, (s, v) => s.BalloonScale = v, 0.5f, 0.5f, 5f, "0.0"),
                Number("balloons.bubble", "Speech bubble scale", s => s.SpeechBubbleScale, (s, v) => s.SpeechBubbleScale = v, 0.5f, 0.5f, 6f, "0.0"),
                Number("balloons.height", "Height above animal", s => s.BalloonHeight, (s, v) => s.BalloonHeight = v, 0.1f, 0f, 5f, "0.0")
            } },
            new UiSettingGroup { Title = "AFTER LANDING", Settings = new[]
            {
                Toggle("lead", "Thrower can lead animal immediately", s => s.TrustThrower, (s, v) => s.TrustThrower = v),
                Toggle("owner", "Thrower owns the animal", s => s.ThrowerOwnsAnimal, (s, v) => s.ThrowerOwnsAnimal = v),
                Number("smoke", "Stop smoke after (seconds, -1 = vanilla)", s => s.StopSmokeAfterLanding, (s, v) => s.StopSmokeAfterLanding = v, 5f, -1f, 210f)
            } }
        };

        private static UiSetting FindSetting(string key)
        {
            return LeftSettingGroups.Concat(RightSettingGroups).SelectMany(g => g.Settings).FirstOrDefault(s => s.Key == key);
        }

        [ChatCommand("livestockadmin")]
        private void ChatLivestockAdmin(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                player.ChatMessage(Msg("NoPermission", player.UserIDString));
                return;
            }

            OpenAdminPanel(player);
        }

        [ConsoleCommand(UiCommand)]
        private void ConsoleAdminUi(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            AdminPanelState state;
            if (!_adminPanels.TryGetValue(player.userID, out state))
                return;

            if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                CloseAdminPanel(player);
                return;
            }

            string[] args = arg.Args?.Select(a => a.ToString()).ToArray() ?? new string[0];
            string action = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (action == "close")
            {
                CloseAdminPanel(player);
                return;
            }

            // Typed text is stored without redrawing: it arrives as the admin clicks away, and a redraw
            // at that moment could replace the button they are in the middle of clicking.
            if (action == "text" || action == "target")
            {
                StoreAdminText(state, args, action);
                return;
            }

            state.Status = null;
            state.StatusIsError = false;
            RunAdminAction(player, state, action, args);

            // Teleporting closes the panel.
            if (_adminPanels.ContainsKey(player.userID))
                DrawAdminPanel(player, state);
        }

        private static void StoreAdminText(AdminPanelState state, string[] args, string action)
        {
            if (action == "target")
            {
                state.GiveTarget = string.Join(" ", args.Skip(1)).Trim();
                return;
            }

            AnimalConfig animal = SelectedAnimal(state);
            if (animal == null || args.Length < 2)
                return;

            string value = string.Join(" ", args.Skip(2)).Trim();
            switch (args[1])
            {
                case "balloon": animal.BalloonText = value; break;
                case "note": animal.Notification = value; break;
            }
        }

        private void RunAdminAction(BasePlayer player, AdminPanelState state, string action, string[] args)
        {
            string value = args.Length > 1 ? args[1] : "";
            int number;
            int.TryParse(value, out number);
            AnimalConfig animal = SelectedAnimal(state);

            switch (action)
            {
                case "tab":
                    state.Tab = value == TabSettings || value == TabActive || value == TabHistory ? value : TabAnimals;
                    break;

                case "historypage":
                    state.HistoryPage += System.Math.Sign(number);
                    break;

                case "activepage":
                    state.ActivePage += System.Math.Sign(number);
                    break;

                case "drop":
                    RunDropAction(player, state, value, args);
                    break;

                case "animal":
                    state.AnimalIndex = Mathf.Clamp(number, 0, Mathf.Max(0, state.Draft.Airdrops.Count - 1));
                    break;

                case "sex":
                    if (animal != null && !SexIsFixed(animal) && (value == "Random" || value == "Male" || value == "Female"))
                        animal.Sex = value;
                    break;

                case "spawn":
                    if (animal != null)
                        animal.SpawnCount = Mathf.Clamp(animal.SpawnCount + System.Math.Sign(number), 1, MaxSpawnCount);
                    break;

                case "wipelimit":
                    if (animal != null)
                        animal.WipeLimit = Mathf.Clamp(animal.WipeLimit + System.Math.Sign(number), 0, MaxWipeLimit);
                    break;

                case "step":
                {
                    UiSetting setting = FindSetting(value);
                    int direction;
                    if (setting != null && !setting.IsToggle && args.Length > 2 && int.TryParse(args[2], out direction))
                        setting.Step(state.Draft.General, System.Math.Sign(direction));
                    break;
                }

                case "toggle":
                {
                    UiSetting setting = FindSetting(value);
                    if (setting != null && setting.IsToggle)
                        setting.SetToggle(state.Draft.General, !setting.GetToggle(state.Draft.General));
                    break;
                }

                case "give":
                    GiveFromAdminPanel(player, state, value == "me");
                    break;

                case "test":
                    TestDropFromAdminPanel(player, state);
                    break;

                case "save":
                    SaveAdminPanel(player, state);
                    break;

                case "revert":
                    state.Draft = CloneConfig();
                    state.Status = "Changes reverted.";
                    break;
            }
        }

        private void GiveFromAdminPanel(BasePlayer player, AdminPanelState state, bool toSelf)
        {
            AnimalConfig saved = SavedAnimal(state);
            if (saved == null)
                return;

            if (!toSelf && string.IsNullOrEmpty(state.GiveTarget))
            {
                SetAdminError(state, "Type a player name or Steam ID first.");
                return;
            }

            string target = toSelf ? player.UserIDString : state.GiveTarget;
            state.Status = GiveAnimal(player.UserIDString, new[] { target, saved.Name });
        }

        // Skips the plane: the animal starts at the configured altitude a few metres in front of the admin.
        private void TestDropFromAdminPanel(BasePlayer player, AdminPanelState state)
        {
            AnimalConfig saved = SavedAnimal(state);
            if (saved == null)
                return;

            if (HasUnsavedChanges(state))
            {
                SetAdminError(state, "Save your changes first. Test drops use the saved settings.");
                return;
            }

            Vector3 forward = player.eyes.HeadForward();
            forward.y = 0f;
            Vector3 position = player.transform.position + forward.normalized * TestDropForwardDistance;
            position.y = Mathf.Max(TerrainMeta.HeightMap.GetHeight(position), player.transform.position.y) + _config.General.DropAltitude;

            PendingDrop pending = new PendingDrop { Animal = saved, OwnerId = player.userID, Id = ++_nextDropId, IsTest = true };
            SpawnHerd(position, pending);
            if (pending.AnimalsInFlight == 0)
            {
                SetAdminError(state, $"'{saved.Name}' could not be spawned. Check the server console.");
                return;
            }

            Puts($"{player.displayName} [{player.UserIDString}] test dropped '{saved.Name}'.");
            state.Status = $"Test drop started: {saved.Name}.";
        }

        private void RunDropAction(BasePlayer player, AdminPanelState state, string action, string[] args)
        {
            int id;
            ActiveDrop drop = args.Length > 2 && int.TryParse(args[2], out id)
                ? BuildActiveDrops().FirstOrDefault(d => d.Drop.Id == id)
                : null;

            if (drop == null)
            {
                SetAdminError(state, "That drop has already finished.");
                return;
            }

            string animalName = drop.Drop.Animal.Name;
            if (action == "tp")
            {
                player.EnsureDismounted();
                player.Teleport(drop.Position + Vector3.up * 0.5f);
                CloseAdminPanel(player);
            }
            else if (action == "land" && drop.CanLand)
            {
                foreach (DescentController descent in DescentsOf(drop.Drop))
                    descent.ForceLand();

                Puts($"{player.displayName} [{player.UserIDString}] landed '{animalName}' (drop {drop.Drop.Id}).");
                state.Status = $"{animalName} landed.";
            }
            else if (action == "cancel" && drop.CanCancel)
            {
                CancelDrop(drop.Drop);
                Puts($"{player.displayName} [{player.UserIDString}] cancelled '{animalName}' (drop {drop.Drop.Id}).");
                state.Status = drop.Drop.IsTest ? $"Test drop of {animalName} cancelled." : $"{animalName} cancelled. The signal goes back to its owner.";
            }
        }

        private void CancelDrop(PendingDrop pending)
        {
            foreach (BaseEntity signal in _armedSignals.Where(e => e.Value == pending).Select(e => e.Key).ToList())
            {
                _armedSignals.Remove(signal);
                if (signal != null && !signal.IsDestroyed)
                    signal.Kill();
            }

            foreach (CargoPlane plane in _planes.Where(e => e.Value == pending).Select(e => e.Key).ToList())
            {
                _planes.Remove(plane);
                if (plane != null && !plane.IsDestroyed)
                    plane.Kill();
            }

            // Refund first: it stops the smoke and clears the signal, so the animals below can't schedule another stop.
            RefundDrop(pending, "an admin cancelled it", true);

            foreach (DescentController descent in DescentsOf(pending))
                descent.Cancel();
        }

        private List<DescentController> DescentsOf(PendingDrop pending)
        {
            return _descents.Where(d => d != null && d.InFlight && d.Drop == pending).ToList();
        }

        private List<ActiveDrop> BuildActiveDrops()
        {
            List<ActiveDrop> drops = new List<ActiveDrop>();

            foreach (KeyValuePair<BaseEntity, PendingDrop> entry in _armedSignals)
            {
                if (entry.Key == null || entry.Key.IsDestroyed)
                    continue;

                drops.Add(new ActiveDrop
                {
                    Drop = entry.Value, Status = "SIGNAL THROWN", StatusColor = UiColor.Neutral, StatusTextColor = UiColor.Text,
                    Detail = "Waiting for plane", Height = "-", Position = entry.Key.transform.position, CanCancel = true
                });
            }

            foreach (KeyValuePair<CargoPlane, PendingDrop> entry in _planes)
            {
                CargoPlane plane = entry.Key;
                if (plane == null || plane.IsDestroyed)
                    continue;

                Vector3 dropPoint = plane.dropPosition;
                dropPoint.y = TerrainMeta.HeightMap.GetHeight(dropPoint);
                int secondsLeft = Mathf.CeilToInt(Mathf.Max(0f, plane.secondsToTake * 0.5f - plane.secondsTaken));
                drops.Add(new ActiveDrop
                {
                    Drop = entry.Value, Status = "PLANE INBOUND", StatusColor = UiColor.Blue, StatusTextColor = UiColor.White,
                    Detail = $"Drop in {secondsLeft} s", Height = "-", Position = dropPoint, CanCancel = true
                });
            }

            foreach (IGrouping<PendingDrop, DescentController> group in _descents.Where(d => d != null && d.InFlight).GroupBy(d => d.Drop))
            {
                DescentController first = group.First();
                int balloons = group.Sum(d => d.BalloonsLeft);
                drops.Add(new ActiveDrop
                {
                    Drop = group.Key, Status = "DESCENDING", StatusColor = UiColor.Amber, StatusTextColor = UiColor.Bar,
                    Detail = balloons > 0 ? $"{balloons} balloons left" : "No balloons",
                    Height = $"{Mathf.RoundToInt(group.Max(d => d.HeightAboveGround))} m",
                    Position = first.GroundBelow, CanLand = true, CanCancel = !group.Key.AnyLanded
                });
            }

            foreach (SmokeStop stop in _smokeStops)
            {
                int secondsLeft = Mathf.CeilToInt(Mathf.Max(0f, stop.EndsAt - Time.realtimeSinceStartup));
                drops.Add(new ActiveDrop
                {
                    Drop = stop.Drop, Status = "LANDED", StatusColor = UiColor.Green, StatusTextColor = UiColor.OnGreen,
                    Detail = $"Smoke stops in {secondsLeft} s", Height = "0 m", Position = stop.Position
                });
            }

            return drops.OrderBy(d => d.Drop.Id).ToList();
        }

        private static List<ActiveDrop> VisibleDrops(AdminPanelState state, List<ActiveDrop> drops)
        {
            int lastPage = Mathf.Max(0, (drops.Count - 1) / ActiveDropsPerPage);
            state.ActivePage = Mathf.Clamp(state.ActivePage, 0, lastPage);
            return drops.Skip(state.ActivePage * ActiveDropsPerPage).Take(ActiveDropsPerPage).ToList();
        }

        // Everything that decides which rows and buttons exist. Heights and countdowns are updated in place.
        private static string ActiveSignature(AdminPanelState state, List<ActiveDrop> drops)
        {
            return $"{drops.Count}|{state.ActivePage}|" + string.Join(",", VisibleDrops(state, drops).Select(d => $"{d.Drop.Id}:{d.Status}:{d.CanLand}:{d.CanCancel}").ToArray());
        }

        private static string ActiveTabText(int count) => count > 0 ? $"ACTIVE DROPS ({count})" : "ACTIVE DROPS";

        private static string DropLabelName(int id, string field) => $"{UiBody}.Drop{id}.{field}";

        private string DropOwnerName(PendingDrop drop)
        {
            BasePlayer owner = BasePlayer.FindAwakeOrSleepingByID(drop.OwnerId);
            string name = owner != null ? owner.displayName : covalence.Players.FindPlayerById(drop.OwnerId.ToString())?.Name ?? drop.OwnerId.ToString();
            if (name.Length > MaxDisplayedNameLength)
                name = name.Substring(0, MaxDisplayedNameLength);

            return drop.IsTest ? $"{name} (test)" : name;
        }

        // Sends only changed text: redrawing would lose typing on the other tabs and could swallow a click.
        private void RefreshAdminPanels()
        {
            if (_adminPanels.Count == 0)
                return;

            List<ActiveDrop> drops = BuildActiveDrops();
            foreach (ulong adminId in _adminPanels.Keys.ToList())
            {
                BasePlayer player = BasePlayer.FindByID(adminId);
                if (player == null || !player.IsConnected)
                    continue;

                if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
                {
                    CloseAdminPanel(player);
                    continue;
                }

                AdminPanelState state = _adminPanels[adminId];
                bool historyChanged = state.Tab == TabHistory && state.ShownHistoryVersion != _historyVersion;
                if (historyChanged || (state.Tab == TabActive && state.ActiveSignature != ActiveSignature(state, drops)))
                {
                    DrawAdminPanel(player, state);
                    continue;
                }

                CuiElementContainer updates = new CuiElementContainer();
                if (state.ShownActiveCount != drops.Count)
                {
                    state.ShownActiveCount = drops.Count;
                    UiUpdateLabel(updates, UiActiveTabLabel, ActiveTabText(drops.Count), 15, state.Tab == TabActive ? UiColor.White : UiColor.Text, TextAnchor.MiddleCenter, true);
                }

                if (state.Tab == TabActive)
                {
                    foreach (ActiveDrop drop in VisibleDrops(state, drops))
                    {
                        UiUpdateLabel(updates, DropLabelName(drop.Drop.Id, "Detail"), drop.Detail, 11, UiColor.Muted, TextAnchor.MiddleLeft);
                        UiUpdateLabel(updates, DropLabelName(drop.Drop.Id, "Height"), drop.Height, 15, UiColor.Text, TextAnchor.MiddleLeft);
                    }
                }

                if (updates.Count > 0)
                    CuiHelper.AddUi(player, updates);
            }
        }

        private void DrawActiveTab(CuiElementContainer c, AdminPanelState state, List<ActiveDrop> drops)
        {
            if (drops.Count == 0)
            {
                UiLabel(c, 24, 104, 900, 40, "No drops in progress.", 18, UiColor.Muted, TextAnchor.MiddleLeft);
                return;
            }

            UiLabel(c, 40, 104, 200, 20, "ANIMAL", 12, UiColor.Muted, TextAnchor.MiddleLeft, true);
            UiLabel(c, 250, 104, 140, 20, "THROWN BY", 12, UiColor.Muted, TextAnchor.MiddleLeft, true);
            UiLabel(c, 400, 104, 150, 20, "STATUS", 12, UiColor.Muted, TextAnchor.MiddleLeft, true);
            UiLabel(c, 556, 104, 70, 20, "HEIGHT", 12, UiColor.Muted, TextAnchor.MiddleLeft, true);
            UiLabel(c, 630, 104, 60, 20, "GRID", 12, UiColor.Muted, TextAnchor.MiddleLeft, true);

            int top = 130;
            foreach (ActiveDrop drop in VisibleDrops(state, drops))
            {
                UiPanel(c, 24, top, 952, 62, UiColor.Inset);
                UiSignalIcon(c, 32, top + 7, 48, drop.Drop.Animal.SkinId);
                UiLabel(c, 88, top, 156, 62, drop.Drop.Animal.Name, 15, UiColor.Text, TextAnchor.MiddleLeft, true);
                UiLabel(c, 250, top, 144, 62, DropOwnerName(drop.Drop), 14, UiColor.Text, TextAnchor.MiddleLeft);
                UiPanel(c, 400, top + 9, 124, 24, drop.StatusColor);
                UiLabel(c, 400, top + 9, 124, 24, drop.Status, 12, drop.StatusTextColor, TextAnchor.MiddleCenter, true);
                UiLabel(c, 400, top + 36, 150, 20, drop.Detail, 11, UiColor.Muted, TextAnchor.MiddleLeft, false, DropLabelName(drop.Drop.Id, "Detail"));
                UiLabel(c, 556, top, 70, 62, drop.Height, 15, UiColor.Text, TextAnchor.MiddleLeft, false, DropLabelName(drop.Drop.Id, "Height"));
                UiLabel(c, 630, top, 60, 62, MapHelper.PositionToString(drop.Position), 15, UiColor.Text, TextAnchor.MiddleLeft, true);

                UiButton(c, 744, top + 10, 50, 42, UiColor.Neutral, "TP", 14, UiColor.Text, $"drop tp {drop.Drop.Id}");
                if (drop.CanLand)
                    UiButton(c, 800, top + 10, 90, 42, UiColor.Blue, "LAND NOW", 13, UiColor.White, $"drop land {drop.Drop.Id}");
                if (drop.CanCancel)
                    UiButton(c, 896, top + 10, 72, 42, UiColor.Red, "CANCEL", 13, UiColor.White, $"drop cancel {drop.Drop.Id}");

                top += 68;
            }
        }

        private void DrawHistoryTab(CuiElementContainer c, AdminPanelState state)
        {
            if (_history.Count == 0)
            {
                UiLabel(c, 24, 104, 900, 40, "No drops recorded yet.", 18, UiColor.Muted, TextAnchor.MiddleLeft);
                return;
            }

            UiLabel(c, 34, 104, 110, 20, "TIME", 12, UiColor.Muted, TextAnchor.MiddleLeft, true);
            UiLabel(c, 150, 104, 170, 20, "ANIMAL", 12, UiColor.Muted, TextAnchor.MiddleLeft, true);
            UiLabel(c, 330, 104, 150, 20, "THROWN BY", 12, UiColor.Muted, TextAnchor.MiddleLeft, true);
            UiLabel(c, 486, 104, 50, 20, "GRID", 12, UiColor.Muted, TextAnchor.MiddleLeft, true);
            UiLabel(c, 540, 104, 100, 20, "RESULT", 12, UiColor.Muted, TextAnchor.MiddleLeft, true);
            UiLabel(c, 650, 104, 320, 20, "DETAILS", 12, UiColor.Muted, TextAnchor.MiddleLeft, true);

            int top = 126;
            foreach (DropRecord record in VisibleHistory(state))
            {
                string badgeColor, badgeText;
                HistoryBadge(record.Result, out badgeColor, out badgeText);

                UiPanel(c, 24, top, 952, 34, UiColor.Inset);
                UiLabel(c, 34, top, 110, 34, record.Time.ToString("dd MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture), 13, UiColor.Muted, TextAnchor.MiddleLeft);
                UiSignalIcon(c, 150, top + 3, 28, record.SkinId);
                UiLabel(c, 184, top, 140, 34, record.Animal, 14, UiColor.Text, TextAnchor.MiddleLeft, true);
                UiLabel(c, 330, top, 150, 34, record.OwnerName, 13, UiColor.Text, TextAnchor.MiddleLeft);
                UiLabel(c, 486, top, 50, 34, record.Grid, 13, UiColor.Text, TextAnchor.MiddleLeft, true);
                UiPanel(c, 540, top + 6, 100, 22, badgeColor);
                UiLabel(c, 540, top + 6, 100, 22, record.Result, 11, badgeText, TextAnchor.MiddleCenter, true);
                UiLabel(c, 650, top, 320, 34, record.Detail, 12, UiColor.Muted, TextAnchor.MiddleLeft);
                top += 38;
            }
        }

        private List<DropRecord> VisibleHistory(AdminPanelState state)
        {
            state.HistoryPage = Mathf.Clamp(state.HistoryPage, 0, LastPage(_history.Count, HistoryRowsPerPage));
            return Enumerable.Reverse(_history).Skip(state.HistoryPage * HistoryRowsPerPage).Take(HistoryRowsPerPage).ToList();
        }

        private static int LastPage(int count, int perPage) => Mathf.Max(0, (count - 1) / perPage);

        private static void HistoryBadge(string result, out string color, out string textColor)
        {
            switch (result)
            {
                case ResultLanded:
                    color = UiColor.Green;
                    textColor = UiColor.OnGreen;
                    break;
                case ResultRefunded:
                    color = UiColor.Blue;
                    textColor = UiColor.White;
                    break;
                case ResultCancelled:
                    color = UiColor.Amber;
                    textColor = UiColor.Bar;
                    break;
                default:
                    color = UiColor.Red;
                    textColor = UiColor.White;
                    break;
            }
        }

        private void SaveAdminPanel(BasePlayer player, AdminPanelState state)
        {
            List<AnimalConfig> drafts = state.Draft.Airdrops;
            for (int i = 0; i < drafts.Count && i < _config.Airdrops.Count; i++)
            {
                AnimalConfig draft = drafts[i];
                AnimalConfig saved = _config.Airdrops[i];

                // Copied onto the live objects so drops already in the air keep working.
                saved.Sex = draft.Sex;
                saved.SpawnCount = draft.SpawnCount;
                saved.Notification = draft.Notification;
                saved.BalloonText = draft.BalloonText;
                saved.WipeLimit = draft.WipeLimit;
            }

            _config.General = state.Draft.General;
            SaveConfig();
            state.Draft = CloneConfig();

            // Balloon prefabs are only resolved at startup when balloons were enabled then.
            if (_config.General.UseBalloons && _balloonPrefabs.Count == 0 && _speechBubblePrefab == null)
                ResolveBalloonPrefabs();

            Puts($"Settings saved from the admin panel by {player.displayName} [{player.UserIDString}].");
            state.Status = "Saved. Changes apply to the next drop.";
        }

        private static void SetAdminError(AdminPanelState state, string message)
        {
            state.Status = message;
            state.StatusIsError = true;
        }

        private PluginConfig CloneConfig() => JsonConvert.DeserializeObject<PluginConfig>(JsonConvert.SerializeObject(_config));

        private bool HasUnsavedChanges(AdminPanelState state) => JsonConvert.SerializeObject(state.Draft) != JsonConvert.SerializeObject(_config);

        private static AnimalConfig SelectedAnimal(AdminPanelState state)
        {
            List<AnimalConfig> animals = state.Draft.Airdrops;
            return state.AnimalIndex >= 0 && state.AnimalIndex < animals.Count ? animals[state.AnimalIndex] : null;
        }

        private AnimalConfig SavedAnimal(AdminPanelState state)
        {
            return state.AnimalIndex >= 0 && state.AnimalIndex < _config.Airdrops.Count ? _config.Airdrops[state.AnimalIndex] : null;
        }

        // With one prefab and no male variant, a prefab that only comes in one sex can't honour the setting.
        private static bool SexIsFixed(AnimalConfig animal)
        {
            if (!string.IsNullOrEmpty(animal.MalePrefabPath))
                return false;

            if (!PrefabExists(animal.PrefabPath))
                return true;

            GameObject prefab = GameManager.server.FindPrefab(animal.PrefabPath.ToLowerInvariant());
            LivestockAnimal livestock = prefab != null ? prefab.GetComponent<LivestockAnimal>() : null;
            return livestock == null || livestock.Form != LivestockAnimal.SexForm.Either;
        }

        private void OpenAdminPanel(BasePlayer player)
        {
            AdminPanelState state = new AdminPanelState { Draft = CloneConfig() };
            _adminPanels[player.userID] = state;

            CuiElementContainer root = new CuiElementContainer();
            root.Add(new CuiPanel
            {
                Image = { Color = UiColor.Backdrop },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, "Overlay", UiRoot);

            CuiHelper.DestroyUi(player, UiRoot);
            CuiHelper.AddUi(player, root);
            DrawAdminPanel(player, state);
        }

        private void CloseAdminPanel(BasePlayer player)
        {
            _adminPanels.Remove(player.userID);
            CuiHelper.DestroyUi(player, UiRoot);
        }

        private void DrawAdminPanel(BasePlayer player, AdminPanelState state)
        {
            CuiElementContainer c = new CuiElementContainer();
            c.Add(new CuiPanel
            {
                Image = { Color = UiColor.Panel },
                RectTransform =
                {
                    AnchorMin = "0.5 0.5",
                    AnchorMax = "0.5 0.5",
                    OffsetMin = $"{-PanelWidth / 2} {-PanelHeight / 2}",
                    OffsetMax = $"{PanelWidth / 2} {PanelHeight / 2}"
                }
            }, UiRoot, UiBody);

            UiPanel(c, 0, 0, PanelWidth, 90, UiColor.Bar);
            UiLabel(c, 20, 0, 260, 50, "LIVESTOCK AIRDROPS", 20, UiColor.Text, TextAnchor.MiddleLeft, true);
            UiLabel(c, 270, 0, 120, 50, $"v{Version}", 12, UiColor.Muted, TextAnchor.MiddleLeft);
            UiButton(c, PanelWidth - 46, 6, 38, 38, UiColor.Red, "X", 16, UiColor.White, "close");

            List<ActiveDrop> drops = BuildActiveDrops();
            state.ShownActiveCount = drops.Count;
            state.ActiveSignature = ActiveSignature(state, drops);

            UiTab(c, state, 20, 120, TabAnimals, "ANIMALS");
            UiTab(c, state, 144, 160, TabSettings, "DROP SETTINGS");
            UiTab(c, state, 308, 200, TabActive, ActiveTabText(drops.Count), UiActiveTabLabel);
            UiTab(c, state, 512, 110, TabHistory, "HISTORY");

            state.ShownHistoryVersion = _historyVersion;
            if (state.Tab == TabSettings)
                DrawSettingsTab(c, state);
            else if (state.Tab == TabActive)
                DrawActiveTab(c, state, drops);
            else if (state.Tab == TabHistory)
                DrawHistoryTab(c, state);
            else
                DrawAnimalsTab(c, state);

            bool activeTab = state.Tab == TabActive;
            bool historyTab = state.Tab == TabHistory;
            string defaultStatus = activeTab ? UiActiveStatus : historyTab ? UiHistoryStatus : UiDefaultStatus;
            UiPanel(c, 0, FooterTop, PanelWidth, PanelHeight - FooterTop, UiColor.Bar);
            UiLabel(c, 24, FooterTop, 700, 60, state.Status ?? defaultStatus, 14, state.StatusIsError ? UiColor.ErrorText : UiColor.Muted, TextAnchor.MiddleLeft);
            if (activeTab)
            {
                UiPager(c, state.ActivePage, LastPage(drops.Count, ActiveDropsPerPage), "activepage");
            }
            else if (historyTab)
            {
                UiPager(c, state.HistoryPage, LastPage(_history.Count, HistoryRowsPerPage), "historypage");
            }
            else
            {
                UiButton(c, 758, FooterTop + 9, 100, 42, UiColor.Neutral, "REVERT", 15, UiColor.Text, "revert");
                UiButton(c, 866, FooterTop + 9, 110, 42, UiColor.Green, "SAVE", 15, UiColor.OnGreen, "save");
            }

            CuiHelper.DestroyUi(player, UiBody);
            CuiHelper.AddUi(player, c);
        }

        // Built from separate parts so the label can be updated without redrawing the button.
        private void UiTab(CuiElementContainer c, AdminPanelState state, int x, int width, string tab, string label, string labelName = null)
        {
            bool selected = state.Tab == tab;
            UiPanel(c, x, 50, width, 40, selected ? UiColor.TabSelected : UiColor.Tab);
            UiLabel(c, x, 50, width, 40, label, 15, selected ? UiColor.White : UiColor.Text, TextAnchor.MiddleCenter, true, labelName);
            UiButton(c, x, 50, width, 40, UiColor.Clear, "", 10, UiColor.Text, $"tab {tab}");
        }

        private static void UiPager(CuiElementContainer c, int page, int lastPage, string command)
        {
            if (lastPage == 0)
                return;

            UiLabel(c, 856, FooterTop, 60, 60, $"{page + 1} / {lastPage + 1}", 14, UiColor.Muted, TextAnchor.MiddleCenter);
            if (page > 0)
                UiButton(c, 800, FooterTop + 9, 52, 42, UiColor.Neutral, "<", 18, UiColor.Text, $"{command} -1");
            if (page < lastPage)
                UiButton(c, 924, FooterTop + 9, 52, 42, UiColor.Neutral, ">", 18, UiColor.Text, $"{command} 1");
        }

        private void DrawAnimalsTab(CuiElementContainer c, AdminPanelState state)
        {
            List<AnimalConfig> animals = state.Draft.Airdrops;
            UiPanel(c, 0, 90, 250, FooterTop - 90, UiColor.Inset);

            if (animals.Count == 0)
            {
                UiLabel(c, 274, 104, 700, 40, "No animals are configured.", 18, UiColor.Muted, TextAnchor.MiddleLeft);
                return;
            }

            state.AnimalIndex = Mathf.Clamp(state.AnimalIndex, 0, animals.Count - 1);
            DrawAnimalList(c, state, animals);
            DrawAnimalEditor(c, state, animals[state.AnimalIndex]);
        }

        private void DrawAnimalList(CuiElementContainer c, AdminPanelState state, List<AnimalConfig> animals)
        {
            bool paged = animals.Count > MaxAnimalsWithoutPaging;
            int pageSize = paged ? AnimalsPerPage : MaxAnimalsWithoutPaging;
            int page = state.AnimalIndex / pageSize;
            int rowTop = 102;

            for (int i = page * pageSize; i < animals.Count && i < (page + 1) * pageSize; i++)
            {
                AnimalConfig animal = animals[i];
                string sex = SexIsFixed(animal) ? "Fixed sex" : animal.Sex;
                UiPanel(c, 12, rowTop, 226, 60, i == state.AnimalIndex ? UiColor.RowSelected : UiColor.Row);
                UiSignalIcon(c, 18, rowTop + 6, 48, animal.SkinId);
                UiLabel(c, 74, rowTop + 8, 160, 24, animal.Name, 15, UiColor.Text, TextAnchor.MiddleLeft, true);
                UiLabel(c, 74, rowTop + 32, 160, 20, $"Spawns {animal.SpawnCount}, {sex}", 11, UiColor.Muted, TextAnchor.MiddleLeft);
                UiButton(c, 12, rowTop, 226, 60, UiColor.Clear, "", 10, UiColor.Text, $"animal {i}");
                rowTop += 66;
            }

            if (!paged)
                return;

            int lastPage = (animals.Count - 1) / pageSize;
            UiLabel(c, 72, 470, 106, 40, $"{page + 1} / {lastPage + 1}", 14, UiColor.Muted, TextAnchor.MiddleCenter);
            if (page > 0)
                UiButton(c, 12, 470, 56, 40, UiColor.Neutral, "<", 18, UiColor.Text, $"animal {(page - 1) * pageSize}");
            if (page < lastPage)
                UiButton(c, 182, 470, 56, 40, UiColor.Neutral, ">", 18, UiColor.Text, $"animal {(page + 1) * pageSize}");
        }

        private void DrawAnimalEditor(CuiElementContainer c, AdminPanelState state, AnimalConfig animal)
        {
            UiSignalIcon(c, 274, 100, 62, animal.SkinId);
            UiLabel(c, 346, 100, 620, 36, animal.Name, 24, UiColor.Text, TextAnchor.MiddleLeft, true);
            UiLabel(c, 346, 136, 620, 22, $"Signal skin {animal.SkinId}", 12, UiColor.Muted, TextAnchor.MiddleLeft);

            // Read-only: shop items call giveanimal by this name, so renaming here would break them.
            UiLabel(c, 274, 176, 341, 18, "ANIMAL NAME", 12, UiColor.Muted, TextAnchor.MiddleLeft, true);
            UiPanel(c, 274, 196, 341, 40, UiColor.Tab);
            UiLabel(c, 284, 196, 321, 40, animal.Name, 15, UiColor.Muted, TextAnchor.MiddleLeft);
            UiField(c, 635, 176, 341, "BALLOON TEXT", animal.BalloonText, 32, "text balloon");
            UiField(c, 274, 242, 702, "NOTIFICATION MESSAGE", animal.Notification, 200, "text note");

            UiLabel(c, 274, 308, 341, 18, "SEX", 12, UiColor.Muted, TextAnchor.MiddleLeft, true);
            if (SexIsFixed(animal))
            {
                UiPanel(c, 274, 328, 341, 40, UiColor.Tab);
                UiLabel(c, 286, 328, 320, 40, "Fixed by this animal type", 14, UiColor.Muted, TextAnchor.MiddleLeft);
            }
            else
            {
                string[] options = { "Random", "Male", "Female" };
                for (int i = 0; i < options.Length; i++)
                {
                    bool selected = string.Equals(animal.Sex, options[i], System.StringComparison.OrdinalIgnoreCase);
                    UiButton(c, 274 + i * 114, 328, 113, 40, selected ? UiColor.Green : UiColor.Tab, options[i], 14, selected ? UiColor.OnGreen : UiColor.Text, $"sex {options[i]}");
                }
            }

            UiLabel(c, 635, 308, 341, 18, "SPAWN COUNT", 12, UiColor.Muted, TextAnchor.MiddleLeft, true);
            UiStepper(c, 635, 328, 46, 40, 60, animal.SpawnCount.ToString(), "spawn -1", "spawn 1");

            UiLabel(c, 812, 308, 164, 18, "LIMIT PER WIPE", 12, UiColor.Muted, TextAnchor.MiddleLeft, true);
            UiStepper(c, 812, 328, 46, 40, 60, animal.WipeLimit > 0 ? animal.WipeLimit.ToString() : "None", "wipelimit -1", "wipelimit 1");

            UiPanel(c, 274, 392, 702, 96, UiColor.Inset);
            UiLabel(c, 288, 402, 400, 18, "GIVE AND TEST", 12, UiColor.Muted, TextAnchor.MiddleLeft, true);
            UiInput(c, 288, 428, 280, 44, state.GiveTarget, 64, "target");
            UiButton(c, 576, 428, 110, 44, UiColor.Neutral, "GIVE SIGNAL", 14, UiColor.Text, "give target");
            UiButton(c, 694, 428, 110, 44, UiColor.Neutral, "GIVE TO ME", 14, UiColor.Text, "give me");
            UiButton(c, 812, 428, 150, 44, UiColor.Blue, "TEST DROP HERE", 14, UiColor.White, "test");
        }

        private void DrawSettingsTab(CuiElementContainer c, AdminPanelState state)
        {
            DrawSettingsColumn(c, state, 24, LeftSettingGroups);
            DrawSettingsColumn(c, state, 508, RightSettingGroups);
        }

        private void DrawSettingsColumn(CuiElementContainer c, AdminPanelState state, int x, UiSettingGroup[] groups)
        {
            const int columnWidth = 468;
            const int rowHeight = 38;
            const int groupHeader = 38;
            const int groupGap = 12;
            const int columnBottom = 508;

            int top = 104;
            for (int g = 0; g < groups.Length; g++)
            {
                UiSettingGroup group = groups[g];
                int height = g == groups.Length - 1 ? columnBottom - top : groupHeader + rowHeight * group.Settings.Length;
                UiPanel(c, x, top, columnWidth, height, UiColor.Inset);
                UiLabel(c, x + 14, top + 8, 300, 24, group.Title, 14, UiColor.Amber, TextAnchor.MiddleLeft, true);

                int rowTop = top + groupHeader;
                foreach (UiSetting setting in group.Settings)
                {
                    DrawSettingRow(c, state, x, rowTop, columnWidth, setting);
                    rowTop += rowHeight;
                }

                top += height + groupGap;
            }
        }

        private void DrawSettingRow(CuiElementContainer c, AdminPanelState state, int x, int top, int columnWidth, UiSetting setting)
        {
            const int controlHeight = 34;
            UiLabel(c, x + 14, top, 290, 38, setting.Label, 14, UiColor.Text, TextAnchor.MiddleLeft);

            if (setting.IsToggle)
            {
                bool on = setting.GetToggle(state.Draft.General);
                UiButton(c, x + columnWidth - 88, top + 2, 74, controlHeight, on ? UiColor.Green : UiColor.Neutral, on ? "ON" : "OFF", 14, on ? UiColor.OnGreen : UiColor.Text, $"toggle {setting.Key}");
                return;
            }

            string value = setting.GetNumber(state.Draft.General).ToString(setting.Format, System.Globalization.CultureInfo.InvariantCulture);
            UiStepper(c, x + columnWidth - 150, top + 2, 38, controlHeight, 60, value, $"step {setting.Key} -1", $"step {setting.Key} 1");
        }

        private void UiStepper(CuiElementContainer c, int x, int top, int buttonWidth, int height, int valueWidth, string value, string decrease, string increase)
        {
            UiButton(c, x, top, buttonWidth, height, UiColor.Neutral, "-", 18, UiColor.Text, decrease);
            UiPanel(c, x + buttonWidth + 2, top, valueWidth, height, UiColor.Field);
            UiLabel(c, x + buttonWidth + 2, top, valueWidth, height, value, 15, UiColor.Text, TextAnchor.MiddleCenter, true);
            UiButton(c, x + buttonWidth + valueWidth + 4, top, buttonWidth, height, UiColor.Neutral, "+", 18, UiColor.Text, increase);
        }

        private void UiField(CuiElementContainer c, int x, int top, int width, string label, string value, int limit, string command)
        {
            UiLabel(c, x, top, width, 18, label, 12, UiColor.Muted, TextAnchor.MiddleLeft, true);
            UiInput(c, x, top + 20, width, 40, value, limit, command);
        }

        private static void UiInput(CuiElementContainer c, int x, int top, int width, int height, string value, int limit, string command)
        {
            UiPanel(c, x, top, width, height, UiColor.Field);

            // The field must fill a holder exactly: Unity copies its offsets onto the caret, which is its child,
            // so a field positioned by offsets draws its caret and selection in the wrong place.
            string holder = CuiHelper.GetGuid();
            c.Add(new CuiElement
            {
                Name = holder,
                Parent = UiBody,
                Components = { UiRect(x + 10, top, width - 20, height) }
            });

            c.Add(new CuiElement
            {
                Name = CuiHelper.GetGuid(),
                Parent = holder,
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        Text = value ?? "",
                        CharsLimit = limit,
                        Command = $"{UiCommand} {command}",
                        Font = UiRegular,
                        FontSize = 15,
                        Color = UiColor.Text,
                        Align = TextAnchor.MiddleLeft,
                        NeedsKeyboard = true
                    },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "0 0", OffsetMax = "0 0" }
                }
            });
        }

        private static void UiSignalIcon(CuiElementContainer c, int x, int top, int size, ulong skinId)
        {
            c.Add(new CuiElement
            {
                Name = CuiHelper.GetGuid(),
                Parent = UiBody,
                Components =
                {
                    new CuiImageComponent { ItemId = ItemManager.FindItemDefinition(SupplySignalShortname).itemid, SkinId = skinId },
                    UiRect(x, top, size, size)
                }
            });
        }

        private static void UiPanel(CuiElementContainer c, int x, int top, int width, int height, string color)
        {
            CuiRectTransformComponent rect = UiRect(x, top, width, height);
            c.Add(new CuiPanel
            {
                Image = { Color = color },
                RectTransform = { AnchorMin = rect.AnchorMin, AnchorMax = rect.AnchorMax, OffsetMin = rect.OffsetMin, OffsetMax = rect.OffsetMax }
            }, UiBody);
        }

        private static void UiLabel(CuiElementContainer c, int x, int top, int width, int height, string text, int size, string color, TextAnchor align, bool bold = false, string name = null)
        {
            CuiRectTransformComponent rect = UiRect(x, top, width, height);
            c.Add(new CuiLabel
            {
                Text = { Text = text ?? "", FontSize = size, Color = color, Align = align, Font = bold ? UiBold : UiRegular },
                RectTransform = { AnchorMin = rect.AnchorMin, AnchorMax = rect.AnchorMax, OffsetMin = rect.OffsetMin, OffsetMax = rect.OffsetMax }
            }, UiBody, name);
        }

        private static void UiUpdateLabel(CuiElementContainer c, string name, string text, int size, string color, TextAnchor align, bool bold = false)
        {
            c.Add(new CuiElement
            {
                Name = name,
                Parent = UiBody,
                Update = true,
                Components = { new CuiTextComponent { Text = text, FontSize = size, Color = color, Align = align, Font = bold ? UiBold : UiRegular } }
            });
        }

        private static void UiButton(CuiElementContainer c, int x, int top, int width, int height, string color, string text, int size, string textColor, string command)
        {
            CuiRectTransformComponent rect = UiRect(x, top, width, height);
            c.Add(new CuiButton
            {
                Button = { Color = color, Command = $"{UiCommand} {command}" },
                Text = { Text = text, FontSize = size, Color = textColor, Align = TextAnchor.MiddleCenter, Font = UiBold },
                RectTransform = { AnchorMin = rect.AnchorMin, AnchorMax = rect.AnchorMax, OffsetMin = rect.OffsetMin, OffsetMax = rect.OffsetMax }
            }, UiBody);
        }

        // Positions are measured from the panel's top-left corner.
        private static CuiRectTransformComponent UiRect(int x, int top, int width, int height)
        {
            return new CuiRectTransformComponent
            {
                AnchorMin = "0 0",
                AnchorMax = "0 0",
                OffsetMin = $"{x} {PanelHeight - top - height}",
                OffsetMax = $"{x + width} {PanelHeight - top}"
            };
        }

        #endregion

        #region Helpers

        private AnimalConfig FindAnimalBySkin(ulong skin)
        {
            if (skin == 0)
                return null;

            return _config.Airdrops.FirstOrDefault(a => a.SkinId == skin);
        }

        private AnimalConfig FindAnimalByName(string name)
        {
            return _config.Airdrops.FirstOrDefault(a => a.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                ?? _config.Airdrops.FirstOrDefault(a => a.Name.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private string AnimalNameList() => string.Join(", ", _config.Airdrops.Select(a => a.Name));

        private static string FormatNotification(AnimalConfig animal)
        {
            return animal.Notification
                .Replace("{name}", animal.Name)
                .Replace("{count}", Mathf.Max(1, animal.SpawnCount).ToString());
        }

        // Keeps the configured prefabs that really are party balloons; with none configured, finds one on this server.
        private void ResolveBalloonPrefabs()
        {
            GeneralSettings settings = _config.General;
            _balloonPrefabs.Clear();

            foreach (string path in settings.BalloonPrefabs ?? new List<string>())
            {
                if (IsBalloonPrefab(path))
                    _balloonPrefabs.Add(path.ToLowerInvariant());
                else
                    PrintWarning($"'{path}' is not a party balloon prefab; skipping it.");
            }

            if (_balloonPrefabs.Count == 0)
            {
                string detected = StringPool.toNumber.Keys
                    .Where(path => path.Contains("balloon") && path.EndsWith(".prefab"))
                    .OrderBy(path => path.Length)
                    .FirstOrDefault(IsBalloonPrefab);
                if (detected != null)
                    _balloonPrefabs.Add(detected);
            }

            _speechBubblePrefab = null;
            if (!string.IsNullOrEmpty(settings.SpeechBubblePrefab))
            {
                if (IsBalloonPrefab(settings.SpeechBubblePrefab))
                    _speechBubblePrefab = settings.SpeechBubblePrefab.ToLowerInvariant();
                else
                    PrintWarning($"Speech bubble '{settings.SpeechBubblePrefab}' is not a party balloon prefab; balloon text goes on every balloon instead.");
            }

            Puts($"Balloons: {string.Join(", ", _balloonPrefabs.Select(p => System.IO.Path.GetFileNameWithoutExtension(p)))}"
                + (_speechBubblePrefab != null ? " + speech bubble." : "."));
        }

        private static bool IsBalloonPrefab(string path)
        {
            GameObject prefab = GameManager.server.FindPrefab(path.ToLowerInvariant());
            return prefab != null && prefab.GetComponent<PartyBalloon>() != null;
        }

        private void WarnOnce(string message)
        {
            if (_warnedOnce.Add(message))
                PrintWarning(message);
        }

        private static bool? ParseSex(string sex)
        {
            if (string.Equals(sex, "Male", System.StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(sex, "Female", System.StringComparison.OrdinalIgnoreCase))
                return false;
            return null;
        }

        private static bool PrefabExists(string path)
        {
            return !string.IsNullOrEmpty(path) && StringPool.toNumber.ContainsKey(path.ToLowerInvariant());
        }

        private void PurgeDeadSignals()
        {
            foreach (BaseEntity signal in _armedSignals.Keys.Where(s => s == null || s.IsDestroyed).ToList())
                _armedSignals.Remove(signal);
        }

        private void PurgeDeadPlanes()
        {
            foreach (CargoPlane plane in _planes.Keys.Where(p => p == null || p.IsDestroyed).ToList())
            {
                PendingDrop undelivered = _planes[plane];
                _planes.Remove(plane);
                RefundDrop(undelivered, "the plane was destroyed before dropping its cargo");
            }
        }

        #endregion
    }
}
