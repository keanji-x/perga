//! Pure terminal client screen projection and input surface encoding.
//! Purified from the read-only seed `tools/rust-terminal/calm-terminal-view/src/lib.rs`.

use anyhow::{Result, ensure};
use rmux_core::{COLOUR_DEFAULT, ScreenLineView, TerminalScreen, input::mode};
use rmux_proto::TerminalSize;
use serde::Serialize;

pub const MAX_COLS: u16 = 512;
pub const MAX_ROWS: u16 = 256;
const MAX_FRAME_TEXT: usize = 512 * 1024;

#[derive(Clone, Debug, Serialize)]
pub struct Cell {
    pub text: String,
    pub width: u8,
    pub attributes: u16,
    pub foreground: i32,
    pub background: i32,
}

impl Cell {
    /// What rmux renders for a cell nothing was written to.
    fn blank() -> Self {
        Self {
            text: " ".into(),
            width: 1,
            attributes: 0,
            foreground: COLOUR_DEFAULT,
            background: COLOUR_DEFAULT,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct Cursor {
    pub column: u32,
    pub row: u32,
    pub visible: bool,
}

#[derive(Clone, Debug, Serialize)]
pub struct Frame {
    pub cols: u16,
    pub rows: u16,
    pub cursor: Cursor,
    pub alternate: bool,
    pub scroll_offset: usize,
    pub history_rows: usize,
    pub modes: u32,
    pub text: Vec<String>,
    pub cells: Vec<Cell>,
    pub foreground: [u8; 3],
    pub background: [u8; 3],
}

/// Only the facts needed to encode an action; cached observations need not retain rendered cells or text.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct InputSurface {
    pub cols: u16,
    pub rows: u16,
    pub modes: u32,
    /// The alternate screen is tracked by the saved grid, not by a mode bit,
    /// so `modes` alone cannot tell a menu from the shell underneath it.
    pub alternate: bool,
    pub scroll_offset: usize,
}

impl InputSurface {
    /// The application has enabled bracketed paste (DECSET 2004).
    pub fn bracketed_paste(&self) -> bool {
        self.modes & mode::MODE_BRACKETPASTE != 0
    }
}

/// The plain text of one row exactly as `Frame::text` carries it: padding cells skipped, unstored cells blank, trailing blanks trimmed.
fn row_text(line: &ScreenLineView, cols: u16) -> String {
    let mut plain = String::new();
    for column in 0..u32::from(cols) {
        match line.cell(column) {
            Some(cell) if cell.is_padding() => {}
            Some(cell) => plain.push_str(cell.text()),
            None => plain.push(' '),
        }
    }
    plain.trim_end().to_owned()
}

impl Frame {
    pub fn input_surface(&self) -> InputSurface {
        InputSurface {
            cols: self.cols,
            rows: self.rows,
            modes: self.modes,
            alternate: self.alternate,
            scroll_offset: self.scroll_offset,
        }
    }
}

pub struct TerminalView {
    terminal: TerminalScreen,
    foreground: [u8; 3],
    background: [u8; 3],
}

impl TerminalView {
    pub fn new(cols: u16, rows: u16, foreground: [u8; 3], background: [u8; 3]) -> Result<Self> {
        ensure!(
            (1..=MAX_COLS).contains(&cols) && (1..=MAX_ROWS).contains(&rows),
            "unsupported terminal geometry"
        );
        let mut terminal = TerminalScreen::new(TerminalSize { cols, rows }, 2000);
        terminal.set_input_buffer_limit(1024 * 1024);
        Ok(Self {
            terminal,
            foreground,
            background,
        })
    }

    pub fn colors(&mut self, foreground: Option<[u8; 3]>, background: Option<[u8; 3]>) {
        if let Some(color) = foreground {
            self.foreground = color;
        }
        if let Some(color) = background {
            self.background = color;
        }
    }

    pub fn resize(&mut self, cols: u16, rows: u16) -> Result<()> {
        ensure!(
            (1..=MAX_COLS).contains(&cols) && (1..=MAX_ROWS).contains(&rows),
            "unsupported terminal geometry"
        );
        self.terminal.resize(TerminalSize { cols, rows });
        Ok(())
    }

    pub fn input_surface(&self) -> InputSurface {
        let screen = self.terminal.screen();
        let size = screen.size();
        InputSurface {
            cols: size.cols,
            rows: size.rows,
            modes: screen.mode(),
            alternate: screen.is_alternate(),
            scroll_offset: 0,
        }
    }

    pub fn feed(&mut self, bytes: &[u8]) {
        self.terminal.feed(bytes);
        // Drain terminal query replies and passthrough buffers so observing views never inject duplicate replies.
        self.terminal.take_replies();
        self.terminal.take_terminal_passthrough();
    }

    pub fn frame(&self, requested_offset: usize) -> Result<Frame> {
        let screen = self.terminal.screen();
        let size = screen.size();
        let history = screen.history_size();
        let offset = if screen.is_alternate() {
            0
        } else {
            requested_offset.min(history)
        };
        let mut cells = Vec::with_capacity(usize::from(size.cols) * usize::from(size.rows));
        let mut text = Vec::with_capacity(usize::from(size.rows));
        let mut bytes = 0usize;
        for row in 0..usize::from(size.rows) {
            let line = screen
                .absolute_line_view(history - offset + row)
                .ok_or_else(|| anyhow::anyhow!("missing terminal row"))?;
            text.push(row_text(&line, size.cols));
            for column in 0..usize::from(size.cols) {
                let cell = match line.cell(column as u32) {
                    Some(cell) => Cell {
                        text: cell.text().into(),
                        width: cell.width(),
                        attributes: cell.attr(),
                        foreground: cell.fg(),
                        background: cell.bg(),
                    },
                    None => Cell::blank(),
                };
                bytes = bytes.saturating_add(cell.text.len());
                ensure!(
                    bytes <= MAX_FRAME_TEXT,
                    "terminal frame text exceeds capture limit"
                );
                cells.push(cell);
            }
        }
        let (column, row) = screen.cursor_position();
        Ok(Frame {
            cols: size.cols,
            rows: size.rows,
            cursor: Cursor {
                column,
                row,
                visible: offset == 0 && screen.mode() & mode::MODE_CURSOR != 0,
            },
            alternate: screen.is_alternate(),
            scroll_offset: offset,
            history_rows: history,
            modes: screen.mode(),
            text,
            cells,
            foreground: self.foreground,
            background: self.background,
        })
    }
}

/// Encode an explicit terminal key vocabulary. Arrow/Home/End keys respect DECCKM (`MODE_KCURSOR`).
pub fn key_bytes(key: &str, modes: u32) -> Result<Vec<u8>> {
    let arrow = if modes & mode::MODE_KCURSOR != 0 {
        "\x1bO"
    } else {
        "\x1b["
    };
    let bytes = match key {
        "Enter" => b"\r".to_vec(),
        "Escape" => vec![27],
        "Tab" => vec![9],
        "Space" => b" ".to_vec(),
        "Backspace" => vec![127],
        "Ctrl+A" => vec![1],
        "Ctrl+C" => vec![3],
        "Ctrl+D" => vec![4],
        "Ctrl+E" => vec![5],
        "Ctrl+J" => vec![10],
        "Ctrl+K" => vec![11],
        "Ctrl+L" => vec![12],
        "Ctrl+R" => vec![18],
        "Ctrl+U" => vec![21],
        "Ctrl+W" => vec![23],
        "Ctrl+Z" => vec![26],
        "Up" => format!("{arrow}A").into_bytes(),
        "Down" => format!("{arrow}B").into_bytes(),
        "Right" => format!("{arrow}C").into_bytes(),
        "Left" => format!("{arrow}D").into_bytes(),
        "Home" => format!("{arrow}H").into_bytes(),
        "End" => format!("{arrow}F").into_bytes(),
        "PageUp" => b"\x1b[5~".to_vec(),
        "PageDown" => b"\x1b[6~".to_vec(),
        "Delete" => b"\x1b[3~".to_vec(),
        _ => anyhow::bail!("unsupported terminal key: {key}"),
    };
    Ok(bytes)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cjk_highlight_bracketed_paste_and_key_modes_share_one_projection() {
        let mut view = TerminalView::new(80, 24, [220, 220, 220], [15, 20, 24]).unwrap();
        view.feed("\x1b[7m/status 中文\x1b[0m\r\n> /s\x1b[?1h\x1b[?2004h".as_bytes());
        let frame = view.frame(0).unwrap();
        assert_eq!(frame.text[0], "/status 中文");
        assert_eq!(frame.cursor.column, 4);
        assert_eq!(frame.cursor.row, 1);
        assert_eq!(frame.cells[8].width, 2);
        assert_eq!(frame.cells[9].width, 0);
        assert_ne!(frame.cells[0].attributes & 16, 0);
        assert!(frame.input_surface().bracketed_paste());
        assert_eq!(key_bytes("Up", frame.modes).unwrap(), b"\x1bOA");
    }

    #[test]
    fn history_scroll_is_read_only_and_alternate_screen_restores() {
        let mut view = TerminalView::new(20, 3, [220, 220, 220], [15, 20, 24]).unwrap();
        for i in 0..20 {
            view.feed(format!("line{i}\r\n").as_bytes());
        }
        let live = view.frame(0).unwrap();
        let previous = view.frame(5).unwrap();
        assert_ne!(live.text, previous.text);
        assert!(!previous.cursor.visible);
        assert_eq!(view.frame(0).unwrap().text, live.text);
        view.feed(b"\x1b[?1049h\x1b[2J\x1b[Hmenu");
        assert!(view.frame(5).unwrap().alternate);
        assert_eq!(view.frame(5).unwrap().scroll_offset, 0);
        view.feed(b"\x1b[?1049l");
        assert_eq!(view.frame(0).unwrap().text, live.text);
    }

    #[test]
    fn history_view_pads_attributed_lines_stored_to_their_written_extent() {
        let mut view = TerminalView::new(20, 3, [220, 220, 220], [15, 20, 24]).unwrap();
        for i in 0..6 {
            view.feed(format!("\x1b[31mred{i}\x1b[0m\r\n").as_bytes());
        }
        let live = view.frame(0).unwrap();
        assert_eq!(live.history_rows, 4);
        assert_eq!(live.text, ["red4", "red5", ""]);
        let blank = &live.cells[live.cells.len() - 1];
        assert_eq!(blank.text, " ");
        for offset in 1..=live.history_rows {
            let frame = view
                .frame(offset)
                .unwrap_or_else(|error| panic!("offset {offset}: {error}"));
            assert_eq!(frame.scroll_offset, offset);
            assert_eq!(frame.cells.len(), 20 * 3);
            let expected: Vec<String> = (0..3)
                .map(|row| format!("red{}", 4 - offset + row))
                .collect();
            assert_eq!(frame.text, expected, "offset {offset}");
            let padded = &frame.cells[4];
            assert_eq!(padded.text, blank.text, "offset {offset}");
            assert_eq!(padded.width, blank.width, "offset {offset}");
            assert_eq!(padded.attributes, blank.attributes, "offset {offset}");
            assert_eq!(padded.foreground, blank.foreground, "offset {offset}");
            assert_eq!(padded.background, blank.background, "offset {offset}");
            assert_ne!(frame.cells[0].foreground, blank.foreground);
        }
    }
}
