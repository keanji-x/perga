# Message submission and scheduling

The composer uses one ordinary message endpoint. The response identifies the
message with the same ID used by queue reads, steering, and transcript rows.
An idle session uses its existing SQLite input receipt. A busy session can
accept a volatile FIFO entry. Waiting entries and their replay keys can be
lost when the Track Actor exits. A formerly queued message's request key
also expires with the actor, even if its history row survived. The browser
makes one automatic attempt when the request can enter the volatile queue. Session
notifications carry no queued text.

The scheduler claims the queue entry before starting its follow-up. At the
start of that turn it records the user message under the queue identity.
The message is sending until the provider confirms receipt or emits turn
activity. The existing session event refreshes this state and the reply
continues through the streaming connection. The committed turn determines
the final message status. The browser gives the history row precedence over
its queue copy and retains the queue copy when the history read is delayed.

Say it now claims the same entry and targets the observed running turn.
The target must have a stored turn record so receipt and history share
the same target identity.
Confirmed delivery writes that identity into the target turn's transcript
and retires the queue entry. A definite miss keeps the original position.
An unconfirmed attempt holds the text away from automatic dispatch. A
failed transcript handoff also leaves the text in memory for inspection.

Verify ordinary idle and busy submission through the Host. Hold provider
output to distinguish Sending from Processing, finish the current turn
during a claimed steer, and check provider calls for duplicate delivery.
In the browser, deliver queue and transcript reads out of order and reopen
the conversation. The message must remain visible exactly once.
