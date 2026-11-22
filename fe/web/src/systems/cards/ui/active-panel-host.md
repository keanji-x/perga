# Single active panel lifecycle

The route translates `?card=` to one selected panel. ActivePanelHost mounts only
visited selections and retains each keyed CardCell while hidden or switched.
Inactive panels are hidden and inert; CardCell publishes visibility, focus and
measured geometry through the existing CardHost lifecycle. Resource removal,
host replacement and route disposal release the mount. Dock-owned IDs remain
excluded by the route, preserving TerminalWorkbenchView's independent ownership.

The feature overlay retains LayerDismissal and LayerDepthProvider. It owns no
layout persistence, drag handles, resizing or grid animation. The active surface
fills the available viewport; file search and terminal scrolling belong to their
existing renderers. Deletion still invokes the caller's confirmation callback.

Verification covers lazy mounting, retained DOM/controller identity, selection,
hide/reopen, removal, host replacement, real terminal reconnect/focus, file search
at different viewport sizes, route navigation, dismissal and workbench behavior.
