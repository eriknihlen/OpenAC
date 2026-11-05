//! `fetcher`: the network test plugin. `/fetch <url>` sends a GET and
//! posts the reply's status, error and body; a refusal is posted at once.

use openac_plugin::net::NetEvent;
use openac_plugin::{Event, Host, Plugin, StartInfo};

#[derive(Default)]
pub struct Fetcher;

impl Plugin for Fetcher {
    fn enable(&mut self, host: &mut Host<'_>, _info: &StartInfo) -> Result<(), String> {
        host.register_command("fetch", "Fetches an address")?;
        Ok(())
    }

    fn on_event(&mut self, host: &mut Host<'_>, event: Event) {
        match event {
            Event::Command(call) => {
                if let Err(why) = host.http_get(call.arguments.trim()) {
                    host.chat_post(&format!("fetch refused: {why}"));
                }
            }
            Event::Net(NetEvent::HttpCompleted(reply)) => {
                host.chat_post(&format!(
                    "fetched {} {:?} {}",
                    reply.status,
                    reply.error,
                    String::from_utf8_lossy(&reply.body)
                ));
            }
            _ => {}
        }
    }
}

openac_plugin::export_plugin!(Fetcher);
