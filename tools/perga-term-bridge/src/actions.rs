//! Input action validation and encoding against the live `InputSurface`.
//! Purified from `tools/rust-terminal/terminal_interaction_ref/actions.rs` and extended with
//! `encode_rpc_action` for `term.action` (`text` | `keys` | `paste` | `submit`).

use crate::view::{InputSurface, key_bytes};
use anyhow::{Result, ensure};
use serde_json::Value;

/// Bound on the bytes one action may write (text, paste, submit, and sequences alike).
pub const ACTION_BYTES_MAX: usize = 16384;

/// A sequence carries 2..=8 steps.
pub const SEQUENCE_STEPS_MIN: usize = 2;
pub const SEQUENCE_STEPS_MAX: usize = 8;

/// Keys a sequence step may send: cursor movement and draft editing only, so a sequence can
/// never carry a CR or an LF.
pub const SEQUENCE_KEYS: [&str; 9] = [
    "Left",
    "Right",
    "Up",
    "Down",
    "Home",
    "End",
    "Backspace",
    "Delete",
    "Ctrl+U",
];

/// What one action writes: raw bytes, or a `submit` (text plus one trailing CR, handed to the PTY
/// as two physical writes when text is non-empty).
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Encoded {
    Bytes(Vec<u8>),
    Submit(Vec<u8>),
}

impl Encoded {
    pub fn into_bytes(self) -> Vec<u8> {
        match self {
            Self::Bytes(bytes) | Self::Submit(bytes) => bytes,
        }
    }
}

fn printable_text<'a>(
    action: &'a Value,
    object: &serde_json::Map<String, Value>,
    kind: &str,
) -> Result<&'a str> {
    ensure!(
        object.len() == 2 && object.contains_key("text"),
        "{kind} action accepts only type/text"
    );
    let text = action["text"]
        .as_str()
        .ok_or_else(|| anyhow::anyhow!("text required"))?;
    ensure!(
        text.len() <= ACTION_BYTES_MAX && !text.is_empty() && !text.chars().any(char::is_control),
        "text must be nonempty printable text; use explicit keys for Enter or controls"
    );
    Ok(text)
}

fn encode_key(
    action: &Value,
    object: &serde_json::Map<String, Value>,
    surface: &InputSurface,
    allowed: Option<&[&str]>,
) -> Result<Vec<u8>> {
    ensure!(
        (2..=3).contains(&object.len())
            && object
                .keys()
                .all(|field| matches!(field.as_str(), "type" | "key" | "repeat")),
        "key action accepts only type/key/repeat"
    );
    let key = action["key"]
        .as_str()
        .ok_or_else(|| anyhow::anyhow!("key required"))?;
    if let Some(allowed) = allowed {
        ensure!(
            allowed.contains(&key),
            "a sequence step may send only {allowed:?}; send Enter, Escape, Tab or control keys as their own action"
        );
    }
    let repeat = match object.get("repeat") {
        None => 1,
        Some(value) => value
            .as_u64()
            .filter(|value| (1..=32).contains(value))
            .ok_or_else(|| anyhow::anyhow!("repeat must be an integer from 1 to 32"))?,
    };
    ensure!(
        repeat == 1
            || matches!(
                key,
                "Left" | "Right" | "Up" | "Down" | "Backspace" | "Delete"
            ),
        "only navigation and editing keys may repeat"
    );
    Ok(key_bytes(key, surface.modes)?.repeat(repeat as usize))
}

fn encode_step(step: &Value, surface: &InputSurface) -> Result<Vec<u8>> {
    let object = step
        .as_object()
        .ok_or_else(|| anyhow::anyhow!("sequence step must be an object"))?;
    match step["type"].as_str() {
        Some("text") => Ok(printable_text(step, object, "text")?.as_bytes().to_vec()),
        Some("key") => encode_key(step, object, surface, Some(&SEQUENCE_KEYS)),
        _ => anyhow::bail!("sequence steps must be text or key actions"),
    }
}

