using System;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    public enum CombatType
    {
        Melee,
        Missile,
        Magic
    };

    /// <summary>
    /// Handles combat with a Player as the attacker
    /// generalized methods for melee / missile
    /// </summary>
    partial class Player
    {
        /// <summary>
        /// Returns the current attack skill for the player
        /// </summary>
        public override Skill GetCurrentAttackSkill()
        {
            if (CombatMode == CombatMode.Magic)
                return GetCurrentMagicSkill();
            else
                return GetCurrentWeaponSkill();
        }

        /// <summary>
        /// Returns the current weapon skill for the player
        /// </summary>
        public override Skill GetCurrentWeaponSkill()
        {
            var weapon = GetEquippedWeapon();

            // missile weapon
            if (weapon != null && weapon.CurrentWieldedLocation == EquipMask.MissileWeapon)
                return GetCreatureSkill(Skill.MissileWeapons).Skill;

            // hack for converting pre-MoA skills
            var maxMelee = GetCreatureSkill(GetHighestMeleeSkill());

            // DualWieldAlternate will be TRUE if *next* attack is offhand
            if (IsDualWieldAttack && !DualWieldAlternate)
            {
                var dualWield = GetCreatureSkill(Skill.DualWield);

                // offhand attacks use the lower skill level between dual wield and weapon skill
                if (dualWield.Current < maxMelee.Current)
                    return dualWield.Skill;
            }

            return maxMelee.Skill;
        }

        /// <summary>
        /// Returns the highest melee skill for the player
        /// (light / heavy / finesse)
        /// </summary>
        public Skill GetHighestMeleeSkill()
        {
            var light = GetCreatureSkill(Skill.LightWeapons);
            var heavy = GetCreatureSkill(Skill.HeavyWeapons);
            var finesse = GetCreatureSkill(Skill.FinesseWeapons);

            var maxMelee = light;
            if (heavy.Current > maxMelee.Current)
                maxMelee = heavy;
            if (finesse.Current > maxMelee.Current)
                maxMelee = finesse;

            return maxMelee.Skill;
        }

        public override CombatType GetAttackType()
        {
            var weapon = GetEquippedWeapon();

            if (weapon == null || weapon.CurrentWieldedLocation != EquipMask.MissileWeapon)
                return CombatType.Melee;
            else
                return CombatType.Missile;
        }

        public float? DamageTarget(Creature target, WorldObject damageSource)
        {
            if (target.Health.Current <= 0)
                return 0.0f;

            var critical = false;
            var sneakAttack = false;

            // check PK status
            var targetPlayer = target as Player;
            if (targetPlayer != null)
            {
                var pkStatus = CheckPKStatusVsTarget(this, targetPlayer, null);
                if (pkStatus != null && pkStatus == false)
                {
                    Session.Network.EnqueueSend(new GameMessageSystemChat($"You fail to affect {targetPlayer.Name} because you are not a player killer!", ChatMessageType.Magic));
                    return 0.0f;
                }
            }
            var bodyPart = BodyPart.Chest;
            var damageType = GetDamageType();

            float? damage = null;
            if (targetPlayer != null)
            {
                damage = CalculateDamagePVP(target, damageSource, damageType, ref critical, ref sneakAttack, ref bodyPart);

                // TODO: level up shield mod?
                if (targetPlayer.Invincible ?? false)
                    damage = 0.0f;
            }
            else
                damage = CalculateDamage(target, damageSource, ref critical, ref sneakAttack);

            if (damage != null)
            {
                var attackType = GetAttackType();
                OnDamageTarget(target, attackType);

                if (targetPlayer != null)
                    targetPlayer.TakeDamage(this, damageType, damage.Value, bodyPart, critical);
                else
                    target.TakeDamage(this, damageType, damage.Value, critical);
            }
            else
            {
                if (targetPlayer != null && targetPlayer.UnderLifestoneProtection)
                    Session.Network.EnqueueSend(new GameMessageSystemChat($"The Lifestone's magic protects {target.Name} from the attack!", ChatMessageType.Magic));
                else
                    Session.Network.EnqueueSend(new GameMessageSystemChat($"{target.Name} evaded your attack.", ChatMessageType.CombatSelf));
            }

            if (damage != null && target.Health.Current > 0)
            {
                var recklessnessMod = critical ? 1.0f : GetRecklessnessMod();

                var attackConditions = new AttackConditions();
                if (recklessnessMod > 1.0f)
                    attackConditions |= AttackConditions.Recklessness;
                if (sneakAttack)
                    attackConditions |= AttackConditions.SneakAttack;

                // notify attacker
                var intDamage = (uint)Math.Round(damage.Value);
                if (damageSource?.ItemType == ItemType.MissileWeapon)
                {
                    Session.Network.EnqueueSend(new GameEventAttackerNotification(Session, target.Name, damageType, (float)intDamage / target.Health.MaxValue, intDamage, critical, attackConditions));
                }
                else
                {
                    Session.Network.EnqueueSend(new GameEventAttackerNotification(Session, target.Name, GetDamageType(), (float)intDamage / target.Health.MaxValue, intDamage, critical, attackConditions));
                }

                // splatter effects
                if (targetPlayer == null)
                {
                    Session.Network.EnqueueSend(new GameMessageSound(target.Guid, Sound.HitFlesh1, 0.5f));
                    if (damage >= target.Health.MaxValue * 0.25f)
                    {
                        var painSound = (Sound)Enum.Parse(typeof(Sound), "Wound" + ThreadSafeRandom.Next(1, 3), true);
                        Session.Network.EnqueueSend(new GameMessageSound(target.Guid, painSound, 1.0f));
                    }
                    var splatter = (PlayScript)Enum.Parse(typeof(PlayScript), "Splatter" + GetSplatterHeight() + GetSplatterDir(target));
                    Session.Network.EnqueueSend(new GameMessageScript(target.Guid, splatter));
                }

                // handle Dirty Fighting
                if (GetCreatureSkill(Skill.DirtyFighting).AdvancementClass >= SkillAdvancementClass.Trained)
                    FightDirty(target);
            }

            if (damage != null && damage > 0.0f)
                Session.Network.EnqueueSend(new GameEventUpdateHealth(Session, target.Guid.Full, (float)target.Health.Current / target.Health.MaxValue));

            if (targetPlayer == null)
                OnAttackMonster(target);

            return damage;
        }

        /// <summary>
        /// Called when a player hits a target
        /// </summary>
        public override void OnDamageTarget(WorldObject target, CombatType attackType)
        {
            var attackSkill = GetCreatureSkill(GetCurrentWeaponSkill());
            var difficulty = GetTargetEffectiveDefenseSkill(target);

            Proficiency.OnSuccessUse(this, attackSkill, difficulty);
        }

        public uint GetEffectiveAttackSkill()
        {
            var attackType = GetAttackType();
            var attackSkill = GetCreatureSkill(GetCurrentWeaponSkill()).Current;
            var offenseMod = GetWeaponOffenseModifier(this);
            var accuracyMod = attackType == CombatType.Missile ? AccuracyLevel + 0.6f : 1.0f;
            attackSkill = (uint)Math.Round(attackSkill * accuracyMod * offenseMod);

            //if (IsExhausted)
                //attackSkill = GetExhaustedSkill(attackSkill);

            //var baseStr = offenseMod != 1.0f ? $" (base: {GetCreatureSkill(GetCurrentWeaponSkill()).Current})" : "";
            //Console.WriteLine("Attack skill: " + attackSkill + baseStr);

            return attackSkill;
        }

        public uint GetTargetEffectiveDefenseSkill(WorldObject target)
        {
            var creature = target as Creature;
            if (creature == null) return 0;

            var attackType = GetAttackType();
            var defenseSkill = attackType == CombatType.Missile ? Skill.MissileDefense : Skill.MeleeDefense;
            var defenseMod = defenseSkill == Skill.MeleeDefense ? GetWeaponMeleeDefenseModifier(creature) : 1.0f;
            var effectiveDefense = (uint)Math.Round(creature.GetCreatureSkill(defenseSkill).Current * defenseMod);

            if (creature.IsExhausted) effectiveDefense = 0;

            //var baseStr = defenseMod != 1.0f ? $" (base: {creature.GetCreatureSkill(defenseSkill).Current})" : "";
            //Console.WriteLine("Defense skill: " + effectiveDefense + baseStr);

            return effectiveDefense;
        }

        public float GetEvadeChance(WorldObject target)
        {
            // get player attack skill
            var attackSkill = GetEffectiveAttackSkill();

            // get target defense skill
            var difficulty = GetTargetEffectiveDefenseSkill(target);

            var evadeChance = 1.0f - SkillCheck.GetSkillChance((int)attackSkill, (int)difficulty);
            return (float)evadeChance;
        }

        /// <summary>
        /// Called when player successfully avoids an attack
        /// </summary>
        public override void OnEvade(WorldObject attacker, CombatType attackType)
        {
            if (UnderLifestoneProtection)
                return;

            // http://asheron.wikia.com/wiki/Attributes

            // Endurance will also make it less likely that you use a point of stamina to successfully evade a missile or melee attack.
            // A player is required to have Melee Defense for melee attacks or Missile Defense for missile attacks trained or specialized
            // in order for this specific ability to work. This benefit is tied to Endurance only, and it caps out at around a 75% chance
            // to avoid losing a point of stamina per successful evasion.

            var defenseSkillType = attackType == CombatType.Missile ? Skill.MissileDefense : Skill.MeleeDefense;
            var defenseSkill = GetCreatureSkill(defenseSkillType);

            if (CombatMode != CombatMode.NonCombat)
            {
                if (defenseSkill.AdvancementClass >= SkillAdvancementClass.Trained)
                {
                    var enduranceBase = Endurance.Base;
                    // TODO: find exact formula / where it caps out at 75%
                    var enduranceCap = 400;
                    var effective = Math.Min(enduranceBase, enduranceCap);
                    var noStaminaUseChance = effective / enduranceCap * 0.75f;
                    if (noStaminaUseChance < ThreadSafeRandom.Next(0.0f, 1.0f))
                        UpdateVitalDelta(Stamina, -1);
                }
                else
                    UpdateVitalDelta(Stamina, -1);
            }
            else
                UpdateVitalDelta(Stamina, -1);

            Session.Network.EnqueueSend(new GameMessageSystemChat($"You evaded {attacker.Name}!", ChatMessageType.CombatEnemy));

            var creature = attacker as Creature;
            if (creature == null) return;

            var difficulty = creature.GetCreatureSkill(creature.GetCurrentWeaponSkill()).Current;
            // attackMod?
            Proficiency.OnSuccessUse(this, defenseSkill, difficulty);
        }

        public override Range GetBaseDamage()
        {
            var attackType = GetAttackType();
            var damageSource = attackType == CombatType.Melee ? GetEquippedWeapon() : GetEquippedAmmo();

            return damageSource != null ? damageSource.GetDamageMod(this) : new Range(1, 5);
        }

        /// <summary>
        /// Calculates the creature damage for a physical monster attack
        /// </summary>
        public float? CalculateDamagePVP(WorldObject target, WorldObject damageSource, DamageType damageType, ref bool criticalHit, ref bool sneakAttack, ref BodyPart bodyPart)
        {
            // verify target player killer
            var targetPlayer = target as Player;
            if (targetPlayer == null)
                return null;

            // check lifestone protection
            if (targetPlayer.UnderLifestoneProtection)
            {
                targetPlayer.HandleLifestoneProtection();
                return null;
            }

            // evasion chance
            var evadeChance = GetEvadeChance(target);
            if (ThreadSafeRandom.Next(0.0f, 1.0f) < evadeChance)
                return null;

            // get base damage
            var baseDamageRange = GetBaseDamage();
            var baseDamage = ThreadSafeRandom.Next(baseDamageRange.Min, baseDamageRange.Max);

            // get damage mods
            var attackType = GetAttackType();
            var attributeMod = GetAttributeMod(attackType);
            var powerAccuracyMod = GetPowerAccuracyMod();
            var recklessnessMod = GetRecklessnessMod(this, targetPlayer);
            var sneakAttackMod = GetSneakAttackMod(target);
            sneakAttack = sneakAttackMod > 1.0f;

            var damageRatingMod = AdditiveCombine(recklessnessMod, sneakAttackMod, GetRatingMod(EnchantmentManager.GetDamageRating()));
            //Console.WriteLine("Damage rating: " + ModToRating(damageRatingMod));

            var damage = baseDamage * attributeMod * powerAccuracyMod * damageRatingMod;

            // critical hit
            var attackSkill = GetCreatureSkill(GetCurrentWeaponSkill());
            var critical = GetWeaponPhysicalCritFrequencyModifier(this, attackSkill);
            if (ThreadSafeRandom.Next(0.0f, 1.0f) < critical)
            {
                damage = baseDamageRange.Max * attributeMod * powerAccuracyMod * sneakAttackMod * (1.0f + GetWeaponCritMultiplierModifier(this, attackSkill));
                criticalHit = true;
            }

            // select random body part @ current attack height
            bodyPart = BodyParts.GetBodyPart(AttackHeight.Value);

            // get armor piece
            var armor = GetArmor(bodyPart);

            // get armor modifiers
            var armorMod = GetArmorMod(armor, damageSource, damageType, attackSkill);

            // get resistance modifiers (protect/vuln)
            var resistanceMod = damageSource != null && damageSource.IgnoreMagicResist ? 1.0f : AttackTarget.EnchantmentManager.GetResistanceMod(damageType);

            // weapon resistance mod?
            var damageResistRatingMod = GetNegativeRatingMod(AttackTarget.EnchantmentManager.GetDamageResistRating());

            // get shield modifier
            var attackTarget = AttackTarget as Creature;
            var shieldMod = attackTarget.GetShieldMod(this, damageType);

            var slayerMod = GetWeaponCreatureSlayerModifier(this, target as Creature);
            var elementalDamageMod = GetMissileElementalDamageModifier(this, target as Creature, damageType);

            // scale damage by modifiers
            var outDamage = (damage + elementalDamageMod) * armorMod * shieldMod * slayerMod * resistanceMod * damageResistRatingMod;

            return outDamage;
        }

        public float? CalculateDamage(WorldObject target, WorldObject damageSource, ref bool criticalHit, ref bool sneakAttack)
        {
            var creature = target as Creature;

            // evasion chance
            var evadeChance = GetEvadeChance(target);
            if (ThreadSafeRandom.Next(0.0f, 1.0f) < evadeChance)
                return null;

            // get weapon base damage
            var baseDamageRange = GetBaseDamage();
            var baseDamage = ThreadSafeRandom.Next(baseDamageRange.Min, baseDamageRange.Max);

            // get damage mods
            var attackType = GetAttackType();
            var attributeMod = GetAttributeMod(attackType);
            var powerAccuracyMod = GetPowerAccuracyMod();
            var recklessnessMod = GetRecklessnessMod(this, creature);
            var sneakAttackMod = GetSneakAttackMod(target);
            sneakAttack = sneakAttackMod > 1.0f;

            var damageRatingMod = AdditiveCombine(recklessnessMod, sneakAttackMod, GetRatingMod(EnchantmentManager.GetDamageRating()));
            //Console.WriteLine("Damage rating: " + ModToRating(damageRatingMod));

            var damage = baseDamage * attributeMod * powerAccuracyMod * damageRatingMod;

            // critical hit
            var attackSkill = GetCreatureSkill(GetCurrentWeaponSkill());
            var critical = GetWeaponPhysicalCritFrequencyModifier(this, attackSkill);
            if (ThreadSafeRandom.Next(0.0f, 1.0f) < critical)
            {
                var criticalDamageMod = 1.0f + GetWeaponCritMultiplierModifier(this, attackSkill);
                damage = baseDamageRange.Max * attributeMod * powerAccuracyMod * sneakAttackMod * criticalDamageMod;
                criticalHit = true;
            }

            // get random body part @ attack height
            var bodyPart = BodyParts.GetBodyPart(target, AttackHeight.Value);
            if (bodyPart == null) return null;

            var creaturePart = new Creature_BodyPart(creature, bodyPart, damageSource != null ? damageSource.IgnoreMagicArmor : false, damageSource != null ? damageSource.IgnoreMagicResist : false);
            var weapon = GetEquippedWeapon();
            if (weapon != null && weapon.HasImbuedEffect(ImbuedEffectType.ArmorRending))
                creaturePart.WeaponArmorMod = GetArmorRendingMod(attackSkill);

            // get target armor
            var armor = creaturePart.BaseArmorMod;

            // get target resistance
            DamageType damageType;
            if (damageSource?.ItemType == ItemType.MissileWeapon)
                damageType = (DamageType)damageSource.GetProperty(PropertyInt.DamageType);
            else
                damageType = GetDamageType();

            creaturePart.WeaponResistanceMod = GetWeaponResistanceModifier(this, attackSkill, damageType);
            var resistance = GetResistance(creaturePart, damageType);

            // ratings
            var damageResistRatingMod = GetNegativeRatingMod(creature.EnchantmentManager.GetDamageResistRating());
            //Console.WriteLine("Damage resistance rating: " + NegativeModToRating(damageResistRatingMod));

            // scale damage for armor and shield
            var armorMod = SkillFormula.CalcArmorMod(resistance);
            var shieldMod = creature.GetShieldMod(this, damageType);

            var slayerMod = GetWeaponCreatureSlayerModifier(this, target as Creature);
            var elementalDamageMod = GetMissileElementalDamageModifier(this, target as Creature, damageType);
            return (damage + elementalDamageMod) * armorMod * shieldMod * slayerMod * damageResistRatingMod;
        }

        public float GetPowerAccuracyMod()
        {
            var attackType = GetAttackType();
            if (attackType == CombatType.Melee)
                return PowerLevel + 0.5f;
            else if (attackType == CombatType.Missile)
                return AccuracyLevel + 0.6f;
            else
                return 1.0f;
        }

        public float GetPowerAccuracyBar()
        {
            return GetAttackType() == CombatType.Missile ? AccuracyLevel : PowerLevel;
        }

        public double GetLifeResistance(DamageType damageType)
        {
            double resistance = 1.0;

            switch (damageType)
            {
                case DamageType.Slash:
                    resistance = ResistAcidMod;
                    break;

                case DamageType.Pierce:
                    resistance = ResistPierceMod;
                    break;

                case DamageType.Bludgeon:
                    resistance = ResistBludgeonMod;
                    break;

                case DamageType.Fire:
                    resistance = ResistFireMod;
                    break;

                case DamageType.Cold:
                    resistance = ResistColdMod;
                    break;

                case DamageType.Acid:
                    resistance = ResistAcidMod;
                    break;

                case DamageType.Electric:
                    resistance = ResistElectricMod;
                    break;

                case DamageType.Nether:
                    resistance = ResistNetherMod;
                    break;
            }

            return resistance;
        }

        public float GetResistance(Creature_BodyPart part, DamageType damageType)
        {
            var resistance = 1.0f;

            switch (damageType)
            {
                case DamageType.Slash:
                    resistance = part.ArmorVsSlash;
                    break;

                case DamageType.Pierce:
                    resistance = part.ArmorVsPierce;
                    break;

                case DamageType.Bludgeon:
                    resistance = part.ArmorVsBludgeon;
                    break;

                case DamageType.Fire:
                    resistance = part.ArmorVsFire;
                    break;

                case DamageType.Cold:
                    resistance = part.ArmorVsCold;
                    break;

                case DamageType.Acid:
                    resistance = part.ArmorVsAcid;
                    break;

                case DamageType.Electric:
                    resistance = part.ArmorVsElectric;
                    break;

                case DamageType.Nether:
                    resistance = part.ArmorVsNether;
                    break;
            }

            return resistance;
        }

        public Sound GetHitSound(WorldObject source, BodyPart bodyPart)
        {
            /*var creature = source as Creature;
            var armors = creature.GetArmor(bodyPart);

            foreach (var armor in armors)
            {
                var material = armor.GetProperty(PropertyInt.MaterialType) ?? 0;
                //Console.WriteLine("Name: " + armor.Name + " | Material: " + material);
            }*/
            return Sound.HitFlesh1;
        }

        /// <summary>
        /// Simplified player take damage function, only called for DoTs currently
        /// </summary>
        public override void TakeDamageOverTime(float _amount, DamageType damageType)
        {
            if (Invincible ?? false || IsDead) return;

            // check lifestone protection
            if (UnderLifestoneProtection)
            {
                HandleLifestoneProtection();
                return;
            }

            var amount = (uint)Math.Round(_amount);
            var percent = (float)amount / Health.MaxValue;

            // update health
            var damageTaken = (uint)-UpdateVitalDelta(Health, (int)-amount);

            // update stamina
            UpdateVitalDelta(Stamina, -1);

            // send damage text message
            var nether = damageType == DamageType.Nether ? "nether " : "";
            var text = new GameMessageSystemChat($"You receive {amount} points of periodic {nether}damage.", ChatMessageType.Combat);
            Session.Network.EnqueueSend(text);

            // splatter effects
            //var splatter = new GameMessageScript(Guid, (PlayScript)Enum.Parse(typeof(PlayScript), "Splatter" + creature.GetSplatterHeight() + creature.GetSplatterDir(this)));  // not sent in retail, but great visual indicator?
            var splatter = new GameMessageScript(Guid, damageType == DamageType.Nether ? ACE.Entity.Enum.PlayScript.HealthDownVoid : ACE.Entity.Enum.PlayScript.DirtyFightingDamageOverTime);
            EnqueueBroadcast(splatter);

            if (Health.Current <= 0)
            {
                // since damage over time is possibly combined from multiple sources,
                // sending a message to the last damager here could be tricky..
                OnDeath(null, damageType, false);
                Die();

                return;
            }

            if (percent >= 0.1f)
                EnqueueBroadcast(new GameMessageSound(Guid, Sound.Wound1, 1.0f));
        }

        /// <summary>
        /// Applies damages to a player from a physical damage source
        /// </summary>
        public void TakeDamage(WorldObject source, DamageType damageType, float _amount, BodyPart bodyPart, bool crit = false)
        {
            if (Invincible ?? false) return;

            // check lifestone protection
            if (UnderLifestoneProtection)
            {
                HandleLifestoneProtection();
                return;
            }

            var amount = (uint)Math.Round(_amount);
            var percent = (float)amount / Health.MaxValue;

            // update health
            var damageTaken = (uint)-UpdateVitalDelta(Health, (int)-amount);
            DamageHistory.Add(source, damageType, damageTaken);

            if (Health.Current == 0)
            {
                OnDeath(source, damageType, crit);
                Die();
                return;
            }

            // update stamina
            UpdateVitalDelta(Stamina, -1);

            var damageLocation = (DamageLocation)BodyParts.Indices[bodyPart];

            // send network messages
            var creature = source as Creature;
            var hotspot = source as Hotspot;
            if (creature != null)
            {
                var text = new GameEventDefenderNotification(Session, creature.Name, damageType, percent, amount, damageLocation, crit, AttackConditions.None);
                Session.Network.EnqueueSend(text);

                var hitSound = new GameMessageSound(Guid, GetHitSound(source, bodyPart), 1.0f);
                var splatter = new GameMessageScript(Guid, (PlayScript)Enum.Parse(typeof(PlayScript), "Splatter" + creature.GetSplatterHeight() + creature.GetSplatterDir(this)));
                EnqueueBroadcast(hitSound, splatter);
            }
            else if (hotspot != null)
            {
                if (!string.IsNullOrWhiteSpace(hotspot.ActivationTalkString))
                    Session.Network.EnqueueSend(new GameMessageSystemChat(hotspot.ActivationTalkString.Replace("%i", amount.ToString()), ChatMessageType.Craft));
                if (!hotspot.Visibility)
                    hotspot.EnqueueBroadcast(new GameMessageSound(hotspot.Guid, Sound.TriggerActivated, 1.0f));
            }

            if (percent >= 0.1f)
                EnqueueBroadcast(new GameMessageSound(Guid, Sound.Wound1, 1.0f));
        }

        public string GetArmorType(BodyPart bodyPart)
        {
            // Flesh, Leather, Chain, Plate
            // for hit sounds
            return null;
        }

        /// <summary>
        /// Returns the total burden of items held in both hands
        /// (main hand and offhand)
        /// </summary>
        public int GetHandItemBurden()
        {
            // get main hand item
            var weapon = GetEquippedWeapon();

            // get off-hand item
            var shield = GetEquippedShield();

            var weaponBurden = weapon != null ? (weapon.EncumbranceVal ?? 0) : 0;
            var shieldBurden = shield != null ? (shield.EncumbranceVal ?? 0) : 0;

            return weaponBurden + shieldBurden;
        }

        public float GetStaminaMod()
        {
            var endurance = Endurance.Base;

            var staminaMod = 1.0f - (endurance - 100.0f) / 600.0f;   // guesstimated formula: 50% reduction at 400 base endurance
            staminaMod = Math.Clamp(staminaMod, 0.5f, 1.0f);

            return staminaMod;
        }

        /// <summary>
        /// Calculates the amount of stamina required to perform this attack
        /// </summary>
        public int GetAttackStamina(PowerAccuracy powerAccuracy)
        {
            // Stamina cost for melee and missile attacks is based on the total burden of what you are holding
            // in your hands (main hand and offhand), and your power/accuracy bar.

            // Attacking(Low power / accuracy bar)   1 point per 700 burden units
            //                                       1 point per 1200 burden units
            //                                       1.5 points per 1600 burden units
            // Attacking(Mid power / accuracy bar)   1 point per 700 burden units
            //                                       2 points per 1200 burden units
            //                                       3 points per 1600 burden units
            // Attacking(High power / accuracy bar)  2 point per 700 burden units
            //                                       4 points per 1200 burden units
            //                                       6 points per 1600 burden units

            // The higher a player's base Endurance, the less stamina one uses while attacking. This benefit is tied to Endurance only,
            // and caps out at 50% less stamina used per attack. Scaling is similar to other Endurance bonuses. Applies only to players.

            // When stamina drops to 0, your melee and missile defenses also drop to 0 and you will be incapable of attacking.
            // In addition, you will suffer a 50% penalty to your weapon skill. This applies to players and creatures.

            var burden = GetHandItemBurden();

            var baseCost = StaminaTable.GetStaminaCost(powerAccuracy, burden);

            var staminaMod = GetStaminaMod();

            var staminaCost = Math.Max(baseCost * staminaMod, 1);

            //Console.WriteLine($"GetAttackStamina({powerAccuracy}) - burden: {burden}, baseCost: {baseCost}, staminaMod: {staminaMod}, staminaCost: {staminaCost}");

            return (int)Math.Round(staminaCost);
        }

        /// <summary>
        /// Returns the damage rating modifier for an applicable Recklessness attack
        /// </summary>
        /// <param name="powerAccuracyBar">The 0.0 - 1.0 power/accurary bar</param>
        public float GetRecklessnessMod(/*float powerAccuracyBar*/)
        {
            // ensure melee or missile combat mode
            if (CombatMode != CombatMode.Melee && CombatMode != CombatMode.Missile)
                return 1.0f;

            var skill = GetCreatureSkill(Skill.Recklessness);

            // recklessness skill must be either trained or specialized to use
            if (skill.AdvancementClass < SkillAdvancementClass.Trained)
                return 1.0f;

            // recklessness is active when attack bar is between 20% and 80% (according to wiki)
            // client attack bar range seems to indicate this might have been updated, between 10% and 90%?
            var powerAccuracyBar = GetPowerAccuracyBar();
            //if (powerAccuracyBar < 0.2f || powerAccuracyBar > 0.8f)
            if (powerAccuracyBar < 0.1f || powerAccuracyBar > 0.9f)
                return 1.0f;

            // recklessness only applies to non-critical hits,
            // which is handled outside of this method.

            // damage rating is increased by 20 for specialized, and 10 for trained.
            // incoming non-critical damage from all sources is increased by the same.
            var damageRating = skill.AdvancementClass == SkillAdvancementClass.Specialized ? 20 : 10;

            // if recklessness skill is lower than current attack skill (as determined by your equipped weapon)
            // then the damage rating is reduced proportionately. The damage rating caps at 10 for trained
            // and 20 for specialized, so there is no reason to raise the skill above your attack skill.
            var attackSkill = GetCreatureSkill(GetCurrentAttackSkill());

            if (skill.Current < attackSkill.Current)
            {
                var scale = (float)skill.Current / attackSkill.Current;
                damageRating = (int)Math.Round(damageRating * scale);
            }

            // The damage rating adjustment for incoming damage is also adjusted proportinally if your Recklessness skill
            // is lower than your active attack skill

            var recklessnessMod = GetDamageRating(damageRating);    // trained DR 1.10 = 10% additional damage
                                                                    // specialized DR 1.20 = 20% additional damage
            return recklessnessMod;
        }

        public Player GetKiller_PKLite()
        {
            if (PlayerKillerStatus == PlayerKillerStatus.PKLite)
                return CurrentLandblock?.GetObject(new ObjectGuid(Killer ?? 0)) as Player;
            else
                return null;
        }
    }
}
