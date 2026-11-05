//! Answering a proxy's `407` as .NET's handler does
//! (`AuthenticationHelper`, `AuthenticationHelper.NtAuth`): only with the
//! credentials the proxy's address carries; of the schemes the proxy
//! offers, Negotiate first, then NTLM, Digest and Basic; Basic once;
//! Digest once more only when the server says its nonce went stale; NTLM
//! and Negotiate leg by leg on one connection until the package is done.
//! Where the proxy will not be satisfied the `407` stands: a tunnel fails
//! with it, a plain request returns it.

use crate::base64;
use crate::digest;
use crate::resolve::{Credentials, ProxyServer};

/// What to do after an answer from the proxy.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Step {
    /// Send the request again with this `Proxy-Authorization` value.
    Send(String),
    /// The proxy's answer stands; the text says why, for an error.
    Stop(String),
    /// The request fails; the text says why.
    Fail(String),
}

/// A scheme .NET answers, in its order of preference.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Scheme {
    Negotiate,
    Ntlm,
    Digest,
    Basic,
}

impl Scheme {
    const PREFERENCE: [Self; 4] = [Self::Negotiate, Self::Ntlm, Self::Digest, Self::Basic];

    fn name(self) -> &'static str {
        match self {
            Self::Negotiate => "Negotiate",
            Self::Ntlm => "NTLM",
            Self::Digest => "Digest",
            Self::Basic => "Basic",
        }
    }
}

#[derive(Debug)]
enum State {
    /// Nothing sent yet.
    Fresh,
    /// Basic credentials were sent.
    Basic,
    /// A Digest answer was sent; `retried` once the stale nonce was.
    Digest { retried: bool },
    /// An NTLM or Negotiate handshake runs (Windows only).
    #[cfg_attr(not(windows), allow(dead_code))]
    Connection { scheme: Scheme, context: Handshake },
    /// Nothing more will be tried.
    Over,
}

/// The connection-based handshake: SSPI on Windows.
#[cfg(windows)]
type Handshake = crate::sspi::Context;
/// No handshake exists off Windows (never made).
#[cfg(not(windows))]
#[derive(Debug)]
enum Handshake {}

/// The proxy-authentication state of one request (or one tunnel).
#[derive(Debug)]
pub struct ProxyAuth {
    proxy: String,
    proxy_host: String,
    credentials: Option<Credentials>,
    /// The request's body as text, for Digest's `auth-int`.
    body: String,
    state: State,
}

/// Splits `Proxy-Authenticate` values into challenges `(scheme, data)`, as
/// .NET's header parser does: a header may hold several challenges split
/// by commas; a new one starts at a name not followed by `=`. `data` is the
/// rest after the scheme (a token, or the parameter list), `None` if none.
#[must_use]
pub fn split_challenges<'a>(
    values: impl IntoIterator<Item = &'a str>,
) -> Vec<(String, Option<String>)> {
    let mut out: Vec<(String, Option<String>)> = Vec::new();
    for value in values {
        let mut current: Option<(String, Vec<String>)> = None;
        for item in split_outside_quotes(value) {
            let item = item.trim();
            if item.is_empty() {
                continue;
            }
            let name_end = item.find([' ', '\t', '=']).unwrap_or(item.len());
            let starts_challenge = !item[name_end..].trim_start().starts_with('=');
            if starts_challenge {
                if let Some((scheme, parts)) = current.take() {
                    out.push(finish(scheme, parts));
                }
                let rest = item[name_end..].trim();
                let parts = if rest.is_empty() {
                    Vec::new()
                } else {
                    vec![rest.to_owned()]
                };
                current = Some((item[..name_end].to_owned(), parts));
            } else if let Some((_, parts)) = current.as_mut() {
                parts.push(item.to_owned());
            }
        }
        if let Some((scheme, parts)) = current.take() {
            out.push(finish(scheme, parts));
        }
    }
    out
}

fn finish(scheme: String, parts: Vec<String>) -> (String, Option<String>) {
    let data = (!parts.is_empty()).then(|| parts.join(", "));
    (scheme, data)
}

/// `text` split at commas that are not inside a quoted string.
fn split_outside_quotes(text: &str) -> Vec<&str> {
    let mut items = Vec::new();
    let mut quoted = false;
    let mut escaped = false;
    let mut start = 0;
    for (at, c) in text.char_indices() {
        if escaped {
            escaped = false;
            continue;
        }
        match c {
            '\\' if quoted => escaped = true,
            '"' => quoted = !quoted,
            ',' if !quoted => {
                items.push(&text[start..at]);
                start = at + 1;
            }
            _ => {}
        }
    }
    items.push(&text[start..]);
    items
}

