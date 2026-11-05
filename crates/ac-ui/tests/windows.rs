//! Registered windows and their controllers.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/RetailWindowManagerTests.cs`
//! and `RetainedPanelControllerGroupTests.cs`. OpenAC's handle events and
//! synchronous controller calls are window outputs here, routed to
//! controllers by the host when it pumps (a deliberate divergence from OpenAC).

mod common;

use std::cell::RefCell;
use std::rc::Rc;

use ac_ui::host::{AttachError, ControllerGroup, PanelController, UiHost};
use ac_ui::root::{UiOutput, UiRoot};
use ac_ui::tree::Element;
use ac_ui::widgets::Panel;
use ac_ui::windows::{RegisterError, WindowEvent, compute_authored_geometry_revision};
use ac_ui::{ElementId, MouseButton};
use common::*;

#[derive(Debug, Default)]
struct Counts {
    shown: u32,
    hidden: u32,
    disposed: u32,
    focus: Vec<Option<ElementId>>,
    order: Vec<String>,
}

#[derive(Debug, Clone, Default)]
struct Recording(Rc<RefCell<Counts>>, &'static str);

impl PanelController for Recording {
    fn on_shown(&mut self, _ui: &mut UiRoot) {
        let mut c = self.0.borrow_mut();
        c.shown += 1;
        c.order.push(format!("{}:shown", self.1));
    }
    fn on_hidden(&mut self, _ui: &mut UiRoot) {
        let mut c = self.0.borrow_mut();
        c.hidden += 1;
        c.order.push(format!("{}:hidden", self.1));
    }
    fn on_descendant_focus_changed(&mut self, _ui: &mut UiRoot, focused: Option<ElementId>) {
        self.0.borrow_mut().focus.push(focused);
    }
    fn dispose(&mut self, _ui: &mut UiRoot) {
        let mut c = self.0.borrow_mut();
        c.disposed += 1;
        c.order.push(format!("{}:disposed", self.1));
    }
}

fn events(ui: &mut UiRoot, name: &str) -> Vec<WindowEvent> {
    ui.drain_output()
        .into_iter()
        .filter_map(|o| match o {
            UiOutput::Window { name: n, event } if n == name => Some(event),
            _ => None,
        })
        .collect()
}

fn events_of(outputs: &[UiOutput], name: &str) -> Vec<WindowEvent> {
    outputs
        .iter()
        .filter_map(|o| match o {
            UiOutput::Window { name: n, event } if n == name => Some(event.clone()),
            _ => None,
        })
        .collect()
}

#[test]
fn hiding_releases_ownership_and_showing_restores_the_default_input() {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let r = host.root.root();
    let frame = host
        .root
        .add(r, at(0.0, 0.0, 300.0, 150.0), Panel::default());
    let input = host
        .root
        .add(frame, CharField::element(200.0, 20.0), CharField::default());
    host.root.default_text_input = Some(input);
    host.root.modal = Some(frame);
    host.root.set_keyboard_focus(Some(input));
    host.root.set_capture(input);
    host.root.register_window("chat", frame, None, 0).unwrap();
    let counts = Recording::default();
    host.attach_controller("chat", Box::new(counts.clone()))
        .unwrap();
    host.take_outputs();

    host.root.set_visible(frame, false);
    host.pump();
    assert!(!host.root.element(frame).unwrap().visible());
    assert_eq!(host.root.keyboard_focus(), None);
    assert_eq!(host.root.captured(), None);
    assert_eq!(host.root.modal, None);
    assert_eq!(host.root.default_text_input, None);
    assert_eq!((counts.0.borrow().shown, counts.0.borrow().hidden), (1, 1));
    let ev = events_of(&host.take_outputs(), "chat");
    assert!(ev.contains(&WindowEvent::Hidden) && !ev.contains(&WindowEvent::Shown));

    assert!(host.root.show_window("chat"));
    host.pump();
    assert!(host.root.element(frame).unwrap().visible());
    assert_eq!(host.root.default_text_input, Some(input));
    assert_eq!(counts.0.borrow().shown, 2);
    assert!(events_of(&host.take_outputs(), "chat").contains(&WindowEvent::Shown));
}

#[test]
fn geometry_close_and_lock_lifecycle() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let frame = ui.add(
        r,
        Element {
            min_width: 50.0,
            min_height: 40.0,
            ..at(10.0, 20.0, 100.0, 80.0)
        },
        Panel::default(),
    );
    ui.register_window("inventory", frame, None, 0).unwrap();
    ui.drain_output();

    assert!(ui.move_window("inventory", 30.0, 45.0));
    let e = ui.element(frame).unwrap();
    assert_eq!((e.left, e.top), (30.0, 45.0));
    assert!(ui.resize_window("inventory", 160.0, 120.0));
    let e = ui.element(frame).unwrap();
    assert_eq!((e.width, e.height), (160.0, 120.0));
    ui.set_ui_locked(true);
    ui.set_ui_locked(true);
    ui.set_ui_locked(false);
    let ev = events(&mut ui, "inventory");
    assert_eq!(
        ev,
        [
            WindowEvent::Moved,
            WindowEvent::Resized,
            WindowEvent::LockChanged(true),
            WindowEvent::LockChanged(false),
        ]
    );

    assert!(ui.close_window("inventory"));
    assert!(ui.close_window("inventory"));
    assert!(!ui.is_window_visible("inventory"));
    let ev = events(&mut ui, "inventory");
    assert_eq!(ev.iter().filter(|e| **e == WindowEvent::Hidden).count(), 1);
    assert_eq!(ev.iter().filter(|e| **e == WindowEvent::Closed).count(), 1);

    assert!(ui.show_window("inventory"));
    assert!(ui.close_window("inventory"));
    let ev = events(&mut ui, "inventory");
    assert_eq!(
        (
            ev.iter().filter(|e| **e == WindowEvent::Shown).count(),
            ev.iter().filter(|e| **e == WindowEvent::Hidden).count(),
            ev.iter().filter(|e| **e == WindowEvent::Closed).count()
        ),
        (1, 1, 1)
    );
}

