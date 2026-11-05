//! Turns a version 1 profile file into the current shape. Version 1 kept a
//! plugin list and logon commands on every character and, optionally, one
//! user list shared by every server; the current shape keeps plugins and
//! commands on the account (a character may still have its own plugin
//! list) and accounts per server.

use ac_host_core::json::{self as host_json, Node, ObjectReader};

use super::account_name::find_account;
use super::document::{
    AccountProfile, CharacterProfile, LaunchMode, LauncherProfileDocument, ReadError,
    ServerProfile, invalid, read_launch_mode_or_null, required_text,
};
use super::store::CURRENT_PROFILE_VERSION;

struct Version1Character {
    name: String,
    id: Option<String>,
    launch_mode: LaunchMode,
    plugins: Option<Vec<String>>,
    login_commands: Option<Vec<String>>,
}

struct Version1Account {
    account: String,
    password: String,
    characters: Option<Vec<Option<Version1Character>>>,
    selected_character: Option<String>,
    selected_launch_mode: Option<LaunchMode>,
}

struct Version1Server {
    name: String,
    host: String,
    port: i32,
    accounts: Option<Vec<Option<Version1Account>>>,
}

struct Version1User {
    account: String,
    password: String,
}

struct Version1Document {
    servers: Option<Vec<Option<Version1Server>>>,
    users: Option<Vec<Version1User>>,
    show_beta_plugins: bool,
}

/// Reads a version 1 document and returns it in the current shape, with a
/// notice for every logon command list that could not be kept as it was.
pub(crate) fn from_version1(
    bytes: &[u8],
) -> Result<(LauncherProfileDocument, Vec<String>), ReadError> {
    let root = host_json::parse(bytes)?;
    if host_json::is_null(&root) {
        return Err(invalid("The version 1 profile file is empty."));
    }
    let document = read_document(&root)?;
    let Some(servers) = document.servers else {
        return Err(invalid("The servers collection cannot be null."));
    };
    let mut notices = Vec::new();
    let mut result = LauncherProfileDocument {
        version: CURRENT_PROFILE_VERSION,
        servers: Vec::new(),
        show_beta_plugins: document.show_beta_plugins,
    };
    for server in servers {
        let Some(server) = server else {
            return Err(invalid("A server entry cannot be null."));
        };
        let Some(mut accounts) = server.accounts else {
            return Err(invalid(format!(
                "The accounts collection for server '{}' cannot be null.",
                server.name
            )));
        };
        if let Some(users) = &document.users {
            // Version 1 gave every server exactly the shared user list; do
            // the same here so the server shows the accounts it showed.
            accounts = users
                .iter()
                .map(|user| {
                    // The user list's spelling is kept; the server's entry
                    // for that account may be spelled in another case.
                    let existing = find_account(
                        &accounts,
                        |account| account.as_ref().map_or("", |account| &account.account),
                        &user.account,
                    )
                    .and_then(|index| accounts[index].as_ref());
                    Some(Version1Account {
                        account: user.account.clone(),
                        password: user.password.clone(),
                        characters: Some(
                            existing
                                .and_then(|account| account.characters.as_ref())
                                .map(|characters| {
                                    characters.iter().map(|c| c.as_ref().map(copy)).collect()
                                })
                                .unwrap_or_default(),
                        ),
                        selected_character: existing
                            .and_then(|account| account.selected_character.clone()),
                        selected_launch_mode: existing
                            .and_then(|account| account.selected_launch_mode),
                    })
                })
                .collect();
        }
        let accounts = accounts
            .into_iter()
            .map(|account| migrate_account(&server.name, account, &mut notices))
            .collect::<Result<Vec<_>, _>>()?;
        result.servers.push(ServerProfile {
            name: server.name,
            host: server.host,
            port: server.port,
            accounts,
            account_list_initialized: false,
        });
    }
    Ok((result, notices))
}

