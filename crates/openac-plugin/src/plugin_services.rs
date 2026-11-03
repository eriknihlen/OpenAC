//! Typed [`Host`] methods for API group P: plugin services
//! ([`crate::services`], API 1.6), so plugins built to work together call
//! each other and wait for the answer.
//!
//! The plugin offering a service answers it in [`crate::Plugin::callback`]:
//!
//! ```
//! use openac_plugin::{Callback, CallbackAnswer, Host, Plugin, StartInfo};
//!
//! #[derive(Default)]
//! struct Navigation {
//!     distance: u32,
//! }
//!
//! impl Plugin for Navigation {
//!     fn enable(&mut self, host: &mut Host<'_>, _: &StartInfo) -> Result<(), String> {
//!         self.distance = host
//!             .register_service_raw("distance", "Metres to an object: its id, 4 bytes")
//!             .map_err(|error| error.to_string())?;
//!         Ok(())
//!     }
//!
//!     fn callback(&mut self, _host: &mut Host<'_>, callback: Callback) -> CallbackAnswer {
//!         match callback {
//!             Callback::ServiceRequest(request) if request.handle == self.distance => {
//!                 match <[u8; 4]>::try_from(request.payload.as_slice()) {
//!                     Ok(_id) => CallbackAnswer::Service(Ok(12.5f32.to_le_bytes().to_vec())),
//!                     Err(_) => CallbackAnswer::Service(Err("an object id is 4 bytes".into())),
//!                 }
//!             }
//!             _ => CallbackAnswer::Nothing,
//!         }
//!     }
//! }
//! ```
//!
//! and another plugin calls it by its scoped id:
//! `host.call_service_raw("example.navigation/distance", &id.to_le_bytes())`.

use ac_plugin_abi::calls;
use ac_plugin_abi::services::{ServiceCall, ServiceError, ServiceInfo, ServiceRegistration};

use crate::Host;

/// P. Plugin services.
impl Host<'_> {
    /// Offer a service other plugins can call, as `<plugin id>/<name>`;
    /// the plugin's handle for it, which each
    /// [`crate::Callback::ServiceRequest`] for it carries. The plugin answers
    /// those in [`crate::Plugin::callback`] with
    /// [`crate::CallbackAnswer::Service`]: the reply's bytes, or why it
    /// refuses. Every plugin hears [`crate::Event::ServiceChanged`]. The
    /// service goes when the plugin withdraws it or stops.
    ///
    /// # Errors
    /// The name is not 1 to 64 bytes of `a-z`, `0-9` and `-`, the plugin
    /// already offers one by that name, or it offers 32 already.
    pub fn register_service_raw(
        &mut self,
        name: &str,
        description: &str,
    ) -> Result<u32, ServiceError> {
        self.call::<calls::ServiceRegister>(&ServiceRegistration {
            name: name.to_owned(),
            description: description.to_owned(),
        })
    }

    /// Withdraw one of the plugin's services; false for a handle it does not
    /// hold.
    pub fn unregister_service(&mut self, handle: u32) -> bool {
        self.call::<calls::ServiceUnregister>(&handle)
    }

    /// Every service on offer, by id.
    pub fn services(&mut self) -> Vec<ServiceInfo> {
        self.call::<calls::Services>(&())
    }

    /// Call another plugin's service by its scoped id (`<plugin id>/<name>`,
    /// ignoring case) with a request of at most 64 KiB, and wait for its
    /// reply. The service answers as a call of its own plugin: its time is
    /// that plugin's, not the caller's.
    ///
    /// # Errors
    /// No reply ([`ServiceError`]): no such service, the plugin's own, a
    /// call made while answering a callback, a request or reply over 64 KiB,
    /// the service refusing, or its plugin stopping while it answered.
    pub fn call_service_raw(
        &mut self,
        service_id: &str,
        request: &[u8],
    ) -> Result<Vec<u8>, ServiceError> {
        self.call::<calls::ServiceCall>(&ServiceCall {
            service_id: service_id.to_owned(),
            payload: request.to_vec(),
        })
    }
}
