# Track workspace options

The Host exposes workspace options for an Area, optional folder, and optional template. The Kernel chooses the display order and default. VcsKit probes the selected directory and installed tools without changing the repository.

The browser displays the supplied options and submits a stable option ID. The Kernel validates that ID again before creating a Track. The preparation request stores the selected VCS backend, so asynchronous preparation uses the same choice on retry and restart. The VCS adapter validates an explicit backend before it creates an isolated workspace and records the actual backend in the existing workspace spec for teardown.

An omitted folder uses the Area default. An empty folder with an explicit option selects a managed source. A choice of source directory is scoped to that directory in the form. A new directory with only direct access available requires an explicit selection unless the template already recommends direct access.

Existing create requests can omit the option ID. Their request hashes and existing preparation records remain readable. A request carrying a selection uses the existing durable creation receipt to replay the original result.

Verify the option list and create protocol through the Host. A colocated repository offers both Git and Jujutsu when both tools are available, and an explicit Git selection produces a Git worktree. Exercise directory changes and submission through the browser.
