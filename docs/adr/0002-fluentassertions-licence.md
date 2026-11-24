# 0002: FluentAssertions 8 stays until the first paid tier

Status: superseded by [0003](0003-awesomeassertions.md); accepted 2026-11-20 (audit decision D8).

## Context

The .NET test projects (`tests/Ember.Tests`, `tests/Ember.Api.Tests`, `ops/tests/Ember.Ops.Tests`) use FluentAssertions 8.9.0. Since version 8, FluentAssertions is licensed by Xceed: free for non-commercial use, paid for commercial use. Version 7.x is still Apache 2.0. Ember is free today.

## Decision

Keep FluentAssertions 8 while Ember earns no money. Before the first paid tier (for example Ember+), either swap to Shouldly or pin FluentAssertions to the last 7.x release.

## Consequences

- No work now. The tests keep their current style.
- The swap is mechanical but touches every test file, so it is cheaper while the suite is smaller.
- Dependabot bumps within 8.x without flagging the licence, so this record is the reminder.

## Revisit when

Ember starts charging money for anything, or the FluentAssertions licence terms change.