/// The data of the first challenge for `scheme` (names ignoring case):
/// `None` when the proxy did not offer it, `Some(None)` when it did
/// without data.
fn challenge_for(challenges: &[(String, Option<String>)], scheme: Scheme) -> Option<Option<&str>> {
    challenges
        .iter()
        .find(|(name, _)| name.eq_ignore_ascii_case(scheme.name()))
        .map(|(_, data)| data.as_deref())
}

impl ProxyAuth {
    /// For a request through `proxy`, answered with its credentials.
    #[must_use]
    pub fn new(proxy: &ProxyServer) -> Self {
        Self {
            proxy: proxy.to_string(),
            proxy_host: proxy.host.clone(),
            credentials: proxy.credentials.clone(),
            body: String::new(),
            state: State::Fresh,
        }
    }

    /// The request carries `body` (.NET hashes its content, read as text,
    /// into a Digest `auth-int` answer).
    #[must_use]
    pub fn with_body(mut self, body: &[u8]) -> Self {
        self.body = String::from_utf8_lossy(body).into_owned();
        self
    }

    fn refused(&self) -> String {
        format!("The proxy {} refused the credentials.", self.proxy)
    }

    /// The proxy answered `407`: `challenges` are its `Proxy-Authenticate`
    /// values; `method` and `uri` (path and query) are the request's, for
    /// Digest; `endpoint` is the TLS channel binding of the connection to
    /// the proxy (RFC 5929 `tls-server-end-point`), for NTLM and Negotiate.
    pub fn challenged<'a>(
        &mut self,
        challenges: impl IntoIterator<Item = &'a str>,
        method: &str,
        uri: &str,
        endpoint: Option<&[u8]>,
    ) -> Step {
        let challenges = split_challenges(challenges);
        match std::mem::replace(&mut self.state, State::Over) {
            State::Fresh => self.start(&challenges, method, uri, endpoint),
            State::Basic | State::Over => Step::Stop(self.refused()),
            State::Digest { retried } => {
                let stale = challenge_for(&challenges, Scheme::Digest)
                    .flatten()
                    .and_then(|data| digest::Challenge::parse(data).ok())
                    .filter(digest::Challenge::is_stale);
                match (retried, stale) {
                    (false, Some(challenge)) => match self.digest(&challenge, method, uri) {
                        Ok(Some(value)) => {
                            self.state = State::Digest { retried: true };
                            Step::Send(value)
                        }
                        Ok(None) => Step::Stop(self.refused()),
                        Err(text) => Step::Fail(text),
                    },
                    _ => Step::Stop(self.refused()),
                }
            }
            State::Connection { scheme, context } => {
                self.next_leg(scheme, context, challenge_for(&challenges, scheme))
            }
        }
    }

    /// The proxy answered with anything but `407` after this request's
    /// credentials went out: a Negotiate handshake that is not finished
    /// checks the proxy's last token, and the request fails if it does not
    /// verify (.NET's tail check).
    pub fn answered<'a>(
        &mut self,
        challenges: impl IntoIterator<Item = &'a str>,
    ) -> Result<(), String> {
        let state = std::mem::replace(&mut self.state, State::Over);
        let State::Connection { scheme, context } = state else {
            return Ok(());
        };
        let challenges = split_challenges(challenges);
        let Some(Some(data)) = challenge_for(&challenges, scheme) else {
            return Ok(());
        };
        tail_check(scheme, context, data, &self.proxy)
    }

    fn start(
        &mut self,
        challenges: &[(String, Option<String>)],
        method: &str,
        uri: &str,
        endpoint: Option<&[u8]>,
    ) -> Step {
        let Some(credentials) = self.credentials.clone() else {
            return Step::Stop(format!(
                "The proxy {} asks for credentials, and its address carries none.",
                self.proxy
            ));
        };
        for scheme in Scheme::PREFERENCE {
            let Some(data) = challenge_for(challenges, scheme) else {
                continue;
            };
            return match scheme {
                Scheme::Basic => {
                    let text = match credentials.domain() {
                        Some(domain) => format!(
                            "{domain}\\{}:{}",
                            credentials.user(),
                            credentials.password()
                        ),
                        None => format!("{}:{}", credentials.user(), credentials.password()),
                    };
                    self.state = State::Basic;
                    Step::Send(format!("Basic {}", base64::encode(text.as_bytes())))
                }
                Scheme::Digest => {
                    let challenge = match digest::Challenge::parse(data.unwrap_or("")) {
                        Ok(challenge) => challenge,
                        Err(text) => return Step::Fail(text),
                    };
                    match self.digest(&challenge, method, uri) {
                        Ok(Some(value)) => {
                            self.state = State::Digest { retried: false };
                            Step::Send(value)
                        }
                        Ok(None) => Step::Stop(format!(
                            "The proxy {} asks for a Digest answer the client cannot give.",
                            self.proxy
                        )),
                        Err(text) => Step::Fail(text),
                    }
                }
                Scheme::Negotiate | Scheme::Ntlm => {
                    self.begin_handshake(scheme, &credentials, data, endpoint)
                }
            };
        }
        Step::Stop(format!(
            "The proxy {} asks for a kind of authentication the client cannot give.",
            self.proxy
        ))
    }

    fn digest(
        &self,
        challenge: &digest::Challenge,
        method: &str,
        uri: &str,
    ) -> Result<Option<String>, String> {
        let Some(credentials) = &self.credentials else {
            return Ok(None);
        };
        let cnonce = digest::client_nonce()?;
        Ok(
            digest::token_for(credentials, method, uri, &self.body, challenge, &cnonce)
                .map(|parameters| format!("Digest {parameters}")),
        )
    }

    #[cfg(windows)]
    fn begin_handshake(
        &mut self,
        scheme: Scheme,
        credentials: &Credentials,
        data: Option<&str>,
        endpoint: Option<&[u8]>,
    ) -> Step {
        let target = format!("HTTP/{}", crate::sspi::canonical_name(&self.proxy_host));
        match crate::sspi::Context::new(scheme.name(), credentials, &target, endpoint) {
            Ok(context) => self.next_leg(scheme, context, Some(data)),
            Err(text) => Step::Stop(format!(
                "The proxy {} asks for {}: {text}.",
                self.proxy,
                scheme.name()
            )),
        }
    }

    #[cfg(not(windows))]
    fn begin_handshake(
        &mut self,
        scheme: Scheme,
        _credentials: &Credentials,
        _data: Option<&str>,
        _endpoint: Option<&[u8]>,
    ) -> Step {
        let _ = &self.proxy_host;
        Step::Fail(format!(
            "The proxy {} asks for {} authentication, which this client answers only on Windows.",
            self.proxy,
            scheme.name()
        ))
    }

    /// One leg of NTLM or Negotiate: the proxy's token (`None`: it did not
    /// answer with this scheme again) to the next one of ours.
    #[cfg(windows)]
    fn next_leg(
        &mut self,
        scheme: Scheme,
        mut context: Handshake,
        data: Option<Option<&str>>,
    ) -> Step {
        let Some(data) = data else {
            return Step::Stop(self.refused());
        };
        if context.is_authenticated() {
            return Step::Stop(self.refused());
        }
        let input = match data.map(base64::decode) {
            None => None,
            Some(Some(bytes)) => Some(bytes),
            Some(None) => return Step::Stop(self.refused()),
        };
        match context.step(input.as_deref()) {
            Ok(token) => {
                let value = if token.is_empty() {
                    scheme.name().to_owned()
                } else {
                    format!("{} {}", scheme.name(), base64::encode(&token))
                };
                self.state = State::Connection { scheme, context };
                Step::Send(value)
            }
            Err(text) => Step::Stop(format!("{} ({text})", self.refused())),
        }
    }

    #[cfg(not(windows))]
    fn next_leg(
        &mut self,
        _scheme: Scheme,
        context: Handshake,
        _data: Option<Option<&str>>,
    ) -> Step {
        match context {}
    }
}

