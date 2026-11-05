//! The newer markup controls' bindings, `<repeat>` items and dialogs
//! through the SDK and the fake host: a plugin registers a window whose
//! bindings use the newer kinds, pushes their values, and hears their
//! handlers and its dialogs' answers. Ported in spirit from OpenAC
//! `tests/AcDream.App.Tests/UI/ControlsDemoSampleTests.cs` (a checkbox, a
//! number, a multi-select list and a dialog of the sample) and
//! `tests/AcDream.Core.Tests/Plugins/ScopedUiRegistryDialogTests.cs`.

use openac_plugin::testing::FakeHost;
use openac_plugin::ui::{
    ActionValue, Dialog, DialogHandle, Item, ItemType, Items, Panel, PanelSpec, TypedBindingKind,
    TypedValue, UiEvent,
};
use openac_plugin::{Event, Host, Plugin, StartInfo};

const MARKUP: &str = r#"<panel w="300" h="200">
  <checkbox x="8" y="8" w="140" h="16" text="Loot" checked="{Loot}" onchange="{SetLoot}"/>
  <number x="8" y="30" w="80" h="20" value="{HealAt}" onchange="{SetHealAt}" min="0" max="100"/>
  <list x="8" y="60" w="200" h="60" selectmode="multi" selected="{Picked}" onchange="{Pick}">
    <column type="text" items="{Names}"/>
  </list>
</panel>"#;

#[derive(Default)]
struct Demo {
    panel: Option<Panel>,
    loot: Option<bool>,
    picks: Vec<Vec<i32>>,
    answers: Vec<(u32, i32)>,
    dialog: Option<DialogHandle>,
}

impl Plugin for Demo {
    fn enable(&mut self, host: &mut Host<'_>, _: &StartInfo) -> Result<(), String> {
        let spec = PanelSpec::new("demo", "Demo", MARKUP)
            .bind_typed("Loot", TypedBindingKind::OptionalBool)
            .bind_typed("SetLoot", TypedBindingKind::BoolAction)
            .bind_typed("HealAt", TypedBindingKind::Int)
            .bind_typed("SetHealAt", TypedBindingKind::IntAction)
            .bind_typed("Picked", TypedBindingKind::IntList)
            .bind_typed("Pick", TypedBindingKind::IntListAction)
            .bind_typed("Names", TypedBindingKind::TextList)
            .bind_items(
                "Rows",
                ItemType::new("Row")
                    .bind("Id", TypedBindingKind::Text)
                    .bind("Label", TypedBindingKind::Text),
            );
        let mut panel = host.register_panel(&spec)?;
        panel.set("Loot", None::<bool>);
        panel.set("HealAt", 50);
        panel.set(
            "Names",
            vec!["a".to_owned(), "b".to_owned(), "c".to_owned()],
        );
        panel.set(
            "Rows",
            (1..=3)
                .map(|i| {
                    Item::new()
                        .set("Id", i.to_string())
                        .set("Label", format!("Row {i}"))
                })
                .collect::<Items>(),
        );
        panel.flush(host)?;
        self.panel = Some(panel);
        Ok(())
    }

    fn on_event(&mut self, host: &mut Host<'_>, event: Event) {
        let Event::Ui(UiEvent::Action(action)) = event else {
            return;
        };
        if let Some(answer) = action.dialog_answer() {
            self.answers.push(answer);
            return;
        }
        match action.handler.as_str() {
            "SetLoot" => {
                self.loot = action.value.as_bool();
                if let Some(panel) = self.panel.as_mut() {
                    panel.set("Loot", self.loot);
                    let _ = panel.flush(host);
                }
            }
            "Pick" => self
                .picks
                .push(action.value.as_int_list().unwrap_or_default()),
            "SetHealAt" => {
                if let ActionValue::Int(v) = action.value
                    && let Some(panel) = self.panel.as_mut()
                {
                    panel.set("HealAt", v);
                    let _ = panel.flush(host);
                }
            }
            _ => {}
        }
    }
}

#[test]
fn a_typed_window_takes_the_newer_values_and_hears_the_newer_handlers() {
    let mut host = FakeHost::new("example.demo");
    let mut plugin = Demo::default();
    host.enable(&mut plugin).unwrap();
    let panel = plugin.panel.as_ref().unwrap().id();
    assert_eq!(
        host.panel_typed_value(panel, "Loot"),
        Some(TypedValue::OptionalBool(None))
    );
    assert_eq!(host.panel_item_count(panel, "Rows"), Some(3));

    host.act(&mut plugin, panel, "SetLoot", ActionValue::from_bool(true));
    assert_eq!(plugin.loot, Some(true));
    assert_eq!(
        host.panel_typed_value(panel, "Loot"),
        Some(TypedValue::OptionalBool(Some(true)))
    );
    host.act(
        &mut plugin,
        panel,
        "Pick",
        ActionValue::from_int_list(&[0, 2]),
    );
    assert_eq!(plugin.picks, [vec![0, 2]]);
    host.act(&mut plugin, panel, "SetHealAt", ActionValue::Int(75));
    assert_eq!(
        host.panel_typed_value(panel, "HealAt"),
        Some(TypedValue::Int(75))
    );
}

#[derive(Default)]
struct Asker {
    inner: Demo,
}

impl Plugin for Asker {
    fn enable(&mut self, host: &mut Host<'_>, start: &StartInfo) -> Result<(), String> {
        self.inner.enable(host, start)
    }

