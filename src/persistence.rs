use crate::{Engine, WeaponKey, WeaponProgress};
use std::{fs, io, path::Path};

pub fn save(engine: &Engine, path: &Path) -> io::Result<()> {
    if let Some(parent) = path.parent() {
        if !parent.as_os_str().is_empty() {
            fs::create_dir_all(parent)?;
        }
    }
    let mut rows: Vec<_> = engine.records().collect();
    rows.sort_by_key(|(k, _)| (k.inventory_id, k.reinforce_level, k.infusion));
    let mut text = String::from("HBK3\n");
    for (k, p) in rows {
        text.push_str(&format!(
            "W,{},{},{},{:.6},{:.6},{},{},{}\n",
            k.inventory_id,
            k.reinforce_level,
            k.infusion,
            p.temporary,
            p.permanent,
            p.kills,
            p.bosses,
            p.legacies
        ));
    }
    let mut bloodstain: Vec<_> = engine.bloodstain_records().collect();
    bloodstain.sort_by_key(|(k, _)| (k.inventory_id, k.reinforce_level, k.infusion));
    for (k, weight) in bloodstain {
        text.push_str(&format!(
            "B,{},{},{},{:.6}\n",
            k.inventory_id, k.reinforce_level, k.infusion, weight
        ));
    }
    let tmp = path.with_extension("tmp");
    fs::write(&tmp, text)?;
    fs::rename(tmp, path)
}

pub fn load(engine: &mut Engine, path: &Path) -> io::Result<()> {
    let text = fs::read_to_string(path)?;
    let mut lines = text.lines();
    let version = lines.next();
    if version != Some("HBK1") && version != Some("HBK2") && version != Some("HBK3") {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "unsupported HBK save",
        ));
    }
    for line in lines {
        let v: Vec<_> = line.split(',').collect();
        if version == Some("HBK3") && v.first() == Some(&"B") {
            if v.len() >= 5 {
                if let (Ok(inventory_id), Ok(reinforce_level), Ok(infusion), Ok(weight)) =
                    (v[1].parse(), v[2].parse(), v[3].parse(), v[4].parse())
                {
                    engine.merge_bloodstain(
                        WeaponKey {
                            inventory_id,
                            reinforce_level,
                            infusion,
                        },
                        weight,
                    );
                }
            }
            continue;
        }
        let start = if version == Some("HBK3") && v.first() == Some(&"W") {
            1
        } else {
            0
        };
        if v.len() < start + 7 {
            continue;
        }
        let parsed = (
            v[start].parse(),
            v[start + 1].parse(),
            v[start + 2].parse(),
            v[start + 3].parse(),
            v[start + 4].parse(),
            v[start + 5].parse(),
            v[start + 6].parse(),
        );
        if let (
            Ok(inventory_id),
            Ok(reinforce_level),
            Ok(infusion),
            Ok(temporary),
            Ok(permanent),
            Ok(kills),
            Ok(bosses),
        ) = parsed
        {
            // Merge legacy rows that represented reinforcement variants of
            // the same weapon. New saves emit one normalized family row.
            engine.merge_progress(
                WeaponKey {
                    inventory_id,
                    reinforce_level,
                    infusion,
                },
                WeaponProgress {
                    temporary,
                    permanent,
                    kills,
                    bosses,
                    legacies: v.get(start + 7).and_then(|x| x.parse().ok()).unwrap_or(0),
                },
            );
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{Config, KillKind};

    #[test]
    fn bloodstain_survives_save_and_reload() {
        let path = std::env::temp_dir().join(format!(
            "heavier_by_the_kill_{}_bloodstain.save",
            std::process::id()
        ));
        let key = WeaponKey {
            inventory_id: 310004,
            ..Default::default()
        };
        let mut original = Engine::new(Config::default());
        original.kill(key, KillKind::Normal);
        let full = original.progress(key).temporary;
        original.die(0.5);
        save(&original, &path).unwrap();

        let mut loaded = Engine::new(Config::default());
        load(&mut loaded, &path).unwrap();
        assert_eq!(loaded.progress(key).temporary, full * 0.5);
        loaded.recover_bloodstain();
        assert_eq!(loaded.progress(key).temporary, full);
        let _ = fs::remove_file(path);
    }
}