#[cfg(windows)]
fn tail_check(
    scheme: Scheme,
    mut context: Handshake,
    data: &str,
    proxy: &str,
) -> Result<(), String> {
    if context.is_authenticated() {
        return Ok(());
    }
    let token = base64::decode(data);
    match token.map(|bytes| context.step(Some(&bytes))) {
        Some(Ok(_)) => Ok(()),
        _ => Err(format!(
            "The proxy {proxy}'s final {} token did not verify.",
            scheme.name()
        )),
    }
}

#[cfg(not(windows))]
fn tail_check(
    _scheme: Scheme,
    context: Handshake,
    _data: &str,
    _proxy: &str,
) -> Result<(), String> {
    match context {}
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::resolve::ProxyKind;

    fn proxy(credentials: Option<Credentials>) -> ProxyServer {
        ProxyServer {
            kind: ProxyKind::Http,
            host: "proxy.invalid".into(),
            port: 8080,
            credentials,
        }
    }

    fn named(domain: Option<&str>, user: &str, password: &str) -> Option<Credentials> {
        Some(Credentials::Named {
            domain: domain.map(str::to_owned),
            user: user.to_owned(),
            password: password.to_owned(),
        })
    }

    // .NET's `AuthenticationHeaderValue` list: several challenges in one
    // header, split where a name is not followed by `=`; quoted commas stay.
    #[test]
    fn challenges_split_as_dotnet_reads_them() {
        let split = split_challenges([
            "Basic realm=\"a, b\", Digest realm=\"r\", nonce=\"n\", NTLM",
            "Negotiate TlRMTVNTUAABAAAA==",
        ]);
        assert_eq!(
            split,
            vec![
                ("Basic".into(), Some("realm=\"a, b\"".into())),
                ("Digest".into(), Some("realm=\"r\", nonce=\"n\"".into())),
                ("NTLM".into(), None),
                ("Negotiate".into(), Some("TlRMTVNTUAABAAAA==".into())),
            ]
        );
    }

    // .NET: no credentials, no answer; Basic once, with the domain before
    // a backslash; the logon's empty user and password for Basic too.
    #[test]
    fn basic_is_answered_once() {
        let mut none = ProxyAuth::new(&proxy(None));
        assert!(
            matches!(none.challenged(["Basic realm=\"x\""], "CONNECT", "/", None), Step::Stop(text) if text.contains("carries none"))
        );
        let mut auth = ProxyAuth::new(&proxy(named(Some("CORP"), "me", "pw")));
        assert_eq!(
            auth.challenged(["Basic realm=\"x\""], "CONNECT", "/", None),
            Step::Send(format!("Basic {}", base64::encode(b"CORP\\me:pw")))
        );
        assert!(
            matches!(auth.challenged(["Basic realm=\"x\""], "CONNECT", "/", None), Step::Stop(text) if text.contains("refused"))
        );
        let mut logon = ProxyAuth::new(&proxy(Some(Credentials::Logon)));
        assert_eq!(
            logon.challenged(["Basic"], "CONNECT", "/", None),
            Step::Send("Basic Og==".into())
        );
    }

    // .NET's order: Digest before Basic; a second Digest challenge is
    // answered only when it says the nonce went stale, and only once.
    #[test]
    fn digest_is_preferred_to_basic_and_retried_once_when_stale() {
        let mut auth = ProxyAuth::new(&proxy(named(None, "me", "pw")));
        let offer = [
            "Basic realm=\"x\"",
            "Digest realm=\"r\", nonce=\"n1\", qop=\"auth\"",
        ];
        let Step::Send(first) = auth.challenged(offer, "CONNECT", "/", None) else {
            panic!("a Digest answer");
        };
        assert!(
            first.starts_with("Digest username=\"me\", realm=\"r\", nonce=\"n1\", uri=\"/\""),
            "{first}"
        );
        let stale = ["Digest realm=\"r\", nonce=\"n2\", qop=\"auth\", stale=true"];
        let Step::Send(second) = auth.challenged(stale, "CONNECT", "/", None) else {
            panic!("a fresh answer for the stale nonce");
        };
        assert!(second.contains("nonce=\"n2\""));
        assert!(matches!(
            auth.challenged(stale, "CONNECT", "/", None),
            Step::Stop(_)
        ));
        let mut not_stale = ProxyAuth::new(&proxy(named(None, "me", "pw")));
        let _ = not_stale.challenged(offer, "CONNECT", "/", None);
        assert!(matches!(
            not_stale.challenged(["Digest realm=\"r\", nonce=\"n2\""], "CONNECT", "/", None),
            Step::Stop(_)
        ));
    }

    #[test]
    fn an_unknown_scheme_cannot_be_answered() {
        let mut auth = ProxyAuth::new(&proxy(named(None, "me", "pw")));
        assert!(matches!(
            auth.challenged(["Bearer realm=\"x\""], "CONNECT", "/", None),
            Step::Stop(text) if text.contains("cannot give")
        ));
    }

    // NTLM and Negotiate run through SSPI on Windows; elsewhere the request
    // fails saying so.
    #[cfg(not(windows))]
    #[test]
    fn ntlm_and_negotiate_are_refused_off_windows() {
        for scheme in ["NTLM", "Negotiate"] {
            let mut auth = ProxyAuth::new(&proxy(Some(Credentials::Logon)));
            assert!(matches!(
                auth.challenged([scheme, "Basic realm=\"x\""], "CONNECT", "/", None),
                Step::Fail(text) if text.contains("only on Windows")
            ));
        }
    }

    // On Windows the first NTLM leg is SSPI's negotiate message (RFC-less,
    // MS-NLMP 2.2.1.1: the signature, then message type 1).
    #[cfg(windows)]
    #[test]
    fn ntlm_starts_with_the_negotiate_message() {
        let mut auth = ProxyAuth::new(&proxy(Some(Credentials::Logon)));
        let Step::Send(value) =
            auth.challenged(["NTLM", "Basic realm=\"x\""], "CONNECT", "/", None)
        else {
            panic!("an NTLM token");
        };
        let token = base64::decode(value.strip_prefix("NTLM ").unwrap()).unwrap();
        assert_eq!(&token[..12], b"NTLMSSP\0\x01\0\0\0");
    }
}
