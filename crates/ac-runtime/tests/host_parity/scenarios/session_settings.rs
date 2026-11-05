//! What a plugin reads out of the settings its run was started with
//! (OpenAC `Scenarios/SessionSettingsParityTests.cs`): both arms are
//! configured with the same map, and a plugin reads its own settings and
//! nobody else's.

use std::collections::BTreeMap;

use ac_plugin::abi::calls;

use crate::host_parity::arm::{Arm, Setup, run_with};
use crate::host_parity::transcript::Transcript;

const SETTINGS: &str = "openac.parity.settings";
const OTHER: &str = "openac.parity.other";
const UNCONFIGURED: &str = "openac.parity.unconfigured";

fn record(transcript: &mut Transcript, arm: &mut Arm, plugin: &str, key: &str) {
    let settings = arm.call_as::<calls::HostSettings>(plugin, &());
    transcript.record(&format!("{plugin}.count"), settings.len());
    let value = arm.call_as::<calls::HostSetting>(plugin, &key.to_owned());
    transcript.record(
        &format!("{plugin}.{key}"),
        value.unwrap_or_else(|| "<absent>".to_owned()),
    );
}

fn settings(arm: &mut Arm, plugin: &str) -> Vec<(String, String)> {
    let mut all = arm.call_as::<calls::HostSettings>(plugin, &());
    all.sort();
    all
}

// OpenAC `SessionSettingsParityTests.APluginReadsTheSameStartupSettingsOnBothClients`.
#[test]
fn a_plugin_reads_the_same_startup_settings_on_both_clients() {
    let setup = Setup {
        more_plugins: vec![SETTINGS, OTHER, UNCONFIGURED],
        settings: BTreeMap::from([
            (
                SETTINGS.to_owned(),
                BTreeMap::from([
                    ("startMacro".to_owned(), "true".to_owned()),
                    ("profile".to_owned(), "parity".to_owned()),
                ]),
            ),
            (
                OTHER.to_owned(),
                BTreeMap::from([("startMacro".to_owned(), "false".to_owned())]),
            ),
        ]),
        ..Setup::default()
    };
    run_with(&setup, &[], true, |arm, transcript| {
        transcript.step("the settings this plugin was started with");
        record(transcript, arm, SETTINGS, "startMacro");
        record(transcript, arm, SETTINGS, "profile");
        assert_eq!(
            settings(arm, SETTINGS),
            [
                ("profile".to_owned(), "parity".to_owned()),
                ("startMacro".to_owned(), "true".to_owned()),
            ]
        );

        transcript.step("another plugin's settings are its own");
        record(transcript, arm, OTHER, "startMacro");
        record(transcript, arm, OTHER, "profile");
        assert_eq!(
            settings(arm, OTHER),
            [("startMacro".to_owned(), "false".to_owned())]
        );

        transcript.step("a plugin nobody configured");
        record(transcript, arm, UNCONFIGURED, "startMacro");
        assert!(settings(arm, UNCONFIGURED).is_empty());
    });
}
