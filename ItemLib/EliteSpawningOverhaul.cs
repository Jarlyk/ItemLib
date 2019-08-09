﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using RoR2;
namespace ItemLib
{
    public static class EliteSpawningOverhaul
    {
        public static void Init()
        {
            On.RoR2.CombatDirector.PrepareNewMonsterWave += CombatDirectorOnPrepareNewMonsterWave;
            IL.RoR2.CombatDirector.AttemptSpawnOnTarget += CombatDirectorOnAttemptSpawnOnTarget;

            //We also need to override usages of highestEliteCostMultiplier, but there's no built-in hook for this
            //We have to use MonoMod directly
            var method = typeof(CombatDirector).GetMethod("get_highestEliteCostMultiplier");
            MonoMod.RuntimeDetour.HookGen.HookEndpointManager.Modify(method, (Action<ILContext>)CombatDirectorGetHighestEliteCostMultiplier);

            //Create default cards for vanilla elites
            Cards.Add(new EliteAffixCard
            {
                spawnWeight = 1.0f,
                costMultiplier = 6.0f,
                damageBoostCoeff = 2.0f,
                healthBoostCoeff = 4.7f,
                eliteType = EliteIndex.Fire
            });
            Cards.Add(new EliteAffixCard
            {
                spawnWeight = 1.0f,
                costMultiplier = 6.0f,
                damageBoostCoeff = 2.0f,
                healthBoostCoeff = 4.7f,
                eliteType = EliteIndex.Ice
            });
            Cards.Add(new EliteAffixCard
            {
                spawnWeight = 1.0f,
                costMultiplier = 6.0f,
                damageBoostCoeff = 2.0f,
                healthBoostCoeff = 4.7f,
                eliteType = EliteIndex.Lightning
            });
            Cards.Add(new EliteAffixCard
            {
                spawnWeight = 1.0f,
                costMultiplier = 36.0f,
                damageBoostCoeff = 6.0f,
                healthBoostCoeff = 28.2f,
                eliteType = EliteIndex.Poison,
                isAvailable = () => Run.instance.loopClearCount > 0
            });

            Enabled = true;
        }

        public static bool Enabled { get; private set; }

        public static List<EliteAffixCard> Cards { get; } = new List<EliteAffixCard>();

        private static Dictionary<CombatDirector, EliteAffixCard> _chosenAffix = new Dictionary<CombatDirector, EliteAffixCard>();

        private static void CombatDirectorOnPrepareNewMonsterWave(On.RoR2.CombatDirector.orig_PrepareNewMonsterWave orig, CombatDirector self, DirectorCard monsterCard)
        {
            //NOTE: We're completely rewriting this method, so we don't call back to the orig

            self.SetFieldValue("currentMonsterCard", monsterCard);
            _chosenAffix[self] = null;
            if (!((CharacterSpawnCard) monsterCard.spawnCard).noElites)
            {
                var eliteSelection = new WeightedSelection<EliteAffixCard>();

                foreach (var card in Cards)
                {
                    var weight = card.GetSpawnWeight(monsterCard);
                    if (weight > 0 && card.isAvailable())
                    {
                        var cost = monsterCard.cost*card.costMultiplier;
                        if (cost <= self.monsterCredit)
                        {
                            eliteSelection.AddChoice(card, weight);
                        }
                    }
                }

                if (eliteSelection.Count > 0)
                {
                    var rng = self.GetFieldValue<Xoroshiro128Plus>("rng");
                    var card = eliteSelection.Evaluate(rng.nextNormalizedFloat);
                    _chosenAffix[self] = card;
                }
            }

            self.lastAttemptedMonsterCard = monsterCard;
            self.SetFieldValue("spawnCountInCurrentWave", 0);
        }

        private delegate EliteIndex GetNextEliteDel(CombatDirector self, ref float cost, out float scaledCost);

        private delegate void GetCoeffsDel(CombatDirector self, out float hpCoeff, out float dmgCoeff);

