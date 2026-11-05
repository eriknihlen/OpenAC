//! The host-parity harness, ported from OpenAC
//! `tests/AcDream.HostParity.Tests`: the same scripted plugin scenario runs
//! against a session with a window attached and against one without, and
//! the two transcripts are compared line for line.
//!
//! OpenAC's two arms are two hosts with two plugin bindings; here there is
//! one program and one binding, and the arms differ in exactly what the
//! architecture lets differ: whether a window is attached to the session.
//! The windowed arm attaches a stand-in presentation from the start (a
//! window without a GPU, which answers what a window without a drawn world
//! answers: a held portal arrival is shown at once); the windowless arm
//! attaches none. Both run the whole session (the runner, its UDP driver
//! and the plugin host) against a scripted loopback server, and a plugin
//! (`parity`) relays each call the script makes through the session's own
//! binding and logs every event and callback it hears.
//!
//! A difference between the arms is allowed only for a line whose answer
//! the import census marks window-dependent; any other difference fails.
//! The scenarios that need the retail dats (a body that moves) are ignored
//! by default: set `OPENAC_DAT_DIR` or install the dats under
//! `Documents\Asheron's Call` and run with `--include-ignored`.

mod host_parity {
    pub mod arm;
    pub mod dats;
    pub mod server;
    pub mod transcript;
    pub mod wire;
    pub mod world;

    mod scenarios {
        pub mod advancement;
        mod allegiance;
        mod attack;
        mod bodied_combat;
        mod body;
        mod character_option;
        mod character_record;
        pub mod chat_entry;
        pub mod chat_input_interceptor;
        mod chat_route;
        mod client_command;
        mod container_inbound;
        mod contract;
        mod dungeon_map;
        mod fellowship;
        mod first_entry;
        mod full_inventory_pickup;
        mod ghost_dismissal;
        pub mod item;
        mod login_commands;
        mod logoff;
        mod movement;
        mod object_header;
        mod peers;
        mod plugin_chat_line;
        mod plugin_event;
        mod read;
        mod remote_body;
        mod selection_cycle;
        mod selection_lifetime;
        mod session_settings;
        mod spell_table;
        mod stale_item;
        mod surface;
        mod walk;
        mod world_interaction_event;
        mod world_label;
        mod world_object_list;
        mod world_object_use;
    }
}
