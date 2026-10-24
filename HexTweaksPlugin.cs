using System.Collections;
using System.Collections.Generic;
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
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class HexTweaksPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "kaitorque.HexTweaks";
        public const string PluginName = "HexTweaks";
        public const string PluginVersion = "1.0.0";

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
            strifeBossDamageDealtReductionPerStack = Config.Bind("Hex of Strife", "Boss Damage Dealt Reduction Per Stack", 0.2f,
                "Holder deals less damage to bosses per stack, applied multiplicatively. 0.2 = 20% less with 1 stack, 36% less with 2.");
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
                    if (handle.Status == AsyncOperationStatus.Succeeded)
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
