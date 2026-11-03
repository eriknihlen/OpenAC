//! Plugin services through the SDK and the fake host: a plugin offers a
//! service and answers it, as bytes and as JSON, and calls another plugin's
//! service, getting the reply or the reason there is none.

use openac_plugin::services::{MAX_PAYLOAD, ServiceError};
use openac_plugin::testing::FakeHost;
use openac_plugin::{CallError, Callback, CallbackAnswer, Event, Host, Plugin, Service, StartInfo};
use serde::{Deserialize, Serialize};

/// What the navigation plugin is asked: how far an object is.
#[derive(Debug, Serialize, Deserialize, PartialEq)]
struct DistanceTo {
    object: u32,
}

/// Its answer.
#[derive(Debug, Serialize, Deserialize, PartialEq)]
struct Distance {
    meters: f32,
}

/// Offers `distance` as JSON and `echo` as bytes; on `/nav <id>` calls
/// `example.vision/distance` and keeps what came of it.
#[derive(Default)]
struct Navigation {
    distance: Option<Service<DistanceTo, Distance>>,
    echo: u32,
    asked: Vec<Result<Distance, CallError>>,
}

impl Plugin for Navigation {
    fn enable(&mut self, host: &mut Host<'_>, _: &StartInfo) -> Result<(), String> {
        host.register_command("nav", "Asks the vision plugin")?;
        self.distance = Some(
            host.register_service("distance", "Metres to an object")
                .map_err(|error| error.to_string())?,
        );
        self.echo = host
            .register_service_raw("echo", "Says it back")
            .map_err(|error| error.to_string())?;
        Ok(())
    }

    fn on_event(&mut self, host: &mut Host<'_>, event: Event) {
        if let Event::Command(call) = event {
            let object = call.arguments.parse().unwrap_or(0);
            self.asked
                .push(host.call_service("example.vision/distance", &DistanceTo { object }));
        }
    }

    fn callback(&mut self, _host: &mut Host<'_>, callback: Callback) -> CallbackAnswer {
        let Callback::ServiceRequest(request) = callback else {
            return CallbackAnswer::Nothing;
        };
        if request.handle == self.echo {
            return CallbackAnswer::Service(Ok(request.payload));
        }
        self.distance
            .as_ref()
            .and_then(|service| {
                service.answer(&request, |asked| match asked.object {
                    0 => Err("no such object".to_owned()),
                    object => Ok(Distance {
                        meters: object as f32 / 2.0,
                    }),
                })
            })
            .unwrap_or(CallbackAnswer::Nothing)
    }
}

fn started() -> (FakeHost, Navigation) {
    let mut host = FakeHost::new("example.navigation");
    let mut plugin = Navigation::default();
    host.enable(&mut plugin).unwrap();
    (host, plugin)
}

#[test]
fn a_service_answers_as_json_and_as_bytes() {
    let (mut host, mut plugin) = started();
    assert_eq!(host.services.len(), 2);
    let reply = host
        .request_service(
            &mut plugin,
            "distance",
            "example.vision",
            br#"{"object":7}"#,
        )
        .unwrap();
    assert_eq!(
        serde_json::from_slice::<Distance>(&reply).unwrap(),
        Distance { meters: 3.5 }
    );
    assert_eq!(
        host.request_service(
            &mut plugin,
            "distance",
            "example.vision",
            br#"{"object":0}"#
        ),
        Err(ServiceError::Refused("no such object".to_owned()))
    );
    let refused = host
        .request_service(&mut plugin, "distance", "example.vision", b"not json")
        .unwrap_err();
    assert!(
        matches!(&refused, ServiceError::Refused(why) if why.starts_with("the request does not decode")),
        "{refused:?}"
    );
    assert_eq!(
        host.request_service(&mut plugin, "echo", "example.vision", b"hi"),
        Ok(b"hi".to_vec())
    );
    assert_eq!(
        host.request_service(&mut plugin, "nope", "example.vision", b"hi"),
        Err(ServiceError::NotFound)
    );
    assert_eq!(
        host.request_service(&mut plugin, "echo", "example.vision", &[0; MAX_PAYLOAD + 1]),
        Err(ServiceError::TooLarge(65_536))
    );
}

#[test]
fn a_call_gets_the_reply_or_says_why_not() {
    let (mut host, mut plugin) = started();
    host.set_service(
        "example.vision/distance",
        Ok(br#"{"meters":12.5}"#.to_vec()),
    );
    assert!(host.type_line(&mut plugin, "/nav 9"));
    host.set_service("Example.Vision/distance", Ok(b"[]".to_vec()));
    assert!(host.type_line(&mut plugin, "/nav 9"));
    host.set_service("example.vision/distance", Err(ServiceError::TimedOut));
    assert!(host.type_line(&mut plugin, "/nav 9"));
    let reply_error = matches!(&plugin.asked[1], Err(CallError::Reply(_)));
    assert_eq!(plugin.asked[0], Ok(Distance { meters: 12.5 }));
    assert!(reply_error, "{:?}", plugin.asked[1]);
    assert_eq!(
        plugin.asked[2],
        Err(CallError::Service(ServiceError::TimedOut))
    );
    assert_eq!(
        plugin.asked[2].as_ref().unwrap_err().to_string(),
        "the service did not answer in time"
    );
    // The request went as JSON.
    let calls = host.requests::<openac_plugin::calls::ServiceCall>();
    assert_eq!(calls[0].payload, br#"{"object":9}"#);
}

#[test]
fn offers_list_and_follow_the_rules_the_client_keeps() {
    let (mut host, _plugin) = started();
    host.set_service("example.vision/distance", Ok(Vec::new()));
    let ids: Vec<String> = host
        .with_host(|h| h.services())
        .into_iter()
        .map(|s| s.id)
        .collect();
    assert_eq!(
        ids,
        [
            "example.navigation/distance",
            "example.navigation/echo",
            "example.vision/distance"
        ]
    );
    host.with_host(|h| {
        assert_eq!(
            h.register_service_raw("Echo", ""),
            Err(ServiceError::InvalidName)
        );
        assert_eq!(
            h.register_service_raw("echo", ""),
            Err(ServiceError::AlreadyRegistered)
        );
        assert_eq!(
            h.call_service_raw("example.navigation/echo", b"x"),
            Err(ServiceError::SelfCall)
        );
        assert_eq!(
            h.call_service_raw("example.nobody/echo", b"x"),
            Err(ServiceError::NotFound)
        );
        assert!(h.unregister_service(2));
        assert!(!h.unregister_service(2));
    });
    assert_eq!(host.services.len(), 1);
}