    fn on_event(&mut self, host: &mut Host<'_>, event: Event) {
        if let Event::Command(_) = event {
            self.inner.dialog = host
                .confirm(
                    &Dialog::new("Delete", "Delete it?")
                        .buttons(&["Yes", "No"])
                        .owner("demo"),
                )
                .unwrap();
            return;
        }
        self.inner.on_event(host, event);
    }
}

#[test]
fn a_dialog_is_asked_only_with_a_window_and_answered_once() {
    let mut host = FakeHost::new("example.asker");
    let mut plugin = Asker::default();
    host.enable(&mut plugin).unwrap();
    host.with_host(|h| {
        assert_eq!(h.confirm(&Dialog::new("T", "M")), Ok(None));
        assert!(
            h.confirm(&Dialog::new("T", "M").buttons(&["a", "b", "c", "d", "e"]))
                .is_err()
        );
    });
    host.attach_window(&mut plugin, true);
    let handle = host
        .with_host(|h| h.confirm(&Dialog::new("Delete", "Delete it?").buttons(&["Yes", "No"])))
        .unwrap()
        .unwrap();
    host.answer_dialog(&mut plugin, handle.0, 0);
    host.answer_dialog(&mut plugin, handle.0, 1);
    assert_eq!(plugin.inner.answers, [(handle.0, 0)]);
    let stale = host
        .with_host(|h| h.confirm(&Dialog::new("Stale", "?")))
        .unwrap()
        .unwrap();
    assert!(host.with_host(|h| stale.withdraw(h)));
    assert!(!host.with_host(|h| stale.withdraw(h)));
}

const ROWS: &str = r#"<panel w="300" h="200" layout="column">
  <repeat items="{Rows}" key="{Id}">
    <group layout="row" h="22">
      <checkbox checked="{On}" onchange="{SetOn}" w="20"/>
      <label text="{Label}" grow="1"/>
    </group>
  </repeat>
</panel>"#;

/// A row's handler names its row (OpenAC `MarkupRepeatBuildTests.
/// A_copy_reads_its_item_and_calls_back_on_its_item`: the click reaches
/// that row's item only).
#[derive(Default)]
struct Rows {
    changes: Vec<(String, Option<String>, Option<bool>)>,
}

impl Plugin for Rows {
    fn enable(&mut self, host: &mut Host<'_>, _: &StartInfo) -> Result<(), String> {
        let spec = PanelSpec::new("rows", "Rows", ROWS).bind_items(
            "Rows",
            ItemType::new("Row")
                .bind("Id", TypedBindingKind::Int)
                .bind("Label", TypedBindingKind::Text)
                .bind("On", TypedBindingKind::Bool)
                .bind("SetOn", TypedBindingKind::BoolAction),
        );
        let mut panel = host.register_panel(&spec)?;
        panel.set(
            "Rows",
            (1..=3)
                .map(|i| {
                    Item::new()
                        .set("Id", i)
                        .set("Label", format!("Row {i}"))
                        .set("On", false)
                })
                .collect::<Items>(),
        );
        panel.flush(host)?;
        Ok(())
    }

    fn on_event(&mut self, _: &mut Host<'_>, event: Event) {
        let Event::Ui(UiEvent::Action(action)) = event else {
            return;
        };
        let Some(row) = action.row_action() else {
            return;
        };
        self.changes
            .push((row.handler, row.rows[0].key.clone(), action.value.as_bool()));
    }
}

#[test]
fn a_row_handler_reaches_the_plugin_with_its_row() {
    let mut host = FakeHost::new("example.rows");
    let mut plugin = Rows::default();
    host.enable(&mut plugin).unwrap();
    host.act(
        &mut plugin,
        1,
        "Rows[1:2]/SetOn",
        ActionValue::from_bool(true),
    );
    // Not a name of the row's item type: dropped.
    host.act(
        &mut plugin,
        1,
        "Rows[1:2]/Nope",
        ActionValue::from_bool(true),
    );
    assert_eq!(
        plugin.changes,
        [("SetOn".to_owned(), Some("2".to_owned()), Some(true))]
    );
}

/// Names a dock icon on a window, and on a typed window.
#[derive(Default)]
struct IconFiles;

impl Plugin for IconFiles {
    fn enable(&mut self, host: &mut Host<'_>, _: &StartInfo) -> Result<(), String> {
        let plain = PanelSpec::new("plain", "Plain", "<panel/>").icon_file("icons/plain.png");
        host.register_panel(&plain)?;
        let typed = PanelSpec::new("typed", "Typed", "<panel/>")
            .icon_file("icons/typed.png")
            .bind_typed("Value", TypedBindingKind::OptionalBool);
        host.register_panel(&typed)?;
        host.register_panel(&PanelSpec::new("none", "None", "<panel/>"))?;
        Ok(())
    }
}

#[test]
fn a_window_names_its_dock_icon_on_either_way_of_registering() {
    let mut host = FakeHost::new("example.icons");
    host.enable(&mut IconFiles).unwrap();
    let files: Vec<(String, Option<String>)> = host
        .ui
        .panels()
        .map(|p| (p.descriptor.title.clone(), p.descriptor.icon_file.clone()))
        .collect();
    assert_eq!(
        files,
        [
            ("Plain".to_owned(), Some("icons/plain.png".to_owned())),
            ("Typed".to_owned(), Some("icons/typed.png".to_owned())),
            ("None".to_owned(), None),
        ]
    );
}