pub fn sequence_steps(action: &Value) -> Option<usize> {
    (action["type"].as_str() == Some("sequence"))
        .then(|| action["steps"].as_array().map(Vec::len))
        .flatten()
}

/// Structured action encoder preserved from `terminal_interaction_ref/actions.rs`.
pub fn encode(action: &Value, surface: &InputSurface) -> Result<Encoded> {
    let object = action
        .as_object()
        .ok_or_else(|| anyhow::anyhow!("terminal action must be an object"))?;
    let bytes = match action["type"].as_str() {
        Some("text") => Ok(printable_text(action, object, "text")?.as_bytes().to_vec()),
        Some("submit") => {
            let mut bytes = printable_text(action, object, "submit")?
                .as_bytes()
                .to_vec();
            bytes.push(b'\r');
            return Ok(Encoded::Submit(bytes));
        }
        Some("key") => encode_key(action, object, surface, None),
        Some("sequence") => {
            ensure!(
                object.len() == 2 && object.contains_key("steps"),
                "sequence action accepts only type/steps"
            );
            let steps = action["steps"]
                .as_array()
                .ok_or_else(|| anyhow::anyhow!("steps must be an array"))?;
            ensure!(
                (SEQUENCE_STEPS_MIN..=SEQUENCE_STEPS_MAX).contains(&steps.len()),
                "sequence must carry {SEQUENCE_STEPS_MIN}..{SEQUENCE_STEPS_MAX} steps"
            );
            let mut bytes = Vec::new();
            for step in steps {
                bytes.extend(encode_step(step, surface)?);
                ensure!(
                    bytes.len() <= ACTION_BYTES_MAX,
                    "sequence exceeds {ACTION_BYTES_MAX} encoded bytes"
                );
            }
            Ok(bytes)
        }
        _ => anyhow::bail!("unknown terminal action"),
    };
    bytes.map(Encoded::Bytes)
}

fn extract_string_field<'a>(payload: &'a Value, keys: &[&str]) -> Option<&'a str> {
    if let Some(s) = payload.as_str() {
        return Some(s);
    }
    let obj = payload.as_object()?;
    for key in keys {
        if let Some(s) = obj.get(*key).and_then(Value::as_str) {
            return Some(s);
        }
    }
    None
}

fn encode_rpc_key_entry(entry: &Value, surface: &InputSurface) -> Result<Vec<u8>> {
    if let Some(key) = entry.as_str() {
        return key_bytes(key, surface.modes);
    }
    let obj = entry
        .as_object()
        .ok_or_else(|| anyhow::anyhow!("key entry must be a string or object"))?;
    let key = obj
        .get("key")
        .and_then(Value::as_str)
        .ok_or_else(|| anyhow::anyhow!("key field required"))?;
    let repeat = match obj.get("repeat") {
        None => 1,
        Some(v) => v
            .as_u64()
            .filter(|n| (1..=32).contains(n))
            .ok_or_else(|| anyhow::anyhow!("repeat must be an integer from 1 to 32"))?,
    };
    Ok(key_bytes(key, surface.modes)?.repeat(repeat as usize))
}

