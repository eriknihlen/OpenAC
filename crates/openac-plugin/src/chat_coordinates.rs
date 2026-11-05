//! Map coordinates in chat (OpenAC `PluginChatCoordinateParser`,
//! `PluginChatCoordinateLinkRouter`): find compass pairs such as
//! `42.1N 33.5E` in ordinary text, and route the coordinate links the
//! player clicks in the transcript to a plugin's destination.

use ac_plugin_abi::Event;

/// A map coordinate: east-west and north-south, west and south negative
/// (OpenAC `PluginChatCoordinate`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Coordinate {
    /// East-west; west is negative.
    pub east_west: f64,
    /// North-south; south is negative.
    pub north_south: f64,
}

/// Every unambiguous coordinate pair in `text`, in the order they appear;
/// a pair is one north-or-south and one east-or-west value, in either
/// order (`42.1N 33.5E`, `33.5E, 42.1N`). Neither half may sit inside a
/// longer word or number: `10n 20east` and `v2.1N 3E` hold no pair. The
/// client links exactly these pairs in the text a chat line shows, in every
/// line but combat text.
#[must_use]
pub fn parse_all(text: &str) -> Vec<Coordinate> {
    ac_plugin_abi::chat_coordinates::find(text)
        .into_iter()
        .map(|pair| Coordinate {
            east_west: pair.east_west,
            north_south: pair.north_south,
        })
        .collect()
}

/// The one coordinate pair in `text`; none for no pair or for more than
/// one (ambiguous).
#[must_use]
pub fn try_parse(text: &str) -> Option<Coordinate> {
    match parse_all(text).as_slice() {
        [one] => Some(*one),
        _ => None,
    }
}

/// The coordinate a clicked chat link carries, if it is a coordinate link.
#[must_use]
pub fn clicked_coordinate(event: &Event) -> Option<Coordinate> {
    match event {
        Event::ChatLinkClicked(link) if link.kind == 0 => {
            link.coordinate.map(|(east_west, north_south)| Coordinate {
                east_west,
                north_south,
            })
        }
        _ => None,
    }
}

/// Routes the coordinate links the player clicks to a destination until
/// stopped (OpenAC `PluginChatCoordinateLinkRouter`). Hand it every event.
pub struct CoordinateLinkRouter<F: FnMut(Coordinate)> {
    destination: F,
    stopped: bool,
}

impl<F: FnMut(Coordinate)> std::fmt::Debug for CoordinateLinkRouter<F> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CoordinateLinkRouter")
            .field("stopped", &self.stopped)
            .finish_non_exhaustive()
    }
}

impl<F: FnMut(Coordinate)> CoordinateLinkRouter<F> {
    /// Starts routing to `destination`.
    pub fn new(destination: F) -> Self {
        Self {
            destination,
            stopped: false,
        }
    }

    /// A coordinate link's click goes to the destination; true when it
    /// did.
    pub fn handle(&mut self, event: &Event) -> bool {
        if self.stopped {
            return false;
        }
        match clicked_coordinate(event) {
            Some(coordinate) => {
                (self.destination)(coordinate);
                true
            }
            None => false,
        }
    }

    /// Stops routing: clicks after this reach nothing.
    pub fn stop(&mut self) {
        self.stopped = true;
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.Plugin.Tests/PluginChatCoordinateWorkflowTests.cs`.
    use super::*;
    use ac_plugin_abi::ChatLinkClicked;

    fn close(a: f64, b: f64) -> bool {
        (a - b).abs() < 1e-10
    }

    #[test]
    fn the_parser_takes_both_compass_orders_and_the_separators() {
        for (text, east_west, north_south) in [
            ("42.1N, 33.6E", 33.6, 42.1),
            ("42.1 S / 33.6 W", -33.6, -42.1),
            ("33.6E 42.1N", 33.6, 42.1),
        ] {
            let c = try_parse(text).expect(text);
            assert!(
                close(c.east_west, east_west) && close(c.north_south, north_south),
                "{text}"
            );
        }
    }

    #[test]
    fn every_pair_is_returned_and_a_single_parse_refuses_ambiguity() {
        assert_eq!(parse_all("A 1N, 2E then 3S; 4W").len(), 2);
        assert_eq!(try_parse("1N, 2E and 3N, 4E"), None);
        assert_eq!(try_parse("no place here"), None);
        // Two latitudes are not a pair; the letters may be lower case.
        assert!(parse_all("1N 2N").is_empty());
        assert_eq!(
            try_parse("meet at 12.5n,3w"),
            Some(Coordinate {
                east_west: -3.0,
                north_south: 12.5
            })
        );
    }

    #[test]
    fn the_parser_does_not_read_a_pair_inside_a_word_but_does_between_punctuation() {
        // OpenAC PR #224 CoordinateParserDoesNotReadAPairInsideAWord,
        // CoordinateParserReadsAPairBetweenPunctuation.
        for text in ["10n 20east", "10north 20e", "v2.1N 3E"] {
            assert!(parse_all(text).is_empty(), "{text}");
        }
        assert_eq!(
            try_parse("Your corpse is located at (42.1N, 33.5E)."),
            Some(Coordinate {
                east_west: 33.5,
                north_south: 42.1
            })
        );
    }

    fn click(kind: u8, display: &str, coordinate: Option<(f64, f64)>) -> Event {
        Event::ChatLinkClicked(ChatLinkClicked {
            kind,
            display_text: display.to_owned(),
            coordinate,
        })
    }

    #[test]
    fn the_link_router_forwards_coordinates_until_stopped() {
        let mut received = Vec::new();
        {
            let mut router = CoordinateLinkRouter::new(|c| received.push(c));
            assert!(router.handle(&click(0, "1N, 2E", Some((2.0, 1.0)))));
            assert!(!router.handle(&Event::LoginComplete));
            router.stop();
            assert!(!router.handle(&click(0, "3N, 4E", Some((4.0, 3.0)))));
        }
        assert_eq!(
            received,
            [Coordinate {
                east_west: 2.0,
                north_south: 1.0
            }]
        );
    }
}
