//! Live checks against the real network: HTTPS through the operating
//! system's TLS to the release feed and the population list, and the UDP
//! probe against the local ACE server. Ignored by default, and even when
//! ignored tests run (`scripts/check.*` runs them when the retail dats are
//! present) they do nothing unless `OPENAC_LIVE_TESTS=1`
//! (see `docs/launch-options.md`).

use std::sync::Arc;
use std::time::Duration;

use ac_launcher_core::cancel::CancelToken;
use ac_launcher_core::net::{self, HttpRequest, HttpTransport};
use ac_launcher_core::status::{POPULATION_URI, ReachabilityProbe, parse_player_counts};
use ac_launcher_core::updates::{
    PRODUCTION_MANIFEST_URI, ReleaseManifestClient, ReleaseManifestSource,
};
use ac_launcher_core::url::WebUrl;
use ac_launcher_net::{OsHttpTransport, UdpReachabilityProbe};

fn live() -> bool {
    let on = std::env::var("OPENAC_LIVE_TESTS").is_ok_and(|value| value == "1");
    if !on {
        eprintln!("skipped: set OPENAC_LIVE_TESTS=1 to run live tests");
    }
    on
}

#[test]
#[ignore = "network; needs OPENAC_LIVE_TESTS=1"]
fn fetches_the_release_manifest_over_os_tls() {
    if !live() {
        return;
    }
    let transport = OsHttpTransport::new().unwrap();
    let url = WebUrl::parse(PRODUCTION_MANIFEST_URI).unwrap();
    // The first hop is GitHub's redirect to the asset host, returned as is.
    let first = transport
        .get(
            &HttpRequest {
                url: url.clone(),
                timeout: Some(Duration::from_secs(15)),
            },
            &CancelToken::new(),
        )
        .unwrap();
    println!("{} -> {} {:?}", url, first.status, first.location);
    assert!(matches!(first.status, 301 | 302 | 303 | 307 | 308));
    assert!(first.location.is_some());

    // The whole walk, as the updater does it.
    let mut hops = Vec::new();
    let mut response = net::send_with_validated_redirects(
        &transport,
        &url,
        "release manifest",
        Some(Duration::from_secs(15)),
        |next| hops.push(next.to_string()),
        &CancelToken::new(),
    )
    .unwrap();
    assert_eq!(response.status, 200, "{hops:?}");
    let body =
        net::read_bounded(&mut response, 1024 * 1024, "too large", &CancelToken::new()).unwrap();
    println!("{} bytes after {} redirect(s)", body.len(), hops.len());
    assert!(!body.is_empty());

    // And through the core's strict manifest client.
    match ReleaseManifestClient::production(Arc::new(transport)).fetch(&CancelToken::new()) {
        Ok(manifest) => println!("manifest version {}", manifest.version.value()),
        Err(error) => println!("manifest fetched but refused by the strict reader: {error}"),
    }
}

#[test]
#[ignore = "network; needs OPENAC_LIVE_TESTS=1"]
fn fetches_the_population_list_over_os_tls() {
    if !live() {
        return;
    }
    let transport = OsHttpTransport::new().unwrap();
    let mut response = transport
        .get(
            &HttpRequest {
                url: WebUrl::parse(POPULATION_URI).unwrap(),
                timeout: Some(Duration::from_secs(5)),
            },
            &CancelToken::new(),
        )
        .unwrap();
    assert_eq!(response.status, 200);
    let body =
        net::read_bounded(&mut response, 1024 * 1024, "too large", &CancelToken::new()).unwrap();
    let counts = parse_player_counts(&String::from_utf8_lossy(&body)).expect("a JSON array");
    println!("{} servers listed", counts.len());
}

#[test]
#[ignore = "network; needs OPENAC_LIVE_TESTS=1"]
fn a_certificate_for_another_name_is_refused() {
    if !live() {
        return;
    }
    // badssl.com's test host whose certificate names a different host.
    let transport = OsHttpTransport::new().unwrap();
    let result = transport.get(
        &HttpRequest {
            url: WebUrl::parse("https://wrong.host.badssl.com/").unwrap(),
            timeout: Some(Duration::from_secs(15)),
        },
        &CancelToken::new(),
    );
    println!("{result:?}");
    assert!(result.is_err());
}

#[test]
#[ignore = "local ACE server; needs OPENAC_LIVE_TESTS=1"]
fn the_local_ace_server_answers_the_status_probe() {
    if !live() {
        return;
    }
    let latency = UdpReachabilityProbe.probe(
        "127.0.0.1",
        9000,
        Duration::from_secs(3),
        &CancelToken::new(),
    );
    println!("latency {latency:?} ms");
    assert!(latency.is_some());
}
