# Dependency patches

`npm ci` applies these patches with `patch-package --error-on-fail`. Install
failures must be resolved before building; do not skip lifecycle scripts.

## Astryx 0.7.17: DropdownMenu opening focus

An opening animation frame can arrive after keyboard navigation or pointer
hover has already focused a menu item. The patch preserves that focus, cancels
the pending frame on close/unmount, and captures the opening modality and
`focusLastOnOpen` direction so a cancelled pointer open cannot affect a later
programmatic open.

The repair belongs to Astryx's shared popover menu, covering both compound and
data-driven menus without a Sidebar-specific wrapper. The descendant check
also covers inline submenu flyouts. Bottom-sheet focus is unchanged.

[Astryx #10590](https://github.com/meta/astryx/pull/10590) fixes the pointer
descendant case upstream. Version 0.7.17 still does not cancel this frame or
protect keyboard-open navigation.

## Astryx 0.7.17: ChatComposerInput custom token portal wrapper

`ChatComposerInput` wraps custom token portals in an unstyled `<span>` to carry
the React key (`<span key={id}>{token.render()}</span>`), whereas
`ChatComposerTokenElement` and `ChatTokenizedText` render `token.render()`
directly. Inside the `display: inline-flex; height: 1lh; align-items: center;
vertical-align: top` token container, that extra `<span>` is blockified to a
`1lh`-tall box whose inline `<Badge>` sits on its baseline and shifts `1px`
lower than pasted-text and structured tokens on the same line. Passing `id` as
the third argument to `createPortal(..., span, id)` drops the stray wrapper
while keeping portal keying intact.

The patch includes both the package's TypeScript source and its generated
runtime JavaScript. No styles or API declarations change.

When upgrading Astryx, re-evaluate or remove each hunk and keep the observable
tests in `web/src/ui/menu/astryx-focus.browser.test.tsx`, the Sidebar
integration test, and the chat composer token alignment browser test.
Patches apply once per dependency installation, including each CI job that
runs `npm ci`; no additional CI job or runtime listener is added.
