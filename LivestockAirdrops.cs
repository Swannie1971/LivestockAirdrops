using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Rust.Ai.Gen2;
using UnityEngine;
using UnityEngine.AI;

namespace Oxide.Plugins
{
    [Info("Livestock Airdrops", "Swannie", "1.9.3")]
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

        private class PendingDrop
        {
            public AnimalConfig Animal;
            public ulong OwnerId;
            public SupplySignal Signal;
            public int AnimalsInFlight;
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
                ["Received"] = "You received a '{0}' signal. Throw it to call in a livestock drop!"
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
        }

        private void Unload()
        {
            foreach (CargoPlane plane in _planes.Keys.ToList())
            {
                if (plane != null && !plane.IsDestroyed)
                    plane.Kill();
            }

            foreach (DescentController descent in _descents.ToList())
            {
                if (descent != null)
                    descent.ForceLand();
            }

            BalloonRelease.KillAll();
            _instance = null;
        }

        private void OnExplosiveThrown(BasePlayer player, BaseEntity entity, ThrownWeapon weapon) => ArmSignal(player, entity, weapon);

        private void OnExplosiveDropped(BasePlayer player, BaseEntity entity, ThrownWeapon weapon) => ArmSignal(player, entity, weapon);

        private void OnCargoPlaneSignaled(CargoPlane plane, SupplySignal signal)
        {
            PendingDrop pending;
            if (plane == null || signal == null || !_armedSignals.TryGetValue(signal, out pending))
                return;

            _armedSignals.Remove(signal);
            pending.Signal = signal;
            PurgeDeadPlanes();
            ConfigurePlane(plane);
            _planes[plane] = pending;

            BasePlayer owner = BasePlayer.FindByID(pending.OwnerId);
            if (owner != null && !string.IsNullOrEmpty(pending.Animal.Notification))
                owner.ChatMessage(FormatNotification(pending.Animal));

            timer.Once(plane.secondsToTake + PlaneStuckGraceSeconds, () =>
            {
                if (_planes.Remove(plane) && plane != null && !plane.IsDestroyed)
                    plane.Kill();
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
            _armedSignals[entity] = new PendingDrop { Animal = animal, OwnerId = player.userID };
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
                    pending.AnimalsInFlight++;
            }

            if (pending.AnimalsInFlight == 0)
                ScheduleSmokeStop(pending);
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

            if (livestock != null && ownerId != 0 && _config.General.TrustThrower)
                livestock.SetFamiliarity(ownerId, ConVar.Livestock.trustToLead, LivestockAnimal.FamiliarityReason.Purchased);

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
                ScheduleSmokeStop(pending);
        }

        private void ScheduleSmokeStop(PendingDrop pending)
        {
            SupplySignal signal = pending.Signal;
            float delay = _config.General.StopSmokeAfterLanding;
            if (signal == null || delay < 0f)
                return;

            pending.Signal = null;
            timer.Once(Mathf.Max(0f, delay), () =>
            {
                if (signal != null && !signal.IsDestroyed)
                    signal.FinishUp();
            });
        }

        #endregion

        #region Descent Controller

        private class DescentController : MonoBehaviour
        {
            private static readonly int GroundMask = LayerMask.GetMask("Terrain", "World", "Construction", "Deployed");

            private const float Gen2NavmeshSampleRadius = 10f;

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
                Vector3 onNavmesh;
                if (_npc.TrySampleNavmesh(groundPosition, Gen2NavmeshSampleRadius, out onNavmesh))
                    groundPosition = onNavmesh;
                else
                    _instance?.PrintWarning($"{_npc.ShortPrefabName} landed off the navmesh at {groundPosition}; it may not be able to move.");

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

            private void Land(Vector3 groundPosition)
            {
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
                _planes.Remove(plane);
        }

        #endregion
    }
}
