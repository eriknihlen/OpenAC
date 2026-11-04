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
//!
//! With the feature `json` (off by default), requests and replies are types
//! of the plugins' own, sent as JSON: `Host::register_service` gives a
//! `Service`, whose `Service::answer` decodes a request and encodes the
//! reply, and `Host::call_service` encodes the request and decodes the
//! reply. Both plugins derive serde's `Serialize` and `Deserialize` for the
//! types they share.

use ac_plugin_abi::calls;
use ac_plugin_abi::services::{ServiceCall, ServiceError, ServiceInfo, ServiceRegistration};

use crate::Host;

#[cfg(feature = "json")]
pub use json::{CallError, Service};

#[cfg(feature = "json")]
mod json {
    use std::fmt;
    use std::marker::PhantomData;

    use ac_plugin_abi::callback::CallbackAnswer;
    use ac_plugin_abi::services::{ServiceError, ServiceRequest};
    use serde::Serialize;
    use serde::de::DeserializeOwned;

    use crate::Host;

    /// One of the plugin's services, its requests `Req` and its replies
    /// `Rep` sent as JSON ([`Host::register_service`]).
    pub struct Service<Req, Rep> {
        handle: u32,
        types: PhantomData<fn(Req) -> Rep>,
    }

    impl<Req, Rep> fmt::Debug for Service<Req, Rep> {
        fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
            f.debug_struct("Service")
                .field("handle", &self.handle)
                .finish()
        }
    }

    impl<Req: DeserializeOwned, Rep: Serialize> Service<Req, Rep> {
        /// The plugin's handle for it ([`Host::unregister_service`]).
        #[must_use]
        pub fn handle(&self) -> u32 {
            self.handle
        }

        /// The answer to `request` when it calls this service, else `None`
        /// (another of the plugin's services): the request decoded and
        /// handed to `answer`, whose reply goes back encoded, or whose
        /// error refuses the call. A request that does not decode is
        /// refused, saying why.
        pub fn answer(
            &self,
            request: &ServiceRequest,
            answer: impl FnOnce(Req) -> Result<Rep, String>,
        ) -> Option<CallbackAnswer> {
            if request.handle != self.handle {
                return None;
            }
            let reply = serde_json::from_slice::<Req>(&request.payload)
                .map_err(|error| format!("the request does not decode: {error}"))
                .and_then(answer)
                .and_then(|reply| {
                    serde_json::to_vec(&reply)
                        .map_err(|error| format!("the reply does not encode: {error}"))
                });
            Some(CallbackAnswer::Service(reply))
        }
    }

    /// Why a call made with [`Host::call_service`] has no reply.
    #[derive(Debug, Clone, PartialEq)]
    pub enum CallError {
        /// The service gave none ([`ServiceError`]).
        Service(ServiceError),
        /// The request does not encode as JSON.
        Request(String),
        /// The reply does not decode as the reply type.
        Reply(String),
    }

    impl fmt::Display for CallError {
        fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
            match self {
                Self::Service(error) => error.fmt(f),
                Self::Request(why) => write!(f, "the request does not encode: {why}"),
                Self::Reply(why) => write!(f, "the reply does not decode: {why}"),
            }
        }
    }

    impl std::error::Error for CallError {}

    impl From<ServiceError> for CallError {
        fn from(error: ServiceError) -> Self {
            Self::Service(error)
        }
    }

    /// P. Plugin services, as JSON.
    impl Host<'_> {
        /// [`Self::register_service_raw`] for a service whose requests are
        /// `Req` and replies `Rep`, sent as JSON: answer each
        /// [`crate::Callback::ServiceRequest`] with [`Service::answer`].
        ///
        /// # Errors
        /// As [`Self::register_service_raw`].
        pub fn register_service<Req: DeserializeOwned, Rep: Serialize>(
            &mut self,
            name: &str,
            description: &str,
        ) -> Result<Service<Req, Rep>, ServiceError> {
            Ok(Service {
                handle: self.register_service_raw(name, description)?,
                types: PhantomData,
            })
        }

        /// [`Self::call_service_raw`] with a request of type `Req` and a
        /// reply of type `Rep`, sent as JSON.
        ///
        /// # Errors
        /// No reply ([`CallError::Service`]), or a request that does not
        /// encode or a reply that does not decode as `Rep`.
        pub fn call_service<Req: Serialize, Rep: DeserializeOwned>(
            &mut self,
            service_id: &str,
            request: &Req,
        ) -> Result<Rep, CallError> {
            let request = serde_json::to_vec(request)
                .map_err(|error| CallError::Request(error.to_string()))?;
            let reply = self.call_service_raw(service_id, &request)?;
            serde_json::from_slice(&reply).map_err(|error| CallError::Reply(error.to_string()))
        }
    }
}

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
    /// The name is not 1 to 64 bytes of `a-z`, `0-9` and `-`, the
    /// description is over 1 KiB, the plugin already offers one by that
    /// name, or it offers 32 already.
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
