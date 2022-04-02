using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.FormKeys.SkyrimSE;
using Mutagen.Bethesda.Synthesis;
using Noggog;

namespace SpellbindPatcher
{
    public class Program
    {
        private static readonly ModKey SpellbindMod = ModKey.FromNameAndExtension("Spellbind.esp");
        private static readonly FormLink<IMiscItemGetter> SoulGemShard = new FormLink<IMiscItemGetter>(new FormKey(SpellbindMod, 0x14ca1));
        private static readonly FormKey ScrollCraftStationKey = new FormKey(SpellbindMod, 0x0fb2e);
        private static readonly Dictionary<uint, int> ShardCosts = new()
        {
            {0, 1},
            {25, 2},
            {50, 2},
            {75, 3},
            {100, 3}
        };

        public static async Task<int> Main(string[] args)
        {
            return await SynthesisPipeline.Instance
                // Add the runnability check via the pipeline builder
                .AddRunnabilityCheck(CheckRunnability)
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .SetTypicalOpen(GameRelease.SkyrimSE, "YourPatcher.esp")
                .Run(args);
        }

        public static void CheckRunnability(IRunnabilityState state)
        {
            state.LoadOrder.AssertHasMod("Spellbind.esp");
        }

        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            if (!SoulGemShard.TryResolve(state.LinkCache, out _))
            {
                throw new Exception("Could not find required ingredient records");
            }
            
            foreach (var book in state.LoadOrder.PriorityOrder.Book().WinningOverrides())
            {
                if (book.Teaches is not IBookSpellGetter spellBookGetter) continue;
                if (!spellBookGetter.Spell.TryResolve(state.LinkCache, out var taughtSpell)) continue;
                
                Console.WriteLine(
                    "Processing spell: {0} ({1}) from mod {2}", 
                    taughtSpell.Name, 
                    taughtSpell.FormKey.ID, 
                    taughtSpell.FormKey.ModKey.FileName);

                /*
                 Attempt to find a scroll that matches all the effects of this spell.
                 Unfortunately Skyrim scrolls don't actually cast the spell. They just mimic all the effects of
                 whichever spell they are supposed to cast.
                */
                var spellEffects = taughtSpell.Effects
                    .Select(item => item.BaseEffect.TryResolve(state.LinkCache))
                    .NotNull()
                    .Select(x => x.AsLinkGetter())
                    .ToHashSet();

                var matchingScroll = state.LoadOrder.PriorityOrder.Scroll().WinningOverrides()
                    .FirstOrDefault(scroll => spellEffects.Count == scroll.Effects.Count && scroll.Effects.All(
                        eff => spellEffects.Contains(eff.BaseEffect)
                               && eff.BaseEffect.TryResolve(state.LinkCache, out _)));

                if (matchingScroll == null) continue;

                // Calculate shard cost based on the level of the spell being cast.
                // Assume shard cost of 1 if we cannot find the highest minimum skill level to cast.
                var highestMinSkillLevel = matchingScroll.Effects.Max(eff =>
                    eff.BaseEffect.Resolve(state.LinkCache).MinimumSkillLevel);
                var shardCost = ShardCosts[(highestMinSkillLevel / 25) * 25];
                
                Console.WriteLine(
                    "Found matching scroll: {0} ({1}) from mod {2}. Scroll has skill level of {3}. Calculated shard cost is {4}", 
                    matchingScroll.Name, 
                    matchingScroll.FormKey.ID, 
                    matchingScroll.FormKey.ModKey.FileName, 
                    highestMinSkillLevel, 
                    shardCost);

                // Construct and add recipe.
                var spellName = taughtSpell.Name?.ToString() ?? "";
                ConstructibleObject scrollRecipe = new(
                    state.PatchMod, 
                    "GN_CraftScroll_" + Regex.Replace( $"{spellName}{taughtSpell.FormKey}", @"[^A-z0-9_]+", ""))
                {
                    CreatedObject = new FormLinkNullable<IConstructibleGetter>(matchingScroll),
                    WorkbenchKeyword = new FormLinkNullable<IKeywordGetter>(ScrollCraftStationKey),
                    CreatedObjectCount = 1,
                    Items = new ExtendedList<ContainerEntry>()
                    {
                        new ContainerEntry()
                        {
                            Item = new ContainerItem()
                            {
                                Count = 1,
                                Item = Skyrim.MiscItem.PaperRoll,
                            }
                        },
                        new ContainerEntry()
                        {
                            Item = new ContainerItem()
                            {
                                Count = shardCost,
                                Item = SoulGemShard,
                            }
                        },
                    },
                    Conditions = new ExtendedList<Condition>()
                    {
                        new ConditionFloat()
                        {
                            CompareOperator = CompareOperator.EqualTo,
                            ComparisonValue = 1,
                            Data = new FunctionConditionData()
                            {
                                Function = Condition.Function.HasSpell,
                                ParameterOneRecord = new FormLink<ISkyrimMajorRecordGetter>(taughtSpell),
                                RunOnType = Condition.RunOnType.Subject,
                            }
                        },
                    },
                };

                state.PatchMod.ConstructibleObjects.Add(scrollRecipe);
                Console.WriteLine("Successfully created recipe for {0}", matchingScroll.Name);
            }
        }
    }
}