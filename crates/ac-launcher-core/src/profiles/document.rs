//! The profile document's types and its JSON form (version 3: plugins and
//! shared logon commands per account, accounts per server, and on each
//! character its own profile tags and logon commands; the document lists
//! every profile name).

use ac_host_core::json::{self as host_json, JsonError, Node, ObjectReader};

use super::store::{CURRENT_PROFILE_VERSION, ProfileError};
use crate::json::{self, ObjectBuilder, Out};

/// How a character's row launches.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum LaunchMode {
    /// The graphical client, entering the world as this character.
    Gui,
    /// The graphical client at the character screen.
    GuiSelect,
    /// The windowless client, entering the world as this character.
    Headless,
}

impl LaunchMode {
    /// The name the document uses (`gui`, `guiSelect`, `headless`).
    pub const fn json_name(self) -> &'static str {
        match self {
            Self::Gui => "gui",
            Self::GuiSelect => "guiSelect",
            Self::Headless => "headless",
        }
    }

    /// The name OpenAC's enum carries (`Gui`, `GuiSelect`, `Headless`).
    pub const fn name(self) -> &'static str {
        match self {
            Self::Gui => "Gui",
            Self::GuiSelect => "GuiSelect",
            Self::Headless => "Headless",
        }
    }

    pub(crate) const NAMES: [(&'static str, Self); 3] = [
        ("gui", Self::Gui),
        ("guiSelect", Self::GuiSelect),
        ("headless", Self::Headless),
    ];
}

/// One character on an account.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CharacterProfile {
    /// The character's name.
    pub name: String,
    /// The character's id as `0x` and eight hex digits, when known.
    pub id: Option<String>,
    /// How this character launches.
    pub launch_mode: LaunchMode,
    /// This character's own plugin list, used instead of its account's;
    /// `None` (and left out of the file) while it uses the account's.
    pub plugins: Option<Vec<String>>,
    /// The profiles this character is in. Within one account a profile is
    /// on at most one character: choosing the profile picks that character
    /// on the account's row. Left out of the file while empty.
    pub profiles: Vec<String>,
    /// Commands run in order after this character logs in, after its
    /// account's shared ones. Left out of the file while empty.
    pub login_commands: Vec<String>,
}

impl CharacterProfile {
    /// A character with no id, launching to the character screen, using
    /// its account's plugins.
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            id: None,
            launch_mode: LaunchMode::GuiSelect,
            plugins: None,
            profiles: Vec::new(),
            login_commands: Vec::new(),
        }
    }

    /// Whether the character is in `profile`, ignoring case.
    pub fn has_profile(&self, profile: &str) -> bool {
        self.profiles
            .iter()
            .any(|known| known.eq_ignore_ascii_case(profile))
    }
}

/// One account on a server.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AccountProfile {
    /// The account name.
    pub account: String,
    /// The password, in plain text (may be empty).
    pub password: String,
    /// Profile tags such as "Main" or "Bots"; the main window filters
    /// accounts by them.
    pub profiles: Vec<String>,
    /// The plugins every character on this account launches with, unless
    /// the character has its own list.
    pub plugins: Vec<String>,
    /// The shared commands: run in order after any character on this
    /// account logs in (and at the character screen), before the
    /// character's own.
    pub login_commands: Vec<String>,
    /// The characters known on this account.
    pub characters: Vec<CharacterProfile>,
    /// The character this account's row is set to launch; `None` for the
    /// character screen. Left out of the file while unset.
    pub selected_character: Option<String>,
    /// The launch mode this account's row is set to. Left out of the file
    /// while unset.
    pub selected_launch_mode: Option<LaunchMode>,
}

impl AccountProfile {
    /// An account with no characters, plugins, commands or row selection.
    pub fn new(account: impl Into<String>, password: impl Into<String>) -> Self {
        Self {
            account: account.into(),
            password: password.into(),
            profiles: Vec::new(),
            plugins: Vec::new(),
            login_commands: Vec::new(),
            characters: Vec::new(),
            selected_character: None,
            selected_launch_mode: None,
        }
    }

