//! What the motion interpreter asks of the game object behind a body (its
//! "weenie"): how fast it runs, how high it jumps, whether it may jump and
//! what a jump costs. The local player answers from its skills, burden and
//! stamina; a remote object answers nothing (its speeds come from the
//! server).

use ac_core::items::burden::load_modifier;

/// The game object behind a body, as the motion interpreter sees it.
pub trait WeenieObject {
    /// Vertical take-off speed for a jump of `extent` (0..1), metres per
    /// second; `None` when the object cannot say.
    fn inq_jump_velocity(&self, extent: f32) -> Option<f32>;
    /// Run rate (speed factor of the run cycle); `None` when the object
    /// cannot say.
    fn inq_run_rate(&self) -> Option<f32>;
    /// Whether a jump of `extent` is allowed at the current load.
    fn can_jump(&self, extent: f32) -> bool;
    /// A creature (only creatures normalise motions and touch the ground).
    fn is_creature(&self) -> bool {
        true
    }
    /// The player this client controls.
    fn is_the_player(&self) -> bool {
        false
    }
    /// Stamina cost of a jump of `extent`; `None` when it cannot be paid.
    fn jump_stamina_cost(&self, _extent: f32) -> Option<i32> {
        Some(0)
    }
}

/// A shared weenie answers as the one it shares.
impl<T: WeenieObject + ?Sized> WeenieObject for std::rc::Rc<T> {
    fn inq_jump_velocity(&self, extent: f32) -> Option<f32> {
        (**self).inq_jump_velocity(extent)
    }
    fn inq_run_rate(&self) -> Option<f32> {
        (**self).inq_run_rate()
    }
    fn can_jump(&self, extent: f32) -> bool {
        (**self).can_jump(extent)
    }
    fn is_creature(&self) -> bool {
        (**self).is_creature()
    }
    fn is_the_player(&self) -> bool {
        (**self).is_the_player()
    }
    fn jump_stamina_cost(&self, extent: f32) -> Option<i32> {
        (**self).jump_stamina_cost(extent)
    }
}

/// Run rate for a burden (load ratio, 1 = full load) and run skill, with
/// an optional scaling divisor. Skill 800 exactly gives the 4.5 cap.
#[must_use]
pub fn get_run_rate(burden: f32, run_skill: i32, scaling: f32) -> f32 {
    if run_skill == 800 {
        return 18.0 / 4.0;
    }
    let load_mod = load_modifier(burden);
    ((load_mod * (run_skill as f32 / (run_skill + 200) as f32 * 11.0) + 4.0) / scaling) / 4.0
}

/// Jump height, metres, for a burden, jump skill and power (0..1, clamped);
/// never below 0.35 m.
#[must_use]
pub fn get_jump_height(burden: f32, jump_skill: i32, power: f32, scaling: f32) -> f32 {
    let power = power.clamp(0.0, 1.0);
    let load_mod = load_modifier(burden);
    let result =
        load_mod * (jump_skill as f32 / (jump_skill as f32 + 1300.0) * 22.2 + 0.05) * power
            / scaling;
    if result < 0.35 { 0.35 } else { result }
}

/// Stamina a jump of `power` costs: `ceil((burden + 0.5)·power·8 + 2)`, or
/// `(power + 1)·100` for a player killer who attacked in the last 20 s.
#[must_use]
pub fn jump_stamina_cost(power: f32, burden: f32, pk: bool) -> i32 {
    if pk {
        return ((power + 1.0) * 100.0) as i32;
    }
    ((burden + 0.5) * power * 8.0 + 2.0).ceil() as i32
}

/// The jump power `stamina` pays for (the inverse of
/// [`jump_stamina_cost`]).
#[must_use]
pub fn get_jump_power(stamina: u32, burden: f32, pk: bool) -> f32 {
    if pk {
        return stamina as f32 / 100.0 - 1.0;
    }
    (stamina as f32 - 2.0) / (burden * 8.0 + 4.0)
}

