//! Social state the server owns and the client mirrors: the friends list
//! and the squelch database.

mod friends;
mod squelch;

pub use friends::{FriendEntry, FriendsState, FriendsUpdate, FriendsUpdateType};
pub use squelch::{SquelchDatabase, SquelchInfo, SquelchState};