    /// The plugins a character on this account launches with: its own list
    /// when it has one, the account's otherwise.
    pub fn plugins_for<'a>(&'a self, character: Option<&'a CharacterProfile>) -> &'a [String] {
        character
            .and_then(|character| character.plugins.as_deref())
            .unwrap_or(&self.plugins)
    }

    /// The commands a login runs: the account's shared ones, then the
    /// character's own; only the shared ones without a character (the
    /// player picks one at the character screen).
    pub fn login_commands_for(&self, character: Option<&CharacterProfile>) -> Vec<String> {
        let mut commands = self.login_commands.clone();
        if let Some(character) = character {
            commands.extend(character.login_commands.iter().cloned());
        }
        commands
    }
}

/// One game server.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ServerProfile {
    /// The server's name, unique in the document.
    pub name: String,
    /// Host name or address.
    pub host: String,
    /// Port, 1 through 65535 once validated.
    pub port: i32,
    /// The accounts used on this server.
    pub accounts: Vec<AccountProfile>,
    /// True once this server's account list has been created or reviewed
    /// by this launcher. Left out of the file while false.
    pub account_list_initialized: bool,
}

impl ServerProfile {
    /// A server with no accounts.
    pub fn new(name: impl Into<String>, host: impl Into<String>, port: i32) -> Self {
        Self {
            name: name.into(),
            host: host.into(),
            port,
            accounts: Vec::new(),
            account_list_initialized: false,
        }
    }
}

/// The whole profile document.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LauncherProfileDocument {
    /// The document version (3).
    pub version: i32,
    /// Every profile name, in the order the chips show them: a profile
    /// with no account or character in it still exists. A tag used on an
    /// account or a character is always in the list (validation adds it).
    /// Left out of the file while empty.
    pub profiles: Vec<String>,
    /// The servers, in display order.
    pub servers: Vec<ServerProfile>,
    /// Offers beta-only plugins in Discover and Add from URL. Left out of
    /// the file while false.
    pub show_beta_plugins: bool,
}

impl Default for LauncherProfileDocument {
    fn default() -> Self {
        Self {
            version: CURRENT_PROFILE_VERSION,
            profiles: Vec::new(),
            servers: Vec::new(),
            show_beta_plugins: false,
        }
    }
}

impl LauncherProfileDocument {
    /// The document as indented JSON, members in OpenAC's order.
    pub fn to_json(&self) -> String {
        let servers = self.servers.iter().map(server_json).collect();
        let document = ObjectBuilder::new()
            .member("version", Out::Int(i64::from(self.version)))
            .optional(
                "profiles",
                (!self.profiles.is_empty()).then(|| Out::strings(self.profiles.iter().cloned())),
            )
            .member("servers", Out::Array(servers))
            .optional(
                "showBetaPlugins",
                self.show_beta_plugins.then_some(Out::Bool(true)),
            )
            .build();
        json::to_pretty(&document)
    }

    /// Reads a version 2 or 3 document (version 2 is version 3 without the
    /// profile list and the characters' tags and commands). Shape errors (unknown members, wrong
    /// types, a missing required member) come back as [`JsonError`]; a
    /// `null` where a list or object is required comes back as the
    /// validation message OpenAC gives for it.
    pub(crate) fn from_json(bytes: &[u8]) -> Result<Self, ReadError> {
        let root = host_json::parse(bytes)?;
        if host_json::is_null(&root) {
            return Err(ReadError::Empty);
        }
        let mut object = ObjectReader::new(&root)?;
        let version = host_json::int32(object.required("version")?)?;
        let profiles = read_list(&mut object, "profiles", &|| {
            "The profiles collection cannot be null.".to_owned()
        })?
        .unwrap_or_default();
        let servers = match object.take("servers") {
            None => Vec::new(),
            Some(node) if host_json::is_null(node) => {
                return Err(invalid("The servers collection cannot be null."));
            }
            Some(node) => host_json::array_items(node)?
                .iter()
                .map(read_server)
                .collect::<Result<Vec<_>, _>>()?,
        };
        let show_beta_plugins = match object.take("showBetaPlugins") {
            Some(node) => host_json::boolean(node)?,
            None => false,
        };
        object.finish()?;
        Ok(Self {
            version,
            profiles,
            servers,
            show_beta_plugins,
        })
    }
}

