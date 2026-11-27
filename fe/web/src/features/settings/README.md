# Settings

`navigation.tsx` owns the frozen `SETTINGS_SECTIONS` registry used by the desktop
Astryx SideNav, mobile category list and header, and detail pane headings and
ledes. Top-level panes pass their category ID to `SettingsPane`. The app shell
owns navigation and the dialog lifetime. Compact mode uses the mobile header
and keeps the detail panes mounted across viewport changes.

To add a preference, declare a frozen field and bind its current value and
change handler with `bindSettingField`. `SettingFieldsList` renders the
descriptors with the shared list and row layout. Appearance declares the Theme
options once and renders them through Astryx SegmentedControl. `app/theme` owns
persistence, and the app shell supplies the binding. About displays build and
runtime details with Astryx MetadataList and copies them as plain text.
Planners and About show status labels using Astryx StatusDot indicators.

## Planner providers

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
