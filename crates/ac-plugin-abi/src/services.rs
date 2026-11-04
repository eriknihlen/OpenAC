//! Plugin services (API 1.7): a plugin offers a named service, and another
//! plugin calls it and waits for the answer, so plugins built to work
//! together (a suite) ask each other things directly.
//!
//! A service is scoped by its owner, `<plugin id>/<name>`, and answered
//! through the owner's callback export
//! ([`crate::callback::Callback::ServiceRequest`], answered with
//! [`crate::callback::CallbackAnswer::Service`]). The request and the reply
//! are bytes the two plugins agree on; the client never reads them. Each
//! plugin hears [`crate::Event::ServiceChanged`] when a service comes or
//! goes, and a plugin that starts hears one for every service already
//! offered.
//!
//! An answer runs as the owner's own callback, under its own deadline and
//! slow-call time: a slow or stuck answer counts against the owner, never
//! the caller. A plugin cannot call a service while it is itself answering
//! a callback (a service request, a chat filter, an input interceptor, any
//! other), nor one of its own. A plugin that fails while answering is
//! stopped, and its services go at once.

use std::fmt;

use crate::{wire_record, wire_union};

/// The largest request or reply a service call carries, bytes.
pub const MAX_PAYLOAD: usize = 64 << 10;

/// The longest service name, bytes.
pub const MAX_NAME: usize = 64;

/// How many services one plugin may offer at once.
pub const MAX_SERVICES: usize = 32;

/// The longest service description, bytes.
pub const MAX_DESCRIPTION: usize = 1 << 10;

/// Whether `name` may name a service: 1 to [`MAX_NAME`] bytes of `a-z`,
/// `0-9` and `-`.
#[must_use]
pub fn is_valid_name(name: &str) -> bool {
    !name.is_empty()
        && name.len() <= MAX_NAME
        && name
            .bytes()
            .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-')
}

wire_record! {
    /// Offer a service.
    pub struct ServiceRegistration {
        /// Its name, scoped as `<plugin id>/<name>` ([`is_valid_name`]).
        pub name: String,
        /// What it does, for people: at most [`MAX_DESCRIPTION`] bytes.
        pub description: String,
    }
}

wire_record! {
    /// A service on offer.
    pub struct ServiceInfo {
        /// Its scoped id, `<plugin id>/<name>`.
        pub id: String,
        /// The plugin offering it.
        pub owner: String,
        /// What it does, for people.
        pub description: String,
    }
}

wire_record! {
    /// Call a service.
    pub struct ServiceCall {
        /// The scoped id, `<plugin id>/<name>` (compared ignoring case).
        pub service_id: String,
        /// The request, at most [`MAX_PAYLOAD`] bytes.
        pub payload: Vec<u8>,
    }
}

wire_record! {
    /// A call of one of the plugin's services, asked through its callback
    /// export.
    pub struct ServiceRequest {
        /// The plugin's handle for the service.
        pub handle: u32,
        /// The plugin calling it.
        pub caller_id: String,
        /// The request.
        pub payload: Vec<u8>,
    }
}

wire_record! {
    /// A service came or went.
    pub struct ServiceChange {
        /// Its scoped id, `<plugin id>/<name>`.
        pub id: String,
        /// The plugin offering it.
        pub owner: String,
        /// Whether it can be called now: false once its plugin withdrew it
        /// or stopped.
        pub available: bool,
    }
}

wire_union! {
    /// Why a service could not be offered or did not answer. What the
    /// service itself says about a request (not found, not now) belongs in
    /// its reply.
    pub enum ServiceError: "service error" {
        /// No such service.
        0 => NotFound,
        /// The service is the caller's own: a plugin calls its own code
        /// directly.
        1 => SelfCall,
        /// A call made while the caller was answering a callback.
        2 => Nested,
        /// The request or the reply is larger than the limit, bytes.
        3 => TooLarge(u32),
        /// The service did not answer before its deadline; its plugin is
        /// stopped.
        4 => TimedOut,
        /// The service's plugin failed while answering (a panic, a trap); it
        /// is stopped.
        5 => Faulted,
        /// The service refused the call, saying why (at most
        /// [`MAX_PAYLOAD`] bytes, like a reply).
        6 => Refused(String),
        /// The name is not 1 to 64 bytes of `a-z`, `0-9` and `-`.
        7 => InvalidName,
        /// The plugin already offers a service by that name.
        8 => AlreadyRegistered,
        /// The plugin already offers as many services as it may.
        9 => TooMany(u32),
        /// The plugin has no callback export, so it could not answer.
        10 => NoCallback,
        /// The description is longer than the limit, bytes.
        11 => DescriptionTooLarge(u32),
    }
}

impl fmt::Display for ServiceError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::NotFound => f.write_str("no such service"),
            Self::SelfCall => f.write_str("a plugin cannot call its own service"),
            Self::Nested => f.write_str("a service cannot be called while answering a callback"),
            Self::TooLarge(limit) => write!(f, "larger than the {limit}-byte limit"),
            Self::TimedOut => f.write_str("the service did not answer in time"),
            Self::Faulted => f.write_str("the service failed while answering"),
            Self::Refused(reason) => write!(f, "refused: {reason}"),
            Self::InvalidName => f.write_str("a service name is 1 to 64 bytes of a-z, 0-9 and '-'"),
            Self::AlreadyRegistered => f.write_str("a service by that name is already offered"),
            Self::TooMany(limit) => write!(f, "a plugin offers at most {limit} services"),
            Self::NoCallback => f.write_str("the plugin has no callback export to answer with"),
            Self::DescriptionTooLarge(limit) => {
                write!(f, "a service description is at most {limit} bytes")
            }
        }
    }
}

impl std::error::Error for ServiceError {}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::Wire;

    #[test]
    fn names_are_short_lowercase_words() {
        for good in ["path", "find-path-2", "a", &"x".repeat(MAX_NAME)] {
            assert!(is_valid_name(good), "{good}");
        }
        for bad in [
            "",
            "Path",
            "find path",
            "find_path",
            "a/b",
            &"x".repeat(MAX_NAME + 1),
        ] {
            assert!(!is_valid_name(bad), "{bad}");
        }
    }

    #[test]
    fn errors_round_trip_inside_a_result() {
        for error in [
            ServiceError::NotFound,
            ServiceError::TooLarge(65_536),
            ServiceError::Refused("busy".into()),
            ServiceError::NoCallback,
        ] {
            let reply: Result<Vec<u8>, ServiceError> = Err(error);
            assert_eq!(
                Result::<Vec<u8>, ServiceError>::from_bytes(&reply.to_bytes()),
                Ok(reply)
            );
        }
        let reply: Result<Vec<u8>, ServiceError> = Ok(vec![1, 2, 3]);
        assert_eq!(reply.to_bytes(), [0, 3, 0, 0, 0, 1, 2, 3]);
    }
}
