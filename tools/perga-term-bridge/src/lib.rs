#![forbid(unsafe_code)]

//! Perga cross-platform terminal bridge daemon library skeleton (Stage 0).

/// Protocol name exposed over UDS/TCP JSON-RPC 2.0 and binary WebSocket.
pub const BRIDGE_SERVICE_NAME: &str = "perga-term-bridge";

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn service_name_matches_perga() {
        assert_eq!(BRIDGE_SERVICE_NAME, "perga-term-bridge");
    }
}