fn copy(character: &Version1Character) -> Version1Character {
    Version1Character {
        name: character.name.clone(),
        id: character.id.clone(),
        launch_mode: character.launch_mode,
        plugins: character.plugins.clone(),
        login_commands: character.login_commands.clone(),
    }
}

fn migrate_account(
    server_name: &str,
    account: Option<Version1Account>,
    notices: &mut Vec<String>,
) -> Result<AccountProfile, ReadError> {
    let Some(account) = account else {
        return Err(invalid(format!(
            "A null account appears under server '{server_name}'."
        )));
    };
    let Some(characters) = account.characters else {
        return Err(invalid(format!(
            "The characters collection for account '{}' cannot be null.",
            account.account
        )));
    };
    if characters.iter().any(Option::is_none) {
        return Err(invalid(format!(
            "A null character appears under account '{}'.",
            account.account
        )));
    }
    let present: Vec<Version1Character> = characters.into_iter().flatten().collect();
    if present
        .iter()
        .any(|character| character.plugins.is_none() || character.login_commands.is_none())
    {
        return Err(invalid(format!(
            "A character under account '{}' has a null settings collection.",
            account.account
        )));
    }
    let union = union_of_plugins(&present);
    let commands: Vec<String> = present
        .first()
        .and_then(|character| character.login_commands.clone())
        .unwrap_or_default();
    for character in present.iter().skip(1) {
        let own = character.login_commands.clone().unwrap_or_default();
        if own != commands {
            notices.push(describe_dropped_commands(
                server_name,
                &account.account,
                &present[0].name,
                &character.name,
                &own,
            ));
        }
    }
    Ok(AccountProfile {
        account: account.account,
        password: account.password,
        profiles: Vec::new(),
        login_commands: commands,
        selected_character: account.selected_character,
        selected_launch_mode: account.selected_launch_mode,
        characters: present
            .iter()
            .map(|character| {
                let own = without_none(character.plugins.as_deref());
                CharacterProfile {
                    name: character.name.clone(),
                    id: character.id.clone(),
                    launch_mode: character.launch_mode,
                    plugins: (!same_plugins(&own, &union)).then_some(own),
                }
            })
            .collect(),
        plugins: union,
    })
}

/// Every plugin any character had, in the order first seen. The literal
/// "none" an early launcher wrote for an empty list is not a plugin.
fn union_of_plugins(characters: &[Version1Character]) -> Vec<String> {
    let mut union: Vec<String> = Vec::new();
    for character in characters {
        for id in without_none(character.plugins.as_deref()) {
            if !union.iter().any(|known| known.eq_ignore_ascii_case(&id)) {
                union.push(id);
            }
        }
    }
    union
}

fn without_none(plugins: Option<&[String]>) -> Vec<String> {
    plugins
        .unwrap_or_default()
        .iter()
        .filter(|id| !id.eq_ignore_ascii_case("none"))
        .cloned()
        .collect()
}

/// The same plugins in the same order: plugins load in list order, so a
/// different order is a different list.
fn same_plugins(own: &[String], union: &[String]) -> bool {
    own.len() == union.len()
        && own
            .iter()
            .zip(union)
            .all(|(left, right)| left.eq_ignore_ascii_case(right))
}

fn describe_dropped_commands(
    server_name: &str,
    account_name: &str,
    kept_character: &str,
    character: &str,
    commands: &[String],
) -> String {
    if commands.is_empty() {
        return format!(
            "{account_name} on {server_name}: {character} had no logon commands; it now runs \
             {kept_character}'s."
        );
    }
    let mut text = format!(
        "{account_name} on {server_name}: {character}'s logon commands were not kept (the \
         account now runs {kept_character}'s):"
    );
    for command in commands {
        text.push_str("\n    ");
        text.push_str(command);
    }
    text
}

