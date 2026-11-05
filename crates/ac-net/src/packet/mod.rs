//! Packet layout: the 20-byte header, the optional sections its flags
//! announce, message fragments, and the checksum.
//!
//! Wire layout of a datagram:
//!
//! ```text
//! header (20) | optional sections (flag order) | fragments (16-byte header + payload)*
//! ```
//!
//! Checksum = hash(header with placeholder checksum)
//!          + (payload hash, XORed with a keystream word when sealed),
//! where payload hash = hash(optional bytes) + Σ (hash(fragment header) + hash(payload)).

mod codec;
mod flags;
mod fragment;
mod header;
mod login;

pub use codec::{
    ConnectRequest, EncodeError, OptionalHeader, PacketError, ParsedPacket, Sealed, SequenceList,
    decode_with_keystream, encode, parse_packet, seal_in_place, transport_disconnect,
    verify_checksum,
};
pub use flags::PacketFlags;
pub use fragment::{
    Fragment, FragmentHeader, Fragments, MAX_MESSAGE_BODY, parse_fragment, split_message,
    write_fragment, write_single_fragment,
};
pub use header::PacketHeader;
pub use login::{
    LoginBuildError, LoginParseError, LoginRequestFields, NetAuthType, RETAIL_CLIENT_VERSION,
    build_login_request, parse_login_request,
};