/// Why a document could not be read.
#[derive(Debug)]
pub(crate) enum ReadError {
    /// Not the document's shape.
    Json,
    /// The document is `null`.
    Empty,
    /// A value OpenAC's validation refuses.
    Invalid(ProfileError),
}

impl From<JsonError> for ReadError {
    fn from(error: JsonError) -> Self {
        let _ = error;
        Self::Json
    }
}

pub(crate) fn invalid(message: impl Into<String>) -> ReadError {
    ReadError::Invalid(ProfileError::Invalid(message.into()))
}

fn server_json(server: &ServerProfile) -> Out {
    ObjectBuilder::new()
        .member("name", Out::str(&server.name))
        .member("host", Out::str(&server.host))
        .member("port", Out::Int(i64::from(server.port)))
        .member(
            "accounts",
            Out::Array(server.accounts.iter().map(account_json).collect()),
        )
        .optional(
            "accountListInitialized",
            server.account_list_initialized.then_some(Out::Bool(true)),
        )
        .build()
}

fn account_json(account: &AccountProfile) -> Out {
    ObjectBuilder::new()
        .member("account", Out::str(&account.account))
        .member("password", Out::str(&account.password))
        .member("profiles", Out::strings(account.profiles.iter().cloned()))
        .member("plugins", Out::strings(account.plugins.iter().cloned()))
        .member(
            "loginCommands",
            Out::strings(account.login_commands.iter().cloned()),
        )
        .member(
            "characters",
            Out::Array(account.characters.iter().map(character_json).collect()),
        )
        .optional(
            "selectedCharacter",
            account.selected_character.as_deref().map(Out::str),
        )
        .optional(
            "selectedLaunchMode",
            account
                .selected_launch_mode
                .map(|mode| Out::str(mode.json_name())),
        )
        .build()
}

fn character_json(character: &CharacterProfile) -> Out {
    ObjectBuilder::new()
        .member("name", Out::str(&character.name))
        .member("id", character.id.as_deref().map_or(Out::Null, Out::str))
        .member("launchMode", Out::str(character.launch_mode.json_name()))
        .optional(
            "plugins",
            character
                .plugins
                .as_ref()
                .map(|plugins| Out::strings(plugins.iter().cloned())),
        )
        .optional(
            "profiles",
            (!character.profiles.is_empty())
                .then(|| Out::strings(character.profiles.iter().cloned())),
        )
        .optional(
            "loginCommands",
            (!character.login_commands.is_empty())
                .then(|| Out::strings(character.login_commands.iter().cloned())),
        )
        .build()
}

/// A required text member: present (else a shape error); `null` reads as
/// empty so validation names the field.
pub(crate) fn required_text(
    object: &mut ObjectReader<'_>,
    name: &'static str,
) -> Result<String, ReadError> {
    let node = object.required(name)?;
    Ok(host_json::string_or_null(node)?.unwrap_or_default())
}

/// A list of strings that may be absent (empty) but not `null`; a `null`
/// item reads as empty so validation names it.
fn read_list(
    object: &mut ObjectReader<'_>,
    member: &str,
    null_message: &dyn Fn() -> String,
) -> Result<Option<Vec<String>>, ReadError> {
    match object.take(member) {
        None => Ok(None),
        Some(node) if host_json::is_null(node) => Err(invalid(null_message())),
        Some(node) => Ok(Some(
            host_json::array_items(node)?
                .iter()
                .map(|item| Ok(host_json::string_or_null(item)?.unwrap_or_default()))
                .collect::<Result<Vec<_>, ReadError>>()?,
        )),
    }
}

