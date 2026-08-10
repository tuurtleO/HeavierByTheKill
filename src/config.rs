use std::{fs, path::Path};

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Config {
    pub weight_per_kill: f32,
    pub boss_weight: f32,
    pub boss_permanent_fraction: f32,
    pub rest_decay_fraction: f32,
    pub base_damage_multiplier: f32,
    pub base_attack_speed_multiplier: f32,
    pub damage_per_weight: f32,
    pub damage_acceleration_per_weight_squared: f32,
    pub speed_loss_per_weight: f32,
    pub stamina_per_weight: f32,
    pub recovery_per_weight: f32,
    pub impact_per_weight: f32,
    pub knockback_starts_at: f32,
    pub knockback_per_weight: f32,
    pub radial_stagger_starts_at: f32,
    pub radial_radius_per_weight: f32,
    pub camera_shake_starts_at: f32,
    pub tier_tempered_at: f32,
    pub tier_burdened_at: f32,
    pub tier_crushing_at: f32,
    pub tier_devastating_at: f32,
    pub tier_worldbreaker_at: f32,
    pub tier_cataclysmic_at: f32,
    pub standard_stamina_penalty_multiplier: f32,
    pub minimum_attack_speed_multiplier: f32,
    pub maximum_stamina_cost_multiplier: f32,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            weight_per_kill: 1.0,
            boss_weight: 12.0,
            boss_permanent_fraction: 0.35,
            rest_decay_fraction: 0.40,
            base_damage_multiplier: 0.62,
            base_attack_speed_multiplier: 1.15,
            damage_per_weight: 0.045,
            damage_acceleration_per_weight_squared: 0.00035,
            speed_loss_per_weight: 0.010,
            stamina_per_weight: 0.018,
            recovery_per_weight: 0.014,
            impact_per_weight: 0.050,
            knockback_starts_at: 18.0,
            knockback_per_weight: 0.075,
            radial_stagger_starts_at: 35.0,
            radial_radius_per_weight: 0.060,
            camera_shake_starts_at: 20.0,
            tier_tempered_at: 15.0,
            tier_burdened_at: 35.0,
            tier_crushing_at: 65.0,
            tier_devastating_at: 105.0,
            tier_worldbreaker_at: 160.0,
            tier_cataclysmic_at: 240.0,
            standard_stamina_penalty_multiplier: 0.85,
            minimum_attack_speed_multiplier: 0.40,
            maximum_stamina_cost_multiplier: 4.0,
        }
    }
}

impl Config {
    pub fn load(path: impl AsRef<Path>) -> Self {
        let mut out = Self::default();
        let Ok(text) = fs::read_to_string(path) else {
            return out;
        };
        for raw in text.lines() {
            let line = raw.split('#').next().unwrap_or("").trim();
            let Some((key, value)) = line.split_once('=') else {
                continue;
            };
            let Ok(value) = value.trim().parse::<f32>() else {
                continue;
            };
            if !value.is_finite() {
                continue;
            }
            match key.trim() {
                "weight_per_kill" => out.weight_per_kill = value,
                "boss_weight" => out.boss_weight = value,
                "boss_permanent_fraction" => out.boss_permanent_fraction = value,
                "rest_decay_fraction" => out.rest_decay_fraction = value,
                "base_damage_multiplier" => out.base_damage_multiplier = value,
                "base_attack_speed_multiplier" => out.base_attack_speed_multiplier = value,
                "damage_per_weight" => out.damage_per_weight = value,
                "damage_acceleration_per_weight_squared" => {
                    out.damage_acceleration_per_weight_squared = value
                }
                "speed_loss_per_weight" => out.speed_loss_per_weight = value,
                "stamina_per_weight" => out.stamina_per_weight = value,
                "recovery_per_weight" => out.recovery_per_weight = value,
                "impact_per_weight" => out.impact_per_weight = value,
                "knockback_starts_at" => out.knockback_starts_at = value,
                "knockback_per_weight" => out.knockback_per_weight = value,
                "radial_stagger_starts_at" => out.radial_stagger_starts_at = value,
                "radial_radius_per_weight" => out.radial_radius_per_weight = value,
                "camera_shake_starts_at" => out.camera_shake_starts_at = value,
                "tier_tempered_at" => out.tier_tempered_at = value,
                "tier_burdened_at" => out.tier_burdened_at = value,
                "tier_crushing_at" => out.tier_crushing_at = value,
                "tier_devastating_at" => out.tier_devastating_at = value,
                "tier_worldbreaker_at" => out.tier_worldbreaker_at = value,
                "tier_cataclysmic_at" => out.tier_cataclysmic_at = value,
                "standard_stamina_penalty_multiplier" => {
                    out.standard_stamina_penalty_multiplier = value
                }
                "minimum_attack_speed_multiplier" => out.minimum_attack_speed_multiplier = value,
                "maximum_stamina_cost_multiplier" => out.maximum_stamina_cost_multiplier = value,
                _ => {}
            }
        }
        out.sanitize()
    }

