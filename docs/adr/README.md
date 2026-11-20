# Architecture decision records

Each record is one decision that would be expensive to reverse or that a new developer would otherwise question. It is short and plain.

- File name: `NNNN-short-title.md`, numbered in order. Numbers are never reused.
- Sections: **Context** (what forces the decision), **Decision** (what we do), **Consequences** (what gets easier, what gets harder), **Revisit when** (the concrete trigger that reopens it).
- A record is not edited to change the decision. A new record supersedes it, and the old one gets a line pointing to the new one.

| # | Decision |
|---|---|
| [0001](0001-scheduler-in-the-bot-single-instance.md) | The Quartz scheduler stays in the bot process; Ember runs as a single instance |
| [0002](0002-fluentassertions-licence.md) | FluentAssertions 8 stays until the first paid tier |