fn read_server(node: &Node) -> Result<ServerProfile, ReadError> {
    if host_json::is_null(node) {
        return Err(invalid("A server entry cannot be null."));
    }
    let mut object = ObjectReader::new(node)?;
    let name = required_text(&mut object, "name")?;
    let host = required_text(&mut object, "host")?;
    let port = host_json::int32(object.required("port")?)?;
    let accounts = match object.take("accounts") {
        None => Vec::new(),
        Some(node) if host_json::is_null(node) => {
            return Err(invalid(format!(
                "The accounts collection for server '{name}' cannot be null."
            )));
        }
        Some(items) => host_json::array_items(items)?
            .iter()
            .map(|item| {
                if host_json::is_null(item) {
                    Err(invalid(format!(
                        "A null account appears under server '{name}'."
                    )))
                } else {
                    read_account(item)
                }
            })
            .collect::<Result<Vec<_>, _>>()?,
    };
    let account_list_initialized = match object.take("accountListInitialized") {
        Some(node) => host_json::boolean(node)?,
        None => false,
    };
    object.finish()?;
    Ok(ServerProfile {
        name,
        host,
        port,
        accounts,
        account_list_initialized,
    })
}

fn read_account(node: &Node) -> Result<AccountProfile, ReadError> {
    let mut object = ObjectReader::new(node)?;
    let account = required_text(&mut object, "account")?;
    let password = match host_json::string_or_null(object.required("password")?)? {
        Some(password) => password,
        None => {
            return Err(invalid(format!(
                "Password for account '{account}' cannot be null."
            )));
        }
    };
    let null_collection = || format!("Account '{account}' has a null collection.");
    let profiles = read_list(&mut object, "profiles", &null_collection)?.unwrap_or_default();
    let plugins = read_list(&mut object, "plugins", &null_collection)?.unwrap_or_default();
    let login_commands =
        read_list(&mut object, "loginCommands", &null_collection)?.unwrap_or_default();
    let characters = match object.take("characters") {
        None => Vec::new(),
        Some(node) if host_json::is_null(node) => return Err(invalid(null_collection())),
        Some(items) => host_json::array_items(items)?
            .iter()
            .map(|item| {
                if host_json::is_null(item) {
                    Err(invalid(format!(
                        "A null character appears under account '{account}'."
                    )))
                } else {
                    read_character(item)
                }
            })
            .collect::<Result<Vec<_>, _>>()?,
    };
    let selected_character = match object.take("selectedCharacter") {
        Some(node) => host_json::string_or_null(node)?,
        None => None,
    };
    let selected_launch_mode = read_launch_mode_or_null(&mut object, "selectedLaunchMode")?;
    object.finish()?;
    Ok(AccountProfile {
        account,
        password,
        profiles,
        plugins,
        login_commands,
        characters,
        selected_character,
        selected_launch_mode,
    })
}

pub(crate) fn read_launch_mode_or_null(
    object: &mut ObjectReader<'_>,
    member: &str,
) -> Result<Option<LaunchMode>, ReadError> {
    Ok(match object.take(member) {
        Some(node) if !host_json::is_null(node) => {
            Some(host_json::enumeration(node, &LaunchMode::NAMES)?)
        }
        _ => None,
    })
}

fn read_character(node: &Node) -> Result<CharacterProfile, ReadError> {
    let mut object = ObjectReader::new(node)?;
    let name = required_text(&mut object, "name")?;
    let id = match object.take("id") {
        Some(node) => host_json::string_or_null(node)?,
        None => None,
    };
    let launch_mode = match object.take("launchMode") {
        Some(node) => host_json::enumeration(node, &LaunchMode::NAMES)?,
        None => LaunchMode::GuiSelect,
    };
    // A null list is the same as an absent one: the character uses its
    // account's plugins.
    let plugins = match object.take("plugins") {
        Some(node) if !host_json::is_null(node) => Some(
            host_json::array_items(node)?
                .iter()
                .map(|item| Ok(host_json::string_or_null(item)?.unwrap_or_default()))
                .collect::<Result<Vec<_>, ReadError>>()?,
        ),
        _ => None,
    };
    let null_collection = || format!("Character '{name}' has a null collection.");
    let profiles = read_list(&mut object, "profiles", &null_collection)?.unwrap_or_default();
    let login_commands =
        read_list(&mut object, "loginCommands", &null_collection)?.unwrap_or_default();
    object.finish()?;
    Ok(CharacterProfile {
        name,
        id,
        launch_mode,
        plugins,
        profiles,
        login_commands,
    })
}