/// The local player's weenie: run and jump from skills, burden and stamina.
#[derive(Debug, Clone, PartialEq)]
pub struct PlayerWeenie {
    run_skill: i32,
    jump_skill: i32,
    burden: f32,
    player_killer_status: Option<i32>,
    last_pk_attack_timestamp: Option<f32>,
    current_stamina: Option<u32>,
    now_seconds: f32,
}

impl PlayerWeenie {
    /// Loads at or above twice a full load cannot jump.
    pub const CAN_JUMP_LOAD_THRESHOLD: f32 = 2.0;

    /// A player with these skills and burden (load ratio).
    #[must_use]
    pub fn new(run_skill: i32, jump_skill: i32, burden: f32) -> Self {
        Self {
            run_skill,
            jump_skill,
            burden,
            player_killer_status: None,
            last_pk_attack_timestamp: None,
            current_stamina: None,
            now_seconds: 0.0,
        }
    }

    /// Replace the run and jump skills.
    pub fn set_skills(&mut self, run_skill: i32, jump_skill: i32) {
        self.run_skill = run_skill;
        self.jump_skill = jump_skill;
    }

    /// Replace the burden (load ratio).
    pub fn set_burden(&mut self, burden: f32) {
        self.burden = burden;
    }

    /// Current stamina; `None` when unknown (never gates). At 0 the run and
    /// jump skills count as 0.
    pub fn set_stamina(&mut self, current_stamina: Option<u32>) {
        self.current_stamina = current_stamina;
    }

    /// Player-killer status and the time of the last PK attack (seconds on
    /// the same clock as [`PlayerWeenie::set_clock_seconds`]).
    pub fn set_player_killer_status(
        &mut self,
        player_killer_status: Option<i32>,
        last_pk_attack_timestamp: Option<f32>,
    ) {
        self.player_killer_status = player_killer_status;
        self.last_pk_attack_timestamp = last_pk_attack_timestamp;
    }

    /// The current time, seconds, for the 20 s PK jump-cost window.
    pub fn set_clock_seconds(&mut self, now_seconds: f32) {
        self.now_seconds = now_seconds;
    }

    /// The load modifier for a burden.
    #[must_use]
    pub fn get_burden_mod(burden: f32) -> f32 {
        load_modifier(burden)
    }
}

impl Default for PlayerWeenie {
    fn default() -> Self {
        Self::new(0, 0, 0.0)
    }
}

impl WeenieObject for PlayerWeenie {
    fn inq_run_rate(&self) -> Option<f32> {
        let effective_skill = if self.current_stamina == Some(0) {
            0
        } else {
            self.run_skill
        };
        Some(get_run_rate(self.burden, effective_skill, 1.0))
    }

    fn inq_jump_velocity(&self, extent: f32) -> Option<f32> {
        let effective_skill = if self.current_stamina == Some(0) {
            0
        } else {
            self.jump_skill
        };
        let height = get_jump_height(self.burden, effective_skill, extent, 1.0);
        Some((height * 19.6).sqrt())
    }

    fn can_jump(&self, _extent: f32) -> bool {
        self.burden < Self::CAN_JUMP_LOAD_THRESHOLD
    }

    fn is_the_player(&self) -> bool {
        true
    }

    fn jump_stamina_cost(&self, extent: f32) -> Option<i32> {
        let mut pk = false;
        let status = self.player_killer_status.unwrap_or(8);
        if (status == 4 || status == 0x40)
            && let Some(ts) = self.last_pk_attack_timestamp
        {
            pk = ts + 20.0 >= self.now_seconds;
        }
        Some(jump_stamina_cost(extent, self.burden, pk))
    }
}

/// A remote object's weenie: it knows no rates (the server sends them).
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct RemoteWeenie;

impl WeenieObject for RemoteWeenie {
    fn inq_run_rate(&self) -> Option<f32> {
        None
    }

    fn inq_jump_velocity(&self, _extent: f32) -> Option<f32> {
        None
    }

    /// Remotes never start jumps here.
    fn can_jump(&self, _extent: f32) -> bool {
        true
    }
}

#[cfg(test)]
mod tests {
    //! Golden cases from OpenAC `PlayerWeenieTests` and
    //! `MovementSystemTests` (`tests/AcDream.Core.Tests/Physics/`). The PK
    //! window reads the weenie's clock, set by the test, where OpenAC reads
    //! the process tick count.
    use super::*;