/// Encode `term.action` JSON-RPC requests (`kind: "text" | "keys" | "paste" | "submit"`).
/// Uses `InputSurface` (`bracketed_paste`, `modes` / `MODE_KCURSOR`, `alternate`) automatically.
pub fn encode_rpc_action(kind: &str, payload: &Value, surface: &InputSurface) -> Result<Encoded> {
    match kind {
        "text" => {
            let text = extract_string_field(payload, &["text", "value", "input"])
                .ok_or_else(|| anyhow::anyhow!("text action payload must provide string 'text'"))?;
            ensure!(
                text.len() <= ACTION_BYTES_MAX,
                "text payload exceeds {ACTION_BYTES_MAX} bytes"
            );
            Ok(Encoded::Bytes(text.as_bytes().to_vec()))
        }
        "submit" => {
            let text = if payload.is_null() || payload.as_object().is_some_and(|o| o.is_empty()) {
                ""
            } else {
                extract_string_field(payload, &["text", "line", "command", "value"]).ok_or_else(
                    || anyhow::anyhow!("submit action payload must provide string 'text'"),
                )?
            };
            ensure!(
                text.len() <= ACTION_BYTES_MAX,
                "submit payload exceeds {ACTION_BYTES_MAX} bytes"
            );
            ensure!(
                !text.contains('\r') && !text.contains('\n'),
                "submit text must be a single line without CR/LF; use paste for multi-line text"
            );
            let mut bytes = Vec::with_capacity(text.len() + 1);
            bytes.extend_from_slice(text.as_bytes());
            bytes.push(b'\r');
            Ok(Encoded::Submit(bytes))
        }
        "keys" => {
            let mut out = Vec::new();
            if let Some(single) = payload.as_str() {
                out.extend(key_bytes(single, surface.modes)?);
            } else if let Some(arr) = payload.as_array() {
                ensure!(!arr.is_empty(), "keys array must not be empty");
                for item in arr {
                    out.extend(encode_rpc_key_entry(item, surface)?);
                    ensure!(
                        out.len() <= ACTION_BYTES_MAX,
                        "keys payload exceeds {ACTION_BYTES_MAX} encoded bytes"
                    );
                }
            } else if let Some(obj) = payload.as_object() {
                if let Some(arr) = obj.get("keys").and_then(Value::as_array) {
                    ensure!(!arr.is_empty(), "keys array must not be empty");
                    for item in arr {
                        out.extend(encode_rpc_key_entry(item, surface)?);
                        ensure!(
                            out.len() <= ACTION_BYTES_MAX,
                            "keys payload exceeds {ACTION_BYTES_MAX} encoded bytes"
                        );
                    }
                } else if obj.contains_key("key") {
                    out.extend(encode_rpc_key_entry(payload, surface)?);
                } else if let Some(steps) = obj.get("steps").and_then(Value::as_array) {
                    ensure!(
                        (SEQUENCE_STEPS_MIN..=SEQUENCE_STEPS_MAX).contains(&steps.len()),
                        "sequence must carry {SEQUENCE_STEPS_MIN}..{SEQUENCE_STEPS_MAX} steps"
                    );
                    for step in steps {
                        out.extend(encode_step(step, surface)?);
                        ensure!(
                            out.len() <= ACTION_BYTES_MAX,
                            "sequence exceeds {ACTION_BYTES_MAX} encoded bytes"
                        );
                    }
                } else {
                    anyhow::bail!("keys payload must provide 'keys' array or 'key' field");
                }
            } else {
                anyhow::bail!("keys payload must be string, array, or object");
            }
            ensure!(
                out.len() <= ACTION_BYTES_MAX,
                "keys payload exceeds {ACTION_BYTES_MAX} encoded bytes"
            );
            Ok(Encoded::Bytes(out))
        }
        "paste" => {
            let text =
                extract_string_field(payload, &["text", "content", "value"]).ok_or_else(|| {
                    anyhow::anyhow!("paste action payload must provide string 'text'")
                })?;
            ensure!(
                text.len() <= ACTION_BYTES_MAX,
                "paste payload exceeds {ACTION_BYTES_MAX} bytes"
            );
            ensure!(
                !text.contains("\x1b[200~") && !text.contains("\x1b[201~"),
                "paste payload must not contain bracketed-paste delimiter escapes"
            );
            let normalized = text.replace("\r\n", "\n");
            let encoded = if surface.bracketed_paste() {
                let mut wrapped = Vec::with_capacity(normalized.len() + 12);
                wrapped.extend_from_slice(b"\x1b[200~");
                wrapped.extend_from_slice(normalized.as_bytes());
                wrapped.extend_from_slice(b"\x1b[201~");
                wrapped
            } else if surface.alternate {
                normalized.replace('\n', "\r").into_bytes()
            } else {
                normalized.into_bytes()
            };
            Ok(Encoded::Bytes(encoded))
        }
        _ => anyhow::bail!(
            "unsupported term.action kind {kind:?}; expected text, keys, paste, or submit"
        ),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::view::TerminalView;
    use serde_json::json;

    fn surface() -> InputSurface {
        TerminalView::new(80, 24, [220; 3], [20; 3])
            .unwrap()
            .frame(0)
            .unwrap()
            .input_surface()
    }

    #[test]
    fn submit_encodes_text_and_one_cr_and_rejects_repeat() {
        let surface = surface();
        assert_eq!(
            encode(&json!({"type":"submit","text":"ls -la"}), &surface)
                .unwrap()
                .into_bytes(),
            b"ls -la\r".to_vec()
        );
        assert_eq!(
            encode(&json!({"type":"text","text":"ls -la"}), &surface)
                .unwrap()
                .into_bytes(),
            b"ls -la".to_vec()
        );
        for invalid in [
            json!({"type":"submit","text":"x","repeat":2}),
            json!({"type":"submit","text":""}),
            json!({"type":"submit","text":"a\nb"}),
            json!({"type":"submit"}),
            json!({"type":"submit","text":"x".repeat(16385)}),
        ] {
            assert!(encode(&invalid, &surface).is_err(), "{invalid}");
        }
    }

    #[test]
    fn sequence_encodes_steps_in_order_and_rejects_submission_keys() {
        let surface = surface();
        let edit = json!({"type":"sequence","steps":[
            {"type":"text","text":"7200 + 19"},
            {"type":"key","key":"Left","repeat":5},
            {"type":"key","key":"Backspace"},
            {"type":"text","text":"9"}
        ]});
        assert_eq!(
            encode(&edit, &surface).unwrap().into_bytes(),
            b"7200 + 19\x1b[D\x1b[D\x1b[D\x1b[D\x1b[D\x7f9".to_vec()
        );
        assert_eq!(sequence_steps(&edit), Some(4));
        for key in SEQUENCE_KEYS {
            let steps = json!({"type":"sequence","steps":[{"type":"key","key":key},{"type":"text","text":"x"}]});
            let bytes = encode(&steps, &surface).unwrap().into_bytes();
            assert!(!bytes.contains(&b'\r') && !bytes.contains(&b'\n'));
        }
    }

    #[test]
    fn rpc_action_respects_bracketed_paste_kcursor_and_alternate_modes() {
        let mut view = TerminalView::new(80, 24, [220; 3], [20; 3]).unwrap();
        let plain_surface = view.input_surface();
        assert_eq!(
            encode_rpc_action("paste", &json!({"text": "line1\r\nline2"}), &plain_surface)
                .unwrap()
                .into_bytes(),
            b"line1\nline2".to_vec()
        );

        view.feed(b"\x1b[?2004h\x1b[?1h");
        let bp_surface = view.input_surface();
        assert!(bp_surface.bracketed_paste());
        assert_eq!(
            encode_rpc_action("paste", &json!({"text": "line1\nline2"}), &bp_surface)
                .unwrap()
                .into_bytes(),
            b"\x1b[200~line1\nline2\x1b[201~".to_vec()
        );
        assert!(
            encode_rpc_action("paste", &json!({"text": "evil\x1b[201~rm"}), &bp_surface).is_err()
        );
        assert_eq!(
            encode_rpc_action("keys", &json!({"keys": ["Up", "Enter"]}), &bp_surface)
                .unwrap()
                .into_bytes(),
            b"\x1bOA\r".to_vec()
        );

        view.feed(b"\x1b[?2004l\x1b[?1049h");
        let alt_surface = view.input_surface();
        assert!(!alt_surface.bracketed_paste() && alt_surface.alternate);
        assert_eq!(
            encode_rpc_action("paste", &json!({"text": "a\nb"}), &alt_surface)
                .unwrap()
                .into_bytes(),
            b"a\rb".to_vec()
        );
        assert_eq!(
            encode_rpc_action("submit", &json!({"text": "echo ok"}), &alt_surface).unwrap(),
            Encoded::Submit(b"echo ok\r".to_vec())
        );
    }
}