#[test]
fn a_pointer_resize_reports_once_completed() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let frame = ui.add(
        r,
        Element {
            resizable: true,
            ..at(10.0, 20.0, 100.0, 80.0)
        },
        Panel::default(),
    );
    ui.register_window("chat", frame, None, 0).unwrap();
    ui.drain_output();
    ui.on_mouse_down(MouseButton::Left, 109, 60, 0);
    ui.on_mouse_move(149, 60);
    ui.on_mouse_up(MouseButton::Left, 149, 60, 0);
    assert_eq!(ui.element(frame).unwrap().width, 140.0);
    let resized = events(&mut ui, "chat")
        .into_iter()
        .filter(|e| *e == WindowEvent::Resized)
        .count();
    assert_eq!(resized, 1);
}

#[test]
fn a_programmatic_move_keeps_the_window_inside_its_parent() {
    for (left, top, el, et) in [
        (-50.0, -50.0, 0.0, 0.0),
        (5000.0, 5000.0, 500.0, 450.0),
        (30.0, 45.0, 30.0, 45.0),
    ] {
        let mut ui = root(800.0, 600.0);
        let r = ui.root();
        let frame = ui.add(r, at(0.0, 0.0, 300.0, 150.0), Panel::default());
        ui.register_window("inventory", frame, None, 0).unwrap();
        assert!(ui.move_window("inventory", left, top));
        let e = ui.element(frame).unwrap();
        assert_eq!((e.left, e.top), (el, et));
    }
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let frame = ui.add(r, at(0.0, 0.0, 1000.0, 700.0), Panel::default());
    ui.register_window("map", frame, None, 0).unwrap();
    assert!(ui.move_window("map", 250.0, 120.0));
    let e = ui.element(frame).unwrap();
    assert_eq!((e.left, e.top), (0.0, 0.0));
}

