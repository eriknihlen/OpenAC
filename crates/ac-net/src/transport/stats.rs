/// Running totals of transport events, for diagnostics and loss gates.
/// Counters only ever increase.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TransportStats {
    /// Packets that passed admission and checksum.
    pub packets_received: u64,
    /// Datagrams the transport emitted (new, resent, acks, NAKs).
    pub packets_sent: u64,
    /// Datagrams re-emitted because the server asked (NAK).
    pub resends_sent: u64,
    /// Server NAK packets received.
    pub nak_requests_received: u64,
    /// Ids the server NAKed that were no longer cached (keystream-health
    /// invariant: stays zero on a healthy link).
    pub uncached_nak_ids: u64,
    /// Acknowledgements folded into the outbound watermark (explicit acks
    /// plus each NAK's first id).
    pub acks_consumed: u64,
    /// Inbound encrypted duplicates dropped before any keystream use.
    pub inbound_dups_dropped: u64,
    /// Inbound packets dropped for being implausibly far ahead
    /// (keystream-health invariant).
    pub inbound_sanity_drops: u64,
    /// Inbound packets whose checksum did not verify (keystream-health
    /// invariant).
    pub checksum_failures: u64,
    /// Keystream words parked beside a missing (or failed) sequence.
    pub keys_parked: u64,
    /// Cumulative acks we sent.
    pub acks_sent: u64,
    /// NAK packets we sent.
    pub naks_sent: u64,
    /// Ids carried by the NAKs we sent.
    pub nak_ids_sent: u64,
    /// Parked words moved to the reclaim pool after a cleartext reject.
    pub reject_words_reclaimed: u64,
    /// Server reject (will-not-resend) packets received.
    pub rejects_received: u64,
}
