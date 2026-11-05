//! What an attached window's pre-game screens read: the connection's
//! progress (the connecting screen), character selection and creation
//! (their snapshots), the creation choices, and the refusals of the
//! window's own lifecycle requests.
//!
//! The session sends these with the attach snapshot and then whenever they
//! change; the window keeps the latest of each and rebuilds its screens
//! from them. Nothing here touches the network.

use std::sync::Arc;
use std::time::Instant;

use ac_core::chargen::ChargenOptions;

use super::creation::{CharacterCreation, CreationSnapshot};
use super::flow::{Phase, SessionError};
use super::generation::Generation;
use super::selection::{CharacterSelection, SelectionSnapshot};

/// Where the connection is, as the connecting screen shows it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ConnectionStatus {
    /// No connection attempt.
    #[default]
    Inactive,
    /// Logging in.
    Connecting,
    /// Connected; the server checks the game data.
    CheckingData,
    /// The character list and the data check are in.
    Ready,
    /// The server wants a game-data update this client cannot apply.
    Unsupported,
    /// The connection failed.
    Failed,
}

/// The connecting screen's view of the connection.
#[derive(Debug, Clone, PartialEq)]
pub struct ConnectionView {
    /// Where the connection is.
    pub status: ConnectionStatus,
    /// The connection meter, 0..=1.
    pub connection_progress: f32,
    /// The data-update meter, 0..=1.
    pub update_progress: f32,
    /// Why it failed (or why the update cannot be applied).
    pub error: Option<String>,
    /// When this connection attempt began.
    pub started: Instant,
    /// The session enters its configured character by itself: the
    /// connecting and selection screens stay out of the way while it does.
    pub direct_launch: bool,
}

impl ConnectionView {
    /// The view after the connection moved to `status`: the connection
    /// meter fills once connected (and keeps its value on a failure), the
    /// update meter only when ready.
    #[must_use]
    pub fn advance(
        previous: Option<&Self>,
        status: ConnectionStatus,
        error: Option<String>,
        started: Instant,
        direct_launch: bool,
    ) -> Self {
        let connection_progress = match status {
            ConnectionStatus::CheckingData
            | ConnectionStatus::Ready
            | ConnectionStatus::Unsupported => 1.0,
            _ => previous.map_or(0.0, |p| p.connection_progress),
        };
        Self {
            status,
            connection_progress,
            update_progress: if status == ConnectionStatus::Ready {
                1.0
            } else {
                0.0
            },
            error,
            started,
            direct_launch,
        }
    }
}

/// The connecting screen's status for a session in `phase`. The session's
/// loop starts once the transport is up, so logging in is the data check;
/// a failure that is the data update is "unsupported".
#[must_use]
pub fn connection_status(phase: Phase, failure: Option<&SessionError>) -> ConnectionStatus {
    match phase {
        Phase::Connecting => ConnectionStatus::CheckingData,
        Phase::InCharacterSelect
        | Phase::EnteringWorld
        | Phase::InWorld
        | Phase::LoggingOff
        | Phase::LoggedOff => ConnectionStatus::Ready,
        Phase::Failed => match failure {
            Some(SessionError::DataUpdateRequired) => ConnectionStatus::Unsupported,
            _ => ConnectionStatus::Failed,
        },
    }
}

/// One pre-game message to an attached window.
#[derive(Debug, Clone, PartialEq)]
pub enum PreGameEvent {
    /// The connection's progress.
    Connection(ConnectionView),
    /// Character selection.
    Selection(Box<SelectionSnapshot>),
    /// Character creation.
    Creation(Box<CreationSnapshot>),
    /// The creation choices (the dats' character-generation table), sent
    /// once at attach.
    Options(Arc<ChargenOptions>),
    /// A selection or creation request was refused; the text says why.
    Refused(String),
}

/// The session's pre-game state as of now.
#[derive(Debug, Clone, Copy)]
pub struct PreGameSource<'a> {
    /// The connection's status.
    pub status: ConnectionStatus,
    /// Why it failed.
    pub error: Option<&'a str>,
    /// When this connection attempt began.
    pub started: Instant,
    /// The configured character enters by itself.
    pub direct_launch: bool,
    /// The session's generation.
    pub generation: Generation,
    /// Character selection.
    pub selection: &'a CharacterSelection,
    /// Character creation.
    pub creation: &'a CharacterCreation,
}

/// What the attached window was last told, so only changes go out.
#[derive(Debug, Default)]
pub struct PreGamePresenter {
    connection: Option<ConnectionView>,
    selection: Option<(Generation, u64)>,
    creation: Option<(Generation, u64)>,
}

impl PreGamePresenter {
    /// Everything, for a window that just attached.
    pub fn snapshot(
        &mut self,
        source: &PreGameSource<'_>,
        options: Option<&Arc<ChargenOptions>>,
    ) -> Vec<PreGameEvent> {
        *self = Self::default();
        let mut events: Vec<PreGameEvent> = options
            .map(|o| PreGameEvent::Options(Arc::clone(o)))
            .into_iter()
            .collect();
        events.extend(self.changes(source));
        events
    }

