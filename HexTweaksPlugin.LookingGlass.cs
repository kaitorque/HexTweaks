using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LookingGlass.ItemStatsNameSpace;
using RoR2;
using UnityEngine;

namespace HexTweaks
{
    public partial class HexTweaksPlugin
    {
        private const string LookingGlassGuid = "droppod.lookingglass";

        // LookingGlass types live only in the nested class, so they never load unless LookingGlass is installed.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RegisterLookingGlassStats()
        {
            LookingGlassStats.Register(this);
        }

        // Registered after ItemCatalog availability, where LookingGlass adds its vanilla hex stats, so these replace them.
        private static class LookingGlassStats
        {
            public static void Register(HexTweaksPlugin p)
            {
                if (p.rainEnabled.Value && DLC4Content.Items.IncreasedDifficulty)
                {
                    var def = new ItemStatsDef();
                    AddStat(def, "Damage Taken: ", ItemStatsDef.ValueType.Death, ItemStatsDef.MeasurementUnits.Percentage);
                    AddStat(def, "Less Damage Dealt: ", ItemStatsDef.ValueType.Death, ItemStatsDef.MeasurementUnits.Percentage);
                    AddStat(def, "Cooldown Increase: ", ItemStatsDef.ValueType.Death, ItemStatsDef.MeasurementUnits.Percentage);
                    AddStat(def, "Attack Speed Reduction: ", ItemStatsDef.ValueType.Death, ItemStatsDef.MeasurementUnits.Percentage);
                    AddStat(def, "Move Speed Reduction: ", ItemStatsDef.ValueType.Death, ItemStatsDef.MeasurementUnits.Percentage);
                    def.calculateValuesFlat = count => new List<float>
                    {
                        p.rainDamagePerStack.Value * count,
                        MultiplicativeReduction(p.rainDamageDealtReductionPerStack.Value, count),
                        p.rainCooldownPerStack.Value * count,
                        1f - 1f / (1f + p.rainAttackSpeedReductionPerStack.Value * count),
                        1f - 1f / (1f + p.rainMoveSpeedReductionPerStack.Value * count),
                    };
                    ItemDefinitions.allItemDefinitions[(int)DLC4Content.Items.IncreasedDifficulty.itemIndex] = def;
                }
                if (p.strifeEnabled.Value && DLC4Content.Items.HexMountain)
                {
                    var def = new ItemStatsDef();
                    AddStat(def, "Boss Damage Taken: ", ItemStatsDef.ValueType.Death, ItemStatsDef.MeasurementUnits.Percentage);
                    AddStat(def, "Less Damage Dealt to Bosses: ", ItemStatsDef.ValueType.Death, ItemStatsDef.MeasurementUnits.Percentage);
                    def.calculateValuesFlat = count => new List<float>
                    {
                        p.strifeDamagePerStack.Value * count,
                        MultiplicativeReduction(p.strifeBossDamageDealtReductionPerStack.Value, count),
                    };
                    ItemDefinitions.allItemDefinitions[(int)DLC4Content.Items.HexMountain.itemIndex] = def;
                }
                if (p.ossificationEnabled.Value && DLC4Content.Items.BarrierEnemiesOnKill)
                {
                    var def = new ItemStatsDef();
                    AddStat(def, "Less Damage Dealt After Kill: ", ItemStatsDef.ValueType.Death, ItemStatsDef.MeasurementUnits.Percentage);
                    AddStat(def, "Duration: ", ItemStatsDef.ValueType.Utility, ItemStatsDef.MeasurementUnits.Seconds);
                    def.calculateValuesFlat = count => new List<float>
                    {
                        Mathf.Clamp01(p.ossificationDamageReduction.Value),
                        p.ossificationDuration.Value + p.ossificationDurationPerStack.Value * (count - 1),
                    };
                    ItemDefinitions.allItemDefinitions[(int)DLC4Content.Items.BarrierEnemiesOnKill.itemIndex] = def;
                }
                if (p.greedEnabled.Value && DLC4Content.Items.ShareItemsWithEnemies)
                {
                    var def = new ItemStatsDef();
                    AddStat(def, "Gold Reduction: ", ItemStatsDef.ValueType.Gold, ItemStatsDef.MeasurementUnits.Percentage);
                    def.calculateValuesFlat = count => new List<float>
                    {
                        MultiplicativeReduction(p.greedGoldReductionPerStack.Value, count),
                    };
                    ItemDefinitions.allItemDefinitions[(int)DLC4Content.Items.ShareItemsWithEnemies.itemIndex] = def;
                }
            }

            private static void AddStat(ItemStatsDef def, string description, ItemStatsDef.ValueType valueType, ItemStatsDef.MeasurementUnits units)
            {
                def.descriptions.Add(description);
                def.valueTypes.Add(valueType);
                def.measurementUnits.Add(units);
            }

            private static float MultiplicativeReduction(float perStack, int count)
            {
                return 1f - Mathf.Pow(Mathf.Clamp01(1f - perStack), count);
            }
        }
    }
}
