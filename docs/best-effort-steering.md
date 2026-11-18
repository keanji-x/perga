# Best-effort steering

Issue #14 covers sending supplemental text to a running turn. The browser
keeps the text in the conversation and admits another send while the
provider is answering. The tab retains supplement text across route changes. Reloading the tab
reloads the stored transcript. Ordinary sends retain their durable input
receipts.

The browser supplies the turn ID it observed. The Kernel serializes steering
attempts per session and only dispatches against that turn. The provider
adapter reports whether the provider accepted the input, declined it, or
left delivery unconfirmed. A silent provider attempt has a five-second deadline within a fifteen-second
request budget. A request that spends its budget waiting for the actor or an
earlier attempt returns unconfirmed. A definite
miss enters the existing in-memory wake queue, including when the target
already ended. An unconfirmed attempt ends with its text visible and can be
sent again by the user. The browser makes one request per steering press.

Verify the production send path with a controlled provider that holds a
request, then accepts, declines, or stays silent. Check that the composer
accepts a second message, the text stays visible, and only definite misses
start a follow-up turn. Ordinary acceptance must continue to use the same
durable receipt contract.

Recovery from a hung primary turn is a separate lifecycle change. Safe
recovery requires proof that the old provider can no longer write into
the workspace before starting another execution there.
