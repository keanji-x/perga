# Settings

`navigation.tsx` owns the frozen `SETTINGS_SECTIONS` registry used by the desktop
Astryx SideNav, mobile category list and header, and detail pane headings and
ledes. Top-level panes pass their category ID to `SettingsPane`. The app shell
owns navigation and the dialog lifetime. Compact mode uses the mobile header
and keeps the detail panes mounted across viewport changes.

To add a browser-local preference, declare a frozen field and bind its value
and change handler with `bindSettingField`. `SettingFieldsList` renders the
descriptors with the shared list and row layout. Appearance declares the Theme
options once and renders them through Astryx SegmentedControl. `app/theme` owns
persistence, and the app shell supplies the binding. About displays build and
runtime details with Astryx MetadataList and copies them as plain text.
Planners and About show status labels using Astryx StatusDot indicators.

## Planner defaults and providers

Planners renders the `planners` fields from `GET /api/v1/settings` above the
provider checks. The Host-backed registry supplies these field definitions.
[Settings TOML design](../../../../../docs/design/settings-toml.md)

`SettingFieldsList` renders Astryx SegmentedControl and Selector from the schema.
If the current TOML value falls outside the preset list, the control adds it
with the schema unit and sends its original value type when selected.

The app owns the snapshot and sends one field per change with the displayed
revision. A successful PATCH replaces the cached snapshot. A conflict refreshes
the defaults and asks the user to choose again. Load and save errors appear in
Defaults, independently of the provider checks.

The app shell joins shared availability and managed capabilities by provider
identity, preserving availability order. Availability supplies each provider's
status, reason, authentication notice and timestamp. Pending reads show
Checking. A missing capability produces a load error with Retry. Authentication
notices take precedence over readiness copy.

Recheck refreshes availability and the model catalogs. A failed Recheck keeps
the previous answer visible. The header timestamp is the oldest check in the
displayed list. The app owns query lifetime and recovery capabilities.

Contracts are exercised in the Settings unit and browser tests and the app
shell's overlay and planner tests.
