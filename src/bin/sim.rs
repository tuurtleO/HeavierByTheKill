use heavier_by_the_kill::{Config, Engine, KillKind, WeaponKey};
fn main() {
    let config = Config::load("heavier_by_the_kill.ini");
    let mut e = Engine::new(config);
    let sword = WeaponKey {
        inventory_id: 100000,
        reinforce_level: 0,
        infusion: 0,
    };
    println!("kills weight damage speed stamina recovery impact knockback");
    for kill in 0..=50 {
        if kill > 0 {
            e.kill(sword, KillKind::Normal);
        }
        if kill % 5 == 0 {
            let m = e.modifiers(sword);
            println!(
                "{kill:>5} {:>6.1} {:>6.2} {:>5.2} {:>7.2} {:>8.2} {:>6.2} {:>9.2}",
                m.weight,
                m.damage,
                m.attack_speed,
                m.stamina_cost,
                m.recovery,
                m.impact,
                m.knockback
            );
        }
    }
}
