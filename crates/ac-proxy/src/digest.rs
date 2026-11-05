//! Digest access authentication (RFC 7616) as .NET's handler answers a
//! challenge (`AuthenticationHelper.Digest`): MD5, MD5-sess, SHA-256 and
//! SHA-256-sess; `qop=auth` (or `auth-int` when only that is offered); one
//! request per nonce (`nc=00000001`); a 16-character client nonce.

use sha2::{Digest as _, Sha256};

use crate::md5;
use crate::resolve::Credentials;

/// The nonce count .NET always sends.
const NONCE_COUNT: &str = "00000001";
/// The client nonce's characters (.NET `GetRandomAlphaNumericString`).
const CNONCE_ALPHABET: &[u8] = b"0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
/// The client nonce's length.
const CNONCE_LENGTH: usize = 16;

/// A Digest challenge's parameters, names compared ignoring case.
#[derive(Debug, Default, Clone, PartialEq, Eq)]
pub(crate) struct Challenge {
    parameters: Vec<(String, String)>,
}

impl Challenge {
    fn get(&self, key: &str) -> Option<&str> {
        self.parameters
            .iter()
            .find(|(name, _)| name.eq_ignore_ascii_case(key))
            .map(|(_, value)| value.as_str())
    }

    /// Whether the server says the nonce went stale (`stale=true`,
    /// exactly).
    pub(crate) fn is_stale(&self) -> bool {
        self.get("stale") == Some("true")
    }

    /// Reads the parameters after the scheme, as .NET's `DigestResponse`
    /// does: `key=value` pairs split by commas; `realm`, `nonce`, `opaque`
    /// and `qop` must be quoted; only `opaque`, `domain` and `realm` may be
    /// empty; a quoted value keeps its backslashes; reading stops at the
    /// first malformed pair, keeping those before. `Err` for a parameter
    /// given twice (.NET's dictionary refuses it, failing the request).
    pub(crate) fn parse(data: &str) -> Result<Self, String> {
        let data: Vec<char> = data.chars().collect();
        let mut challenge = Self::default();
        let mut at = 0;
        while at < data.len() {
            let Some((key, next)) = next_key(&data, at) else {
                break;
            };
            at = next;
            if key.is_empty() || at >= data.len() {
                break;
            }
            let Some((value, next)) = next_value(&data, at, must_be_quoted(&key)) else {
                break;
            };
            at = next;
            if value.is_empty()
                && !["opaque", "domain", "realm"]
                    .iter()
                    .any(|k| k.eq_ignore_ascii_case(&key))
            {
                break;
            }
            if challenge.get(&key).is_some() {
                return Err(format!("the proxy's Digest challenge gives '{key}' twice"));
            }
            challenge.parameters.push((key, value));
        }
        Ok(challenge)
    }
}

fn space_or_tab(c: char) -> bool {
    c == ' ' || c == '\t'
}

fn must_be_quoted(key: &str) -> bool {
    ["realm", "nonce", "opaque", "qop"]
        .iter()
        .any(|k| k.eq_ignore_ascii_case(key))
}

/// The key at `at` and where its value starts; `None` for a key that does
/// not end with `=`.
fn next_key(data: &[char], mut at: usize) -> Option<(String, usize)> {
    while at < data.len() && space_or_tab(data[at]) {
        at += 1;
    }
    let start = at;
    while at < data.len() && data[at] != '=' && !space_or_tab(data[at]) {
        at += 1;
    }
    if at == data.len() {
        return None;
    }
    let end = at;
    if space_or_tab(data[at]) {
        while at < data.len() && space_or_tab(data[at]) {
            at += 1;
        }
        if at == data.len() || data[at] != '=' {
            return None;
        }
    }
    while at < data.len() && (space_or_tab(data[at]) || data[at] == '=') {
        at += 1;
    }
    Some((data[start..end].iter().collect(), at))
}

/// The value at `at` and where the next key starts; `None` for a value
/// that should be quoted and is not, or that is not followed by a comma.
fn next_value(data: &[char], mut at: usize, expect_quotes: bool) -> Option<(String, usize)> {
    let quoted = data[at] == '"';
    if quoted {
        at += 1;
    }
    if expect_quotes && !quoted {
        return None;
    }
    let mut value = String::new();
    while at < data.len() && ((quoted && data[at] != '"') || (!quoted && data[at] != ',')) {
        value.push(data[at]);
        at += 1;
        if at == data.len() {
            break;
        }
        if !quoted && space_or_tab(data[at]) {
            break;
        }
        if quoted && data[at] == '"' && data[at - 1] == '\\' {
            // An escaped quote is part of the value.
            value.push(data[at]);
            at += 1;
        }
    }
    if quoted {
        at += 1;
    }
    while at < data.len() && space_or_tab(data[at]) {
        at += 1;
    }
    if at >= data.len() {
        return Some((value, at));
    }
    if data[at] != ',' {
        return None;
    }
    at += 1;
    while at < data.len() && space_or_tab(data[at]) {
        at += 1;
    }
    Some((value, at))
}