#[test]
fn a_constrained_programmatic_resize_uses_the_current_position() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let frame = ui.add(
        r,
        Element {
            resize_x: false,
            resize_y: true,
            constrain_resize_to_parent: true,
            min_height: 100.0,
            ..at(40.0, 125.0, 300.0, 200.0)
        },
        Panel::default(),
    );
    ui.register_window("main-panel", frame, None, 0).unwrap();
    assert!(ui.resize_window("main-panel", 300.0, 2_000.0));
    let e = ui.element(frame).unwrap();
    assert_eq!(e.height, 475.0);
    assert_eq!(e.top + e.height, 600.0);
}

#[test]
fn focus_and_capture_transitions_are_scoped_to_the_owning_window() {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let r = host.root.root();
    let first = host
        .root
        .add(r, at(0.0, 0.0, 200.0, 100.0), Panel::default());
    let second = host
        .root
        .add(r, at(250.0, 0.0, 200.0, 100.0), Panel::default());
    let f1 = host
        .root
        .add(first, CharField::element(100.0, 20.0), CharField::default());
    let f2 = host.root.add(
        second,
        CharField::element(100.0, 20.0),
        CharField::default(),
    );
    host.root.register_window("first", first, None, 0).unwrap();
    host.root
        .register_window("second", second, None, 0)
        .unwrap();
    let c1 = Recording::default();
    let c2 = Recording::default();
    host.attach_controller("first", Box::new(c1.clone()))
        .unwrap();
    host.attach_controller("second", Box::new(c2.clone()))
        .unwrap();
    host.take_outputs();

    host.root.set_keyboard_focus(Some(f1));
    host.root.set_keyboard_focus(Some(f2));
    host.root.set_capture(f1);
    host.root.set_capture(f2);
    host.root.release_capture();
    host.pump();
    assert_eq!(c1.0.borrow().focus, [Some(f1), None]);
    assert_eq!(c2.0.borrow().focus, [Some(f2)]);
    let out = host.take_outputs();
    let captures = |name| -> Vec<Option<ElementId>> {
        events_of(&out, name)
            .into_iter()
            .filter_map(|e| match e {
                WindowEvent::DescendantCaptureChanged(c) => Some(c),
                _ => None,
            })
            .collect()
    };
    assert_eq!(captures("first"), [Some(f1), None]);
    assert_eq!(captures("second"), [Some(f2), None]);
}

#[test]
fn unregistering_and_tree_removal_release_controllers_exactly_once() {
    let mut host = UiHost::new();
    let r = host.root.root();
    let first = host
        .root
        .add(r, at(0.0, 0.0, 100.0, 100.0), Panel::default());
    let second = host
        .root
        .add(r, at(0.0, 0.0, 100.0, 100.0), Panel::default());
    host.root.register_window("first", first, None, 0).unwrap();
    host.root
        .register_window("second", second, None, 0)
        .unwrap();
    let c1 = Recording::default();
    let c2 = Recording::default();
    host.attach_controller("first", Box::new(c1.clone()))
        .unwrap();
    host.attach_controller("second", Box::new(c2.clone()))
        .unwrap();

    assert!(host.root.unregister_window("first"));
    host.pump();
    assert!(host.root.window("first").is_none());
    assert_eq!(c1.0.borrow().disposed, 1);
    assert!(!host.root.show_window("first"));

    assert!(host.root.remove_child(r, second));
    host.pump();
    assert!(host.root.window("second").is_none());
    assert_eq!(c2.0.borrow().disposed, 1);
    assert!(!host.root.show_window("second"));

    host.shutdown();
    assert_eq!((c1.0.borrow().disposed, c2.0.borrow().disposed), (1, 1));
}