    #[track_caller]
    fn eq_prec(expected: f32, actual: f32, precision: i32) {
        let scale = 10f64.powi(precision);
        let round = |x: f32| (f64::from(x) * scale).round_ties_even() / scale;
        assert_eq!(round(expected), round(actual), "{expected} vs {actual}");
    }

    fn pw(run: i32, jump: i32) -> PlayerWeenie {
        PlayerWeenie::new(run, jump, 0.0)
    }

    #[test]
    fn run_rate_by_skill() {
        eq_prec(2.375, pw(200, 100).inq_run_rate().unwrap(), 3);
        eq_prec(4.5, pw(800, 100).inq_run_rate().unwrap(), 3);
        eq_prec(1.0, pw(0, 100).inq_run_rate().unwrap(), 3);
    }

    #[test]
    fn jump_velocity_by_extent_and_skill() {
        eq_prec(5.663, pw(100, 100).inq_jump_velocity(1.0).unwrap(), 1);
        let expected_height = 1.0 * (100.0 / 1400.0 * 22.2 + 0.05) * 0.5;
        let expected_vz = (expected_height * 19.6f32).sqrt();
        eq_prec(expected_vz, pw(100, 100).inq_jump_velocity(0.5).unwrap(), 2);
        eq_prec(
            (0.35f32 * 19.6).sqrt(),
            pw(0, 0).inq_jump_velocity(1.0).unwrap(),
            2,
        );
    }

    #[test]
    fn burden_modifier() {
        assert_eq!(PlayerWeenie::get_burden_mod(0.0), 1.0);
        assert_eq!(PlayerWeenie::get_burden_mod(0.5), 1.0);
        assert_eq!(PlayerWeenie::get_burden_mod(0.99), 1.0);
        assert_eq!(PlayerWeenie::get_burden_mod(2.0), 0.0);
        assert_eq!(PlayerWeenie::get_burden_mod(3.0), 0.0);
        eq_prec(0.5, PlayerWeenie::get_burden_mod(1.5), 3);
        eq_prec(0.75, PlayerWeenie::get_burden_mod(1.25), 3);
    }

    #[test]
    fn can_jump_below_twice_a_full_load() {
        assert!(pw(200, 100).can_jump(1.0));
        for burden in [0.0, 1.0, 1.99] {
            assert!(PlayerWeenie::new(200, 100, burden).can_jump(1.0));
        }
        for burden in [2.0, 2.5, 3.0] {
            assert!(!PlayerWeenie::new(200, 100, burden).can_jump(1.0));
        }
        let mut w = pw(200, 100);
        w.set_burden(2.5);
        assert!(!w.can_jump(1.0));
        w.set_burden(0.5);
        assert!(w.can_jump(1.0));
    }

    #[test]
    fn jump_stamina_cost_non_pk() {
        assert_eq!(pw(200, 100).jump_stamina_cost(1.0), Some(6));
        assert_eq!(
            PlayerWeenie::new(200, 100, 1.0).jump_stamina_cost(1.0),
            Some(14)
        );
        let mut w = pw(200, 100);
        w.set_clock_seconds(1000.0);
        w.set_player_killer_status(Some(8), Some(1000.0));
        assert_eq!(w.jump_stamina_cost(1.0), Some(6));
        w.set_player_killer_status(Some(4), None);
        assert_eq!(w.jump_stamina_cost(1.0), Some(6));
    }

    #[test]
    fn jump_stamina_cost_pk_window() {
        let mut w = pw(200, 100);
        w.set_clock_seconds(1000.0);
        w.set_player_killer_status(Some(4), Some(1000.0));
        assert_eq!(w.jump_stamina_cost(1.0), Some(200));
        w.set_player_killer_status(Some(0x40), Some(1000.0));
        assert_eq!(w.jump_stamina_cost(1.0), Some(200));
        w.set_player_killer_status(Some(4), Some(1000.0 - 30.0));
        assert_eq!(w.jump_stamina_cost(1.0), Some(6));
        w.set_player_killer_status(Some(4), Some(1000.0));
        assert_eq!(w.jump_stamina_cost(1.0), Some(200));
        w.set_player_killer_status(None, None);
        assert_eq!(w.jump_stamina_cost(1.0), Some(6));
    }

