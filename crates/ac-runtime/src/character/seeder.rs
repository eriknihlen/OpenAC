//! The character options a session document declares, put onto the
//! character once the session is far enough into the world.
//!
//! The options belong to the server, so this cannot simply set them: it
//! waits until the server has said what the character's options are (the
//! description's option words) and the client has said its login is
//! complete, compares, and changes only the ones that differ. Options the
//! server stores as it receives them need nothing more; the rest are
//! followed by one block save at the end.

use super::{CharacterOptions, option};

/// One change the seeder asks for.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SeedStep {
    /// Change one option (as the options panel does: sent at once when the
    /// option saves itself, else written locally).
    Set {
        /// The option id.
        option: u32,
        /// The value declared.
        value: bool,
    },
    /// Send the options block (after every change that waits for one).
    Save,
}

/// The declared options and whether login complete went out.
#[derive(Debug, Clone, Default)]
pub struct OptionsSeeder {
    /// By ascending option id.
    declared: Vec<(u32, bool)>,
    login_complete_sent: bool,
}

impl OptionsSeeder {
    /// Declared options by id; an id the table does not have is ignored.
    #[must_use]
    pub fn new(declared: &[(u32, bool)]) -> Self {
        let mut declared: Vec<(u32, bool)> = declared.to_vec();
        declared.sort_by_key(|&(id, _)| id);
        Self {
            declared,
            login_complete_sent: false,
        }
    }

    /// Something is declared.
    #[must_use]
    pub fn has_declared_options(&self) -> bool {
        !self.declared.is_empty()
    }

    /// The client told the server its login is complete: the steps to
    /// take now (none until the server's options are in).
    pub fn note_login_complete_sent(&mut self, options: &CharacterOptions) -> Vec<SeedStep> {
        self.login_complete_sent = true;
        self.diff(options)
    }

    /// The server said what the character's options are: the steps to take
    /// now (none until login complete went out).
    #[must_use]
    pub fn note_options_seeded(&self, options: &CharacterOptions) -> Vec<SeedStep> {
        self.diff(options)
    }

    /// A new connection: login complete has not gone out on it.
    pub fn reset_session(&mut self) {
        self.login_complete_sent = false;
    }

    fn diff(&self, options: &CharacterOptions) -> Vec<SeedStep> {
        if !self.login_complete_sent || self.declared.is_empty() || !options.has_server_seed() {
            return Vec::new();
        }
        let mut steps = Vec::new();
        let mut needs_save = false;
        for &(id, desired) in &self.declared {
            let Some(entry) = option(id) else {
                continue;
            };
            if options.get(id) == desired {
                continue;
            }
            steps.push(SeedStep::Set {
                option: id,
                value: desired,
            });
            needs_save |= !entry.auto_save;
        }
        if needs_save {
            steps.push(SeedStep::Save);
        }
        steps
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Runtime.Tests/Gameplay/RuntimeCharacterOptionsSeederTests.cs`
    //! (the steps returned stand for the fake command sink's calls; each
    //! set is applied to the options as the sink applies it).

    use super::super::option_id::{AUTO_REPEAT_ATTACK, IGNORE_TRADE_REQUESTS};
    use super::*;

    /// The options as the server stated them: every bit off.
    fn seeded(options1: u32) -> CharacterOptions {
        let mut options = CharacterOptions::new();
        options.replace(options1, 0, true);
        options
    }

    /// The steps, applied.
    fn apply(options: &mut CharacterOptions, steps: &[SeedStep]) {
        for step in steps {
            if let SeedStep::Set { option, value } = *step {
                options.set_bit(option, value);
            }
        }
    }

    #[test]
    fn declared_equal_to_actual_sends_nothing() {
        let options = seeded(0);
        let mut seeder = OptionsSeeder::new(&[(AUTO_REPEAT_ATTACK, false)]);
        assert!(seeder.note_login_complete_sent(&options).is_empty());
        assert!(seeder.note_options_seeded(&options).is_empty());
    }

    #[test]
    fn a_self_saving_difference_is_one_set() {
        let options = seeded(0);
        let mut seeder = OptionsSeeder::new(&[(AUTO_REPEAT_ATTACK, true)]);
        assert_eq!(
            seeder.note_login_complete_sent(&options),
            [SeedStep::Set {
                option: AUTO_REPEAT_ATTACK,
                value: true
            }]
        );
    }

    #[test]
    fn a_batched_difference_is_a_set_then_one_save() {
        let options = seeded(0);
        let mut seeder = OptionsSeeder::new(&[(IGNORE_TRADE_REQUESTS, true)]);
        assert_eq!(
            seeder.note_login_complete_sent(&options),
            [
                SeedStep::Set {
                    option: IGNORE_TRADE_REQUESTS,
                    value: true
                },
                SeedStep::Save
            ]
        );
    }

    #[test]
    fn every_set_comes_in_id_order_before_the_one_save() {
        let options = seeded(0);
        let mut seeder =
            OptionsSeeder::new(&[(IGNORE_TRADE_REQUESTS, true), (AUTO_REPEAT_ATTACK, true)]);
        assert_eq!(
            seeder.note_login_complete_sent(&options),
            [
                SeedStep::Set {
                    option: AUTO_REPEAT_ATTACK,
                    value: true
                },
                SeedStep::Set {
                    option: IGNORE_TRADE_REQUESTS,
                    value: true
                },
                SeedStep::Save
            ]
        );
    }

    #[test]
    fn neither_signal_alone_sends_anything() {
        let mut options = CharacterOptions::new();
        let mut seeder = OptionsSeeder::new(&[(AUTO_REPEAT_ATTACK, true)]);
        assert!(seeder.note_login_complete_sent(&options).is_empty());
        options.replace(0, 0, true);
        assert_eq!(seeder.note_options_seeded(&options).len(), 1);
    }

    #[test]
    fn options_seeded_before_login_complete_wait_for_it() {
        let options = seeded(0);
        let mut seeder = OptionsSeeder::new(&[(AUTO_REPEAT_ATTACK, true)]);
        assert!(seeder.note_options_seeded(&options).is_empty());
        assert_eq!(seeder.note_login_complete_sent(&options).len(), 1);
    }

    #[test]
    fn nothing_declared_never_sends() {
        let options = seeded(u32::MAX);
        let mut seeder = OptionsSeeder::new(&[]);
        assert!(!seeder.has_declared_options());
        assert!(seeder.note_login_complete_sent(&options).is_empty());
        assert!(seeder.note_options_seeded(&options).is_empty());
    }

    #[test]
    fn a_reconnect_against_a_server_that_now_agrees_sends_nothing() {
        let declared = [(AUTO_REPEAT_ATTACK, true)];
        let mut options = seeded(0);
        let mut seeder = OptionsSeeder::new(&declared);
        let steps = seeder.note_login_complete_sent(&options);
        assert_eq!(steps.len(), 1);
        apply(&mut options, &steps);
        options.reset_session();
        let auto_save_mask = option(AUTO_REPEAT_ATTACK).unwrap().mask;
        options.replace(auto_save_mask, 0, true);
        seeder.reset_session();
        assert!(seeder.note_login_complete_sent(&options).is_empty());
    }
}