#[test]
fn attaching_after_registration_delivers_visibility_and_owns_disposal() {
    let mut host = UiHost::new();
    let r = host.root.root();
    let frame = host
        .root
        .add(r, at(0.0, 0.0, 100.0, 100.0), Panel::default());
    host.root
        .register_window("inventory", frame, None, 0)
        .unwrap();
    let c = Recording::default();
    host.attach_controller("inventory", Box::new(c.clone()))
        .unwrap();
    assert_eq!(c.0.borrow().shown, 1);
    assert_eq!(
        host.attach_controller("inventory", Box::new(Recording::default())),
        Err(AttachError::AlreadyAttached("inventory".into()))
    );
    assert_eq!(
        host.attach_controller("nope", Box::new(Recording::default())),
        Err(AttachError::UnknownWindow("nope".into()))
    );
    host.shutdown();
    host.shutdown();
    assert_eq!((c.0.borrow().hidden, c.0.borrow().disposed), (1, 1));
}

#[test]
fn visibility_events_track_every_transition() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let inv = ui.add(r, at(0.0, 0.0, 100.0, 100.0), Panel::default());
    ui.register_window("inventory", inv, None, 0).unwrap();
    ui.drain_output();
    assert!(ui.hide_window("inventory"));
    assert!(ui.show_window("inventory"));
    assert!(!ui.toggle_window("inventory"));
    let vis: Vec<bool> = events(&mut ui, "inventory")
        .into_iter()
        .filter_map(|e| match e {
            WindowEvent::VisibilityChanged(v) => Some(v),
            _ => None,
        })
        .collect();
    assert_eq!(vis, [false, true, false]);
}

#[test]
fn resize_follows_the_minimum_size_and_axis_locks() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let frame = ui.add(
        r,
        Element {
            resizable: true,
            min_width: 250.0,
            min_height: 150.0,
            ..at(0.0, 0.0, 300.0, 200.0)
        },
        Panel::default(),
    );
    ui.register_window("plugin-resizable", frame, None, 0)
        .unwrap();
    assert!(ui.resize_window("plugin-resizable", 500.0, 400.0));
    assert_eq!(
        (
            ui.element(frame).unwrap().width,
            ui.element(frame).unwrap().height
        ),
        (500.0, 400.0)
    );
    assert!(ui.resize_window("plugin-resizable", 50.0, 50.0));
    assert_eq!(
        (
            ui.element(frame).unwrap().width,
            ui.element(frame).unwrap().height
        ),
        (250.0, 150.0)
    );

    let fixed = ui.add(
        r,
        Element {
            resize_x: false,
            resize_y: false,
            ..at(0.0, 0.0, 300.0, 200.0)
        },
        Panel::default(),
    );
    ui.register_window("plugin-fixed", fixed, None, 0).unwrap();
    ui.drain_output();
    ui.resize_window("plugin-fixed", 500.0, 400.0);
    assert_eq!(
        (
            ui.element(fixed).unwrap().width,
            ui.element(fixed).unwrap().height
        ),
        (300.0, 200.0)
    );
    assert!(!events(&mut ui, "plugin-fixed").contains(&WindowEvent::Resized));
}

#[test]
fn authored_geometry_revisions_are_stable_distinct_and_non_negative() {
    let a = compute_authored_geometry_revision(856.0, 236.0, 400.0, 150.0, true);
    assert_eq!(
        a,
        compute_authored_geometry_revision(856.0, 236.0, 400.0, 150.0, true)
    );
    for (w, h, mw, mh, r) in [
        (984.0, 236.0, 400.0, 150.0, true),
        (856.0, 271.0, 400.0, 150.0, true),
        (856.0, 236.0, 420.0, 150.0, true),
        (856.0, 236.0, 400.0, 160.0, true),
        (856.0, 236.0, 400.0, 150.0, false),
    ] {
        assert_ne!(a, compute_authored_geometry_revision(w, h, mw, mh, r));
    }
    assert!(a >= 0);
    assert!(compute_authored_geometry_revision(0.0, 0.0, 0.0, 0.0, false) >= 0);
    assert!(compute_authored_geometry_revision(-1.0, -1.0, -1.0, -1.0, true) >= 0);
}