        private static void CombatDirectorOnAttemptSpawnOnTarget(ILContext il)
        {
            var c = new ILCursor(il);

            //First, we rewrite the section of the code that haandles scaling cost by elite tier, to see if it can spawn as elite
            //Since cost scaling is now part of the card, we use that instead of the tier def
            c.GotoNext(i => i.MatchLdarg(0),
                       i => i.MatchLdfld("RoR2.CombatDirector", "currentActiveEliteTier"));

            //Getting the next elite also needs to set some local variables as side effects for the rest of the code
            //Thus, the ugly-looking delegate
            c.Emit(OpCodes.Ldarg_0);
            c.Emit(OpCodes.Ldloca_S, (byte) 0);
            c.Emit(OpCodes.Ldloca_S, (byte) 1);
            c.EmitDelegate<GetNextEliteDel>(GetNextElite);
            c.Emit(OpCodes.Stloc_3);
            
            //And we'll need to skip over the code in the original method
            var skip1 = c.DefineLabel();
            c.Emit(OpCodes.Br, skip1);
            c.GotoNext(i => i.MatchLdarg(0),
                       i => i.MatchLdfld("RoR2.CombatDirector", "currentMonsterCard"));
            c.MarkLabel(skip1);

            //The original code now spawns the actual monster and assigns it to a squad, if applicable
            //We're going to modify once it reaches the part where it starts applying the Elite-related changes
            c.GotoNext(i => i.MatchLdloc(2),
                       i => i.MatchLdfld("RoR2.CombatDirector/EliteTierDef", "healthBoostCoefficient"));

            //We're going to replace these 6 instructions:
            //IL_03d5: ldloc.2
            //IL_03d6: ldfld float32 RoR2.CombatDirector / EliteTierDef::healthBoostCoefficient
            //IL_03db: stloc.s V_8
            //IL_03dd: ldloc.2
            //IL_03de: ldfld float32 RoR2.CombatDirector / EliteTierDef::damageBoostCoefficient
            //IL_03e3: stloc.s V_9
            c.RemoveRange(6);
            c.Emit(OpCodes.Ldarg_0);
            c.Emit(OpCodes.Ldloca_S, (byte) 8);
            c.Emit(OpCodes.Ldloca_S, (byte) 9);
            c.EmitDelegate<GetCoeffsDel>(GetCoeffs);

            //Finally, just before it launches the spawnEffect, we'll give the card's creator a chance to apply changes to the character
            c.GotoNext(i => i.MatchLdarg(0),
                       i => i.MatchLdfld("RoR2.CombatDirector", "spawnEffectPrefab"));
            c.Emit(OpCodes.Ldarg_0);
            c.Emit(OpCodes.Ldloc_S, (byte) 10);
            c.EmitDelegate<Action<CombatDirector, CharacterMaster>>(RunOnSpawn);
        }

        private static EliteIndex GetNextElite(CombatDirector self, ref float cost, out float scaledCost)
        {
            _chosenAffix.TryGetValue(self, out var affix);
            scaledCost = affix != null ? affix.costMultiplier*cost : cost;
            if (scaledCost < self.monsterCredit)
            {
                cost = scaledCost;
                return affix?.eliteType ?? EliteIndex.None;
            }

            return EliteIndex.None;
        }

        private static void GetCoeffs(CombatDirector self, out float hpCoeff, out float dmgCoeff)
        {
            _chosenAffix.TryGetValue(self, out var affix);
            if (affix != null)
            {
                hpCoeff = affix.healthBoostCoeff;
                dmgCoeff = affix.damageBoostCoeff;
            }
            else
            {
                hpCoeff = 1;
                dmgCoeff = 1;
            }
        }

        private static void RunOnSpawn(CombatDirector self, CharacterMaster master)
        {
            _chosenAffix.TryGetValue(self, out var affix);
            affix?.onSpawned?.Invoke(master);
        }

        private static void CombatDirectorGetHighestEliteCostMultiplier(ILContext il)
        {
            var c = new ILCursor(il);

            //We completely replace this getter
            c.RemoveRange(c.Instrs.Count);
            c.EmitDelegate<Func<float>>(GetHighestEliteCostMultiplier);
            c.Emit(OpCodes.Ret);
        }

        private static float GetHighestEliteCostMultiplier()
        {
            return Cards.Count == 0 ? 1.0f : Cards.Max(c => c.costMultiplier);
        }
    }
}