    #[test]
    fn zero_stamina_zeroes_the_skills() {
        let mut w = pw(200, 100);
        w.set_stamina(Some(0));
        eq_prec(1.0, w.inq_run_rate().unwrap(), 3);
        eq_prec(
            (0.35f32 * 19.6).sqrt(),
            w.inq_jump_velocity(1.0).unwrap(),
            2,
        );
        w.set_stamina(Some(50));
        eq_prec(2.375, w.inq_run_rate().unwrap(), 3);
        w.set_stamina(None);
        eq_prec(2.375, w.inq_run_rate().unwrap(), 3);
        assert!(w.is_the_player());
    }

    #[test]
    fn run_rate_formula() {
        eq_prec(4.5, get_run_rate(0.0, 800, 1.0), 5);
        eq_prec(3.1994, get_run_rate(0.0, 799, 1.0), 3);
        eq_prec(3.2005, get_run_rate(0.0, 801, 1.0), 3);
        assert!((get_run_rate(0.0, 10200, 1.0) - 3.6971).abs() < 1e-3);
        assert!((get_run_rate(0.0, 14463, 1.0) - 3.7125).abs() < 1e-3);
        assert!((get_run_rate(0.0, 9999, 1.0) - 3.6961).abs() < 1e-3);
        eq_prec(2.375, get_run_rate(0.0, 200, 1.0), 3);
        eq_prec(1.0, get_run_rate(0.0, 0, 1.0), 3);
        for (burden, load_mod) in [
            (0.0, 1.0),
            (0.99, 1.0),
            (1.0, 1.0),
            (1.5, 0.5),
            (2.0, 0.0),
            (3.0, 0.0),
        ] {
            let expected = (load_mod * 5.5 + 4.0) / 4.0;
            eq_prec(expected, get_run_rate(burden, 200, 1.0), 3);
        }
    }

    #[test]
    fn jump_height_formula() {
        let unscaled = 100.0f32 / 1400.0 * 22.2 + 0.05;
        eq_prec(unscaled, get_jump_height(0.0, 100, 1.0, 1.0), 3);
        for extent in [0.0f32, 0.5, 1.0] {
            let expected = (unscaled * extent).max(0.35);
            eq_prec(expected, get_jump_height(0.0, 100, extent, 1.0), 3);
        }
        eq_prec(
            get_jump_height(0.0, 100, 1.0, 1.0),
            get_jump_height(0.0, 100, 5.0, 1.0),
            5,
        );
        eq_prec(0.35, get_jump_height(0.0, 100, -3.0, 1.0), 5);
        eq_prec(0.35, get_jump_height(0.0, 0, 1.0, 1.0), 5);
        for burden in [0.0, 1.0, 1.5, 2.0, 3.0] {
            assert!(get_jump_height(burden, 100, 1.0, 1.0) >= 0.35);
        }
    }

    #[test]
    fn jump_stamina_cost_formula() {
        for (burden, power, expected) in [
            (0.0, 0.0, 2),
            (0.0, 1.0, 6),
            (1.0, 1.0, 14),
            (2.0, 1.0, 22),
            (0.3, 0.7, 7),
        ] {
            assert_eq!(jump_stamina_cost(power, burden, false), expected);
        }
        assert_eq!(jump_stamina_cost(0.5, 1.0, true), 150);
        assert_eq!(jump_stamina_cost(0.0, 1.0, true), 100);
        eq_prec(1.5, get_jump_power(20, 1.0, false), 3);
    }

    #[test]
    fn remote_weenie_knows_no_rates() {
        let w = RemoteWeenie;
        assert_eq!(w.inq_run_rate(), None);
        assert_eq!(w.inq_jump_velocity(1.0), None);
        assert!(w.can_jump(1.0));
        assert!(w.is_creature());
        assert!(!w.is_the_player());
    }
}