#[test]
fn registration_needs_a_name_and_a_root_child() {
    let mut ui = root(800.0, 600.0);
    let r = ui.root();
    let frame = ui.add(r, at(0.0, 0.0, 10.0, 10.0), Panel::default());
    let nested = ui.add(frame, at(0.0, 0.0, 5.0, 5.0), Panel::default());
    assert_eq!(
        ui.register_window(" ", frame, None, 0),
        Err(RegisterError::BlankName)
    );
    assert_eq!(
        ui.register_window("x", nested, None, 0),
        Err(RegisterError::NotARootChild)
    );
    ui.register_window("x", frame, None, 0).unwrap();
    ui.drain_output();
    ui.register_window("x", frame, None, 0).unwrap();
    assert!(
        events(&mut ui, "x").is_empty(),
        "same registration is a no-op"
    );
    ui.register_window("y", frame, None, 0).unwrap();
    assert!(ui.window("x").is_none(), "a frame has one name");
}

#[test]
fn element_messages_reach_the_owning_windows_controller() {
    use ac_ui::root::ElementMessage;

    #[derive(Debug, Default, Clone)]
    struct Messages(Rc<RefCell<Vec<ElementId>>>);
    impl PanelController for Messages {
        fn on_element_message(
            &mut self,
            _ui: &mut UiRoot,
            element: ElementId,
            _m: &ElementMessage,
        ) -> bool {
            self.0.borrow_mut().push(element);
            true
        }
    }

    let mut host = UiHost::new();
    let r = host.root.root();
    let frame = host
        .root
        .add(r, at(0.0, 0.0, 100.0, 100.0), Panel::default());
    let inside = host
        .root
        .add(frame, at(0.0, 0.0, 10.0, 10.0), Panel::default());
    let outside = host
        .root
        .add(r, at(200.0, 0.0, 10.0, 10.0), Panel::default());
    host.root.register_window("w", frame, None, 0).unwrap();
    let m = Messages::default();
    host.attach_controller("w", Box::new(m.clone())).unwrap();
    host.take_outputs();
    host.root.send_message(inside, ElementMessage::Pressed);
    host.root.send_message(outside, ElementMessage::Pressed);
    host.pump();
    assert_eq!(*m.0.borrow(), [inside]);
    let rest = host.take_outputs();
    assert!(matches!(rest.as_slice(), [UiOutput::Element { element, .. }] if *element == outside));
}

#[test]
fn a_window_with_a_group_lends_each_member_by_its_type() {
    #[derive(Debug, Default)]
    struct First(u32);
    impl PanelController for First {}
    #[derive(Debug, Default)]
    struct Second(u32);
    impl PanelController for Second {}

    let mut host = UiHost::new();
    let r = host.root.root();
    let frame = host
        .root
        .add(r, at(0.0, 0.0, 100.0, 100.0), Panel::default());
    host.root.register_window("w", frame, None, 0).unwrap();
    let group = ControllerGroup::new(vec![Box::new(First(1)), Box::new(Second(2))]);
    host.attach_controller("w", Box::new(group)).unwrap();
    assert_eq!(host.with_controller::<First, _>("w", |c, _| c.0), Some(1));
    assert_eq!(host.with_controller::<Second, _>("w", |c, _| c.0), Some(2));
    assert!(
        host.with_controller::<Recording, _>("w", |_, _| ())
            .is_none()
    );
}

#[test]
fn a_controller_group_shows_in_order_and_hides_and_disposes_in_reverse() {
    let log = Rc::new(RefCell::new(Counts::default()));
    let a = Recording(log.clone(), "a");
    let b = Recording(log.clone(), "b");
    let mut group = ControllerGroup::new(vec![Box::new(a), Box::new(b)]);
    let mut ui = UiRoot::new();
    group.on_shown(&mut ui);
    group.on_hidden(&mut ui);
    group.dispose(&mut ui);
    group.dispose(&mut ui);
    group.on_shown(&mut ui);
    assert_eq!(
        log.borrow().order,
        [
            "a:shown",
            "b:shown",
            "b:hidden",
            "a:hidden",
            "b:disposed",
            "a:disposed"
        ]
    );
}