fn read_document(node: &Node) -> Result<Version1Document, ReadError> {
    let mut object = ObjectReader::new(node)?;
    host_json::int32(object.required("version")?)?;
    let servers = match object.take("servers") {
        None => Some(Vec::new()),
        Some(node) if host_json::is_null(node) => None,
        Some(node) => Some(
            host_json::array_items(node)?
                .iter()
                .map(|item| {
                    if host_json::is_null(item) {
                        Ok(None)
                    } else {
                        read_server(item).map(Some)
                    }
                })
                .collect::<Result<Vec<_>, ReadError>>()?,
        ),
    };
    let users = match object.take("users") {
        Some(node) if !host_json::is_null(node) => Some(
            host_json::array_items(node)?
                .iter()
                .map(read_user)
                .collect::<Result<Vec<_>, ReadError>>()?,
        ),
        _ => None,
    };
    let show_beta_plugins = match object.take("showBetaPlugins") {
        Some(node) => host_json::boolean(node)?,
        None => false,
    };
    object.finish()?;
    Ok(Version1Document {
        servers,
        users,
        show_beta_plugins,
    })
}

fn read_user(node: &Node) -> Result<Version1User, ReadError> {
    let mut object = ObjectReader::new(node)?;
    let account = required_text(&mut object, "account")?;
    let password = required_text(&mut object, "password")?;
    object.finish()?;
    Ok(Version1User { account, password })
}

fn read_server(node: &Node) -> Result<Version1Server, ReadError> {
    let mut object = ObjectReader::new(node)?;
    let name = required_text(&mut object, "name")?;
    let host = required_text(&mut object, "host")?;
    let port = host_json::int32(object.required("port")?)?;
    let accounts = match object.take("accounts") {
        None => Some(Vec::new()),
        Some(node) if host_json::is_null(node) => None,
        Some(node) => Some(
            host_json::array_items(node)?
                .iter()
                .map(|item| {
                    if host_json::is_null(item) {
                        Ok(None)
                    } else {
                        read_account(item).map(Some)
                    }
                })
                .collect::<Result<Vec<_>, ReadError>>()?,
        ),
    };
    object.finish()?;
    Ok(Version1Server {
        name,
        host,
        port,
        accounts,
    })
}

fn read_account(node: &Node) -> Result<Version1Account, ReadError> {
    let mut object = ObjectReader::new(node)?;
    let account = required_text(&mut object, "account")?;
    let Some(password) = host_json::string_or_null(object.required("password")?)? else {
        return Err(invalid(format!(
            "Password for account '{account}' cannot be null."
        )));
    };
    let characters = match object.take("characters") {
        None => Some(Vec::new()),
        Some(node) if host_json::is_null(node) => None,
        Some(node) => Some(
            host_json::array_items(node)?
                .iter()
                .map(|item| {
                    if host_json::is_null(item) {
                        Ok(None)
                    } else {
                        read_character(item).map(Some)
                    }
                })
                .collect::<Result<Vec<_>, ReadError>>()?,
        ),
    };
    let selected_character = match object.take("selectedCharacter") {
        Some(node) => host_json::string_or_null(node)?,
        None => None,
    };
    let selected_launch_mode = read_launch_mode_or_null(&mut object, "selectedLaunchMode")?;
    object.finish()?;
    Ok(Version1Account {
        account,
        password,
        characters,
        selected_character,
        selected_launch_mode,
    })
}

fn read_strings(node: Option<&Node>) -> Result<Option<Vec<String>>, ReadError> {
    match node {
        None => Ok(Some(Vec::new())),
        Some(node) if host_json::is_null(node) => Ok(None),
        Some(node) => Ok(Some(
            host_json::array_items(node)?
                .iter()
                .map(|item| Ok(host_json::string_or_null(item)?.unwrap_or_default()))
                .collect::<Result<Vec<_>, ReadError>>()?,
        )),
    }
}

fn read_character(node: &Node) -> Result<Version1Character, ReadError> {
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
    let plugins = read_strings(object.take("plugins"))?;
    let login_commands = read_strings(object.take("loginCommands"))?;
    object.finish()?;
    Ok(Version1Character {
        name,
        id,
        launch_mode,
        plugins,
        login_commands,
    })
}