    fn sanitize(mut self) -> Self {
        self.weight_per_kill = self.weight_per_kill.clamp(0.0, 100.0);
        self.boss_weight = self.boss_weight.clamp(0.0, 1000.0);
        self.boss_permanent_fraction = self.boss_permanent_fraction.clamp(0.0, 1.0);
        self.rest_decay_fraction = self.rest_decay_fraction.clamp(0.0, 1.0);
        self.base_damage_multiplier = self.base_damage_multiplier.clamp(0.05, 10.0);
        self.base_attack_speed_multiplier = self.base_attack_speed_multiplier.clamp(0.05, 3.0);
        self.damage_per_weight = self.damage_per_weight.max(0.0);
        self.damage_acceleration_per_weight_squared =
            self.damage_acceleration_per_weight_squared.max(0.0);
        self.speed_loss_per_weight = self.speed_loss_per_weight.max(0.0);
        self.stamina_per_weight = self.stamina_per_weight.max(0.0);
        self.recovery_per_weight = self.recovery_per_weight.max(0.0);
        self.impact_per_weight = self.impact_per_weight.max(0.0);
        self.knockback_per_weight = self.knockback_per_weight.max(0.0);
        self.radial_stagger_starts_at = self.radial_stagger_starts_at.max(0.0);
        self.radial_radius_per_weight = self.radial_radius_per_weight.max(0.0);
        self.camera_shake_starts_at = self.camera_shake_starts_at.max(0.0);
        self.tier_tempered_at = self.tier_tempered_at.max(0.0);
        self.tier_burdened_at = self.tier_burdened_at.max(self.tier_tempered_at);
        self.tier_crushing_at = self.tier_crushing_at.max(self.tier_burdened_at);
        self.tier_devastating_at = self.tier_devastating_at.max(self.tier_crushing_at);
        self.tier_worldbreaker_at = self.tier_worldbreaker_at.max(self.tier_devastating_at);
        self.tier_cataclysmic_at = self.tier_cataclysmic_at.max(self.tier_worldbreaker_at);
        self.standard_stamina_penalty_multiplier =
            self.standard_stamina_penalty_multiplier.clamp(0.0, 2.0);
        self.minimum_attack_speed_multiplier =
            self.minimum_attack_speed_multiplier.clamp(0.05, 3.0);
        self.maximum_stamina_cost_multiplier = self.maximum_stamina_cost_multiplier.max(1.0);
        self
    }
}

#[cfg(test)]
mod tests {
    use super::Config;

    #[test]
    fn tier_thresholds_are_sanitized_into_order() {
        let mut config = Config::default();
        config.tier_tempered_at = 50.0;
        config.tier_burdened_at = 10.0;
        config.tier_crushing_at = 5.0;
        let config = config.sanitize();
        assert_eq!(config.tier_burdened_at, 50.0);
        assert_eq!(config.tier_crushing_at, 50.0);
    }
}
