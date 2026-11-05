//! The options window's small rules. The binding against the retail
//! layout is tested with the dats in `tests/options.rs`.

use super::*;

#[test]
fn a_number_slider_snaps_to_its_step_inside_its_range() {
    // OpenAC `ConfigOptionsPageController.SnapExplicitNumber`
    // (ConfigOptionsPageControllerTests.TheVoicesSlider_WritesWholeNumbersInsideSixteenToSixtyFour).
    assert_eq!(snap(16.0 + 0.49 * 48.0, 16.0, 64.0, 1.0, true), 40.0);
    assert_eq!(snap(100.0, 16.0, 64.0, 1.0, true), 64.0);
    assert_eq!(snap(-3.0, 16.0, 64.0, 1.0, true), 16.0);
    assert_eq!(snap(3.6, 0.0, 8.0, 1.0, true), 4.0);
}

#[test]
fn a_chat_filter_splits_into_the_bitfields_halves_and_back() {
    let mask = 0x0000_0001_8391_2021;
    let (low, high) = split_mask(mask);
    assert_eq!((low, high), (0x8391_2021, 1));
    assert_eq!(combine_mask(low, high), mask);
}

#[test]
fn urgent_assistance_and_report_abuse_say_different_things() {
    // OpenAC `OptionsPanelControllerTests.UrgentAssistanceAndReportAbuse_UseDifferentText`.
    let urgent = urgent_assistance_unavailable();
    let abuse = report_abuse_unavailable();
    assert_ne!(urgent, abuse);
    assert!(urgent.contains("urgent assistance request"));
    assert!(abuse.contains("abuse report"));
    assert!(urgent.ends_with(SUPPORT_URL));
}

#[test]
fn the_pages_are_known_by_their_element_ids() {
    assert_eq!(OptionsPage::of(0x1000_0212), Some(OptionsPage::Gameplay));
    assert_eq!(OptionsPage::of(0x1000_0211), Some(OptionsPage::Character));
    assert_eq!(OptionsPage::of(0x1000_050C), Some(OptionsPage::Chat));
    assert_eq!(
        OptionsPage::of(0x1000_0213),
        Some(OptionsPage::Configuration)
    );
    assert_eq!(OptionsPage::of(0), None);
}
