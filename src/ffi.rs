use crate::{
    persistence, AttackKind, AttackOutcome, Config, Engine, ForgeReceipt, KillKind, Modifiers,
    WeaponClass, WeaponKey,
};
use std::{
    ffi::{c_char, CStr},
    path::PathBuf,
    sync::{Mutex, OnceLock},
};

struct ProfileState {
    engine: Engine,
    profile: Option<String>,
}

static STATE: OnceLock<Mutex<ProfileState>> = OnceLock::new();
fn state() -> &'static Mutex<ProfileState> {
    STATE.get_or_init(|| {
        Mutex::new(ProfileState {
            engine: Engine::new(Config::load("heavier_by_the_kill.ini")),
            profile: None,
        })
    })
}
fn profile_filename(name: &str) -> String {
    let mut safe = String::new();
    for ch in name.trim().to_lowercase().chars() {
        if ch.is_ascii_alphanumeric() || ch == '-' || ch == '_' {
            safe.push(ch);
        } else {
            safe.push_str(&format!("_{:x}", ch as u32));
        }
    }
    format!("{}.save", safe)
}
fn save_path(profile: &str) -> PathBuf {
    PathBuf::from("profiles").join(profile_filename(profile))
}
fn save_current(s: &ProfileState) -> bool {
    s.profile
        .as_ref()
        .is_some_and(|profile| persistence::save(&s.engine, &save_path(profile)).is_ok())
}

#[no_mangle]
pub extern "C" fn HBK_initialize() -> bool {
    let _guard = state().lock().unwrap();
    true
}
#[no_mangle]
pub unsafe extern "C" fn HBK_select_profile(name: *const c_char) -> bool {
    if name.is_null() {
        return false;
    }
    let Ok(name) = CStr::from_ptr(name).to_str() else {
        return false;
    };
    let name = name.trim();
    if name.is_empty() {
        return false;
    }
    let canonical = name.to_lowercase();
    let mut s = state().lock().unwrap();
    if s.profile.as_deref() == Some(canonical.as_str()) {
        return true;
    }
    if s.profile.is_some() {
        let _ = save_current(&s);
    }
    let mut next = Engine::new(Config::load("heavier_by_the_kill.ini"));
    match persistence::load(&mut next, &save_path(&canonical)) {
        Ok(()) => {}
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(_) => return false,
    }
    s.engine = next;
    s.profile = Some(canonical);
    true
}
#[no_mangle]
pub extern "C" fn HBK_on_kill(key: WeaponKey, boss: bool) {
    let mut s = state().lock().unwrap();
    s.engine.kill(
        key,
        if boss {
            KillKind::Boss
        } else {
            KillKind::Normal
        },
    );
    let _ = save_current(&s);
}
#[no_mangle]
pub extern "C" fn HBK_on_kill_ex(key: WeaponKey, boss: bool, class: WeaponClass, legacy: u32) {
    let mut s = state().lock().unwrap();
    s.engine.kill_with_context(
        key,
        if boss {
            KillKind::Boss
        } else {
            KillKind::Normal
        },
        class,
        legacy,
    );
    let _ = save_current(&s);
}
#[no_mangle]
pub extern "C" fn HBK_on_rest() {
    let mut s = state().lock().unwrap();
    s.engine.rest();
    let _ = save_current(&s);
}
#[no_mangle]
pub extern "C" fn HBK_undo_rest() -> bool {
    let mut s = state().lock().unwrap();
    let restored = s.engine.undo_rest();
    if restored {
        let _ = save_current(&s);
    }
    restored
}
#[no_mangle]
pub extern "C" fn HBK_modifiers(key: WeaponKey) -> Modifiers {
    state().lock().unwrap().engine.modifiers(key)
}
#[no_mangle]
pub extern "C" fn HBK_modifiers_for(key: WeaponKey, class: WeaponClass) -> Modifiers {
    state().lock().unwrap().engine.modifiers_for(key, class)
}
#[no_mangle]
pub extern "C" fn HBK_progress(key: WeaponKey) -> crate::WeaponProgress {
    state().lock().unwrap().engine.progress(key)
}
#[no_mangle]
pub extern "C" fn HBK_config() -> Config {
    state().lock().unwrap().engine.config
}
#[no_mangle]
pub extern "C" fn HBK_attack(
    key: WeaponKey,
    class: WeaponClass,
    kind: AttackKind,
    base_stamina: f32,
    current_stamina: f32,
) -> AttackOutcome {
    state()
        .lock()
        .unwrap()
        .engine
        .attack(key, class, kind, base_stamina, current_stamina)
}
#[no_mangle]
pub extern "C" fn HBK_forge(key: WeaponKey, requested: f32, available_souls: u32) -> ForgeReceipt {
    let mut s = state().lock().unwrap();
    let receipt = s.engine.forge(key, requested, available_souls);
    let _ = save_current(&s);
    receipt
}
#[no_mangle]
pub extern "C" fn HBK_on_death(loss_fraction: f32) {
    let mut s = state().lock().unwrap();
    s.engine.die(loss_fraction);
    let _ = save_current(&s);
}
#[no_mangle]
pub extern "C" fn HBK_on_bloodstain_recovered() {
    let mut s = state().lock().unwrap();
    s.engine.recover_bloodstain();
    let _ = save_current(&s);
}
#[no_mangle]
pub extern "C" fn HBK_save() -> bool {
    save_current(&state().lock().unwrap())
}

#[cfg(test)]
mod tests {
    use super::profile_filename;

    #[test]
    fn profile_names_are_safe_and_case_insensitive() {
        assert_eq!(profile_filename("Alice"), "alice.save");
        assert_eq!(
            profile_filename("../Other Save"),
            "_2e_2e_2fother_20save.save"
        );
    }
}