/// Lower-case hex of `data`'s hash: SHA-256 for an algorithm named
/// `SHA-256...`, else MD5.
fn hash(data: &str, algorithm: &str) -> String {
    let sha = algorithm
        .get(..7)
        .is_some_and(|head| head.eq_ignore_ascii_case("SHA-256"));
    let bytes: Vec<u8> = if sha {
        Sha256::digest(data.as_bytes()).to_vec()
    } else {
        md5::digest(data.as_bytes()).to_vec()
    };
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

/// `key="value"` with `"` and `\` escaped, or `key=value`.
fn append(out: &mut String, key: &str, value: &str, quoted: bool, comma: bool) {
    out.push_str(key);
    out.push('=');
    if quoted {
        out.push('"');
        for c in value.chars() {
            if c == '"' || c == '\\' {
                out.push('\\');
            }
            out.push(c);
        }
        out.push('"');
    } else {
        out.push_str(value);
    }
    if comma {
        out.push_str(", ");
    }
}

/// RFC 5987 `ext-value` of a user name that is not ASCII (.NET
/// `HeaderUtilities.Encode5987`).
fn encode_5987(text: &str) -> String {
    const ATTR: &[u8] =
        b"!#$&+-.0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ^_`abcdefghijklmnopqrstuvwxyz|~";
    let mut out = String::from("utf-8''");
    for byte in text.bytes() {
        if ATTR.contains(&byte) {
            out.push(char::from(byte));
        } else {
            out.push_str(&format!("%{byte:02X}"));
        }
    }
    out
}

/// A fresh client nonce.
pub(crate) fn client_nonce() -> Result<String, String> {
    crate::random::string(CNONCE_ALPHABET, CNONCE_LENGTH)
}

/// The `Proxy-Authorization` parameters answering `challenge` for a
/// request of `method` on `uri` (its path and query) carrying `body` (as
/// text, for `auth-int`), with `cnonce` as the client nonce; `None` when
/// the challenge cannot be answered (an unknown
/// algorithm, no nonce or no realm), as .NET then gives up.
pub(crate) fn token_for(
    credentials: &Credentials,
    method: &str,
    uri: &str,
    body: &str,
    challenge: &Challenge,
    cnonce: &str,
) -> Option<String> {
    let specified = challenge.get("algorithm");
    let algorithm = match specified {
        Some(name) => {
            if !["SHA-256", "MD5", "SHA-256-sess", "MD5-sess"]
                .iter()
                .any(|known| known.eq_ignore_ascii_case(name))
            {
                return None;
            }
            name
        }
        None => "MD5",
    };
    let nonce = challenge.get("nonce")?;
    let opaque = challenge.get("opaque");
    let realm = challenge.get("realm")?;
    let user = credentials.user();
    let password = credentials.password();

    let mut out = String::new();
    if let Some(userhash) = challenge.get("userhash").filter(|v| *v == "true") {
        append(
            &mut out,
            "username",
            &hash(&format!("{user}:{realm}"), algorithm),
            true,
            true,
        );
        append(&mut out, "userhash", userhash, false, true);
    } else if !user.is_ascii() {
        append(&mut out, "username*", &encode_5987(user), false, true);
    } else {
        append(&mut out, "username", user, true, true);
    }
    append(&mut out, "realm", realm, true, true);
    append(&mut out, "nonce", nonce, true, true);
    append(&mut out, "uri", uri, true, true);

    let qop_offered = challenge.get("qop");
    let mut qop = "auth";
    if let Some(offered) = qop_offered
        && let Some(int_at) = offered.find("auth-int")
        && offered.find("auth") == Some(int_at)
        && !offered[int_at + "auth-int".len()..].contains("auth")
    {
        qop = "auth-int";
    }

    let mut a1 = format!("{user}:{realm}:{password}");
    if algorithm
        .get(algorithm.len().saturating_sub(4)..)
        .is_some_and(|tail| tail.eq_ignore_ascii_case("sess"))
    {
        a1 = format!("{}:{nonce}:{cnonce}", hash(&a1, algorithm));
    }
    let mut a2 = format!("{method}:{uri}");
    if qop == "auth-int" {
        a2 = format!("{a2}:{}", hash(body, algorithm));
    }
    let response = if qop_offered.is_some() {
        hash(
            &format!(
                "{}:{nonce}:{NONCE_COUNT}:{cnonce}:{qop}:{}",
                hash(&a1, algorithm),
                hash(&a2, algorithm)
            ),
            algorithm,
        )
    } else {
        hash(
            &format!("{}:{nonce}:{}", hash(&a1, algorithm), hash(&a2, algorithm)),
            algorithm,
        )
    };
    append(
        &mut out,
        "response",
        &response,
        true,
        opaque.is_some() || specified.is_some() || qop_offered.is_some(),
    );
    if let Some(opaque) = opaque {
        append(
            &mut out,
            "opaque",
            opaque,
            true,
            specified.is_some() || qop_offered.is_some(),
        );
    }
    if specified.is_some() {
        append(
            &mut out,
            "algorithm",
            algorithm,
            false,
            qop_offered.is_some(),
        );
    }
    if qop_offered.is_some() {
        append(&mut out, "qop", qop, false, true);
        append(&mut out, "nc", NONCE_COUNT, false, true);
        append(&mut out, "cnonce", cnonce, true, false);
    }
    Some(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A token for a request without a body.
    fn token(
        credentials: &Credentials,
        method: &str,
        uri: &str,
        challenge: &Challenge,
        cnonce: &str,
    ) -> Option<String> {
        token_for(credentials, method, uri, "", challenge, cnonce)
    }

    // `auth-int` hashes the request's body into A2 (RFC 7616 3.4.3).
    #[test]
    fn auth_int_covers_the_body() {
        let user = named("me", "pw");
        let int_only = Challenge::parse("realm=\"r\", nonce=\"n\", qop=\"auth-int\"").unwrap();
        let a = token_for(&user, "POST", "/", "one", &int_only, "c").unwrap();
        let b = token_for(&user, "POST", "/", "two", &int_only, "c").unwrap();
        assert_ne!(a, b);
        let auth = Challenge::parse("realm=\"r\", nonce=\"n\", qop=\"auth\"").unwrap();
        assert_eq!(
            token_for(&user, "POST", "/", "one", &auth, "c"),
            token_for(&user, "POST", "/", "two", &auth, "c")
        );
    }

    fn named(user: &str, password: &str) -> Credentials {
        Credentials::Named {
            domain: None,
            user: user.to_owned(),
            password: password.to_owned(),
        }
    }

    /// The `response` parameter of a token.
    fn response_of(token: &str) -> &str {
        let start = token.find("response=\"").unwrap() + "response=\"".len();
        &token[start..start + token[start..].find('"').unwrap()]
    }

    // RFC 7616 section 3.9.1: the SHA-256 and MD5 examples (Mufasa, "Circle
    // of Life", GET /dir/index.html, cnonce f2/3d...). The order and quoting
    // of the parameters are .NET's.
    #[test]
    fn the_rfc_7616_examples_give_their_responses() {
        let challenge = |algorithm: &str| {
            Challenge::parse(&format!(
                "realm=\"http-auth@example.org\", qop=\"auth, auth-int\", algorithm={algorithm}, \
                 nonce=\"7ypf/xlj9XXwfDPEoM4URrv/xwf94BcCAzFZH4GiTo0v\", \
                 opaque=\"FQhe/qaU925kfnzjCev0ciny7QMkPqMAFRtzCUYo5tdS\""
            ))
            .unwrap()
        };
        let user = named("Mufasa", "Circle of Life");
        let cnonce = "f2/wE4q74E6zIJEtWaHKaf5wv/H5QzzpXusqGemxURZJ";
        let sha = token(
            &user,
            "GET",
            "/dir/index.html",
            &challenge("SHA-256"),
            cnonce,
        )
        .unwrap();
        assert_eq!(
            response_of(&sha),
            "753927fa0e85d155564e2e272a28d1802ca10daf4496794697cf8db5856cb6c1"
        );
        assert_eq!(
            sha,
            "username=\"Mufasa\", realm=\"http-auth@example.org\", \
             nonce=\"7ypf/xlj9XXwfDPEoM4URrv/xwf94BcCAzFZH4GiTo0v\", uri=\"/dir/index.html\", \
             response=\"753927fa0e85d155564e2e272a28d1802ca10daf4496794697cf8db5856cb6c1\", \
             opaque=\"FQhe/qaU925kfnzjCev0ciny7QMkPqMAFRtzCUYo5tdS\", algorithm=SHA-256, \
             qop=auth, nc=00000001, cnonce=\"f2/wE4q74E6zIJEtWaHKaf5wv/H5QzzpXusqGemxURZJ\""
        );
        let md5 = token(&user, "GET", "/dir/index.html", &challenge("MD5"), cnonce).unwrap();
        assert_eq!(response_of(&md5), "8ca523f5e9506fed4657c9700eebdbec");
    }

    // RFC 2617 section 3.5 (no algorithm, qop=auth): the MD5 response, and
    // no `algorithm` parameter is sent when the challenge named none.
    #[test]
    fn the_rfc_2617_example_gives_its_response() {
        let challenge = Challenge::parse(
            "realm=\"testrealm@host.com\", qop=\"auth,auth-int\", \
             nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\", \
             opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"",
        )
        .unwrap();
        let token = token(
            &named("Mufasa", "Circle Of Life"),
            "GET",
            "/dir/index.html",
            &challenge,
            "0a4f113b",
        )
        .unwrap();
        assert_eq!(response_of(&token), "6629fae49393a05397450978507c4ef1");
        assert!(!token.contains("algorithm="));
        assert!(token.ends_with("qop=auth, nc=00000001, cnonce=\"0a4f113b\""));
    }

    // .NET's choices: no qop offered sends no qop, nc or cnonce; `auth-int`
    // alone is taken; an unknown algorithm or a missing nonce is no answer;
    // a non-ASCII user name goes as `username*`; `userhash=true` hashes it.
    #[test]
    fn the_parameters_follow_what_the_challenge_offers() {
        let user = named("Mufasa", "pw");
        let bare = Challenge::parse("realm=\"r\", nonce=\"n\"").unwrap();
        let t = token(&user, "CONNECT", "/", &bare, "c").unwrap();
        assert!(
            t.contains("uri=\"/\", response=\"") && t.ends_with('"'),
            "{t}"
        );
        assert!(!t.contains("qop") && !t.contains("cnonce") && !t.contains("nc="));
        let int_only = Challenge::parse("realm=\"r\", nonce=\"n\", qop=\"auth-int\"").unwrap();
        assert!(
            token(&user, "GET", "/", &int_only, "c")
                .unwrap()
                .contains("qop=auth-int")
        );
        let both = Challenge::parse("realm=\"r\", nonce=\"n\", qop=\"auth-int,auth\"").unwrap();
        assert!(
            token(&user, "GET", "/", &both, "c")
                .unwrap()
                .contains("qop=auth,")
        );
        let unknown = Challenge::parse("realm=\"r\", nonce=\"n\", algorithm=SHA-512").unwrap();
        assert_eq!(token(&user, "GET", "/", &unknown, "c"), None);
        let no_nonce = Challenge::parse("realm=\"r\"").unwrap();
        assert_eq!(token(&user, "GET", "/", &no_nonce, "c"), None);
        let accented = token(&named("Jäsón", "pw"), "GET", "/", &bare, "c").unwrap();
        assert!(
            accented.starts_with("username*=utf-8''J%C3%A4s%C3%B3n, "),
            "{accented}"
        );
        let hashed = Challenge::parse("realm=\"r\", nonce=\"n\", userhash=true").unwrap();
        let t = token(&user, "GET", "/", &hashed, "c").unwrap();
        assert!(t.starts_with(&format!(
            "username=\"{}\", userhash=true, ",
            hash("Mufasa:r", "MD5")
        )));
    }

    // .NET's `DigestResponse`: unquoted `nonce` stops the reading; `stale`
    // is true only as exactly `true`; a parameter given twice fails.
    #[test]
    fn challenges_parse_as_dotnet_reads_them() {
        let c = Challenge::parse(" realm = \"a\\\"b\" ,nonce=\"n\", stale=true").unwrap();
        assert_eq!(c.get("REALM"), Some("a\\\"b"));
        assert!(c.is_stale());
        assert!(
            !Challenge::parse("nonce=\"n\", stale=TRUE")
                .unwrap()
                .is_stale()
        );
        let stopped = Challenge::parse("realm=\"r\", nonce=n, opaque=\"o\"").unwrap();
        assert_eq!(stopped.get("nonce"), None);
        assert_eq!(stopped.get("opaque"), None);
        assert!(Challenge::parse("realm=\"r\", realm=\"s\"").is_err());
    }
}