    /// What changed since the window was last told. A snapshot is taken
    /// only when its revision moved.
    pub fn changes(&mut self, source: &PreGameSource<'_>) -> Vec<PreGameEvent> {
        let mut events = Vec::new();
        let connection = ConnectionView::advance(
            self.connection.as_ref(),
            source.status,
            source.error.map(str::to_owned),
            source.started,
            source.direct_launch,
        );
        if self.connection.as_ref() != Some(&connection) {
            self.connection = Some(connection.clone());
            events.push(PreGameEvent::Connection(connection));
        }
        let selection = (source.generation, source.selection.revision());
        if self.selection != Some(selection) {
            self.selection = Some(selection);
            events.push(PreGameEvent::Selection(Box::new(
                source.selection.snapshot(),
            )));
        }
        let creation = (source.generation, source.creation.revision());
        if self.creation != Some(creation) {
            self.creation = Some(creation);
            events.push(PreGameEvent::Creation(Box::new(source.creation.snapshot())));
        }
        events
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::session::creation::SeededRandom;

    #[test]
    fn the_meters_follow_the_connection_as_the_runtime_reports_it() {
        // OpenAC `RuntimeConnectionState.Apply`: connected (checking,
        // ready, unsupported) fills the connection meter; a failure keeps
        // it; only ready fills the update meter.
        let t = Instant::now();
        let connecting =
            ConnectionView::advance(None, ConnectionStatus::Connecting, None, t, false);
        assert_eq!(
            (connecting.connection_progress, connecting.update_progress),
            (0.0, 0.0)
        );
        let checking = ConnectionView::advance(
            Some(&connecting),
            ConnectionStatus::CheckingData,
            None,
            t,
            false,
        );
        assert_eq!(
            (checking.connection_progress, checking.update_progress),
            (1.0, 0.0)
        );
        let failed = ConnectionView::advance(
            Some(&checking),
            ConnectionStatus::Failed,
            Some("gone".into()),
            t,
            false,
        );
        assert_eq!(
            (failed.connection_progress, failed.update_progress),
            (1.0, 0.0)
        );
        assert_eq!(failed.error.as_deref(), Some("gone"));
        let ready =
            ConnectionView::advance(Some(&checking), ConnectionStatus::Ready, None, t, false);
        assert_eq!(
            (ready.connection_progress, ready.update_progress),
            (1.0, 1.0)
        );
        let unsupported =
            ConnectionView::advance(None, ConnectionStatus::Unsupported, None, t, false);
        assert_eq!(
            (unsupported.connection_progress, unsupported.update_progress),
            (1.0, 0.0)
        );
    }

    #[test]
    fn a_phase_is_a_connection_status() {
        assert_eq!(
            connection_status(Phase::Connecting, None),
            ConnectionStatus::CheckingData
        );
        for phase in [
            Phase::InCharacterSelect,
            Phase::EnteringWorld,
            Phase::InWorld,
            Phase::LoggingOff,
            Phase::LoggedOff,
        ] {
            assert_eq!(connection_status(phase, None), ConnectionStatus::Ready);
        }
        assert_eq!(
            connection_status(Phase::Failed, Some(&SessionError::DataUpdateRequired)),
            ConnectionStatus::Unsupported
        );
        assert_eq!(
            connection_status(Phase::Failed, Some(&SessionError::EnterWorldTimeout)),
            ConnectionStatus::Failed
        );
    }

    #[test]
    fn the_window_hears_everything_at_attach_then_only_changes() {
        let t = Instant::now();
        let mut selection = CharacterSelection::new();
        let mut creation = CharacterCreation::new(
            Arc::new(ChargenOptions::default()),
            Box::new(SeededRandom::new(1)),
        );
        let options = Arc::new(ChargenOptions::default());
        let mut presenter = PreGamePresenter::default();
        let first = Generation::default();
        fn source<'a>(
            status: ConnectionStatus,
            generation: Generation,
            started: Instant,
            selection: &'a CharacterSelection,
            creation: &'a CharacterCreation,
        ) -> PreGameSource<'a> {
            PreGameSource {
                status,
                error: None,
                started,
                direct_launch: true,
                generation,
                selection,
                creation,
            }
        }
        let all = presenter.snapshot(
            &source(
                ConnectionStatus::CheckingData,
                first,
                t,
                &selection,
                &creation,
            ),
            Some(&options),
        );
        assert_eq!(all.len(), 4, "options, connection, selection, creation");
        assert!(matches!(&all[0], PreGameEvent::Options(_)));
        assert!(matches!(&all[1], PreGameEvent::Connection(v) if v.direct_launch));
        let same = presenter.changes(&source(
            ConnectionStatus::CheckingData,
            first,
            t,
            &selection,
            &creation,
        ));
        assert!(same.is_empty(), "nothing changed");

        let generation = first.next();
        selection.begin(generation);
        creation.begin(generation);
        let changed = presenter.changes(&source(
            ConnectionStatus::Ready,
            generation,
            t,
            &selection,
            &creation,
        ));
        assert_eq!(changed.len(), 3);
        assert!(
            matches!(&changed[0], PreGameEvent::Connection(v) if v.status == ConnectionStatus::Ready)
        );
        assert!(matches!(&changed[1], PreGameEvent::Selection(_)));
        assert!(matches!(&changed[2], PreGameEvent::Creation(_)));
    }
}
