//! Portal space against the retail dats: the tunnel's Setup and animation
//! resolve through the client enum maps to the ids OpenAC pins, the
//! animation is long enough for the exit window, and the tunnel plays (its
//! one sound hook fires, its parts stay finite). Ignored by default;
//! `scripts/check.*` runs them when the dats are present.

use std::path::PathBuf;
use std::sync::Arc;

use ac_core::sky::DotNetRandom;
use ac_dat::objects::{Animation, HookData};
use ac_render::animation::source::{AnimationSource, ContentAnimationSource};
use ac_render::content::Content;
use ac_render::portal_space::sequencer::TUNNEL_END_FRAME;
use ac_render::portal_space::tunnel::{
    ANIMATION_CLIENT_ENUM, CLIENT_ENUM_CATEGORY, PortalTunnel, SETUP_CLIENT_ENUM,
    resolve_client_enum,
};

fn content() -> Arc<Content> {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    Arc::new(Content::open(&dir).expect("open the retail dats"))
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_tunnel_setup_and_animation_resolve_to_the_retail_ids() {
    // OpenAC PortalTunnelAssetTests.InstalledDat_ResolvesRetailPortalSetupAndAnimation.
    let content = content();
    let dats = content.dats.uncached();
    let setup_id = resolve_client_enum(dats, SETUP_CLIENT_ENUM, CLIENT_ENUM_CATEGORY);
    let animation_id = resolve_client_enum(dats, ANIMATION_CLIENT_ENUM, CLIENT_ENUM_CATEGORY);
    assert_eq!(setup_id, 0x0200_0306);
    assert_eq!(animation_id, 0x0300_05AC);
    let source = ContentAnimationSource::new(Arc::clone(&content));
    let setup = source.setup(setup_id).expect("the tunnel's Setup");
    let animation = dats
        .get::<Animation>(animation_id)
        .unwrap()
        .expect("the tunnel's animation");
    assert!(!setup.parts.is_empty());
    assert!(setup.parts.iter().all(|part| part >> 24 == 0x01));
    assert_eq!(setup.default_animation, 0);
    assert_eq!(setup.default_script, 0);
    assert_eq!(setup.default_script_table, 0);
    assert!(animation.part_frames.len() >= TUNNEL_END_FRAME as usize);
    let hooks: Vec<_> = animation
        .part_frames
        .iter()
        .flat_map(|f| &f.hooks)
        .collect();
    assert_eq!(hooks.len(), 1);
    assert!(matches!(hooks[0].data, HookData::SoundTweaked { .. }));
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_tunnel_plays_its_animation_and_its_sound() {
    let content = content();
    let dats = content.dats.uncached();
    let setup_id = resolve_client_enum(dats, SETUP_CLIENT_ENUM, CLIENT_ENUM_CATEGORY);
    let animation_id = resolve_client_enum(dats, ANIMATION_CLIENT_ENUM, CLIENT_ENUM_CATEGORY);
    let source = ContentAnimationSource::new(Arc::clone(&content));
    let setup = source.setup(setup_id).unwrap();
    let mut tunnel = PortalTunnel::new(
        setup_id,
        setup,
        animation_id,
        source.animations(),
        DotNetRandom::new(1),
    );
    tunnel.enter();
    assert!(tunnel.is_visible());
    // Three and a half seconds at 60 frames per second: 140 frames of the
    // animation from frame 1, so it reaches its end and loops.
    let mut sounds = 0;
    let mut turns = 0;
    let mut highest = 0;
    for _ in 0..210 {
        let (hooks, new_turn) = tunnel.tick(1.0 / 60.0);
        sounds += hooks
            .iter()
            .filter(|h| matches!(h.data, HookData::SoundTweaked { .. }))
            .count();
        turns += usize::from(new_turn);
        highest = highest.max(tunnel.current_animation_frame());
        assert_eq!(tunnel.poses().len(), tunnel.setup().parts.len());
        assert!(
            tunnel
                .poses()
                .iter()
                .all(|p| p.visual.is_finite() && p.rigid.is_finite())
        );
    }
    // Frames 0 to 119: the last one was reached.
    assert_eq!(highest, TUNNEL_END_FRAME - 1);
    assert!(sounds >= 1, "the tunnel's sound hook never fired");
    // A camera turn lasts 0.6 to 1.8 s: at least two began.
    assert!(turns >= 2, "{turns}");
    tunnel.exit();
    assert!(!tunnel.is_visible());
}
