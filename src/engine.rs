use crate::Config;
use std::collections::HashMap;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Eq, Hash, PartialEq)]
pub struct WeaponKey {
    pub inventory_id: u32,
    pub reinforce_level: u16,
    pub infusion: u16,
}

impl WeaponKey {
    pub fn normalized(mut self) -> Self {
        // DSR stores reinforcement/upgrade-path variants in the low three
        // digits of the equipped weapon ID. Weight belongs to the weapon
        // family, so taking a weapon from +4 to +5 does not erase its history.
        self.inventory_id -= self.inventory_id % 1000;
        self.reinforce_level = 0;
        self.infusion = 0;
        self
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct WeaponProgress {
    pub temporary: f32,
    pub permanent: f32,
    pub kills: u32,
    pub bosses: u32,
    pub legacies: u32,
}

impl WeaponProgress {
    pub fn total(&self) -> f32 {
        self.temporary + self.permanent
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum KillKind {
    Normal,
    Boss,
}

#[repr(u32)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WeaponClass {
    Dagger = 0,
    Light = 1,
    Standard = 2,
    Heavy = 3,
    Colossal = 4,
}

impl WeaponClass {
    fn gain(self) -> f32 {
        match self {
            Self::Dagger => 1.30,
            Self::Light => 1.15,
            Self::Standard => 1.0,
            Self::Heavy => 0.85,
            Self::Colossal => 0.72,
        }
    }
    fn impact(self) -> f32 {
        match self {
            Self::Dagger => 0.55,
            Self::Light => 0.75,
            Self::Standard => 1.0,
            Self::Heavy => 1.18,
            Self::Colossal => 1.35,
        }
    }
}

#[repr(u32)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AttackKind {
    Quick = 0,
    Running = 1,
    Heavy = 2,
    Critical = 3,
}

#[repr(u32)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WeightTier {
    Light = 0,
    Tempered = 1,
    Burdened = 2,
    Crushing = 3,
    Devastating = 4,
    Worldbreaker = 5,
    Cataclysmic = 6,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct AttackOutcome {
    pub modifiers: Modifiers,
    pub stamina_after: f32,
    pub exhaustion_seconds: f32,
    pub radial_stagger: f32,
    pub presentation_intensity: f32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct ForgeReceipt {
    pub converted_weight: f32,
    pub soul_cost: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Modifiers {
    pub weight: f32,
    pub damage: f32,
    pub attack_speed: f32,
    pub stamina_cost: f32,
    pub recovery: f32,
    pub impact: f32,
    pub knockback: f32,
}

#[derive(Debug)]
pub struct Engine {
    pub config: Config,
    weapons: HashMap<WeaponKey, WeaponProgress>,
    bloodstain: HashMap<WeaponKey, f32>,
}

impl Engine {
    pub fn new(config: Config) -> Self {
        Self {
            config,
            weapons: HashMap::new(),
            bloodstain: HashMap::new(),
        }
    }
    pub fn progress(&self, key: WeaponKey) -> WeaponProgress {
        self.weapons
            .get(&key.normalized())
            .copied()
            .unwrap_or_default()
    }
    pub fn records(&self) -> impl Iterator<Item = (WeaponKey, WeaponProgress)> + '_ {
        self.weapons.iter().map(|(k, v)| (*k, *v))
    }
    pub fn bloodstain_records(&self) -> impl Iterator<Item = (WeaponKey, f32)> + '_ {
        self.bloodstain.iter().map(|(k, v)| (*k, *v))
    }
    pub fn set_progress(&mut self, key: WeaponKey, progress: WeaponProgress) {
        self.weapons.insert(key.normalized(), progress);
    }
    pub fn merge_progress(&mut self, key: WeaponKey, progress: WeaponProgress) {
        let existing = self.weapons.entry(key.normalized()).or_default();
        existing.temporary += progress.temporary;
        existing.permanent += progress.permanent;
        existing.kills = existing.kills.saturating_add(progress.kills);
        existing.bosses = existing.bosses.saturating_add(progress.bosses);
        existing.legacies |= progress.legacies;
    }
    pub fn merge_bloodstain(&mut self, key: WeaponKey, weight: f32) {
        if weight.is_finite() && weight > 0.0 {
            *self.bloodstain.entry(key.normalized()).or_default() += weight;
        }
    }

    pub fn kill(&mut self, key: WeaponKey, kind: KillKind) -> WeaponProgress {
        self.kill_with_context(key, kind, WeaponClass::Standard, 0)
    }

    pub fn kill_with_context(
        &mut self,
        key: WeaponKey,
        kind: KillKind,
        class: WeaponClass,
        legacy: u32,
    ) -> WeaponProgress {
        let p = self.weapons.entry(key.normalized()).or_default();
        match kind {
            KillKind::Normal => {
                p.temporary += self.config.weight_per_kill * class.gain();
                p.kills = p.kills.saturating_add(1);
            }
            KillKind::Boss => {
                let gained = self.config.boss_weight;
                p.permanent += gained * self.config.boss_permanent_fraction;
                p.temporary += gained * (1.0 - self.config.boss_permanent_fraction);
                p.bosses = p.bosses.saturating_add(1);
                if legacy < 32 {
                    p.legacies |= 1u32 << legacy;
                }
            }
        }
        *p
    }

    pub fn rest(&mut self) {
        let keep = 1.0 - self.config.rest_decay_fraction;
        for p in self.weapons.values_mut() {
            p.temporary *= keep;
            if p.temporary < 0.01 {
                p.temporary = 0.0;
            }
        }
    }

    pub fn undo_rest(&mut self) -> bool {
        let keep = 1.0 - self.config.rest_decay_fraction;
        if keep <= f32::EPSILON {
            return false;
        }
        for p in self.weapons.values_mut() {
            p.temporary /= keep;
        }
        true
    }

    pub fn modifiers(&self, key: WeaponKey) -> Modifiers {
        self.modifiers_for(key, WeaponClass::Standard)
    }

    pub fn modifiers_for(&self, key: WeaponKey, class: WeaponClass) -> Modifiers {
        let w = self.progress(key).total().max(0.0);
        let c = &self.config;
        let stamina_penalty_scale = if class == WeaponClass::Standard {
            c.standard_stamina_penalty_multiplier
        } else {
            1.0
        };
        // Damage, recovery, impact, and knockback remain uncapped. Speed and
        // stamina use explicit playability limits.
        Modifiers {
            weight: w,
            damage: c.base_damage_multiplier
                + c.damage_per_weight * w
                + c.damage_acceleration_per_weight_squared * w * w,
            attack_speed: (c.base_attack_speed_multiplier / (1.0 + c.speed_loss_per_weight * w))
                .max(c.minimum_attack_speed_multiplier),
            stamina_cost: (1.0 + c.stamina_per_weight * stamina_penalty_scale * w)
                .min(c.maximum_stamina_cost_multiplier),
            recovery: 1.0 + c.recovery_per_weight * w,
            impact: 1.0 + c.impact_per_weight * w,
            knockback: 1.0 + c.knockback_per_weight * (w - c.knockback_starts_at).max(0.0),
        }
    }

    pub fn tier(&self, key: WeaponKey) -> WeightTier {
        let c = &self.config;
        match self.progress(key).total() {
            w if w < c.tier_tempered_at => WeightTier::Light,
            w if w < c.tier_burdened_at => WeightTier::Tempered,
            w if w < c.tier_crushing_at => WeightTier::Burdened,
            w if w < c.tier_devastating_at => WeightTier::Crushing,
            w if w < c.tier_worldbreaker_at => WeightTier::Devastating,
            w if w < c.tier_cataclysmic_at => WeightTier::Worldbreaker,
            _ => WeightTier::Cataclysmic,
        }
    }

    pub fn attack(
        &self,
        key: WeaponKey,
        class: WeaponClass,
        kind: AttackKind,
        base_stamina: f32,
        stamina: f32,
    ) -> AttackOutcome {
        let mut m = self.modifiers_for(key, class);
        let attack_scale = match kind {
            AttackKind::Quick => 0.75,
            AttackKind::Running => 1.15,
            AttackKind::Heavy => 1.45,
            AttackKind::Critical => 1.0,
        };
        m.impact = 1.0 + (m.impact - 1.0) * class.impact() * attack_scale;
        m.knockback = 1.0 + (m.knockback - 1.0) * class.impact() * attack_scale;
        // Each boss leaves a small permanent character on the weapon. The
        // groups intentionally grant a benefit and a matching commitment so
        // legacy never becomes a free upgrade. The bit positions correspond
        // to LiveGame's stable boss-defeat table.
        let legacies = self.progress(key).legacies;
        let predator_mask = (1u32 << 3) | (1u32 << 6) | (1u32 << 7) | (1u32 << 8) | (1u32 << 23);
        let titan_mask = (1u32 << 0)
            | (1u32 << 1)
            | (1u32 << 10)
            | (1u32 << 18)
            | (1u32 << 19)
            | (1u32 << 24)
            | (1u32 << 25);
        let predator = (legacies & predator_mask).count_ones() as f32;
        let titan = (legacies & titan_mask).count_ones() as f32;
        let arcane = (legacies & !(predator_mask | titan_mask)).count_ones() as f32;
        m.damage *= 1.0 + 0.025 * predator + 0.015 * titan + 0.02 * arcane;
        m.impact *= 1.0 + 0.04 * titan + 0.015 * predator;
        m.knockback *= 1.0 + 0.035 * titan;
        m.stamina_cost *= 1.0 + 0.018 * predator + 0.025 * titan + 0.02 * arcane;
        m.recovery *= 1.0 + 0.025 * titan + 0.02 * arcane;
        m.attack_speed /= 1.0 + 0.012 * predator + 0.018 * titan + 0.015 * arcane;
        // Commitment stays extreme, but never reaches an effectively frozen
        // animation or an unusable stamina cost, even after boss legacies.
        m.attack_speed = m
            .attack_speed
            .max(self.config.minimum_attack_speed_multiplier);
        m.stamina_cost = m
            .stamina_cost
            .min(self.config.maximum_stamina_cost_multiplier);
        if matches!(kind, AttackKind::Critical) {
            // Backstabs and ripostes keep their authored grab alignment.
            m.knockback = 1.0;
        }
        let cost = (base_stamina * m.stamina_cost).max(0.0);
        let deficit = (cost - stamina).max(0.0);
        let weight = m.weight;
        AttackOutcome {
            modifiers: m,
            stamina_after: (stamina - cost).max(0.0),
            exhaustion_seconds: if deficit > 0.0 {
                0.75 + (deficit / base_stamina.max(1.0)) * 0.65
            } else {
                0.0
            },
            radial_stagger: if weight >= self.config.radial_stagger_starts_at
                && matches!(kind, AttackKind::Heavy)
            {
                (weight - self.config.radial_stagger_starts_at)
                    * self.config.radial_radius_per_weight
            } else {
                0.0
            },
            presentation_intensity: ((weight - 20.0) / 55.0).clamp(0.0, 3.0),
        }
    }

    pub fn forge(&mut self, key: WeaponKey, requested: f32, available_souls: u32) -> ForgeReceipt {
        let p = self.weapons.entry(key.normalized()).or_default();
        let amount = requested.max(0.0).min(p.temporary);
        let soul_cost = (amount * 1000.0).ceil() as u32;
        if amount <= 0.0 || soul_cost > available_souls {
            return ForgeReceipt::default();
        }
        p.temporary -= amount;
        p.permanent += amount;
        ForgeReceipt {
            converted_weight: amount,
            soul_cost,
        }
    }

    pub fn die(&mut self, loss_fraction: f32) {
        self.bloodstain.clear();
        let loss = loss_fraction.clamp(0.0, 1.0);
        for (k, p) in self.weapons.iter_mut() {
            let taken = p.temporary * loss;
            p.temporary -= taken;
            if taken > 0.0 {
                self.bloodstain.insert(*k, taken);
            }
        }
    }

    pub fn recover_bloodstain(&mut self) {
        for (k, w) in self.bloodstain.drain() {
            self.weapons.entry(k.normalized()).or_default().temporary += w;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn key(n: u32) -> WeaponKey {
        WeaponKey {
            inventory_id: n,
            ..Default::default()
        }
    }
    #[test]
    fn weapons_progress_independently() {
        let mut e = Engine::new(Config::default());
        e.kill(key(1000), KillKind::Normal);
        assert_eq!(e.progress(key(1000)).kills, 1);
        assert_eq!(e.progress(key(2000)).kills, 0);
    }
    #[test]
    fn rest_never_removes_boss_forging() {
        let mut e = Engine::new(Config::default());
        e.kill(key(1), KillKind::Boss);
        let permanent = e.progress(key(1)).permanent;
        for _ in 0..50 {
            e.rest();
        }
        assert_eq!(e.progress(key(1)).permanent, permanent);
    }
    #[test]
    fn an_erroneous_rest_can_be_reversed_without_touching_permanent_weight() {
        let mut e = Engine::new(Config::default());
        let k = key(1);
        e.set_progress(
            k,
            WeaponProgress {
                temporary: 10.0,
                permanent: 4.0,
                ..Default::default()
            },
        );
        e.rest();
        assert!(e.undo_rest());
        assert!((e.progress(k).temporary - 10.0).abs() < 0.0001);
        assert_eq!(e.progress(k).permanent, 4.0);
    }
    #[test]
    fn fresh_weapon_is_light_and_weak() {
        let e = Engine::new(Config::default());
        let m = e.modifiers(key(1));
        assert_eq!(m.damage, 0.62);
        assert_eq!(m.attack_speed, 1.15);
    }
    #[test]
    fn curve_is_monotonic_with_commitment_caps() {
        let mut e = Engine::new(Config::default());
        let mut last = e.modifiers(key(1));
        for _ in 0..1000 {
            e.kill(key(1), KillKind::Normal);
            let m = e.modifiers(key(1));
            assert!(
                m.damage >= last.damage
                    && m.attack_speed <= last.attack_speed
                    && m.stamina_cost >= last.stamina_cost
            );
            last = m;
        }
        assert!(last.damage > 40.0);
        assert_eq!(last.stamina_cost, 4.0);
        assert!(last.recovery > 10.0);
        assert_eq!(last.attack_speed, 0.40);
    }
    #[test]
    fn damage_reward_accelerates_as_weight_grows() {
        let mut e = Engine::new(Config::default());
        let k = key(1);
        let at_zero = e.modifiers(k).damage;
        e.kill(k, KillKind::Normal);
        let first_gain = e.modifiers(k).damage - at_zero;
        for _ in 0..49 {
            e.kill(k, KillKind::Normal);
        }
        let at_fifty = e.modifiers(k).damage;
        e.kill(k, KillKind::Normal);
        let later_gain = e.modifiers(k).damage - at_fifty;
        assert!(later_gain > first_gain);
    }
    #[test]
    fn standard_weapons_only_reduce_weight_derived_stamina_penalty() {
        let mut e = Engine::new(Config::default());
        let k = key(1);
        e.set_progress(
            k,
            WeaponProgress {
                temporary: 50.0,
                ..Default::default()
            },
        );
        let standard = e.modifiers_for(k, WeaponClass::Standard);
        let heavy = e.modifiers_for(k, WeaponClass::Heavy);
        assert!(standard.stamina_cost < heavy.stamina_cost);
        assert_eq!(standard.attack_speed, heavy.attack_speed);
        assert_eq!(standard.damage, heavy.damage);
        assert_eq!(standard.recovery, heavy.recovery);
    }
    #[test]
    fn every_weapon_class_unlocks_heavy_attack_effects() {
        let mut e = Engine::new(Config::default());
        let k = key(1);
        e.set_progress(
            k,
            WeaponProgress {
                temporary: 100.0,
                ..Default::default()
            },
        );
        for class in [
            WeaponClass::Dagger,
            WeaponClass::Light,
            WeaponClass::Standard,
            WeaponClass::Heavy,
            WeaponClass::Colossal,
        ] {
            let outcome = e.attack(k, class, AttackKind::Heavy, 0.0, 0.0);
            assert!(outcome.modifiers.knockback > 1.0, "{class:?}");
            assert!(outcome.radial_stagger > 0.0, "{class:?}");
            assert!(outcome.presentation_intensity > 0.0, "{class:?}");
        }
    }
    #[test]
    fn commitment_caps_hold_after_boss_legacies() {
        let mut e = Engine::new(Config::default());
        let k = key(1);
        e.set_progress(
            k,
            WeaponProgress {
                temporary: 1000.0,
                legacies: u32::MAX,
                ..Default::default()
            },
        );
        let outcome = e.attack(k, WeaponClass::Heavy, AttackKind::Heavy, 0.0, 0.0);
        assert_eq!(outcome.modifiers.attack_speed, 0.40);
        assert_eq!(outcome.modifiers.stamina_cost, 4.0);
    }
    #[test]
    fn exhausted_attack_is_allowed_and_reports_recovery() {
        let mut e = Engine::new(Config::default());
        for _ in 0..50 {
            e.kill(key(1), KillKind::Normal);
        }
        let out = e.attack(key(1), WeaponClass::Heavy, AttackKind::Heavy, 30.0, 5.0);
        assert_eq!(out.stamina_after, 0.0);
        assert!(out.exhaustion_seconds > 0.75);
        assert!(out.modifiers.impact > e.modifiers(key(1)).impact);
    }
    #[test]
    fn bloodstain_restores_only_temporary_weight() {
        let mut e = Engine::new(Config::default());
        e.kill(key(1), KillKind::Boss);
        let before = e.progress(key(1));
        e.die(0.5);
        assert!(e.progress(key(1)).temporary < before.temporary);
        assert_eq!(e.progress(key(1)).permanent, before.permanent);
        e.recover_bloodstain();
        assert_eq!(e.progress(key(1)), before);
    }
    #[test]
    fn forging_requires_the_full_displayed_cost() {
        let mut e = Engine::new(Config::default());
        for _ in 0..10 {
            e.kill(key(1), KillKind::Normal);
        }
        let receipt = e.forge(key(1), 8.0, 3000);
        assert_eq!(receipt, ForgeReceipt::default());
        assert_eq!(e.progress(key(1)).temporary, 10.0);
        let receipt = e.forge(key(1), 8.0, 8000);
        assert_eq!(receipt.converted_weight, 8.0);
        assert_eq!(receipt.soul_cost, 8000);
    }

    #[test]
    fn fractional_reforge_charges_exact_fractional_cost() {
        let mut e = Engine::new(Config::default());
        e.set_progress(
            key(1000),
            WeaponProgress {
                temporary: 2.5,
                ..Default::default()
            },
        );
        let receipt = e.forge(key(1000), 5.0, 2500);
        assert_eq!(receipt.converted_weight, 2.5);
        assert_eq!(receipt.soul_cost, 2500);
    }

    #[test]
    fn boss_legacy_adds_power_and_commitment() {
        let mut e = Engine::new(Config::default());
        let k = key(1);
        let before = e.attack(k, WeaponClass::Heavy, AttackKind::Heavy, 30.0, 30.0);
        e.kill_with_context(k, KillKind::Boss, WeaponClass::Heavy, 0);
        let with_legacy = e.attack(k, WeaponClass::Heavy, AttackKind::Heavy, 30.0, 30.0);
        let weight_only = e.modifiers(k);
        assert!(with_legacy.modifiers.damage > weight_only.damage);
        assert!(with_legacy.modifiers.stamina_cost > weight_only.stamina_cost);
        assert!(with_legacy.modifiers.attack_speed < weight_only.attack_speed);
        assert!(with_legacy.modifiers.damage > before.modifiers.damage);
    }

    #[test]
    fn reinforcement_and_infusion_keep_weapon_progress() {
        let mut e = Engine::new(Config::default());
        e.kill(key(310004), KillKind::Normal);
        assert_eq!(e.progress(key(310005)).kills, 1);
        assert_eq!(e.progress(key(310200)).kills, 1);
        assert_eq!(e.progress(key(311000)).kills, 0);
    }

    #[test]
    fn heavy_radial_radius_grows_without_a_cap() {
        let mut e = Engine::new(Config::default());
        let k = key(1000);
        for _ in 0..100 {
            e.kill(k, KillKind::Normal);
        }
        let at_100 = e.attack(k, WeaponClass::Heavy, AttackKind::Heavy, 0.0, 0.0);
        for _ in 0..100 {
            e.kill(k, KillKind::Normal);
        }
        let at_200 = e.attack(k, WeaponClass::Heavy, AttackKind::Heavy, 0.0, 0.0);
        assert!(at_100.radial_stagger > 0.0);
        assert!(at_200.radial_stagger > at_100.radial_stagger);
        assert_eq!(
            e.attack(k, WeaponClass::Heavy, AttackKind::Quick, 0.0, 0.0)
                .radial_stagger,
            0.0
        );
    }

    #[test]
    fn weight_tiers_follow_configured_boundaries() {
        let mut config = Config::default();
        config.tier_tempered_at = 10.0;
        config.tier_burdened_at = 20.0;
        let mut e = Engine::new(config);
        let k = key(1000);
        e.set_progress(
            k,
            WeaponProgress {
                temporary: 15.0,
                ..Default::default()
            },
        );
        assert_eq!(e.tier(k), WeightTier::Tempered);
    }
}
