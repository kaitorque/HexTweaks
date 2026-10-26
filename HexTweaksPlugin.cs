using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using RoR2;
using RoR2.Items;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace HexTweaks
{
    [BepInDependency("com.bepis.r2api.content_management")]
    [BepInDependency("com.bepis.r2api.recalculatestats")]
    [BepInDependency(LookingGlassGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public partial class HexTweaksPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "kaitorque.HexTweaks";
        public const string PluginName = "HexTweaks";
        public const string PluginVersion = "1.0.3";

        private static readonly FieldInfo PatienceControllersField =
            typeof(OutOfTPRadiusManager).GetField("_teamIndextoFogDamageController", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo FogStacksField =
            typeof(FogDamageController).GetField("characterBodyToStacks", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo FogExponentialField =
            typeof(FogDamageController).GetField("incrementExponentially", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo FogTickPeriodField =
            typeof(FogDamageController).GetField("tickPeriodSeconds", BindingFlags.NonPublic | BindingFlags.Instance);

        private static HolderOnlyMarker tickingPatienceController;

        private static BuffDef ossificationBuff;
        private static readonly Dictionary<RoR2.CharacterAI.BaseAI, CharacterMaster> mimicOwners = new Dictionary<RoR2.CharacterAI.BaseAI, CharacterMaster>();

        private ConfigEntry<bool> rainEnabled;
        private ConfigEntry<float> rainDamagePerStack;
        private ConfigEntry<float> rainDamageDealtReductionPerStack;
        private ConfigEntry<float> rainCooldownPerStack;
        private ConfigEntry<float> rainAttackSpeedReductionPerStack;
        private ConfigEntry<float> rainMoveSpeedReductionPerStack;
        private ConfigEntry<float> strifeBossDamageDealtReductionPerStack;
        private ConfigEntry<bool> patienceEnabled;
        private ConfigEntry<bool> strifeEnabled;
        private ConfigEntry<float> strifeDamagePerStack;
        private ConfigEntry<bool> ossificationEnabled;
        private ConfigEntry<float> ossificationDamageReduction;
        private ConfigEntry<float> ossificationDuration;
        private ConfigEntry<float> ossificationDurationPerStack;
        private ConfigEntry<bool> greedEnabled;
        private ConfigEntry<float> greedGoldReductionPerStack;
        private ConfigEntry<bool> mimicryEnabled;
        private ConfigEntry<bool> hexColorsEnabled;
        private ConfigEntry<bool> logHexMaterials;
        private ConfigEntry<float> stoneGlow;
        private ConfigEntry<float> runeGlow;
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int EmColorId = Shader.PropertyToID("_EmColor");
        private static readonly int EmTexId = Shader.PropertyToID("_EmTex");
        private static readonly int EmPowerId = Shader.PropertyToID("_EmPower");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly Color WhiteRuneAlbedo = new Color(0.45f, 0.45f, 0.45f);
        private const float AutoRuneLuminanceThreshold = 0.45f;        private readonly List<HexColor> hexColors = new List<HexColor>();
        private readonly Dictionary<ItemIndex, HexColor> hexColorsByItem = new Dictionary<ItemIndex, HexColor>();
        private readonly Dictionary<(ItemIndex, Material), RecoloredMaterial> recoloredMaterials = new Dictionary<(ItemIndex, Material), RecoloredMaterial>();
        private readonly HashSet<Material> recoloredMaterialSet = new HashSet<Material>();
        private FileSystemWatcher configWatcher;
        private volatile bool configFileChanged;
        private float configReloadTime;
        private readonly HashSet<ItemIndex> loggedHexes = new HashSet<ItemIndex>();

        private void Awake()
        {
            rainEnabled = Config.Bind("Hex of Rain", "Enabled", true,
                "Hex of Rain no longer raises the run's difficulty. Instead, the holder gets weaker in every way.");
            rainDamagePerStack = Config.Bind("Hex of Rain", "Damage Taken Per Stack", 0.15f,
                "Extra damage taken by the holder per stack. 0.15 = +15% per stack.");
            rainDamageDealtReductionPerStack = Config.Bind("Hex of Rain", "Damage Dealt Reduction Per Stack", 0.2f,
                "Holder's damage reduction per stack, applied multiplicatively. 0.2 = 20% less damage with 1 stack, 36% less with 2.");
            rainCooldownPerStack = Config.Bind("Hex of Rain", "Cooldown Increase Per Stack", 0.15f,
                "Holder's skill cooldowns are longer by this much per stack. 0.15 = +15% per stack.");
            rainAttackSpeedReductionPerStack = Config.Bind("Hex of Rain", "Attack Speed Reduction Per Stack", 0.1f,
                "Holder's attack speed reduction per stack. Works like other slows: 0.1 per stack divides attack speed by 1.1, 1.2, ...");
            rainMoveSpeedReductionPerStack = Config.Bind("Hex of Rain", "Move Speed Reduction Per Stack", 0.1f,
                "Holder's movement speed reduction per stack. Works like other slows: 0.1 per stack divides movement speed by 1.1, 1.2, ...");
            patienceEnabled = Config.Bind("Hex of Patience", "Enabled", true,
                "Hex of Patience only hurts the players holding it, and each holder's damage ramps with their own stacks only.");
            strifeEnabled = Config.Bind("Hex of Strife", "Enabled", true,
                "Hex of Strife no longer adds Shrine of the Mountain effects to the teleporter. Instead, bosses deal more damage to the holder.");
            strifeDamagePerStack = Config.Bind("Hex of Strife", "Boss Damage Taken Per Stack", 0.3f,
                "Extra damage the holder takes from bosses per stack. 0.3 = +30% per stack.");
            strifeBossDamageDealtReductionPerStack = Config.Bind("Hex of Strife", "Boss Damage Dealt Reduction Per Stack", 0.25f,
                "Holder deals less damage to bosses per stack, applied multiplicatively. 0.25 = 25% less with 1 stack, 44% less with 2.");
            ossificationEnabled = Config.Bind("Hex of Ossification", "Enabled", true,
                "Hex of Ossification no longer gives enemies barrier. Instead, after the holder kills an enemy, the holder deals less damage for a few seconds.");
            ossificationDamageReduction = Config.Bind("Hex of Ossification", "Damage Reduction", 0.3f,
                "How much less damage the holder deals after a kill. 0.3 = 30% less.");
            ossificationDuration = Config.Bind("Hex of Ossification", "Duration", 5f,
                "Seconds the reduction lasts after a kill, with 1 stack.");
            ossificationDurationPerStack = Config.Bind("Hex of Ossification", "Duration Per Extra Stack", 2f,
                "Extra seconds per additional stack.");
            greedEnabled = Config.Bind("Hex of Greed", "Enabled", true,
                "Hex of Greed no longer gives your items to enemies. Instead, the holder earns less gold.");
            greedGoldReductionPerStack = Config.Bind("Hex of Greed", "Gold Reduction Per Stack", 0.25f,
                "Gold reduction per stack, applied multiplicatively. 0.25 = 25% less gold with 1 stack, 44% less with 2.");
            mimicryEnabled = Config.Bind("Hex of Mimicry", "Enabled", true,
                "Mimics spawned by Hex of Mimicry only target the player who spawned them.");
            hexColorsEnabled = Config.Bind("Hex Colors", "Enabled", true,
                "Recolors each hex pickup's stone and runes so they can be told apart.");
            stoneGlow = Config.Bind("Hex Colors", "Stone Glow", 4.5f,
                "How strongly the stone glows in its own color. The glow scales with the stone color's brightness, so darker colors glow less. 0 = no glow, tint only.");
            runeGlow = Config.Bind("Hex Colors", "White Rune Glow", 1.5f,
                "Glow strength of white runes. Raise it if the runes are hard to see against the stone.");
            logHexMaterials = Config.Bind("Hex Colors", "Log Materials", false,
                "Logs each hex pickup's renderers and material properties once per session. Debug aid.");
            BindHexColor("Hex of Patience", () => DLC4Content.Items.OutOfTPRadius, "#E02020");
            BindHexColor("Hex of Pain", () => DLC4Content.Items.HexCurse, "#6A0F1A");
            BindHexColor("Hex of Rain", () => DLC4Content.Items.IncreasedDifficulty, "#2040E0");
            BindHexColor("Hex of Acceleration", () => DLC4Content.Items.SlipperyGround, "#20B0C0");
            BindHexColor("Hex of Strife", () => DLC4Content.Items.HexMountain, "#7A20D8");
            BindHexColor("Hex of Leaping", () => DLC4Content.Items.WeakAssHamstring, "#609A00");
            BindHexColor("Hex of Ossification", () => DLC4Content.Items.BarrierEnemiesOnKill, "#D0D0D0");
            BindHexColor("Hex of Loss", () => DLC4Content.Items.RoamingItemDisable, "#707070");
            BindHexColor("Hex of Greed", () => DLC4Content.Items.ShareItemsWithEnemies, "#FFCC00");
            BindHexColor("Hex of Misfortune", () => DLC4Content.Items.RandomDebuffOnInteract, "#FF6000");
            BindHexColor("Hex of Mimicry", () => DLC4Content.Items.Mimicry, "#20A030");
            BindHexColor("Hex of Famine", () => DLC4Content.Items.LowerHealing, "#D010D0");
            BindHexColor("Hex of Haunting", () => DLC4Content.Items.HexHaunting, "#FF60B0");
            BindHexColor("Hex of Blindness", () => DLC4Content.Items.Blindness, "#1A1A1A");
            ItemCatalog.availability.CallWhenAvailable(IndexHexColors);
            PickupCatalog.availability.CallWhenAvailable(LoadHexIcons);
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(LookingGlassGuid))
            {
                PickupCatalog.availability.CallWhenAvailable(RegisterLookingGlassStats);
            }
            On.RoR2.PickupDisplay.OnModelInstanceDiscovered += PickupDisplay_OnModelInstanceDiscovered;
            Config.SettingChanged += (_, __) => ApplyAllHexColors();
            WatchConfigFile();

            On.RoR2.Util.GetTotalItemCountFromTeam += Util_GetTotalItemCountFromTeam;
            R2API.RecalculateStatsAPI.GetStatCoefficients += RecalculateStatsAPI_GetStatCoefficients;
            On.RoR2.GlobalEventManager.ProcBarrierEnemiesOnKill += GlobalEventManager_ProcBarrierEnemiesOnKill;
            On.RoR2.Items.ShareItemsWithEnemiesBehaviour.FixedUpdate += ShareItemsWithEnemiesBehaviour_FixedUpdate;
            On.RoR2.CharacterMaster.GiveMoney_uint_bool += CharacterMaster_GiveMoney;
            On.RoR2.CharacterMaster.AddDeployable += CharacterMaster_AddDeployable;
            On.RoR2.CharacterAI.BaseAI.ManagedFixedUpdate += BaseAI_ManagedFixedUpdate;
            On.RoR2.CharacterAI.BaseAI.FindEnemyHurtBox += BaseAI_FindEnemyHurtBox;
            Stage.onServerStageBegin += _ => mimicOwners.Clear();

            ossificationBuff = ScriptableObject.CreateInstance<BuffDef>();
            ossificationBuff.name = "bdHexTweaksOssification";
            ossificationBuff.buffColor = new Color(0.85f, 0.82f, 0.7f);
            ossificationBuff.canStack = false;
            ossificationBuff.isDebuff = true;
            R2API.ContentAddition.AddBuffDef(ossificationBuff);
            ItemCatalog.availability.CallWhenAvailable(LoadOssificationIcon);
            On.RoR2.HealthComponent.TakeDamageProcess += HealthComponent_TakeDamageProcess;
            On.RoR2.Items.OutOfTPRadiusManager.InstantiateDamagerForTeam += OutOfTPRadiusManager_InstantiateDamagerForTeam;
            On.RoR2.FogDamageController.MyFixedUpdate += FogDamageController_MyFixedUpdate;
            On.RoR2.Language.GetLocalizedStringByToken += Language_GetLocalizedStringByToken;
        }

        private void RecalculateStatsAPI_GetStatCoefficients(CharacterBody body, R2API.RecalculateStatsAPI.StatHookEventArgs args)
        {
            if (!rainEnabled.Value || !body.inventory || !DLC4Content.Items.IncreasedDifficulty)
            {
                return;
            }
            int count = body.inventory.GetItemCountTotal(DLC4Content.Items.IncreasedDifficulty.itemIndex);
            if (count <= 0)
            {
                return;
            }
            args.allSkills.cooldownMultAdd += rainCooldownPerStack.Value * count;
            args.attackSpeedReductionMultAdd += rainAttackSpeedReductionPerStack.Value * count;
            args.moveSpeedReductionMultAdd += rainMoveSpeedReductionPerStack.Value * count;
            args.damageTotalMult *= Mathf.Pow(Mathf.Clamp01(1f - rainDamageDealtReductionPerStack.Value), count);
        }

        private void LoadOssificationIcon()
        {
            ItemDef item = DLC4Content.Items.BarrierEnemiesOnKill;
            if (!item)
            {
                Logger.LogWarning("Hex of Ossification item not found; buff will have no icon.");
                return;
            }
            AssetReferenceT<Sprite> iconReference = item.GetIconReference();
            if (iconReference != null && iconReference.RuntimeKeyIsValid())
            {
                Addressables.LoadAssetAsync<Sprite>(iconReference.RuntimeKey).Completed += handle =>
                {
                    if (handle.Status == AsyncOperationStatus.Succeeded && !ossificationBuff.iconSprite)
                    {
                        ossificationBuff.iconSprite = handle.Result;
                    }
                };
            }
            else
            {
#pragma warning disable CS0618
                ossificationBuff.iconSprite = item.pickupIconSprite;
#pragma warning restore CS0618
            }
        }

        private int Util_GetTotalItemCountFromTeam(On.RoR2.Util.orig_GetTotalItemCountFromTeam orig, Util.GetTotalItemCountFromTeamArgs args)
        {
            if (rainEnabled.Value && DLC4Content.Items.IncreasedDifficulty && args.itemIndex == DLC4Content.Items.IncreasedDifficulty.itemIndex)
            {
                return 0;
            }
            if (strifeEnabled.Value && DLC4Content.Items.HexMountain && args.itemIndex == DLC4Content.Items.HexMountain.itemIndex)
            {
                return 0;
            }
            return orig(args);
        }

        private void GlobalEventManager_ProcBarrierEnemiesOnKill(On.RoR2.GlobalEventManager.orig_ProcBarrierEnemiesOnKill orig, DamageReport damageReport, int itemStacks, CharacterBody victimBody, TeamIndex attackerTeamIndex)
        {
            if (!ossificationEnabled.Value)
            {
                orig(damageReport, itemStacks, victimBody, attackerTeamIndex);
                return;
            }
            CharacterBody attacker = damageReport.attackerBody;
            if (attacker && ossificationBuff)
            {
                float duration = ossificationDuration.Value + ossificationDurationPerStack.Value * (itemStacks - 1);
                attacker.ClearTimedBuffs(ossificationBuff);
                attacker.AddTimedBuff(ossificationBuff, duration);
            }
        }

        private void ShareItemsWithEnemiesBehaviour_FixedUpdate(On.RoR2.Items.ShareItemsWithEnemiesBehaviour.orig_FixedUpdate orig, ShareItemsWithEnemiesBehaviour self)
        {
            if (!greedEnabled.Value)
            {
                orig(self);
            }
        }

        private void CharacterMaster_GiveMoney(On.RoR2.CharacterMaster.orig_GiveMoney_uint_bool orig, CharacterMaster self, uint amount, bool bypassStatManager)
        {
            if (greedEnabled.Value && self.inventory && DLC4Content.Items.ShareItemsWithEnemies)
            {
                int count = self.inventory.GetItemCountEffective(DLC4Content.Items.ShareItemsWithEnemies);
                if (count > 0)
                {
                    float keep = Mathf.Pow(Mathf.Clamp01(1f - greedGoldReductionPerStack.Value), count);
                    amount = (uint)Mathf.RoundToInt(amount * keep);
                }
            }
            orig(self, amount, bypassStatManager);
        }

        private void CharacterMaster_AddDeployable(On.RoR2.CharacterMaster.orig_AddDeployable orig, CharacterMaster self, Deployable deployable, DeployableSlot slot)
        {
            orig(self, deployable, slot);
            if (!mimicryEnabled.Value || slot != DeployableSlot.Mimic || !deployable)
            {
                return;
            }
            CharacterBody mimicBody = deployable.GetComponent<CharacterBody>();
            RoR2.CharacterAI.BaseAI ai = mimicBody && mimicBody.master ? mimicBody.master.GetComponent<RoR2.CharacterAI.BaseAI>() : null;
            if (ai)
            {
                mimicOwners[ai] = self;
            }
        }

        private void BaseAI_ManagedFixedUpdate(On.RoR2.CharacterAI.BaseAI.orig_ManagedFixedUpdate orig, RoR2.CharacterAI.BaseAI self, float deltaTime)
        {
            if (mimicOwners.TryGetValue(self, out CharacterMaster owner))
            {
                CharacterBody ownerBody = owner ? owner.GetBody() : null;
                if (ownerBody && ownerBody.healthComponent && ownerBody.healthComponent.alive)
                {
                    if (self.currentEnemy.gameObject != ownerBody.gameObject)
                    {
                        self.currentEnemy.Reset();
                        self.currentEnemy.gameObject = ownerBody.gameObject;
                        self.currentEnemy.bestHurtBox = ownerBody.mainHurtBox;
                    }
                    self.enemyAttention = Mathf.Max(self.enemyAttention, 1f);
                }
                else if (self.currentEnemy.gameObject)
                {
                    self.currentEnemy.Reset();
                }
            }
            orig(self, deltaTime);
        }

        private HurtBox BaseAI_FindEnemyHurtBox(On.RoR2.CharacterAI.BaseAI.orig_FindEnemyHurtBox orig, RoR2.CharacterAI.BaseAI self, float maxDistance, bool full360Vision, bool filterByLoS)
        {
            if (mimicOwners.TryGetValue(self, out CharacterMaster owner))
            {
                CharacterBody ownerBody = owner ? owner.GetBody() : null;
                return ownerBody && ownerBody.healthComponent && ownerBody.healthComponent.alive ? ownerBody.mainHurtBox : null;
            }
            return orig(self, maxDistance, full360Vision, filterByLoS);
        }

        private void HealthComponent_TakeDamageProcess(On.RoR2.HealthComponent.orig_TakeDamageProcess orig, HealthComponent self, DamageInfo damageInfo)
        {
            if (tickingPatienceController && self.body)
            {
                damageInfo.damage *= GetOwnStacksPatienceMultiplier(tickingPatienceController, self.body);
            }
            Inventory victimInventory = self.body ? self.body.inventory : null;
            if (rainEnabled.Value && victimInventory && DLC4Content.Items.IncreasedDifficulty)
            {
                int count = victimInventory.GetItemCountTotal(DLC4Content.Items.IncreasedDifficulty.itemIndex);
                if (count > 0)
                {
                    damageInfo.damage *= 1f + rainDamagePerStack.Value * count;
                }
            }

            CharacterBody attackerBody = damageInfo.attacker ? damageInfo.attacker.GetComponent<CharacterBody>() : null;
            if (strifeEnabled.Value && victimInventory && attackerBody && attackerBody.isBoss && DLC4Content.Items.HexMountain)
            {
                int count = victimInventory.GetItemCountEffective(DLC4Content.Items.HexMountain);
                if (count > 0)
                {
                    damageInfo.damage *= 1f + strifeDamagePerStack.Value * count;
                }
            }
            if (strifeEnabled.Value && self.body && self.body.isBoss && attackerBody && attackerBody.inventory && DLC4Content.Items.HexMountain)
            {
                int count = attackerBody.inventory.GetItemCountEffective(DLC4Content.Items.HexMountain);
                if (count > 0)
                {
                    damageInfo.damage *= Mathf.Pow(Mathf.Clamp01(1f - strifeBossDamageDealtReductionPerStack.Value), count);
                }
            }
            if (ossificationEnabled.Value && attackerBody && ossificationBuff && attackerBody.HasBuff(ossificationBuff))
            {
                damageInfo.damage *= Mathf.Clamp01(1f - ossificationDamageReduction.Value);
            }
            orig(self, damageInfo);
        }

        private void OutOfTPRadiusManager_InstantiateDamagerForTeam(On.RoR2.Items.OutOfTPRadiusManager.orig_InstantiateDamagerForTeam orig, IZone safeZone, TeamIndex affectedTeam, int itemCount)
        {
            orig(safeZone, affectedTeam, itemCount);

            if (!patienceEnabled.Value || PatienceControllersField == null)
            {
                return;
            }
            if (!(PatienceControllersField.GetValue(null) is IDictionary controllers) || !controllers.Contains(affectedTeam))
            {
                return;
            }
            object instance = controllers[affectedTeam];
            FogDamageController controller = instance.GetType().GetField("fogDamageControllerInstance")?.GetValue(instance) as FogDamageController;
            object originalRamp = instance.GetType().GetField("originalHealthFractionRampCoefficientPerSecond")?.GetValue(instance);
            if (!controller || !(originalRamp is float) || controller.GetComponent<HolderOnlyMarker>())
            {
                return;
            }
            HolderOnlyMarker marker = controller.gameObject.AddComponent<HolderOnlyMarker>();
            marker.controller = controller;
            marker.originalRamp = (float)originalRamp;
            controller.AddSafeZone(new NonHolderSafeZone(affectedTeam));
        }

        private void FogDamageController_MyFixedUpdate(On.RoR2.FogDamageController.orig_MyFixedUpdate orig, FogDamageController self, float deltaTime)
        {
            HolderOnlyMarker marker = self.GetComponent<HolderOnlyMarker>();
            tickingPatienceController = marker;
            try
            {
                orig(self, deltaTime);
            }
            finally
            {
                tickingPatienceController = null;
            }
        }

        // The team's damage zone ramps with every holder's stacks combined; rescale each tick to the victim's own stacks.
        private static float GetOwnStacksPatienceMultiplier(HolderOnlyMarker marker, CharacterBody body)
        {
            FogDamageController controller = marker.controller;
            if (!controller || !body.inventory || !(FogStacksField?.GetValue(controller) is Dictionary<CharacterBody, int> stacks) || !stacks.TryGetValue(body, out int timeStacks))
            {
                return 1f;
            }
            int ownCount = body.inventory.GetItemCountEffective(DLC4Content.Items.OutOfTPRadius);
            if (ownCount <= 0)
            {
                return 1f;
            }
            float teamRamp = controller.healthFractionRampCoefficientPerSecond;
            float ownRamp = marker.originalRamp + marker.originalRamp % 1f * (ownCount - 1);
            int n = timeStacks - 1;
            if (n <= 0 || Mathf.Approximately(teamRamp, ownRamp))
            {
                return 1f;
            }
            if (FogExponentialField != null && (bool)FogExponentialField.GetValue(controller))
            {
                return Mathf.Pow(ownRamp / teamRamp, n);
            }
            float tick = FogTickPeriodField != null ? (float)FogTickPeriodField.GetValue(controller) : 1f;
            return (1f + n * ownRamp * tick) / (1f + n * teamRamp * tick);
        }

        private string Language_GetLocalizedStringByToken(On.RoR2.Language.orig_GetLocalizedStringByToken orig, Language self, string token)
        {
            switch (token)
            {
                case "ITEM_INCREASEDDIFFICULTY_PICKUP" when rainEnabled.Value:
                    return "Petrichor V <style=cDeath>weighs you down</style>.";
                case "ITEM_INCREASEDDIFFICULTY_DESC" when rainEnabled.Value:
                    return $"You take <style=cDeath>{Pct(rainDamagePerStack.Value)}</style> more damage, deal <style=cDeath>{Pct(rainDamageDealtReductionPerStack.Value)}</style> less damage, " +
                        $"have <style=cDeath>{Pct(rainCooldownPerStack.Value)}</style> longer skill cooldowns, <style=cDeath>{Pct(rainAttackSpeedReductionPerStack.Value)}</style> lower attack speed, and <style=cDeath>{Pct(rainMoveSpeedReductionPerStack.Value)}</style> lower movement speed " +
                        "<style=cStack>(all increase per stack)</style>.";
                case "ITEM_OUTOFTPRADIUS_PICKUP" when patienceEnabled.Value:
                    return "Standing outside of the Teleporter's radius <style=cDeath>hurts</style> you.";
                case "ITEM_OUTOFTPRADIUS_DESC" when patienceEnabled.Value:
                    return orig(self, token).Replace("\n<style=cHumanObjective>This effect applies to your allies as well</style>.", "").Trim();
                case "ITEM_HEXMOUNTAIN_PICKUP" when strifeEnabled.Value:
                    return "Bosses <style=cDeath>hit you harder</style> and <style=cDeath>resist your attacks</style>.";
                case "ITEM_HEXMOUNTAIN_DESC" when strifeEnabled.Value:
                    string strife = Pct(strifeDamagePerStack.Value);
                    string strifeDealt = Pct(strifeBossDamageDealtReductionPerStack.Value);
                    return $"You take <style=cDeath>{strife}</style> <style=cStack>(+{strife} per stack)</style> more damage from <style=cDeath>bosses</style>, " +
                        $"and deal <style=cDeath>{strifeDealt}</style> <style=cStack>(+{strifeDealt} per stack)</style> less damage to them.";
                case "ITEM_BARRIERENEMIES_PICKUP" when ossificationEnabled.Value:
                    return "After killing an enemy, you <style=cDeath>deal less damage</style> for a short time.";
                case "ITEM_BARRIERENEMIES_DESC" when ossificationEnabled.Value:
                    string reduction = Mathf.RoundToInt(ossificationDamageReduction.Value * 100f) + "%";
                    return $"After killing an enemy, you deal <style=cDeath>{reduction}</style> less damage for <style=cIsUtility>{ossificationDuration.Value:0.#}s</style> <style=cStack>(+{ossificationDurationPerStack.Value:0.#}s per stack)</style>.";
                case "ITEM_SHAREITEMS_PICKUP" when greedEnabled.Value:
                    return "You <style=cDeath>earn less gold</style>.";
                case "ITEM_SHAREITEMS_DESC" when greedEnabled.Value:
                    string greed = Mathf.RoundToInt(greedGoldReductionPerStack.Value * 100f) + "%";
                    return $"You earn <style=cDeath>{greed}</style> <style=cStack>(+{greed} per stack, multiplicative)</style> less gold.";
                case "ITEM_MIMICRY_PICKUP" when mimicryEnabled.Value:
                    return orig(self, token) + " It only hunts <style=cDeath>you</style>.";
                case "ITEM_MIMICRY_DESC" when mimicryEnabled.Value:
                    return orig(self, token) + " The Mimic only targets <style=cDeath>you</style>.";
            }
            return orig(self, token);
        }

        private static string Pct(float value)
        {
            return Mathf.RoundToInt(value * 100f) + "%";
        }

        private void BindHexColor(string section, System.Func<ItemDef> getItem, string stoneHex)
        {
            ColorUtility.TryParseHtmlString(stoneHex, out Color stone);
            hexColors.Add(new HexColor
            {
                name = section,
                getItem = getItem,
                stone = Config.Bind(section, "Stone Color", stone, "Stone tint and glow color. Each hex has its own hue so they can be told apart from a distance."),
                runeColor = Config.Bind(section, "Rune Color", RuneColor.Auto, "Auto picks black runes on light stones and white runes on dark ones."),
            });
        }

        private void IndexHexColors()
        {
            foreach (HexColor hex in hexColors)
            {
                ItemDef item = hex.getItem();
                if (item && item.itemIndex != ItemIndex.None)
                {
                    hexColorsByItem[item.itemIndex] = hex;
                }
                else
                {
                    Logger.LogWarning($"{hex.name} item not found; it won't be recolored.");
                }
            }
        }

        private void PickupDisplay_OnModelInstanceDiscovered(On.RoR2.PickupDisplay.orig_OnModelInstanceDiscovered orig, PickupDisplay self, bool isNew)
        {
            orig(self, isNew);
            List<Renderer> renderers = self.modelRenderers;
            if (renderers == null || renderers.Count == 0)
            {
                return;
            }
            PickupDef pickupDef = PickupCatalog.GetPickupDef(self.GetPickupIndex());
            if (pickupDef == null || !hexColorsByItem.TryGetValue(pickupDef.itemIndex, out HexColor hex))
            {
                return;
            }
            if (logHexMaterials.Value && loggedHexes.Add(pickupDef.itemIndex))
            {
                LogHexMaterials(hex.name, renderers);
            }
            foreach (Renderer renderer in renderers)
            {
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
                {
                    continue;
                }
                Material[] materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i])
                    {
                        materials[i] = GetRecoloredMaterial(pickupDef.itemIndex, hex, materials[i]);
                    }
                }
                renderer.sharedMaterials = materials;
            }
        }

        private Material GetRecoloredMaterial(ItemIndex itemIndex, HexColor hex, Material original)
        {
            if (recoloredMaterials.TryGetValue((itemIndex, original), out RecoloredMaterial entry))
            {
                return entry.material;
            }
            if (recoloredMaterialSet.Contains(original))
            {
                return original;
            }
            entry = new RecoloredMaterial
            {
                material = new Material(original) { name = original.name + " (" + hex.name + ")" },
                original = original,
                hex = hex,
                isRunes = original.name.Contains("Rune"),
            };
            ApplyHexColor(entry);
            recoloredMaterials[(itemIndex, original)] = entry;
            recoloredMaterialSet.Add(entry.material);
            return entry.material;
        }

        // Pickups share one recolored material per hex, so editing it in place updates every pickup already spawned.
        private void ApplyHexColor(RecoloredMaterial entry)
        {
            Material material = entry.material;
            if (!material || !entry.original)
            {
                return;
            }
            material.CopyPropertiesFromMaterial(entry.original);
            if (!hexColorsEnabled.Value)
            {
                return;
            }
            if (entry.isRunes)
            {
                if (ResolveRuneColor(entry.hex) == RuneColor.White)
                {
                    material.SetColor(ColorId, WhiteRuneAlbedo);
                    material.SetColor(EmColorId, Color.white * runeGlow.Value);
                }
                else
                {
                    material.SetColor(ColorId, Color.black);
                    material.SetColor(EmColorId, Color.black);
                }
                return;
            }
            material.SetColor(ColorId, entry.hex.stone.Value);
            if (stoneGlow.Value > 0f)
            {
                material.SetTexture(EmTexId, entry.original.GetTexture(MainTexId));
                material.SetColor(EmColorId, entry.hex.stone.Value * stoneGlow.Value);
                material.SetFloat(EmPowerId, 1f);
            }
        }

        private void ApplyAllHexColors()
        {
            foreach (RecoloredMaterial entry in recoloredMaterials.Values)
            {
                ApplyHexColor(entry);
            }
            foreach (HexColor hex in hexColors)
            {
                ApplyHexIcon(hex);
            }
        }

        private void LoadHexIcons()
        {
            foreach (HexColor hex in hexColors)
            {
                ItemDef item = hex.getItem();
                if (!item)
                {
                    continue;
                }
                AssetReferenceT<Sprite> iconReference = item.GetIconReference();
                if (iconReference != null && iconReference.RuntimeKeyIsValid())
                {
                    Addressables.LoadAssetAsync<Sprite>(iconReference.RuntimeKey).Completed += handle =>
                    {
                        if (handle.Status == AsyncOperationStatus.Succeeded)
                        {
                            SetupHexIcon(hex, item, handle.Result);
                        }
                    };
                }
                else
                {
#pragma warning disable CS0618
                    SetupHexIcon(hex, item, item.pickupIconSprite);
#pragma warning restore CS0618
                }
            }
        }

        private void SetupHexIcon(HexColor hex, ItemDef item, Sprite original)
        {
            if (!original)
            {
                Logger.LogWarning($"{hex.name} icon not found; it won't be recolored.");
                return;
            }
            Rect rect = original.textureRect;
            int width = Mathf.RoundToInt(rect.width);
            int height = Mathf.RoundToInt(rect.height);
            Texture source = original.texture;
            RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(source, rt);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { name = original.name + " (" + hex.name + ")" };
            texture.ReadPixels(new Rect(rect.x, rect.y, width, height), 0, 0);
            texture.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);

            hex.originalIconPixels = texture.GetPixels32();
            hex.iconClasses = ClassifyIcon(hex.originalIconPixels, width, height);
            hex.originalIcon = original;
            hex.originalIconReference = item.pickupIconSpriteReference;
            hex.icon = Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), original.pixelsPerUnit);
            hex.icon.name = texture.name;
            hex.item = item;
            ApplyHexIcon(hex);

            if (logHexMaterials.Value)
            {
                string dir = System.IO.Path.Combine(Paths.ConfigPath, "HexTweaksIcons");
                Directory.CreateDirectory(dir);
                var originalCopy = new Texture2D(width, height, TextureFormat.RGBA32, false);
                originalCopy.SetPixels32(hex.originalIconPixels);
                File.WriteAllBytes(System.IO.Path.Combine(dir, hex.name + " original.png"), originalCopy.EncodeToPNG());
                File.WriteAllBytes(System.IO.Path.Combine(dir, hex.name + " recolored.png"), texture.EncodeToPNG());
                Destroy(originalCopy);
            }
        }

        private void ApplyHexIcon(HexColor hex)
        {
            if (!hex.icon || hex.originalIconPixels == null)
            {
                return;
            }
            bool enabled = hexColorsEnabled.Value;
            Sprite sprite = enabled ? hex.icon : hex.originalIcon;
            if (enabled)
            {
                Texture2D texture = hex.icon.texture;
                texture.SetPixels32(RecolorIcon(hex.originalIconPixels, hex.iconClasses, hex.stone.Value, ResolveRuneColor(hex) == RuneColor.White));
                texture.Apply();
            }
#pragma warning disable CS0618
            hex.item.pickupIconSprite = sprite;
#pragma warning restore CS0618
            hex.item.pickupIconSpriteReference = enabled ? new AssetReferenceT<Sprite>("") : hex.originalIconReference ?? new AssetReferenceT<Sprite>("");
            PickupDef pickupDef = PickupCatalog.GetPickupDef(PickupCatalog.FindPickupIndex(hex.item.itemIndex));
            if (pickupDef != null)
            {
                pickupDef.iconSprite = sprite;
                pickupDef.iconTexture = sprite.texture;
                pickupDef.iconSpriteReference = hex.item.pickupIconSpriteReference;
            }
            if (ossificationBuff && hex.item == DLC4Content.Items.BarrierEnemiesOnKill)
            {
                ossificationBuff.iconSprite = sprite;
            }
        }

        // Saturated yellow-green pixels are either the outline ring (near the transparent edge) or the runes (interior).
        private static IconPixel[] ClassifyIcon(Color32[] source, int width, int height)
        {
            const int edgeRadius = 6;
            var result = new IconPixel[source.Length];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    Color32 px = source[i];
                    if (px.a == 0)
                    {
                        result[i] = IconPixel.Transparent;
                        continue;
                    }
                    Color.RGBToHSV(px, out float h, out float s, out _);
                    if (!(s > 0.35f && h > 0.12f && h < 0.36f))
                    {
                        result[i] = IconPixel.Stone;
                        continue;
                    }
                    bool nearEdge = false;
                    for (int dy = -edgeRadius; dy <= edgeRadius && !nearEdge; dy++)
                    {
                        for (int dx = -edgeRadius; dx <= edgeRadius; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= width || ny >= height || source[ny * width + nx].a < 40)
                            {
                                nearEdge = true;
                                break;
                            }
                        }
                    }
                    result[i] = nearEdge ? IconPixel.Outline : IconPixel.Rune;
                }
            }
            return result;
        }

        private static Color32[] RecolorIcon(Color32[] source, IconPixel[] classes, Color stone, bool whiteRunes)
        {
            var result = new Color32[source.Length];
            Color outline = Color.Lerp(stone, Color.white, 0.35f);
            for (int i = 0; i < source.Length; i++)
            {
                Color32 px = source[i];
                Color.RGBToHSV(px, out _, out _, out float v);
                Color c;
                switch (classes[i])
                {
                    case IconPixel.Transparent:
                        result[i] = px;
                        continue;
                    case IconPixel.Rune:
                        c = whiteRunes ? Color.Lerp(Color.white * 0.75f, Color.white, v) : Color.Lerp(Color.black, Color.white * 0.12f, v);
                        break;
                    case IconPixel.Outline:
                        c = outline * Mathf.Clamp01(0.4f + v * 0.8f);
                        break;
                    default:
                        c = stone * Mathf.Clamp01(0.55f + v * 1.5f);
                        break;
                }
                c.a = px.a / 255f;
                result[i] = c;
            }
            return result;
        }

        private enum IconPixel : byte
        {
            Transparent,
            Stone,
            Rune,
            Outline,
        }

        private RuneColor ResolveRuneColor(HexColor hex)
        {
            RuneColor rune = hex.runeColor.Value;
            if (rune == RuneColor.Auto)
            {
                rune = hex.stone.Value.grayscale > AutoRuneLuminanceThreshold ? RuneColor.Black : RuneColor.White;
            }
            return rune;
        }

        private void Update()
        {
            if (configFileChanged)
            {
                configFileChanged = false;
                configReloadTime = Time.unscaledTime + 0.3f;
            }
            if (configReloadTime > 0f && Time.unscaledTime >= configReloadTime)
            {
                configReloadTime = 0f;
                try
                {
                    Config.Reload();
                }
                catch (System.Exception e)
                {
                    Logger.LogWarning("Couldn't reload config: " + e.Message);
                }
                ApplyAllHexColors();
            }
        }

        private void WatchConfigFile()
        {
            try
            {
                configWatcher = new FileSystemWatcher(System.IO.Path.GetDirectoryName(Config.ConfigFilePath), System.IO.Path.GetFileName(Config.ConfigFilePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                configWatcher.Changed += (_, __) => configFileChanged = true;
                configWatcher.Created += (_, __) => configFileChanged = true;
                configWatcher.EnableRaisingEvents = true;
            }
            catch (System.Exception e)
            {
                Logger.LogWarning("Couldn't watch the config file; color changes need a restart: " + e.Message);
            }
        }

        private void OnDestroy()
        {
            configWatcher?.Dispose();
        }

        private void LogHexMaterials(string hexName, List<Renderer> renderers)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[Hex Colors] {hexName}: {renderers.Count} renderer(s)");
            foreach (Renderer renderer in renderers)
            {
                if (!renderer)
                {
                    continue;
                }
                sb.AppendLine($"  {renderer.GetType().Name} '{renderer.name}' (root '{renderer.transform.root.name}')");
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (!material)
                    {
                        continue;
                    }
                    Shader shader = material.shader;
                    sb.AppendLine($"    material '{material.name}', shader '{(shader ? shader.name : "none")}'");
                    if (!shader)
                    {
                        continue;
                    }
                    for (int i = 0; i < shader.GetPropertyCount(); i++)
                    {
                        string prop = shader.GetPropertyName(i);
                        string value;
                        switch (shader.GetPropertyType(i))
                        {
                            case UnityEngine.Rendering.ShaderPropertyType.Color:
                                value = material.GetColor(prop).ToString();
                                break;
                            case UnityEngine.Rendering.ShaderPropertyType.Texture:
                                Texture tex = material.GetTexture(prop);
                                value = tex ? tex.name : "none";
                                break;
                            case UnityEngine.Rendering.ShaderPropertyType.Vector:
                                value = material.GetVector(prop).ToString();
                                break;
                            default:
                                value = material.GetFloat(prop).ToString("0.###");
                                break;
                        }
                        sb.AppendLine($"      {prop} = {value}");
                    }
                }
            }
            Logger.LogInfo(sb.ToString());
        }

        private class RecoloredMaterial
        {
            public Material material;
            public Material original;
            public HexColor hex;
            public bool isRunes;
        }

        private class HexColor
        {
            public string name;
            public System.Func<ItemDef> getItem;
            public ConfigEntry<Color> stone;
            public ConfigEntry<RuneColor> runeColor;
            public ItemDef item;
            public Sprite originalIcon;
            public AssetReferenceT<Sprite> originalIconReference;
            public Sprite icon;
            public Color32[] originalIconPixels;
            public IconPixel[] iconClasses;
        }

        public enum RuneColor
        {
            Auto,
            White,
            Black,
        }

        private class HolderOnlyMarker : MonoBehaviour
        {
            public FogDamageController controller;
            public float originalRamp;
        }

        // FogDamageController only passes a position, so the body standing there is looked up to decide if it holds the hex.
        private class NonHolderSafeZone : IZone
        {
            private readonly TeamIndex team;

            public NonHolderSafeZone(TeamIndex team)
            {
                this.team = team;
            }

            public bool IsInBounds(Vector3 position)
            {
                IReadOnlyList<TeamComponent> members = TeamComponent.GetTeamMembers(team);
                for (int i = 0; i < members.Count; i++)
                {
                    TeamComponent member = members[i];
                    if (!member || member.transform.position != position)
                    {
                        continue;
                    }
                    CharacterBody body = member.body;
                    Inventory inventory = body ? body.inventory : null;
                    return !inventory || inventory.GetItemCountEffective(DLC4Content.Items.OutOfTPRadius) <= 0;
                }
                return false;
            }
        }
    }
}
